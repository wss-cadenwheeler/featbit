using Application.Caches;
using Application.Configuration;
using Application.ControlPlane;
using Application.Services;
using Api.Infrastructure.Caches;
using Domain.FeatureFlags;
using Domain.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.Application.ControlPlane;

/// <summary>
/// C3b-2 commit coordinator. Under <see cref="ConsistencyMode.GatedCommit"/> this periodically
/// reconciles pending (staged-but-not-committed) flag changes: a pending version is committed
/// (its committed pointer/index advanced in every DC's Redis, the Mongo/EF pending promoted to
/// committed, and the change published to the evaluation-server topic) only once EVERY currently
/// live DC has that exact version staged in its own Redis.
///
/// Gate model (locked design): the control plane probes each live DC's Redis directly
/// (<see cref="CompositeRedisCacheService.GetStagedDcsAsync"/>) — "live" = a DC with at least one
/// unexpired lease in <see cref="ILeaseStore"/>. The pending set is enumerated via a DB scan
/// (<see cref="IFeatureFlagService.GetPendingAsync"/>); commit granularity is per-flag.
///
/// Idempotent + crash-safe: re-running a tick is a no-op once a flag is committed — the commit
/// broadcast and the version-guarded promote both no-op when already applied.
///
/// TODO: single-instance/leader election if the control plane runs multiple replicas. Today
/// multiple replicas would each run this loop; the commit + version-guarded promote are idempotent
/// so this is safe (at worst duplicate publishes), but a leader election would avoid the redundant
/// work. Out of scope for C3b-2; tracked as a follow-up.
/// </summary>
public sealed class CommitCoordinatorWorker : BackgroundService
{
    /// <summary>
    /// Default interval between coordinator ticks when not overridden via
    /// <c>ControlPlane:CommitCoordinator:IntervalSeconds</c>.
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICacheService _compositeCache;
    private readonly IMessageProducer _messageProducer;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly ILogger<CommitCoordinatorWorker> _logger;

    public CommitCoordinatorWorker(
        IServiceScopeFactory scopeFactory,
        [FromKeyedServices("compositeCache")] ICacheService compositeCache,
        IMessageProducer messageProducer,
        IConfiguration configuration,
        ILogger<CommitCoordinatorWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _compositeCache = compositeCache;
        _messageProducer = messageProducer;
        _logger = logger;
        _enabled = configuration.GetConsistencyMode() == ConsistencyMode.GatedCommit;

        var seconds = configuration.GetValue<int?>("ControlPlane:CommitCoordinator:IntervalSeconds");
        _interval = seconds is > 0
            ? TimeSpan.FromSeconds(seconds.Value)
            : DefaultInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation(
                "Commit coordinator disabled (consistency mode is not GatedCommit).");
            return;
        }

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var committed = await RunOnceAsync(stoppingToken);
                if (committed > 0)
                {
                    _logger.LogInformation(
                        "Commit coordinator committed {CommittedCount} pending flag change(s).",
                        committed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // ignore cancellation from the timer loop itself
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while running the commit coordinator tick.");
            }
        }
    }

    /// <summary>
    /// Performs a single coordinator tick and returns the number of flags committed. Exposed so it
    /// can be invoked directly (e.g. by integration tests) without waiting on the periodic timer.
    ///
    /// For each pending flag whose pending version is newer than its committed version, if every
    /// currently live DC has that version staged, the flag is committed across all DCs, its pending
    /// is promoted (version-guarded), and the change is published to the evaluation-server topic.
    /// Otherwise the flag is left pending and retried on the next tick.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        // IFeatureFlagService is a scoped (per-request) service in DI; resolve it inside a scope so
        // the singleton BackgroundService does not capture a scoped/disposed instance.
        using var scope = _scopeFactory.CreateScope();
        var featureFlagService = scope.ServiceProvider.GetRequiredService<IFeatureFlagService>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<ILeaseStore>();

        var pending = await featureFlagService.GetPendingAsync();
        if (pending.Count == 0)
        {
            return 0;
        }

        // Live DCs = distinct DcIds that currently hold at least one unexpired lease.
        var liveSet = await leaseStore.GetLiveSetAsync(DateTimeOffset.UtcNow);
        var liveDcs = liveSet
            .Select(lease => lease.DcId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList();

        if (liveDcs.Count == 0)
        {
            // No live DC to serve to -> nothing to commit this tick.
            return 0;
        }

        // GetStagedDcsAsync is a coordinator-only probe that lives on CompositeRedisCacheService,
        // not on the ICacheService contract. In the Redis path the keyed "compositeCache" service
        // is always a CompositeRedisCacheService; guard the cast so a misconfiguration (e.g. None
        // cache) degrades to a clear log instead of a crash loop.
        if (_compositeCache is not CompositeRedisCacheService composite)
        {
            _logger.LogWarning(
                "Commit coordinator requires the composite Redis cache (got {CacheType}); skipping tick.",
                _compositeCache.GetType().FullName);
            return 0;
        }

        var count = 0;

        foreach (var flag in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pendingChange = flag.Pending;
            if (pendingChange == null)
            {
                continue;
            }

            var version = pendingChange.Version;

            // Monotonicity (#34): never commit a version that is not strictly newer than what is
            // already committed. Skips stale/duplicate pending rows.
            if (version <= flag.CommittedVersion)
            {
                continue;
            }

            var stagedMap = await composite.GetStagedDcsAsync(flag.Id, version);

            // Gate: EVERY live DC must have this exact version staged. A DC missing from the map,
            // or present-but-false, blocks the commit (we retry next tick once it catches up).
            var allLiveStaged = liveDcs.All(dc => stagedMap.TryGetValue(dc, out var staged) && staged);
            if (!allLiveStaged)
            {
                continue;
            }

            // Broadcast the commit to all DCs: advance the committed pointer + index everywhere.
            await composite.CommitFlagAsync(flag.EnvId, flag.Id.ToString(), version);

            // Version-guarded promote of the DB pending -> committed. If a racing SetPendingAsync
            // replaced the pending version since GetPendingAsync read it, the guard makes this a
            // no-op and we do NOT publish a stale value.
            var promoted = await featureFlagService.PromotePendingAsync(flag.EnvId, flag.Key, version);
            if (!promoted)
            {
                continue;
            }

            // Publish the newly committed value to the evaluation-server topic. The pending value
            // IS the new committed state; it carries the same shape the BestEffort path publishes.
            await _messageProducer.PublishAsync(Topics.FeatureFlagChange, pendingChange.Value);
            count++;
        }

        return count;
    }
}

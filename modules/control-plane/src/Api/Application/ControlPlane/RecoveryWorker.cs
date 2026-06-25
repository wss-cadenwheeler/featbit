using Application.Caches;
using Application.Configuration;
using Application.ControlPlane;
using Application.Services;
using Api.Infrastructure.Caches;
using Domain.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Action = Domain.Messages.Action;

namespace Api.Application.ControlPlane;

/// <summary>
/// E1 returning-DC recovery (catch-up-before-serve). Under <see cref="ConsistencyMode.GatedCommit"/>
/// the commit coordinator commits a flag (and, identically, a segment) on the LIVE set, so a
/// flag/segment that changed while a DC was evicted is committed with NO pending — the returned DC's
/// Redis would otherwise stay stale forever. This worker watches the live set; when a DC newly
/// appears (its lease returns), it backfills that DC's Redis with the COMMITTED value of every flag
/// AND every segment (stage the versioned value + flip the committed pointer + advance the index),
/// so the returned DC catches up.
///
/// Model A, locked decisions:
///  - A: a separate worker (this one), not folded into the commit coordinator.
///  - B: per-DC TARGETED writes (<see cref="CompositeRedisCacheService.StageFlagToDcAsync"/> /
///       <see cref="CompositeRedisCacheService.CommitFlagToDcAsync"/>) — only the returned DC is
///       written, never a broadcast.
///  - C: no readiness gate — backfill runs as soon as the DC is seen live.
///  - D: per-DC client refresh — after a returned DC's backfill, a <c>PushFullSync</c> command is
///       published with <see cref="ControlPlaneCommand.TargetDcId"/> set to that DcId, so only that
///       DC's eval servers refresh their connected SDK clients (others ignore the targeted command).
///       Best-effort: a publish failure is logged but does NOT fail the backfill.
///
/// Idempotent: re-staging/re-committing an already-present version is a no-op (the staged value key
/// and committed pointer are version-keyed). It does NOT publish <c>Topics.FeatureFlagChange</c> —
/// the committed value did not change globally, only one DC's Redis is being repaired.
///
/// TODO: leader election if control plane runs multiple replicas. Today multiple replicas would each
/// run this loop; the staged-write + commit-pointer flip are idempotent so it is safe (at worst
/// redundant writes), but a leader election would avoid the duplicate work. Out of scope here.
///
/// ASSUMPTION (per-DC client refresh): the targeted <c>PushFullSync</c> command relies on the
/// <see cref="ControlPlaneTopics.ControlPlaneCommand"/> topic reaching EVERY DC's eval servers (the
/// existing admin "push full sync to ALL active clients" implies it does). The TargetDcId filter then
/// scopes who acts. If, in some MQ topologies, control-plane commands are delivered only locally,
/// remote-DC targeting would additionally need a per-DC command publish — a follow-up, NOT built here.
/// </summary>
public sealed class RecoveryWorker : BackgroundService
{
    /// <summary>
    /// Default interval between recovery ticks when not overridden via
    /// <c>ControlPlane:Recovery:IntervalSeconds</c>.
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICacheService _compositeCache;
    private readonly IMessageProducer _messageProducer;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly ILogger<RecoveryWorker> _logger;

    // DcIds seen live on the previous tick. A DcId present now but absent here is "newly present"
    // (returned / first-seen) and gets backfilled. First-seen counts as returned, which is harmless
    // (the DC is current after the backfill regardless).
    private HashSet<string> _previousLiveSet = new();

    public RecoveryWorker(
        IServiceScopeFactory scopeFactory,
        [FromKeyedServices("compositeCache")] ICacheService compositeCache,
        IMessageProducer messageProducer,
        IConfiguration configuration,
        ILogger<RecoveryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _compositeCache = compositeCache;
        _messageProducer = messageProducer;
        _logger = logger;
        _enabled = configuration.GetConsistencyMode() == ConsistencyMode.GatedCommit;

        var seconds = configuration.GetValue<int?>("ControlPlane:Recovery:IntervalSeconds");
        _interval = seconds is > 0
            ? TimeSpan.FromSeconds(seconds.Value)
            : DefaultInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation(
                "Recovery worker disabled (consistency mode is not GatedCommit).");
            return;
        }

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var backfilled = await RunOnceAsync(stoppingToken);
                if (backfilled > 0)
                {
                    _logger.LogInformation(
                        "Recovery worker backfilled {BackfilledCount} returning DC(s).",
                        backfilled);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // ignore cancellation from the timer loop itself
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while running the recovery worker tick.");
            }
        }
    }

    /// <summary>
    /// Performs a single recovery tick and returns the number of DCs backfilled. Exposed so it can
    /// be invoked directly (e.g. by integration tests) without waiting on the periodic timer.
    ///
    /// For each DcId newly present in the live set since the previous tick, every flag's AND every
    /// segment's committed value is staged and committed into that DC's Redis (targeted, not broadcast).
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        // ILeaseStore / IFeatureFlagService are scoped (per-request) in DI; resolve them inside a
        // scope so the singleton BackgroundService does not capture a scoped/disposed instance.
        using var scope = _scopeFactory.CreateScope();
        var featureFlagService = scope.ServiceProvider.GetRequiredService<IFeatureFlagService>();
        var segmentService = scope.ServiceProvider.GetRequiredService<ISegmentService>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<ILeaseStore>();

        // Live DCs = distinct DcIds that currently hold at least one unexpired lease.
        var liveSet = await leaseStore.GetLiveSetAsync(DateTimeOffset.UtcNow);
        var liveDcs = liveSet
            .Select(lease => lease.DcId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToHashSet();

        // Newly-present DcIds = live now but not on the previous tick.
        var returned = liveDcs.Where(dc => !_previousLiveSet.Contains(dc)).ToList();

        // Advance the watermark for the next tick regardless of what we do below.
        _previousLiveSet = liveDcs;

        if (returned.Count == 0)
        {
            return 0;
        }

        // StageFlagToDcAsync / CommitFlagToDcAsync are coordinator-only targeted writes that live on
        // CompositeRedisCacheService, not on the ICacheService contract. In the Redis path the keyed
        // "compositeCache" service is always a CompositeRedisCacheService; guard the cast so a
        // misconfiguration (e.g. None cache) degrades to a clear log instead of a crash loop.
        if (_compositeCache is not CompositeRedisCacheService composite)
        {
            _logger.LogWarning(
                "Recovery worker requires the composite Redis cache (got {CacheType}); skipping tick.",
                _compositeCache.GetType().FullName);
            return 0;
        }

        var allCommitted = await featureFlagService.GetAllCommittedAsync();
        var allCommittedSegments = await segmentService.GetAllCommittedAsync();

        // Resolve each committed segment's target env ids ONCE (not per returned DC): the env ids of a
        // segment are a function of the segment, independent of which DC is being repaired. Mirrors the
        // flag loop deriving its own ts but reuses the same envIds across DCs.
        var segmentEnvIds = new Dictionary<string, ICollection<Guid>>(allCommittedSegments.Count);
        foreach (var segment in allCommittedSegments)
        {
            segmentEnvIds[segment.Id.ToString()] = await segmentService.GetEnvironmentIdsAsync(segment);
        }

        var backfilled = 0;
        foreach (var dcId in returned)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var flag in allCommitted)
            {
                // The committed value's version token, mirroring how FeatureFlagChangeMessageHandler
                // derives the staged version from the flag's UpdatedAt.
                var ts = new DateTimeOffset(flag.UpdatedAt).ToUnixTimeMilliseconds();

                // Stage the versioned value, then flip the committed pointer + index — both targeted
                // at ONLY the returned DC. Idempotent: re-applying an already-present version no-ops.
                await composite.StageFlagToDcAsync(dcId, flag, ts);
                await composite.CommitFlagToDcAsync(dcId, flag.EnvId, flag.Id.ToString(), ts);
            }

            foreach (var segment in allCommittedSegments)
            {
                // Same shape as the flag backfill: the committed segment's version token (unix-ms of
                // UpdatedAt, mirroring SegmentChangeMessageHandler), then stage the versioned value +
                // flip the committed pointer + per-env index — targeted at ONLY the returned DC.
                // Idempotent: re-applying an already-present version no-ops.
                var ts = new DateTimeOffset(segment.UpdatedAt).ToUnixTimeMilliseconds();
                var envIds = segmentEnvIds[segment.Id.ToString()];

                await composite.StageSegmentToDcAsync(dcId, segment, ts);
                await composite.CommitSegmentToDcAsync(dcId, envIds, segment.Id.ToString(), ts);
            }

            _logger.LogInformation(
                "Recovery: backfilled returning DC {DcId} with {FlagCount} committed flag(s) and " +
                "{SegmentCount} committed segment(s).",
                dcId,
                allCommitted.Count,
                allCommittedSegments.Count);

            // D (per-DC client refresh): the returned DC's Redis is now current, but SDK clients still
            // connected to that DC's eval servers hold stale values until they reconnect. Publish a
            // PushFullSync command TARGETED at this DcId so only that DC's eval servers refresh their
            // clients (others ignore the TargetDcId-scoped command). Best-effort: a publish failure is
            // logged but does NOT fail the backfill — the Redis repair already succeeded.
            await PublishClientRefreshAsync(dcId);

            backfilled++;
        }

        return backfilled;
    }

    /// <summary>
    /// Best-effort: publish a <see cref="Action.PushFullSync"/> command scoped to <paramref name="dcId"/>
    /// (<see cref="ControlPlaneCommand.TargetDcId"/>) so only that DC's eval servers refresh their
    /// connected SDK clients after the backfill. A publish failure is logged and swallowed — it must
    /// not fail the backfill that already repaired the DC's Redis.
    /// </summary>
    private async Task PublishClientRefreshAsync(string dcId)
    {
        try
        {
            await _messageProducer.PublishAsync(
                ControlPlaneTopics.ControlPlaneCommand,
                new ControlPlaneCommand { Action = Action.PushFullSync, TargetDcId = dcId });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Recovery: failed to publish per-DC client refresh (PushFullSync) for returning DC {DcId}. " +
                "Backfill succeeded; clients on that DC will refresh on their next reconnect.",
                dcId);
        }
    }
}

using Api.Infrastructure.Caches;
using Application.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.Application.ControlPlane;

/// <summary>
/// Mode-agnostic cache self-heal for every configured DC (local + peers). Rebuilds a DC's Redis
/// cache from the source of truth (Mongo/Postgres) whenever that DC's Redis link becomes reachable —
/// on control-plane startup and on a disconnected -> connected transition. Covers two gaps:
///
///  - LOCAL refill: the api-server's RedisPopulatingService skips on restart when Redis is already
///    populated (the <c>featbit:redis-is-populated</c> guard survives a compute-only outage), so a
///    cluster that recovers with stale-but-populated Redis never rebuilds it. The local poll fires
///    on startup (the in-cluster link is reachable) and backfills the LOCAL DC from the source of
///    truth — bypassing that guard, and independent of whether the peer is up. This is what lets a
///    DC self-heal even when its peer is down.
///
///  - PEER (cross-DC) refill: when a peer DC's cross-DC Redis link is down during flag/segment/secret
///    changes, that peer's cache misses the writes (BestEffort drops them; GatedCommit staging can't
///    reach it — secrets are never staged/gated, in either mode). When the link returns, this
///    backfills the peer from the source of truth.
///
/// Covers three cache categories via <see cref="IDcBackfiller"/> (#91): flags, segments, and secrets.
/// Secrets are backfilled unconditionally in both consistency modes (they carry no staged/committed
/// lifecycle), so a DC whose Redis lost its secret keys stops failing SDK auth once this reconciles
/// it — not just once its flags/segments look correct.
///
/// Detection is a poll of <c>IConnectionMultiplexer.IsConnected</c> (touching <c>.Connection</c>
/// materializes the lazy multiplexer; instances are built with <c>abortConnect=false</c> so this
/// never throws/caches a permanent failure). A poll covers startup + reconnect + a safety net with
/// one mechanism and is straightforward to test. Backfill is via <see cref="IDcBackfiller"/>
/// (mode-appropriate writes + a per-DC client refresh) and is idempotent, so redundant runs and the
/// overlap with <see cref="RecoveryWorker"/> (GatedCommit lease-return trigger) are harmless — the
/// only-advance guard (#89) on the underlying targeted writes is what actually makes them so: two
/// concurrent backfillers (or a backfiller racing a normal commit) can never revert each other's
/// pointer/index to an older snapshot, they just converge on whichever version is newest.
///
/// #90: when a tick finds more than one DC newly reachable (e.g. control-plane startup with several
/// configured peers), <see cref="RunOnceAsync"/> fetches the committed snapshot ONCE
/// (<see cref="IDcBackfiller.FetchCommittedSnapshotAsync"/>) and shares it across all of them, instead
/// of each DC's backfill re-reading the source of truth. That fetch is LAZY — an idle tick where
/// nothing is newly reachable (the steady-state common case) never touches the source of truth.
/// Disable via <c>ControlPlane:CacheReconcile:Enabled=false</c>.
/// </summary>
public sealed class CacheReconciler : BackgroundService
{
    /// <summary>Default poll interval when not overridden via config.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyList<DcRedisConnection> _dcs;
    private readonly IDcBackfiller _backfiller;
    private readonly ConsistencyMode _mode;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly ILogger<CacheReconciler> _logger;

    // Last observed IsConnected per DcId; key absent = never observed yet.
    private readonly Dictionary<string, bool> _lastConnected = new();

    // DcIds with a backfill currently running, to coalesce overlapping reconciles.
    private readonly HashSet<string> _inFlight = new();

    public CacheReconciler(
        IReadOnlyList<DcRedisConnection> dcs,
        IDcBackfiller backfiller,
        IConfiguration configuration,
        ILogger<CacheReconciler> logger)
    {
        _dcs = dcs;
        _backfiller = backfiller;
        _logger = logger;
        _mode = configuration.GetConsistencyMode();
        _enabled = configuration.GetValue<bool?>("ControlPlane:CacheReconcile:Enabled") ?? true;

        var seconds = configuration.GetValue<int?>("ControlPlane:CacheReconcile:IntervalSeconds");
        _interval = seconds is > 0
            ? TimeSpan.FromSeconds(seconds.Value)
            : DefaultInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation(
                "Cache reconciler disabled (ControlPlane:CacheReconcile:Enabled=false).");
            return;
        }

        if (_dcs.Count == 0)
        {
            _logger.LogInformation(
                "Cache reconciler has no DCs configured (cache provider is not Redis); nothing to reconcile.");
            return;
        }

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // ignore cancellation from the timer loop itself
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while running the cache reconciler tick.");
            }
        }
    }

    /// <summary>
    /// One detection tick: poll each DC's connection state and reconcile any DC that is newly
    /// reachable (first seen connected — i.e. startup — or a disconnected -> connected transition). A
    /// steady-connected DC is not re-reconciled. Exposed so tests can drive ticks without the timer.
    ///
    /// #90: the committed snapshot (flags + segments + segment env-ids) is fetched LAZILY — only if at
    /// least one DC is newly reachable this tick — and then shared across every newly-reachable DC's
    /// backfill in the SAME tick, so an idle steady-state tick (the common case, every ~10s) never
    /// touches the source of truth, and a tick with multiple newly-reachable DCs (e.g. control-plane
    /// startup with several peers) reads it once instead of once per DC.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var newlyReachable = new List<(string DcId, bool IsLocal)>();

        foreach (var dc in _dcs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool isConnected;
            try
            {
                // Touching .Connection materializes the lazy multiplexer (starting its background
                // connect) and reports live state.
                isConnected = dc.Client.Connection.IsConnected;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Cache reconciler: could not read connection state for DC {DcId}; will retry next tick.",
                    dc.DcId);
                continue;
            }

            var seenBefore = _lastConnected.TryGetValue(dc.DcId, out var wasConnected);
            _lastConnected[dc.DcId] = isConnected;

            if (isConnected && (!seenBefore || !wasConnected))
            {
                newlyReachable.Add((dc.DcId, dc.IsLocal));
            }
        }

        if (newlyReachable.Count == 0)
        {
            // Idle tick: nothing newly reachable, so no snapshot fetch and no DB read this tick.
            return;
        }

        CommittedSnapshot snapshot;
        try
        {
            snapshot = await _backfiller.FetchCommittedSnapshotAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Cache reconciler: failed to fetch the shared committed snapshot for {Count} newly " +
                "reachable DC(s); all will retry on a later tick.",
                newlyReachable.Count);
            // Force a retry next tick for every DC that would have been reconciled this tick.
            foreach (var (dcId, _) in newlyReachable)
            {
                _lastConnected.Remove(dcId);
            }
            return;
        }

        foreach (var (dcId, isLocal) in newlyReachable)
        {
            await RunReconcileAsync(dcId, isLocal, snapshot, cancellationToken);
        }
    }

    /// <summary>
    /// Backfill one DC's cache from the pre-fetched <paramref name="snapshot"/> (via
    /// <see cref="IDcBackfiller"/>), coalescing overlapping calls per DcId. On failure the
    /// last-connected watermark for the DC is cleared so the next tick re-detects it as newly reachable
    /// and retries. Exposed for tests.
    /// </summary>
    public async Task RunReconcileAsync(
        string dcId,
        bool isLocal,
        CommittedSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        lock (_inFlight)
        {
            if (!_inFlight.Add(dcId))
            {
                // A backfill for this DC is already running; coalesce.
                return;
            }
        }

        try
        {
            _logger.LogInformation(
                "Cache reconciler: {Scope} DC {DcId} is reachable; backfilling its cache from the source of truth ({Mode}).",
                isLocal ? "local" : "peer",
                dcId,
                _mode);
            await _backfiller.BackfillDcAsync(dcId, _mode, snapshot, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Cache reconciler: backfill failed for DC {DcId}; will retry on a later tick.",
                dcId);
            // Force a retry next tick by forgetting the watermark for this DC.
            _lastConnected.Remove(dcId);
        }
        finally
        {
            lock (_inFlight)
            {
                _inFlight.Remove(dcId);
            }
        }
    }
}

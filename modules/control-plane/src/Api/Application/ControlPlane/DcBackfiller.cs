using Application.Caches;
using Application.Configuration;
using Application.ControlPlane;
using Application.Services;
using Api.Infrastructure.Caches;
using Domain.Environments;
using Domain.FeatureFlags;
using Domain.Messages;
using Domain.Segments;
using Microsoft.Extensions.DependencyInjection;
using Action = Domain.Messages.Action;

namespace Api.Application.ControlPlane;

/// <summary>
/// A point-in-time snapshot of the source of truth (Mongo/Postgres) committed flags + segments +
/// secrets, plus each committed segment's resolved target env ids — fetched once via
/// <see cref="IDcBackfiller.FetchCommittedSnapshotAsync"/> and shared across every DC backfilled in
/// the same tick (<see cref="RecoveryWorker"/>, <see cref="CacheReconciler"/>) so two DCs recovering
/// in one tick are written from an IDENTICAL view of the source of truth, and so the DB is read once
/// per tick rather than once per DC (#90). Env ids are resolved once here (independent of the DC being
/// repaired) because they are themselves a DB lookup (<c>ISegmentService.GetEnvironmentIdsAsync</c>).
/// <see cref="Secrets"/> (#91) is enumerated the same way the api-server's
/// <c>RedisPopulatingService.PopulateSecretsAsync</c> does (<c>IEnvironmentService.GetSecretCachesAsync</c>)
/// so a DC backfill's secret coverage matches startup population.
/// </summary>
public sealed record CommittedSnapshot(
    IReadOnlyList<FeatureFlag> Flags,
    IReadOnlyList<Segment> Segments,
    IReadOnlyDictionary<string, ICollection<Guid>> SegmentEnvIds,
    IReadOnlyList<SecretCache> Secrets);

/// <summary>
/// Backfills ONE DC's Redis with the authoritative (committed) value of every flag, segment, and
/// secret, read from the source of truth (Mongo/Postgres) — then publishes a per-DC
/// <c>PushFullSync</c> so that DC's eval servers refresh their connected SDK clients.
///
/// Shared by two triggers:
///  - <see cref="RecoveryWorker"/> (GatedCommit, lease-return trigger), and
///  - <see cref="CacheReconciler"/> (mode-agnostic, Redis-link-reachable trigger, local + peers).
///
/// The flag/segment write path is mode-appropriate: GatedCommit stages the versioned value + flips
/// the committed pointer (so the eval reads the versioned snapshot); BestEffort upserts the legacy
/// <c>featbit:flag:{id}</c> key the BestEffort eval reads. Secrets (#91) are NOT staged/gated in
/// either mode — the source-of-truth secret cache shape (<c>featbit:secret:{value}</c>, a hash keyed
/// by the secret's own value) has no version to gate on, so they are written unconditionally in BOTH
/// modes, exactly like the api-server's initial population. All writes are targeted at the one DC
/// (never broadcast) and idempotent, so re-running is safe.
///
/// Each backfill run is driven by a <see cref="CommittedSnapshot"/> of the source of truth taken up
/// front (<see cref="FetchCommittedSnapshotAsync"/>), then awaits per-item writes, so a version
/// committed AFTER the snapshot was taken can land on the target DC before this backfill's write for
/// the same flag/segment does. Without a guard that race would let a stale snapshot revert a fresher
/// pointer (#89) — every targeted flag/segment write this method makes
/// (<see cref="CompositeRedisCacheService.CommitFlagToDcAsync"/>,
/// <see cref="CompositeRedisCacheService.CommitSegmentToDcAsync"/>,
/// <see cref="CompositeRedisCacheService.UpsertFlagToDcAsync"/>,
/// <see cref="CompositeRedisCacheService.UpsertSegmentToDcAsync"/>) is only-advance guarded at the
/// Redis layer, so a stale write from this snapshot is a no-op instead of a regression. The secret
/// write (<see cref="CompositeRedisCacheService.UpsertSecretToDcAsync"/>) is NOT guarded the same
/// way — there is no version to compare, so it is last-write-wins, matching the existing (broadcast)
/// <c>UpsertSecretAsync</c> semantic.
///
/// #90: a caller backfilling more than one DC in the same tick (RecoveryWorker/CacheReconciler) should
/// call <see cref="FetchCommittedSnapshotAsync"/> ONCE and pass the resulting snapshot to the
/// <see cref="BackfillDcAsync(string,ConsistencyMode,CommittedSnapshot,CancellationToken)"/> overload
/// for each DC, instead of using the no-snapshot overload per DC (which re-fetches every call). This
/// invariant (one fetch per tick) applies to secrets too (#91): they are part of the same
/// <see cref="CommittedSnapshot"/>.
///
/// #92: the per-DC in-flight set lives HERE (a singleton shared by both <see cref="RecoveryWorker"/>
/// and <see cref="CacheReconciler"/>), not on either caller, so a <see cref="RecoveryWorker"/> tick and
/// a <see cref="CacheReconciler"/> tick backfilling the SAME DcId at the same time coalesce against
/// each other rather than each running a redundant full read/write cycle — the second concurrent call
/// for a given DcId returns immediately with <see cref="Skipped"/> instead of doing any work.
/// </summary>
public interface IDcBackfiller
{
    /// <summary>
    /// Sentinel <see cref="BackfillDcAsync(string,ConsistencyMode,CancellationToken)"/> /
    /// <see cref="BackfillDcAsync(string,ConsistencyMode,CommittedSnapshot,CancellationToken)"/> return
    /// value meaning the call did NO work — either the composite Redis cache is unavailable (see
    /// <see cref="IsCompositeCacheAvailable"/>) or a backfill for that DcId was already in flight
    /// (#92 coalescing). Distinct from a legitimate <c>0</c>, which means the backfill ran but the
    /// committed snapshot happened to contain zero flags (e.g. a segment/secret-only tick).
    /// </summary>
    public const int Skipped = -1;

    /// <summary>
    /// Cheap, non-logging check of whether the composite Redis cache is configured (i.e. backfills can
    /// actually do anything). Callers that process several DCs per tick (<see cref="RecoveryWorker"/>,
    /// <see cref="CacheReconciler"/>) should check this ONCE per tick before looping so a
    /// misconfiguration logs a single warning per tick instead of once per DC (#92) — see
    /// <see cref="RecoveryWorker.RunOnceAsync"/>.
    /// </summary>
    bool IsCompositeCacheAvailable { get; }

    /// <summary>
    /// Backfill <paramref name="dcId"/>'s Redis from the source of truth using the write path for
    /// <paramref name="mode"/>, then publish a targeted client refresh. Returns the number of flags
    /// written, or <see cref="Skipped"/> if the composite cache is unavailable or this DcId's backfill
    /// coalesced with one already in flight (#92). Convenience overload for single-DC callers: fetches
    /// its own <see cref="CommittedSnapshot"/> internally. A caller backfilling multiple DCs in one tick
    /// should instead call <see cref="FetchCommittedSnapshotAsync"/> once and use the snapshot-accepting
    /// overload so every DC in that tick shares one fetch.
    /// </summary>
    Task<int> BackfillDcAsync(string dcId, ConsistencyMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Backfill <paramref name="dcId"/>'s Redis from the pre-fetched <paramref name="snapshot"/> using
    /// the write path for <paramref name="mode"/>, then publish a targeted client refresh. Returns the
    /// number of flags written, or <see cref="Skipped"/> if the composite cache is unavailable or this
    /// DcId's backfill coalesced with one already in flight (#92).
    /// </summary>
    Task<int> BackfillDcAsync(
        string dcId,
        ConsistencyMode mode,
        CommittedSnapshot snapshot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetch a fresh <see cref="CommittedSnapshot"/> of the source of truth: every committed flag,
    /// every committed segment, each committed segment's resolved target env ids, and every secret
    /// cache entry (#91). Call this ONCE per tick when backfilling multiple DCs so they share an
    /// identical view (#90).
    /// </summary>
    Task<CommittedSnapshot> FetchCommittedSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed class DcBackfiller(
    IServiceScopeFactory scopeFactory,
    [FromKeyedServices("compositeCache")] ICacheService compositeCache,
    IMessageProducer messageProducer,
    ILogger<DcBackfiller> logger) : IDcBackfiller
{
    // #92: DcIds with a backfill currently running, shared across EVERY caller (RecoveryWorker and
    // CacheReconciler both hold a reference to this same singleton), so the two workers coalesce
    // against each other instead of each maintaining their own (previously CacheReconciler-only)
    // in-flight set.
    private readonly object _inFlightLock = new();
    private readonly HashSet<string> _inFlight = new();

    public bool IsCompositeCacheAvailable => compositeCache is CompositeRedisCacheService;

    public async Task<int> BackfillDcAsync(
        string dcId,
        ConsistencyMode mode,
        CancellationToken cancellationToken = default)
    {
        // Convenience single-DC path: not shared with any other DC's backfill, so a fetch-per-call is
        // fine here. Guard the composite cache BEFORE fetching so a misconfiguration (e.g. None cache)
        // skips the DB round-trip entirely, same as before #90.
        if (!TryGetComposite(dcId, out _))
        {
            return IDcBackfiller.Skipped;
        }

        var snapshot = await FetchCommittedSnapshotAsync(cancellationToken);
        return await BackfillDcAsync(dcId, mode, snapshot, cancellationToken);
    }

    public async Task<int> BackfillDcAsync(
        string dcId,
        ConsistencyMode mode,
        CommittedSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        // StageFlagToDcAsync / UpsertFlagToDcAsync etc. are targeted writes that live on
        // CompositeRedisCacheService, not on the ICacheService contract. In the Redis path the keyed
        // "compositeCache" service is always a CompositeRedisCacheService; guard the cast so a
        // misconfiguration (e.g. None cache) degrades to a clear log instead of a crash loop.
        if (!TryGetComposite(dcId, out var composite))
        {
            return IDcBackfiller.Skipped;
        }

        // #92: coalesce concurrent backfills of the SAME DcId — whether both come from this process's
        // RecoveryWorker and CacheReconciler racing each other, or two overlapping ticks of the same
        // caller. The loser returns immediately without touching Redis; the winner's in-progress (or
        // just-completed) backfill already covers it, and every underlying write is idempotent/
        // only-advance-guarded (#89) so there is nothing for the loser to "miss".
        lock (_inFlightLock)
        {
            if (!_inFlight.Add(dcId))
            {
                logger.LogDebug(
                    "DC backfill: a backfill for DC {DcId} is already in flight; coalescing (no-op).",
                    dcId);
                return IDcBackfiller.Skipped;
            }
        }

        try
        {
            return await RunBackfillAsync(dcId, mode, snapshot, composite, cancellationToken);
        }
        finally
        {
            lock (_inFlightLock)
            {
                _inFlight.Remove(dcId);
            }
        }
    }

    private async Task<int> RunBackfillAsync(
        string dcId,
        ConsistencyMode mode,
        CommittedSnapshot snapshot,
        CompositeRedisCacheService composite,
        CancellationToken cancellationToken)
    {
        foreach (var flag in snapshot.Flags)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Version token mirrors how FeatureFlagChangeMessageHandler derives the staged version
            // from the flag's UpdatedAt.
            var ts = new DateTimeOffset(flag.UpdatedAt).ToUnixTimeMilliseconds();

            if (mode == ConsistencyMode.GatedCommit)
            {
                // Stage the versioned value, then flip the committed pointer + index — targeted at
                // ONLY this DC. Idempotent: re-applying an already-present version no-ops.
                await composite.StageFlagToDcAsync(dcId, flag, ts);
                await composite.CommitFlagToDcAsync(dcId, flag.EnvId, flag.Id.ToString(), ts);
            }
            else
            {
                // BestEffort: write the legacy value key the BestEffort eval reads.
                await composite.UpsertFlagToDcAsync(dcId, flag);
            }
        }

        foreach (var segment in snapshot.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ts = new DateTimeOffset(segment.UpdatedAt).ToUnixTimeMilliseconds();
            var envIds = snapshot.SegmentEnvIds[segment.Id.ToString()];

            if (mode == ConsistencyMode.GatedCommit)
            {
                await composite.StageSegmentToDcAsync(dcId, segment, ts);
                await composite.CommitSegmentToDcAsync(dcId, envIds, segment.Id.ToString(), ts);
            }
            else
            {
                await composite.UpsertSegmentToDcAsync(dcId, envIds, segment);
            }
        }

        // Secrets (#91) are NOT mode-gated: unlike flags/segments there is no staged/committed
        // lifecycle for them (the cache shape is a last-write-wins hash keyed by the secret's own
        // value, not by env+id — see UpsertSecretToDcAsync), so they are written unconditionally in
        // BOTH GatedCommit and BestEffort. Without this, a DC whose Redis lost its secret keys keeps
        // failing SDK auth even after flags/segments are healed (the eval server's secret lookup has
        // no DB fallback).
        foreach (var secretCache in snapshot.Secrets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await composite.UpsertSecretToDcAsync(dcId, secretCache.Descriptor, secretCache.Secret);
        }

        logger.LogInformation(
            "DC backfill: repaired DC {DcId} ({Mode}) from source of truth with {FlagCount} flag(s), " +
            "{SegmentCount} segment(s), and {SecretCount} secret(s).",
            dcId,
            mode,
            snapshot.Flags.Count,
            snapshot.Segments.Count,
            snapshot.Secrets.Count);

        await PublishClientRefreshAsync(dcId);

        return snapshot.Flags.Count;
    }

    /// <summary>
    /// Fetch a fresh <see cref="CommittedSnapshot"/>: every committed flag, every committed segment,
    /// each committed segment's resolved target env ids (a DB lookup, resolved once here —
    /// independent of any DC being repaired), and every secret cache entry (#91, enumerated the same
    /// way as the api-server's <c>RedisPopulatingService.PopulateSecretsAsync</c> via
    /// <see cref="IEnvironmentService.GetSecretCachesAsync"/>). Callers backfilling multiple DCs in
    /// one tick should call this ONCE and share the result (#90); see
    /// <see cref="RecoveryWorker.RunOnceAsync"/> and <see cref="CacheReconciler.RunOnceAsync"/>.
    /// </summary>
    public async Task<CommittedSnapshot> FetchCommittedSnapshotAsync(CancellationToken cancellationToken = default)
    {
        // IFeatureFlagService / ISegmentService / IEnvironmentService are scoped (per-request) in DI;
        // resolve them inside a scope so a singleton caller does not capture a scoped/disposed instance.
        using var scope = scopeFactory.CreateScope();
        var featureFlagService = scope.ServiceProvider.GetRequiredService<IFeatureFlagService>();
        var segmentService = scope.ServiceProvider.GetRequiredService<ISegmentService>();
        var environmentService = scope.ServiceProvider.GetRequiredService<IEnvironmentService>();

        var allCommitted = await featureFlagService.GetAllCommittedAsync();
        var allCommittedSegments = await segmentService.GetAllCommittedAsync();

        // Resolve each committed segment's target env ids once (independent of the DC being repaired).
        var segmentEnvIds = new Dictionary<string, ICollection<Guid>>(allCommittedSegments.Count);
        foreach (var segment in allCommittedSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            segmentEnvIds[segment.Id.ToString()] = await segmentService.GetEnvironmentIdsAsync(segment);
        }

        var secretCaches = await environmentService.GetSecretCachesAsync();

        return new CommittedSnapshot(
            allCommitted,
            allCommittedSegments,
            segmentEnvIds,
            secretCaches.ToList());
    }

    /// <summary>
    /// Resolve <see cref="compositeCache"/> to the concrete <see cref="CompositeRedisCacheService"/>
    /// the targeted writes need, logging + returning <c>false</c> (instead of throwing) if the composite
    /// cache is misconfigured (e.g. a <c>None</c> cache provider).
    /// </summary>
    private bool TryGetComposite(string dcId, out CompositeRedisCacheService composite)
    {
        if (compositeCache is CompositeRedisCacheService redisComposite)
        {
            composite = redisComposite;
            return true;
        }

        logger.LogWarning(
            "DC backfill requires the composite Redis cache (got {CacheType}); skipping backfill for DC {DcId}.",
            compositeCache.GetType().FullName,
            dcId);
        composite = null!;
        return false;
    }

    /// <summary>
    /// Best-effort: publish a <see cref="Action.PushFullSync"/> command scoped to
    /// <paramref name="dcId"/> so only that DC's eval servers refresh their connected SDK clients
    /// after the backfill. A publish failure is logged and swallowed — it must not fail the backfill
    /// that already repaired the DC's Redis.
    /// </summary>
    private async Task PublishClientRefreshAsync(string dcId)
    {
        try
        {
            await messageProducer.PublishAsync(
                ControlPlaneTopics.ControlPlaneCommand,
                new ControlPlaneCommand { Action = Action.PushFullSync, TargetDcId = dcId });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "DC backfill: failed to publish per-DC client refresh (PushFullSync) for DC {DcId}. " +
                "Backfill succeeded; clients on that DC will refresh on their next reconnect.",
                dcId);
        }
    }
}

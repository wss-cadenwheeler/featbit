using Application.Caches;
using Application.Configuration;
using Application.ControlPlane;
using Application.Services;
using Api.Infrastructure.Caches;
using Domain.FeatureFlags;
using Domain.Messages;
using Domain.Segments;
using Microsoft.Extensions.DependencyInjection;
using Action = Domain.Messages.Action;

namespace Api.Application.ControlPlane;

/// <summary>
/// A point-in-time snapshot of the source of truth (Mongo/Postgres) committed flags + segments,
/// plus each committed segment's resolved target env ids — fetched once via
/// <see cref="IDcBackfiller.FetchCommittedSnapshotAsync"/> and shared across every DC backfilled in
/// the same tick (<see cref="RecoveryWorker"/>, <see cref="CacheReconciler"/>) so two DCs recovering
/// in one tick are written from an IDENTICAL view of the source of truth, and so the DB is read once
/// per tick rather than once per DC (#90). Env ids are resolved once here (independent of the DC being
/// repaired) because they are themselves a DB lookup (<c>ISegmentService.GetEnvironmentIdsAsync</c>).
/// </summary>
public sealed record CommittedSnapshot(
    IReadOnlyList<FeatureFlag> Flags,
    IReadOnlyList<Segment> Segments,
    IReadOnlyDictionary<string, ICollection<Guid>> SegmentEnvIds);

/// <summary>
/// Backfills ONE DC's Redis with the authoritative (committed) value of every flag and segment,
/// read from the source of truth (Mongo/Postgres) — then publishes a per-DC <c>PushFullSync</c>
/// so that DC's eval servers refresh their connected SDK clients.
///
/// Shared by two triggers:
///  - <see cref="RecoveryWorker"/> (GatedCommit, lease-return trigger), and
///  - <see cref="CacheReconciler"/> (mode-agnostic, Redis-link-reachable trigger, local + peers).
///
/// The write path is mode-appropriate: GatedCommit stages the versioned value + flips the
/// committed pointer (so the eval reads the versioned snapshot); BestEffort upserts the legacy
/// <c>featbit:flag:{id}</c> key the BestEffort eval reads. All writes are targeted at the one DC
/// (never broadcast) and idempotent, so re-running is safe.
///
/// Each backfill run is driven by a <see cref="CommittedSnapshot"/> of the source of truth taken up
/// front (<see cref="FetchCommittedSnapshotAsync"/>), then awaits per-item writes, so a version
/// committed AFTER the snapshot was taken can land on the target DC before this backfill's write for
/// the same flag/segment does. Without a guard that race would let a stale snapshot revert a fresher
/// pointer (#89) — every targeted write this method makes
/// (<see cref="CompositeRedisCacheService.CommitFlagToDcAsync"/>,
/// <see cref="CompositeRedisCacheService.CommitSegmentToDcAsync"/>,
/// <see cref="CompositeRedisCacheService.UpsertFlagToDcAsync"/>,
/// <see cref="CompositeRedisCacheService.UpsertSegmentToDcAsync"/>) is only-advance guarded at the
/// Redis layer, so a stale write from this snapshot is a no-op instead of a regression.
///
/// #90: a caller backfilling more than one DC in the same tick (RecoveryWorker/CacheReconciler) should
/// call <see cref="FetchCommittedSnapshotAsync"/> ONCE and pass the resulting snapshot to the
/// <see cref="BackfillDcAsync(string,ConsistencyMode,CommittedSnapshot,CancellationToken)"/> overload
/// for each DC, instead of using the no-snapshot overload per DC (which re-fetches every call).
/// </summary>
public interface IDcBackfiller
{
    /// <summary>
    /// Backfill <paramref name="dcId"/>'s Redis from the source of truth using the write path for
    /// <paramref name="mode"/>, then publish a targeted client refresh. Returns the number of flags
    /// written (0 if the composite cache is unavailable). Convenience overload for single-DC callers:
    /// fetches its own <see cref="CommittedSnapshot"/> internally. A caller backfilling multiple DCs in
    /// one tick should instead call <see cref="FetchCommittedSnapshotAsync"/> once and use the
    /// snapshot-accepting overload so every DC in that tick shares one fetch.
    /// </summary>
    Task<int> BackfillDcAsync(string dcId, ConsistencyMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Backfill <paramref name="dcId"/>'s Redis from the pre-fetched <paramref name="snapshot"/> using
    /// the write path for <paramref name="mode"/>, then publish a targeted client refresh. Returns the
    /// number of flags written (0 if the composite cache is unavailable).
    /// </summary>
    Task<int> BackfillDcAsync(
        string dcId,
        ConsistencyMode mode,
        CommittedSnapshot snapshot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetch a fresh <see cref="CommittedSnapshot"/> of the source of truth: every committed flag,
    /// every committed segment, and each committed segment's resolved target env ids. Call this ONCE
    /// per tick when backfilling multiple DCs so they share an identical view (#90).
    /// </summary>
    Task<CommittedSnapshot> FetchCommittedSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed class DcBackfiller(
    IServiceScopeFactory scopeFactory,
    [FromKeyedServices("compositeCache")] ICacheService compositeCache,
    IMessageProducer messageProducer,
    ILogger<DcBackfiller> logger) : IDcBackfiller
{
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
            return 0;
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
            return 0;
        }

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

        logger.LogInformation(
            "DC backfill: repaired DC {DcId} ({Mode}) from source of truth with {FlagCount} flag(s) " +
            "and {SegmentCount} segment(s).",
            dcId,
            mode,
            snapshot.Flags.Count,
            snapshot.Segments.Count);

        await PublishClientRefreshAsync(dcId);

        return snapshot.Flags.Count;
    }

    /// <summary>
    /// Fetch a fresh <see cref="CommittedSnapshot"/>: every committed flag, every committed segment,
    /// and each committed segment's resolved target env ids (a DB lookup, resolved once here —
    /// independent of any DC being repaired). Callers backfilling multiple DCs in one tick should call
    /// this ONCE and share the result (#90); see <see cref="RecoveryWorker.RunOnceAsync"/> and
    /// <see cref="CacheReconciler.RunOnceAsync"/>.
    /// </summary>
    public async Task<CommittedSnapshot> FetchCommittedSnapshotAsync(CancellationToken cancellationToken = default)
    {
        // IFeatureFlagService / ISegmentService are scoped (per-request) in DI; resolve them inside a
        // scope so a singleton caller does not capture a scoped/disposed instance.
        using var scope = scopeFactory.CreateScope();
        var featureFlagService = scope.ServiceProvider.GetRequiredService<IFeatureFlagService>();
        var segmentService = scope.ServiceProvider.GetRequiredService<ISegmentService>();

        var allCommitted = await featureFlagService.GetAllCommittedAsync();
        var allCommittedSegments = await segmentService.GetAllCommittedAsync();

        // Resolve each committed segment's target env ids once (independent of the DC being repaired).
        var segmentEnvIds = new Dictionary<string, ICollection<Guid>>(allCommittedSegments.Count);
        foreach (var segment in allCommittedSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            segmentEnvIds[segment.Id.ToString()] = await segmentService.GetEnvironmentIdsAsync(segment);
        }

        return new CommittedSnapshot(allCommitted, allCommittedSegments, segmentEnvIds);
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

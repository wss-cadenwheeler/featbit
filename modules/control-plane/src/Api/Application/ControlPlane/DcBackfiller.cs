using Application.Caches;
using Application.Configuration;
using Application.ControlPlane;
using Application.Services;
using Api.Infrastructure.Caches;
using Domain.Messages;
using Microsoft.Extensions.DependencyInjection;
using Action = Domain.Messages.Action;

namespace Api.Application.ControlPlane;

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
/// </summary>
public interface IDcBackfiller
{
    /// <summary>
    /// Backfill <paramref name="dcId"/>'s Redis from the source of truth using the write path for
    /// <paramref name="mode"/>, then publish a targeted client refresh. Returns the number of flags
    /// written (0 if the composite cache is unavailable).
    /// </summary>
    Task<int> BackfillDcAsync(string dcId, ConsistencyMode mode, CancellationToken cancellationToken = default);
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
        // StageFlagToDcAsync / UpsertFlagToDcAsync etc. are targeted writes that live on
        // CompositeRedisCacheService, not on the ICacheService contract. In the Redis path the keyed
        // "compositeCache" service is always a CompositeRedisCacheService; guard the cast so a
        // misconfiguration (e.g. None cache) degrades to a clear log instead of a crash loop.
        if (compositeCache is not CompositeRedisCacheService composite)
        {
            logger.LogWarning(
                "DC backfill requires the composite Redis cache (got {CacheType}); skipping backfill for DC {DcId}.",
                compositeCache.GetType().FullName,
                dcId);
            return 0;
        }

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
            segmentEnvIds[segment.Id.ToString()] = await segmentService.GetEnvironmentIdsAsync(segment);
        }

        foreach (var flag in allCommitted)
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

        foreach (var segment in allCommittedSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ts = new DateTimeOffset(segment.UpdatedAt).ToUnixTimeMilliseconds();
            var envIds = segmentEnvIds[segment.Id.ToString()];

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
            allCommitted.Count,
            allCommittedSegments.Count);

        await PublishClientRefreshAsync(dcId);

        return allCommitted.Count;
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

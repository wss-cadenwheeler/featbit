using Application.Caches;
using Domain.Connections;
using Domain.Environments;
using Domain.FeatureFlags;
using Domain.Health;
using Domain.Segments;
using Domain.Workspaces;
using Microsoft.Extensions.Logging;

namespace Api.Infrastructure.Caches;

/// <summary>
/// Pairs a <see cref="ICacheService"/> (one DC's Redis) with the id of the DC it
/// serves, so broadcast results can be keyed by DcId rather than ordinal index.
/// </summary>
public record DcCacheService(string DcId, ICacheService Service);

public class CompositeRedisCacheService(
    IEnumerable<DcCacheService> cacheServices,
    ILogger<CompositeRedisCacheService> logger) : ICacheService
{
    // The public ICacheService methods keep returning Task and discard the
    // per-instance result map so externally observable behavior is unchanged
    // (swallow-and-continue). The map is surfaced only via the internal
    // BroadcastAsync for a future commit coordinator.
    public Task UpsertFlagAsync(FeatureFlag flag) =>
        BroadcastAsync(s => s.UpsertFlagAsync(flag), nameof(UpsertFlagAsync));

    public Task StageFlagAsync(FeatureFlag flag, long ts) =>
        BroadcastAsync(s => s.StageFlagAsync(flag, ts), nameof(StageFlagAsync));

    public Task CommitFlagAsync(Guid envId, string flagId, long ts) =>
        BroadcastAsync(s => s.CommitFlagAsync(envId, flagId, ts), nameof(CommitFlagAsync));

    // ICacheService probe: returns the LOCAL DC's result (first instance), consistent with
    // the heartbeat/health local-first convention above. This is NOT the coordinator API —
    // the coordinator must use GetStagedDcsAsync to see EVERY DC's staged presence.
    public Task<bool> HasStagedFlagAsync(Guid id, long ts)
    {
        var local = cacheServices.First();
        return local.Service.HasStagedFlagAsync(id, ts);
    }

    public Task DeleteFlagAsync(Guid envId, Guid flagId) =>
        BroadcastAsync(s => s.DeleteFlagAsync(envId, flagId), nameof(DeleteFlagAsync));

    public Task UpsertSegmentAsync(ICollection<Guid> envIds, Segment segment) =>
        BroadcastAsync(s => s.UpsertSegmentAsync(envIds, segment), nameof(UpsertSegmentAsync));

    public Task DeleteSegmentAsync(ICollection<Guid> envIds, Guid segmentId) =>
        BroadcastAsync(s => s.DeleteSegmentAsync(envIds, segmentId), nameof(DeleteSegmentAsync));

    public Task UpsertLicenseAsync(Workspace workspace) =>
        BroadcastAsync(s => s.UpsertLicenseAsync(workspace), nameof(UpsertLicenseAsync));

    public Task UpsertSecretAsync(ResourceDescriptor resourceDescriptor, Secret secret) =>
        BroadcastAsync(s => s.UpsertSecretAsync(resourceDescriptor, secret), nameof(UpsertSecretAsync));

    public Task DeleteSecretAsync(Secret secret) =>
        BroadcastAsync(s => s.DeleteSecretAsync(secret), nameof(DeleteSecretAsync));

    public async Task<string> GetOrSetLicenseAsync(Guid workspaceId, Func<Task<string>> licenseGetter)
    {
        // Try each instance in order until one succeeds; write-through to all.
        var license = string.Empty;
        var succeeded = false;
        foreach (var dc in cacheServices)
        {
            try
            {
                license = await dc.Service.GetOrSetLicenseAsync(workspaceId, licenseGetter);
                succeeded = true;
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Redis cache operation '{Operation}' failed for DC {DcId} (implementation {CacheService}). Trying next instance.",
                    nameof(GetOrSetLicenseAsync),
                    dc.DcId,
                    dc.Service.GetType().FullName);
            }
        }

        if (!succeeded)
        {
            throw new InvalidOperationException(
                $"All Redis cache instances failed for {nameof(GetOrSetLicenseAsync)}.");
        }

        await BroadcastAsync(
            s => s.GetOrSetLicenseAsync(workspaceId, () => Task.FromResult(license)),
            nameof(GetOrSetLicenseAsync));

        return license;
    }

    public Task UpsertConnectionMadeAsync(ConnectionMessage connectionMessage) =>
        BroadcastAsync(s => s.UpsertConnectionMadeAsync(connectionMessage), nameof(UpsertConnectionMadeAsync));

    public Task DeleteConnectionMadeAsync(ConnectionMessage connectionMessage) =>
        BroadcastAsync(s => s.DeleteConnectionMadeAsync(connectionMessage), nameof(DeleteConnectionMadeAsync));

    /// <summary>
    /// Broadcasts <paramref name="action"/> to every cache instance, swallowing and
    /// logging per-instance failures so one DC's outage does not fail the others.
    /// Returns a per-DC success map (keyed by DcId) so callers (e.g. a future commit
    /// coordinator) can observe partial failure instead of having it silently swallowed.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, bool>> BroadcastAsync(
        Func<ICacheService, Task> action,
        string operationName)
    {
        var instances = cacheServices.ToList();
        var tasks = instances
            .Select(dc => ExecuteSafelyAsync(dc, action, operationName))
            .ToList();

        var outcomes = await Task.WhenAll(tasks);

        var results = new Dictionary<string, bool>(outcomes.Length);
        for (var i = 0; i < outcomes.Length; i++)
        {
            results[instances[i].DcId] = outcomes[i];
        }

        return results;
    }

    /// <summary>
    /// Coordinator-facing probe: returns, per <see cref="DcCacheService.DcId"/>, whether that
    /// DC's Redis holds the staged version <c>flag:{id}:v{ts}</c>. Iterates every configured
    /// DC (unlike the local-first <see cref="HasStagedFlagAsync"/>) with the same
    /// swallow-and-continue resilience as <see cref="BroadcastAsync"/>: a DC whose probe throws
    /// is reported as <c>false</c> rather than failing the whole call.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, bool>> GetStagedDcsAsync(Guid id, long ts)
    {
        var instances = cacheServices.ToList();
        var tasks = instances
            .Select(dc => ProbeStagedSafelyAsync(dc, id, ts))
            .ToList();

        var outcomes = await Task.WhenAll(tasks);

        var results = new Dictionary<string, bool>(outcomes.Length);
        for (var i = 0; i < outcomes.Length; i++)
        {
            results[instances[i].DcId] = outcomes[i];
        }

        return results;
    }

    /// <summary>
    /// Recovery-facing targeted write (E1): stages <paramref name="flag"/> at version
    /// <paramref name="ts"/> into ONE DC's Redis (the <see cref="DcCacheService"/> whose
    /// <see cref="DcCacheService.DcId"/> matches <paramref name="dcId"/>), unlike the broadcast
    /// <see cref="StageFlagAsync"/>. If no DC matches, logs a warning and no-ops. A failing write is
    /// swallowed and logged with the same resilience as <see cref="BroadcastAsync"/>.
    /// </summary>
    public Task StageFlagToDcAsync(string dcId, FeatureFlag flag, long ts) =>
        TargetedAsync(dcId, s => s.StageFlagAsync(flag, ts), nameof(StageFlagToDcAsync));

    /// <summary>
    /// Recovery-facing targeted write (E1): commits <paramref name="flagId"/> at version
    /// <paramref name="ts"/> into ONE DC's Redis (advancing that DC's committed pointer + index),
    /// unlike the broadcast <see cref="CommitFlagAsync"/>. If no DC matches <paramref name="dcId"/>,
    /// logs a warning and no-ops. A failing write is swallowed and logged with the same resilience
    /// as <see cref="BroadcastAsync"/>.
    /// </summary>
    public Task CommitFlagToDcAsync(string dcId, Guid envId, string flagId, long ts) =>
        TargetedAsync(dcId, s => s.CommitFlagAsync(envId, flagId, ts), nameof(CommitFlagToDcAsync));

    private async Task TargetedAsync(string dcId, Func<ICacheService, Task> action, string operationName)
    {
        var dc = cacheServices.FirstOrDefault(c => c.DcId == dcId);
        if (dc == null)
        {
            logger.LogWarning(
                "Targeted cache operation '{Operation}' requested for unknown DC {DcId}; no matching cache instance. No-op.",
                operationName,
                dcId);
            return;
        }

        await ExecuteSafelyAsync(dc, action, operationName);
    }

    private async Task<bool> ProbeStagedSafelyAsync(DcCacheService dc, Guid id, long ts)
    {
        try
        {
            return await dc.Service.HasStagedFlagAsync(id, ts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Staged-flag probe failed for DC {DcId} (implementation {CacheService}). Reporting not-staged.",
                dc.DcId,
                dc.Service.GetType().FullName);
            return false;
        }
    }

    private async Task<bool> ExecuteSafelyAsync(
        DcCacheService dc,
        Func<ICacheService, Task> action,
        string operationName)
    {
        try
        {
            await action(dc.Service);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Redis cache broadcast operation '{Operation}' failed for DC {DcId} (implementation {CacheService}). Continuing.",
                operationName,
                dc.DcId,
                dc.Service.GetType().FullName);
            return false;
        }
    }

    // Heartbeats live ONLY in the local DC's Redis (cacheServices.First()).
    // - UpsertPodHeartbeat writes to the local instance only (this method).
    // - GetAllHealthMessages reads from the local instance only.
    // - DeletePodConnection still broadcasts, because it also clears the pod's
    //   connection keys, and those were broadcast on connect by
    //   UpsertConnectionMadeAsync.
    // Each DC's control plane MUST be configured with its own Redis as
    // Redis:Instances[0] for this contract to hold.
    public Task UpsertPodHeartbeat(HealthMessage healthMessage)
    {
        var local = cacheServices.First();
        return ExecuteSafelyAsync(
            local,
            s => s.UpsertPodHeartbeat(healthMessage),
            nameof(UpsertPodHeartbeat));
    }

    public Task DeletePodConnection(Guid podId) =>
        BroadcastAsync(s => s.DeletePodConnection(podId), nameof(DeletePodConnection));

    public Task<List<HealthMessage>> GetAllHealthMessages()
    {
        var local = cacheServices.First();
        return local.Service.GetAllHealthMessages();
    }
}
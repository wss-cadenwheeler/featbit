using Application.Caches;
using Domain.Connections;
using Domain.Environments;
using Domain.FeatureFlags;
using Domain.Health;
using Domain.Segments;
using Domain.Workspaces;
using Microsoft.Extensions.Logging;

namespace Api.Infrastructure.Caches;

public class CompositeRedisCacheService(
    IEnumerable<ICacheService> cacheServices,
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
        foreach (var service in cacheServices)
        {
            try
            {
                license = await service.GetOrSetLicenseAsync(workspaceId, licenseGetter);
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
                    "Redis cache operation '{Operation}' failed for implementation {CacheService}. Trying next instance.",
                    nameof(GetOrSetLicenseAsync),
                    service.GetType().FullName);
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
    /// Returns a per-instance success map so callers (e.g. a future commit coordinator)
    /// can observe partial failure instead of having it silently swallowed.
    /// </summary>
    // TODO: key by DC id once instances carry identity (C2/C3).
    internal async Task<IReadOnlyDictionary<string, bool>> BroadcastAsync(
        Func<ICacheService, Task> action,
        string operationName)
    {
        var instances = cacheServices.ToList();
        var tasks = instances
            .Select(s => ExecuteSafelyAsync(s, action, operationName))
            .ToList();

        var outcomes = await Task.WhenAll(tasks);

        var results = new Dictionary<string, bool>(outcomes.Length);
        for (var i = 0; i < outcomes.Length; i++)
        {
            results[i.ToString()] = outcomes[i];
        }

        return results;
    }

    private async Task<bool> ExecuteSafelyAsync(
        ICacheService service,
        Func<ICacheService, Task> action,
        string operationName)
    {
        try
        {
            await action(service);
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
                "Redis cache broadcast operation '{Operation}' failed for implementation {CacheService}. Continuing.",
                operationName,
                service.GetType().FullName);
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
        return local.GetAllHealthMessages();
    }
}
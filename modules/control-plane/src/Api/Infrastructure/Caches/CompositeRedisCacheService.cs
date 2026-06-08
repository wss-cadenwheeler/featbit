using Application.Caches;
using Domain.Connections;
using Domain.Environments;
using Domain.FeatureFlags;
using Domain.Segments;
using Domain.Workspaces;
using Microsoft.Extensions.Logging;

namespace Api.Infrastructure.Caches;

public class CompositeRedisCacheService(
    IEnumerable<ICacheService> cacheServices,
    ILogger<CompositeRedisCacheService> logger) : ICacheService
{
    public Task UpsertFlagAsync(FeatureFlag flag) =>
        BroadcastAsync(s => s.UpsertFlagAsync(flag), nameof(UpsertFlagAsync));

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

    private Task BroadcastAsync(Func<ICacheService, Task> action, string operationName)
    {
        var tasks = cacheServices.Select(s => ExecuteSafelyAsync(s, action, operationName));
        return Task.WhenAll(tasks);
    }

    private async Task ExecuteSafelyAsync(
        ICacheService service,
        Func<ICacheService, Task> action,
        string operationName)
    {
        try
        {
            await action(service);
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
        }
    }
}
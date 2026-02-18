using Application.Caches;
using Domain.Environments;
using Domain.FeatureFlags;
using Domain.Segments;
using Domain.Workspaces;

namespace Api.Infrastructure.Caches;

public class CompositeRedisCacheService(IEnumerable<ICacheService> cacheServices) : ICacheService
{
    public Task UpsertFlagAsync(FeatureFlag flag) =>
        Task.WhenAll(cacheServices.Select(s => s.UpsertFlagAsync(flag)));

    public Task DeleteFlagAsync(Guid envId, Guid flagId) =>
        Task.WhenAll(cacheServices.Select(s => s.DeleteFlagAsync(envId, flagId)));

    public Task UpsertSegmentAsync(ICollection<Guid> envIds, Segment segment) =>
        Task.WhenAll(cacheServices.Select(s => s.UpsertSegmentAsync(envIds, segment)));

    public Task DeleteSegmentAsync(ICollection<Guid> envIds, Guid segmentId) =>
        Task.WhenAll(cacheServices.Select(s => s.DeleteSegmentAsync(envIds, segmentId)));

    public Task UpsertLicenseAsync(Workspace workspace) =>
        Task.WhenAll(cacheServices.Select(s => s.UpsertLicenseAsync(workspace)));

    public Task UpsertSecretAsync(ResourceDescriptor resourceDescriptor, Secret secret) =>
        Task.WhenAll(cacheServices.Select(s => s.UpsertSecretAsync(resourceDescriptor, secret)));

    public Task DeleteSecretAsync(Secret secret) =>
        Task.WhenAll(cacheServices.Select(s => s.DeleteSecretAsync(secret)));

    public async Task<string> GetOrSetLicenseAsync(Guid workspaceId, Func<Task<string>> licenseGetter)
    {
        // Read from the first instance; write-through to all
        var first = cacheServices.First();
        var license = await first.GetOrSetLicenseAsync(workspaceId, licenseGetter);

        // Ensure the rest have it too
        var rest = cacheServices.Skip(1);
        await Task.WhenAll(rest.Select(s =>
            s.GetOrSetLicenseAsync(workspaceId, () => Task.FromResult(license))));

        return license;
    }

    public async Task UpsertConnectionMade(Guid envId, string secert)
    {
        await Task.WhenAll(cacheServices.Select(s => s.UpsertConnectionMade(envId, secert)));
    }

    public async Task DeleteConnectionMade(string secert)
    {
        await Task.WhenAll(cacheServices.Select(s => s.DeleteConnectionMade(secert)));
    }
}
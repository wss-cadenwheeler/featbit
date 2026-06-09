using Domain.Environments;
using Domain.Segments;
using Domain.FeatureFlags;
using Domain.Workspaces;
using Domain.Connections;
using Domain.Health;

namespace Application.Caches;

public interface ICacheService
{
    Task UpsertFlagAsync(FeatureFlag flag);

    Task DeleteFlagAsync(Guid envId, Guid flagId);

    Task UpsertSegmentAsync(ICollection<Guid> envIds, Segment segment);

    Task DeleteSegmentAsync(ICollection<Guid> envIds, Guid segmentId);

    Task UpsertLicenseAsync(Workspace workspace);

    Task UpsertSecretAsync(ResourceDescriptor resourceDescriptor, Secret secret);

    Task DeleteSecretAsync(Secret secret);

    Task<string> GetOrSetLicenseAsync(Guid workspaceId, Func<Task<string>> licenseGetter);

    Task UpsertConnectionMadeAsync(ConnectionMessage connectionInfo);

    Task DeleteConnectionMadeAsync(ConnectionMessage connectionInfo);

    Task UpsertPodHeartbeat(HealthMessage healthMessage);

    Task DeletePodConnection(Guid podId);

    Task<List<HealthMessage>> GetAllHealthMessages();
}
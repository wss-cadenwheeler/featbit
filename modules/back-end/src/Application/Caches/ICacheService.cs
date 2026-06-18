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

    /// <summary>
    /// Stages a new flag version (B1 stage/commit storage) without moving the committed
    /// pointer or touching the env flag index, so the previously committed value stays
    /// readable until <see cref="CommitFlagAsync"/> is called.
    /// </summary>
    Task StageFlagAsync(FeatureFlag flag, long ts);

    /// <summary>
    /// Commits a previously staged flag version (B1 stage/commit storage): moves the
    /// committed pointer and advances the env flag index to <paramref name="ts"/>.
    /// </summary>
    Task CommitFlagAsync(Guid envId, string flagId, long ts);

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
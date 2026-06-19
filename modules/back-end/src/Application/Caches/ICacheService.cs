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

    /// <summary>
    /// Probes whether THIS cache holds the staged version key <c>flag:{id}:v{ts}</c>
    /// (written by <see cref="StageFlagAsync"/>). Used by the commit coordinator to
    /// confirm a staged version is present in a given DC's Redis before committing.
    /// </summary>
    Task<bool> HasStagedFlagAsync(Guid id, long ts);

    Task DeleteFlagAsync(Guid envId, Guid flagId);

    Task UpsertSegmentAsync(ICollection<Guid> envIds, Segment segment);

    /// <summary>
    /// Stages a new segment version (B2 stage/commit storage) without moving the committed
    /// pointer or touching any env segment index, so the previously committed value stays
    /// readable until <see cref="CommitSegmentAsync"/> is called. Mirrors <see cref="StageFlagAsync"/>.
    /// </summary>
    Task StageSegmentAsync(Segment segment, long ts);

    /// <summary>
    /// Commits a previously staged segment version (B2 stage/commit storage): moves the
    /// committed pointer and advances the segment index to <paramref name="ts"/> for each env
    /// in <paramref name="envIds"/> (segments can belong to multiple envs). Mirrors
    /// <see cref="CommitFlagAsync"/>.
    /// </summary>
    Task CommitSegmentAsync(ICollection<Guid> envIds, string segmentId, long ts);

    /// <summary>
    /// Probes whether THIS cache holds the staged version key <c>segment:{id}:v{ts}</c>
    /// (written by <see cref="StageSegmentAsync"/>). Used by the commit coordinator to
    /// confirm a staged version is present in a given DC's Redis before committing.
    /// Mirrors <see cref="HasStagedFlagAsync"/>.
    /// </summary>
    Task<bool> HasStagedSegmentAsync(Guid id, long ts);

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
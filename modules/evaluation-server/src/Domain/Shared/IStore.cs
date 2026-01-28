using Domain.FeatureFlags;
using Domain.Segments;

namespace Domain.Shared;

public interface IStore
{
    string Name { get; }

    Task<bool> IsAvailableAsync();

    Task<IEnumerable<byte[]>> GetFlagsAsync(Guid envId, long timestamp);

    Task<IEnumerable<byte[]>> GetFlagsAsync(IEnumerable<string> ids);

    Task<byte[]> GetSegmentAsync(string id);

    Task<IEnumerable<byte[]>> GetSegmentsAsync(Guid envId, long timestamp);

    Task<Secret?> GetSecretAsync(string secretString);
    
    Task UpsertFlagAsync(FeatureFlag flag);
    
    Task UpsertSegmentAsync(ICollection<Guid> envIds, Segment segment);
}

public interface IDbStore : IStore;
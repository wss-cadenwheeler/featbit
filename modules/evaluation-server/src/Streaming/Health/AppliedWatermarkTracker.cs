using System.Collections.Concurrent;

namespace Streaming.Health;

/// <summary>
/// Thread-safe <see cref="IAppliedWatermarkTracker"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> that keeps the maximum version
/// per environment. Registered as a singleton in the eval-server DI.
/// </summary>
public sealed class AppliedWatermarkTracker : IAppliedWatermarkTracker
{
    private readonly ConcurrentDictionary<Guid, long> _watermarks = new();

    public void Update(Guid envId, long version)
    {
        _watermarks.AddOrUpdate(
            envId,
            version,
            (_, existing) => Math.Max(existing, version)
        );
    }

    public Dictionary<Guid, long> Snapshot()
    {
        // ConcurrentDictionary enumeration is thread-safe and produces a moment-in-time view.
        return new Dictionary<Guid, long>(_watermarks);
    }
}

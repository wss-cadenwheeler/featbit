namespace Streaming.Health;

/// <summary>
/// Tracks the highest applied watermark (committed flag version, as unix-ms) this pod
/// has applied per environment. Used by the heartbeat to report how far this pod is
/// serving for each environment, for liveness / self-fence / recovery / metrics.
/// This does NOT gate commits; the commit gate is the control plane's responsibility.
/// </summary>
/// <remarks>
/// Cold-start limitation: the tracker only reflects versions seen since pod start.
/// It does not query the store/Redis to backfill. Scope is flags only; segments can be
/// folded in later.
/// </remarks>
public interface IAppliedWatermarkTracker
{
    /// <summary>
    /// Records that the pod has applied <paramref name="version"/> for the given
    /// environment. Keeps the maximum: a lower or equal version never lowers the
    /// recorded watermark. Thread-safe.
    /// </summary>
    /// <param name="envId">The environment id.</param>
    /// <param name="version">The committed flag version as unix-ms.</param>
    void Update(Guid envId, long version);

    /// <summary>
    /// Returns a point-in-time copy of the highest applied watermark per environment,
    /// keyed by environment id.
    /// </summary>
    Dictionary<Guid, long> Snapshot();
}

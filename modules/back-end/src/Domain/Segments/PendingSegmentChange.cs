namespace Domain.Segments;

/// <summary>
/// A staged-but-not-committed change to a <see cref="Segment"/>. Held alongside the
/// committed value so the authoritative (committed) read can ignore it until promotion.
/// </summary>
public class PendingSegmentChange
{
    /// <summary>
    /// Monotonic version this pending change will carry once it is promoted to committed.
    /// </summary>
    public long Version { get; set; }

    /// <summary>
    /// The staged segment value. This is NOT the authoritative value and must not be served
    /// until <see cref="Segment.PromotePending"/> makes it committed.
    /// </summary>
    public Segment Value { get; set; }
}

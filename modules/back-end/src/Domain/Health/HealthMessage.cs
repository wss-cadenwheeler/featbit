namespace Domain.Health;

public sealed record HealthMessage
{
    public required string PodId { get; init; }
    public DateTimeOffset Timestamp { get; set; }
}

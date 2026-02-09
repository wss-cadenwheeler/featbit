namespace Infrastructure.MQ.Kafka;

public class AlternativeTopics
{
    public const string SectionName = nameof(AlternativeTopics);

    public bool Enabled { get; set; }

    public required string FeatureFlagChangeTopic { get; set; }
    
    public required string SegmentChangeTopic { get; set; }
    
    public required string SecretChangeTopic { get; set; }
    
    public required string LicenseChangeTopic { get; set; }
}
namespace Domain.Messages;

public static class Topics
{
    public const string EndUser = "featbit-endusers";

    public const string FeatureFlagChange = "featbit-feature-flag-change";

    public const string SegmentChange = "featbit-segment-change";

    public const string Insights = "featbit-insights";
    
    public const string ControlPlaneFeatureFlagChange = "featbit-control-plane-feature-flag-change";
    
    public const string ControlPlaneSegmentChange = "featbit-control-plane-segment-change";
    
    public const string ControlPlaneSecretChange = "featbit-control-plane-secret-change";
    
    public const string ControlPlaneLicenseChange = "featbit-control-plane-license-change";

    public const string ConnectionMade = "featbit-connection-made";
    public const string ConnectionClosed = "featbit-connection-closed";

    public static string ToChannel(string topic) => topic switch
    {
        FeatureFlagChange => "featbit_feature_flag_change_channel",
        SegmentChange => "featbit_segment_change_channel",
        _ => throw new ArgumentOutOfRangeException(nameof(topic), topic, "Unsupported topic")
    };
}
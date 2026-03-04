namespace Domain.Messages;

public static class ControlPlaneTopics
{
    public const string ControlPlaneFeatureFlagChange = "featbit-control-plane-feature-flag-change";
    
    public const string ControlPlaneSegmentChange = "featbit-control-plane-segment-change";
    
    public const string ControlPlaneSecretChange = "featbit-control-plane-secret-change";
    
    public const string ControlPlaneLicenseChange = "featbit-control-plane-license-change";
    
    public const string PushFullSyncChange = "featbit-control-plane-push-full-sync-change";

    public const string ConnectionMade = "featbit-connection-made";
    
    public const string ConnectionClosed = "featbit-connection-closed";
    
    public const string PodHeartbeat = "featbit-pod-heartbeat";
}
using Api.Configuration;
using Domain.Health;
using Domain.Messages;
using Streaming.Health;

namespace Api.Health;

public class HeartbeatService(
    IMessageProducer messageProducer,
    ILogger<HeartbeatService> logger,
    IConfiguration configuration,
    IAppliedWatermarkTracker appliedWatermarkTracker) : BackgroundService
{
    private readonly Guid _podId = InfrastructureInfo.Id;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("HeartbeatService started with PodId: {PodId}", _podId);

        var heartbeatInterval = configuration.GetHeartbeatIntervalSeconds();

        var heartbeatTimeSpan = TimeSpan.FromSeconds(heartbeatInterval > 0 ? heartbeatInterval : 60);

        while (!stoppingToken.IsCancellationRequested)
        {
            await messageProducer.PublishAsync(Topics.PodHeartbeat, BuildHeartbeat());

            await Task.Delay(heartbeatTimeSpan, stoppingToken);
        }

        logger.LogInformation("HeartbeatService stopping with PodId: {PodId}", _podId);
    }

    /// <summary>
    /// Builds the heartbeat payload for the current pod: liveness (PodId/Timestamp),
    /// placement (DcId/Region from config), and how far this pod is serving per
    /// environment (AppliedWatermarks). Extracted for unit testing.
    /// </summary>
    internal HealthMessage BuildHeartbeat()
    {
        var snapshot = appliedWatermarkTracker.Snapshot();

        return new HealthMessage
        {
            PodId = _podId.ToString(),
            Timestamp = DateTimeOffset.UtcNow,
            Region = configuration.GetRegion(),
            DcId = configuration.GetDcId(),
            AppliedWatermarks = snapshot.Count > 0 ? snapshot : null
        };
    }
}

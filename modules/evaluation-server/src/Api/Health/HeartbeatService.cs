using Api.Configuration;
using Domain.Health;
using Domain.Messages;
using Streaming.Health;

namespace Api.Health;

public class HeartbeatService(
    IMessageProducer messageProducer,
    ILogger<HeartbeatService> logger,
    IConfiguration configuration,
    IAppliedWatermarkReader appliedWatermarkReader) : BackgroundService
{
    private readonly Guid _podId = InfrastructureInfo.Id;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("HeartbeatService started with PodId: {PodId}", _podId);

        var heartbeatInterval = configuration.GetHeartbeatIntervalSeconds();

        var heartbeatTimeSpan = TimeSpan.FromSeconds(heartbeatInterval > 0 ? heartbeatInterval : 60);

        while (!stoppingToken.IsCancellationRequested)
        {
            await messageProducer.PublishAsync(Topics.PodHeartbeat, await BuildHeartbeatAsync(stoppingToken));

            await Task.Delay(heartbeatTimeSpan, stoppingToken);
        }

        logger.LogInformation("HeartbeatService stopping with PodId: {PodId}", _podId);
    }

    /// <summary>
    /// Builds the heartbeat payload for the current pod: liveness (PodId/Timestamp),
    /// placement (DcId/Region from config), and how far this DC is serving per environment
    /// (AppliedWatermarks). The watermarks are derived from the local DC Redis flag index via
    /// <see cref="IAppliedWatermarkReader"/>, so all pods in a DC report the same value and a
    /// fresh pod is immediately correct. Extracted for testing.
    /// </summary>
    internal async Task<HealthMessage> BuildHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        var watermarks = await appliedWatermarkReader.ReadAsync(cancellationToken);

        return new HealthMessage
        {
            PodId = _podId.ToString(),
            Timestamp = DateTimeOffset.UtcNow,
            Region = configuration.GetRegion(),
            DcId = configuration.GetDcId(),
            AppliedWatermarks = watermarks.Count > 0 ? watermarks : null
        };
    }
}

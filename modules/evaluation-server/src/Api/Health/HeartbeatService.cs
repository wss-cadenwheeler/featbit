using Domain.Health;
using Domain.Messages;

namespace Api.Health;

public class HeartbeatService(IMessageProducer messageProducer, ILogger<HeartbeatService> logger, IConfiguration configuration) : BackgroundService
{
    private readonly Guid _podId = Guid.NewGuid();
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("HeartbeatService started with PodId: {PodId}", _podId);

        var heartbeatInterval = configuration.GetValue<int>("HeartbeatIntervalSeconds");

        var heartbeatTimeSpan = TimeSpan.FromSeconds(heartbeatInterval > 0 ? heartbeatInterval : 60);

        while (!stoppingToken.IsCancellationRequested)
        {
            await messageProducer.PublishAsync(Topics.PodHeartbeat, new HealthMessage
            {
                PodId = _podId.ToString(),
                Timestamp = DateTimeOffset.UtcNow
            });

            await Task.Delay(heartbeatTimeSpan, stoppingToken);
        }

        logger.LogInformation("HeartbeatService stopping with PodId: {PodId}", _podId);
    }
}

using Application.Caches;

namespace Api.Application.ControlPlane;

public class PodHealthChecker(ICacheService cacheService, ILogger<HeartbeatMessageHandler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        
        while (!stoppingToken.IsCancellationRequested)
        {
            var deadPodTimeStamp = DateTime.UtcNow.AddSeconds(-90);

            var healthMessages = await cacheService.GetAllHealthMessages();

            foreach (var healthMessage in healthMessages)
            {
                if (healthMessage.Timestamp < deadPodTimeStamp)
                {
                    logger.LogWarning("Pod {PodId} is considered unhealthy. Last heartbeat at {Timestamp}", healthMessage.PodId, healthMessage.Timestamp);
                    await cacheService.DeletePodConnection(Guid.Parse(healthMessage.PodId));
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken);
        }
    }
}

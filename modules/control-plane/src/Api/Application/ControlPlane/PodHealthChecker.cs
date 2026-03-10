using Application.Caches;

namespace Api.Application.ControlPlane;

public class PodHealthChecker(ICacheService cacheService, ILogger<HeartbeatMessageHandler> logger, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkIntervalSeconds = configuration.GetValue<int>("PodHealthCheckIntervalSeconds", 90);
        var podTimeoutSeconds = configuration.GetValue<int>("PodHealthTimeoutSeconds", 90);

        while (!stoppingToken.IsCancellationRequested)
        {
            var deadPodTimeStamp = DateTime.UtcNow.AddSeconds(podTimeoutSeconds *-1);

            var healthMessages = await cacheService.GetAllHealthMessages();

            foreach (var healthMessage in healthMessages)
            {
                if (healthMessage.Timestamp < deadPodTimeStamp)
                {
                    logger.LogWarning("Pod {PodId} is considered unhealthy. Last heartbeat at {Timestamp}", healthMessage.PodId, healthMessage.Timestamp);
                    await cacheService.DeletePodConnection(Guid.Parse(healthMessage.PodId));
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds), stoppingToken);
        }
    }
}

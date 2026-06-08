using Application.Caches;

namespace Api.Application.ControlPlane;

public class PodHealthChecker(ICacheService cacheService, ILogger<PodHealthChecker> logger, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkIntervalSeconds = configuration.GetValue("PodHealth:CheckIntervalInSeconds", 90);
        var podTimeoutSeconds = configuration.GetValue("PodHealth:TimeoutInSeconds", 90);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deadPodTimeStamp = DateTimeOffset.UtcNow.AddSeconds(-podTimeoutSeconds);

                var healthMessages = await cacheService.GetAllHealthMessages();

                foreach (var healthMessage in healthMessages)
                {
                    if (healthMessage.Timestamp >= deadPodTimeStamp)
                    {
                        continue;
                    }

                    if (!Guid.TryParse(healthMessage.PodId, out var podId))
                    {
                        logger.LogWarning(
                            "Skipping unhealthy pod with invalid PodId {PodId} (last heartbeat at {Timestamp})",
                            healthMessage.PodId, healthMessage.Timestamp);
                        continue;
                    }

                    logger.LogWarning(
                        "Pod {PodId} is considered unhealthy. Last heartbeat at {Timestamp}",
                        healthMessage.PodId, healthMessage.Timestamp);
                    await cacheService.DeletePodConnection(podId);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Pod health check iteration failed; will retry next interval");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // graceful shutdown
            }
        }
    }
}

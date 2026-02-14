using Domain.Messages;
using Infrastructure.Caches.Redis;

namespace Api.Infrastructure.MQ.Redis;

public partial class RedisMessageConsumer(
    IRedisClient redisClient,
    IServiceProvider serviceProvider,
    ILogger<RedisMessageConsumer> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {

        var tasks = new[]
        {
            ConsumeAsync(Topics.ControlPlaneFeatureFlagChange, stoppingToken),
            ConsumeAsync(Topics.ControlPlaneLicenseChange, stoppingToken),
            ConsumeAsync(Topics.ControlPlaneSecretChange, stoppingToken),
            ConsumeAsync(Topics.ControlPlaneSegmentChange, stoppingToken)
        };

        return Task.WhenAll(tasks);
    }

    public async Task ConsumeAsync(string topic, CancellationToken cancellationToken)
    {
        var redis = redisClient.GetDatabase();

        logger.LogInformation("Start consuming {Topic} messages...", topic);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // LPop json message from topic list
                var rawMessage = await redis.ListLeftPopAsync(topic);
                if (!rawMessage.HasValue)
                {
                    // If the topic doesn't exist yet or there are no messages, delay the consumer by 1 second
                    await Task.Delay(1000, cancellationToken);
                    continue;
                }

                using var scope = serviceProvider.CreateScope();
                var sp = scope.ServiceProvider;

                var handler = sp.GetKeyedService<IMessageHandler>(topic);
                if (handler == null)
                {
                    Log.NoHandlerForTopic(logger, topic);
                    continue;
                }

                var message = rawMessage.ToString();
                try
                {
                    await handler.HandleAsync(message);
                    Log.MessageHandled(logger, message);
                }
                catch (Exception ex)
                {
                    Log.ErrorConsumeMessage(logger, message, ex);
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                Log.ErrorConsumeTopic(logger, topic, ex);

                // Exception occurred while consuming topic, delay consumer by 1 second
                await Task.Delay(1000, cancellationToken);
            }
        }
    }
}

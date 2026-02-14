using Confluent.Kafka;
using Domain.Messages;

namespace Api.Infrastructure.MQ.Kafka;

public partial class KafkaMessageConsumer : BackgroundService
{
    private readonly IConsumer<Null, string> _consumer;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<KafkaMessageConsumer> _logger;

    public KafkaMessageConsumer(
        ConsumerConfig config,
        IServiceProvider serviceProvider,
        ILogger<KafkaMessageConsumer> logger)
    {
        _consumer = new ConsumerBuilder<Null, string>(config).Build();
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.Factory.StartNew(
            async () => { await StartConsumerLoop(stoppingToken); },
            TaskCreationOptions.LongRunning
        );
    }

    private async Task StartConsumerLoop(CancellationToken cancellationToken)
    {
        string[] topics =
        [
            Topics.ControlPlaneFeatureFlagChange, Topics.ControlPlaneLicenseChange, Topics.ControlPlaneSecretChange,
            Topics.ControlPlaneSegmentChange
        ];
        _consumer.Subscribe(topics);
        _logger.LogInformation("Start consuming messages for {Topics}...", string.Join(", ", topics));

        ConsumeResult<Null, string>? consumeResult = null;
        var message = string.Empty;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                consumeResult = _consumer.Consume(cancellationToken);
                if (consumeResult.IsPartitionEOF)
                {
                    continue;
                }

                message = consumeResult.Message.Value;
                if (string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }
                
                var topic = consumeResult.Topic;
                if (string.IsNullOrWhiteSpace(topic))
                {
                    continue;
                }
                
                using var scope = _serviceProvider.CreateScope();
                var sp = scope.ServiceProvider;
                
                var handler = sp.GetKeyedService<IMessageHandler>(topic);
                if (handler == null)
                {
                    Log.NoHandlerForTopic(_logger, topic);
                    continue;
                }
                
                await handler.HandleAsync(message);
                
            }
            catch (ConsumeException ex)
            {
                var error = ex.Error.ToString();
                Log.FailedConsumeMessage(_logger, message, error);

                if (ex.Error.IsFatal)
                {
                    // https://github.com/edenhill/librdkafka/blob/master/INTRODUCTION.md#fatal-consumer-errors
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                Log.ErrorConsumeMessage(_logger, message, ex);
            }
            finally
            {
                try
                {
                    if (consumeResult != null)
                    {
                        // store offset manually
                        _consumer.StoreOffset(consumeResult);
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorStoreOffset(_logger, ex);
                }
            }
        }
    }

    public override void Dispose()
    {
        // Commit offsets and leave the group cleanly.
        _consumer.Close();
        _consumer.Dispose();

        base.Dispose();
    }
}
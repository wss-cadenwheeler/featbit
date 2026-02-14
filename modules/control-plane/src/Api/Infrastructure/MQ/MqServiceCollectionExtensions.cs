using Api.Application.ControlPlane;
using Confluent.Kafka;
using Domain.Messages;
using Infrastructure;
using Infrastructure.MQ;
using KafkaMessageProducer = Api.Infrastructure.MQ.Kafka.KafkaMessageProducer;
using NoneMessageProducer = Api.Infrastructure.MQ.None.NoneMessageProducer;
using PostgresMessageProducer = Api.Infrastructure.MQ.Postgres.PostgresMessageProducer;
using RedisMessageProducer = Api.Infrastructure.MQ.Redis.RedisMessageProducer;

namespace Api.Infrastructure.MQ;

public static class MqServiceCollectionExtensions
{
    public static void AddMq(this IServiceCollection services, IConfiguration configuration)
    {
        var mqProvider = configuration.GetMqProvider();

        switch (mqProvider)
        {
            case MqProvider.None:
                AddNone();
                break;
            case MqProvider.Redis:
                AddRedis();
                break;
            case MqProvider.Kafka:
                AddKafka();
                break;
            case MqProvider.Postgres:
                AddPostgres();
                break;
        }

        services.AddKeyedTransient<IMessageHandler, FeatureFlagChangeMessageHandler>(Topics.ControlPlaneFeatureFlagChange);
        services.AddKeyedTransient<IMessageHandler, LicenseChangeMessageHandler>(Topics.ControlPlaneLicenseChange);
        services.AddKeyedTransient<IMessageHandler, SecretChangeMessageHandler>(Topics.ControlPlaneSecretChange);
        services.AddKeyedTransient<IMessageHandler, SegmentChangeMessageHandler>(Topics.ControlPlaneSegmentChange);
        return;

        void AddNone()
        {
            services.AddSingleton<IMessageProducer, NoneMessageProducer>();
        }

        void AddRedis()
        {
            services.TryAddRedis(configuration);

            services.AddSingleton<IMessageProducer, RedisMessageProducer>();
            services.AddHostedService<Redis.RedisMessageConsumer>();
        }

        void AddKafka()
        {
            var producerConfigDictionary = new Dictionary<string, string>();
            configuration.GetSection("Kafka:Producer").Bind(producerConfigDictionary);
            var producerConfig = new ProducerConfig(producerConfigDictionary);
            services.AddSingleton(producerConfig);

            var consumerConfigDictionary = new Dictionary<string, string>();
            configuration.GetSection("Kafka:Consumer").Bind(consumerConfigDictionary);
            var consumerConfig = new ConsumerConfig(consumerConfigDictionary);
            services.AddSingleton(consumerConfig);

            services.AddSingleton<IMessageProducer, KafkaMessageProducer>();
            services.AddHostedService<Kafka.KafkaMessageConsumer>();
        }

        void AddPostgres()
        {
            services.TryAddPostgres(configuration);
            
            services.AddSingleton<IMessageProducer, PostgresMessageProducer>();
            services.AddHostedService<Postgres.PostgresMessageConsumer>();
        }
    }
}
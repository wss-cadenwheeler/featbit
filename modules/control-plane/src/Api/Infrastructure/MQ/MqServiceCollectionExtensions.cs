using Api.Application.ControlPlane;
using Confluent.Kafka;
using Domain.Messages;
using Infrastructure;
using Infrastructure.Caches.Redis;
using Infrastructure.MQ;
using Infrastructure.MQ.Kafka;
using Infrastructure.MQ.None;
using Infrastructure.MQ.Postgres;
using Infrastructure.MQ.Redis;
using Npgsql;

namespace Api.Infrastructure.MQ;

public static class MqServiceCollectionExtensions
{
    public static void AddMq(this IServiceCollection services, IConfiguration configuration)
    {
        var mqProvider = configuration.GetMqProvider();
        
        var topics = new[]
        {
            Topics.ControlPlaneFeatureFlagChange, Topics.ControlPlaneLicenseChange,
            Topics.ControlPlaneSecretChange, Topics.ControlPlaneSegmentChange,
            Topics.ConnectionMade,
        };
        
        services.AddKeyedTransient<IMessageHandler, FeatureFlagChangeMessageHandler>(
            Topics.ControlPlaneFeatureFlagChange);
        services.AddKeyedTransient<IMessageHandler, LicenseChangeMessageHandler>(Topics.ControlPlaneLicenseChange);
        services.AddKeyedTransient<IMessageHandler, SecretChangeMessageHandler>(Topics.ControlPlaneSecretChange);
        services.AddKeyedTransient<IMessageHandler, SegmentChangeMessageHandler>(Topics.ControlPlaneSegmentChange);
        services.AddKeyedTransient<IMessageHandler, ClientConnectionMadeHandler>(Topics.ConnectionMade);

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
        
        return;

        void AddNone()
        {
            services.AddSingleton<IMessageProducer, NoneMessageProducer>();
        }

        void AddRedis()
        {
            services.TryAddRedis(configuration);

            services.AddSingleton<IMessageProducer, RedisMessageProducer>();
            services.AddHostedService(sp =>
            {
                var redisClient = sp.GetRequiredService<IRedisClient>();
                var logger = sp.GetRequiredService<ILogger<RedisMessageConsumer>>();

                return new RedisMessageConsumer(redisClient, sp, logger, topics);
            });
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

            services.AddHostedService(sp =>
            {
                var cfg = sp.GetRequiredService<ConsumerConfig>();
                var logger = sp.GetRequiredService<ILogger<KafkaMessageConsumer>>();
                var provider = sp.GetRequiredService<IServiceProvider>();

                return new KafkaMessageConsumer(cfg, provider, logger, topics);
            });
        }

        void AddPostgres()
        {
            services.TryAddPostgres(configuration);

            services.AddSingleton<IMessageProducer, PostgresMessageProducer>(sp =>
            {
                var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
                var logger = sp.GetRequiredService<ILogger<PostgresMessageProducer>>();

                return new PostgresMessageProducer(dataSource, logger, []);
            });
            services.AddHostedService(sp =>
            {
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
                var logger = sp.GetRequiredService<ILogger<PostgresMessageConsumer>>();

                return new PostgresMessageConsumer(scopeFactory, dataSource, logger, topics);
            });
        }
    }
}
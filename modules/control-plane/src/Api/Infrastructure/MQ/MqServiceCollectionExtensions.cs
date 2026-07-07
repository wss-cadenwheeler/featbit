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
    /// <summary>
    /// The <c>Kafka:Consumer:group.id</c> shipped in appsettings.json. Two control planes that
    /// both consume this unmodified default from the same Kafka broker collide: Kafka hands each
    /// topic-partition to exactly one member of the group, so only one DC's control plane
    /// processes flag/segment changes and heartbeats while the other silently idles (#100).
    /// </summary>
    public const string DefaultConsumerGroupId = "featbit-control-plane";

    /// <summary>
    /// Derives the effective Kafka consumer group id from the bound <c>Kafka:Consumer</c> config
    /// and the local DC identity (<c>Redis:Instances:0:DcId</c>).
    /// <list type="bullet">
    /// <item>Operator set a custom (non-default) group id: returned unchanged. Explicit
    /// configuration always wins — this is what <c>Deploy-FeatBitClusters.ps1</c> relies on when
    /// it sets a per-cluster literal group id directly.</item>
    /// <item>Group id is still the shipped default AND a non-empty local DcId is configured:
    /// suffix with "-{DcId}" so control planes in different DCs sharing a broker land in
    /// different consumer groups by default, without requiring an explicit override.</item>
    /// <item>Group id is the shipped default and no DcId is configured (single-DC/legacy
    /// deployments): returned unchanged — today's behavior is preserved.</item>
    /// </list>
    /// This is a pure function (no logging) so it stays trivially unit-testable; effective group
    /// id is observable at startup via the Kafka consumer's own logging.
    /// </summary>
    public static string ResolveConsumerGroupId(IConfiguration configuration, string boundGroupId)
    {
        if (boundGroupId != DefaultConsumerGroupId)
        {
            return boundGroupId;
        }

        var dcId = configuration["Redis:Instances:0:DcId"];
        return string.IsNullOrEmpty(dcId) ? boundGroupId : $"{boundGroupId}-{dcId}";
    }

    public static void AddMq(this IServiceCollection services, IConfiguration configuration)
    {
        var mqProvider = configuration.GetMqProvider();
        
        var topics = new[]
        {
            ControlPlaneTopics.ControlPlaneFeatureFlagChange, ControlPlaneTopics.ControlPlaneLicenseChange,
            ControlPlaneTopics.ControlPlaneSecretChange, ControlPlaneTopics.ControlPlaneSegmentChange,
            ControlPlaneTopics.ConnectionMade, ControlPlaneTopics.ConnectionClosed, ControlPlaneTopics.PodHeartbeat,
        };
        
        services.AddKeyedTransient<IMessageHandler, FeatureFlagChangeMessageHandler>(
            ControlPlaneTopics.ControlPlaneFeatureFlagChange);
        services.AddKeyedTransient<IMessageHandler, LicenseChangeMessageHandler>(ControlPlaneTopics.ControlPlaneLicenseChange);
        services.AddKeyedTransient<IMessageHandler, SecretChangeMessageHandler>(ControlPlaneTopics.ControlPlaneSecretChange);
        services.AddKeyedTransient<IMessageHandler, SegmentChangeMessageHandler>(ControlPlaneTopics.ControlPlaneSegmentChange);
        services.AddKeyedTransient<IMessageHandler, ClientConnectionMadeHandler>(ControlPlaneTopics.ConnectionMade);
        services.AddKeyedTransient<IMessageHandler, ClientConnectionClosedHandler>(ControlPlaneTopics.ConnectionClosed);
        services.AddKeyedTransient<IMessageHandler, HeartbeatMessageHandler>(ControlPlaneTopics.PodHeartbeat);
      
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

            // Resolve a per-DC group.id when the operator left the shipped default in place
            // (#100). A custom group.id explicitly configured — e.g. by
            // Deploy-FeatBitClusters.ps1's per-cluster literals — is never touched.
            // No ILogger is available yet at this point in service registration, so the
            // resolved value isn't logged here; KafkaMessageConsumer's own "Start consuming
            // messages for {Topics}..." startup log plus the Confluent.Kafka client's debug
            // logging make the effective group.id observable without adding a registration-time
            // logging dependency for what is a pure, easily unit-tested config transform.
            if (!string.IsNullOrEmpty(consumerConfig.GroupId))
            {
                consumerConfig.GroupId = ResolveConsumerGroupId(configuration, consumerConfig.GroupId);
            }

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
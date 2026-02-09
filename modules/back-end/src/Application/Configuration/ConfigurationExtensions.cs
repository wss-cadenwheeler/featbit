using Infrastructure.MQ.Kafka;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection.Configuration;

public static class ConfigurationExtensions
{
    public static AlternativeTopics? GetKafkaAlternativeTopicsConfiguration(this IConfiguration configuration)
    {
        return configuration.GetSection(AlternativeTopics.SectionName).Get<AlternativeTopics>();
    }
}
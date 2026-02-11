using System.Text.Json;
using Application.Caches;
using Domain.EndUsers;
using Domain.FeatureFlags;
using Domain.Messages;
using Domain.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.MQ.ControlPlane;

public class FeatureFlagChangeMessageHandler([FromKeyedServices("compositeCache")] ICacheService cacheService, IMessageProducer messageProducer) : IMessageHandler
{
    public string Topic => Topics.ControlPlaneFeatureFlagChange;

    public async Task HandleAsync(string message)
    {
        var flag = JsonSerializer.Deserialize<FeatureFlag>(message, ReusableJsonSerializerOptions.Web);
        if (flag != null)
        {
            await cacheService.UpsertFlagAsync(flag);
            await messageProducer.PublishAsync(Topics.FeatureFlagChange, flag);
        }
    }
}
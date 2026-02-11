using System.Text.Json;
using Application.Caches;
using Domain.EndUsers;
using Domain.FeatureFlags;
using Domain.Messages;
using Domain.Utils;

namespace Infrastructure.MQ.ControlPlane;

public class FeatureFlagChangeMessageHandler(ICacheService cacheService, IMessageProducer messageProducer) : IMessageHandler
{
    public string Topic => Topics.ControlPlaneFeatureFlagChange;

    public async Task HandleAsync(string message)
    {
        var flag = JsonSerializer.Deserialize<FeatureFlag>(message, ReusableJsonSerializerOptions.Web);
        if (flag != null)
        {
            // TODO: Upsert to all Redis Instances
            await cacheService.UpsertFlagAsync(flag);
            await messageProducer.PublishAsync(Topics.FeatureFlagChange, flag);
        }
    }
}
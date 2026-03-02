using System.Text.Json;
using Application.Caches;
using Domain.FeatureFlags;
using Domain.Messages;
using Domain.Utils;

namespace Api.Application.ControlPlane;

public class FeatureFlagChangeMessageHandler([FromKeyedServices("compositeCache")] ICacheService cacheService, IMessageProducer messageProducer, ILogger<FeatureFlagChangeMessageHandler> logger) : IMessageHandler
{
    public string Topic => ControlPlaneTopics.ControlPlaneFeatureFlagChange;

    public async Task HandleAsync(string message)
    {
        try
        {
            var flag = JsonSerializer.Deserialize<FeatureFlag>(message, ReusableJsonSerializerOptions.Web);
            if (flag != null)
            {
                await cacheService.UpsertFlagAsync(flag);
                await messageProducer.PublishAsync(Topics.FeatureFlagChange, flag);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error handling feature flag change message");
            throw;
        }

    }
}
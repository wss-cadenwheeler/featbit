using System.Text.Json;
using Application.Caches;
using Application.Configuration;
using Application.FeatureFlags;
using Domain.FeatureFlags;
using Domain.Messages;
using Domain.Utils;

namespace Api.Application.ControlPlane;

public class FeatureFlagChangeMessageHandler([FromKeyedServices("compositeCache")] ICacheService cacheService, IMessageProducer messageProducer, ILogger<FeatureFlagChangeMessageHandler> logger, IConfiguration configuration) : IMessageHandler
{
    public string Topic => ControlPlaneTopics.ControlPlaneFeatureFlagChange;

    public async Task HandleAsync(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!root.TryGetProperty("notification", out var notification) ||
                !root.TryGetProperty("region", out var region))
            {
                throw new InvalidDataException("invalid flag change data");
            }
            var deserializedFlagNotification = notification.Deserialize<OnFeatureFlagChanged>(ReusableJsonSerializerOptions.Web);
            if (deserializedFlagNotification != null)
            {
                await cacheService.UpsertFlagAsync(deserializedFlagNotification.Flag);
                await messageProducer.PublishAsync(Topics.FeatureFlagChange, deserializedFlagNotification.Flag);
            }
            var deserializedRegion = region.Deserialize<string>(ReusableJsonSerializerOptions.Web);
            if (deserializedRegion != null && deserializedRegion == configuration.GetRegion())
            {
                var webHooksMessage = new { notification = deserializedFlagNotification, region = deserializedRegion, type = ControlPlaneWebHookType.FeatureFlag };
                await messageProducer.PublishAsync(ControlPlaneTopics.ControlPlaneWebHooks, webHooksMessage);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error handling feature flag change message");
            throw;
        }

    }
}
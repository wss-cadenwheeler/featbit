using System.Text.Json;
using Application.Caches;
using Application.Configuration;
using Application.Segments;
using Application.Services;
using Domain.Messages;
using Domain.Segments;
using Domain.Utils;

namespace Api.Application.ControlPlane;

public class SegmentChangeMessageHandler(
    [FromKeyedServices("compositeCache")] ICacheService cacheService,
    IFeatureFlagAppService featureFlagAppService,
    ISegmentMessageService segmentMessageService,
    ILogger<SegmentChangeMessageHandler> logger,
    IConfiguration configuration,
    IMessageProducer messageProducer) : IMessageHandler
{
    public string Topic => ControlPlaneTopics.ControlPlaneSegmentChange;

    public async Task HandleAsync(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!root.TryGetProperty("segmentNonSpecific", out var segmentNonSpecific) ||
                !root.TryGetProperty("envIds", out var envIds) ||
                !root.TryGetProperty("notification", out var notification) ||
                !root.TryGetProperty("region", out var region))
            {
                throw new InvalidDataException("invalid segment change data");
            }

            var deserializedSegmentNonEnvironmentSpecificNode =
                segmentNonSpecific.Deserialize<Segment>(ReusableJsonSerializerOptions.Web);
            var deserializedEnvIdsNode = envIds.Deserialize<ICollection<Guid>>(ReusableJsonSerializerOptions.Web);
            var deserializedNotificationNode =
                notification.Deserialize<OnSegmentChange>(ReusableJsonSerializerOptions.Web);
            var deserializedRegionNode = region.Deserialize<string>(ReusableJsonSerializerOptions.Web);

            if (deserializedSegmentNonEnvironmentSpecificNode is not null && deserializedEnvIdsNode is not null &&
                deserializedNotificationNode is not null && deserializedRegionNode is not null &&
                deserializedRegionNode == configuration.GetRegion())
            {
                await cacheService
                    .UpsertSegmentAsync(deserializedEnvIdsNode, deserializedSegmentNonEnvironmentSpecificNode);

                foreach (var envId in deserializedEnvIdsNode)
                {
                    var affectedFlags =
                        await segmentMessageService.GetAffectedFlagsAsync(envId, deserializedNotificationNode);

                    // update affected flags
                    if (affectedFlags.Count > 0)
                    {
                        await featureFlagAppService.OnSegmentUpdatedAsync(deserializedSegmentNonEnvironmentSpecificNode,
                            deserializedNotificationNode.OperatorId, affectedFlags);
                    }

                    // publish segment change message
                    await segmentMessageService.PublishSegmentChangeMessage(envId, affectedFlags,
                        deserializedSegmentNonEnvironmentSpecificNode);
                }

                var webHooksMessage = new
                {
                    notification = deserializedNotificationNode, envIds = deserializedEnvIdsNode,
                    region = deserializedRegionNode, type = ControlPlaneWebHookType.Segment
                };
                await messageProducer.PublishAsync(ControlPlaneTopics.ControlPlaneWebHooks, webHooksMessage);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error processing segment change message");
            throw;
        }
    }
}
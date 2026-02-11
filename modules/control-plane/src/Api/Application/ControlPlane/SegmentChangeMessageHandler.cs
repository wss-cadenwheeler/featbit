using System.Text.Json;
using Application.Caches;
using Application.Segments;
using Application.Services;
using Domain.Messages;
using Domain.Segments;

namespace Api.Application.ControlPlane;

public class SegmentChangeMessageHandler(
    [FromKeyedServices("compositeCache")] ICacheService cacheService,
    IFeatureFlagAppService featureFlagAppService,
    ISegmentMessageService segmentMessageService) : IMessageHandler
{
    public string Topic => Topics.ControlPlaneSegmentChange;

    public async Task HandleAsync(string message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        if (!root.TryGetProperty("segmentNonSpecific", out var segmentNonSpecific) ||
            !root.TryGetProperty("envIds", out var envIds) ||
            !root.TryGetProperty("notification", out var notification))
        {
            throw new InvalidDataException("invalid segment change data");
        }

        var deserializedSegmentNonEnvironmentSpecificNode = segmentNonSpecific.Deserialize<Segment>();
        var deserializedEnvIdsNode = envIds.Deserialize<ICollection<Guid>>();
        var deserializedNotificationNode = notification.Deserialize<OnSegmentChange>();
        if (deserializedSegmentNonEnvironmentSpecificNode is not null && deserializedEnvIdsNode is not null &&
            deserializedNotificationNode is not null)
        {
            await cacheService
                .UpsertSegmentAsync(deserializedEnvIdsNode, deserializedSegmentNonEnvironmentSpecificNode);

            foreach (var envId in deserializedEnvIdsNode)
            {
                var affectedFlags = await segmentMessageService.GetAffectedFlagsAsync(envId, deserializedNotificationNode);

                // update affected flags
                if (affectedFlags.Count > 0)
                {
                    await featureFlagAppService.OnSegmentUpdatedAsync(deserializedSegmentNonEnvironmentSpecificNode,
                        deserializedNotificationNode.OperatorId, affectedFlags);
                }

                // publish segment change message
                await segmentMessageService.PublishSegmentChangeMessage(envId, affectedFlags, deserializedSegmentNonEnvironmentSpecificNode);
            }
        }
    }
}
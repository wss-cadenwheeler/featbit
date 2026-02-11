using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Caches;
using Application.Segments;
using Domain.AuditLogs;
using Domain.FeatureFlags;
using Domain.Messages;
using Domain.Segments;
using Domain.Utils;

namespace Infrastructure.MQ.ControlPlane;

public class SegmentChangeMessageHandler(
    ICacheService cacheService,
    IMessageProducer messageProducer,
    IFeatureFlagAppService featureFlagAppService,
    ISegmentService segmentService,
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
            // TODO: Upsert to all Redis Instances
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
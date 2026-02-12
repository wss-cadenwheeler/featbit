using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Caches;
using Application.Configuration;
using Domain.AuditLogs;
using Domain.Messages;
using Domain.Segments;
using Domain.Utils;
using Microsoft.Extensions.Configuration;

namespace Application.Segments;

public class OnSegmentChange : INotification
{
    public Segment Segment { get; set; }

    public string Operation { get; set; }

    public DataChange DataChange { get; set; }

    public Guid OperatorId { get; set; }

    public string Comment { get; set; }

    public bool IsTargetingChange { get; set; }

    public OnSegmentChange(
        Segment segment,
        string operation,
        DataChange dataChange,
        Guid operatorId,
        string comment = "",
        bool isTargetingChange = false)
    {
        Segment = segment;
        Operation = operation;
        DataChange = dataChange;
        OperatorId = operatorId;
        Comment = comment;
        IsTargetingChange = isTargetingChange;
    }

    public AuditLog GetAuditLog()
    {
        var auditLog = AuditLog.For(Segment, Operation, DataChange, Comment, OperatorId);

        return auditLog;
    }
}

public class OnSegmentChangeHandler(
    ISegmentService segmentService,
    IMessageProducer messageProducer,
    ICacheService cache,
    IAuditLogService auditLogService,
    IFeatureFlagAppService featureFlagAppService,
    IWebhookHandler webhookHandler,
    IConfiguration configuration,
    ISegmentMessageService segmentMessageService)
    : INotificationHandler<OnSegmentChange>
{
    public async Task Handle(OnSegmentChange notification, CancellationToken cancellationToken)
    {
        // write audit log
        await auditLogService.AddOneAsync(notification.GetAuditLog());

        var segment = notification.Segment;
        var envIds = await segmentService.GetEnvironmentIdsAsync(segment);

        // update cache
        await cache.UpsertSegmentAsync(envIds, segment);

        if (configuration.UseControlPlane())
        {
            var segmentNonEnvironmentSpecificNode = JsonSerializer.SerializeToNode(segment, ReusableJsonSerializerOptions.Web);
            var envIdsNode = JsonSerializer.SerializeToNode(envIds, ReusableJsonSerializerOptions.Web);
            var notificationNode = JsonSerializer.SerializeToNode(notification, ReusableJsonSerializerOptions.Web);

            JsonObject segmentUpsertMessage = new()
            {
                ["segmentNonSpecific"] = segmentNonEnvironmentSpecificNode,
                ["envIds"] = envIdsNode,
                ["notification"] = notificationNode
            };
            
            await messageProducer.PublishAsync(Topics.ControlPlaneSegmentChange, segmentUpsertMessage);
        }
        else
        {
            foreach (var envId in envIds)
            {
                var affectedFlags = await segmentMessageService.GetAffectedFlagsAsync(envId, notification);

                // update affected flags
                if (affectedFlags.Count > 0)
                {
                    await featureFlagAppService.OnSegmentUpdatedAsync(segment, notification.OperatorId, affectedFlags);
                }

                // publish segment change message
                await segmentMessageService.PublishSegmentChangeMessage(envId, affectedFlags, segment);
            }
        }

        foreach (var envId in envIds)
        {
            // handle webhook asynchronously
            _ = webhookHandler.HandleAsync(
                envId,
                segment,
                notification.DataChange,
                notification.OperatorId
            );
        }

        return;


    }
}
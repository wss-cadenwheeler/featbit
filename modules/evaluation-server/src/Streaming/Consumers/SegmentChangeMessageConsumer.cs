using System.Text.Json;
using Domain.Messages;
using Domain.Segments;
using Domain.Shared;
using Infrastructure;
using Infrastructure.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Streaming.Connections;
using Streaming.Protocol;
using Streaming.Services;


namespace Streaming.Consumers;

public class SegmentChangeMessageConsumer(
    IConnectionManager connectionManager,
    IDataSyncService dataSyncService,
    ILogger<SegmentChangeMessageConsumer> logger,
    IStore store,
    IConfiguration configuration)
    : IMessageConsumer
{
    public string Topic => Topics.SegmentChange;

    public async Task HandleAsync(string message, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        if (!root.TryGetProperty("segment", out var segment) ||
            !root.TryGetProperty("affectedFlagIds", out var affectedFlagIds))
        {
            throw new InvalidDataException("invalid segment change data");
        }

        var envId = segment.GetProperty("envId").GetGuid();
        var flagIds = affectedFlagIds.Deserialize<string[]>()!;


        if (configuration.GetRedisShouldUpsertState() && store is HybridStore &&
            root.TryGetProperty("segmentNonSpecific", out var segmentNonSpecific) &&
            root.TryGetProperty("envIds", out var envIds))
        {
            try
            {
                if (segmentNonSpecific.Deserialize<Segment>(ReusableJsonSerializerOptions.Web) is { } segmentObj &&
                    envIds.Deserialize<ICollection<Guid>>(ReusableJsonSerializerOptions.Web) is { } envIdsCollection)
                {
                    await store.UpsertSegmentAsync(envIdsCollection, segmentObj).ConfigureAwait(false);
                }
            }
            catch (JsonException ex)
            {
                logger.LogError(
                    ex,
                    "Exception occurred deserializing segment change message."
                );
            }
        }

        var connections = connectionManager.GetEnvConnections(envId);
        foreach (var connection in connections)
        {
            try
            {
                if (connection.Type == ConnectionType.Client && flagIds.Length == 0)
                {
                    continue;
                }

                var payload = await dataSyncService.GetSegmentChangePayloadAsync(connection, segment, flagIds);
                var serverMessage = new ServerMessage(MessageTypes.DataSync, payload);

                await connection.SendAsync(serverMessage, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Exception occurred while processing segment change message for connection {ConnectionId} in env {EnvId}.",
                    connection.Id,
                    envId
                );
            }
        }
    }
}
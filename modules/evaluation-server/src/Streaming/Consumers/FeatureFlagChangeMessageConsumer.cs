using System.Text.Json;
using Domain.FeatureFlags;
using Domain.Messages;
using Domain.Shared;
using Infrastructure.Store;
using Microsoft.Extensions.Logging;
using Streaming.Connections;
using Streaming.Protocol;
using Streaming.Services;

namespace Streaming.Consumers;

public class FeatureFlagChangeMessageConsumer(
    IConnectionManager connectionManager,
    IDataSyncService dataSyncService,
    ILogger<FeatureFlagChangeMessageConsumer> logger,
    IStore store)
    : IMessageConsumer
{
    public string Topic => Topics.FeatureFlagChange;

    public async Task HandleAsync(string message, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(message);
        var flag = document.RootElement;

        var envId = flag.GetProperty("envId").GetGuid();
        
        
        if (store is HybridStore)
        {
            try
            {
                var deserializedFlag = JsonSerializer.Deserialize<FeatureFlag>(message, ReusableJsonSerializerOptions.Web);
                if (deserializedFlag is { } flagObj)
                {
                    await store.UpsertFlagAsync(flagObj).ConfigureAwait(false);
                }
            }
            catch (JsonException ex)
            {
                logger.LogError(
                    ex,
                    "Exception occurred deserializing flag change message."
                );
            }
        }

        var connections = connectionManager.GetEnvConnections(envId);
        foreach (var connection in connections)
        {
            try
            {
                var payload = await dataSyncService.GetFlagChangePayloadAsync(connection, flag);
                var serverMessage = new ServerMessage(MessageTypes.DataSync, payload);

                await connection.SendAsync(serverMessage, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Exception occurred while processing feature flag change message for connection {ConnectionId} in env {EnvId}.",
                    connection.Id,
                    envId
                );
            }
        }
    }
}
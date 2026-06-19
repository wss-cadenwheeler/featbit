using System.Text.Json;
using Domain.Messages;
using Microsoft.Extensions.Logging;
using Streaming.Connections;
using Streaming.Health;
using Streaming.Protocol;
using Streaming.Services;

namespace Streaming.Consumers;

public class FeatureFlagChangeMessageConsumer(
    IConnectionManager connectionManager,
    IDataSyncService dataSyncService,
    IAppliedWatermarkTracker appliedWatermarkTracker,
    ILogger<FeatureFlagChangeMessageConsumer> logger)
    : IMessageConsumer
{
    public string Topic => Topics.FeatureFlagChange;

    public async Task HandleAsync(string message, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(message);
        var flag = document.RootElement;

        var envId = flag.GetProperty("envId").GetGuid();

        // Record the applied watermark for this env. The version is the flag's updatedAt as
        // unix-ms, consistent with the Redis flag index (see ClientSdkFlag). The tracker keeps
        // the max per env. Scope: flags only — segments can be folded in later.
        if (flag.TryGetProperty("updatedAt", out var updatedAt) &&
            updatedAt.TryGetDateTimeOffset(out var updatedAtValue))
        {
            appliedWatermarkTracker.Update(envId, updatedAtValue.ToUnixTimeMilliseconds());
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

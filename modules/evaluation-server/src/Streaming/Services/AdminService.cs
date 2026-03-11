using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Streaming.Connections;
using Streaming.Protocol;

namespace Streaming.Services;

public class AdminService(IConnectionManager connectionManager, IDataSyncService dataSyncService, ILogger<AdminService> logger)
    : IAdminService
{
    public async Task PushFullSyncToAllActiveClients()
    {
        var connections = connectionManager.GetAllConnections();
        var groupedByEnv = connections.GroupBy(c => c.EnvId);

        foreach (var group in groupedByEnv)
        {
            var envId = group.Key;
            var payload = await dataSyncService.GetServerSdkPayloadAsync(envId, 0);
            payload.EventType = DataSyncEventTypes.Full;
                
            var serverMessage = new ServerMessage(MessageTypes.DataSync, payload);

            // Send to all connections in this environment in parallel or sequentially
            var failures = new ConcurrentBag<(string ConnectionId, Exception Exception)>();

            var tasks = connections.Select(async connection =>
            {
                try
                {
                    await connection.SendAsync(serverMessage, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    failures.Add((connection.Id, ex));
                }
            });

            await Task.WhenAll(tasks);
            
            foreach (var (connectionId, ex) in failures)
            {
                logger.LogError(ex, "Send failed for connection {ConnectionId}", connectionId);
            }
        }
    }
}
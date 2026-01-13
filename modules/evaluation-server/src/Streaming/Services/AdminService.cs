using Streaming.Connections;
using Streaming.Protocol;

namespace Streaming.Services;

public class AdminService(IConnectionManager connectionManager, IDataSyncService dataSyncService)
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
            var tasks = group.Select(connection => connection.SendAsync(serverMessage, CancellationToken.None));
            await Task.WhenAll(tasks);
        }
    }
}
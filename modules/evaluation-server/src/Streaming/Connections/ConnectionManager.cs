using System.Collections.Concurrent;
using Domain.Connection;
using Domain.Messages;
using Microsoft.Extensions.Logging;

namespace Streaming.Connections;

public sealed partial class ConnectionManager(ILogger<ConnectionManager> logger, IMessageProducer producer) : IConnectionManager
{
    internal readonly ConcurrentDictionary<string, Connection> Connections = new(StringComparer.Ordinal);
  
    public async Task Add(ConnectionContext context)
    {
        bool connectionAdded = false;

        if (context.Type == ConnectionType.RelayProxy)
        {
            foreach (var connection in context.MappedRpConnections)
            { 
               connectionAdded = Connections.TryAdd(connection.Id, connection);
                
                if (connectionAdded)
                {
                    await producer.PublishAsync(Topics.FeatbitConnectionMade, ConnectionMessage.CreateConnectionMadeMessage(connection.Id, connection.EnvId, connection.Secret.ProjectKey));
                }
            }
        }
        else
        {
            connectionAdded = Connections.TryAdd(context.Connection.Id, context.Connection);

            if (connectionAdded)
            {
                await producer.PublishAsync(Topics.FeatbitConnectionMade, ConnectionMessage.CreateConnectionMadeMessage(context.Connection.Id, context.Connection.EnvId, context.Connection.Secret.ProjectKey));
            }

        }

        Log.ConnectionAdded(logger, context);
    }

    public async Task Remove(ConnectionContext context)
    {
        bool connectionRemoved = false;

        if (context.Type == ConnectionType.RelayProxy)
        {
            foreach (var mappedConnection in context.MappedRpConnections)
            {
             connectionRemoved=   Connections.TryRemove(mappedConnection.Id, out _);

                if (connectionRemoved)
                {
                    await producer.PublishAsync(Topics.FeatbitConnectionClosed, ConnectionMessage.CreateConnectionClosedMessage(context.Connection.Id, context.Connection.EnvId, mappedConnection.Secret.ProjectKey));
                }
            }
        }
        else
        {
            connectionRemoved = Connections.TryRemove(context.Connection.Id, out _);

            if (connectionRemoved)
            {
                await producer.PublishAsync(Topics.FeatbitConnectionClosed, ConnectionMessage.CreateConnectionClosedMessage(context.Connection.Id, context.Connection.EnvId, context.Connection.Secret.ProjectKey));
            }

        }

        context.MarkAsClosed();

        Log.ConnectionRemoved(logger, context);
    }

    public ICollection<Connection> GetEnvConnections(Guid envId)
    {
        var connections = new List<Connection>();

        // the enumerator returned from the concurrent dictionary is safe to use concurrently with reads and writes to the dictionary
        // see https://learn.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentdictionary-2.getenumerator?view=net-6.0
        foreach (var entry in Connections)
        {
            var connection = entry.Value;
            if (connection.EnvId == envId)
            {
                connections.Add(connection);
            }
        }

        return connections;
    }
        
    public ICollection<Connection> GetAllConnections()
    {
        return Connections.Values;
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Domain.Health;

namespace Domain.Connection
{
    public enum ConnectionMessageType
    {
        ConnectionMade = 1,
        ConnectionClosed = 2
    }

    public class ConnectionMessage
    {
        public ConnectionMessageType Type { get; init; }
        public Guid EnvId { get; init; }
        public required string Secret { get; init; }
        public required string Id { get; init;  }
        public string HeartbeatId { get; init; } = InfrastructureInfo.Id.ToString();

        public static ConnectionMessage CreateConnectionMadeMessage(string connectionId, Guid envId, string secert)
        {
            return new ConnectionMessage
            {
                Type = ConnectionMessageType.ConnectionMade,
                EnvId = envId,
                Secret = secert,
                Id = connectionId
            };
        }

        public static ConnectionMessage CreateConnectionClosedMessage(string connectionId, Guid envId, string secert)
        {
            return new ConnectionMessage
            {
                Type = ConnectionMessageType.ConnectionClosed,
                EnvId = envId,
                Secret = secert,
                Id = connectionId
            };
        }
    }
}

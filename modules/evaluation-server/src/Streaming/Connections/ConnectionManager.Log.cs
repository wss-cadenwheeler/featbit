using Microsoft.Extensions.Logging;

namespace Streaming.Connections;

partial class ConnectionManager
{
    public static partial class Log
    {
        [LoggerMessage(1, LogLevel.Trace, "Connection added", EventName = "ConnectionAdded")]
        public static partial void ConnectionAdded(
            ILogger logger,
            [TagProvider(typeof(ConnectionContextTagProvider), nameof(ConnectionContextTagProvider.RecordTags))]
            ConnectionContext connection
        );

        [LoggerMessage(2, LogLevel.Trace, "Connection removed", EventName = "ConnectionRemoved")]
        public static partial void ConnectionRemoved(
            ILogger logger,
            [TagProvider(typeof(ConnectionContextTagProvider), nameof(ConnectionContextTagProvider.RecordTags))]
            ConnectionContext connection
        ); 
        
        [LoggerMessage(3, LogLevel.Error, "Connection could not be added. Connection ID: {connectionId}", EventName = "ConnectionAdditionFailure")]
        public static partial void ConnectionCouldNotBeAdded(
            ILogger logger,
            string connectionId,
            Exception exception
        );
        
        [LoggerMessage(4, LogLevel.Error, "Connection could not be removed. Connection ID: {connectionId}", EventName = "ConnectionRemovalFailure")]
        public static partial void ConnectionCouldNotBeRemoved(
            ILogger logger,
            string connectionId,
            Exception exception
        );
    }
}
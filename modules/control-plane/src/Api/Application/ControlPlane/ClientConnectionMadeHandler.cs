using System.Text.Json;
using Application.Caches;
using Domain.Connections;
using Domain.Messages;

namespace Api.Application.ControlPlane;

public class ClientConnectionMadeHandler(ICacheService cacheService, ILogger<ClientConnectionMadeHandler> logger) : IMessageHandler
{
    public string Topic => Topics.ConnectionMade;

    public async Task HandleAsync(string message)
    {
        logger.LogInformation($"Handling connection made message: {message}");

        var connectionInfo = JsonSerializer.Deserialize<ConnectionMessage>(message);

        if (connectionInfo == null)
        {
            logger.LogError("Failed to deserialize connection message: {Message}", message);
            return;
        }

        await cacheService.UpsertConnectionMadeAsync(connectionInfo);
    }
}

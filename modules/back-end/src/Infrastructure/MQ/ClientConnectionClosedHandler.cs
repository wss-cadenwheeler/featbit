using System.Text.Json;
using Application.Caches;
using Domain.Connections;
using Domain.Messages;
using Microsoft.Extensions.Logging;

namespace Infrastructure.MQ;

public class ClientConnectionClosedHandler(ICacheService cacheService, ILogger<ClientConnectionClosedHandler> logger) : IMessageHandler
{
	public string Topic => Topics.ConnectionClosed;

	public async Task HandleAsync(string message)
	{
		logger.LogInformation($"Handling connection made message: {message}");

		var connectionInfo = JsonSerializer.Deserialize<ConnectionMessage>(message);

		if (connectionInfo == null)
		{
            logger.LogError("Failed to deserialize connection message: {Message}", message);
            return;
		}

		await cacheService.DeleteConnectionMadeAsync(connectionInfo);
	}
}
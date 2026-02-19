using System.Text.Json;
using Application.Caches;
using Domain.Connections;
using Domain.Messages;

namespace Infrastructure.MQ;

public class ClientConnectionClosedHandler(ICacheService cacheService) : IMessageHandler
{
	public string Topic => Topics.ConnectionClosed;

	public async Task HandleAsync(string message)
	{
		Console.WriteLine($"Handling connection made message: {message}");

		var connectionInfo = JsonSerializer.Deserialize<ConnectionMessage>(message);

		if (connectionInfo == null)
		{
			Console.WriteLine("Failed to deserialize connection message.");
			return;
		}

		await cacheService.DeleteConnectionMadeAsync(connectionInfo.Id);
	}
}
using System.Text.Json;
using Application.Caches;
using Domain.Health;
using Domain.Messages;

namespace Api.Application.ControlPlane;

public class HeartbeatMessageHandler(ICacheService cacheService, ILogger<HeartbeatMessageHandler> logger) : IMessageHandler
{
    public string Topic => ControlPlaneTopics.PodHeartbeat;

    //TODO: Implement monitor for if we havent heard from pod to purge the clients connected to that pod
    public async Task HandleAsync(string message)
    {
        logger.LogInformation($"Received heartbeat message: {message}");

		try
		{
			var heartBeatMessage = JsonSerializer.Deserialize<HealthMessage>(message);

			if (!TryValidate(heartBeatMessage, message))
			{
				return;
			}

			await cacheService.UpsertPodHeartbeat(heartBeatMessage);
        }
		catch (Exception e)
		{
			logger.LogError(e, "Failed to process heartbeat message: {Message}", message);
        }
    }

	private bool TryValidate(HealthMessage? heartBeatMessage, string rawMessage)
	{
		if (heartBeatMessage is null)
		{
			logger.LogError("Heartbeat message is null after deserialization: {Message}", rawMessage);
			return false;
		}
		if (string.IsNullOrWhiteSpace(heartBeatMessage.PodId))
		{
			logger.LogError("Pod id is null or empty: {Message}", rawMessage);
			return false;
		}
		if (heartBeatMessage.Timestamp == default)
		{
			logger.LogError("Timestamp is default value: {Message}", rawMessage);
			return false;
		}
		return true;
    }
}

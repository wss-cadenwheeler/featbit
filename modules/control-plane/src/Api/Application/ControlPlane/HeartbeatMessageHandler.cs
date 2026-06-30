using System.Text.Json;
using Application.Caches;
using Application.Configuration;
using Application.ControlPlane;
using Domain.ControlPlane;
using Domain.Health;
using Domain.Messages;

namespace Api.Application.ControlPlane;

public class HeartbeatMessageHandler(
    [FromKeyedServices("compositeCache")] ICacheService cacheService,
    ILogger<HeartbeatMessageHandler> logger,
    ILeaseStore leaseStore,
    IConfiguration configuration) : IMessageHandler
{
    private const int DefaultLeaseTtlSeconds = 15;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string Topic => ControlPlaneTopics.PodHeartbeat;

    public async Task HandleAsync(string message)
    {
        logger.LogInformation("Received heartbeat message: {Message}", message);

        try
        {
            var heartBeatMessage = JsonSerializer.Deserialize<HealthMessage>(message, JsonOptions);

            if (!TryValidate(heartBeatMessage, message))
            {
                return;
            }

            await cacheService.UpsertPodHeartbeat(heartBeatMessage!);

            // Under GatedCommit, the heartbeat doubles as live-set membership: persist a
            // DC lease so the control plane can track which DCs are currently live and what
            // versions they have applied. BestEffort keeps today's fire-and-forget behavior.
            if (configuration.GetConsistencyMode() == ConsistencyMode.GatedCommit)
            {
                await UpsertLeaseAsync(heartBeatMessage!);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to process heartbeat message: {Message}", message);
        }
    }

    private async Task UpsertLeaseAsync(HealthMessage heartBeatMessage)
    {
        var leaseTtlSeconds = configuration.GetValue("ControlPlane:LeaseTtlSeconds", DefaultLeaseTtlSeconds);
        var leaseTtl = TimeSpan.FromSeconds(leaseTtlSeconds);

        var lease = new DcLease
        {
            DcId = heartBeatMessage.DcId ?? heartBeatMessage.PodId,
            Region = heartBeatMessage.Region ?? string.Empty,
            LastHeartbeatAt = heartBeatMessage.Timestamp,
            LeaseExpiresAt = heartBeatMessage.Timestamp + leaseTtl,
            AppliedWatermarks = heartBeatMessage.AppliedWatermarks ?? new Dictionary<Guid, long>()
        };

        await leaseStore.UpsertLeaseAsync(lease);
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

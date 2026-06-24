using System.Text.Json;
using Domain.Messages;
using Domain.Shared;
using Microsoft.Extensions.Logging;
using Streaming.Services;
using Action = Domain.Messages.Action;

namespace Streaming.Consumers;

public class ControlPlaneCommandMessageConsumer(
    IAdminService adminService,
    ILogger<ControlPlaneCommandMessageConsumer> logger) : IMessageConsumer
{
    public string Topic => Topics.ControlPlaneCommand;

    public async Task HandleAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            var command = JsonSerializer.Deserialize<ControlPlaneCommand>(message, ReusableJsonSerializerOptions.Web);
            if (command == null)
            {
                logger.LogWarning("Invalid control plane command message format: {Message}", message);
                return;
            }

            switch (command.Action)
            {
                case Action.PushFullSync:
                    await adminService.PushFullSyncToAllActiveSdks();
                    break;
                default:
                    throw new ArgumentOutOfRangeException($"Invalid action: {command.Action}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Exception occurred while processing push full sync change message"
            );
        }
    }
}
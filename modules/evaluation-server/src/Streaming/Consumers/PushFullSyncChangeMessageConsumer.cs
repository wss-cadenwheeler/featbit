using Domain.Messages;
using Microsoft.Extensions.Logging;
using Streaming.Services;

namespace Streaming.Consumers;

public class PushFullSyncChangeMessageConsumer(IAdminService adminService, ILogger<PushFullSyncChangeMessageConsumer> logger) : IMessageConsumer
{
    public string Topic => Topics.PushFullSyncChange;
    public async Task HandleAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            await adminService.PushFullSyncToAllActiveClients();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Exception occurred while processing push full sync change message "
            );
        }
        
    }
}
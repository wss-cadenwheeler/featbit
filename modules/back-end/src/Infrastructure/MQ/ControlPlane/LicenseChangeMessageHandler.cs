using System.Text.Json;
using Application.Caches;
using Domain.FeatureFlags;
using Domain.Messages;
using Domain.Utils;
using Domain.Workspaces;

namespace Infrastructure.MQ.ControlPlane;

public class LicenseChangeMessageHandler(ICacheService cacheService) : IMessageHandler
{
    public string Topic => Topics.ControlPlaneLicenseChange;

    public async Task HandleAsync(string message)
    {
        var workspace = JsonSerializer.Deserialize<Workspace>(message, ReusableJsonSerializerOptions.Web);
        if (workspace != null)
        {
            // TODO: Upsert to all Redis Instances
            await cacheService.UpsertLicenseAsync(workspace);
        }
    }
}
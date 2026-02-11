using System.Text.Json;
using Application.Caches;
using Domain.FeatureFlags;
using Domain.Messages;
using Domain.Utils;
using Domain.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.MQ.ControlPlane;

public class LicenseChangeMessageHandler([FromKeyedServices("compositeCache")] ICacheService cacheService) : IMessageHandler
{
    public string Topic => Topics.ControlPlaneLicenseChange;

    public async Task HandleAsync(string message)
    {
        var workspace = JsonSerializer.Deserialize<Workspace>(message, ReusableJsonSerializerOptions.Web);
        if (workspace != null)
        {
            await cacheService.UpsertLicenseAsync(workspace);
        }
    }
}
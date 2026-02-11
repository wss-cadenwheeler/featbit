using System.Text.Json;
using Application.Caches;
using Domain.Environments;
using Domain.FeatureFlags;
using Domain.Messages;
using Domain.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.MQ.ControlPlane;

public class SecretChangeMessageHandler([FromKeyedServices("compositeCache")] ICacheService cacheService) : IMessageHandler
{
    public string Topic => Topics.ControlPlaneSecretChange;

    public async Task HandleAsync(string message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        if (!root.TryGetProperty("resourceDescriptor", out var resourceDescriptor) ||
            !root.TryGetProperty("secret", out var secret))
        {
            throw new InvalidDataException("invalid secret change data");
        }
        var deserializedResourceDescriptor = resourceDescriptor.Deserialize<ResourceDescriptor>();
        var deserializedSecret = secret.Deserialize<Secret>();
        if (deserializedResourceDescriptor != null && deserializedSecret != null)
        {
            await cacheService.UpsertSecretAsync(deserializedResourceDescriptor, deserializedSecret).ConfigureAwait(false);
        }
    }
}
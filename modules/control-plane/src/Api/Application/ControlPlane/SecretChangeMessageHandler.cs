using System.Text.Json;
using Application.Caches;
using Domain.Environments;
using Domain.Messages;
using Domain.Utils;

namespace Api.Application.ControlPlane;

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
        var deserializedResourceDescriptor = resourceDescriptor.Deserialize<ResourceDescriptor>(ReusableJsonSerializerOptions.Web);
        var deserializedSecret = secret.Deserialize<Secret>(ReusableJsonSerializerOptions.Web);
        if (deserializedResourceDescriptor != null && deserializedSecret != null)
        {
            await cacheService.UpsertSecretAsync(deserializedResourceDescriptor, deserializedSecret).ConfigureAwait(false);
        }
    }
}
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Environments;
using Domain.Utils;

namespace Application.Environments;

public static class ControlPlaneSecretHelpers
{
    public static JsonObject CreateMessage(ResourceDescriptor resourceDescriptor, Secret secret)
    {
        var resourceDescriptorNode = JsonSerializer.SerializeToNode(resourceDescriptor, ReusableJsonSerializerOptions.Web);
        var secretNode = JsonSerializer.SerializeToNode(secret, ReusableJsonSerializerOptions.Web);
        JsonObject secretUpsertMessage = new()
        {
            ["resourceDescriptor"] = resourceDescriptorNode,
            ["secret"] = secretNode
        };

        return secretUpsertMessage;
    }
}
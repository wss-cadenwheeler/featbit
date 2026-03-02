using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Caches;
using Application.Configuration;
using Domain.Messages;
using Domain.Utils;
using Microsoft.Extensions.Configuration;
using Environment = Domain.Environments.Environment;

namespace Application.Environments;

public class OnEnvironmentAdded : INotification
{
    public Environment Environment { get; }

    public OnEnvironmentAdded(Environment environment)
    {
        Environment = environment;
    }
}

public class OnEnvironmentAddedHandler(
    IEnvironmentService _envService,
    ICacheService _cache,
    IConfiguration _configuration,
    IMessageProducer _messageProducer)
    : INotificationHandler<OnEnvironmentAdded>
{
    public async Task Handle(OnEnvironmentAdded notification, CancellationToken cancellationToken)
    {
        var env = notification.Environment;

        // add secret cache
        var resourceDescriptor = await _envService.GetResourceDescriptorAsync(env.Id);
        foreach (var secret in env.Secrets)
        {
            await _cache.UpsertSecretAsync(resourceDescriptor, secret);
            if (_configuration.UseControlPlane())
            {
                var message = ControlPlaneSecretHelpers.CreateMessage(resourceDescriptor, secret);
                await _messageProducer.PublishAsync(ControlPlaneTopics.ControlPlaneSecretChange, message);
            }
        }
    }
}
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Caches;
using Domain.Environments;
using Domain.Messages;
using Domain.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Configuration;

namespace Application.Environments;

public class OnSecretAdded : INotification
{
    public Guid EnvId { get; }

    public Secret Secret { get; }

    public OnSecretAdded(Guid envId, Secret secret)
    {
        EnvId = envId;
        Secret = secret;
    }
}

public class OnSecretAddedHandler : INotificationHandler<OnSecretAdded>
{
    private readonly ICacheService _cache;
    private readonly IEnvironmentService _envService;
    private readonly IConfiguration _configuration;
    private readonly IMessageProducer _messageProducer;

    public OnSecretAddedHandler(ICacheService cache, IEnvironmentService envService, IConfiguration configuration, IMessageProducer messageProducer)
    {
        _cache = cache;
        _envService = envService;
        _configuration = configuration;
        _messageProducer = messageProducer;
    }

    public async Task Handle(OnSecretAdded notification, CancellationToken cancellationToken)
    {
        var resourceDescriptor = await _envService.GetResourceDescriptorAsync(notification.EnvId);

        await _cache.UpsertSecretAsync(resourceDescriptor, notification.Secret);
        
        var alternativeKafkaTopics = _configuration.GetKafkaAlternativeTopicsConfiguration();
        
        if (alternativeKafkaTopics is { Enabled: true })
        {
            var resourceDescriptorNode = JsonSerializer.SerializeToNode(resourceDescriptor, ReusableJsonSerializerOptions.Web);
            var secretNode = JsonSerializer.SerializeToNode(notification.Secret, ReusableJsonSerializerOptions.Web);
            JsonObject secretUpsertMessage = new()
            {
                ["resourceDescriptor"] = resourceDescriptorNode,
                ["secret"] = secretNode
            };
            await _messageProducer.PublishAsync(alternativeKafkaTopics.SecretChangeTopic, secretUpsertMessage);
        }
    }
}
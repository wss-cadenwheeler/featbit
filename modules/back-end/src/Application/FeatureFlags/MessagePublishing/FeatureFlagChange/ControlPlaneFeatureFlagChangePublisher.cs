using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Configuration;
using Domain.FeatureFlags;
using Domain.Messages;
using Domain.Utils;
using Microsoft.Extensions.Configuration;

namespace Application.FeatureFlags.MessagePublishing.FeatureFlagChange;

public class ControlPlaneFeatureFlagChangePublisher(IMessageProducer messageProducer, IConfiguration configuration) : IFeatureFlagChangePublisher
{
    public async Task PublishAsync(OnFeatureFlagChanged notification)
    {
        var flagMessage = new { notification, region = configuration.GetRegion() };
        await messageProducer.PublishAsync(ControlPlaneTopics.ControlPlaneFeatureFlagChange, flagMessage);
    }
}
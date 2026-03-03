using Domain.FeatureFlags;
using Domain.Messages;

namespace Application.FeatureFlags.MessagePublishing.FeatureFlagChange;

public class ControlPlaneFeatureFlagChangePublisher(IMessageProducer messageProducer) : IFeatureFlagChangePublisher
{
    public async Task PublishAsync(FeatureFlag flag)
    {
        await messageProducer.PublishAsync(ControlPlaneTopics.ControlPlaneFeatureFlagChange, flag);
    }
}
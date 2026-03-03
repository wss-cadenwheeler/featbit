using Domain.FeatureFlags;
using Domain.Messages;

namespace Application.FeatureFlags.MessagePublishing.FeatureFlagChange;

public class DirectFeatureFlagChangePublisher(IMessageProducer messageProducer) : IFeatureFlagChangePublisher
{
    public async Task PublishAsync(FeatureFlag flag)
    {
        await messageProducer.PublishAsync(Topics.FeatureFlagChange, flag);
    }
}
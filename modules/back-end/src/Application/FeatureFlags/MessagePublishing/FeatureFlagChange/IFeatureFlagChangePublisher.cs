using Domain.FeatureFlags;

namespace Application.FeatureFlags.MessagePublishing.FeatureFlagChange;

public interface IFeatureFlagChangePublisher
{
    Task PublishAsync(FeatureFlag flag);
}
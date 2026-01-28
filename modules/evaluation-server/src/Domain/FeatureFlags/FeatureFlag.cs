using Domain.Bases;
using Domain.Targetting;

namespace Domain.FeatureFlags;

public class FeatureFlag : FullAuditedEntity
{
    public const string KeyPattern = "^[a-zA-Z0-9._-]+$";

    public Guid EnvId { get; set; }

    public Guid Revision { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public string Key { get; set; }

    public string VariationType { get; set; }

    public ICollection<Variation> Variations { get; set; }

    public ICollection<TargetUser> TargetUsers { get; set; }

    public ICollection<TargetRule> Rules { get; set; }

    public bool IsEnabled { get; set; }

    public string DisabledVariationId { get; set; }

    public Fallthrough Fallthrough { get; set; }

    public bool ExptIncludeAllTargets { get; set; }

    public string[] Tags { get; set; }

    public bool IsArchived { get; set; }

    public FeatureFlag()
    {
    }

    public FeatureFlag(
        Guid envId,
        string name,
        string description,
        string key,
        bool isEnabled,
        string variationType,
        ICollection<Variation> variations,
        string disabledVariationId,
        string enabledVariationId,
        string[] tags,
        Guid currentUserId) : base(currentUserId)
    {
        Revision = Guid.NewGuid();

        EnvId = envId;

        Name = name;
        Description = description;
        Key = key;

        VariationType = variationType;
        Variations = variations;

        TargetUsers = Array.Empty<TargetUser>();
        Rules = Array.Empty<TargetRule>();

        IsEnabled = isEnabled;
        DisabledVariationId = disabledVariationId;
        Fallthrough = new Fallthrough
        {
            IncludedInExpt = true,
            Variations = new List<RolloutVariation>
            {
                new()
                {
                    Id = enabledVariationId,
                    Rollout = [0, 1],
                    ExptRollout = 1
                }
            }
        };
        ExptIncludeAllTargets = true;

        Tags = tags ?? [];
        IsArchived = false;
    }
}
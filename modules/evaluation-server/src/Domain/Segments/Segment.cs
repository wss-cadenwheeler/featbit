using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Bases;
using Domain.EndUsers;
using Domain.Shared;
using Domain.Targetting;

namespace Domain.Segments;

public class Segment : AuditedEntity
{
    public Guid WorkspaceId { get; set; }

    public Guid EnvId { get; set; }

    public string Name { get; set; }

    public string Key { get; set; }

    public string Type { get; set; }

    public string[] Scopes { get; set; }

    public string Description { get; set; }

    public string[] Included { get; set; }

    public string[] Excluded { get; set; }

    public ICollection<MatchRule> Rules { get; set; }

    public string[] Tags { get; set; }

    public bool IsArchived { get; set; }

    public bool IsEnvironmentSpecific => Type == SegmentType.EnvironmentSpecific;

    public Segment(
        Guid workspaceId,
        Guid envId,
        string name,
        string key,
        string type,
        string[] scopes,
        string[] included,
        string[] excluded,
        ICollection<MatchRule> rules,
        string description)
    {
        WorkspaceId = workspaceId;
        EnvId = envId;
        Name = name;
        Key = key;
        Type = type;
        Scopes = scopes;
        Included = included ?? [];
        Excluded = excluded ?? [];
        Rules = rules;
        Description = description ?? string.Empty;

        CreatedAt = DateTime.UtcNow;
        IsArchived = false;
        Tags = [];
    }
    
    public JsonObject SerializeAsEnvironmentSpecific(Guid? envId = null)
    {
        var json = JsonSerializer.SerializeToNode(this, ReusableJsonSerializerOptions.Web)!.AsObject();

        json["envId"] = Type == SegmentType.EnvironmentSpecific
            ? EnvId.ToString()
            : envId?.ToString() ?? string.Empty;

        json.Remove("type");
        json.Remove("workspaceId");
        json.Remove("scopes");
        json.Remove("isEnvironmentSpecific");

        return json;
    }
    
}
using System.Text.Json.Serialization;

namespace Domain.Messages;

public class ControlPlaneCommand
{
    public Action Action { get; set; }

    public string ConnectionId { get; set; } = "*";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Action
{
    PushFullSync
}
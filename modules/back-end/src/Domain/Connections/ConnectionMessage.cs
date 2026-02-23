
namespace Domain.Connections;

public class ConnectionMessage
{
    public string Id { get; set; }
    public Guid EnvId { get; init; }
    public string Secert { get; init; }
}

namespace Domain.Targetting;

public class MatchRule
{
    public string Id { get; set; }

    public string Name { get; set; }

    public ICollection<Condition> Conditions { get; set; } = Array.Empty<Condition>();
}
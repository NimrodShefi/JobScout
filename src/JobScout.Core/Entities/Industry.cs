namespace JobScout.Core.Entities;

/// <summary>A search term used by the discovery job against each job board.</summary>
public class Industry
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

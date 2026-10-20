namespace MetadataApi.Persistence.Entities;

public sealed class Application
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Title { get; set; }
    public string? Version { get; set; }
    public string? Company { get; set; }
    public string? Website { get; set; }
    public string? Source { get; set; }
    public string? License { get; set; }
    public string? Description { get; set; }
}

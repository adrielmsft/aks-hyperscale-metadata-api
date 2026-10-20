namespace MetadataApi.Contracts;

/// <summary>Metadata supplied when creating an application; the server assigns its ID.</summary>
public sealed record CreateApplicationRequest
{
    public string? Title { get; init; }
    public string? Version { get; init; }
    public string? Company { get; init; }
    public string? Website { get; init; }
    public string? Source { get; init; }
    public string? License { get; init; }
    public string? Description { get; init; }
}

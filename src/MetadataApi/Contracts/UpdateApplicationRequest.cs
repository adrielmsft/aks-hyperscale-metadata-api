namespace MetadataApi.Contracts;

/// <summary>Full replacement metadata; omitted optional fields are cleared, without changing ID or associations.</summary>
public sealed record UpdateApplicationRequest
{
    public string? Title { get; init; }
    public string? Version { get; init; }
    public string? Company { get; init; }
    public string? Website { get; init; }
    public string? Source { get; init; }
    public string? License { get; init; }
    public string? Description { get; init; }
}

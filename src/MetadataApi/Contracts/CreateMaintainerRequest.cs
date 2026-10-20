namespace MetadataApi.Contracts;

/// <summary>Contact metadata for a new maintainer; the server assigns its ID.</summary>
public sealed record CreateMaintainerRequest
{
    public string? Name { get; init; }
    public string? Email { get; init; }
}

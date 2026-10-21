namespace MetadataApi.Contracts;

/// <summary>A full replacement of a maintainer's required name and email.</summary>
public sealed record UpdateMaintainerRequest
{
    public string? Name { get; init; }
    public string? Email { get; init; }
}

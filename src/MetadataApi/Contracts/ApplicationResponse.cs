namespace MetadataApi.Contracts;

/// <summary>An application's stable identifier and current metadata.</summary>
public sealed record ApplicationResponse(
    Guid Id,
    string Title,
    string? Version,
    string? Company,
    string? Website,
    string? Source,
    string? License,
    string? Description);

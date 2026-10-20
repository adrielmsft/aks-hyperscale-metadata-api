namespace MetadataApi.Contracts;

/// <summary>Stored maintainer contact metadata with its stable server-generated ID.</summary>
public sealed record MaintainerResponse(Guid Id, string Name, string Email);

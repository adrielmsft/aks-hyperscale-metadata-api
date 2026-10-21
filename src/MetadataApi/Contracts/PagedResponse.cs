namespace MetadataApi.Contracts;

/// <summary>A bounded page of resources with the next supported offset, if available.</summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Limit, int Offset, int? NextOffset);

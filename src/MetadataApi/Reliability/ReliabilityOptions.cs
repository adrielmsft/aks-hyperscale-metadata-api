using System.ComponentModel.DataAnnotations;

namespace MetadataApi.Reliability;

public sealed class RateLimitingOptions
{
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 1000;

    [Range(1, 86400)]
    public int WindowSeconds { get; set; } = 60;
}

public sealed class RequestLimitOptions
{
    [Range(1, 16 * 1024 * 1024)]
    public int MaxRequestBodyBytes { get; set; } = 64 * 1024;
}

using Microsoft.AspNetCore.Authentication;

namespace MetadataApi.Authentication;

public sealed class ApiKeyOptions : AuthenticationSchemeOptions
{
    public const string Scheme = "ApiKey";
    public const int MinimumKeyLength = 32;

    public string? ApiKey { get; set; }
}

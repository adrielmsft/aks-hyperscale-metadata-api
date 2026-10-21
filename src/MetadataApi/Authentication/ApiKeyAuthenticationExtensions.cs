using Microsoft.AspNetCore.Authorization;

namespace MetadataApi.Authentication;

public static class ApiKeyAuthenticationExtensions
{
    public static IServiceCollection AddApiKeyAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication(ApiKeyOptions.Scheme)
            .AddScheme<ApiKeyOptions, ApiKeyAuthenticationHandler>(ApiKeyOptions.Scheme, _ => { });
        services.AddOptions<ApiKeyOptions>(ApiKeyOptions.Scheme)
            .Bind(configuration.GetSection("Authentication"))
            .Validate(options => !string.IsNullOrWhiteSpace(options.ApiKey)
                && options.ApiKey.Length >= ApiKeyOptions.MinimumKeyLength,
                "Authentication:ApiKey must contain at least 32 characters and must not be blank.")
            .ValidateOnStart();
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().Build();
        });
        return services;
    }
}

using MetadataApi.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MetadataApi.Tests;

public sealed class ApiKeyAuthenticationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("Basic abc", false)]
    [InlineData("Bearer", false)]
    [InlineData("Bearer wrong", false)]
    [InlineData("Bearer integration-test-key-not-a-real-secret-33", false)]
    [InlineData("Bearer integration-test-key-not-a-real-secret-32", true)]
    [InlineData("bearer integration-test-key-not-a-real-secret-32", true)]
    public async Task Handler_AuthenticatesOnlyMatchingBearerKey(string? header, bool succeeds)
    {
        using var services = CreateServices(MetadataApiFactory.ApiKey);
        var context = new DefaultHttpContext { RequestServices = services };
        if (header is not null)
        {
            context.Request.Headers.Authorization = header;
        }

        var result = await context.AuthenticateAsync();

        Assert.Equal(succeeds, result.Succeeded);
        if (succeeds)
        {
            Assert.True(result.Principal!.Identity!.IsAuthenticated);
            Assert.DoesNotContain(result.Principal.Claims, claim => claim.Value == MetadataApiFactory.ApiKey);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("                                ")]
    [InlineData("too-short")]
    [InlineData("1234567890123456789012345678901")]
    public void Options_RejectMissingBlankOrShortKey(string? key)
    {
        using var services = CreateServices(key);

        var exception = Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptionsMonitor<ApiKeyOptions>>().Get(ApiKeyOptions.Scheme));

        Assert.Contains("Authentication:ApiKey", exception.Message);
        if (!string.IsNullOrWhiteSpace(key))
        {
            Assert.DoesNotContain(key, exception.Message);
        }
    }

    [Fact]
    public void Options_AcceptMinimumLengthKey()
    {
        var key = new string('a', ApiKeyOptions.MinimumKeyLength);
        using var services = CreateServices(key);

        Assert.Equal(key, services.GetRequiredService<IOptionsMonitor<ApiKeyOptions>>()
            .Get(ApiKeyOptions.Scheme).ApiKey);
    }

    private static ServiceProvider CreateServices(string? key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Authentication:ApiKey"] = key }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApiKeyAuthentication(configuration);
        return services.BuildServiceProvider();
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MetadataApi.E2E.Tests;

public sealed class AuthenticationE2ETests(ApiServerFixture server) : IClassFixture<ApiServerFixture>
{
    [Fact]
    public async Task RealServer_RequiresKeyForResourcesButNotHealth()
    {
        using var client = server.CreateUnauthenticatedClient();
        foreach (var header in new[] { null, new AuthenticationHeaderValue("Bearer", "wrong") })
        {
            client.DefaultRequestHeaders.Authorization = header;
            using var denied = await client.GetAsync($"/api/v1/applications/{Guid.NewGuid()}");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.Equal("Bearer", Assert.Single(denied.Headers.WwwAuthenticate).Scheme);
            Assert.Equal("application/problem+json", denied.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
            Assert.Equal(401, problem.RootElement.GetProperty("status").GetInt32());
        }

        using var health = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var allowed = await server.Client.GetAsync("/api/v1/applications");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task RealServer_ExitsAtStartupWithoutConfiguredKey()
    {
        var (exitCode, output) = await server.StartWithoutApiKeyAsync();
        Assert.NotEqual(0, exitCode);
        Assert.Contains("OptionsValidationException", output);
        Assert.Contains("Authentication:ApiKey must contain at least 32 characters", output);
        Assert.DoesNotContain("Now listening on:", output);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace MetadataApi.Tests;

public sealed class AuthenticationApiTests(MetadataApiFactory factory) : IClassFixture<MetadataApiFactory>
{
    [Theory]
    [InlineData(null)]
    [InlineData("Basic wrong")]
    [InlineData("Bearer wrong")]
    public async Task InvalidCredentials_ReturnGenericProblemAndChallenge(string? header)
    {
        using var client = factory.CreateUnauthenticatedClient();
        if (header is not null)
        {
            client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(header);
        }
        // Problem Details must also be returned to callers requesting a different format.
        client.DefaultRequestHeaders.Accept.ParseAdd("text/plain");

        using var response = await client.GetAsync("/api/v1/applications");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(401, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Unauthorized", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal("A valid API key is required.", problem.RootElement.GetProperty("detail").GetString());
        Assert.StartsWith("https://", problem.RootElement.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("/api/v1/applications")]
    [InlineData("/api/v1/maintainers")]
    public async Task ValidKey_AllowsResourceAccess(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousRequests_CannotDistinguishExistingAndMissingResources()
    {
        using var authenticated = factory.CreateClient();
        using var created = await authenticated.PostAsJsonAsync(
            "/api/v1/applications", new { title = "Private metadata" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var client = factory.CreateUnauthenticatedClient();

        using var existing = await client.GetAsync(created.Headers.Location);
        using var missing = await client.GetAsync($"/api/v1/applications/{Guid.NewGuid()}");
        using var future = await client.GetAsync("/api/v1/future-resource");

        Assert.Equal(HttpStatusCode.Unauthorized, existing.StatusCode);
        Assert.Equal(existing.StatusCode, missing.StatusCode);
        Assert.Equal(existing.StatusCode, future.StatusCode);
        using var existingProblem = JsonDocument.Parse(await existing.Content.ReadAsStringAsync());
        using var missingProblem = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
        foreach (var field in new[] { "type", "title", "status", "detail" })
        {
            Assert.Equal(existingProblem.RootElement.GetProperty(field).GetRawText(),
                missingProblem.RootElement.GetProperty(field).GetRawText());
        }
    }

    [Fact]
    public async Task LivenessAndDevelopmentOpenApi_AreExplicitlyAnonymous()
    {
        using var development = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        // A malformed key must not prevent access to explicitly anonymous endpoints.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");

        foreach (var path in new[] { "/health/live", "/openapi/v1.json" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        client.DefaultRequestHeaders.Authorization = null;
        foreach (var path in new[] { "/health/live", "/openapi/v1.json" })
        {
            using var anonymous = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        }
    }
}

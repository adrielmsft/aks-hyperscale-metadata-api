using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace MetadataApi.Tests;

public sealed class ApiSmokeTests : IClassFixture<MetadataApiFactory>
{
    private readonly MetadataApiFactory factory;

    public ApiSmokeTests(MetadataApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Liveness_ReturnsHealthy()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OpenApi_InDevelopment_ReturnsDocument()
    {
        using var developmentFactory = factory.WithWebHostBuilder(builder =>
            builder.UseEnvironment("Development"));
        using var client = developmentFactory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.StartsWith("3.", document.RootElement.GetProperty("openapi").GetString());
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("paths").ValueKind);
    }

    [Fact]
    public async Task OpenApi_InProduction_IsNotExposed()
    {
        using var productionFactory = factory.WithWebHostBuilder(builder =>
            builder.UseEnvironment("Production"));
        using var client = productionFactory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownRoute_ReturnsProblemDetails()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, document.RootElement.GetProperty("status").GetInt32());
    }
}

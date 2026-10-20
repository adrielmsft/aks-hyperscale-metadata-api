using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace MetadataApi.E2E.Tests;

public sealed class ApplicationE2ETests(ApiServerFixture server) : IClassFixture<ApiServerFixture>
{
    [Fact]
    public async Task CreateAndRead_UsesRealHttpAndReturnsNormalizedMetadata()
    {
        using var response = await server.Client.PostAsJsonAsync("/api/v1/applications", new
        {
            title = " Microsoft Garage ", version = " 1.2.0 ",
            company = " Microsoft ", website = " https://garage.example.com ",
            source = " https://github.com/example/garage ", license = " Apache-2.0 ",
            description = " An internal application for managing Microsoft Garage locations and staff. "
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var application = document.RootElement;
        Assert.NotEqual(Guid.Empty, application.GetProperty("id").GetGuid());
        Assert.Equal("Microsoft Garage", application.GetProperty("title").GetString());
        Assert.Equal("1.2.0", application.GetProperty("version").GetString());
        Assert.Equal("Microsoft", application.GetProperty("company").GetString());
        Assert.Equal("https://garage.example.com", application.GetProperty("website").GetString());
        Assert.Equal("https://github.com/example/garage", application.GetProperty("source").GetString());
        Assert.Equal("Apache-2.0", application.GetProperty("license").GetString());
        Assert.Equal("An internal application for managing Microsoft Garage locations and staff.",
            application.GetProperty("description").GetString());
        Assert.Equal($"/api/v1/applications/{application.GetProperty("id").GetGuid()}",
            response.Headers.Location?.OriginalString);

        using var retrieved = await server.Client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, retrieved.StatusCode);
        Assert.Equal(application.GetRawText(), await retrieved.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MinimalCreates_AllowDuplicateTitlesAndReturnExplicitNulls()
    {
        var ids = new HashSet<Guid>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await server.Client.PostAsJsonAsync(
                "/api/v1/applications", new { title = "Microsoft Garage" });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(ids.Add(document.RootElement.GetProperty("id").GetGuid()));
            Assert.All(new[] { "version", "company", "website", "source", "license", "description" },
                field => Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty(field).ValueKind));
        }
    }

    [Fact]
    public async Task CreatedRecord_SurvivesServerProcessRestart()
    {
        using var created = await server.Client.PostAsJsonAsync(
            "/api/v1/applications", new { title = "Microsoft Garage Locations", version = "2.0.0" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var expected = await created.Content.ReadAsStringAsync();
        var location = created.Headers.Location;

        await server.RestartAsync();

        using var retrieved = await server.Client.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, retrieved.StatusCode);
        Assert.Equal(expected, await retrieved.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InvalidMetadata_ReportsAllFieldErrors()
    {
        using var response = await server.Client.PostAsJsonAsync("/api/v1/applications", new
        {
            title = " ", version = "v1", website = "ftp://garage.example.com"
        });

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        var errors = problem.GetProperty("errors");
        Assert.Equal(3, errors.EnumerateObject().Count());
        Assert.True(errors.TryGetProperty("title", out _));
        Assert.True(errors.TryGetProperty("version", out _));
        Assert.True(errors.TryGetProperty("website", out _));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"title\":123}")]
    [InlineData("{\"title\":\"Microsoft Garage\",\"unknown\":true}")]
    [InlineData("{\"title\":\"Microsoft Garage\",\"id\":\"54b28c61-8ec5-42c1-966b-3564c59bf965\"}")]
    [InlineData("{\"title\":\"Microsoft Garage\",\"title\":\"Microsoft Garage Staff\"}")]
    public async Task InvalidJson_ReturnsProblemDetails(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await server.Client.PostAsync("/api/v1/applications", content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidId_ReturnsBadRequest(string id)
    {
        using var response = await server.Client.GetAsync($"/api/v1/applications/{id}");

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.True(problem.GetProperty("errors").TryGetProperty("applicationId", out _));
    }

    [Fact]
    public async Task MissingApplication_ReturnsNotFound()
    {
        using var response = await server.Client.GetAsync($"/api/v1/applications/{Guid.NewGuid()}");

        await AssertProblemAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task NonJsonBody_ReturnsUnsupportedMediaType()
    {
        using var content = new StringContent("not JSON", Encoding.UTF8, "text/plain");
        using var response = await server.Client.PostAsync("/api/v1/applications", content);

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task ProductionServer_DoesNotExposeOpenApi()
    {
        using var response = await server.Client.GetAsync("/openapi/v1.json");

        await AssertProblemAsync(response, HttpStatusCode.NotFound);
    }

    private static async Task<JsonElement> AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)expected, document.RootElement.GetProperty("status").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("exception", out _));
        return document.RootElement.Clone();
    }
}

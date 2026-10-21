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

    [Fact]
    public async Task ReplaceAndDelete_UseRealHttpAndPersistAcrossProcessRestarts()
    {
        using var created = await server.Client.PostAsJsonAsync("/api/v1/applications", new
        {
            title = "Microsoft Garage", version = "1.0.0", company = "Microsoft",
            website = "https://garage.example.com", source = "https://github.com/example/garage",
            license = "MIT", description = "Garage locations and staff"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var location = created.Headers.Location;
        using var original = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = original.RootElement.GetProperty("id").GetGuid();
        using var updated = await server.Client.PutAsJsonAsync(location, new
        {
            title = " Microsoft Garage Locations ", version = " 2.0.0-rc.1+build.7 ",
            company = " Microsoft Garage ", website = " https://locations.example.com ",
            source = " https://github.com/example/garage-locations ", license = " Apache-2.0 ",
            description = " Garage locations\n\tGarage staff "
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("application/json", updated.Content.Headers.ContentType?.MediaType);
        using var replacement = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        Assert.Equal(id, replacement.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Microsoft Garage Locations", replacement.RootElement.GetProperty("title").GetString());
        Assert.Equal("2.0.0-rc.1+build.7", replacement.RootElement.GetProperty("version").GetString());
        Assert.Equal("Microsoft Garage", replacement.RootElement.GetProperty("company").GetString());
        Assert.Equal("https://locations.example.com", replacement.RootElement.GetProperty("website").GetString());
        Assert.Equal("https://github.com/example/garage-locations", replacement.RootElement.GetProperty("source").GetString());
        Assert.Equal("Apache-2.0", replacement.RootElement.GetProperty("license").GetString());
        Assert.Equal("Garage locations\n\tGarage staff", replacement.RootElement.GetProperty("description").GetString());
        Assert.Equal(replacement.RootElement.GetRawText(), await server.Client.GetStringAsync(location));

        using var cleared = await server.Client.PutAsJsonAsync(location, new { title = " Microsoft Garage Staff " });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var expected = await cleared.Content.ReadAsStringAsync();
        using var minimal = JsonDocument.Parse(expected);
        Assert.Equal(id, minimal.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Microsoft Garage Staff", minimal.RootElement.GetProperty("title").GetString());
        Assert.All(new[] { "version", "company", "website", "source", "license", "description" },
            field => Assert.Equal(JsonValueKind.Null, minimal.RootElement.GetProperty(field).ValueKind));
        using var repeated = await server.Client.PutAsJsonAsync(location, new { title = "Microsoft Garage Staff" });
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(expected, await repeated.Content.ReadAsStringAsync());

        await server.RestartAsync();
        Assert.Equal(expected, await server.Client.GetStringAsync(location));
        using var deleted = await server.Client.DeleteAsync(location);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await deleted.Content.ReadAsByteArrayAsync());
        await server.RestartAsync();
        using var missing = await server.Client.GetAsync(location);
        using var deleteAgain = await server.Client.DeleteAsync(location);
        await AssertProblemAsync(missing, HttpStatusCode.NotFound);
        await AssertProblemAsync(deleteAgain, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_UsesDefaultAndExplicitPaginationWithStableDatabaseOrdering()
    {
        for (var i = 0; i < 23; i++)
        {
            using var created = await server.Client.PostAsJsonAsync(
                "/api/v1/applications", new { title = $"Microsoft Garage Location {i}" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using var all = JsonDocument.Parse(await server.Client.GetStringAsync("/api/v1/applications?limit=100"));
        var allItems = all.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.InRange(allItems.Length, 23, 99);
        var ids = allItems.Select(item => item.GetProperty("id").GetString()).ToArray();
        Assert.Equal(ids.Order(StringComparer.OrdinalIgnoreCase), ids);
        using var first = JsonDocument.Parse(await server.Client.GetStringAsync("/api/v1/applications"));
        Assert.Equal(20, first.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(0, first.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(20, first.RootElement.GetProperty("nextOffset").GetInt32());
        Assert.Equal(ids.Take(20), first.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()));
        using var next = JsonDocument.Parse(await server.Client.GetStringAsync("/api/v1/applications?limit=2&offset=2"));
        Assert.Equal(2, next.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(2, next.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(4, next.RootElement.GetProperty("nextOffset").GetInt32());
        Assert.Equal(ids.Skip(2).Take(2), next.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()));
        using var exactEnd = JsonDocument.Parse(await server.Client.GetStringAsync(
            $"/api/v1/applications?limit=1&offset={ids.Length - 1}"));
        Assert.Single(exactEnd.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, exactEnd.RootElement.GetProperty("nextOffset").ValueKind);
        using var empty = JsonDocument.Parse(await server.Client.GetStringAsync("/api/v1/applications?limit=100&offset=10000"));
        Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(100, empty.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(10000, empty.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.RootElement.GetProperty("nextOffset").ValueKind);
        Assert.All(allItems, item =>
        {
            Assert.Equal(8, item.EnumerateObject().Count());
            Assert.True(item.TryGetProperty("description", out _));
        });
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("limit=abc")]
    [InlineData("limit=1.5")]
    [InlineData("limit=")]
    [InlineData("offset=-1")]
    [InlineData("offset=10001")]
    [InlineData("offset=abc")]
    [InlineData("offset=0.5")]
    [InlineData("offset=2147483648")]
    public async Task InvalidPagination_ReturnsProblemDetails(string query)
    {
        using var response = await server.Client.GetAsync($"/api/v1/applications?{query}");
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"title\":123}")]
    [InlineData("{\"title\":\"Microsoft Garage\",\"unknown\":true}")]
    [InlineData("{\"title\":\"Microsoft Garage\",\"id\":\"54b28c61-8ec5-42c1-966b-3564c59bf965\"}")]
    [InlineData("{\"title\":\"Microsoft Garage\",\"maintainers\":[]}")]
    [InlineData("{\"title\":\"Microsoft Garage\",\"title\":\"Garage Staff\"}")]
    [InlineData("{}")]
    [InlineData("{\"title\":null}")]
    [InlineData("{\"title\":\" \"}")]
    public async Task InvalidReplacement_DoesNotModifyApplication(string json)
    {
        using var created = await server.Client.PostAsJsonAsync("/api/v1/applications",
            new { title = "Microsoft Garage", version = "1.0.0" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var expected = await created.Content.ReadAsStringAsync();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await server.Client.PutAsync(created.Headers.Location, content);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(expected, await server.Client.GetStringAsync(created.Headers.Location));
    }

    [Fact]
    public async Task InvalidReplacementMetadata_ReportsAllErrors()
    {
        using var response = await server.Client.PutAsJsonAsync($"/api/v1/applications/{Guid.NewGuid()}", new
        {
            title = " ", version = "v1", website = "ftp://garage.example.com", source = "/garage"
        });
        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(4, problem.GetProperty("errors").EnumerateObject().Count());
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidMutationId_ReturnsBadRequest(string id)
    {
        using var put = await server.Client.PutAsJsonAsync($"/api/v1/applications/{id}", new { title = "Microsoft Garage" });
        using var delete = await server.Client.DeleteAsync($"/api/v1/applications/{id}");
        await AssertProblemAsync(put, HttpStatusCode.BadRequest);
        await AssertProblemAsync(delete, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MissingMutationTargets_ReturnNotFoundWithoutCreating()
    {
        var path = $"/api/v1/applications/{Guid.NewGuid()}";
        using var put = await server.Client.PutAsJsonAsync(path, new { title = "Microsoft Garage" });
        using var delete = await server.Client.DeleteAsync(path);
        using var get = await server.Client.GetAsync(path);
        await AssertProblemAsync(put, HttpStatusCode.NotFound);
        await AssertProblemAsync(delete, HttpStatusCode.NotFound);
        await AssertProblemAsync(get, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task NonJsonReplacement_ReturnsUnsupportedMediaType()
    {
        using var content = new StringContent("Microsoft Garage", Encoding.UTF8, "text/plain");
        using var response = await server.Client.PutAsync($"/api/v1/applications/{Guid.NewGuid()}", content);
        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType);
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

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MetadataApi.Contracts;
using MetadataApi.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MetadataApi.Tests;

public sealed class ApplicationApiTests(MetadataApiFactory factory) : IClassFixture<MetadataApiFactory>
{
    [Fact]
    public async Task CreateAndGet_ReturnStoredMetadataAndLocation()
    {
        using var client = factory.CreateClient();
        var request = new CreateApplicationRequest
        {
            Title = " Inventory Portal ", Version = " 1.2.0-rc.1+build.42 ",
            Company = " Example Company ", Website = " https://inventory.example.com ",
            Source = " https://github.com/example/inventory ", License = " Apache-2.0 ",
            Description = " First line\nSecond line "
        };

        using var created = await client.PostAsJsonAsync("/api/v1/applications", request);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var application = Assert.IsType<ApplicationResponse>(
            await created.Content.ReadFromJsonAsync<ApplicationResponse>());
        Assert.NotEqual(Guid.Empty, application.Id);
        Assert.Equal($"/api/v1/applications/{application.Id}", created.Headers.Location?.OriginalString);
        Assert.Equal("Inventory Portal", application.Title);
        Assert.Equal("1.2.0-rc.1+build.42", application.Version);
        Assert.Equal("Example Company", application.Company);
        Assert.Equal("https://inventory.example.com", application.Website);
        Assert.Equal("https://github.com/example/inventory", application.Source);
        Assert.Equal("Apache-2.0", application.License);
        Assert.Equal("First line\nSecond line", application.Description);
        using var fetched = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal(application, await fetched.Content.ReadFromJsonAsync<ApplicationResponse>());

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        Assert.True(await database.Applications.AnyAsync(entity => entity.Id == application.Id));
        Assert.False(await database.ApplicationMaintainers.AnyAsync(entity => entity.ApplicationId == application.Id));
    }

    [Fact]
    public async Task MinimalCreate_ReturnsExplicitNullsAndAllowsDuplicateTitles()
    {
        using var client = factory.CreateClient();
        var ids = new HashSet<Guid>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.PostAsJsonAsync(
                "/api/v1/applications", new { title = "Same Title" });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(ids.Add(document.RootElement.GetProperty("id").GetGuid()));
            Assert.All(new[] { "version", "company", "website", "source", "license", "description" },
                field => Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty(field).ValueKind));
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"title\":null}")]
    [InlineData("{\"title\":\"   \"}")]
    public async Task MissingTitle_ReturnsFieldValidationProblem(string json)
    {
        using var client = factory.CreateClient();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/v1/applications", content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("title", out _));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"title\":123}")]
    [InlineData("{\"title\":\"Portal\",\"id\":\"54b28c61-8ec5-42c1-966b-3564c59bf965\"}")]
    [InlineData("{\"title\":\"Portal\",\"typo\":\"value\"}")]
    [InlineData("{\"Title\":\"Portal\"}")]
    [InlineData("{\"title\":\"First\",\"title\":\"Second\"}")]
    public async Task InvalidJsonOrProperties_ReturnBadRequestWithoutWriting(string json)
    {
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var count = await database.Applications.CountAsync();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/v1/applications", content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(count, await database.Applications.CountAsync());
    }

    [Fact]
    public async Task InvalidFields_ReportAllErrorsWithoutWriting()
    {
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var count = await database.Applications.CountAsync();

        using var response = await client.PostAsJsonAsync("/api/v1/applications", new
        {
            title = "", version = "v1", website = "ftp://example.com", source = "/relative"
        });

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = document.RootElement.GetProperty("errors");
        Assert.Equal(4, errors.EnumerateObject().Count());
        Assert.Equal(count, await database.Applications.CountAsync());
    }

    [Fact]
    public async Task NonJsonBody_ReturnsUnsupportedMediaType()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent("{\"title\":\"Portal\"}", Encoding.UTF8, "text/plain");

        using var response = await client.PostAsync("/api/v1/applications", content);

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidId_ReturnsFieldValidationProblem(string id)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/v1/applications/{id}");

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("applicationId", out _));
    }

    [Fact]
    public async Task MissingApplication_ReturnsNotFoundProblem()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/v1/applications/{Guid.NewGuid()}");

        await AssertProblemAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DatabaseContention_ReturnsServiceUnavailableWithoutWriting()
    {
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var count = await database.Applications.CountAsync();
        await using var transaction = await database.Database.BeginTransactionAsync();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/applications", new { title = "Blocked application" });

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SqliteException", body);
        Assert.DoesNotContain("INSERT", body);
        await transaction.RollbackAsync();
        Assert.Equal(count, await database.Applications.CountAsync());
    }

    [Fact]
    public async Task OpenApi_DescribesBothOperationsAndResponses()
    {
        using var developmentFactory = factory.WithWebHostBuilder(builder =>
            builder.UseEnvironment("Development"));
        using var client = developmentFactory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");

        var create = paths.GetProperty("/api/v1/applications").GetProperty("post");
        Assert.Equal("CreateApplication", create.GetProperty("operationId").GetString());
        Assert.True(create.GetProperty("responses").TryGetProperty("201", out _));
        Assert.True(create.GetProperty("responses").TryGetProperty("400", out _));
        var get = paths.GetProperty("/api/v1/applications/{applicationId}").GetProperty("get");
        Assert.Equal("GetApplication", get.GetProperty("operationId").GetString());
        Assert.True(get.GetProperty("responses").TryGetProperty("200", out _));
        Assert.True(get.GetProperty("responses").TryGetProperty("404", out _));
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)expected, document.RootElement.GetProperty("status").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("exception", out _));
    }
}

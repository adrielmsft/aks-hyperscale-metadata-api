using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MetadataApi.Contracts;
using MetadataApi.Persistence;
using MetadataApi.Persistence.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MetadataApi.Tests;

public sealed class ApplicationMaintainerApiTests(MetadataApiFactory factory) : IClassFixture<MetadataApiFactory>
{
    [Fact]
    public async Task EnsureAndRemove_UseBodyless204ResponsesAndPreserveResources()
    {
        using var client = factory.CreateClient();
        using var application = await client.PostAsJsonAsync("/api/v1/applications", new { title = "Microsoft Garage" });
        using var maintainer = await client.PostAsJsonAsync("/api/v1/maintainers",
            new { name = "Garage Makers", email = "makers@example.com" });
        var applicationId = (await application.Content.ReadFromJsonAsync<ApplicationResponse>())!.Id;
        var maintainerId = (await maintainer.Content.ReadFromJsonAsync<MaintainerResponse>())!.Id;
        var path = $"/api/v1/applications/{applicationId}/maintainers/{maintainerId}";

        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            var expectedCount = method == HttpMethod.Put ? 1 : 0;
            var forward = await ReadPageAsync(client, $"/api/v1/applications/{applicationId}/maintainers");
            var reverse = await ReadPageAsync(client, $"/api/v1/maintainers/{maintainerId}/applications");
            Assert.Equal(expectedCount, forward.GetProperty("items").GetArrayLength());
            Assert.Equal(expectedCount, reverse.GetProperty("items").GetArrayLength());
        }

        Assert.Equal(await application.Content.ReadAsStringAsync(), await client.GetStringAsync(application.Headers.Location));
        Assert.Equal(await maintainer.Content.ReadAsStringAsync(), await client.GetStringAsync(maintainer.Headers.Location));
    }

    [Theory]
    [InlineData("not-a-guid", "not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000", "00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-guid", "54b28c61-8ec5-42c1-966b-3564c59bf965")]
    [InlineData("54b28c61-8ec5-42c1-966b-3564c59bf965", "not-a-guid")]
    public async Task InvalidIds_ReportEveryInvalidField(string applicationId, string maintainerId)
    {
        using var client = factory.CreateClient();
        var applicationInvalid = !Guid.TryParse(applicationId, out var application) || application == Guid.Empty;
        var maintainerInvalid = !Guid.TryParse(maintainerId, out var maintainer) || maintainer == Guid.Empty;
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, $"/api/v1/applications/{applicationId}/maintainers/{maintainerId}");
            using var response = await client.SendAsync(request);
            var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
            var errors = problem.GetProperty("errors");
            Assert.Equal(applicationInvalid, errors.TryGetProperty("applicationId", out _));
            Assert.Equal(maintainerInvalid, errors.TryGetProperty("maintainerId", out _));
        }

        if (applicationInvalid)
        {
            using var response = await client.GetAsync($"/api/v1/applications/{applicationId}/maintainers");
            var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
            Assert.True(problem.GetProperty("errors").TryGetProperty("applicationId", out _));
        }

        if (maintainerInvalid)
        {
            using var response = await client.GetAsync($"/api/v1/maintainers/{maintainerId}/applications");
            var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
            Assert.True(problem.GetProperty("errors").TryGetProperty("maintainerId", out _));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingParent_Returns404ForWritesAndLists(bool missingApplication)
    {
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var application = new Application { Title = "Microsoft Garage" };
        var maintainer = new Maintainer { Name = "Garage Makers", Email = "makers@example.com" };
        if (missingApplication)
        {
            database.Maintainers.Add(maintainer);
        }
        else
        {
            database.Applications.Add(application);
        }

        await database.SaveChangesAsync();
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, $"/api/v1/applications/{application.Id}/maintainers/{maintainer.Id}");
            using var response = await client.SendAsync(request);
            var problem = await AssertProblemAsync(response, HttpStatusCode.NotFound);
            Assert.Equal(missingApplication ? "Application not found" : "Maintainer not found", problem.GetProperty("title").GetString());
        }

        var missingPath = missingApplication
            ? $"/api/v1/applications/{application.Id}/maintainers"
            : $"/api/v1/maintainers/{maintainer.Id}/applications";
        using var missing = await client.GetAsync(missingPath);
        await AssertProblemAsync(missing, HttpStatusCode.NotFound);
        var existingPath = missingApplication
            ? $"/api/v1/maintainers/{maintainer.Id}/applications"
            : $"/api/v1/applications/{application.Id}/maintainers";
        Assert.Empty((await ReadPageAsync(client, existingPath)).GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NestedLists_UseResourceShapesFilteringOrderingAndBoundedPagination(bool listMaintainers)
    {
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var application = new Application { Title = "Microsoft Garage Locations" };
        var maintainer = new Maintainer { Name = "Garage Makers", Email = "makers@example.com" };
        database.AddRange(application, maintainer);
        var path = listMaintainers
            ? $"/api/v1/applications/{application.Id}/maintainers"
            : $"/api/v1/maintainers/{maintainer.Id}/applications";
        await database.SaveChangesAsync();
        var empty = await ReadPageAsync(client, path);
        Assert.Empty(empty.GetProperty("items").EnumerateArray());
        Assert.Equal(20, empty.GetProperty("limit").GetInt32());
        Assert.Equal(0, empty.GetProperty("offset").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("nextOffset").ValueKind);
        var ids = new List<string>();
        for (var i = 0; i < 23; i++)
        {
            var linkedApplication = listMaintainers ? application : new Application { Title = $"Microsoft Garage Location {i}" };
            var linkedMaintainer = listMaintainers ? new Maintainer { Name = $"Garage Maker {i}", Email = "makers@example.com" } : maintainer;
            if (listMaintainers)
            {
                database.Maintainers.Add(linkedMaintainer);
            }
            else
            {
                database.Applications.Add(linkedApplication);
            }

            database.ApplicationMaintainers.Add(new() { ApplicationId = linkedApplication.Id, MaintainerId = linkedMaintainer.Id });
            ids.Add((listMaintainers ? linkedMaintainer.Id : linkedApplication.Id).ToString());
        }

        database.AddRange(new Application { Title = "Microsoft Garage Unlinked" },
            new Maintainer { Name = "Garage Unlinked", Email = "unlinked@example.com" });
        await database.SaveChangesAsync();
        ids.Sort(StringComparer.Ordinal);
        var first = await ReadPageAsync(client, path);
        Assert.Equal(ids.Take(20), ReadIds(first));
        Assert.Equal(20, first.GetProperty("nextOffset").GetInt32());
        var last = await ReadPageAsync(client, path + "?offset=20");
        Assert.Equal(ids.Skip(20), ReadIds(last));
        Assert.Equal(20, last.GetProperty("offset").GetInt32());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextOffset").ValueKind);
        var exact = await ReadPageAsync(client, path + "?limit=3&offset=20");
        Assert.Equal(ids.Skip(20), ReadIds(exact));
        Assert.Equal(3, exact.GetProperty("limit").GetInt32());
        Assert.Equal(JsonValueKind.Null, exact.GetProperty("nextOffset").ValueKind);
        var minimum = await ReadPageAsync(client, path + "?limit=1&offset=1");
        Assert.Equal(ids.Skip(1).Take(1), ReadIds(minimum));
        Assert.Equal(2, minimum.GetProperty("nextOffset").GetInt32());
        var maximum = await ReadPageAsync(client, path + "?limit=100&offset=10000");
        Assert.Empty(ReadIds(maximum));
        Assert.Equal(100, maximum.GetProperty("limit").GetInt32());
        Assert.Equal(10000, maximum.GetProperty("offset").GetInt32());
        Assert.Equal(JsonValueKind.Null, maximum.GetProperty("nextOffset").ValueKind);
        foreach (var item in first.GetProperty("items").EnumerateArray())
        {
            var root = listMaintainers ? "maintainers" : "applications";
            Assert.Equal(item.GetRawText(), await client.GetStringAsync($"/api/v1/{root}/{item.GetProperty("id").GetGuid()}"));
        }
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("limit=bad")]
    [InlineData("limit=1.5")]
    [InlineData("limit=")]
    [InlineData("offset=-1")]
    [InlineData("offset=10001")]
    [InlineData("offset=bad")]
    [InlineData("offset=1.5")]
    [InlineData("offset=")]
    [InlineData("offset=2147483648")]
    public async Task InvalidPagination_Returns400InBothDirections(string query)
    {
        using var client = factory.CreateClient();
        foreach (var path in new[] { $"/api/v1/applications/{Guid.NewGuid()}/maintainers", $"/api/v1/maintainers/{Guid.NewGuid()}/applications" })
        {
            using var response = await client.GetAsync($"{path}?{query}");
            await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task InvalidListParameters_ReportIdAndBothPaginationErrorsTogether()
    {
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/api/v1/applications/not-a-guid/maintainers", "/api/v1/maintainers/not-a-guid/applications" })
        {
            using var response = await client.GetAsync(path + "?limit=0&offset=10001");
            var errors = (await AssertProblemAsync(response, HttpStatusCode.BadRequest)).GetProperty("errors");
            Assert.Equal(3, errors.EnumerateObject().Count());
            Assert.True(errors.TryGetProperty("limit", out _));
            Assert.True(errors.TryGetProperty("offset", out _));
        }
    }

    [Fact]
    public async Task OpenApi_DescribesBodylessMutationsAndNestedPages()
    {
        using var developmentFactory = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = developmentFactory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        var mutations = paths.GetProperty("/api/v1/applications/{applicationId}/maintainers/{maintainerId}");
        foreach (var method in new[] { "put", "delete" })
        {
            var operation = mutations.GetProperty(method);
            Assert.False(operation.TryGetProperty("requestBody", out _));
            var responses = operation.GetProperty("responses");
            Assert.True(responses.TryGetProperty("204", out var success));
            Assert.False(success.TryGetProperty("content", out _));
            Assert.True(responses.TryGetProperty("400", out _));
            Assert.True(responses.TryGetProperty("404", out _));
        }

        foreach (var path in new[] { "/api/v1/applications/{applicationId}/maintainers", "/api/v1/maintainers/{maintainerId}/applications" })
        {
            var responses = paths.GetProperty(path).GetProperty("get").GetProperty("responses");
            Assert.True(responses.TryGetProperty("200", out _));
            Assert.True(responses.TryGetProperty("400", out _));
            Assert.True(responses.TryGetProperty("404", out _));
        }
    }

    private static IEnumerable<string?> ReadIds(JsonElement page)
        => page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString());

    private static async Task<JsonElement> ReadPageAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "items", "limit", "offset", "nextOffset" },
            document.RootElement.EnumerateObject().Select(property => property.Name));
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)expected, document.RootElement.GetProperty("status").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("exception", out _));
        return document.RootElement.Clone();
    }
}

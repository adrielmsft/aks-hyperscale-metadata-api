using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MetadataApi.Contracts;
using MetadataApi.Persistence;
using MetadataApi.Persistence.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MetadataApi.Tests;

public sealed class ApplicationCrudApiTests : IDisposable
{
    private readonly MetadataApiFactory factory = new();

    [Fact]
    public async Task Put_ReplacesAllFieldsPreservesIdentityAndLinksAndIsLastWriteWins()
    {
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/v1/applications", new
        {
            title = "Microsoft Garage", version = "1.0.0", company = "Microsoft",
            website = "https://garage.example.com", source = "https://github.com/example/garage",
            license = "MIT", description = "Garage locations and staff"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var original = (await created.Content.ReadFromJsonAsync<ApplicationResponse>())!;
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var maintainer = new Maintainer { Name = "Garage Staff", Email = "garage@example.com" };
        database.Maintainers.Add(maintainer);
        database.ApplicationMaintainers.Add(new() { ApplicationId = original.Id, MaintainerId = maintainer.Id });
        await database.SaveChangesAsync();

        var replacement = new
        {
            title = " Microsoft Garage Locations ", version = " 2.0.0-rc.1+build.7 ",
            company = " Microsoft Garage ", website = " https://locations.example.com ",
            source = " https://github.com/example/garage-locations ", license = " Apache-2.0 ",
            description = " First floor\n\tSecond floor "
        };
        using var updated = await client.PutAsJsonAsync(created.Headers.Location, replacement);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var result = (await updated.Content.ReadFromJsonAsync<ApplicationResponse>())!;
        Assert.Equal(original.Id, result.Id);
        Assert.Equal("Microsoft Garage Locations", result.Title);
        Assert.Equal("2.0.0-rc.1+build.7", result.Version);
        Assert.Equal("Microsoft Garage", result.Company);
        Assert.Equal("https://locations.example.com", result.Website);
        Assert.Equal("https://github.com/example/garage-locations", result.Source);
        Assert.Equal("Apache-2.0", result.License);
        Assert.Equal("First floor\n\tSecond floor", result.Description);
        Assert.Equal(result, await client.GetFromJsonAsync<ApplicationResponse>(created.Headers.Location));

        using var cleared = await client.PutAsJsonAsync(created.Headers.Location, new { title = "Microsoft Garage" });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var expected = new ApplicationResponse(original.Id, "Microsoft Garage", null, null, null, null, null, null);
        Assert.Equal(expected, await cleared.Content.ReadFromJsonAsync<ApplicationResponse>());
        using var repeated = await client.PutAsJsonAsync(created.Headers.Location, new { title = "Microsoft Garage" });
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(expected, await client.GetFromJsonAsync<ApplicationResponse>(created.Headers.Location));
        Assert.Equal(expected.Title, (await database.Applications.AsNoTracking().SingleAsync()).Title);
        Assert.Single(await database.ApplicationMaintainers.ToListAsync());
        Assert.Single(await database.Maintainers.ToListAsync());
    }

    [Fact]
    public async Task Delete_CascadesOnlyTargetLinksAndRepeatReturnsNotFound()
    {
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var target = new Application { Title = "Microsoft Garage Locations" };
        var other = new Application { Title = "Microsoft Garage Staff" };
        var maintainer = new Maintainer { Name = "Garage Staff", Email = "garage@example.com" };
        database.AddRange(target, other, maintainer);
        database.ApplicationMaintainers.AddRange(
            new() { ApplicationId = target.Id, MaintainerId = maintainer.Id },
            new() { ApplicationId = other.Id, MaintainerId = maintainer.Id });
        await database.SaveChangesAsync();

        using var deleted = await client.DeleteAsync($"/api/v1/applications/{target.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await deleted.Content.ReadAsByteArrayAsync());
        using var fetched = await client.GetAsync($"/api/v1/applications/{target.Id}");
        await AssertProblemAsync(fetched, HttpStatusCode.NotFound);
        using var repeated = await client.DeleteAsync($"/api/v1/applications/{target.Id}");
        await AssertProblemAsync(repeated, HttpStatusCode.NotFound);
        Assert.Equal(other.Id, (await database.Applications.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(maintainer.Id, (await database.Maintainers.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(other.Id, (await database.ApplicationMaintainers.AsNoTracking().SingleAsync()).ApplicationId);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task PutAndDelete_InvalidIdReturnValidationProblem(string id)
    {
        using var client = factory.CreateClient();
        using var put = await client.PutAsJsonAsync($"/api/v1/applications/{id}", new { title = "Microsoft Garage" });
        using var delete = await client.DeleteAsync($"/api/v1/applications/{id}");
        foreach (var response in new[] { put, delete })
        {
            var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
            Assert.True(problem.GetProperty("errors").TryGetProperty("applicationId", out _));
        }
    }

    [Fact]
    public async Task PutAndDelete_MissingIdReturnNotFoundAndNeverCreate()
    {
        using var client = factory.CreateClient();
        var path = $"/api/v1/applications/{Guid.NewGuid()}";
        using var put = await client.PutAsJsonAsync(path, new { title = "Microsoft Garage" });
        using var delete = await client.DeleteAsync(path);
        await AssertProblemAsync(put, HttpStatusCode.NotFound);
        await AssertProblemAsync(delete, HttpStatusCode.NotFound);
        var page = (await client.GetFromJsonAsync<PagedResponse<ApplicationResponse>>("/api/v1/applications"))!;
        Assert.Empty(page.Items);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"title\":123}")]
    [InlineData("{\"title\":\"Microsoft Garage\",\"id\":\"54b28c61-8ec5-42c1-966b-3564c59bf965\"}")]
    [InlineData("{\"title\":\"Microsoft Garage\",\"maintainers\":[]}")]
    [InlineData("{\"title\":\"Microsoft Garage\",\"unknown\":true}")]
    [InlineData("{\"Title\":\"Microsoft Garage\"}")]
    [InlineData("{\"title\":\"Microsoft Garage\",\"title\":\"Garage Staff\"}")]
    [InlineData("{}")]
    [InlineData("{\"title\":null}")]
    [InlineData("{\"title\":\"   \"}")]
    public async Task Put_InvalidJsonOrRequiredFieldsDoesNotChangeStoredResource(string json)
    {
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/v1/applications", new { title = "Microsoft Garage", version = "1.0.0" });
        var expected = await created.Content.ReadAsStringAsync();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.PutAsync(created.Headers.Location, content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(expected, await client.GetStringAsync(created.Headers.Location));
    }

    [Fact]
    public async Task Put_ReportsAllInvalidFieldsWithoutWriting()
    {
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/v1/applications", new { title = "Microsoft Garage" });
        var expected = await created.Content.ReadAsStringAsync();
        using var response = await client.PutAsJsonAsync(created.Headers.Location, new
        {
            title = " ", version = "v1", company = "Garage\nStaff", website = "ftp://garage.example.com",
            source = "/garage", license = new string('a', 101), description = "Garage\0Staff"
        });
        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(7, problem.GetProperty("errors").EnumerateObject().Count());
        Assert.Equal(expected, await client.GetStringAsync(created.Headers.Location));
    }

    [Fact]
    public async Task Put_NonJsonReturnsUnsupportedMediaType()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent("Microsoft Garage", Encoding.UTF8, "text/plain");
        using var response = await client.PutAsync($"/api/v1/applications/{Guid.NewGuid()}", content);
        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=-1")]
    [InlineData("limit=101")]
    [InlineData("limit=1.5")]
    [InlineData("limit=abc")]
    [InlineData("limit=")]
    [InlineData("limit=2147483648")]
    [InlineData("offset=-1")]
    [InlineData("offset=10001")]
    [InlineData("offset=1.5")]
    [InlineData("offset=abc")]
    [InlineData("offset=")]
    [InlineData("offset=2147483648")]
    public async Task List_InvalidPaginationReturnsBadRequest(string query)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/v1/applications?{query}");
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_InvalidBoundsReportsBothFields()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/applications?limit=0&offset=10001");
        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        var errors = problem.GetProperty("errors");
        Assert.Equal(2, errors.EnumerateObject().Count());
        Assert.True(errors.TryGetProperty("limit", out _));
        Assert.True(errors.TryGetProperty("offset", out _));
    }

    [Fact]
    public async Task List_EmptyAndPopulatedPagesUseDefaultsOrderingAndOneRowLookahead()
    {
        using var client = factory.CreateClient();
        var empty = (await client.GetFromJsonAsync<PagedResponse<ApplicationResponse>>("/api/v1/applications"))!;
        Assert.Empty(empty.Items);
        Assert.Equal(20, empty.Limit);
        Assert.Equal(0, empty.Offset);
        Assert.Null(empty.NextOffset);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        database.Applications.AddRange(Enumerable.Range(1, 23).Reverse().Select(i => new Application
        {
            Id = Guid.Parse($"{i:x8}-0000-0000-0000-000000000000"), Title = $"Microsoft Garage {i}"
        }));
        await database.SaveChangesAsync();
        var expected = await database.Applications.AsNoTracking().OrderBy(entity => entity.Id)
            .Select(entity => entity.Id).ToListAsync();
        var first = (await client.GetFromJsonAsync<PagedResponse<ApplicationResponse>>("/api/v1/applications"))!;
        Assert.Equal(expected.Take(20), first.Items.Select(item => item.Id));
        Assert.Equal(20, first.NextOffset);
        var last = (await client.GetFromJsonAsync<PagedResponse<ApplicationResponse>>("/api/v1/applications?offset=20"))!;
        Assert.Equal(expected.Skip(20), last.Items.Select(item => item.Id));
        Assert.Equal(20, last.Offset);
        Assert.Null(last.NextOffset);
        var exact = (await client.GetFromJsonAsync<PagedResponse<ApplicationResponse>>("/api/v1/applications?limit=3&offset=20"))!;
        Assert.Equal(3, exact.Items.Count);
        Assert.Null(exact.NextOffset);
        var single = (await client.GetFromJsonAsync<PagedResponse<ApplicationResponse>>("/api/v1/applications?limit=1"))!;
        Assert.Single(single.Items);
        Assert.Equal(1, single.NextOffset);
        var max = (await client.GetFromJsonAsync<PagedResponse<ApplicationResponse>>("/api/v1/applications?limit=100&offset=10000"))!;
        Assert.Empty(max.Items);
        Assert.Equal(100, max.Limit);
        Assert.Equal(10000, max.Offset);
        Assert.Null(max.NextOffset);
    }

    [Fact]
    public async Task List_TraversalCapDoesNotAdvertiseUnsupportedOffsetsEvenWithMoreRows()
    {
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        database.Applications.AddRange(Enumerable.Range(1, 10002).Select(i => new Application
        {
            Id = Guid.Parse($"{i:x8}-0000-0000-0000-000000000000"), Title = $"Microsoft Garage {i}"
        }));
        await database.SaveChangesAsync();
        var before = (await client.GetFromJsonAsync<PagedResponse<ApplicationResponse>>("/api/v1/applications?limit=1&offset=9999"))!;
        Assert.Equal(10000, before.NextOffset);
        var cap = (await client.GetFromJsonAsync<PagedResponse<ApplicationResponse>>("/api/v1/applications?limit=1&offset=10000"))!;
        Assert.Single(cap.Items);
        Assert.Null(cap.NextOffset);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Writes_DatabaseContentionReturns503WithoutChangingResource(string method)
    {
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/v1/applications", new { title = "Microsoft Garage" });
        var expected = await created.Content.ReadAsStringAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        await using var transaction = await database.Database.BeginTransactionAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), created.Headers.Location);
        if (method == "PUT")
        {
            request.Content = JsonContent.Create(new { title = "Microsoft Garage Staff" });
        }

        using var response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
        Assert.DoesNotContain("SqliteException", await response.Content.ReadAsStringAsync());
        await transaction.RollbackAsync();
        Assert.Equal(expected, await client.GetStringAsync(created.Headers.Location));
    }

    [Fact]
    public async Task OpenApi_DescribesCrudQueryDefaultsAndFailureResponses()
    {
        using var development = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        var collection = paths.GetProperty("/api/v1/applications");
        var resource = paths.GetProperty("/api/v1/applications/{applicationId}");
        foreach (var (operation, name, success) in new[]
        {
            (collection.GetProperty("get"), "ListApplications", "200"),
            (resource.GetProperty("put"), "UpdateApplication", "200"),
            (resource.GetProperty("delete"), "DeleteApplication", "204")
        })
        {
            Assert.Equal(name, operation.GetProperty("operationId").GetString());
            foreach (var status in new[] { success, "400", "500", "503" })
            {
                Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
            }
        }

        Assert.True(resource.GetProperty("put").GetProperty("responses").TryGetProperty("404", out _));
        Assert.True(resource.GetProperty("put").GetProperty("responses").TryGetProperty("415", out _));
        Assert.True(resource.GetProperty("delete").GetProperty("responses").TryGetProperty("404", out _));
        var parameters = collection.GetProperty("get").GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(20, parameters.Single(p => p.GetProperty("name").GetString() == "limit")
            .GetProperty("schema").GetProperty("default").GetInt32());
        Assert.Equal(0, parameters.Single(p => p.GetProperty("name").GetString() == "offset")
            .GetProperty("schema").GetProperty("default").GetInt32());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task UnexpectedDatabaseFailures_ReturnGeneric500ProblemDetails(string method)
    {
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        await database.Database.ExecuteSqlRawAsync("DROP TABLE Applications");
        var path = method == "GET" ? "/api/v1/applications" : $"/api/v1/applications/{Guid.NewGuid()}";
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "PUT")
        {
            request.Content = JsonContent.Create(new { title = "Microsoft Garage" });
        }

        using var response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SqliteException", body);
        Assert.DoesNotContain("no such table", body);
        Assert.DoesNotContain("SELECT", body);
        Assert.DoesNotContain("UPDATE", body);
        Assert.DoesNotContain("DELETE", body);
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

    public void Dispose() => factory.Dispose();
}

using System.Diagnostics;
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

public sealed class MaintainerApiTests(MetadataApiFactory factory) : IClassFixture<MetadataApiFactory>
{
    private const string Root = "/api/v1/maintainers";

    [Fact]
    public async Task Crud_NormalizesPreservesCasingAndReplacesBothFields()
    {
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync(Root, new
        {
            name = " Zoë  O'Connor-李 ", email = " Garage.Team@Example.COM "
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var maintainer = (await created.Content.ReadFromJsonAsync<MaintainerResponse>())!;
        Assert.NotEqual(Guid.Empty, maintainer.Id);
        Assert.Equal("Zoë  O'Connor-李", maintainer.Name);
        Assert.Equal("Garage.Team@Example.COM", maintainer.Email);
        Assert.Equal($"{Root}/{maintainer.Id}", created.Headers.Location?.OriginalString);
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "id", "name", "email" }, document.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(maintainer, await client.GetFromJsonAsync<MaintainerResponse>(created.Headers.Location));

        using var replaced = await client.PutAsJsonAsync(created.Headers.Location, new
        {
            name = " Garage Makers ", email = " Makers@Example.COM "
        });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var expected = new MaintainerResponse(maintainer.Id, "Garage Makers", "Makers@Example.COM");
        Assert.Equal(expected, await replaced.Content.ReadFromJsonAsync<MaintainerResponse>());
        Assert.Equal(expected, await client.GetFromJsonAsync<MaintainerResponse>(created.Headers.Location));
        using var deleted = await client.DeleteAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal("", await deleted.Content.ReadAsStringAsync());
        using var missing = await client.GetAsync(created.Headers.Location);
        await AssertProblemAsync(missing, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DuplicateNamesAndEmails_CreateDistinctRecords()
    {
        using var client = factory.CreateClient();
        var ids = new HashSet<Guid>();
        for (var i = 0; i < 2; i++)
        {
            using var created = await client.PostAsJsonAsync(Root, new { name = "Garage Team", email = "garage@example.com" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var maintainer = (await created.Content.ReadFromJsonAsync<MaintainerResponse>())!;
            Assert.True(ids.Add(maintainer.Id));
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":null,\"email\":null}")]
    [InlineData("{\"name\":\" \",\"email\":\" \"}")]
    [InlineData("{\"name\":\"Garage\"}")]
    [InlineData("{\"email\":\"garage@example.com\"}")]
    [InlineData("{\"name\":\"Garage\\nLab\",\"email\":\"Garage <garage@example.com>\"}")]
    public async Task InvalidRequiredFields_OnCreateAndReplace_ReturnFieldErrors(string json)
    {
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync(Root, new { name = "Garage", email = "garage@example.com" });
        var original = await created.Content.ReadAsStringAsync();
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
        {
            using var request = new HttpRequestMessage(method, method == HttpMethod.Post ? Root : created.Headers.Location!.OriginalString)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(request);
            var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
            Assert.NotEmpty(problem.GetProperty("errors").EnumerateObject());
        }

        Assert.Equal(original, await client.GetStringAsync(created.Headers.Location));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"name\":123,\"email\":\"garage@example.com\"}")]
    [InlineData("{\"name\":\"Garage\",\"email\":true}")]
    [InlineData("{\"name\":\"Garage\",\"email\":\"garage@example.com\",\"id\":\"54b28c61-8ec5-42c1-966b-3564c59bf965\"}")]
    [InlineData("{\"name\":\"Garage\",\"email\":\"garage@example.com\",\"unknown\":true}")]
    [InlineData("{\"Name\":\"Garage\",\"email\":\"garage@example.com\"}")]
    [InlineData("{\"name\":\"Garage\",\"name\":\"Lab\",\"email\":\"garage@example.com\"}")]
    public async Task InvalidJsonAndUnknownProperties_OnCreateAndReplace_DoNotWrite(string json)
    {
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync(Root, new { name = "Garage", email = "garage@example.com" });
        var original = await created.Content.ReadAsStringAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var count = await database.Maintainers.CountAsync();
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
        {
            using var request = new HttpRequestMessage(method, method == HttpMethod.Post ? Root : created.Headers.Location!.OriginalString)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(request);
            await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        }

        Assert.Equal(count, await database.Maintainers.CountAsync());
        Assert.Equal(original, await client.GetStringAsync(created.Headers.Location));
    }

    [Fact]
    public async Task FieldLengthBoundaries_AreEnforcedOnCreateAndReplace()
    {
        using var client = factory.CreateClient();
        var name = new string('名', 200);
        var email = new string('a', 64) + "@" + new string('b', 185) + ".com";
        using var created = await client.PostAsJsonAsync(Root, new { name = " " + name + " ", email = " " + email + " " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var replaced = await client.PutAsJsonAsync(created.Headers.Location, new { name, email });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
        {
            using var request = new HttpRequestMessage(method, method == HttpMethod.Post ? Root : created.Headers.Location!.OriginalString)
            {
                Content = JsonContent.Create(new { name = name + "x", email = "a" + email })
            };
            using var response = await client.SendAsync(request);
            var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal(2, problem.GetProperty("errors").EnumerateObject().Count());
        }
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidIds_ReturnFieldErrorsForAllItemOperations(string id)
    {
        using var client = factory.CreateClient();
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, $"{Root}/{id}");
            if (method == HttpMethod.Put)
            {
                request.Content = JsonContent.Create(new { name = "Garage", email = "garage@example.com" });
            }

            using var response = await client.SendAsync(request);
            var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
            Assert.True(problem.GetProperty("errors").TryGetProperty("maintainerId", out _));
        }
    }

    [Fact]
    public async Task MissingIds_Return404ForAllItemOperations()
    {
        using var client = factory.CreateClient();
        var location = $"{Root}/{Guid.NewGuid()}";
        using var get = await client.GetAsync(location);
        using var put = await client.PutAsJsonAsync(location, new { name = "Garage", email = "garage@example.com" });
        using var delete = await client.DeleteAsync(location);
        foreach (var response in new[] { get, put, delete })
        {
            await AssertProblemAsync(response, HttpStatusCode.NotFound);
        }
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task UnsupportedContentType_Returns415(string method)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), method == "POST" ? Root : $"{Root}/{Guid.NewGuid()}")
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain")
        };
        using var response = await client.SendAsync(request);
        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType);
    }

    [Theory]
    [InlineData("?limit=0", "limit")]
    [InlineData("?limit=101", "limit")]
    [InlineData("?limit=-1", "limit")]
    [InlineData("?limit=1.5", "limit")]
    [InlineData("?limit=no", "limit")]
    [InlineData("?limit=", "limit")]
    [InlineData("?limit=2147483648", "limit")]
    [InlineData("?limit=1&limit=2", "limit")]
    [InlineData("?offset=-1", "offset")]
    [InlineData("?offset=10001", "offset")]
    [InlineData("?offset=1.5", "offset")]
    [InlineData("?offset=no", "offset")]
    [InlineData("?offset=", "offset")]
    [InlineData("?offset=2147483648", "offset")]
    public async Task InvalidPagination_IsRejectedNotClamped(string query, string field)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Root + query);
        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Pagination_DefaultsBoundsOrderingLookaheadAndSupportedNextOffset()
    {
        using var isolated = new MetadataApiFactory();
        using var client = isolated.CreateClient();
        var empty = (await client.GetFromJsonAsync<PagedResponse<MaintainerResponse>>(Root))!;
        Assert.Empty(empty.Items);
        Assert.Equal(20, empty.Limit);
        Assert.Equal(0, empty.Offset);
        Assert.Null(empty.NextOffset);
        await using var scope = isolated.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        await database.Database.ExecuteSqlRawAsync("""
            WITH RECURSIVE numbers(n) AS (
                SELECT 1 UNION ALL SELECT n + 1 FROM numbers WHERE n < 10002
            )
            INSERT INTO Maintainers (Id, Name, Email)
            SELECT printf('00000000-0000-0000-0000-%012d', n), 'Garage Team', 'garage@example.com'
            FROM numbers ORDER BY n DESC;
            """);
        var first = (await client.GetFromJsonAsync<PagedResponse<MaintainerResponse>>(Root))!;
        Assert.Equal(20, first.Items.Count);
        Assert.Equal(20, first.NextOffset);
        Assert.Equal(Enumerable.Range(1, 20).Select(Id), first.Items.Select(item => item.Id));
        var page = (await client.GetFromJsonAsync<PagedResponse<MaintainerResponse>>(Root + "?limit=100&offset=20"))!;
        Assert.Equal(100, page.Limit);
        Assert.Equal(20, page.Offset);
        Assert.Equal(120, page.NextOffset);
        Assert.Equal(Enumerable.Range(21, 100).Select(Id), page.Items.Select(item => item.Id));
        var edge = (await client.GetFromJsonAsync<PagedResponse<MaintainerResponse>>(Root + "?limit=1&offset=9999"))!;
        Assert.Equal(Id(10000), Assert.Single(edge.Items).Id);
        Assert.Equal(10000, edge.NextOffset);
        var lastSupported = (await client.GetFromJsonAsync<PagedResponse<MaintainerResponse>>(Root + "?limit=1&offset=10000"))!;
        Assert.Equal(Id(10001), Assert.Single(lastSupported.Items).Id);
        Assert.Null(lastSupported.NextOffset);
        var crossing = (await client.GetFromJsonAsync<PagedResponse<MaintainerResponse>>(Root + "?limit=3&offset=9998"))!;
        Assert.Equal(3, crossing.Items.Count);
        Assert.Null(crossing.NextOffset);
        await database.Database.ExecuteSqlRawAsync("DELETE FROM Maintainers;");
        database.Maintainers.Add(new Maintainer { Id = Id(1), Name = "Garage", Email = "garage@example.com" });
        await database.SaveChangesAsync();
        var exact = (await client.GetFromJsonAsync<PagedResponse<MaintainerResponse>>(Root + "?limit=1"))!;
        Assert.Single(exact.Items);
        Assert.Null(exact.NextOffset);
        var beyond = (await client.GetFromJsonAsync<PagedResponse<MaintainerResponse>>(Root + "?limit=100&offset=10000"))!;
        Assert.Empty(beyond.Items);
        Assert.Null(beyond.NextOffset);

        static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
    }

    [Fact]
    public async Task UpdatePreservesLinks_DeleteCascadesOnlyThisMaintainersLinks()
    {
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var first = new Maintainer { Name = "Garage Makers", Email = "makers@example.com" };
        var second = new Maintainer { Name = "Garage Lab", Email = "lab@example.com" };
        var applications = new[] { new Application { Title = "Garage Locations" }, new Application { Title = "Garage Staff" } };
        database.Maintainers.AddRange(first, second);
        database.Applications.AddRange(applications);
        await database.SaveChangesAsync();
        database.ApplicationMaintainers.AddRange(
            new ApplicationMaintainer { ApplicationId = applications[0].Id, MaintainerId = first.Id },
            new ApplicationMaintainer { ApplicationId = applications[1].Id, MaintainerId = first.Id },
            new ApplicationMaintainer { ApplicationId = applications[0].Id, MaintainerId = second.Id });
        await database.SaveChangesAsync();
        using var updated = await client.PutAsJsonAsync($"{Root}/{first.Id}", new { name = second.Name, email = second.Email });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(2, await database.ApplicationMaintainers.CountAsync(link => link.MaintainerId == first.Id));
        using var deleted = await client.DeleteAsync($"{Root}/{first.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(await database.Maintainers.AnyAsync(entity => entity.Id == first.Id));
        Assert.False(await database.ApplicationMaintainers.AnyAsync(link => link.MaintainerId == first.Id));
        Assert.True(await database.Maintainers.AnyAsync(entity => entity.Id == second.Id));
        Assert.True(await database.ApplicationMaintainers.AnyAsync(link => link.MaintainerId == second.Id));
        Assert.Equal(2, await database.Applications.CountAsync(entity => applications.Select(a => a.Id).Contains(entity.Id)));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task DatabaseContention_Returns503AndLeavesDataUnchanged(string method)
    {
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync(Root, new { name = "Garage", email = "garage@example.com" });
        var original = await created.Content.ReadAsStringAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var count = await database.Maintainers.CountAsync();
        await using var transaction = await database.Database.BeginTransactionAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), method == "POST" ? Root : created.Headers.Location!.OriginalString);
        if (method != "DELETE")
        {
            request.Content = JsonContent.Create(new { name = "Blocked Garage", email = "blocked@example.com" });
        }

        var watch = Stopwatch.StartNew();
        using var response = await client.SendAsync(request);
        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(12));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SqliteException", body);
        Assert.DoesNotContain("INSERT", body);
        Assert.DoesNotContain("UPDATE", body);
        Assert.DoesNotContain("DELETE", body);
        await transaction.RollbackAsync();
        Assert.Equal(count, await database.Maintainers.CountAsync());
        Assert.Equal(original, await client.GetStringAsync(created.Headers.Location));
    }

    [Fact]
    public async Task OpenApi_DescribesAllOperationsAndResponses()
    {
        using var development = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        foreach (var (path, method, operation, success) in new[]
        {
            (Root, "post", "CreateMaintainer", "201"),
            (Root, "get", "ListMaintainers", "200"),
            (Root + "/{maintainerId}", "get", "GetMaintainer", "200"),
            (Root + "/{maintainerId}", "put", "UpdateMaintainer", "200"),
            (Root + "/{maintainerId}", "delete", "DeleteMaintainer", "204")
        })
        {
            var endpoint = paths.GetProperty(path).GetProperty(method);
            Assert.Equal(operation, endpoint.GetProperty("operationId").GetString());
            var responses = endpoint.GetProperty("responses");
            Assert.True(responses.TryGetProperty(success, out _));
            Assert.True(responses.TryGetProperty("400", out _));
            Assert.True(responses.TryGetProperty("503", out _));
        }
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

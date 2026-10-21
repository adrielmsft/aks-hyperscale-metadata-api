using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MetadataApi.E2E.Tests;

public sealed class ApplicationMaintainerE2ETests(ApiServerFixture server) : IClassFixture<ApiServerFixture>
{
    [Fact]
    public async Task Associations_AreIdempotentListedBothWaysAndPersistAcrossRestart()
    {
        var application = await CreateAsync("applications", new { title = "Microsoft Garage Locations", version = "1.0.0" });
        var secondApplication = await CreateAsync("applications", new { title = "Microsoft Garage Staff" });
        var maintainer = await CreateAsync("maintainers", new { name = "Garage Makers", email = "Garage.Makers@example.com" });
        var secondMaintainer = await CreateAsync("maintainers", new { name = "Garage Lab Team", email = "lab@example.com" });
        var path = LinkPath(application, maintainer);
        var forwardPath = ResourcePath("applications", application) + "/maintainers";
        var reversePath = ResourcePath("maintainers", maintainer) + "/applications";
        Assert.Empty((await ReadPageAsync(forwardPath)).GetProperty("items").EnumerateArray());
        Assert.Empty((await ReadPageAsync(reversePath)).GetProperty("items").EnumerateArray());

        await AssertNoContentAsync(HttpMethod.Put, path);
        await AssertNoContentAsync(HttpMethod.Put, path);
        await AssertNoContentAsync(HttpMethod.Put, LinkPath(application, secondMaintainer));
        await AssertNoContentAsync(HttpMethod.Put, LinkPath(secondApplication, maintainer));
        await AssertPagesAsync(forwardPath, maintainer, secondMaintainer);
        await AssertPagesAsync(reversePath, application, secondApplication);

        await server.RestartAsync();
        Assert.Equal(application.GetRawText(), await server.Client.GetStringAsync(ResourcePath("applications", application)));
        Assert.Equal(maintainer.GetRawText(), await server.Client.GetStringAsync(ResourcePath("maintainers", maintainer)));
        await AssertPagesAsync(forwardPath, maintainer, secondMaintainer);
        await AssertPagesAsync(reversePath, application, secondApplication);

        using var updatedApplication = await server.Client.PutAsJsonAsync(ResourcePath("applications", application),
            new { title = "Microsoft Garage Locations Updated" });
        using var updatedMaintainer = await server.Client.PutAsJsonAsync(ResourcePath("maintainers", maintainer),
            new { name = "Garage Makers Updated", email = "Garage.Updated@example.com" });
        Assert.Equal(HttpStatusCode.OK, updatedApplication.StatusCode);
        Assert.Equal(HttpStatusCode.OK, updatedMaintainer.StatusCode);
        var replacementApplication = await ReadJsonAsync(updatedApplication);
        var replacementMaintainer = await ReadJsonAsync(updatedMaintainer);
        await AssertPagesAsync(forwardPath, replacementMaintainer, secondMaintainer);
        await AssertPagesAsync(reversePath, replacementApplication, secondApplication);

        await AssertNoContentAsync(HttpMethod.Delete, path);
        await AssertNoContentAsync(HttpMethod.Delete, path);
        await server.RestartAsync();
        var forward = (await ReadPageAsync(forwardPath)).GetProperty("items");
        var reverse = (await ReadPageAsync(reversePath)).GetProperty("items");
        Assert.Equal(secondMaintainer.GetRawText(), Assert.Single(forward.EnumerateArray()).GetRawText());
        Assert.Equal(secondApplication.GetRawText(), Assert.Single(reverse.EnumerateArray()).GetRawText());
        Assert.Equal(replacementApplication.GetRawText(), await server.Client.GetStringAsync(ResourcePath("applications", application)));
        Assert.Equal(replacementMaintainer.GetRawText(), await server.Client.GetStringAsync(ResourcePath("maintainers", maintainer)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParentDeletion_RemovesOnlyItsLinksAndPersistsAcrossRestart(bool deleteApplication)
    {
        var application = await CreateAsync("applications", new { title = "Microsoft Garage Locations" });
        var otherApplication = await CreateAsync("applications", new { title = "Microsoft Garage Staff" });
        var maintainer = await CreateAsync("maintainers", new { name = "Garage Makers", email = "makers@example.com" });
        var otherMaintainer = await CreateAsync("maintainers", new { name = "Garage Lab Team", email = "lab@example.com" });
        foreach (var app in new[] { application, otherApplication })
        {
            foreach (var maker in new[] { maintainer, otherMaintainer })
            {
                await AssertNoContentAsync(HttpMethod.Put, LinkPath(app, maker));
            }
        }

        var deletedPath = deleteApplication ? ResourcePath("applications", application) : ResourcePath("maintainers", maintainer);
        await AssertNoContentAsync(HttpMethod.Delete, deletedPath);
        await server.RestartAsync();
        using var missing = await server.Client.GetAsync(deletedPath);
        await AssertProblemAsync(missing, HttpStatusCode.NotFound);
        using var missingList = await server.Client.GetAsync(deletedPath + (deleteApplication ? "/maintainers" : "/applications"));
        await AssertProblemAsync(missingList, HttpStatusCode.NotFound);
        foreach (var survivor in deleteApplication ? new[] { maintainer, otherMaintainer } : new[] { application, otherApplication })
        {
            var root = deleteApplication ? "maintainers" : "applications";
            Assert.Equal(survivor.GetRawText(), await server.Client.GetStringAsync(ResourcePath(root, survivor)));
            var page = await ReadPageAsync(ResourcePath(root, survivor) + (deleteApplication ? "/applications" : "/maintainers"));
            var remaining = Assert.Single(page.GetProperty("items").EnumerateArray());
            Assert.Equal((deleteApplication ? otherApplication : otherMaintainer).GetRawText(), remaining.GetRawText());
        }
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidIds_ReturnValidationProblemsOverRealHttp(string id)
    {
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, $"/api/v1/applications/{id}/maintainers/{id}");
            using var response = await server.Client.SendAsync(request);
            var errors = (await AssertProblemAsync(response, HttpStatusCode.BadRequest)).GetProperty("errors");
            Assert.Equal(2, errors.EnumerateObject().Count());
            Assert.True(errors.TryGetProperty("applicationId", out _));
            Assert.True(errors.TryGetProperty("maintainerId", out _));
        }
    }

    [Fact]
    public async Task MissingTargetsAndInvalidPagination_ReturnProblemDetails()
    {
        var application = await CreateAsync("applications", new { title = "Microsoft Garage Locations" });
        var maintainer = await CreateAsync("maintainers", new { name = "Garage Makers", email = "makers@example.com" });
        foreach (var path in new[]
        {
            ResourcePath("applications", application) + $"/maintainers/{Guid.NewGuid()}",
            $"/api/v1/applications/{Guid.NewGuid()}/maintainers/{maintainer.GetProperty("id").GetGuid()}"
        })
        {
            foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete })
            {
                using var request = new HttpRequestMessage(method, path);
                using var response = await server.Client.SendAsync(request);
                await AssertProblemAsync(response, HttpStatusCode.NotFound);
            }
        }

        foreach (var path in new[] { $"/api/v1/applications/{Guid.NewGuid()}/maintainers", $"/api/v1/maintainers/{Guid.NewGuid()}/applications" })
        {
            using var missing = await server.Client.GetAsync(path);
            await AssertProblemAsync(missing, HttpStatusCode.NotFound);
            using var invalid = await server.Client.GetAsync(path + "?limit=0&offset=10001");
            var errors = (await AssertProblemAsync(invalid, HttpStatusCode.BadRequest)).GetProperty("errors");
            Assert.Equal(2, errors.EnumerateObject().Count());
        }
    }

    private async Task<JsonElement> CreateAsync(string resource, object body)
    {
        using var response = await server.Client.PostAsJsonAsync($"/api/v1/{resource}", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private async Task AssertNoContentAsync(HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        using var response = await server.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    private async Task AssertPagesAsync(string path, params JsonElement[] resources)
    {
        var expected = resources.OrderBy(resource => resource.GetProperty("id").GetString(), StringComparer.Ordinal).ToArray();
        var all = await ReadPageAsync(path);
        Assert.Equal(20, all.GetProperty("limit").GetInt32());
        Assert.Equal(0, all.GetProperty("offset").GetInt32());
        Assert.Equal(JsonValueKind.Null, all.GetProperty("nextOffset").ValueKind);
        Assert.Equal(expected.Select(item => item.GetRawText()), all.GetProperty("items").EnumerateArray().Select(item => item.GetRawText()));
        var first = await ReadPageAsync(path + "?limit=1");
        Assert.Equal(1, first.GetProperty("limit").GetInt32());
        Assert.Equal(expected[0].GetRawText(), Assert.Single(first.GetProperty("items").EnumerateArray()).GetRawText());
        Assert.Equal(1, first.GetProperty("nextOffset").GetInt32());
        var last = await ReadPageAsync(path + "?limit=1&offset=1");
        Assert.Equal(expected[1].GetRawText(), Assert.Single(last.GetProperty("items").EnumerateArray()).GetRawText());
        Assert.Equal(1, last.GetProperty("offset").GetInt32());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextOffset").ValueKind);
    }

    private async Task<JsonElement> ReadPageAsync(string path)
    {
        using var response = await server.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var page = await ReadJsonAsync(response);
        Assert.Equal(new[] { "items", "limit", "offset", "nextOffset" }, page.EnumerateObject().Select(property => property.Name));
        return page;
    }

    private static string ResourcePath(string resource, JsonElement item)
        => $"/api/v1/{resource}/{item.GetProperty("id").GetGuid()}";

    private static string LinkPath(JsonElement application, JsonElement maintainer)
        => ResourcePath("applications", application) + $"/maintainers/{maintainer.GetProperty("id").GetGuid()}";

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJsonAsync(response);
        Assert.Equal((int)expected, problem.GetProperty("status").GetInt32());
        Assert.False(problem.TryGetProperty("exception", out _));
        return problem;
    }
}

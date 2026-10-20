using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace MetadataApi.E2E.Tests;

public sealed class MaintainerE2ETests(ApiServerFixture server) : IClassFixture<ApiServerFixture>
{
    private const string Root = "/api/v1/maintainers";

    [Fact]
    public async Task Crud_UsesRealHttpAndPersistsReplacementAcrossRestart()
    {
        using var created = await server.Client.PostAsJsonAsync(Root, new
        {
            name = " Garage Zoë  O'Connor-李 ", email = " Garage.Team@Example.COM "
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("application/json", created.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var maintainer = document.RootElement;
        var id = maintainer.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(new[] { "id", "name", "email" }, maintainer.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Garage Zoë  O'Connor-李", maintainer.GetProperty("name").GetString());
        Assert.Equal("Garage.Team@Example.COM", maintainer.GetProperty("email").GetString());
        Assert.Equal($"{Root}/{id}", created.Headers.Location?.OriginalString);
        Assert.Equal(maintainer.GetRawText(), await server.Client.GetStringAsync(created.Headers.Location));

        using var updated = await server.Client.PutAsJsonAsync(created.Headers.Location, new
        {
            name = " Garage Makers ", email = " Makers@Example.COM "
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var replacement = await updated.Content.ReadAsStringAsync();
        using var replaced = JsonDocument.Parse(replacement);
        Assert.Equal(id, replaced.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Garage Makers", replaced.RootElement.GetProperty("name").GetString());
        Assert.Equal("Makers@Example.COM", replaced.RootElement.GetProperty("email").GetString());
        await server.RestartAsync();
        Assert.Equal(replacement, await server.Client.GetStringAsync(created.Headers.Location));

        using var deleted = await server.Client.DeleteAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal("", await deleted.Content.ReadAsStringAsync());
        using var missing = await server.Client.GetAsync(created.Headers.Location);
        await AssertProblemAsync(missing, HttpStatusCode.NotFound);
        using var deletedAgain = await server.Client.DeleteAsync(created.Headers.Location);
        await AssertProblemAsync(deletedAgain, HttpStatusCode.NotFound);
        using var updateMissing = await server.Client.PutAsJsonAsync(created.Headers.Location, new
        {
            name = "Garage", email = "garage@example.com"
        });
        await AssertProblemAsync(updateMissing, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DuplicateContactMetadata_IsAllowed()
    {
        var ids = new HashSet<Guid>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await server.Client.PostAsJsonAsync(Root, new
            {
                name = "Garage Makers", email = "makers@example.com"
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(ids.Add(document.RootElement.GetProperty("id").GetGuid()));
        }
    }

    [Fact]
    public async Task Pagination_UsesBoundedEnvelopeAndDatabaseIdOrder()
    {
        var existing = await ReadPageAsync("?limit=100");
        foreach (var item in existing.GetProperty("items").EnumerateArray())
        {
            using var deleted = await server.Client.DeleteAsync($"{Root}/{item.GetProperty("id").GetGuid()}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var empty = await ReadPageAsync("");
        Assert.Empty(empty.GetProperty("items").EnumerateArray());
        Assert.Equal(20, empty.GetProperty("limit").GetInt32());
        Assert.Equal(0, empty.GetProperty("offset").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("nextOffset").ValueKind);
        var ids = new List<string>();
        for (var i = 0; i < 22; i++)
        {
            using var response = await server.Client.PostAsJsonAsync(Root, new
            {
                name = $"Garage Maker {i}", email = "makers@example.com"
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            ids.Add(document.RootElement.GetProperty("id").GetString()!);
        }

        ids.Sort(StringComparer.Ordinal);
        var first = await ReadPageAsync("");
        Assert.Equal(ids.Take(20), first.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        Assert.Equal(20, first.GetProperty("nextOffset").GetInt32());
        var last = await ReadPageAsync("?limit=20&offset=20");
        Assert.Equal(ids.Skip(20), last.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextOffset").ValueKind);
        var minimum = await ReadPageAsync("?limit=1&offset=0");
        Assert.Single(minimum.GetProperty("items").EnumerateArray());
        Assert.Equal(1, minimum.GetProperty("nextOffset").GetInt32());
        var maximum = await ReadPageAsync("?limit=100&offset=10000");
        Assert.Empty(maximum.GetProperty("items").EnumerateArray());
        Assert.Equal(100, maximum.GetProperty("limit").GetInt32());
        Assert.Equal(10000, maximum.GetProperty("offset").GetInt32());
        Assert.Equal(JsonValueKind.Null, maximum.GetProperty("nextOffset").ValueKind);
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?limit=1.5")]
    [InlineData("?limit=bad")]
    [InlineData("?offset=-1")]
    [InlineData("?offset=10001")]
    [InlineData("?offset=bad")]
    public async Task InvalidPagination_Returns400FieldErrors(string query)
    {
        using var response = await server.Client.GetAsync(Root + query);
        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.NotEmpty(problem.GetProperty("errors").EnumerateObject());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":null,\"email\":null}")]
    [InlineData("{\"name\":\" \",\"email\":\" \"}")]
    [InlineData("{\"name\":\"Garage\"}")]
    [InlineData("{\"name\":\"Garage\\nLab\",\"email\":\"Garage <garage@example.com>\"}")]
    public async Task InvalidFields_OnCreateAndReplace_ReturnValidationProblems(string json)
    {
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
        {
            using var request = new HttpRequestMessage(method, method == HttpMethod.Post ? Root : $"{Root}/{Guid.NewGuid()}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            using var response = await server.Client.SendAsync(request);
            var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
            Assert.NotEmpty(problem.GetProperty("errors").EnumerateObject());
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"name\":123}")]
    [InlineData("{\"name\":\"Garage\",\"email\":\"garage@example.com\",\"unknown\":true}")]
    [InlineData("{\"name\":\"Garage\",\"email\":\"garage@example.com\",\"id\":\"54b28c61-8ec5-42c1-966b-3564c59bf965\"}")]
    [InlineData("{\"name\":\"Garage\",\"name\":\"Makers\",\"email\":\"garage@example.com\"}")]
    public async Task InvalidJson_OnCreateAndReplace_Returns400(string json)
    {
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
        {
            using var request = new HttpRequestMessage(method, method == HttpMethod.Post ? Root : $"{Root}/{Guid.NewGuid()}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            using var response = await server.Client.SendAsync(request);
            await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        }
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidRouteIds_Return400ForEveryItemOperation(string id)
    {
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, $"{Root}/{id}");
            if (method == HttpMethod.Put)
            {
                request.Content = JsonContent.Create(new { name = "Garage", email = "garage@example.com" });
            }

            using var response = await server.Client.SendAsync(request);
            var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
            Assert.True(problem.GetProperty("errors").TryGetProperty("maintainerId", out _));
        }
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task WrongContentType_Returns415(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), method == "POST" ? Root : $"{Root}/{Guid.NewGuid()}")
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain")
        };
        using var response = await server.Client.SendAsync(request);
        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task LengthBoundaries_AreEnforcedOverRealHttp()
    {
        var name = new string('名', 200);
        var email = new string('a', 64) + "@" + new string('b', 185) + ".com";
        using var created = await server.Client.PostAsJsonAsync(Root, new { name, email });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var updated = await server.Client.PutAsJsonAsync(created.Headers.Location, new { name, email });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
        {
            using var request = new HttpRequestMessage(method, method == HttpMethod.Post ? Root : created.Headers.Location!.OriginalString)
            {
                Content = JsonContent.Create(new { name = name + "x", email = "a" + email })
            };
            using var response = await server.Client.SendAsync(request);
            var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal(2, problem.GetProperty("errors").EnumerateObject().Count());
        }
    }

    private async Task<JsonElement> ReadPageAsync(string query)
    {
        using var response = await server.Client.GetAsync(Root + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

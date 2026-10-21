using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;

namespace MetadataApi.Tests;

public sealed class OpenApiTests
{
    [Fact]
    public async Task Document_matches_committed_snapshot_and_documents_contract()
    {
        using var source = new MetadataApiFactory();
        using var factory = source.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        AssertContract(document);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MetadataApi.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "docs", "openapi.json");
        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI_SNAPSHOT") == "1"
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
        {
            await File.WriteAllTextAsync(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }

        Assert.True(File.Exists(path), "Missing docs/openapi.json. Run the OpenApiTests with UPDATE_OPENAPI_SNAPSHOT=1 to regenerate.");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(path)), document),
            "docs/openapi.json is stale. Run the OpenApiTests with UPDATE_OPENAPI_SNAPSHOT=1 and commit the updated document.");
    }
    private static void AssertContract(JsonNode document)
    {
        Assert.Equal("http", document["components"]!["securitySchemes"]!["Bearer"]!["type"]!.GetValue<string>());
        Assert.Equal("bearer", document["components"]!["securitySchemes"]!["Bearer"]!["scheme"]!.GetValue<string>());
        Assert.Empty(document["security"]![0]!["Bearer"]!.AsArray());
        var paths = document["paths"]!.AsObject();
        Assert.Equal(7, paths.Count);
        Assert.All(paths, path => Assert.StartsWith("/api/v1/", path.Key));
        var operations = paths.SelectMany(path => path.Value!.AsObject()).Select(operation => operation.Value!).ToArray();
        Assert.Equal(14, operations.Length);
        foreach (var operation in operations)
        {
            Assert.False(string.IsNullOrWhiteSpace(operation["summary"]?.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(operation["description"]?.GetValue<string>()));
            Assert.NotEmpty(operation["tags"]!.AsArray());
            Assert.Null(operation["security"]); // All API operations inherit the document-level requirement.
            foreach (var status in new[] { "400", "401", "413", "429", "500", "503" })
            {
                Assert.NotNull(operation["responses"]![status]!["content"]!["application/problem+json"]!["schema"]);
            }
            Assert.Equal(operation["requestBody"] is not null, operation["responses"]!["415"] is not null);
        }

        var paginated = operations.Where(operation => operation["parameters"]?.AsArray()
            .Any(parameter => parameter!["name"]!.GetValue<string>() == "limit") == true).ToArray();
        Assert.Equal(4, paginated.Length);
        foreach (var operation in paginated)
        {
            foreach (var name in new[] { "limit", "offset" })
            {
                var schema = operation["parameters"]!.AsArray().Single(parameter => parameter!["name"]!.GetValue<string>() == name)!["schema"]!;
                Assert.Equal("integer", schema["type"]!.GetValue<string>());
                Assert.Equal(name == "limit" ? 20 : 0, schema["default"]!.GetValue<int>());
                Assert.Equal(name == "limit" ? 1 : 0, schema["minimum"]!.GetValue<int>());
                Assert.Equal(name == "limit" ? 100 : 10000, schema["maximum"]!.GetValue<int>());
            }
        }

        var request = document["components"]!["schemas"]!["CreateApplicationRequest"]!;
        Assert.Contains(request["required"]!.AsArray(), property => property!.GetValue<string>() == "title");
        Assert.Equal("string", request["properties"]!["title"]!["type"]!.GetValue<string>());
        Assert.Contains(request["properties"]!["version"]!["type"]!.AsArray(), type => type!.GetValue<string>() == "null");
    }
}


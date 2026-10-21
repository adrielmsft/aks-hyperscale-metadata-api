using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MetadataApi.Tests;

public sealed class MetadataApiFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "integration-test-key-not-a-real-secret-32";

    private readonly TestDatabase database = new();
    private readonly IReadOnlyDictionary<string, string> settings;

    public MetadataApiFactory() : this(new Dictionary<string, string>()) { }

    internal MetadataApiFactory(IReadOnlyDictionary<string, string> settings) => this.settings = settings;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Path", database.FilePath)
            .UseSetting("Authentication:ApiKey", ApiKey);
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    }

    public HttpClient CreateUnauthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            database.Dispose();
        }
    }
}

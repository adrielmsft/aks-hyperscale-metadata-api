using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MetadataApi.Tests;

public sealed class MetadataApiFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "integration-test-key-not-a-real-secret-32";

    private readonly TestDatabase database = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.UseSetting("Database:Path", database.FilePath)
            .UseSetting("Authentication:ApiKey", ApiKey);

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

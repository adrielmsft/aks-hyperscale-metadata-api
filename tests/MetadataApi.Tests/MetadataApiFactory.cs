using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MetadataApi.Tests;

public sealed class MetadataApiFactory : WebApplicationFactory<Program>
{
    private readonly TestDatabase database = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.UseSetting("Database:Path", database.FilePath);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            database.Dispose();
        }
    }
}

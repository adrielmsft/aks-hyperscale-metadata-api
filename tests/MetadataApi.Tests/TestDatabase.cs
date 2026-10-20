using MetadataApi.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MetadataApi.Tests;

internal sealed class TestDatabase : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"metadata-api-tests-{Guid.NewGuid():N}");
    private bool disposed;

    public string FilePath => Path.Combine(directory, "metadata.db");

    public TestDatabase(bool migrate = true)
    {
        Directory.CreateDirectory(directory);
        if (migrate)
        {
            using var database = CreateContext();
            database.Database.Migrate();
        }
    }

    public MetadataDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MetadataDbContext>();
        options.UseMetadataSqlite(FilePath, directory);
        return new MetadataDbContext(options.Options);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Directory.Delete(directory, recursive: true);
        disposed = true;
    }
}

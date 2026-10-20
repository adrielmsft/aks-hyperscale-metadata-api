using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MetadataApi.Persistence;

public static class MetadataDatabase
{
    public const int LockTimeoutSeconds = 5;

    public static void UseMetadataSqlite(
        this DbContextOptionsBuilder options,
        string? databasePath,
        string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || databasePath == ":memory:")
        {
            throw new InvalidOperationException("Database:Path must specify a file-backed SQLite database.");
        }

        var fullPath = Path.GetFullPath(databasePath, contentRootPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Database:Path must have a parent directory.");
        Directory.CreateDirectory(directory);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = LockTimeoutSeconds
        }.ToString();

        options.UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(LockTimeoutSeconds))
            .AddInterceptors(new SqlitePragmaInterceptor());
    }

    public static async Task InitializeAsync(
        MetadataDbContext database,
        CancellationToken cancellationToken = default)
    {
        var pendingMigrations = await database.Database.GetPendingMigrationsAsync(cancellationToken);
        if (pendingMigrations.Any())
        {
            throw new InvalidOperationException(
                "The database has pending migrations. Run 'dotnet ef database update --project src\\MetadataApi' before starting the API.");
        }

        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            using var command = database.Database.GetDbConnection().CreateCommand();
            command.CommandTimeout = LockTimeoutSeconds;
            command.CommandText = "PRAGMA journal_mode=WAL;";
            var journalMode = await command.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(journalMode as string, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("SQLite WAL journal mode could not be enabled.");
            }
        }
        finally
        {
            await database.Database.CloseConnectionAsync();
        }
    }
}

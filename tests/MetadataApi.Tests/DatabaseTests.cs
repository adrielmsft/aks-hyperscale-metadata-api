using System.Diagnostics;
using MetadataApi.Persistence;
using MetadataApi.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MetadataApi.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly TestDatabase store = new();

    [Fact]
    public async Task Migrations_CanBeAppliedRepeatedly()
    {
        await using var database = store.CreateContext();

        await database.Database.MigrateAsync();
        await MetadataDatabase.InitializeAsync(database);

        Assert.Single(await database.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await database.Database.GetPendingMigrationsAsync());
        Assert.Empty(await database.Applications.ToListAsync());
        Assert.Empty(await database.Maintainers.ToListAsync());
        Assert.Empty(await database.ApplicationMaintainers.ToListAsync());
    }

    [Fact]
    public async Task Settings_AreAppliedOnEveryConnection()
    {
        await using var database = store.CreateContext();
        await MetadataDatabase.InitializeAsync(database);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var reopened = store.CreateContext();
            await reopened.Database.OpenConnectionAsync();
            using var command = reopened.Database.GetDbConnection().CreateCommand();

            command.CommandText = "PRAGMA foreign_keys;";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
            command.CommandText = "PRAGMA synchronous;";
            Assert.Equal(2L, await command.ExecuteScalarAsync());
            command.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", await command.ExecuteScalarAsync());
            Assert.Equal(5, reopened.Database.GetCommandTimeout());
            Assert.Equal(5, Assert.IsType<SqliteConnection>(reopened.Database.GetDbConnection()).DefaultTimeout);
        }
    }

    [Fact]
    public async Task RecordsAndAssociations_SurviveReopeningDatabase()
    {
        var application = new Application { Title = "Inventory Portal" };
        var maintainer = new Maintainer { Name = "Alex Morgan", Email = "alex@example.com" };
        Assert.NotEqual(Guid.Empty, application.Id);
        Assert.NotEqual(Guid.Empty, maintainer.Id);

        await using (var database = store.CreateContext())
        {
            database.AddRange(application, maintainer);
            database.ApplicationMaintainers.Add(new ApplicationMaintainer
            {
                ApplicationId = application.Id,
                MaintainerId = maintainer.Id
            });
            await database.SaveChangesAsync();
        }

        await using var reopened = store.CreateContext();
        Assert.Equal("Inventory Portal", (await reopened.Applications.SingleAsync()).Title);
        Assert.Null((await reopened.Applications.SingleAsync()).Version);
        Assert.Equal("alex@example.com", (await reopened.Maintainers.SingleAsync()).Email);
        var link = await reopened.ApplicationMaintainers.SingleAsync();
        Assert.Equal(application.Id, link.ApplicationId);
        Assert.Equal(maintainer.Id, link.MaintainerId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Association_WithMissingParent_IsRejected(bool missingApplication)
    {
        await using var database = store.CreateContext();
        var application = new Application { Title = "Portal" };
        var maintainer = new Maintainer { Name = "Alex", Email = "alex@example.com" };
        if (missingApplication)
        {
            database.Maintainers.Add(maintainer);
        }
        else
        {
            database.Applications.Add(application);
        }

        await database.SaveChangesAsync();
        database.ApplicationMaintainers.Add(new ApplicationMaintainer
        {
            ApplicationId = application.Id,
            MaintainerId = maintainer.Id
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => database.SaveChangesAsync());
        Assert.Equal(787, Assert.IsType<SqliteException>(exception.InnerException).SqliteExtendedErrorCode);
    }

    [Fact]
    public async Task DuplicateAssociation_IsRejectedByDatabase()
    {
        var (applicationId, maintainerId) = await SeedAssociationAsync();
        await using var database = store.CreateContext();
        database.ApplicationMaintainers.Add(new ApplicationMaintainer
        {
            ApplicationId = applicationId,
            MaintainerId = maintainerId
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => database.SaveChangesAsync());

        Assert.Equal(1555, Assert.IsType<SqliteException>(exception.InnerException).SqliteExtendedErrorCode);
        await using var verification = store.CreateContext();
        Assert.Single(await verification.ApplicationMaintainers.ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeletingParent_DeletesOnlyItsLinks(bool deleteApplication)
    {
        var (applicationId, maintainerId) = await SeedAssociationAsync();
        await using (var database = store.CreateContext())
        {
            if (deleteApplication)
            {
                var application = await database.Applications.SingleAsync(entity => entity.Id == applicationId);
                database.Applications.Remove(application);
            }
            else
            {
                var maintainer = await database.Maintainers.SingleAsync(entity => entity.Id == maintainerId);
                database.Maintainers.Remove(maintainer);
            }

            await database.SaveChangesAsync();
        }

        await using var verification = store.CreateContext();
        Assert.Empty(await verification.ApplicationMaintainers.ToListAsync());
        Assert.Equal(deleteApplication ? 0 : 1, await verification.Applications.CountAsync());
        Assert.Equal(deleteApplication ? 1 : 0, await verification.Maintainers.CountAsync());
    }

    [Fact]
    public async Task FailedWorkflow_CanRollBackEarlierSave()
    {
        var application = new Application { Title = "Uncommitted" };
        await using (var database = store.CreateContext())
        {
            await using var transaction = await database.Database.BeginTransactionAsync();
            database.Applications.Add(application);
            await database.SaveChangesAsync();
            database.ApplicationMaintainers.Add(new ApplicationMaintainer
            {
                ApplicationId = application.Id,
                MaintainerId = Guid.NewGuid()
            });

            var exception = await Assert.ThrowsAsync<DbUpdateException>(
                () => database.SaveChangesAsync());
            Assert.Equal(787, Assert.IsType<SqliteException>(exception.InnerException).SqliteExtendedErrorCode);
            await transaction.RollbackAsync();
        }

        await using var verification = store.CreateContext();
        Assert.Empty(await verification.Applications.ToListAsync());
        Assert.Empty(await verification.ApplicationMaintainers.ToListAsync());
    }

    [Fact]
    public async Task ContendingWrite_FailsWithinBoundedWait()
    {
        await using var writer = store.CreateContext();
        await using var contender = store.CreateContext();
        await writer.Database.OpenConnectionAsync();
        await contender.Database.OpenConnectionAsync();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        writer.Applications.Add(new Application { Title = "Writer" });
        await writer.SaveChangesAsync();
        contender.Applications.Add(new Application { Title = "Contender" });
        var elapsed = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => contender.SaveChangesAsync());

        elapsed.Stop();
        Assert.Equal(5, Assert.IsType<SqliteException>(exception.InnerException).SqliteErrorCode);
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(12));
        await transaction.RollbackAsync();
        await using var verification = store.CreateContext();
        Assert.Empty(await verification.Applications.ToListAsync());
    }

    [Fact]
    public async Task Startup_RejectsUnmigratedDatabase()
    {
        using var unmigrated = new TestDatabase(migrate: false);
        await using var database = unmigrated.CreateContext();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MetadataDatabase.InitializeAsync(database));

        Assert.Contains("pending migrations", exception.Message);
        Assert.NotEmpty(await database.Database.GetPendingMigrationsAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(":memory:")]
    public void Configuration_RejectsMissingOrInMemoryPath(string? path)
    {
        var options = new DbContextOptionsBuilder<MetadataDbContext>();

        Assert.Throws<InvalidOperationException>(
            () => options.UseMetadataSqlite(path, Path.GetTempPath()));
    }

    private async Task<(Guid ApplicationId, Guid MaintainerId)> SeedAssociationAsync()
    {
        await using var database = store.CreateContext();
        var application = new Application { Title = "Portal" };
        var maintainer = new Maintainer { Name = "Alex", Email = "alex@example.com" };
        database.AddRange(application, maintainer);
        database.ApplicationMaintainers.Add(new ApplicationMaintainer
        {
            ApplicationId = application.Id,
            MaintainerId = maintainer.Id
        });
        await database.SaveChangesAsync();
        return (application.Id, maintainer.Id);
    }

    public void Dispose() => store.Dispose();
}

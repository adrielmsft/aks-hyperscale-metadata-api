using MetadataApi.Persistence.Entities;
using MetadataApi.Services;
using Microsoft.EntityFrameworkCore;

namespace MetadataApi.Tests;

public sealed class ApplicationMaintainerServiceTests
{
    [Fact]
    public async Task EnsureAndRemove_AreIdempotentAndLeaveBothResourcesIntact()
    {
        using var store = new TestDatabase();
        await using var database = store.CreateContext();
        var application = new Application { Title = "Microsoft Garage Locations" };
        var maintainer = new Maintainer { Name = "Garage Makers", Email = "makers@example.com" };
        database.AddRange(application, maintainer);
        await database.SaveChangesAsync();
        var service = new ApplicationMaintainerService(database);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(AssociationResult.Success,
                await service.EnsureAsync(application.Id, maintainer.Id, CancellationToken.None));
            Assert.Single(await database.ApplicationMaintainers.AsNoTracking().ToListAsync());
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(AssociationResult.Success,
                await service.RemoveAsync(application.Id, maintainer.Id, CancellationToken.None));
            Assert.Empty(await database.ApplicationMaintainers.AsNoTracking().ToListAsync());
        }

        Assert.Single(await database.Applications.ToListAsync());
        Assert.Single(await database.Maintainers.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentEnsure_WithIndependentConnections_CreatesExactlyOneLink()
    {
        using var store = new TestDatabase();
        var application = new Application { Title = "Microsoft Garage Locations" };
        var maintainer = new Maintainer { Name = "Garage Makers", Email = "makers@example.com" };
        await using (var database = store.CreateContext())
        {
            database.AddRange(application, maintainer);
            await database.SaveChangesAsync();
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await using var database = store.CreateContext();
            await start.Task;
            return await new ApplicationMaintainerService(database)
                .EnsureAsync(application.Id, maintainer.Id, CancellationToken.None);
        })).ToArray();
        start.SetResult();
        Assert.All(await Task.WhenAll(writes), result => Assert.Equal(AssociationResult.Success, result));

        await using var verification = store.CreateContext();
        var link = await verification.ApplicationMaintainers.SingleAsync();
        Assert.Equal(application.Id, link.ApplicationId);
        Assert.Equal(maintainer.Id, link.MaintainerId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingParent_IsReportedWithoutCreatingLinks(bool missingApplication)
    {
        using var store = new TestDatabase();
        await using var database = store.CreateContext();
        var application = new Application { Title = "Microsoft Garage" };
        var maintainer = new Maintainer { Name = "Garage Makers", Email = "makers@example.com" };
        if (missingApplication)
        {
            database.Maintainers.Add(maintainer);
        }
        else
        {
            database.Applications.Add(application);
        }

        await database.SaveChangesAsync();
        var service = new ApplicationMaintainerService(database);
        var expected = missingApplication ? AssociationResult.ApplicationNotFound : AssociationResult.MaintainerNotFound;
        Assert.Equal(expected, await service.EnsureAsync(application.Id, maintainer.Id, CancellationToken.None));
        Assert.Equal(expected, await service.RemoveAsync(application.Id, maintainer.Id, CancellationToken.None));
        Assert.Null(await service.ListMaintainersAsync(Guid.NewGuid(), 20, 0, CancellationToken.None));
        Assert.Null(await service.ListApplicationsAsync(Guid.NewGuid(), 20, 0, CancellationToken.None));
        Assert.Empty(await database.ApplicationMaintainers.ToListAsync());
    }

    [Theory]
    [InlineData("ensure")]
    [InlineData("remove")]
    [InlineData("maintainers")]
    [InlineData("applications")]
    public async Task Operations_ForwardCancellation(string operation)
    {
        using var store = new TestDatabase();
        await using var database = store.CreateContext();
        var service = new ApplicationMaintainerService(database);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var id = Guid.NewGuid();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (operation)
            {
                case "ensure":
                    await service.EnsureAsync(id, id, cancellation.Token);
                    break;
                case "remove":
                    await service.RemoveAsync(id, id, cancellation.Token);
                    break;
                case "maintainers":
                    await service.ListMaintainersAsync(id, 20, 0, cancellation.Token);
                    break;
                case "applications":
                    await service.ListApplicationsAsync(id, 20, 0, cancellation.Token);
                    break;
            }
        });
        Assert.Empty(await database.ApplicationMaintainers.ToListAsync());
    }
}

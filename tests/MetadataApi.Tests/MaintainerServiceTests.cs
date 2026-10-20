using MetadataApi.Contracts;
using MetadataApi.Services;

namespace MetadataApi.Tests;

public sealed class MaintainerServiceTests
{
    [Fact]
    public async Task ServiceLifecycle_UsesStableIdentityAndLastWriteWins()
    {
        using var storage = new TestDatabase();
        await using var database = storage.CreateContext();
        var service = new MaintainerService(database);
        var created = await service.CreateAsync(new CreateMaintainerRequest
        {
            Name = "Garage Team", Email = "garage@example.com"
        }, CancellationToken.None);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(created, await service.GetAsync(created.Id, CancellationToken.None));
        var replacement = new UpdateMaintainerRequest { Name = "Garage Makers", Email = "makers@example.com" };
        await service.UpdateAsync(created.Id, replacement, CancellationToken.None);
        replacement = replacement with { Name = "Garage Lab", Email = "lab@example.com" };
        var updated = await service.UpdateAsync(created.Id, replacement, CancellationToken.None);
        Assert.Equal(new MaintainerResponse(created.Id, "Garage Lab", "lab@example.com"), updated);
        Assert.Equal(updated, await service.GetAsync(created.Id, CancellationToken.None));
        Assert.True(await service.DeleteAsync(created.Id, CancellationToken.None));
        Assert.False(await service.DeleteAsync(created.Id, CancellationToken.None));
        Assert.Null(await service.GetAsync(created.Id, CancellationToken.None));
        Assert.Null(await service.UpdateAsync(created.Id, replacement, CancellationToken.None));
    }

    [Fact]
    public async Task CancelledOperations_PropagateCancellation()
    {
        using var storage = new TestDatabase();
        await using var database = storage.CreateContext();
        var service = new MaintainerService(database);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var id = Guid.NewGuid();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(
            new CreateMaintainerRequest { Name = "Garage", Email = "garage@example.com" }, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(id, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListAsync(20, 0, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UpdateAsync(id,
            new UpdateMaintainerRequest { Name = "Garage", Email = "garage@example.com" }, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DeleteAsync(id, cancellation.Token));
    }
}

using MetadataApi.Contracts;
using MetadataApi.Persistence.Entities;
using MetadataApi.Services;
using Microsoft.EntityFrameworkCore;

namespace MetadataApi.Tests;

public sealed class ApplicationServiceTests
{
    [Theory]
    [InlineData("list")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task Operations_ForwardCancellationWithoutChangingStorage(string operation)
    {
        using var store = new TestDatabase();
        await using var database = store.CreateContext();
        var application = new Application { Title = "Microsoft Garage", Version = "1.0.0" };
        database.Applications.Add(application);
        await database.SaveChangesAsync();
        var service = new ApplicationService(database);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (operation)
            {
                case "list":
                    await service.ListAsync(20, 0, cancellation.Token);
                    break;
                case "update":
                    await service.UpdateAsync(application.Id,
                        new UpdateApplicationRequest { Title = "Microsoft Garage Staff" }, cancellation.Token);
                    break;
                case "delete":
                    await service.DeleteAsync(application.Id, cancellation.Token);
                    break;
            }
        });

        var stored = await database.Applications.AsNoTracking().SingleAsync();
        Assert.Equal("Microsoft Garage", stored.Title);
        Assert.Equal("1.0.0", stored.Version);
    }
}

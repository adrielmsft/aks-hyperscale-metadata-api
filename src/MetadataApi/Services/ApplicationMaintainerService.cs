using MetadataApi.Contracts;
using MetadataApi.Persistence;
using MetadataApi.Validation;
using Microsoft.EntityFrameworkCore;

namespace MetadataApi.Services;

public sealed class ApplicationMaintainerService(MetadataDbContext database) : IApplicationMaintainerService
{
    public Task<AssociationResult> EnsureAsync(
        Guid applicationId, Guid maintainerId, CancellationToken cancellationToken)
        => ChangeAsync(applicationId, maintainerId, ensureExists: true, cancellationToken);

    public Task<AssociationResult> RemoveAsync(
        Guid applicationId, Guid maintainerId, CancellationToken cancellationToken)
        => ChangeAsync(applicationId, maintainerId, ensureExists: false, cancellationToken);

    private async Task<AssociationResult> ChangeAsync(
        Guid applicationId, Guid maintainerId, bool ensureExists, CancellationToken cancellationToken)
    {
        // SQLite's non-deferred transaction keeps parent deletion from racing these checks and the write.
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        if (!await database.Applications.AnyAsync(entity => entity.Id == applicationId, cancellationToken))
        {
            return AssociationResult.ApplicationNotFound;
        }

        if (!await database.Maintainers.AnyAsync(entity => entity.Id == maintainerId, cancellationToken))
        {
            return AssociationResult.MaintainerNotFound;
        }

        if (ensureExists)
        {
            // Ignore only a duplicate pair, never foreign-key or other constraint failures.
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ApplicationMaintainers" ("ApplicationId", "MaintainerId")
                VALUES ({applicationId}, {maintainerId})
                ON CONFLICT ("ApplicationId", "MaintainerId") DO NOTHING;
                """, cancellationToken);
        }
        else
        {
            await database.ApplicationMaintainers
                .Where(entity => entity.ApplicationId == applicationId && entity.MaintainerId == maintainerId)
                .ExecuteDeleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return AssociationResult.Success;
    }

    public async Task<PagedResponse<MaintainerResponse>?> ListMaintainersAsync(
        Guid applicationId, int limit, int offset, CancellationToken cancellationToken)
    {
        if (!await database.Applications.AnyAsync(entity => entity.Id == applicationId, cancellationToken))
        {
            return null;
        }

        var maintainers = await (from maintainer in database.Maintainers.AsNoTracking()
                                 join link in database.ApplicationMaintainers on maintainer.Id equals link.MaintainerId
                                 where link.ApplicationId == applicationId
                                 orderby maintainer.Id
                                 select new MaintainerResponse(maintainer.Id, maintainer.Name, maintainer.Email))
            .Skip(offset).Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = maintainers.Count > limit;
        if (hasMore)
        {
            maintainers.RemoveAt(limit);
        }

        return new(maintainers, limit, offset, PaginationValidation.GetNextOffset(limit, offset, hasMore));
    }

    public async Task<PagedResponse<ApplicationResponse>?> ListApplicationsAsync(
        Guid maintainerId, int limit, int offset, CancellationToken cancellationToken)
    {
        if (!await database.Maintainers.AnyAsync(entity => entity.Id == maintainerId, cancellationToken))
        {
            return null;
        }

        var applications = await (from application in database.Applications.AsNoTracking()
                                  join link in database.ApplicationMaintainers on application.Id equals link.ApplicationId
                                  where link.MaintainerId == maintainerId
                                  orderby application.Id
                                  select new ApplicationResponse(
                                      application.Id, application.Title, application.Version, application.Company,
                                      application.Website, application.Source, application.License, application.Description))
            .Skip(offset).Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = applications.Count > limit;
        if (hasMore)
        {
            applications.RemoveAt(limit);
        }

        return new(applications, limit, offset, PaginationValidation.GetNextOffset(limit, offset, hasMore));
    }
}

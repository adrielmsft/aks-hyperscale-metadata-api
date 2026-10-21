using MetadataApi.Contracts;
using MetadataApi.Persistence;
using MetadataApi.Persistence.Entities;
using MetadataApi.Validation;
using Microsoft.EntityFrameworkCore;

namespace MetadataApi.Services;

public sealed class ApplicationService(MetadataDbContext database) : IApplicationService
{
    public async Task<ApplicationResponse> CreateAsync(
        CreateApplicationRequest request,
        CancellationToken cancellationToken)
    {
        var title = request.Title;
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var application = new Application
        {
            Title = title,
            Version = request.Version,
            Company = request.Company,
            Website = request.Website,
            Source = request.Source,
            License = request.License,
            Description = request.Description
        };

        database.Applications.Add(application);
        await database.SaveChangesAsync(cancellationToken);
        return ToResponse(application);
    }

    public async Task<ApplicationResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var application = await database.Applications.AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.Id == id, cancellationToken);
        return application is null ? null : ToResponse(application);
    }

    private static ApplicationResponse ToResponse(Application application) => new(
        application.Id,
        application.Title,
        application.Version,
        application.Company,
        application.Website,
        application.Source,
        application.License,
        application.Description);

    public async Task<PagedResponse<ApplicationResponse>> ListAsync(
        int limit, int offset, CancellationToken cancellationToken)
    {
        var applications = await database.Applications.AsNoTracking()
            .OrderBy(entity => entity.Id)
            .Skip(offset)
            .Take(limit + 1)
            .Select(entity => new ApplicationResponse(
                entity.Id, entity.Title, entity.Version, entity.Company,
                entity.Website, entity.Source, entity.License, entity.Description))
            .ToListAsync(cancellationToken);
        var hasMore = applications.Count > limit;
        if (hasMore)
        {
            applications.RemoveAt(limit);
        }

        return new(applications, limit, offset, PaginationValidation.GetNextOffset(limit, offset, hasMore));
    }

    public async Task<ApplicationResponse?> UpdateAsync(
        Guid id, UpdateApplicationRequest request, CancellationToken cancellationToken)
    {
        var title = request.Title;
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var updated = await database.Applications.Where(entity => entity.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(entity => entity.Title, title)
                .SetProperty(entity => entity.Version, request.Version)
                .SetProperty(entity => entity.Company, request.Company)
                .SetProperty(entity => entity.Website, request.Website)
                .SetProperty(entity => entity.Source, request.Source)
                .SetProperty(entity => entity.License, request.License)
                .SetProperty(entity => entity.Description, request.Description), cancellationToken);

        return updated == 0 ? null : new ApplicationResponse(
            id, title, request.Version, request.Company,
            request.Website, request.Source, request.License, request.Description);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
        => await database.Applications.Where(entity => entity.Id == id)
            .ExecuteDeleteAsync(cancellationToken) > 0;
}

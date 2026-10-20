using MetadataApi.Contracts;
using MetadataApi.Persistence;
using MetadataApi.Persistence.Entities;
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
}

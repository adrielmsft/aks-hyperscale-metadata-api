using MetadataApi.Contracts;

namespace MetadataApi.Services;

public interface IApplicationService
{
    Task<ApplicationResponse> CreateAsync(CreateApplicationRequest request, CancellationToken cancellationToken);
    Task<ApplicationResponse?> GetAsync(Guid id, CancellationToken cancellationToken);
}

using MetadataApi.Contracts;

namespace MetadataApi.Services;

public interface IApplicationService
{
    Task<ApplicationResponse> CreateAsync(CreateApplicationRequest request, CancellationToken cancellationToken);
    Task<ApplicationResponse?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<PagedResponse<ApplicationResponse>> ListAsync(int limit, int offset, CancellationToken cancellationToken);
    Task<ApplicationResponse?> UpdateAsync(Guid id, UpdateApplicationRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

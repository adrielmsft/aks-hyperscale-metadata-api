using MetadataApi.Contracts;

namespace MetadataApi.Services;

public interface IMaintainerService
{
    Task<MaintainerResponse> CreateAsync(CreateMaintainerRequest request, CancellationToken cancellationToken);
    Task<MaintainerResponse?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<PagedResponse<MaintainerResponse>> ListAsync(int limit, int offset, CancellationToken cancellationToken);
    Task<MaintainerResponse?> UpdateAsync(Guid id, UpdateMaintainerRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

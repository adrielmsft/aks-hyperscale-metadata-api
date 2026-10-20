using MetadataApi.Contracts;

namespace MetadataApi.Services;

public enum AssociationResult
{
    Success,
    ApplicationNotFound,
    MaintainerNotFound
}

public interface IApplicationMaintainerService
{
    Task<AssociationResult> EnsureAsync(Guid applicationId, Guid maintainerId, CancellationToken cancellationToken);
    Task<AssociationResult> RemoveAsync(Guid applicationId, Guid maintainerId, CancellationToken cancellationToken);
    Task<PagedResponse<MaintainerResponse>?> ListMaintainersAsync(
        Guid applicationId, int limit, int offset, CancellationToken cancellationToken);
    Task<PagedResponse<ApplicationResponse>?> ListApplicationsAsync(
        Guid maintainerId, int limit, int offset, CancellationToken cancellationToken);
}

using MetadataApi.Contracts;
using MetadataApi.Persistence;
using MetadataApi.Persistence.Entities;
using MetadataApi.Validation;
using Microsoft.EntityFrameworkCore;

namespace MetadataApi.Services;

public sealed class MaintainerService(MetadataDbContext database) : IMaintainerService
{
    public async Task<MaintainerResponse> CreateAsync(
        CreateMaintainerRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Email);
        var maintainer = new Maintainer { Name = request.Name, Email = request.Email };
        database.Maintainers.Add(maintainer);
        await database.SaveChangesAsync(cancellationToken);
        return new(maintainer.Id, maintainer.Name, maintainer.Email);
    }

    public Task<MaintainerResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
        => database.Maintainers.AsNoTracking()
            .Where(entity => entity.Id == id)
            .Select(entity => new MaintainerResponse(entity.Id, entity.Name, entity.Email))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<PagedResponse<MaintainerResponse>> ListAsync(
        int limit, int offset, CancellationToken cancellationToken)
    {
        var items = await database.Maintainers.AsNoTracking()
            .OrderBy(entity => entity.Id)
            .Skip(offset).Take(limit + 1)
            .Select(entity => new MaintainerResponse(entity.Id, entity.Name, entity.Email))
            .ToListAsync(cancellationToken);
        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(limit);
        }

        return new(items, limit, offset, PaginationValidation.GetNextOffset(limit, offset, hasMore));
    }

    public async Task<MaintainerResponse?> UpdateAsync(
        Guid id, UpdateMaintainerRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Email);
        var affected = await database.Maintainers.Where(entity => entity.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(entity => entity.Name, request.Name)
                .SetProperty(entity => entity.Email, request.Email), cancellationToken);
        return affected == 0 ? null : new(id, request.Name, request.Email);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
        => await database.Maintainers.Where(entity => entity.Id == id)
            .ExecuteDeleteAsync(cancellationToken) > 0;
}

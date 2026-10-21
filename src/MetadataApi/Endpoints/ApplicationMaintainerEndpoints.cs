using MetadataApi.Contracts;
using MetadataApi.Services;
using MetadataApi.Validation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MetadataApi.Endpoints;

public static class ApplicationMaintainerEndpoints
{
    public static void MapApplicationMaintainers(this WebApplication app)
    {
        var associations = app.MapGroup("/api/v1").WithTags("Associations");

        associations.MapPut("/applications/{applicationId}/maintainers/{maintainerId}", EnsureAsync)
            .WithName("EnsureApplicationMaintainer")
            .WithSummary("Ensure an application-maintainer association exists")
            .WithDescription("No request body. Returns 204 even if the link already exists, including concurrent duplicate requests. Both resources must exist.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        associations.MapDelete("/applications/{applicationId}/maintainers/{maintainerId}", RemoveAsync)
            .WithName("RemoveApplicationMaintainer")
            .WithSummary("Ensure an application-maintainer association is absent")
            .WithDescription("No request body. Returns 204 even if the link is absent. Both resources must exist; neither is deleted.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        associations.MapGet("/applications/{applicationId}/maintainers", ListMaintainersAsync)
            .WithName("ListApplicationMaintainers")
            .WithSummary("List an application's maintainers")
            .WithDescription("Returns maintainer resources ordered by ID ascending. Limit defaults to 20 (1–100); offset defaults to 0 (0–10000). Invalid values return 400; missing parents return 404. NextOffset is null at the end or traversal cap; concurrent writes may shift pages.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        associations.MapGet("/maintainers/{maintainerId}/applications", ListApplicationsAsync)
            .WithName("ListMaintainerApplications")
            .WithSummary("List a maintainer's applications")
            .WithDescription("Returns application resources ordered by ID ascending. Limit defaults to 20 (1–100); offset defaults to 0 (0–10000). Invalid values return 400; missing parents return 404. NextOffset is null at the end or traversal cap; concurrent writes may shift pages.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> EnsureAsync(
        string applicationId, string maintainerId, IApplicationMaintainerService service, CancellationToken cancellationToken)
    {
        var errors = ValidateIds(applicationId, maintainerId, out var application, out var maintainer);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await service.EnsureAsync(application, maintainer, cancellationToken);
        if (result != AssociationResult.Success)
        {
            return MissingResource(result);
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> RemoveAsync(
        string applicationId, string maintainerId, IApplicationMaintainerService service, CancellationToken cancellationToken)
    {
        var errors = ValidateIds(applicationId, maintainerId, out var application, out var maintainer);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await service.RemoveAsync(application, maintainer, cancellationToken);
        if (result != AssociationResult.Success)
        {
            return MissingResource(result);
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<PagedResponse<MaintainerResponse>>, ValidationProblem, ProblemHttpResult>> ListMaintainersAsync(
        string applicationId, IApplicationMaintainerService service, CancellationToken cancellationToken,
        int limit = PaginationValidation.DefaultLimit, int offset = 0)
    {
        var errors = PaginationValidation.Validate(limit, offset);
        var id = ValidateId(applicationId, "applicationId", "Application", errors);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var page = await service.ListMaintainersAsync(id, limit, offset, cancellationToken);
        if (page is null)
        {
            return MissingResource(AssociationResult.ApplicationNotFound);
        }

        return TypedResults.Ok(page);
    }

    private static async Task<Results<Ok<PagedResponse<ApplicationResponse>>, ValidationProblem, ProblemHttpResult>> ListApplicationsAsync(
        string maintainerId, IApplicationMaintainerService service, CancellationToken cancellationToken,
        int limit = PaginationValidation.DefaultLimit, int offset = 0)
    {
        var errors = PaginationValidation.Validate(limit, offset);
        var id = ValidateId(maintainerId, "maintainerId", "Maintainer", errors);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var page = await service.ListApplicationsAsync(id, limit, offset, cancellationToken);
        if (page is null)
        {
            return MissingResource(AssociationResult.MaintainerNotFound);
        }

        return TypedResults.Ok(page);
    }

    private static Dictionary<string, string[]> ValidateIds(
        string applicationId, string maintainerId, out Guid application, out Guid maintainer)
    {
        var errors = new Dictionary<string, string[]>();
        application = ValidateId(applicationId, "applicationId", "Application", errors);
        maintainer = ValidateId(maintainerId, "maintainerId", "Maintainer", errors);
        return errors;
    }

    private static Guid ValidateId(string value, string field, string resource, Dictionary<string, string[]> errors)
    {
        if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
        {
            errors[field] = [$"{resource} ID must be a non-empty GUID."];
        }

        return id;
    }

    private static ProblemHttpResult MissingResource(AssociationResult result)
    {
        var resource = result == AssociationResult.ApplicationNotFound ? "Application" : "Maintainer";
        return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound,
            title: $"{resource} not found", detail: $"No {resource.ToLowerInvariant()} exists with the specified ID.");
    }
}

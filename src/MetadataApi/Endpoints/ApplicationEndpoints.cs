using MetadataApi.Contracts;
using MetadataApi.Services;
using MetadataApi.Validation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MetadataApi.Endpoints;

public static class ApplicationEndpoints
{
    public static void MapApplications(this WebApplication app)
    {
        var applications = app.MapGroup("/api/v1/applications").WithTags("Applications");

        applications.MapPost("", CreateAsync)
            .WithName("CreateApplication")
            .WithSummary("Create an application")
            .WithDescription("Creates metadata with a server-generated ID and no maintainer associations.")
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        applications.MapGet("/{applicationId}", GetAsync)
            .WithName("GetApplication")
            .WithSummary("Get an application by ID")
            .WithDescription("Returns the stored metadata. Invalid or empty GUIDs return 400; missing applications return 404.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        applications.MapGet("", ListAsync)
            .WithName("ListApplications")
            .WithSummary("List applications")
            .WithDescription("Returns applications ordered by ID ascending. Limit defaults to 20 (1–100); offset defaults to 0 (0–10000). Invalid values return 400. NextOffset is null at the end or traversal cap; concurrent writes may shift pages.")
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        applications.MapPut("/{applicationId}", UpdateAsync)
            .WithName("UpdateApplication")
            .WithSummary("Replace application metadata")
            .WithDescription("Requires title and clears omitted optional fields. ID and maintainer associations remain unchanged. Does not create missing resources. Updates use last-write-wins.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        applications.MapDelete("/{applicationId}", DeleteAsync)
            .WithName("DeleteApplication")
            .WithSummary("Delete an application")
            .WithDescription("Deletes the application and its association links only, never maintainers. Missing resources, including repeat deletions, return 404.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<Results<Created<ApplicationResponse>, ValidationProblem>> CreateAsync(
        CreateApplicationRequest request,
        IApplicationService service,
        CancellationToken cancellationToken)
    {
        var normalized = ApplicationValidation.Normalize(request);
        var errors = ApplicationValidation.Validate(normalized);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var application = await service.CreateAsync(normalized, cancellationToken);
        return TypedResults.Created($"/api/v1/applications/{application.Id}", application);
    }

    private static async Task<Results<Ok<ApplicationResponse>, ValidationProblem, ProblemHttpResult>> GetAsync(
        string applicationId,
        IApplicationService service,
        CancellationToken cancellationToken)
    {
        if (!TryGetId(applicationId, out var id))
        {
            return InvalidId();
        }

        var application = await service.GetAsync(id, cancellationToken);
        if (application is null)
        {
            return MissingApplication();
        }

        return TypedResults.Ok(application);
    }

    private static async Task<Results<Ok<PagedResponse<ApplicationResponse>>, ValidationProblem>> ListAsync(
        IApplicationService service,
        CancellationToken cancellationToken,
        int limit = PaginationValidation.DefaultLimit,
        int offset = 0)
    {
        var errors = PaginationValidation.Validate(limit, offset);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        return TypedResults.Ok(await service.ListAsync(limit, offset, cancellationToken));
    }

    private static async Task<Results<Ok<ApplicationResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        string applicationId,
        UpdateApplicationRequest request,
        IApplicationService service,
        CancellationToken cancellationToken)
    {
        if (!TryGetId(applicationId, out var id))
        {
            return InvalidId();
        }

        var normalized = ApplicationValidation.Normalize(request);
        var errors = ApplicationValidation.Validate(normalized);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var application = await service.UpdateAsync(id, normalized, cancellationToken);
        if (application is null)
        {
            return MissingApplication();
        }

        return TypedResults.Ok(application);
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> DeleteAsync(
        string applicationId,
        IApplicationService service,
        CancellationToken cancellationToken)
    {
        if (!TryGetId(applicationId, out var id))
        {
            return InvalidId();
        }

        if (!await service.DeleteAsync(id, cancellationToken))
        {
            return MissingApplication();
        }

        return TypedResults.NoContent();
    }

    private static bool TryGetId(string value, out Guid id) => Guid.TryParse(value, out id) && id != Guid.Empty;

    private static ValidationProblem InvalidId() => TypedResults.ValidationProblem(
        new Dictionary<string, string[]>
        {
            ["applicationId"] = ["Application ID must be a non-empty GUID."]
        });

    private static ProblemHttpResult MissingApplication() => TypedResults.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Application not found",
        detail: "No application exists with the specified ID.");
}

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
        if (!Guid.TryParse(applicationId, out var id) || id == Guid.Empty)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["applicationId"] = ["Application ID must be a non-empty GUID."]
            });
        }

        var application = await service.GetAsync(id, cancellationToken);
        if (application is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Application not found",
                detail: "No application exists with the specified ID.");
        }

        return TypedResults.Ok(application);
    }
}

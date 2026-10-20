using System.Globalization;
using MetadataApi.Contracts;
using MetadataApi.Services;
using MetadataApi.Validation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MetadataApi.Endpoints;

public static class MaintainerEndpoints
{
    public static void MapMaintainers(this WebApplication app)
    {
        var maintainers = app.MapGroup("/api/v1/maintainers").WithTags("Maintainers");

        maintainers.MapPost("", CreateAsync)
            .WithName("CreateMaintainer")
            .WithSummary("Create a maintainer")
            .WithDescription("Creates contact metadata with a server-generated ID. Duplicate names and emails are allowed.")
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        maintainers.MapGet("", ListAsync)
            .WithName("ListMaintainers")
            .WithSummary("List a bounded page of maintainers")
            .WithDescription("Orders by ID ascending. Limit defaults to 20 (1–100); offset defaults to 0 (0–10000).")
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        maintainers.MapGet("/{maintainerId}", GetAsync)
            .WithName("GetMaintainer")
            .WithSummary("Get a maintainer by ID")
            .WithDescription("Invalid or empty GUIDs return 400; missing maintainers return 404.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        maintainers.MapPut("/{maintainerId}", UpdateAsync)
            .WithName("UpdateMaintainer")
            .WithSummary("Replace a maintainer's name and email")
            .WithDescription("Both fields are required. The ID and application associations remain unchanged. Last write wins.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        maintainers.MapDelete("/{maintainerId}", DeleteAsync)
            .WithName("DeleteMaintainer")
            .WithSummary("Delete a maintainer")
            .WithDescription("Deletes the maintainer and its association rows, never the linked applications. Missing maintainers return 404.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<Results<Created<MaintainerResponse>, ValidationProblem>> CreateAsync(
        CreateMaintainerRequest request, IMaintainerService service, CancellationToken cancellationToken)
    {
        var normalized = MaintainerValidation.Normalize(request);
        var errors = MaintainerValidation.Validate(normalized.Name, normalized.Email);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var maintainer = await service.CreateAsync(normalized, cancellationToken);
        return TypedResults.Created($"/api/v1/maintainers/{maintainer.Id}", maintainer);
    }

    private static async Task<Results<Ok<PagedResponse<MaintainerResponse>>, ValidationProblem>> ListAsync(
        IMaintainerService service, CancellationToken cancellationToken, string? limit = null, string? offset = null)
    {
        var parsedLimit = PaginationValidation.DefaultLimit;
        var parsedOffset = 0;
        var validLimit = limit is null || int.TryParse(limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedLimit);
        var validOffset = offset is null || int.TryParse(offset, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedOffset);
        var errors = PaginationValidation.Validate(parsedLimit, parsedOffset);
        if (!validLimit)
        {
            errors["limit"] = ["Limit must be an integer between 1 and 100."];
        }

        if (!validOffset)
        {
            errors["offset"] = ["Offset must be an integer between 0 and 10000."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        return TypedResults.Ok(await service.ListAsync(parsedLimit, parsedOffset, cancellationToken));
    }

    private static async Task<Results<Ok<MaintainerResponse>, ValidationProblem, ProblemHttpResult>> GetAsync(
        string maintainerId, IMaintainerService service, CancellationToken cancellationToken)
    {
        if (!TryParseId(maintainerId, out var id))
        {
            return InvalidId();
        }

        var maintainer = await service.GetAsync(id, cancellationToken);
        return maintainer is null ? Missing() : TypedResults.Ok(maintainer);
    }

    private static async Task<Results<Ok<MaintainerResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        string maintainerId, UpdateMaintainerRequest request, IMaintainerService service, CancellationToken cancellationToken)
    {
        var normalized = MaintainerValidation.Normalize(request);
        var errors = MaintainerValidation.Validate(normalized.Name, normalized.Email);
        if (!TryParseId(maintainerId, out var id))
        {
            errors["maintainerId"] = ["Maintainer ID must be a non-empty GUID."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var maintainer = await service.UpdateAsync(id, normalized, cancellationToken);
        return maintainer is null ? Missing() : TypedResults.Ok(maintainer);
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> DeleteAsync(
        string maintainerId, IMaintainerService service, CancellationToken cancellationToken)
    {
        if (!TryParseId(maintainerId, out var id))
        {
            return InvalidId();
        }

        return await service.DeleteAsync(id, cancellationToken) ? TypedResults.NoContent() : Missing();
    }

    private static bool TryParseId(string value, out Guid id)
        => Guid.TryParse(value, out id) && id != Guid.Empty;

    private static ValidationProblem InvalidId() => TypedResults.ValidationProblem(new Dictionary<string, string[]>
    {
        ["maintainerId"] = ["Maintainer ID must be a non-empty GUID."]
    });

    private static ProblemHttpResult Missing() => TypedResults.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Maintainer not found",
        detail: "No maintainer exists with the specified ID.");
}

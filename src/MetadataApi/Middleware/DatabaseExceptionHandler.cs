using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MetadataApi.Middleware;

internal sealed class DatabaseExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<DatabaseExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var databaseException = exception is DbUpdateException ? exception.InnerException : exception;
        if (databaseException is not SqliteException { SqliteErrorCode: 5 or 6 })
        {
            return false;
        }

        logger.LogWarning(exception, "Database lock contention exceeded the configured wait.");
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Service Unavailable",
                Detail = "The database is busy. Try again later."
            }
        });
        return true;
    }
}

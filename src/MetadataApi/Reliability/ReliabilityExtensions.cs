using System.Diagnostics;
using System.Globalization;
using System.Threading.RateLimiting;
using MetadataApi.Middleware;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace MetadataApi.Reliability;

public static class ReliabilityExtensions
{
    public static void AddReliability(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<RateLimitingOptions>()
            .BindConfiguration("RateLimiting").ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddOptions<RequestLimitOptions>()
            .BindConfiguration("Limits").ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["traceId"] =
                Activity.Current?.Id ?? context.HttpContext.TraceIdentifier);
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddHealthChecks().AddCheck<DatabaseReadinessCheck>("database", tags: ["ready"]);
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<RateLimitingOptions>>((options, configured) =>
            {
                var settings = configured.Value;
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                    RateLimitPartition.GetFixedWindowLimiter("global", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.PermitLimit,
                        Window = TimeSpan.FromSeconds(settings.WindowSeconds),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
                options.OnRejected = async (context, cancellationToken) =>
                {
                    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    {
                        context.HttpContext.Response.Headers.RetryAfter =
                            Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                    }

                    context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    await context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>()
                        .WriteAsync(new ProblemDetailsContext { HttpContext = context.HttpContext });
                };
            });
    }

    public static void UseReliability(this WebApplication app)
    {
        app.UseRateLimiter();
        app.UseMiddleware<RequestBodyLimitMiddleware>();
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
            .DisableRateLimiting().AllowAnonymous().ExcludeFromDescription();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready")
        }).DisableRateLimiting().AllowAnonymous().ExcludeFromDescription();
    }
}

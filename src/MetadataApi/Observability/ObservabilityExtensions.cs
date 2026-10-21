using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MetadataApi.Observability;

public static class ObservabilityExtensions
{
    public static WebApplicationBuilder AddApiObservability(this WebApplicationBuilder builder)
    {
        var resource = ResourceBuilder.CreateDefault().AddService(
            "metadata-api", serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString());
        var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var export = !string.IsNullOrWhiteSpace(endpoint);

        builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
        builder.Services.Configure<ConsoleLoggerOptions>(options => options.FormatterName = "json");
        builder.Logging.Configure(options => options.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);
        // Framework request diagnostics include raw URLs; emit a safe route-template completion log instead.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("metadata-api",
                serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString()))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                    .AddHttpClientInstrumentation()
                    .AddProcessor(new SafeHttpTagsProcessor());
                if (export)
                {
                    tracing.AddOtlpExporter(options => ConfigureExporter(options, endpoint!, "traces"));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel",
                    "Microsoft.AspNetCore.RateLimiting");
                if (export)
                {
                    metrics.AddOtlpExporter(options => ConfigureExporter(options, endpoint!, "metrics"));
                }
            });

        builder.Logging.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(resource);
            options.IncludeScopes = true;
            options.IncludeFormattedMessage = true;
            if (export)
            {
                options.AddOtlpExporter((exporter, processor) =>
                {
                    ConfigureExporter(exporter, endpoint!, "logs");
                    processor.BatchExportProcessorOptions.MaxQueueSize = 512;
                    processor.BatchExportProcessorOptions.MaxExportBatchSize = 128;
                    processor.BatchExportProcessorOptions.ScheduledDelayMilliseconds = 1000;
                    processor.BatchExportProcessorOptions.ExporterTimeoutMilliseconds = 1000;
                });
            }
        });
        return builder;
    }

    public static WebApplication UseApiObservability(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            await next(context);
            if (!context.Request.Path.StartsWithSegments("/health"))
            {
                var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
                app.Logger.LogInformation("HTTP {Method} {Route} completed with {StatusCode}",
                    context.Request.Method, route, context.Response.StatusCode);
            }
        });
        return app;
    }

    private static void ConfigureExporter(OtlpExporterOptions options, string endpoint, string signal)
    {
        options.Endpoint = new Uri(options.Protocol == OtlpExportProtocol.HttpProtobuf
            ? $"{endpoint.TrimEnd('/')}/v1/{signal}"
            : endpoint);
        options.TimeoutMilliseconds = 1000;
        options.BatchExportProcessorOptions = new BatchExportProcessorOptions<Activity>
        {
            MaxQueueSize = 512,
            MaxExportBatchSize = 128,
            ScheduledDelayMilliseconds = 1000,
            ExporterTimeoutMilliseconds = 1000
        };
    }

    // Default HTTP instrumentation includes raw URL/path/query tags. Keep only bounded, non-PII dimensions.
    private sealed class SafeHttpTagsProcessor : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity)
        {
            foreach (var tag in activity.TagObjects.ToArray())
            {
                if (tag.Key is not ("http.request.method" or "http.response.status_code" or "http.route"
                    or "network.protocol.version" or "error.type" or "server.port"))
                {
                    activity.SetTag(tag.Key, null);
                }
            }
        }
    }
}

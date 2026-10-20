using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MetadataApi.Tests;

[CollectionDefinition("Telemetry", DisableParallelization = true)]
public sealed class TelemetryCollection;

[Collection("Telemetry")]
public sealed class ObservabilityTests
{
    [Fact]
    public async Task Request_ExportsSafeRouteSpanMetricsAndCorrelatedLog()
    {
        var spans = new List<Activity>();
        var metrics = new List<Metric>();
        var logs = new List<LogRecord>();
        using var baseline = new MetadataApiFactory();
        using var factory = baseline.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddOpenTelemetry().WithTracing(tracing => tracing.AddInMemoryExporter(spans))
                .WithMetrics(meter => meter.AddInMemoryExporter(metrics));
            services.AddLogging(logging => logging.AddOpenTelemetry(options => options.AddInMemoryExporter(logs)));
        }));
        using var client = factory.CreateClient();
        var id = Guid.NewGuid().ToString();
        const string email = "private-maintainer@example.test";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/applications/{id}?email={email}");
        var authorization = client.DefaultRequestHeaders.Authorization?.ToString() ?? "Bearer private-api-key";
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var problemTrace = problem.RootElement.GetProperty("traceId").GetString();
        factory.Services.GetRequiredService<TracerProvider>().ForceFlush();
        factory.Services.GetRequiredService<MeterProvider>().ForceFlush();
        const string route = "/api/v1/applications/{applicationId}";
        var span = Assert.Single(spans, span => span.Kind == ActivityKind.Server
            && span.GetTagItem("http.route") as string == route);
        Assert.Contains(span.TraceId.ToHexString(), problemTrace!);
        var tags = string.Join("|", span.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain(id, tags);
        Assert.DoesNotContain(email, tags);
        Assert.DoesNotContain(authorization.Split(' ', 2)[1], tags);
        Assert.DoesNotContain("Authorization", tags, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url.", tags);
        Assert.DoesNotContain(id, span.DisplayName);
        var resource = factory.Services.GetRequiredService<TracerProvider>().GetResource();
        Assert.Contains(resource.Attributes, attribute => attribute.Key == "service.name"
            && attribute.Value as string == "metadata-api");
        Assert.Contains(resource.Attributes, attribute => attribute.Key == "service.version"
            && !string.IsNullOrWhiteSpace(attribute.Value as string));
        var messages = string.Join("|", logs.Select(log => log.FormattedMessage));
        Assert.DoesNotContain(email, messages);
        Assert.DoesNotContain(authorization.Split(' ', 2)[1], messages);
        Assert.Contains(logs, log => log.TraceId == span.TraceId && log.SpanId == span.SpanId
            && log.FormattedMessage == $"HTTP GET {route} completed with 404");
        var duration = Assert.Single(metrics, metric => metric.Name == "http.server.request.duration");
        var found = false;
        foreach (ref readonly var point in duration.GetMetricPoints())
        {
            foreach (var tag in point.Tags)
            {
                Assert.DoesNotContain(id, tag.Value?.ToString() ?? "");
                Assert.DoesNotContain(email, tag.Value?.ToString() ?? "");
                found |= tag.Key == "http.route" && tag.Value?.ToString() == route;
            }
        }
        Assert.True(found, "Duration histogram (including request count) must use the route template.");
        Assert.Contains(metrics, metric => metric.Name == "http.server.active_requests");
    }

    [Fact]
    public async Task HealthRequest_IsNotTraced()
    {
        var spans = new List<Activity>();
        using var baseline = new MetadataApiFactory();
        using var factory = baseline.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddOpenTelemetry().WithTracing(tracing => tracing.AddInMemoryExporter(spans))));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/live");
        response.EnsureSuccessStatusCode();
        factory.Services.GetRequiredService<TracerProvider>().ForceFlush();
        Assert.DoesNotContain(spans, span => span.Kind == ActivityKind.Server);
    }

    [Fact]
    public async Task UnreachableExporter_DoesNotFailRequests()
    {
        using var baseline = new MetadataApiFactory();
        using var factory = baseline.WithWebHostBuilder(builder =>
            builder.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:1"));
        using var client = factory.CreateClient();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.GetAsync("/api/v1/applications");
            response.EnsureSuccessStatusCode();
            // Exercise another request after the background batch export has attempted delivery.
            await Task.Delay(1200);
        }
        Assert.True(factory.Services.GetRequiredService<TracerProvider>().ForceFlush(3000));
    }
}

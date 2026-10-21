using System.Net;
using System.Text.Json;

namespace MetadataApi.E2E.Tests;

public sealed class CollectorSmokeFactAttribute : FactAttribute
{
    public CollectorSmokeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_SMOKE_EXPORT_FILE")))
        {
            Skip = "Set OTEL_SMOKE_EXPORT_FILE and OTEL_SMOKE_ENDPOINT to run against a collector.";
        }
    }
}

public sealed class ObservabilityE2ETests
{
    [CollectorSmokeFact]
    public async Task Collector_ReceivesRequestTraceAndDurationMetric()
    {
        var exportFile = Environment.GetEnvironmentVariable("OTEL_SMOKE_EXPORT_FILE")!;
        var endpoint = Environment.GetEnvironmentVariable("OTEL_SMOKE_ENDPOINT")
            ?? "http://127.0.0.1:4317";
        var fixture = new ApiServerFixture
        {
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint,
                ["OTEL_METRIC_EXPORT_INTERVAL"] = "1000"
            }
        };
        try
        {
            await fixture.InitializeAsync();
            using var response = await fixture.Client.GetAsync($"/api/v1/applications/{Guid.NewGuid()}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var traceId = problem.RootElement.GetProperty("traceId").GetString()!;
            // ASP.NET's Problem Details uses the W3C Activity.Id (00-<trace>-<span>-flags).
            traceId = traceId.Contains('-') ? traceId.Split('-')[1] : traceId;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    using var file = new FileStream(exportFile, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(file);
                    var exported = await reader.ReadToEndAsync();
                    if (exported.Contains(traceId, StringComparison.OrdinalIgnoreCase)
                        && exported.Contains("http.server.request.duration", StringComparison.Ordinal)
                        && exported.Contains("/api/v1/applications/{applicationId}", StringComparison.Ordinal))
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                    // The collector may not have opened/flushed the output file yet.
                }
                await Task.Delay(250);
            }
            Assert.Fail($"Collector did not export trace {traceId} and the route duration metric within 30 seconds.");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}

using System.Diagnostics;
using System.Globalization;

namespace MetadataApi.E2E.Tests;

internal sealed class HttpTraceHandler(Action<string> writeLine) : DelegatingHandler(new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var status = "ERROR";
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            return response;
        }
        finally
        {
            // Headers/bodies (including credentials and maintainer emails) are never inspected.
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            writeLine(FormattableString.Invariant(
                $"{request.Method} {request.RequestUri!.PathAndQuery} -> {status} ({elapsed:F1} ms)"));
        }
    }
}

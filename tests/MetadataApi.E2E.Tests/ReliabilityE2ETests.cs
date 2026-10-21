using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MetadataApi.E2E.Tests;

[CollectionDefinition("Reliability", DisableParallelization = true)]
public sealed class ReliabilityCollection : ICollectionFixture<ReliabilityServerFixture>;

public sealed class ReliabilityServerFixture : IAsyncLifetime
{
    private readonly ApiServerFixture server = new(new Dictionary<string, string>
    {
        ["RateLimiting__PermitLimit"] = "3",
        ["RateLimiting__WindowSeconds"] = "3600",
        ["Limits__MaxRequestBodyBytes"] = "128"
    });

    public HttpClient Client => server.Client;
    public Task InitializeAsync() => server.InitializeAsync();
    public Task DisposeAsync() => server.DisposeAsync();
}

[Collection("Reliability")]
public sealed class ReliabilityE2ETests(ReliabilityServerFixture server)
{
    [Fact]
    public async Task Kestrel_EnforcesBodyAndRateLimits_WithProblemDetails()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"title\":\"" + new string('x', 256) + "\"}");
        foreach (var chunked in new[] { false, true })
        {
            using HttpContent content = chunked ? new UnknownLengthContent(bytes)
                : new StringContent(Encoding.UTF8.GetString(bytes), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/applications")
            {
                Content = content, Version = HttpVersion.Version11
            };
            request.Headers.TransferEncodingChunked = chunked;
            using var oversized = await server.Client.SendAsync(request);
            await AssertProblemAsync(oversized, HttpStatusCode.RequestEntityTooLarge);
        }

        using var allowed = await server.Client.GetAsync("/api/v1/applications");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var rejected = await server.Client.GetAsync("/api/v1/maintainers");
        await AssertProblemAsync(rejected, HttpStatusCode.TooManyRequests);
        Assert.True(rejected.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        foreach (var path in new[] { "/health/live", "/health/ready" })
        {
            using var health = await server.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)status, document.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("traceId").GetString()));
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] bytes;
        public UnknownLengthContent(byte[] bytes)
        {
            this.bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(bytes).AsTask();
    }
}

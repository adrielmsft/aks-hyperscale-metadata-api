using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MetadataApi.Persistence;
using MetadataApi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MetadataApi.Tests;

public sealed class ReliabilityApiTests
{
    [Fact]
    public async Task GlobalLimit_Returns429WithRetryAfter_AndExemptsHealth()
    {
        using var factory = new MetadataApiFactory(new Dictionary<string, string>
        {
            ["RateLimiting:PermitLimit"] = "1", ["RateLimiting:WindowSeconds"] = "3600"
        });
        using var client = factory.CreateClient();
        using var first = await client.GetAsync("/api/v1/applications");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var rejected = await client.GetAsync("/api/v1/maintainers");
        await AssertProblemAsync(rejected, HttpStatusCode.TooManyRequests);
        Assert.True(rejected.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        foreach (var path in new[] { "/health/live", "/health/ready" })
        {
            using var health = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedBody_Returns413_BeforeJsonBinding(bool chunked)
    {
        using var factory = new MetadataApiFactory(new Dictionary<string, string>
        {
            ["Limits:MaxRequestBodyBytes"] = "128"
        });
        using var client = factory.CreateClient();
        var bytes = Encoding.UTF8.GetBytes("{\"title\":\"" + new string('x', 256) + "\"}");
        using HttpContent content = chunked ? new UnknownLengthContent(bytes)
            : new StringContent(Encoding.UTF8.GetString(bytes), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/applications") { Content = content };
        request.Headers.TransferEncodingChunked = chunked;
        using var response = await client.SendAsync(request);
        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<MetadataDbContext>().Applications.AnyAsync());
    }

    [Fact]
    public async Task BodyAtLimit_IsAccepted()
    {
        const string json = "{\"title\":\"Portal\"}";
        using var factory = new MetadataApiFactory(new Dictionary<string, string>
        {
            ["Limits:MaxRequestBodyBytes"] = Encoding.UTF8.GetByteCount(json).ToString()
        });
        using var client = factory.CreateClient();
        using var content = new UnknownLengthContent(Encoding.UTF8.GetBytes(json));
        using var response = await client.PostAsync("/api/v1/applications", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_QueriesDatabase_WhileLivenessDoesNot()
    {
        using var factory = new MetadataApiFactory();
        using var client = factory.CreateClient();
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
            await database.Database.ExecuteSqlRawAsync("DROP TABLE Applications");
        }

        using var failed = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("Unhealthy", await failed.Content.ReadAsStringAsync());
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task UnsupportedMethod_Returns405WithAllow()
    {
        using var factory = new MetadataApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PatchAsJsonAsync("/api/v1/applications", new { title = "Portal" });
        await AssertProblemAsync(response, HttpStatusCode.MethodNotAllowed);
        Assert.Contains("GET", response.Content.Headers.Allow);
        Assert.Contains("POST", response.Content.Headers.Allow);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(400)]
    [InlineData(413)]
    public async Task Exceptions_ReturnSafeProblemDetails_InProduction(int status)
    {
        using var factory = new MetadataApiFactory();
        using var failing = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services => services.AddScoped<IApplicationService>(_ =>
                throw (status == 500 ? new InvalidOperationException("sensitive database details")
                    : new BadHttpRequestException("sensitive request details", status))));
        });
        using var client = failing.CreateClient();
        using var response = await client.GetAsync("/api/v1/applications");
        await AssertProblemAsync(response, (HttpStatusCode)status);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sensitive", body);
        Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain("stack", body, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(body);
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("traceId").GetString()));
        if (status == 500)
        {
            Assert.Equal("Internal Server Error", document.RootElement.GetProperty("title").GetString());
        }
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)status, document.RootElement.GetProperty("status").GetInt32());
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

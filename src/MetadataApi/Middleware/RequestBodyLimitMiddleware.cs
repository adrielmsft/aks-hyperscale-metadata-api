using MetadataApi.Reliability;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace MetadataApi.Middleware;

internal sealed class RequestBodyLimitMiddleware(RequestDelegate next, IOptions<RequestLimitOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false })
        {
            await next(context);
            return;
        }

        var limit = options.Value.MaxRequestBodyBytes;
        if (context.Request.ContentLength > limit)
        {
            throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
        }

        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = limit;
        }

        // Bound buffering before the JSON binder; also works when no Content-Length is available.
        var originalBody = context.Request.Body;
        await using var body = new MemoryStream();
        var buffer = new byte[Math.Min(limit + 1, 8192)];
        while (true)
        {
            var read = await originalBody.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit + 1L - body.Length)), context.RequestAborted);
            if (read == 0)
            {
                break;
            }

            await body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            if (body.Length > limit)
            {
                throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
            }
        }

        body.Position = 0;
        context.Request.Body = body;
        try
        {
            await next(context);
        }
        finally
        {
            context.Request.Body = originalBody;
        }
    }
}

using System.Text.Json;
using SeoTester.Crawler;
using SeoTester.Fetching;

namespace SeoTester.Fetcher.Endpoints;

/// <summary>One call's bound on the fetcher (<see cref="FetcherHost.OperationTimeoutSetting"/>), registered by <see cref="FetcherHost.Build"/>.</summary>
internal sealed record FetcherCallBound(TimeSpan Timeout)
{
    /// <summary>One call under the bound; a call that runs out answers 504.</summary>
    public async Task<IResult> JsonAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken requestAborted)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        deadline.CancelAfter(Timeout);
        try
        {
            return Results.Json(await call(deadline.Token), SeoFetcherProtocol.Json);
        }
        catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
    }
}

/// <summary>The fetcher's request bodies: JSON, at most <see cref="SeoFetchLimits.MaxApiRequestBytes"/>.</summary>
internal static class FetcherRequestBody
{
    /// <summary>The body as <typeparamref name="T"/>; null when it is larger than the cap, not JSON, or not this shape.</summary>
    public static async Task<T?> ReadAsync<T>(Stream body, long? contentLength, CancellationToken cancellationToken) where T : class
    {
        if (contentLength > SeoFetchLimits.MaxApiRequestBytes)
            return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > SeoFetchLimits.MaxApiRequestBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        try
        {
            return JsonSerializer.Deserialize<T>(buffer.ToArray(), SeoFetcherProtocol.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A fetch's answer: 200, the frame's content type, and the frame (<see cref="SeoFetcherProtocol.WriteFrameAsync"/>).</summary>
internal sealed class FetchFrameResult(FetchResult result) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = SeoFetcherProtocol.FramedContentType;
        await SeoFetcherProtocol.WriteFrameAsync(httpContext.Response.Body, SeoFetcherProtocol.ToWire(result), result.Body, httpContext.RequestAborted);
    }
}

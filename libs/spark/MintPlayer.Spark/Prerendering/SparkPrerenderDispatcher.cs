using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using MintPlayer.Spark.Abstractions.Prerendering;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Prerendering;

/// <summary>
/// Runs one request a server-rendered page is going to make, in-process, through the application's
/// own endpoint (PRD §3, M3).
/// <para>
/// The request goes through routing, the endpoint's authorization policies and the endpoint itself,
/// under the page visitor's identity and cookies, in a DI scope of its own as a real request would get.
/// The response is therefore the one the visitor would have fetched, boundary net and redaction
/// included, byte for byte (SP4). Only an endpoint marked <see cref="SparkPrerenderableAttribute"/> runs.
/// </para>
/// </summary>
internal sealed class SparkPrerenderDispatcher
{
    /// <summary>
    /// The branch pipeline <c>UseSparkPrerendering</c> builds: routing, the allow-list, authorization and
    /// the endpoint. Null until then, and a prerender without it supplies nothing.
    /// </summary>
    public RequestDelegate? Pipeline { get; set; }

    private const string NotPrerenderableKey = "MintPlayer.Spark.Prerendering.NotPrerenderable";

    /// <summary>Whether the endpoint was refused because it is not marked prerenderable.</summary>
    public static bool WasRefused(HttpContext request) => request.Items.ContainsKey(NotPrerenderableKey);

    /// <summary>The branch: everything after <c>UseRouting</c>.</summary>
    public static void Configure(IApplicationBuilder branch)
    {
        branch.UseRouting();

        // The allow-list comes before authorization: an endpoint that may not run here never gets as far
        // as a challenge either.
        branch.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<SparkPrerenderableAttribute>() is null)
            {
                context.Items[NotPrerenderableKey] = true;
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next(context);
        });

        branch.UseAuthorization();

        branch.Run(async context =>
        {
            var endpoint = context.GetEndpoint()!;
            try
            {
                await endpoint.RequestDelegate!(context);
            }
            catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
            {
                // The page renders what the browser would have shown for a failed request.
                context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger<SparkPrerenderDispatcher>()
                    .LogWarning(ex, "Server rendering: {Method} {Path} failed.", context.Request.Method, context.Request.Path);
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }
            }
        });
    }

    /// <summary>
    /// Runs the request. <paramref name="pathAndQuery"/> is origin-relative, as the page sends it.
    /// </summary>
    public Task<(SparkPrerenderResponse Response, bool Refused)> SendAsync(SparkPrerenderPage page, string method, string pathAndQuery, string body)
    {
        var pipeline = Pipeline ?? throw new InvalidOperationException("Server rendering is not configured: call UseSparkPrerendering() on the SPA builder.");

        // ⚠️ Without its own execution context. The request sets IHttpContextAccessor.HttpContext, and the
        // accessor's setter clears the holder it finds in the CURRENT async context first: run inline,
        // it would blank the page request's HttpContext for everything else that page is still doing.
        Task<(SparkPrerenderResponse, bool)> task;
        using (ExecutionContext.SuppressFlow())
            task = Task.Run(() => RunAsync(pipeline, page, method, pathAndQuery, body));
        return task;
    }

    private static async Task<(SparkPrerenderResponse, bool)> RunAsync(RequestDelegate pipeline, SparkPrerenderPage page, string method, string pathAndQuery, string body)
    {
        await using var scope = page.Scopes.CreateAsyncScope();
        using var responseBody = new MemoryStream();

        var request = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = page.User };
        request.Features.Set<IHttpRequestLifetimeFeature>(new HttpRequestLifetimeFeature { RequestAborted = page.RequestAborted });
        request.Connection.RemoteIpAddress = page.RemoteIpAddress;
        request.Connection.RemotePort = page.RemotePort;

        request.Request.Method = method;
        request.Request.Scheme = page.Scheme;
        request.Request.Host = page.Host;
        request.Request.PathBase = page.PathBase;
        var queryStart = pathAndQuery.IndexOf('?');
        var path = new PathString(queryStart < 0 ? pathAndQuery : pathAndQuery[..queryStart]);
        request.Request.Path = path.StartsWithSegments(page.PathBase, out var remaining) ? remaining : path;
        request.Request.QueryString = queryStart < 0 ? QueryString.Empty : new QueryString(pathAndQuery[queryStart..]);

        // What the browser's request would carry: its cookies (sign-in, anti-forgery), and the language and
        // zone ng-spark's interceptors add.
        var headers = request.Request.Headers;
        headers.Cookie = page.Cookie;
        headers.UserAgent = page.UserAgent;
        headers.Accept = "application/json, text/plain, */*";
        headers.AcceptLanguage = page.Language;
        headers[RequestTimeZoneResolver.HeaderName] = page.TimeZone;
        if (body.Length > 0)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            headers.ContentType = "application/json";
            request.Request.ContentLength = bytes.Length;
            request.Request.Body = new MemoryStream(bytes);
        }
        request.Response.Body = responseBody;

        var accessor = scope.ServiceProvider.GetService<IHttpContextAccessor>();
        if (accessor is not null)
            accessor.HttpContext = request;
        try
        {
            await pipeline(request);
        }
        finally
        {
            if (accessor is not null)
                accessor.HttpContext = null;
        }

        if (WasRefused(request))
        {
            scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<SparkPrerenderDispatcher>()
                .LogWarning("Server rendering: {Method} {Path} is not marked [SparkPrerenderable], so it was not run.", method, pathAndQuery);
            return (new SparkPrerenderResponse(StatusCodes.Status404NotFound, null), true);
        }

        var text = responseBody.Length == 0 ? null : Encoding.UTF8.GetString(responseBody.GetBuffer(), 0, (int)responseBody.Length);
        return (new SparkPrerenderResponse(request.Response.StatusCode, text), false);
    }
}

/// <summary>
/// What a page's requests take from the page request, read once on the request's own thread: HttpContext
/// is not thread-safe, and the page's requests run concurrently (two of them reading <c>page.Request</c> at
/// once threw a NullReferenceException from the feature cache, 2026-10-30).
/// </summary>
internal sealed record SparkPrerenderPage(
    IServiceScopeFactory Scopes,
    System.Security.Claims.ClaimsPrincipal User,
    CancellationToken RequestAborted,
    System.Net.IPAddress? RemoteIpAddress,
    int RemotePort,
    string Scheme,
    HostString Host,
    PathString PathBase,
    Microsoft.Extensions.Primitives.StringValues Cookie,
    Microsoft.Extensions.Primitives.StringValues UserAgent,
    string Language,
    string TimeZone)
{
    public static SparkPrerenderPage From(HttpContext page, string language, string timeZone) => new(
        page.RequestServices.GetRequiredService<IServiceScopeFactory>(),
        page.User,
        page.RequestAborted,
        page.Connection.RemoteIpAddress,
        page.Connection.RemotePort,
        page.Request.Scheme,
        page.Request.Host,
        page.Request.PathBase,
        page.Request.Headers.Cookie,
        page.Request.Headers.UserAgent,
        language,
        timeZone);
}

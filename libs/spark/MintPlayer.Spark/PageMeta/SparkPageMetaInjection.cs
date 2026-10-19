using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Extensions;

namespace MintPlayer.Spark.PageMeta;

/// <summary>
/// Server-side meta tags for SPA routes (commerce PRD X6, spike S19): a navigation to a public page registered by an
/// <see cref="ISparkPageMetaProvider"/> gets <c>index.html</c> with that page's title, description, OpenGraph and
/// Twitter tags and canonical link, so link previews and indexing work without SSR.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="IStartupFilter"/> (the <see cref="Services.SparkServiceWorkerCacheHeaders"/> pattern): the static-file
/// middleware (<c>UseSpaStaticFilesImproved</c>) and the SPA fallback (<c>UseSpaImproved</c> in production,
/// <c>UseAngularCliServer</c>'s proxy in Development) all run outside <c>UseSpark()</c>. Wrapping the whole pipeline
/// covers both: each ends in a <c>text/html</c> body written through <see cref="IHttpResponseBodyFeature"/>, which this
/// middleware swaps for a buffer. The static file's <c>SendFileAsync</c> lands in the buffer too
/// (<see cref="StreamResponseBodyFeature"/> copies the file into its stream).
/// </para>
/// <para>
/// The provider is asked <b>before</b> anything is buffered: a <see langword="null"/> answer, the fast path for an
/// unknown id, passes the request through untouched. Only a 200 <c>text/html</c> answer is rewritten; anything else
/// is copied out as it came.
/// </para>
/// </remarks>
internal static class SparkPageMetaInjection
{
    /// <summary>Paths that are never a SPA page: Spark's API and the identity provider's endpoints.</summary>
    private static readonly PathString[] ExcludedPrefixes = [Endpoints.SparkGroup.Prefix, "/connect", "/.well-known"];

    /// <summary>The compiled patterns of each provider type, read once.</summary>
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<(string Pattern, TemplateMatcher Matcher)>> Matchers = new();

    public static IServiceCollection AddSparkPageMeta(this IServiceCollection services)
    {
        services.AddSparkConfigurationSection<SparkPageMetaOptions>();
        services.AddTransient<IStartupFilter, StartupFilter>();
        return services;
    }

    /// <summary>The middleware itself, separated so it can be tested without a host.</summary>
    internal static async Task Invoke(HttpContext context, Func<Task> next)
    {
        var request = context.Request;
        if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            || !AcceptsHtml(request)
            || ExcludedPrefixes.Any(p => request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next();
            return;
        }

        var providers = context.RequestServices.GetServices<ISparkPageMetaProvider>();
        if (!providers.Any())
        {
            await next();
            return;
        }

        var options = context.RequestServices.GetRequiredService<IOptions<SparkPageMetaOptions>>().Value;
        if (!options.Enabled || Match(providers, request.Path) is not { } match)
        {
            await next();
            return;
        }

        var meta = await match.Provider.GetAsync(
            new SparkPageMetaContext(context, match.Pattern, match.Values), context.RequestAborted);
        if (meta is null)
        {
            await next();
            return;
        }

        // The dev server must answer in plain text, or there is nothing to rewrite. And no conditional or partial
        // answer: a 304 against the template's ETag would keep a browser on a copy without this page's tags.
        request.Headers.AcceptEncoding = default;
        request.Headers.IfNoneMatch = default;
        request.Headers.IfModifiedSince = default;
        request.Headers.IfRange = default;
        request.Headers.Range = default;

        var originalBody = context.Features.Get<IHttpResponseBodyFeature>()!;
        using var buffer = new MemoryStream();
        var bufferingBody = new StreamResponseBodyFeature(buffer, originalBody);
        context.Features.Set<IHttpResponseBodyFeature>(bufferingBody);
        try
        {
            await next();
            await bufferingBody.CompleteAsync();
        }
        finally
        {
            context.Features.Set(originalBody);
        }

        var response = context.Response;
        var html = response.StatusCode == StatusCodes.Status200OK
            && response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true
            && string.IsNullOrEmpty(response.Headers.ContentEncoding)
            && !HttpMethods.IsHead(request.Method)
                ? Rewrite(context, options, meta, buffer)
                : null;

        if (html is null && HttpMethods.IsHead(request.Method) && response.StatusCode == StatusCodes.Status200OK
            && response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            // HEAD: the headers a GET would carry, no body. The length of the rewritten page is not known here.
            SetHeaders(response, contentLength: null);
            return;
        }

        if (html is null)
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(response.Body, context.RequestAborted);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        SetHeaders(response, bytes.Length);
        await response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    /// <summary>
    /// A navigation: <c>text/html</c> or <c>*/*</c> in <c>Accept</c>, or no <c>Accept</c> at all. Several link-preview
    /// bots send <c>*/*</c>; the response's content type is the real gate.
    /// </summary>
    private static bool AcceptsHtml(HttpRequest request)
    {
        var accept = request.Headers.Accept.ToString();
        return accept.Length == 0
            || accept.Contains("text/html", StringComparison.OrdinalIgnoreCase)
            || accept.Contains("*/*", StringComparison.Ordinal);
    }

    private static (ISparkPageMetaProvider Provider, string Pattern, RouteValueDictionary Values)? Match(
        IEnumerable<ISparkPageMetaProvider> providers, PathString path)
    {
        foreach (var provider in providers)
        {
            var matchers = Matchers.GetOrAdd(provider.GetType(), _ => provider.RoutePatterns
                .Select(pattern => (pattern, new TemplateMatcher(
                    TemplateParser.Parse(pattern.TrimStart('~').TrimStart('/')), new RouteValueDictionary())))
                .ToArray());

            foreach (var (pattern, matcher) in matchers)
            {
                var values = new RouteValueDictionary();
                if (matcher.TryMatch(path, values))
                    return (provider, pattern, values);
            }
        }

        return null;
    }

    private static string? Rewrite(HttpContext context, SparkPageMetaOptions options, SparkPageMeta meta, MemoryStream buffer)
    {
        var services = context.RequestServices;
        var origin = SparkPublicBaseUrl.Resolve(
            options.PublicOrigin, services.GetService<IConfiguration>(), services.GetService<IHostEnvironment>(), context);
        Uri? originUri = origin is not null && Uri.TryCreate(origin + "/", UriKind.Absolute, out var o) && IsHttp(o) ? o : null;

        var canonical = Absolute(meta.Canonical, originUri)
            ?? (originUri is null ? null : new Uri(originUri, "./" + context.Request.Path.ToUriComponent().TrimStart('/')));
        var image = Absolute(meta.Image, originUri);

        var template = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return SparkPageMetaHtml.Rewrite(template, meta, canonical, image);
    }

    /// <summary>
    /// <paramref name="uri"/> as an absolute http(s) URL: as given when it is one, resolved against
    /// <paramref name="origin"/> when relative, else <see langword="null"/>.
    /// </summary>
    /// <remarks>⚠️ A rooted path is an absolute <c>file://</c> URI on Linux, hence the scheme check.</remarks>
    private static Uri? Absolute(Uri? uri, Uri? origin)
    {
        if (uri is null)
            return null;
        if (uri.IsAbsoluteUri && IsHttp(uri))
            return uri;
        var relative = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
        return origin is null ? null : new Uri(origin, "./" + relative.TrimStart('/'));
    }

    private static bool IsHttp(Uri uri) => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>The page is per-route now: no validators of the template, and revalidated on every navigation.</summary>
    private static void SetHeaders(HttpResponse response, long? contentLength)
    {
        response.Headers.ETag = default;
        response.Headers.LastModified = default;
        response.Headers.TransferEncoding = default;
        response.Headers.AcceptRanges = default;
        response.ContentLength = contentLength;
        response.Headers.CacheControl = "no-cache";
    }

    private sealed class StartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) => Invoke(context, () => nextMiddleware(context)));
            next(app);
        };
    }
}

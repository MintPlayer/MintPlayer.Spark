using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Links;
using MintPlayer.Spark.Abstractions.Prerendering;
using MintPlayer.Spark.Endpoints;

namespace MintPlayer.Spark.Prerendering;

/// <summary>
/// The server half of <see cref="ISparkEntityRedirect"/> (#602, tier c): a page path that matches a registered pattern
/// is answered before Angular renders. The lookup names an object, which is loaded through <c>POST /spark/po/load</c>
/// like any page's object, and a readable one is a 301 to its <c>href</c>. Everything else (no object, a missing one,
/// a denied one) is the uniform refusal: 404, or 401 for an anonymous caller in an app that can sign in.
/// </summary>
internal sealed partial class SparkEntityRedirectPrerenderContributor : ISparkPrerenderContributor
{
    [Inject] private readonly IEnumerable<ISparkEntityRedirect> redirects;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public async Task ContributeAsync(ISparkPrerenderContext context)
    {
        var segments = SparkRoutesPrerenderContributor.Segments(context.Path);
        foreach (var redirect in redirects)
        {
            if (Match(redirect.Pattern, segments) is not { } values)
                continue;

            var target = await redirect.ResolveAsync(values, httpContextAccessor.HttpContext?.RequestAborted ?? default);
            if (target is null)
            {
                context.SetPageStatus(httpContextAccessor.HttpContext is { } http
                    ? SparkDenial.Refuse(http).StatusCode
                    : StatusCodes.Status404NotFound);
                return;
            }

            // The load decides the status (missing ≡ denied) and the 301 goes to its href, never to a path built here.
            await SparkRoutesPrerenderContributor.SupplyObjectAsync(context, target.Type, target.Id, canonicalize: true);
            return;
        }
    }

    /// <summary>The pattern's placeholder values, or null when the path does not match it.</summary>
    internal static IReadOnlyDictionary<string, string>? Match(string pattern, string[] segments)
    {
        var parts = pattern.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != segments.Length)
            return null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length > 2 && part[0] == '{' && part[^1] == '}')
                values[part[1..^1]] = segments[i];
            else if (!string.Equals(part, segments[i], StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return values;
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Mcp.Authentication;

namespace MintPlayer.Spark.Mcp.Http;

/// <summary>
/// RFC 9728 for <c>/mcp</c> (MCP PRD D5 gap G2, plan M3b): the protected-resource metadata document that tells an
/// agent where to sign in, and the <c>WWW-Authenticate</c> challenge that points at it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Served by the Mcp package</b>, not by the authorization server: the document describes the resource
/// (<c>/mcp</c>, its canonical URL, D20), and the setup that issues its tokens may be the embedded server or an
/// external one (M3c). Each OAuth setup names its server through <c>IAgentCredentialScheme.AuthorizationServer</c>;
/// the embedded one is listed first (PRD §4.2).
/// </para>
/// <para>
/// Published only when a setup offers OAuth: with personal tokens alone there is nowhere to sign in, and a 401 stays
/// a plain <c>Bearer</c> (PRD §4.2, setup 1).
/// </para>
/// </remarks>
internal static class McpProtectedResource
{
    /// <summary>The RFC 9728 well-known prefix; the resource's path follows it (<c>/.well-known/oauth-protected-resource/mcp</c>).</summary>
    public const string WellKnown = "/.well-known/oauth-protected-resource";

    /// <summary>
    /// The canonical URL (<c>mcp.CanonicalUrl</c>, else the public base URL + the path). Only in Development, with
    /// neither configured, the request's own origin, as the embedded server's issuer does; never elsewhere.
    /// </summary>
    public static string? ResourceUrl(HttpContext context, SparkMcpOptions options)
    {
        var services = context.RequestServices;
        if (McpOrigin.CanonicalUrl(options, services.GetRequiredService<IConfiguration>()) is { } canonical)
            return canonical;
        return services.GetRequiredService<IHostEnvironment>().IsDevelopment()
            ? Origin(context) + options.NormalizedPath
            : null;
    }

    /// <summary>Where the document of <paramref name="resourceUrl"/> lives (RFC 9728 §3.1: the well-known prefix inserted before the path).</summary>
    public static string MetadataUrl(string resourceUrl)
    {
        var uri = new Uri(resourceUrl);
        return uri.GetLeftPart(UriPartial.Authority) + WellKnown + uri.AbsolutePath.TrimEnd('/');
    }

    /// <summary>The scopes the OAuth setups offer, de-duplicated, in their order.</summary>
    public static IReadOnlyList<string> Scopes(McpAuthentication authentication)
        => authentication.OAuthSetups.SelectMany(s => s.Source.ScopesSupported).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// The <c>WWW-Authenticate</c> values of a 401: one RFC 9728 challenge when a setup offers OAuth
    /// (<c>Bearer resource_metadata="…", scope="…"</c>), else a plain <c>Bearer</c>; with
    /// <c>error="invalid_token"</c> when a presented token was refused.
    /// </summary>
    public static string Challenge(HttpContext context, SparkMcpOptions options, McpAuthentication authentication, bool invalidToken)
    {
        var parameters = new List<string>();
        if (invalidToken)
            parameters.Add("error=\"invalid_token\"");

        if (authentication.OffersOAuth && ResourceUrl(context, options) is { } resource)
        {
            parameters.Add($"resource_metadata=\"{MetadataUrl(resource)}\"");
            if (Scopes(authentication) is { Count: > 0 } scopes)
                parameters.Add($"scope=\"{string.Join(' ', scopes)}\"");
        }

        return parameters.Count == 0 ? "Bearer" : "Bearer " + string.Join(", ", parameters);
    }

    /// <summary>The document (RFC 9728 §2), or 404 when no OAuth setup is wired or the resource URL is unknown.</summary>
    public static IResult Document(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<SparkMcpOptions>();
        var authentication = context.RequestServices.GetRequiredService<McpAuthentication>();
        if (!authentication.OffersOAuth || ResourceUrl(context, options) is not { } resource)
            return Results.NotFound();

        var origin = Origin(context);
        var servers = authentication.OAuthSetups
            .Select(s => s.Source.AuthorizationServer(origin))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        context.Response.Headers.CacheControl = "public, max-age=300";
        return Results.Json(new Dictionary<string, object>
        {
            ["resource"] = resource,
            ["authorization_servers"] = servers,
            ["scopes_supported"] = Scopes(authentication),
            ["bearer_methods_supported"] = new[] { "header" },
            ["resource_name"] = options.ServerName,
        });
    }

    private static string Origin(HttpContext context) => $"{context.Request.Scheme}://{context.Request.Host}";
}

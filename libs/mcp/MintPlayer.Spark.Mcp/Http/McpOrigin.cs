using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Mcp.Http;

/// <summary>
/// The canonical server URL and the <c>Origin</c> rule (MCP PRD D3, D20).
/// </summary>
internal static class McpOrigin
{
    /// <summary>
    /// <c>mcp.CanonicalUrl</c>, else the configured <c>Spark:Auth:PublicBaseUrl</c> + the path, without a
    /// trailing slash; <see langword="null"/> when neither is configured. Never the request's <c>Host</c>: here
    /// it would be chosen by the very page a DNS-rebinding attack runs.
    /// </summary>
    public static string? CanonicalUrl(SparkMcpOptions options, IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(options.CanonicalUrl))
            return options.CanonicalUrl.TrimEnd('/');

        var origin = SparkPublicBaseUrl.Resolve(null, configuration, environment: null, context: null);
        return origin is null ? null : origin + options.NormalizedPath;
    }

    /// <summary>
    /// A request without <c>Origin</c> (every desktop agent) passes. One with an <c>Origin</c> passes only when it
    /// is the canonical URL's origin, or, in Development, a loopback origin (a browser-based inspector).
    /// </summary>
    public static bool IsAllowed(HttpContext context, SparkMcpOptions options, IConfiguration configuration, IHostEnvironment environment)
    {
        var header = context.Request.Headers.Origin;
        if (header.Count == 0)
            return true;
        if (header.Count > 1)
            return false;

        // The scheme check is load-bearing: on Linux Uri.TryCreate("/x", Absolute) succeeds as file:///x.
        if (!Uri.TryCreate(header.ToString(), UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https"))
            return false;

        if (CanonicalUrl(options, configuration) is { } canonical
            && Uri.TryCreate(canonical, UriKind.Absolute, out var server)
            && string.Equals(origin.Scheme, server.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.IdnHost, server.IdnHost, StringComparison.OrdinalIgnoreCase)
            && origin.Port == server.Port)
        {
            return true;
        }

        return environment.IsDevelopment() && origin.IsLoopback;
    }
}

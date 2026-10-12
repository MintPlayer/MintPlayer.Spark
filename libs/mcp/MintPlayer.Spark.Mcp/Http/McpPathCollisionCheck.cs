using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.Mcp.Http;

/// <summary>
/// Refuses startup when another endpoint already answers the MCP path (MCP PRD D20): one of the two would
/// silently shadow the other.
/// </summary>
/// <remarks>
/// <para>
/// A startup filter that checks <i>after</i> the rest of the pipeline is configured: that is the first moment
/// every endpoint is mapped — the application's own are added after <c>MapSpark()</c>, which is where the MCP
/// endpoint is mapped, and a minimal-hosting app's <c>UseEndpoints</c> runs last. A hosted service is not
/// ordered reliably against the server's pipeline build (it ran first in a test host and saw no endpoints).
/// </para>
/// <para>
/// Only endpoints are seen. An Angular route named like the path is client-side and invisible here (the guide
/// says so).
/// </para>
/// </remarks>
internal sealed class McpPathCollisionCheck(SparkMcpOptions options) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);
        Verify(app.ApplicationServices);
    };

    private void Verify(IServiceProvider services)
    {
        var path = options.NormalizedPath.Trim('/');
        var collisions = services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<SparkMcpEndpointMarker>() is null)
            .Where(e => string.Equals((e.RoutePattern.RawText ?? string.Empty).Trim('/'), path, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.DisplayName ?? e.RoutePattern.RawText)
            .Distinct()
            .ToList();

        if (collisions.Count > 0)
        {
            throw new InvalidOperationException(
                $"The MCP endpoint path '{options.NormalizedPath}' is already mapped by {string.Join(", ", collisions)}. "
                + "Choose another mcp.Path (Spark:Mcp:Path) or move the other endpoint: the canonical MCP URL is permanent once users have added it.");
        }
    }
}

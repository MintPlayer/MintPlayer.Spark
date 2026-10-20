using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Mcp.Authentication;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Mcp;

/// <summary>
/// What this application makes available to agents, for the pages where a user chooses what an agent may do
/// (Connected applications, the consent screen): the layers the Mcp package owns, app(agent) and type, so the
/// page offers exactly what a call could use (MCP PRD D6, §4.6).
/// </summary>
internal sealed class McpAgentExposure(
    ISparkCatalogue catalogue,
    SparkMcpOptions options,
    McpAuthentication authentication,
    Microsoft.Extensions.Configuration.IConfiguration configuration) : IAgentExposure
{
    public EAgentAuthentication Setups => authentication.Effective;

    public string? ResourceUrl => Http.McpOrigin.CanonicalUrl(options, configuration);

    public async Task<IReadOnlyList<AgentExposedType>> ListTypesAsync(CancellationToken cancellationToken = default)
    {
        // The catalogue lists the types the caller may see on the web, through the same filtering as
        // /spark/types: a user is never offered a type whose existence they may not know.
        var types = await catalogue.ListEntityTypesAsync(cancellationToken);
        var queries = await catalogue.ListQueriesAsync(cancellationToken);

        return types
            .Where(t => (t.AgentPermissions & EAgentPermission.Read) != 0)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new AgentExposedType(
                t.Id.ToString("D"),
                t.Name,
                SparkMcpResult.Label(t.Label) ?? t.Name,
                t.AgentPermissions,
                queries
                    .Where(q => q.ParentReference is null && string.Equals(q.EntityType, t.Name, StringComparison.Ordinal))
                    .Select(q => SparkMcpResult.Label(q.Label) ?? q.Alias ?? q.Name)
                    .ToList()))
            .ToList();
    }

    EAgentPermission IAgentExposure.AppPermissions(AgentInformation agent) => AppPermissions(options, agent);

    /// <summary>The application layer for <paramref name="agent"/>; an anonymous call is always read-only (MCP PRD D17).</summary>
    internal static EAgentPermission AppPermissions(SparkMcpOptions options, AgentInformation agent)
    {
        var app = options.AgentPermissions(agent);
        return agent.Kind == EAgentKind.Anonymous ? app & EAgentPermission.Read : app;
    }
}

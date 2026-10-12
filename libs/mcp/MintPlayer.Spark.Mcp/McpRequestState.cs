using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Mcp.Authentication;

namespace MintPlayer.Spark.Mcp;

/// <summary>
/// What one <c>/mcp</c> request is: the agent behind it, the user's grant for it, and the application's
/// permission for that agent, clamped. Set by <see cref="Http.McpGateMiddleware"/> as an <see cref="HttpContext"/>
/// feature, so the agent restriction inside <c>IPermissionService</c> and the tool filters read the same answer
/// whichever DI scope they run in. Absent on every other request, which is how the restriction leaves the web
/// untouched.
/// </summary>
internal sealed class McpRequestState
{
    public required AgentInformation Agent { get; init; }

    /// <summary>The setup whose scheme authenticated the token; <see langword="null"/> for an anonymous call.</summary>
    public McpAuthenticationSetup? Setup { get; init; }

    /// <summary>
    /// The user's grant for this agent (MCP PRD D12), read for this request; <see langword="null"/> for an
    /// anonymous call, which has no grant and is governed by the application layer and <c>security.json</c>.
    /// </summary>
    public AgentGrantScope? Grant { get; init; }

    /// <summary>
    /// The application layer (<c>mcp.AgentPermissions(agent)</c>), evaluated once for this request; for an
    /// anonymous caller clamped to <see cref="EAgentPermission.Read"/> (MCP PRD D17).
    /// </summary>
    public required EAgentPermission AppPermissions { get; init; }

    /// <summary>
    /// The most this request may do on anything: the application layer ∩ the grant's cap (D6). What the tool list
    /// is filtered by; per type, the type layer and the grant's type choice narrow it further.
    /// </summary>
    public EAgentPermission Permissions => Grant is null ? AppPermissions : AppPermissions & Grant.Permissions;

    public bool IsAnonymous => Agent.Kind == EAgentKind.Anonymous;

    /// <summary>Rows a tool returned, for the audit event. Set by <see cref="SparkMcpResult"/>.</summary>
    public int? RowCount { get; set; }

    /// <summary>The per-call fence tag (MCP PRD D10), created on first use.</summary>
    public string? FenceTag { get; set; }

    public static McpRequestState? Of(HttpContext? context) => context?.Features.Get<McpRequestState>();
}

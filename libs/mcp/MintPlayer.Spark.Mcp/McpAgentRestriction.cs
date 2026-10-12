using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Mcp;

/// <summary>
/// The agent layers of the effective permission, applied inside <c>IPermissionService</c> (MCP PRD D6, D13):
/// app(agent) ∩ the type's <c>agentPermissions</c>. Asked only about a resource <c>security.json</c> already
/// allows, so it narrows and never widens; every tool, generic or app-written, and every framework path they
/// call (references, sub-queries, the catalogue) honours it without asking.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read or Write.</b> <c>Read</c>, <c>Query</c> and <c>QueryRead</c> need <see cref="EAgentPermission.Read"/>;
/// every other action — <c>Edit</c>, <c>New</c>, <c>Delete</c>, a custom action, <c>ViewDeleted</c> — needs
/// <see cref="EAgentPermission.Write"/>. Unknown means Write: a new verb fails closed.
/// </para>
/// <para>
/// <b>Types opt in.</b> A target naming an entity type (or <c>Type/Attribute</c>) is allowed only when the type
/// grants the needed permission; its default, <see cref="EAgentPermission.None"/>, hides it — and with it every
/// reference to it, which then shows the placeholder (D19). A target that is not an entity type (an
/// application resource) is governed by the application layer and <c>security.json</c> alone.
/// </para>
/// <para>
/// The user's grant (D12) is a further <c>IAgentRequestRestriction</c> the Authorization package registers
/// (plan M3a); <c>IPermissionService</c> requires every registered restriction to allow.
/// </para>
/// </remarks>
internal sealed class McpAgentRestriction(IHttpContextAccessor httpContextAccessor, IModelLoader modelLoader) : IAgentRequestRestriction
{
    public ValueTask<bool> AllowsAsync(string action, string target, CancellationToken cancellationToken = default)
    {
        var state = McpRequestState.Of(httpContextAccessor.HttpContext);
        if (state is null)
            return ValueTask.FromResult(true); // not an agent request: the web is untouched

        var needed = Needs(action);
        if ((state.AppPermissions & needed) != needed)
            return ValueTask.FromResult(false);

        var slash = target.IndexOf('/');
        var typeName = slash < 0 ? target : target[..slash];
        var type = modelLoader.GetEntityTypeByName(typeName);
        return ValueTask.FromResult(type is null || (type.AgentPermissions & needed) == needed);
    }

    /// <summary>The agent permission an action needs.</summary>
    internal static EAgentPermission Needs(string action)
        => action.Equals("Read", StringComparison.OrdinalIgnoreCase)
           || action.Equals("Query", StringComparison.OrdinalIgnoreCase)
           || action.Equals("QueryRead", StringComparison.OrdinalIgnoreCase)
            ? EAgentPermission.Read
            : EAgentPermission.Write;
}

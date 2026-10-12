using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Mcp;

/// <summary>
/// The agent layers of the effective permission, applied inside <c>IPermissionService</c> (MCP PRD D6, D13):
/// app(agent) ∩ the type's <c>agentPermissions</c> ∩ the user's grant. Asked only about a resource <c>security.json</c> already
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
/// <b>The user's grant (D12, D13)</b> is applied here too: its read/changes cap on every target, and its type
/// choice on entity types. The package that issued the token reads the grant for the request
/// (<c>IAgentCredentialScheme.DescribeAsync</c>, Abstractions) and the gate keeps it on the request state; only this
/// package knows which targets are entity types, so the grant's semantics (<c>AgentGrantScope.For</c>) and the
/// type test meet here (decision log, M3a seam).
/// </para>
/// <para>
/// <b>An application tool's declared reach (decision log, M5).</b> While a tool marked
/// <see cref="McpReadsThroughAttribute"/> runs, reading its type is judged by the "through" type's layers (a run's pages
/// as the run); while one marked <see cref="McpRunsActionAttribute"/> runs, that one action needs <c>Write</c> from the
/// application layer and the grant's cap (checked first, as for every write) and <c>Read</c> from the type and the
/// grant's choice of types. Both are declared on the tool by its developer, apply to that call only, and never touch
/// <c>security.json</c> or row security.
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
        if ((state.Permissions & needed) != needed)
            return ValueTask.FromResult(false); // app layer ∩ the grant's read/changes

        var slash = target.IndexOf('/');
        var typeName = slash < 0 ? target : target[..slash];
        var type = modelLoader.GetEntityTypeByName(typeName);
        if (type is null)
            return ValueTask.FromResult(true); // an application resource: app layer and security.json only

        // The type layer and the grant's choice of types: of this type, or what the running tool declared.
        var layer = needed;
        if (state.Reach is { } reach)
        {
            if (needed == EAgentPermission.Read && reach.ReadThrough(type.Name) is { } through)
            {
                // Read as part of the "through" type: its layers, never more (an unknown name fails closed).
                if (modelLoader.GetEntityTypeByName(through) is not { } throughType)
                    return ValueTask.FromResult(false);
                type = throughType;
            }
            else if (needed == EAgentPermission.Write && reach.RunsAction(type.Name, action))
            {
                layer = EAgentPermission.Read; // the tool's own write; the type must still be readable by the agent
            }
        }

        return ValueTask.FromResult(
            (type.AgentPermissions & layer) == layer
            && (state.Grant is null || (state.Grant.For(type.Name) & layer) == layer));
    }

    /// <summary>The agent permission an action needs.</summary>
    internal static EAgentPermission Needs(string action)
        => action.Equals("Read", StringComparison.OrdinalIgnoreCase)
           || action.Equals("Query", StringComparison.OrdinalIgnoreCase)
           || action.Equals("QueryRead", StringComparison.OrdinalIgnoreCase)
            ? EAgentPermission.Read
            : EAgentPermission.Write;
}

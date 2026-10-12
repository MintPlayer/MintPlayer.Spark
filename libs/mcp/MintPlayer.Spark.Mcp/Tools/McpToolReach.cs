using ModelContextProtocol.Server;

namespace MintPlayer.Spark.Mcp;

/// <summary>
/// An application tool that shows rows of an entity type as part of another one, which agents may read (MCP PRD
/// §4.7: SeoTester's <c>PageResult</c> stays <c>None</c>, "reached through the tools"). While the tool runs, reading
/// <see cref="Type"/> is governed by <see cref="Through"/>'s agent layers — its <c>agentPermissions</c> and the user's
/// grant for it — instead of its own. Writing it is not affected, and <c>security.json</c> and row security still
/// decide which rows, exactly as on the web.
/// </summary>
/// <example>
/// <code>
/// [McpServerTool(Name = "list_findings", ReadOnly = true)]
/// [McpReadsThrough("PageResult", "AuditRun")]   // a run's pages, for an agent that may read the run
/// public async Task&lt;CallToolResult&gt; ListFindingsAsync(...)
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class McpReadsThroughAttribute(string type, string through) : Attribute
{
    /// <summary>The entity type the tool reads, by name; its own <c>agentPermissions</c> may be <c>None</c>.</summary>
    public string Type { get; } = type;

    /// <summary>The entity type whose agent layers stand in for <see cref="Type"/>'s when the tool reads it.</summary>
    public string Through { get; } = through;
}

/// <summary>
/// An application tool that runs one custom action on an entity type agents may only read (MCP PRD D24: SeoTester's one
/// write, <c>seotester_start_scan</c>, runs <c>StartAudit</c> on <c>Site</c>, whose <c>agentPermissions</c> is
/// <c>Read</c>). While the tool runs, that action needs <c>Write</c> from the application layer and the grant's
/// read/changes — as every write does — but only <c>Read</c> from the type's <c>agentPermissions</c> and the user's choice
/// of types: the developer chose this one action by writing the tool. Every other action on the type still needs
/// <c>Write</c> there, and <c>security.json</c>, row security, disabled actions and quotas apply as for the button.
/// </summary>
/// <remarks>The tool must not be <c>ReadOnly</c>: it is a write, listed only to agents allowed changes. Startup fails otherwise.</remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class McpRunsActionAttribute(string type, string action) : Attribute
{
    /// <summary>The entity type the action runs on, by name.</summary>
    public string Type { get; } = type;

    /// <summary>The custom action, by name.</summary>
    public string Action { get; } = action;
}

/// <summary>
/// What the tool being called declared it reaches (<see cref="McpReadsThroughAttribute"/>, <see cref="McpRunsActionAttribute"/>),
/// kept on the request state for the call so the agent restriction inside <c>IPermissionService</c> can apply it.
/// </summary>
internal sealed class McpToolReach
{
    private readonly IReadOnlyList<McpReadsThroughAttribute> readsThrough;
    private readonly IReadOnlyList<McpRunsActionAttribute> runsActions;

    private McpToolReach(IReadOnlyList<McpReadsThroughAttribute> readsThrough, IReadOnlyList<McpRunsActionAttribute> runsActions)
    {
        this.readsThrough = readsThrough;
        this.runsActions = runsActions;
    }

    /// <summary>The reach <paramref name="tool"/> declares, or <see langword="null"/> when it declares none (every generic tool).</summary>
    public static McpToolReach? Of(McpServerTool tool)
    {
        var reads = tool.Metadata.OfType<McpReadsThroughAttribute>().ToList();
        var runs = tool.Metadata.OfType<McpRunsActionAttribute>().ToList();
        return reads.Count == 0 && runs.Count == 0 ? null : new McpToolReach(reads, runs);
    }

    /// <summary>The type whose agent layers govern reading <paramref name="typeName"/> in this tool, or <see langword="null"/>.</summary>
    public string? ReadThrough(string typeName)
        => readsThrough.FirstOrDefault(r => string.Equals(r.Type, typeName, StringComparison.OrdinalIgnoreCase))?.Through;

    /// <summary>Whether this tool declared it runs <paramref name="action"/> on <paramref name="typeName"/>.</summary>
    public bool RunsAction(string typeName, string action)
        => runsActions.Any(r => string.Equals(r.Type, typeName, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Action, action, StringComparison.OrdinalIgnoreCase));
}

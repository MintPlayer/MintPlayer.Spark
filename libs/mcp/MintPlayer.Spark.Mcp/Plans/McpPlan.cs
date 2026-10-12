namespace MintPlayer.Spark.Mcp.Plans;

/// <summary>
/// A set of changes an agent proposed (MCP PRD D8), in the <c>McpPlans</c> collection: what <c>propose_changes</c>
/// validated and showed, bound to the user, the agent and the grant that proposed it. <c>apply_changes</c> runs it
/// once. A proposed plan carries <c>@expires</c> 10 minutes ahead; an applied (or failed) one is kept
/// <see cref="SparkMcpOptions.PlanRetention"/> as the record of what agents changed.
/// </summary>
/// <remarks>
/// RavenDB deletes an expired plan itself, which is housekeeping only: every read checks
/// <see cref="ExpiresAt"/>, because the expiration sweep may run long after it.
/// </remarks>
internal sealed class McpPlan
{
    public string? Id { get; set; }

    /// <summary>The local user id of the proposer.</summary>
    public required string UserId { get; set; }

    /// <summary>The agent's client id (<see cref="Abstractions.AgentInformation.ClientId"/>).</summary>
    public string? ClientId { get; set; }

    /// <summary>The agent's kind, for the audit record.</summary>
    public required string AgentKind { get; set; }

    /// <summary>The user's grant for the agent (<c>AgentGrants/…</c>).</summary>
    public string? GrantId { get; set; }

    /// <summary>The agent's own one-line description of the plan. Agent-written, so data, never instructions.</summary>
    public string? Summary { get; set; }

    public List<McpPlanOperation> Operations { get; set; } = [];

    /// <summary>The readable diff shown to the agent, one entry per operation, which <c>apply_changes</c> must echo.</summary>
    public List<string> Changes { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    /// <summary>When a proposed plan stops being appliable; for an applied or failed one, when the record goes.</summary>
    public DateTime ExpiresAt { get; set; }

    public McpPlanStatus Status { get; set; } = McpPlanStatus.Proposed;

    public DateTime? AppliedAt { get; set; }

    /// <summary>Why an apply failed, as the tool answered it.</summary>
    public string? Error { get; set; }

    /// <summary>The ids the plan's creates were given, in operation order (null for the other operations).</summary>
    public List<string?>? ResultIds { get; set; }
}

/// <summary>Where a plan is in its one life.</summary>
internal enum McpPlanStatus
{
    /// <summary>Validated and shown; appliable until it expires.</summary>
    Proposed,
    /// <summary>Claimed by an apply that is running (or crashed); never appliable again.</summary>
    Applying,
    /// <summary>Every operation committed.</summary>
    Applied,
    /// <summary>The apply was refused or conflicted; nothing was saved.</summary>
    Failed,
}

/// <summary>One operation of a plan, as proposed.</summary>
internal sealed class McpPlanOperation
{
    /// <summary><c>create</c>, <c>update</c>, <c>delete</c> or <c>action</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>The entity type's name.</summary>
    public required string Type { get; set; }

    /// <summary>The object's id (update, delete; the object a custom action runs on).</summary>
    public string? Id { get; set; }

    /// <summary>The object's version when the plan was proposed: the write must still find it.</summary>
    public string? Etag { get; set; }

    /// <summary>The values to set, by attribute name, each as its JSON text.</summary>
    public Dictionary<string, string>? Values { get; set; }

    /// <summary>The custom action's name.</summary>
    public string? Action { get; set; }

    /// <summary>The query a custom action's selection comes from.</summary>
    public string? Query { get; set; }

    /// <summary>The selected rows of a custom action.</summary>
    public List<string>? Ids { get; set; }
}

using System.ComponentModel;
using MintPlayer.Spark.Mcp.Plans;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MintPlayer.Spark.Mcp.Tools;

/// <summary>
/// The generic write tools (MCP PRD D8, §4.4): <c>propose_changes</c> validates and stores a plan and changes
/// nothing; <c>apply_changes</c> runs it once, after the agent echoed the readable diff, so the host's approval
/// prompt shows the user exactly what will run. Both need <c>Write</c> (neither is read-only), so an agent whose
/// permission lacks it does not see them; anonymous callers never do (D17).
/// </summary>
[McpServerToolType]
internal sealed class SparkWriteTools(McpPlanner planner, SparkMcpOptions options)
{
    [McpServerTool(Name = "propose_changes", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Validates a set of changes (create, update, delete, custom action) as this user, exactly as the application's own pages would, and stores them as a plan. Changes nothing. Returns planId and the readable changes; to run them, call apply_changes with both, the changes exactly as returned. At most 50 operations; a plan expires after 10 minutes and runs once.")]
    public async Task<CallToolResult> ProposeChangesAsync(
        [Description("One line saying what the plan does and why, for the user and the record.")] string summary,
        [Description("The operations, in order. Read objects with get first: update and delete apply to the version you read.")] McpChange[] operations,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var proposal = await planner.ProposeAsync(summary, operations, cancellationToken);
            var apply = McpToolRegistration.Prefixed(options.ToolPrefix, "apply_changes");
            return SparkMcpResult.Text(
                $"Proposed; nothing has changed yet. Show the user the changes below. To run them, call {apply} with this planId " +
                $"and changes exactly as listed (same entries, same order). The plan expires at {proposal.ExpiresAt:O} and runs once.\n" +
                SparkMcpResult.Untrusted(System.Text.Json.JsonSerializer.Serialize(
                    new { planId = proposal.PlanId, changes = proposal.Changes }, Json)));
        }
        catch (McpAnonymousPlanException)
        {
            return SparkMcpResult.Refuse();
        }
        catch (McpPlanRefusal refusal)
        {
            return SparkMcpResult.Invalid(refusal.Message);
        }
    }

    [McpServerTool(Name = "apply_changes", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Runs a plan propose_changes stored: pass its planId and its changes exactly as returned. Every check runs again; if anything the plan touches changed since, nothing is saved and the answer says so. A plan runs once, only for the user and agent that proposed it.")]
    public async Task<CallToolResult> ApplyChangesAsync(
        [Description("The planId propose_changes returned.")] string planId,
        [Description("The changes propose_changes returned, exactly: what the user approves is what runs.")] string[] changes,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var application = await planner.ApplyAsync(planId, changes, cancellationToken);
            SparkMcpResult.Track(application.Changes.Count);
            // An operation the application diverted for approval (commerce X7) is committed as a proposal, not saved:
            // the answer says so rather than claim every operation was saved.
            var headline = application.Submitted.Count == 0
                ? $"Applied: all {application.Changes.Count} operation(s) were saved.\n"
                : $"Applied: {application.Changes.Count - application.Submitted.Count} operation(s) were saved; " +
                  $"operation(s) {string.Join(", ", application.Submitted.Select(s => s.Operation))} were submitted for approval " +
                  "and are not saved until someone approves them.\n";
            return SparkMcpResult.Text(
                headline +
                // Through Spark's options: a custom action's result may be a persistent object (the redaction net, D10).
                SparkMcpResult.Untrusted(SparkMcpResult.Serialize(
                    new
                    {
                        planId = application.PlanId,
                        ids = application.ResultIds,
                        result = application.ActionResult,
                        submitted = application.Submitted.Count == 0 ? null : application.Submitted,
                    })));
        }
        catch (McpAnonymousPlanException)
        {
            return SparkMcpResult.Refuse();
        }
        catch (McpPlanRefusal refusal)
        {
            return SparkMcpResult.Invalid(refusal.Message);
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

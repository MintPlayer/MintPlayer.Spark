using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The startup check on each type's <c>agentPermissions</c> (MCP PRD D6): <c>Write</c> without <c>Read</c>
/// is refused, because an agent that may change what it cannot read would write blind; so is a value with
/// bits no <see cref="EAgentPermission"/> names, which a numeric value in a hand-written model can produce.
/// </summary>
internal static class AgentPermissionsCheck
{
    private const EAgentPermission Known = EAgentPermission.Read | EAgentPermission.Write;

    /// <summary>One message per type that declares an invalid <c>agentPermissions</c>.</summary>
    public static IReadOnlyList<string> Problems(IEnumerable<EntityTypeDefinition> definitions)
    {
        var problems = new List<string>();
        foreach (var definition in definitions)
        {
            var permissions = definition.AgentPermissions;
            if ((permissions & ~Known) != 0)
                problems.Add($"Model type {definition.Name} declares \"agentPermissions\": {(int)permissions}, which is not a combination of Read and Write.");
            else if (permissions.HasFlag(EAgentPermission.Write) && !permissions.HasFlag(EAgentPermission.Read))
                problems.Add($"Model type {definition.Name} declares \"agentPermissions\": \"Write\" without \"Read\"; use \"Read, Write\".");
        }
        return problems;
    }

    /// <exception cref="InvalidOperationException">A type declares an invalid <c>agentPermissions</c>.</exception>
    public static void Verify(IModelLoader modelLoader)
    {
        var problems = Problems(modelLoader.GetEntityTypes());
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "The model's agent permissions are invalid:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
    }
}

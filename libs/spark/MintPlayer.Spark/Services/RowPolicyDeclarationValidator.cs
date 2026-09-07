using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Refuses a type whose rows are reachable by a well-known group but whose row policy nobody ever
/// stated.
/// </summary>
/// <remarks>
/// <para>
/// The population matters. For an ordinary group, "no row rule" is a safe default: the type-level
/// grant is itself the constraint, and someone had to be put in that group deliberately. The
/// well-known roles are the exception — <c>anonymous</c> is the public internet and
/// <c>authenticated</c> is every account that ever signs up — so for those two the row filter is the
/// only thing between the caller and the whole collection, and its absence is invisible.
/// </para>
/// <para>
/// <b>This does not refuse public data.</b> <c>ReportSecurityPosture</c> states the principle it is
/// built on: a genuinely public API is a policy decision an application is entitled to make, and
/// refusing to start over one would be wrong. So an application may still publish a whole
/// collection — it just has to say so, by implementing <see cref="ISparkOwnsRowSecurity"/> and
/// writing down why. What is refused is the case nobody decided.
/// </para>
/// <para>
/// Only row-returning actions count. A grant of <c>Edit</c> or a custom action to a well-known group
/// is a different question with different answers, and the write paths have their own gates.
/// </para>
/// </remarks>
internal static class RowPolicyDeclarationValidator
{
    /// <summary>The actions that hand rows to a caller, and therefore need a row policy behind them.</summary>
    private static readonly string[] RowReturningActions = ["Query", "Read"];

    /// <summary>
    /// One message per undeclared type, each naming the grant that exposed it. Empty means every
    /// well-known-group grant is accounted for.
    /// </summary>
    /// <param name="configuration">The loaded security model.</param>
    /// <param name="types">Every entity type in the model.</param>
    /// <param name="hasRowRule">Whether a type's actions class declares a row rule.</param>
    /// <param name="actionsFor">The actions instance for a type name, for the declaration check.</param>
    public static IReadOnlyList<string> Validate(
        SecurityConfiguration configuration,
        IReadOnlyCollection<EntityTypeDefinition> types,
        Func<EntityTypeDefinition, bool> hasRowRule,
        Func<EntityTypeDefinition, object?> actionsFor)
    {
        var wellKnownGroupIds = (configuration.WellKnown ?? [])
            .Where(kv => SparkWellKnownGroups.All.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

        if (wellKnownGroupIds.Count == 0)
            return [];

        var byName = types.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var right in configuration.Rights)
        {
            // A denial cannot expose anything, and a wildcard target names no single type to check.
            //
            // Note there is deliberately NO "GroupId is empty" guard. The conventional id for the
            // anonymous role is 00000000-0000-0000-0000-000000000000 — every app in this repository
            // uses it — so treating Guid.Empty as "unset" would skip precisely the grants that matter
            // most, and the gate would pass in silence on a fully public surface.
            if (right.IsDenied || string.IsNullOrEmpty(right.Resource))
                continue;
            if (!wellKnownGroupIds.TryGetValue(right.GroupId.ToString(), out var role))
                continue;

            var pattern = ResourcePattern.Parse(right.Resource);
            if (pattern.Target is "*" || !byName.TryGetValue(pattern.Target, out var type))
                continue;

            var grantsRows = SparkCombinedActions.Expand(pattern.Action)
                .Any(a => RowReturningActions.Contains(a, StringComparer.OrdinalIgnoreCase))
                || pattern.Action == "*";
            if (!grantsRows)
                continue;

            // A composed type is covered by its own declaration at execution time, and it has no
            // documents for a row rule to filter in the first place.
            if (SparkComposedQueries.IsComposed(type))
                continue;

            if (hasRowRule(type))
                continue;

            if (actionsFor(type) is ISparkOwnsRowSecurity { RowSecurityRationale.Length: > 0 })
                continue;

            if (!reported.Add(type.Name))
                continue;

            problems.Add(
                $"security.json grants '{right.Resource}' to the '{role}' role, so every " +
                $"{(role == SparkWellKnownGroups.Anonymous ? "visitor, signed in or not" : "signed-in caller")} " +
                $"can list rows of '{type.Name}' — but '{type.Name}Actions' declares no row rule, so nothing " +
                $"narrows which rows they see. Publishing the whole collection is a decision an application is " +
                $"entitled to make; leaving it undecided is not. Either override GetRowFilterAsync (or " +
                $"IsAllowedAsync) on '{type.Name}Actions', or make it implement ISparkOwnsRowSecurity and use " +
                $"RowSecurityRationale to record why every row is safe to publish.");
        }

        return problems;
    }
}

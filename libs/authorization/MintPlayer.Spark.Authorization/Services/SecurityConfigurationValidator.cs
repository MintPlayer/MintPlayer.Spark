using MintPlayer.Spark.Authorization.Models;

namespace MintPlayer.Spark.Authorization.Services;

/// <summary>
/// Refuses a <c>security.json</c> whose meaning cannot be trusted, at load time rather than at the
/// first request that depends on it.
/// <para>
/// Throwing here is deliberate and is <em>not</em> the same call as the security-posture summary,
/// which only logs. Malformed configuration means the file does not say what its author thinks it
/// says; a permissive-but-well-formed posture means it does, and refusing to start over a policy
/// decision an application is entitled to make would be wrong.
/// </para>
/// </summary>
internal static class SecurityConfigurationValidator
{
    internal const string Anonymous = "anonymous";
    internal const string Authenticated = "authenticated";

    private static readonly string[] RecognisedWellKnownKeys = [Anonymous, Authenticated];

    /// <summary>The token deleted in preview.60, kept here only so its use can be diagnosed.</summary>
    private const string RemovedEveryoneName = "Everyone";

    /// <summary>
    /// Read from <see cref="AccessControlService"/> rather than restated here, so the set the loader
    /// judges is provably the set the evaluator expands.
    /// </summary>
    private static IReadOnlyCollection<string> CombinedActionNames => AccessControlService.CombinedActionNames;

    public static void Validate(SecurityConfiguration config)
    {
        ValidateEveryoneIsGone(config);
        ValidateWellKnown(config);
        ValidateRights(config);
    }

    /// <summary>
    /// Two rules about the rights list, both about a file meaning something other than it looks like.
    /// </summary>
    private static void ValidateRights(SecurityConfiguration config)
    {
        var seenIds = new HashSet<Guid>();

        foreach (var right in config.Rights)
        {
            var slash = right.Resource?.IndexOf('/') ?? -1;
            if (right.Resource is null || slash <= 0 || slash == right.Resource.Length - 1)
            {
                throw new InvalidOperationException(
                    $"security.json declares a right with resource '{right.Resource}', which is not in "
                    + "the form '<action>/<target>' (for example 'QueryRead/Person'). It would match "
                    + "nothing.");
            }

            // Combined actions expand on the GRANT side only: IsAllowedAsync filters expansion to
            // non-denied rights, so a denial written 'EditNewDelete/Person' denies the literal string
            // 'EditNewDelete/Person' and nothing else — it denies nothing. Symmetric syntax,
            // asymmetric semantics, and the author's intent is unmistakably the opposite.
            var action = right.Resource[..slash];
            if (right.IsDenied && CombinedActionNames.Contains(action, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"security.json denies '{right.Resource}', but combined actions expand only when "
                    + "granting. This denial would match nothing and deny nothing. Write one denial "
                    + $"per action instead: {string.Join(", ", ExpandForMessage(action, right.Resource[(slash + 1)..]))}.");
            }

            if (right.Id != Guid.Empty && !seenIds.Add(right.Id))
            {
                throw new InvalidOperationException(
                    $"security.json declares two rights with id '{right.Id}'. Nothing reads the id "
                    + "today, so the duplicate is currently harmless — which is exactly why it should "
                    + "be fixed before something does.");
            }
        }
    }

    private static IEnumerable<string> ExpandForMessage(string action, string target)
        => AccessControlService.ExpandCombinedAction(action).Select(a => $"{a}/{target}");

    /// <summary>
    /// <c>Everyone</c> meant "the public internet", and nothing at the point of writing said so —
    /// which is the whole of #298. Renaming it would keep one token meaning that and hope a better
    /// word carried the warning; deleting it forces the author to type <c>anonymous</c>, which is
    /// self-documenting where it is written.
    /// <para>
    /// Only fires on a file that has not been migrated at all (no <c>wellKnown</c> block). Once the
    /// roles are declared by id, a group's <em>name</em> carries no meaning, so an application is
    /// free to have one displayed as "Everyone" — that inertness is the point of the change, and
    /// continuing to police the name afterwards would contradict it.
    /// </para>
    /// </summary>
    private static void ValidateEveryoneIsGone(SecurityConfiguration config)
    {
        if (config.WellKnown is { Count: > 0 })
            return;

        var offending = config.Groups.FirstOrDefault(g => g.Value.Translations.Values
            .Any(v => string.Equals(v, RemovedEveryoneName, StringComparison.OrdinalIgnoreCase)));

        if (string.IsNullOrEmpty(offending.Key))
            return;

        throw new InvalidOperationException(
            $"security.json declares a group named '{RemovedEveryoneName}' ({offending.Key}), which no "
            + "longer has any special meaning. Every right granted to it was granted to the public "
            + "internet.\n"
            + "Migrate by adding a \"wellKnown\" block naming the group ids that play each role:\n"
            + "  \"wellKnown\": { \"anonymous\": \"<group-id>\", \"authenticated\": \"<group-id>\" }\n"
            + "Then decide, per right, whether public access was intended. If it was, leave it on the "
            + "anonymous group. If it was not, MOVE it to the authenticated group — do not delete it, "
            + "because type-level rights gate row rules, so a deleted grant denies signed-in users too. "
            + "A right that both must keep becomes two grants.");
    }

    /// <summary>
    /// A <c>wellKnown</c> entry that does not resolve is worse than none: the role silently stops
    /// applying, and every grant to it stops taking effect with nothing to indicate it.
    /// </summary>
    private static void ValidateWellKnown(SecurityConfiguration config)
    {
        if (config.WellKnown is not { Count: > 0 } wellKnown)
            return;

        var seen = new Dictionary<Guid, string>();

        foreach (var (key, value) in wellKnown)
        {
            if (!RecognisedWellKnownKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"security.json declares an unknown well-known group '{key}'. The recognised keys "
                    + $"are {string.Join(" and ", RecognisedWellKnownKeys)}.");
            }

            if (!Guid.TryParse(value, out var id))
            {
                throw new InvalidOperationException(
                    $"security.json maps well-known group '{key}' to '{value}', which is not a group id.");
            }

            if (!config.Groups.Keys.Any(k => Guid.TryParse(k, out var g) && g == id))
            {
                throw new InvalidOperationException(
                    $"security.json maps well-known group '{key}' to '{value}', but no group with that "
                    + "id is declared. A role pointing at nothing grants nothing, silently.");
            }

            if (seen.TryGetValue(id, out var other))
            {
                throw new InvalidOperationException(
                    $"security.json maps both '{other}' and '{key}' to group '{value}'. They mean "
                    + "different sets of callers — anonymous includes everyone, authenticated only "
                    + "those who signed in — so one group cannot be both.");
            }

            seen[id] = key;
        }
    }
}

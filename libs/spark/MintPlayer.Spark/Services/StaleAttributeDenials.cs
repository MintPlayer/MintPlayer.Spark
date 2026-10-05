using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The stale-deny trap of attribute rights (PRD §5 Q13): a group restricts a verb on some attributes
/// of a type, and an attribute added later — mentioned by no attribute right — silently inherits the
/// type-level grant. Found here for the security-posture report; SPARK024 reports the same at build.
/// </summary>
/// <remarks>
/// A finding, never an error: leaving an attribute on the type-level grant is legitimate. The point is
/// that it should be so <em>on purpose</em>.
/// Only the attributes the model declares are counted: nothing else can reach the wire. SPARK024 counts
/// the same set (not the CLR type's properties), so build and runtime list the same attributes.
/// A group whose every deny for a verb and type the other well-known group repeats (anonymous ↔
/// authenticated: the attribute is hidden from everyone) is not reported, as in SPARK024 (#264, G-Q23).
/// </remarks>
internal static class StaleAttributeDenials
{
    internal sealed record Finding(
        Guid GroupId, string GroupName, string Verb, string EntityType,
        int RestrictedCount, int AttributeCount, IReadOnlyList<string> Unmentioned)
    {
        public string Message
            => $"Group '{GroupName}' restricts {Verb} on {RestrictedCount} of {AttributeCount} attributes of "
               + $"'{EntityType}'; {string.Join(", ", Unmentioned.Select(a => $"'{a}'"))} "
               + $"{(Unmentioned.Count == 1 ? "is" : "are")} still {Adjective(Verb)} through the type-level "
               + $"right — intended?";
    }

    /// <summary>What a verb lets the caller do with an attribute, for the message.</summary>
    internal static string Adjective(string verb) => verb.ToUpperInvariant() switch
    {
        "QUERY" => "queryable",
        "READ" => "readable",
        "EDIT" => "editable",
        "NEW" => "settable on create",
        _ => $"allowed for {verb}",
    };

    public static IReadOnlyList<Finding> Find(SecurityConfiguration config, IModelLoader model)
    {
        // (group, VERB, TYPE) → attributes denied / attributes mentioned at all, upper-cased.
        var denied = new Dictionary<(Guid, string, string), HashSet<string>>();
        var mentioned = new Dictionary<(Guid, string, string), HashSet<string>>();

        foreach (var right in config.Rights)
        {
            if (!SparkAttributeRights.TrySplit(right.Resource, out var action, out var type, out var attribute))
                continue;

            foreach (var verb in SparkCombinedActions.Expand(action))
            {
                if (!SparkAttributeRights.IsVerb(verb))
                    continue; // The validator refuses these; never report on them.

                Add(mentioned, (right.GroupId, verb.ToUpperInvariant(), type.ToUpperInvariant()), attribute);
                if (right.IsDenied)
                {
                    Add(denied, (right.GroupId, verb.ToUpperInvariant(), type.ToUpperInvariant()), attribute);
                    continue;
                }

                // A grant mentions what it implies (Read ⇒ Query), exactly as the index expands it.
                foreach (var implied in SparkRightImplications.Implied(verb))
                    Add(mentioned, (right.GroupId, implied.ToUpperInvariant(), type.ToUpperInvariant()), attribute);
            }
        }

        var counterparts = WellKnownCounterparts(config);
        var findings = new List<Finding>();
        foreach (var ((groupId, verb, type), deniedAttributes) in denied)
        {
            var definition = model.GetEntityTypeByName(type);
            if (definition is null || definition.Attributes.Length == 0)
                continue;

            // G-Q23: every deny repeated by the other well-known group hides the attribute from everyone.
            if (counterparts.TryGetValue(groupId, out var counterpart)
                && denied.TryGetValue((counterpart, verb, type), out var other)
                && deniedAttributes.All(other.Contains))
                continue;

            var seen = mentioned[(groupId, verb, type)];
            var unmentioned = definition.Attributes
                .Select(a => a.Name)
                .Where(name => !seen.Contains(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            if (unmentioned.Count == 0)
                continue;

            findings.Add(new Finding(
                groupId,
                GroupName(config, groupId),
                SparkAttributeRights.Verbs.First(v => string.Equals(v, verb, StringComparison.OrdinalIgnoreCase)),
                definition.Name,
                deniedAttributes.Count,
                definition.Attributes.Length,
                unmentioned));
        }

        return findings
            .OrderBy(f => f.GroupName, StringComparer.Ordinal)
            .ThenBy(f => f.EntityType, StringComparer.Ordinal)
            .ThenBy(f => f.Verb, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The <c>wellKnown</c> anonymous and authenticated group ids, each mapped to the other; empty unless both are declared.</summary>
    private static Dictionary<Guid, Guid> WellKnownCounterparts(SecurityConfiguration config)
    {
        var result = new Dictionary<Guid, Guid>();
        string? Id(string role) => config.WellKnown?
            .FirstOrDefault(kv => string.Equals(kv.Key, role, StringComparison.OrdinalIgnoreCase)).Value;

        if (!Guid.TryParse(Id(SparkWellKnownGroups.Anonymous), out var anonymous)
            || !Guid.TryParse(Id(SparkWellKnownGroups.Authenticated), out var authenticated)
            || anonymous == authenticated)
            return result;

        result[anonymous] = authenticated;
        result[authenticated] = anonymous;
        return result;
    }

    private static void Add(Dictionary<(Guid, string, string), HashSet<string>> map, (Guid, string, string) key, string attribute)
    {
        if (!map.TryGetValue(key, out var set))
            map[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(attribute);
    }

    private static string GroupName(SecurityConfiguration config, Guid groupId)
    {
        foreach (var (key, name) in config.Groups)
        {
            if (Guid.TryParse(key, out var id) && id == groupId)
                return name;
        }

        return groupId.ToString();
    }
}

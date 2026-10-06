using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Computes what an unauthenticated caller can reach, and the table of every effective right with
/// the layer it came from, from the composed <c>security.json</c> alone.
/// <para>
/// This is the mirror image of SPARK004. Middleware order is a property of the code and undetectable
/// at runtime, so it ships as an analyzer; the anonymous surface is a property of a hot-reloadable
/// JSON file that is not in the compilation and is not the file that ships, so it is trivially
/// computable at runtime and barely computable at build time. Startup is where it belongs.
/// </para>
/// </summary>
[Register(typeof(ISecurityPostureReporter), ServiceLifetime.Singleton)]
internal partial class SecurityPostureReporter : ISecurityPostureReporter
{
    [Inject] private readonly ISecurityConfigurationLoader configLoader;
    [Inject] private readonly IModelLoader modelLoader;

    public SecurityPosture Describe()
    {
        var config = configLoader.GetConfiguration();
        var warnings = new List<string>();

        // Info-level: the stale attribute-deny trap (PRD §5 Q13), for every group, not just anonymous.
        var notes = StaleAttributeDenials.Find(config, modelLoader).Select(f => f.Message).ToList();

        var anonymousGroupId = ResolveAnonymousGroupId(config);

        // The whole effective table (composition D4), not only the anonymous surface: the review point
        // for a library's rights is this table changing in the pull request that adds or updates it.
        var table = config.Rights.Select(r => Row(config, r, inert: false, anonymousGroupId)).OrderBy(r => r, RowOrder.Instance).ToList();
        var inert = config.InertRights.Select(r => Row(config, r, inert: true, null)).OrderBy(r => r, RowOrder.Instance).ToList();

        if (anonymousGroupId is null)
            return new SecurityPosture([], warnings, notes, table, inert);

        // A caller who has not signed in belongs to the anonymous group and to nothing else: group
        // membership otherwise comes from claims, and an unauthenticated principal carries none that
        // resolve. So the anonymous surface is exactly that group's rights.
        //
        // Expanded on both sides through the same table and implications the evaluator uses.
        // Reporting the literal strings would print one line for QueryReadEditNewDelete/Person
        // where five rights are granted, and — worse — would leave a right visible that a
        // combined denial takes away.
        var rights = config.Rights.Where(r => r.GroupId == anonymousGroupId).ToList();

        // Grants also carry what they entail (Read ⇒ Query), through the same
        // SparkRightImplications the evaluator's index consults; denials never do. A report that
        // expanded grants more narrowly than the evaluator would understate the anonymous
        // surface, which is the one way this baseline can lie.
        var denied = Expand(rights.Where(r => r.IsDenied && !r.IsImportant), withImplications: false);
        var importantDenied = Expand(rights.Where(r => r.IsDenied && r.IsImportant), withImplications: false);
        var granted = Expand(rights.Where(r => !r.IsDenied), withImplications: true);

        // Mirrors RightsDecision.Allows: an important grant survives an ordinary denial, an
        // important denial survives everything.
        var importantGranted = Expand(rights.Where(r => !r.IsDenied && r.IsImportant), withImplications: true);
        granted.ExceptWith(denied);
        granted.UnionWith(importantGranted);
        granted.ExceptWith(importantDenied);

        // An attribute-level grant never unlocks what its type right withholds (Q13), so one whose
        // type-level right is not reachable reaches nothing and must not be listed.
        granted.RemoveWhere(r => SparkAttributeRights.TrySplit(r, out var verb, out var type, out _)
                                 && !granted.Contains($"{verb}/{type}"));

        // No "floor, not ceiling" caveat is needed: wildcard rights are refused at load, so the
        // list is exactly the anonymous surface.
        return new SecurityPosture(
            granted.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList(),
            warnings,
            notes,
            table,
            inert);
    }

    /// <summary>One right as a table row. An inert right never resolved, so it shows its token.</summary>
    private static SecurityPostureRow Row(SecurityConfiguration config, Right right, bool inert, Guid? anonymousGroupId)
    {
        var token = right.Group is { } g && !Guid.TryParse(g, out _) ? g : null;
        string group;
        if (inert)
            group = right.Group ?? right.GroupId.ToString();
        else
        {
            var name = config.Groups.FirstOrDefault(e => Guid.TryParse(e.Key, out var id) && id == right.GroupId).Value
                       ?? right.GroupId.ToString();
            group = token is null ? name : $"{name} ({token})";
        }

        var effect = (right.IsDenied ? "deny" : "grant") + (right.IsImportant ? " important" : "");
        return new SecurityPostureRow(group, effect, right.Resource, right.Key, right.Layer ?? "app", !inert && right.GroupId == anonymousGroupId);
    }

    /// <summary>Group, resource, effect, key, layer: a reordering of security.json never reads as drift.</summary>
    private sealed class RowOrder : IComparer<SecurityPostureRow>
    {
        public static readonly RowOrder Instance = new();

        public int Compare(SecurityPostureRow? x, SecurityPostureRow? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            foreach (var (a, b) in new[] { (x.Group, y.Group), (x.Resource, y.Resource), (x.Effect, y.Effect), (x.Key, y.Key), (x.Layer, y.Layer) })
            {
                var c = StringComparer.OrdinalIgnoreCase.Compare(a, b);
                if (c == 0) c = StringComparer.Ordinal.Compare(a, b);
                if (c != 0) return c;
            }
            return 0;
        }
    }

    private static HashSet<string> Expand(IEnumerable<Right> rights, bool withImplications)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var right in rights)
        {
            var slash = right.Resource.IndexOf('/');
            if (slash <= 0)
                continue;

            var action = right.Resource[..slash];
            var target = right.Resource[(slash + 1)..];

            foreach (var expanded in SparkCombinedActions.Expand(action))
            {
                result.Add($"{expanded}/{target}");

                if (!withImplications)
                    continue;

                foreach (var implied in SparkRightImplications.Implied(expanded))
                    result.Add($"{implied}/{target}");
            }
        }

        return result;
    }

    private static Guid? ResolveAnonymousGroupId(SecurityConfiguration config)
    {
        foreach (var (key, value) in config.WellKnown ?? [])
        {
            if (string.Equals(key, SparkWellKnownGroups.Anonymous, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(value, out var id))
            {
                return id;
            }
        }

        return null;
    }
}

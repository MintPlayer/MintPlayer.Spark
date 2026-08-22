using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Computes what an unauthenticated caller can reach, from <c>security.json</c> alone.
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

    public SecurityPosture Describe()
    {
        var config = configLoader.GetConfiguration();
        var warnings = new List<string>();

        var anonymousGroupId = ResolveAnonymousGroupId(config);

        if (anonymousGroupId is null)
            return new SecurityPosture([], warnings);

        // A caller who has not signed in belongs to the anonymous group and to nothing else: group
        // membership otherwise comes from claims, and an unauthenticated principal carries none that
        // resolve. So the anonymous surface is exactly that group's rights.
        //
        // Expanded on both sides through the same table the evaluator uses. Reporting the literal
        // strings would print one line for QueryReadEditNewDelete/Person where five rights are
        // granted, and — worse — would leave a right visible that a combined denial takes away.
        var rights = config.Rights.Where(r => r.GroupId == anonymousGroupId).ToList();

        var denied = Expand(rights.Where(r => r.IsDenied && !r.IsImportant));
        var importantDenied = Expand(rights.Where(r => r.IsDenied && r.IsImportant));
        var granted = Expand(rights.Where(r => !r.IsDenied));

        // Mirrors RightsDecision.Allows: an important grant survives an ordinary denial, an
        // important denial survives everything.
        var importantGranted = Expand(rights.Where(r => !r.IsDenied && r.IsImportant));
        granted.ExceptWith(denied);
        granted.UnionWith(importantGranted);
        granted.ExceptWith(importantDenied);

        foreach (var wildcard in granted.Where(r => r.Contains(ResourcePattern.Wildcard)))
        {
            warnings.Add(
                $"The anonymous group holds the wildcard right '{wildcard}', so the list above is a "
                + "floor rather than a ceiling: it covers resources that do not exist yet, including "
                + "every entity type and custom action added later.");
        }

        return new SecurityPosture(
            granted.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList(),
            warnings);
    }

    private static HashSet<string> Expand(IEnumerable<Right> rights)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var right in rights)
        {
            var slash = right.Resource.IndexOf('/');
            if (slash <= 0)
                continue;

            var action = right.Resource[..slash];
            var target = right.Resource[(slash + 1)..];

            if (action == ResourcePattern.Wildcard)
            {
                result.Add(right.Resource);
                continue;
            }

            foreach (var expanded in SparkCombinedActions.Expand(action))
                result.Add($"{expanded}/{target}");
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

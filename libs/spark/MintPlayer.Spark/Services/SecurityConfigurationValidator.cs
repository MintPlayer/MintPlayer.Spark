using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

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
    /// <summary>The token deleted in preview.60, kept here only so its use can be diagnosed.</summary>
    private const string RemovedEveryoneName = "Everyone";

    public static void Validate(SecurityConfiguration config) => Validate(config, model: null);

    /// <param name="model">
    /// The loaded model, against which an attribute-level right's type and attribute are checked.
    /// Null skips only that lookup (the syntax of an attribute right is still checked) — the loader
    /// always passes it.
    /// </param>
    public static void Validate(SecurityConfiguration config, IModelLoader? model)
    {
        ValidateEveryoneIsGone(config);
        ValidateWellKnown(config);
        ValidateRights(config);
        ValidateAttributeRights(config, model);
    }

    /// <summary>
    /// <c>{verb}/{Type}/{Attr}</c> rights (PRD §5 Q12): a verb with an attribute form, a type the
    /// model declares (by name — resources name types by name, never by alias), and an attribute
    /// that type declares. Anything else is refused rather than left to match nothing: before
    /// attribute rights existed a three-segment right loaded and silently granted nothing, which is
    /// exactly the failure this validator exists to refuse.
    /// </summary>
    private static void ValidateAttributeRights(SecurityConfiguration config, IModelLoader? model)
    {
        foreach (var right in config.Rights)
        {
            if (!SparkAttributeRights.TrySplit(right.Resource, out var action, out var type, out var attribute))
                continue;

            if (type.Length == 0 || attribute.Length == 0 || attribute.Contains('/'))
            {
                throw new SparkSecurityConfigurationException(
                    $"security.json declares a right with resource '{right.Resource}', which is not in the "
                    + "form '<verb>/<Type>/<Attribute>' (for example 'Edit/Song/Lyrics'). An attribute of an "
                    + "AsDetail row is targeted on the row type ('Edit/Lyrics/Text'), never with a longer path.");
            }

            if (!SparkAttributeRights.IsAttributeAction(action))
            {
                throw new SparkSecurityConfigurationException(
                    $"security.json declares a right with resource '{right.Resource}', but '{action}' has no "
                    + $"attribute-level form. Only {string.Join(", ", SparkAttributeRights.Verbs)} and the combined "
                    + $"verbs made solely of them ({string.Join(", ", SparkAttributeRights.CombinedAttributeActions)}) "
                    + "take a third segment. Delete, custom actions and every other verb stay type-level: write "
                    + $"'{action}/{type}'.");
            }

            if (model is null)
                continue;

            var definition = model.GetEntityTypeByName(type);
            if (definition is null && SatelliteNamed(model, type) is { } satellite)
            {
                // A satellite type (a generated contribution or current type) whose model file the
                // next synchronize writes: the model is one build behind, so it is judged by its
                // class, like SPARK014 does at build time. Synchronize itself must be able to start
                // with these rights already granted.
                if (!SatelliteAttributes(satellite).Contains(attribute, StringComparer.OrdinalIgnoreCase))
                {
                    throw new SparkSecurityConfigurationException(
                        $"security.json declares a right with resource '{right.Resource}', but '{satellite.Name}' "
                        + $"declares no attribute '{attribute}' (its attributes: {string.Join(", ", SatelliteAttributes(satellite).Take(20))}). "
                        + "The right would match nothing.");
                }
                continue;
            }

            definition = definition
                ?? throw new SparkSecurityConfigurationException(
                    $"security.json declares a right with resource '{right.Resource}', but no persistent object "
                    + $"named '{type}' exists in {SparkAppData.Relative("Model")}. An attribute right must name the type by its "
                    + "name (not its alias, a query or a reserved target such as LookupReferences).");

            if (!definition.Attributes.Any(a => string.Equals(a.Name, attribute, StringComparison.OrdinalIgnoreCase)))
            {
                var known = string.Join(", ", definition.Attributes.Select(a => a.Name).Take(20));
                throw new SparkSecurityConfigurationException(
                    $"security.json declares a right with resource '{right.Resource}', but '{definition.Name}' "
                    + $"declares no attribute '{attribute}' (its attributes: {known}). The right would match "
                    + "nothing.");
            }
        }
    }

    /// <summary>
    /// The satellite type (<see cref="Abstractions.Model.SparkModelSatellites"/>) of a model type that
    /// is named <paramref name="name"/>, or null. Only satellites of types the model declares count:
    /// those are the ones synchronize writes model files for.
    /// </summary>
    private static Type? SatelliteNamed(IModelLoader model, string name)
    {
        foreach (var definition in model.GetEntityTypes() ?? Enumerable.Empty<Abstractions.EntityTypeDefinition>())
        {
            if (SparkTypeResolver.ResolveClrType(definition.ClrType) is not { } owner)
                continue;
            foreach (var satellite in Abstractions.Model.SparkModelSatellites.For(owner))
            {
                if (string.Equals(satellite.ModelType.Name, name, StringComparison.OrdinalIgnoreCase))
                    return satellite.ModelType;
            }
        }
        return null;
    }

    /// <summary>The public instance properties of a satellite type, but <c>Id</c>: the attributes its model file will hold.</summary>
    private static IEnumerable<string> SatelliteAttributes(Type type)
        => type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "Id")
            .Select(p => p.Name)
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Two rules about the rights list, both about a file meaning something other than it looks
    /// like — a wildcard included, because it silently grows to cover every type added later.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There used to be a third, rejecting a combined action in a denial, because expansion ran on
    /// the grant side only and such a denial denied nothing. Expansion is symmetric now — see
    /// <see cref="SparkCombinedActions"/> — so the shape it refused is the shape that works, and
    /// keeping the rule would refuse valid files.
    /// </para>
    /// <para>
    /// And one refusing two rights with one id, which became the keyed set (composition D4): the
    /// engine refuses a key stated twice in one layer, and a library's keys are namespaced by its
    /// alias, so the composed set cannot hold one twice.
    /// </para>
    /// </remarks>
    private static void ValidateRights(SecurityConfiguration config)
    {
        foreach (var right in config.Rights)
        {
            var slash = right.Resource?.IndexOf('/') ?? -1;
            if (right.Resource is null || slash <= 0 || slash == right.Resource.Length - 1)
            {
                throw new SparkSecurityConfigurationException(
                    $"security.json declares a right with resource '{right.Resource}', which is not in "
                    + "the form '<action>/<target>' (for example 'QueryRead/Person'). It would match nothing.");
            }

            if (right.Resource.Contains('*'))
            {
                throw new SparkSecurityConfigurationException(
                    $"security.json declares a right with resource '{right.Resource}', which uses the "
                    + "wildcard '*'. Wildcard rights are not supported: an access review has to be able "
                    + "to enumerate who can do what, and a wildcard also covers types and actions that "
                    + "do not exist yet. Name every target, and use a combined action to cover several "
                    + "actions at once — for example 'QueryReadEditNewDelete/Person' instead of "
                    + "'*/Person' (see SparkCombinedActions).");
            }
        }
    }

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

        var offending = config.Groups.FirstOrDefault(g => string.Equals(g.Value, RemovedEveryoneName, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(offending.Key))
            return;

        throw new SparkSecurityConfigurationException(
            // $$ so the JSON below keeps its literal braces; {{…}} is the interpolation hole.
            $$"""
            security.json declares a group named '{{RemovedEveryoneName}}' ({{offending.Key}}), which no longer has any special meaning. Every right granted to it was granted to the public internet.
            Migrate by adding a "wellKnown" block naming the group ids that play each role:
              "wellKnown": { "anonymous": "<group-id>", "authenticated": "<group-id>" }
            Then decide, per right, whether public access was intended. If it was, leave it on the anonymous group. If it was not, MOVE it to the authenticated group — do not delete it, because type-level rights gate row rules, so a deleted grant denies signed-in users too. A right that both must keep becomes two grants.
            """);
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
            if (!SparkWellKnownGroups.All.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                throw new SparkSecurityConfigurationException(
                    $"security.json declares an unknown well-known group '{key}'. The recognised keys "
                    + $"are {string.Join(" and ", SparkWellKnownGroups.All)}.");
            }

            if (!Guid.TryParse(value, out var id))
            {
                throw new SparkSecurityConfigurationException(
                    $"security.json maps well-known group '{key}' to '{value}', which is not a group id.");
            }

            if (!config.Groups.Keys.Any(k => Guid.TryParse(k, out var g) && g == id))
            {
                throw new SparkSecurityConfigurationException(
                    $"security.json maps well-known group '{key}' to '{value}', but no group with that "
                    + "id is declared. A role pointing at nothing grants nothing, silently.");
            }

            if (seen.TryGetValue(id, out var other))
            {
                throw new SparkSecurityConfigurationException(
                    $"security.json maps both '{other}' and '{key}' to group '{value}'. They mean "
                    + "different sets of callers — anonymous includes everyone, authenticated only "
                    + "those who signed in — so one group cannot be both.");
            }

            seen[id] = key;
        }
    }
}

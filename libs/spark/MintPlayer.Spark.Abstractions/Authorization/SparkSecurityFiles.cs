using MintPlayer.Spark.Layering;

namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>The composed <c>security.json</c>, and every reason it cannot be trusted (empty when it can).</summary>
/// <param name="Configuration">The effective configuration: resolved rights with their layer, the inert ones, groups and bindings.</param>
/// <param name="Problems">Guard-rail breaches, unresolved tokens and slots, and edits of library grants, one sentence each, naming the layer.</param>
public sealed record SparkSecurityComposition(SecurityConfiguration Configuration, IReadOnlyList<string> Problems);

/// <summary>
/// Composes <c>security.json</c> in layers at run time (composition D4): the rights each library in
/// <see cref="SparkLayerCatalog"/> ships, in layer order, then the application's file. The shared engine
/// does the work (<see cref="SparkKinds.Security"/>, <c>SparkSecurityLayers</c>), the same code the
/// security analyzer composes with.
/// </summary>
/// <remarks>
/// This reverses the position that a package cannot ship rights (decision log row 6). What keeps that
/// safe is the guard rails — a library only grants, only on what it ships, only to tokens and its own
/// slots — and the posture table, which lists every effective right with the layer it came from.
/// </remarks>
public static class SparkSecurityFiles
{
    /// <summary>The application layer's name in messages.</summary>
    public static string AppLayerName => SparkAppData.Relative(FileName);

    private const string FileName = "security.json";

    /// <summary>The application layer's path for a content root.</summary>
    public static string PathFor(string contentRootPath) => SparkAppData.Path(contentRootPath, FileName);

    /// <summary>
    /// The libraries among <paramref name="libraries"/> (<see langword="null"/>: the process's) that ship a
    /// <c>security.json</c> layer, in layer order, each with the first 12 hex digits of the SHA-256 of what
    /// it states, whitespace aside (composition D7): the identity the posture table records per layer.
    /// </summary>
    public static IReadOnlyList<(string Alias, string Assembly, string Hash)> Layers(IEnumerable<SparkLibrary>? libraries = null)
        => SparkLayerCatalog.Of(libraries ?? SparkLayerCatalog.Libraries, SparkLayerKinds.Security)
            .Select(x => (x.Library.Alias, x.Library.AssemblyName, Model.SparkLayerProvenance.Sha256Hex(Canonical(x.Layer.Json))[..12]))
            .ToList();

    private static string Canonical(string json)
    {
        try
        {
            return SparkJson.Write(SparkJson.Parse(json));
        }
        catch (SparkJsonException)
        {
            // Composing refuses it with its own message; the hash only has to be stable.
            return json;
        }
    }

    /// <summary>
    /// <paramref name="appJson"/> composed over <paramref name="libraries"/> (<see langword="null"/>: the
    /// process's, <see cref="SparkLayerCatalog.Libraries"/>).
    /// </summary>
    /// <param name="modelTypeNames">The composed model's type names, so a library's reserved target cannot claim one.</param>
    /// <exception cref="InvalidOperationException">A layer is not strict JSON, or a right is not an object with a string <c>key</c>, or states one twice.</exception>
    public static SparkSecurityComposition Compose(string appJson, IEnumerable<SparkLibrary>? libraries = null, IEnumerable<string>? modelTypeNames = null)
    {
        var layers = (libraries ?? SparkLayerCatalog.Libraries)
            .Select(l => new SparkSecurityLibrary(
                l.Alias,
                l.Layers.FirstOrDefault(x => x.Kind == SparkLayerKinds.Security)?.Json,
                SparkSecurityLayers.ModelTargets(l.Layers.Where(x => x.Kind == SparkLayerKinds.Model).Select(x => x.Json))))
            .ToList();

        var types = modelTypeNames is null ? null : new HashSet<string>(modelTypeNames, StringComparer.OrdinalIgnoreCase);
        var composed = SparkSecurityLayers.Compose(layers, appJson, AppLayerName, types);
        var problems = composed.Problems.Select(p => p.ToString()).ToList();

        var configuration = new SecurityConfiguration
        {
            Groups = composed.Groups.ToDictionary(g => g.Key, g => g.Value),
            WellKnown = composed.WellKnown?.ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase),
            Bindings = composed.Bindings?.ToDictionary(e => e.Key, e => e.Value.ToList(), StringComparer.OrdinalIgnoreCase),
            Libraries = composed.Libraries?.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
        };

        foreach (var grant in composed.Grants)
        {
            foreach (var id in grant.GroupIds)
            {
                // Resolve accepts only a Guid for a group named by id, and a token or slot resolves to
                // declared group ids; a wellKnown value that is no id is the validator's to report.
                if (!Guid.TryParse(id, out var groupId))
                {
                    problems.Add($"{grant.Library ?? AppLayerName}: right '{grant.Key}' ({grant.Resource}) resolves to '{id}', which is not a group id.");
                    continue;
                }
                configuration.Rights.Add(ToRight(grant, groupId));
            }
        }

        foreach (var grant in composed.Inert)
            configuration.InertRights.Add(ToRight(grant, Guid.Empty));

        return new SparkSecurityComposition(configuration, problems);
    }

    /// <summary>
    /// The group ids <paramref name="group"/> stands for in <paramref name="configuration"/>: a group id
    /// as itself, <c>@anonymous</c>/<c>@authenticated</c> through <c>wellKnown</c>, a slot through
    /// <c>bindings</c>. <see langword="null"/>, with <paramref name="problem"/> saying why, when it does
    /// not resolve. Moderation resolves its privileges' slots with it (grill Q6).
    /// </summary>
    public static IReadOnlyList<Guid>? ResolveGroup(SecurityConfiguration configuration, string group, out string? problem)
    {
        var ids = SparkSecurityLayers.Resolve(
            group,
            configuration.Groups,
            configuration.WellKnown,
            configuration.Bindings,
            out problem);
        if (ids is null) return null;

        var result = new List<Guid>();
        foreach (var id in ids)
        {
            if (!Guid.TryParse(id, out var parsed))
            {
                problem = $"names '{group}', which resolves to '{id}', not a group id.";
                return null;
            }
            result.Add(parsed);
        }
        return result;
    }

    private static Right ToRight(SparkSecurityGrant grant, Guid groupId) => new()
    {
        Key = grant.Key,
        Resource = grant.Resource,
        GroupId = groupId,
        Group = grant.Group,
        IsDenied = grant.IsDenied,
        IsImportant = grant.IsImportant,
        Layer = grant.Library,
    };
}

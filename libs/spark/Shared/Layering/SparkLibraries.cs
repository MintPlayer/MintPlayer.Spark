using System;
using System.Collections.Generic;
using System.Linq;

namespace MintPlayer.Spark.Layering;

/// <summary>
/// The kinds a library ships as layers (composition D1/D3), each named by its path under the
/// library's <c>App_Data</c>. The path decides the kind, so the build, the generator and the run time
/// cannot disagree on it.
/// </summary>
internal static class SparkLayerKinds
{
    public const string Actions = "actions";
    public const string Translations = "translations";
    public const string Model = "model";
    public const string Security = "security";
    public const string ProgramUnits = "programUnits";
    public const string Moderation = "moderation";

    /// <summary>
    /// The kind of the file at <paramref name="path"/> (relative to <c>App_Data</c>, either separator),
    /// or <see langword="null"/> for a file no kind ships: <c>culture.json</c> is the application's
    /// alone, and <c>modelHashes.json</c> is a gate output (D15).
    /// </summary>
    public static string? FromPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("Model/", StringComparison.OrdinalIgnoreCase))
        {
            var file = normalized.Substring("Model/".Length);
            return file.IndexOf('/') < 0 && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Model : null;
        }

        return normalized.ToLowerInvariant() switch
        {
            "actions.json" => Actions,
            "translations.json" => Translations,
            "security.json" => Security,
            "programunits.json" => ProgramUnits,
            "moderation.json" => Moderation,
            _ => null,
        };
    }
}

/// <summary>
/// The order library layers stack in (composition D2, grill Q3): <c>MintPlayer.Spark</c> first, each
/// library above every library it depends on, ordinal-alphabetical by assembly name between unrelated
/// ones. One function for the run time and the generators (D14), so both views agree.
/// </summary>
internal static class SparkLibraryOrder
{
    public const string CoreAssemblyName = "MintPlayer.Spark";

    /// <summary>
    /// <paramref name="libraries"/> in layer order. <paramref name="dependsOn"/> names the layered
    /// assemblies a library stacks above (as its generator recorded them); a name outside the set is
    /// ignored, and so is a cycle, which assembly references cannot form.
    /// </summary>
    public static List<T> Sort<T>(IEnumerable<T> libraries, Func<T, string> name, Func<T, IEnumerable<string>> dependsOn)
    {
        var remaining = libraries.ToList();
        var names = new HashSet<string>(remaining.Select(name), StringComparer.Ordinal);
        var pending = remaining.ToDictionary(
            name,
            l => new HashSet<string>(dependsOn(l).Where(d => names.Contains(d) && d != name(l)), StringComparer.Ordinal),
            StringComparer.Ordinal);

        var ordered = new List<T>(remaining.Count);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(l => pending[name(l)].Count == 0).ToList();
            var next = (ready.Count > 0 ? ready : remaining)
                .OrderBy(l => name(l) == CoreAssemblyName ? 0 : 1)
                .ThenBy(name, StringComparer.Ordinal)
                .First();

            ordered.Add(next);
            remaining.Remove(next);
            foreach (var set in pending.Values)
                set.Remove(name(next));
        }

        return ordered;
    }
}

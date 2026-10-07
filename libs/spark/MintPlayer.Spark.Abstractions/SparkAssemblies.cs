using System.Reflection;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// The assemblies that can declare something Spark reads by reflection: reserved verbs
/// (<c>[assembly: SparkReservedActions]</c>), and library layers (<c>[assembly: SparkLayer]</c>) when the
/// application recorded none (<see cref="SparkLayerCatalog"/>).
/// </summary>
public static class SparkAssemblies
{
    /// <summary>
    /// The loaded assemblies, plus — transitively — the references of every assembly that references
    /// a Spark vocabulary assembly (<see cref="VocabularyAssemblyNames"/>), since only those can declare
    /// anything or pull in one that does. References are followed because assemblies load lazily: a
    /// package the application references but has not touched yet (SoftDelete, before the first
    /// request) still counts.
    /// </summary>
    public static IEnumerable<Assembly> SparkAware()
    {
        var vocabulary = VocabularyAssemblyNames;
        var seen = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Assembly>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || assembly.GetName().Name is not { } name || !seen.TryAdd(name, assembly))
                continue;
            queue.Enqueue(assembly);
        }

        while (queue.Count > 0)
        {
            var assembly = queue.Dequeue();
            AssemblyName[] references;
            try { references = assembly.GetReferencedAssemblies(); }
            catch { continue; }

            if (!vocabulary.Contains(assembly.GetName().Name ?? string.Empty)
                && !references.Any(r => r.Name is { } referenced && vocabulary.Contains(referenced)))
                continue;

            foreach (var reference in references)
            {
                if (reference.Name is not { } name || seen.ContainsKey(name) || IsPlatform(name))
                    continue;
                try
                {
                    var loaded = Assembly.Load(reference);
                    seen[name] = loaded;
                    queue.Enqueue(loaded);
                }
                catch
                {
                    seen[name] = null!; // Not loadable here; nothing it declares can be asked for either.
                }
            }
        }

        return seen.Values.Where(a => a is not null);
    }

    /// <summary>
    /// The assemblies whose types a Spark-aware package uses: Abstractions, and the plain-SDK packages
    /// split off it (#388). A package such as Moderation.Abstractions references only Model, yet
    /// declares <c>[assembly: SparkReservedActions]</c>; keyed on Abstractions alone, its verbs would
    /// silently stop being reserved. Named through types, so a rename cannot go unnoticed.
    /// </summary>
    internal static readonly HashSet<string> VocabularyAssemblyNames = new(
        new[]
        {
            typeof(SparkAssemblies).Assembly,                                   // MintPlayer.Spark.Abstractions
            typeof(TranslatedString).Assembly,                                  // MintPlayer.Spark.Model
            typeof(GenerateIndexAttribute).Assembly,                            // MintPlayer.Spark.Attributes
        }.Select(a => a.GetName().Name!),
        StringComparer.OrdinalIgnoreCase);

    internal static bool IsPlatform(string name)
        => name.StartsWith("System", StringComparison.Ordinal)
           || name.StartsWith("Microsoft.", StringComparison.Ordinal)
           || name is "netstandard" or "mscorlib";
}

using System.Reflection;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// The assemblies that can declare something Spark reads by reflection: reserved verbs
/// (<c>[assembly: SparkReservedActions]</c>) and library actions (<c>[assembly: SparkActions]</c>).
/// </summary>
public static class SparkAssemblies
{
    /// <summary>
    /// The loaded assemblies, plus — transitively — the references of every assembly that references
    /// <c>MintPlayer.Spark.Abstractions</c>, since only those can declare anything or pull in one that
    /// does. References are followed because assemblies load lazily: a package the application
    /// references but has not touched yet (SoftDelete, before the first request) still counts.
    /// </summary>
    public static IEnumerable<Assembly> SparkAware()
    {
        var abstractionsName = typeof(SparkAssemblies).Assembly.GetName().Name;
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

            if (!string.Equals(assembly.GetName().Name, abstractionsName, StringComparison.OrdinalIgnoreCase)
                && !references.Any(r => string.Equals(r.Name, abstractionsName, StringComparison.OrdinalIgnoreCase)))
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

    private static bool IsPlatform(string name)
        => name.StartsWith("System", StringComparison.Ordinal)
           || name.StartsWith("Microsoft.", StringComparison.Ordinal)
           || name is "netstandard" or "mscorlib";
}

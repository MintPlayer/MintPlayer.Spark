using MintPlayer.Spark.Abstractions;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Resolves the English seed for an attribute's <c>description</c> from C# (#348): an explicit
/// <see cref="DescriptionAttribute"/> on the property wins, else the <c>///</c> summary the
/// <c>AttributeDescriptionsGenerator</c> compiled into the declaring assembly as
/// <see cref="SparkAttributeDescriptionAttribute"/> rows, else nothing.
/// </summary>
/// <remarks>
/// Rows are read per assembly, once, via <see cref="Assembly.GetCustomAttributes(Type, bool)"/> — there is
/// no file to locate. A Release-built assembly has no rows (the attribute is <c>[Conditional("DEBUG")]</c>)
/// and is indistinguishable from one whose properties carry no summaries; the one info line printed for
/// such an assembly is what makes a Release-configured synchronize diagnosable.
/// </remarks>
internal sealed class AttributeDescriptionCatalog
{
    private readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<(Type Type, string Property), string>> byAssembly = new();
    private readonly Action<string> log;

    public AttributeDescriptionCatalog() : this(Console.WriteLine) { }

    public AttributeDescriptionCatalog(Action<string> log) => this.log = log;

    /// <summary>The English text C# provides for <paramref name="property"/>, or <see langword="null"/> when it provides none.</summary>
    public string? Seed(PropertyInfo property)
    {
        var explicitText = property.GetCustomAttribute<DescriptionAttribute>(inherit: true)?.Description?.Trim();
        if (!string.IsNullOrEmpty(explicitText))
            return explicitText;

        var declaringType = property.DeclaringType;
        if (declaringType is null)
            return null;

        // The generator spells generic types unbound (typeof(Box<>)); reflection hands us the
        // constructed one (Box<int>) when the entity closes it.
        if (declaringType.IsGenericType && !declaringType.IsGenericTypeDefinition)
            declaringType = declaringType.GetGenericTypeDefinition();

        var rows = byAssembly.GetOrAdd(declaringType.Assembly, Load);
        return rows.TryGetValue((declaringType, property.Name), out var summary) ? summary : null;
    }

    private IReadOnlyDictionary<(Type, string), string> Load(Assembly assembly)
    {
        var rows = assembly
            .GetCustomAttributes(typeof(SparkAttributeDescriptionAttribute), inherit: false)
            .Cast<SparkAttributeDescriptionAttribute>()
            .GroupBy(a => (a.Type, a.Property))
            .ToDictionary(g => g.Key, g => g.First().Summary);

        if (rows.Count == 0)
        {
            log($"No attribute descriptions found in {assembly.GetName().Name}: either its public properties " +
                "carry no /// summaries, or it was built in Release (descriptions are compiled in DEBUG builds only).");
        }

        return rows;
    }
}

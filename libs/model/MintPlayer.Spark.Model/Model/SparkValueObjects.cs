using System.Collections.Concurrent;

namespace MintPlayer.Spark.Abstractions.Model;

/// <summary>
/// Which embedded types carry a generated row key, and how to read it.
/// </summary>
/// <remarks>
/// Populated by generated module initializers, one per assembly, so the runtime sees the union of
/// everything loaded. That is what lets an element type declared in one assembly and used from
/// another be keyed without either side knowing about the other — the case a compile-time-only
/// answer cannot reach, because a generator may only emit into its own compilation.
/// <para>
/// The accessor is emitted as a typed lambda rather than resolved from a property name. Reading a
/// key happens once per row per save, and <c>Type.GetProperty</c> is the one genuinely expensive
/// part of reflection; <c>typeof</c> and a <c>Type</c>-keyed lookup are not.
/// </para>
/// </remarks>
public static class SparkValueObjects
{
    private static readonly ConcurrentDictionary<Type, (string PropertyName, Func<object, string?> Read)> Accessors = new();

    /// <summary>
    /// Declares that <paramref name="type"/> carries a row key held by <paramref name="propertyName"/>
    /// and readable by <paramref name="accessor"/>. Called by generated code; idempotent, because a
    /// module initializer may run more than once across load contexts.
    /// </summary>
    /// <remarks>
    /// ⚠️ The <em>name</em> is not redundant beside the accessor. The accessor reads a key; the name
    /// is what lets the mapper write one back. A <c>[ValueKey]</c> need not be called <c>Id</c>, and
    /// before the name was registered the mapper round-tripped the key through a hard-coded
    /// <c>Id</c> property — so a key named anything else silently never came back, making every save
    /// of that collection look like a delete of every row plus a create of its replacement.
    /// </remarks>
    public static void Register(Type type, string propertyName, Func<object, string?> accessor)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrEmpty(propertyName);
        ArgumentNullException.ThrowIfNull(accessor);
        Accessors[type] = (propertyName, accessor);
    }

    /// <summary>
    /// Whether rows of <paramref name="type"/> can be matched across a save.
    /// </summary>
    /// <remarks>
    /// ⚠️ A caller deciding whether to <em>enforce</em> something must treat <see langword="false"/>
    /// as "cannot judge", never as "nothing to check". An unregistered type is indistinguishable
    /// from one whose generator never ran, and both mean the same thing: the comparison this answer
    /// feeds is not trustworthy.
    /// </remarks>
    public static bool IsKeyed(Type type) => Accessors.ContainsKey(type);

    /// <summary>
    /// Reads <paramref name="row"/>'s key, or <see langword="null"/> when its type carries none.
    /// </summary>
    public static string? GetKey(object row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Accessors.TryGetValue(row.GetType(), out var entry) ? entry.Read(row) : null;
    }

    /// <summary>
    /// The name of <paramref name="type"/>'s key property, or <see langword="null"/> when the type
    /// is not a registered value object. Used to write a key back, which the accessor cannot do.
    /// </summary>
    public static string? GetKeyPropertyName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Accessors.TryGetValue(type, out var entry) ? entry.PropertyName : null;
    }

    /// <summary>Every registered type, for the startup gate to check the model against.</summary>
    public static IReadOnlyCollection<Type> RegisteredTypes => (IReadOnlyCollection<Type>)Accessors.Keys;
}

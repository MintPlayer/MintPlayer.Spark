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
    private static readonly ConcurrentDictionary<Type, Func<object, string?>> Accessors = new();

    /// <summary>
    /// Declares that <paramref name="type"/> carries a row key readable by <paramref name="accessor"/>.
    /// Called by generated code; idempotent, because a module initializer may run more than once
    /// across load contexts.
    /// </summary>
    public static void Register(Type type, Func<object, string?> accessor)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(accessor);
        Accessors[type] = accessor;
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
        return Accessors.TryGetValue(row.GetType(), out var accessor) ? accessor(row) : null;
    }

    /// <summary>Every registered type, for the startup gate to check the model against.</summary>
    public static IReadOnlyCollection<Type> RegisteredTypes => (IReadOnlyCollection<Type>)Accessors.Keys;
}

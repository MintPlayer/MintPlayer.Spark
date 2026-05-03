using System.Reflection;

namespace MintPlayer.Spark.Abstractions.Reflection;

/// <summary>
/// Cached convenience wrappers over the most common <see cref="Type"/> reflection
/// lookups, all backed by <see cref="ReflectionCache"/>. These are pure sugar — every
/// method composes against <see cref="ReflectionCache.GetOrAdd{TValue}(Type, Func{Type, TValue})"/>
/// or its overloads. The cache primitive itself stays domain-agnostic.
/// <para>
/// Use these whenever you'd otherwise call <see cref="Type.GetProperty(string, BindingFlags)"/>
/// or <see cref="Type.GetProperties(BindingFlags)"/> in a hot path. They scope to public
/// instance members, which matches every existing reflection call site in Spark.
/// </para>
/// </summary>
public static class ReflectedTypeExtensions
{
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    /// <summary>
    /// Returns <c>type.GetProperties(Public | Instance)</c>, cached per <see cref="Type"/>.
    /// </summary>
    public static PropertyInfo[] GetCachedProperties(this Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ReflectionCache.GetOrAdd<PropertyInfo[]>(type, static t => t.GetProperties(PublicInstance));
    }

    /// <summary>
    /// Returns <c>type.GetProperty(name, Public | Instance)</c>, cached per
    /// <c>(Type, name)</c>. <c>null</c> results are cached too (negative caching),
    /// so a known-missing property doesn't trigger a fresh reflection lookup on every
    /// call.
    /// </summary>
    public static PropertyInfo? GetCachedProperty(this Type type, string name)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(name);
        return ReflectionCache.GetOrAdd<PropertyInfo?>(
            $"prop|{type.FullName ?? type.Name}|{name}",
            () => type.GetProperty(name, PublicInstance));
    }

    /// <summary>
    /// Returns <see cref="MemberInfo.GetCustomAttribute{T}"/>, cached per
    /// <see cref="MemberInfo"/>. Includes negative caching for "no such attribute on
    /// this member".
    /// </summary>
    public static TAttribute? GetCachedCustomAttribute<TAttribute>(this MemberInfo member)
        where TAttribute : Attribute
    {
        ArgumentNullException.ThrowIfNull(member);
        var key = $"attr|{typeof(TAttribute).FullName}|{member.DeclaringType?.FullName}|{member.Name}";
        return ReflectionCache.GetOrAdd<TAttribute?>(key, member.GetCustomAttribute<TAttribute>);
    }

    /// <summary>
    /// Reads <c>Task&lt;T&gt;.Result</c> reflectively using a cached
    /// <see cref="PropertyInfo"/> + compiled getter. Use this when a non-generic
    /// <see cref="Task"/> reference was produced via reflection (e.g.
    /// <c>MethodInfo.Invoke</c> on a <c>Task&lt;T&gt;</c>-returning method) and you
    /// need to extract the result without paying for fresh reflection on every call.
    /// The task <strong>must already be completed</strong> — this method does not
    /// await; it just reads the <c>Result</c> property.
    /// </summary>
    public static object? GetCompletedTaskResult(this Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var prop = task.GetType().GetCachedProperty("Result");
        return prop is not null ? AccessorCache.GetGetter(prop)(task) : null;
    }
}

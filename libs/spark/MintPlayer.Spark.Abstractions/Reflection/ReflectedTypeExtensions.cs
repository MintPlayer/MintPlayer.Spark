using System.Reflection;

namespace MintPlayer.Spark.Abstractions.Reflection;

/// <summary>
/// Cached convenience wrappers over the most common <see cref="Type"/> reflection
/// lookups, all backed by <see cref="ReflectionCache"/>'s identity-keyed tier. These
/// are pure sugar — every method composes against the cache primitive with the natural
/// runtime-identity key (Type, (Type, name), (MemberInfo, attrType)).
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
    /// Returns <c>type.GetProperties(Public | Instance)</c>, cached per <see cref="Type"/>
    /// instance.
    /// </summary>
    public static PropertyInfo[] GetCachedProperties(this Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ReflectionCache.GetOrAdd<Type, PropertyInfo[]>(type, static t => t.GetProperties(PublicInstance));
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
        return ReflectionCache.GetOrAdd<(Type Type, string Name), PropertyInfo?>(
            (type, name),
            static k => k.Type.GetProperty(k.Name, PublicInstance));
    }

    /// <summary>
    /// Returns <see cref="MemberInfo.GetCustomAttribute{T}"/>, cached per
    /// <c>(MemberInfo, attribute Type)</c>. Includes negative caching for "no such
    /// attribute on this member".
    /// </summary>
    public static TAttribute? GetCachedCustomAttribute<TAttribute>(this MemberInfo member)
        where TAttribute : Attribute
    {
        ArgumentNullException.ThrowIfNull(member);
        return ReflectionCache.GetOrAdd<(MemberInfo Member, Type AttrType), TAttribute?>(
            (member, typeof(TAttribute)),
            static k => (TAttribute?)k.Member.GetCustomAttribute(k.AttrType));
    }

    /// <summary>
    /// The property-level <c>[Breadcrumb]</c>-marked property of <paramref name="type"/>, or
    /// <c>null</c>. A raw member scan on purpose — the sanctioned marker shape is a computed
    /// property carrying <c>[IgnoreProperty]</c> (persisted, hidden from the model), which the
    /// model filters would wrongly exclude. Ordinal-min name wins when several are marked,
    /// mirroring the source generator's rule. Runtime twin of
    /// <c>SparkModelSymbols.GetBreadcrumbProperties</c> — keep the two in step.
    /// </summary>
    public static PropertyInfo? GetBreadcrumbProperty(this Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ReflectionCache.GetOrAdd<(string Op, Type Type), PropertyInfo?>(
            ("BreadcrumbMarkedProperty", type),
            static k => k.Type.GetCachedProperties()
                .Where(p => p.CanRead && p.GetCachedCustomAttribute<BreadcrumbAttribute>() is not null)
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .FirstOrDefault());
    }

    /// <summary>
    /// Returns the properties of <paramref name="type"/> that take part in the Spark
    /// model, i.e. <see cref="GetCachedProperties"/> filtered by
    /// <see cref="IsSparkModelProperty"/>.
    /// <para>
    /// This is the single definition of "is this property part of the model" — use it anywhere
    /// that answer matters (model synchronization, include resolution) so the rule cannot drift
    /// between call sites. It is <b>not</b> the definition of "may Spark write this": that is
    /// <see cref="GetSparkWritableProperties"/>, and the distinction is load-bearing for
    /// replication's write authorization.
    /// </para>
    /// </summary>
    public static IEnumerable<PropertyInfo> GetSparkModelProperties(this Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.GetCachedProperties().Where(IsSparkModelProperty);
    }

    /// <summary>
    /// Whether <paramref name="property"/> takes part in the Spark model: it is not the
    /// document id, it is readable, and it is not marked <see cref="IgnorePropertyAttribute"/>.
    /// <para>
    /// Readable is enough — a get-only computed property is a legitimate model attribute, surfaced
    /// read-only. Ask <see cref="IsSparkWritableProperty"/> instead when the question is whether a
    /// value may be written back; the two are deliberately different questions.
    /// </para>
    /// </summary>
    public static bool IsSparkModelProperty(this PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.Name != "Id"
            && property.CanRead
            // An indexer is not a field of the entity — it needs arguments to produce a value, so
            // there is nothing for an attribute to read. Reflection reports it as a property named
            // "Item", which would otherwise surface as an attribute of that name.
            && property.GetIndexParameters().Length == 0
            && !property.IsIgnoredForSparkModel();
    }

    /// <summary>
    /// Returns the properties of <paramref name="type"/> that Spark may <b>write</b> to, i.e.
    /// <see cref="GetCachedProperties"/> filtered by <see cref="IsSparkWritableProperty"/>.
    /// </summary>
    public static IEnumerable<PropertyInfo> GetSparkWritableProperties(this Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.GetCachedProperties().Where(IsSparkWritableProperty);
    }

    /// <summary>
    /// Whether <paramref name="property"/> may be written by Spark: a model property that also has
    /// a setter.
    /// <para>
    /// Separate from <see cref="IsSparkModelProperty"/> on purpose. "Appears in the model" and "may
    /// be written" used to be the same predicate, and widening that one predicate to admit get-only
    /// computed properties would have silently extended replication's write-authorization list
    /// (<c>SyncActionInterceptor.GetPropertyNames</c>) to properties that cannot be written at all.
    /// Callers deciding what to <i>display</i> want the model question; callers deciding what to
    /// <i>accept</i> want this one.
    /// </para>
    /// </summary>
    public static bool IsSparkWritableProperty(this PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.IsSparkModelProperty() && property.CanWrite;
    }

    /// <summary>
    /// Whether <paramref name="property"/> carries <see cref="IgnorePropertyAttribute"/>.
    /// <para>
    /// Prefer <see cref="IsSparkModelProperty"/>. This narrower check exists for callers
    /// that must answer "was this deliberately excluded?" on its own — notably the
    /// entity/projection union in model synchronization, where an exclusion on either
    /// side has to veto a property the other side still declares.
    /// </para>
    /// </summary>
    public static bool IsIgnoredForSparkModel(this PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.GetCachedCustomAttribute<IgnorePropertyAttribute>() is not null;
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


using System.Collections.Concurrent;

namespace MintPlayer.Spark.Abstractions.Reflection;

/// <summary>
/// Process-wide memoization primitive for reflection (and other) lookups whose
/// result is immutable for the lifetime of the AppDomain. Two-tier:
/// <list type="bullet">
///   <item><c>GetOrAdd&lt;TOwner, TValue&gt;(string, Func&lt;TValue&gt;)</c> —
///   per-type cache. Each <typeparamref name="TOwner"/> gets its own dictionary
///   via generic-static specialization, so keys never collide across types.</item>
///   <item><c>GetOrAdd&lt;TValue&gt;(string, Func&lt;TValue&gt;)</c> —
///   global string-keyed cache for cross-type lookups.</item>
///   <item><c>GetOrAdd&lt;TValue&gt;(Type, Func&lt;Type, TValue&gt;)</c> —
///   global Type-keyed cache, avoids <c>type.FullName</c> boilerplate at call
///   sites.</item>
/// </list>
/// Backed by <see cref="ConcurrentDictionary{TKey,TValue}"/> + <see cref="Lazy{T}"/>
/// with <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>: lock-free reads,
/// factory runs exactly once per key under contention, no TOCTOU window.
/// <para>
/// Entries live for the lifetime of the AppDomain — there is no eviction. Reflection
/// metadata cannot change at runtime, so this is correct.
/// </para>
/// <para>
/// <strong>Exception caching.</strong> If a factory throws, the exception is captured
/// by <see cref="Lazy{T}"/> and re-thrown on every subsequent access for that key.
/// This is the correct behavior for deterministic reflection failures (e.g. a missing
/// member that genuinely doesn't exist). Callers that need transient-failure
/// semantics should not use this cache.
/// </para>
/// <para>
/// <strong>Null caching.</strong> Factories may return <c>null</c>; the null is
/// cached and returned on subsequent calls (negative caching). This is the intended
/// shape for "lookup may genuinely have no result" cases such as
/// <c>Type? FindActionsType(string name)</c>.
/// </para>
/// </summary>
public static class ReflectionCache
{
    private static class PerType<TOwner>
    {
        // ReSharper disable once StaticMemberInGenericType
        // — intentional: one dictionary per closed TOwner.
        internal static readonly ConcurrentDictionary<string, Lazy<object?>> Cache = new();
    }

    private static readonly ConcurrentDictionary<string, Lazy<object?>> globalCache = new();
    // Keyed on (Type, ValueType) so distinct call sites that cache different things per
    // Type don't collide. Without the value-type discriminator, two callers asking for
    // typeof(Foo) with different TValue would share an entry and the second would get
    // the first's cached object, hitting an InvalidCastException at the boundary.
    private static readonly ConcurrentDictionary<(Type Key, Type ValueType), Lazy<object?>> typeKeyedCache = new();

    /// <summary>
    /// Per-type tier. Returns the cached value for <paramref name="key"/> in the
    /// dictionary owned by <typeparamref name="TOwner"/>, computing it via
    /// <paramref name="factory"/> on first access.
    /// </summary>
    public static TValue GetOrAdd<TOwner, TValue>(string key, Func<TValue> factory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        var lazy = PerType<TOwner>.Cache.GetOrAdd(
            key,
            _ => new Lazy<object?>(() => factory(), LazyThreadSafetyMode.ExecutionAndPublication));
        return (TValue)lazy.Value!;
    }

    /// <summary>
    /// Global string-keyed tier. Use when the cache key is naturally a string and
    /// not bound to one owning type (assembly scans, name → Type maps, …).
    /// <para>
    /// <strong>Keyspace convention.</strong> All callers share one global dictionary,
    /// so keys must carry a unique namespace prefix to avoid silent collisions across
    /// unrelated call sites. Established prefixes in Spark: <c>prop|</c> (property
    /// lookups), <c>attr|</c> (custom-attribute reads), <c>get|</c>/<c>set|</c>
    /// (compiled accessors), <c>resolveType|</c> (Type.GetType + assembly walk),
    /// <c>sessionLoadAsync|</c> (closed generic <c>LoadAsync&lt;T&gt;</c> MethodInfos).
    /// New call sites should pick a fresh, descriptive prefix and document it where
    /// the key is constructed.
    /// </para>
    /// </summary>
    public static TValue GetOrAdd<TValue>(string key, Func<TValue> factory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        var lazy = globalCache.GetOrAdd(
            key,
            _ => new Lazy<object?>(() => factory(), LazyThreadSafetyMode.ExecutionAndPublication));
        return (TValue)lazy.Value!;
    }

    /// <summary>
    /// Global Type-keyed tier. Convenience overload for "given any
    /// <see cref="Type"/>, give me its X" lookups; saves callers from
    /// stringifying <c>type.FullName</c>.
    /// </summary>
    public static TValue GetOrAdd<TValue>(Type key, Func<Type, TValue> factory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        var lazy = typeKeyedCache.GetOrAdd(
            (key, typeof(TValue)),
            entry => new Lazy<object?>(() => factory(entry.Key), LazyThreadSafetyMode.ExecutionAndPublication));
        return (TValue)lazy.Value!;
    }

    /// <summary>
    /// Test-only helper: clears the global tiers. Does not (and cannot) clear
    /// per-type tiers — those are owned by <see cref="PerType{TOwner}"/> generic
    /// statics and have AppDomain lifetime by design.
    /// </summary>
    internal static void ClearGlobalForTests()
    {
        globalCache.Clear();
        typeKeyedCache.Clear();
    }

    /// <summary>Diagnostic — total entries across the two global tiers.</summary>
    internal static int GlobalCount => globalCache.Count + typeKeyedCache.Count;
}

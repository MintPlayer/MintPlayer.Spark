using System.Collections.Concurrent;

namespace MintPlayer.Spark.Abstractions.Reflection;

/// <summary>
/// Process-wide memoization primitive for reflection (and other) lookups whose
/// result is immutable for the lifetime of the AppDomain. Three tiers:
/// <list type="bullet">
///   <item><c>GetOrAdd&lt;TOwner, TValue&gt;(string, Func&lt;TValue&gt;)</c> —
///   per-type cache. Each <typeparamref name="TOwner"/> gets its own dictionary
///   via generic-static specialization, so keys never collide across owners.</item>
///   <item><c>GetOrAdd&lt;TValue&gt;(string, Func&lt;TValue&gt;)</c> —
///   global string-keyed cache for cross-type lookups whose natural key is a
///   string the caller controls (e.g. user-supplied CLR type names).</item>
///   <item><c>GetOrAdd&lt;TKey, TValue&gt;(TKey, Func&lt;TKey, TValue&gt;)</c> —
///   identity-keyed cache. Pass any <see cref="Type"/>, <see cref="System.Reflection.PropertyInfo"/>,
///   <see cref="System.Reflection.MemberInfo"/>, or <c>ValueTuple</c> thereof
///   directly — no string composition, no FullName ambiguity, equality is whatever
///   <typeparamref name="TKey"/> defines.</item>
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

    /// <summary>
    /// One dictionary per closed <typeparamref name="TKey"/> via generic-static
    /// specialization. The inner key tuple includes <c>typeof(TValue)</c> so two
    /// callers using the same <typeparamref name="TKey"/> shape but caching different
    /// value types don't collide on a shared slot and trigger
    /// <see cref="InvalidCastException"/> at the unbox boundary.
    /// </summary>
    private static class TypedKey<TKey> where TKey : notnull
    {
        // ReSharper disable once StaticMemberInGenericType
        internal static readonly ConcurrentDictionary<(TKey Key, Type ValueType), Lazy<object?>> Cache = new();
    }

    private static readonly ConcurrentDictionary<string, Lazy<object?>> globalCache = new();

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
    /// Global string-keyed tier. Use when the cache key is naturally a string the
    /// caller controls — typically a user-supplied CLR type name or other free-form
    /// identifier. For Type / PropertyInfo / MemberInfo lookups, prefer the
    /// identity-keyed tier (<see cref="GetOrAdd{TKey,TValue}(TKey, Func{TKey, TValue})"/>) —
    /// it sidesteps every <see cref="Type.FullName"/> / <see cref="Type.AssemblyQualifiedName"/>
    /// ambiguity by keying on runtime identity.
    /// <para>
    /// <strong>Keyspace convention.</strong> All callers of this tier share one
    /// dictionary, so keys must carry a unique namespace prefix to avoid silent
    /// collisions across unrelated call sites (e.g. <c>resolveType|...</c>,
    /// <c>clrType|...</c>). New call sites should pick a fresh, descriptive prefix
    /// and document it where the key is constructed.
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
    /// Identity-keyed tier. Pass any <typeparamref name="TKey"/> with correct
    /// <see cref="object.Equals(object?)"/> + <see cref="object.GetHashCode"/> semantics —
    /// <see cref="Type"/>, <see cref="System.Reflection.PropertyInfo"/>,
    /// <see cref="System.Reflection.MemberInfo"/>, or <c>ValueTuple</c> compositions of
    /// those — and the cache uses runtime identity, not a stringified surrogate.
    /// <para>
    /// One dictionary exists per closed <typeparamref name="TKey"/> via generic-static
    /// specialization. Within a dictionary, entries are discriminated on
    /// <c>typeof(TValue)</c> so two callers reusing the same key shape for different
    /// value types stay isolated.
    /// </para>
    /// </summary>
    public static TValue GetOrAdd<TKey, TValue>(TKey key, Func<TKey, TValue> factory)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        var lazy = TypedKey<TKey>.Cache.GetOrAdd(
            (key, typeof(TValue)),
            entry => new Lazy<object?>(() => factory(entry.Key), LazyThreadSafetyMode.ExecutionAndPublication));
        return (TValue)lazy.Value!;
    }

    /// <summary>
    /// Test-only helper: clears the global string-keyed tier. The per-<typeparamref name="TOwner"/>
    /// and identity-keyed tiers are generic-static-specialized and have AppDomain lifetime
    /// by design — tests that exercise them must use unique TOwner/TKey marker types per case.
    /// </summary>
    internal static void ClearGlobalForTests()
    {
        globalCache.Clear();
    }

    /// <summary>Diagnostic — entries in the global string-keyed tier.</summary>
    internal static int GlobalCount => globalCache.Count;
}

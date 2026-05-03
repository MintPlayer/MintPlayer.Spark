# PRD: ReflectionCache — Generic Any-Use Memoization Primitive

| | |
|---|---|
| **Version** | 1.0 |
| **Date** | 2026-05-03 |
| **Status** | Proposed |
| **Owner** | MintPlayer |
| **Package** | `MintPlayer.Spark` |
| **Issue** | [#151](https://github.com/MintPlayer/MintPlayer.Spark/issues/151) |
| **Reference** | `C:\Repos\CronosCore\CronosCore.RavenDB\ReferenceObject\ReflectionCache.cs` |

---

## 1. Problem Statement

Spark performs the same reflection lookups repeatedly on hot request paths. None of the heavy lookups are memoized today; each call walks `Type.GetProperties`, `GetCustomAttribute<T>`, `Assembly.GetTypes`, etc., from scratch. Reflection results are immutable for the lifetime of the AppDomain, so every repeat call after the first is wasted work.

### Concrete hotspots (from codebase audit)

**Tier 1 — per-request, currently uncached:**

| Site | What it does | Frequency |
|---|---|---|
| `EntityMapper.PopulateAttributeValues` (`Services/EntityMapper.cs:178–228`) | `GetProperty(name)` + `GetValue` per attribute, per entity, per result row | 100s–1000s of calls/request |
| `EntityMapper.GetEntityDisplayName` (`Services/EntityMapper.cs:902–918`) | `GetProperties()` scan per entity | per result row |
| `EntityMapper.GetCollectionElementType` (`Services/EntityMapper.cs:296–314`) | `GetGenericArguments` + `GetInterfaces` per collection property | per AsDetail property per row |
| `ReferenceResolver.GetReferenceProperties` (`Services/ReferenceResolver.cs:34–63`) | `GetProperties()` + `GetCustomAttribute<ReferenceAttribute>` per query | per query |
| `ActionsResolver.FindActionsType` (`Services/ActionsResolver.cs:62–79`) | `Assembly.GetTypes()` walk per missed lookup; no negative caching | per entity-type resolution |

**Tier 2 — moderately hot, partially cached:**

- `QueryExecutor.customQueryMethodCache` (`Services/QueryExecutor.cs:30`) — already uses a `ConcurrentDictionary<string, ...>`; ad-hoc and should consolidate onto the new primitive.
- `MessageBus.StoreMessageAsync` (`MintPlayer.Spark.Messaging/Services/MessageBus.cs:29–35`) — `GetCustomAttribute<MessageQueueAttribute>` per broadcast.
- `ModelSynchronizer.CreateOrUpdateEntityTypeDefinition` (`Services/ModelSynchronizer.cs:281–325`) — startup-only but high redundancy across types.

**Tier 3 — startup-only, already adequately cached:**

- `LookupReferenceDiscoveryService`, `IndexRegistry`, `EtlScriptCollector`. Listed for completeness; no migration needed.

---

## 2. Goals & Non-Goals

### Goals

1. **Generic any-use primitive** — a single static cache class that any caller can use to memoize *anything* keyed by a string (or by type). Not a domain-specific "GetAllPropertiesWith[Indexed]" helper class.
2. **Two-tier cache** mirroring the CronosCore reference:
   - **Per-type tier** (`Cache<T>`) — generic-static specialization, one dictionary per `T`, ideal for "all properties of T", "the [Foo] attribute on T", etc.
   - **Global tier** — string-keyed, for cross-type lookups ("all types in assembly X derived from Y").
3. **Thread-safe**, computation-runs-once-per-key semantics.
4. **Zero new external dependencies** — no Vidyano `CacheLock` (used by the reference); use BCL primitives only.
5. **Migrate Tier 1 hotspots** onto the new primitive in this PR; consolidate Tier 2 ad-hoc caches in follow-ups.

### Non-Goals

- **Eviction / TTL.** Reflection metadata is AppDomain-immutable; never evict.
- **Cross-process / distributed cache.** In-process only.
- **Compiled accessor delegates** (`Expression.Lambda` for getters/setters) are *not in initial scope* but the API must accommodate them as a pure consumer — i.e. caching `Func<object, object>` should be a one-liner from a caller, with no changes to `ReflectionCache` itself. (Tracked as a follow-up; see §7.)
- **Replacing every reflection call site at once.** Audit-driven, tier-prioritized rollout.

---

## 3. Design

### 3.1 Locking strategy — depart from the reference

The CronosCore reference uses a `CacheLock` (a Vidyano-supplied wrapper around `ReaderWriterLockSlim`). We reject this for two reasons:

1. **No external dep available.** Vidyano is not a Spark dependency, and we don't want to add it for a single helper.
2. **`ConcurrentDictionary<TKey, Lazy<TValue>>.GetOrAdd` is strictly better** for this workload:
   - Lock-free reads (the dominant case after warmup).
   - `Lazy<T>` with `LazyThreadSafetyMode.ExecutionAndPublication` guarantees the factory runs exactly once per key, even under contention — no upgradeable-read dance, no TOCTOU window, no risk of duplicate factory execution.
   - Standard idiom; every .NET developer recognizes it.

The CronosCore upgradeable-read pattern is correct but heavier than necessary for a memoize-forever cache.

### 3.2 API surface

```csharp
namespace MintPlayer.Spark.Reflection;

public static class ReflectionCache
{
    // Per-type tier — one dictionary per TOwner, lock-free reads via ConcurrentDictionary.
    // Use when the cache key naturally belongs to one type (properties of T, attribute on T, etc.).
    public static TValue GetOrAdd<TOwner, TValue>(string key, Func<TValue> factory);

    // Global tier — single string-keyed dictionary shared across the AppDomain.
    // Use for cross-type lookups (assembly scans, name->type maps, etc.).
    public static TValue GetOrAdd<TValue>(string key, Func<TValue> factory);

    // Convenience overload — uses Type as the key without forcing callers to stringify.
    public static TValue GetOrAdd<TValue>(Type key, Func<Type, TValue> factory);

    // Diagnostics (debug builds / tests only).
    internal static int Count { get; }
    internal static void Clear(); // test-only
}
```

**Implementation sketch:**

```csharp
public static class ReflectionCache
{
    private static class PerType<TOwner>
    {
        // ReSharper disable once StaticMemberInGenericType — that's the point.
        internal static readonly ConcurrentDictionary<string, Lazy<object?>> Cache = new();
    }

    private static readonly ConcurrentDictionary<string, Lazy<object?>> globalCache = new();
    private static readonly ConcurrentDictionary<Type, Lazy<object?>> typeKeyedCache = new();

    public static TValue GetOrAdd<TOwner, TValue>(string key, Func<TValue> factory)
    {
        var lazy = PerType<TOwner>.Cache.GetOrAdd(
            key,
            _ => new Lazy<object?>(() => factory(), LazyThreadSafetyMode.ExecutionAndPublication));
        return (TValue)lazy.Value!;
    }

    public static TValue GetOrAdd<TValue>(string key, Func<TValue> factory)
    {
        var lazy = globalCache.GetOrAdd(
            key,
            _ => new Lazy<object?>(() => factory(), LazyThreadSafetyMode.ExecutionAndPublication));
        return (TValue)lazy.Value!;
    }

    public static TValue GetOrAdd<TValue>(Type key, Func<Type, TValue> factory)
    {
        var lazy = typeKeyedCache.GetOrAdd(
            key,
            t => new Lazy<object?>(() => factory(t), LazyThreadSafetyMode.ExecutionAndPublication));
        return (TValue)lazy.Value!;
    }
}
```

### 3.3 Why three overloads, not one

- `GetOrAdd<TOwner, TValue>(string, Func<TValue>)` — **per-type**. The generic-static `PerType<TOwner>` gives each `TOwner` its own dictionary at zero key-prefixing cost. Use for "properties of T with attribute X".
- `GetOrAdd<TValue>(string, Func<TValue>)` — **global string-keyed**. Use when the key is naturally a string and not bound to one type ("all `IPersistentObject` types in `Assembly.GetExecutingAssembly()`").
- `GetOrAdd<TValue>(Type, Func<Type, TValue>)` — **global type-keyed**. Avoids `type.FullName` boilerplate at every call site and is the natural shape for "given any Type, give me its X" lookups (e.g. `ActionsResolver.FindActionsType`).

### 3.4 Caller patterns

The cache is the *primitive*. Domain-specific helpers live near their callers, not inside `ReflectionCache`:

```csharp
// In EntityMapper:
private static PropertyInfo[] GetMappedProperties(Type entityType) =>
    ReflectionCache.GetOrAdd(entityType, t =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance));

// In ReferenceResolver:
private static (PropertyInfo Prop, ReferenceAttribute Attr)[] GetReferenceProps<T>() =>
    ReflectionCache.GetOrAdd<T, (PropertyInfo, ReferenceAttribute)[]>("references", () =>
        typeof(T).GetProperties()
            .Select(p => (p, p.GetCustomAttribute<ReferenceAttribute>()!))
            .Where(x => x.Item2 != null)
            .ToArray());

// In ActionsResolver — note negative caching via nullable:
private static Type? FindActionsType(string entityName) =>
    ReflectionCache.GetOrAdd<Type?>($"actions:{entityName}", () =>
        AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetTypes())
            .FirstOrDefault(t => t.Name == $"{entityName}Actions"));
```

Negative caching (caching `null`) falls out for free because `Lazy<object?>` happily holds `null`.

---

## 4. Migration Plan

### Phase 1 — primitive + Tier 1 (this PR)

1. Add `MintPlayer.Spark/Reflection/ReflectionCache.cs` per §3.2.
2. Migrate Tier 1 sites:
   - `EntityMapper.PopulateAttributeValues` — cache `(Type, propertyName) → PropertyInfo`.
   - `EntityMapper.GetEntityDisplayName` — cache `Type → PropertyInfo[]`.
   - `EntityMapper.GetCollectionElementType` — cache `Type → Type?`.
   - `ReferenceResolver.GetReferenceProperties` — cache `Type → (PropertyInfo, ReferenceAttribute)[]`.
   - `ActionsResolver.FindActionsType` — cache `string → Type?`.
3. Unit tests (see §6).

### Phase 2 — Tier 2 consolidation (follow-up)

4. Replace `QueryExecutor.customQueryMethodCache` ad-hoc dictionary with `ReflectionCache.GetOrAdd<...>("custom-query:" + key, ...)`.
5. Migrate `MessageBus.StoreMessageAsync` attribute lookup.
6. Migrate `ModelSynchronizer` per-type attribute reads.

### Phase 3 — compiled accessor delegates (follow-up, optional)

7. Add a separate `AccessorCache` static helper (consumes `ReflectionCache` — no changes to it) that builds and caches `Func<object, object>` getters / `Action<object, object>` setters via `Expression.Lambda` for properties read/written on hot paths (`EntityMapper.PopulateAttributeValues` is the prime candidate). Track separately; only ship if Phase 1 profiling shows getter/setter invocation is still hot after `PropertyInfo` lookups are cached.

---

## 5. Risks & Open Questions

### Risks

- **Memory growth.** Cache entries live forever. Bounded by `(num types) × (num distinct keys per type)` — finite and small for reflection metadata. Acceptable.
- **Stale cache during hot reload.** .NET hot reload can replace types. Out of scope; if it bites, add a `MetadataUpdateHandler` later.
- **Lazy factory exceptions.** A throwing factory under `ExecutionAndPublication` caches the *exception* and re-throws on every subsequent access. This is the correct behavior — a reflection lookup that fails will fail deterministically — but document it in xmldoc.

### Open questions

1. Should the cache live in `MintPlayer.Spark` or `MintPlayer.Spark.Abstractions`? **Recommendation:** `MintPlayer.Spark/Reflection/` for now — no abstraction needed; it's a static helper, not a service. Promote later if multiple packages need it.
2. Diagnostic counters — emit anything via `Activity` / `Metrics`? **Recommendation:** no, defer until a real ask.

---

## 6. Testing

- `ReflectionCacheTests`:
  - `GetOrAdd_PerType_DifferentOwners_GetIsolatedCaches`
  - `GetOrAdd_Global_SameKey_FactoryRunsOnce` (use a counter; assert `factory` invoked exactly once across N concurrent `Task.Run` callers).
  - `GetOrAdd_NullValue_IsCachedAndReturned` (negative caching contract).
  - `GetOrAdd_FactoryThrows_ExceptionIsCachedAndRethrown`.
  - `GetOrAdd_TypeKeyed_DifferentTypes_AreIsolated`.
- Migration sites: existing tests must still pass; add benchmarks (optional `BenchmarkDotNet` smoke run) for `EntityMapper.PopulateAttributeValues` before/after to validate the win.

---

## 7. Out of Scope (deferred)

- Compiled property getters/setters (Phase 3 above — open if profiling justifies).
- Eviction / TTL.
- Cross-AppDomain / distributed sharing.
- Replacing reflection inside source-generator-generated code (already compile-time).

---

## 8. Acceptance Criteria

- [ ] `ReflectionCache` exists with the §3.2 API and §6 unit tests passing.
- [ ] Tier 1 hotspots migrated; existing tests green.
- [ ] No new external NuGet dependencies.
- [ ] xmldoc on every public member, including the lazy-exception caching note.
- [ ] Issue #151 description updated to reflect this design.

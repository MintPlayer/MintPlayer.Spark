# PRD: ReflectionCache — Generic Any-Use Memoization Primitive

| | |
|---|---|
| **Version** | 1.1 |
| **Date** | 2026-05-03 |
| **Status** | Implemented (PR #152) |
| **Owner** | MintPlayer |
| **Package** | `MintPlayer.Spark.Abstractions` (promoted from `MintPlayer.Spark` so the messaging and replication-abstractions packages can consume it without a new dependency edge to `MintPlayer.Spark`) |
| **Issue** | [#151](https://github.com/MintPlayer/MintPlayer.Spark/issues/151) |
| **Reference** | `C:\Repos\CronosCore\CronosCore.RavenDB\ReferenceObject\ReflectionCache.cs` |

> **v1.1** — design unchanged from v1.0. Implementation shipped in PR #152 includes Phase 3 (compiled accessor delegates) up-front; the Phase-2/Phase-3 split was collapsed at the user's request to ship comprehensive coverage in a single PR.

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
- **Replacing every reflection call site at once.** ~~Audit-driven, tier-prioritized rollout.~~ **Updated in v1.1**: comprehensive single-PR rollout (the user requested every reflection hotspot be migrated in this PR; see §4).

### Goals expanded in v1.1

- **Compiled accessor delegates included.** `AccessorCache.GetGetter(PropertyInfo) → Func<object, object?>` and `GetSetter → Action<object, object?>` build via `Expression.Lambda` and cache on top of `ReflectionCache`. Used by `EntityMapper.PopulateAttributeValues` (Tier 1 hot path) and the `task.GetType().GetProperty("Result").GetValue(task)` extraction pattern that recurs in 10+ reflective dispatch sites (folded into the `Task.GetCompletedTaskResult()` extension method).

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

## 4. Migration Plan (v1.1 — all phases shipped in PR #152)

### Phase 1 — primitive + Tier 1

1. `MintPlayer.Spark.Abstractions/Reflection/ReflectionCache.cs` — three `GetOrAdd` overloads.
2. `MintPlayer.Spark.Abstractions/Reflection/AccessorCache.cs` — compiled getter/setter delegates.
3. `MintPlayer.Spark.Abstractions/Reflection/ReflectedTypeExtensions.cs` — `GetCachedProperties`, `GetCachedProperty`, `GetCachedCustomAttribute`, `GetCompletedTaskResult`.
4. Tier 1 migrations: `EntityMapper.{PopulateAttributeValues, GetEntityDisplayName, ResolveDisplayFormat, GetCollectionElementType, IsComplexType, MergeTranslatedString, LoadReferenceAsync, ResolveType, TryWriteId, SetPropertyValue}`, `ReferenceResolver.{GetReferenceProperties, ApplyIncludes, LoadEntityAsync, ResolveReferencedDocumentsAsync}`, `ActionsResolver.{FindActionsType, ResolveForType}`.

### Phase 2 — Tier 2 consolidation

5. `QueryExecutor` — replaces `customQueryMethodCache` ad-hoc dictionary with `ReflectionCache`; caches `ExtractQueryableElementType`, `FindClrType`, `MaterializeQueryable`, `ApplyIndexWithType`, `ApplyIndexByName`, `ApplyProjection`, `ApplySorting` per-(entity, property), `ExecuteQueryableAsync`.
6. `MessageBus.StoreMessageAsync` — `GetCachedCustomAttribute<MessageQueueAttribute>`.
7. `ModelSynchronizer` — `GetCachedProperties` and `GetCachedCustomAttribute` throughout, plus collection-element-type caching.

### Phase 3 — compiled accessor delegates

8. `AccessorCache.GetGetter` / `GetSetter` consumed by every property read/write previously calling `PropertyInfo.GetValue` / `SetValue` directly across `EntityMapper`, `ReferenceResolver`, `DatabaseAccess`, `SyncActionInterceptor`, `LookupReferenceService`, `LookupReferenceDiscoveryService`, `SyncAction`.

### Phase 3.5 — comprehensive sweep across every remaining reflection site

9. `DatabaseAccess` — caches all `IAsyncDocumentSession.LoadAsync<T>` / `Query<T>` (3-arg overload) / `LinqExtensions.ToListAsync<T>` / `LinqExtensions.ProjectInto<T>` generic-method instantiations; caches `OnLoadAsync`/`OnSaveAsync`/`OnQueryAsync`/`OnDeleteAsync`/`IsAllowedAsync` MethodInfos per actions type; caches `"Id"` PropertyInfo lookups.
10. `SyncActionHandler` — same shape plus `GetCachedProperties` for the CLR-fallback property scan.
11. `StreamingQueryExecutor` — replaces ad-hoc `streamingMethodCache`; caches `IAsyncEnumerator<T>.GetAsyncEnumerator`/`MoveNextAsync`/`Current` member-info per `(elementType, isSingleItem)`; caches `FindClrType`.
12. `LookupReferenceService` / `LookupReferenceDiscoveryService` — caches static `Items` PropertyInfo per transient type, `DisplayType` lookup, transient-item per-property reads.
13. `SparkMiddleware` — caches `Assembly.GetTypes()` filtered scans for index types and `[FromIndex]` projection types per assembly.
14. `Endpoints/ProgramUnits/Get` — `GetCachedProperties` for the per-request SparkContext walk.
15. `IndexRegistry.GetCollectionTypeFromIndex` — caches the base-type traversal per index type.
16. `SyncActionInterceptor` — replaces `_replicatedCache` and `_propertyNamesCache` ad-hoc `ConcurrentDictionary`s with `ReflectionCache`.
17. `EtlScriptCollector` — caches the per-assembly `[Replicated]`-annotated type scan.
18. `MessageSubscriptionWorker` — caches `IRecipient<T>`/`ICheckpointRecipient<T>` closed generic types and their `HandleAsync` MethodInfos per CLR message type.
19. `MessageSubscriptionManager` — `GetCachedCustomAttribute` for `[MessageQueue]` discovery.
20. `SyncAction.EntityToDictionary` (Replication.Abstractions) — `GetCachedProperties` + `AccessorCache.GetGetter`. Required adding a project reference from `MintPlayer.Spark.Replication.Abstractions` to `MintPlayer.Spark.Abstractions`.

---

## 5. Risks & Open Questions

### Risks

- **Memory growth.** Cache entries live forever. Bounded by `(num types) × (num distinct keys per type)` — finite and small for reflection metadata. Acceptable.
- **Stale cache during hot reload.** .NET hot reload can replace types. Out of scope; if it bites, add a `MetadataUpdateHandler` later.
- **Lazy factory exceptions.** A throwing factory under `ExecutionAndPublication` caches the *exception* and re-throws on every subsequent access. This is the correct behavior — a reflection lookup that fails will fail deterministically — but document it in xmldoc.

### Open questions (resolved in v1.1)

1. ~~Should the cache live in `MintPlayer.Spark` or `MintPlayer.Spark.Abstractions`?~~ **Resolved**: lives in `MintPlayer.Spark.Abstractions/Reflection/`. Required because both `MintPlayer.Spark.Messaging` and `MintPlayer.Spark.Replication.Abstractions` need it and neither references `MintPlayer.Spark`.
2. ~~Diagnostic counters — emit anything via `Activity` / `Metrics`?~~ **Deferred**, no real ask yet.

### New footgun caught during implementation

The original `GetOrAdd<TValue>(Type, Func<Type, TValue>)` overload used a `ConcurrentDictionary<Type, Lazy<object?>>` keyed only on `Type`. Two call sites that cache different things per Type (e.g. `MethodInfo` for `LoadAsync<T>` vs `Type?` for "collection element type of T") would have collided and hit `InvalidCastException` at the boundary. The cache key is now `(Type, ValueType)` — the value type is included automatically, so distinct `TValue` parameters never collide. Pinned by `GetOrAdd_type_keyed_isolates_distinct_value_types_for_same_Type` in `ReflectionCacheTests`.

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

## 7. Out of Scope (still deferred in v1.1)

- Eviction / TTL — reflection metadata is AppDomain-immutable; never evict.
- Cross-AppDomain / distributed sharing.
- Replacing reflection inside source-generator-generated code (already compile-time).
- Caching dynamic CLR-name resolutions (`ResolveType(string)` etc.) does cache *negative* results too. Spark's runtime doesn't dynamically load entity-type assemblies after startup, so a stale-null cached entry on a late-loaded assembly is a non-issue today; if hot reload becomes a target, revisit `ResolveType` with positive-only caching.

---

## 8. Acceptance Criteria

- [x] `ReflectionCache` exists with the §3.2 API and §6 unit tests passing (18 tests across `ReflectionCacheTests` + `AccessorCacheTests` + `ReflectedTypeExtensionsTests`).
- [x] Tier 1 hotspots migrated; existing tests green (783 / 783 pass).
- [x] Tier 2 ad-hoc caches consolidated.
- [x] Tier 3 sweep across every remaining reflection call site.
- [x] No new external NuGet dependencies.
- [x] xmldoc on every public member, including the lazy-exception caching note and the `(Type, TValue)` collision note.
- [x] Issue #151 description updated to reflect this design.

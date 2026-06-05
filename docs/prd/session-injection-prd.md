# PRD: Inject `IAsyncDocumentSession` from DI throughout Spark

**Status:** Proposed
**Date:** 2026-04-27
**Branch target:** new branch off `master`, single PR

## Background

The `CustomQueryArgs` class — passed into user-authored custom query methods on Actions classes — currently exposes a `Session` property of type `IAsyncDocumentSession`:

```csharp
// MintPlayer.Spark/Queries/CustomQueryArgs.cs
public sealed class CustomQueryArgs
{
    public PersistentObject? Parent { get; set; }
    public string? ParentType { get; set; }
    public required SparkQuery Query { get; set; }
    public required IAsyncDocumentSession Session { get; set; }   // ← side-channel for DI
}
```

`QueryExecutor.ExecuteCustomQueryAsync` opens a fresh session via `documentStore.OpenAsyncSession()`, stuffs it into the args, invokes the user method, and disposes it via `using` when the method returns. The session is purely a side-channel for DI: it has nothing to do with query *args*, but it is the only way the framework currently lets user code reach a session.

This is awkward. The Actions class already uses `[Inject]` for every other dependency. More broadly: **the framework as a whole opens a fresh `documentStore.OpenAsyncSession()` per operation** — `DatabaseAccess` does it in 6 methods, `QueryExecutor` does it in two more — instead of letting DI manage a request-scoped session. This is unusual for a web framework and creates several pain points:

- `CustomQueryArgs` carries a session it shouldn't have to.
- User code has no way to inject a session into Actions classes for non-custom-query needs.
- The identity map is reset on every framework operation, so two `GetPersistentObjectAsync` calls in the same request don't share cached docs.
- `WebhooksDemo` already injects `[Inject] private readonly IAsyncDocumentSession _session` in 3 places (`GitHubProjectsController.cs:20`, `HandlePullRequestEvent.cs:15`, `HandleIssuesEvent.cs:16`) — so the pattern is already informally proven, just not formalized.

## Goal

1. Register `IAsyncDocumentSession` (and `IDocumentSession`) as Scoped in DI from `AddSpark`.
2. Inject that scoped session into the framework's own services (`DatabaseAccess`, `QueryExecutor`) and into user Actions classes.
3. Remove the `Session` property from `CustomQueryArgs`.

After this change, a single HTTP request uses one Raven session for everything — framework reads, framework writes, user custom queries, reference resolution. Standard unit-of-work semantics, single identity map.

The single exception is the optimistic-concurrency ETag check in `SavePersistentObjectAsync`, which needs an explicitly isolated side session (see §4).

## Current usage of `CustomQueryArgs.Session`

All 4 usages are in demo user code:

| File | Line | Call |
|---|---|---|
| `Demo/HR/HR/Actions/PersonActions.cs` | 17 | `args.Session.Query<VPerson, People_Overview>()` |
| `Demo/Fleet/Fleet/Actions/CarActions.cs` | 125 | `args.Session.Query<VCar, Cars_Overview>()` |
| `Demo/DemoApp/DemoApp/Actions/PersonActions.cs` | 50 | `args.Session.Query<VPerson, People_Overview>()` |
| `Demo/WebhooksDemo/WebhooksDemo/Actions/ProjectColumnActions.cs` | 18 | `args.Session.LoadAsync<GitHubProject>(args.Parent.Id)` |

No framework-internal code reads `CustomQueryArgs.Session` — only user methods do. The framework only writes to it.

## Current `OpenAsyncSession` calls in the framework

These all become `[Inject] IAsyncDocumentSession session` after this change, except the explicitly isolated ETag-check side session:

| File | Method | Line | Purpose |
|---|---|---|---|
| `MintPlayer.Spark/Services/DatabaseAccess.cs` | `GetDocumentAsync<T>` | 26 | Replace |
| `MintPlayer.Spark/Services/DatabaseAccess.cs` | `GetDocumentsAsync<T>` | 32 | Replace |
| `MintPlayer.Spark/Services/DatabaseAccess.cs` | `GetDocumentsByObjectTypeIdAsync<T>` | 38 | Replace |
| `MintPlayer.Spark/Services/DatabaseAccess.cs` | `SaveDocumentAsync<T>` | 46 | Replace |
| `MintPlayer.Spark/Services/DatabaseAccess.cs` | `DeleteDocumentAsync<T>` | 64 | Replace |
| `MintPlayer.Spark/Services/DatabaseAccess.cs` | `GetPersistentObjectAsync` | 89 | Replace |
| `MintPlayer.Spark/Services/DatabaseAccess.cs` | `GetPersistentObjectsAsync` | 126 | Replace |
| `MintPlayer.Spark/Services/DatabaseAccess.cs` | `SavePersistentObjectAsync` (main) | 205 | Replace |
| `MintPlayer.Spark/Services/DatabaseAccess.cs` | `SavePersistentObjectAsync` (ETag side) | 214 | **Keep** — see §4 |
| `MintPlayer.Spark/Services/DatabaseAccess.cs` | `DeletePersistentObjectAsync` | 256 | Replace |
| `MintPlayer.Spark/Services/QueryExecutor.cs` | `ExecuteDatabaseQueryAsync` | 94 | Replace |
| `MintPlayer.Spark/Services/QueryExecutor.cs` | `ExecuteCustomQueryAsync` | 189 | Replace |

## Feasibility

### Is it safe to register `IAsyncDocumentSession` as Scoped?

**Yes.** The pattern is:

```csharp
services.AddScoped<IAsyncDocumentSession>(sp =>
    sp.GetRequiredService<IDocumentStore>().OpenAsyncSession());

services.AddScoped<IDocumentSession>(sp =>
    sp.GetRequiredService<IDocumentStore>().OpenSession());
```

ASP.NET Core's DI container disposes `IDisposable` scoped services at the end of the request scope. RavenDB's `IAsyncDocumentSession` implements `IAsyncDisposable` and `IDisposable` — DI handles this correctly. Lifetime is bounded by the HTTP request, which matches RavenDB's recommended unit-of-work pattern for web apps.

### `SaveChangesAsync` semantics under a shared session

This is the substantive change in framework behavior. Today, each `using var session` block is its own unit of work: one operation, one session, dispose at end. With a request-scoped session, **multiple framework operations within a request share change tracking**.

Concretely:

- `SavePersistentObjectAsync` calls `OnSaveAsync` on the Actions class, which stores the entity and calls `session.SaveChangesAsync()`. With a scoped session, that `SaveChangesAsync()` will also commit any *other* in-flight tracked changes that have accumulated in the same request.
- Today's behavior: each Save is isolated; if Get-then-Save is wired naively, two sessions are involved and the loaded entity isn't tracked by the saving session.
- New behavior: Get-then-Save in one request shares a session, so a load + property mutation + save commits the mutation. This is **standard Raven unit-of-work behavior**. It's only a "change" relative to Spark's current isolated-session pattern.

**This is acceptable.** Spark's CRUD endpoints don't load-then-mutate-then-save without going through `OnSaveAsync` (which receives a fresh `PersistentObject` from the wire and translates it via the entity mapper). User code that loads via `GetPersistentObjectAsync` and then calls `SavePersistentObjectAsync` in the same request is going through the actions pipeline, which is the intended unit of work anyway.

The one place this matters is the **optimistic-concurrency ETag check** in `SavePersistentObjectAsync` (lines 212-222 of `DatabaseAccess.cs`). The current code explicitly opens a side `checkSession` so that loading the existing entity for ETag comparison does NOT pollute change tracking for the main save session. This isolation **must be preserved** — we cannot use the scoped session for that check, because the actions pipeline's `StoreAsync` would then conflict with the already-tracked existing entity.

**Solution:** keep `documentStore.OpenAsyncSession()` for that one specific call site. The framework retains `[Inject] IDocumentStore documentStore` for this reason. (There may be other future cases — bulk insert, isolated background work — where a side session is the right call.)

### Reference resolution

`IReferenceResolver.ResolveReferencedDocumentsAsync(session, ...)` and `.ApplyIncludes(query, ...)` already accept a session as a parameter. Pass the scoped session. Documents resolved as references stay in the identity map for the rest of the request, so a follow-up `GetPersistentObjectAsync` for one of those references is free.

### Custom queries: framework-side execution

Today, `QueryExecutor.ExecuteCustomQueryAsync` opens a session, passes it into `CustomQueryArgs`, the user method binds an `IRavenQueryable<T>` to that session, and the framework materializes (`.ToListAsync`) before disposing the session. Reference resolution runs against the same session.

After this change:
- The framework injects the scoped session.
- The user Actions class injects the same scoped session via `[Inject]`.
- The user's queryable binds to that scoped session.
- Framework materialization and reference resolution both run against the scoped session.
- Nothing is disposed mid-method; DI cleans up at end of request.

The user's `CustomQueryArgs` parameter is now optional (parameterless custom query methods are already supported by the dispatcher in `QueryExecutor.cs:213-220` via `methodInfo.AcceptsArgs`).

### `[Inject]` source generator support

Confirmed: `[Inject]` resolves any DI-registered service via `IServiceProvider.GetRequiredService<T>()`. No source-generator changes needed. The 3 existing usages in `WebhooksDemo` already work.

## Proposed change

### 1. Register sessions in `AddSparkCore` (`MintPlayer.Spark/SparkMiddleware.cs`)

Right after `services.AddSingleton<IDocumentStore>(...)`:

```csharp
services.AddScoped<IAsyncDocumentSession>(sp =>
    sp.GetRequiredService<IDocumentStore>().OpenAsyncSession());

services.AddScoped<IDocumentSession>(sp =>
    sp.GetRequiredService<IDocumentStore>().OpenSession());
```

Leave `MaxNumberOfRequestsPerSession` at Raven's default (30). The framework's own operations on a typical page (one `Get` or `List`, a handful of sub-queries with `.Include`, batched reference resolution) sit comfortably under that budget. If the framework ever bumps into the limit during normal rendering, that's a real signal worth investigating — N+1, missing `.Include`, missing index projection — not a number to paper over.

### 2. Migrate `DatabaseAccess`

Add `[Inject] private readonly IAsyncDocumentSession session;` to the field list. For each method in the table above (except the ETag-check side session), drop `using var session = documentStore.OpenAsyncSession();` and use the field. Methods don't need to change their internal signatures — `LoadEntityViaActionsAsync`, `FilterByRowLevelAuthAsync`, `QueryEntitiesWithIncludesAsync`, `SaveEntityViaActionsAsync`, `DeleteEntityViaActionsAsync` already take `IAsyncDocumentSession` as a parameter, so callers just pass `this.session`.

**Keep `documentStore.OpenAsyncSession()` for the ETag check at `SavePersistentObjectAsync` line 214.** Add a comment explaining why isolation is required there.

`[Inject] private readonly IDocumentStore documentStore;` stays as a field — it's still needed for the ETag side session.

### 3. Migrate `QueryExecutor`

Add `[Inject] private readonly IAsyncDocumentSession session;`. Drop the `using var session = documentStore.OpenAsyncSession();` lines in `ExecuteDatabaseQueryAsync` (line 94) and `ExecuteCustomQueryAsync` (line 189). Use the injected `session` instead.

`[Inject] private readonly IDocumentStore documentStore;` can be removed from `QueryExecutor` if no other usage remains. (Verify during implementation; remove if unused, keep if there's a path I missed.)

### 4. Remove `Session` from `CustomQueryArgs`

```csharp
// MintPlayer.Spark/Queries/CustomQueryArgs.cs
public sealed class CustomQueryArgs
{
    public PersistentObject? Parent { get; set; }
    public string? ParentType { get; set; }
    public required SparkQuery Query { get; set; }
    // Session removed
}
```

Drop `Session = session,` from the args construction in `QueryExecutor.ExecuteCustomQueryAsync` (around line 220 of `QueryExecutor.cs`).

### 5. Migrate the 4 demo Actions classes

Each becomes:

```csharp
public partial class XActions : DefaultPersistentObjectActions<X>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IRavenQueryable<...> Some_Query(CustomQueryArgs args)
    {
        args.EnsureParent("Foo");
        return session.Query<...>().Where(c => c.Parent == args.Parent!.Id);
    }
}
```

Methods that take no parent context (e.g. `Stolen_Cars`) drop the `CustomQueryArgs` parameter entirely. The `WebhooksDemo` `ProjectColumnActions` case is async and already has other `[Inject]` fields — just add the session field and drop `args.Session`.

### 6. `MaxNumberOfRequestsPerSession` — keep Raven's default of 30, ship a scoped escape hatch

The Raven default of 30 stays for the framework's session factory. Hitting that limit is a useful signal — N+1, missing `.Include`, missing index projection — and we don't want to hide it. For the rare case where a single method legitimately needs more budget, ship an `IgnoreMaxRequests()` extension that **scopes the elevation** to a `using` block.

#### 6a. `SessionExtensions.IgnoreMaxRequests`

New file: `MintPlayer.Spark/Extensions/SessionExtensions.cs`. Public, in namespace `MintPlayer.Spark`.

Critical design point: because the session is request-scoped, a heavy custom query in the middle of a request must not silently elevate the budget for every subsequent operation in that same request. The implementation captures the original max on entry and **restores it on dispose** — scoped lift, not permanent. (A pattern that just sets the max and walks away is fine when session lifetime equals method scope, but ours doesn't.)

API:

```csharp
namespace MintPlayer.Spark;

public static class SessionExtensions
{
    /// <summary>
    /// Temporarily disables the per-session request budget for the duration of the returned scope.
    /// On dispose, the original <c>MaxNumberOfRequestsPerSession</c> is restored. If the scope
    /// performed more requests than <paramref name="expectedMaximumRequests"/> (default: the
    /// session's original max), a warning is logged.
    /// </summary>
    public static IDisposable IgnoreMaxRequests(
        this IAsyncDocumentSession session,
        int? expectedMaximumRequests = null,
        ILogger? logger = null,
        [CallerMemberName] string? scope = null);

    public static IDisposable IgnoreMaxRequests(
        this IDocumentSession session,
        int? expectedMaximumRequests = null,
        ILogger? logger = null,
        [CallerMemberName] string? scope = null);
}
```

Implementation sketch:

```csharp
public static IDisposable IgnoreMaxRequests(
    this IAsyncDocumentSession session,
    int? expectedMaximumRequests = null,
    ILogger? logger = null,
    [CallerMemberName] string? scope = null)
{
    var baseline   = session.Advanced.NumberOfRequests;
    var originalMax = session.Advanced.MaxNumberOfRequestsPerSession;
    var allowed     = expectedMaximumRequests ?? (originalMax == int.MaxValue ? 30 : originalMax);

    session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;

    return new MaxRequestsScope(() =>
    {
        // Restore — critical for request-scoped sessions where this method is one
        // of several operations sharing the same session.
        session.Advanced.MaxNumberOfRequestsPerSession = originalMax;

        var used = session.Advanced.NumberOfRequests - baseline;
        if (used > allowed)
            logger?.LogWarning(
                "[IgnoreMaxRequests] {Scope} performed {Used} requests, expected ≤ {Allowed}",
                scope, used, allowed);
    });
}
```

`MaxRequestsScope` is a private nested `IDisposable` that calls the captured Action exactly once.

Usage (user code):

```csharp
public partial class ReportActions : DefaultPersistentObjectActions<Report>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ILogger<ReportActions> logger;

    public async Task<...> HeavyAggregation(CustomQueryArgs args)
    {
        using var _ = session.IgnoreMaxRequests(expectedMaximumRequests: 80, logger: logger);
        // ... heavy multi-query work ...
        // budget restored to 30 when this method returns, so a follow-up Save in
        // the same request still gets its honest constraint
    }
}
```

#### 6b. Other escape hatches (still valid)

- **Inject `IDocumentStore` for a fully isolated session.** Use this when you specifically *want* a fresh identity map and independent change tracking — e.g., a bulk import that shouldn't share tracking with the rest of the request:

  ```csharp
  [Inject] private readonly IDocumentStore documentStore;

  public async Task<...> BulkImport()
  {
      using var session = documentStore.OpenAsyncSession();
      session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
      // ... heavy isolated work ...
  }
  ```

- **Application-wide override** via last-registration-wins:

  ```csharp
  builder.Services.AddSpark(builder.Configuration, spark => { ... });
  builder.Services.AddScoped<IAsyncDocumentSession>(sp =>
  {
      var session = sp.GetRequiredService<IDocumentStore>().OpenAsyncSession();
      session.Advanced.MaxNumberOfRequestsPerSession = 200;
      return session;
  });
  ```

Document all three patterns in `guide-getting-started.md`, with `IgnoreMaxRequests()` as the recommended default — it's the cheapest (no extra session, shared identity map) and the safest (auto-resets, optional warning).

### 7. Documentation updates

- `docs/guide-queries-and-sorting.md`: update custom-query example to use injected session.
- `docs/guide-custom-actions.md`: ditto, plus note that any DI-registered service (including `IAsyncDocumentSession`) can be injected.
- `docs/guide-getting-started.md`: mention the scoped session registration and `MaxNumberOfRequestsPerSession` default.

## Risks

| Risk | Mitigation |
|---|---|
| User loads an entity via injected session and accidentally mutates it; framework `SaveChangesAsync` on a later operation persists the mutation. | This is standard Raven unit-of-work behavior. Document it clearly. Anyone reading "request-scoped Raven session" should expect this. |
| Heavy requests exceed 30-request budget. | Default kept at Raven's 30 — hitting the limit is a useful signal, not a bug to hide. Three escape hatches in increasing isolation: `session.IgnoreMaxRequests()` (scoped lift, auto-reset), inject `IDocumentStore` (separate session), or re-register `IAsyncDocumentSession` after `AddSpark()` (app-wide). See §6. |
| ETag check loads the existing entity and conflicts with the actions pipeline's `StoreAsync` on the same session. | Preserved by keeping a dedicated `documentStore.OpenAsyncSession()` for that one call site. |
| Background workers, message-bus handlers, or non-HTTP code paths assume a per-request session that doesn't exist outside an HTTP scope. | Non-HTTP code paths already create their own DI scopes (the message-bus subscription workers use `IServiceScopeFactory`). The Scoped registration works correctly inside any scope, not just HTTP. Verify message-bus / webhook handlers during implementation. |
| External NuGet consumers (currently `10.0.0-preview.18`) using `CustomQueryArgs.Session` directly. | Bump to `10.0.0-preview.19`. List as a breaking change in the changelog. The user-facing migration is one line: replace `args.Session` with `[Inject] IAsyncDocumentSession session`. |

## Out of scope

- Multi-database scenarios. The current single-`IDocumentStore` model is unchanged.
- Removing the `[Inject] IDocumentStore documentStore` field from `DatabaseAccess` — kept for the ETag side session.

## Acceptance criteria

- [ ] `IAsyncDocumentSession` and `IDocumentSession` resolvable from DI as Scoped after `AddSpark()` is called.
- [ ] Session factories use Raven's default `MaxNumberOfRequestsPerSession` (no override).
- [ ] `SessionExtensions.IgnoreMaxRequests` ships in `MintPlayer.Spark` with overloads for `IAsyncDocumentSession` and `IDocumentSession`. Restores original max on dispose. Optional `expectedMaximumRequests` and `ILogger` parameters; logs a warning when overshot.
- [ ] Unit test: `IgnoreMaxRequests()` lifts the budget within scope, restores it on dispose, and emits a warning when the actual request count exceeds the expected max.
- [ ] All three escape hatches (`IgnoreMaxRequests`, `IDocumentStore` injection, last-registration-wins override) documented in `guide-getting-started.md`.
- [ ] `CustomQueryArgs` no longer has a `Session` property.
- [ ] `DatabaseAccess` uses the injected session for all 9 operation methods; the ETag side session in `SavePersistentObjectAsync` remains explicitly isolated with a code comment explaining why.
- [ ] `QueryExecutor.ExecuteDatabaseQueryAsync` and `ExecuteCustomQueryAsync` use the injected session.
- [ ] All 4 demo Actions classes (`HR/PersonActions`, `Fleet/CarActions`, `DemoApp/PersonActions`, `WebhooksDemo/ProjectColumnActions`) use `[Inject] IAsyncDocumentSession` and pass their respective custom-query test paths in the existing demo apps.
- [ ] `Stolen_Cars` migrated to parameterless signature (drops `CustomQueryArgs` since it doesn't use Parent).
- [ ] All 3 docs files updated (`guide-queries-and-sorting.md`, `guide-custom-actions.md`, `guide-getting-started.md`).
- [ ] Preview NuGet version bumped (`10.0.0-preview.19`).
- [ ] Smoke test: each demo app boots, the custom-query endpoints return rows, a CRUD round-trip (create → load → save → delete) works, and reference attributes resolve correctly.
- [ ] Existing test suite green.

# Implementation plan — Issue #239 (async `GetRowFilterAsync` + 30-cap safety)

See [issue_239_PRD.md](./issue_239_PRD.md). Branch `feat/async-row-filter`. One PR. Tests batched at the end (build + read to verify intermediate steps). All `file:line` against `master` @ 5d5fce0.

**Shape of the PR:** the async rename is mechanical (M1); the cap-safety work (M2–M5) is what makes it shippable and is the reason this is its own PRD. M2 (memo) is mandatory — without it an async hook breaks the first list page. M3–M4 fix two pre-existing cap bugs the async hook makes visible.

## M1 — Async hook conversion (mechanical, breaking)

1. `Actions/DefaultPersistentObjectActions.cs:150` — `GetRowFilter` → `virtual Task<Expression<Func<T,bool>>?> GetRowFilterAsync(string action) => Task.FromResult<…>(null)`; keep `[NoInterfaceMember]`; update the doc block (construction may await; expression stays sync; **add the purity + "at most once per (type,action) per request" cost contract** from PRD §5).
2. `Actions/DefaultPersistentObjectActions.cs:81` — `var filter = await GetRowFilterAsync(action);` in `EnsureRowSaveAllowedAsync` (already `async`).
3. `Services/RowSecurity.cs`:
   - `ResolveFilterHook` (`:423-429`) — reflect `"GetRowFilterAsync"` (cache key + `GetMethod` name); stays a sync `MethodInfo` lookup (`GetMethod` matches name+params, not return type).
   - `InvokeGetRowFilter` (`:392-400`) → `async Task<LambdaExpression?> InvokeGetRowFilterAsync(...)`: cast the reflected invoke result to non-generic `Task`, `await`, then `(LambdaExpression?)task.GetCompletedTaskResult()` (the cached helper in `Abstractions/Reflection/ReflectedTypeExtensions.cs`; same pattern as the `IsAllowedAsync` reflection at `:384-386`). **The memo (M2) lives here.**
   - `ResolveEffectiveRule` (`:356`) → `async Task<Func<object,Task<bool>>?> ResolveEffectiveRuleAsync(...)`; `:366` → `await InvokeGetRowFilterAsync(...)`.
   - `IsAllowedAsync:106`, `FilterAsync:125` — `await ResolveEffectiveRuleAsync(...)`.
   - `ComposeRowFilter` (`:190`) → `async Task<object> ComposeRowFilterAsync(...)`; `:196` → `await InvokeGetRowFilterAsync(...)`. Invariants (system-context `:193`, constant-predicate `:203`, projection decision `:206-219`, `announced` logging) unchanged.
   - `IRowSecurity` interface (`:72`) — the one signature change: `Task<object> ComposeRowFilterAsync(...)`. Add `void ResetRequestFilterCache()` for M3.
   - `HasRowRule` (`:110`) stays synchronous (pure reflection).
4. `Services/QueryExecutor.cs:167,274` and `Services/DatabaseAccess.cs:447` — `await rowSecurity.ComposeRowFilterAsync(...)` (all enclosing methods already `async`).
5. `Demo/Fleet/Fleet/Actions/CarActions.cs:39` — override → `GetRowFilterAsync` returning `Task<…>`; wrap the four returns in `Task.FromResult` (avoids CS1998); keep the service-account doc block.
6. `Demo/WebhooksDemo/.../GitHubProjectActions.cs` — **DONE (the worked async example):** migrated `IsAllowedAsync => _orgAccess.IsOwnerAllowedAsync(...)` to `GetRowFilterAsync` that `await`s `GetAllowedOwnersAsync()` and returns `p => owners.Contains(p.OwnerLogin)` — gains pushdown + WITH CHECK. The redundant `OnLoadAsync` and `OnBeforeDeleteAsync` org checks were deleted (framework read/delete gates derive from the same filter); `OnBeforeSaveAsync` keeps only its GitHub column-fetch. No tests referenced the removed members.

## M2 — Per-request memo (mandatory — PRD §3.2)

1. Add to `RowSecurity` (scoped) a `Dictionary<(Type, string), Task<LambdaExpression?>>` (+ a parallel compiled-delegate cache), consulted inside `InvokeGetRowFilterAsync` **before** invoking the hook. Store the `Task`, not the awaited result, so concurrent awaiters share one invocation.
2. `ResolveEffectiveRule`'s `filter?.Compile()` (`:373`) reads the memo's cached compiled delegate rather than recompiling per call.
3. System-context short-circuit stays before the memo lookup.
4. Leave `EnsureRowSaveAllowedAsync` (once-per-save) unmemoized — no benefit, and it must see the current save's state.

**Tests:** a list-page fixture with a referenced type + N seeded referenced documents → assert the hook resolves **once per referenced type**, not once per document (a counting Actions fixture); assert the count is invariant as N grows (10 → 100 rows → same invocation count).

## M3 — Memo invalidation on the streaming re-auth tick (security — PRD §3.3)

1. `Streaming/StreamingQueryExecutor.cs:98-103` — on the same tick that re-runs `permissionService.IsAllowedAsync`, call `rowSecurity.ResetRequestFilterCache()`.
2. `RowSecurity.ResetRequestFilterCache()` clears the M2 memo (and compiled-delegate cache).

**Test:** a stream whose allow-list shrinks mid-connection stops delivering the revoked rows within one re-auth tick (a mutable fake allow-list + a counting/asserting fixture).

## M4 — Streaming session fix (pre-existing bug — PRD §3.4)

1. `StreamingQueryExecutor.cs` — inside the `await foreach` batch loop, open a short-lived **framework** session per batch (`using var batchSession = documentStore.OpenAsyncSession();`) and pass it to `FilterAsync` (`:109`), `breadcrumbResolver.ResolveAsync` (`:119`), `RedactAsync` (`:124`). Do **not** replace the consumer's `args.Session`.
2. Uncap the consumer's connection session: on the session created at `:76`, set `Advanced.MaxNumberOfRequestsPerSession = int.MaxValue` with a comment (a socket is not "a request"). Framework per-batch sessions stay at 30.

**Test:** a projection-backed, referenced-type stream survives ≫30 batches; a counting fixture proves per-batch framework requests don't accumulate across batches. (This path has **zero** row-security tests today — this is net-new coverage.)

## M5 — Custom-action per-item loop + diagnostic (PRD §3.5, §3.6)

1. `Endpoints/Actions/ExecuteCustomAction.cs:113` — replace the per-selected-item sequential `GetPersistentObjectAsync` loop with a batched row-gated load (or a deliberately-reasoned `IgnoreMaxRequests` with the expected ceiling + logger).
2. Diagnostic: `RowSecurity` counts hook invocations per request; `logger.LogWarning` above ~20, naming the types. Same one-shot spirit as `announced`.

**Test:** a custom action with many selected items does not throw the cap exception.

## M6 — Default includes: fix dead `ApplyIncludes` + add `GetDefaultIncludes()` (PRD §7)

Independent of the async work (can land in parallel), bundled here because it attacks the same breadcrumb-driven cap pressure and touches the same query-build sites.

1. **Fix the dead `ApplyIncludes`** (`Services/ReferenceResolver.cs:71-94`): it reflects for a non-existent *instance* `Include(string)` → always no-ops. Change signature to `object ApplyIncludes(object queryable, Type elementType, IReadOnlyCollection<string> paths)` and invoke the **static** `LinqExtensions.Include<TResult>(IQueryable<TResult>, string)` reflectively — `typeof(LinqExtensions).GetMethods()` → the `(IQueryable<TResult>, string)` overload → `MakeGenericMethod(elementType)` → `Invoke(null, [queryable, path])`, cached per element type. Precedent: `RowSecurity.ComposeRowFilter` (`RowSecurity.cs:229-238`). **This changes emitted RQL — a query-shape change, not a pure addition.**
2. **New hook** `IReadOnlyCollection<string>? GetDefaultIncludes()` on `DefaultPersistentObjectActions<T>` (`:149`, `[NoInterfaceMember]`, default `null`). Resolve via `IReferenceResolver.GetDefaultIncludes(Type)` reflectively (mirror `RowSecurity.ResolveFilterHook`/`InvokeGetRowFilter`). Validate each path's first segment against `entityType`'s props; one-shot `announced`-style warn on an unknown segment.
3. **Merge + apply** at each site (PRD §7.6): `[Reference]` property names ∪ default-include paths, deduped.
   - Detail: default `OnLoadAsync` (`DefaultPersistentObjectActions.cs:25`) → `session.LoadAsync<T>(id, b => { foreach (var p in paths) b.IncludeDocuments(p); }, ct)`; **document the "override takes over includes" caveat** (reuse the `:71-72` wording).
   - PO list `DatabaseAccess.cs:438-441` (element type `queryType`); DB query `QueryExecutor.cs:159-162` (element type `resultType`); custom query — new call after `~:268`, guarded on `methodInfo.IsRavenQueryable`, element type `methodInfo.ResultElementType`.
   - Streaming: **cannot be framework-applied** — surface the resolved paths on `StreamingQueryArgs` so the consumer's `StreamItems` can apply them, or document the gap. Decide at implementation.
4. **Scope note:** nested paths are **embedded-object only** (`"Repository.Owner"` where Repository is embedded with an Owner id); RavenDB 7.x has no cross-document recursive include (PRD §7.5). Don't promise arbitrary reference-chain depth.

**Tests:** a test that **inspects the generated RQL / query and asserts it contains `include`** for a `[Reference]` type (nothing today would catch the dead code); a `GetDefaultIncludes()` test asserting the declared path is honoured on detail + list (referenced doc arrives in the same round-trip → assert session request count doesn't increase for the referenced access); the unknown-segment diagnostic.

## Demo examples (worked demonstrations of both features)

Demos have no functional meaning (per the maintainer) — these exist to show the features end to end:
- **Async row filter — `Demo/WebhooksDemo/.../GitHubProjectActions.cs`:** `GetRowFilterAsync` awaits the caller's GitHub-org allow-list, then returns a pushed-down `owner in (…)` predicate. The exact I/O-in-construction case the sync hook couldn't express; replaces three hand-written org checks with one filter across all paths (M1.6 above).
- **`GetDefaultIncludes()` — `Demo/Fleet/Fleet/Actions/CarActions.cs`:** declares `[nameof(Car.Manager)]` so the manager reference is primed in the same round-trip.
- **New row-scoped query — `Demo/Fleet/.../CarActions.cs` + `App_Data/Model/Car.json`:** a `Custom.Recent_Cars` spark-query (cars ≥ 2020) added alongside the existing `Stolen_Cars`, to show row security composes onto any new query surface for free (via the projection fallback, since `VCar` lacks `CreatedBy`).

## M7 — Docs + version bump

1. `docs/guide-row-security.md` (`:13,:20-46,:65,:94-97`) — signature → `GetRowFilterAsync`; add the async example (WebhooksDemo shape), the **at-most-once-per-(type,action)-per-request contract**, the stream-staleness note (~10 batches), the "pure per request" rule, and the "`IsAllowedAsync` is per-row / not memoized — use the filter for I/O rules" edge (PRD §5).
2. `libs/spark/MintPlayer.Spark/README.md:254-259` — signature; **delete the stale `OnQueryAsync` bullet at `:247`** (hook no longer exists).
3. `docs/issue_236_PRD.md` / `docs/issue_236_plan.md` — the M7 stub becomes a one-line "superseded by #239 — see `docs/issue_239_*`" pointer (don't rewrite history).
4. Document `GetDefaultIncludes()` (guide-row-security.md or a short new section): the string dotted-path signature, the `[Reference]`-props-are-auto-included note, the **embedded-only nested** limitation (no cross-document recursion), the detail-path override caveat, and the streaming gap.
5. **PR body must state** that fixing `ApplyIncludes` is a real query-shape change (RQL now emits `include`) — includes never actually ran before this PR.
6. Version bump: `.NET → 10.0.0-preview.45` in lockstep across all 21 csproj (ng-spark untouched — no client-visible change). Do **not** publish by hand; bump + merge, CI publishes.

## Dead-code / cleanup sweep (do NOT leave orphans)

The investigations surfaced dead and misleading code; prune it in the same PR rather than leaving it around the new paths.

1. **Delete the old synchronous `GetRowFilter`** — not kept as an overload (M1; the additive form has a fail-open trap).
2. **Correct the false `.Include()`-priming comments.** `QueryExecutor.cs:184-185` and `DatabaseAccess.cs:169-171` claim referenced docs are "primed into the session cache by `.Include()`" — untrue today (`ApplyIncludes` is dead, PRD §7.1). Once M6 makes includes real the claim becomes true; review the wording and keep it honest (and only for level-1 embedded paths, not cross-document).
3. **Remove WebhooksDemo's now-redundant manual checks** in `OnLoadAsync`/`OnBeforeSaveAsync`/`OnBeforeDeleteAsync` once the framework `GetRowFilterAsync` gates cover them (M1.6) — verified by test, not assumed.
4. **Delete the stale `OnQueryAsync` README bullet** (`README.md:247`) — the hook was removed in #236 M5 (M7).
5. **Drop the two uncached `GetType().GetProperty("Result")` reflections** in `RowSecurity` (the `IsAllowedAsync` closure + `RedactAsync`) in favour of the cached `GetCompletedTaskResult()` helper the new `InvokeGetRowFilterAsync` uses — removes the "two ways to do the same thing in one file" inconsistency while we're in there.
6. **General sweep at the end:** grep for now-unreachable branches introduced by the async/memo/includes changes (e.g. any sync-hook fallback, any pre-async compose helper that no longer has a caller) and delete them. A dead branch a reviewer can't distinguish from a live one is worse than a compile break.

## Test inventory (batched at the end)

Migrate the existing fixtures to the async signature and add the new cap/security tests:
- `tests/.../Services/RowFilterPushdownTests.cs` — fixtures `:50,:64` async; `await` the 3 `ComposeRowFilterAsync` sites (`:127,:181,:230`); **add a genuinely-awaiting fixture** (fake async service + `await`) so something proves the async path beyond compilation.
- `tests/.../Services/RowLevelWithCheckTests.cs` — fixtures `:43,:53` async.
- `tests/.../_Infrastructure/PermissiveRowSecurity.cs` — `ComposeRowFilterAsync(...) => Task.FromResult(queryable)`; implement the new `ResetRequestFilterCache()` no-op.
- NSubstitute `IRowSecurity` doubles (`RowLevelQueryAuthorizationTests.cs:112`, `QueryExecutorIntegrationTests.cs:69`, `BreadcrumbResolverTests.cs:64`) regenerate the renamed members automatically — verify a substituted `ComposeRowFilterAsync` returning a null `Task` doesn't NRE.
- **New:** memo invocation-count test (M2), streaming invocation-count + >30-batch survival test (M3/M4), memo-staleness/re-auth test (M3), custom-action many-items test (M5).
- Run the row-security suite as one batch: `RowFilterPushdownTests`, `RowLevelWithCheckTests`, `RowLevelQueryAuthorizationTests`, `QueryExecutorIntegrationTests`, `BreadcrumbResolverTests`, `CrossCollectionBindingTests`, `RowSecurityProjectionBatchingTests`, plus the new streaming tests; then the full `MintPlayer.Spark.Tests` sweep + CI.

## Milestone map

| Milestone | What | Blocks |
|---|---|---|
| M1 | async hook conversion | — |
| M2 | per-request memo | M1 (mandatory for M1 to be safe) |
| M3 | memo invalidation on re-auth tick | M2 |
| M4 | per-batch streaming session + uncap consumer session | — (independent bug; ships here) |
| M5 | custom-action loop + diagnostic | — |
| M6 | includes: fix dead `ApplyIncludes` + `GetDefaultIncludes()` | — (independent; ships here) |
| M7 | docs + version bump | M1–M6 |

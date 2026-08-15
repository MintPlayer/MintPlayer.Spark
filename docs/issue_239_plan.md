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
6. `Demo/WebhooksDemo/.../GitHubProjectActions.cs:27` — **the payoff**: migrate `IsAllowedAsync => _orgAccess.IsOwnerAllowedAsync(...)` to `GetRowFilterAsync` (await the org list, return `p => allowed.Contains(p.OwnerLogin)`); gains pushdown + WITH CHECK; closes `docs/issue_236_plan.md:604`. Delete the redundant `OnLoadAsync`/`OnBeforeSaveAsync`/`OnBeforeDeleteAsync` manual checks **only if tests confirm the framework gates cover them**.

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

## M6 — Docs + version bump

1. `docs/guide-row-security.md` (`:13,:20-46,:65,:94-97`) — signature → `GetRowFilterAsync`; add the async example (WebhooksDemo shape), the **at-most-once-per-(type,action)-per-request contract**, the stream-staleness note (~10 batches), the "pure per request" rule, and the "`IsAllowedAsync` is per-row / not memoized — use the filter for I/O rules" edge (PRD §5).
2. `libs/spark/MintPlayer.Spark/README.md:254-259` — signature; **delete the stale `OnQueryAsync` bullet at `:247`** (hook no longer exists).
3. `docs/issue_236_PRD.md` / `docs/issue_236_plan.md` — the M7 stub becomes a one-line "superseded by #239 — see `docs/issue_239_*`" pointer (don't rewrite history).
4. Version bump: `.NET → 10.0.0-preview.45` in lockstep across all 21 csproj (ng-spark untouched — no client-visible change). Do **not** publish by hand; bump + merge, CI publishes.

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
| M6 | docs + version bump | M1–M5 |

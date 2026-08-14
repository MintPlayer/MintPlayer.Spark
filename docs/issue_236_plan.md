# Implementation plan — Issue #236 (complete built-in row-level security)

See [issue_236_PRD.md](./issue_236_PRD.md) for the problem statement, evidence, and tracked decisions (D1–D5). **One PR per milestone, in dependency order.** Every milestone keeps row security in core, auth-package-agnostic (the packaging invariant), and keeps consumer code near-zero.

```
M0 (bug) ──► M1 (pushdown) ──► M2 (WITH CHECK)
   │                     └───► M5 (per-row "can") ──► M6 (perf, later)
   └────► M3 (custom actions)
M4 (redaction) — independent
```

## M0 — 🐛 Batch `RowSecurity.FilterAsync`'s projection reload

**File:** `libs/spark/MintPlayer.Spark/Services/RowSecurity.cs` (per-row `LoadAsync` at `:125`).

1. **Failing test first** (`tests/MintPlayer.Spark.Tests`): a row-scoped entity + projection index with **>30 seeded rows**; assert the filtered query succeeds (today it throws `MaxNumberOfRequestsPerSession` past ~29 rows) and returns exactly the allowed rows. Reuse the Fleet-shaped configuration (`Car`/`Cars_Overview`/`VCar`) or a test-local equivalent.
2. Collect the ids for the projection page, then one `session.LoadAsync<object>(string[])` per page; iterate results against the row rule.
3. Preserve the fail-closed branches byte-for-byte in behavior: unreadable/absent `Id` property → empty result (`:100-107`); empty id → row dropped (`:120-121`); base doc missing from the batch result → row dropped (`:126-127`).
4. Regression test for the fail-closed paths (a projection row whose base doc was deleted → dropped, not thrown).

**Depends on:** — . **Decision gates:** none.

## M1 — `GetRowFilter` expression pushdown

**Files:** `Actions/DefaultPersistentObjectActions.cs`, `Services/RowSecurity.cs`, `Services/QueryExecutor.cs`, `Services/StreamingQueryExecutor.cs`, `Services/DatabaseAccess.cs` (list path), Fleet demo.

1. Resolve **D1** (action-string vocabulary) and **D2** (bless anonymous-read pattern) — record answers in the PRD decision table before coding.
2. New optional virtual: `Expression<Func<T, bool>>? GetRowFilter(string action)` on `DefaultPersistentObjectActions<T>` (default `null`). Override detection via the `HasRowRule` reflection pattern (`RowSecurity.cs:71-82`), cached.
3. Composition into `IRavenQueryable` via `Queryable.Where` + `MakeGenericMethod`, following the `ApplySorting` template (`QueryExecutor.cs:552-578`). Wire **all** row-filter sites: database query path (`QueryExecutor.cs:176-177`), custom-query path (`:291-296`), streaming (`StreamingQueryExecutor.cs:85-107`), PO list (`DatabaseAccess.cs:145`).
4. **Derivation rule:** only `GetRowFilter` → single-row checks (get/edit/delete) use the compiled expression (cache the compile); only `IsAllowedAsync` → today's behavior; both → AND. Startup diagnostic logs each row-scoped type and its mode.
5. **Projection fallback:** when the query's element type ≠ the entity type (index projection, `QueryExecutor.cs:132-140`), fall back to post-filter using M0's batched reload + log a diagnostic. Never silently unfiltered.
6. Filter composes **before** materialization so `totalRecords`/paging are computed on the filtered set (no behavior change vs post-filter, minus the O(collection) cost on the pushdown path).
7. **Demo:** migrate Fleet `CarActions` to the expression form as the worked example (`CreatedBy == CurrentUserId || admin` as an expression; keep `IsAllowedAsync` deleted or as refinement per the derivation rule).
8. Tests: mode-derivation matrix (filter-only / predicate-only / both), projection fallback (diagnostic emitted, rows filtered), paging totals on filtered sets, anonymous-read pattern (D2) E2E.
9. Docs: README §row-level security + start `docs/guide-row-security.md` (consumer story incl. the D2 loud section).

**Depends on:** M0 (fallback uses batched reload). **Decision gates:** D1, D2.

## M2 — Create-side `WITH CHECK` + post-state Edit re-check

**Files:** `Services/DatabaseAccess.cs` (`EnsureSaveAuthorizedAsync` `:177-186`, id-less skip `:206`, Edit check `:220`), `Services/SyncActionHandler.cs` (`:46,:62`).

1. Resolve **D3** (system/module principal story) first — sync/replication writes go through the same chokepoint and must not be broken by user-scoped rules.
2. Create path: after the actions pipeline (`OnBeforeSaveAsync` etc.) has mutated the entity, evaluate the row rule (`IsAllowedAsync("New", entity)` or compiled `GetRowFilter("New")`) against the **resulting** state; deny → same error shape as the Edit gate (`SparkRowLevelAccessDeniedException`).
3. Update path: keep the pre-update check (`:220`), add a post-mutation re-check of `"Edit"` so a row can't be edited *into* someone else's scope.
4. Implement the D3 outcome (e.g. a system-context flag visible to row rules, or documented `Module:*` handling) and cover `SyncActionHandler` writes with tests: sync write to a row-scoped type must still succeed under the module principal.
5. Tests: create-with-foreign-owner denied; ownership-stamping app (Fleet pattern) still succeeds; edit-into-foreign-scope denied; sync-path regression.

**Depends on:** M1 (uses compiled filter). **Decision gates:** D3.

## M3 — Custom actions: server-resolved, row-gated `Parent`/`SelectedItems`

**Files:** `Endpoints/Actions/ExecuteCustomAction.cs` (type check `:44`, client POs `:60-72`), `MintPlayer.Spark.Abstractions/Actions/ICustomAction.cs` (`CustomActionArgs`).

1. Re-resolve `Parent` and each `SelectedItems` entry server-side by id through the row-gated load path (`GetPersistentObjectAsync`) before invoking the action.
2. Unresolvable or denied ids → **404** (no existence oracle, consistent with M-3).
3. `CustomActionArgs`: add server-loaded, row-checked entities; keep the client-supplied POs available only as *submitted values*. **Breaking change** for actions relying on client-supplied state — release-notes entry + migration note in `docs/guide-custom-actions.md`.
4. Tests: action naming a denied id → 404; action naming an allowed id gets the server-loaded entity; submitted values still flow for edit-style actions.

**Depends on:** M0 only (independent of M1/M2 — can ship in parallel with M1).

## M4 — Per-viewer attribute redaction (`GetProtectedAttributesAsync`)

**Files:** `Actions/DefaultPersistentObjectActions.cs`, `Services/EntityMapper.cs` (`PopulateAttributeValues` `:185-258`, AsDetail recursion `:268-320`).

1. Resolve **D4** (implement vs delete the dead property-level `security.json` rights) — if "delete", strip schema + docs in this PR; if "implement", the declarative half lands in the Authorization package behind the same core redaction funnel.
2. New optional virtual: `Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, T entity)` (default null = nothing). Override-detected; zero cost when absent.
3. Enforce in the `EntityMapper` funnel (single choke point, 5 call sites), including AsDetail children. Redaction = `Value = null` + `IsVisible = false` (redact, don't omit — `BreadcrumbResolver` precedent).
4. Write path: a redacted attribute submitted back must not clobber the stored value (round-trip safety) — decide and test (skip-on-write for protected names).
5. Demo: WebhooksDemo or Fleet gets a protected attribute as the worked example (stand-in for Coverage's `BadgeToken`).
6. Tests: redacted for non-managers, present for managers; AsDetail child redaction; write-back safety; zero-cost when not overridden.

**Depends on:** — (independent). **Decision gates:** D4.

## M5 — Per-row `can` block + ng-spark gating

**Files:** `Services/DatabaseAccess.cs` / `EntityMapper`, `PersistentObject.cs`, ng-spark `po-detail` (+ later `query-list`/`sub-query`).

1. Resolve **D5** (list-path opt-in vs automatic-when-cheap).
2. Detail path first: after the row-gated get, evaluate `"Edit"`/`"Delete"` for the single row; attach optional `"can": { "edit": bool, "delete": bool }` to the PO payload. Absent block = clients fall back to type-level flags (backward compatible).
3. ng-spark: `spark-po-detail` prefers the per-row block for Edit/Delete buttons; models updated (`PersistentObject` TS type).
4. List path per D5 outcome; measure before enabling (per-row × 2 actions per page — cheap only with a compiled `GetRowFilter`).
5. Tests: .NET payload shape (+ absence when no row rule); ng-spark Vitest for button gating both with and without the block.

**Depends on:** M1. **Decision gates:** D5.

## M6 — (perf, separate) Raven-side `Skip/Take` pushdown + `Statistics` totals

Deliberately out of the main series (`docs/PRD-CoverageHandoff.md:423`): collides with the in-memory search filter (`QueryExecutor.cs:47-59`), needs a story for search + benchmarks before/after. Only meaningful once M1 makes the row filter composable. Scope when M1–M5 have shipped.

## Verification sweep (end of series)

Per global working rules, full test suites run once at the end of each milestone PR, not per step: `dotnet test tests/MintPlayer.Spark.Tests` + affected demo E2E; ng-spark Vitest for M5. Coverage acceptance benchmark (PRD §Acceptance) re-checked against the shipped surface at the end of M5 — that's the point where Coverage can drop its hand-written controllers and DenyAll workaround.

## Milestone → decision map

| Milestone | Blocked by decisions | Blocked by milestones |
|---|---|---|
| M0 | — | — |
| M1 | D1, D2 | M0 |
| M2 | D3 | M1 |
| M3 | — | M0 |
| M4 | D4 | — |
| M5 | D5 | M1 |
| M6 | — | M1 (scope later) |

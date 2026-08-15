# PRD — Issue #236: complete built-in row-level security

- **Issue:** [#236](https://github.com/MintPlayer/MintPlayer.Spark/issues/236)
- **Branch:** `feat/issue-236-row-level-security`
- **Origin:** The Coverage app (MintPlayer/CodeCoverage) wants generic row-level authorization. It currently runs Spark with **DenyAll and no `security.json`** and re-implements its entire read surface as hand-written `[ApiController]`s, purely because of the gaps below. Its requirements are the acceptance benchmark for this work.
- **Lineage:** Finishes what the Coverage-handoff M5 started (`docs/coverage-handoff-plan.md` §M5). The declarative row-filter design there (`:584-598`) was specified, then explicitly superseded — the shipped M5 delivered only the single `IsAllowedAsync` hook. This PRD revives the deferred half with the "two hooks, four states" objection resolved (see G1).
- **Status:** **Shipped** (M0–M5) in PR #237, released as `10.0.0-preview.44` / `@mintplayer/ng-spark@22.0.9` (2026-08-14). M6 (Raven `Skip/Take` pushdown) remains a separate perf PR. **Active follow-up (2026-08-15): `GetRowFilter` → async `GetRowFilterAsync`** — see the "Async follow-up" note under G1 and the checklist in [issue_236_plan.md](./issue_236_plan.md). All `file:line` claims below reflect the shipped design (`master` @ 5d5fce0) unless a line is marked superseded.

## What already ships (baseline — do not rebuild)

Row-level security lives in **core** (`MintPlayer.Spark`), auth-package-agnostic:

- `IRowSecurity` / `RowSecurity` — `libs/spark/MintPlayer.Spark/Services/RowSecurity.cs`
- Per-entity hook: `DefaultPersistentObjectActions<T>.IsAllowedAsync(string action, T entity)` (`Actions/DefaultPersistentObjectActions.cs:92`), override-detected via `HasRowRule` (`RowSecurity.cs:71-82`) so unoverridden types pay zero cost.
- Enforced on: PO get (`DatabaseAccess.cs:99`), PO list (`:145`), Edit (`:220`, pre-update state), Delete (`:264`), query execute (`QueryExecutor.cs:176-177`, plus the custom-query path `:291-296`), streaming (`StreamingQueryExecutor.cs:85-107`), breadcrumb reference loads (`BreadcrumbResolver.cs:115-129`).
- Consumer cost is already near-zero: Fleet's `CarActions.IsAllowedAsync` is 6 lines (`Demo/Fleet/Fleet/Actions/CarActions.cs:33-39`); WebhooksDemo's `GitHubProjectActions` is 2 (`Demo/WebhooksDemo/WebhooksDemo/Actions/GitHubProjectActions.cs:27-28`).

**Packaging constraint (invariant for every milestone):** row security stays in core and needs nothing from `MintPlayer.Spark.Authorization` (whose only core touchpoint remains `IAccessControl`). The Authorization package gets involved only if the declarative surface of G4 option A is chosen (Decision D4).

## Acceptance benchmark (Coverage's requirements)

1. Anonymous viewers see **public** repositories; authenticated viewers additionally see private repos their GitHub identity can access. (WebhooksDemo's `IsAllowedAsync => _orgAccess.IsOwnerAllowedAsync(entity.OwnerLogin)` is ~90% of this.)
2. Collections are large (all commits/builds of all repos) — post-materialization filtering of whole collections is not acceptable at that scale. → **G1**
3. `Repository.BadgeToken` must be visible **only** to viewers who can manage that repository — per-row, per-attribute. → **G4**
4. The generic UI must not render Edit/Delete buttons that will 404. → **G5**

*(Historical note: Coverage's `Program.cs` cites finding "R4-H1" as the reason for DenyAll; per `docs/prd/PRD-SecurityAudit.md` that identifier is fabricated, and the real findings H-2/H-2a were resolved in M5. The remaining reasons are exactly the gaps below.)*

## G0 — Bug: unbatched per-row `LoadAsync` breaks projection-backed row-scoped queries

`RowSecurity.FilterAsync` (`RowSecurity.cs:84-137`): when `resultType != entityType` (projection), it loads the base document **once per row** — `await session.LoadAsync<object>(id)` inside the `foreach` at `:125`. The request session has RavenDB's default `MaxNumberOfRequestsPerSession = 30`, so a projection-backed query on a row-scoped entity **throws past ~29 rows**. Fleet's `GetCars` (`Car` + `Cars_Overview`/`VCar` + `CarActions`) is exactly this configuration and survives E2E only because tests seed a handful of cars.

**Fix:** batch with `session.LoadAsync<object>(IEnumerable<string>)` — one request per page — as `docs/coverage-handoff-plan.md:598` already specified. The mechanics fall out of what the code already has in hand: the projection's `Id` property holds the *base document's* id (the existing `idGetter` reads it per row today), so collect the ids up front, do one untyped batch load (RavenDB materializes the CLR type from `Raven-Clr-Type` metadata — no compile-time type needed, exactly like today's untyped `LoadAsync<object>(id)`), and the per-row loop becomes a dictionary lookup. Keep the fail-closed branches exactly as shipped, which map one-to-one onto the dictionary shape: unreadable/absent `Id` property → `return []` (`:100-107`); empty id → excluded from the batch, row dropped (`:120-121`); deleted base doc → null value in the result dictionary, row dropped (`:126-127`). The session dedupes ids, serves already-tracked docs from cache, and iterating the original `entities` order preserves row order — output byte-identical to today, minus N−1 round-trips.

Independent of everything else; ships first.

## G1 — Expression pushdown: let the row rule compose into the RavenDB query

**Today:** the row filter runs after full materialization. `QueryExecutor.ExecuteQueryAsync` (`Services/QueryExecutor.cs:32-73`) reads the whole collection, maps every row, then filters and pages in memory (search filter `:47-59`, `ToList` + `TotalRecords` `:61-62`, `Skip/Take` `:64`) — permanently O(collection).

**Proposal:** one optional member next to the existing hook. **The hook is `async` — construction can `await`; the returned expression stays synchronous** so it still translates into a RavenDB `IQueryable` (see the async follow-up note below):

```csharp
public class RepositoryActions : DefaultPersistentObjectActions<Repository>
{
    // NEW — composes into the Raven query; evaluated per request. Construction may await
    // (fetch an allow-list); the returned Expression is synchronous and RavenDB-translatable.
    public override async Task<Expression<Func<Repository, bool>>?> GetRowFilterAsync(string action)
    {
        var owners = await orgAccess.GetAllowedOwnersAsync();   // request-scoped data captured as constants
        return r => !r.IsPrivate || owners.Contains(r.OwnerLogin);
    }
}
```

- **Framework mechanics:** override detection reuses the shipped `HasRowRule` pattern (`RowSecurity.cs:71-82`). Composition via `Queryable.Where` + `MakeGenericMethod` follows the working `ApplySorting` template (`QueryExecutor.cs:552-578`) — dispatch is untyped but `entityType`/`resultType` are in hand at every call site. Must cover **both** row-filter call sites: the database path (`:176-177`) and the custom-query path (`:291-296`).
- **Derivation rule (resolves the "two hooks, four states" objection that killed this in M5):** define derivation, not interaction. Only `GetRowFilter` overridden → the framework derives the single-row check by compiling the expression (cached) — one source of truth, list and detail can't diverge. Only `IsAllowedAsync` overridden → exactly today's behavior (post-filter). Both → AND semantics (filter narrows, predicate refines). A startup diagnostic names each type and which mode it runs in.
- **Projection fallback, never silent:** a predicate typed on `Car` can't compose into `IRavenQueryable<VCar>` (`Cars_Overview` auto-selected by `IndexRegistry`, `QueryExecutor.cs:132-140`). Fall back automatically to post-filter with the G0 batched reload, and emit a diagnostic — never silently unfiltered.
- **Paging/totals stay correct for free:** compose the filter before `ToListAsync`; `totalRecords` and `Skip/Take` derive from the post-filter list today. Raven-side `Skip/Take` pushdown (with `Statistics`) is a **separate, later** perf PR (M6) — it collides with in-memory search and needs its own benchmarks (as `docs/PRD-CoverageHandoff.md:423` concluded).

### Async follow-up (2026-08-15) — `GetRowFilterAsync`, no backward compatibility

**Motivation:** the Coverage app needs to `await` while *building* the filter (its allow-list comes from an async service call — `await orgAccess.GetAllowedOwnersAsync()`), which the shipped synchronous `GetRowFilter` can't express. **Decision:** rename the hook to `GetRowFilterAsync` returning `Task<Expression<Func<T,bool>>?>`; **the old synchronous signature is removed, not kept as an overload** (no backward compatibility — the framework is still in preview).

Only *construction* becomes async — the returned `Expression<Func<T,bool>>` is unchanged and still translates into the RavenDB `IQueryable`, so the pushdown, derivation rule, projection fallback, and WITH CHECK all keep working exactly as designed above. The change is mechanical: the hook + the framework's reflective invocation (`RowSecurity.InvokeGetRowFilter` → awaited via the cached `Task.GetCompletedTaskResult()` helper), plus `ComposeRowFilter` → `ComposeRowFilterAsync` and its three `await`ed call sites. `HasRowRule` stays synchronous (it only checks whether the method is overridden). Full edit checklist in the plan.

## G2 — Create-side `WITH CHECK`: writes that *produce* rows you couldn't see

**Today:** in SQL RLS terms Spark implements `USING` but not `WITH CHECK`. `Create.cs:58` forces `Id = null` and `SavePersistentObjectAsync` skips the row gate for id-less saves (the gate sits inside `if (!string.IsNullOrEmpty(persistentObject.Id))`, `DatabaseAccess.cs:206`) — nothing stops an authenticated caller creating a document stamped with someone else's tenant/owner. Apps paper over it in `OnBeforeSaveAsync` (`CarActions.cs:41-46` stamps `CreatedBy`), which works only if the app remembers.

**Proposal:** after the actions pipeline has mutated the entity (so ownership stamping has happened), evaluate the row rule against the **resulting** state:

- create → `IsAllowedAsync("New", entity)` (or the compiled `GetRowFilter("New")`);
- update → re-check `"Edit"` against the **post-update** state in addition to the existing pre-update check (`DatabaseAccess.cs:220`), so a caller can't edit a row *into* someone else's scope.

Zero new consumer surface — same hook, two more action strings, framework-enforced. Requires Decision D3 (system principal): `SyncActionHandler` routes module-to-module writes through the same chokepoint (`SyncActionHandler.cs:46,62`) under an mTLS module principal, and a user-scoped rule must not break legitimate sync writes.

**As-built caveat (service/machine accounts).** The create-side WITH CHECK means a per-user ownership filter now also gates create — so a principal with the type-level `New` right but *no user id* (a machine / client-credentials token, which is **not** a `SparkSystemContext` mTLS module) is denied if the filter degenerates to "deny all" for "no user". This surfaced as a CI failure on the Fleet machine-token test. The consumer fix (shown in Fleet's `CarActions.GetRowFilter`, documented in `guide-row-security.md`): return `null` (unrestricted) for an authenticated principal with no user id, and reserve the deny-everything branch for a *truly anonymous* caller.

## G3 — Custom actions: the one endpoint with no row enforcement at all

`ExecuteCustomAction.cs`: type-level check at `:44`, then the action receives **client-supplied** `Parent` and `SelectedItems` (deserialized off the wire at `:60`, args built at `:68-72`, never re-loaded or row-checked; the class injects no `IRowSecurity` at all). A caller with the type-level action right can name any id of that type. `ListCustomActions.cs:42` filtering the *menu* makes the surface look narrower than it is.

**Proposal:** the framework re-resolves `Parent`/`SelectedItems` server-side by id through the row-gated load path (`GetPersistentObjectAsync`) before invoking the action; unresolvable/denied ids → 404 (consistent with M-3's no-existence-oracle rule). `CustomActionArgs` grows server-loaded, row-checked entities; the client-supplied POs remain available only as the *submitted values* (for actions that edit). **Breaking change** for actions that relied on client-supplied state — call out in release notes.

## G4 — Per-viewer attribute redaction (the `BadgeToken` problem)

**Today:** no read-path hook sees the mapped `PersistentObject` (`IPersistentObjectActions<T>` has `OnLoadAsync` returning `T`, nothing PO-shaped; `OnQueryAsync` was deleted in M5 for having zero call sites). `IsVisible` is outbound-advisory only — `EntityMapper.PopulateAttributeValues` (`EntityMapper.cs:185-258`) never consults it, so `IsVisible = false` still ships the value in JSON. And `security.json`'s documented property-level rights (`Read/Type/Property`) are dead code — `MatchesResource` is exact string equality (`AccessControlService.cs:120-123`, used at `:70,:77`).

**Proposal (core):** one more optional Actions member, evaluated per row at mapping time:

```csharp
// NEW — names attributes to redact for this viewer on this row. Null/empty = nothing.
public override Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Repository entity)
    => CanManage(entity) ? Task.FromResult<IReadOnlyCollection<string>?>(null)
                         : Task.FromResult<IReadOnlyCollection<string>?>(["BadgeToken"]);
```

Enforced in `EntityMapper.ToPersistentObject`/`PopulateAttributeValues` (the single funnel — 5 call sites), including recursion into AsDetail children (`EntityMapper.cs:268-320` — precisely where embedded rows need redaction, since they can't be row-filtered). Redaction = `Value = null` + `IsVisible = false`, following `BreadcrumbResolver`'s precedent (`RedactedPlaceholder`, `BreadcrumbResolver.cs:139,153`, default `"—"` from `SparkOptions.cs:24`) of redacting rather than omitting — dropping the attribute would break name-indexed clients and leak the rule via schema mismatch. Zero cost when not overridden (same override-detection pattern).

**Option A (declarative, Authorization package):** additionally implement the documented property-level rights — an attribute becomes *protected* when any property-scoped rule names it, granted per group. Covers the static per-role case with zero consumer code; the code hook covers the per-row case. → Decision D4.

## G5 — Per-row permissions for the generic UI

**Today:** `GET /spark/permissions/{entityTypeId}` returns exactly four type-level booleans (`GetPermissions.cs:26-31`), and neither `PersistentObject` (`PersistentObject.cs:5-66`) nor the query payload carries any per-row flag. `spark-po-detail`/`spark-query-list` gate Edit/Delete/New off type-level flags, so a row the caller may read but not edit renders an Edit button that fails at `DatabaseAccess.cs:220` as a 404.

**Proposal (opt-in, computed only when a row rule exists):** after `FilterAsync`, evaluate `"Edit"`/`"Delete"` per surviving row — cheap when `GetRowFilter` exists (compiled predicate, in-memory, entities already materialized) — and attach an optional block to the PO payload (e.g. `"can": { "edit": false, "delete": false }`; absent = fall back to type-level flags, fully backward-compatible). ng-spark: `spark-po-detail` prefers the per-row block for its Edit/Delete buttons; `spark-query-list`/`spark-sub-query` may later use it for row affordances. Detail path first (1 row, negligible cost); list path measured before enabling. → Decision D5.

## Non-goals

- Raven-side `Skip/Take` pushdown + `Statistics` totals — deferred to M6 (perf PR with benchmarks; collides with in-memory search).
- The related findings below — separate issues, not this PR series.
- Changing the DenyAll default or type-level permission model.

## Related findings (separate issues unless folded in)

- **`parentId`/`parentType` validated then ignored for `Database.*` queries** (`Execute.cs:97-109` → `QueryExecutor.cs:36-44`): a `Database.*` sub-query returns the whole collection regardless of parent. Silently breaks `spark-sub-query` on model-declared relations. Deserves its own issue.
- `IDatabaseAccess.Get*UncheckedAsync`/`Save…`/`Delete…Unchecked` are public bypasses of every gate on the injected interface — need a loud contract or restriction if RLS is an invariant.
- Streaming principal never re-validated after connect (partially-open R2-M4).
- `ProgramUnits/Get.cs:88-91` fails **open** when `BuildContextPropertyMap` throws; lookup-reference `Get`/`List` endpoints have no permission check at all.

## Decisions (RESOLVED 2026-08-14 — recorded before implementation)

| # | Question | Needed by | Resolution |
|---|---|---|---|
| D1 | Action-string vocabulary for `GetRowFilter(action)` — per-action parameter or parameterless? | M1 | **Keep the `action` parameter** (`"Query"/"Read"/"Edit"/"Delete"/"New"`). Costs nothing to ignore in the common case, and G2 routes `"New"` through the same rule; `IsAllowedAsync` remains the per-action refinement point. |
| D2 | Anonymous read: is type-level `Query`/`Read` to `Everyone` + a row filter the blessed pattern? | M1 docs | **Yes — blessed and documented loudly** in `docs/guide-row-security.md`: when granted to Everyone, the row filter is the only thing between the public internet and the collection. |
| D3 | System/module principal vs row rules (sync/replication writes under module mTLS principals). | M2 | **Explicit system context, positive-claim-only:** a request whose principal carries the `SparkSystemContext` claim (stamped by the module cert + HTTP-sync principal factories) is exempt from row rules — row security scopes *user* visibility; module-to-module sync is infrastructure already authenticated via mTLS. **The absence of an HTTP request is NOT system context** — that is the default state of every non-request path, and treating it as exempt would silently switch row security off wherever there is no live request. Fail closed: no proven system claim ⇒ treated as a viewer, rules apply. No `Module:*` special-casing inside consumer rules. Cross-ref `docs/findings-replication-mtls.md:189` (F9). |
| D4 | Property-level `security.json` rights: implement (Authorization package) or delete the dead schema/docs? | M4 | **Delete the dead promise** from docs (and schema surface if any) in M4 — minimal diff, honest docs. The core `GetProtectedAttributesAsync` hook covers the per-row case (Coverage's actual need); the static per-role case is one-line consumer code through the same hook. Declarative surface deferred to a follow-up issue filed at PR time (tracked, not forgotten). |
| D5 | G5 list-path cost: opt-in flag or automatic-when-cheap? | M5 | **Automatic-when-cheap, no knob:** detail path always computes the `can` block when a row rule exists (1 row); list path computes it only when a compiled `GetRowFilter` is available (in-memory predicate over already-materialized entities — negligible). No per-type opt-in flag — pull complexity down, no configuration surface. |

## Docs deliverables (per milestone)

`libs/spark/MintPlayer.Spark/README.md` §row-level security, plus a new `docs/guide-row-security.md` consolidating the consumer story (today spread across audit findings and the README).

## Evidence base

`docs/prd/PRD-SecurityAudit.md` (H-2/H-2a/M-1/M-3/R2-H1/R2-H2/R2-H4/R2-H8/R2-H10/R2-M4), `docs/coverage-handoff-plan.md` §M5 (`:547` header; superseded `GetRowFilter` design `:584-598` — explicitly never shipped, per the banner at `:549-563`), `docs/PRD-CoverageHandoff.md` §5/§7 (`:367,:423`), `docs/findings-replication-mtls.md:189,232,281` (F9 row-level scoping for module tokens). All code citations re-verified against `master` (c3be2ed) on 2026-08-14.

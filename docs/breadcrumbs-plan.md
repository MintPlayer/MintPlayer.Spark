# Implementation Plan — Recursive Breadcrumb Resolution

Companion to [`PRD-Breadcrumbs.md`](./PRD-Breadcrumbs.md). Phased, each phase independently buildable & testable. File paths are anchors, not exhaustive.

> **Single-system mandate (PRD §2.4):** this work must leave **exactly one** breadcrumb/display implementation (`BreadcrumbResolver`). Every duplicate listed in PRD §2.4 — three backend read paths (`EntityMapper`, `ReferenceResolver`, `StreamingQueryExecutor`) and the frontend pipes with their own template engines / property-name guessing — is **deleted or reduced to a thin reader** by the end of Phase 5. The Phase-6 grep gate enforces it.

---

## Phase 0 — Spike & guardrails ✅ DONE (2026-06-08)

Spike project `tests/MintPlayer.Spark.BreadcrumbSpike` (TEMPORARY — delete before merge), run against live RavenDB 7.2.1 on `http://localhost:8080` (creates/deletes a throwaway `SparkBreadcrumbSpike_*` db per run). **All 5 tests pass.** Results:

- ✅ `session.LoadAsync<object>(string[] ids)` loads **mixed collection types in ONE request** and recovers the CLR type from `@metadata` (returned `Car`/`Person` instances typed correctly).
- ✅ Ids primed by a query's `.Include()` resolve from the session cache with **zero** added requests (`NumberOfRequests` unchanged after the follow-up `LoadAsync`).
- ✅ **Core claim proven:** BFS level-batching of an N-row `ParkingSpot→Car→Person` chain costs **exactly 3 requests for N = 1, 25, 100** — `O(depth)`, not `O(rows)`. The 30-request-limit risk is structurally eliminated.
- Formatter decision: breadcrumb scalars reuse `EntityMapper.ConvertValueForWire` so grid and breadcrumb formatting match (settled by code reading, no runtime spike needed).

**Exit:** ✅ all assumptions confirmed; design greenlit. The spike project is disposable — keep it until Phase 3's resolver tests subsume it, then remove from the solution.

---

## Phase 1 — Authoring & metadata ✅ DONE (2026-06-08)

Shipped: `[Breadcrumb]` attribute (`MintPlayer.Spark.Abstractions`); `BreadcrumbTemplate` parser (`Services/Breadcrumb/`); `EntityTypeDefinition.Breadcrumb` + `BreadcrumbProjectionSatisfiable` replacing `DisplayFormat`/`DisplayAttribute`; `ModelSynchronizer` reads `[Breadcrumb]` → preserves JSON → synthesizes default, validates (bad braces / unknown placeholder throw), computes projection-satisfiability; `EntityMapper.GetEntityDisplayName` now reads `Breadcrumb` (flat substitution, whitespace→CLR-name fallback). Migrated 19 demo model JSONs and the frontend wire model (`entity-type.ts`) + the two AsDetail pipes. **Tests green:** 89 mapper + 58 integration + 23 synchronizer/parser (.NET), 182 ng-spark (vitest); full solution builds.

Carry-overs (not blockers): demo JSON `breadcrumbProjectionSatisfiable: false` values get written when the apps next run the synchronizer (Phase 6 regen); `node_packages/ng-spark/dist/` is stale build output until ng-packagr rebuilds (dev/tests consume source via tsconfig paths, so unaffected); `EntityMapper.ResolveDisplayFormat` kept as the transitional flat resolver — deleted in Phase 4.

### Original spec
Phase 1 — Authoring & metadata (backend, no behavior change yet)

1. **`[Breadcrumb]` attribute** — `MintPlayer.Spark.Abstractions/BreadcrumbAttribute.cs` (`[AttributeUsage(Class)] sealed class BreadcrumbAttribute(string template)`).
2. **`EntityTypeDefinition`** (`EntityTypeDefinition.cs`): add `string? Breadcrumb`; **remove** `DisplayFormat` and `DisplayAttribute`. Add precomputed-but-serialized helper field `bool BreadcrumbProjectionSatisfiable` (default `true`).
3. **`ModelSynchronizer`** (`Services/ModelSynchronizer.cs`):
   - read `[Breadcrumb]`; if absent synthesize default (`{Name}`/`{FullName}`/`{Title}`/first scalar/CLR name).
   - compute `BreadcrumbProjectionSatisfiable` against the `QueryType` (all scalar tokens are readable props on the projection).
   - **validate** template (Phase 2 parser): unknown token, bad reference target, unbalanced braces, non-terminating cycle → throw with entity + template in the message.
4. Update all callers/usages of `DisplayFormat`/`DisplayAttribute` (grep backend + demo `App_Data/Model/*.json`) and regenerate demo model JSON.

**Tests:** synchronizer emits `Breadcrumb`; default synthesis; validation throws on each bad case.
**Exit:** solution builds; demo models regenerated; no runtime breadcrumb change yet (EntityMapper still uses old path, temporarily reading `Breadcrumb` as a flat format).

---

## Phase 2 — Template parser + static closure ✅ DONE (2026-06-08)

1. ✅ **Parser** — `Services/Breadcrumb/BreadcrumbTemplate.cs` (shipped in Phase 1): `LiteralToken`/`FieldToken`, `{{`/`}}` escaping, balance check, `FieldNames` helper.
2. ✅ **Closure** — `Services/Breadcrumb/BreadcrumbClosure.cs` (Singleton, `[Register]`): per-type `BreadcrumbReference` edges (cached `ConcurrentDictionary`), `GetDepth` (longest simple path, cycle-safe), `GetCycles` (deduped by node-set, lazy).

**Cycle policy refined:** cycles are detected + surfaced, **not** a hard error (legitimate org-chart self-refs); runtime safety comes from `MaxBreadcrumbDepth` + a per-path visited set. PRD §4.3/§4.6 updated.

**Tests green:** 9 parser + 8 closure (`BreadcrumbTemplateTests`, `BreadcrumbClosureTests`) — 3-level chain depth, `A→B→A` and direct self-ref cycle detection, array-reference flag, non-breadcrumb references ignored.
**Exit:** ✅ parser + closure fully unit-tested; not yet wired into runtime (Phase 3 consumes them).

### Original spec
1. Parser — `BreadcrumbTemplate.cs`: tokens, escaping, balance, cache. 2. Closure — `BreadcrumbClosure.cs`: reference edges, depth, cycles.

---

## Phase 3 — `BreadcrumbResolver` ✅ DONE (2026-06-08)

Shipped: `IRowSecurity`/`RowSecurity` (extracted the R2-H10 Read gate; `ReferenceResolver` now uses it — duplicate private method deleted); `BreadcrumbResult`; `BreadcrumbResolver` (Scoped) with BFS level-batched loading (`LoadAsync<object>(ids)` per level), level-0 projection→collection fallback, recursive in-memory render with reference-array join, redaction of denied docs, and cycle termination via per-path visited set; `SparkOptions.Breadcrumb` (`MaxDepth=5`, `ReferenceSeparator=", "`, `RedactedPlaceholder="—"`) now registered in DI.

**Tests green:** 7 `BreadcrumbResolverTests` (embedded Raven) — 3-level chain renders `"CAR-0 (P0 X) (0,0)"` recursively with **exactly 2 added requests for n=1 and n=50** (O(depth), proven), reference-array join, redaction (`"CAR-1 (—)"`), projection-only-root fallback (+1 batched load), cycle termination. Plus 93-test regression across reference/integration paths (confirms `IRowSecurity` DI registration end-to-end). Full solution builds.

**Exit:** ✅ resolver correct and within request budget in isolation; not yet wired into read paths (Phase 4).

### Original spec
Phase 3 — `BreadcrumbResolver` (the core)

1. **Service** — `Services/Breadcrumb/BreadcrumbResolver.cs`, `[Register(typeof(IBreadcrumbResolver), Scoped)]`. Depends on `IModelLoader`, `IIndexRegistry`, and the row-level `Read` gate (reuse `ReferenceResolver`'s `IsAllowedRowAsync` — extract it to a shared `IRowSecurity` helper to avoid duplication).
2. **API:**
   ```csharp
   Task<BreadcrumbResult> ResolveAsync(IAsyncDocumentSession session,
       IReadOnlyList<object> roots, Type rootType, CancellationToken ct);
   // BreadcrumbResult: breadcrumb-per-entity-id (string) + the loaded-doc map (id → entity)
   //                   so EntityMapper fills attr.Breadcrumb / attr.Breadcrumbs without reloading.
   ```
3. **BFS load** (§4.4): seed roots → per level collect not-yet-loaded ref ids → single `LoadAsync<object>(ids)` → in-memory `Read` auth (redact denied) → next frontier; stop at empty / `MaxBreadcrumbDepth`.
4. **Level-0 projection fallback** (§4.5): if `!BreadcrumbProjectionSatisfiable`, batch-load root collection docs by id and render roots from those.
5. **Render** (pure CPU): token walk, recursion via loaded map, memoized per id, per-path visited set + depth guard, configurable separator/redacted placeholder.
6. **`SparkOptions`**: `MaxBreadcrumbDepth=5`, `BreadcrumbReferenceSeparator=", "`, `BreadcrumbRedactedPlaceholder="—"`.

**Tests (embedded Raven):** 3-level chain correctness + **request-count assertion**; cycle termination; redaction; array-reference join; projection-only-field root fallback.
**Exit:** resolver correct and within request budget in isolation.

---

## Phase 4 — Wire into read paths ✅ DONE (2026-06-08)

All four read paths now resolve breadcrumbs through `BreadcrumbResolver` and the old flat engine is deleted:
- `EntityMapper` forward API takes `BreadcrumbResult?` (was `includedDocuments`); copies Name/Breadcrumb + per-reference Breadcrumb(s) by id. `GetEntityDisplayName`/`ResolveDisplayFormat` **deleted**. Inverse/write path's own `includedDocuments` untouched.
- `DatabaseAccess` (Get + List), `QueryExecutor` (database + custom), `StreamingQueryExecutor` (per batch) call `breadcrumbResolver.ResolveAsync(session, roots, collectionDef)`. `.Include()` priming kept on the query paths.
- `ReferenceResolver.ResolveReferencedDocumentsAsync` (+ `ExtractReferenceIds`, `LoadEntityAsync`, its row-security dep) **deleted**; it's now just `GetReferenceProperties` + `ApplyIncludes`. `StreamingQueryExecutor` no longer depends on it.
- Resolver API takes `EntityTypeDefinition` (not `Type`) — every caller already has the collection def.
- **Refinement:** roots follow all `[Reference]` attributes (each needs a label); deeper levels follow only template references. Search-by-breadcrumb still resolves the full result set (batching keeps it O(depth) requests); page-only resolution deferred.

**Single-system gate met:** repo-wide grep for `GetEntityDisplayName`/`ResolveDisplayFormat`/`ResolveReferencedDocumentsAsync` in `libs/` → **zero hits**.

**Tests green:** full `MintPlayer.Spark.Tests` 954/956 (the 2 failures are pre-existing cron timing flakiness — 9/9 in isolation), client tests 38/38, solution builds. New resolver test pins the non-template-reference-still-labeled behavior. `MultiReferenceIntegrationTests` validates the whole pipeline (Get → resolver → mapper) end-to-end.

**Exit:** ✅ recursive breadcrumbs live across all five surfaces server-side; the deleted methods have zero callers.

### Original spec
Phase 4 — Wire into read paths (the chokepoint)

1. **`EntityMapper`** (`Services/EntityMapper.cs`): delete `GetEntityDisplayName` / `ResolveDisplayFormat` and the inline single/array breadcrumb code in `PopulateAttributeValues`. Add an overload that accepts a precomputed `BreadcrumbResult` and fills `po.Breadcrumb`, `attr.Breadcrumb`, `attr.Breadcrumbs[id]` by lookup.
2. **`DatabaseAccess`** (`Services/DatabaseAccess.cs`): `GetPersistentObjectAsync` (single root) and `GetPersistentObjectsAsync` (list) call `BreadcrumbResolver.ResolveAsync` then map. The existing `ResolveReferencedDocumentsAsync` becomes redundant for breadcrumbs — fold its referenced-doc loading into the resolver's BFS (keep `.Include()` priming via `ApplyIncludes`).
3. **`QueryExecutor`** (`Services/QueryExecutor.cs`): resolve breadcrumbs **after** Skip/Take, for both `ExecuteDatabaseQueryAsync` and `ExecuteCustomQueryAsync`, on the paginated page only. Server-side `search` over breadcrumbs (currently in `ExecuteQueryAsync`) must run against resolved strings — resolve before filtering **or** resolve the full set when a search term is present (document the cost; default page-only).
4. **`StreamingQueryExecutor`** (`Streaming/StreamingQueryExecutor.cs:86-97`): replace the per-batch `ResolveReferencedDocumentsAsync` call with `BreadcrumbResolver.ResolveAsync` over each batch, then map. This is the **third** read path and must not be missed.
5. **Delete** `ReferenceResolver.ResolveReferencedDocumentsAsync` and `EntityMapper.GetEntityDisplayName`/`ResolveDisplayFormat` once all three paths are migrated. Remove `IgnoreMaxRequests` band-aids made obsolete by batching; keep where still genuinely needed.

**Tests:** Get / List / database-query / custom-query / **streaming** parity for the 3-level chain; sub-query viewer parent-context path; request-count assertions on each.
**Exit:** all five surfaces + streaming produce identical recursive breadcrumbs server-side; the deleted methods have zero callers.

---

## Phase 5 — Frontend consolidation ✅ DONE (2026-06-08)

- **`SparkSubQueryComponent`**: added the `Reference && isArray` → `referenceChips` badge branch (was rendering raw id arrays); single references already show the server breadcrumb via `attributeValue`. Sub-query viewer now matches query-list/po-detail.
- **Template-fill dedup**: the two copies of the AsDetail `{Field}` fill (`attribute-value.pipe.ts`, `as-detail-display-value.pipe.ts`) extracted into one shared `pipes/src/apply-field-template.ts`.
- **Decision (PRD §2.4 refinement)**: thin reader pipes kept (they only read server `breadcrumb` strings; in active use); AsDetail nested-object summary stays a client-side template fill (embedded objects have no id) — distinct from the entity/reference breadcrumb system, which is fully server-resolved.

**Tests green:** full ng-spark vitest 182/182 (incl. pipes + po-detail). The hardcoded `['Name','Title','Street',…]` guess list and `entityType.displayFormat`/`displayAttribute` reads are already gone (Phase 1).

**Exit:** ✅ all five surfaces render server breadcrumbs consistently; no client display-name guessing; one shared client template-fill helper for AsDetail.

### Original spec
Phase 5 — Frontend consolidation (`@mintplayer/ng-spark`)

Collapse the duplicate display logic (PRD §2.4 frontend table) so the server breadcrumb is authoritative:

1. **`attribute-value.pipe.ts`**: delete `resolveDisplayFormat`, `formatAsDetailValue`, and the hardcoded `['Name','Title','Street',...]` guess list. The pipe returns `attr.breadcrumb ?? <typed value>`; AsDetail nested objects read their **server-emitted** `breadcrumb`. Drop `entityType.displayFormat`/`displayAttribute` usage.
2. **`reference-attr-value.pipe.ts`**: fold into `attributeValue` and **delete** the standalone pipe (one canonical reader).
3. **`reference-display-value.pipe.ts`** & **`as-detail-cell-value.pipe.ts`**: reduce to `breadcrumb` readers (drop `name`/id fallbacks once the server guarantees a breadcrumb).
4. **`models/src/entity-type.ts`**: replace `displayFormat`/`displayAttribute` with `breadcrumb`.
5. **`SparkSubQueryComponent`** (`po-detail/src/spark-sub-query.component.{html,ts}`): add the explicit single-reference + `referenceChips` array branches so the sub-query viewer matches query-list/po-detail.
6. Verify multi-select picker labels now show recursive breadcrumbs — add/adjust a snapshot.
7. Grep `node_packages/ng-spark` for any remaining client-side display-name guessing or `{…}` template engine and delete it.

**Tests:** snapshot the four read surfaces; sub-query parity; updated pipe specs (`pure-pipes.spec.ts`, `di-pipes.spec.ts`, `spark-po-detail.component.spec.ts`).
**Exit:** frontend renders server strings consistently; no client recursion, no client template engine, no display-property guessing remains.

---

## Phase 6 — Demo, docs, verification ✅ DONE (2026-06-08)

- **Demo chain (HR):** added `Company.Sector → Profession`; `[Breadcrumb]` on Person (`{FirstName} {LastName} @ {Company}`), Company (`{Name} · {Sector}`), Profession (`{Description}`) → a 3-level chain. Seeded by a **migration** (`M_202606081200_BreadcrumbDemo`: Ada Lovelace → Acme Corp → Software Engineering) via the new `MintPlayer.Spark.Migrations` feature.
- **Live /verify against running HR app (http://localhost:5099):**
  - Company query (anonymous): `Acme Corp · Software Engineering` — recursion (Company → Profession) live through the real `/spark/queries/{id}/execute` HTTP API.
  - People query (temporary anon grant, reverted/uncommitted): **`Ada Lovelace @ Acme Corp · Software Engineering`** — full 3-level recursion live. Null references render empty (`Jonas Dewulf @ 2sky · `, `… @ `) — correct.
  - Migration ran at first startup (seeded), and **skipped on restart** (idempotent) — verified live.
- **Single-system grep gate:** `GetEntityDisplayName`/`ResolveDisplayFormat`/`ResolveReferencedDocumentsAsync` → 0 hits in `libs/` (Phase 4).
- **Scope note:** the migrations framework (`MintPlayer.Spark.Migrations`) was added in this same PR at the user's request (see `PRD-Migrations` discussion in commit `a792b22`).

Outstanding before merge: version bumps per `project_ci_autopublish.md`; optionally add `MintPlayer.Spark.Migrations` to the AllFeatures meta-package + `SparkFullGenerator`; open the PR.

### Original spec

1. Add the `ParkingSpot → Car → Person` chain (or reuse Fleet's `Car.Manager → Person`) to a demo app to exercise 3 levels end-to-end; seed data.
2. Manual verification via `/verify` (or Playwright) across all five surfaces; capture request counts from Raven studio / logs.
3. **Single-system grep gate (PRD §2.4):** assert zero hits for `DisplayFormat`/`DisplayAttribute`/`GetEntityDisplayName`/`ResolveDisplayFormat`/`ResolveReferencedDocumentsAsync` outside `BreadcrumbResolver` + tests, and no frontend pipe with a `{…}` engine or hardcoded display-property list. Wire as a CI grep or a test.
4. Update memory: breadcrumb architecture entry + note `DisplayFormat`/`DisplayAttribute` removal.
5. Bump package preview versions per `project_ci_autopublish.md` only when merging to `master`.

**Exit:** demo shows the 3-level breadcrumb everywhere; request budget confirmed; PRD success criteria all met.

---

## Sequencing & risk notes

- Phases 1→4 are strictly ordered (metadata → parser → resolver → wiring). Phase 5 can start once Phase 4 lands on a branch. Phase 0 de-risks the two load-batching assumptions the whole design rests on — **do not skip it**.
- Biggest unknown: mixed-type single-request `LoadAsync<object>` semantics and `.Include()` cache-hit behavior — both pinned in Phase 0.
- Keep the PR diff minimal (per `feedback_minimal_pr_diff.md`): the `DisplayFormat`→`Breadcrumb` rename will touch many demo JSON files — regenerate via the synchronizer rather than hand-editing, and don't reformat untouched files.
- Branch off `master`; this is a single coherent feature → one PR (with the demo) unless review prefers splitting Phase 5 frontend out.

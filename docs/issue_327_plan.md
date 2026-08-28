# Plan — Query result model: rows, batching, composed queries

> Companion to [`issue_327_PRD.md`](issue_327_PRD.md). Issue
> [#327](https://github.com/MintPlayer/MintPlayer.Spark/issues/327).
> Branch `feat/issue-327-query-result-item`, cut from master @ `5ebfaa45` (`preview.65` / ng-spark `22.6.0`).
> **One pull request**, per the repo's standing rule.

## Milestones

| # | Milestone | Independent? | Risk |
|---|---|---|---|
| M0 | Spikes (S2 renderer shim, S3 composed query end-to-end) | — | — |
| M1 | Free fixes: `DistinctBy`, in-memory sort, reject `PersistentObject`/`object` rows | yes | low |
| M2 | Batch the selection load (the live N+1) | yes | medium |
| M3 | Model hash `source` + `entityType`; alias symmetry; verify ordering | yes | low |
| M4 | Row/entity separation — the wire contract, server + client | no (the rewrite) | **high** |
| M5 | `clrType` optional in the query path (composed queries) | after M4 | medium |
| M6 | Every silent bail becomes loud | spans M1–M5 | medium |
| M7 | `CancellationToken` through `IQueryExecutor` | after M4 | low |
| M8 | Docs + demo (DemoApp `StartPage` gains a composed query) | last | low |
| M9 | The additive asks from issue §9 | yes | low |
| M10 | Versions + release notes | last | low |

**Sequencing intent:** M1–M3 are correct and shippable on their own and land first, so the tree is green
before the rewrite starts. M4 is the only milestone that cannot be split — the wire contract changes on
both sides at once, and a half-migrated client is not a testable state. M5 rides on M4's seams. M6 is
folded into whichever milestone owns each site rather than being a separate sweep, except for the
startup/verify diagnostics, which land with M5. Commit per milestone; **run the full test suite once, at
the end** (repo convention), with type-checks and targeted tests in between.

**Two CI gates to respect throughout:** `--spark-verify-model` and `--spark-verify-security` run against
all four demo apps on every PR. M3 forces a hash rebake in all four; M8's demo query moves DemoApp's
`securityPosture.txt`. Both fail the build rather than warn.

---

## M0 — Spikes

**S1 — batch load with includes.** **Resolved during investigation, no spike needed.**
`IAsyncDocumentSession.LoadAsync<T>(IEnumerable<string>, Action<IIncludeBuilder<T>>, CancellationToken)`
is present in the pinned RavenDB.Client 7.2.5 (verified in the shipped XML docs). The repo simply never
used the combination. M2 uses it directly.

**S2 — renderer compatibility shim** (shapes M4). Reconstruct the `(value, attribute, options, item)` bag
from `(itemValue, column)` in `spark-grid-renderers.ts`, and prove Fleet's `color-swatch` column renderer
and DemoApp's `address-card` render unchanged. `withDeclaredInputs` already filters by declared inputs, so
the bet is that renderers ignoring the new fields need no edit. Pattern to follow:
`renderers/src/renderer-inputs.spec.ts`.
**Exit:** both demo renderers render from a `QueryResultItemValue` with no renderer-side change.

**S3 — composed query end to end** (shapes M5). A JSON-only type with a `Custom.*` query returning
computed rows, rendered by the real grid. No virtual type has a query today (both carry `"queries": []`),
which is exactly why the gap went unnoticed.
**Exit:** rows render, columns come from the result, and the first-column link is withheld (no `Read`).

---

## M1 — Free fixes

Independent of everything else; each closes a silent failure.

- **`DistinctBy`** — keep at `QueryExecutor.cs:222` (Raven fan-out, semantically expected); remove from
  `:382`. ⚠ `:382` is the single return for all three custom shapes, so the Raven sub-case must keep dedup
  **explicitly** rather than lose it by accident. Kills S1 (the null-`Id` grid collapse).
- **In-memory sort fallback** beside the existing in-memory search fallback (`:55-67`), so a custom query
  returning a plain `IEnumerable` honours `sortColumns` instead of ignoring them. Kills S3. Comparer pinned
  explicitly (`OrdinalIgnoreCase`, nulls last) and the divergence from RavenDB term ordering documented
  (R7).
- **Reject `PersistentObject` and `object`/`dynamic`** as custom-query element types, loudly, naming the
  declared return type. ⚠ The existing `!= typeof(object)` guard only covers the interface-scan branch —
  the direct declarations (`IEnumerable<object>`, `Task<IQueryable<object>>`, …) bypass it entirely, so the
  check goes in `ExtractQueryableElementType`'s first two branches too. Closes F6 and R12.

**Tests:** `Services/QueryExecutorUnitTests.cs` (element-type rejection, message content),
`QueryExecutorIntegrationTests.cs` (null-id rows survive; in-memory sort applies).

---

## M2 — Batch the selection load

The live N+1, on entity-backed grids, independent of composed queries.

1. `IPersistentObjectActions<T>` gains
   `Task<IReadOnlyList<PersistentObject>> OnLoadManyAsync(IReadOnlyList<string> ids, PersistentObject? parent)`.
2. `DefaultPersistentObjectActions<T>` implements the whole pipeline there, batched — one
   `LoadAsync<T>(ids, includes)`, one `breadcrumbResolver.ResolveAsync(session, entities, def)`, one
   `RedactAsync(session, pairs, …)`; guard/`Read`/mapping/`Can`/etag stay per-row and cost no I/O.
   `OnLoadAsync` becomes `(await OnLoadManyAsync([id], parent)).FirstOrDefault()` — **one pipeline, so
   single and batch cannot drift.**
3. `IDatabaseAccess` gains the batch sibling, keeping the `Read` type gate (memoized: N ids, one decision)
   and the virtual-type fork (`clrType == null` falls back to the per-id compose path).
4. `ExecuteCustomAction` makes one call and **refuses when the returned count is short of the requested
   count** — never silently shrink.
5. `estimatedRequests` becomes a small constant; the `#239 M5` comment is rewritten (it currently
   documents lifting the ceiling *as the fix*).

⚠ **Expected breakage, by design:** `LegacyHandWrittenActions`
(`Actions/HandWrittenActionsCompatibilityTests.cs:32`) implements every interface member and inherits
nothing, and the interface carries a standing warning that adding a member breaks it. That tripwire firing
is the correct outcome; its implementation is updated in the same commit.

**Tests.** Five existing tests in `Endpoints/Actions/ExecuteCustomActionTests.cs` assert
`GetPersistentObjectAsync` per id (`:143`, `:180`, `:203`, `:240`, `:267`) and must be restubbed onto the
batch member — keeping the behavioural invariants they pin: all-or-nothing refusal, id-less selected item ⇒
404, route-type-not-wire-type, server state (not wire state) reaching the action. New:

- A `SparkTestDriver` integration test modelled on
  `Services/RowSecurityProjectionBatchingTests.cs:54` — seed ~50 rows of a reference-heavy, row-scoped
  type, invoke a custom action selecting all of them, assert `session.Advanced.NumberOfRequests` (or
  `RqlRecorder`) stays O(depth) not O(N), and that correctness no longer depends on `IgnoreMaxRequests`.
- The missing `MaxSelectedItems` boundary test (200 proceeds, 201 refuses) — M2 is tempted to touch that
  ceiling, so pin it.
- ⚠ There is **no end-to-end HTTP custom-action test in the repo**. Add one via
  `SparkClient.ExecuteActionAsync` on a `SparkEndpointFactory`-booted host (`SparkClient` mints
  antiforgery itself), following the `ExecuteQueryEndpointTests` idiom.

---

## M3 — Model hashing, alias symmetry, verify ordering

Before composed queries ship, not after — under M5 a query's `source` names an arbitrary method that skips
row security, and `entityType` chooses the right that gates it.

- `ModelFileShape.Describe` — `source` and `entityType` become structural, **and the `indexName`-gated
  `continue` is removed** so a query always contributes a line. ⚠ Today `"queries": []` and
  `"queries": [<a whole query with no indexName>]` hash identically; that is exactly the shape #327 adds.
- Entity-type alias collision **throws**, symmetrically with `SparkQueryAliases.Index`; `byId`'s last-wins
  is aligned with `byAlias`'s first-wins so the two indexes cannot disagree (F19).
- `Verify` runs `VerifyQueryAliasesAreUnique` and `VerifyRefreshTriggersAreImplemented` regardless of hash
  drift (F20), and the drift message distinguishes a hand-authored file from a generated one.
- **Rebake `modelHashes.json` in all four demo apps** via `--spark-synchronize-model`.

**Tests:** `Model/ModelHashVerifierTests.cs`, `Model/SparkModelShapeTests.cs` (a query differing only in
`source` now hashes differently; a query with no `indexName` contributes a line),
`Services/ModelLoaderTests.cs` (alias collision throws).

---

## M4 — The row/entity separation

The rewrite. Server and client change together; a half-migrated client is not a testable state.

**Server.** New `QueryResultItem`, `QueryResultItemValue`, `QueryColumn` in `Abstractions`; `QueryResult`
reshaped (`Columns` + `Items` + `TotalItems`). `EntityMapper` gains a row-producing sibling to
`ToPersistentObject`, reusing `PopulateAttributeValues`/`ConvertValueForWire`. `RowSecurity.RedactAsync`
re-expressed over rows. `Execute.cs` resolves columns once and ships them. Streaming follows:
`StreamingQueryExecutor`, `IStreamingQueryExecutor`, `StreamingDiffEngine` (keys on `po.Id`, diffs
attributes by name), `StreamingMessage` (`SnapshotMessage.Data`, `PatchItem.Attributes` → positional
values), `StreamExecuteQuery`.

**Client.** `models/src/query-result.ts` + new column/item/value types; `spark.service.ts:51-82` (the one
fetch, and `executeQueryByName`'s hardcoded empty fallback); `attribute-value.pipe.ts`,
`reference-chips.pipe.ts`, `renderer-inputs.ts` (row → value); `spark-grid-renderers.ts:43-63` plus the two
hand-copied twins in po-detail (`:198-210`) and po-form (`:378`) — the **compatibility shim** lands here,
so renderers migrate once centrally; `spark-grid-columns.ts` flips its column source from
`EntityType.attributes` to per-result `columns`; `spark-query-grid.component.ts/.html`;
`query-list` (streaming patch merge, client search/sort); `spark-query-card` (passthrough).

⚠ **`executeQuery` is also the reference-option-list source** for po-form, po-detail and
`spark-reference-picker`, which display `breadcrumb || name || id`. That is why `QueryResultItem` carries
`Breadcrumb` (D1) — without it those three surfaces each need a second fetch. `executeCustomAction` posts
whole `PersistentObject`s as `selectedItems`; with row-shaped selection it posts ids, which M2 already made
the server's primitive.

**Also in this pass (R3):** `clrType?: string` on the TS model, `t.clrType?.endsWith(...)` at
`spark-query-grid.component.ts:359`, and the unguarded `entityType()!` at `.html:64`.

**Tests:** `Endpoints/Queries/ExecuteQueryEndpointTests.cs` (result shape), `Streaming/*`,
`Services/QueryExecutor*`, `Mapper/*`; client `grid/src/*.spec.ts` (3),
`renderers/src/renderer-inputs.spec.ts`, `query-list/src/*.spec.ts`, `services/src/spark.service.spec.ts`,
`pipes/src/*.spec.ts`, `po-form/src/spark-po-form.component.spec.ts:418` (registry literal).

---

## M5 — Composed queries (`clrType` optional in the query path)

- `ResolveByEntityName(def.Name)` replaces `ResolveForType`; the CLR bail at `:242-246` goes.
- Row security skipped **because there is nothing to evaluate** — the rule written into the doc comment of
  the enforcing hook (improvement #8; the prior art documents this nowhere).
- A **loud startup diagnostic per composed query**, naming the type and stating that row filtering,
  redaction and per-row permissions are the actions class's responsibility. This is the containment for the
  risk in §12 of the issue.
- Row identity required: null or duplicate id ⇒ throw, never a collapsed grid.
- Per-row envelope squared closed (`Can = { Edit = false, Delete = false }`).
- `SparkQueryPage<T>` for author-owned paging, with the **binary** authority rule (R6): the framework owns
  filter/search/sort/count/page, or the author does. No partial delegation.
- Streaming refused for a `clrType`-less type at `--spark-verify-model` and `QueryLoader` index-build time
  (F16), not at first `MoveNext` inside a websocket.
- Validate at startup that a `clrType`-null type carrying queries has at least one `ShowedOn.Query`
  attribute (R8) — both existing virtual types are `PersistentObject`-only on every attribute, and copying
  one is what authors will do.

**Tests:** a new `Endpoints/Queries/ComposedQueryTests.cs` following `VirtualObjectEndpointTests` (the #324
sibling): a JSON-only type with a `Custom.*` query, rows rendered, `Query` right enforced, duplicate/null id
throwing, `SparkQueryPage` honoured, streaming refused.

---

## M6 — Loud failures

Folded into the milestone owning each site. The nine `([], false)` bails and ten further silent
degradations become `DEV:`-style errors naming the fix, or verify-time refusals — following
`LoadVirtualObjectViaActionsAsync`, which already throws on a shape mismatch rather than 404ing.
Includes moving authorization above the `Database.*` bails (F1), and R13/R14 (in M3).

---

## M7 — `CancellationToken`

One method, one implementation, one call site; `httpContext.RequestAborted` already in scope.
`ExecuteQueryableAsync`'s hardcoded `CancellationToken.None` goes. `IRowSecurity.FilterAsync` /
`ComposeRowFilterAsync` / `RedactAsync` gain tokens. Composed row counts capped loudly (R5).

---

## M8 — Docs + demo

- `docs/guide-queries-and-sorting.md` — a composed-queries section, the row/column wire contract, type
  hints, and the `SparkQueryPage` authority rule.
- `docs/guide-custom-actions.md` — selection is ids; the server re-materializes and re-judges.
- An **AsDetail-array subsection** documenting the escape hatch that works today for small fixed lists,
  with its two easy-to-miss constraints (the child type needs a `clrType`; the child needs its own `Query`
  grant) and the explicit warning that it is not the mechanism — the next page will have 5,000 rows.
- **DemoApp's `StartPage` gains a composed query** — no virtual type has one today, which is why the gap
  went unnoticed. Moves `securityPosture.txt`; regenerate.
- `README.md` guide table if a new guide file is added.

---

## M9 — The additive asks (issue §9)

Independently shippable, and in this PR because the repo's rule is one PR.

- **§9.1** `"image"` and `"url"` data types — `GetDataType`, `spark-grid-cell.component.html` (inline
  styles: the grid is inside `mp-datatable`'s shadow root), the po-detail chain, `input-type.pipe.ts`.
- **§9.2** `rowRoute` — an optional `(row) => unknown[] | null` replacing the anchor's target, `canRead()`
  gate untouched.
- **§9.4** `[SparkAuthorize(Group = …)]` bypasses the `wellKnown` reservation — resolve through
  `ISecurityConfigurationLoader` and refuse a name resolving to a reserved id.
- **§9.5** SPARK010's message overstates what is lost — correct it to antiforgery path scoping and
  pipeline ordering.
- **§9.6** `SparkDenial` is `internal` — make `Refuse` public (or map `[SparkAuthorize]` failures through
  it) so apps can match the 404-not-403 posture.
- **§9.7** `SparkQueryActionsService` exporting `actionsFor(queryIdOrAlias)` / `execute(...)`, so a
  page-level action can be rendered outside the query card. Plus a `*sparkShellTopbarActions` slot that
  sits *beside* the language selector rather than replacing it.
- **§9.9** Document that `[SparkAuthorize]` on a SignalR hub would resolve the root provider and throw
  (latent — no SignalR in Spark today).

---

## M10 — Versions + release notes

- NuGet `10.0.0-preview.65` → `10.0.0-preview.66` across all `MintPlayer.Spark*` projects (.NET 10 → major
  stays 10).
- npm `@mintplayer/ng-spark` and `@mintplayer/ng-spark-auth` `22.6.0` → `22.7.0`, **in lockstep** (Angular
  22 → major stays 22). A breaking API change is a minor here; the major tracks the platform.
- `docs/release-notes-preview-66.md`.
- Final full sweep: `nx run-many -t test`, both `--spark-verify-model` and `--spark-verify-security` across
  all four demo apps, then the PR.

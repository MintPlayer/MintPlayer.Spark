# Plan — Query result model: rows, batching, composed queries

> Companion to [`issue_327_PRD.md`](issue_327_PRD.md). Issue
> [#327](https://github.com/MintPlayer/MintPlayer.Spark/issues/327).
> Branch `feat/issue-327-query-result-item`, cut from master @ `5ebfaa45` (`preview.65` / ng-spark `22.6.0`).
> **One pull request**, per the repo's standing rule.

## Milestones

Status as of the latest commit on `feat/issue-327-query-result-item`.

| # | Milestone | Status | Commit |
|---|---|---|---|
| M0 | Spikes | ✅ S1 resolved without prototyping, S2 rejected, S3 folded into M5 | — |
| M1 | Free fixes: `DistinctBy`, in-memory sort, reject `PersistentObject`/`object` rows | ✅ done | `361aaeb8` |
| M2 | Batch the selection load (the live N+1) | ✅ done | `22e1f533`, reworked `161e107d` |
| M3 | Model hash `source` + `entityType`; alias symmetry; verify ordering | ✅ done | `f5585e52` |
| M4 | Row/entity separation — the wire contract, server + client | ✅ done | `107bf1bd` (server), `7add96b6` (client) |
| M5 | `clrType` optional in the query path (composed queries) | ✅ done | `0caa20d0` |
| M6 | Every silent bail becomes loud | ✅ done | folded into M1/M3/M4/M5 + `be073176` |
| M7 | `CancellationToken` through `IQueryExecutor` | ✅ done | `be073176` |
| M8 | Docs + demo (DemoApp `StartPage` gains a composed query) | ✅ done | `637daeab` |
| M9 | The additive asks from issue §9 | ✅ done | `ce53a6a4` + tests |
| M10 | Versions + release notes | ✅ done | `c1f47463` |
| M11 | `CustomActionArgs.SelectedItems` becomes `QueryResultItem[]` | ⬜ next | — |

**Sequencing intent (held):** M1–M3 were correct and shippable on their own and landed first, so the
tree was green before the rewrite started. M4 could not be split — the wire contract changes on both
sides at once, and a half-migrated client is not a testable state — so it landed as two commits that
are only green together. M5 rides on M4's seams. M6 is folded into whichever milestone owns each
site rather than being a separate sweep, except the startup/verify diagnostics, which land with M5.

**Test discipline:** the full suite runs once at the end (repo convention), with type-checks,
targeted suites and the AOT library build in between. ⚠️ M9 initially shipped with **no tests at
all** — caught by auditing coverage before handover rather than by anything failing, since untested
code does not go red. Backfilled to 41 tests across the six additions. Both halves of M4 were verified against the
full 1795-test server suite and the 330-test client suite before commit, because a wire change has no
smaller safe unit.

**Two CI gates to respect throughout:** `--spark-verify-model` and `--spark-verify-security` run
against all four demo apps on every PR. M3 forced a hash rebake in all four (done); M8's demo
query rebaked DemoApp's model hash again. It did **not** move `securityPosture.txt`, contrary to the
prediction here — `Read/StartPage` already implies `Query/StartPage`, so the composed query needed no
new grant. Both gates fail the build rather than warn.

---

## M0 — Spikes ✅

**S1 — batch load with includes.** **Resolved during investigation, no spike needed.**
`IAsyncDocumentSession.LoadAsync<T>(IEnumerable<string>, Action<IIncludeBuilder<T>>, CancellationToken)`
is present in the pinned RavenDB.Client 7.2.5 (verified in the shipped XML docs). The repo simply never
used the combination. M2 uses it directly.

**S2 — renderer compatibility shim. Rejected, not run.** It would have rebuilt the old
`(value, attribute, options, item)` bag from `(itemValue, column)` so renderers migrated once centrally.
Once "no backward compatibility" was restated as the governing rule the shim stopped being worth
de-risking — and it was worse than unnecessary: the bag it reconstructs hands every column renderer an
`EntityAttributeDefinition` the grid no longer possesses, which is fabricated metadata a projection
deliberately does not carry. Renderers take `column: SparkCellColumn` instead (PRD D9); the two demo
column renderers were a two-line change each.

**S3 — composed query end to end. Folded into M5 rather than run ahead of it.** M4 landed the contract a
composed query renders through, so the prototype and the milestone became the same work. What the spike
was meant to establish is now pinned by `Endpoints/Queries/ComposedQueryTests.cs`. The observation that
prompted it held: no virtual type had a query (both carried `"queries": []`), which is why the gap went
unnoticed — and why M5 validates that a composed type carrying a query shows at least one attribute on it.

---

## M1 — Free fixes ✅

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

## M2 — Batch the selection load ✅

The live N+1, on entity-backed grids, independent of composed queries.

1. **No new hook.** `IPersistentObjectActions<T>` is unchanged -- batching is an optimization, not a
   seam (owner decision; the reference framework has no plural hook either). The batched pipeline lives
   on `DefaultPersistentObjectActions<T>` as `LoadManyAsync`, reached through an internal non-generic
   `IBatchedLoadActions`, and `SupportsBatchedLoad` turns it off for any subclass that overrides
   `OnLoadAsync` so a decorated page can never be skipped by a bulk path.
2. `DefaultPersistentObjectActions<T>` implements the whole pipeline there, batched -- one
   `LoadAsync<T>(ids, includes)`, one `breadcrumbResolver.ResolveAsync(session, entities, def)`, one
   `RedactAsync(session, pairs, ...)`; guard/`Read`/mapping/`Can`/etag stay per-row and cost no I/O.
   `OnLoadAsync` becomes `(await LoadManyAsync([id], parent)).FirstOrDefault()` -- **one pipeline, so
   single and batch cannot drift.**
3. `IDatabaseAccess` gains the batch sibling, keeping the `Read` type gate (memoized: N ids, one decision)
   and the virtual-type fork (`clrType == null` falls back to the per-id compose path).
4. `ExecuteCustomAction` makes one call and **refuses when the returned count is short of the requested
   count** — never silently shrink.
5. `estimatedRequests` becomes a small constant; the `#239 M5` comment is rewritten (it currently
   documents lifting the ceiling *as the fix*).

✅ **No interface breakage.** Because the batch form is internal rather than a new interface member,
`LegacyHandWrittenActions` (`Actions/HandWrittenActionsCompatibilityTests.cs:32`) is untouched and its
tripwire never fires -- which is the point of keeping batching off the public surface.

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

## M3 — Model hashing, alias symmetry, verify ordering ✅

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

## M4 — The row/entity separation ✅

The rewrite. Server and client changed together; a half-migrated client is not a testable state.
Landed as `107bf1bd` (server) and `7add96b6` (client).

**Server.** New `QueryResultItem`, `QueryResultItemValue`, `QueryColumn` in `Abstractions`;
`QueryResult` reshaped (`Columns` + `Items` + `TotalItems`). `QueryResultProjector` builds the
columns from `ShowedOn.Query` and projects each mapped row — see PRD D10 for why the pipeline still
builds a `PersistentObject` internally rather than growing a second mapper. Streaming follows the
same contract: `StreamingQueryBatch` carries columns (sent once, with the snapshot),
`StreamingDiffEngine` keys on a now-guaranteed id and diffs `Values` by key, `PatchItem.Attributes`
becomes `Values`.

**Client.** New wire models; `spark.service.ts` (the one fetch, and `executeQueryByName`'s empty
fallback); a new `queryCellValue` / `queryReferenceChips` pipe pair for the projected row shape,
deliberately separate from the attribute-shaped `attributeValue` (the fallbacks that make sense there
— reaching into `attr.object`, recomputing a breadcrumb template — are unreachable for a projection,
so sharing the code would carry branches that can never fire and invite feeding one shape into the
other); `spark-grid-renderers.ts`; the grid component and template; `query-list` (streaming merge,
client search/sort); `spark-query-card` passthrough; the reference picker and the po-form/po-detail
option lists.

**Decisions taken during implementation, recorded in the PRD:**

- **No renderer shim** (S2 rejected). Renderers take `column: SparkCellColumn`; see D9.
- **Selection is ids.** `SelectedItems` → `SelectedItemIds`, `SubmittedSelectedItems` deleted. Nothing
  in `libs/`, `Demo/` or `tests/` read it except one assertion on its length.
- **Row identity is enforced here, not in M5.** `QueryResultItem.Id` is non-nullable, so "no id" and
  "duplicate id" became loud errors as soon as the type existed. M5 keeps the composed-query
  diagnostics that surround it.
- **AsDetail columns project a child count** plus, for a single child, the resolved breadcrumb —
  otherwise a grid cell that used to read "3 items" would render empty.

**Also in this pass (R3):** `clrType?: string` on the TS model, guarded `?.endsWith(...)`, and the
unguarded `entityType()!` in the row link.

**Tests migrated, not weakened.** Two server tests inverted into their new truth (a streaming mapper
stub that gave every row the id `"echo"` now trips the uniqueness check; the M1 null-id test became
"a row with no id is refused"). Twelve client specs moved to the new fixtures; the reference-picker
and `ReferenceDisplayValuePipe` cases that asserted a `name` fallback now assert the id fallback,
with a comment saying why the middle rung is gone.

---

## M5 — Composed queries (`clrType` optional in the query path) ✅

Landed as `0caa20d0`. What shipped, and the two places it diverged from the plan above.

- `ResolveByEntityName(def.Name)` where there is no CLR type, `ResolveForType` where there is; the
  silent CLR bail is gone. A `clrType` that *is* declared but resolves to nothing stays a loud
  error — that is a broken binding, not a composed type, and the two need opposite fixes.
- **Row security skipped, because there is nothing to evaluate.** Written into the enforcing hook,
  at the `FilterAsync` call, at the length it deserves: what is skipped, why it cannot be otherwise,
  and what the actions class is therefore responsible for.
- **Every composed query announces itself at startup** (`SparkComposedQueries.Announce`, called from
  `QueryLoader`), naming the type and what does not apply.
- ~~Row identity required~~ **landed in M4** — `QueryResultItem.Id` is non-nullable.
- ~~Per-row envelope squared closed~~ **needed no code.** M4 removed `can` from the row shape
  altogether, so there is nothing to force to false on this path or any other. Pinned as a
  type-shape test (`A_row_carries_no_affordance_to_close`) rather than re-asserted per path — the
  executor edit that did it was written, found to be dead, and removed.
- `SparkQueryPage<T>` with the binary authority rule (R6). `CustomQueryArgs` gains `Skip`, `Take`
  and `Search`, without which the escape hatch is unusable.
- Streaming refused for a `clrType`-less type, and a composed type carrying a query required to show
  at least one attribute on it (R8) — both in `SparkComposedQueries.Validate`, shared by
  `QueryLoader` and `--spark-verify-model` exactly as `SparkQueryAliases` is, so CI cannot accept a
  model the runtime refuses. The streaming executor keeps a third copy of the refusal for the model
  that changes under a running process.

**Also fixed here, because M5 is where it surfaced:** `ModelLoader`'s per-file `catch (Exception)`
swallowed the entity alias-collision throw that M3 had just added — the exception was raised, printed
as `Error loading model file …`, and discarded, so the application started with one of two types
unroutable. Narrowed to `JsonException`/`IOException`/`UnauthorizedAccessException`. The test that
pinned first-wins as intended behaviour is inverted, and a companion test pins that an unparseable
file still degrades to a message.

**Tests:** `Endpoints/Queries/ComposedQueryTests.cs` (15) — rows rendered from a name-resolved
actions class, breadcrumb template over a computed row, `Query` right enforced, missing actions class
and duplicate ids both loud, sort honoured, and five on `SparkQueryPage` (author's total, no second
paging, ordering kept under `?sortColumns=`, result kept under `?search=`). Plus the streaming
refusal in `StreamingQueryExecutorUnitTests`.

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

---

## M11 — `SelectedItems` becomes `QueryResultItem[]`

The half of the issue's title that M2/M4 did not land. The wire became ids and the resolution became
one batched load; the **author-facing** type stayed `PersistentObject[]`. Design and rationale in PRD
**D12**; this is the work.

**Not a performance milestone.** M2 already made the selection one round trip. This changes the shape
an author sees, and closes the parity gap the issue documented.

### The change

1. `CustomActionArgs.SelectedItems` : `PersistentObject[]` -> `QueryResultItem[]`. `Parent`,
   `SubmittedParent`, `QueryParent` and `SubmittedSelectedItemIds` are unchanged.
2. `ExecuteCustomAction` projects the already-loaded, already-redacted rows through
   `QueryResultProjector` at the boundary. The load itself does not move.
3. `QueryResultProjector` gains an all-visible-attributes column builder for the action path (PRD
   D12: reusing the `ShowedOn.Query` filter would silently hide detail-only attributes from actions).
   `ToItems`'s `queryName` parameter widens to a neutral context string -- its two error messages
   currently say *"Query '...' produced..."* and would otherwise name a query that was not involved.
4. `DefaultPersistentObjectActions<T>` gains the materialization hook: `public virtual`,
   `[NoInterfaceMember]`, called from inside `LoadManyAsync` at the load. **No change to
   `IPersistentObjectActions<T>`**, so the hand-written-actions tripwire does not fire.
5. An id-less row from a JSON-only virtual type refuses at the endpoint, naming the type, rather than
   reaching the projector and becoming a generic 500.
6. `IDatabaseAccess.GetPersistentObjectsByIdAsync` has exactly one caller. If the selection path
   moves to a `QueryResultItem`-producing member, replace it rather than leaving dead public surface.

### Three invariants to protect, and the tests that already do

Each fails **silently** if the projection is built the wrong way. Keep these tests exactly as they
are; if one needs editing to compile, that is a signal to re-read PRD D12 rather than to edit it.

| Invariant | Pinned by |
|---|---|
| Short-result refusal compares **loaded** rows against the distinct id count | `A_selection_the_batch_shrinks_is_refused_rather_than_partially_applied` |
| One batched call carrying all ids, zero singular loads -- i.e. rows are **not** echoed from the client | `The_whole_selection_is_resolved_in_one_batched_call` |
| Id-less selection refuses the whole request, upstream of the load | `An_id_less_selected_item_refuses_the_whole_request` |

Redaction is the fourth: the projector must consume the **redacted** `PersistentObject`, which is
what it already does on the query path. Projecting from raw entities bypasses `RedactAsync` and turns
this into a disclosure bug.

### Migration

- `args.Parent ?? args.SelectedItems.FirstOrDefault()` stops compiling -- `Parent` stays a
  `PersistentObject`, so the two no longer unify. Coalesce on ids instead:
  `args.Parent?.Id ?? args.SelectedItems.FirstOrDefault()?.Id`. Three demo actions, ~3 lines each,
  and every external consumer using the same idiom. Needs an explicit release-note line.
- One test assertion reads `SelectedItems[0].Name`; `QueryResultItem` has no `Name`. The property it
  pins -- server state, not submitted state -- is still worth pinning, restated over `Breadcrumb`.

### Documented limitations (PRD D12)

- **Index-computed columns arrive null.** A selection is a document load, so a value computed inside
  an index and stored there is on neither the document nor the CLR class. Silent at every step
  today; the guide must say so.
- **`Etag` and `Can` are lost.** `QueryResultItem` carries neither. Accepted; recorded so it is a
  decision rather than a discovery.

### Docs that go stale

`docs/guide-custom-actions.md` states the opposite in as many words -- *"`SelectedItems` holds
**entities**, not the rows the grid displayed"* -- and both it and `docs/guide-row-security.md` still
reference a `SubmittedSelectedItems` that #327 already removed. `docs/release-notes-preview-66.md`
needs the migration line.

# Plan — Issue #467 (one pull request)

Requirements, decisions (D1–D23 grilled 2026-10-03; D24–D29 settled during implementation) and spike results live in
[issue_467_query_selection_PRD.md](issue_467_query_selection_PRD.md) §7. This file is the order of work.
Where the PRD's §2 and §7 disagree, §7 wins.

**Rules for executing this plan**
- One branch, one PR: `feat/467-query-selection`. Every decision D1–D32 lands in this PR; the PR also closes #482.
- Commit per milestone. **Do not run test suites per milestone.** Verify with a build + reading the code.
  One full sweep at the end (M9):
  `RAVENDB_LICENSE='C:\Repos\MintPlayer.Spark\.secrets\raven-license.log' npm run test:affected`.
  While fixing, run only the affected class (`--filter "FullyQualifiedName~Issue467"` etc.).
- **Spike tests are intentionally red** until their milestone lands:
  `tests/MintPlayer.Spark.Tests/Spikes/Issue467/` (12 red and 4 green when written; M5 added the custom-action S4 half, the execute S7 half and an S8 retry). Each test names its spike and decision. None has been run since M0 — that is the M9 sweep.
  When a milestone turns them green, move them next to the related tests (e.g. `Endpoints/PersistentObject/`)
  and drop the `Spikes` folder before the PR. **Do not push the branch while they are red** (CI costs money;
  pushing would only show the known reds).
- Write logs raw to the scratchpad (`cmd > x.log 2>&1; echo "EXIT: $?"`), then grep them.
- Never start `ng serve` next to a running host; `dotnet run` is the whole command.
- No backward compatibility (preview). Touched `libs/` csproj: minor bump within 11.x;
  `@mintplayer/ng-spark`: minor bump within 22.x. Breaking changes go in the release notes.

---

## Status

- [x] Investigation (four agents) and grilling, D1–D23.
- [x] Branch `feat/467-query-selection`; PRD and plan committed (`e00fd8a5`).
- [x] Spikes S1 (upstream part), S2, S4, S5, S6, S7, S8, S9, S10, S11, S12, S13 (results in PRD §7).
- [x] Deferred spikes written: S1 remainder (M4 spec), the custom-action halves of S4/S7 and S8's 449 retry (M5). S3 (cost of the D18 message) is measured in M9.
- [x] Committed: M0 `d212723a`, M1 `af26aeb2`, M2 `009b4c22`, M3 `fe10392c`, M4 `393352aa`, M5 `d0782ea5`, M6 (concurrency). Next: M7.
- [x] Found during M6: the D1 override gap (base `OnSaveAsync`/`OnDeleteAsync` own the persistence, so an override can skip guarantees). Filed as #482. Owner decisions 2026-10-03: first "not in this PR", then **reversed — #482 and the durable hooks (D17) land in this PR too** (M7, M7b; the PR closes #467 and #482). Design refined to DI-registered per-phase hooks (D31). The bug the investigation found is fixed here: the OIDC application/scope uniqueness check ran only after the commit, so a duplicate was refused with a 400 yet stayed stored — now also checked before the write.
- [x] D29c confirmed by the owner (2026-10-03): the D20 reason stays server-side only; no framework prompt — an app prompts with `manager.Retry.Action` itself.

---

## Milestones

### M0 — ng-bootstrap (prerequisite) — upstream DONE
- [x] MintPlayer/mintplayer-ng-bootstrap#422 fixed by #423 (squash `aa348d4`): the selection survives
      paging, and `selectionMode="checkbox"` makes a click open the row and the checkbox select. Published
      as ng-bootstrap **22.21.0**, web-components **2.18.0** (publish run 37136442215, success).
- [x] (`d212723a`) Bump `@mintplayer/ng-bootstrap` to 22.21.0 (and `@mintplayer/web-components` to 2.18.0 if pinned)
      **from the repo root** (`npm install` at the root only). Adjust for #423's breaking changes:
      `compareWith` removed (identity is `rowKey`); `selectedRows` event typed `(T | undefined)[]`; Enter
      opens, Space selects; a `[settings]` page is kept together with `perPage`.

### M1 — Translation composition (D2, D3, D23)
- [x] `HostTranslationsAggregatorGenerator.ApplyAssembly`/`MergeTranslations` (`:136-198`): merge per
      (key, language). **Append new languages after existing ones** (`TranslatedString.GetValue` falls back
      to the first language). An app's `""` counts as not defined (D23). Merging is compile-time only (S9).
- [x] `SPARK_TRANS_005`: library-vs-library only, same (key, language), different value; reword it.
      Update the pinned snapshot `HostTranslationsAggregatorGenerator_aggregates_chunks_and_host_overrides_a_key`.
- [x] Tests (`HostTranslationsCompositionTests`, run in M9): the app adds `es` and keeps `en/fr/nl`; the app overrides only `nl`; `""` does not
      blank a value; two libraries clash and warn; the app over a library raises no diagnostic.

### M2 — All localized text out of `App_Data` JSON (D1, D4, D5, D6)
- [x] Key convention: `model.{Entity}.label`, `model.{Entity}.attributes.{Attr}.label|description`,
      `queries.{Query}.label`, `programUnits.{…}`, `security.groups.{…}`, `culture.{…}`, plus an optional
      explicit key. Keys use `.label`/`.description` *siblings*, never a child under a leaf
      (`SPARK_TRANS_002`, S10). The resolver falls back to the humanized name.
- [x] Model shape: `label`/`description` become an optional key string; the entity-level `description`
      becomes `model.{Entity}.label` (D6). Server, `SparkModelShape`, ng-spark models, every
      `resolveTranslation` call site.
- [x] `ModelSynchronizer` stops writing inline labels (`:941`). Description seed (D5) per S10: `JsonNode`
      + relaxed encoder, keeping the file's line endings and trailing newline. A canonical file only gets
      insertions; a non-canonical one is reformatted once with a notice (verify fails). Paths are matched
      dotted and nested. "Defined by no layer" = compiled translations + the app file on disk. Verify fails
      exactly when sync would write.
- [x] Info diagnostic / sync report: missing keys per language that `culture.json` declares (D4).
- [x] `programUnits.json`, `security.json` group names, `culture.json` names → keys.
- [x] Migrate all apps and the libraries' own files (814 inline strings). **Back up before the bulk edit.** Done by `tools/migrate-467-translations.mjs` (D25); `customActions.json` text moves with M3. Test fixtures that embed the old shapes are fixed in the M9 sweep.
      The core library's hand-aligned `translations.json` is never written by sync, so it keeps its layout.
- [ ] (M9 sweep) `--spark-verify-model` and the CI-only gates (`reference_ci_only_gates_spark`) pass.

### M3 — `actions.json` composition, built-in Edit, Revoke (D7, D8, D22, R2)
- [x] Per S12: a library `App_Data/actions.json` is an AdditionalFile, and a new generator emits
      `[assembly: SparkActions("<raw json>")]` (raw text; MiniJson rejects numbers and booleans). Runtime
      discovery by reflection, core first and then by assembly name, **not** a host-generated registry
      (`MintPlayer.Spark.Tests` runs no generator). Analyzers (SPARK011, the D7 conflict warning) read the
      attribute.
- [x] Rename `customActions.json` → `actions.json`: loader, `spark.targets:176`,
      `SecurityConfigurationAnalyzer:162`, `ConfigFileShape`, docs, all apps. App layer stays on disk with
      hot reload; extend the watcher to Created/Renamed.
- [x] Composition on raw JSON objects *before* binding (names case-insensitive). `"Name": null` removes the
      action; a property set to `null` resets it to the default, replacing the `""` convention
      (`SparkDefaultActions.cs:17-19`). The source layer is recorded per property. The composed result is
      validated. The model hash covers the composed `showedOn`/`selectionRule`.
- [x] Core `actions.json`: New (no rule), Edit (`=1`, query + detail, pencil), Delete (`>0`, danger,
      confirmation with a count placeholder). `SparkDefaultActions.cs` keeps only the reserved names.
- [x] `--spark-print-effective-actions`.
- [x] `ListCustomActions`: Edit under `Edit/T`; `ExecuteCustomAction` 404 for reserved names.
- [x] Detail page: Edit/Delete from the catalogue ∩ `can.edit/can.delete`; the hard-coded buttons go (D8).
- [x] D22: CodeCoverage `Revoke` becomes a query action on the ApiToken sub-query card (`>0`, confirmation),
      acting on the selected tokens; `RevokeTokenAction` checks per token that the caller manages the account.
- [x] Fix the stale rule doc comments (`CustomActionDefinition.cs:25`, `SparkCustomAction.cs:27-29`).
- [x] Tests (written; run in the M9 sweep): Edit listed by right; override/removal/null-reset; reserved-name `ICustomAction` never runs;
      custom action rule violation → 400 (R4); print switch; Revoke on the card.

### M4 — Selection and action strip (R1, R3, D9, D10, D19) — needs the M0 bump
- [x] `SparkSelectionMode` = `auto|none|multiple` (remove `single`: server, ng-spark, `SparkSubQueryTests.cs:19,26`).
- [x] `selectionModeFor` (`selection-mode.ts:32`) from the **effective** list: right ∩ `showedOn` includes
      the query ∩ not in `disabledActions` ∩ not hidden by deleted mode ∩ **the rule accepts at least one
      row** (`=0` no longer counts, S2). Result: `multiple` or `none`. Recomputed on `disabledActions` /
      deleted-mode change; cleared when the mode becomes `none`. The same list feeds `toolbarActions()`.
- [x] Grid: `selectionMode="checkbox"` (ng-bootstrap 22.21.0); a row click opens; the selection is kept
      across pages; the chip shows the off-page count.
- [x] Toolbar: `kind: 'edit'` (navigate to `/po/{type}/{id}/edit` with return state); Delete not gated on
      "already selectable"; Edit hidden when `disabledActions` has `Edit`/`Save`; pencil icon in the
      query-list and card templates; Edit in the row ⋮ menu. `spark-query-list` `selectionMode` input.
- [x] Update the old-behaviour specs (S2): `spark-query-toolbar.spec.ts` (:121, 126, 144, 158, 251, 290, 419,
      458, 471), `sub-query.spec.ts` (:13, 30, 36, 55), `spark-query-list.component.spec.ts:356`, grid spec
      :243-262.
- [x] **S1 remainder:** the chip count equals what actions receive.
- [x] New specs (type-checked; run in the M9 sweep): one per R1 condition where only that condition fails (incl. a `=0` rule), explicit
      override, click opens, checkbox selects, cross-page selection.

### M5 — Write-path gates (D11, D12, D13, D18, D20, D21)
- [x] D11: Read right + read row filter + the list's deleted mode on delete-many and the
      `ExecuteCustomAction` fallback. Turns `Issue467DeleteManyGateTests` S4 green. **Write the
      custom-action half of S4 first.**
- [x] D12: `queryId` required for bulk calls without a parent (400); delete-many fetches the rows through
      that query. `SparkClient.DeletePersistentObjectsAsync`/`ExecuteActionAsync` and ng-spark `deleteMany`
      require it. Turns S7 green. **Write the execute-without-`queryId` half first.** Fix
      `QnASubQueryTests.cs:51,68` (no `queryId`/etags).
- [x] D13: document object-level vs query-level `OnDisableActionsAsync` targets in the guide.
- [x] D18: one refusal message with the redacted breadcrumbs of every failing readable row; unreadable or
      missing rows counted, not named. Turns S8 green. **S3** (breadcrumb cost for 200 rows). **S8's 449
      retry inside a batch.**
- [x] D20: one soft-delete `reason` on delete-many (server only; no reason input or required-reason setting exists — D29c, owner to confirm).
- [x] D21: `SparkThrottledException` in `Delete.cs`; fix the `DeleteMany.cs:109-110` comment; document
      self-saving `OnDeleteAsync`.

### M6 — Concurrency (D14, D15, D16)
- [x] `QueryResultItem.Etag` = the document's change vector. *Deviation (D30a):* read by one
      metadata-only lookup per page (`RowChangeVectors`, via `QueryResultProjector.ToItemsAsync`), not
      from each path's query metadata — `GetChangeVectorFor` throws on untracked projections (S5b).
- [x] D14: delete and delete-many take `(id, etag)` per row (`items: [{ id, etag }]`); 400 without,
      409 on mismatch (bulk: naming the rows); the base `OnDeleteAsync` deletes with that change vector
      (D30g). Purge too (D30h). .NET client and ng-spark (grid, detail page, recycle bin).
- [x] D15: an Update of a deleted, soft-deleted or no-longer-readable object → 409 `deleted` over HTTP;
      through `IDatabaseAccess` a gone row with an etag → 409, never a resurrection (base `OnSaveAsync`
      refuses too). The edit page shows `common.deletedByAnotherUser` and keeps the form. Creates unchanged.
- [x] D16: Update requires an etag (400). Callers from S11: `Create.cs` natural-id collision → 409
      `exists` (D30d); `Update.cs`, `SparkClient.UpdatePersistentObjectAsync`, ng-spark `update()` require
      it; OIDC overrides call the base (nothing to change). `SyncActionHandler` must not recreate (D30i):
      the replica sends Insert/Update correctly and the owner applies an Update only to a row it still
      has. Rewritten: `UpdateEndpointConcurrencyTests.Put_with_no_etag_is_400_and_writes_nothing`,
      `ConcurrentWriteRaceTests.Internal_save_without_etag_still_protects_the_load_to_write_window`; added
      `ConcurrentWriteRaceTests.Hard_delete_refuses_a_write_that_raced_past_the_etag_check`.

### M7 — The framework owns persistence; DI-registered per-phase hooks (#482, D31)
Design: issue #482, section "Hook interfaces". Lands in this PR (owner decision, 2026-10-03), so the PR closes #467 and #482.
- [ ] Persister in the framework, called from `DatabaseAccess`. It runs, in this order:
      load/construct → after-materialize → `MapAsync` → `IDeleteReplacement` (deletes only) → before-hooks (`Default`, then
      `Finalize`) → WITH CHECK → store or delete with the expected change vector → **one commit owned by `DatabaseAccess`** →
      replication → after-hooks (each isolated) → durable enqueue (M7b).
      `ISparkWriteBatch.IsDeferring`, `AnnounceBeforeSaveBypass`, the `savedEarly` warning, the Mark/Consume handshake and
      `InvokeBeforeDeleteHookAsync` are removed.
- [ ] Hook interfaces in Abstractions:
      - `IBeforeSave`, `IAfterSave`, `IBeforeDelete`, `IAfterDelete`, `IAfterMaterialize`, `IAfterLoad`, `INaturalIdCollision`;
      - typed `IXxx<T>` sugar;
      - `IDeleteReplacement`;
      - `HookStage { Default, Finalize }`.

      `IPersistentObjectInterceptor` is deleted. `spark.AddHook<T>()` registers a hook, and a `HookRegistrationGenerator`
      emits `AddHooks`, which `AddSparkFull` calls.
- [ ] `SparkCancelException`: write nothing, evict, no after-hooks. Answers: delete/delete-many 204, update 200 (as stored),
      create 204. One cancel cancels a whole bulk delete. A `Retry.Action` from a hook during a bulk delete is refused.
- [ ] The Actions class loses `OnSaveAsync`, `OnDeleteAsync`, `OnBeforeSaveAsync`, `OnAfterSaveAsync` and `OnBeforeDeleteAsync`;
      `IPersistentObjectActions<T>` loses them too. `MapAsync(obj, existing?)` is new.
- [ ] Migrate SoftDelete, History, Moderation, Contributions, Replication (`ISyncActionInterceptor` reads `WasReplaced`), the QnA
      interceptors, and every app override (CodeCoverage ApiToken/GitHubProject/Repository, DemoApp Person/Company,
      Fleet Car (prompt in `IBeforeDelete<Car>`, cancel by throwing; toast in `IAfterSave<Car>`), QnA Question, OIDC).
      ApiToken's shown-once secret stays a **sync** `IAfterSave` and must never be durable.
- [ ] D32: no `StoreAsync` seam; nested value objects are written only with their parent. Replication becomes hooks. Load/query hooks stay out of scope.
- [ ] D32(2) spike, then the guard: does RavenDB raise `OnBeforeDelete` for `session.Delete(id)` on an untracked document? Then a document-store listener refuses a raw hard delete of an `ISoftDeletable` document that the persister did not issue, unless inside `SparkRawWrites.Allow()`. Test: raw delete refused, purge allowed, opt-out allowed.
- [ ] Tests: existing ordering tests (`InterceptorOrderTests` → stages/replacement), S4 (an override cannot defeat a replacement:
      now structural), F6 (refusal evicts side documents), F7/D14 (expected change vector), `RetryFromEveryHookTests`,
      isolated after-hooks, cancel, generator snapshot tests. Docs: `guide-row-security`, the SoftDelete README,
      `guide-manager-retry-actions`, the Spark README/AGENTS.md, and a new hooks guide.

### M7b — Durable after-commit hooks (D17), per S6/S13
- [ ] `IAfterSaveCommitted` / `IAfterDeleteCommitted` take a payload, never the live entity: type name, id, operation,
      `WasReplaced`/`IsPurge`, actor id + system flag, time, previous change vector, and `Facts` filled by before-hooks.
- [ ] Messaging: `EnqueueAsync(IAsyncDocumentSession, msg, options)`, store only, unique id, never the dedupe path. An
      `ISparkAfterCommitOutbox` seam in Abstractions. The persister enqueues one message per row and hook in the data change's
      own `SaveChanges`. A recipient runs the durable hooks, with #369 retries and dead-lettering.
- [ ] **Decided (owner, 2026-10-03):** a durable hook registered without Messaging is a **startup error**. A library that ships one
      references Messaging, so apps get it transitively (Moderation vote reversal).
- [ ] Hooks move to the durable phase: SoftDelete observers, History observer notification (relax `SparkRevisionEvent.ChangeVector`
- [ ] D32(4): `ISoftDeleteObserver` and `ISparkRevisionObserver` are removed; their users (libraries, tests) move to `IAfterDeleteCommitted`/`IAfterSaveCommitted` (payload: operation, id, actor, reason, changed attributes; no new change vector).
      for deferred observers), Moderation vote reversal, DemoApp broadcasts. This fixes the side bug: DemoApp
      `PersonActions.OnBeforeDeleteAsync` broadcasts before the commit.

### M8 — Demo, E2E, docs
- [ ] DemoApp / Fleet: an editor sees Edit + Delete in the strip; a read-only role sees no checkboxes.
      Note that DemoApp anonymous visitors hold full CRUD and so get checkboxes (S2); that's intended.
- [ ] E2E: select one → Edit → edit page and back; select two → Edit disabled, Delete enabled → confirm
      (count shown) → rows gone; cross-page selection; read-only → no checkboxes; `selectionMode: none`;
      stale delete → 409; save after someone deleted → 409 "deleted by another user"; CodeCoverage Revoke
      on the ApiToken card.
- [ ] Docs: guide-custom-actions (actions.json, layering, removal, Edit, R1), guide-row-security (bulk
      gates), translations guide (composition, keys, seeding, `""`), release notes (all breaking changes).
      Mark issue_460_PRD D17 "superseded in part by #467".

### M9 — Full verification and PR
- [ ] Test call sites made stale by D12 (selections and delete-many without `queryId`): `ExecuteCustomActionTests` (fallback-path unit tests), `DisableActionsTests`, `ModerationToolsTests`, `SoftDeleteTests`, `SubQueryActionsTests`; and fixtures embedding pre-#467 shapes (see M2 note).
- [ ] Test call sites made stale by D14/D16 (M6): raw posts to `/po/delete`, `/po/delete-many` (`ids` → `items`), `/po/purge` and `/po/update` without an etag — `DenyAllEndpointMirrorTests`, `DisableActionsTests`, `RetryFromEveryHookTests`, `SubQueryActionsTests`, `XsrfSurfaceTests`, `HistoryTests`, `ModerationToolsTests`, `SoftDeleteTests`, E2E `QnAContributionsTests`, `RetryActionDeleteTests`; any HTTP test asserting 404 for an update of a hidden row now gets 409 `deleted` (D30c); natural-id collision tests now get 409 `exists` (D30d). The typed client call sites already compile (they load first: `DeleteAsLoadedAsync` / `AsListedAsync` test helpers).
- [ ] S3: measure the D18 refusal message for a 200-row batch (breadcrumbs resolve in one batched call; confirm the cost).
- [ ] The `Spikes/Issue467` tests are all green and moved; the `Spikes` folder is gone.
- [ ] Full local sweep (`npm run test:affected`, Developer licence), all five test projects green.
- [ ] Versions: NuGet minor (11.x), ng-spark minor (22.x), ng-bootstrap 22.21.0. Check the diff — CI
      publishes on merge.
- [ ] Update the #467 and #482 descriptions, then open the PR closing #467 and #482.

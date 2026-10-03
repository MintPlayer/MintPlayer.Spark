# Plan — Issue #467 (one pull request)

Requirements, decisions (D1–D23, grilled 2026-10-03) and spike results live in
[issue_467_query_selection_PRD.md](issue_467_query_selection_PRD.md) §7. This file is the order of work.
Where the PRD's §2 and §7 disagree, §7 wins.

**Rules for executing this plan**
- One branch, one PR: `feat/467-query-selection`. Every decision D1–D23 lands in this PR.
- Commit per milestone. **Do not run test suites per milestone.** Verify with a build + reading the code.
  One full sweep at the end (M9):
  `RAVENDB_LICENSE='C:\Repos\MintPlayer.Spark\.secrets\raven-license.log' npm run test:affected`.
  While fixing, run only the affected class (`--filter "FullyQualifiedName~Issue467"` etc.).
- **Spike tests are intentionally red** until their milestone lands:
  `tests/MintPlayer.Spark.Tests/Spikes/Issue467/` (12 red, 4 green). Each test names its spike and decision.
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
- [ ] Deferred spikes: S1 remainder (M4), S3 (M5), the custom-action halves of S4/S7 and S8's 449 retry (M5).

---

## Milestones

### M0 — ng-bootstrap (prerequisite) — upstream DONE
- [x] MintPlayer/mintplayer-ng-bootstrap#422 fixed by #423 (squash `aa348d4`): the selection survives
      paging, and `selectionMode="checkbox"` makes a click open the row and the checkbox select. Published
      as ng-bootstrap **22.21.0**, web-components **2.18.0** (publish run 37136442215, success).
- [ ] Bump `@mintplayer/ng-bootstrap` to 22.21.0 (and `@mintplayer/web-components` to 2.18.0 if pinned)
      **from the repo root** (`npm install` at the root only). Adjust for #423's breaking changes:
      `compareWith` removed (identity is `rowKey`); `selectedRows` event typed `(T | undefined)[]`; Enter
      opens, Space selects; a `[settings]` page is kept together with `perPage`.

### M1 — Translation composition (D2, D3, D23)
- [ ] `HostTranslationsAggregatorGenerator.ApplyAssembly`/`MergeTranslations` (`:136-198`): merge per
      (key, language). **Append new languages after existing ones** (`TranslatedString.GetValue` falls back
      to the first language). An app's `""` counts as not defined (D23). Merging is compile-time only (S9).
- [ ] `SPARK_TRANS_005`: library-vs-library only, same (key, language), different value; reword it.
      Update the pinned snapshot `HostTranslationsAggregatorGenerator_aggregates_chunks_and_host_overrides_a_key`.
- [ ] Tests (run in M9): the app adds `es` and keeps `en/fr/nl`; the app overrides only `nl`; `""` does not
      blank a value; two libraries clash and warn; the app over a library raises no diagnostic.

### M2 — All localized text out of `App_Data` JSON (D1, D4, D5, D6)
- [ ] Key convention: `model.{Entity}.label`, `model.{Entity}.attributes.{Attr}.label|description`,
      `queries.{Query}.label`, `programUnits.{…}`, `security.groups.{…}`, `culture.{…}`, plus an optional
      explicit key. Keys use `.label`/`.description` *siblings*, never a child under a leaf
      (`SPARK_TRANS_002`, S10). The resolver falls back to the humanized name.
- [ ] Model shape: `label`/`description` become an optional key string; the entity-level `description`
      becomes `model.{Entity}.label` (D6). Server, `SparkModelShape`, ng-spark models, every
      `resolveTranslation` call site.
- [ ] `ModelSynchronizer` stops writing inline labels (`:941`). Description seed (D5) per S10: `JsonNode`
      + relaxed encoder, keeping the file's line endings and trailing newline. A canonical file only gets
      insertions; a non-canonical one is reformatted once with a notice (verify fails). Paths are matched
      dotted and nested. "Defined by no layer" = compiled translations + the app file on disk. Verify fails
      exactly when sync would write.
- [ ] Info diagnostic / sync report: missing keys per language that `culture.json` declares (D4).
- [ ] `programUnits.json`, `security.json` group names, `culture.json` names → keys.
- [ ] Migrate all apps and the libraries' own files (814 inline strings). **Back up before the bulk edit.**
      The core library's hand-aligned `translations.json` is never written by sync, so it keeps its layout.
- [ ] `--spark-verify-model` and the CI-only gates (`reference_ci_only_gates_spark`) pass.

### M3 — `actions.json` composition, built-in Edit, Revoke (D7, D8, D22, R2)
- [ ] Per S12: a library `App_Data/actions.json` is an AdditionalFile, and a new generator emits
      `[assembly: SparkActions("<raw json>")]` (raw text; MiniJson rejects numbers and booleans). Runtime
      discovery by reflection, core first and then by assembly name, **not** a host-generated registry
      (`MintPlayer.Spark.Tests` runs no generator). Analyzers (SPARK011, the D7 conflict warning) read the
      attribute.
- [ ] Rename `customActions.json` → `actions.json`: loader, `spark.targets:176`,
      `SecurityConfigurationAnalyzer:162`, `ConfigFileShape`, docs, all apps. App layer stays on disk with
      hot reload; extend the watcher to Created/Renamed.
- [ ] Composition on raw JSON objects *before* binding (names case-insensitive). `"Name": null` removes the
      action; a property set to `null` resets it to the default, replacing the `""` convention
      (`SparkDefaultActions.cs:17-19`). The source layer is recorded per property. The composed result is
      validated. The model hash covers the composed `showedOn`/`selectionRule`.
- [ ] Core `actions.json`: New (no rule), Edit (`=1`, query + detail, pencil), Delete (`>0`, danger,
      confirmation with a count placeholder). `SparkDefaultActions.cs` keeps only the reserved names.
- [ ] `--spark-print-effective-actions`.
- [ ] `ListCustomActions`: Edit under `Edit/T`; `ExecuteCustomAction` 404 for reserved names.
- [ ] Detail page: Edit/Delete from the catalogue ∩ `can.edit/can.delete`; the hard-coded buttons go (D8).
- [ ] D22: CodeCoverage `Revoke` becomes a query action on the ApiToken sub-query card (`>0`, confirmation),
      acting on the selected tokens; `RevokeTokenAction` checks per token that the caller manages the account.
- [ ] Fix the stale rule doc comments (`CustomActionDefinition.cs:25`, `SparkCustomAction.cs:27-29`).
- [ ] Tests: Edit listed by right; override/removal/null-reset; reserved-name `ICustomAction` never runs;
      custom action rule violation → 400 (R4); print switch; Revoke on the card.

### M4 — Selection and action strip (R1, R3, D9, D10, D19) — needs the M0 bump
- [ ] `SparkSelectionMode` = `auto|none|multiple` (remove `single`: server, ng-spark, `SparkSubQueryTests.cs:19,26`).
- [ ] `selectionModeFor` (`selection-mode.ts:32`) from the **effective** list: right ∩ `showedOn` includes
      the query ∩ not in `disabledActions` ∩ not hidden by deleted mode ∩ **the rule accepts at least one
      row** (`=0` no longer counts, S2). Result: `multiple` or `none`. Recomputed on `disabledActions` /
      deleted-mode change; cleared when the mode becomes `none`. The same list feeds `toolbarActions()`.
- [ ] Grid: `selectionMode="checkbox"` (ng-bootstrap 22.21.0); a row click opens; the selection is kept
      across pages; the chip shows the off-page count.
- [ ] Toolbar: `kind: 'edit'` (navigate to `/po/{type}/{id}/edit` with return state); Delete not gated on
      "already selectable"; Edit hidden when `disabledActions` has `Edit`/`Save`; pencil icon in the
      query-list and card templates; Edit in the row ⋮ menu. `spark-query-list` `selectionMode` input.
- [ ] Update the old-behaviour specs (S2): `spark-query-toolbar.spec.ts` (:121, 126, 144, 158, 251, 290, 419,
      458, 471), `sub-query.spec.ts` (:13, 30, 36, 55), `spark-query-list.component.spec.ts:356`, grid spec
      :243-262.
- [ ] **S1 remainder:** the chip count equals what actions receive.
- [ ] New specs: one per R1 condition where only that condition fails (incl. a `=0` rule), explicit
      override, click opens, checkbox selects, cross-page selection.

### M5 — Write-path gates (D11, D12, D13, D18, D20, D21)
- [ ] D11: Read right + read row filter + the list's deleted mode on delete-many and the
      `ExecuteCustomAction` fallback. Turns `Issue467DeleteManyGateTests` S4 green. **Write the
      custom-action half of S4 first.**
- [ ] D12: `queryId` required for bulk calls without a parent (400); delete-many fetches the rows through
      that query. `SparkClient.DeletePersistentObjectsAsync`/`ExecuteActionAsync` and ng-spark `deleteMany`
      require it. Turns S7 green. **Write the execute-without-`queryId` half first.** Fix
      `QnASubQueryTests.cs:51,68` (no `queryId`/etags).
- [ ] D13: document object-level vs query-level `OnDisableActionsAsync` targets in the guide.
- [ ] D18: one refusal message with the redacted breadcrumbs of every failing readable row; unreadable or
      missing rows counted, not named. Turns S8 green. **S3** (breadcrumb cost for 200 rows). **S8's 449
      retry inside a batch.**
- [ ] D20: one soft-delete `reason` on delete-many; required-reason types refuse an empty one.
- [ ] D21: `SparkThrottledException` in `Delete.cs`; fix the `DeleteMany.cs:109-110` comment; document
      self-saving `OnDeleteAsync`.

### M6 — Concurrency (D14, D15, D16)
- [ ] `QueryResultItem.Etag` = the document's change vector. Read it **from the query metadata**
      (streaming, `@metadata`): `GetChangeVectorFor` throws on untracked projections (S5b).
- [ ] D14: delete and delete-many take `(id, etag)` per row; 400 without, 409 on mismatch; the server
      deletes with that change vector. .NET client and ng-spark. Turns `Issue467StaleDeleteTests` green.
- [ ] D15: an Update of a deleted or soft-deleted document → 409 "deleted by another user", before any hook,
      both over HTTP (today 404 via the `Update.cs:47` pre-read) and through `IDatabaseAccess` (today
      **resurrects**, `DatabaseAccess.cs:322` + `ToEntity`). `OnSaveAsync` writes Updates with a
      change vector that requires the document to exist. Creates unchanged.
- [ ] D16: Update requires an etag (400). Fix the callers from S11: `SyncActionHandler.cs:46` (internal
      overwrite option, unreachable over HTTP; must not recreate), `Create.cs:106` (natural-id collision →
      Edit without an etag: refuse or 409), `Update.cs:74`, `SparkClient.cs:318`, ng-spark
      `spark.service.ts:242`, OIDC Actions overrides. Rewrite
      `UpdateEndpointConcurrencyTests.Put_with_no_etag_skips_concurrency_check_and_succeeds` and
      `ConcurrentWriteRaceTests.Save_without_etag_still_protects_the_load_to_write_window`. Turns
      `Issue467UpdateDeletedRowTests` green.

### M7 — Durable after-commit work (D17), per S6/S13
- [ ] Three hook categories: **SYNC** (in-request `OnAfter*`, best-effort: log, never fail a committed
      change; except purge's existence check and `Purged` flag), **IN-TX** (written in the data change's own
      `SaveChanges`: Moderation audit, Contributions `FlushAuditsAsync`, replication `SparkSyncAction`),
      **DEFERRABLE** (durable handler: SoftDelete observers, History observer notification, Moderation vote
      reversal, DemoApp broadcasts). `ApiTokenActions.OnAfterSaveAsync` stays SYNC and **must never be
      deferred** (plaintext token).
- [ ] Messaging: `EnqueueAsync(IAsyncDocumentSession, msg, options)`, store only, unique id, never the
      dedupe path. `Spark.Abstractions` seam (e.g. `ISparkAfterCommitOutbox`, session as `object`). Core
      enqueues before each `SaveChanges` (on save: inside the base `OnSaveAsync`, or from a payload captured
      in before-save). A recipient runs the durable hooks (#369 retries and dead-lettering).
- [ ] Payload: type name, id, operation, `WasReplaced`/`IsPurge`, actor id + `IsSystemContext`, time,
      previous change vector, small captured facts. Relax History `SparkRevisionEvent.ChangeVector` for
      deferred observers.
- [ ] **Open, decide here:** a durable hook registered without Messaging → startup error, or a synchronous
      fallback? Server-assigned `|` ids are unknown before the commit.
- [ ] Fix the side bug: DemoApp `PersonActions.OnBeforeDeleteAsync` broadcasts before the commit.

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
- [ ] The `Spikes/Issue467` tests are all green and moved; the `Spikes` folder is gone.
- [ ] Full local sweep (`npm run test:affected`, Developer licence), all five test projects green.
- [ ] Versions: NuGet minor (11.x), ng-spark minor (22.x), ng-bootstrap 22.21.0. Check the diff — CI
      publishes on merge.
- [ ] Update the #467 description, then open the PR closing #467.

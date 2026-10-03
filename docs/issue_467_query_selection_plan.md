# Plan — Issue #467 (one pull request)

Requirements, decisions (D1–D21, grilled 2026-10-03) and spikes (S1–S13) live in
[issue_467_query_selection_PRD.md](issue_467_query_selection_PRD.md). This file is the order of work.
Where the PRD's §2 and §7 disagree, §7 wins.

**Rules for executing this plan**
- One branch, one PR: `feat/467-query-selection`. Every decision D1–D21 lands in this PR.
- **Cross-repo prerequisite:** MintPlayer/mintplayer-ng-bootstrap#422 is fixed and released first (the
  same ordering #462 used). M4 waits for that release; M1–M3 and M5–M7 do not.
- Commit per milestone. **Do not run test suites per milestone.** Verify with a build + reading the code.
  Run a spike when a milestone depends on its answer, and record the result in PRD §7. One full sweep at
  the end (M9): `RAVENDB_LICENSE='C:\Repos\MintPlayer.Spark\.secrets\raven-license.log' npm run test:affected`.
- Write logs raw to the scratchpad (`cmd > x.log 2>&1; echo "EXIT: $?"`), then grep them.
- Never start `ng serve` next to a running host; `dotnet run` is the whole command.
- No backward compatibility (preview). Touched `libs/` csproj: minor bump within 11.x;
  `@mintplayer/ng-spark`: minor bump within 22.x. Breaking changes go in the release notes.

---

## Milestones

### M0 — ng-bootstrap (other repository, prerequisite)
- [ ] MintPlayer/mintplayer-ng-bootstrap#422 §1: the selection survives `[fetch]` paging and virtual
      eviction (`Map<key,row>` in `mp-datatable`, merge-by-key in the wrapper, `compareWith` used or removed).
- [ ] #422 §2: opt-in mode where a row click, right-click and Enter only emit, Space toggles, the whole
      checkbox cell is the hit target, and there is no Shift-range.
- [ ] Specs for both; release (minor bump, CHANGELOG `[Unreleased]`); bump the dependency in this repo.
      Release path: squash-merging the PR triggers `publish-master.yml` on ng-bootstrap's `master`.
      When that run completes, the packages are already on npmjs.com (`npm view @mintplayer/ng-bootstrap
      version`). Bump `@mintplayer/ng-bootstrap` (and `@mintplayer/web-components` if pinned) from the
      repo root only (`npm install` at the root).

### M1 — Translation composition (D2, D3)
- [ ] **S9** first: where merging happens (generator only, or runtime too).
- [ ] `HostTranslationsAggregatorGenerator.MergeTranslations` (`:136-196`): merge per (key, language);
      libraries in a stable order, the app last.
- [ ] `ConflictingKey` (`TranslationsDiagnostics.cs:43-46`): only library-vs-library, same (key,
      language), different value. App overrides and added languages are silent.
- [ ] Tests (written, run in M9): the app adds `es` to a library key and keeps `en/fr/nl`; the app
      overrides `nl` only; two libraries clash and warn; the app-over-library case raises no diagnostic.

### M2 — All localized text out of `App_Data` JSON (D1, D4, D5, D6)
- [ ] Key convention (`model.{Entity}.label`, `model.{Entity}.attributes.{Attr}.label|description`,
      `queries.{Query}.label`, `programUnits.{…}`, `security.groups.{…}`, `culture.{…}`) plus an optional
      explicit key field. The resolver falls back to the humanized name (`AddSpacesToCamelCase`).
- [ ] Model shape: `label`/`description` become an optional **key** (string); the entity-level
      `description` becomes `model.{Entity}.label` (D6). Server, `SparkModelShape`, ng-spark models,
      `resolveTranslation` call sites (po-edit/create/detail/form, grid headers, query-list title).
- [ ] `ModelSynchronizer`: stop writing inline labels (`:941`). **S10**, then the description seed (D5):
      add `en` to the app's `translations.json` only when no layer defines the key; verify fails exactly
      when sync would write.
- [ ] Info diagnostic / sync report: model elements whose key is missing for a language that
      `culture.json` declares (D4).
- [ ] `programUnits.json`, `security.json` group names, `culture.json` language names: keys.
- [ ] Migrate all apps (CodeCoverage, DemoApp, Fleet, HR, QnA; 814 inline strings) and the libraries'
      own model/config files into their `translations.json`. Back up before the bulk edit.
- [ ] `--spark-verify-model` / CI-only gates still pass on the migrated apps (read
      `reference_ci_only_gates_spark` first).

### M3 — `actions.json` composition and built-in Edit (D7, D8, R2)
- [ ] **S12** first: how libraries ship `App_Data/actions.json`.
- [ ] Rename `customActions.json` → `actions.json` (loader, docs, all apps). Text uses keys
      (`actions.{Name}.label|confirmation|description`).
- [ ] Core library `App_Data/actions.json`: New (no rule), Edit (`=1`, query + detail, pencil), Delete
      (`>0`, danger, confirmation with a count placeholder, D19). `SparkDefaultActions.cs` keeps only the
      reserved names (`IsDefault`: New, Edit, Delete).
- [ ] Per-property layering, `null` removal, library-vs-library conflict warning.
- [ ] `--spark-print-effective-actions`: the composed catalogue with each property's source layer.
- [ ] `ListCustomActions`: Edit under `Edit/T`; `ExecuteCustomAction` 404 for the reserved names.
- [ ] Detail page: Edit/Delete from the catalogue (`showedOn`, removal) ∩ `can.edit/can.delete`; the
      hard-coded buttons (`po-detail.component.html:14-21`) go (D8).
- [ ] Fix the stale rule doc comments (`CustomActionDefinition.cs:25`, `SparkCustomAction.cs:27-29`).
- [ ] Tests: Edit listed by right; override/removal; reserved-name `ICustomAction` never runs; custom
      action rule violation → 400 (R4); the print switch output.

### M4 — Selection and action strip (R1, R3, D9, D10, D19) — needs the M0 release
- [ ] **S2** inventory first.
- [ ] `SparkSelectionMode` = `auto|none|multiple` (remove `single`, server + ng-spark + QnA model).
- [ ] `selectionModeFor` from the **effective** list: right (server-filtered) ∩ `showedOn` includes the
      query ∩ not in `disabledActions` ∩ not hidden by deleted mode ∩ non-empty rule → `multiple`, else
      `none`. Same list feeds `toolbarActions()`. Recomputed on `disabledActions` / deleted-mode change;
      cleared when the mode becomes `none`.
- [ ] Grid: the #422 opt-in mode; a row click always opens (D9); the selection is kept across pages (D19);
      the chip shows the off-page count.
- [ ] Toolbar: `kind: 'edit'` (navigate to `/po/{type}/{id}/edit` with return state); Delete is no
      longer gated on "already selectable"; Edit hidden when `disabledActions` has `Edit`/`Save`; pencil
      icon in the query-list and card templates; Edit in the row ⋮ menu.
- [ ] `spark-query-list` `selectionMode` input.
- [ ] **S1** remainder: chip count equals what actions receive.
- [ ] Specs: one per R1 condition where only that condition fails and checkboxes disappear (no right /
      `showedOn` detail-only / disabled / recycle bin / no rule), explicit override, click opens,
      checkbox selects, cross-page selection.

### M5 — Write-path gates (D11, D12, D13, D18, D20, D21)
- [ ] **S4, S7, S8** red tests first.
- [ ] D11: Read right + read row filter + the list's deleted mode on delete-many and the
      `ExecuteCustomAction` fallback branch.
- [ ] D12: `queryId` required for bulk calls without a parent (400); delete-many fetches the rows through
      that query; `queryId` required in `SparkClient` and ng-spark `deleteMany`.
- [ ] D13: document object-level vs query-level `OnDisableActionsAsync` targets in the guide.
- [ ] D18: one refusal message with the redacted breadcrumbs of every failing readable row; unreadable or
      missing rows are counted, not named. **S3** for the cost.
- [ ] D20: one soft-delete `reason` on delete-many; required-reason types refuse an empty one.
- [ ] D21: `SparkThrottledException` in `Delete.cs`; fix the `DeleteMany.cs:109-110` comment; document
      self-saving `OnDeleteAsync`.

### M6 — Concurrency (D14, D15, D16)
- [ ] **S5, S11** first (projection change vector; resurrection red test; internal update callers).
- [ ] `QueryResultItem.Etag` (the document's change vector).
- [ ] D14: delete and delete-many take `(id, etag)` per row, 400 without, 409 on mismatch; the server
      deletes with that change vector. .NET client and ng-spark updated.
- [ ] D15: an Update of a deleted or soft-deleted document → 409 "deleted by another user", before any hook;
      `OnSaveAsync` writes with a change vector that requires the document to exist. Creates unchanged.
      ng-spark keeps the form values and shows the message.
- [ ] D16: Update requires an etag (400 without); internal callers load first or use the internal
      overwrite option that HTTP cannot reach.

### M7 — Durable after-commit work (D17)
- [ ] **S6** (hook classification) and **S13** (outbox in the same `SaveChanges`) first.
- [ ] Split the hook contract: in-request best-effort `OnAfter*` (failures logged, never an error for a
      committed change) + a durable after-commit hook run by a #369 messaging handler (retries,
      dead-lettering).
- [ ] Delete, delete-many and save write one outbox message per row in the same `SaveChanges`.
- [ ] Move the deferrable hooks of SoftDelete / Moderation / History / Contributions per S6.

### M8 — Demo, E2E, docs
- [ ] DemoApp / Fleet: an editor sees Edit + Delete in the strip; a read-only role sees no checkboxes.
- [ ] E2E: select one → Edit → edit page and back; select two → Edit disabled, Delete enabled → confirm
      (count shown) → rows gone; cross-page selection; read-only → no checkboxes; `selectionMode: none`;
      stale delete → 409; delete while someone edits → 409 "deleted by another user". Update
      `QnASubQueryTests` for click-opens.
- [ ] Docs: guide-custom-actions (actions.json, layering, removal, Edit, R1), guide-row-security (bulk
      gates), translations guide (composition, keys, seeding), release notes (all breaking changes).
      Mark issue_460_PRD D17 "superseded in part by #467".

### M9 — Full verification and PR
- [ ] Full local sweep (`npm run test:affected`, Developer licence), all five test projects green.
- [ ] Versions: NuGet minor (11.x), ng-spark minor (22.x), ng-bootstrap dependency at the #422 release.
      Check the diff — CI publishes on merge.
- [ ] PR closing #467.

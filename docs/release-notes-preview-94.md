# Spark 11.0.0-preview.94 — query checkboxes, list Edit/Delete, and framework-owned persistence (#467, #482)

**Packages:** every changed `MintPlayer.Spark*` NuGet package → `11.0.0-preview.94`. npm:
`@mintplayer/ng-spark` → `22.27.0`, which requires `@mintplayer/ng-bootstrap` `22.21.0`. The majors do
not move: the packages still target .NET 11 and Angular 22, and breaking changes ship as a minor.
*(The exact numbers are set when the versions are bumped; check the PR diff.)*

Two themes:

- **A list is a selection surface** (#467). An editor's lists show checkboxes and an action strip
  with the built-in Edit and Delete. Every bulk write re-reads its rows through the query they were
  ticked in, and every update or delete names the version it changes.
- **The framework owns persistence** (#482). Save and delete hooks are DI-registered per-phase
  interfaces instead of overridable Actions methods, so no override can skip a guarantee. After-commit
  work that must not be lost goes through a Messaging outbox, in the same transaction as the write.

The authoritative record, with every decision and its evidence, is
`docs/issue_467_query_selection_PRD.md` (§7, D1–D34).

---

## ⚠️ Breaking changes

No backward compatibility (preview). Each item says what to change.

### 1. Every localized string lives in `translations.json` (D1–D6, D23–D26)

- Model files, `programUnits.json`, `security.json` and `culture.json` carry **keys**, never text.
  A loader that finds inline `{ "en": … }` refuses to start and names the key it belongs under.
  `tools/migrate-467-translations.mjs` migrates an app (idempotent; back up first).
- Model elements find their text by convention (`model.{Entity}.label`,
  `model.{Entity}.attributes.{Attribute}.label|description`, `queries.{Query}.label`, …). The
  entity-level `description` became `label`.
- `culture.json` lists codes: `"languages": ["en", "fr", "nl"]`; names come from
  `culture.languages.{code}`.
- A `security.json` group's value is its untranslated **name**, and claims match the name only
  (D24); the displayed label is `security.groups.{name}.label`. `groupComments` is gone.
- Translations compose **per (key, language)** across the libraries and the app; an app's `""` counts
  as not defined. `SPARK_TRANS_005` now warns only when two *libraries* disagree.
- Synchronize no longer writes labels. It seeds attribute descriptions into the app's
  `translations.json` and prints the keys missing a declared language.

See `docs/guide-translated-strings.md`.

### 2. `customActions.json` → `actions.json`, composed in layers (D7, D8, D27)

- Rename the file. The core library's layer ships New, Edit and Delete; a library may ship its own
  (`SparkActionsLayer="library"`); the app's file composes on top per property.
- `"Edit": null` removes an inherited action; a property set to `null` resets it to the default
  (replacing the old `""` convention).
- Texts move to `actions.{Name}.label` / `.confirmation` / `.description`; `displayName` and
  `confirmationMessageKey` are refused with a hint. `{count}` in a confirmation is the selection size.
- The detail page's Edit and Delete come from the catalogue (`showedOn`), intersected with the row's
  `can` block; the hard-coded buttons are gone. Core `New` is `showedOn: "query"`.
- `--spark-print-effective-actions` prints the composed catalogue.

See `docs/guide-custom-actions.md`.

### 3. Selection (R1, D9, D10, D28)

- `SparkSelectionMode` is `auto | none | multiple`: **`single` is removed** (server and ng-spark).
- `auto` counts the built-in Edit and Delete like any action whose rule accepts a row, so a list where
  the user can edit or delete gets checkboxes, and a read-only user's list has none.
- **A row click opens the row; only the checkbox cell selects.** Actions whose rule does not match the
  count are disabled, and the selection is never trimmed. The selection survives paging; the chip
  counts rows on other pages, and its ⊗ clears them all.
- ng-spark: requires `@mintplayer/ng-bootstrap` 22.21.0 (checkbox selection mode).

### 4. Bulk writes go through the query and the Read gate (D11, D12, D18, D20, D29)

- `/spark/po/delete-many` and a custom action on a selection (with ids and no parent) **require
  `queryId`** (400 otherwise). The rows are fetched through that query and must be readable; a
  missing or unreadable row refuses the lot with 404.
- One refusal names every readable row that failed (by breadcrumb, with the reason). A row refused by
  the `Delete` row rule is now **403** naming it (was 404).
- `delete-many` takes an optional `reason`, recorded by SoftDelete on every row.

### 5. Every update and delete names the version it changes (D14–D16, D30)

- **Wire:** `/spark/po/update`, `/po/delete` and `/po/purge` require an `etag`; `/po/delete-many`
  takes `items: [{ id, etag }]` instead of `ids`. A missing etag is a 400.
- A stale etag is **409** (`reason: "changed"`). An update of a row that is gone, soft-deleted or no
  longer readable is **409 `deleted`** (was 404); ng-spark shows "Somebody else deleted this record".
  A natural-id collision on create is **409 `exists`**.
- `QueryResultItem.Etag` carries each row's change vector.
- .NET client: `DeletePersistentObjectAsync(typeId, id, etag)` (or pass the loaded object),
  `DeletePersistentObjectsAsync(typeId, rows, queryId, …)` with `SparkRowVersion`s;
  `UpdatePersistentObjectAsync` requires `Etag`. `ExecuteActionAsync` throws for a selection without
  `queryId`.
- ng-spark: `delete(type, id, etag)`, `deleteMany(type, items, { queryId, … })`.
- `IDatabaseAccess` callers without an etag (jobs, sync, imports) keep overwriting as before.

See `docs/guide-concurrency.md`.

### 6. Hooks replace interceptors and the Actions save/delete methods (#482, D31–D33)

- `IPersistentObjectInterceptor` is deleted. Implement the per-phase interfaces instead:
  `IBeforeSave`, `IAfterSave`, `IBeforeDelete`, `IAfterDelete`, `IAfterMaterialize`, `IAfterLoad`,
  `INaturalIdCollision` (each with a typed `<T>` form), plus `IDeleteReplacement` and
  `HookStage { Default, Finalize }`. Register with `spark.AddHook<T>()`, or let the hook generator
  find it (`AddSparkFull` calls `AddHooks`).
- The Actions class loses `OnSaveAsync`, `OnDeleteAsync`, `OnBeforeSaveAsync`, `OnAfterSaveAsync` and
  `OnBeforeDeleteAsync`; `MapAsync(obj, existing?)` is new. An Actions class may implement the hook
  interfaces for its own type without registering them.
- The framework owns the single commit: hooks never call `SaveChanges`. WITH CHECK exists only in
  the framework write.
- After-hooks run in registration order, isolated: a throwing after-hook is logged and never fails a
  committed change. A hook that wants to cancel throws `SparkCancelException` (delete answers 204,
  update 200 as stored, create 204); a `Retry.Action` from a hook during a bulk delete refuses its row.
- App hooks skip `Sync` unless they opt in (`HandlesSync`).
- A raw `session.Delete` of an `ISoftDeletable` document is refused outside `SparkRawWrites.Allow()`.

See `docs/guide-hooks.md`.

### 7. Durable after-commit hooks; observers removed (#482, D17, D34)

- New: `IAfterSaveCommitted` / `IAfterDeleteCommitted` (typed forms) receive a
  `SparkCommittedChange` (type, id, operation, user, previous change vector, **facts**) after the
  commit, delivered by Messaging from an outbox message written in the same transaction. Before-hooks
  hand details over through `SparkHookContext.Facts`; never put a secret in one.
- **A durable hook requires `spark.AddMessaging()`**; without it `UseSpark` throws. Tests can use
  `AddTestAfterCommitOutbox()`.
- Removed: `ISoftDeleteObserver`, `SoftDeleteEvent`, `AddSoftDeleteObserver`,
  `ISparkRevisionObserver`, `SparkRevisionEvent`, `AddRevisionObserver`. Implement a durable hook
  instead; the soft-delete reason is `change.Reason` and History's changed attributes are
  `change.Facts[SparkFacts.ChangedAttributes]`.
- **Moderation now requires Messaging:** the vote reversal after a moderator delete is a durable hook,
  so it happens on delivery, not in the request.
- New public API: `IMessageOutbox.EnqueueAsync(session, message, options)` stores a message in the
  caller's session, committed with the caller's `SaveChanges`.

---

## Apps

- **Fleet:** Recent cars declares `selectionMode: "none"`. Administrators get Edit and Delete in the
  strip; Fleet managers get Edit; Viewers get no checkboxes. Cancel on the "stolen" prompt now cancels
  the save, and a plate mismatch on delete is a 400 (was 500).
- **DemoApp:** anonymous visitors hold full CRUD and therefore get checkboxes, by design. The Person and
  Company broadcasts are durable hooks, and a refused delete no longer broadcasts.
- **CodeCoverage:** `Revoke` moved to the ApiToken card's action strip as a query action on the
  selected tokens (`>0`, with confirmation); every signed-in user gets checkboxes and Edit on the
  public lists, with row security deciding what they may change.
- **QnA:** the Answers card is `multiple` (was `single`).

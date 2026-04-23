# PRD: PersistentObject Factory — Follow-ups

## Status

Parent PRD: [`docs/PRD-PersistentObjectFactory.md`](./PRD-PersistentObjectFactory.md).
That PRD shipped in four PRs: #125 (DTO ownership), #126 (mapper + generic
overloads + `PersistentObjectIds` generator), #127 (SyncActionHandler
migration), #128 (E2E test migration).

The parent PRD listed five items as explicitly deferred to follow-up work.
This PRD captures each as its own implementable chunk, with a recommended
order and enough context for a fresh session to pick one up and ship it
without re-reading the parent.

## Recommended order

1. [Richer `PopulateObjectValues` (PO → entity)](#1-richer-populateobjectvalues-po--entity) — natural inverse of the forward path we just built, same testing playbook, clear scope. **Start here.**
2. [First-class `PersistentObjectAttributeAsDetail`](#2-first-class-persistentobjectattributeasdetail) — builds on (1)'s richer round-trip, introduces nested-PO support. Biggest lift of the five.
3. [Frontend popup-form rendering](#3-frontend-popup-form-rendering) — unblocks the parent PRD's retry-action popup pattern end-to-end; different surface (Angular/TypeScript) so it's a clean context switch.
4. [`CustomAction` return-value builder](#4-customaction-return-value-builder) — coordinate with `docs/custom-actions-prd.md` first so we don't duplicate design work.
5. [Rename `NewPersistentObject` → `GetPersistentObject`](#5-rename-newpersistentobject--getpersistentobject) — trivial mechanical rename, preview-mode breaking. Save for last so it rebases cleanly over (1)–(4).

Each section below is independently actionable.

---

## 1. Richer `PopulateObjectValues` (PO → entity)

### Why deferred

The parent PRD's §2 explicitly states:

> Rewriting the inverse path (`PO → entity`). Spark has
> `PopulateObjectValues<T>` and `ToEntity<T>` extensions; they stay as-is for
> now. Vidyano's richer `PopulateObjectValues` (reference resolution via
> `ITargetContext`, TranslatedString merging, concurrency tokens) is a
> follow-up PRD once the forward path is stable.

The forward path is now stable (through PRs #125–#128), so this is unblocked.

### Current state

- **`MintPlayer.Spark/Extensions/PersistentObjectExtensions.cs`** — the `PopulateObjectValues<T>(this PersistentObject, T entity)` and `ToEntity<T>()` extension methods survived PR #125 (the two *forward* extensions `ToPersistentObject<T>` / `PopulateAttributeValues<T>` were deleted). They do basic reflection-based property copying via a private `SetPropertyValue` helper — no reference resolution, no `TranslatedString` merging, no etag/concurrency handling.
- **`MintPlayer.Spark/Services/EntityMapper.cs`** — the `IEntityMapper` interface already has `ToEntity(object)` / `ToEntity<T>(PersistentObject)` methods that do the PO → entity direction via reflection and `SetPropertyValue`. **The extension methods are a parallel (and inferior) duplicate** — same smell as the pre-PR-#125 situation on the forward path.
- **Vidyano reference** — see the parent PRD's "Vidyano reference" section. Key: `PopulateObjectValues(object entity, ITargetContext? targetContext = null, bool includeAll = true)` on `PersistentObject`, with:
  - Reference attributes resolved via `targetContext.GetEntity(attribute)` (throws `DBConcurrencyException` on missing)
  - `TranslatedString` columns: JSON-parse the wire value, merge per-language entries with what's already on the entity
  - Type coercion via `DataTypes.FromServiceString` (Vidyano's canonical wire-string → CLR converter)
  - Attributes with `.` in their name are skipped (Vidyano parity for dot-notation nested paths)

### Design

**Surface** — add to `IEntityMapper`, mirroring the forward path's shape:

```csharp
public interface IEntityMapper
{
    // Existing forward-path methods (unchanged).
    PersistentObject NewPersistentObject(string name);
    PersistentObject NewPersistentObject(Guid id);
    PersistentObject NewPersistentObject<T>() where T : class;
    PersistentObject ToPersistentObject(object entity, Guid objectTypeId, Dictionary<string, object>? includedDocuments = null);
    PersistentObject ToPersistentObject<T>(T entity, Dictionary<string, object>? includedDocuments = null) where T : class;
    void PopulateAttributeValues(PersistentObject po, object entity, Dictionary<string, object>? includedDocuments = null);
    void PopulateAttributeValues<T>(PersistentObject po, T entity, Dictionary<string, object>? includedDocuments = null) where T : class;
    object ToEntity(PersistentObject persistentObject);
    T ToEntity<T>(PersistentObject persistentObject) where T : class;

    // NEW — richer inverse. Populates properties on an *existing* entity from the PO.
    // Handles reference resolution (via IAsyncDocumentSession.LoadAsync, mirroring the
    // includedDocuments dict on the forward path), TranslatedString merging, type
    // coercion for Guid/DateTime/DateOnly/enum/Color, concurrency token handoff.
    // Attributes with '.' in their name are skipped (reserved for nested AsDetail).
    Task PopulateObjectValuesAsync(PersistentObject po, object entity,
        IAsyncDocumentSession? session = null, CancellationToken cancellationToken = default);

    // Optional sync convenience for callers that don't need reference resolution:
    void PopulateObjectValues(PersistentObject po, object entity);
}
```

The async overload is needed because reference resolution hits RavenDB via
`session.LoadAsync<T>(refId)`. The sync version stays for callers that don't
touch references (or that pre-loaded the referenced documents via
`includedDocuments` on the forward path and now want to flow those through).

**Reference handling** — the forward path uses a `Dictionary<string, object>? includedDocuments` pre-load. The inverse should do the same to keep the APIs symmetric: callers pass the dict when they've already loaded referenced entities; otherwise the async overload resolves on-demand via `session`.

**`TranslatedString` merging** — when the entity's property is a `TranslatedString` and the incoming attribute value is a JSON dict (from the wire), the method should:
1. Parse the JSON into a `Dictionary<string, string>` of language-keyed values
2. If the entity's existing `TranslatedString` has entries for languages NOT in the incoming dict, preserve them (Vidyano behavior — a partial update with only `{ "en": "..." }` shouldn't wipe out the `"fr"` translation)
3. Overwrite entries for languages present in the incoming dict

**Extension cleanup** — delete `PersistentObjectExtensions.PopulateObjectValues<T>` and `ToEntity<T>` after migrating callers to `IEntityMapper` (same pattern as PR #125's deletion of the forward extensions). Note: internal `SetPropertyValue` in `EntityMapper.cs` is the existing source of truth for per-property coercion — enhance it with `TranslatedString` + reference branches, keep as the single coercion site.

### Acceptance criteria

- [ ] `IEntityMapper.PopulateObjectValuesAsync(po, entity, session)` sets every writable entity property whose name matches an attribute. Non-matching attributes are silent skips. Dot-notation attribute names are skipped.
- [ ] Reference attributes (`DataType == "Reference"` with `Value` = refId string): `session.LoadAsync<T>(refId)` resolves the referenced entity and assigns it to the property. Null-or-empty refId assigns null.
- [ ] `TranslatedString` attributes: incoming language dict merges with existing entity value (no lost translations on partial updates).
- [ ] `Guid` / `DateTime` / `DateOnly` / `Color` / enum coercion preserved (parity with existing `SetPropertyValue`).
- [ ] The sync `PopulateObjectValues` overload works identically for non-Reference attributes; throws `InvalidOperationException` if called on a PO containing Reference attributes (avoid silent data loss).
- [ ] `PersistentObjectExtensions.PopulateObjectValues<T>` and `ToEntity<T>` deleted; remaining framework call sites updated to inject `IEntityMapper`.
- [ ] Unit tests: reference resolution (existing + missing), TranslatedString merge (new + preserved), enum/Color/DateOnly coercion, dot-notation skip, concurrency-etag propagation.

### Estimated size

Medium. ~200 lines of production code (mapper changes) + ~150 lines of tests + ~30 lines of extension-deletion migration. One PR.

### Files to touch

- `MintPlayer.Spark.Abstractions/IManager.cs` — NO changes (inverse is mapper-scope, not user-facing)
- `MintPlayer.Spark/Services/EntityMapper.cs` — add new methods + helper enhancements
- `MintPlayer.Spark/Extensions/PersistentObjectExtensions.cs` — delete `PopulateObjectValues<T>` + `ToEntity<T>`, prune orphaned helpers
- `MintPlayer.Spark.Tests/EntityMapperInverseTests.cs` — new test file (or extend `EntityMapperFactoryTests`)

---

## 2. First-class `PersistentObjectAttributeAsDetail`

### Why deferred

Parent PRD's "Out of scope":

> **First-class `PersistentObjectAttributeAsDetail` (nested PO arrays) in
> the mapper.** Today `EntityMapper` converts `AsDetail`-typed values to a
> plain `Dictionary<string, object?>` for serialization; a richer port
> would use `PersistentObjectAttributeAsDetail` with nested scaffolded POs,
> letting the populate phase recurse. Follow-up PRD once Virtual POs + the
> scaffold/populate pipeline are stable.

Scaffold/populate is now stable. The dot-notation-skip hook in
`PopulateAttributeValues` is the forward-compat slot for this work.

### Current state

- **`MintPlayer.Spark/Services/EntityMapper.cs`** — the `ConvertValueForWire` helper converts AsDetail values to `Dictionary<string, object?>` (single) or `List<Dictionary<string, object?>>` (array). Fully flat — no nested PersistentObjects, no attribute metadata on nested fields, no per-nested-attribute Rules/Renderers.
- **`MintPlayer.Spark.Abstractions/PersistentObject.cs`** — only `PersistentObject` + `PersistentObjectAttribute`. No subclass or variant that models "this attribute contains nested POs".
- **`EntityAttributeDefinition`** — already has `AsDetailType` (CLR type of nested entity) and `IsArray` (one-vs-many). The JSON schema supports it.
- **Frontend** — demo apps' PO-form components have no UI for AsDetail attributes beyond the current flat-dict rendering. Whatever shape this PRD picks, the frontend needs follow-on work (track under §3).

### Design

**New type** — add `PersistentObjectAttributeAsDetail : PersistentObjectAttribute` in `MintPlayer.Spark.Abstractions`:

```csharp
public sealed class PersistentObjectAttributeAsDetail : PersistentObjectAttribute
{
    /// <summary>
    /// For `IsArray = false`: a single nested PO (or null when the CLR field is null).
    /// </summary>
    public PersistentObject? Object { get; set; }

    /// <summary>
    /// For `IsArray = true`: the nested PO collection. Each element is a fully scaffolded
    /// PO for the AsDetail entity type, with Parent set to this attribute (NOT to the
    /// outer PO — nested ownership is through the attribute).
    /// </summary>
    public IReadOnlyList<PersistentObject>? Objects { get; init; }
}
```

Wire-format impact — this is additive: JSON gains an optional `Object` / `Objects` property on attributes that have `DataType == "AsDetail"`. Legacy dict-of-strings output stays as a fallback when consumers don't supply a nested PO type.

**Mapper impact** — `EntityMapper.FromDefinition` produces a
`PersistentObjectAttributeAsDetail` (instead of a plain
`PersistentObjectAttribute`) when `def.DataType == "AsDetail"`. The scaffold
recurses — for each AsDetail definition, scaffold a child PO using
`modelLoader.GetEntityTypeByClrType(def.AsDetailType)`. `PopulateAttributeValues`
recurses into the child PO(s) when filling values.

**`ConvertValueForWire`** gets simpler — AsDetail no longer routes to dict conversion; instead `PopulateAttributeValues` recursively scaffolds+populates the nested PO and attaches it to `Object` / `Objects`.

**`PopulateObjectValues`** (§1) recurses the inverse direction.

### Acceptance criteria

- [ ] `PersistentObjectAttributeAsDetail` lives in Abstractions; subclass of `PersistentObjectAttribute`; wire-compatible (attributes without nested content still serialize identically).
- [ ] `EntityMapper.NewPersistentObject<T>()` for a `T` that has an AsDetail property returns a PO whose corresponding attribute is a `PersistentObjectAttributeAsDetail` with a pre-scaffolded empty child (IsArray=false) or empty list (IsArray=true).
- [ ] `PopulateAttributeValues` recursively fills nested POs. Parent back-reference on nested attributes points to the nested PO, not the outer one.
- [ ] `PopulateObjectValuesAsync` recursively fills nested entities (requires §1).
- [ ] Round-trip test: typed entity with a nested `Address` + a `List<CarreerJob>` — scaffold → populate → serialize → deserialize → `ToEntity` → assert deep equality with the original.
- [ ] Demo: HR's `CarreerJob[]` or `Address` on `Person` visibly uses the new type after migration.

### Estimated size

Large. ~400 lines including the new DTO type + recursive mapper changes + frontend-data round-trip tests + a demo migration. Probably two PRs (DTO + mapper; then demo + round-trip tests).

### Depends on

**§1** — the recursive populate-inverse path needs §1's richer `PopulateObjectValuesAsync`. Do §1 first.

---

## 3. Frontend popup-form rendering

### Why deferred

Parent PRD's §9 and "Out of scope":

> `SparkRetryActionModalComponent` currently ignores
> `persistentObject.attributes` (agent confirmed). For the popup flow to
> actually render the cloned attribute as a form input, the modal needs to
> learn to render an attributes array — that is **out of scope** for this
> PRD but explicitly flagged as a follow-up.

The parent PRD's [§8 retry-action popup pattern](./PRD-PersistentObjectFactory.md#8-retryaction-popup-pattern) example sends a PO with a confirmation attribute to the modal — but the modal throws it away. That's why this is a follow-up: the server side is now correct; the frontend just needs to render it.

### Current state

- **`node_packages/ng-spark/projects/ng-spark/src/lib/`** — the extracted ng-spark library (from earlier reusability work noted in memory). Contains `RetryActionModalComponent` candidates (check memory entry `components_comparison.md` for the exact location — possibly still in demo apps).
- **Demo apps** — DemoApp / HR / Fleet each have their own PO-form component that renders attribute lists based on `DataType` / `Renderer` / `Rules`. The form rendering logic is the thing to reuse inside the retry modal.
- **Server side** (already done) — `manager.NewPersistentObject(PersistentObjectNames.ConfirmDeleteCar)` produces a correctly-shaped PO with full metadata; the modal just needs to render it.

### Design

**Option A — embed the existing PO form** (preferred). The retry modal hosts the same `SparkPoFormComponent` that demo apps use for their main PO pages. Pass `persistentObject` as input, listen for value-changes on a submit event, return the modified PO to the caller of `manager.Retry.Action(...)`.

**Option B — minimal inline renderer**. A stripped-down renderer embedded directly in the modal, supporting only the common `DataType`s (string, number, boolean). Simpler but duplicates form-rendering logic.

Recommend **A** — reuse is the whole point of the ng-spark extraction work.

**Data flow:**
1. Server's `manager.Retry.Action(title, options, persistentObject)` returns an awaitable that suspends the request
2. Client receives the `ActionRequest` with the PO payload via the sync channel (existing plumbing)
3. `SparkRetryActionModalComponent` opens, renders the PO form, waits for user submit + option-button click
4. Submitted PO + selected option get sent back through the sync channel
5. Server wakes up, `Retry.Action` returns a `RetryActionResult { Option, PersistentObject }`

### Acceptance criteria

- [ ] `SparkRetryActionModalComponent` accepts a PO input and renders its attributes as form fields (one field per attribute, dispatching on `DataType`, honoring `IsRequired` / `IsReadOnly` / `Rules`).
- [ ] Submit collects current values + returns them along with the selected option to the server.
- [ ] `manager.Retry.Action` on the server sees the user's values on the returned `PersistentObject`.
- [ ] Worked example in a demo app: a `MergeWith`-style or `ConfirmDelete`-style CustomAction that opens the modal, user types a confirmation value, server acts on the submitted value.
- [ ] Playwright e2e test covering the full round-trip (open modal → fill field → submit → server sees value).

### Estimated size

Medium-large. Angular component work + existing form-renderer integration + sync-channel wire-up + e2e test. One or two PRs.

### Notes

- **This item is the only one that touches the frontend.** Different skillset from the rest — may be worth batching with other frontend work (or pulling in someone frontend-focused).
- Uses `@mintplayer/ng-bootstrap` for modal chrome (per memory).
- Kill all `node.exe` processes before dev-server testing (per memory — Windows zombie-process issue).

---

## 4. `CustomAction` return-value builder

### Why deferred

Parent PRD's "Out of scope":

> A `CustomAction` return-value builder that uses the factory (separate PRD
> once CustomActions land broadly).

### Current state

- **`docs/custom-actions-prd.md`** — the CustomActions PRD. READ THIS FIRST before designing anything here. Must coordinate so we don't duplicate design decisions about how CustomActions shape their return payloads.
- `IManager.NewPersistentObject<T>()` et al. are available (from PR #126) for CustomAction implementations to use, but there's no builder pattern yet that makes "I want to return a PO with a notification + next-action" ergonomic.

### Design

Don't design here — design in coordination with `docs/custom-actions-prd.md`'s author / the current state of that PRD's implementation. This PRD item is a placeholder that says: "when CustomActions land, make sure their return-value construction uses the factory pattern from day one, not hand-built POs."

Likely shape (subject to coordination):
```csharp
return CustomActionResult
    .Ok(manager.NewPersistentObject<Car>())
    .WithNotification("Car created", NotificationKind.Success);
```

### Acceptance criteria

Deferred until CustomActions PRD is further along. Revisit after §1–§3.

### Estimated size

Unknown until scope is locked with the CustomActions PRD.

---

## 5. Rename `NewPersistentObject` → `GetPersistentObject`

### Why deferred

Parent PRD's "Out of scope":

> Renaming `NewPersistentObject` to `GetPersistentObject` à la Vidyano — the
> Spark naming is already established in `prd-manager-retry-action.md`.

Left for last because it's a mechanical rename that breaks every call site — rebasing §1–§4 over it is cheaper than rebasing it over them.

### Current state

- **`IManager`** — `NewPersistentObject(string)` / `NewPersistentObject(Guid)` / `NewPersistentObject<T>()`
- **`IEntityMapper`** — same three overloads plus `ToPersistentObject(...)` / `PopulateAttributeValues(...)` (the `ToPersistentObject` and `PopulateAttributeValues` names stay — those aren't a Vidyano rename, just the `New*` → `Get*`).
- **Callers**: after PR #126 landed, `Manager` is the primary caller of `IEntityMapper.NewPersistentObject`. Demo apps have zero call sites today. Tests have direct call sites in `ManagerTests.cs` and `EntityMapperFactoryTests.cs`.
- **Generator output** — unchanged; `PersistentObjectNames` / `PersistentObjectIds` constants are unaffected by the method rename.

### Design

Straight rename. No semantic change.

- `IManager.NewPersistentObject(...)` → `IManager.GetPersistentObject(...)`
- `IEntityMapper.NewPersistentObject(...)` → `IEntityMapper.GetPersistentObject(...)`
- Update all internal call sites (Manager thin-forwards, EntityMapper self-calls, SyncActionHandler's schema-branch call, ManagerTests, EntityMapperFactoryTests)
- Update the parent PRD + this PRD + any other `docs/*.md` references

Because this is a preview-mode project (NuGet `10.0.0-preview.31` per memory), no deprecation path needed — just rename and bump the preview version.

### Acceptance criteria

- [ ] `grep -rn "NewPersistentObject" MintPlayer.Spark*/ --include='*.cs'` returns only the parent PRD's outdated references and this PRD (to be updated in the same commit).
- [ ] All 330+ tests still pass.
- [ ] `docs/PRD-PersistentObjectFactory.md` (parent) and this PRD updated to use the new name.
- [ ] CHANGELOG / preview-version notes mention the breaking rename if such a file exists.

### Estimated size

Small. ~50 lines of diff across maybe 10 files — mostly mechanical. Single-commit PR.

---

## Cross-cutting notes

- **Branch naming convention** — keep the `feat/`, `docs/`, `fix/` pattern established in PRs #125–#129. Suggested names: `feat/po-populate-object-values-async` (§1), `feat/po-as-detail` (§2), `feat/spark-retry-modal-form` (§3), `feat/custom-action-result-builder` (§4), `refactor/get-persistent-object-rename` (§5).
- **PR size discipline** — the parent PRD's phases averaged +300/-100 line diffs. Keep the same discipline here — if §2 gets big, split DTO/mapper changes from the demo migration into two PRs.
- **Test count after parent PRD** — 330 unit tests green on master at the time of writing. Each follow-up should hold or grow this number with no regressions.
- **Codecov carryforward** — PR #129 fixed the spurious `-33%` gate. Test-only or docs-only PRs in this series won't re-trigger it.
- **Memory** — `~/.claude/projects/C--Repos-MintPlayer-Spark/memory/feedback_oos_followthrough.md` records the preference that drove this PRD: out-of-scope items get explicit follow-through, not silent deferral. Future sessions should cycle back to any remaining §1–§5 item after the current one ships.

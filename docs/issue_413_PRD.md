# Product Requirements Document: Refresh triggers inside a single embedded AsDetail

**Issue**: #413
**Title**: Coverage - ProjectTargetPercent attribute never becomes visible
**Status**: Draft
**Created**: 2026-09-17
**Last Updated**: 2026-09-17

---

## Summary

The Coverage Gate card on a repository never shows or hides `Project target (%)` when the
comparison mode changes. The card is not Spark — it is `RepoGatePanelComponent`, 140 hand-written
lines with its own REST endpoints — and its show/hide is broken by an ordinary Angular signal
mutation under `OnPush`. Rather than repair the hand-written card, this work moves the gate onto
Spark's own `triggersRefresh` mechanism and deletes the card.

Doing that exposes a real framework gap: `triggersRefresh` works for a root attribute and for a
column inside an **array** AsDetail grid, but has never worked for a **single** (`isArray: false`)
embedded object, because such an object is edited in a modal whose recursive form is given no
refresh context. The server already dispatches a nested refresh to the row's own actions class with
the parent on `obj.Parent` — that design is correct and unchanged. What is missing is the client
ever sending such a request, and a path grammar that accepts an index-free `Gate.ProjectMode`.

The load-bearing trade-off: presentation rules live in `GateSettingsActions.OnRefreshAsync`, but the
server-side guarantee lives in `RepositoryActions` — the object that actually saves. The two can
drift; that is accepted deliberately, because teaching Spark's save path to re-derive rules per
detail row would put an unbounded hook loop on the hot path of every Create and Update.

---

## Overview

Three layers change:

1. **Framework (server)** — `NestedTrigger` learns an index-free grammar so `Gate.ProjectMode`
   resolves to the `Gate` attribute's row type; a non-array reader lifts a submitted
   `JsonValueKind.Object` the way `RowAt` lifts an array element.
2. **Framework (client)** — the AsDetail modal's recursive `spark-po-form` gains a trigger-path
   prefix and emits trigger events upward; the parent form issues the request on its own
   coordinator and applies the response to `asDetailFormData`.
3. **Application (CodeCoverage)** — `Repository.Gate` becomes writable, `ProjectMode` gets
   `triggersRefresh`, `GateSettingsActions.OnRefreshAsync` shows/hides `ProjectTarget`,
   `RepositoryActions` enforces the gate's rules on save, and the bespoke panel plus its REST
   endpoints are deleted.

### Naming note

The issue says "Project comparison" and `Project Target (%)`; those are labels invented by the
hand-written card. The underlying attributes are `GateSettings.ProjectMode` and
`GateSettings.ProjectTarget`. There is no attribute named `ProjectComparison` or
`ProjectTargetPercent` anywhere in the codebase.

---

## Goals & Objectives

### Primary Goals

- Changing the gate's comparison mode shows or hides the project target, driven by the server hook.
- The gate is editable through the standard Spark PO form; the bespoke card is gone.
- `triggersRefresh` becomes a capability of single embedded AsDetail objects generally, not a
  special case for CodeCoverage.
- No weakening of the server-side validation that `PutGate` provided today.

### Success Metrics

- Selecting `fixed` on `/po/repository/{id}` reveals `Project Target`; selecting `auto` hides it,
  with the decision made server-side.
- A client that never calls `/spark/po/refresh` cannot save `mode=fixed` with a null target.
- `RepoGatePanelComponent`, `browse.service.getGate/putGate` and
  `RepoSettingsController.GetGate/PutGate` no longer exist.
- No change to any byte stored in `Repositories.Gate` or `Builds.GateSnapshot`.

---

## Chosen Design

**Design fan-out not run in its generic Workflow form.** A three-lens targeted reconnaissance was
run instead (client plumbing / server grammar + save path / data-migration risk), because the
interface question here is not "which of several shapes is nicest" but "which shapes are physically
possible against the shipped contracts". Two candidate shapes were identified and one was
eliminated by measurement rather than preference — see below.

### The shape

The child form does not talk to the server. It is told **where it sits** and **reports upward**;
the parent owns the request, the coordinator and the response.

```ts
// SparkPoFormComponent — new inputs/outputs
/** Path prefix identifying this form as an embedded object inside a parent's attribute, e.g. "Gate". */
triggerPathPrefix = input<string | null>(null);
/** Emitted instead of self-issuing when this form is embedded; carries the full path, e.g. "Gate.ProjectMode". */
nestedTriggerRequested = output<string>();
```

```html
<!-- spark-po-form.component.html, the modal's recursive form -->
<spark-po-form
  [entityType]="attr | asDetailType:asDetailTypes()"
  [(formData)]="asDetailFormData"
  [parentId]="parentId()"
  [parentType]="parentType()"
  [triggerPathPrefix]="editingAsDetailAttr()?.name ?? null"
  (nestedTriggerRequested)="onEmbeddedTrigger($event)" />
```

The parent's handler mirrors the existing `onInlineCellChange` exactly, differing only in the
discriminant it records:

```ts
onEmbeddedTrigger(path: string): void {
  this.pendingNestedTrigger = { kind: 'object', attribute: path.split('.')[0] };
  void this.refreshCoordinator.trigger(path);
}
```

`pendingNestedTrigger` becomes a **tagged union**, not a nullable index:

```ts
private pendingNestedTrigger:
  | { kind: 'row'; attribute: string; rowIndex: number }
  | { kind: 'object'; attribute: string }
  | null = null;
```

Server side, `NestedTrigger` gains a nullable index and a second grammar branch:

```csharp
internal readonly record struct NestedTrigger(string Attribute, int? RowIndex, string Column);
// "Jobs[1].ProfessionId" -> ("Jobs", 1, "ProfessionId")
// "Gate.ProjectMode"     -> ("Gate", null, "ProjectMode")
// "FullName"             -> null  (falls through to the root hook, unchanged)
```

`BuildNestedRow` keeps its structure; only the row extraction forks — `RowAt` for an index,
a new `ObjectValue` reader for `JsonValueKind.Object`. Everything downstream is untouched:
the nested `EntityTypeDefinition` still comes from `attribute.AsDetailType`, `row.Parent` is
still set to the parent object, and dispatch still lands on the row type's own actions class.

### What complexity this hides

- The child form never learns about authorization, object ids or payload shape. It knows only its
  own `entityType` and a string prefix.
- Authorization stays where the array path already puts it: on the **root** type in the route.
  Nested AsDetail types are deliberately absent from `security.json` (`Refresh.cs:118-125`), so the
  right that governs editing a gate is the right that governs editing its Repository.
- One coordinator per form instance survives untouched, so the supersede/serialization semantics
  (`refresh-coordinator.ts:66-87`) are unchanged and the guarding test at
  `spark-po-form-refresh.spec.ts:484-498` stays green.

### Designs considered (and rejected)

- **Child issues its own request, given the parent's `objectTypeId` + a prefix.** Rejected on
  measurement, not taste: `buildRefreshPayload` (`spark-po-form.component.ts:614-633`) reads
  `this.entityType()`, which inside the modal is `GateSettings`. The request would carry a
  `GateSettings`-shaped body under a `Repository` type id and fail to round-trip. Passing the
  *row type's* id instead would refresh `GateSettings` as a top-level type, which is an
  authorization bypass — nobody grants rights on `GateSettings`.
- **Share the parent's coordinator with the child.** Rejected: contradicts the documented
  per-instance rule (`refresh-coordinator.ts:16-26`) and breaks an existing test. Emitting an event
  upward achieves the same sequencing without the coupling.
- **Teach Spark's save path to re-derive rules per detail row.** Rejected — see Out of Scope.
- **Convert `ProjectMode`/`ProjectBasis` to capitalised enums.** Rejected — see Out of Scope.

### The trap in the chosen design

The parent now owns state about a form it does not render. If the `pendingNestedTrigger`
discriminant were ever collapsed back to a nullable `rowIndex`, an array refresh carrying a bad
index would fall silently into the object branch and write the response to the wrong place. The
tagged union is load-bearing, not stylistic — `applyNestedResponse` today already fails
half-silently in exactly this way (`:537`'s `Array.isArray` guard skips the value half while the
metadata half still runs).

---

## Out of Scope

- **Save-time re-derivation of detail-row rules** (`RefreshInvoker.BuildEffectiveAsync` descending
  into `AsDetailType` attributes) — *Rationale: `GateSettingsActions.OnSave` is not a thing; the
  Repository is what saves, so the gate's server-side validation belongs in `RepositoryActions`.
  Descending would also put N rows × M triggers of hook invocation on the hot path of every Create
  and Update against a 30-request budget (`RefreshInvoker.cs:83`), with a DB load per hook call in
  the existing HR sample. The consequence is accepted and documented: an `IsRequired` set by a
  detail row's refresh hook is a presentation rule only.*
- **Converting `ProjectMode`/`ProjectBasis` to capitalised C# enums** — *Rationale: enums persist
  by name (`M_202609092000_DeleteBranchFlagBecomesAPolicy.cs:62-70`), so `"auto"` would become
  `"Auto"` across `Repositories.Gate` and `Builds.GateSnapshot` — the latter written on every
  published build — requiring an RQL migration over the largest collection. It would also diverge
  the stored vocabulary from `coverage.yml`, a user-authored public file format documented at
  `docs/code-coverage/upload-api.md:383-392` that cannot be migrated.*
- **Per-row redaction on the nested refresh response** — *Rationale: the nested branch returns at
  `Refresh.cs:133` before `ApplyRedactionOf`, and that method is root-shaped by construction. Out
  of scope here but documented; see Open Questions.*
- **`GateEvaluator`'s synthetic `"whole"` basis value** (`GateEvaluator.cs:24`) — *Rationale: a
  derived, non-selectable state, not part of the user-facing vocabulary. It stays a string
  comparison and must not acquire a lookup entry.*
- **Repairing `RepoGatePanelComponent`'s signal-mutation bug** — *Rationale: the component is
  deleted by this work; fixing it first would be throwaway.*
- **`RotateBadgeToken`** — *Rationale: unrelated endpoint on the same controller; stays.*

---

## Functional Requirements

### Must Have (P0)

- [ ] **FR-1**: A `triggersRefresh` attribute inside a single (`isArray: false`) AsDetail object
      fires a refresh request when its value changes in the AsDetail modal.
- [ ] **FR-2**: The request is addressed `{attribute}.{column}` and is authorized on the **root**
      type, matching the array path's rule.
- [ ] **FR-3**: The server dispatches to the embedded type's own actions class
      (`GateSettingsActions.OnRefreshAsync`), with the owning object available as `obj.Parent`.
- [ ] **FR-4**: The response applies to the modal's working copy (`asDetailFormData`) and to the
      embedded type's column metadata — never to the parent's top-level overlay.
- [ ] **FR-5**: Cancelling the modal discards refreshed values; the parent's `formData` is written
      only by `saveAsDetailObject`.
- [ ] **FR-6**: A bare root trigger name (no dot) still reaches the root hook; an array path still
      reaches the row hook. Neither path changes behaviour.
- [ ] **FR-7**: `Repository.Gate` and the `GateSettings` attributes are editable in the Spark PO
      form, restricted to owners by the existing `RepositoryActions.GetRowFilterAsync` write arm.
- [ ] **FR-8**: Selecting comparison mode `fixed` reveals `Project Target`; `auto` hides it.
- [ ] **FR-9**: `RepositoryActions` rejects a save with `ProjectMode = fixed` and a null
      `ProjectTarget`, targets outside 0-100, or thresholds outside 0-100 — the checks `PutGate`
      performed.
- [ ] **FR-10**: `RepoGatePanelComponent`, its spec, its mount in `po-detail-page.component.ts`,
      `browse.service.getGate/putGate`, and `RepoSettingsController.GetGate/PutGate` are removed.
- [ ] **FR-11**: No stored byte in `Repositories.Gate` or `Builds.GateSnapshot` changes, and
      `coverage.yml` continues to accept lowercase `auto|fixed|scoped|projection`.

### Should Have (P1)

- [ ] **FR-12**: A `triggersRefresh` on a **free-text** inline AsDetail column fires on blur.
      Today `spark-po-form.component.html:190,199` bind `(ngModelChange)="onFieldChange()"` with no
      argument, so `onInlineCellChange` never runs and no request is ever issued. Same mechanism,
      found while investigating; included per the single-PR rule.
- [ ] **FR-13**: `ProjectMode` and `ProjectBasis` render as dropdowns with the labels the deleted
      card used, via a string-keyed `TransientLookupReference` (M0 decides feasibility).

---

## Timeline & Milestones

### Milestone 0: Spike — string-keyed lookup

- [ ] Prove `TransientLookupReference<string>` with `Key = "auto"` round-trips: synchronizer emits
      `lookupReferenceType`, `LookupReferenceService` keys the list by `"auto"`, `EntityMapper`
      writes the posted key back to a `string` property.
- [ ] If it fails, fall back to lowercase enum member identifiers (`auto`, `fixed`) — byte-identical
      storage, still no migration. Record the outcome in this PRD.

### Milestone 1: Server grammar

- [ ] `NestedTrigger.Index` → `int? RowIndex`; second `TryParse` branch for `{attr}.{col}`.
- [ ] `ObjectValue` reader beside `RowAt` for `JsonValueKind.Object`.
- [ ] Guard that the model attribute's `IsArray` agrees with the presence of an index, following
      the precedent in `New.cs:146-152` / `DeleteRow.cs:120-124`.
- [ ] Tests: the first server-side coverage of the nested path in either form.

### Milestone 2: Client plumbing

- [ ] `triggerPathPrefix` input + `nestedTriggerRequested` output on `SparkPoFormComponent`.
- [ ] Wire the modal's recursive form; route `noteChange` through the prefix.
- [ ] `pendingNestedTrigger` tagged union; single-object arm in `applyNestedResponse` writing
      `asDetailFormData.set({...})`.
- [ ] FR-12: inline free-text columns pass their column to `onInlineCellChange`.
- [ ] Specs mirroring `describe('AsDetail row triggers')`, plus a cancel-discards case.

### Milestone 3: Model

- [ ] `Repository.json` — `Gate` writable.
- [ ] `GateSettings.json` — attributes writable, `triggersRefresh` on `ProjectMode`, lookups per M0,
      labels matching the deleted card.
- [ ] Regenerate `modelHashes.json` in the same commit.

### Milestone 4: Behaviour

- [ ] `GateSettingsActions.OnRefreshAsync` — presentation only, establishing complete state on every
      call.
- [ ] `RepositoryActions` — the server-side rules that replace `PutGate`'s `BadRequest` checks.

### Milestone 5: Deletion

- [ ] Remove the panel, its spec, its mount, the browse-service methods, the two controller actions
      and their tests. Keep `RotateBadgeToken`.

### Milestone 6: Docs

- [ ] `docs/guide-triggers-refresh.md` — a single-object section beside "Triggers inside a detail
      grid", including the save-enforcement caveat.
- [ ] Check `{ProjectMode}` breadcrumb against the #384 regression tests.

---

## Open Questions

- [ ] **Redaction on the nested refresh response.** The nested branch returns before
      `ApplyRedactionOf` (`Refresh.cs:133` vs `:235-258`), and a row hook that loads from the
      database and writes onto the row is not redacted. — *Assumption: documented exemption, not
      fixed here. `GateSettings` carries no protected attributes (`BadgeToken` lives on
      `Repository` and is withheld by `GetProtectedAttributesAsync`), so this work adds no
      exposure. Flagged for a follow-up issue.*
- [ ] **Validation error keys for a single AsDetail.** The client reads inline errors keyed
      `{attr}[{i}].{col}` (`spark-po-form.component.ts:469-478`); nothing consumes a
      `Gate.ProjectMode` key yet. — *Assumption: not needed, because FR-9 puts the enforcement on
      `RepositoryActions`, whose errors key on the root attribute.*

---

## Technical Notes (Issue-Specific)

- `--spark-verify-model` already covers nested model files
  (`SparkDevelopmentExtensions.cs:289-342`), so declaring `triggersRefresh` in `GateSettings.json`
  without a `GateSettingsActions.OnRefreshAsync` override fails CI today, unchanged. There is no
  `GateSettingsActions.cs` yet — M3 and M4 must land together.
- A nested type with no actions class resolves to `DefaultPersistentObjectActions<T>` and the hook
  is skipped **without diagnostic** (`RefreshInvoker.cs:190-193`). CI is the only thing that
  catches it.
- `EffectiveObjectFactory.Build` copies only `Value`/`IsValueChanged` and never fills
  `Object`/`Objects` (`EffectiveObjectFactory.cs:50-57`), so both paths depend on the row arriving
  in `attribute.Value` as a `JsonElement`. That holds for the shipped client, which sends no
  `dataType`.
- `EntityMapper`'s write path swallows conversion failures in a bare `catch`
  (`EntityMapper.cs:1159-1162`), so a mismatched lookup key is a silent no-op rather than an error.
  This is why M0 exists.

---

## Related

- Issue #413
- `docs/guide-triggers-refresh.md` — the mechanism this extends
- `docs/coverage_project_automation_PRD.md:630` — named this exact conversion as out of scope at
  the time, and named the hand-written panel as the cost of not doing it
- See CLAUDE.md for: `apps/CodeCoverage` is production; package majors track the platform

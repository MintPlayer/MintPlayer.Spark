# Development Plan: Issue #413

**Issue**: #413
**Title**: Coverage - ProjectTargetPercent attribute never becomes visible
**Type**: Feature / Enhancement (framework capability + application migration)
**Priority**: Medium

## Executive Summary

Move the repository Coverage Gate off its bespoke Angular card and onto Spark's `triggersRefresh`
mechanism, and — because the gate is a single embedded object rather than a grid — teach Spark to
fire a refresh from inside the AsDetail modal. The user-visible symptom in the issue disappears
because the hand-written show/hide that produced it is deleted.

---

## Problem Statement

### Current Behavior

On `/po/repository/{id}`, the "Coverage gate" card's "Project comparison" select never shows or
hides "Project target (%)". The card is `RepoGatePanelComponent`, mounted as an extra in
`po-detail-page.component.ts:44`, with its own `RepoSettingsController` endpoints. Its `gate` is a
`signal<GateSettings | null>`; `[(ngModel)]="g.projectMode"` mutates the held object in place, the
signal reference never changes, and under `ChangeDetectionStrategy.OnPush` the
`@if (g.projectMode === 'fixed')` guard is never re-evaluated. The value does change — a save
persists it — only the show/hide never happens.

Separately, the Spark-native route to the same behaviour does not work: `Repository.Gate` is
`isReadOnly: true` so it is filtered out of the edit form entirely, and even made writable, a
non-array AsDetail is edited in a modal whose recursive `spark-po-form` is never given a refresh
context (`canRefresh()` requires an `objectTypeId` the modal does not receive).

### Expected Behavior

The gate is edited through the standard Spark PO form. Changing the comparison mode posts
`Gate.ProjectMode` to `/spark/po/refresh`; the server dispatches `GateSettingsActions.OnRefreshAsync`
with the Repository on `obj.Parent`; the hook shows or hides `ProjectTarget`; the response reshapes
the modal. Saving is guarded by `RepositoryActions`, which enforces the rules `PutGate` enforced.

### Impact

Production (`coverage.mintplayer.com`). Repository owners currently cannot tell from the UI whether
a project target applies, and the panel lets them save a `fixed` policy whose target field was never
shown. Framework-wide, every Spark consumer with a single embedded object silently loses
`triggersRefresh`.

---

## Technical Analysis

### Files to Modify

**Framework — server** (`libs/spark/MintPlayer.Spark/`)
- `Endpoints/PersistentObject/Refresh.cs` — `NestedTrigger` (`:271`), `TryParse` (`:273-290`),
  `BuildNestedRow` (`:161-184`), new object reader beside `RowAt` (`:190-214`).

**Framework — client** (`libs/node_packages/ng-spark/po-form/src/`)
- `spark-po-form.component.ts` — new input/output, `noteChange` (`:572-581`),
  `pendingNestedTrigger` (`:518`), `applyNestedResponse` (`:530-559`), `onInlineCellChange`
  (`:503-515`).
- `spark-po-form.component.html` — the modal's recursive form (`:410-415`); free-text inline
  columns (`:190`, `:199`).
- `spark-po-form-refresh.spec.ts` — new `describe` block.

**Application** (`apps/CodeCoverage/`)
- `CodeCoverage/App_Data/Model/Repository.json` — `Gate` writable.
- `CodeCoverage/App_Data/Model/GateSettings.json` — writable attributes, `triggersRefresh`,
  lookups, labels.
- `CodeCoverage/App_Data/modelHashes.json` — regenerated.
- `CodeCoverage/Actions/GateSettingsActions.cs` — **new**.
- `CodeCoverage/Actions/RepositoryActions.cs` — save-time gate validation.
- `CodeCoverage.Library/LookupReferences/` — **new** lookup(s), pending M0.
- `CodeCoverage/Controllers/RepoSettingsController.cs` — delete `GetGate`/`PutGate`.
- `CodeCoverage/ClientApp/src/app/components/repo-gate-panel/` — **deleted**.
- `CodeCoverage/ClientApp/src/app/spark/po-detail-page.component.ts` — drop the mount.
- `CodeCoverage/ClientApp/src/app/services/browse.service.ts` — drop `getGate`/`putGate`.
- `CodeCoverage.Tests/Controllers/RepoSettingsControllerTests.cs` — drop the gate tests.

**Docs**
- `docs/guide-triggers-refresh.md`.

### Dependencies

- M0 (the string-keyed lookup spike) gates M3's shape. Everything else is independent.
- No external services. No package version bump: no targeted platform major changes.

### Architecture Considerations

See the PRD's **Chosen Design**. The short version: the child form reports upward, the parent owns
the request. Authorization stays on the root type, matching the array path
(`Refresh.cs:118-125`). Presentation rules live in the refresh hook; the server-side guarantee
lives in `RepositoryActions`, because the Repository is what saves — `GateSettingsActions.OnSave` is
never invoked.

---

## Implementation Plan

### Phase 1: Spike (M0)

1. Build a throwaway `TransientLookupReference<string>` with `Key = "auto"`; confirm the
   synchronizer emits `lookupReferenceType`, `LookupReferenceService` keys by `"auto"`, and a
   posted `"auto"` reaches the `string` property through `EntityMapper`.
2. Record the outcome in the PRD. On failure, switch to lowercase enum member identifiers.

### Phase 2: Framework server (M1)

3. `NestedTrigger.Index` → `int? RowIndex`; add the `{attr}.{col}` grammar branch, still returning
   `null` for a bare name so the root hook runs.
4. Add the `JsonValueKind.Object` reader; fork `BuildNestedRow`'s extraction on `RowIndex`.
5. Add the `IsArray`/`DataType` agreement guard.
6. Server tests for both nested grammars — none exist today.

### Phase 3: Framework client (M2)

7. Add `triggerPathPrefix` / `nestedTriggerRequested`; route `noteChange` through the prefix.
8. Wire the modal's recursive form to the parent's handler.
9. Convert `pendingNestedTrigger` to the tagged union; add the single-object arm to
   `applyNestedResponse`, writing `asDetailFormData.set({...})`.
10. FR-12: pass the column from the free-text inline editors.
11. Specs.

### Phase 4: Application (M3, M4)

12. Model: `Gate` writable, `GateSettings` attributes writable, `triggersRefresh` on `ProjectMode`,
    lookups, labels from the deleted card. Regenerate `modelHashes.json`.
13. `GateSettingsActions.OnRefreshAsync` — establish complete presentation state every call.
14. `RepositoryActions` — the gate's save-time rules.

### Phase 5: Deletion and docs (M5, M6)

15. Delete the panel, the mount, the browse-service methods, the two controller actions, their tests.
16. Update `docs/guide-triggers-refresh.md`.

---

## Test Scenarios

### Scenario 1: Single-object trigger reaches the embedded type's hook

- **Given**: a `Repository` open in the PO form with the `Gate` modal open
- **When**: `ProjectMode` changes to `fixed`
- **Then**: one request to `/spark/po/refresh` with `triggeredBy == "Gate.ProjectMode"`, dispatched
  to `GateSettingsActions.OnRefreshAsync` with `obj.Parent` being the Repository

### Scenario 2: Response reshapes the modal, not the parent

- **Given**: the hook sets `ProjectTarget.IsVisible = true`
- **When**: the response arrives
- **Then**: values land in `asDetailFormData`, metadata in `asDetailTypes()['Gate']`; the parent's
  top-level overlay and rendered attributes are untouched

### Scenario 3: Cancel discards a refreshed value

- **Given**: a refresh has applied values inside the open modal
- **When**: the modal is dismissed
- **Then**: `formData()['Gate']` is unchanged

### Scenario 4: Existing paths unaffected

- **Given**: HR's `Person.Jobs[1].ProfessionId` and Fleet's root `Car.Status`
- **When**: each triggers
- **Then**: behaviour is byte-identical to today, including row array identity

### Scenario 5: Save is guarded without a refresh

- **Given**: a client that never calls `/spark/po/refresh`
- **When**: it saves `ProjectMode = fixed` with a null `ProjectTarget`
- **Then**: `RepositoryActions` refuses the save

### Scenario 6: Stored representation is untouched

- **Given**: an existing repository with `Gate.ProjectMode == "auto"`
- **When**: the gate is loaded, edited and saved through the PO form
- **Then**: the stored value is still lowercase `auto`, and `coverage.yml` overrides still parse

---

## Acceptance Criteria

- [ ] FR-1 … FR-11 met (PRD)
- [ ] Selecting `fixed` reveals `Project Target`; `auto` hides it, decided server-side
- [ ] No server-side validation lost relative to `PutGate`
- [ ] `RepoGatePanelComponent` and the gate endpoints are gone
- [ ] Existing root and array/inline refresh tests pass unchanged
- [ ] First server-side tests of the nested refresh path exist
- [ ] No stored byte in `Repositories.Gate` / `Builds.GateSnapshot` changes
- [ ] `--spark-verify-model` passes

---

## Build & Test Commands

```bash
dotnet build MintPlayer.Spark.sln -c Debug
dotnet test tests/MintPlayer.Spark.Tests/MintPlayer.Spark.Tests.csproj -c Debug
dotnet test apps/CodeCoverage/CodeCoverage.Tests/CodeCoverage.Tests.csproj -c Debug
npx nx test ng-spark
```

Test runs are batched at the end, after all milestones are implemented.

---

## Related Files

- `libs/spark/MintPlayer.Spark/Endpoints/PersistentObject/Refresh.cs`
- `libs/spark/MintPlayer.Spark/Services/RefreshInvoker.cs` (read-only reference; not modified)
- `libs/node_packages/ng-spark/po-form/src/spark-po-form.component.{ts,html}`
- `apps/CodeCoverage/CodeCoverage/App_Data/Model/{Repository,GateSettings}.json`
- `apps/CodeCoverage/CodeCoverage/Actions/RepositoryActions.cs`
- `apps/HR/HR/Actions/CarreerJobActions.cs` (the array-path reference sample)
- `docs/guide-triggers-refresh.md`

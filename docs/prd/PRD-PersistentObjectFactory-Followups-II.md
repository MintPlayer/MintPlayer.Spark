# PRD: PersistentObject Factory Follow-ups — Part II

## Status

Continuation of [`docs/PRD-PersistentObjectFactory-Followups.md`](./PRD-PersistentObjectFactory-Followups.md). Items §1–§3 of that PRD shipped via **PR #130**:

- §1 Richer `PopulateObjectValues` (PO → entity inverse path) ✅
- §2 First-class `PersistentObjectAttributeAsDetail` + polymorphic JSON converter ✅
- §3 Frontend popup-form rendering in retry modal ✅

This PRD covers the two remaining items (§4 and §5 of the predecessor), renumbered here as §1 and §2. The two items are unrelated in scope but share the same context (factory work stream) and similar "small, scoped, doable without new architecture" size, so they live in a single PRD.

## Recommended order

1. **§2 Rename `NewPersistentObject` → `GetPersistentObject`** first — mechanical, unblocked, fast. Doing this before §1 means §1's design doesn't need to mention the old name.
2. **§1 `CustomAction` return-value builder** — currently **blocked** on the Custom Actions PRD (`docs/custom-actions-prd.md`) committing to a return-value shape. Revisit when that PRD's §8 ("Future Phase: Navigate & Notify via IManager") is closed out.

---

## 1. `CustomAction` return-value builder

> **Status: obsoleted by [`docs/PRD-ClientOperations.md`](./PRD-ClientOperations.md).**
> A return-value builder on `ICustomAction.ExecuteAsync` was never the right shape. The Client Operations PRD generalizes the problem: backend side-effects (including what this builder would have produced — navigate, notify) are accumulated onto `IManager.Client` during action execution and ride out in a unified response envelope. This applies uniformly to CustomActions, CRUD PO actions, and any future action type — not just CustomActions. No `CustomActionResult` object will exist; `ICustomAction.ExecuteAsync`'s signature stays `Task`. The content below is preserved as historical context for the decision.

### Why deferred (from predecessor PRD §4)

> A `CustomAction` return-value builder that uses the factory (separate PRD
> once CustomActions land broadly).

### Current state

- **`docs/custom-actions-prd.md`** defines Custom Actions v1. Key quote from §4.1:
  ```csharp
  public interface ICustomAction
  {
      Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default);
  }
  ```
  v1 deliberately has **no return type** — `Task`, not `Task<T>`. A "return-value builder" with no return value to build is premature.
- **`docs/custom-actions-prd.md` §8** — "Future Phase: Navigate & Notify via IManager" — is the hook where return-shape lands. Until that phase commits to a `CustomActionResult` (or equivalent), this item has nothing concrete to design against.
- **`IEntityMapper.NewPersistentObject<T>()`** et al. are available (shipped in PR #126) for CustomAction implementations to use directly. A builder is ergonomic sugar on top; it is not load-bearing.

### Blocker

**Custom Actions PRD must commit to `CustomActionResult` first.** Expected shape (per predecessor PRD §4 sketch):

```csharp
return CustomActionResult
    .Ok(manager.NewPersistentObject<Car>())   // or GetPersistentObject<Car>() post-rename
    .WithNotification("Car created", NotificationKind.Success);
```

But this sketch is **not yet design** — it's a placeholder. The actual shape (navigate? notify? error? follow-up action? PO payload vs query refresh?) is a Custom Actions design decision that belongs in `docs/custom-actions-prd.md`, not here.

### Design (when unblocked)

Once `CustomActionResult` is defined by the Custom Actions PRD, this item becomes: "wire the factory into `CustomActionResult`'s PO-producing factory methods so callers can't hand-build POs." Specifically:

- Any `CustomActionResult` method that takes a PO — `.Ok(po)`, `.WithContent(po)`, etc. — gets a generic overload `.Ok<T>()` / `.WithContent<T>()` that calls `IEntityMapper.GetPersistentObject<T>()` internally.
- Documentation pass on `docs/guide-custom-actions.md` to show the builder as the one-true-way to construct action results; mark hand-built POs as an anti-pattern.

### Acceptance criteria (when unblocked)

- [ ] Custom Actions PRD §8 is closed with a concrete `CustomActionResult` definition.
- [ ] `CustomActionResult` exposes generic factory overloads that route through `IEntityMapper` — unit tests verify the PO payload has correct `ObjectTypeId`, `Attributes`, etc.
- [ ] At least one demo app ships a worked CustomAction using the builder (Fleet "Merge duplicate Cars" or HR "Promote Employee" are natural candidates) with a Playwright test invoking it from the UI.
- [ ] `docs/guide-custom-actions.md` updated — builder-first, mentions the hand-built-PO anti-pattern.

### Estimated size

**Blocked — unknown until §8 of the Custom Actions PRD is closed.** Expect small once unblocked: the factory work is done, this is an ergonomic wrapper + docs pass + one demo exercise.

### Files that will touch (when unblocked)

- `MintPlayer.Spark.Abstractions/Actions/CustomActionResult.cs` (new, ~80 lines)
- `MintPlayer.Spark.Abstractions/Actions/ICustomAction.cs` (return type change)
- `MintPlayer.Spark/Actions/SparkCustomAction.cs` (abstract signature change)
- Endpoint dispatcher (wherever `ExecuteAsync` is awaited — result gets serialized to wire)
- `docs/custom-actions-prd.md` + `docs/guide-custom-actions.md`
- One demo app's `Actions/*CustomAction.cs` + corresponding `customActions.json` + Playwright test

### Tracking

Open a **GitHub issue linked to Custom Actions PRD §8** with this PRD's item as the descendant. Do not open a PR for this item until the blocker lifts.

---

## 2. Rename `NewPersistentObject` → `GetPersistentObject`

### Why deferred (from predecessor PRD §5)

> Renaming `NewPersistentObject` to `GetPersistentObject` à la Vidyano — the
> Spark naming is already established in `prd-manager-retry-action.md`.

Left for last in the original sequence because a mechanical rename breaks every call site — rebasing new work over a rename is cheaper than rebasing a rename over new work. With §1–§3 shipped and §1-here (CustomAction builder) blocked, **now is the quiet window** to do the rename.

### Current state (post-PR #130)

Call-site snapshot (excluding docs/markdown and `.claude/worktrees/`):

| File | Occurrences | Role |
|---|---|---|
| `MintPlayer.Spark.Abstractions/IManager.cs` | 4 | interface declaration |
| `MintPlayer.Spark.Abstractions/PersistentObject.cs` | 1 | xml-doc reference |
| `MintPlayer.Spark/Services/EntityMapper.cs` | 8 | implementation + internal call sites |
| `MintPlayer.Spark/Services/Manager.cs` | 6 | thin forwarders |
| `MintPlayer.Spark/Services/SyncActionHandler.cs` | 2 | schema-branch call |
| `MintPlayer.Spark.SourceGenerators/Generators/PersistentObjectNamesGenerator.IdsProducer.cs` | 1 | xml-doc reference |
| `MintPlayer.Spark.SourceGenerators/Models/PersistentObjectIdInfo.cs` | 1 | xml-doc reference |
| `Demo/Fleet/Fleet/Actions/CarActions.cs` | 1 | **new in PR #130** |
| `Demo/Fleet/Fleet.Library/VirtualObjects/ConfirmDeleteCar.cs` | 1 | **new in PR #130** |
| `MintPlayer.Spark.Tests/EntityMapperFactoryTests.cs` | 22 | test usage |
| `MintPlayer.Spark.Tests/EntityMapperAsDetailTests.cs` | 16 | test usage (includes multi-level test added post-PR #130) |
| `MintPlayer.Spark.Tests/Services/SyncActionHandlerBuildPersistentObjectTests.cs` | 7 | test usage |
| `MintPlayer.Spark.Tests/Services/ManagerTests.cs` | 11 | test usage |

Total: **9 production files + 4 test files + 2 PRDs**. The predecessor PRD said "Demo apps have zero call sites today" — that's no longer true, Fleet picked up 2 during PR #130. Scope grew marginally.

### Design

Straight rename. No semantic change.

- `IManager.NewPersistentObject(string)` → `GetPersistentObject(string)`
- `IManager.NewPersistentObject(Guid)` → `GetPersistentObject(Guid)`
- `IManager.NewPersistentObject<T>()` → `GetPersistentObject<T>()`
- `IEntityMapper.NewPersistentObject(...)` (same three overloads) → `GetPersistentObject(...)`
- All internal callers (Manager thin-forwards, EntityMapper self-calls, SyncActionHandler, Fleet Actions/VirtualObjects) updated in the same commit.
- All test call sites updated in the same commit.
- `docs/PRD-PersistentObjectFactory.md` (parent) and `docs/PRD-PersistentObjectFactory-Followups.md` (predecessor) updated to use the new name.

**`ToPersistentObject(...)` and `PopulateAttributeValues(...)` names stay** — those are not part of the Vidyano rename.

Because this is a preview-mode project (NuGet `10.0.0-preview.*`), **no deprecation path** — just rename and bump the preview version.

### Acceptance criteria

- [ ] `grep -rn "NewPersistentObject" MintPlayer.Spark*/ Demo/ --include='*.cs'` returns **zero hits** post-rename.
- [ ] All unit tests pass (≥330 expected).
- [ ] All E2E tests pass.
- [ ] Demo apps (`DemoApp`, `HR`, `Fleet`, `WebhooksDemo`) still build and run — smoke-test Fleet (the only demo with call sites) by exercising `ConfirmDeleteCar` flow end-to-end.
- [ ] `docs/PRD-PersistentObjectFactory.md` + `docs/PRD-PersistentObjectFactory-Followups.md` updated to use the new name.
- [ ] NuGet preview version bumped.
- [ ] CHANGELOG / preview-version notes mention the breaking rename if such a file exists.

### Estimated size

**Small — ~80 lines of diff** (up from the predecessor PRD's ~50-line estimate, because Fleet now has call sites). Single-commit PR, single reviewer, no design discussion needed.

### Suggested branch name

`refactor/get-persistent-object-rename` (per predecessor PRD's naming convention).

---

## Cross-cutting notes

- **Ordering is load-bearing.** §2 (rename) must land before §1 (builder) — the §1 design examples reference `GetPersistentObject<T>()`, and shipping them in reverse order means a mini-rebase on the builder PR.
- **§1 is genuinely blocked, not "deferred for planning".** If Custom Actions §8 stays open for months, this PRD should be revisited — either by folding §1 into `docs/custom-actions-prd.md` directly (it's really a CustomActions design note, not a factory follow-up), or by closing §1 here and leaving the CustomActions PRD owner to pick it up when they design §8.
- **No live-test scenarios for §2** beyond the existing demo-app smoke tests. Unlike §1–§3 of the predecessor PRD (which each added demo-app + Playwright coverage), a rename doesn't change observable behavior — unit + E2E passing + one demo smoke run is sufficient.
- **PR size discipline** — parent PRD + predecessor PRD phases averaged +300/-100 diffs. §2 here is even smaller. Don't bundle §1 and §2 into one PR even when §1 unblocks — different review scopes, different risk profiles.
- **Test count** — 344 unit tests green on master at time of writing (330 parent + 14 AsDetail from PR #130). §2 must hold or grow this number.

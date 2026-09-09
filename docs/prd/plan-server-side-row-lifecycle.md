# Plan — Server-side lifecycle for New and Delete

**Status: IMPLEMENTED** on `feat/issue-386-server-side-row-lifecycle` · PRD: `PRD-Server-Side-Row-Lifecycle.md`
**Was blocked on:** `PRD-AsDetail-Row-Identity.md` R3 (key round-trips through the client) and R4
(save merges onto the stored row). ✅ **Both shipped in `4f9e9319` (#382)** — R3 is the
`__sparkRowKey` round-trip in `as-detail-conversions.ts`, R4 the stored-row merge in `EntityMapper`.
#384, #391 and #392 have all built on them since. Tracked as issue #386.

## What the salvage cost (estimated 2026-09-09 before starting; outcome noted inline)

The three commits **do** cherry-pick cleanly, but that is the easy 30% of the work:

- Every **code** hunk of `0f013ffe`, `69749631` and `89d7b235` applies to master without conflict.
  The one conflict is a modify/delete on `docs/prd/PRD-AsDetail-Server-Lifecycle.md`, which master
  deleted (superseded by the row-identity PRD) — resolve with `git rm`; it recurs on two of the three.
- Together they are ~577 insertions across 7 code files, and contain **zero tests**. ✅ 20 tests added across three suites, plus 10 client specs.
- **Nothing in ng-spark posts to `/spark/po/{type}/new`.** The endpoint registers and is unreached, so
  the client wiring in `po-create` / `addInlineRow` / `addArrayItem` / `removeArrayItem` is still to
  write, plus the `EntityType` field that exposes the opt-in.
- The delete round-trip (L3) was never written at all. ✅ Built: `SparkDeleteRowArgs<T>`, `OnDeleteRowAsync` as a DIM, `DeleteRowInvoker`, and `POST /spark/po/{type}/delete-row`.
- Realistically **20–30 files** (actual: 24, close enough that the estimate was worth making), and it touches published surface — `MintPlayer.Spark.Abstractions`,
  `MintPlayer.Spark` and `@mintplayer/ng-spark` — so a NuGet *and* npm bump. Since #391 the CI guard
  fails a PR that changes `libs/**` without one.

✅ **Audited, and it is safe.** `EntityMapper` never reads `IsValueChanged`; the write path
writes every attribute it is given. One production caller of `SetValue` exists and dirty-marking is
correct there. Three tests now pin the `SetValue` / `SetOriginalValue` distinction, which had none —
without them the pair was two names for one method and the documented advice ("use `SetOriginalValue`
for defaults") produced exactly the behaviour it exists to avoid.

## Salvage

From the abandoned branch `feat/coverage-account-po-and-branch-deletion`. None of the code these
touch has moved, so they cherry-pick cleanly.

| Commit | Take | Notes |
|---|---|---|
| `0f013ffe` | yes | `SparkNewArgs<T>`, `OnNewAsync` as a DIM, `SetValue`/`SetOriginalValue` |
| `69749631` | yes, except its authorization block | the endpoint and `NewInvoker` |
| `89d7b235` | yes | `ServerSideRowLifecycle` + the row-type authorization that replaced `69749631`'s |
| `488ebd35` | ⚠️ **no** | this is W1 — the rights check whose ten tests passed over a path that carries no key |

## Milestones

### L1 — Re-apply and re-verify
Cherry-pick the three commits. ⚠️ Re-audit `SetValue` gaining `IsValueChanged = true` against the
tree at that time — it feeds the save path and the original commit shipped it with no test asserting
dirty state.

### L2 — Key the row before the hook runs
`New.cs` scaffolds from the model (`entityMapper.GetPersistentObject`), so the CLR entity is never
constructed and the `Guid` initializer never runs — the row comes back keyless. Mint the key and set
`po.Id` at the top of `HandleAsDetailRowAsync`, before `NewInvoker` fires, so a hook can reference the
row it is building.

### L3 — Delete round-trip
The half that was never written. A delete hook, invoked when an opted-in row is removed, able to
refuse. Writes nothing itself.

### L4 — Make the flag real
`ServerSideRowLifecycle` is declared, documented at length and read by nothing. Needs: a model-file
emitter, exposure on the client `EntityType`, and a gate in `New.cs` and the delete path.
⚠️ It gates the **round trip only**. Save-time rights enforcement must never read it — the flag is
unhashed, so a one-line model edit would otherwise switch off a security check.

### L5 — Client wiring
`po-create` calls `/new` before rendering. `addInlineRow` / `addArrayItem` call `/new` when the row
type opts in; the delete path calls the hook. Surface a hook's veto (400) as a readable message.
Touches every AsDetail form.

### L6 — Tests
`NewInvoker` signature resolution; the five refusal paths in `New.cs`; `SetValue` /
`SetOriginalValue` dirty semantics; the flag changing no rights outcome (criterion 5 — this is the
one that keeps it from becoming a security control); and one E2E through a real detail grid.

## Order

L1 → L2 → L3 → L4 → L5 → L6. L2 before L3 because a delete hook that cannot name the row it is
refusing is not useful.

## Traps

1. **The authorization decision flipped between commits.** Keep `89d7b235`'s row-type reading; its
   predecessor's rationale ("nested types are not in security.json") is false —
   `apps/HR/HR/App_Data/security.json:83` grants `QueryReadEditNewDelete/CarreerJob` and
   `GetPermissions.cs:34-40` already serves it.
2. **The endpoint already registers and is simply unreached.** `POST /spark/po/{type}/new` exists at
   runtime via `IPostEndpoint` + `PersistentObjectGroup`; nothing in `ng-spark` posts to it.
3. **Unit tests over a hand-built wire object prove nothing about the path** — the lesson of W1.
   `tests/MintPlayer.Spark.E2E.Tests` currently has smoke and return-url coverage only.

## Outcome, milestone by milestone

| | Built | Notes |
|---|---|---|
| L1 Re-apply | ✅ | Three cherry-picks, one modify/delete conflict each on the superseded PRD; `git rm`. `SetValue` audited — safe, and now tested. |
| L2 Key the row | ✅ | `New.cs` constructs the CLR instance and reflects it over the scaffold, so the key **and** every property initializer arrive. |
| L3 Delete round-trip | ✅ | `SparkDeleteRowArgs<T>`, `OnDeleteRowAsync`, `DeleteRowInvoker`, `POST /{type}/delete-row`. Refusal → 400 with a readable message. |
| L4 Make the flag real | ✅ / ⚠️ changed | Exposed on the client `EntityType` and read by the grid. **No server gate** — see PRD §8 D1 for why the plan's own instruction was not followed. No model-emitter needed: the synchronizer mutates the existing definition in place, so a hand-authored entity-level field survives untouched (same mechanism as `alias`). |
| L5 Client wiring | ✅ | `SparkService.newObject` / `deleteRow`; `addInlineRow`, `addArrayItem`, `removeArrayItem` round-trip when the row type opts in; refusals render against the grid they came from. |
| L6 Tests | ✅ | See below. |

## What the tests actually pin

- **`PersistentObjectAttributeTests`** (3) — `SetValue` marks changed, `SetOriginalValue` does not,
  and `SetOriginalValue` does not *clear* an already-set flag.
- **`RowLifecycleInvokerTests`** (9) — both invokers, including the DIM trap: an actions class that
  merely inherits the interface's default must read as having no hook, or every click pays a
  reflection dispatch to accomplish nothing.
- **`ServerSideRowLifecycleFlagTests`** (3) — the flag moves no hash, with a control asserting that
  `isReadOnly` still does, so the suite cannot pass by having stopped reading the file.
- **`NewRowIsKeyedTests`** (4) — model-only scaffolding is keyless; constructing the entity is what
  mints the key; two new rows do not collide; property initializers become defaults.
- **`spark-po-form-row-lifecycle.spec.ts`** (10) — a type that has *not* opted in issues no request
  at all; a refusal leaves the collection untouched; a row never stored is not asked about.
- **`ServerSideRowLifecycleTests`** (8, E2E) — the assertions no in-memory test can make: the row
  arrives keyed *on the wire*, the hook really read the parent, an invoiced row is refused with a
  readable reason, an unknown key is refused without revealing that it is unknown.

## What the sweep actually cost, and what it bought

One full build + test sweep at the end, as the plan requires. It went red **11 times in the .NET unit
suite, 3 in the client suite and 8 in the E2E** — and every one of those was worth having:

- 2 unit failures were the shared-constructor-cache collision (PRD §9 F1), a bug that predates this
  work and sat on master unfired.
- 5 endpoint failures were the veto arriving as a 500 (F2) plus a fixture that had to answer the
  row-security declaration gate rather than route around it.
- 3 client failures were existing specs that had to `await` mutators which are now async — the
  correct breakage for a genuine signature change.
- 8 E2E failures were, in order: a nested AsDetail row needs `objectTypeId` on the wire; Create
  answers 201 and the test asserted 200; and finally the real one, `[ValueObject]` generating nothing
  in `Fleet.Library` (F3).

The last of those is the argument for the E2E existing at all. Every in-memory test passed against a
row with no key, because a keyless row deserialises with a freshly minted guid — the wire is the only
place the absence is visible.

## ✅ Verified in a real browser (2026-09-09)

The gap this section used to record — "nothing clicks the Add button in a real page" — is closed.
Driven through Playwright against Fleet on `https://localhost:5003`, signed in as an administrator,
on a seeded car (`Cars/rowlifecycle-demo`) holding one ordinary and one invoiced entry.

The whole network trace for the session, which is exactly what the design promises and nothing more:

```
POST /spark/po/{ServiceEntry}/new         → 200
POST /spark/po/{ServiceEntry}/delete-row  → 400   (invoiced row, refused)
POST /spark/po/{ServiceEntry}/delete-row  → 200   (ordinary row, allowed)
PUT  /spark/po/{Car}/Cars%2Frowlifecycle-demo → 200
```

| Criterion | Result |
|---|---|
| 1. New round-trips and the row carries server defaults | ✅ `Description` = "Service — 1-RLC-386" (the hook read `AsDetailParent`'s plate, so the parent really was loaded server-side), `PerformedOn` = today, `Odometer`/`Cost` = 0 from the C# initializers |
| 2. A hook's veto is readable, not a discarded save | ✅ 400, row stays, message renders against its own grid |
| 3. Removing an opted-in row invokes the hook; a refusal leaves the collection unchanged | ✅ both halves; the stale refusal cleared on the next attempt |
| 4. A type **not** opted in behaves exactly as today | ✅ flag flipped to `false`, app restarted: Add opened a blank modal and issued **zero** requests |
| 5. Toggling the flag changes no rights outcome | ✅ and stronger — see below |
| 6. At least one E2E through a real detail grid | ✅ this, plus the 8 host-level facts |

**The key round-tripped, which is the entire point of the feature.** `/new` returned
`id: d5b5bb9c9e8f4a76b0c2e366b5402f06`; after saving, the stored document holds exactly that key, and
the untouched invoiced row kept its *original* key — so the save matched against the stored
collection rather than rebuilding it. Two rows, two distinct non-empty keys, the new one persisted
once rather than duplicated.

**Every attribute arrived `isValueChanged: false`.** `SetOriginalValue` does what its doc claims:
adding a row and abandoning it leaves no phantom unsaved-changes state. Until this run that was
asserted only by tests written alongside the code.

### Two facts settled by measurement rather than argument

1. **Adding a keyed embedded collection to an app with existing documents needs no backfill
   migration.** The startup gate runs `from 'Cars' where ServiceEntries[].Id == null` against 10,009
   existing cars, none of which has the field. Measured directly against RavenDB: `TotalResults: 0`
   — an **absent array does not match**, unlike an absent scalar, which does. Fleet then logged
   *"row keys verified across 1 embedded collection(s)"*, so the collection is genuinely discovered
   and checked rather than skipped.
2. **The flag is outside the model hash, in the running application.** Flipping
   `serverSideRowLifecycle` and restarting produced the identical hash `f239fdd2ac5d…` and started
   clean — no re-synchronize, no `SparkModelOutOfSyncException`. That is criterion 5's property
   demonstrated on a live host, not only in `ServerSideRowLifecycleFlagTests`.

## Still not covered

- **The inline (`editMode: "inline"`) branch was never exercised in a browser.** Fleet's
  `ServiceEntries` has no `editMode`, so it renders as the modal-array table and the run above went
  through `addArrayItem`/`removeArrayItem`. `addInlineRow` is covered by the client specs only.
- ~~**`spark-po-create` was not exercised.**~~ ✅ Exercised, and it was **broken** — see PRD §9 F4.
  Now verified: the create page issues `POST /new` with `parentType` as the type id and no
  `parentId`, and the modal opens with the server's date default and an empty description, since the
  hook finds no parent to read a plate from and guards on exactly that.
- **`ServiceEntryActions.OnNewAsync` reading `AsDetailParent` is only covered for a saved parent**
  on the server side.
- **A non-admin caller was not driven through the UI.** Fleet grants fleet managers `ReadEdit` but
  not `New`/`Delete` on `ServiceEntry`, so the Add and delete buttons should not render for them at
  all — asserted at the endpoint level, not the button level.

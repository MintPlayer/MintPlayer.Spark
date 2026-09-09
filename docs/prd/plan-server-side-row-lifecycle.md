# Plan — Server-side lifecycle for New and Delete

**Status: NOT STARTED — the blocker is lifted.** · PRD: `PRD-Server-Side-Row-Lifecycle.md`
**Was blocked on:** `PRD-AsDetail-Row-Identity.md` R3 (key round-trips through the client) and R4
(save merges onto the stored row). ✅ **Both shipped in `4f9e9319` (#382)** — R3 is the
`__sparkRowKey` round-trip in `as-detail-conversions.ts`, R4 the stored-row merge in `EntityMapper`.
#384, #391 and #392 have all built on them since. Tracked as issue #386.

## ⚠️ What the salvage actually costs (measured 2026-09-09, before starting)

The three commits **do** cherry-pick cleanly, but that is the easy 30% of the work:

- Every **code** hunk of `0f013ffe`, `69749631` and `89d7b235` applies to master without conflict.
  The one conflict is a modify/delete on `docs/prd/PRD-AsDetail-Server-Lifecycle.md`, which master
  deleted (superseded by the row-identity PRD) — resolve with `git rm`; it recurs on two of the three.
- Together they are ~577 insertions across 7 code files, and contain **zero tests**.
- **Nothing in ng-spark posts to `/spark/po/{type}/new`.** The endpoint registers and is unreached, so
  the client wiring in `po-create` / `addInlineRow` / `addArrayItem` / `removeArrayItem` is still to
  write, plus the `EntityType` field that exposes the opt-in.
- The delete round-trip (L3) was never written at all.
- Realistically **20–30 files**, and it touches published surface — `MintPlayer.Spark.Abstractions`,
  `MintPlayer.Spark` and `@mintplayer/ng-spark` — so a NuGet *and* npm bump. Since #391 the CI guard
  fails a PR that changes `libs/**` without one.

⚠️ **Audit rather than trust:** `0f013ffe` changes `PersistentObject.SetValue` to mark
`IsValueChanged`. That feeds the save path and shipped with no test asserting dirty state — it is the
same shape as the defects #384 and #391 were about, so it wants a test before it is relied on.

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

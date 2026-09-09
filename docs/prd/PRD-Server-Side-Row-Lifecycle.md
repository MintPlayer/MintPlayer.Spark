# PRD — Server-side lifecycle for New and Delete

**Status: NOT STARTED.** Blocked on `PRD-AsDetail-Row-Identity.md` landing.
**Origin:** sidestepped from PR #381; commits `0f013ffe`, `69749631`, `89d7b235` on the abandoned
branch `feat/coverage-account-po-and-branch-deletion`.

---

## 1. Problem

**Spark has no server-side construction hook.** Not for a root persistent object, not for an AsDetail
row. The hook surface in `DefaultPersistentObjectActions` is `OnLoadAsync`, `OnSaveAsync`,
`OnDeleteAsync`, `IsAllowedAsync`, `OnQueryAsync`, `GetRowFilterAsync`, `GetProtectedAttributesAsync`,
`OnBeforeSaveAsync`, `OnAfterSaveAsync`, `OnBeforeDeleteAsync`, `OnRefreshAsync` — eleven hooks, and
none of them fires when a user clicks **New**.

Clicking New is purely a client-side act today:

- root: `spark-po-create.component.ts:60 initFormData()` builds a blank form from entity-type
  metadata (`''`, `false`, `[]`, `null`); no HTTP call happens before the user types;
- AsDetail row: `spark-po-form.component.ts:691 addInlineRow` pushes `{}`, `:700 addArrayItem` opens
  a modal on `{}`, `:715 removeArrayItem` splices by index.

So there is nowhere to put a server-computed default, a parent-derived value, or a veto — and the
`New/{Type}` right is never consulted for a click the server never sees. (It *is* consulted at save
time once `PRD-AsDetail-Row-Identity.md` R5 lands; this PRD is about the click.)

## 2. Why it is blocked, not merely unscheduled

The New half is mechanically independent — it constructs a persistent object and writes nothing. The
**Delete** half is not, and the two ship together or the feature is half a feature.

A delete round-trip has to answer "which row was removed", and until row identity lands that is not a
computable question: the client sends an array, the server replaces it wholesale, and no row has a
stable key to be absent. Building the hook first produces the same defect that
`PRD-AsDetail-Row-Identity.md` §3 W1 dissects — a mechanism whose unit tests pass because they
construct the wire object by hand, over a path that never carries a key.

**Row identity enables this feature.** Do not start it first.

## 3. What already exists, and what it is worth

The three abandoned commits are unusually complete on the server and untouched on the client. None of
their code has moved since, so re-applying is close to trivial.

| Commit | Contents | State |
|---|---|---|
| `0f013ffe` | `SparkNewArgs<T>` (sealed, get-only, internal ctor); `OnNewAsync` as a default interface method so no hand-written implementer breaks; `PersistentObject.SetValue` marks `IsValueChanged`, new `SetOriginalValue` does not | reads finished; no tests |
| `69749631` | `POST /spark/po/{objectTypeId}/new` (206 lines): standalone and AsDetail-row paths, every failure collapsed to one refusal so none is an existence oracle, `SparkValidationException` → 400 so a hook may veto; `NewInvoker` mirroring `RefreshInvoker`'s exact-signature resolution | reads finished; no tests |
| `89d7b235` | `EntityTypeDefinition.ServerSideRowLifecycle`; reversed the authorization decision | flag inert — declared, documented, **read by nothing** |

⚠️ **The authorization decision flipped between the two commits, and the later one is right.**
`69749631` used the *parent's* right (`New/Person` to add a phone number), reasoning that nested types
are absent from `security.json`. That reasoning is false — `apps/HR/HR/App_Data/security.json:83`
grants `QueryReadEditNewDelete/CarreerJob`, and `GetPermissions.cs:34-40` already serves it.
`89d7b235` reversed to the row type's own right, which matches both the button the client renders and
`PRD-AsDetail-Row-Identity.md` R5. Keep the reversal; discard the original rationale.

⚠️ **Do not resurrect `488ebd35`** from the same branch. That is the `EnforceRowRightsAsync` the
row-identity PRD dissects as W1. These three commits are unaffected by it.

## 4. Design

### L1 — Both halves, or neither

New round-trips to `OnNewAsync`; Delete round-trips to a delete hook. Shipping New alone leaves the
asymmetry that motivated the work.

### L2 — Opt in per row type, and the flag is never a security control

`ServerSideRowLifecycle` on the row type's own model file decides whether the client round-trips.
⚠️ Its own doc comment already states the rule and it must survive implementation: **save-time
enforcement must never read this flag.** It is an unhashed model field; gating a rights check on it
would let a one-line model edit switch that check off. R5's enforcement stays unconditional.

### L3 — The row arrives keyed

⚠️ Today `/new` returns a row with **no key at all**: `New.cs` scaffolds through
`entityMapper.GetPersistentObject(entityType.Id)`, which builds a blank PO from the *model* and never
constructs the CLR entity — so the `Guid.NewGuid()` field initializer (row-identity R2) never runs.

That happens to be consistent with the save-path merge (an unmatched row correctly takes the
`Activator.CreateInstance` branch), but only by accident, and it means `OnNewAsync` cannot see or set
the identity of the row it is building. Mint the key and set `po.Id` at the top of
`HandleAsDetailRowAsync`, before the hook runs, so a hook can reference the row — for an audit entry,
a cross-row default, or a parent-scoped `(root id, child key)` pair.

### L4 — Authorization is the row type's own right

`New/{RowType}`, with the parent loaded as a **separate** gate: the row type's right says the caller
may create rows of this kind, not which parent they may attach one to.

### L5 — Non-goals

- Re-litigating where per-row rights are enforced on save. That is row-identity R5 and it is settled.
- A general client-side "server round trip on every field change". `OnRefreshAsync` already exists.

## 5. What is missing

- **Zero tests** across all three commits.
- **No client wiring at all.** `po-create` still builds the form locally; `addInlineRow` /
  `addArrayItem` still push `{}`. Nothing in `ng-spark` posts to `/new` — the endpoint registers
  through `IPostEndpoint` + `PersistentObjectGroup`, so it exists at runtime and is simply unreached.
- **No Delete counterpart**, though `ServerSideRowLifecycle`'s doc comment promises one.
- **No model-file emitter** for the flag and no exposure of it on the client `EntityType`.
- ⚠️ **`SetValue` becoming dirty-marking is an unaudited behaviour change** feeding the save path.
  The commit claims only two callers, both tests, neither asserting dirty state — re-verify against
  the tree at the time rather than re-applying blind.

## 6. Acceptance criteria

1. Clicking New on an opted-in AsDetail row type issues one request and the returned row carries
   server-set defaults.
2. A hook throwing `SparkValidationException` surfaces as a 400 the user can read, not a discarded
   save.
3. Removing a row on an opted-in type invokes the delete hook, and a hook that refuses leaves the
   collection unchanged.
4. A type **not** opted in behaves exactly as today — no request.
5. ⚠️ Toggling `ServerSideRowLifecycle` changes no rights outcome whatsoever. Test it explicitly:
   the flag is unhashed, so this is the property that stops it becoming a security control.
6. At least one E2E through a real detail grid. `tests/MintPlayer.Spark.E2E.Tests` has only smoke and
   return-url coverage today, and per the row-identity PRD's own criteria a green unit suite over a
   hand-built wire object proves nothing about the path.

## 7. Sizing

One PR, medium. The server half is written; the work is the half never started.

| | |
|---|---|
| Re-apply the three commits (no touched code has moved) | trivial |
| Mint and propagate the row key into `/new`; expose on `SparkNewArgs` if hooks need it | small |
| Delete round-trip + make the flag actually gate it + model emitter + client `EntityType` exposure | medium |
| Client wiring, error and veto surfacing | medium — touches every AsDetail form |
| Tests, including the E2E | medium |

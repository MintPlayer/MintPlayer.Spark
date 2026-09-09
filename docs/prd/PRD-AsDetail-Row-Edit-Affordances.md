# PRD — `Edit/{RowType}` affordances on AsDetail rows

**Status: NOT STARTED.** Should follow `PRD-AsDetail-Row-Identity.md` R5, which creates the problem
this solves.
**Origin:** sidestepped from PR #381, investigating security.json-driven button rendering.

---

## 1. Problem

**`New` and `Delete` are already gated in the client; `Edit` is gated nowhere.**

The `[+ Add]` and trash affordances on an AsDetail array are wrapped in capability pipes
(`spark-po-form.component.html:258` and `:313` for create, `:222` and `:297` for delete), fed by
`GET /spark/permissions/{entityTypeId}` for the **child** type
(`spark-po-form.component.ts:275-293`). So `QueryReadEditNewDelete/CarreerJob` in HR's
`security.json` really is read for two of its three verbs.

`canEdit` is computed by `GetPermissions.cs:37`, serialised in `entity-permissions.ts`, and fetched
by the same call — **and consumed by nothing.** Neither the modal branch's per-row edit pencil
(`spark-po-form.component.html:294`) nor any of the inline cell inputs (`:114-195`) consults it.

Today that is merely untidy: the save path ignores the child type's rights entirely, so an ungated
input is honest about what will happen.

⚠️ **Row-identity R5 turns it into silent data loss.** R5 enforces `Edit/{RowType}` at save, and
chooses to **restore the stored row** rather than refuse — deliberately, following
`ShieldProtectedAttributesAsync`, so that a save touching other things still succeeds. Combined with
ungated inputs, a user lacking `Edit/CarreerJob` will:

1. see editable-looking inputs,
2. type into them,
3. save,
4. get a **success message**,
5. and find their edits gone.

That is worse than a refusal, and it is worse than the status quo. A restore that nobody can see is
indistinguishable from a bug.

## 2. Design

### E1 — Disable, do not hide

An input the user may not change is shown read-only, not removed. The row still needs to be readable,
and a vanishing field reads as missing data. This differs from `New`/`Delete`, where hiding the
affordance is right because there is nothing to read.

### E2 — Source the fact that already exists

`canEdit` is on the wire and already fetched. No server change, no wire-model change. A
`canEditDetailRow` pipe mirroring the two existing ones, applied to the pencil at
`spark-po-form.component.html:294` and to the inline cell inputs at `:114-195`.

### E3 — ⚠️ Make a restore visible, wherever it happens

Even fully gated, a restore can still occur: rights can change between page load and save, and a
caller can post directly. R5 must not silently discard content.

Options, to be settled during implementation:
- return the restored rows in the save response and let the form show what reverted;
- or surface a notification naming the affected row type.

Doing nothing is not an option — that is the failure this PRD exists to remove, and gating the inputs
only makes it rarer.

### E4 — Non-goals

- Per-**row** (as opposed to per-type) edit rights. `security.json` grants are type-level, and
  `PersistentObject.Can` (`PersistentObject.cs:47`) is parent-row only, with no embedded equivalent.
  A per-row story needs a different mechanism and is not this.
- Changing R5's restore-rather-than-refuse semantics. That choice is right; it just has to be
  visible.

## 3. Acceptance criteria

1. A user without `Edit/{RowType}` sees the row's inputs read-only, and the per-row edit pencil is
   absent or disabled.
2. The same user can still **read** every visible field of the row.
3. A user with the right sees no change from today.
4. ⚠️ A save that restores a row tells the user something — verified by forcing a restore
   server-side, since the gated UI makes the natural path unreachable.
5. `canEdit` is read from the child type's permissions, not the parent's — verified with a user who
   holds `Edit/Person` but not `Edit/CarreerJob`.

## 4. Sizing

Small-to-medium, client only for E1/E2 (~10 input bindings in the inline cell template plus one
pipe). E3 may need a server-side response change and is the larger unknown.

## 5. Related

`PRD-AsDetail-Row-Identity.md` R5 (the enforcement), and the two ~15-line client fixes folded into
that work: the fail-open default in `can-create-detail-row.pipe.ts:8` and the `Query`-gated
catalogue coupling that hides a child type's permissions from a user who holds `New` but not `Query`.

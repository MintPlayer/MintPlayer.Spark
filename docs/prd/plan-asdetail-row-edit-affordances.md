# Plan — `Edit/{RowType}` affordances on AsDetail rows

**Status: NOT STARTED** · PRD: `PRD-AsDetail-Row-Edit-Affordances.md`
**Should follow** `PRD-AsDetail-Row-Identity.md` R5, which is what turns this from untidiness into
silent data loss.

## Milestones

### E1 — `canEditDetailRow` pipe
Mirror `can-create-detail-row.pipe.ts` / `can-delete-detail-row.pipe.ts`. ⚠️ Fail **closed** — the
existing two return `true` when no entry is present (`can-create-detail-row.pipe.ts:8`, asserted as
intended in `pure-pipes.spec.ts:322-324`), and the row-identity work is already fixing that. Do not
copy the old default.

No server or wire-model change: `canEdit` is computed at `GetPermissions.cs:37`, serialised in
`entity-permissions.ts`, and already fetched by `spark-po-form.component.ts:275-293`. It is simply
unused.

### E2 — Apply it
The per-row edit pencil on the modal branch (`spark-po-form.component.html:294`), and the inline cell
inputs (`:114-195`, ~10 bindings). **Read-only, not hidden** — the row still has to be readable.

### E3 — Make a restore visible
The larger unknown, and the reason this PRD exists. R5 restores a row rather than refusing the save,
so without this a user sees a success message over discarded edits. Gating the inputs makes that
rarer, never impossible: rights can change between load and save, and a caller can post directly.

Either return the restored rows in the save response, or notify naming the row type. Settle during
implementation; doing nothing is not an option.

## Order

E1 → E2 → E3. E3 could go first — it is the correctness half — but it needs a response-shape decision
that E1/E2 do not.

## Traps

1. **`canEdit` is already on the wire and already fetched.** Do not add a server endpoint or extend
   the wire model; the fact exists and is dropped on the floor.
2. **Read the CHILD type's permission, not the parent's.** The existing pipes take
   `asDetailPermissions()` keyed by attribute name; follow that, and test with a user holding
   `Edit/Person` but not `Edit/CarreerJob`.
3. **Do not change R5's restore-rather-than-refuse semantics.** They are right — a save touching
   other things should still succeed. The defect is that the restore is invisible.
4. **`isReadOnly` on an attribute is model-driven and parent-scoped** (`spark-po-form.component.ts:154-159`
   filters `editableAttributes`, `refresh-overlay.ts:26` lets a hook flip it per request). There is
   no per-row path into it today, so this is a new mechanism rather than a reuse.

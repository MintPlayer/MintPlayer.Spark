# PRD — Issue #274: Synchronize must preserve a hand-edited `showedOn`

**Status:** Planned
**Issue:** [#274](https://github.com/MintPlayer/MintPlayer.Spark/issues/274)
**Branch:** `fix/issue-274-preserve-showedon`
**Plan:** [issue_274_plan.md](issue_274_plan.md)

## Problem

For an entity with a `[FromIndex]` projection type, `--spark-synchronize-model` rewrites every
projected attribute's `showedOn` on **every** run. A hand-edited trim
(`"showedOn": "PersistentObject"` to keep a load-bearing but presentationally-wrong field off the
generic grid) is silently reverted to the derived value the next time anyone synchronizes — for any
reason. Measured in MintPlayer/CodeCoverage on `10.0.0-preview.53`: re-running synchronize after
hand-editing `Repository.json`/`Build.json` reverted every edit byte-for-byte.

Nothing reports the loss: `showedOn` is deliberately excluded from the structural hash
(`ModelFileShape.StructuralAttributeFields`, `ModelFileShape.cs:114-119`), so `--spark-verify-model`
never trips — the wipe is invisible to CI.

## Investigation findings (three-agent sweep, 2026-08-19)

### F1 — One offender, one line

A field-by-field audit of every property on `EntityTypeDefinition`, `EntityAttributeDefinition`,
`SparkQuery`, `SortColumn`, `ValidationRule`, `AttributeTab` and `AttributeGroup` against the
synchronizer's update path found **exactly one** presentation field overwritten on re-run:

```csharp
// ModelSynchronizer.cs:585-592 — EXISTING-attribute branch
if (projectionType != null)
{
    existingAttr.InCollectionType = inCollectionType ? null : false;
    existingAttr.InQueryType = inQueryType ? null : false;
    // Update ShowedOn based on type availability
    existingAttr.ShowedOn = showedOn;          // <-- the #274 bug
}
```

The derived value (`ModelSynchronizer.cs:538-554`) is `Query | PersistentObject` for a property
present on both the entity and the projection, narrowed to one side for single-sided properties.
Every other presentation field (`Label`, `IsVisible`, `IsReadOnly`, `Order`, `EditMode`,
`ReferenceDisplayType`, `Group`, `ColumnSpan`, `Renderer`, `RendererOptions`, `Rules`) is either
create-only or conditionally merged, per the #253 principle.

### F2 — Only `[FromIndex]`-projected entities are affected

The overwrite is guarded by `projectionType != null`; the `else` branch (`:594-597`) never touches
`ShowedOn`. Plain entities already behave correctly. The bug only *visibly* bites dual-present
properties (on both entity and projection): single-sided properties re-derive the same narrowed
value an author would want, which is why the wipe went unnoticed until CodeCoverage hand-trimmed a
dual-present column set.

### F3 — The framework already classifies `showedOn` as presentational

- `SparkModelShape` does not hash it (class doc explicitly scopes human-authored fields out).
- `ModelFileShape.StructuralAttributeFields` excludes it, so a hand edit does not require a re-sync
  and does not trip `--spark-verify-model`.
- `libs/spark/MintPlayer.Spark/README.md:365-368` documents `showedOn` as a hand-editable model
  field ("Change visibility (`isVisible`, `showedOn`)") — the README already promises the behavior
  this issue asks for.
- `docs/model_sync_lifecycle_PRD.md:369-374` requires a projection-only attribute's
  `showedOn: Query` to be carried over verbatim when it becomes an orphan — the orphan carry-over
  path (`:651-659`) honors that; the live update path does not. The same field is treated two
  different ways.
- The structural truth `showedOn` is derived from is already persisted separately as
  `inCollectionType`/`inQueryType` (`:588-589`), which remain legitimately re-derived. `showedOn`
  is a redundant, derived projection of two structural fields; preserving it costs no hash drift.

### F4 — The in-file precedents for the fix

- `IsReadOnly` (`:615-618`): set only on create; the update branch never reassigns it, so a
  hand-set value survives — the exact #253 shape.
- `DataType` (`:558-566`): guarded re-derive — the `MultiLineString` override beats the derived
  `string`.
- The unconditional overwrites at `:574-583` (`ReferenceType`, `Query`, `IsArray`, `IsSortable`, …)
  are documented as deliberate *because they are structural and feed the hash*. That justification
  does not extend to `showedOn`, which feeds no hash.

### F5 — No test pins the current behavior

Grep for `ShowedOn` across `tests/` returns nothing (outside source-generator snapshots). The
overwrite can be changed without breaking a single existing assertion — and conversely, nothing
would have caught this regression.

### F6 — Serialization caveat

`EntityAttributeDefinition.ShowedOn` is a **non-nullable** `EShowedOn` defaulting to
`Query | PersistentObject`, and the loader is plain STJ — an *absent* `showedOn` in JSON is
indistinguishable after load from an explicit "both". This is fine for the chosen design (D1):
absent loads as "both", intersection with capability reproduces today's narrowing exactly.

## Requirements

- **R1** — A hand-edited `showedOn` on a `[FromIndex]`-projected entity survives
  `--spark-synchronize-model`, run any number of times (fixed point).
- **R2** — The derived default is still established when an attribute is **first created**
  (create path `:629`, unchanged).
- **R3** — Structural narrowing still self-applies: an attribute that leaves the projection loses
  the `Query` flag; an attribute not on the entity (projection-only) does not carry
  `PersistentObject`. Synchronize may **remove** sides the attribute can no longer appear on; it
  must never **add** one back.
- **R4** — Adding `[FromIndex]`/`[GenerateIndex]` to an existing entity produces the same first-sync
  narrowing as today (collection-only attributes narrow to `PersistentObject`, projection-only
  attributes are created with `Query`). Only *re-runs over an authored value* change behavior.
- **R5** — A `showedOn` left with no valid side (intersection empty — e.g. a hand-set `Query` on an
  attribute that left the projection) self-heals to the derived capability rather than becoming
  permanently invisible.
- **R6** — Plain (non-projected) entities keep today's behavior: `showedOn` untouched on update.
- **R7** — Zero diff when re-synchronizing the committed demo models: the fix must not move any
  generated JSON or `.spark-model-hash` file.

## Design

### D1 — Intersect, never widen (chosen)

```csharp
// showedOn (derived at :541-554) is the structural capability set
var narrowed = existingAttr.ShowedOn & showedOn;
existingAttr.ShowedOn = narrowed != 0 ? narrowed : showedOn;
```

`showedOn` is presentation constrained by structure: membership in the projection/entity is the
*capability* to appear on a side; the author chooses a subset. Intersection preserves every trim,
still strips sides that structurally disappeared (R3), reproduces today's first-sync narrowing on
freshly-projected entities because their stored value is "both" (R4), and self-heals an empty
result (R5).

**Design-twice alternatives, rejected:**

- *Delete the line* (pure set-on-create, the `IsReadOnly` shape): simplest, but loses the
  structural self-narrowing — an attribute later excluded from the projection via
  `[IgnoreForIndex]` would keep advertising `Query` and render as a permanently-empty grid column.
  `showedOn` differs from `IsReadOnly` precisely in having a structural component.
- *Nullable `EShowedOn?` where `null` means "derive"*: cleanly distinguishes authored from derived,
  but changes the JSON contract for every existing model file, touches the wire converter and the
  Angular client, and buys nothing over D1 — every existing model JSON already writes `showedOn`
  explicitly.

### D2 — Scope: `showedOn` only

The audit (F1) cleared every other field. Two adjacent findings are deliberately **out of scope**
and filed as follow-ups (see below) to keep the diff minimal.

## Out of scope / follow-ups

- **`existingAttr.Query` wipe** (`:575`): a hand-set `query` on a non-`[Reference]` attribute with a
  matching CLR property is nulled on every run — same defect class, lower severity (orphaned
  attributes take the carry-over path and are safe). Follow-up issue to file with this PR.
- **SparkQuery staleness** (the inverse problem): `Source`/`IndexName`/`UseProjection` are never
  refreshed after creation, so renaming a `SparkContext` property leaves a dangling
  `"source": "Database.OldName"` and mints a duplicate query. Follow-up issue to file with this PR.
- **Per-query column model** (`SparkQuery.Columns` / a `columns` input on `spark-sub-query`):
  named in #274 as "related but separate, can file on request" — not filed; the issue author owns
  that call.

## Acceptance criteria

1. Hand-trim `"showedOn"` on a dual-present attribute of a projected entity, run synchronize twice
   → the trim survives both runs, byte-for-byte.
2. All existing `ModelSynchronizer`, idempotency and correctness suites pass unchanged.
3. Re-running `--spark-synchronize-model` on all four demo apps produces zero git diff.
4. New tests fail on `master` (red before the fix), pass on the branch.

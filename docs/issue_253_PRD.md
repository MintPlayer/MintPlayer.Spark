# PRD — Issue #253 part 2: synchronize must not delete model attributes

**Issue:** [#253](https://github.com/MintPlayer/MintPlayer.Spark/issues/253) ·
**Branch:** `feat/issue-253-preserve-model-attributes` · **Targets:** `10.0.0-preview.50`

Part 1 of #253 (`[IgnoreProperty]`) shipped separately as #254 / PR #255. This covers everything else
the issue proposed.

> Line references below are **as of the code before this change** — they locate the findings, and the
> edits have since shifted them (e.g. the wholesale assignment at `:481` now sits at `:530`). Search by
> symbol rather than by line.

## Background

`ModelSynchronizer` regenerates `App_Data/Model/<Type>.json` from CLR reflection. It does not delete
attributes explicitly — it rebuilds the array from the current property set and assigns it wholesale
(`ModelSynchronizer.cs:481`), so anything without a matching property is dropped by omission.

The convention it violates is its own: the update branch (`:401-450`) deliberately never reassigns
`Label`, `IsRequired`, `IsVisible`, `IsReadOnly`, `EditMode`, `ReferenceDisplayType`, `Group`,
`Renderer`, `RendererOptions`, `Rules`, `ColumnSpan` or `Id` — hand edits to those survive because the
existing object is carried over by reference (`:449`). So field-level hand edits are preserved while
whole hand-authored attributes vanish.

Cost: renaming or removing a C# property silently discards that attribute's label translations,
`renderer`, `rendererOptions`, `group`, `editMode`, `rules` **and its stable `Id`**. A rename becomes
delete-and-recreate-with-defaults, unlogged, surfacing later as a missing column or unstyled cell.

## F1 — `[IgnoreProperty]` removal is a different code path and must stay destructive

Two existing tests assert removal: `Re_synchronize_removes_an_attribute_that_has_become_ignored`
(`ModelSynchronizerTests.cs:431`) and `Ignoring_a_property_on_the_entity_vetoes_the_same_name_on_the_projection`
(`:482`). Both are `[IgnoreProperty]`-driven, and `IgnorePropertyAttribute`'s own doc comment
(`:20-23`) documents that removal as intentional.

**These must keep passing unchanged.** Marking a property ignored is an explicit instruction to drop
its attribute; a property silently disappearing is not. Conflating them is the easy way to get this
wrong.

Notably **no test exists** for the actual target scenario — a property removed from the class with no
`[IgnoreProperty]` involved. The behaviour we are fixing was never pinned in either direction.

## F2 — no marker exists to distinguish intentional from obsolete

`EntityAttributeDefinition` (`EntityTypeDefinition.cs:53-140`) has 24 fields and **none of them says
"this attribute has no CLR property on purpose."**

This is decisive for prune (D1). A deliberately virtual attribute and an obsolete leftover are
byte-identical in the model JSON.

## F3 — Vidyano precedent: a positive marker, compiler-enforced; and no prune at all

Surveyed ~14 DeCronosGroep repos:

- Truly property-less attributes on a typed entity: **2 sites**, both in Insurance
  (`InsurancePolicyActions.cs:107-109`, `InsurancePolicySupplierActions.cs:26`), both **transient and
  request-scoped** via `PersistentObjectExtensions.AddAttribute` — never part of a persisted model
  definition.
- The common "computed value" case is **`[Calculated(nameof(Source))]`** — 176 hits / 61 files — and it
  always backs a **real declared CLR property**. It is enforced by a Roslyn analyzer
  (`MissingCalculatedMethodAnalyzer`) that fails the build if a `[Calculated]` property has no matching
  `CalculatedMethods.Add` registration.
- **Vidyano ships no prune equivalent**, and does not need one: it builds the model from live
  reflection on every request, so there is no persisted artifact that can drift.

**Verified against the persisted models, not just the code:** the Insurance attribute does not appear
in `App_Data/Model/CronosInsurance/InsurancePolicy.json` at all — it is injected into the in-memory PO
per request and never written down. A broadened sweep (16 repos grepped, 8 read in depth) found the
`AddAttribute` pattern in only 3 repos, and everywhere except Insurance it targets the framework's
inherently dynamic `UserSettings` PO, which has no CLR class by design. No marker field
(`IsCalculated`/`IsVirtual`/`Persisted`/…) exists in any Vidyano attribute JSON either; the only
`IsSystem` hit marks builtin actions in the top-level `model.json`.

**So Vidyano is not a precedent in either direction, and this PRD does not claim it as one.** It has no
statically-declared property-less attribute *and* no marker *and* no prune. That is architecture, not
endorsement: virtual data there either never touches a persisted model (transient PO mutation, which
Vidyano can afford because it re-reflects every request) or rides on a real CLR property marked
`[Calculated]` — which Spark's reflection would already preserve, since the property exists.

The transferable lesson is narrow but real: where this codebase family expresses "a value without an
obvious backing", it uses a **positive, mechanically-enforced marker** and never infers intent from
absence.

## F4 — the motivating use case does not work today

The `CustomerActions.OnLoad` → `TotalPurchaseBudget` example **cannot be written against the current
API.** `IPersistentObjectActions<T>.OnLoadAsync` returns `Task<T?>` — the entity, not the
PersistentObject — and `DatabaseAccess.GetPersistentObjectAsync:113` calls
`entityMapper.ToPersistentObject` itself with no callback afterwards. Same on the list path
(`:180`). There is no post-map hook on either.

The plumbing *tolerates* a virtual attribute in both directions — `PopulateAttributeValues` silently
skips an attribute with no property (`EntityMapper.cs:222-224`) and `IsWritableBySchema` refuses to
write one (`:489-490`, `:513-514`) — so preserving one is safe and produces an always-null attribute,
not a crash. `PersistentObject` even has the needed string indexer (`PersistentObject.cs:64`, used at
`Demo/Fleet/Fleet/Actions/CarActions.cs:99`), but only on synthetic popup POs.

**What this PR can do about it:** M2 (get-only properties) covers the entity-computable half directly —
`public decimal TotalPurchaseBudget => Orders.Sum(o => o.Total);` becomes a read-only model attribute
automatically, which is today impossible because the filter requires `CanWrite`. Values needing a
separate query still need a hook. See "Out of scope".

## F5 — the shared property filter has a call site that cannot accept get-only properties

`IsSparkModelProperty` (`ReflectedTypeExtensions.cs:80-87`) currently requires `CanRead && CanWrite`.
Five call sites share it, and its doc comment (`:60-67`) explicitly says the answer "cannot drift
between call sites". One of them structurally contradicts admitting get-only properties:

**`SyncActionInterceptor.GetPropertyNames` (`:197-204`, replication) computes the write-authorization
list** — "the only properties that should be synced back to the owner". A get-only property entering
that list is a property the replication path believes it may write and cannot.

Also affected:
- `ModelSynchronizer.cs:194` (`CollectEmbeddedTypes`) — a get-only *complex* property would newly
  generate its own embedded `{Type}.json`.
- `SyncActionHandler.cs:152` (`BuildFromClrReflection`) — outbound-only, so read-only properties are
  benign there.

**Conclusion: the filter must split.** One predicate answers "is this part of the model shape"
(admits get-only), a separate one answers "may this be written" (requires `CanWrite`). Widening the
single shared filter would silently expand replication's write authorization — a security-adjacent
change disguised as a model-shape change.

## F6 — indexers are not excluded

`GetCachedProperties` uses `GetProperties(Public | Instance)`, which includes indexers. A public
read/write `this[...]` would pass the filter and surface as an attribute named `"Item"`. Latent today
(no Spark entity declares one) and worth closing while we are in this filter.

## Requirements

- **R1** An attribute whose CLR property no longer exists is **preserved**, with every field intact —
  `Id` above all, since it is the stable identity clients key on.
- **R2** Each preserved orphan is logged once, actionably.
- **R3** `[IgnoreProperty]`-driven removal stays destructive and unchanged (F1).
- **R4** For attributes that still have a property, behaviour is **byte-identical** to today.
- **R5** Get-only computed properties become model attributes with `IsReadOnly = true`.
- **R6** Admitting get-only properties must not widen replication's write-authorization list (F5).
- **R7** Indexers are excluded from the model.
- **R8** Synchronize only ever adds or modifies. Nothing else deletes.

## Decisions

- **D1 — `--prune-orphaned-attributes` is NOT implemented.** Deliberate, and the main judgement call
  here.

  Without a marker (F2) the flag has no information to act on: it would ask the operator to assert "I
  have no intentionally virtual attributes", which is unverifiable, and when wrong it destroys exactly
  the attributes this PR exists to protect. Adding a marker now does not rescue it either — every
  attribute authored *before* the marker existed would lack it and be pruned on first run, reproducing
  the bug for the whole existing population.

  **The obvious counter-argument, addressed:** the survey found no persisted property-less attribute
  anywhere in Vidyano, so one could conclude prune would break nothing in practice. That inference does
  not transfer, because the absence is architectural. Vidyano re-reflects the model every request, so
  it has a *transient* place to put virtual values and never needs to persist one. Spark's model JSON
  **is** the persisted artifact, and the whole point of R1 is that hand-authored attributes live in it
  — including exactly the virtual attributes this project intends to start writing. Prune would be safe
  only for as long as nobody uses the feature this PR is delivering.

  Nor does Vidyano supply a design to copy: it has no marker field in its attribute JSON either (F3).
  Spark would have to invent one from scratch, which returns us to the grandfathering problem above.

  **The safe subset of prune is a report, and R2 already delivers it**: every orphan is logged with its
  name and type, so an operator can identify and hand-remove obsolete entries. That is most of the
  value at none of the risk. If a real need for automation appears later, the prerequisite is a
  positive marker plus a grandfathering story — not a flag.

- **D2 — split the filter rather than widen it** (F5). `GetSparkModelProperties()` describes model
  shape and admits get-only; a new `GetSparkWritableProperties()` answers write authorization and
  keeps `CanWrite`. Replication moves to the latter.

- **D3 — preserved orphans keep their original relative order** and are appended after the rebuilt
  set. Array position is not semantically meaningful (`Order` is a persisted field, only assigned when
  previously unset, `:412`), so appending cannot disturb layout.

- **D4 — get-only complex properties do generate embedded model files.** Consistent with how any other
  complex property is treated; the alternative is an attribute referencing a type with no model.

## Out of scope

- **A post-load actions hook** (F4) — e.g. `OnAfterLoadAsync(PersistentObject po, T entity)` on both
  the Get and List paths — is the missing half of the virtual-attribute story. Genuinely useful, but a
  new public extension point on the read path deserves its own issue and its own review, not a
  passenger seat in a synchronizer fix. **Filed as [#261](https://github.com/MintPlayer/MintPlayer.Spark/issues/261).**
- **Get/List projection asymmetry.** Index-projected fields populate on the list path (the query runs
  against the projection type, and `PopulateAttributeValues` reflects over `entity.GetType()`,
  `EntityMapper.cs:193`) but not on single-Get, which always loads the base entity via `OnLoadAsync`
  (`DefaultPersistentObjectActions.cs:25-40`). Pre-existing, unrelated to this change. Filed as
  [#262](https://github.com/MintPlayer/MintPlayer.Spark/issues/262).
- **A `[Calculated]`-style marker + analyzer.** Only worth building if D1 is ever revisited.

## Risks

- **R1 changes generated output for any model with a stale attribute.** Intended, but a first
  synchronize after upgrading may show attributes reappearing in diffs that previous runs had been
  quietly deleting. The log line explains each one.
- **R5 changes generated output for every entity with a get-only property** — new attributes appear.
  This is the largest behavioural surface in the PR and the reason it is a separate milestone.
- **Breadcrumb validation** (`:522-540`) throws when a template references an unknown attribute.
  Preserving orphans can only *reduce* those failures, never introduce them.

## Results

Delivered in PR [#263](https://github.com/MintPlayer/MintPlayer.Spark/pull/263) — 5 commits, CI green,
**1487 tests** (1389 + 60 + 38). Shipping as `10.0.0-preview.50`.

Every requirement met (R1–R8). Two risks turned out smaller than expected:

- **R5's blast radius is currently zero.** No entity in `Demo/` or `libs/` declares a get-only
  property, so nothing regenerates differently today. The change matters going forward, not on upgrade.
- **A required preserved attribute blocks saves**, which the plan did not anticipate. `ValidationService`
  validates against the *model*, so a required attribute nothing can populate fails every save. It
  warns rather than clearing `IsRequired` — silently rewriting hand-authored model state is the failure
  mode this whole PRD exists to remove — and a client-submitted value can legitimately satisfy it.

One documentation gap surfaced late and was closed here: **`[IgnoreProperty]` vs `[JsonIgnore]` had
never actually been documented** in `MintPlayer.Spark/README.md`, despite being in #253's Files table
and in #254's plan. It now has its own section.

D1 (no prune) stands. If it is ever revisited, the prerequisite is unchanged: a positive marker **and**
a grandfathering story for attributes authored before it existed.

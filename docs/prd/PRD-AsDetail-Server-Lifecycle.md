# PRD: Server-side lifecycle for AsDetail rows

**Status:** Draft — decisions N1–N8 open
**Last updated:** 2026-09-08
**Owner:** framework (`libs/spark`, `libs/node_packages/ng-spark`)

## 1. Problem

Clicking **New** or **Delete** on an `AsDetail` array attribute never reaches the server. The row is
created and destroyed entirely in the browser, so no server code observes either event.

The concrete consequences:

- **A new row cannot be given defaults.** `addInlineRow` pushes a literal `{}`
  (`libs/node_packages/ng-spark/po-form/src/spark-po-form.component.ts:691`). There is no way to
  default a date to now, copy a denormalised value down from the parent, or preselect an enum.
- **A deleted row is unobservable.** `removeArrayItem` splices a local copy (`:715`). On save the
  whole collection is rebuilt from what the client sent
  (`libs/spark/MintPlayer.Spark/Services/EntityMapper.cs:661-676`), so a removal is expressed as
  *absence*. Nothing can veto it, audit it, or turn it into a soft delete.
- **The child type's own Actions class is never consulted on the write path.** Every hook runs
  against the parent type (`DefaultPersistentObjectActions.cs:295-310`); the child is a bare
  `Activator.CreateInstance` (`EntityMapper.cs:670`).

## 2. Current state

### 2.1 The complete hook surface

`IPersistentObjectActions<T>` (`libs/spark/MintPlayer.Spark/Actions/IPersistentObjectActions.cs`)
declares eleven hooks. This inventory is part of the problem statement: note that **none of them
runs at construction time**, and none is reachable per AsDetail row on the write path.

| # | Hook | Signature | Line | Purpose |
|---|---|---|---|---|
| 1 | `OnLoadAsync` | `Task<PersistentObject?>(string id, PersistentObject? parent)` | :30 | The only load hook. id in, page out; `null` = 404 |
| 2 | `OnSaveAsync` | `Task<T>(IAsyncDocumentSession, PersistentObject)` | :40 | Create **and** update; entity mapping happens inside |
| 3 | `OnDeleteAsync` | `Task(IAsyncDocumentSession, string id)` | :48 | Delete; should call `OnBeforeDeleteAsync` |
| 4 | `OnBeforeSaveAsync` | `Task(PersistentObject, T entity)` | :57 | Validate / transform / enrich before persist |
| 5 | `OnAfterSaveAsync` | `Task(PersistentObject, T entity)` | :66 | Notifications, auditing, cache invalidation |
| 6 | `OnBeforeDeleteAsync` | `Task(T entity)` | :73 | Validation / cascade |
| 7 | `OnRefreshAsync` | `Task(SparkRefreshArgs<T>)` | :93 | Reshape the form when a `triggersRefresh` attribute changes |
| 8 | `IsAllowedAsync` | `Task<bool>(string action, T entity)` | :120 | Per-row gate; action ∈ Read/Query/Edit/Delete/New. Not memoized |
| 9 | `GetRowFilterAsync` | `Task<Expression<Func<T,bool>>?>(string action)` | :139 | Pushdown predicate. **`null` = unrestricted, not deny.** Cached per (type, action) |
| 10 | `GetProtectedAttributesAsync` | `Task<IReadOnlyCollection<string>?>(string action, T entity)` | :151 | Per-row redaction; a dotted name (`Jobs.Salary`) reaches inside AsDetail rows |
| 11 | `OnQueryAsync` | `Task(SparkQueryContext)` | :181 | Withhold custom actions before rows exist. Default impl. **Not** for row filtering |

Two further virtuals live on `DefaultPersistentObjectActions<T>` only, not on the interface:
`GetDefaultIncludes()` (:210) and `MaterializeAsync(ids)` (:236); `LoadManyAsync` (:106) is
non-virtual. Companion interfaces: `ISparkOwnsRowSecurity` (a `RowSecurityRationale` string enforced
by a startup validator), `ISparkRowRule<T>.ApplyAsync` (filter + predicate in one call), and
`ICustomAction`.

**`grep -rn "OnNewAsync|OnConstruct|OnNew(" libs/**/*.cs` returns zero hits.**

### 2.2 There is no server New flow at all — not even for root objects

`GET /spark/po/{objectTypeId}/{**id}` explicitly refuses an empty id
(`Endpoints/PersistentObject/Get.cs:29-32`). A blank root object is scaffolded in the browser from
`GET /spark/types` metadata (`spark-po-create.component.ts:59-73`), and the first time the server
sees it is `POST /spark/po/{objectTypeId}` at save. So a construction hook is greenfield for root
objects and AsDetail rows alike.

### 2.3 The one existing seam

`Endpoints/PersistentObject/Refresh.cs:128-132` already resolves an AsDetail row to **the child
type's own Actions class** and invokes `OnRefreshAsync` on it, using `NestedTrigger.TryParse`
(wire format `Jobs[2].ProfessionId`, `:269-289`) and `BuildNestedRow`. Its doctrine comment
(`:120-127`) is the precedent this PRD follows:

> the hook that owns a type's shape is that type's own; the row is handed its Parent for the context
> it cannot have alone — while **authorization stays on the route type**, because nested AsDetail
> types are not in `security.json`.

Server-side blank-object scaffolding also already exists — `EntityMapper.ScaffoldFrom`
(`EntityMapper.cs:367`), reachable through `IManager.GetPersistentObject`, exposed by no endpoint.

### 2.4 No prior decision is being reversed

Six design documents describe AsDetail (`PRD-array-asdetail.md`, `PRD-inline-asdetail-editing.md`,
`recursive-asdetail-prd.md`, `PRD-AsDetailReferenceParentContext.md`, `PRD-PoDetailActionBar.md`,
`guide-asdetail-attributes.md`). **None weighs a server round-trip and rejects it** — the
client-side model is asserted, never debated. The nearest adjacent deferrals are backend breadcrumb
resolution inside arrays (`PRD-array-asdetail.md:199`) and server-side per-item validation (`:404`),
both deferred on "the client already does it", not on principle. This is a gap, not a re-litigation.

## 3. Prior art

Vidyano, the framework Spark's object model follows, resolves this with one invariant:

> **The unit of work is the parent, but object construction is always a server responsibility.**

Its construction hook takes four arguments, each load-bearing: the freshly constructed child, the
**parent**, the **originating list** (so one child type can default differently depending on where
Add was pressed), and a **parameter bag** (so a New split-button can say which variant was chosen).
The framework pre-wires the obvious part itself — if the child has exactly one attribute whose type
matches the parent's, it is set to the parent and made read-only — and the hook handles everything
beyond that.

Three findings from that prior art shape this design:

1. **Create round-trips in both collection shapes; delete round-trips in only one.** For an embedded
   child collection, delete is a client-side flag applied during the parent's save. For an
   independently-persisted associated list, delete round-trips immediately to a delete hook. The
   asymmetry is deliberate: New is a *construction* request, not a *persistence* request, so it buys
   server-side defaulting without buying a per-row save.
2. **Three distinct value-setting primitives, and conflating them is a design bug** — *set as
   default* (value appears, field **not** dirty), *set as edit* (dirty), *set and cascade*. Defaults
   on a new child must use the first, or a user who adds a row and abandons it produces spurious
   change tracking.
3. **Two parent references, not one** — a general "I was opened from / created by this object", and
   a narrower "I am a row inside this object's embedded collection". The narrow one is what the
   persistence layer keys on: when it is present the child does **not** persist itself, because the
   aggregate root owns the save. Getting this wrong yields double writes or orphans.
4. **The parent reference is a mutable input, not a fixed fact.** The framework auto-populates the
   child's parent-typed attribute from the caller-supplied parent *before* the hook runs — and a
   hook may **replace that parent and delegate to the base implementation**, re-pointing the
   automatic wiring somewhere else. This is not an exotic case: it is what a **grandchild
   collection** requires, where the object that owns the save is two levels above the grid the user
   pressed Add in. The same substitution is needed in the load hook. Correspondingly, child identity
   is **decomposable back into (aggregate-root id, child id)**, so a hook can reach either level.
   A framework that hard-wires the parent before the hook, with no way to redirect it, simply breaks
   at two levels deep.

There is deliberately no client-only add mode there. The stated reason is worth quoting into our own
decision: the moment one exists, every defaulting hook has two execution contexts to reason about.

## 4. Proposed design

### N1 — New round-trips to the server

Pressing Add on an AsDetail array issues a construct request and inserts the **server-returned**
object into the grid. Nothing is persisted; the parent still owns the save.

New endpoint, following the `Delete.cs` handler template (antiforgery metadata, `ResolveEntityType`,
`ClientResult.EnvelopeRefusal` for both unknown-type and denied so neither is a disclosure oracle):

```
POST /spark/po/{objectTypeId}/new
body: { parentType?, parentId?, asDetailAttribute?, query?, parameters? }
→ ClientOperationEnvelope { Result: PersistentObject, Operations: [] }
```

It reuses `EntityMapper.ScaffoldFrom` for the blank object and `EnsureAsDetailTypeDeclared`
(`EntityMapper.cs:705`) to fail closed on an undeclared child type, exactly as the save path does —
otherwise the endpoint becomes a mass-assignment bypass.

**Recommendation: do not make this configurable per attribute.** The owner asked whether the
developer should choose. The cost of choice is that every defaulting hook acquires two execution
contexts, and the failure mode is silent — a grid flipped to client-only stops running defaults with
no error. If a fast path is wanted later it should be an explicit, separately-named opt-out on the
attribute (`"clientSideNew": true`) documented as skipping hooks, never the default. **N1 is the one
decision most worth pushing back on if you disagree — say so and it becomes a flag.**

### N2 — Delete does not round-trip at click time; the hook runs at parent save

Matching the prior art, and matching what Spark's aggregate model already implies. The row is marked
deleted client-side, the parent becomes dirty, and during the parent's save the framework
diffs the incoming collection against the stored one and invokes, per removed row, the child type's
`OnBeforeDeleteAsync`. Throwing from it vetoes the parent's save. A soft delete is expressed by the
developer keeping the row and setting a flag instead.

This is what makes the owner's `UploadToken` revoke plan work on an embedded row: the hook fires,
and a revoke can be a veto-plus-flag rather than a removal.

### N3 — Row identity is the blocking prerequisite ⚠️

N2 is impossible today. `EntityMapper.WriteAsDetailAsync` (`:661-676`) does
`Activator.CreateInstance` for **every** incoming child and rebuilds the collection wholesale — no
identity matching, so "which rows disappeared" is not a computable question. Spark has no
`[ValueKey]` concept at all (**zero hits repo-wide**).

Introducing a key for embedded rows collides with four standing assumptions:

| Assumption | Location | Collision |
|---|---|---|
| Embedded children have no id, so breadcrumbs render in place | `EntityMapper.cs:198-205`, `EmbeddedBreadcrumbRenderer.cs:65-71` | Give them ids and they fall into `breadcrumbs?.Get(po.Id)`, which misses → blank breadcrumbs |
| `New` vs `Edit` is decided by `string.IsNullOrEmpty(po.Id)` | `DatabaseAccess.cs:179` | A child carrying an id flips the verb on any path that routes it through the top-level save decision. This branch was an authorization hole once (`Create.cs:70-72`) |
| Row identity is **array position** | `Refresh.cs:269-289` (`Jobs[1].ProfessionId`), `spark-po-form.component.ts:723-735` (reorder) | A server round-trip that mutates the array while the client holds indices is a race: a stale index addresses a different row |
| Child ids are read from a CLR `Id` property if one happens to exist, else null | `EntityMapper.cs:195` | Already inconsistent — `EventColumnMapping` has one, stamped by hand in `GitHubProjectActions.cs:79-83`; other embedded types do not. Nothing enforces uniqueness |

**Recommendation:** an explicit opt-in key attribute on the embedded type rather than reusing `Id`,
so nothing that keys on `Id` changes meaning. Generated client-side on Add and round-tripped
verbatim. Types without the key keep today's positional behaviour and get no delete hook — which
keeps this change additive.

**The key must be decomposable** (prior-art finding 4). A row's identity should resolve back to
*(aggregate-root id, child key)*, with a documented way to split it, because a hook on a grandchild
needs the root id to reach the object that actually owns the save, and the child key to find its own
row. A flat opaque key that cannot be split forces every such hook to re-derive the root from
context it may not have. Design the key's string form for that from the start; retrofitting a
separator into a key already written into documents is a migration.

### N4 — The hook: `OnNewAsync(SparkNewArgs<T> args)`

Mirrors `SparkRefreshArgs<T>` (`Actions/SparkRefreshArgs.cs`), Spark's established shape for
"an unsaved object being shaped":

| Member | Why |
|---|---|
| `PersistentObject` | the object to mutate |
| `Parent` | copy or derive values from the owner |
| `AsDetailParent` | the narrow reference of prior-art finding 3 — non-null only for an embedded row |
| `AsDetailAttribute` | which collection Add was pressed in; one child type, different defaults per site |
| `Parameters` | the New-variant bag |

**All members get-only**, and **Spark does not automatically bind the child's parent-typed
attribute**. An earlier draft of this PRD had settable parents so a grandchild's hook could redirect
that binding; measurement retired both halves of that idea.

*Why an args object rather than four plain parameters.* Two independent lines of evidence:

- **Dispatch.** Plain-parameter hooks are resolved by bare name (`GetMethod(name)`, which cannot even
  tolerate an overload) and invoked with positional literals in seven places —
  `DatabaseAccess.cs:99,143,492,537,546`, `SyncActionHandler.cs:269,282`, e.g.
  `Invoke(actions, [id, null])`. Adding a parameter means editing every literal, and a miscount is a
  runtime `TargetParameterCountException`. `RefreshInvoker.cs:168` resolves by exact signature and
  invokes `[args]`; adding a member to the args type touches no dispatch code. Spark has already
  broken `OnLoadAsync`'s signature twice (`ae37fedc`, `5ebfaa45`), with 11 live overrides today.
- **Ageing, measured in the prior art.** Across 29 releases spanning 22 months, **no
  plain-parameter hook ever gained a parameter** — they froze — while args types grew members
  freely. The one plain-parameter hook that needed more inputs got a parallel args overload beside
  it rather than a fifth parameter. Arity is not the criterion either: that framework's refresh args
  carries two get-only members and is still an args object, and every hook it added after its first
  generation is args-shaped.

*Why get-only, and why no auto-binding.* Substitution appears in ~3% of delegating call sites in the
prior art, always in the parent slot, in two idioms (replace with a synthesized parent; pass null to
suppress binding, then restore the association by hand). But it exists **only because that framework
binds the parent attribute before the hook runs** — it is a workaround for an implicit convenience,
not a requirement of construction. Spark does not have that convenience, so a grandchild's hook
needs no redirect at all: it sets the attribute it wants from the object it wants.

⚠️ **If auto-binding is ever added, express the redirect as an explicit argument or a named method —
never a setter.** In the clearest real example the substituting hook goes on to read the *original*
parent **after** delegating with a substitute, so both values must stay reachable simultaneously; a
setter destroys the original unless every author remembers to stash it first. That prior-art
framework never exposes a settable `Parent`, `Query` or `PersistentObject` on any args type — where
it swaps a hook's target it uses named intent methods. This also removes the ordering trap
("substitute before calling base, never after"): a rule that only needs stating because the binding
is implicit.

Spark's `OnLoadAsync(string id, PersistentObject? parent)` already takes the parent as a parameter,
so the load path can pass a different one downward with no signature change.

### N5 — Value-setting primitives are missing and must be added

Spark's `PersistentObjectAttribute` exposes `Value` and `IsValueChanged` as plain settable
properties (`PersistentObject.cs:184,190`) with no helpers. Prior-art finding 2 says the
default-vs-edit distinction is exactly what a defaulting hook needs. Add `SetValue` (dirty) and
`SetOriginalValue` (not dirty), and use the latter in every defaulting example, or every defaulted
row is born dirty and an abandoned Add leaves the parent falsely modified.

### N6 — Authorization follows the `Refresh.cs` doctrine

The governing right for constructing or removing a child row is the **parent route type's**
(`Refresh.cs:78` uses `isNew ? "New" : "Read"` on the route type). Nested AsDetail types are not in
`security.json`, and this PRD does not change that.

⚠️ `QueryReadEditNewDelete/EventColumnMapping` in the CodeCoverage app gates **metadata, not rows**:
`GET /spark/types` omits a type the caller has no `Query` right on, and without the grant an inline
row rendered with no fields at all — no console error, no log line
(`apps/CodeCoverage/CodeCoverage/Actions/EventColumnMappingActions.cs:7-24`). The `New`/`Delete`
halves gate nothing server-side today; they feed the client's `canCreateDetailRow` /
`canDeleteDetailRow` pipes, **which default to `true` when no entry exists**
(`pipes/src/can-create-detail-row.pipe.ts:6-9`). That client-side fail-open is tolerable only while
the server owns the real gate — the new endpoint must not start trusting those pipes.

### N7 — Where the flag lives, and the hash consequence

A schema flag belongs on `EntityAttributeDefinition`
(`libs/spark/MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs:97`) next to `TriggersRefresh`
(:163) — the exact precedent: nullable, schema-only, deliberately not echoed on the runtime
`PersistentObjectAttribute` so a client cannot claim an undeclared one.

`ModelSynchronizer`'s update branch (`ModelSynchronizer.cs:760-822`) only resets fields it
explicitly assigns, so a hand-set flag survives re-sync provided no assignment is added there.

⚠️ **Adding a schema property does not change the model hash.** `ModelFileShape.Describe` hashes a
whitelist (`ModelFileShape.cs:138-144`) that a new field is not in, and `SparkModelShape` hashes CLR
`PropertyInfo`. That is convenient — no consuming app is forced to re-synchronize — and it is a
security decision: a field outside the whitelist can be edited on a deployed model without tripping
the gate. If the flag decides whether server-side hooks run, weigh adding it to
`StructuralAttributeFields` (a one-time forced re-sync for apps that have it written). The
whitelist's own doc comment (`ModelFileShape.cs:114-121`) is the precedent for "security-relevant
fields belong in the hash".

### N8 — Non-goals

- **A `PreClient` equivalent.** The prior art runs one presentation hook after construct/load/refresh
  so "how this object looks in state X" lives in one place. Spark has no equivalent and would
  benefit from one, but it changes the load and refresh paths too and deserves its own PRD rather
  than riding in on this one.
- **Per-row save for embedded children.** The parent remains the unit of work. Anything wanting
  independent persistence should be a root document with a declared sub-query instead.
- **Retrofitting keys onto existing embedded types.** N3 is opt-in; untouched types keep today's
  behaviour.

## 5. Acceptance criteria

1. Add on an AsDetail array issues exactly one `POST /spark/po/{type}/new` and inserts the returned
   object; a child type overriding `OnNewAsync` sees its defaults in the grid without a save.
2. A default set with `SetOriginalValue` leaves the parent **not** dirty; one set with `SetValue`
   does.
3. `OnNewAsync` receives a non-null `AsDetailParent` for an embedded row and null for a root object.
3b. A hook that **replaces** `args.Parent` and delegates to the base behaviour sees the child's
   parent-typed attribute wired to the substituted object, not the caller-supplied one — verified
   with a two-level-deep (grandchild) collection, which is the case that fails without it.
3c. A keyed row's identity splits into (aggregate-root id, child key).
4. For a keyed embedded type, removing a row and saving the parent invokes the child's
   `OnBeforeDeleteAsync`; throwing from it fails the parent's save with a validation error.
5. An unkeyed embedded type behaves exactly as it does today.
6. Re-running `--spark-synchronize-model` preserves the new flag and leaves the model hash unchanged
   (template: `tests/MintPlayer.Spark.Tests/Model/TriggersRefreshPreservationTests.cs`).
7. An undeclared AsDetail child type is refused by the new endpoint identically to the save path.
8. Unknown type and denied both return the same refusal shape.

## 6. Risks

| Risk | Mitigation |
|---|---|
| Stale array index addresses the wrong row after a server-side insert | N3 keys; the client inserts the returned object itself rather than re-fetching |
| Giving embedded rows ids silently breaks breadcrumbs | N3 uses a separate key attribute, not `Id` |
| One HTTP request per added row | Accepted, as in the prior art. Revisit only with a measured complaint |
| A flag outside the hash whitelist is tamperable on a deployed model | N7 — decide explicitly, do not default into it |
| Client permission pipes fail open | N6 — server gate is authoritative; endpoint must not read them |
| No E2E test drives a detail grid today | Add one; `tests/MintPlayer.Spark.E2E.Tests` currently only has smoke and return-url coverage |

# PRD: Server-side lifecycle for AsDetail rows

**Status:** Design settled (N1–N10). F1–F2 landed; F3 onward outstanding.
**Last updated:** 2026-09-08
**Owner:** framework (`libs/spark`, `libs/node_packages/ng-spark`)
**Issue:** #380

## 1. Problem

Clicking **New** or **Delete** on an `AsDetail` array attribute never reaches the server.

- **A new row cannot be given defaults.** `addInlineRow` pushes a literal `{}`
  (`spark-po-form.component.ts:691`) — no way to default a date to now, copy a value down from the
  parent, or preselect an option.
- **A removed row is unobservable.** `removeArrayItem` splices a local copy (`:715`), and the save
  rebuilds the collection wholesale (`EntityMapper.cs:661-676`), so a deletion is expressed as
  *absence*. Nothing can veto it, audit it, or turn it into a soft delete.
- **The child type's rights are fiction.** `New/X`, `Edit/X` and `Delete/X` can be granted on an
  embedded type — HR's `security.json:83` grants `QueryReadEditNewDelete/CarreerJob` today — and
  **none of them is consulted on any write path.** A caller with `Edit/Person` can add, remove and
  rewrite phone numbers freely.

## 2. Current state

### 2.1 The hook surface

`IPersistentObjectActions<T>` (`libs/spark/MintPlayer.Spark/Actions/IPersistentObjectActions.cs`):

| # | Hook | Signature | Line |
|---|---|---|---|
| 1 | `OnLoadAsync` | `Task<PersistentObject?>(string id, PersistentObject? parent)` | :30 |
| 2 | `OnSaveAsync` | `Task<T>(IAsyncDocumentSession, PersistentObject)` — create **and** update | :40 |
| 3 | `OnDeleteAsync` | `Task(IAsyncDocumentSession, string id)` | :48 |
| 4 | `OnBeforeSaveAsync` | `Task(PersistentObject, T)` | :57 |
| 5 | `OnAfterSaveAsync` | `Task(PersistentObject, T)` | :66 |
| 6 | `OnBeforeDeleteAsync` | `Task(T entity)` | :73 |
| 7 | `OnRefreshAsync` | `Task(SparkRefreshArgs<T>)` — `triggersRefresh` only | :93 |
| 8 | **`OnNewAsync`** | `Task(SparkNewArgs<T>)` — **added by this work**, default-implemented | :117 |
| 9 | `IsAllowedAsync` | `Task<bool>(string action, T)` — per-row, not memoized | :144 |
| 10 | `GetRowFilterAsync` | `Task<Expression<Func<T,bool>>?>(string action)` — **null = unrestricted** | :163 |
| 11 | `GetProtectedAttributesAsync` | `Task<IReadOnlyCollection<string>?>(string action, T)` | :175 |
| 12 | `OnQueryAsync` | `Task(SparkQueryContext)` — default impl | :205 |

Plus `GetDefaultIncludes()` (:210) and `MaterializeAsync(ids)` (:236) on the base class only.

Before this work there was **no construction hook and no server New endpoint at all**, not even for
root objects: `GET /spark/po/{type}/{**id}` refuses an empty id (`Get.cs:29-32`) and a blank object
is scaffolded in the browser (`spark-po-create.component.ts:59-73`).

### 2.2 What the save path does today

`Update.cs:65` → `DatabaseAccess.cs:173-182`, whose entire type-level check is:

```csharp
var action = string.IsNullOrEmpty(persistentObject.Id) ? "New" : "Edit";
await permissionService.EnsureAuthorizedAsync(action, entityTypeDefinition.Name);   // Person
```

Then `EntityMapper.WriteAsDetailAsync` (`:661-676`) `Activator.CreateInstance`s every incoming row
and replaces the collection wholesale. **Ten rows in, zero rows out, or ten rows with every field
rewritten are the same operation**, and no child `EntityTypeDefinition` is ever resolved to check a
right. The only child-aware gate is `EnsureAsDetailTypeDeclared` (`:704-712`), an existence check.

### 2.3 The existing seam

`Refresh.cs:128-132` already dispatches per AsDetail row to **the child type's own Actions class**,
using `NestedTrigger.TryParse` (wire form `Jobs[2].ProfessionId`, `:269-289`). Its authorization
doctrine (`:120-127`) borrows the owner's right — correct for refresh, which has no verb of its own,
and deliberately *not* what this PRD does for New/Delete (see N6).

### 2.4 No prior decision is being reversed

Six documents describe AsDetail and **none weighs a server round-trip and rejects it**. This is a
gap, not a re-litigation.

## 3. Prior art, as measured

Findings from the framework Spark's object model follows — read from its source, its running app,
and five of its production databases. Where an earlier draft of this PRD guessed, the measurement is
noted.

1. **Both New and Delete round-trip, including for embedded value-object collections.** An earlier
   draft claimed delete stayed client-side for embedded rows. Wrong: a company's addresses are a
   plain `List<Address>` on the company document, and both the Add button and each row's Delete
   button call the address type's own actions class.
2. **New and Delete are requests *about* a row, not persistence *of* one.** For an embedded child
   the hook writes nothing — the row appears or disappears when the parent saves. That is what buys
   server-side defaulting, validation and a permission check without buying a per-row save.
3. **The endpoint genuinely enforces; the parent's save does not.** `ExecuteAction("Query.New" /
   "Query.Delete")` calls `CheckRight` and throws. But `SaveDetailsAsync` / `ProcessValueDetails`
   partition the client's rows by the client-supplied `IsNew` / `IsDeleted` flags and apply them
   with **no rights call anywhere** — the single check is on the parent type. The detail-save path
   structurally bypasses the dispatch that checks. Its tamper token does not cover collection
   membership either.
4. **Identity is an explicit marker with a field initializer**: `[ValueObject]` on the class,
   `[ValueKey] public string Id { get; set; } = <new guid>;`. The key is minted when the CLR object
   is constructed, not by a hook.
5. **Value objects are always collections.** Across five production databases, every single nested
   object is a translated string, a serialized envelope or a settings blob — not one is a keyed
   value object. Single-valued embedded objects are a Spark-specific shape with no prior art.
6. **Keys are near-universal but not universal.** ~14,400 embedded rows sampled: all keyed except
   **12**, concentrated in two collections. Format is 32 hex chars (`ToString("N")`).
   **Those 12 were never backfilled** — that framework tolerates keyless rows indefinitely, which it
   can afford because it does no save-time enforcement. Spark cannot (N9).
7. **Three value-setting primitives, and conflating them is a bug**: set-as-default (not dirty),
   set-as-edit (dirty), set-and-cascade. Defaults on a new row must use the first.
8. **Signatures age badly, args objects don't.** Across 29 releases spanning 22 months, no
   plain-parameter hook ever gained a parameter; args types grew members freely, and the one
   plain-parameter hook that needed more inputs got a parallel args overload beside it.

## 4. Design

### N1 — New round-trips

`POST /spark/po/{objectTypeId}/new` returns a server-constructed, unsaved object which the client
inserts into the grid. Handler follows the `Delete.cs` template: antiforgery metadata,
`ResolveEntityType`, and `ClientResult.EnvelopeRefusal` for both unknown-type and denied so neither
is a disclosure oracle. Scaffolding reuses `EntityMapper.ScaffoldFrom` (`:367`);
`EnsureAsDetailTypeDeclared` (`:705`) must be called or the endpoint becomes a mass-assignment
bypass.

### N2 — Delete round-trips

Clicking Delete calls the row type's delete hook, which may **refuse** (throw ⇒ row stays, error
surfaced), **react** (audit, notify, cascade outside the aggregate), or **do nothing**, which is the
common case. The row is then marked deleted client-side and leaves the document when the parent
saves. A soft delete stays what it always was: keep the row, set a flag.

### N3 — `ServerSideRowLifecycle`, on the row type's own model file

Opt-in per row type — the type that owns the hooks owns the decision, and one setting governs every
grid it appears in. Default off: today's purely client-side behaviour.

⚠️ **Save-time enforcement (N6) must not read this flag.** It applies to every embedded collection
regardless. That is also what keeps the flag safely outside the model hash (which covers only
`name`, `clrType`, `alias`, `queryType`, `indexName`): were the rights check conditional on it,
editing one unhashed line on a deployed model would switch the check off.

### N4 — `[ValueObject]` is mandatory for AsDetail

AsDetail stops being inferred from "is a complex type" and becomes declared. Every embedded type
must be `[ValueObject] partial`. A complex-typed property whose type lacks the attribute is a
synchronize/startup error, not a silently-inferred detail.

This is a breaking change across CodeCoverage, HR, DemoApp, Fleet and the identity-provider library.
Missing `partial` is a compile error — the loud kind.

### N5 — `[ValueKey]`, generated, and only for collection-used types

A source generator emits `[ValueKey] public string Id { get; set; } = Guid.NewGuid().ToString("N");`
onto `[ValueObject] partial` classes. It **skips a type that already declares an `Id`** —
`EventColumnMapping`'s is *derived* from the event type and stamped in `OnBeforeSaveAsync`
(`GitHubProjectActions.cs:79-83`), and a generated Guid would fight it.

**Only types used in a collection get a key.** A single nested object has no siblings to be
distinguished from; it is addressed by its property name on the parent. The generator determines
this from the semantic model — every parent/child pair in the workspace shares an assembly, so
scanning the compilation for `List<T>` / `T[]` properties whose element type is `[ValueObject]` is
sufficient. A cross-assembly miss is caught by the gate in N9.

Why generated rather than hand-written: **this has already failed here.** `EventColumnMapping.Id`
shipped unassigned, every rule on a board keyed `""`, invisible until someone opened the database —
and the inline editor identifies rows by that key across saves, so two blank-keyed rows were
indistinguishable to it. The prior art gets away with a hand-written initializer because its
`GuidId.New()` is a house convention people copy; Spark has no such convention.

⚠️ Keys on collection-used types leave the four working embedded breadcrumbs alone (`CoverageSummary`,
`GateSettings`, HR `Address`, `AddressDescription` are all singles). Two breadcrumbs are **already**
dead for this reason — `DemoApp/Address` (`{Street}, {City} {State}`) and `ProjectColumn`
(`{Name}`) — because the fallback is gated on `string.IsNullOrEmpty(po.Id)` at
`EntityMapper.cs:204`. That one-line fix is in scope as a bug fix.

### N6 — Rights are the row type's own, and the save is the enforcement point

The grid's affordances are governed by the type in the grid. **This diverges from the prior art,
which leaves the parent's save unchecked** (§3.3) — deliberately, because a right that the obvious
bypass defeats is not a right.

| Right | Enforced at save | UI |
|---|---|---|
| `New/PhoneNumber` | a row key absent from the stored set may appear | `[+ New]` visible |
| `Edit/PhoneNumber` | a matched row's incoming content is accepted; **without it the stored content is restored** | row inputs enabled |
| `Delete/PhoneNumber` | a stored key absent from the incoming set may disappear | `[bin]` visible |

All three are decided at one site — `EntityMapper.cs:661`, where the stored collection is still
readable (the wholesale replacement is at `:676`) and rows are already being matched by key. No
extra load.

Content restoration follows the established pattern: `ShieldProtectedAttributesAsync`
(`DefaultPersistentObjectActions.cs:482-505`) already restores a stored value onto an incoming
attribute and clears `IsValueChanged` before the merge. Its `if (name.Contains('.')) continue;`
skip (`:493`) is a limitation of expressing shielding as a *dotted attribute name*; at collection
level the stored row is in hand, so the limitation does not apply. Restoration is silent, matching
that precedent — with the inputs disabled a legitimate client never sends changed content, so the
only caller who reaches it is one that tampered.

**Deliberately narrower than the refresh doctrine** (`Refresh.cs:120-127`, "the right that governs
editing a row is the one governing the object that owns it"). That is right *for refresh*: reshaping
a form has no verb of its own. Adding and removing rows are real verbs a deployment may want to
grant separately — a person's details editable by many, their phone numbers by few.

**The parent is still loaded on the New endpoint, and that load is still a gate.** The row type's
right says the caller may create rows of this kind, not which parent they may attach one to.

⚠️ The `Query` half of a grant is separately load-bearing: `GET /spark/types` omits a type the caller
has no `Query` right on, and without it an inline row renders **with no fields at all** — no console
error, no log line (`EventColumnMappingActions.cs:7-24`).

⚠️ The client's `canCreateDetailRow` / `canDeleteDetailRow` pipes return `true` when no permission
entry exists (`pipes/src/can-create-detail-row.pipe.ts:6-9`). Those must flip to `false`, and the
server must never read them.

### N7 — The hook: `OnNewAsync(SparkNewArgs<T> args)`

| Member | Why |
|---|---|
| `PersistentObject` | the object to mutate |
| `Parent` | copy or derive values from the owner |
| `AsDetailParent` | non-null only for an embedded row — the reference that says the parent owns the save |
| `AsDetailAttribute` | which collection Add was pressed in; one type, different defaults per site |
| `Parameters` | the New-variant bag; never null, empty when the client sent none |

**All members get-only, and Spark does not auto-bind the child's parent-typed attribute.** An
earlier draft had settable parents so a grandchild's hook could redirect that binding; measurement
retired both halves. Substitution exists in the prior art *only because* it binds the parent before
the hook runs — a workaround for an implicit convenience, not a requirement of construction. Without
the binding, a grandchild's hook simply sets the attribute it wants from the object it wants, and
the ordering trap ("substitute before calling base, never after") disappears with it.

⚠️ If auto-binding is ever added, the redirect must be an explicit argument or a named method,
**never a setter**: in the clearest prior-art example the substituting hook reads the *original*
parent after delegating with a substitute, so both must stay reachable.

*Why an args object rather than plain parameters.* Dispatch: plain hooks are resolved by bare name
(`GetMethod(name)`, which cannot tolerate an overload) and invoked with positional literals in seven
places — `DatabaseAccess.cs:99,143,492,537,546`, `SyncActionHandler.cs:269,282`. A new parameter
means editing every literal, and a miscount is a runtime `TargetParameterCountException`;
`OnLoadAsync` has already been broken twice (`ae37fedc`, `5ebfaa45`). `RefreshInvoker.cs:168`
resolves by exact signature and invokes `[args]`, untouched as its args type grows. Plus §3.8.

### N8 — Value primitives

`SetValue` marks the attribute changed; **`SetOriginalValue` sets the value and leaves it clean.**
Every defaulting example must use the latter, or an added-then-abandoned row leaves the parent
falsely modified. (Landed: `PersistentObject.cs`.)

### N9 — Legacy keyless rows: migrate, then fail closed

**Spark's existing data is 100% keyless** — verified in production: every `Builds.Sessions` row has
no `Id`. This is not the prior art's 0.08% corner; it is the whole dataset, so tolerating it would
mean the fragile path runs once for every document.

- **A backfill migration per affected type** (`ISparkMigration`, pattern already in
  `apps/CodeCoverage/CodeCoverage/Migrations/`) stamping `Guid.NewGuid().ToString("N")` into keyless
  rows. Two for the current workspace: Coverage's `Builds.Sessions`, HR's `Person.Jobs`.
- **The diff refuses a save it cannot judge.** If any *stored* row in the collection is keyless,
  throw naming the document and the type. With the migration run this never fires; without it an app
  gets one loud error instead of spurious `New`/`Delete` refusals scattered across its data.
- **Startup gate**, in the style of `RowPolicyDeclarationValidator`: an AsDetail attribute with
  `isArray: true` whose child type has no `[ValueKey]` is refused at startup. This is what catches a
  cross-assembly miss in N5, and it makes "enforcement silently not applying" unrepresentable.

⚠️ Operational sharp edge: a restored old backup, or an upgrade without migrations, fails on save
rather than degrading. That is the right direction for something enforcing a permission, and it
argues for the migration being scaffolded rather than hand-written per app.

### N10 — Non-goals

- **A `PreClient` equivalent.** The prior art runs one presentation hook after construct/load/refresh
  so "how this object looks in state X" lives in one place. Spark would benefit, but it changes the
  load and refresh paths too and deserves its own PRD.
- **Per-row save for embedded children.** The parent remains the unit of work. Anything needing
  independent persistence should be a root document with a declared sub-query.
- **Per-field `Edit` rights.** `Edit/X` is per-row, all-or-nothing on content.

## 5. Acceptance criteria

1. Add issues exactly one `POST /spark/po/{type}/new` and inserts the returned object; a type
   overriding `OnNewAsync` sees its defaults in the grid without a save.
2. A default set with `SetOriginalValue` leaves the parent **not** dirty; `SetValue` does.
3. `OnNewAsync` receives a non-null `AsDetailParent` for an embedded row, null for a root object.
4. With `ServerSideRowLifecycle` on, Delete calls the row type's hook; throwing leaves the row and
   surfaces the error. With it off, neither button round-trips.
5. **The save enforces the rights with the endpoints bypassed entirely.** `PUT` a `Person` holding
   `Edit/Person` but not `New/PhoneNumber`, carrying an unknown row key ⇒ refused. Holding
   `Edit/Person` but not `Delete/PhoneNumber`, omitting a stored row ⇒ refused. Holding
   `Edit/Person` but not `Edit/PhoneNumber`, changing a matched row ⇒ **saved, with the stored
   content intact**. All three must hold with `ServerSideRowLifecycle` **off**.
6. A save that adds one row and removes another in the same request is caught — the case a
   count-based approximation misses.
7. A stored collection containing a keyless row makes the save throw, naming the document.
8. An `isArray` AsDetail whose child type has no `[ValueKey]` refuses startup.
9. A single-valued `[ValueObject]` gets no key, and the four working embedded breadcrumbs still
   render. `DemoApp/Address` and `ProjectColumn` breadcrumbs render too (existing bug fixed).
10. Re-running `--spark-synchronize-model` preserves `ServerSideRowLifecycle` and leaves the model
    hash unchanged (template: `Model/TriggersRefreshPreservationTests.cs`).
11. Unknown type, wrong parent type, a name that is not an AsDetail attribute of that parent, a
    child type disagreeing with the parent's schema, and an invisible parent all return the same
    refusal.
12. An AsDetail type with no permission entry renders neither New nor Delete.

## 6. Risks

| Risk | Mitigation |
|---|---|
| Legacy keyless rows make the diff undecidable | N9 — migrate, then fail closed and loud |
| `[ValueObject]` mandatory breaks every app | Compile error for missing `partial`; startup error for a missing attribute. Loud, not silent |
| Generated key fights a derived key | N5 — generator skips types declaring their own `Id` |
| Keys break embedded breadcrumbs | N5 — collection-used types only; `EntityMapper.cs:204` fixed regardless |
| Client pipes fail open | N6 — flip to `false`; server never reads them |
| Diverging from the prior art on save enforcement | Deliberate and documented; the alternative is a right the obvious bypass defeats |
| No E2E test drives a detail grid today | Add one; `tests/MintPlayer.Spark.E2E.Tests` has only smoke and return-url coverage |

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

### N4 — `[ValueObject]` is an explicit marker

```csharp
[ValueObject]
public partial class PhoneNumber
{
    public string Phone { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
```

The generator finds these with `ForAttributeWithMetadataName` — Roslyn's fast path — and emits a
`partial` half for each. That is the whole discovery mechanism.

**This replaces a computed design, and the reason is worth keeping.** An earlier draft derived the set
by walking the type graph, on the argument that a marker can be forgotten while a computed set
cannot. Building it retired that argument twice over:

- **The walk cannot start where it must emit.** A generator may only add a `partial` half to a type in
  its own compilation. Every Spark context lives in the app project; every entity lives in a
  `*.Library` that the app references. So a context-rooted walk, running where the entities are,
  finds no context — and running where the context is, finds nothing it may emit for.
- **Rooting it somewhere else keyed the wrong types.** With `[GenerateIndex]` as a proxy root the
  first build reported `TreeFileSummary`, `AssemblyBuild`, `LineCoverage` and `BranchCoverage` —
  none of which has a model file, and one of which is a row *per line of code*. `[GenerateIndex]` is
  also an incomplete proxy: `OidcApplication` and `Commit` are persistent objects without one, so
  their collections were silently skipped.

A computed set is only safer than a marker when the computation can express the thing being asked.
Here it could not, and every attempt to approximate it substituted a different question —
"is this indexed?", "is this reachable?" — for the one that matters: *is this a value object?*
The author knows; the compiler is guessing.

`partial` was already required for a generated member, so the marginal cost is one attribute on a
class the author is already editing. A missing `partial` is a compile error; a missing `[ValueObject]`
is caught by the startup gate (N9), which compares the model's `isArray` AsDetail element types
against what was registered.

### N5 — `[ValueKey]`, and why a type with its own id must still be registered

`[ValueKey]` marks an **existing** property as the row key. Absent, the generator emits one:

| Declaration | Generated | Registered key |
|---|---|---|
| `[ValueObject] partial class X` | `public string Id { get; set; } = Guid.NewGuid().ToString("N");` | the generated `Id` |
| `[ValueObject] partial class X` with `[ValueKey] public string OptionId { get; set; }` | nothing | `OptionId` |

⚠️ **This closes a hole the first implementation had.** It skipped any type declaring its own `Id`,
which conflated two different things — *do not generate a key* and *has no key*. `ProjectColumn` and
`EventColumnMapping` both carry perfectly good keys (a GitHub single-select option id, and a value
derived from the event type in `OnBeforeSaveAsync`) and both were dropped from the registry entirely.
Under the fail-closed diff of N9 that makes their collections unjudgeable — for types that are
correctly keyed. `[ValueKey]` says "this is the key" rather than leaving the generator to infer it
from a property name.

A `[ValueObject]` type that is not `partial` and has no `[ValueKey]` is reported, never skipped
(SPARK016). Skipping is how the four non-model types above went unreported on the first run: the
generator emitted only for what it could and said nothing about the rest.

### N5b — A generated registry, per assembly

Each compilation emits its list plus a module initializer, following two patterns already in this
repo — a generated static list (`ClassNameList.g.cs`) and a module initializer
(`HostTranslationsAggregatorGenerator.Producer.cs:38`):

```csharp
internal static class SparkValueObjectRegistration
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() => SparkValueObjects.Register(
        typeof(PhoneNumber), static o => ((PhoneNumber)o).Id);
}
```

`SparkValueObjects` lives in Abstractions and holds type → key accessor. Per assembly, so the runtime
sees the union of everything loaded — which is what lets a type declared in one assembly and used
from another be keyed without either side knowing about the other.

The accessor is an emitted typed lambda, never a property name: `Type.GetProperty` is the expensive
part of reflection and this runs once per row per save, whereas `typeof` is a `ldtoken` plus a handle
lookup and a `Type`-keyed dictionary hashes a handle.

⚠️ **Module initializers run on first use of the module, not at process start.** The gate is safe
because loading the model resolves each `ClrType` and forces the assembly to load — but that is an
assumption. The gate must fail loudly on an unregistered type, never treat an empty registry as
"nothing to check".

### N5c — Two new packages

**`MintPlayer.Spark.Attributes`** — the 12 attributes currently in Abstractions move here:
`GenerateIndex`, `FromIndex`, `DefaultIndex`, `IgnoreForIndex`, `IgnoreProperty`, `Search`,
`Sortable`, `Reference`, `LookupReference`, `Breadcrumb`, `SparkTranslations`,
`SparkAttributeDescription` — plus the new `ValueObject` and `ValueKey`. Every one depends on nothing
but `System.Type` and primitives, so the package needs zero references and no circular dependency
blocks the move. Model it on `MintPlayer.Spark.Messaging.Abstractions`: plain `Microsoft.NET.Sdk`,
no `PackageReference`, no `ProjectReference`.

*Why extract at all.* `MintPlayer.Spark.Abstractions` is `Sdk="Microsoft.NET.Sdk.Web"` and genuinely
uses ASP.NET Core — three files under `Authentication/`. An entity library that wants only
`[ValueObject]` currently pays for the whole ASP.NET Core shared framework.

**The namespace is a free choice; the verification is not.** C# metadata names are namespace-based,
not assembly-based, so *keeping* `MintPlayer.Spark.Abstractions` makes the move 12 `git mv`s and 2
csproj edits — every `using` stays valid, and no consumer csproj changes because `ProjectReference`
is transitive.

*Renaming* is also cheap — a find-replace over the usings — but it has one hazard worth naming.
Thirty fully-qualified metadata strings live in the generators and analyzers
(`"MintPlayer.Spark.Abstractions.GenerateIndexAttribute"` and friends), **all thirty in code where a
miss fails silently**: the compiler does not check them, no test asserts on them, and a generator
that stops recognising an attribute simply emits nothing. A blanket replace is also wrong —
`"MintPlayer.Spark.Abstractions.Actions.ICustomAction"` shares the prefix and is not moving — so it
is 12 targeted replaces, one per moved type.

⚠️ If the namespace is renamed, verify with
`grep -rn '"MintPlayer\.Spark\.Abstractions\.' --include=*.cs libs/` and confirm `ICustomAction` is
the only survivor. With that check the risk is gone and the choice is taste: a zero-touch move, or an
assembly whose name and namespace agree.

✅ **Taken: the namespace stayed.** The move was 12 `git mv`s, and the solution built clean on the
first attempt. What did *not* stay clean is described next, and it had nothing to do with the
namespace.

#### ⚠️ Moving attributes out of an assembly can silently unreference that assembly

Splitting the attributes out broke HR's index generation, and only HR's. The mechanism is worth
knowing well beyond this change:

`GenerateIndexGenerator` finds entities in *referenced* assemblies, and first filters to references
that themselves reference `MintPlayer.Spark.Abstractions` — a cheap proxy for "this could hold Spark
entities", without which it walks the BCL. But **the C# compiler emits an `AssemblyRef` only for
assemblies a compilation actually uses.** `HR.Library` used Abstractions for attributes and nothing
else, so the moment the attributes left, `HR.Library.dll` stopped naming Abstractions at all — the
filter skipped it, and every one of its indexes vanished. The app then failed to compile on a missing
`HR.Indexes.VPerson`, several layers from the cause.

`CodeCoverage.Library` survived only by accident: it also uses Spark interfaces, so it kept its
reference. The filter now names `MintPlayer.Spark.Attributes` (still accepting the old name), which
is the assembly the attributes actually live in.

The general shape: **an assembly-name filter is a claim about what a compilation uses, and moving a
type invalidates it silently.** No error, no warning — a generator that finds nothing emits nothing.

`SparkAuthorizeAttribute` stays where it is. It inherits ASP.NET Core's `AuthorizeAttribute` — its
own doc comment records that the derivation is load-bearing and that getting it wrong fails open —
and it already lives in `MintPlayer.Spark` rather than Abstractions.

`MessageQueueAttribute` and `ReplicatedAttribute` stay in their own abstractions packages; they are
subsystem vocabulary, not model vocabulary.

**`MintPlayer.Spark.LibraryGenerators`** — the value-object generator moves here from
`MintPlayer.Spark.SourceGenerators`. Entity libraries reference this and the attributes package;
they need neither the index/actions/DI generators nor Abstractions itself.
`MintPlayer.Spark.AllFeatures.SourceGenerators` is the precedent for a second generator package: a
byte-identical csproj but for `PackageId` and `Description`, with no packing items of its own —
`MintPlayer.SourceGenerators.Tools`'s props emits the `analyzers/dotnet/roslyn4.0|4.9/cs` entries and
ships `Tools.dll` alongside.

⚠️ **Version both at `10.0.0-preview.75`**, the current wave — not `1.0.0`. CI runs a solution-wide
`dotnet pack` and pushes `nupkgs/*.nupkg` on merge to master, so a new packable project publishes
automatically and a wrong major is burned permanently.

⚠️ Entity libraries import no Spark `.targets` today, so the existing `SPARK001`–`SPARK003` reference
guards never run there. A missing LibraryGenerators reference surfaces as missing generated symbols
rather than a clean error. A guard is possible but needs new import wiring; `MintPlayer.Spark.slnx`
also lists projects explicitly and needs a line for each new package.

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
- **Startup gate**, in the style of `RowPolicyDeclarationValidator`: every `isArray` AsDetail
  element type in the model must appear in the `SparkValueObjects` registry, or startup is
  refused. This is what catches a forgotten `[ValueObject]` — the one failure mode a marker has
  that a computed set would not. The registry in N5b is what makes
  this exact across assemblies, and together they make "enforcement silently not applying"
  unrepresentable.

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

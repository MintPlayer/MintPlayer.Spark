# PRD — Row identity for AsDetail collections

**Status:** ✅ implemented, in review · **Branch:** `feat/asdetail-row-identity` · **PR:** #382 ·
**Issues:** #379, #380

All nine acceptance criteria in §5 are met — 1, 2 and 6 in a real browser (§5.1), the rest by test.

Supersedes an earlier draft (`PRD-AsDetail-Server-Lifecycle.md`, never merged, kept out-of-tree at
`~/.claude/pending-plans/asdetail-lifecycle-v1/`). That draft's central mechanism was measured to be
unsatisfiable through the shipped client. §3 records what it got wrong so none of it is re-proposed.

---

## 1. Problem

An AsDetail collection is a list of embedded objects stored inside their parent's document. Saving
the parent **replaces the whole collection**, because the mapper builds every row from scratch:

```csharp
var childEntity = Activator.CreateInstance(elementType);          // EntityMapper.cs:693
await PopulateObjectValuesAsync(childPo, childEntity, session, ct); // :697
...
AccessorCache.GetSetter(property)(entity, BuildCollection(items, propertyType, elementType)); // :703
```

Nothing survives that is not on the wire. Two consequences, one of them live in production:

**1a — Read-only embedded properties are silently wiped on every parent save.** `IsWritableBySchema`
(`EntityMapper.cs:573-580`) refuses to write a property the model marks read-only or invisible — the
correct behaviour on a client-supplied value — but the row it is refusing to write onto is a *fresh
instance*, so the stored value is not there to be preserved. On `EventColumnMapping` that is
`LastError`, `LastErrorAtUtc` and `LastFiredAtUtc`: three fields the server owns, destroyed whenever a
user edits an unrelated field on the parent board. `ShieldProtectedAttributesAsync` does not cover
this — it skips dotted names outright (`DefaultPersistentObjectActions.cs:492-493`).

**1b — A row type's own rights are fiction on the write path.** `New/X`, `Edit/X` and `Delete/X` can
be granted on an embedded type — HR's `security.json:83` grants `QueryReadEditNewDelete/CarreerJob` —
and **no code reads any of them**. `DatabaseAccess.cs:173-182` checks the parent type only. Anyone who
may edit the parent may add, alter and remove rows of a type they hold no rights to.

Both need the same missing thing: a way to say *this incoming row is that stored row*.

---

## 2. What was measured

Everything in this section was observed, not reasoned, against RavenDB server 7.1.1 with client
7.2.5. **Appendix A is the whole program and its output** — twenty lines, reproducible anywhere, and
the thing to re-run before trusting any claim below. A second repro covers the keyless case that the
appendix program structurally cannot produce (§A.2).

### 2.1 RavenDB needs no help storing a nested id

| # | Question | Measured |
|---|---|---|
| M1 | Does a nested object's `Id` survive a round trip? | **Yes, exactly.** Written `e1d5868d…` / `d309a4e6…`, read back byte-identical in a new session — Appendix A. |
| M2 | How is it stored? | An ordinary property: `{"Children":[{"Id":"39d50b43…","Value":"a"}]}`. No metadata, no special handling. |
| M5 | Is array order preserved? | **Yes**, verbatim, for 5 elements stored 1..5. |

**So persistence requires no identity mechanism at all.** A previous comment in the codebase —
"Stable key for this row, so the generic UI's inline collection editor can identify it across saves"
(`EventColumnMapping.cs:19-21`, introduced by #369 in `f5ec5068`) — describes a problem RavenDB does
not have, and attributes it to a consumer that does not exist (§2.3). The same claim recurs at
`GitHubProjectActions.cs:85-87`.

### 2.2 ⚠️ A field-initialized Guid disguises a missing key

| # | Question | Measured |
|---|---|---|
| M3 | A stored row whose JSON has **no** `Id` field — what is the key after loading? | **A brand-new random guid, different on every load.** Load A: `3e79edc2…`; load B of the same untouched document: `864812a4…`. Json.NET constructs the object (running `= Guid.NewGuid().ToString("N")`) then overwrites only properties present in the JSON. |
| M4 | Does merely loading such a document dirty it? | **Yes.** `HasChanges` is `true` before any mutation; `WhatChanged` reports `NewField Children[0]/Id '' -> '4cb105cf…'`. A `SaveChanges()` anywhere in that session writes random ids to a document nobody edited. Change vector `A:4` → `A:5`. |

**A legacy row is indistinguishable from a new one**, because both arrive carrying a plausible fresh
guid, so any check of the form `string.IsNullOrEmpty(key)` is unreachable code. And a keyless row is
not inert: it is a phantom write waiting for an unrelated `SaveChanges()` in the same session.

⚠️ This does **not** condemn the initializer — see R2, which keeps it. What it condemns is relying on
the *running system* to notice a keyless row. It cannot, ever. The check has to live in the database,
before the property ships.

### 2.3 The key does not round-trip through the client

| # | Where | Measured |
|---|---|---|
| M6 | Server → wire | **Works.** `EntityMapper.cs:207-209` sets `po.Id` from the key property. |
| M7 | Wire → server | **Works.** `TryWriteId` (`EntityMapper.cs:583-592`) is called first in `PopulateObjectValuesAsync` (`:499`), *before* the schema gate, so the key needs no model attribute. |
| M8 | Wire → Angular → wire | **Broken.** `nestedPoToDict` (`as-detail-conversions.ts:23-38`) copies `po.attributes` and `po.breadcrumb` — **never `po.id`**. `dictToNestedPo` (`:138-146`) rebuilds it as `dict['Id'] ?? dict['id'] ?? ''`, and no shipped value object declares `Id` as a model attribute (checked `EventColumnMapping.json`, `BuildSession.json`, `ProjectColumn.json`, `CarreerJob.json`). |
| M9 | The inline editor | **Positional.** `addInlineRow` pushes `{}`; `removeArrayItem` splices by index (`spark-po-form.component.ts:691-720`). It never reads a row id. |

So every embedded row reaches the server with `id: ''`, `TryWriteId` returns immediately, and the
fresh instance keeps the guid its initializer just minted. **Stored keys and incoming keys are both
present, both plausible, and never equal.**

The repo already contains independent evidence of this: `GitHubProjectActions.cs:78-129` records that
rules were saved with `Id: ""` — "verified in the database, so **every** rule on a board shared the
same key" — and the fix shipped was to re-derive the id server-side in `OnBeforeSaveAsync`, not to
make the client return it. ⚠️ That hook runs at `DefaultPersistentObjectActions.cs:277`, *after* the
mapping at `:264`, so it cannot rescue anything the mapper decided.

### 2.4 Nothing else consumes a row key

Complete list of runtime readers of `SparkValueObjects`, by grep across `libs/`, `apps/`, `tests/`:
the mapper's two round-trip sites and the rights check. No grid, no inline editor, no change
tracking, no breadcrumb consumer. `RegisteredTypes` has zero consumers.

### 2.5 An analyzer can see a referenced type, but not where it is written

Measured with a purpose-built two-project solution plus an in-memory harness (Appendix B). The
question is whether an analyzer in the **application** project can report — and a code fix repair — a
problem in a type declared in a `*.Library` project.

| # | Question | Measured |
|---|---|---|
| M10 | Under `dotnet build`, how does a `ProjectReference` arrive? | **As a .dll.** The app's compilation held **0 `CompilationReference`s and 168 `PortableExecutableReference`s**. |
| M11 | Can the analyzer get a source location for the referenced type? | **No.** `DeclaringSyntaxReferences.Length == 0`, `Locations[0].Kind == MetadataFile`, `IsInSource == False`, `GetLineSpan().Path == null`. A diagnostic reported *at* that location renders as bare `CSC : warning XASM002` — no file, no line. |
| M12 | And under a `CompilationReference`? | **Yes, fully.** `DeclaringSyntaxReferences.Length == 1`, `Kind == SourceFile`, and the diagnostic renders as `LibA\Thing.cs(1,14)`. |
| M13 | Which does the workspace layer use? | `MSBuildWorkspace.OpenProjectAsync` on the app resolves the ProjectReference to a **`CSharpCompilationReference`** and yields the full source location. |
| M14 | Can `partial` be checked from the app side? | **No, ever.** `partial` is source-only and leaves no trace in IL. |

⚠️ **Two consequences, and they are the whole shape of R9.**

*Code fixes are an IDE-only affordance.* A `CodeFixProvider` does not run during `dotnet build` at
all, and even the diagnostic it would attach to has no file to attach to there. The prior art in
`MintPlayer.Dotnet.Tools` confirms the pattern rather than contradicting it: its one cross-project fix
(`InterfaceImplementationAnalyzer.Codefix.cs`, `AddMissingMembersToInterfaceAcrossProjects`) is
IDE-only by construction and has **no multi-project test** — all five of its tests put both types in
one document. Its analyzer skips the case outright: `if (iface.Locations.All(l => !l.IsInSource))
continue` (`:33-35`). The MapperGenerator, cited as prior art for cross-assembly diagnostics, does
none: it is a generator with no code fix, and every location it reports comes from an attribute's own
application syntax in the compiling project.

*But a located diagnostic was never what enforcement needed.* `Location.None` with
`DiagnosticSeverity.Error` still fails the build, naming the type in its message. **The location is a
convenience; the severity is the guarantee.**

⚠️ `MintPlayer.SourceGenerators.Tools`'s `SymbolExtensions.cs:55` computes `IsPartial` as
`DeclaringSyntaxReferences.Select(...).All(...)`, and `.All()` over an empty collection is **`true`** —
so every metadata type reports as `partial`. Do not use it across assemblies. (Its sibling in
`ValueComparerGenerator.cs:51` uses `FirstOrDefault()` and silently returns the opposite.)

---

## 3. What the previous draft got wrong

Recorded so it is not re-proposed, and because two of these were built and passed their tests.

**W1 — Rights enforcement keyed off a guid that never matches.** The previous branch shipped
`EnforceRowRightsAsync`, which diffs stored rows against incoming rows by key. Given M8 it can never
match anything: every incoming row takes the `New` branch and every stored row takes the `Delete`
branch, so editing one field on a board demands `New` **and** `Delete` on `EventColumnMapping`. Where
those rights are granted it rewrites every row's key on every save. **Its ten tests passed because
they construct the wire PO with `Id` set** — they tested the design, not the path.

**W2 — Fail-closed on an empty key.** The design said a stored row with no key must throw. Given M3
the key is never empty, so the guard is unreachable and the real legacy case falls through into W1.

**W3 — "Rows are already being matched by key."** Asserted in the previous PRD; false. Nothing
matched rows by anything before this work.

**W4 — Deriving the value-object set by walking the type graph.** Tried and abandoned earlier, for
reasons that still hold: a generator may only add a `partial` half to a type in its own compilation,
every Spark context lives in the application project and every entity in a library, so a
context-rooted walk cannot start where it must emit. Substitute roots answered a different question —
rooting at `[GenerateIndex]` keyed four types with no model file, one of them a row per line of source
code, while missing two persistent objects that carry no index. **The marker stays.**

---

## 4. Design

### R1 — Row identity is required, but not for persistence

Two consumers justify it, in this order of value:

1. **Merging onto the stored row** (§1a) — a live data-loss bug, fixed for every app immediately.
2. **Per-row rights** (§1b) — makes three configurable rights mean something.

Persistence needs nothing (M1). Any documentation, comment or design note claiming otherwise is
wrong and gets corrected as part of this work — including `EventColumnMapping.cs:19-21` and
`GitHubProjectActions.cs:85-87`.

*Positional matching is rejected.* Order is stable in storage (M5), but the client may reorder rows
(`[Sortable]`), insert and remove them, so position is not identity — it is only identity when nothing
happened, which is the case that needs no matching.

### R2 — The key is a `Guid.NewGuid().ToString("N")` initializer, and the migration is a prerequisite

```csharp
[ValueKey] public string Id { get; set; } = global::System.Guid.NewGuid().ToString("N");
```

A value object **always** has an id. The initializer is the simplest way to mean that, and Appendix A
shows it behaves correctly: the initializer runs during construction, the persisted value overwrites
it, and the row keeps its key forever after.

⚠️ **The exposure is rows written before the property existed, and for most of these types that is
every row.** On `master` today:

| Type | Has an `Id` now | Stored rows |
|---|---|---|
| `ProjectColumn` | yes — the GitHub option id | keyed |
| `EventColumnMapping` | yes — derived during save | keyed |
| `BuildSession` | **no** | all keyless — **production**, `Builds.Sessions` |
| `CarreerJob` | **no** | all keyless |
| `ClientSecret`, `ClientClaim` | **no** | all keyless |

For those four, adding the property means every stored row hits M3: a fresh guid on every load, two
loads disagreeing, and — M4 — the document marked dirty by the load alone, so the next unrelated
`SaveChanges()` in that session writes random ids into a `Build` nobody edited.

**So the backfill migration is not cleanup, it is a precondition of the property shipping.** After it
runs, no keyless row exists and the ambiguity never arises in the running system.

⚠️ The cost, stated plainly because it is real: with an initializer, a *missed* migration cannot be
detected in-process — every row looks keyed. Detection therefore has to happen where the raw JSON is
still visible, which is the database. The startup gate (R6) is not optional, and it **refuses to
start** rather than warning.

*The alternative was considered and rejected.* Defaulting the key to `string.Empty` and minting it
server-side would make a legacy row detectable at save time — but it weakens the invariant to "has an
id once saved", pushes a mint into the mapper, and buys a check that a correctly-run migration makes
redundant anyway.

### R3 — The key round-trips through the client under a reserved dict key

`nestedPoToDict` carries `po.id` under a reserved key; `dictToNestedPo` reads it back and drops it
from the attribute walk. **The breadcrumb is the exact precedent** — `AS_DETAIL_SELF_BREADCRUMB_KEY`
already does this, and its comment explains why it is safe: `dictToNestedPo` walks the entity type's
attributes, never the dict's keys, so a reserved key is never sent as an attribute.

Without R3 nothing else in this document works.

### R4 — Save merges onto the stored row instead of rebuilding it

For a matched row, populate onto **the stored instance** rather than a new one. Properties the schema
refuses to write then keep their stored values, because they were never lost. This fixes §1a for
every embedded type at once, with no per-type configuration.

An unmatched incoming row is still `Activator.CreateInstance` — there is nothing to merge onto.

### R5 — Per-row rights, enforced at the save

At the same point, with stored and incoming both in hand:

| Right | On | Effect when absent |
|---|---|---|
| `New/X` | a key matching no stored row | refuse |
| `Edit/X` | a matched row | **restore the stored row**, do not refuse |
| `Delete/X` | a stored key absent from incoming | refuse |

`Edit` restores rather than refusing, following `ShieldProtectedAttributesAsync`: a save that also
touches something the caller may change should still succeed, with the rest unchanged.

⚠️ **Must not consult any model flag.** Enforcement is unconditional. Gating it on a model field
would make an unhashed file a security control.

**The client already gates the buttons — on the child type's own rights.**
`spark-po-form.component.html:258/313` wrap `[+ Add]` and the trash in `canCreateDetailRow` /
`canDeleteDetailRow`, fed by `GET /spark/permissions/{entityTypeId}` for the AsDetail child type. So
`QueryReadEditNewDelete/CarreerJob` really is read today — by introspection. Only the write path
ignored it, which is the hole this closes. Two small fixes ride along, because R5 turns them from
cosmetic into user-visible:

- ⚠️ **Fail closed.** `can-create-detail-row.pipe.ts:8` returns `true` when no entry exists.
- ⚠️ **Decouple from the catalogue.** Permissions are reachable only via the entity-type list, which
  is `Query`-gated (`List.cs:28-29`). A user with `New/CarreerJob` but not `Query/CarreerJob` never
  fetches them, the fail-open default renders the button, and R5 then refuses the save. Fetch
  `getPermissions` directly from `attr.asDetailType` instead of via `types.find`.
- The refusal surfaces as **404** (deliberate, anti-oracle), and `spark-po-edit.component.ts:177-188`
  only special-cases 400 and reads `error.message` rather than `error.error?.error` as the load path
  does — so the user sees a raw Angular string in the validation summary and **the whole save is
  discarded**. One line.

⚠️ **`Edit`'s restore is invisible, and that is a defect in this rule, not just its UI.** A user
lacking `Edit/{RowType}` gets editable inputs, a success message, and vanished edits. Gating the
inputs is `PRD-AsDetail-Row-Edit-Affordances.md`; making a restore *visible* belongs here, because
rights can change between load and save and a caller can post directly. Settle it when implementing
R5 — at minimum the save response must say which rows were restored.

### R6 — Legacy rows: migrate before the property ships, and gate on it

R2 keeps the `Guid` initializer, so a keyless row **cannot** be recognised once it is in memory — it
arrives looking perfectly keyed. Detection has to happen where the raw JSON is still visible.

- A **backfill migration** per app, stamping a guid into every keyless embedded row
  (`PatchByQueryOperation`). It is idempotent, so the count it touches is itself the signal.
  ⚠️ Four of the six value objects gain a brand-new `Id`, and for those the migration rewrites
  **every stored row** — including `Builds.Sessions` in production.
- A **startup gate** that queries for documents still holding keyless rows and **refuses to start**
  if any remain. Not a warning: per M4, an unmigrated deployment does not merely mismatch, it writes
  random ids into documents nobody edited, on the first unrelated `SaveChanges()` in the session.
- The gate also compares the model's `isArray` element types against `SparkValueObjects`, so a type
  that was never marked is caught at boot. ⚠️ It must verify the key actually **round-trips**, not
  merely that the type is registered — M6–M8 is exactly the case where registration was fine and the
  round trip was not.

There is deliberately **no per-save keyless check**. It would be unreachable code (M3), and pretending
otherwise is what W2 got wrong.

### R7 — `[ValueObject]` / `[ValueKey]`, and the two packages

Carried over unchanged from the previous branch, where they were built and tested and are the part
that held up:

- `[ValueObject]` on the class, found with `ForAttributeWithMetadataName`; `[ValueKey]` names an
  existing key so nothing is generated for it. Both are needed: `ProjectColumn` (a GitHub option id)
  and `EventColumnMapping` (derived during save) carry real keys that must be registered, and an
  earlier implementation that skipped any type declaring an `Id` dropped both from the registry —
  where absent is not neutral, it is unjudgeable.
- `MintPlayer.Spark.Attributes` (the 12 model attributes plus these two, zero dependencies) and
  `MintPlayer.Spark.LibraryGenerators`. Namespace stays `MintPlayer.Spark.Abstractions`.
- The registry stores the key's **property name** beside its accessor, because an accessor can only
  read and R3 needs to write.
- SPARK016 for a decorated non-partial type with no `[ValueKey]`.

⚠️ **Version: bump every package, in this PR.** `preview.75` is **already published**
(`db6f3cc0`, on master). CI packs solution-wide and pushes with `--skip-duplicate`
(`dotnet-build-master.yml:82-83, 148-149`), so shipping the split at `preview.75` publishes the two
new packages fine and then **silently keeps the old `MintPlayer.Spark.Abstractions` — the one that
still physically contains all 12 attribute types.** A consumer with both then gets CS0433. Worse for
the generators: `GetTypeByMetadataName` returns `null` on a duplicate declaration, so
`GenerateIndexGenerator.cs:108`/`:459`, `HostTranslationsAggregatorGenerator.cs:30`,
`ProjectionPropertyAnalyzer.cs:30,54` and `AttributeDescriptionsGenerator.cs:82` all switch off and
indexes vanish with no diagnostic.

🚫 **`[TypeForwardedTo]` was added and then removed** (`19c4c4b2`), and the reasoning is worth
keeping because it is finely balanced. The break is not one a consumer can see — the attributes are
read by *runtime reflection* (`ModelSynchronizer.cs:684-686`, `ReferenceResolver.cs:15`), so a
pre-built entity assembly against the new Abstractions yields a **wrong model** rather than an error,
which is exactly the failure class forwarders exist for. It came out because there is no
backward-compatibility requirement at all here: forwarders serve a consumer nobody is promising
anything to. If that ever changes, twelve one-line forwards restore it.

⚠️ **Carry the `GenerateIndexGenerator` fix with it.** Moving the attributes out of Abstractions
silently broke HR's index generation: the generator filters referenced assemblies to those
referencing Abstractions, and the C# compiler emits an `AssemblyRef` only for assemblies a
compilation actually *uses*, so `HR.Library` — which used Abstractions for attributes and nothing else
— stopped referencing it and was skipped without a word. Two test fixtures had the same dependency and
need `TranslatedString` named explicitly.

⚠️ **That was not bad luck.** HR was the *only* library using Abstractions for nothing but attributes;
`CodeCoverage.Library` survived because it also touches `TransientLookupReference`. Prefer deriving
the filter from the resolved symbol —
`compilation.GetTypeByMetadataName(GenerateIndexAttributeFullName)!.ContainingAssembly.Name`, ∪ the
legacy name for pre-split binaries — over asserting a literal pair, so a future third attribute host
cannot repeat it. `tests/.../Generators/ReferencedAssemblyEntityTests.cs:18-28` already models this
exact shape and is the gate to run.

**This split frees exactly one project of four.** The other three entity libraries still reach
Abstractions for `TranslatedString`, `TransientLookupReference`, `DynamicLookupReference` and
`ELookupDisplayType`, and `Replication.Abstractions` pulls it in for Fleet and HR regardless.
Finishing that job is `PRD-Entity-Library-Dependency-Split.md`; it is deliberately not in scope here,
where the attributes move only because `[ValueObject]` needs a home.

### R8 — Completeness: an analyzer in the application, rooted at `SparkContext`

R7's marker answers "is this a value object?" but cannot answer "did you forget one?" — nothing in an
entity library knows which of its types the model actually reaches. The application does: its
`SparkContext` subclass is the root of the whole object graph.

**The walk that failed as a generator succeeds as an analyzer.** It was abandoned earlier because a
generator may only emit into its own compilation, and contexts and entities live in different
assemblies. An analyzer emits nothing, so that constraint does not apply.

```
find the class extending SparkContext
  → its IRavenQueryable<T> properties → T                        (the document roots)
  → recursively: for each property whose type is a class, and each
    collection whose element type is a class, require [ValueObject]
```

Rooting at the context is also strictly better than the `[GenerateIndex]` proxy root tried on the
abandoned branch, which keyed four types with no model file — one of them a row per line of source
code — while missing two persistent objects that carry no index. The context roots the *model*, which
is the actual question.

⚠️ **Severity is the enforcement; the location is a convenience.** Per M10–M13, under `dotnet build`
the referenced type has no source location at all, so the diagnostic is reported with
`Location.None` and renders as `CSC : error SPARK017: …`. That still fails the build, which is all
the invariant requires. In the IDE the same analyzer receives a `CompilationReference`, gets a real
location, and a `CodeFixProvider` can offer to add `partial` and `[ValueObject]` to the library file.

The code fix is therefore **an affordance, never the mechanism**. It cannot run in CI (M11), and the
one cross-project fix in the prior art has no multi-project test at all. The build-failing diagnostic
is what guarantees the invariant; the fix just saves typing while the solution is open.

⚠️ **The analyzer must not check `partial`** (M14): it is source-only and invisible in metadata, and
the obvious helper silently answers `true` for every metadata type. Partiality is the *library-side*
generator's question, which it already answers correctly as SPARK016 in the compilation that can see
the syntax.

The division of labour, with each half asking only what its own compilation can answer:

| | Runs in | Sees | Answers |
|---|---|---|---|
| `LibraryGenerators` generator | the entity library | syntax | is it `partial`? emit `Id`, register (SPARK016) |
| `SourceGenerators` analyzer | the application | metadata + the context | is anything reachable **missing** `[ValueObject]`? (SPARK017) |

### R9 — Non-goals

Each has a PRD of its own, written and not started, so "deferred" means scheduled rather than
forgotten:

| Deferred | Why not here | Document |
|---|---|---|
| Round-tripping New/Delete clicks to the row type's hooks | needs R3 and R4 first; without them it rebuilds W1 behind a nicer endpoint | `PRD-Server-Side-Row-Lifecycle.md` |
| Disabling a row's inputs when `Edit/{RowType}` is absent | client-only, ~10 bindings, and it follows from R5 rather than blocking it | `PRD-AsDetail-Row-Edit-Affordances.md` |
| Freeing entity libraries from ASP.NET Core | the attributes move here because `[ValueObject]` needs a home; the rest is an optimisation | `PRD-Entity-Library-Dependency-Split.md` |


- Server round-tripping of New/Delete clicks to the row type's hooks (the previous draft's N1/N2/N3).
  It is a separate feature, it depends on everything above, and it is not what makes the rights real.
- Changing how embedded rows are stored, ordered, or rendered.
- `SparkAuthorizeAttribute` moving packages — it inherits ASP.NET Core's `AuthorizeAttribute`, where
  getting the derivation wrong fails open.

---

## 5. Acceptance criteria

1. A parent save that edits one field of one embedded row leaves that row's read-only properties
   (`LastError`, `LastErrorAtUtc`, `LastFiredAtUtc`) **unchanged**, verified against a real save
   through the Angular client, not only through a constructed PO.
2. An embedded row's key is present in the payload the browser sends back — verified by inspecting a
   real request, since this is exactly what W1 got wrong.
3. With `New/X` withheld, adding a row is refused; with it granted, adding succeeds and every other
   row keeps its key.
4. With `Delete/X` withheld, removing a row is refused.
5. With `Edit/X` withheld, editing a row succeeds and the stored content is unchanged.
6. An unchanged collection saved twice produces **no** change vector bump on the embedded rows.
7. The backfill migration exists, is idempotent, and running it twice touches zero rows the second
   time.
8. With a keyless row left in the database, the application **refuses to start**, naming the
   collection — verified by planting one, since this cannot be detected once loaded (M3).
9. A build fails with SPARK017 when a type reachable from `SparkContext` is used as an embedded
   collection element without `[ValueObject]` — verified by `dotnet build`, not only in the IDE,
   because the IDE is the easy half (M11).

⚠️ Criteria 1, 2, 6 and 9 all exist because a green test suite proved nothing last time. Each names
the environment the check must run in — the browser, or a command-line build — because in every case
the environment the previous attempt tested was the one where the mechanism happened to work.

### ✅ Verified in a browser, 2026-09-09

Against the HR demo, `Person.Jobs` (three `CarreerJob` rows), through the real Angular client — edit
a field on one row, save, and read the database directly.

| # | Result |
|---|---|
| 1 | Row 0's `ContractStart` changed; **all three rows kept their original keys**, and rows 1 and 2 were untouched. |
| 2 | The `PUT` body carried `id` on **every** nested row (`…Jobs0`, `…Jobs1`, `…Jobs2`) — captured from the request, not inferred. |
| 6 | Saving an unchanged collection left the change vector **and** last-modified byte-identical (`A:8491830295`). The real edit then moved it by exactly one. |

Two incidental confirmations. The row keys in production shape were
`Peoplea79d8d71cd604f6b9dcde64d03ed40b8Jobs0` — the backfill migration's derived format, so it ran at
startup against a real database and worked on real data. And criterion 6 is the sharpest of the
three: under the old rebuild-from-scratch behaviour every save minted fresh guids for every row, so
an unchanged collection could not have left the change vector alone.

⚠️ **What this does not cover.** `CarreerJob` has no read-only attribute in HR's model, so this
demonstrates row *matching*, not read-only preservation. That is covered twice over:
`AsDetailStoredRowMergeTests` asserts the matched row is the **same instance** rather than a copy
carrying the same values — the property that makes preservation hold for every read-only field
rather than the ones a test happens to name — and
`AsDetailRowIdentityRoundTripTests.A_read_only_field_survives_an_edit_to_its_row` proves it end to
end, through `SavePersistentObjectAsync` and back out of RavenDB.

### ⚠️ A preserved key proves nothing on its own

The sharpest trap in this whole design, found while writing the round-trip tests, and it invalidates
the most obvious way to check the work.

`EntityMapper.TryWriteId` writes the payload's key onto the row **on both paths** — the merged stored
instance and a freshly built one. So a row that was rebuilt from scratch is *indistinguishable by its
key* from a row that was merged. Every "the keys were preserved" assertion, the browser check in the
table above included, is really testing that the key round-trips and gets written back. It says
nothing about whether R4's merge happened.

**Only a field the payload does not carry separates the two.** That is why criterion 1 is the load-
bearing one and why it names a read-only property, and it is why the round-trip test above is worth
its cost even though six of its seven assertions duplicate cheaper tests.

### ⚠️ One session is not one request

`SparkEndpointFactory.GetService<T>()` resolves from the **root** provider, but
`IAsyncDocumentSession` — and everything over it, `IDatabaseAccess` included — is *scoped*. A test
that resolves one and reuses it therefore runs every save through a single Raven session, and a Raven
session has an identity map: the update path is handed the instance loaded during the create, so a
document changed out of band in between is simply invisible.

Written that way, the read-only test above **failed against entirely correct mapper code** — the
merge faithfully preserved what its stale `existing` held, which was the pre-stamp value. Use
`SparkEndpointFactory.CreateScope()` to model a second request. This is a fixture hazard, not a
product one: real requests get a scope each.

⚠️ **A trap worth recording for anyone repeating this.** The first attempt edited
`input[type=date] >> nth=0`, which is `DateOfBirth` on the General tab — not a job row, which lives
on a collapsed `Employment` tab and reports `visible: false` until it is opened. The save succeeded,
the payload looked plausible, and the assertion under test was never exercised. Scope the selector to
the grid (`table input[type=date]`) and open the tab first.

---

## 5.2 Fixed along the way

Both were found by using the feature rather than by testing it, and both are pre-existing — neither
was introduced here.

**The reference picker rendered no selectable rows.** Adding a `Job` row and opening the `Profession`
picker threw `can't access property "find", item.attributes is undefined` on every cell.
`reference-attr-value.pipe.ts` read `item.attributes`, which is the `PersistentObject` shape — but a
picker's rows are `QueryResultItem`s, carrying `values` (`[{ key, value, breadcrumb }]`) and no
`attributes` at all. Latent since #155; the row shape changed under it in #327, whose M13 added
`valueFor` for exactly this reason ("a renderer reused across a grid and an AsDetail table sees two
[shapes]") — this pipe was simply missed in that sweep. Now goes through `valueFor`, so
`QueryResultItem` is untouched and the pipe also works if a picker is ever pointed at an AsDetail
sub-table.

Checked for siblings: four other pipes still read `.attributes` directly (`arrayValue`,
`attributeValue`, `rawAttributeValue`, `referenceChips`), but all four are used only from
`spark-po-detail.component.html`, which renders `PersistentObject`s. `referenceAttrValue` was the
only one pointed at a query row.

**`isVisible` was outside the model hash.** Every field that gates a write must be structural, or a
change to it does not register as model drift. Added to `ModelFileShape.StructuralAttributeFields`. The question arrived the other way round — whether
`isReadOnly` should come *out*, since a synchronize has no business changing it. It stayed: every
field that gates a write is structural, and a future SparkEditor that toggles one recomputes the hash
anyway. Auditing the list against that rule is what turned up `isVisible`, which was the actual hole.

---

## 6. Risks

| Risk | Handling |
|---|---|
| The client change touches every AsDetail form | Reserved-key precedent already exists and is commented; covered by the ng-spark test suite. |
| Merging onto stored rows changes save semantics for every embedded type | It only *adds* preservation of values that were previously destroyed. No value that was written before stops being written. |
| Apps with existing keyless data | R6: migration first, then fail closed. Spark's own data is 100% keyless. |
| Breaking change for consumers | Packages are preview-grade and there is no compatibility requirement, so no `[TypeForwardedTo]`. ⚠️ Note the failure mode if that ever changes: these attributes are reflected at runtime, so a stale consumer gets a *wrong model*, not a load error. |

---

## Appendix A — the repro

✅ **These findings are now pinned by tests**, in
`tests/MintPlayer.Spark.Tests/Services/NestedRowIdentityTests.cs` (`SparkTestDriver`, real embedded
RavenDB). The console programs below are how the behaviour was *discovered*; the tests are what keeps
it true. Three cases: a stored row keeps its key (M1), a row stored without one is given a different
key on every load (M3), and loading such a document marks it dirty (M4).

⚠️ The second and third tests assert on behaviour we do **not** want. If they ever start failing,
that is not a regression to fix by adjusting the assertion — it means the constraint that forces the
backfill migration has gone away, and R2/R6 can be revisited.

### A.1 A nested `Id` round-trips exactly (M1, M2, M5)

The whole program. `Address.Id` is initialized the way the abandoned design generated it, which is
what makes this the relevant experiment rather than a toy.

```csharp
using Newtonsoft.Json;
using Raven.Client.Documents;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

using var store = new DocumentStore
{
    Urls = ["http://localhost:8080"],
    Database = "TestDb"
};
store.Initialize();

// Create the database when it is not there yet. CreateDatabaseOperation is not idempotent -- it
// throws rather than no-opping -- so "if not exists" is a catch, not a flag.
try
{
    store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord("TestDb")));
}
catch (ConcurrencyException)
{
    // Already exists.
}

using (var session1 = store.OpenSession())
{
    foreach (var item in session1.Query<Person>())
    {
        session1.Delete(item);
    }
    Person newPerson = new()
    {
        FirstName = "Pieterjan",
        LastName = "De Clippel",
        Addresses =
        [
            new() { Street = "Deinzestraat", Number = "231" },
            new() { Street = "Abdijsteeg", Number = "30" },
        ]
    };
    session1.Store(newPerson);
    session1.SaveChanges();
    Console.WriteLine(JsonConvert.SerializeObject(newPerson, Formatting.Indented));
}

using (var session2 = store.OpenSession())
{
    var people = session2.Query<Person>().ToArray();
    Console.WriteLine(JsonConvert.SerializeObject(people, Formatting.Indented));
}

class Person
{
    public string Id { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public List<Address> Addresses { get; set; } = [];
}

class Address
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Street { get; set; }
    public string Number { get; set; }
}
```

Output — first block is what was **written**, second is what a **new session read back**:

```json
{
  "Id": "people/6-A",
  "FirstName": "Pieterjan",
  "LastName": "De Clippel",
  "Addresses": [
    { "Id": "e1d5868d333e4ae79ec9dca0ddad5d58", "Street": "Deinzestraat", "Number": "231" },
    { "Id": "d309a4e676ee4b4ea75754472d5a9d90", "Street": "Abdijsteeg",   "Number": "30"  }
  ]
}
[
  {
    "Id": "people/6-A",
    "FirstName": "Pieterjan",
    "LastName": "De Clippel",
    "Addresses": [
      { "Id": "e1d5868d333e4ae79ec9dca0ddad5d58", "Street": "Deinzestraat", "Number": "231" },
      { "Id": "d309a4e676ee4b4ea75754472d5a9d90", "Street": "Abdijsteeg",   "Number": "30"  }
    ]
  }
]
```

Byte-identical, including array order. **RavenDB needs no help storing a nested id**, so no part of
this design may be justified by persistence.

⚠️ Note what this program **cannot** show. Every `Address` it creates runs the field initializer, so
there is no way to produce a row whose stored JSON lacks `Id` — which is exactly the case that
breaks. That needs a raw `PUT`, below.

### A.2 A row whose JSON has no `Id` (M3, M4)

Write the document directly, bypassing the model, so the nested objects carry `Value` and no `Id`:

```json
{"Children":[{"Value":"legacy-one"},{"Value":"legacy-two"}],
 "@metadata":{"@collection":"Parents","Raven-Clr-Type":"Parent, idtest"}}
```

Then load it twice, each time through a fresh store and session:

```
load A ids: 3e79edc2c3af466eb2a0358707a9ef73, 3fa2f1fcde174630af137e52e91b7cf6
load B ids: 864812a41a9e4080b6235a943cd68511, 76a0ecc65e2645e9a40d064757662e84
load A == load B: False
```

And the same document, loaded once and **not mutated**:

```
HasChanges before SaveChanges: True
WhatChanged: parents/legacy-1: NewField Children[0]/Id '' -> '4cb105cfb92546ba940221328b78d06a'
             parents/legacy-1: NewField Children[1]/Id '' -> '545671d2ce104a0dbee89fe6c3500fc8'
```

After `SaveChanges()` the ids are persisted and the change vector goes `A:4` → `A:5`. The row is
self-healing after one save — but that save is a phantom write of random values to a document nobody
edited, triggered by any unrelated `SaveChanges()` in the same session.

This is R2's entire justification.

### A.3 Cross-assembly analyzer reach (M10–M14)

Two projects, `LibA` declaring `public class Thing { }` and `AppB` referencing it by
`ProjectReference`, plus an analyzer on `AppB` that resolves `Thing` and probes it.

**`dotnet build AppB`:**

```
CSC : warning XASM001: PROBE || assembly=LibA || DeclaringSyntaxReferences.Length=0
  || Locations.Length=1 || Locations[0].Kind=MetadataFile || Locations[0].IsInSource=False
  || GetLineSpan().Path=(null) || compilationRefCount=0 || metadataRefCount=168
CSC : warning XASM002: reported-at-symbol-location (kind=MetadataFile)
```

`XASM002` was deliberately reported *at* the symbol's own location and still rendered with no file
and no line — the prefix is literally `CSC :`.

**The same symbol, in memory, differing only in reference kind:**

```
=== CASE 1: compilationA.ToMetadataReference()  [CompilationReference] ===
  DeclaringSyntaxReferences.Len : 1
  Locations[0].Kind             : SourceFile
  Diagnostic.ToString()         : C:\...\LibA\Thing.cs(1,14): warning XASM900: probe

=== CASE 2: MetadataReference.CreateFromImage(emitted dll) ===
  DeclaringSyntaxReferences.Len : 0
  Locations[0].Kind             : MetadataFile
  Diagnostic.ToString()         : warning XASM900: probe
```

**And through the workspace layer the IDE is built on:**

```
MSBuildWorkspace.OpenProjectAsync(AppB.csproj) → GetCompilationAsync()
  CompilationReference count in compilation: 1  -> LibA (CSharpCompilationReference)
  Locations[0].Kind : SourceFile
  Diagnostic        : C:\...\LibA\Thing.cs(1,14): warning XASM901: probe
```

⚠️ Remaining inference: that Visual Studio and C# DevKit use this same workspace path in the live
editor. That is the standard architecture and `MSBuildWorkspace` was measured, but a running IDE was
not observed.

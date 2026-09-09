# PRD — Row identity for AsDetail collections

**Status:** draft · **Branch:** `feat/asdetail-row-identity` · **Issues:** #379, #380

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

Everything in this section was observed, not reasoned. The RavenDB findings come from a standalone
repro at `C:\Repos\idtest` (net10.0 console, RavenDB.Client 7.2.5, server 7.1.1 at localhost:8080),
which is left in place and re-runnable.

### 2.1 RavenDB needs no help storing a nested id

| # | Question | Measured |
|---|---|---|
| M1 | Does a nested object's `Id` survive a round trip? | **Yes, exactly.** Stored `39d50b43…`, loaded `39d50b43…` through a fresh `IDocumentStore` and session. |
| M2 | How is it stored? | An ordinary property: `{"Children":[{"Id":"39d50b43…","Value":"a"}]}`. No metadata, no special handling. |
| M5 | Is array order preserved? | **Yes**, verbatim, for 5 elements stored 1..5. |

**So persistence requires no identity mechanism at all.** A previous comment in the codebase —
"Stable key for this row, so the generic UI's inline collection editor can identify it across saves"
(`EventColumnMapping.cs:19-21`, introduced by #369 in `f5ec5068`) — describes a problem RavenDB does
not have, and attributes it to a consumer that does not exist (§2.3). The same claim recurs at
`GitHubProjectActions.cs:85-87`.

### 2.2 ⚠️ A field-initialized Guid is worse than no key

| # | Question | Measured |
|---|---|---|
| M3 | A stored row whose JSON has **no** `Id` field — what is the key after loading? | **A brand-new random guid, different on every load.** Load A: `3e79edc2…`; load B of the same untouched document: `864812a4…`. Json.NET constructs the object (running `= Guid.NewGuid().ToString("N")`) then overwrites only properties present in the JSON. |
| M4 | Does merely loading such a document dirty it? | **Yes.** `HasChanges` is `true` before any mutation; `WhatChanged` reports `NewField Children[0]/Id '' -> '4cb105cf…'`. A `SaveChanges()` anywhere in that session writes random ids to a document nobody edited. Change vector `A:4` → `A:5`. |

This is the single most important finding in this document. **The initializer that was supposed to
guarantee identity is exactly what disguises its absence.** A legacy row is indistinguishable from a
new one, because both arrive carrying a plausible fresh guid. Any check of the form
`string.IsNullOrEmpty(key)` is unreachable code.

It also means a keyless row is not inert: it is a phantom write waiting for an unrelated
`SaveChanges()` in the same session.

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

### R2 — ⚠️ The generated key defaults to empty, and the server mints it

```csharp
// NOT this:
public string Id { get; set; } = global::System.Guid.NewGuid().ToString("N");
// but this:
public string Id { get; set; } = string.Empty;
```

This is the correction that makes everything else possible. With an empty default:

- a **stored** row with an empty key is genuinely a legacy row, detectably (M3 no longer applies);
- an **incoming** row with an empty key is genuinely a new row;
- loading a legacy document no longer dirties it, so the phantom write in M4 disappears.

The server mints the key, in the mapper, at the one moment it can tell the two apart: an incoming row
whose key is empty **and** which therefore matches no stored row is new, and gets a fresh guid.

⚠️ The cost is that a row constructed in application code — a hook adding a row server-side — starts
keyless. The mapper mints on the save path, so this is only visible to code that inspects the key
before saving. Documented in the migration notes rather than solved by re-adding an initializer.

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

### R6 — Legacy rows: migrate, then fail closed

With R2 in place a stored empty key is unambiguous, so:

- a **backfill migration** per app, stamping a guid into every keyless embedded row
  (`PatchByQueryOperation`); Spark's own production data is 100% keyless today;
- **fail closed** afterwards — a stored keyless row makes the save throw, naming the document, the
  type and the migration;
- a **startup gate** comparing the model's `isArray` element types against `SparkValueObjects`, so a
  type that was never marked is found at boot rather than at save.

⚠️ The gate must also verify the key actually **round-trips**, not merely that the type is registered
— M6–M8 is precisely the case where registration was fine and the round trip was not.

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

⚠️ **Carry the `GenerateIndexGenerator` fix with it.** Moving the attributes out of Abstractions
silently broke HR's index generation: the generator filters referenced assemblies to those
referencing Abstractions, and the C# compiler emits an `AssemblyRef` only for assemblies a
compilation actually *uses*, so `HR.Library` — which used Abstractions for attributes and nothing else
— stopped referencing it and was skipped without a word. Two test fixtures had the same dependency and
need `TranslatedString` named explicitly.

### R8 — Non-goals

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
7. A stored keyless row fails the save with a message naming the migration — and the migration
   exists.
8. Loading a document with keyless embedded rows does not mark it dirty.

⚠️ Criteria 1, 2 and 6 exist because the previous attempt passed its unit tests while being broken in
the browser. At least one end-to-end check through the real client is required before this is called
done.

---

## 6. Risks

| Risk | Handling |
|---|---|
| The client change touches every AsDetail form | Reserved-key precedent already exists and is commented; covered by the ng-spark test suite. |
| Merging onto stored rows changes save semantics for every embedded type | It only *adds* preservation of values that were previously destroyed. No value that was written before stops being written. |
| Apps with existing keyless data | R6: migration first, then fail closed. Spark's own data is 100% keyless. |
| Breaking change for consumers | Packages are preview-grade; breaking changes are acceptable and no `[TypeForwardedTo]` is needed. |

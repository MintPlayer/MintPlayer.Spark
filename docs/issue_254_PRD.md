# PRD — Issue #254: `[IgnoreProperty]` — exclude a property from the model

**Status:** Implemented — all 7 milestones done (see [plan](issue_254_plan.md))
**Issue:** [#254](https://github.com/MintPlayer/MintPlayer.Spark/issues/254)
**Branch:** `feat/issue-254-ignore-property-attribute`

## Problem

There is no way to tell Spark that a public read/write property on an entity is not part of the
model. Vidyano has `IgnorePropertyAttribute`; Spark ships nothing equivalent.

`ModelSynchronizer` reflects over entity properties with one convention-based predicate, repeated
**verbatim** at three sites (`ModelSynchronizer.cs:191-192`, `:283-285`, `:288-291`):

```csharp
entityType.GetCachedProperties().Where(p => p.Name != "Id" && p.CanRead && p.CanWrite)
```

No custom attribute is inspected anywhere, so the only exclusions available today are: name the
property `Id`, make it non-public, or drop its getter/setter. `[JsonIgnore]` does **nothing** — a
real trap for anyone arriving from EF or `System.Text.Json`.

The existing near-misses all fail for a *stored* property that needs a public setter:
`"isVisible": false` and `"isReadOnly": true` block write-back but **still serialize the value to
the client** (`PopulateAttributeValues` has no visibility check); `GetProtectedAttributesAsync` is
per-row runtime authorization, not a static declaration. Hand-editing the JSON does not work either
— `newAttributes` is rebuilt from scratch each sync (`:300`, assigned `:471`).

## Investigation findings (three-spike sweep, 2026-08-16)

### F1 — One predicate, three copies, one obvious seam

The three filters are byte-identical, and `GetCachedCustomAttribute<T>` already exists with
negative caching (`ReflectedTypeExtensions.cs:50-57`). A single shared predicate is the natural
fix and removes the copy-paste at the same time.

**Critically, the filter must be shared between `CollectEmbeddedTypes` (`:191`) and
`CreateOrUpdateEntityTypeDefinition` (`:283`).** If only the attribute builder filtered, an ignored
complex property would still be *discovered* as an embedded type and get a `{Type}.json` file with
no attribute referencing it.

### F2 — Removal from committed JSON already works, for free

`newAttributes` is built solely from the reflected property names (`:303`); an existing attribute is
carried over **only** inside that loop (`:439`); `:471` replaces the array wholesale; the file is
fully overwritten (`:116`). So the moment a property stops being enumerated, its attribute block is
physically deleted from the JSON on the next sync. **AC #2 needs no new code — only a test.**

Consequence to document: removal discards everything hand-authored on that attribute (id GUID,
translated label, rules, renderer, group). Re-adding the property later regenerates it with a
**new** `Id` GUID (`:446`).

### F3 — The model JSON is committed; demo diffs are real

All 23 demo model files are tracked by git (`Demo/{DemoApp,Fleet,HR,WebhooksDemo}/*/App_Data/Model/*.json`).
Any demo entity we decorate produces a JSON diff that must be committed alongside.

### F4 — Many runtime paths bypass the schema entirely

Fixing the synchronizer covers `EntityMapper` for free — the read path iterates `po.Attributes`
(`:213-262`) and the write path is gated by `IsWritableBySchema` (`:550-559`), which refuses
attributes with no schema entry. AsDetail recursion resolves embedded children through their own
generated JSON, so embedded types are covered on both read and write.

But these sites **reflect over CLR properties directly and would still leak an ignored property**:

| Site | Leak |
|---|---|
| `ReferenceResolver.cs:56-62`, `:66-86` | An ignored `[Reference]` property is still `.Include()`d into the RavenDB query — pointless loads, and the referenced document lands in the session for a field the client must never see. |
| `SyncActionHandler.cs:135-151` | The explicit "no registered `EntityTypeDefinition`" fallback builds a PO attribute per CLR property, values included. Cannot inherit the synchronizer's exclusion. |
| `LookupReferenceService.cs:289-299` | Reflects every extra property on a transient lookup item into the wire DTO's `Extra` dictionary. No schema gate at all. |
| `SyncActionInterceptor.cs:91-95`, `:186-192`; `SyncAction.cs:99-104` | Replication transmits the value cross-module, and `GetPropertyNames` is the **write-authorization list** the owner module honours. Security-relevant. |
| `PersistentObjectNamesGenerator.cs:49-67` | `AttributeNames.Person.Secret` keeps compiling against an attribute the model no longer has. |
| `ProjectionPropertyAnalyzer.cs:57-62` | Raises SPARK type-mismatch diagnostics for a property that is no longer part of the model. |

### F5 — The raw-JSON write hole (`EntityMapper.cs:874-902`)

`SetPropertyValue` hands a client-supplied JSON object/array straight to `System.Text.Json` with
`PropertyNameCaseInsensitive = true` when the target CLR property is complex and the attribute is
not a `PersistentObjectAttributeAsDetail`. There is **no per-child schema gate on that path**, so an
ignored property on an *embedded* type remains writable through it. If `[IgnoreProperty]` is to mean
anything on embedded types, the serializer must honour it too.

### F6 — Breadcrumbs fail loudly, which is correct but needs a good message

`ValidateBreadcrumb` (`:497-521`) runs after removal and throws if a `{Placeholder}` names an
attribute that no longer exists. Ignoring a property used in a breadcrumb template will therefore
hard-fail synchronize. That is the right behaviour — it just needs to say *why*.
`SynthesizeDefaultBreadcrumb` (`:489-494`) and `GetDefaultSortProperty` (`:690-698`) also pick from
the new attribute list, so ignoring `Name` shifts defaults.

## Decisions

- **D1 — One shared predicate, in `Abstractions.Reflection`.** Add
  `PropertyInfo.IsSparkModelProperty()` and `Type.GetSparkModelProperties()` next to the existing
  cached-reflection helpers, encapsulating the whole rule (`!= "Id"`, `CanRead`, `CanWrite`, no
  `[IgnoreProperty]`). All sites call it. Rejected: filtering inside `GetCachedProperties()` itself
  — that primitive is also used on `SparkContext`, `Task<T>` and the `IsComplexType` heuristic,
  where entity-model semantics are simply wrong.

- **D2 — Ignore is a union-wide veto.** A property is excluded if the attribute is present on the
  entity property **or** on the projection property. Filtering each side independently is not
  enough: the two name sets are unioned (`:294-297`), so ignoring on the entity alone would let the
  property back in through the projection half.

- **D3 — `IsComplexType` stays unfiltered.** Filtering it would make a type whose every property is
  ignored look like a scalar and change how it is mapped. The edge case (an empty embedded type) is
  cosmetic; mis-typing a property is not.

- **D4 — Honour the attribute in `System.Text.Json` too (F5).** A `DefaultJsonTypeInfoResolver`
  modifier that drops `[IgnoreProperty]` properties, applied to the mapper's serializer options.
  This closes the embedded-type write hole and keeps CLR-level serialization consistent with the
  model. Without it the attribute is advisory rather than enforced.

- **D5 — Replication is in scope.** `GetPropertyNames` (`SyncActionInterceptor.cs:186-192`) is the
  list of fields the owner module is told it may write. Leaving it unfiltered would mean
  `[IgnoreProperty]` silently does not apply cross-module — the opposite of what the name promises.

- **D6 — Generators are in scope.** `AttributeNames.<Entity>.<Ignored>` must stop being emitted,
  otherwise existing code keeps compiling against a constant that no longer resolves to anything.

- **D7 — No new "inconsistent projection" diagnostic.** The analyzer will skip ignored properties,
  but flagging `[IgnoreProperty]` on one side of an entity/projection pair and not the other is left
  out: D2 already makes the mismatch harmless. Revisit if it confuses anyone in practice.

- **D8 — Orphan embedded-type JSON files are left alone.** If ignoring the last property that
  referenced a complex type means that type is no longer discovered, its `{Type}.json` lingers (only
  *projection* files are cleaned up, `:174-186`). Deleting embedded model files on sight risks
  destroying hand-authored definitions for types that are temporarily unreferenced. Documented, not
  automated.

- **D9 — Name it `IgnorePropertyAttribute`.** Matches Vidyano, so the concept transfers for this
  team. `[Ignore]` is too broad next to `[Reference]`/`[Sortable]`; `[NotMapped]` would wrongly
  imply EF semantics.

## Acceptance criteria

1. A public get/set property marked `[IgnoreProperty]` never appears in a freshly generated model JSON.
2. An attribute already present in a committed model JSON is **removed** on the next sync once the
   property is marked (F2 — behaviour exists; pin it with a test).
3. The property is not populated onto the wire `PersistentObject` and is not written back from
   client input.
4. Works on embedded / value-object types as well as entity roots — including through the raw-JSON
   write path (F5).
5. An ignored complex property does not drag its type into the model as a new embedded type (F1).
6. An ignored `[Reference]` property is not `.Include()`d by `ReferenceResolver`.
7. Replication neither transmits an ignored property nor lists it as writable.
8. `AttributeNames.<Entity>` does not emit a constant for an ignored property.
9. Ignoring a property named in a breadcrumb template fails synchronize with a message that names
   the attribute and explains the cause (F6).

## Out of scope

- Class-level exclusion — types are opted **in** via the `SparkContext`, so this is already covered.
- Changing the `Id` / `CanRead && CanWrite` conventions.
- Cleaning up orphan embedded-type model files (D8).
- A projection/entity `[IgnoreProperty]` mismatch diagnostic (D7).

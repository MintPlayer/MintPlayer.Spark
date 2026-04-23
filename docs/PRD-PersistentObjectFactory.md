# PRD: PersistentObject Factory + Populate + Attribute Clone

## Motivation

Every place in the codebase that wants to hand a `PersistentObject` to another
layer (retry-action popup, custom action result, sync handler, test) currently
writes an object-initializer by hand:

```csharp
new PersistentObject
{
    Name = "Car",
    ObjectTypeId = CarTypeId,
    Attributes =
    [
        new PersistentObjectAttribute { Name = "LicensePlate", Value = plate },
        new PersistentObjectAttribute { Name = "Model", Value = "X1" },
        new PersistentObjectAttribute { Name = "Year", Value = 2024 },
    ],
}
```

That has five problems:

1. **Magic strings everywhere.** `"Car"`, `"LicensePlate"`, `"Model"` scatter the
   schema across the codebase. We just added the
   `PersistentObjectNames` / `AttributeNames` source generators — they only pay
   off if callers actually consume them.
2. **Schema drift.** Hand-built POs omit `DataType`, `IsRequired`, `Rules`,
   `Label`, `Renderer`, `ShowedOn`, `Order`, `Group`. The client then receives
   an attribute that doesn't match the server's declared schema. Every caller
   re-invents which subset of properties is "enough".
3. **`ObjectTypeId` is almost always wrong.** Most call sites set it to
   `Guid.Empty` or omit it; the schema file has the real `Id` and the client
   needs it.
4. **No ergonomic way to build a popup PO** where you want *one* attribute with
   all the framework metadata (rules, renderer, permissions). Today you either
   copy the attribute's entire declaration by hand into the popup or you skip
   the metadata and the client form renders incorrectly.
5. **Three duplicate entity→PO mappers already exist in the framework**, each
   with a different metadata subset and different bugs. Anyone adding a sixth
   call site picks one at random and diverges further.

Vidyano solved (1)–(3) with `Manager.Current.GetPersistentObject(Types.Foo)`,
(4) with `attribute.Clone(id, name, label)`, and (5) with a matched pair:
scaffold the PO (metadata only) via `GetPersistentObject`, then fill values
via `po.PopulateAttributeValues(entity)`. Spark already has the
`PersistentObjectNames.Foo` generator output, a stub
`IManager.NewPersistentObject(...)` method with zero callers, and a weak
`PopulateAttributeValues<T>` extension method. This PRD wires them together
and promotes the scaffold-then-populate model to the framework's canonical
entity→PO conversion path — replacing `EntityMapper.ToPersistentObject`,
`SyncActionHandler.BuildPersistentObject`, and the extension-method
`ToPersistentObject<T>` with a single implementation.

## Goals

- `manager.NewPersistentObject(PersistentObjectNames.Foo)` returns a fully
  schema-backed, blank PersistentObject — `ObjectTypeId`, every declared
  attribute with correct `DataType` / `Label` / `Rules` / `Renderer` /
  `ShowedOn` / `Order` / `Group` / `IsRequired` / `IsVisible` / `IsReadOnly` /
  `IsArray`, values null.
- `po.PopulateAttributeValues(entity)` reads property values off the entity via
  reflection, applies the framework's type conversions
  (enum→string, `Color`→hex, `AsDetail`→dictionary), resolves Reference
  breadcrumbs, and sets `Id` / `Name` / `Breadcrumb` on the PO. Schema-aware.
- `EntityMapper.ToPersistentObject` becomes a thin wrapper: scaffold via
  `NewPersistentObject`, populate via `PopulateAttributeValues`. Its call sites
  stop changing.
- `SyncActionHandler.BuildPersistentObject` migrates to the same pattern with
  an extra `IsValueChanged` overlay from the incoming `properties[]` array.
- `attribute.CloneAndAdd(name, label?)` on an attribute that belongs to a PO
  returns a deep copy that is *already added* to the same PO's
  `Attributes` — caller doesn't touch the list.
- An attribute is always owned by exactly one PO for its lifetime. The
  `Attributes` list is read-only on the public surface
  (`IReadOnlyList<PersistentObjectAttribute>`); only framework-internal code
  mutates it via a single `AddAttribute` helper that sets the child's
  `Parent` back-reference.
- Popup / dialog POs that don't correspond to a CLR entity are declared as
  **Virtual POs** in JSON — no separate code path, no
  `ObjectTypeId == Guid.Empty` fallback. The same `NewPersistentObject(name)`
  / `NewPersistentObject(Guid)` factory handles entity-backed and virtual
  POs identically.
- The source generator emits a sibling `PersistentObjectIds` class —
  nested static classes per database schema, each holding
  `const string` Guid values — so apps with cross-schema name collisions
  can disambiguate via `manager.NewPersistentObject(Guid)`.
- The three half-baked extension methods (`ToPersistentObject<T>`,
  `PopulateAttributeValues<T>`, `PopulateObjectValues<T>`) in
  `PersistentObjectExtensions.cs` are either deleted or rewritten to delegate
  to the canonical service. They do not silently keep their inferior behavior.
- Every direct `new PersistentObject { ... }` / `new PersistentObjectAttribute { ... }`
  call site outside of framework internals migrates to the factory pattern.
- The generated `PersistentObjectNames` / `PersistentObjectIds` /
  `AttributeNames` constants become the canonical way to reference entity
  and attribute identifiers in user code.

## Non-goals

- Changing the wire format (`PersistentObject` / `PersistentObjectAttribute`
  JSON on the HTTP boundary stays identical).
- Server-side changes to the RetryAction modal frontend rendering. The frontend
  gap (`SparkRetryActionModalComponent` ignores `persistentObject.attributes`)
  is tracked separately; this PRD only ensures the server *sends* a correct
  payload.
- `CustomAction` execute-result construction (covered by existing
  `docs/custom-actions-prd.md`, will adopt the factory in its own migration).
- Rewriting the inverse path (`PO → entity`). Spark has
  `PopulateObjectValues<T>` and `ToEntity<T>` extensions; they stay as-is for
  now. Vidyano's richer `PopulateObjectValues` (reference resolution via
  `ITargetContext`, TranslatedString merging, concurrency tokens) is a
  follow-up PRD once the forward path is stable.

## Current state

| Piece | Location | Status |
|---|---|---|
| `IManager.NewPersistentObject(name, params attrs)` | `MintPlayer.Spark.Abstractions/IManager.cs` | Exists, trivial wrapper, zero callers |
| `Manager.NewPersistentObject` impl | `MintPlayer.Spark/Services/Manager.cs:16` | Returns `new PersistentObject { ObjectTypeId = Guid.Empty, Attributes = attributes }` — not schema aware |
| `IModelLoader` singleton with `GetEntityTypeByName` / `GetEntityTypeByClrType` / `ResolveEntityType` | `MintPlayer.Spark/Services/ModelLoader.cs` | Already loads every `App_Data/Model/*.json` lazily |
| `EntityTypeDefinition.Attributes` (`EntityAttributeDefinition[]`) | `MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs` | Full attribute schema, loaded from JSON |
| `EntityMapper.ToPersistentObject` (schema-aware entity→PO) | `MintPlayer.Spark/Services/EntityMapper.cs:55-158` | Copies 14 metadata fields inline per attribute, does enum/Color/AsDetail value conversions, resolves Reference breadcrumbs. 6 callers (DatabaseAccess×2, QueryExecutor×2, StreamingQueryExecutor×1, PersistentObjectExtensions×1). |
| `SyncActionHandler.BuildPersistentObject` (schema-aware dict→PO) | `MintPlayer.Spark/Services/SyncActionHandler.cs:67-133` | Duplicates 9 of EntityMapper's 14 metadata fields; adds `IsValueChanged` overlay from `properties[]`; has a CLR-reflection fallback when no schema. |
| `PersistentObjectExtensions.ToPersistentObject<T>(this T, Guid)` | `MintPlayer.Spark/Extensions/PersistentObjectExtensions.cs:98-131` | **Inferior duplicate** — reads from CLR properties directly (not schema), derives `DataType` via type switch, sets 0 of EntityMapper's 14 metadata fields besides `Name`/`Value`/`DataType`/`Query`. Used nowhere in framework; public surface — may have external callers. |
| `PersistentObjectExtensions.PopulateAttributeValues<T>` | `MintPlayer.Spark/Extensions/PersistentObjectExtensions.cs:17-43` | **Weak**: sets `Id` / `Name` / `Breadcrumb` + raw `property.GetValue(entity)` per attribute. No enum/Color/AsDetail conversions, no breadcrumb resolution for References. |
| `PersistentObjectExtensions.PopulateObjectValues<T>` (PO→entity) | `MintPlayer.Spark/Extensions/PersistentObjectExtensions.cs:51-76` | Stays for now; out of scope. |
| `PersistentObjectNames.*` / `AttributeNames.*` generator | `MintPlayer.Spark.SourceGenerators/Generators/PersistentObjectNamesGenerator.cs` | Lands in consumer projects, zero callers today |
| `PersistentObject` / `PersistentObjectAttribute` DTOs | `MintPlayer.Spark.Abstractions/PersistentObject.cs` | Both sealed, no internal fields, no back-reference |
| Direct `new PersistentObject { ... }` / `new PersistentObjectAttribute { ... }` call sites | Schema-backed in framework (EntityMapper, SyncActionHandler). E2E tests (4 files in `Security/`). Unit tests (PersistentObjectAttributeTests, StreamingDiffEngineTests, ValidationServiceTests). **Zero in demo apps.** Zero in retry-action popup flow. |

## Vidyano reference

The user called out that Vidyano's `Manager.GetPersistentObject` + instance
method `po.PopulateAttributeValues(entity)` is the pattern to mirror. Relevant
specifics found in `Vidyano.Service` (decompiled):

- **Signature** — `public void PopulateAttributeValues(object entity)` and a
  `PopulateAttributeValuesAsync` variant. **Instance method on
  `PersistentObject`**, not extension.
- **Mapping rule** — for each attribute in `this.Attributes`, reflect
  `entity.GetType().GetProperty(attr.Name)` and assign. Attributes with a dot
  in the name (`.` → computed / nested) are **skipped**. Attributes whose
  property doesn't exist on the entity are **silently skipped** (no throw).
- **Does NOT** construct the PO, set metadata, handle `Details` (nested POs
  via `PersistentObjectAttributeAsDetail`), or execute queries.
- **Primary usage** — auto-generated `PopulateAfterPersist` calls
  `obj.PopulateAttributeValues(entity)` after a save so the returned PO
  reflects the saved state. Custom `OnLoad` actions also call it after
  setting `obj.ObjectId = entity.Id`.
- **Inverse** — `PopulateObjectValues(object entity, ITargetContext?, bool
  includeAll)` walks `this.Attributes`, looks up matching writable entity
  properties, resolves References via `ITargetContext.GetEntity`, does
  `TranslatedString` merging and `DataTypes.FromServiceString` coercion.

Spark already has both methods (as weak extensions) — this PRD promotes
`PopulateAttributeValues` to a first-class service method with the framework's
actual conversions baked in.

## Design

### 1. `PersistentObject` / `PersistentObjectAttribute` DTO changes

Both types remain sealed and wire-compatible. Key invariant: **every
attribute belongs to exactly one PO for its entire lifetime**. No public
`Attach` / `Detach` surface — attributes are constructed already-owned, via
scaffold (`NewPersistentObject`), clone (`attribute.CloneAndAdd`), or
deserialization.

```csharp
public sealed class PersistentObject
{
    // ... existing surface unchanged ...

    // Backing field is a mutable list (framework-internal). Public surface is
    // IReadOnlyList<T> — callers can index and enumerate but cannot add/remove.
    // Wire format: serialized as a JSON array (verify in
    // PersistentObjectSerializationTests).
    private readonly List<PersistentObjectAttribute> _attributes = [];
    public IReadOnlyList<PersistentObjectAttribute> Attributes => _attributes;

    public PersistentObjectAttribute this[string name]
        => _attributes.FirstOrDefault(a => a.Name == name)
           ?? throw new KeyNotFoundException($"Attribute '{name}' not on PO '{Name}'");

    // Framework-internal. Used by:
    //   - EntityMapper when scaffolding from schema
    //   - PersistentObjectAttribute.CloneAndAdd (via Parent back-reference)
    //   - JSON deserializer (OnDeserialized hook)
    // Sets the child's Parent pointer and appends to _attributes.
    internal void AddAttribute(PersistentObjectAttribute attribute);
}

public sealed class PersistentObjectAttribute
{
    // ... existing surface unchanged ...

    // Always set once the attribute has been added to its PO. Not serialized
    // (cycle + wire contract stability).
    [JsonIgnore]
    public PersistentObject Parent { get; internal set; } = null!;

    /// <summary>
    /// Deep-copies this attribute under a new name / label and adds it to the
    /// same parent PO. Returns the clone so the caller can mutate it inline.
    /// </summary>
    public PersistentObjectAttribute CloneAndAdd(string name, TranslatedString? label = null);
}
```

**How `Parent` gets set** — three paths, all framework-internal:

- `EntityMapper.NewPersistentObject(name)` — per-schema-attribute
  construction calls `po.AddAttribute(attr)`.
- `attribute.CloneAndAdd(name, label?)` — reads `this.Parent`, builds the
  clone, calls `Parent.AddAttribute(clone)`.
- JSON deserialization (client → server path) — `System.Text.Json`
  `OnDeserialized` hook walks the deserialized list and re-issues
  `AddAttribute` for each element so `Parent` is set post-materialization.

No user code path mutates `_attributes` directly. The "attach twice"
question (Open Q #3 in v2) disappears — there's no public attach at all.

**`CloneAndAdd` body** — `MemberwiseClone` the source, null out `Id` (so
server treats it as new), overwrite `Name`, optionally overwrite `Label`,
set `IsValueChanged = false`, null out `Value`, **copy** `Rules` array and
`RendererOptions` dictionary by value (not reference — otherwise two
attributes share validation state), then `Parent.AddAttribute(clone)`.
Return the clone.

### 2. `IManager` surface

Single schema-backed entry point, two overloads that disambiguate
same-named entities across database schemas (see §10 for the generator
output that feeds these).

```csharp
public interface IManager
{
    // NEW — schema-backed factory keyed by name. Throws KeyNotFoundException
    // if the name is unknown OR ambiguous (more than one entity with this
    // name across schemas). Recommend the Guid overload for cross-schema
    // apps to avoid ambiguity.
    PersistentObject NewPersistentObject(string name);

    // NEW — schema-backed factory keyed by ObjectTypeId. Preferred when the
    // app declares entities in multiple database schemas (same name can
    // legally repeat). Throws KeyNotFoundException if the id is unknown.
    PersistentObject NewPersistentObject(Guid id);

    // existing
    IRetryAccessor Retry { get; }
    string GetTranslatedMessage(string key, params object[] parameters);
    string GetMessage(string key, string language, params object[] parameters);
}
```

There is **no "synthetic" overload**. Popup POs that don't correspond to a
CLR entity are defined as **Virtual POs** in JSON (existing Spark concept —
PO schema with `IsVirtual: true` and no `ClrType`). That gives them a real
`ObjectTypeId`, real declared attributes, and lets `NewPersistentObject`
treat them identically to entity-backed POs. No separate code path, no
`Guid.Empty` fallback.

### 3. `IEntityMapper` surface (extended)

`IEntityMapper` owns the entire entity ↔ PO machinery. `Manager` injects it
and thinly forwards the schema-backed `NewPersistentObject(name)` overload so
the user-facing ergonomic stays on `IManager`.

```csharp
public interface IEntityMapper
{
    // NEW — schema-backed factory (scaffold only, values null). Keyed by
    // name. Throws on unknown / ambiguous name.
    PersistentObject NewPersistentObject(string name);

    // NEW — schema-backed factory keyed by ObjectTypeId. Never ambiguous.
    PersistentObject NewPersistentObject(Guid id);

    // Existing surface — unchanged signature. Reimplemented internally as
    // NewPersistentObject(objectTypeId) + PopulateAttributeValues.
    PersistentObject ToPersistentObject(object entity, Guid objectTypeId,
        Dictionary<string, object>? includedDocuments = null);

    // NEW — populate an already-scaffolded PO from an entity. Handles:
    //   - Id extraction (entity.Id → po.Id)
    //   - Name / Breadcrumb resolution (GetEntityDisplayName)
    //   - Per-attribute value via reflection
    //   - Type conversions: enum → string, Color → "#RRGGBB", AsDetail → dict
    //   - Reference breadcrumb resolution from includedDocuments
    // Attributes whose name doesn't match a property on `entity` are
    // left with Value = null (Vidyano parity).
    // Attributes whose name contains '.' are skipped (Vidyano parity).
    void PopulateAttributeValues(PersistentObject po, object entity,
        Dictionary<string, object>? includedDocuments = null);
}
```

The per-attribute construction (the 14-metadata-field copy) lives as a
`private static` helper inside `EntityMapper` — pure function of
`EntityAttributeDefinition`, no dependencies, no interface ceremony.

### 4. `Manager` implementation

Thin. Both overloads forward to `IEntityMapper`.

```csharp
[Register(typeof(IManager), ServiceLifetime.Scoped)]
internal sealed partial class Manager : IManager
{
    [Inject] private readonly IRetryAccessor retry;
    [Inject] private readonly ITranslationsLoader translationsLoader;
    [Inject] private readonly IRequestCultureResolver requestCultureResolver;
    [Inject] private readonly IEntityMapper entityMapper;            // NEW

    public PersistentObject NewPersistentObject(string name)
        => entityMapper.NewPersistentObject(name);

    public PersistentObject NewPersistentObject(Guid id)
        => entityMapper.NewPersistentObject(id);
}
```

Note: `Manager` no longer needs `IModelLoader` — `EntityMapper` owns the
schema lookup. Dependency graph is strictly `Manager → EntityMapper →
ModelLoader`, acyclic.

### 5. `EntityMapper` reimplementation

Owns scaffold, populate, and the per-attribute metadata copy. No `IManager`
dependency — the factory body and `ToPersistentObject` both self-call
`NewPersistentObject`.

```csharp
[Register(typeof(IEntityMapper), ServiceLifetime.Scoped)]
internal partial class EntityMapper : IEntityMapper
{
    [Inject] private readonly IModelLoader modelLoader;
    // NO IManager dependency.

    public PersistentObject NewPersistentObject(string name)
    {
        var def = modelLoader.GetEntityTypeByName(name)
            ?? throw new KeyNotFoundException($"Unknown entity type '{name}'");
        return ScaffoldFrom(def);
    }

    public PersistentObject NewPersistentObject(Guid id)
    {
        var def = modelLoader.GetEntityType(id)
            ?? throw new KeyNotFoundException($"Unknown ObjectTypeId '{id}'");
        return ScaffoldFrom(def);
    }

    public PersistentObject ToPersistentObject(object entity, Guid objectTypeId,
        Dictionary<string, object>? includedDocuments = null)
    {
        var po = NewPersistentObject(objectTypeId);   // self-call, no DI
        PopulateAttributeValues(po, entity, includedDocuments);
        return po;
    }

    private static PersistentObject ScaffoldFrom(EntityTypeDefinition def)
    {
        var po = new PersistentObject
        {
            Id = null,
            Name = def.Name,
            ObjectTypeId = def.Id,
        };

        foreach (var attrDef in def.Attributes)
            po.AddAttribute(FromDefinition(attrDef));

        return po;
    }

    public void PopulateAttributeValues(PersistentObject po, object entity,
        Dictionary<string, object>? includedDocuments = null)
    {
        var entityType = entity.GetType();
        var idProperty = entityType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        po.Id = idProperty?.GetValue(entity)?.ToString();

        var displayName = GetEntityDisplayName(entity, entityType, /* def lookup */);
        po.Name = displayName;
        po.Breadcrumb = displayName;

        foreach (var attr in po.Attributes)
        {
            if (attr.Name.Contains('.')) continue;  // Vidyano parity

            var property = entityType.GetProperty(attr.Name,
                BindingFlags.Public | BindingFlags.Instance);
            if (property is null || !property.CanRead) continue;

            var raw = property.GetValue(entity);
            attr.Value = ConvertValueForWire(raw, property.PropertyType, attr);

            if (attr.DataType == "Reference" && attr.Value is string refId
                && !string.IsNullOrEmpty(refId) && includedDocuments is not null
                && includedDocuments.TryGetValue(refId, out var referenced)
                && referenced is not null)
            {
                attr.Breadcrumb = GetEntityDisplayName(
                    referenced, referenced.GetType(),
                    modelLoader.GetEntityTypeByClrType(
                        referenced.GetType().FullName ?? referenced.GetType().Name));
            }
        }
    }

    // Pure function of the definition. No state, no DI — private static.
    // Single place that owns "which 14 metadata fields copy from the schema
    // to the wire PO" — the piece currently duplicated across EntityMapper,
    // SyncActionHandler, and PersistentObjectExtensions.
    private static PersistentObjectAttribute FromDefinition(EntityAttributeDefinition def)
        => new()
        {
            Name = def.Name,
            Label = def.Label,
            DataType = def.DataType,
            IsArray = def.IsArray,
            IsRequired = def.IsRequired,
            IsVisible = def.IsVisible,
            IsReadOnly = def.IsReadOnly,
            Order = def.Order,
            ShowedOn = def.ShowedOn,
            Rules = def.Rules ?? [],
            Group = def.Group,
            Renderer = def.Renderer,
            RendererOptions = def.RendererOptions,
            Query = def.DataType == "Reference" ? def.Query : null,
            Value = null,  // filled by PopulateAttributeValues
        };
}
```

Four consequences:

- **The 14-property metadata copy lives in exactly one place** (`FromDefinition`,
  `private static`). Drift is impossible.
- **`ToPersistentObject`'s 6 callers don't change.** Same signature, same
  return contract; the body now self-delegates.
- **Value-dependent metadata** (`Breadcrumb` on Reference attributes) stays in
  the populate phase, where it belongs. Not a refactor blocker.
- **No DI cycle.** `Manager → EntityMapper → ModelLoader`, one-way. If
  `SyncActionHandler` later needs the scaffold directly, it injects
  `IEntityMapper` (already registered) — no `IManager` dep required.

### 6. `SyncActionHandler` migration

`SyncActionHandler.BuildPersistentObject` scaffolds from schema, applies a
dict of incoming values, overlays `IsValueChanged` from `properties[]`, and
falls back to CLR reflection when no schema exists. New shape:

```csharp
private PersistentObject BuildPersistentObject(Type entityType, string? documentId,
    Dictionary<string, object?> data, string[]? properties)
{
    var entityTypeDef = FindEntityTypeDefinition(entityType);

    // Schema path: use the factory. No schema path: keep the CLR-fallback
    // inline (it's a genuinely different beast — no PersistentObjectNames
    // constant exists, we're inventing attributes on the fly).
    var po = entityTypeDef is not null
        ? entityMapper.NewPersistentObject(entityTypeDef.Id)
        : BuildPoFromClrTypeFallback(entityType, documentId);

    po.Id = documentId;

    var propertySet = properties is not null
        ? new HashSet<string>(properties, StringComparer.OrdinalIgnoreCase)
        : null;

    foreach (var attr in po.Attributes)
    {
        var hasValue = TryGetValue(data, attr.Name, out var value);
        attr.Value = hasValue ? NormalizeValue(value) : null;
        attr.IsValueChanged = propertySet?.Contains(attr.Name) ?? hasValue;
    }

    return po;
}
```

The CLR-fallback branch stays (separate method, same behavior as today). The
schema branch drops ~20 lines, gains the missing `Label` / `Group` /
`Renderer` / `RendererOptions` fields for free, and stays consistent with
EntityMapper by construction.

### 7. `PersistentObjectExtensions` cleanup

Three existing extension methods overlap with this PRD's canonical path:

- `PopulateAttributeValues<T>(this PersistentObject, T)` — **rewrite** to call
  `IEntityMapper.PopulateAttributeValues` via a scoped resolver, or **delete**.
  Recommendation: delete. Extension methods can't get DI; anything worth doing
  needs `IEntityMapper`. Callers migrate to the service method.
- `ToPersistentObject<T>(this T, Guid)` — **delete**. Inferior to
  `IEntityMapper.ToPersistentObject`, missing 11 of 14 metadata fields, no
  callers in framework, preview-mode project (no backwards-compat debt).
- `PopulateObjectValues<T>` + `ToEntity<T>` — **keep**, out of scope (PO→entity
  direction).

This is the cleanup called out as Goal #5 — without it, the "one canonical
path" claim is a lie the moment someone calls the extension.

### 8. RetryAction popup pattern

Popup POs are Virtual POs — regular schema entries with no CLR entity.
They go through the same `NewPersistentObject` factory as entity-backed POs.

```json
// App_Data/Model/ConfirmDeleteCar.json — a Virtual PO definition
{
  "Id": "e3b5...{guid}...",
  "IsVirtual": true,
  "Name": "ConfirmDeleteCar",
  "Attributes": [
    { "Name": "LicensePlate",  "DataType": "String", "IsReadOnly": true },
    { "Name": "Confirmation",  "DataType": "String", "IsRequired": true,
      "Label": { "en": "Type the plate to confirm" } }
  ]
}
```

```csharp
public override async Task OnBeforeDeleteAsync(Car car)
{
    // Same API as any other PO. No "synthetic" concept.
    var popup = manager.NewPersistentObject(
        PersistentObjectNames.ConfirmDeleteCar);
    popup[AttributeNames.ConfirmDeleteCar.LicensePlate].Value = car.LicensePlate;

    var result = manager.Retry.Action(
        title: "Delete car",
        options: ["Delete", "Cancel"],
        persistentObject: popup);

    if (result?.Option == "Cancel") throw new OperationCanceledException();
}
```

`CloneAndAdd` remains useful for **dynamic duplication** — a `MergeWith`-style
CustomAction that lets the user pick a target entity and then shows its
attributes alongside the source's by cloning each source attribute with a
`_target` suffix. For fixed popup shapes, declare a Virtual PO.

Vidyano differences we adopt: `CloneAndAdd(name, label)` auto-adds to the
parent (Vidyano's `Clone` requires manual `.Add()`); indexer access
`po[...]`. Differences we don't: Vidyano's `Clone(Guid id, ...)` — Spark's
`Id` is `string?`, server issues it at persistence time, clone leaves
`Id = null`.

### 9. Frontend implications

`SparkRetryActionModalComponent` currently ignores
`persistentObject.attributes` (agent confirmed). For the popup flow to
actually render the cloned attribute as a form input, the modal needs to
learn to render an attributes array — that is **out of scope** for this PRD
but explicitly flagged as a follow-up. The server-side work in this PRD is
prerequisite.

### 10. `PersistentObjectIds` generator output

The existing `PersistentObjectNamesGenerator` already emits a
`PersistentObjectNames` class with `const string` entries keyed by entity
name. This PRD adds a sibling `PersistentObjectIds` class that emits the
`ObjectTypeId` (as a `const string` holding the Guid value) organized into
nested static classes per **database schema** — so apps that declare two
entities with the same name in different schemas can still disambiguate.

Generator output for a DemoApp with a default schema + an `Audit` schema:

```csharp
// Auto-generated
public static class PersistentObjectIds
{
    public static class Default
    {
        public const string Car    = "a1b2c3d4-e5f6-7890-abcd-ef0123456789";
        public const string Person = "11111111-2222-3333-4444-555555555555";
    }

    public static class Audit
    {
        public const string AuditLog = "99999999-8888-7777-6666-555555555555";
    }
}
```

Consumer code that needs disambiguation:

```csharp
var po = manager.NewPersistentObject(new Guid(PersistentObjectIds.Audit.AuditLog));
```

`const string` (not `static readonly Guid`) per the user's directive — lets
the constants work in attribute arguments and `switch` expressions. The
`new Guid(str)` call site is O(1) + inlinable; no runtime cost worth
optimizing for.

**How schemas are determined** — `EntityTypeDefinition.Schema` (or
equivalent JSON field; TBD during phase 2 of this PRD's implementation).
If no schema is declared, constants land under `Default`. A flat class
structure (`PersistentObjectIds.Default.Car`) is the fallback for apps that
never introduce a second schema — zero migration cost.

## Migration plan

**Phase 1 — DTO + ownership plumbing**

1. Add `PersistentObjectAttribute.Parent` (`{ get; internal set; }`, `[JsonIgnore]`).
2. Switch `PersistentObject.Attributes` from the existing
   `PersistentObjectAttribute[]` to a private
   `List<PersistentObjectAttribute> _attributes` backing with
   `public IReadOnlyList<PersistentObjectAttribute> Attributes` accessor.
   Update the ~3 framework call sites that index / assign the array
   (`.Length` → `.Count`, replace array-initializers with `AddAttribute`).
   Verify JSON wire format unchanged (`PersistentObjectSerializationTests`).
3. Add `internal void PersistentObject.AddAttribute(PersistentObjectAttribute)`
   — sets `Parent` + appends to backing list. Single mutation point.
4. Wire a `JsonConverter` (or `OnDeserialized` hook) so every PO deserialized
   on the server re-runs `AddAttribute` per element (sets `Parent` after
   materialization).
5. Add `PersistentObject` indexer + `PersistentObjectAttribute.CloneAndAdd`.
6. Unit tests: `PersistentObjectAttributeTests.CloneAndAddTests`,
   `PersistentObjectSerializationTests` (parent set after deserialize;
   parent not serialized; `Attributes` is read-only to callers).

**Phase 2 — Mapper refactor (canonical path)**

1. Add `private static EntityMapper.FromDefinition(EntityAttributeDefinition)`
   and `private static EntityMapper.ScaffoldFrom(EntityTypeDefinition)`.
2. Add `IEntityMapper.NewPersistentObject(string name)` and
   `IEntityMapper.NewPersistentObject(Guid id)` — both delegate to
   `ScaffoldFrom`.
3. Add `IEntityMapper.PopulateAttributeValues(po, entity, includedDocuments?)`.
4. Rewrite `EntityMapper.ToPersistentObject` body as
   `NewPersistentObject(objectTypeId)` + `PopulateAttributeValues` — one
   self-call, no `IManager` dependency.
5. Add `IEntityMapper` injection to `Manager`; drop `IModelLoader` injection
   (no longer needed there).
6. Implement `Manager.NewPersistentObject(string)` and
   `Manager.NewPersistentObject(Guid)` as thin forwards.
7. Tests:
   - `ManagerTests.NewPersistentObjectTests` — both overloads copy all 14
     metadata fields; throw on unknown name / id; name overload throws on
     ambiguity (two schemas, same entity name).
   - `EntityMapperTests.ToPersistentObject` — enum/Color/AsDetail conversions
     (currently uncovered in `EntityMapperBreadcrumbTests`); existing 4
     breadcrumb tests still pass.
   - `EntityMapperTests.PopulateAttributeValues` — direct unit test of the
     new method on a scaffold with various property shapes.

**Phase 2b — `PersistentObjectIds` generator**

1. Extend `PersistentObjectNamesGenerator` to emit a sibling
   `PersistentObjectIds` class, schema-nested, `const string` per entity
   holding the Guid value.
2. Generator source at
   `MintPlayer.Spark.SourceGenerators/Generators/PersistentObjectNamesGenerator.cs`.
3. Tests in `MintPlayer.Spark.SourceGenerators.Tests` — verify output for
   single-schema + multi-schema JSON inputs; verify `Default` fallback
   when no schema is declared; verify a snapshot test on the generated
   source.
4. Can land independently of the other phases — the generator output is
   additive, no existing callers.

**Phase 3 — Framework internals migration**

1. `SyncActionHandler.BuildPersistentObject` — migrate schema branch to
   `entityMapper.NewPersistentObject(entityTypeDef.Id)` + value loop
   (inject `IEntityMapper` directly; no need to go through `IManager`).
   Keep CLR-fallback as a separate method.
2. `PersistentObjectExtensions.ToPersistentObject<T>` — delete.
3. `PersistentObjectExtensions.PopulateAttributeValues<T>` — delete (or
   rewrite as a sugar wrapper that DI-resolves `IEntityMapper`; prefer
   delete given preview-mode project state).
4. Update any caller of the deleted extensions (grep first — the
   call-site inventory agent found none inside the framework, but public
   API may have external Demo-app callers).

**Phase 4 — Test migration**

Unit/E2E tests are the biggest holders of hand-built POs:

- `MintPlayer.Spark.Tests/Streaming/StreamingDiffEngineTests.cs` (`Po()` helper)
- `MintPlayer.Spark.Tests/Services/ValidationServiceTests.cs` (`Po()` helper)
- `MintPlayer.Spark.Tests/PersistentObjectAttributeTests.cs` (7 hand-built attrs)
- `MintPlayer.Spark.E2E.Tests/Security/*` — RowLevelAuthz, Concurrency,
  AttributeWriteProtection, NotFoundVsForbidden (4 files, all build Car POs
  as HTTP request bodies)

E2E security tests go through `SparkClient` on the wire — their PO
construction is building the **request body**, not calling `IManager`
(server-side service). They should migrate to a test-local builder that uses
`PersistentObjectNames.*` / `AttributeNames.*` constants so the magic strings
disappear. A shared `E2ETestFixtures.NewCar(plate, model, year)` is a clear
win (4 call sites, identical shape).

Unit tests that construct POs as *server-side* fixtures either define a
test-only Virtual PO in `App_Data/Model/` (when they need schema-backed
behavior) or keep hand-constructing via `new PersistentObject { ... }` for
test-local internals (when the test is specifically about malformed POs
— `PersistentObjectAttributeTests` is the clearest example). Because
`Attributes` is now `IReadOnlyList`, fixtures that today do
`Attributes = new[] { ... }` on an object-initializer need to move to a
test helper that uses the internal `AddAttribute` path (exposed via
`InternalsVisibleTo("MintPlayer.Spark.Tests")` if not already).

**Phase 5 — Demo apps**

Zero direct PO construction in demo apps today. Migration here is
**opportunity-based**: when a demo Actions class grows a retry-action popup
or a `CustomAction` return value, use `PersistentObjectNames.*` +
`NewPersistentObject` from day one. A worked example (e.g. a `MergeWith`-style
CustomAction in DemoApp) is the best documentation.

## Acceptance criteria

- [ ] `manager.NewPersistentObject(PersistentObjectNames.Person)` on DemoApp
  returns a PO with `ObjectTypeId == Person.Id`, `Attributes.Count ==
  entityDef.Attributes.Length`, each attribute has the full 14-field metadata
  matching the schema JSON, `Value == null`, and `Parent == returnedPO`.
- [ ] `manager.NewPersistentObject(new Guid(PersistentObjectIds.Default.Person))`
  returns an equivalent PO. Both overloads behave identically for
  unambiguous names.
- [ ] `po.PopulateAttributeValues(entity)` (via `IEntityMapper`) on that same
  scaffold fills every attribute's `Value` matching the entity's property,
  applies enum→string and `Color`→hex conversions, and sets `Id` / `Name` /
  `Breadcrumb`. Attributes whose name has no matching property are left
  `Value == null` (no throw). Attributes with `.` in their name are skipped.
- [ ] `EntityMapper.ToPersistentObject` returns a PO byte-identical (after
  JSON serialization) to the pre-change implementation for all 6 existing
  callers. Locked by golden tests on the 6 call paths.
- [ ] `SyncActionHandler.BuildPersistentObject` schema branch produces a PO
  with `IsValueChanged` set correctly from `properties[]` — existing sync
  handler tests still pass; new test covers the case where the incoming dict
  has a key not in the schema (expected: ignored, no throw).
- [ ] `po[AttributeNames.Person.FirstName].CloneAndAdd("Confirmation")` adds a
  new attribute to `po.Attributes`, returns it, and the clone's `Parent == po`.
- [ ] Rules and RendererOptions on the clone are not the same reference as on
  the source (mutation on one does not bleed to the other).
- [ ] `PersistentObject.Attributes` is typed `IReadOnlyList<PersistentObjectAttribute>`
  on the public surface; no call site outside the framework mutates it
  directly (verified by grep: no `.Attributes.Add(` / `.Attributes = ` outside
  `MintPlayer.Spark/`).
- [ ] Wire format unchanged: round-tripping a PO through
  `JsonSerializer.Serialize` + `Deserialize` produces JSON matching the
  pre-change byte output, and after deserialization every
  `PersistentObjectAttribute.Parent` is correctly set.
- [ ] Virtual PO defined in `App_Data/Model/ConfirmDeleteCar.json` flows
  through `NewPersistentObject` identically to an entity-backed PO
  (same return-shape assertions, no special-case code path).
- [ ] `PersistentObjectExtensions.ToPersistentObject<T>` and
  `PopulateAttributeValues<T>` are removed (or rewritten as delegating
  wrappers — no parallel implementation remains).
- [ ] All `MintPlayer.Spark.Tests` pass; all `MintPlayer.Spark.E2E.Tests` pass.
- [ ] `grep -rn "new PersistentObjectAttribute" MintPlayer.Spark/` returns
  only `EntityMapper.FromDefinition` (schema-backed) and the
  `SyncActionHandler` CLR-fallback branch (deliberate, no-schema path).

## Open questions

1. **`CloneAndAdd` label type — `TranslatedString?` or `string?`?** Vidyano
   uses raw `TranslatedString`. Spark's `Label` is already
   `TranslatedString?`, so mirror Vidyano. A `string` overload is trivial to
   add later if ergonomic.

2. **Ambiguous name throw vs. silent-pick in `NewPersistentObject(string)`.**
   When two schemas declare an entity with the same name, should the name
   overload throw (`AmbiguousMatchException`) or fall back to the first
   match? Proposal: throw — forces the caller to switch to the Guid
   overload, which is unambiguous by construction. Silent-pick is a
   footgun.

3. **Where does the schema name for the generator come from?** Candidates:
   (a) a new optional `"Schema"` field on the `EntityTypeDefinition` JSON;
   (b) a folder convention (`App_Data/Model/Audit/*.json` → `Audit` schema);
   (c) RavenDB's own database/collection namespace. Preference: (a), explicit
   + portable, falls back to `"Default"` when omitted. Decide before
   Phase 2b generator work.

## Resolved (previous versions of this PRD)

- ~~DI cycle between `Manager` and `EntityMapper`~~ — resolved by Option A:
  `Manager` forwards to `IEntityMapper`, no reverse dependency.
- ~~Array vs. List for `Attributes`~~ — resolved: private `List<T>` backing,
  public `IReadOnlyList<T>` accessor.
- ~~`Attach` / `Detach` / re-attach semantics~~ — removed: attributes are
  constrained to exactly one PO for life, no public attach surface.
- ~~"Synthetic" PO concept~~ — removed: popups use Virtual POs, same
  factory path.
- ~~`ObjectTypeIds` generator~~ — in scope now (§10 + Phase 2b).
- ~~`PopulateAttributeValues` placement (instance vs. extension vs. service)~~
  — resolved: service method on `IEntityMapper`.

## Out of scope

- Frontend modal rendering of popup attribute forms
  (`SparkRetryActionModalComponent`).
- The inverse path (`PO → entity`). Existing `PopulateObjectValues<T>` /
  `ToEntity<T>` extensions stay untouched. A richer Vidyano-parity port
  (reference resolution via `ITargetContext`, `TranslatedString` merging)
  is a follow-up PRD.
- A `CustomAction` return-value builder that uses the factory (separate PRD
  once CustomActions land broadly).
- Renaming `NewPersistentObject` to `GetPersistentObject` à la Vidyano — the
  Spark naming is already established in `prd-manager-retry-action.md`.
- **First-class `PersistentObjectAttributeAsDetail` (nested PO arrays) in
  the mapper.** Today `EntityMapper` converts `AsDetail`-typed values to a
  plain `Dictionary<string, object?>` for serialization; a richer port
  would use `PersistentObjectAttributeAsDetail` with nested scaffolded POs,
  letting the populate phase recurse. Follow-up PRD once Virtual POs + the
  scaffold/populate pipeline are stable. The `.Contains('.')` skip in
  `PopulateAttributeValues` is a forward-compatibility hook for this.

## References

- `docs/prd-manager-retry-action.md` — parent PRD for `IManager` / retry.
- `MintPlayer.Spark/Services/EntityMapper.cs:55-158` — the mapping logic
  being split into `FromDefinition` + `PopulateAttributeValues`.
- `MintPlayer.Spark/Services/SyncActionHandler.cs:67-133` — second schema
  branch being migrated to the factory.
- `MintPlayer.Spark/Extensions/PersistentObjectExtensions.cs:17,51,98` — weak
  extension methods being deleted / superseded.
- `MintPlayer.Spark.SourceGenerators/Generators/PersistentObjectNamesGenerator.cs`
  — the generator whose output this PRD activates.
- `MintPlayer.Spark/Services/ModelLoader.cs` — schema source at runtime.
- `C:\Repos\Vidyano.Service\Vidyano.Service\Repository\PersistentObject.cs:780`
  — Vidyano `PopulateAttributeValues` signature (decompiled).
- `C:\Repos\CV\CV\Service\CustomActions\MergeWith.cs:88` — Vidyano `Clone`
  usage (manual `.Add()` after clone; we improve by auto-attaching).
- `C:\Repos\b\CareAdmin\CareAdmin\Service\Actions\WebshopShoppingCartActions.cs:72-73`
  — Vidyano `PopulateAttributeValues` usage in a custom `OnLoad`.

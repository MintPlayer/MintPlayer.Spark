# PRD: SparkEditor Model Datatypes & Shared Models Library

## Problem Statement

The SparkEditor application (a Spark meta-app that edits other Spark app definitions) has model JSON files in `SparkEditor/SparkEditor/App_Data/Model/` where many attributes use incorrect datatypes. Fields that should be TranslatedStrings, References, or enum/lookup values are all defined as plain `"string"` dataType. This means the Spark UI renders simple text inputs instead of the appropriate editors (multi-language input, entity lookup dropdown, enum selector, etc.), making the editor much less usable.

## Scope

All 10 model JSON files in `SparkEditor/SparkEditor/App_Data/Model/`:

| File | Entity |
|------|--------|
| ProgramUnitDef.json | Navigation items |
| ProgramUnitGroupDef.json | Navigation groups |
| PersistentObjectDefinition.json | Entity type definitions |
| QueryDefinition.json | Query definitions |
| AttributeDefinition.json | Attribute/field definitions |
| CustomActionDef.json | Custom action definitions |
| SecurityGroupDef.json | Security group definitions |
| SecurityRightDef.json | Security right definitions |
| LanguageDef.json | Language definitions |
| TranslationDef.json | Translation key-value pairs |

## Analysis: Issues by Entity

### 1. ProgramUnitDef.json

| Attribute | Current | Should Be | Notes |
|-----------|---------|-----------|-------|
| `Name` | `string` | `TranslatedString` | Program unit names are displayed in navigation and must be translatable |
| `QueryId` | `string` | `Reference` (optional) | Foreign key to QueryDefinition. Needs `referenceType: "SparkEditor.Entities.QueryDefinition"` and `query: "GetQueryDefinitions"` |
| `PersistentObjectId` | `string` | `Reference` (optional) | Foreign key to PersistentObjectDefinition. Needs `referenceType: "SparkEditor.Entities.PersistentObjectDefinition"` and `query: "GetPersistentObjectDefinitions"` |
| `GroupId` | `string` (readOnly) | `Reference` (optional) | Foreign key to ProgramUnitGroupDef. Should NOT be readOnly - users must be able to assign a unit to a group. Needs `referenceType: "SparkEditor.Entities.ProgramUnitGroupDef"` and `query: "GetProgramUnitGroupDefs"` |
| `Type` | `string` | `string` + lookupReferenceType | Should be constrained to known values: `"query"`, `"persistentObject"`. Add a transient LookupReference. |

### 2. ProgramUnitGroupDef.json

| Attribute | Current | Should Be | Notes |
|-----------|---------|-----------|-------|
| `Name` | `string` | `TranslatedString` | Group names are displayed in navigation and must be translatable |

### 3. PersistentObjectDefinition.json

| Attribute | Current | Should Be | Notes |
|-----------|---------|-----------|-------|
| `Name` | `string` | `string` | OK - internal identifier, not displayed to end users |
| `Label` | `string` | `TranslatedString` | The display label shown in UI, must be translatable |
| `Breadcrumb` | `string` | `string` | OK - this is a format template (e.g. `"{Street}, {City}"`) |
| `Description` | `string` | `TranslatedString` | Description shown in UI, should be translatable |
| `ContextProperty` | `string` | `string` | OK - deprecated field, may be removed |

### 4. QueryDefinition.json

| Attribute | Current | Should Be | Notes |
|-----------|---------|-----------|-------|
| `Label` | `string` | `TranslatedString` | Query display label, must be translatable |
| `Description` | `string` | `TranslatedString` | Query description, should be translatable |
| `EntityType` | `string` | `Reference` (optional) | Reference to PersistentObjectDefinition. Needs `referenceType` and `query` |
| `RenderMode` | `string` | `string` + lookupReferenceType | Constrained to `"Pagination"`, `"VirtualScrolling"`. Add transient LookupReference. |
| `Source` | `string` | `string` | OK - uses `"Database.X"` / `"Custom.X"` convention, free text is fine |

### 5. AttributeDefinition.json

| Attribute | Current | Should Be | Notes |
|-----------|---------|-----------|-------|
| `Label` | `string` | `TranslatedString` | Attribute display label, must be translatable |
| `DataType` | `string` | `string` + lookupReferenceType | Constrained to known values: `"string"`, `"number"`, `"decimal"`, `"boolean"`, `"datetime"`, `"date"`, `"guid"`, `"color"`, `"Reference"`, `"AsDetail"`. Add transient LookupReference. |
| `ShowedOn` | `string` | `string` + lookupReferenceType | Constrained to: `"Query"`, `"PersistentObject"`, `"Query, PersistentObject"`. Add transient LookupReference. |
| `LookupReferenceType` | `string` | `Reference` | Should reference available LookupReferences in the project. Needs a new `LookupReferenceDef` entity type backed by `LookupReferenceDiscoveryService`, with a custom query `GetLookupReferenceDefs` that enumerates all registered transient and dynamic lookup references. The stored value is the lookup reference name (e.g., `"CarStatus"`). Needs `referenceType` pointing to the new entity and `query: "GetLookupReferenceDefs"`. |

### 6. CustomActionDef.json

| Attribute | Current | Should Be | Notes |
|-----------|---------|-----------|-------|
| `DisplayName` | `string` | `TranslatedString` | Action display name, must be translatable |
| `Description` | `string` | `TranslatedString` | Action description, should be translatable |
| `ShowedOn` | `string` | `string` + lookupReferenceType | Constrained to: `"Query"`, `"PersistentObject"`, `"Query, PersistentObject"`. Reuse same LookupReference as AttributeDefinition.ShowedOn |
| `SelectionRule` | `string` | `string` + lookupReferenceType | Constrained to: `"None"`, `"ZeroOrMore"`, `"OneOrMore"`, `"ExactlyOne"`. Add transient LookupReference. |
| `ConfirmationMessageKey` | `string` | `Reference` (optional) | Reference to TranslationDef key. Needs `referenceType: "SparkEditor.Entities.TranslationDef"` and `query: "GetTranslationDefs"` |

### 7. SecurityGroupDef.json

| Attribute | Current | Should Be | Notes |
|-----------|---------|-----------|-------|
| `Name` | `string` | `TranslatedString` | Security group name, should be translatable |
| `Comment` | `string` | `TranslatedString` | Comment/description, should be translatable |

### 8. SecurityRightDef.json

| Attribute | Current | Should Be | Notes |
|-----------|---------|-----------|-------|
| `GroupId` | `string` | `Reference` | Foreign key to SecurityGroupDef. Needs `referenceType: "SparkEditor.Entities.SecurityGroupDef"` and `query: "GetSecurityGroupDefs"` |

### 9. LanguageDef.json

| Attribute | Current | Should Be | Notes |
|-----------|---------|-----------|-------|
| `Name` | `string` | `TranslatedString` | Language display name (e.g. "English" / "Engels" / "Anglais"), must be translatable |

### 10. TranslationDef.json

| Attribute | Current | Should Be | Notes |
|-----------|---------|-----------|-------|
| `Values` | `string` | `TranslatedString` | This IS a translated string - a dictionary of language code to translation value |

## Summary of Changes by Category

### TranslatedString changes (13 attributes)

These attributes store multi-language text (`{"en": "...", "nl": "..."}`) and need a TranslatedString editor:

1. `ProgramUnitDef.Name`
2. `ProgramUnitGroupDef.Name`
3. `PersistentObjectDefinition.Label`
4. `PersistentObjectDefinition.Description`
5. `QueryDefinition.Label`
6. `QueryDefinition.Description`
7. `AttributeDefinition.Label`
8. `CustomActionDef.DisplayName`
9. `CustomActionDef.Description`
10. `SecurityGroupDef.Name`
11. `SecurityGroupDef.Comment`
12. `LanguageDef.Name`
13. `TranslationDef.Values`

### Reference changes (8 attributes)

These attributes store foreign key IDs and need Reference dropdowns:

1. `ProgramUnitDef.QueryId` -> QueryDefinition
2. `ProgramUnitDef.PersistentObjectId` -> PersistentObjectDefinition
3. `ProgramUnitDef.GroupId` -> ProgramUnitGroupDef (also: remove `isReadOnly: true`)
4. `QueryDefinition.EntityType` -> PersistentObjectDefinition
5. `AttributeDefinition.LookupReferenceType` -> LookupReferenceDef (new entity type, see below)
6. `CustomActionDef.ConfirmationMessageKey` -> TranslationDef
7. `SecurityRightDef.GroupId` -> SecurityGroupDef

### LookupReference changes (5 attributes)

These attributes have a fixed set of valid values and need dropdown selectors:

1. `ProgramUnitDef.Type` -> new LookupReference `"ProgramUnitType"`: `["query", "persistentObject"]`
2. `QueryDefinition.RenderMode` -> new LookupReference `"QueryRenderMode"`: `["Pagination", "VirtualScrolling"]`
3. `AttributeDefinition.DataType` -> new LookupReference `"AttributeDataType"`: `["string", "number", "decimal", "boolean", "datetime", "date", "guid", "color", "Reference", "AsDetail"]`
4. `AttributeDefinition.ShowedOn` -> new LookupReference `"ShowedOnOptions"`: `["Query", "PersistentObject", "Query, PersistentObject"]`
5. `CustomActionDef.SelectionRule` -> new LookupReference `"SelectionRuleOptions"`: `["None", "ZeroOrMore", "OneOrMore", "ExactlyOne"]`
6. `CustomActionDef.ShowedOn` -> reuse `"ShowedOnOptions"`

## Prerequisites / Dependencies

### TranslatedString as a dataType

The Spark framework's `GetDataType()` in `ModelSynchronizer.cs` and `EntityMapper.cs` does not currently recognize `TranslatedString` as a first-class dataType. It would be detected as `"AsDetail"` because `TranslatedString` is a complex class. However, the SparkEditor uses hand-crafted model JSONs (not auto-generated), so we can define any dataType string.

**Decision needed**: Either:
- **(A)** Add `"translatedString"` as a recognized dataType in the ng-spark frontend, with a dedicated multi-language text editor component. This is the correct long-term approach.
- **(B)** Keep C# entity properties as `string?` (storing serialized JSON) and add a custom renderer (e.g., `"renderer": "translated-string-editor"`) that provides the multi-language editing UX. Works with existing infrastructure.

**Recommendation**: Option (A) — `TranslatedString` is a fundamental Spark concept used across all apps. Making it a first-class dataType benefits all Spark applications, not just the editor.

### C# Entity Type Changes

If going with option (A), the SparkEditor C# entity properties that currently store serialized TranslatedString JSON as `string?` should be changed to `TranslatedString?`:

- `ProgramUnitDef.Name`: `string?` -> `TranslatedString?`
- `ProgramUnitGroupDef.Name`: `string?` -> `TranslatedString?`
- `PersistentObjectDefinition.Label`: `string?` -> `TranslatedString?`
- `PersistentObjectDefinition.Description`: `string?` -> `TranslatedString?`
- `QueryDefinition.Label`: `string?` -> `TranslatedString?`
- `QueryDefinition.Description`: `string?` -> `TranslatedString?`
- `AttributeDefinition.Label`: `string?` -> `TranslatedString?`
- `CustomActionDef.DisplayName`: `string?` -> `TranslatedString?`
- `CustomActionDef.Description`: `string?` -> `TranslatedString?`
- `SecurityGroupDef.Name`: `string?` -> `TranslatedString?`
- `SecurityGroupDef.Comment`: `string?` -> `TranslatedString?`
- `LanguageDef.Name`: `string?` -> `TranslatedString?`
- `TranslationDef.Values`: `string?` -> `TranslatedString?`

### Transient LookupReferences

New transient LookupReferences must be registered in the SparkEditor's startup (`Program.cs` or via actions classes):

- `ProgramUnitType`: `["query", "persistentObject"]`
- `QueryRenderMode`: `["Pagination", "VirtualScrolling"]`
- `AttributeDataType`: `["string", "number", "decimal", "boolean", "datetime", "date", "guid", "color", "Reference", "AsDetail"]`
- `ShowedOnOptions`: `["Query", "PersistentObject", "Query, PersistentObject"]`
- `SelectionRuleOptions`: `["None", "ZeroOrMore", "OneOrMore", "ExactlyOne"]`

---

## Part 2: Extract Shared Models Library (MintPlayer.Spark.Models)

### Problem Statement

The JSON transport models (data classes serialized to/from the API) currently live in `MintPlayer.Spark.Abstractions` alongside service interfaces, C# attributes, and DI builder contracts. This forces any project that only needs the JSON models (like the SparkEditor, or a lightweight API client) to take a dependency on the full Abstractions package.

Additionally, the SparkEditor project duplicates these models in its own `Entities/` folder with slightly different representations (string IDs vs Guid, serialized JSON strings vs typed TranslatedString). After extracting a shared models library, the SparkEditor can reference it directly and **delete its entire `Entities/` folder**.

### Current Architecture

#### MintPlayer.Spark.Abstractions contains 4 categories mixed together:

| Category | Files | Should Move? |
|----------|-------|-------------|
| **JSON Transport Models** | PersistentObject.cs, SparkQuery.cs, QueryResult.cs, EntityTypeDefinition.cs, EntityTypeFile.cs, ProgramUnit.cs, CultureConfiguration.cs, TranslatedString.cs, SortColumn.cs, ValidationRule.cs, ValidationError.cs, DynamicLookupReference.cs, TransientLookupReference.cs, EShowedOn.cs, ELookupDisplayType.cs, SparkQueryRenderMode.cs | **YES** -> MintPlayer.Spark.Models |
| **Service Interfaces** | IDatabaseAccess.cs, IManager.cs, ISyncActionHandler.cs, ISyncActionInterceptor.cs, IAccessControl.cs, IPermissionService.cs, IGroupMembershipProvider.cs, IRetryAccessor.cs, ICustomAction.cs, ISparkBuilder.cs | No - stay in Abstractions |
| **C# Attributes** | ReferenceAttribute.cs, LookupReferenceAttribute.cs, FromIndexAttribute.cs | No - stay in Abstractions |
| **Builder/DI** | SparkModuleRegistry.cs | No - stay in Abstractions |
| **Retry Models** | RetryResult.cs | **YES** -> MintPlayer.Spark.Models |

#### Models scattered in other projects that should also move:

| Current Location | Class | Notes |
|-----------------|-------|-------|
| `MintPlayer.Spark/Models/CustomActionDefinition.cs` | `CustomActionDefinition` | Definition metadata loaded from customActions.json |
| `MintPlayer.Spark/Models/CustomActionsConfiguration.cs` | `CustomActionsConfiguration` | Root model for customActions.json (`Dictionary<string, CustomActionDefinition>`) |
| `MintPlayer.Spark.Authorization/Models/SecurityConfiguration.cs` | `SecurityConfiguration` | Root model for security.json (Groups dict + Rights list) |
| `MintPlayer.Spark.Authorization/Models/Right.cs` | `Right` | Permission assignment (Resource, GroupId, IsDenied, IsImportant) |

#### SparkEditor entities that are duplicates of Abstractions models:

| SparkEditor Entity | Equivalent Shared Model | Key Differences |
|---|---|---|
| `PersistentObjectDefinition` | `EntityTypeDefinition` | string? Id vs Guid Id; Label/Description as string? vs TranslatedString; no Tabs/Groups/Attributes arrays |
| `AttributeDefinition` | `EntityAttributeDefinition` | string? Id vs Guid Id; Label as string? vs TranslatedString; has PersistentObjectId/Name back-refs; missing ValidationRule[], RendererOptions |
| `QueryDefinition` | `SparkQuery` | string? Id vs Guid Id; Description as string? vs TranslatedString; missing SortColumns[], UseProjection, IsStreamingQuery; has PersistentObjectName back-ref |
| `ProgramUnitDef` | `ProgramUnit` | string? Id vs Guid Id; Name as string? vs TranslatedString; QueryId/PersistentObjectId as string? vs Guid? |
| `ProgramUnitGroupDef` | `ProgramUnitGroup` | string? Id vs Guid Id; Name as string? vs TranslatedString; no nested ProgramUnits[] |
| `CustomActionDef` | `CustomActionDefinition` | Has Name (identifier) + all definition fields; CustomActionDefinition lacks Name (it's the dictionary key) |
| `SecurityGroupDef` | Part of `SecurityConfiguration.Groups` | No standalone model - embedded as `Dictionary<string, TranslatedString>` |
| `SecurityRightDef` | `Right` | string? Id vs Guid Id; GroupId as string? vs Guid |
| `LanguageDef` | Part of `CultureConfiguration.Languages` | No standalone model - embedded as `Dictionary<string, TranslatedString>` |
| `TranslationDef` | Part of translations `Dictionary<string, TranslatedString>` | No standalone model - embedded in a flat dictionary |

### Proposed Architecture

#### New project: `MintPlayer.Spark.Models`

A lightweight library with **zero dependencies** (only `System.Text.Json`) containing all JSON transport models. This becomes the lowest layer in the dependency graph.

```
MintPlayer.Spark.Models          (NEW - JSON transport models only)
  ^
  |
MintPlayer.Spark.Abstractions   (service interfaces, attributes - references Models)
  ^
  |
MintPlayer.Spark                (core framework)
MintPlayer.Spark.Authorization  (authorization)
SparkEditor                     (can reference Models directly)
```

#### Complete type inventory for MintPlayer.Spark.Models

All types below have been verified through dependency tracing. Every cross-reference between them is self-contained — the only external dependency is `System.Text.Json`. **21 source files, 32 types total.**

**From `MintPlayer.Spark.Abstractions/` (17 files, 26 types):**

| Source File | Types Defined | Internal Dependencies |
|------------|---------------|----------------------|
| `TranslatedString.cs` | `TranslatedString`, `TranslatedStringJsonConverter` | _(none — leaf type)_ |
| `EShowedOn.cs` | `EShowedOn` (flags enum) | _(none — leaf type)_ |
| `ELookupDisplayType.cs` | `ELookupDisplayType` (enum) | _(none — leaf type)_ |
| `SparkQueryRenderMode.cs` | `SparkQueryRenderMode` (enum) | _(none — leaf type)_ |
| `SortColumn.cs` | `SortColumn` | _(none — leaf type)_ |
| `ValidationRule.cs` | `ValidationRule` | `TranslatedString` |
| `ValidationError.cs` | `ValidationError`, `ValidationResult` | `TranslatedString` |
| `PersistentObject.cs` | `PersistentObject`, `PersistentObjectAttribute` | `TranslatedString`, `EShowedOn`, `ValidationRule` |
| `EntityTypeDefinition.cs` | `EntityTypeDefinition`, `EntityAttributeDefinition`, `AttributeTab`, `AttributeGroup` | `TranslatedString`, `EShowedOn`, `ValidationRule` |
| `EntityTypeFile.cs` | `EntityTypeFile` | `EntityTypeDefinition`, `SparkQuery` |
| `SparkQuery.cs` | `SparkQuery` | `TranslatedString`, `SortColumn`, `SparkQueryRenderMode` |
| `QueryResult.cs` | `QueryResult` | `PersistentObject` |
| `ProgramUnit.cs` | `ProgramUnitsConfiguration`, `ProgramUnitGroup`, `ProgramUnit` | `TranslatedString` |
| `CultureConfiguration.cs` | `CultureConfiguration` | `TranslatedString` |
| `DynamicLookupReference.cs` | `EmptyValue`, `LookupReferenceValue<TValue>`, `DynamicLookupReference`, `DynamicLookupReference<TValue>` | `TranslatedString`, `ELookupDisplayType` |
| `TransientLookupReference.cs` | `TransientLookupReference`, `TransientLookupReference<TKey>` | `TranslatedString`, `ELookupDisplayType` |
| `Retry/RetryResult.cs` | `RetryResult` | `PersistentObject` |

**From `MintPlayer.Spark/Models/` (2 files, 2 types):**

| Source File | Types Defined | Internal Dependencies |
|------------|---------------|----------------------|
| `CustomActionDefinition.cs` | `CustomActionDefinition` | `TranslatedString` |
| `CustomActionsConfiguration.cs` | `CustomActionsConfiguration` | `CustomActionDefinition` |

**From `MintPlayer.Spark.Authorization/Models/` (2 files, 2 types):**

| Source File | Types Defined | Internal Dependencies |
|------------|---------------|----------------------|
| `SecurityConfiguration.cs` | `SecurityConfiguration` | `TranslatedString`, `Right` |
| `Right.cs` | `Right` | _(none — leaf type)_ |

**New standalone models (3 types, currently embedded in parent containers):**

| New Type | Extracted From | Purpose |
|----------|---------------|---------|
| `SecurityGroupDefinition` | `SecurityConfiguration.Groups` (`Dictionary<string, TranslatedString>`) | Standalone security group with Id, Name (TranslatedString), Comment (TranslatedString) |
| `LanguageDefinition` | `CultureConfiguration.Languages` (`Dictionary<string, TranslatedString>`) | Standalone language with Culture code and Name (TranslatedString) |
| `TranslationEntry` | `translations.json` (`Dictionary<string, TranslatedString>`) | Standalone translation with Key and Values (TranslatedString) |

#### What stays in MintPlayer.Spark.Abstractions (adds `<ProjectReference>` to Models):

| Category | Files | Types |
|----------|-------|-------|
| **Service Interfaces** | `IDatabaseAccess.cs` | `IDatabaseAccess` |
| | `IManager.cs` | `IManager` |
| | `ISyncActionHandler.cs` | `ISyncActionHandler` |
| | `ISyncActionInterceptor.cs` | `ISyncActionInterceptor` |
| | `Authorization/IAccessControl.cs` | `IAccessControl` |
| | `Authorization/IPermissionService.cs` | `IPermissionService` |
| | `Authorization/IGroupMembershipProvider.cs` | `IGroupMembershipProvider` |
| | `Authorization/SparkAccessDeniedException.cs` | `SparkAccessDeniedException` |
| | `Retry/IRetryAccessor.cs` | `IRetryAccessor` |
| | `Actions/ICustomAction.cs` | `ICustomAction`, `CustomActionArgs` |
| **C# Attributes** | `ReferenceAttribute.cs` | `ReferenceAttribute` |
| | `LookupReferenceAttribute.cs` | `LookupReferenceAttribute` |
| | `FromIndexAttribute.cs` | `FromIndexAttribute` |
| **Builder/DI** | `Builder/ISparkBuilder.cs` | `ISparkBuilder` |
| | `Builder/SparkModuleRegistry.cs` | `SparkModuleRegistry` |

These interfaces/attributes reference types from `MintPlayer.Spark.Models` (e.g., `PersistentObject` in `IDatabaseAccess`, `RetryResult` in `IRetryAccessor`), which is why Abstractions takes a project reference to Models.

### SparkEditor Entity Elimination

After the shared models library exists, the SparkEditor can delete its entire `Entities/` folder and use the shared models directly. This requires resolving the key differences:

#### 1. ID Type: string? vs Guid

The shared models use `Guid` IDs. The SparkEditor currently prefixes string IDs (e.g., `"PersistentObjectDefinitions/{guid}"`).

**Resolution**: The `SparkEditorFileService` already parses GUIDs from JSON. Instead of converting to prefixed strings, it should keep them as `Guid`. The Spark framework's Actions/Context layer handles RavenDB document IDs separately from the model's `Id` property. The `Guid` ID in the shared model is the logical identifier; the RavenDB document ID (with prefix) is the storage identifier.

#### 2. TranslatedString: string? vs TranslatedString

SparkEditor entities store TranslatedString as serialized JSON strings. The shared models use the strongly-typed `TranslatedString` class.

**Resolution**: Use `TranslatedString` directly. The `TranslatedStringJsonConverter` already handles serialization to/from the flat `{"en": "...", "nl": "..."}` JSON format. `SparkEditorFileService` can deserialize directly to `TranslatedString` instead of calling `GetRawText()`.

#### 3. Flat vs Nested Structure

SparkEditor loads AttributeDefinitions and QueryDefinitions as separate top-level entities. In the shared models, these are nested arrays inside `EntityTypeDefinition` and `EntityTypeFile`.

**Resolution**: The SparkEditor Actions classes can still load individual attributes/queries from the `SparkEditorFileService`, which parses the nested JSON and returns flattened collections. The shared model classes work fine as individual objects — they just happen to also be nestable in their parent containers.

#### 4. Back-reference Fields (PersistentObjectName, PersistentObjectId)

SparkEditor's `AttributeDefinition` and `QueryDefinition` have parent back-reference fields that don't exist on the shared models.

**Resolution**: Add optional back-reference fields to the shared models:
- `EntityAttributeDefinition`: add `string? PersistentObjectName` (optional, set by loaders that flatten the hierarchy)
- `SparkQuery`: add `string? PersistentObjectName` (same rationale)

These fields are ignored during normal framework operation but useful when models are loaded in a flattened context (SparkEditor, tooling).

#### 5. Missing Fields on Shared Models

`CustomActionDefinition` lacks a `Name` property (it's the dictionary key in `CustomActionsConfiguration`).

**Resolution**: Add `string? Name` to `CustomActionDefinition`. Set it from the dictionary key during loading.

#### Entity-to-Model Mapping After Migration

| SparkEditor currently uses... | Replaced by shared model... |
|---|---|
| `SparkEditor.Entities.PersistentObjectDefinition` | `MintPlayer.Spark.Models.EntityTypeDefinition` |
| `SparkEditor.Entities.AttributeDefinition` | `MintPlayer.Spark.Models.EntityAttributeDefinition` |
| `SparkEditor.Entities.QueryDefinition` | `MintPlayer.Spark.Models.SparkQuery` |
| `SparkEditor.Entities.ProgramUnitDef` | `MintPlayer.Spark.Models.ProgramUnit` |
| `SparkEditor.Entities.ProgramUnitGroupDef` | `MintPlayer.Spark.Models.ProgramUnitGroup` |
| `SparkEditor.Entities.CustomActionDef` | `MintPlayer.Spark.Models.CustomActionDefinition` |
| `SparkEditor.Entities.SecurityGroupDef` | `MintPlayer.Spark.Models.SecurityGroupDefinition` (new) |
| `SparkEditor.Entities.SecurityRightDef` | `MintPlayer.Spark.Models.Right` |
| `SparkEditor.Entities.LanguageDef` | `MintPlayer.Spark.Models.LanguageDefinition` (new) |
| `SparkEditor.Entities.TranslationDef` | `MintPlayer.Spark.Models.TranslationEntry` (new) |

### Impact on Existing Projects

All projects that currently reference `MintPlayer.Spark.Abstractions` will continue to work because Abstractions will re-export (depend on) the Models package. No breaking change for existing consumers.

| Project | Change |
|---------|--------|
| `MintPlayer.Spark` | Add reference to `.Models`. Remove `Models/CustomActionDefinition.cs` and `Models/CustomActionsConfiguration.cs`. Update namespaces. |
| `MintPlayer.Spark.Abstractions` | Add reference to `.Models`. Move model files out. Update `using` statements — types are still available transitively. |
| `MintPlayer.Spark.Authorization` | Add reference to `.Models`. Remove `Models/SecurityConfiguration.cs` and `Models/Right.cs`. Update namespaces. |
| `SparkEditor` | Replace reference from `.Spark` to `.Models` (or keep `.Spark` which transitively includes `.Models`). Delete entire `Entities/` folder. Update Actions classes and `SparkEditorFileService` to use shared model types. |
| Demo apps (DemoApp, HR, Fleet) | No change — they reference Abstractions which transitively includes Models. |
| `MintPlayer.Spark.Replication` | No change — references Abstractions. |
| `MintPlayer.Spark.Messaging` | No change — references Abstractions. |

---

## New Entity Type: LookupReferenceDef

The `AttributeDefinition.LookupReferenceType` attribute needs a Reference to the available lookup references in the project. Since lookup references are discovered at runtime (via `LookupReferenceDiscoveryService`) rather than stored as persistent objects, a new virtual entity type is needed.

### Entity Definition

A new model JSON file `LookupReferenceDef.json` in `App_Data/Model/`:

```json
{
  "persistentObject": {
    "id": "...",
    "name": "LookupReferenceDef",
    "description": { "en": "Lookup Reference", "nl": "Opzoekreferentie" },
    "clrType": "MintPlayer.Spark.Abstractions.LookupReferenceDef",
    "displayAttribute": "Name",
    "attributes": [
      { "name": "Name", "label": { "en": "Name", "nl": "Naam" }, "dataType": "string", "showedOn": "Query, PersistentObject" },
      { "name": "IsTransient", "label": { "en": "Transient", "nl": "Transient" }, "dataType": "boolean", "showedOn": "Query, PersistentObject" },
      { "name": "ValueCount", "label": { "en": "Values", "nl": "Waarden" }, "dataType": "number", "showedOn": "Query" }
    ]
  },
  "queries": [
    { "name": "GetLookupReferenceDefs", "description": { "en": "Lookup References", "nl": "Opzoekreferenties" }, "source": "Custom.GetAll" }
  ]
}
```

### C# Entity Class

A lightweight class in `MintPlayer.Spark.Core` (or in SparkEditor entities):

```csharp
public class LookupReferenceDef
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public bool IsTransient { get; set; }
    public int ValueCount { get; set; }
}
```

### Actions Class

A new `LookupReferenceDefActions` in `SparkEditor/Actions/`:

```csharp
[Register(typeof(LookupReferenceDefActions), ServiceLifetime.Scoped)]
public partial class LookupReferenceDefActions : DefaultPersistentObjectActions<LookupReferenceDef>
{
    [Inject] private readonly ILookupReferenceDiscoveryService discoveryService;

    public override Task<IEnumerable<LookupReferenceDef>> OnQueryAsync(ISparkSession session)
    {
        var infos = discoveryService.GetAllLookupReferences();
        return Task.FromResult<IEnumerable<LookupReferenceDef>>(
            infos.Select(info => new LookupReferenceDef
            {
                Id = info.Name,
                Name = info.Name,
                IsTransient = info.IsTransient,
                ValueCount = /* resolve from service */
            })
        );
    }
}
```

### AttributeDefinition.json Change

Update the `LookupReferenceType` attribute:

```json
{
  "name": "LookupReferenceType",
  "label": { "en": "Lookup Reference", "nl": "Opzoekreferentie" },
  "dataType": "Reference",
  "query": "GetLookupReferenceDefs",
  "referenceType": "MintPlayer.Spark.Abstractions.LookupReferenceDef",
  "isRequired": false,
  "isVisible": true,
  "isReadOnly": false,
  "order": 11,
  "showedOn": "PersistentObject"
}
```

This approach is consistent with how other Reference attributes work in the SparkEditor (e.g., `ProgramUnitDef.GroupId` → `ProgramUnitGroupDef`). The `LookupReferenceDiscoveryService` already enumerates all available lookup references at startup, so the Actions class simply wraps that data into the entity format the Reference selector expects.

---

## Implementation Phases

### Phase 1: Extract MintPlayer.Spark.Models library

1. Create `MintPlayer.Spark.Models/MintPlayer.Spark.Models.csproj` (net10.0, no dependencies beyond System.Text.Json)
2. Move all JSON transport model files from Abstractions to Models (update namespaces)
3. Move `CustomActionDefinition.cs` and `CustomActionsConfiguration.cs` from `MintPlayer.Spark/Models/`
4. Move `SecurityConfiguration.cs` and `Right.cs` from `MintPlayer.Spark.Authorization/Models/`
5. Create new standalone models: `SecurityGroupDefinition`, `LanguageDefinition`, `TranslationEntry`
6. Add optional back-reference fields to `EntityAttributeDefinition` and `SparkQuery`
7. Add `string? Name` to `CustomActionDefinition`
8. Add `<ProjectReference>` from Abstractions -> Models
9. Update all `using` statements across the solution
10. Verify solution builds and all tests pass

### Phase 2: Migrate SparkEditor to shared models

1. Delete `SparkEditor/SparkEditor/Entities/` folder entirely
2. Update `SparkEditorContext.cs` to use shared model types
3. Update all Action classes to use shared model types
4. Update `SparkEditorFileService` to deserialize directly to shared models (use `TranslatedString` instead of `GetRawText()`, use `Guid` IDs instead of prefixed strings)
5. Update `SparkEditor.csproj` references
6. Update model JSON files (`App_Data/Model/*.json`) `clrType` fields to reference shared model namespaces

### Phase 3: Fix model JSON datatypes (Reference + LookupReference)

Update model JSON files for:
- All Reference attributes (add `dataType: "Reference"`, `referenceType`, `query`)
- All LookupReference attributes (add `lookupReferenceType`)
- Register transient LookupReferences in SparkEditor startup
- Fix `ProgramUnitDef.GroupId` readOnly flag

**LookupReferenceDef entity (for `AttributeDefinition.LookupReferenceType`):**
1. Create `LookupReferenceDef` C# class (Id, Name, IsTransient, ValueCount)
2. Create `App_Data/Model/LookupReferenceDef.json` with entity definition and `GetLookupReferenceDefs` query
3. Create `LookupReferenceDefActions` that uses `LookupReferenceDiscoveryService` to enumerate available lookups
4. Update `AttributeDefinition.json`: change `LookupReferenceType` from `dataType: "string"` to `dataType: "Reference"` with `query: "GetLookupReferenceDefs"` and `referenceType` pointing to the new entity

### Phase 4: TranslatedString as first-class dataType

1. Add `"translatedString"` dataType recognition to the ng-spark frontend
2. Create a TranslatedString editor component (inputs for each configured language)
3. Add `TranslatedString` case to `GetDataType()` in `ModelSynchronizer.cs` and `EntityMapper.cs`
4. Update model JSON files to use `dataType: "translatedString"` for the 13 identified attributes

### Phase 5: Flags enum support (EShowedOn as multiselect)

The `EShowedOn` enum uses `[Flags]` and has values `Query = 1`, `PersistentObject = 2`. Attributes like `AttributeDefinition.ShowedOn` and `CustomActionDef.ShowedOn` serialize as comma-separated strings (e.g., `"Query, PersistentObject"`). Currently rendered as a plain text input.

**Required changes:**
1. Add a new dataType `"flagsEnum"` (or reuse LookupReference with a `displayType: "Multiselect"` option)
2. The backend `GetDataType()` should detect `[Flags]` enums and return `"flagsEnum"`
3. The ng-spark frontend needs a `<bs-multiselect>` (or checkbox group) component for flags enums
4. The serialization should handle combining/splitting the comma-separated values
5. Update SparkEditor model JSON `ShowedOn` attributes to use the new dataType

**Approach considerations:**
- A dedicated `"flagsEnum"` dataType is cleaner than overloading LookupReference
- The backend can emit the available values via a new endpoint or embed them in the attribute definition
- The frontend component should render checkboxes for each flag value, combining them into the comma-separated string

### Phase 6: Validation and UX polish

- Add appropriate validation rules to Reference attributes (e.g., `isRequired` where applicable)
- Verify all queries referenced in Reference attributes exist and return correct data
- Test end-to-end: create/edit all SparkEditor entity types through the WebView UI

---

## Issues Found During Implementation

### Issue: PO detail page "Object not found" (fixed)

**Symptom:** Clicking an item in a query list navigated to `/po/{alias}/{guid}` but returned "Object with ID {guid} not found".

**Root cause:** When the `SparkEditorFileService` was rewritten to use shared models, `LoadPersistentObject(string id)` was comparing `po.SourceFile == id` (a file path) instead of `po.Id.ToString() == id` (the GUID). The framework passes the entity's GUID as the `id` parameter.

**Fix:** Changed comparison to `po.Id.ToString() == id`.

### Issue: TranslatedString attributes show `[object Object]` in query lists (fixed)

**Symptom:** The Label column in `<bs-datatable>` query lists rendered as `[object Object]`.

**Root cause:** Two-part problem:
1. The SparkEditor model JSON files still had `"dataType": "string"` for TranslatedString attributes, so the `attributeValue` pipe didn't know to call `resolveTranslation()`.
2. The `attributeValue` pipe had no handler for the `translatedString` dataType.

**Fix:**
1. Updated all 13 TranslatedString attributes across 9 model JSON files to `"dataType": "translatedString"`.
2. Added `translatedString` handling to `attributeValue` pipe, `spark-po-detail.component.html`, and `spark-po-form.component.html` (with editing modal).

### Issue: Model JSON `clrType` references stale entity class names (fixed earlier)

**Symptom:** All queries returned empty results after deleting SparkEditor Entities folder.

**Root cause:** The `clrType` fields in model JSON files still referenced `SparkEditor.Entities.*` but the entity classes were replaced by shared models in `MintPlayer.Spark.Abstractions.*`.

**Fix:** Updated all 10 model JSON `clrType` fields to reference the shared model types.

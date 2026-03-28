# PRD: Spark Editor - Visual Configuration Tool for MintPlayer.Spark

**Version:** 1.0
**Date:** 2026-03-28
**Status:** Draft
**Author:** Claude (AI-assisted design)

---

## 1. Executive Summary

MintPlayer.Spark uses JSON configuration files (`App_Data/`) as the basis for defining entities, queries, program units, security, translations, culture, and custom actions. Currently, developers must hand-edit these files with no schema validation, no autocomplete, and no visual tooling. This leads to errors, slow iteration, and a steep learning curve.

This PRD defines a **Spark Editor** initiative with four deliverables:

| # | Deliverable | Purpose |
|---|-------------|---------|
| 1 | **JSON Schema Package** | Hosted JSON schemas for all Spark config files, enabling IntelliSense in any editor |
| 2 | **VS Code Extension** | Launches a local Spark Editor web app and opens it in a VS Code webview tab |
| 3 | **Visual Studio Extension** | Same experience in Visual Studio via a tool window with WebView2 |
| 4 | **Spark Editor Web Application** | An Angular + Spark backend that provides a full management UI for editing JSON config files, modeled after the Vidyano Management program unit |

The Spark Editor web application is itself a Spark app. Instead of RavenDB, its Actions classes read/write the developer's `App_Data/` JSON files directly. This dogfoods the Spark framework and provides the familiar Spark UI for editing Spark configuration.

---

## 2. Problem Statement

### Current Pain Points

1. **No schema validation** -- Developers get no feedback when they write invalid JSON structures. A typo in a property name silently breaks the app at runtime.
2. **No IntelliSense** -- IDEs show no autocomplete, no descriptions, and no type hints for Spark JSON files.
3. **Manual GUID generation** -- Entity IDs, query IDs, attribute IDs, group IDs, and program unit IDs all require manually generating GUIDs.
4. **No referential integrity** -- There's no tooling to verify that a query's `source` references a valid context property or custom method, that a program unit's `queryId` matches an actual query, or that security `rights` reference valid entity types.
5. **No visual overview** -- Developers can't see "all persistent objects and their attributes at a glance" the way the Vidyano Management UI allows.
6. **Error-prone translations** -- TranslatedString objects require manually adding entries for each language, with no visibility into missing translations.
7. **Complex cross-file relationships** -- Entity models reference queries, queries reference entity types, program units reference queries, security references entities and custom actions, custom actions reference entities. These relationships span multiple files and are hard to reason about.

### Who Is Affected

- **Spark application developers** -- Anyone building apps with MintPlayer.Spark who needs to configure entities, queries, UI structure, security, and translations.
- **New developers** -- The learning curve for understanding all 6 JSON file types and their relationships is steep.

---

## 3. Goals & Non-Goals

### Goals

- Provide JSON schemas for all 6 Spark configuration file types, hosted publicly
- Provide IDE extensions (VS Code + Visual Studio) that offer a one-click visual editor
- Build a management UI (modeled after Vidyano's) that allows CRUD operations on all configuration objects
- The editor backend is a Spark application itself, proving the framework's flexibility
- Auto-generate GUIDs for new entities, attributes, queries, etc.
- Validate referential integrity across configuration files
- Support all TranslatedString fields with a multi-language editing experience

### Non-Goals

- **Not a runtime admin panel** -- This is a developer-time configuration tool, not a production admin dashboard
- **Not a code generator** -- The editor modifies JSON files, not C# code (though it could be extended later)
- **Not a replacement for the Synchronize command** -- The `--spark-synchronize-model` CLI command generates/updates JSON from C# models and must be preserved. However, the editor SHOULD expose this as a "Synchronize" custom action button (visible in the Persistent Objects toolbar, just like Vidyano's), which triggers the same synchronization logic. This gives developers both CLI and UI access to the same operation.
- **Business rules, Jobs, Patches, Plugins, Reports** -- These Vidyano Management features are not yet available in Spark and are out of scope
- **Data Types** -- Spark doesn't support custom data types; out of scope
- **Websites** -- Spark only supports a single website; out of scope

---

## 4. Architecture Overview

```
+-------------------------------------------------------------------+
|                     Developer's Machine                            |
|                                                                    |
|  +-----------+     +------------------------------------------+   |
|  | VS Code   |     |  Spark Editor (localhost:PORT)            |   |
|  | Extension |---->|  ASP.NET Core (Kestrel) + Angular SPA    |   |
|  +-----------+     |                                           |   |
|       OR           |  Actions classes read/write App_Data/     |   |
|  +-----------+     |  JSON files from the TARGET project       |   |
|  | Visual    |     |  (not RavenDB)                            |   |
|  | Studio    |---->|                                           |   |
|  | Extension |     +------------------------------------------+   |
|  +-----------+                                                     |
|                                                                    |
|  +--------------------+     +----------------------------------+  |
|  | Target Spark App   |     | json.spark.mintplayer.com        |  |
|  | (e.g., Fleet)      |     | Static site hosting JSON schemas |  |
|  | App_Data/*.json <--+     +----------------------------------+  |
|  +--------------------+           (hosted via Docker/Traefik)     |
+-------------------------------------------------------------------+
```

### Component Breakdown

#### 4.1 JSON Schema Package (`Schemas/`)

A static web project that hosts JSON Schema files for all Spark configuration types:

| Schema File | Validates |
|-------------|-----------|
| `entity-type.schema.json` | `App_Data/Model/*.json` (EntityTypeFile) |
| `program-units.schema.json` | `App_Data/programUnits.json` |
| `translations.schema.json` | `App_Data/translations.json` |
| `culture.schema.json` | `App_Data/culture.json` |
| `security.schema.json` | `App_Data/security.json` |
| `custom-actions.schema.json` | `App_Data/customActions.json` |

Hosted at `https://json.spark.mintplayer.com/schemas/v1/{schema-file}`.

Developers reference these from their JSON files:
```json
{
  "$schema": "https://json.spark.mintplayer.com/schemas/v1/entity-type.schema.json",
  "persistentObject": { ... },
  "queries": [ ... ]
}
```

**Deployment**: Dockerfile (multi-stage: Node build + nginx) + docker-compose.yml with Traefik labels for `json.spark.mintplayer.com`. Same pattern as [mintplayer-ng-bootstrap](https://github.com/MintPlayer/mintplayer-ng-bootstrap).

#### 4.2 VS Code Extension (`extensions/vscode/`)

**Activation**: When workspace contains an `App_Data/` folder with Spark JSON files.

**Behavior**:
1. On activation (or command `spark.openEditor`), find an available TCP port via `net.createServer().listen(0)`
2. Spawn the Spark Editor .NET process: `dotnet SparkEditor.dll --port {port} --app-data "{path/to/App_Data}"`
3. Wait for the server to report ready (stdout message)
4. Create a `WebviewPanel` with an iframe pointing to `http://localhost:{port}`
5. Add a command `Spark: Open Editor` to the Command Palette and an icon in the Activity Bar / Editor Title Bar
6. On deactivation, kill the .NET process

**Package manifest contributions**:
- Command: `spark.openEditor` ("Spark: Open Editor")
- Menu: Editor title bar icon, Explorer context menu on `App_Data/` folders
- Activation: `workspaceContains:**/App_Data/programUnits.json`

#### 4.3 Visual Studio Extension (`extensions/visualstudio/`)

**Architecture**: In-process VSIX using `Community.VisualStudio.Toolkit` with WebView2.

**Behavior**:
1. Menu item under `Tools > Spark Editor` (defined in `.vsct`)
2. Opens a tool window (`BaseToolWindow<T>`) docked in the document well
3. The tool window hosts a WPF `UserControl` with a `WebView2` control
4. On first open, starts embedded Kestrel on port 0 (in-process, same .NET runtime)
5. Reads the assigned port from `IServerAddressesFeature`
6. Navigates WebView2 to `http://localhost:{port}`
7. Passes the solution's `App_Data/` path via Kestrel configuration

**Advantages over VS Code**: Kestrel runs in-process (no child process spawn), sharing the same .NET runtime. Faster startup, simpler lifecycle.

#### 4.4 Spark Editor Web Application (`SparkEditor/`)

This is the core deliverable. It's a **Spark application** that uses the Spark framework itself, but with custom Actions classes that read/write JSON files instead of RavenDB.

##### 4.4.1 Backend: File-Based Spark Application

**Key insight**: The Spark framework already abstracts persistence through Actions classes. By overriding `OnQueryAsync`, `OnLoadAsync`, `OnSaveAsync`, and `OnDeleteAsync`, we can make a Spark app that operates on JSON files instead of a database.

**SparkContext replacement**: `SparkEditorContext` -- a context that doesn't connect to RavenDB but instead provides queryable collections backed by in-memory data loaded from JSON files.

**Entity types for the editor** (each maps to a section of the JSON config files):

| Editor Entity | Source JSON File | Management Section |
|---------------|------------------|--------------------|
| `PersistentObjectDefinition` | `App_Data/Model/*.json` → `persistentObject` | Fleet > Persistent Objects |
| `PersistentObjectAttributeDefinition` | `App_Data/Model/*.json` → `persistentObject.attributes[]` | Fleet > PO Attributes |
| `QueryDefinition` | `App_Data/Model/*.json` → `queries[]` | Fleet > Queries |
| `CustomActionDefinition` | `App_Data/customActions.json` | Service > Custom Actions |
| `ProgramUnitGroupDefinition` | `App_Data/programUnits.json` → `programUnitGroups[]` | Client > Program Units (groups) |
| `ProgramUnitDefinition` | `App_Data/programUnits.json` → `...programUnits[]` | Client > Program Units (items) |
| `SecurityGroupDefinition` | `App_Data/security.json` → `groups` | Security > Groups |
| `SecurityRightDefinition` | `App_Data/security.json` → `rights[]` | Security > Rights |
| `LanguageDefinition` | `App_Data/culture.json` → `languages` | Culture > Languages |
| `TranslationDefinition` | `App_Data/translations.json` | Culture > Messages |
| `SettingDefinition` | (future: `App_Data/settings.json`) | Advanced > Settings |

**Actions class pattern** (example for PersistentObjectDefinition):

```csharp
public class PersistentObjectDefinitionActions
    : DefaultPersistentObjectActions<PersistentObjectDefinition>
{
    [Inject] private readonly ISparkEditorFileService _fileService;

    public override Task<IEnumerable<PersistentObjectDefinition>> OnQueryAsync(
        IAsyncDocumentSession session)
    {
        // Read all App_Data/Model/*.json files
        // Extract persistentObject from each
        // Return as IEnumerable
        return _fileService.LoadAllPersistentObjects();
    }

    public override Task<PersistentObjectDefinition?> OnLoadAsync(
        IAsyncDocumentSession session, string id)
    {
        return _fileService.LoadPersistentObject(id);
    }

    public override Task<PersistentObjectDefinition> OnSaveAsync(
        IAsyncDocumentSession session, PersistentObject persistentObject)
    {
        var entity = MapFromPersistentObject(persistentObject);
        _fileService.SavePersistentObject(entity);
        return Task.FromResult(entity);
    }

    public override Task OnDeleteAsync(
        IAsyncDocumentSession session, string id)
    {
        _fileService.DeletePersistentObject(id);
        return Task.CompletedTask;
    }
}
```

**ISparkEditorFileService**: Core service that:
- Takes a `--app-data` command-line argument specifying the target project's `App_Data/` path
- Reads/writes JSON files with `System.Text.Json`
- Maintains an in-memory cache with file-watcher invalidation
- Generates GUIDs for new entities
- Validates referential integrity on save

##### 4.4.2 Frontend: Angular SPA (Management Program Unit)

The frontend uses the existing `@mintplayer/ng-spark` library components (`SparkQueryListComponent`, `SparkPoFormComponent`, etc.) since the editor IS a Spark app. The Spark framework renders the UI automatically based on the editor's own entity metadata.

The editor's `App_Data/` folder contains its own entity definitions that describe how to edit Spark configuration files. This is **meta-configuration**: Spark configuration files that define how to edit Spark configuration files.

**Navigation structure** (modeled after Vidyano Management):

```
Management
├── {App Name}
│   ├── Persistent Objects    (list all entities, click to edit)
│   │   ├── [PO Detail]
│   │   │   ├── Persistent Object tab (properties)
│   │   │   ├── Attributes tab (list/edit attributes)
│   │   │   ├── Queries tab (associated queries)
│   │   │   ├── Rights tab (security permissions for this entity)
│   │   │   ├── Tabs tab (attribute tab grouping)
│   │   │   └── Groups tab (attribute groups within tabs)
│   │   └── ...
│   ├── PO Attributes         (flat list of ALL attributes across all POs)
│   └── Queries               (list all queries, click to edit)
├── Service
│   └── Custom Actions        (list/edit custom action definitions)
├── Client
│   └── Program Units         (list/edit program unit groups and items)
├── Security
│   ├── Groups                (manage security groups)
│   └── Rights                (manage permission assignments)
├── Culture
│   ├── Languages             (manage available languages)
│   └── Messages              (manage translation keys/values)
└── Advanced
    └── Settings              (application settings)
```

---

## 5. Detailed Specifications

### 5.1 JSON Schemas

Each schema must precisely reflect the C# models used by the Spark framework loaders.

#### 5.1.1 Entity Type Schema (`entity-type.schema.json`)

Validates files matching the `EntityTypeFile` structure:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://json.spark.mintplayer.com/schemas/v1/entity-type.schema.json",
  "title": "Spark Entity Type Definition",
  "description": "Defines a persistent object (entity) and its associated queries for MintPlayer.Spark",
  "type": "object",
  "required": ["persistentObject"],
  "properties": {
    "$schema": { "type": "string" },
    "persistentObject": { "$ref": "#/$defs/EntityTypeDefinition" },
    "queries": {
      "type": "array",
      "items": { "$ref": "#/$defs/SparkQuery" }
    }
  },
  "$defs": {
    "TranslatedString": {
      "type": "object",
      "description": "An object with language codes as keys and translated text as values",
      "additionalProperties": { "type": "string" },
      "examples": [{ "en": "Person", "fr": "Personne", "nl": "Persoon" }]
    },
    "EntityTypeDefinition": {
      "type": "object",
      "required": ["id", "name", "clrType", "attributes"],
      "properties": {
        "id": { "type": "string", "format": "uuid" },
        "name": { "type": "string" },
        "description": { "$ref": "#/$defs/TranslatedString" },
        "alias": { "type": "string", "description": "URL-friendly name, auto-generated from name if omitted" },
        "clrType": { "type": "string", "description": "Full .NET type name (e.g., 'Fleet.Entities.Car')" },
        "queryType": { "type": "string", "description": "Projection type for index queries" },
        "indexName": { "type": "string", "description": "RavenDB index name" },
        "displayFormat": { "type": "string", "description": "Template string for breadcrumb display (e.g., '{Street}, {City}')" },
        "displayAttribute": { "type": "string", "description": "Single attribute name for display fallback" },
        "tabs": { "type": "array", "items": { "$ref": "#/$defs/AttributeTab" } },
        "groups": { "type": "array", "items": { "$ref": "#/$defs/AttributeGroup" } },
        "attributes": { "type": "array", "items": { "$ref": "#/$defs/EntityAttributeDefinition" } },
        "queries": { "type": "array", "items": { "type": "string" }, "description": "Query IDs shown on detail pages" }
      }
    },
    "EntityAttributeDefinition": {
      "type": "object",
      "required": ["id", "name", "dataType"],
      "properties": {
        "id": { "type": "string", "format": "uuid" },
        "name": { "type": "string" },
        "label": { "$ref": "#/$defs/TranslatedString" },
        "dataType": {
          "type": "string",
          "enum": ["string", "number", "boolean", "datetime", "date", "color", "Reference", "AsDetail", "KeyValuePair"]
        },
        "isRequired": { "type": "boolean", "default": false },
        "isVisible": { "type": "boolean", "default": true },
        "isReadOnly": { "type": "boolean", "default": false },
        "order": { "type": "integer" },
        "query": { "type": "string", "description": "Query ID for Reference type attributes" },
        "referenceType": { "type": "string", "description": "CLR type for Reference attributes" },
        "asDetailType": { "type": "string", "description": "CLR type for AsDetail attributes" },
        "isArray": { "type": "boolean", "default": false },
        "editMode": { "type": "string", "enum": ["modal", "inline"], "default": "modal" },
        "lookupReferenceType": { "type": "string" },
        "inCollectionType": { "type": "boolean" },
        "inQueryType": { "type": "boolean" },
        "showedOn": {
          "type": "string",
          "enum": ["Query", "PersistentObject", "Query, PersistentObject"],
          "default": "Query, PersistentObject"
        },
        "rules": { "type": "array", "items": { "$ref": "#/$defs/ValidationRule" } },
        "group": { "type": "string", "format": "uuid", "description": "AttributeGroup ID" },
        "columnSpan": { "type": "integer", "default": 1 },
        "renderer": { "type": "string", "description": "Custom renderer component name" },
        "rendererOptions": { "type": "object", "description": "Renderer configuration" }
      }
    },
    "ValidationRule": {
      "type": "object",
      "required": ["type"],
      "properties": {
        "type": { "type": "string", "enum": ["minLength", "maxLength", "email", "url", "regex", "range"] },
        "value": {},
        "min": { "type": "integer" },
        "max": { "type": "integer" },
        "message": { "$ref": "#/$defs/TranslatedString" }
      }
    },
    "AttributeTab": {
      "type": "object",
      "required": ["id", "name"],
      "properties": {
        "id": { "type": "string", "format": "uuid" },
        "name": { "$ref": "#/$defs/TranslatedString" },
        "order": { "type": "integer" }
      }
    },
    "AttributeGroup": {
      "type": "object",
      "required": ["id", "name"],
      "properties": {
        "id": { "type": "string", "format": "uuid" },
        "name": { "$ref": "#/$defs/TranslatedString" },
        "tab": { "type": "string", "format": "uuid", "description": "AttributeTab ID" },
        "order": { "type": "integer" },
        "columnCount": { "type": "integer", "default": 2 }
      }
    },
    "SparkQuery": {
      "type": "object",
      "required": ["id", "name", "source"],
      "properties": {
        "id": { "type": "string", "format": "uuid" },
        "name": { "type": "string" },
        "description": { "$ref": "#/$defs/TranslatedString" },
        "source": {
          "type": "string",
          "pattern": "^(Database|Custom)\\..+$",
          "description": "Data source: 'Database.PropertyName' or 'Custom.MethodName'"
        },
        "alias": { "type": "string" },
        "sortColumns": {
          "type": "array",
          "items": {
            "type": "object",
            "required": ["attributeName"],
            "properties": {
              "attributeName": { "type": "string" },
              "direction": { "type": "string", "enum": ["Ascending", "Descending"], "default": "Ascending" }
            }
          }
        },
        "renderMode": { "type": "string", "enum": ["Pagination", "Streaming"], "default": "Pagination" },
        "indexName": { "type": "string" },
        "useProjection": { "type": "boolean" },
        "entityType": { "type": "string" },
        "isStreamingQuery": { "type": "boolean" }
      }
    }
  }
}
```

#### 5.1.2 Program Units Schema (`program-units.schema.json`)

```
Root: { programUnitGroups: ProgramUnitGroup[] }
ProgramUnitGroup: { id, name (TranslatedString), icon?, order, programUnits: ProgramUnit[] }
ProgramUnit: { id, name (TranslatedString), icon?, type ("query"|"persistentObject"), queryId?, persistentObjectId?, order, alias? }
```

#### 5.1.3 Security Schema (`security.schema.json`)

```
Root: { groups: { [guid]: TranslatedString }, groupComments?: { [guid]: TranslatedString }, rights: Right[] }
Right: { id, resource (pattern: "{Action}/{EntityName}"), groupId, isDenied?, isImportant? }
Action enum: Query, Read, Edit, New, Delete, QueryRead, QueryReadEdit, QueryReadEditNew, QueryReadEditNewDelete
```

#### 5.1.4 Culture Schema (`culture.schema.json`)

```
Root: { languages: { [code]: TranslatedString }, defaultLanguage: string }
```

#### 5.1.5 Translations Schema (`translations.schema.json`)

```
Root: { [key: string]: TranslatedString }
```

#### 5.1.6 Custom Actions Schema (`custom-actions.schema.json`)

```
Root: { [actionName: string]: CustomActionDefinition }
CustomActionDefinition: { displayName (TranslatedString), icon?, description?, showedOn?, selectionRule?, refreshOnCompleted?, confirmationMessageKey?, offset? }
```

### 5.2 Spark Editor Entity Model

The editor itself is a Spark app. Its `App_Data/Model/` defines entities that represent Spark configuration objects. Here are the key editor entities and their attributes:

#### PersistentObjectDefinition (maps to Fleet > Persistent Objects tab)

| Attribute | DataType | Description | Vidyano Equivalent |
|-----------|----------|-------------|-------------------|
| Type | string | Entity name (e.g., "Car") | Type |
| Label | TranslatedString | Display name | Label |
| Breadcrumb | string | Display format template | Breadcrumb / Context property |
| ContextProperty | string | Collection property name on SparkContext | Context property |
| ClrType | string | Full .NET type name | (implicit) |
| QueryType | string | Projection type for indexes | (implicit) |
| IndexName | string | RavenDB index name | (implicit) |
| IsReadOnly | boolean | Makes all attributes read-only | Is read-only |
| IsHidden | boolean | Hides PO from user | Is hidden |
| IsReferential | boolean | Can be used as a reference | Is referential |
| StateBehavior | enum | None / OpenAfterNew / OpenAsDialog | State behavior |
| QueryLayoutMode | enum | Application / MasterDetail | Query layout mode |
| Description | TranslatedString | Purpose description | Description |

**Detail tabs (as sub-queries)**:
- **Attributes** -- Lists all `EntityAttributeDefinition` for this PO
- **Queries** -- Lists all `SparkQuery` whose entityType matches this PO
- **Rights** -- Lists all security rights referencing this entity name
- **Tabs** -- Lists `AttributeTab` definitions for this PO
- **Groups** -- Lists `AttributeGroup` definitions for this PO

#### PersistentObjectAttributeDefinition (maps to Fleet > PO Attributes tab)

| Attribute | DataType | Description |
|-----------|----------|-------------|
| Name | string | Property name |
| Label | TranslatedString | Display label |
| DataType | enum | string, number, boolean, datetime, date, color, Reference, AsDetail, KeyValuePair |
| IsRequired | boolean | Mandatory field |
| IsVisible | boolean | Shown in UI |
| IsReadOnly | boolean | Cannot edit |
| Order | integer | Display sequence |
| ShowedOn | flags | Query, PersistentObject, or both |
| Group | Reference | AttributeGroup reference |
| ColumnSpan | integer | Grid layout span |
| Renderer | string | Custom renderer name |
| PersistentObject | Reference | Parent PO (back-reference) |

#### QueryDefinition (maps to Fleet > Queries tab)

| Attribute | DataType | Description |
|-----------|----------|-------------|
| Name | string | Functional query name |
| Label | TranslatedString | Display label |
| PersistentObject | Reference | Entity type this query returns |
| Source | string | "Database.X" or "Custom.X" |
| LookupSource | string | For detail query add-existing |
| IsHidden | boolean | Hidden from UI |
| PageSize | integer | Paging block size |
| Description | TranslatedString | Purpose description |

#### CustomActionDefinition (maps to Service > Custom Actions)

| Attribute | DataType | Description |
|-----------|----------|-------------|
| Name | string | Action identifier |
| DisplayName | TranslatedString | Button/menu label |
| Pinned | boolean | Always visible |
| Options | string | Semicolon-separated options |
| GroupAction | boolean | Can execute on multiple items |
| SelectionRule | string | "=0", "=1", ">0" |
| RefreshOnCompleted | boolean | Refresh query after execution |
| ShowedOn | enum | Query, PersistentObject, Both |
| Icon | string | Icon identifier |
| Confirmation | string | Confirmation type (AreYouSure, etc.) |
| Intent | enum | ReadOnly, ReadWrite |
| Description | TranslatedString | Purpose description |

#### ProgramUnitDefinition (maps to Client > Program Units)

| Attribute | DataType | Description |
|-----------|----------|-------------|
| Name | string | Internal name |
| Title | TranslatedString | Display title |
| Offset | integer | Sort order |
| Icon | string | Iconify icon identifier |
| Type | enum | query, persistentObject |
| QueryId | Reference | Referenced query |
| ProgramUnitGroup | Reference | Parent group |

#### SecurityGroupDefinition (maps to Security > Groups)

| Attribute | DataType | Description |
|-----------|----------|-------------|
| Name | TranslatedString | Group name |
| Comment | TranslatedString | Description |

#### SecurityRightDefinition (maps to Security > Rights)

| Attribute | DataType | Description |
|-----------|----------|-------------|
| Resource | string | "{Action}/{EntityName}" |
| Group | Reference | SecurityGroup reference |
| IsDenied | boolean | Deny rule |
| IsImportant | boolean | Audit flag |

#### LanguageDefinition (maps to Culture > Languages)

| Attribute | DataType | Description |
|-----------|----------|-------------|
| Culture | string | Language code (en, fr, nl) |
| Name | TranslatedString | Language name in each language |

#### TranslationDefinition (maps to Culture > Messages)

| Attribute | DataType | Description |
|-----------|----------|-------------|
| Key | string | Translation key |
| Values | TranslatedString | Translated text per language |

### 5.3 File-Based Actions Architecture

The Spark Editor replaces RavenDB interaction with JSON file I/O. The key service:

```csharp
public interface ISparkEditorFileService
{
    // Entity Model files
    IEnumerable<EntityTypeFile> LoadAllEntityTypeFiles();
    EntityTypeFile? LoadEntityTypeFile(string entityName);
    void SaveEntityTypeFile(EntityTypeFile file);
    void DeleteEntityTypeFile(string entityName);

    // Program Units
    ProgramUnitsConfiguration LoadProgramUnits();
    void SaveProgramUnits(ProgramUnitsConfiguration config);

    // Security
    SecurityConfiguration LoadSecurity();
    void SaveSecurity(SecurityConfiguration config);

    // Culture
    CultureConfiguration LoadCulture();
    void SaveCulture(CultureConfiguration config);

    // Translations
    Dictionary<string, TranslatedString> LoadTranslations();
    void SaveTranslations(Dictionary<string, TranslatedString> translations);

    // Custom Actions
    CustomActionsConfiguration LoadCustomActions();
    void SaveCustomActions(CustomActionsConfiguration config);

    // File watching
    event EventHandler<FileChangedEventArgs> FileChanged;
}
```

**Configuration**: The `App_Data` path of the **target project** is passed via:
- Command-line: `--target-app-data "C:\Repos\MyApp\MyApp\App_Data"`
- The editor's own `App_Data/` is separate and contains the editor's entity definitions

### 5.4 Referential Integrity Validation

On save, the editor validates cross-file references:

| Source | Reference | Target |
|--------|-----------|--------|
| Query.source | `Database.X` | Context property name exists |
| Query.source | `Custom.X` | Actions class method exists (best-effort, warns) |
| Query.entityType | entity name | EntityTypeDefinition.name exists |
| ProgramUnit.queryId | GUID | SparkQuery.id exists |
| ProgramUnit.persistentObjectId | GUID | EntityTypeDefinition.id exists |
| SecurityRight.resource | `Action/EntityName` | EntityTypeDefinition.name exists |
| SecurityRight.groupId | GUID | SecurityGroup id exists |
| Attribute.group | GUID | AttributeGroup.id exists |
| Attribute.referenceType | CLR type | EntityTypeDefinition.clrType exists |
| Attribute.asDetailType | CLR type | EntityTypeDefinition.clrType exists |

Warnings are shown in the UI but do not block saves (some references may be to C# code not visible to the editor).

---

## 6. Project Structure

```
MintPlayer.Spark/
├── Schemas/                                    # NEW: JSON schema hosting
│   ├── schemas/v1/
│   │   ├── entity-type.schema.json
│   │   ├── program-units.schema.json
│   │   ├── translations.schema.json
│   │   ├── culture.schema.json
│   │   ├── security.schema.json
│   │   └── custom-actions.schema.json
│   ├── index.html                              # Landing page
│   ├── nginx.conf
│   ├── Dockerfile
│   └── docker-compose.yml
│
├── SparkEditor/                                # NEW: Spark Editor application
│   ├── SparkEditor/                            # ASP.NET Core + Spark backend
│   │   ├── Program.cs
│   │   ├── SparkEditorContext.cs               # File-based context
│   │   ├── Entities/                           # Editor entity models
│   │   ├── Actions/                            # File-backed Actions classes
│   │   ├── Services/
│   │   │   └── SparkEditorFileService.cs       # JSON file I/O
│   │   ├── App_Data/                           # Editor's own Spark config
│   │   │   ├── Model/                          # Entity definitions for the editor itself
│   │   │   ├── programUnits.json
│   │   │   └── translations.json
│   │   └── ClientApp/                          # Angular SPA
│   │       └── src/app/
│   │           ├── app.routes.ts
│   │           ├── shell/                      # Management-style shell
│   │           └── ...
│   └── SparkEditor.Abstractions/               # Shared interfaces
│
├── extensions/                                 # NEW: IDE extensions
│   ├── vscode/                                 # VS Code extension
│   │   ├── package.json
│   │   ├── src/
│   │   │   └── extension.ts
│   │   └── tsconfig.json
│   └── visualstudio/                           # Visual Studio VSIX
│       ├── SparkEditor.Vsix/
│       │   ├── source.extension.vsixmanifest
│       │   ├── SparkEditorPackage.cs
│       │   ├── Commands/
│       │   ├── ToolWindows/
│       │   └── VSCommandTable.vsct
│       └── SparkEditor.Vsix.csproj
```

---

## 7. Deployment & Hosting

### 7.1 JSON Schema Site (json.spark.mintplayer.com)

**Dockerfile** (multi-stage):
```dockerfile
# Stage 1: Build (optional, for any preprocessing)
FROM node:22 AS builder
WORKDIR /app
COPY . .
# If we add a build step (e.g., schema validation, landing page generation)

# Stage 2: Serve
FROM nginx:latest
COPY schemas/ /usr/share/nginx/html/schemas/
COPY index.html /usr/share/nginx/html/
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
```

**docker-compose.yml**:
```yaml
services:
  spark-json-schemas:
    image: ghcr.io/mintplayer/spark-json-schemas:master
    restart: unless-stopped
    labels:
      - "traefik.enable=true"
      - "traefik.http.routers.spark-json-schemas.rule=Host(`json.spark.mintplayer.com`)"
      - "traefik.http.routers.spark-json-schemas.entrypoints=websecure"
      - "traefik.http.routers.spark-json-schemas.tls.certresolver=myresolver"
    networks:
      - web

networks:
  web:
    external: true
```

**GitHub Actions**: Same pattern as mintplayer-ng-bootstrap -- build Docker image, push to GHCR, SSH deploy to VPS with `docker compose pull && down && up -d`.

### 7.2 Spark Editor Distribution

The Spark Editor .NET app is distributed as:
1. **NuGet tool**: `dotnet tool install -g MintPlayer.SparkEditor` (global tool)
2. **Bundled in extensions**: VS Code extension includes the published .NET app; Visual Studio extension embeds it

---

## 8. User Workflows

### Workflow 1: Developer adds `$schema` to JSON files

1. Developer adds `"$schema": "https://json.spark.mintplayer.com/schemas/v1/entity-type.schema.json"` to their entity JSON files
2. VS Code / Visual Studio immediately provide IntelliSense, autocomplete, and validation
3. Errors are underlined with descriptive messages

### Workflow 2: Developer opens Spark Editor from VS Code

1. Developer opens a Spark project in VS Code
2. Extension detects `App_Data/programUnits.json` and activates
3. Developer runs `Spark: Open Editor` from Command Palette (or clicks the Spark icon)
4. Extension spawns the Spark Editor on a random port, opens a webview tab
5. The Management UI loads, showing all Persistent Objects, Queries, Custom Actions, etc.
6. Developer clicks on a Persistent Object, edits attributes, reorders, adds new ones
7. Changes are written directly to the `App_Data/Model/{Entity}.json` file
8. Developer's Spark app picks up changes (hot reload if enabled)

### Workflow 3: Developer creates a new entity via the editor

1. In the Persistent Objects list, developer clicks "New"
2. A form appears with fields: Type (name), Label, CLR Type, Display Format, etc.
3. The editor auto-generates a GUID for the `id`
4. Developer fills in the entity name and CLR type
5. Switches to the Attributes tab and adds attributes (name, dataType, label, validation rules)
6. Switches to the Queries tab and creates a query (auto-generates `Database.{PluralName}` source)
7. On save, a new `App_Data/Model/{EntityName}.json` file is created
8. The developer can also add the entity to a Program Unit in the Client > Program Units section

### Workflow 4: Developer manages security

1. In the Security section, developer sees groups (like Administrators, Users)
2. Creates a new group "Managers" with a translated name
3. Switches to Rights tab, clicks "New"
4. Selects Resource: "QueryReadEdit/Car", Group: "Managers"
5. The right is added to `security.json`

---

## 9. Technical Considerations

### 9.1 File Locking & Concurrency

The editor reads/writes files that the developer may also edit manually. Strategy:
- Use `FileSystemWatcher` to detect external changes and refresh the in-memory cache
- Use advisory file locks during writes (short duration)
- Show a conflict notification if the file changed while the editor had unsaved modifications
- Always pretty-print JSON with consistent formatting (2-space indent, sorted keys where applicable)

### 9.2 No RavenDB Dependency

The Spark Editor must NOT require RavenDB. The `SparkEditorContext` provides queryable collections backed by in-memory lists populated from JSON files. The `IDocumentStore` is replaced with a no-op implementation or the editor uses a custom `DatabaseAccess` that bypasses RavenDB entirely.

**Approach**: Register a custom `IDatabaseAccess` implementation (`SparkEditorDatabaseAccess`) that delegates all operations to the `ISparkEditorFileService` instead of to RavenDB sessions.

### 9.3 Port Discovery for Extensions

Both extensions need to discover the dynamically assigned port:

- **VS Code**: Spawn process, parse stdout for "Now listening on: http://localhost:{port}"
- **Visual Studio**: Start Kestrel in-process, read port from `IServerAddressesFeature` after `StartAsync()`

### 9.4 TranslatedString Editing UX

TranslatedString attributes need special handling in the form:
- Show a mini-form with one text field per language (loaded from the target app's `culture.json`)
- If `culture.json` doesn't exist, fall back to a single "en" field
- Highlight missing translations with a warning indicator

### 9.5 GUID Generation

All new entities, attributes, queries, groups, etc. should have GUIDs auto-generated on creation. The editor should use `Guid.NewGuid()` and display the ID as read-only after creation.

---

## 10. Phases

### Phase 0: Spark Core Storage Abstraction (Prerequisite)

**Deliverables**:
- Refactor `MintPlayer.Spark` to extract storage-agnostic core from RavenDB-specific code
- New package split:
  - `MintPlayer.Spark` -- Core framework (endpoints, Actions base classes, EntityMapper, ModelLoader, etc.)
  - `MintPlayer.Spark.RavenDB` -- RavenDB storage provider (`ISparkStorageProvider` implementation, `IDocumentStore` setup, index creation)
  - `MintPlayer.Spark.FileSystem` -- File-based storage provider (used by the Spark Editor)
- Define `ISparkStorageProvider` interface abstracting session/CRUD operations
- Add `PersistentObjectAttribute.TriggersRefresh` to core + `OnRefresh(...)` lifecycle hook on Actions classes
- Existing demo apps (DemoApp, HR, Fleet) migrate to `MintPlayer.Spark.RavenDB` with no functional changes

**Estimated scope**: Large -- foundational refactor, but enables the editor and future storage providers (SqlServer, etc.)

### Phase 1: JSON Schemas + Hosting

**Deliverables**:
- All 6 JSON schema files
- Dockerfile + docker-compose.yml for hosting
- GitHub Actions pipeline for auto-deploy
- Landing page at `json.spark.mintplayer.com` listing available schemas
- Documentation for adding `$schema` references to existing projects

**Estimated scope**: Small -- mostly static files and infrastructure

### Phase 2: Spark Editor Backend (File-Based Spark App)

**Deliverables**:
- `SparkEditor` project using `MintPlayer.Spark.FileSystem` storage provider
- `ISparkEditorFileService` for reading/writing all 6 JSON file types
- Editor entity models (PersistentObjectDefinition, QueryDefinition, etc.)
- Editor's own `App_Data/` with meta-configuration
- Multi-project support: can target multiple `App_Data/` folders simultaneously (e.g., Fleet + HR in one solution)
- CLI interface: `dotnet SparkEditor.dll --target-app-data {path1} --target-app-data {path2} --port {port}`
- "Synchronize" custom action that invokes `--spark-synchronize-model` on the target project
- C# Actions class / custom query method detection (via assembly scanning or Roslyn)
- Referential integrity validation
- Distributed as a NuGet package (`MintPlayer.Spark.Editor`)

**Estimated scope**: Large -- core of the entire initiative

### Phase 3: Spark Editor Frontend (Angular Management UI)

**Deliverables**:
- Angular SPA using `@mintplayer/ng-spark` components
- Management-style navigation (sidebar with Fleet/Service/Client/Security/Culture/Advanced sections)
- Entity list and detail views for all editor entities
- TranslatedString editing UX
- Cross-reference navigation (click a query to go to its PO, etc.)

**Estimated scope**: Medium -- largely leverages existing ng-spark components

### Phase 4: VS Code Extension

**Deliverables**:
- VS Code extension package
- Auto-detection of Spark projects
- Command + menu item to launch the editor
- WebviewPanel integration with iframe
- Process lifecycle management (spawn/kill .NET process)
- Extension published to VS Code Marketplace via GitHub Actions

**GitHub Actions publishing** (reference: `mintplayer-ng-bootstrap/.github/workflows/publish-master.yml`):
```yaml
- name: Publish VS Code Extension
  uses: HaaLeo/publish-vscode-extension@v1.5.0
  with:
    pat: ${{ secrets.PUBLISH_SPARK_EDITOR_VSCODE }}
    registryUrl: https://marketplace.visualstudio.com
    extensionFile: extensions/vscode/*.vsix
```
- Publisher: MintPlayer
- Secret required: `PUBLISH_SPARK_EDITOR_VSCODE` (VS Marketplace Personal Access Token)

**Estimated scope**: Medium

### Phase 5: Visual Studio Extension

**Deliverables**:
- VSIX project with Community.VisualStudio.Toolkit
- Tool window with WebView2
- Menu item under Tools > Spark Editor
- In-process Kestrel hosting
- Extension published to Visual Studio Marketplace via GitHub Actions

**GitHub Actions publishing**: Use the `cezarypiatek/VsixPublisherAction` or similar action:
```yaml
- name: Publish Visual Studio Extension
  uses: cezarypiatek/VsixPublisherAction@1.1
  with:
    extension-file: extensions/visualstudio/SparkEditor.Vsix/bin/Release/*.vsix
    publish-manifest-file: extensions/visualstudio/publishManifest.json
    personal-access-code: ${{ secrets.PUBLISH_SPARK_EDITOR_VS }}
```
- Publisher: MintPlayer
- Secret required: `PUBLISH_SPARK_EDITOR_VS` (VS Marketplace Personal Access Token)

**Estimated scope**: Medium

---

## 11. Reference: Vidyano Management Feature Mapping

The following table maps Vidyano Management features to their Spark Editor equivalents and relevance:

| Vidyano Section | Subsection | Spark Editor Equivalent | Priority | Notes |
|-----------------|------------|------------------------|----------|-------|
| **Fleet (App)** | Persistent Objects | PO list + detail editor | P0 | Core feature |
| | PO Attributes | Flat attribute list | P0 | With filtering by PO |
| | Queries | Query list + detail editor | P0 | Core feature |
| **Service** | Custom Actions | Custom action editor | P1 | Important for advanced apps |
| | Business Rules | -- | P3 | Not yet in Spark |
| | Jobs | -- | P3 | Not yet in Spark |
| **Client** | Websites | -- | -- | Spark = single website, N/A |
| | Program Units | Program unit editor | P1 | Important for navigation |
| | Notifications | -- | P3 | Not yet in Spark |
| **Security** | Authentication | Auth provider config | P2 | Partially in Spark |
| | Users | -- | P2 | Runtime data, not config |
| | Groups | Security group editor | P1 | Part of security.json |
| | Rights | Permission editor | P1 | Part of security.json |
| **Culture** | Languages | Language editor | P1 | Part of culture.json |
| | Messages | Translation editor | P1 | Part of translations.json |
| **Advanced** | Data Types | -- | -- | Not in Spark |
| | Feedback | -- | P3 | Not yet in Spark |
| | Settings | Settings editor | P2 | Future: settings.json |
| | Reports | -- | P3 | Not yet in Spark |
| | Logs | -- | -- | Runtime, not config |
| | Patches | -- | P3 | Not yet in Spark |
| | Plugins | -- | P3 | Not yet in Spark |
| | Descriptions | -- | P2 | Could map to field help text |

---

## 12. Success Metrics

1. **Schema adoption**: Developers can add `$schema` references and get immediate IntelliSense in VS Code / Visual Studio without any extension installed
2. **Editor usability**: A developer can create a new entity with 5 attributes, a query, and a program unit entry in under 2 minutes using the editor (vs. 10+ minutes manually)
3. **Zero runtime errors from config**: The editor's validation catches all common config errors (missing IDs, broken references, invalid data types) before the Spark app is run
4. **Framework dogfooding**: The Spark Editor IS a Spark app, proving the framework can support non-database backends

---

## 13. Open Questions

1. **~~Should the editor support editing multiple projects simultaneously?~~** -- **Confirmed: Yes, from the start.** The editor must support multiple projects (e.g., a solution with Fleet + HR demo apps) via multiple `--target-app-data` arguments or by scanning the solution for Spark projects.

2. **~~Should the editor detect C# Actions classes and custom query methods?~~** -- **Confirmed: Yes, this is desired.** This enables validating `Custom.X` query sources and showing available custom actions. Implementation options: (a) Roslyn analysis of source code, (b) require the target project to be built first and scan the assembly via reflection, (c) parse the source-generated `AddSparkActions()` output. Approach TBD.

3. **~~Should there be a "Synchronize" action in the editor?~~** -- **Confirmed: Yes.** A "Synchronize" custom action button in the Persistent Objects toolbar will trigger the same `--spark-synchronize-model` logic. Implementation detail: how does the editor invoke this? Options: (a) shell out to `dotnet run --spark-synchronize-model` on the target project, (b) reference the synchronization logic directly via a shared library, (c) invoke it as a CLI command on the target project's built assembly.

4. **~~NuGet package or standalone tool?~~** -- **Confirmed: NuGet package.** The Spark Editor will be distributed as a NuGet package referenced by Spark projects. This integrates naturally with the existing `AddSpark()` pattern and allows the editor to share types with the main Spark packages.

5. **~~Should the Spark framework itself be modified to support non-RavenDB backends?~~** -- **Confirmed: Yes.** The framework will be refactored to abstract the storage layer. This results in a package split:
   - `MintPlayer.Spark` -- Core framework (storage-agnostic)
   - `MintPlayer.Spark.RavenDB` -- RavenDB storage provider
   - `MintPlayer.Spark.SqlServer` -- SQL Server storage provider (future, not in scope now)
   - `MintPlayer.Spark.FileSystem` -- File-based storage provider (used by the Spark Editor)

   This is a prerequisite for the editor and benefits the broader framework. The `IDocumentSession` abstraction will be replaced with a storage-agnostic `ISparkStorageProvider` interface.

6. **~~Triggers Refresh~~** -- **Confirmed: Yes, add to Spark core first.** `PersistentObjectAttribute.TriggersRefresh` will be added to the core framework. When ANY attribute's value changes (text fields, booleans, dropdowns, etc.), the frontend sends a request to the backend's `OnRefresh(...)` method. This method can hide/show attributes, change items in a dropdown, modify values, or update any `PersistentObjectAttribute` property the user sees. This is needed for the editor (e.g., changing a Query's PersistentObject should refresh the available Source options) and is broadly useful for all Spark apps.

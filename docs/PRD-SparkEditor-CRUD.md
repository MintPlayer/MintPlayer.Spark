# PRD: Spark Editor - Full CRUD & File Watching

**Version:** 1.0
**Date:** 2026-04-04
**Status:** Draft
**Parent PRD:** `docs/PRD-SparkEditor.md` (v1.0)
**Scope:** Phase 2 completion -- all write operations + external file change detection

---

## 1. Executive Summary

The SparkEditor can currently **visualize** Persistent Objects, Queries, Attributes, Custom Actions, Program Units, Security Groups/Rights, Languages, and Translations from the target project's `App_Data/` JSON files. However, all write operations (`SavePersistentObject`, `DeletePersistentObject`, `SaveCustomAction`, `DeleteCustomAction`) throw `NotImplementedException`, and there is no file watching for external changes.

This PRD defines the implementation of:
1. **Full CRUD** -- Create, Update, Delete for all 10 editor entity types
2. **File Watching** -- Detect external changes to JSON files and push updates to the webview
3. **Referential Integrity** -- Validate cross-file references on save, warn on broken links
4. **Conflict Detection** -- Detect and handle concurrent edits (editor vs. external)

---

## 2. Current State Analysis

### 2.1 What Works (Read-Only)

| Entity Type | Actions Class | File Service Method | Status |
|-------------|---------------|---------------------|--------|
| EntityTypeDefinition | `PersistentObjectDefinitionActions` | `LoadAllPersistentObjects()`, `LoadPersistentObject(id)` | Read OK |
| EntityAttributeDefinition | `AttributeDefinitionActions` | `LoadAllAttributes()`, `LoadAttributesForPO(name)` | Read OK |
| SparkQuery | `QueryDefinitionActions` | `LoadAllQueries()`, `LoadQueriesForPO(name)` | Read OK |
| CustomActionDefinition | `CustomActionDefActions` | `LoadAllCustomActions()` | Read OK |
| ProgramUnitGroup | `ProgramUnitGroupDefActions` | `LoadAllProgramUnitGroups()` | Read OK |
| ProgramUnit | `ProgramUnitDefActions` | `LoadAllProgramUnits()` | Read OK |
| SecurityGroupDefinition | `SecurityGroupDefActions` | `LoadAllSecurityGroups()` | Read OK |
| Right | `SecurityRightDefActions` | `LoadAllSecurityRights()` | Read OK |
| LanguageDefinition | `LanguageDefActions` | `LoadAllLanguages()` | Read OK |
| TranslationEntry | `TranslationDefActions` | `LoadAllTranslations()` | Read OK |
| LookupReferenceDef | `LookupReferenceDefActions` | N/A (hardcoded enums) | Read OK (read-only by design) |

### 2.2 What's Missing

1. **No OnSaveAsync/OnDeleteAsync** -- None of the 11 Actions classes implement save or delete
2. **No file write methods** -- `ISparkEditorFileService` declares `SavePersistentObject`, `DeletePersistentObject`, `SaveCustomAction`, `DeleteCustomAction`, but all throw `NotImplementedException`. Missing entirely: save/delete for Attributes, Queries, ProgramUnits, ProgramUnitGroups, SecurityGroups, SecurityRights, Languages, Translations
3. **No FileSystemWatcher** -- No detection of external file changes
4. **No conflict detection** -- No mechanism to warn when a file changed while the user was editing
5. **No GUID auto-generation** -- New entities need auto-assigned GUIDs

### 2.3 File-to-Entity Mapping

Understanding which entities live in which files is critical for the write implementation:

```
App_Data/
  Model/{EntityName}.json          # 1 file per entity type
    ├── persistentObject: { ... }  → EntityTypeDefinition (1:1 with file)
    │   ├── .tabs[]                → AttributeTab (embedded in PO)
    │   ├── .groups[]              → AttributeGroup (embedded in PO)
    │   └── .attributes[]          → EntityAttributeDefinition (embedded in PO)
    └── queries: [...]             → SparkQuery[] (embedded in file)

  programUnits.json                # 1 file for all groups + units
    └── programUnitGroups[]        → ProgramUnitGroup (each with nested ProgramUnit[])

  customActions.json               # 1 file for all actions
    └── {actionName}: { ... }      → CustomActionDefinition (keyed by name)

  security.json                    # 1 file for groups + rights
    ├── groups: { guid: name }     → SecurityGroupDefinition
    ├── groupComments: { guid: comment }
    └── rights: [...]              → Right[]

  culture.json                     # 1 file for all languages
    ├── languages: { code: name }  → LanguageDefinition
    └── defaultLanguage: string

  translations.json                # 1 file for all translations
    └── {key}: TranslatedString    → TranslationEntry
```

**Key complexity**: Attributes and Queries are flattened as separate editor entities (separate list views, separate detail pages) but are **embedded** within the PO's JSON file. Saving an attribute means reading the parent PO's JSON file, updating the attribute in the `attributes[]` array, and writing the entire file back.

---

## 3. Goals & Non-Goals

### Goals

- Implement save and delete for all 10 mutable entity types
- Preserve the exact JSON structure and formatting when writing (round-trip fidelity)
- Auto-generate GUIDs for new entities
- Watch for external file changes and refresh the in-memory state
- Push file change notifications to the Angular frontend so the webview re-renders
- Validate referential integrity on save (warn, don't block)
- Handle concurrent edit conflicts gracefully

### Non-Goals

- **LookupReferenceDef CRUD** -- These are hardcoded C# enums, not JSON-editable
- **New entity types** -- No new editor entities beyond the existing 10
- **Undo/redo** -- Out of scope for this PRD
- **Multi-user collaboration** -- Single-user editor (one developer at a time)
- **Schema migration** -- No versioned migrations for JSON format changes

---

## 4. Detailed Design

### 4.1 `ISparkEditorFileService` Interface Expansion

The interface needs save/delete methods for every mutable entity type. Since Attributes and Queries are embedded within PO files, their save/delete methods need the parent PO identifier.

```csharp
public interface ISparkEditorFileService
{
    IReadOnlyList<string> TargetPaths { get; }

    // === Persistent Objects (Model/{Name}.json files) ===
    List<EntityTypeDefinition> LoadAllPersistentObjects();
    EntityTypeDefinition? LoadPersistentObject(string id);
    void SavePersistentObject(EntityTypeDefinition po);          // Create or update
    void DeletePersistentObject(string id);                      // Deletes the .json file

    // === Attributes (embedded in Model/{POName}.json) ===
    List<EntityAttributeDefinition> LoadAllAttributes();
    List<EntityAttributeDefinition> LoadAttributesForPO(string poName);
    void SaveAttribute(string poName, EntityAttributeDefinition attr);   // NEW
    void DeleteAttribute(string poName, string attributeId);             // NEW

    // === Queries (embedded in Model/{POName}.json) ===
    List<SparkQuery> LoadAllQueries();
    List<SparkQuery> LoadQueriesForPO(string poName);
    void SaveQuery(string poName, SparkQuery query);                     // NEW
    void DeleteQuery(string poName, string queryId);                     // NEW

    // === Custom Actions (customActions.json) ===
    List<CustomActionDefinition> LoadAllCustomActions();
    void SaveCustomAction(string name, CustomActionDefinition action);   // Implement
    void DeleteCustomAction(string name);                                // Implement

    // === Program Unit Groups (programUnits.json) ===
    List<ProgramUnitGroup> LoadAllProgramUnitGroups();
    void SaveProgramUnitGroup(ProgramUnitGroup group);                   // NEW
    void DeleteProgramUnitGroup(string id);                              // NEW

    // === Program Units (nested in programUnits.json) ===
    List<ProgramUnit> LoadAllProgramUnits();
    void SaveProgramUnit(ProgramUnit unit);                              // NEW
    void DeleteProgramUnit(string id);                                   // NEW

    // === Security Groups (security.json) ===
    List<SecurityGroupDefinition> LoadAllSecurityGroups();
    void SaveSecurityGroup(SecurityGroupDefinition group);               // NEW
    void DeleteSecurityGroup(string id);                                 // NEW

    // === Security Rights (security.json) ===
    List<Right> LoadAllSecurityRights();
    void SaveSecurityRight(Right right);                                 // NEW
    void DeleteSecurityRight(string id);                                 // NEW

    // === Languages (culture.json) ===
    List<LanguageDefinition> LoadAllLanguages();
    void SaveLanguage(LanguageDefinition language);                      // NEW
    void DeleteLanguage(string id);                                      // NEW

    // === Translations (translations.json) ===
    List<TranslationEntry> LoadAllTranslations();
    void SaveTranslation(TranslationEntry translation);                  // NEW
    void DeleteTranslation(string id);                                   // NEW

    // === File Watching ===
    event EventHandler<FileChangedEventArgs> FileChanged;                // NEW
    void StartWatching();                                                // NEW
    void StopWatching();                                                 // NEW
}
```

### 4.2 File Write Strategies

Each JSON file type has a different structure, requiring different write strategies:

#### 4.2.1 Model Files (one file per PO)

**File pattern**: `App_Data/Model/{EntityName}.json`

**Round-trip approach**: Use `JsonNode` (mutable JSON DOM) instead of `JsonDocument` (read-only) to preserve unknown properties and formatting.

```csharp
// Pseudocode for SavePersistentObject
void SavePersistentObject(EntityTypeDefinition po)
{
    // 1. Determine target file path
    var filePath = po.SourceFile
        ?? Path.Combine(TargetPaths[0], "Model", $"{po.Name}.json");

    // 2. Load existing JSON (or create new)
    JsonNode root;
    if (File.Exists(filePath))
    {
        root = JsonNode.Parse(File.ReadAllText(filePath));
    }
    else
    {
        root = new JsonObject();
        po.Id = po.Id == Guid.Empty ? Guid.NewGuid() : po.Id;
    }

    // 3. Merge PO properties into the "persistentObject" node
    var poNode = root["persistentObject"]?.AsObject() ?? new JsonObject();
    poNode["id"] = po.Id.ToString();
    poNode["name"] = po.Name;
    // ... all other properties

    // 4. Write back with pretty-print
    root["persistentObject"] = poNode;
    File.WriteAllText(filePath, root.ToJsonString(JsonOptions));
}
```

**Key decisions**:
- New POs create a new file: `App_Data/Model/{po.Name}.json`
- The `SourceFile` property tracks which file a PO was loaded from (already exists on `EntityTypeDefinition`)
- When renaming a PO, rename the file (old file deleted, new file created)
- Deleting a PO deletes the entire `.json` file (both PO and its queries)

**SaveAttribute / DeleteAttribute**:
- Read the parent PO's file
- Find or insert the attribute in `persistentObject.attributes[]`
- Match by `id` (GUID); if no match, append (create)
- Write the entire file back

**SaveQuery / DeleteQuery**:
- Read the parent PO's file
- Find or insert the query in `queries[]`
- Match by `id` (GUID)
- Write the entire file back

#### 4.2.2 Single-Object Files

**programUnits.json**:
- Load entire file as `JsonNode`
- For SaveProgramUnitGroup: find by `id` in `programUnitGroups[]`, update or append
- For SaveProgramUnit: find the parent group (by `unit.GroupId`), then find unit by `id` in `programUnits[]`
- For DeleteProgramUnitGroup: remove from array, also removes nested units
- For DeleteProgramUnit: find in parent group, remove from array

**customActions.json**:
- Load entire file as `JsonNode`
- For Save: set/replace the `{actionName}` property
- For Delete: remove the `{actionName}` property

**security.json**:
- Load entire file as `JsonNode`
- For SaveSecurityGroup: set `groups[{guid}]` and optionally `groupComments[{guid}]`
- For DeleteSecurityGroup: remove from `groups` and `groupComments`; also remove related `rights[]` entries
- For SaveSecurityRight: find by `id` in `rights[]`, update or append
- For DeleteSecurityRight: remove from `rights[]`

**culture.json**:
- Load entire file as `JsonNode`
- For SaveLanguage: set `languages[{code}]`
- For DeleteLanguage: remove from `languages`; if deleting `defaultLanguage`, set to first remaining

**translations.json**:
- Load entire file as `JsonNode`
- For SaveTranslation: set `{key}` property
- For DeleteTranslation: remove `{key}` property

#### 4.2.3 Multi-Target Path Handling

The editor supports multiple `--target-app-data` paths. For **read** operations, all paths are merged. For **write** operations:

- **Existing entities**: Write back to the same `TargetPath` the entity was loaded from. This requires tracking the source path per entity.
- **New entities**: Write to the **first** target path (`TargetPaths[0]`) by default. The UI could optionally let the user pick the target path when multiple are configured.

**Implementation**: Add a `SourcePath` property to each entity model (or use the existing `SourceFile` on `EntityTypeDefinition` and extend the pattern to other types). During load, tag each entity with its source target path.

### 4.3 Actions Class Updates

Each Actions class needs `OnSaveAsync` and `OnDeleteAsync`. The pattern is consistent:

```csharp
// Example: PersistentObjectDefinitionActions
public override Task<EntityTypeDefinition> OnSaveAsync(
    ISparkSession session, PersistentObject persistentObject)
{
    // 1. Map PersistentObject attributes to EntityTypeDefinition
    var entity = MapFromPersistentObject(persistentObject);

    // 2. Auto-generate GUID if new
    if (entity.Id == Guid.Empty)
        entity.Id = Guid.NewGuid();

    // 3. Persist to file
    _fileService.SavePersistentObject(entity);

    return Task.FromResult(entity);
}

public override Task OnDeleteAsync(ISparkSession session, string id)
{
    _fileService.DeletePersistentObject(id);
    return Task.CompletedTask;
}
```

**Entity-specific mapping considerations**:

| Entity | Mapping Notes |
|--------|---------------|
| EntityTypeDefinition | Map PO attributes → name, description, clrType, alias, displayFormat, etc. |
| EntityAttributeDefinition | Requires parent PO name. Map → name, label, dataType, isRequired, rules, etc. PO name comes from a Reference attribute or URL context. |
| SparkQuery | Requires parent PO name. Map → name, source, alias, sortColumns, renderMode, etc. |
| CustomActionDefinition | Key is the action name. Map → displayName, icon, selectionRule, etc. |
| ProgramUnitGroup | Map → name (TranslatedString), icon, order |
| ProgramUnit | Requires parent group ID. Map → name, type, queryId, persistentObjectId, alias, order |
| SecurityGroupDefinition | Map → name (TranslatedString), comment |
| Right | Map → resource, groupId, isDenied, isImportant |
| LanguageDefinition | Map → culture code, name (TranslatedString) |
| TranslationEntry | Map → key, values (TranslatedString) |

**Parent entity resolution**: For entities embedded in another file (Attributes in PO, Queries in PO, ProgramUnits in Group), the Actions class must determine the parent entity. This is done via:
1. A `PersistentObjectName` back-reference property on the entity model (already exists on `EntityAttributeDefinition` and `SparkQuery`)
2. A `GroupId` property on `ProgramUnit` (already exists)

### 4.4 File Watching

#### 4.4.1 FileSystemWatcher Setup

One `FileSystemWatcher` per target path, monitoring:
- `App_Data/Model/*.json` -- PO/Attribute/Query changes
- `App_Data/programUnits.json` -- Program unit changes
- `App_Data/customActions.json` -- Custom action changes
- `App_Data/security.json` -- Security changes
- `App_Data/culture.json` -- Language changes
- `App_Data/translations.json` -- Translation changes

```csharp
public class SparkEditorFileService : ISparkEditorFileService, IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();

    public event EventHandler<FileChangedEventArgs> FileChanged;

    public void StartWatching()
    {
        foreach (var targetPath in TargetPaths)
        {
            // Watch Model/ directory
            var modelDir = Path.Combine(targetPath, "Model");
            if (Directory.Exists(modelDir))
            {
                var modelWatcher = new FileSystemWatcher(modelDir, "*.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite
                                 | NotifyFilters.FileName
                                 | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
                modelWatcher.Changed += OnFileChanged;
                modelWatcher.Created += OnFileChanged;
                modelWatcher.Deleted += OnFileChanged;
                modelWatcher.Renamed += OnFileRenamed;
                _watchers.Add(modelWatcher);
            }

            // Watch root App_Data/ for single-object files
            var rootWatcher = new FileSystemWatcher(targetPath, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            rootWatcher.Changed += OnFileChanged;
            rootWatcher.Created += OnFileChanged;
            rootWatcher.Deleted += OnFileChanged;
            _watchers.Add(rootWatcher);
        }
    }
}
```

#### 4.4.2 Debouncing

`FileSystemWatcher` often fires multiple events for a single file save (especially on Windows). Use a debounce strategy:

```csharp
private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounceTokens = new();

private void OnFileChanged(object sender, FileSystemEventArgs e)
{
    // Skip if this is our own write (set a flag during SaveXxx methods)
    if (_isWriting) return;

    // Debounce: cancel previous timer for this file, start new one
    if (_debounceTokens.TryGetValue(e.FullPath, out var existingCts))
        existingCts.Cancel();

    var cts = new CancellationTokenSource();
    _debounceTokens[e.FullPath] = cts;

    Task.Delay(300, cts.Token).ContinueWith(t =>
    {
        if (!t.IsCanceled)
        {
            _debounceTokens.TryRemove(e.FullPath, out _);
            FileChanged?.Invoke(this, new FileChangedEventArgs
            {
                FilePath = e.FullPath,
                ChangeType = e.ChangeType
            });
        }
    });
}
```

#### 4.4.3 Self-Write Suppression

When the editor itself writes a file, the `FileSystemWatcher` will fire. Suppress this:

```csharp
private volatile bool _isWriting;

private void WriteFileWithSuppression(string path, string content)
{
    _isWriting = true;
    try
    {
        File.WriteAllText(path, content);
    }
    finally
    {
        // Delay re-enabling to account for async FSW events
        Task.Delay(500).ContinueWith(_ => _isWriting = false);
    }
}
```

#### 4.4.4 Pushing Changes to the Frontend via WebSocket

The Angular frontend runs inside an iframe served by the SparkEditor's ASP.NET Core backend. Spark already has a WebSocket infrastructure for streaming queries (`/spark/queries/{id}/stream`, `SparkStreamingService`, `StreamingMessage` protocol). We extend this same infrastructure for file change notifications.

**Why WebSocket over SSE**: Spark already enables `app.UseWebSockets()` in `SparkMiddleware.cs`, the Angular client already has `SparkStreamingService` with reconnection/backoff logic, and the `StreamingMessage` type-discriminated protocol (`snapshot`, `patch`, `error`) is already understood by the frontend. Reusing this keeps the stack unified.

##### Server-Side: New WebSocket Endpoint

Add a new endpoint at `/spark/editor/file-events` following the same pattern as `StreamExecuteQuery`:

```csharp
// Endpoints/Editor/FileEventsEndpoint.cs
public class FileEventsEndpoint : IEndpoint, IMemberOf<EditorGroup>
{
    public static string Path => "/file-events";
    public static string[] Methods => ["GET"];

    public static async Task HandleAsync(
        HttpContext httpContext,
        ISparkEditorFileService fileService)
    {
        if (!httpContext.WebSockets.IsWebSocketRequest)
        {
            httpContext.Response.StatusCode = 400;
            return;
        }

        using var ws = await httpContext.WebSockets.AcceptWebSocketAsync();
        var tcs = new TaskCompletionSource();

        void handler(object? s, FileChangedEventArgs e)
        {
            // Reuse the StreamingMessage protocol with a new "fileChanged" type
            var message = new FileChangedMessage
            {
                FilePath = e.FilePath,
                FileName = System.IO.Path.GetFileName(e.FilePath),
                ChangeType = e.ChangeType.ToString(),
                AffectedEntities = ResolveAffectedEntities(e.FilePath)
            };

            var json = JsonSerializer.Serialize(message, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            _ = ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        fileService.FileChanged += handler;

        try
        {
            // Keep connection open, listen for close frame
            var buffer = new byte[256];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, httpContext.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            fileService.FileChanged -= handler;
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
    }

    /// <summary>
    /// Maps a file path to the editor entity types it affects,
    /// so the frontend knows which views to refresh.
    /// </summary>
    private static string[] ResolveAffectedEntities(string filePath)
    {
        var fileName = System.IO.Path.GetFileName(filePath);
        var dir = System.IO.Path.GetDirectoryName(filePath) ?? "";

        if (dir.EndsWith("Model"))
            return ["PersistentObjectDefinition", "AttributeDefinition", "QueryDefinition"];
        return fileName switch
        {
            "programUnits.json" => ["ProgramUnitGroupDef", "ProgramUnitDef"],
            "customActions.json" => ["CustomActionDef"],
            "security.json" => ["SecurityGroupDef", "SecurityRightDef"],
            "culture.json" => ["LanguageDef"],
            "translations.json" => ["TranslationDef"],
            _ => []
        };
    }
}
```

**Message format** (extends the existing `StreamingMessage` pattern):

```json
{
  "type": "fileChanged",
  "filePath": "C:\\Projects\\Fleet\\App_Data\\Model\\Car.json",
  "fileName": "Car.json",
  "changeType": "Changed",
  "affectedEntities": ["PersistentObjectDefinition", "AttributeDefinition", "QueryDefinition"]
}
```

##### Client-Side: New `SparkFileWatchService`

Build on the same patterns as `SparkStreamingService` (RxJS Observable, reconnection with exponential backoff, `NgZone.run` for change detection):

```typescript
@Injectable({ providedIn: 'root' })
export class SparkFileWatchService {
    private ngZone = inject(NgZone);
    private config = inject(SPARK_CONFIG);

    readonly fileChanged = signal<FileChangedMessage | null>(null);
    private ws: WebSocket | null = null;
    private retryCount = 0;
    private retryTimeout: any = null;

    connect(): void {
        const baseUrl = this.config.baseUrl || '/spark';
        const wsUrl = this.buildWsUrl(`${baseUrl}/editor/file-events`);
        this.ws = new WebSocket(wsUrl);

        this.ws.onopen = () => { this.retryCount = 0; };

        this.ws.onmessage = (event) => {
            const message: FileChangedMessage = JSON.parse(event.data);
            this.ngZone.run(() => this.fileChanged.set(message));
        };

        this.ws.onclose = (event) => {
            if (event.code !== 1000 && this.retryCount < 10) {
                // Exponential backoff: 1s, 2s, 4s, ..., max 30s
                const delay = Math.min(1000 * Math.pow(2, this.retryCount), 30000);
                this.retryCount++;
                this.retryTimeout = setTimeout(() => this.connect(), delay);
            }
        };
    }

    disconnect(): void {
        clearTimeout(this.retryTimeout);
        this.ws?.close(1000, 'Client disconnected');
        this.ws = null;
    }

    private buildWsUrl(relative: string): string {
        // Same protocol detection logic as SparkStreamingService
        const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
        return `${protocol}//${location.host}${relative}`;
    }
}

export interface FileChangedMessage {
    type: 'fileChanged';
    filePath: string;
    fileName: string;
    changeType: 'Changed' | 'Created' | 'Deleted' | 'Renamed';
    affectedEntities: string[];
}
```

##### Component Integration

Components use `effect()` to react to file changes:

```typescript
// In SparkQueryListComponent or any list/detail component
private fileWatchService = inject(SparkFileWatchService);

constructor() {
    effect(() => {
        const event = this.fileWatchService.fileChanged();
        if (event && event.affectedEntities.includes(this.entityTypeName())) {
            // Refetch the query data
            this.refresh();
        }
    });
}
```

The `ShellComponent` connects on init and disconnects on destroy:

```typescript
ngOnInit() { this.fileWatchService.connect(); }
ngOnDestroy() { this.fileWatchService.disconnect(); }
```

### 4.5 Conflict Detection

When a user has an entity open for editing and the underlying file changes externally:

1. The WebSocket `fileChanged` message arrives with the changed file path and affected entity types
2. The Angular frontend checks if the current detail/edit view corresponds to the changed file
3. If so, display a notification bar: "This file was modified externally. Reload to see changes, or continue editing to overwrite."
4. The user chooses:
   - **Reload**: Discard local changes, reload from file
   - **Continue**: Keep editing, next save will overwrite the external changes

This is a simple last-write-wins model with user awareness.

### 4.6 Referential Integrity Validation

On save, validate references and return warnings (not errors) in the API response.

| Save Operation | Validations |
|----------------|-------------|
| SavePersistentObject | Warn if `clrType` doesn't match any known type |
| SaveAttribute | Warn if `referenceType` doesn't match any PO's `clrType`; warn if `group` ID doesn't exist in parent PO's `groups[]` |
| SaveQuery | Warn if `source` starts with `Database.` but no matching context property; warn if `entityType` doesn't match any PO |
| SaveProgramUnit | Warn if `queryId` doesn't match any query; warn if `persistentObjectId` doesn't match any PO |
| SaveSecurityRight | Warn if `groupId` doesn't match any security group; warn if resource entity name doesn't match any PO |

**Implementation**: Return warnings as a `warnings[]` array in the save response. The Spark framework already returns `400` with validation errors; warnings are returned as a `200` response with an additional `warnings` header or response body field.

### 4.7 JSON Serialization Strategy: Stable Output for Clean Git Diffs

**Primary requirement**: Editing one entity must produce a minimal, predictable git diff. Adding an attribute to `Car.json` should show only the inserted lines, not a reshuffling of the entire file.

**Secondary requirement**: Round-trip fidelity. Loading and saving a file without changes must produce an identical file.

#### 4.7.1 Formatting Rules

Match the existing conventions observed across all Demo app JSON files:

| Rule | Value | Example |
|------|-------|---------|
| Indentation | 2 spaces | `  "name": "Car"` |
| Trailing newline | Yes, exactly one `\n` | End of every file |
| Null properties | Omit entirely | Don't write `"alias": null` |
| Empty arrays | Keep inline | `"rules": []` |
| Boolean format | Lowercase | `true`, `false` |
| TranslatedString key order | Alphabetical by language code | `{ "en": "...", "fr": "...", "nl": "..." }` |

```csharp
private static readonly JsonSerializerOptions WriteOptions = new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

// Override indentation to 2 spaces (System.Text.Json defaults to 2 in .NET 9+)
// Ensure trailing newline on write:
private void WriteJsonFile(string path, JsonNode root)
{
    var json = root.ToJsonString(WriteOptions);
    if (!json.EndsWith('\n')) json += "\n";
    AtomicWriteFile(path, json);
}
```

#### 4.7.2 Stable Array Ordering

The key design insight: **PersistentObjects and Attributes are not renamed** (the user confirmed this). Their `name` property is a stable, unique key within their parent scope. Use `name` as the sort key for arrays where it applies.

| Array | Sort Key | Rationale |
|-------|----------|-----------|
| `persistentObject.attributes[]` | `name` (string, ascending) | Attribute names are stable identifiers; sorting by name keeps diffs localized to the insertion point |
| `persistentObject.tabs[]` | `order` (int), then `name` | `order` is the user's intended sequence; `name` as tiebreaker |
| `persistentObject.groups[]` | `order` (int), then `name` | Same as tabs |
| `queries[]` | `name` (string, ascending) | Query names are stable; alphabetical keeps them predictable |
| `programUnitGroups[]` | `order` (int) | Groups have explicit ordering |
| `programUnitGroups[].programUnits[]` | `order` (int) | Units have explicit ordering |
| `security.rights[]` | `resource` (string), then `groupId` | Sorting by resource groups related rights together |
| `customActions` (object keys) | Alphabetical by key name | JSON object property order; alphabetical is conventional |
| `translations` (object keys) | Alphabetical by key name | Same -- keeps translation keys in a predictable order for git |
| `culture.languages` (object keys) | Alphabetical by code | `en`, `fr`, `nl` |

**Implementation**: After modifying any array, always re-sort before serializing:

```csharp
private static void SortAttributes(JsonArray attributes)
{
    var sorted = attributes
        .Select(a => a!.AsObject())
        .OrderBy(a => a["name"]?.GetValue<string>() ?? "")
        .ToList();

    attributes.Clear();
    foreach (var attr in sorted)
        attributes.Add(attr);
}

private static void SortByOrder(JsonArray items)
{
    var sorted = items
        .Select(i => i!.AsObject())
        .OrderBy(i => i["order"]?.GetValue<int>() ?? int.MaxValue)
        .ThenBy(i => i["name"]?.GetValue<string>() ?? "")
        .ToList();

    items.Clear();
    foreach (var item in sorted)
        items.Add(item);
}
```

For JSON objects where key order matters (customActions, translations, culture.languages), rebuild the object with sorted keys:

```csharp
private static JsonObject SortObjectKeys(JsonObject obj)
{
    var sorted = new JsonObject();
    foreach (var kvp in obj.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
    {
        var value = kvp.Value;
        obj.Remove(kvp.Key);       // Detach from old parent
        sorted.Add(kvp.Key, value);
    }
    return sorted;
}
```

#### 4.7.3 Stable Property Ordering Within Objects

When writing an entity object (e.g., an attribute), properties must appear in a fixed, conventional order -- not alphabetical, but in the logical grouping already established in the codebase:

**Attribute property order** (matches existing Demo app files):
1. `id`
2. `name`
3. `label`
4. `dataType`
5. `isRequired`
6. `isVisible`
7. `isReadOnly`
8. `order`
9. `isArray`
10. `inCollectionType`, `inQueryType` (optional)
11. `query`, `referenceType`, `asDetailType`, `lookupReferenceType`, `editMode` (optional, type-specific)
12. `showedOn`
13. `rules`
14. `group`, `columnSpan` (optional)
15. `renderer`, `rendererOptions` (optional)

**PersistentObject property order**:
1. `id`
2. `name`
3. `description`
4. `clrType`
5. `queryType`, `indexName` (optional)
6. `alias`
7. `displayFormat`, `displayAttribute` (optional)
8. `isReadOnly`, `isHidden` (optional)
9. `tabs`
10. `groups`
11. `attributes`
12. `queries` (sub-array reference list)

**Query property order**:
1. `id`
2. `name`
3. `description`
4. `source`
5. `alias`
6. `sortColumns`
7. `renderMode`, `indexName`, `useProjection`, `entityType`, `isStreamingQuery` (optional)

**Implementation**: Build each entity as a `JsonObject` by adding properties in the defined order. Since `JsonObject` preserves insertion order, this produces stable output:

```csharp
private static JsonObject BuildAttributeNode(EntityAttributeDefinition attr)
{
    var node = new JsonObject();
    node.Add("id", attr.Id.ToString());
    node.Add("name", attr.Name);
    if (attr.Label != null) node.Add("label", SerializeTranslatedString(attr.Label));
    node.Add("dataType", attr.DataType);
    node.Add("isRequired", attr.IsRequired);
    node.Add("isVisible", attr.IsVisible);
    node.Add("isReadOnly", attr.IsReadOnly);
    node.Add("order", attr.Order);
    node.Add("isArray", attr.IsArray);
    // ... optional properties only if non-null/non-default
    if (attr.ShowedOn != null) node.Add("showedOn", attr.ShowedOn);
    node.Add("rules", SerializeRules(attr.Rules));
    // ...
    return node;
}
```

#### 4.7.4 TranslatedString Key Ordering

TranslatedString objects (`{ "en": "...", "fr": "...", "nl": "..." }`) must always have keys sorted alphabetically. This is especially important because culture.json defines the available languages, and the editor may add/remove language keys.

```csharp
private static JsonObject SerializeTranslatedString(TranslatedString ts)
{
    var node = new JsonObject();
    foreach (var kvp in ts.OrderBy(k => k.Key, StringComparer.Ordinal))
        node.Add(kvp.Key, kvp.Value);
    return node;
}
```

#### 4.7.5 Round-Trip Example

Given this initial `Car.json`:
```json
{
  "persistentObject": {
    "id": "facb6829-...",
    "name": "Car",
    "attributes": [
      { "id": "...", "name": "Brand", ... },
      { "id": "...", "name": "LicensePlate", ... }
    ]
  },
  "queries": [...]
}
```

Adding an attribute named "Color" produces this diff:
```diff
       { "id": "...", "name": "Brand", ... },
+      { "id": "...", "name": "Color", ... },
       { "id": "...", "name": "LicensePlate", ... }
```

Because attributes are sorted by `name`, "Color" slots between "Brand" and "LicensePlate". The rest of the file is untouched. Git shows a clean, minimal diff.

### 4.8 GUID Auto-Generation

For new entities, generate GUIDs automatically:

- When `OnSaveAsync` receives a `PersistentObject` where the entity's ID is `Guid.Empty` or the string "new", generate a new GUID via `Guid.NewGuid()`
- The Spark framework's create endpoint sends the entity with a placeholder ID
- The Actions class detects this and assigns a real GUID before persisting
- Return the entity with the assigned GUID so the frontend navigates to the correct detail URL

---

## 5. Implementation Plan

### Phase 1: Core File Write Infrastructure

**Scope**: Get `SparkEditorFileService` writing files correctly with clean, git-friendly output.

1. **Refactor parsing to use `JsonNode`** -- Replace `JsonDocument`-based read-only parsing with `JsonNode`-based mutable parsing. This enables modifying and writing back.
2. **Implement stable serialization helpers** -- `BuildAttributeNode`, `BuildQueryNode`, `SerializeTranslatedString`, `SortAttributes` (by `name`), `SortByOrder`, `SortObjectKeys`. These ensure every write produces a deterministic, minimal-diff output (see 4.7).
3. **Implement `SavePersistentObject`** -- Write/update `Model/{Name}.json`, handling both create (new file) and update (modify existing file). Sort `attributes[]` by `name`, `queries[]` by `name`, `tabs[]`/`groups[]` by `order`.
4. **Implement `DeletePersistentObject`** -- Delete the `Model/{Name}.json` file
5. **Implement `SaveAttribute` / `DeleteAttribute`** -- Read parent PO file, modify `attributes[]`, re-sort by `name`, write back
6. **Implement `SaveQuery` / `DeleteQuery`** -- Read parent PO file, modify `queries[]`, re-sort by `name`, write back
7. **Implement all single-file saves** -- `SaveCustomAction`, `SaveProgramUnitGroup`, `SaveProgramUnit`, `SaveSecurityGroup`, `SaveSecurityRight`, `SaveLanguage`, `SaveTranslation` and their delete counterparts. Sort object keys alphabetically for customActions.json, translations.json, culture.json. Sort arrays by `order` for programUnits.json, by `resource` for security rights.
8. **Add `AtomicWriteFile` + `WriteFileWithSuppression` helper** -- Central write method with temp-file-then-rename and file-watcher suppression
9. **Unit tests** -- Test each save/delete method with a temp directory of JSON files. **Include round-trip tests**: load → save without changes → file content must be byte-identical. **Include diff tests**: add one attribute → only that attribute appears in the diff.

### Phase 2: Actions Class CRUD

**Scope**: Wire up the UI to the file service.

1. **Add `OnSaveAsync` to all 10 Actions classes** -- Map `PersistentObject` attributes to entity properties, call file service
2. **Add `OnDeleteAsync` to all 10 Actions classes** -- Extract ID, call file service
3. **Add `OnCreateAsync` where needed** -- For entities that need initialization (GUID generation, default values)
4. **Handle parent entity resolution** -- For Attributes, Queries, and ProgramUnits, resolve the parent from the `PersistentObject` attribute or URL context
5. **Update editor's `App_Data/Model/*.json` files** -- Ensure entity definitions include the correct permissions (`QueryReadEditNewDelete`) so the UI shows create/edit/delete buttons
6. **Integration test** -- Open editor, create a PO, add attributes, verify JSON file content

### Phase 3: File Watching

**Scope**: Detect external changes and refresh the UI.

1. **Add `FileSystemWatcher` setup** to `SparkEditorFileService`
2. **Add debouncing** to suppress duplicate events
3. **Add self-write suppression** to ignore editor's own writes
4. **Add WebSocket endpoint** (`/spark/editor/file-events`) following the existing `StreamExecuteQuery` pattern
5. **Add Angular `SparkFileWatchService`** -- WebSocket client with reconnection (same pattern as `SparkStreamingService`), expose `fileChanged` signal
6. **Update list components** -- On `fileChanged` event where `affectedEntities` matches, refetch query results
7. **Update detail/edit components** -- On file change for the current entity, show conflict notification
8. **Connect WebSocket in `ShellComponent.ngOnInit()`**, disconnect in `ngOnDestroy()`
9. **Call `StartWatching()` on server startup**, `StopWatching()` on shutdown

### Phase 4: Referential Integrity & Polish

**Scope**: Validation and UX improvements.

1. **Add referential integrity checks** on save (warnings, not errors)
2. **Add conflict notification UI** -- Banner on detail/edit pages when file changes externally
3. **Add "Reload" action** to conflict notification
4. **Add validation error display** -- Show save warnings in the UI
5. **Add GUID display** -- Show entity IDs as read-only fields on detail pages

---

## 6. Technical Considerations

### 6.1 File Locking

Multiple threads may attempt to read/write the same file simultaneously (e.g., user saves while `FileSystemWatcher` triggers a reload). Use a per-file `SemaphoreSlim` to serialize access:

```csharp
private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

private SemaphoreSlim GetLock(string filePath)
    => _fileLocks.GetOrAdd(Path.GetFullPath(filePath), _ => new SemaphoreSlim(1, 1));

private async Task<string> ReadFileAsync(string filePath)
{
    var semaphore = GetLock(filePath);
    await semaphore.WaitAsync();
    try { return await File.ReadAllTextAsync(filePath); }
    finally { semaphore.Release(); }
}

private async Task WriteFileAsync(string filePath, string content)
{
    var semaphore = GetLock(filePath);
    await semaphore.WaitAsync();
    try
    {
        _isWriting = true;
        await File.WriteAllTextAsync(filePath, content);
    }
    finally
    {
        await Task.Delay(500); // Let FSW events settle
        _isWriting = false;
        semaphore.Release();
    }
}
```

### 6.2 Multi-Path Entity Source Tracking

For entities loaded from different target paths, track the source:

- `EntityTypeDefinition.SourceFile` already exists and contains the full file path
- For other entity types, add an internal `SourceTargetPath` property (not serialized to JSON) that records which target path the entity was loaded from
- On save, use the tracked source path; for new entities, default to `TargetPaths[0]`

### 6.3 Atomic File Writes

To prevent corrupted files on crash/power loss, use write-to-temp-then-rename:

```csharp
private void AtomicWriteFile(string filePath, string content)
{
    var tempPath = filePath + ".tmp";
    File.WriteAllText(tempPath, content);
    File.Move(tempPath, filePath, overwrite: true);
}
```

### 6.4 Attribute/Query Parent Resolution in Actions

When saving an Attribute or Query, the Actions class needs to know which PO file to write to. Two approaches:

**Approach A: Parent PO name on the entity model** (recommended)
- `EntityAttributeDefinition.PersistentObjectName` already exists
- `SparkQuery.PersistentObjectName` already exists
- The Spark frontend sends these values as part of the `PersistentObject` attributes
- The Actions class reads the PO name from the `PersistentObject` and passes it to the file service

**Approach B: URL-based parent context**
- The Spark sub-query system passes `parentId`/`parentType` in the query context
- Less reliable for direct saves

**Decision**: Use Approach A. The editor's entity model JSON files should include a `PersistentObjectName` Reference attribute pointing to the parent PO.

### 6.5 File Creation for New Entities

When creating a new entity that requires a new file:

| Entity | File Created |
|--------|-------------|
| EntityTypeDefinition (new PO) | `App_Data/Model/{Name}.json` |
| ProgramUnitGroup (first group when no programUnits.json) | `App_Data/programUnits.json` |
| CustomActionDefinition (first action when no customActions.json) | `App_Data/customActions.json` |
| SecurityGroupDefinition (first group when no security.json) | `App_Data/security.json` |
| LanguageDefinition (first language when no culture.json) | `App_Data/culture.json` |
| TranslationEntry (first key when no translations.json) | `App_Data/translations.json` |

For entities embedded in existing files (Attributes, Queries, ProgramUnits, Rights), the parent file must already exist.

### 6.6 Delete Cascading

Some deletes should cascade:

| Delete | Cascades To |
|--------|------------|
| PersistentObject | All embedded Attributes, Queries (file deleted) |
| ProgramUnitGroup | All nested ProgramUnits |
| SecurityGroup | All Rights referencing this group |
| Language | Remove language key from all TranslatedStrings in all files (optional, warn user) |

**Language deletion**: This is destructive -- it would modify every `TranslatedString` in every JSON file. Instead of cascading, warn the user: "Removing language '{code}' will not remove existing translations. You may want to clean up TranslatedString entries manually."

---

## 7. Editor Entity Model Updates

The editor's own `App_Data/Model/*.json` files need updates to enable CRUD in the UI.

### 7.1 Permission Changes

Currently the editor entities are read-only because the Spark framework checks for save/delete permissions. The editor's `App_Data/security.json` (if it exists) or the default permission behavior needs to allow all operations.

**Solution**: The SparkEditor doesn't use the Authorization package. Configure the editor to return full permissions for all entity types by default. This may already be the case since the editor doesn't register `MintPlayer.Spark.Authorization`.

### 7.2 Entity Model Attribute Updates

Some entity model JSON files need additional attributes to support editing:

**PersistentObjectDefinition.json** -- Add attributes for:
- `Alias` (string, order 3.5)
- `QueryType` (string)
- `IndexName` (string)
- `DisplayFormat` (string)

**AttributeDefinition.json** -- Needs `PersistentObjectName` as a Reference attribute so the editor knows which PO file to update.

**QueryDefinition.json** -- Needs `PersistentObjectName` as a Reference attribute.

**ProgramUnitDef.json** -- Needs `GroupId` as a Reference to ProgramUnitGroup.

---

## 8. Testing Strategy

### 8.1 Unit Tests (`SparkEditorFileService`)

- Create a temp directory with sample JSON files
- Test each Save method: verify file content matches expected JSON
- Test each Delete method: verify file is removed or entry is removed
- Test round-trip: Load → Save → Load produces identical entities
- Test GUID generation: new entities get assigned GUIDs
- Test multi-path: entities from path A are saved back to path A

### 8.2 Integration Tests

- Start the SparkEditor server against a temp `App_Data/`
- Exercise the Spark API endpoints: POST (create), PUT (update), DELETE
- Verify JSON files are written correctly
- Verify file watching triggers reload

### 8.3 Manual Testing

- Open the editor in VS Code
- Create a new PO, add attributes, create a query
- Verify the JSON file is created with correct content
- Edit the JSON file externally, verify the editor refreshes
- Delete a PO, verify the file is removed

---

## 9. Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| File corruption on crash | Loss of JSON config | Atomic writes (write-to-temp-then-rename) |
| FileSystemWatcher unreliable on network drives | Missed external changes | Document that target path should be local; add manual "Refresh" button |
| Concurrent edits lose data | Last-write-wins overwrites | Conflict detection with user notification |
| Breaking existing JSON formatting | Noisy git diffs | Round-trip fidelity with `JsonNode`; consistent formatting options |
| Attribute/Query parent resolution fails | Can't save embedded entities | Require `PersistentObjectName` back-reference; validate on save |
| Large translation files slow to parse | Slow UI for save | Use incremental updates via `JsonNode` (don't re-serialize entire file) |

---

## 10. Success Criteria

1. All 10 mutable entity types support Create, Update, and Delete through the editor UI
2. Changes made in the editor are persisted to the correct JSON files with correct formatting
3. Changes made externally (by hand-editing JSON files) are detected within 1 second and reflected in the editor UI
4. No data loss: round-trip (read/write) preserves all existing JSON properties
5. New entities get auto-generated GUIDs
6. Referential integrity warnings are shown (but don't block saves)
7. The conflict notification appears when a file changes while the user is editing

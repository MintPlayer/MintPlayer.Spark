# PRD: Custom Actions for MintPlayer.Spark

## 1. Overview

Custom Actions are user-defined operations that can be invoked on persistent objects (from a detail view) or on query results (from a list view). They complement the built-in CRUD operations (Query, Read, Edit, New, Delete) with arbitrary business logic like "Copy Car", "Approve Invoice Lines", or "Export to Excel".

### Inspiration
The design is inspired by Fleet/Vidyano's CustomAction system but adapted to Spark's patterns:
- **No context constructor requirement** — uses `[Inject]` for DI instead of `CustomAction<TContext>(context)` base class
- **Async-first** — `ExecuteAsync` instead of synchronous `Execute`
- **Same authorization model** — reuses `security.json` and `IAccessControl`/`IPermissionService`
- **Same retry/dialog system** — reuses `IManager.Retry` for multi-step flows

## 2. Goals

1. Enable developers to define custom business actions as C# classes
2. Authorize custom actions via the existing `security.json` permission system
3. Configure action metadata (display name, icon, selection rules) via `App_Data/customActions.json`
4. Expose custom actions through a REST endpoint under `/spark/actions/`
5. Auto-discover custom action classes via source generator (same pattern as `ActionsRegistrationGenerator`)
6. Support both detail-view actions (single object) and query-view actions (selected items from a list)

## 3. Non-Goals (Out of Scope for v1)

- File download/stream responses from custom actions (can be added later)
- Client-side operations queue (e.g., "open URL", "refresh query") — frontend handles refresh
- Bulk edit as a custom action (this is a separate feature)
- Custom action scheduling/background execution

## 4. Architecture

### 4.1 Interface: `ICustomAction`

Located in `MintPlayer.Spark.Abstractions/Actions/ICustomAction.cs`:

```csharp
namespace MintPlayer.Spark.Abstractions.Actions;

/// <summary>
/// Context passed to a custom action when executed.
/// </summary>
public class CustomActionArgs
{
    /// <summary>
    /// The parent PersistentObject (when invoked from a detail view).
    /// Null when invoked from a query with no parent.
    /// </summary>
    public PersistentObject? Parent { get; set; }

    /// <summary>
    /// Selected items from a query (when invoked from a list view).
    /// Empty when invoked from a detail view.
    /// </summary>
    public PersistentObject[] SelectedItems { get; set; } = [];
}

/// <summary>
/// Interface for custom actions. Implement this to create a custom action.
/// </summary>
public interface ICustomAction
{
    /// <summary>
    /// Executes the custom action.
    /// Navigate/Notify capabilities will be added in a future phase via IManager
    /// (same mechanism used by PersistentObject Actions classes).
    /// </summary>
    Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default);
}
```

### 4.2 Abstract Base Class: `SparkCustomAction`

Located in `MintPlayer.Spark/Actions/SparkCustomAction.cs`:

```csharp
namespace MintPlayer.Spark.Actions;

/// <summary>
/// Convenience base class for custom actions.
/// Developers can inherit from this OR implement ICustomAction directly.
/// In a future phase, Navigate/Notify helper methods will be added here,
/// powered by IManager (same mechanism as PersistentObject Actions classes).
/// </summary>
public abstract class SparkCustomAction : ICustomAction
{
    public abstract Task ExecuteAsync(
        CustomActionArgs args, CancellationToken cancellationToken = default);
}
```

### 4.3 Configuration: `App_Data/customActions.json`

This file defines **metadata** for custom actions — display names, icons, selection rules, and which entity types they apply to. The actual implementation is a C# class discovered by the source generator.

```json
{
  "CarCopy": {
    "displayName": { "en": "Copy", "fr": "Copier", "nl": "Kopiëren" },
    "icon": "Copy",
    "description": "Creates a copy of the selected car",
    "showedOn": "query",
    "selectionRule": "=1",
    "refreshOnCompleted": true,
    "confirmationMessageKey": "AreYouSure",
    "offset": 0
  },
  "CarMakeNote": {
    "displayName": { "en": "Note", "fr": "Note", "nl": "Notitie" },
    "icon": "Action_New",
    "description": "Creates a new note for the selected car",
    "showedOn": "both",
    "selectionRule": "=1",
    "refreshOnCompleted": false,
    "offset": 2
  },
  "ApproveInvoiceLines": {
    "displayName": { "en": "Approve", "fr": "Approuver", "nl": "Goedkeuren" },
    "icon": "Check",
    "showedOn": "query",
    "selectionRule": ">0",
    "refreshOnCompleted": true
  }
}
```

#### Field definitions:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `displayName` | `TranslatedString` | Yes | Localized display name |
| `icon` | `string` | No | Icon identifier for the frontend |
| `description` | `string` | No | Human-readable description |
| `showedOn` | `string` | Yes | `"detail"`, `"query"`, or `"both"` |
| `selectionRule` | `string` | No | `"=0"` (none), `"=1"` (exactly one), `">0"` (one or more). Only relevant when `showedOn` includes `"query"`. Defaults to `"=0"`. |
| `refreshOnCompleted` | `bool` | No | Whether the frontend should refresh after execution. Defaults to `false`. |
| `confirmationMessageKey` | `string` | No | Translation key for a confirmation dialog before execution. If set, frontend shows confirmation first. |
| `offset` | `int` | No | Display order (lower = first). Defaults to `0`. |

### 4.4 Authorization via `security.json`

Custom actions integrate into the existing resource-based authorization system. The resource format is:

```
{ActionName}/{EntityType}
```

Example `security.json` entries:

```json
{
  "rights": [
    {
      "id": "...",
      "resource": "CarCopy/Car",
      "groupId": "...",
      "isDenied": false
    },
    {
      "id": "...",
      "resource": "ApproveInvoiceLines/Car",
      "groupId": "...",
      "isDenied": false
    }
  ]
}
```

The existing `IPermissionService.EnsureAuthorizedAsync(action, target)` works as-is:
- `action` = the custom action name (e.g., `"CarCopy"`)
- `target` = the entity type name (e.g., `"Car"`)

No changes needed to `AccessControlService` — custom action names are just new action strings that don't appear in the `CombinedActions` dictionary, so they get matched by exact resource string.

### 4.5 REST Endpoints

#### `GET /spark/actions/{objectTypeId}`
Returns the list of custom actions available for a given entity type, filtered by:
1. Actions configured in `customActions.json` whose security.json resource `{ActionName}/{EntityType}` is authorized for the current user
2. Actions whose C# implementation class exists (discovered by source generator)

**Response:**
```json
[
  {
    "name": "CarCopy",
    "displayName": "Copy",
    "icon": "Copy",
    "description": "Creates a copy of the selected car",
    "showedOn": "query",
    "selectionRule": "=1",
    "refreshOnCompleted": true,
    "confirmationMessageKey": "AreYouSure",
    "offset": 0
  }
]
```

#### `POST /spark/actions/{objectTypeId}/{actionName}`
Executes a custom action.

**Request body:**
```json
{
  "parent": { /* PersistentObject */ },
  "selectedItems": [ /* PersistentObject[] */ ],
  "retryResults": [ /* RetryResult[] - for multi-step dialogs */ ]
}
```

**Response (200):**
Empty body (action completed successfully). Frontend refreshes based on `refreshOnCompleted` metadata.

**Response (449 - Retry):**
```json
{
  "title": "Confirm operation",
  "message": "Are you sure?",
  "options": ["Yes", "No"],
  "persistentObject": null
}
```

**Response (403):**
Custom action not authorized for current user.

### 4.6 Custom Action Discovery & Registration

#### Source Generator Enhancement

Extend `MintPlayer.Spark.SourceGenerators` with a new `CustomActionsRegistrationGenerator` that:

1. Scans for all classes implementing `ICustomAction` (or extending `SparkCustomAction`)
2. Generates a `SparkCustomActionsExtensions` class with `AddSparkCustomActions()` method
3. Registers each discovered class as a **named** scoped service so they can be resolved by action name

```csharp
// Generated code:
public static class SparkCustomActionsExtensions
{
    public static IServiceCollection AddSparkCustomActions(this IServiceCollection services)
    {
        services.AddScoped<CarCopyAction>();
        services.AddScoped<CarMakeNoteAction>();
        services.AddScoped<ApproveInvoiceLinesAction>();
        return services;
    }
}
```

#### Custom Action Resolver

`ICustomActionResolver` (new service) resolves a custom action class by name:

```csharp
public interface ICustomActionResolver
{
    ICustomAction? Resolve(string actionName);
    IReadOnlyList<string> GetRegisteredActionNames();
}
```

Implementation uses a naming convention: the class name must match the action name in `customActions.json`, with an optional `Action` suffix. For example, action `"CarCopy"` resolves to a class named `CarCopy` or `CarCopyAction`.

The resolver scans the DI container for registered `ICustomAction` implementations and matches by type name.

### 4.7 Configuration Loader: `ICustomActionsConfigurationLoader`

Similar to `SecurityConfigurationLoader`, a new `ICustomActionsConfigurationLoader`:

```csharp
public interface ICustomActionsConfigurationLoader
{
    CustomActionsConfiguration GetConfiguration();
}
```

- Loads from `{ContentRootPath}/App_Data/customActions.json`
- Caches in memory with configurable TTL (same pattern as `SecurityConfigurationLoader`)
- Gracefully handles missing file (returns empty configuration)

### 4.8 Package Placement

| Component | Package |
|-----------|---------|
| `ICustomAction`, `CustomActionArgs` | `MintPlayer.Spark.Abstractions` |
| `SparkCustomAction` base class | `MintPlayer.Spark` |
| `ICustomActionResolver`, `CustomActionsConfigurationLoader` | `MintPlayer.Spark` |
| Custom action endpoints | `MintPlayer.Spark` (in `Endpoints/Actions/`) |
| Source generator for discovery | `MintPlayer.Spark.SourceGenerators` |
| Authorization checks | Uses existing `IPermissionService` (no changes needed) |

## 5. Developer Experience

### Example: Implementing a Custom Action

```csharp
// File: Demo/Fleet/Fleet/CustomActions/CarCopyAction.cs

namespace Fleet.CustomActions;

public partial class CarCopyAction : SparkCustomAction
{
    [Inject] private readonly IDatabaseAccess dbAccess;

    public override async Task ExecuteAsync(
        CustomActionArgs args, CancellationToken cancellationToken)
    {
        // Get the selected car
        var selectedItem = args.SelectedItems.FirstOrDefault()
            ?? throw new InvalidOperationException("No item selected");

        var carId = selectedItem.Id
            ?? throw new InvalidOperationException("Selected item has no ID");

        // Load the source car
        var car = await dbAccess.GetDocumentAsync<Car>(carId, cancellationToken);
        if (car == null)
            throw new InvalidOperationException("Car not found");

        // Create a copy
        var copy = new Car
        {
            Brand = car.Brand,
            Model = car.Model,
            // ... copy fields, leave Id null for new document
        };

        await dbAccess.SaveDocumentAsync(copy, cancellationToken);
    }
}
```

### App_Data/customActions.json:
```json
{
  "CarCopy": {
    "displayName": { "en": "Copy Car", "nl": "Auto kopiëren" },
    "icon": "Copy",
    "showedOn": "query",
    "selectionRule": "=1",
    "refreshOnCompleted": true,
    "confirmationMessageKey": "AreYouSure"
  }
}
```

### App_Data/security.json (add right):
```json
{
  "rights": [
    {
      "id": "...",
      "resource": "CarCopy/Car",
      "groupId": "...",
      "isDenied": false
    }
  ]
}
```

### Program.cs registration:
```csharp
builder.Services.AddSpark(configuration);
builder.Services.AddSparkActions();        // existing - auto-generated
builder.Services.AddSparkCustomActions();  // new - auto-generated
```

## 6. Implementation Plan

### Phase 1: Core Infrastructure
1. **Abstractions** — Add `ICustomAction`, `CustomActionArgs` to `MintPlayer.Spark.Abstractions`
2. **Base class** — Add `SparkCustomAction` to `MintPlayer.Spark/Actions/`
3. **Configuration loader** — `CustomActionsConfigurationLoader` in `MintPlayer.Spark/Services/`
4. **Resolver** — `CustomActionResolver` in `MintPlayer.Spark/Services/`

### Phase 2: Endpoints
5. **List actions endpoint** — `GET /spark/actions/{objectTypeId}` in `MintPlayer.Spark/Endpoints/Actions/`
6. **Execute action endpoint** — `POST /spark/actions/{objectTypeId}/{actionName}` with retry support
7. **Register endpoints** in `SparkMiddleware.MapSpark()`

### Phase 3: Source Generator
8. **CustomActionsRegistrationGenerator** — Discovers `ICustomAction` implementations and generates `AddSparkCustomActions()`

### Phase 4: Demo
9. **Fleet demo** — Add a sample custom action (e.g., `CarCopyAction`) with `customActions.json` and `security.json` entries

## 7. Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| Interface + base class (ICustomAction / SparkCustomAction) | Flexibility: implement interface for full control, extend base class for future convenience (Navigate/Notify via IManager) |
| Separate `customActions.json` (not in `model.json`) | Follows Spark's existing pattern of separate App_Data files per concern (model, security, queries, programUnits, translations) |
| Action name = class name (convention-based) | Consistent with existing `{Entity}Actions` convention for CRUD actions. Optional `Action` suffix for readability. |
| No `CustomAction<TEntity>` generic | Unlike CRUD actions, custom actions may operate across multiple entity types or need no entity at all. The `CustomActionArgs` provides the parent/selected items generically. |
| Reuse existing authorization (no changes to AccessControlService) | Custom action names are just strings — they naturally fit the `{action}/{target}` resource pattern |
| Source-generated registration | Consistent with `AddSparkActions()` pattern; zero manual registration |
| `showedOn` in customActions.json (not security.json) | Security controls *who* can use an action; metadata controls *where/how* it appears in UI |

## 8. Future Phase: Navigate & Notify via IManager

In a later phase, `IManager` will be extended to support navigation and notification from both Custom Actions and PersistentObject Actions classes (e.g., `PersonActions`). This ensures a unified mechanism for all action types:

- **Navigate** — `IManager.Navigate(PersistentObject obj)` — tells the frontend to open/navigate to a PersistentObject after the action completes
- **Notify** — `IManager.Notify(string message, string type)` — tells the frontend to show a notification message

These will work the same way regardless of whether the caller is a Custom Action or a CRUD Actions class. The `SparkCustomAction` base class may then expose convenience wrappers, but the underlying mechanism is `IManager`.

This is intentionally deferred so that v1 focuses on the core execution pipeline and authorization.

## 9. Open Questions

1. **Should custom actions support parameters?** — Fleet uses `e.GetParameter("MenuOption")` for action parameters. This could be added as a `Dictionary<string, string>` on `CustomActionArgs`. Deferred to v2.
2. **Should `customActions.json` define which entity types an action applies to?** — Currently, this binding happens in `security.json` (resource = `{ActionName}/{EntityType}`). We could add an optional `"entityTypes": ["Car", "Truck"]` field to `customActions.json` for explicit binding. For v1, we rely on security.json.
3. **File download support** — Should custom actions support returning a stream/file? Deferred to future phase alongside Navigate/Notify via IManager.

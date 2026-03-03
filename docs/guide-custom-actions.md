# Custom Actions

Spark lets you define server-side actions that users can trigger from entity detail pages or query list views. A custom action combines a C# implementation with JSON-based metadata for display name, icon, selection rules, and authorization.

## Overview

A custom action has three parts:

1. **C# implementation** -- a class that implements `ICustomAction` (or extends `SparkCustomAction`)
2. **JSON configuration** -- an entry in `App_Data/customActions.json` defining display metadata
3. **Authorization** (optional) -- entries in `App_Data/security.json` controlling who can execute the action

## Step 1: Create the Action Class

Implement `ICustomAction` or extend `SparkCustomAction`. The class name determines the action name: strip the optional `Action` suffix. For example, `CarCopyAction` maps to action name `CarCopy`.

```csharp
// Fleet/CustomActions/CarCopyAction.cs
using Fleet.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Actions;

namespace Fleet.CustomActions;

public partial class CarCopyAction : SparkCustomAction
{
    [Inject] private readonly IDatabaseAccess dbAccess;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken)
    {
        // Support both detail view (parent) and query view (selectedItems)
        var source = args.Parent ?? args.SelectedItems.FirstOrDefault();
        if (source is null)
            throw new InvalidOperationException("No item selected");

        var carId = source.Id
            ?? throw new InvalidOperationException("Selected item has no ID");

        var car = await dbAccess.GetDocumentAsync<Car>(carId);
        if (car == null)
            throw new InvalidOperationException("Car not found");

        var copy = new Car
        {
            LicensePlate = $"{car.LicensePlate} (copy)",
            Model = car.Model,
            Year = car.Year,
            Color = car.Color,
            Brand = car.Brand,
            Status = car.Status,
        };

        await dbAccess.SaveDocumentAsync(copy);
    }
}
```

Key points:
- Use `[Inject]` for dependency injection (requires `partial` class)
- `args.Parent` is populated when invoked from a detail page
- `args.SelectedItems` is populated when invoked from a query list (contains the selected rows)
- Use `IDatabaseAccess` (or your own services) for data operations
- Throw exceptions for errors -- they are caught and returned as 500 responses

### The CustomActionArgs Class

```csharp
public class CustomActionArgs
{
    /// The parent PersistentObject (when invoked from a detail view).
    /// Null when invoked from a query with no parent.
    public PersistentObject? Parent { get; set; }

    /// Selected items from a query (when invoked from a list view).
    /// Empty when invoked from a detail view.
    public PersistentObject[] SelectedItems { get; set; } = [];
}
```

### SparkCustomAction vs ICustomAction

You can either extend `SparkCustomAction` (convenience base class) or implement `ICustomAction` directly. Both approaches work identically. The base class currently provides the same abstract method, but in a future phase it will add helper methods for navigation and notifications (same mechanism as PersistentObject Actions classes).

## Step 2: Configure customActions.json

Create `App_Data/customActions.json` in your application. Each key is the action name (must match the C# class name minus the `Action` suffix).

```json
{
  "CarCopy": {
    "displayName": { "en": "Copy Car", "fr": "Copier la voiture", "nl": "Auto kopiëren" },
    "icon": "Copy",
    "description": "Creates a copy of the selected car",
    "showedOn": "both",
    "selectionRule": "=1",
    "refreshOnCompleted": true,
    "confirmationMessageKey": "AreYouSure"
  }
}
```

### Configuration Properties

| Property | Type | Required | Description |
|---|---|---|---|
| `displayName` | TranslatedString | Yes | The button/menu label shown to the user |
| `icon` | string | No | Icon name (displayed next to the action label) |
| `description` | string | No | Human-readable description (for documentation/tooltips) |
| `showedOn` | string | No | Where the action appears: `"detail"`, `"query"`, or `"both"` (default: `"both"`) |
| `selectionRule` | string | No | For query views: how many items must be selected. See below. |
| `refreshOnCompleted` | boolean | No | Whether the UI should refresh after successful execution |
| `confirmationMessageKey` | string | No | Translation key for a confirmation dialog shown before execution |
| `offset` | number | No | Display order (lower values appear first). Default: `0` |

### Selection Rules

The `selectionRule` controls when the action button is enabled in query list views:

| Rule | Meaning |
|---|---|
| `"=0"` | No selection required (action applies to the entire list) |
| `"=1"` | Exactly one item must be selected |
| `">0"` | One or more items must be selected |

When omitted, the action has no selection requirement.

### File Watching

The `customActions.json` file is cached in memory and watched for changes using `FileSystemWatcher`. When the file is modified, the cache is automatically invalidated. No restart is needed to pick up configuration changes.

## Step 3: Authorization (Optional)

If your application uses Spark Authorization, add entries to `App_Data/security.json` to control who can execute each action. The authorization resource follows the pattern `{ActionName}/{EntityTypeName}`:

```json
{
  "groups": {
    "a1b2c3d4-0000-0000-0000-000000000001": {"en": "Administrators"},
    "a1b2c3d4-0000-0000-0000-000000000002": {"en": "Fleet managers"}
  },
  "rights": [
    {
      "id": "ca000001-0000-0000-0000-000000000001",
      "resource": "CarCopy/Car",
      "groupId": "a1b2c3d4-0000-0000-0000-000000000001",
      "isDenied": false
    },
    {
      "id": "ca000001-0000-0000-0000-000000000002",
      "resource": "CarCopy/Car",
      "groupId": "a1b2c3d4-0000-0000-0000-000000000002",
      "isDenied": false
    }
  ]
}
```

In this example, both Administrators and Fleet managers can execute the `CarCopy` action on `Car` entities. Other groups are denied by default.

If no authorization is configured (no `security.json` or no matching entries), the action is available to all users.

## REST API

Spark exposes two endpoints for custom actions under the `/spark/actions` prefix:

### List Available Actions

```
GET /spark/actions/{objectTypeId}
```

Returns the list of custom actions available for the given entity type. Only actions with a matching C# implementation **and** authorized for the current user are included. The response is sorted by `offset`.

**Response:**

```json
[
  {
    "name": "CarCopy",
    "displayName": { "en": "Copy Car", "fr": "Copier la voiture", "nl": "Auto kopiëren" },
    "icon": "Copy",
    "description": "Creates a copy of the selected car",
    "showedOn": "both",
    "selectionRule": "=1",
    "refreshOnCompleted": true,
    "confirmationMessageKey": "AreYouSure",
    "offset": 0
  }
]
```

### Execute an Action

```
POST /spark/actions/{objectTypeId}/{actionName}
```

Executes the action. This endpoint requires an antiforgery token (`X-XSRF-TOKEN` header).

**Request body:**

```json
{
  "parent": { "id": "cars/1-A", "name": "Car" },
  "selectedItems": []
}
```

The `parent` field is set when executing from a detail view. The `selectedItems` array is set when executing from a query list with selected rows.

**Responses:**

| Status | Description |
|---|---|
| 200 | Action executed successfully |
| 401 | Authentication required |
| 403 | Access denied (user lacks permission) |
| 404 | Entity type or action not found |
| 449 | Retry action required (see Manager & Retry Actions guide) |
| 500 | Action threw an exception |

## Action Name Resolution

The `CustomActionResolver` discovers action classes at startup by scanning all loaded assemblies for types that implement `ICustomAction`. The action name is derived from the class name:

- `CarCopyAction` -> `CarCopy` (strips `Action` suffix)
- `ExportData` -> `ExportData` (no suffix to strip)

Name matching is case-insensitive.

The JSON key in `customActions.json` must match this resolved name. Only actions that have both a C# implementation **and** a JSON configuration entry are returned by the list endpoint.

## Angular Integration

On the Angular side, the `CustomActionDefinition` model represents an action:

```typescript
export interface CustomActionDefinition {
  name: string;
  displayName: TranslatedString;
  icon?: string;
  description?: string;
  showedOn: string;
  selectionRule?: string;
  refreshOnCompleted: boolean;
  confirmationMessageKey?: string;
  offset: number;
}
```

The frontend fetches available actions via `GET /spark/actions/{type}`, renders buttons or menu items based on `showedOn`, evaluates `selectionRule` against the current selection, shows a confirmation dialog if `confirmationMessageKey` is set, and executes via `POST /spark/actions/{type}/{name}`.

## Complete Example

See the Fleet demo app for a working example:
- `Demo/Fleet/Fleet/CustomActions/CarCopyAction.cs` -- C# implementation
- `Demo/Fleet/Fleet/App_Data/customActions.json` -- action metadata
- `Demo/Fleet/Fleet/App_Data/security.json` -- authorization entries
- `MintPlayer.Spark.Abstractions/Actions/ICustomAction.cs` -- interface definition
- `MintPlayer.Spark/Actions/SparkCustomAction.cs` -- base class
- `MintPlayer.Spark/Models/CustomActionDefinition.cs` -- metadata model
- `MintPlayer.Spark/Services/CustomActionResolver.cs` -- action discovery
- `MintPlayer.Spark/Endpoints/Actions/ListCustomActions.cs` -- list endpoint
- `MintPlayer.Spark/Endpoints/Actions/ExecuteCustomAction.cs` -- execute endpoint

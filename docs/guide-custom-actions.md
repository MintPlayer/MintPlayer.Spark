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
    /// The parent PersistentObject (when invoked from a detail view),
    /// re-loaded server-side and row-checked. Null when the request named none.
    public PersistentObject? Parent { get; set; }

    /// Selected rows from a query, each re-loaded server-side and row-checked.
    /// Empty when invoked from a detail view.
    public PersistentObject[] SelectedItems { get; set; } = [];

    /// The parent exactly as the client submitted it -- untrusted, for actions that edit.
    public PersistentObject? SubmittedParent { get; set; }

    /// The ids the client named, before resolution. Rarely needed -- see below.
    public string[] SubmittedSelectedItemIds { get; set; } = [];
}
```

`SelectedItems` holds **entities**, not the rows the grid displayed. The client posts
`selectedItemIds` -- a list of strings -- and the framework loads each one through the row-gated
read path before your action runs. That is what makes a selection safe to act on: a row is a
projection the server handed out, never a document a client may hand back.

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

`selectionRule` is a **cardinality expression over the number of selected rows**. It disables the
action's button while unsatisfied, and — since `10.0.0-preview.61` — is **enforced on the server**,
which answers `400` to a request that violates it.

`X` is the count. Whitespace is insignificant, terms split on `X` are combined with AND (so a range
is `1<X<5`), and a number-first term is mirrored (`0<X` means `>0`).

| Rule | Meaning |
|---|---|
| omitted / `""` | No requirement |
| `"=0"` | Exactly zero — the action is **disabled once anything is selected** |
| `"=1"` | Exactly one |
| `">0"` / `">=1"` | One or more |
| `"<=5"` | At most five |
| `"!=0"` | Any non-empty selection |
| `"1<X<5"` | Between two and four |

Operators are `<=`, `>=`, `<`, `>`, `!=`, `=`.

**A malformed rule is refused when the configuration loads, not silently permitted.** `"1-5"`,
`"*"` and `"=abc"` are all rejected, and every offender in the file is named at once. The load is
lazy, so this surfaces the first time custom actions are read rather than at process start. (Vidyano, where this syntax comes from, treats anything
unparseable as "always true" — safe for a greyed-out button, wrong for a server-side gate, where it
would let any selection through.)

**The rule applies to the query path only.** An action invoked from a detail page names a parent
rather than a selection, so its count is zero by definition; enforcing there would break every
`showedOn: "both"` action. `Demo/Fleet`'s `CarCopy` is exactly that shape.

⚠️ **`selectionRule` is not an authorization boundary.** It is input validation and a UI affordance.
The gate is the action's grant, enforced regardless of which query the caller clicked from — a
caller can always POST directly.

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

If no authorization is configured, the default is **deny**, not allow — `PermissionService` refuses
everything until `security.json` grants it. A custom action's resource is `{ActionName}/{Type}`.

**A custom action that names rows also requires `Read/{Type}`.** Every parent and selected item is
re-loaded through the row-gated read path before the action runs, and that load applies the
type-level `Read` right first. So granting `CarCopy/Car` alone is not sufficient for an action that
receives a parent or a selection — the caller needs `Read/Car` too. An action that names no rows (a
pure command) has no such requirement.

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

## Row-level security: server-loaded `Parent` / `SelectedItems` (#236)

`CustomActionArgs.Parent` and `CustomActionArgs.SelectedItems` are **server-loaded and row-checked**. The framework re-resolves the ids the client named through the same row-gated read path as every other load before invoking your action:

- an id the caller may not see (or that does not exist) fails the whole request with **404** -- your action is never invoked, and denial is indistinguishable from not-found;
- the entities your action receives are the **current server state**, not whatever the client typed;
- the submitted parent remains available as `SubmittedParent` for actions that need edited, possibly unsaved values -- treat it as untrusted input;
- a parent submitted **without an id** (unsaved form state) is not resolved: `Parent` is `null` and the submitted values are in `SubmittedParent`.

**There is no `SubmittedSelectedItems`, by design (#327).** A selected row is named by an **id** and
nothing else; `SubmittedSelectedItemIds` carries those raw ids, and they are rarely what you want,
since `SelectedItems` is the same list resolved and row-checked. There is no submitted-object form
of a selection because a row was never a document: the grid renders a projection with no attribute
metadata, no `can` block and no etag, so there is nothing meaningful a client could submit back.
An action that wants edited values wants a *detail form*, which is `SubmittedParent`.

**All or nothing.** If any named id fails to resolve -- missing, foreign collection, or refused by
the row rule -- the whole request is refused. An action never silently receives 498 of the 500 rows
the user selected.

**Migration note (breaking):** actions that relied on client-supplied state arriving in
`Parent`/`SelectedItems` must switch those reads to `SubmittedParent`. Actions that only used
`Parent`/`SelectedItems` to obtain an id and re-load the entity can skip the reload -- the entity
they receive already passed the row gate.

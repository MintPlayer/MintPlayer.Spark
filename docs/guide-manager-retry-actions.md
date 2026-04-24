# Manager and Retry Actions

Spark provides the `IManager` interface for accessing framework utilities inside Actions classes. Its most powerful feature is the **Retry Action** pattern, which lets backend code prompt the user for confirmation or input through modal dialogs -- without writing any frontend code.

## Overview

The `IManager` interface exposes:

| Member | Purpose |
|---|---|
| `Retry` | Access to the Retry Action subsystem (`IRetryAccessor`) |
| `GetPersistentObject()` | Create a virtual PersistentObject for custom dialog forms |
| `GetTranslatedMessage()` | Get a translated string for the current request culture |
| `GetMessage()` | Get a translated string for a specific language |

The Retry Action pattern uses HTTP status **449** (Retry With) to signal the frontend that user input is needed before the operation can complete. The Angular frontend intercepts this status, displays a modal, and re-submits the request with the user's answer.

## Step 1: Inject IManager

In your Actions class, inject `IManager` using the `[Inject]` attribute:

```csharp
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;

public partial class CarActions : DefaultPersistentObjectActions<Car>
{
    [Inject] private readonly IManager manager;
}
```

## Step 2: Add Retry Actions

Call `manager.Retry.Action()` in any lifecycle hook (`OnBeforeSaveAsync`, `OnBeforeDeleteAsync`, etc.) to prompt the user:

```csharp
public override async Task OnBeforeDeleteAsync(Car entity)
{
    manager.Retry.Action(
        title: "Confirm deletion",
        options: ["Delete"],
        message: $"Are you sure you want to delete {entity.LicensePlate}?"
    );

    if (manager.Retry.Result!.Option == "Cancel")
        return;

    await base.OnBeforeDeleteAsync(entity);
}
```

### How It Works

1. On the first invocation, `Action()` throws a `SparkRetryActionException` internally -- it never returns
2. The endpoint catches the exception and responds with HTTP 449 and a JSON payload describing the dialog
3. The Angular frontend displays a modal with the title, message, and option buttons
4. The user clicks a button (or dismisses the modal, which sends "Cancel")
5. The frontend re-submits the original request with the user's answer in `retryResults`
6. On re-invocation, `Action()` replays the answered step, populates `Result`, and returns normally
7. Your code inspects `Result.Option` and proceeds accordingly

### The 449 Response

When `Action()` throws, the endpoint returns:

```json
{
  "type": "retry-action",
  "step": 0,
  "title": "Confirm deletion",
  "message": "Are you sure you want to delete ABC-123?",
  "options": ["Delete"],
  "defaultOption": null,
  "persistentObject": null
}
```

The Angular `SparkService` intercepts this automatically -- no custom error handling is needed in your page components.

## Chained Confirmations

Multiple `Action()` calls can be chained. Each call is tracked by a step index (0, 1, 2, ...) and the framework replays already-answered steps before hitting the next unanswered one:

```csharp
public override async Task OnBeforeSaveAsync(PersistentObject obj, Car entity)
{
    var statusAttr = obj.Attributes.FirstOrDefault(a => a.Name == nameof(Car.Status));
    if (statusAttr?.IsValueChanged == true && entity.Status == CarStatus.Stolen)
    {
        // Step 0: Confirm marking as stolen
        manager.Retry.Action(
            title: "Report vehicle as stolen",
            options: ["Confirm"],
            message: $"Are you sure you want to mark {entity.LicensePlate} as stolen? " +
                     "This will lock the vehicle record."
        );

        if (manager.Retry.Result!.Option == "Cancel")
            return;

        // Step 1: Ask whether to notify fleet managers
        manager.Retry.Action(
            title: "Notify fleet managers",
            options: ["Yes, notify", "No, skip"],
            message: "Should all fleet managers be notified about this stolen vehicle?"
        );

        if (manager.Retry.Result!.Option == "Cancel")
            return;

        // Both steps answered -- proceed with save
    }

    await base.OnBeforeSaveAsync(obj, entity);
}
```

The user sees two sequential modals. The flow:

1. First request: Step 0 fires, HTTP 449 returned, user sees "Report vehicle as stolen" modal
2. User clicks "Confirm" -- second request sent with `retryResults: [{ step: 0, option: "Confirm" }]`
3. Step 0 replays (already answered), Step 1 fires, HTTP 449 returned, user sees "Notify fleet managers" modal
4. User clicks "Yes, notify" -- third request sent with both results
5. Both steps replay, save proceeds

### The Cancel Option

When the user dismisses the modal (clicking the X button or pressing Escape), the frontend sends `"Cancel"` as the option. You do not need to include "Cancel" in your `options` array -- it is always available as a dismiss action. Check for it in your code to abort the operation:

```csharp
if (manager.Retry.Result!.Option == "Cancel")
    return;
```

## Action() Parameters

```csharp
void Action(
    string title,              // Modal title
    string[] options,          // Button labels shown in the modal footer
    string? defaultOption,     // Optional: which button gets primary styling
    PersistentObject? persistentObject,  // Optional: form fields to show in the modal body
    string? message            // Optional: text message shown in the modal body
);
```

| Parameter | Required | Description |
|---|---|---|
| `title` | Yes | The modal's header text |
| `options` | Yes | Array of button labels. Each becomes a button in the modal footer |
| `defaultOption` | No | Which option gets primary (blue) button styling |
| `persistentObject` | No | A virtual PO with attributes -- renders as a form in the modal body |
| `message` | No | Plain text displayed in the modal body |

## Custom Dialog Forms

You can display a form inside the retry modal by passing a `PersistentObject` with attributes. Use `manager.GetPersistentObject()` to create one:

```csharp
manager.Retry.Action(
    title: "Enter reason",
    options: ["Submit"],
    persistentObject: manager.GetPersistentObject("ReasonForm",
        new PersistentObjectAttribute
        {
            Name = "Reason",
            DataType = "string",
            IsRequired = true,
        },
        new PersistentObjectAttribute
        {
            Name = "NotifyManager",
            DataType = "boolean",
        }
    )
);

if (manager.Retry.Result!.Option == "Cancel")
    return;

// Read the user's input
var reason = manager.Retry.Result.PersistentObject?
    .Attributes.FirstOrDefault(a => a.Name == "Reason")?.Value?.ToString();
```

The `PersistentObject` in `Result` contains the attribute values as filled in by the user.

## Translated Messages

Use `GetTranslatedMessage()` to display localized modal text. The key is looked up in `App_Data/translations.json`:

```csharp
manager.Retry.Action(
    title: manager.GetTranslatedMessage("confirm_delete_title"),
    options: [manager.GetTranslatedMessage("delete"), manager.GetTranslatedMessage("cancel")],
    message: manager.GetTranslatedMessage("confirm_delete_message", entity.LicensePlate)
);
```

`GetTranslatedMessage` uses the current request culture (from `Accept-Language` header). `GetMessage` takes an explicit language code.

## Frontend Integration

The retry action system works automatically with the `@mintplayer/ng-spark` library. Three pieces make it work:

### RetryActionService

A singleton Angular service that manages the modal lifecycle:

```typescript
@Injectable({ providedIn: 'root' })
export class RetryActionService {
  payload = signal<RetryActionPayload | null>(null);

  show(payload: RetryActionPayload): Promise<RetryActionResult>;
  respond(result: RetryActionResult): void;
}
```

### SparkRetryActionModalComponent

A pre-built modal component that displays the retry action dialog. Add it to your root component's template:

```html
<!-- app.html -->
<router-outlet />
<spark-retry-action-modal />
```

```typescript
import { SparkRetryActionModalComponent } from '@mintplayer/ng-spark';

@Component({
  imports: [RouterOutlet, SparkRetryActionModalComponent],
  // ...
})
export class AppComponent {}
```

### SparkService

The `SparkService.create()`, `.update()`, `.delete()`, and `.executeCustomAction()` methods automatically handle 449 responses. They intercept the error, display the modal via `RetryActionService`, collect the user's answer, and re-submit the request with the accumulated `retryResults`. No custom error handling is needed in page components.

## Request/Response Format

The request body for Create and Update operations wraps the PersistentObject and any retry results:

```json
{
  "persistentObject": { /* ... */ },
  "retryResults": [
    { "step": 0, "option": "Confirm" },
    { "step": 1, "option": "Yes, notify" }
  ]
}
```

For Delete operations, the retry results are sent in the request body (the endpoint reads the body only when `ContentLength > 0`).

## Complete Example

See the Fleet demo app for a working example:

- `Demo/Fleet/Fleet/Actions/CarActions.cs` -- chained retry actions on save and delete
- `MintPlayer.Spark/Services/RetryAccessor.cs` -- step tracking and replay logic
- `MintPlayer.Spark/Exceptions/SparkRetryActionException.cs` -- the internal exception
- `MintPlayer.Spark/Endpoints/PersistentObject/Create.cs` -- endpoint catching 449
- `node_packages/ng-spark/src/lib/services/retry-action.service.ts` -- Angular service
- `node_packages/ng-spark/src/lib/components/retry-action-modal/spark-retry-action-modal.component.ts` -- modal component
- `node_packages/ng-spark/src/lib/services/spark.service.ts` -- automatic 449 handling

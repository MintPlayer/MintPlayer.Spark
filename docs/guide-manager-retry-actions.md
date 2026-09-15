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

The Retry Action pattern uses HTTP status **449** (Retry With) to signal the caller that user input is needed before the operation can complete. The caller collects the answer and re-submits the **same request** with it appended.

Two callers do this today, and they speak the same wire: the Angular frontend (which shows a modal) and `MintPlayer.Spark.Client` (which asks a `SparkRetryHandler`, or hands the question back as a result).

### ⚠️ All nine hooks that can prompt now do

A retry is no longer a custom-action feature. Every endpoint that runs application code can raise one — create, update, delete, delete-row, new, refresh, load, query and execute-action — because **reads became `POST`** precisely so they would have a body to carry the answers in.

Two consequences worth knowing before using it from a read hook:

- **A prompt raised in `OnLoadAsync` fires on every read of that type**, including the loads that delete, refresh and delete-row perform on their way elsewhere. `OnQueryAsync` fires on every execution, including the ones a grid issues while paging. A handler that answers unconditionally answers far more often than it looks.
- **The 449 is emitted centrally**, by one `catch` in `SparkMiddleware` — not by each endpoint. Adding a new endpoint that runs a hook gets retry support without doing anything; the older per-endpoint `catch` blocks are gone.

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

`SparkService` handles 449 automatically on every method that can receive one. It intercepts the error, displays the modal via `RetryActionService`, collects the user's answer, and re-submits the request with the accumulated `retryResults`. No custom error handling is needed in page components.

### From .NET — `MintPlayer.Spark.Client`

The same conversation, without a browser:

```csharp
await client.UpdatePersistentObjectAsync(car, onRetry: async (prompt, ct) =>
    prompt.Step == 0 ? RetryAnswer.Choose("Confirm") : RetryAnswer.Cancel());
```

Pass no handler and the prompt comes back instead of being answered — as `SparkActionResult.IsRetry` for an action (answer it with `ContinueAsync`), or as `SparkRetryRequiredException` elsewhere. See the [client README](../libs/client/MintPlayer.Spark.Client/README.md).

⚠️ **`step` comes from the server; never count answers locally.** A hook may skip a step — asking the second question only when the first was answered a particular way — and a local counter agrees right up until that happens, then silently answers a different question.

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

⚠️ **Every call is a `POST` to a literal path with its parameters in the body**, deletes and reads included — so `retryResults` rides along the same way everywhere. The older shapes (a `DELETE` whose body was read only when `ContentLength > 0`, a `GET` with the type in the route) are gone.

⚠️ **The whole body is resent on each attempt, not a diff.** The server replays the hook from the top and feeds it the accumulated answers, so a request carrying only the answers would have nothing to replay.

## Complete Example

See the Fleet demo app for a working example:

- `apps/Fleet/Fleet/Actions/CarActions.cs` -- chained retry actions on save and delete
- `libs/spark/MintPlayer.Spark/Services/RetryAccessor.cs` -- step tracking and replay logic
- `libs/spark/MintPlayer.Spark/Exceptions/SparkRetryActionException.cs` -- the internal exception
- `libs/spark/MintPlayer.Spark/SparkMiddleware.cs` -- the single `catch` that turns a raised retry into its 449
- `libs/node_packages/ng-spark/services/src/retry-action.service.ts` -- Angular service
- `libs/node_packages/ng-spark/retry-action-modal/src/spark-retry-action-modal.component.ts` -- modal component
- `libs/node_packages/ng-spark/services/src/spark.service.ts` -- automatic 449 handling
- `libs/client/MintPlayer.Spark.Client/SparkClient.cs` -- the .NET conversation loop

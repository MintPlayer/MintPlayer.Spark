# Manager and Retry Actions

Spark provides the `IManager` interface for accessing framework utilities inside Actions classes. Its most powerful feature is the **Retry Action** pattern, which lets backend code prompt the user for confirmation or input through modal dialogs -- without writing any frontend code.

## Overview

The `IManager` interface exposes:

| Member | Purpose |
|---|---|
| `Retry` | Access to the Retry Action subsystem (`IRetryAccessor`) |
| `GetPersistentObjectAsync(name, verb)` | Build a PersistentObject from its model, **for the current caller** ([below](#construction-applies-the-callers-rights)) — dialog forms, composed pages |
| `AsSystem()` | The elevated construction: whole objects, for system work. Named so it can be reviewed |
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

Call `manager.Retry.Action()` in any before-interceptor ([persistence interceptors](guide-interceptors.md): `IBeforeSave<T>`, `IBeforeDelete<T>` — on an interceptor class or on the Actions class itself) to prompt the user:

```csharp
public partial class CarActions : DefaultPersistentObjectActions<Car>, IBeforeDelete<Car>
{
    [Inject] private readonly IManager manager;

    public ValueTask OnBeforeDeleteAsync(Car entity, DeleteContext context)
    {
        manager.Retry.Action(
            title: "Confirm deletion",
            options: ["Delete"],
            message: $"Are you sure you want to delete {entity.LicensePlate}?",
            cancellable: true
        );

        // Cancel deletes nothing: a silent no-op, answered with 204 (#482).
        if (manager.Retry.Result!.Option == "Cancel")
            throw new SparkCancelException();

        return ValueTask.CompletedTask;
    }
}
```

`return` would not stop anything: the framework, not the interceptor, does the delete. `SparkCancelException` tells it to write nothing, keep nothing the interceptors stored, and run no after-interceptor. A prompt during a **bulk** delete is refused (that row is named in the refusal): a per-row prompt across a selection is unworkable.

### How It Works

1. On the first invocation, `Action()` throws a `SparkRetryActionException` internally -- it never returns
2. The endpoint catches the exception and responds with HTTP 449 and a JSON payload describing the dialog
3. The Angular frontend displays a modal with the title, message, and option buttons
4. The user clicks a button (or, on a `cancellable` prompt, Cancel or the modal's close, which send `"Cancel"`)
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
public ValueTask OnBeforeSaveAsync(Car entity, SaveContext context)
{
    var statusAttr = context.PersistentObject.Attributes.FirstOrDefault(a => a.Name == nameof(Car.Status));
    if (statusAttr?.IsValueChanged == true && entity.Status == CarStatus.Stolen)
    {
        // Step 0: Confirm marking as stolen
        manager.Retry.Action(
            title: "Report vehicle as stolen",
            options: ["Confirm"],
            message: $"Are you sure you want to mark {entity.LicensePlate} as stolen? " +
                     "This will lock the vehicle record.",
            cancellable: true
        );

        if (manager.Retry.Result!.Option == "Cancel")
            throw new SparkCancelException();

        // Step 1: Ask whether to notify fleet managers
        manager.Retry.Action(
            title: "Notify fleet managers",
            options: ["Yes, notify", "No, skip"],
            message: "Should all fleet managers be notified about this stolen vehicle?",
            cancellable: true
        );

        if (manager.Retry.Result!.Option == "Cancel")
            throw new SparkCancelException();

        // Both steps answered -- the framework proceeds with the save
    }

    return ValueTask.CompletedTask;
}
```

The user sees two sequential modals. The flow:

1. First request: Step 0 fires, HTTP 449 returned, user sees "Report vehicle as stolen" modal
2. User clicks "Confirm" -- second request sent with `retryResults: [{ step: 0, option: "Confirm" }]`
3. Step 0 replays (already answered), Step 1 fires, HTTP 449 returned, user sees "Notify fleet managers" modal
4. User clicks "Yes, notify" -- third request sent with both results
5. Both steps replay, save proceeds

### The Cancel Option

Cancel is not one of your `options`: pass `cancellable: true`. The client then adds a Cancel button of its own, labelled in the user's language (`common.cancel`), and answers it, and the modal being closed (the X button or Escape), with `"Cancel"` (`RetryResult.CancelOption`). So the label is translated and the answer never is. `Action` refuses an `options` array that contains `"Cancel"` (`ArgumentException`). Without `cancellable`, closing the modal abandons the request and the action never hears of it.

Check for it in your code to abort the operation. In a save or delete interceptor, abort with `SparkCancelException` — the framework then writes nothing (a `return` would let the write go ahead); in a custom action, a plain `return` is enough, since the action itself is the work:

```csharp
if (manager.Retry.Result!.Option == "Cancel")
    throw new SparkCancelException(); // in a custom action: return;
```

## Action() Parameters

```csharp
void Action(
    string title,              // Modal title
    string[] options,          // Button labels shown in the modal footer
    string? defaultOption,     // Optional: which button gets primary styling
    PersistentObject? persistentObject,  // Optional: form fields to show in the modal body
    string? message,           // Optional: text message shown in the modal body
    bool cancellable           // Optional: the client adds a translated Cancel, answered as "Cancel"
);
```

| Parameter | Required | Description |
|---|---|---|
| `title` | Yes | The modal's header text |
| `options` | Yes | Array of button labels. Each becomes a button in the modal footer |
| `defaultOption` | No | Which option gets primary (blue) button styling |
| `persistentObject` | No | A virtual PO with attributes -- renders as a form in the modal body |
| `message` | No | Plain text displayed in the modal body |
| `cancellable` | No | Adds the client's own translated Cancel button; it and closing the modal answer `"Cancel"`. Never put `"Cancel"` in `options` |

## Custom Dialog Forms

You can display a form inside the retry modal by passing a `PersistentObject`. Declare its shape as a
virtual type in `App_Data/Model/ReasonForm.json` (no `clrType`; attributes `Reason`, a required
string, and `NotifyManager`, a boolean) and build it with `GetPersistentObjectAsync`:

```csharp
var form = await manager.GetPersistentObjectAsync("ReasonForm");

manager.Retry.Action(
    title: "Enter reason",
    options: ["Submit"],
    cancellable: true,
    persistentObject: form
);

if (manager.Retry.Result!.Option == "Cancel")
    throw new SparkCancelException(); // in a custom action: return;

// Read the user's input
var reason = manager.Retry.Result.PersistentObject?
    .Attributes.FirstOrDefault(a => a.Name == "Reason")?.Value?.ToString();
```

The `PersistentObject` in `Result` contains the attribute values as filled in by the user. A prompt
form needs no right of its own: it is only shown, never loaded through an endpoint. The passkeys page's
`PasskeyRename` form is a worked example (`MintPlayer.Spark.Authorization`, `RenamePasskeyAction`).

### Construction applies the caller's rights

`GetPersistentObjectAsync(name | id | <T>, verb = "Read")` builds the object **for the current
caller**, the same way the endpoints present one, so no hook can hand out what the caller may not
see:

| `verb` | Removed | Read-only |
|---|---|---|
| `Read` (default), `Edit` | Read-denied attributes | Edit-denied attributes |
| `New` | Read-denied attributes | New-denied attributes |
| `Query` | Query-denied attributes | — |

- **A refused attribute is removed, not blanked**, so not even its existence reaches the client.
- **Writing to a removed attribute is a silent no-op** (`obj["Salary"].Value = …` does nothing), and
  reading it gives `null`. A name the type never had still throws. Ask
  `obj.TryGetAttribute("Salary", out var attribute)` when the difference matters.
  `manager.Client.RefreshAttribute(...)` for a removed attribute is a no-op too.
- **A hook that needs a value the caller may not see** builds the object with
  `manager.AsSystem().GetPersistentObject(name)`: whole, synchronous, decides nothing. The name is
  there so a reviewer can find every elevation.
- **Outside a request** (a background job, a cron task) the caller is the system, and nothing is
  removed.
- **The boundary net.** Every persistent object written to a response (the envelope, query rows,
  retry prompts, client operations) passes one serialization hook. An object that was not built for
  the caller — `new PersistentObject { … }`, or one from `AsSystem()` — **throws in Development**, and
  outside Development is pruned (fail closed: Query- and Read-denied removed, Edit-denied read-only)
  with a warning in the log. So build prompt and page objects with `GetPersistentObjectAsync`, never
  by hand.
- A static `Read` deny on a required attribute the caller **did not post** makes it unwritable for
  that save: it keeps its stored value (on a create, what your actions class or an interceptor
  fills in), and validation skips it. Fill such an attribute server-side, or that caller's create
  stores the default.

## Client-method retries: a step that runs in the browser

Some steps can only run in the browser: a WebAuthn ceremony, a file picked from disk, a clipboard
read. `Retry.Invoke` asks the client to run a **registered** method and hands its answer back to the
same action:

```csharp
Task Invoke(string clientMethod, Func<Task<object?>> arguments);
```

On the first pass `Invoke` throws the retry signal, and the 449 carries a `retry` operation with
`clientMethod` and the serialized `arguments` (`options` is empty). The client awaits its method
instead of showing a modal and re-sends the request with `{ step, option: "OK", value }`. On that
pass `Invoke` returns, and `Result.Value` (a `JsonElement`) holds the method's answer. `Invoke` and
`Action` share one step counter, so "ask the browser, then ask the user, then ask the browser again"
is three calls in one action.

**The worked example: adding a passkey** (`AddPasskeyAction`, `MintPlayer.Spark.Authorization`).

```csharp
public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
{
    if (!await passkeys.IsAvailableAsync()) { /* notify, return */ }   // side-effect free: runs on both passes

    // Pass 1: mint the challenge (sets the ceremony's state cookie) and ask the browser.
    await manager.Retry.Invoke("webauthn.create", async () => (object?)await passkeys.CreationOptionsAsync());

    // Pass 2: the browser's credential.
    var answer = manager.Retry.Result!;
    if (answer.Option == RetryResult.CancelOption || answer.Value is not { ValueKind: JsonValueKind.Object } credential)
        return;                                    // the user dismissed the authenticator prompt
    var outcome = await passkeys.RegisterAsync(credential.GetRawText());
    // notify, refresh the query
}
```

On the client, `@mintplayer/ng-spark/auth`'s `provideSparkAuth()` registers `webauthn.create`. An
application registers its own the same way:

```typescript
import { provideSparkClientMethods } from '@mintplayer/ng-spark/client-operations';

provideSparkClientMethods({
  'files.pick': {
    invoke: async (args) => { /* runs in an injection context; resolve the answer */ },
    supported: () => 'showOpenFilePicker' in window,   // optional
    unsupportedReason: 'files.pickUnsupported',        // optional translation key
  },
});
```

The registration is multi-provided, and a later registration of a name wins.

⚠️ **The trap: the action re-runs from the top on every pass.** Everything before `Invoke` runs again
when the answer arrives. Had the passkey action called `CreationOptionsAsync()` before `Invoke`, pass
2 would have minted a new challenge and overwritten the state cookie, and the attestation would have
failed. That is why `arguments` is a **factory**: it runs only while the step is unanswered. Keep
every line before a retry — `Invoke` or `Action` — side-effect free or idempotent, and put the
expensive or stateful work inside the factory or after the retry returns.

- **The set of methods is closed.** The server can only name a method the client registered; nothing
  it sends is evaluated. An unknown name (one `console.warn`), a method that rejects or throws, and an
  unsupported one (which is not run) all answer `option: "Cancel"`. A client-method Cancel is always
  sent to the server, never swallowed, so the action decides what a cancelled browser step means.
- **Arguments are serialized at `Invoke` time** with the application's HTTP JSON options, so their
  spelling matches the envelope, and a persistent object inside them still meets the boundary net.
- **State that must survive to pass 2** travels with the request: the 449 can set cookies (the passkey
  ceremony's state cookie does), and the answers ride in `retryResults`.
- **From .NET**, `MintPlayer.Spark.Client` sees `RetryActionPayload.ClientMethod` and `Arguments` and
  answers with `RetryAnswer.Return(value)`.

### `requiresClient`: disable an action the browser cannot run

An `actions.json` entry can name the client method it depends on:

```json
"AddPasskey": { "icon": "plus-lg", "showedOn": "detail", "selectionRule": "=0", "requiresClient": "webauthn.create" }
```

`/spark/actions/list` passes it on. Where the method is missing or its `supported()` says no, the grid
toolbar, query card, query list, row menu and detail action bar show the action disabled, with the
translated reason as its tooltip: the method's `unsupportedReason`, or `common.clientUnsupported`.
`requiresClient` is presentation; the server still runs whatever a caller with the right posts.

## Translated Messages

Use `GetTranslatedMessage()` to display localized modal text. The key is looked up in `App_Data/translations.json`:

```csharp
manager.Retry.Action(
    title: manager.GetTranslatedMessage("confirm_delete_title"),
    options: [manager.GetTranslatedMessage("delete")],
    message: manager.GetTranslatedMessage("confirm_delete_message", entity.LicensePlate),
    cancellable: true // the client's Cancel is translated already, and still answers "Cancel"
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

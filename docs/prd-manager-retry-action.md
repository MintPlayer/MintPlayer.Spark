# PRD: IManager Interface & Retry Action Pattern

## Overview

Introduce an `IManager` interface (abstractions) and `internal Manager` class (core) that developers can inject into their Actions classes. The Manager provides two key capabilities:

1. **`NewPersistentObject()`** — Create virtual/temporary PersistentObjects with custom attributes (not backed by a database entity).
2. **`Retry.Action()` / `Retry.Result`** — An exception-driven confirmation/dialog pattern that lets action methods pause execution, present a modal to the user, and resume with the user's response.

## Motivation

Many business workflows require user confirmation or additional input mid-action. For example:
- "Are you sure you want to delete this?" (simple confirmation)
- "This invoice amount changed from €100 to €200. Continue?" (contextual confirmation)
- "Select a retry strategy:" with a form containing dropdown + text fields (complex dialog)

The Retry Action pattern solves this elegantly: the developer writes a single method that checks `Retry.Result`, calls `Retry.Action()` if no result yet (which throws an exception), and handles the result on the second invocation. The framework catches the exception and returns structured data to the frontend for modal rendering.

## Architecture

### New Types

#### MintPlayer.Spark.Abstractions

```
IManager                          — Public interface, injectable by developers
IRetryAccessor                    — Public interface for Retry.Action() / Retry.Result
RetryResult                       — Public class holding the user's response
```

#### MintPlayer.Spark (Core)

```
Manager                           — Internal implementation of IManager (scoped)
RetryAccessor                     — Internal implementation of IRetryAccessor
SparkRetryActionException         — Internal exception thrown by Retry.Action()
```

### Component Diagram

```
Developer's Actions Class (CRUD hooks or future Custom Actions)
    │
    ├── Injects IManager
    │       │
    │       ├── .NewPersistentObject(name, attributes[])
    │       │       → Returns a virtual PersistentObject (no DB backing)
    │       │
    │       └── .Retry
    │               ├── .Result  → RetryResult? (null until step is answered)
    │               └── .Action(title, options[], ...)
    │                       → Auto-appends "Cancel" if missing
    │                       → On unanswered step: throws SparkRetryActionException
    │                       → On answered step: sets Result and returns
    │
    ▼
Endpoint (Create/Update/Delete + future Custom Actions)
    │
    ├── Deserialize retryResult from request (if present)
    ├── Set RetryAccessor.AnsweredStep + AnsweredResult
    ├── try { actions.OnSaveAsync(...) }
    │   catch (SparkRetryActionException ex)
    │       → Return 449 with { step, title, message, options[], persistentObject }
    │
    ▼
Frontend (BsModalComponent)
    │
    ├── Receives 449 retry payload
    ├── Renders PersistentObject attributes in modal (if present)
    ├── Renders action buttons from options[] (always includes "Cancel")
    ├── User clicks a button OR closes modal (→ "Cancel")
    └── Re-submits the original request with retryResult { step, option, persistentObject }
```

## Detailed Design

### 1. IManager Interface

**Location:** `MintPlayer.Spark.Abstractions/IManager.cs`

```csharp
public interface IManager
{
    /// <summary>
    /// Creates a virtual PersistentObject (not backed by a DB entity).
    /// Useful for building custom dialogs in Retry.Action().
    /// </summary>
    PersistentObject NewPersistentObject(string name, params PersistentObjectAttribute[] attributes);

    /// <summary>
    /// Access to the Retry Action subsystem.
    /// </summary>
    IRetryAccessor Retry { get; }
}
```

### 2. IRetryAccessor Interface

**Location:** `MintPlayer.Spark.Abstractions/Retry/IRetryAccessor.cs`

```csharp
public interface IRetryAccessor
{
    /// <summary>
    /// The result from the user's previous retry response for the current step.
    /// Null on the first invocation, or when a new (not-yet-answered) step is reached.
    /// </summary>
    RetryResult? Result { get; }

    /// <summary>
    /// Interrupts the current action and requests the frontend to display
    /// a confirmation/dialog modal. Never returns — throws internally.
    ///
    /// If options[] does not contain "Cancel", the framework auto-appends it.
    /// The frontend always re-invokes with the result (including cancellation).
    /// </summary>
    [DoesNotReturn]
    void Action(
        string title,
        string[] options,
        string? defaultOption = null,
        PersistentObject? persistentObject = null,
        string? message = null
    );
}
```

### 3. RetryResult Class

**Location:** `MintPlayer.Spark.Abstractions/Retry/RetryResult.cs`

```csharp
public sealed class RetryResult
{
    /// <summary>
    /// The label of the button the user clicked.
    /// </summary>
    public required string Option { get; init; }

    /// <summary>
    /// The step index this result corresponds to (0-based).
    /// Managed automatically by the framework.
    /// </summary>
    public int Step { get; init; }

    /// <summary>
    /// The PersistentObject with attribute values as filled in by the user.
    /// Null if no PersistentObject was shown in the modal.
    /// </summary>
    public PersistentObject? PersistentObject { get; init; }
}
```

### 4. Internal Manager Implementation

**Location:** `MintPlayer.Spark/Services/Manager.cs`

```csharp
[Register(typeof(IManager), ServiceLifetime.Scoped)]
internal sealed partial class Manager : IManager
{
    [Inject] private readonly IRetryAccessor retry;

    public IRetryAccessor Retry => retry;

    public PersistentObject NewPersistentObject(string name, params PersistentObjectAttribute[] attributes)
    {
        return new PersistentObject
        {
            Id = null,  // Virtual — no DB identity
            Name = name,
            ObjectTypeId = Guid.Empty,  // Signals virtual PO
            Attributes = attributes,
        };
    }
}
```

### 5. Internal RetryAccessor Implementation

**Location:** `MintPlayer.Spark/Services/RetryAccessor.cs`

```csharp
[Register(typeof(IRetryAccessor), ServiceLifetime.Scoped)]
internal sealed partial class RetryAccessor : IRetryAccessor
{
    /// <summary>
    /// The step index of the retry that was answered by the user.
    /// Set by the endpoint from the incoming request's retryResult.step value.
    /// </summary>
    internal int? AnsweredStep { get; set; }

    /// <summary>
    /// The full result payload from the user's response.
    /// Set by the endpoint from the incoming request's retryResult value.
    /// </summary>
    internal RetryResult? AnsweredResult { get; set; }

    /// <summary>
    /// Tracks the current step index during action execution.
    /// Incremented each time Action() is called.
    /// </summary>
    private int currentStep;

    public RetryResult? Result { get; private set; }

    [DoesNotReturn]
    public void Action(
        string title,
        string[] options,
        string? defaultOption = null,
        PersistentObject? persistentObject = null,
        string? message = null)
    {
        var step = currentStep++;

        // If this step was already answered, expose the result and continue
        // (This is handled in the property — see below)
        // If the answered step matches, set Result so the developer can read it
        if (AnsweredStep.HasValue && step <= AnsweredStep.Value)
        {
            if (step == AnsweredStep.Value)
            {
                Result = AnsweredResult;
                return; // ← This is the one case where Action() DOES return
            }
            // step < AnsweredStep: a previously-answered step, skip it
            return;
        }

        // Auto-append "Cancel" if not present (case-insensitive)
        if (!options.Any(o => o.Equals("Cancel", StringComparison.OrdinalIgnoreCase)))
        {
            options = [.. options, "Cancel"];
        }

        // New unanswered step — throw to interrupt and prompt the user
        throw new SparkRetryActionException(step, title, options, defaultOption, persistentObject, message);
    }
}
```

> **Note:** `Action()` is marked `[DoesNotReturn]` on the interface for the developer's benefit (their code after `Action()` is unreachable on the first pass). Internally, the implementation *does* return when replaying already-answered steps. The `[DoesNotReturn]` attribute on the interface is a developer-facing hint; the concrete class omits it.

### 6. Internal SparkRetryActionException

**Location:** `MintPlayer.Spark/Exceptions/SparkRetryActionException.cs`

```csharp
internal sealed class SparkRetryActionException : Exception
{
    public int Step { get; }
    public string Title { get; }
    public string[] Options { get; }
    public string? DefaultOption { get; }
    public PersistentObject? PersistentObject { get; }
    public string? RetryMessage { get; }

    public SparkRetryActionException(
        int step,
        string title,
        string[] options,
        string? defaultOption,
        PersistentObject? persistentObject,
        string? message)
        : base($"Retry action requested at step {step}: {title}")
    {
        Step = step;
        Title = title;
        Options = options;
        DefaultOption = defaultOption;
        PersistentObject = persistentObject;
        RetryMessage = message;
    }
}
```

### 7. Endpoint Exception Handling

Each endpoint that invokes actions hooks (Create, Update, Delete) wraps the call in a try-catch for `SparkRetryActionException`. On catch, it returns a structured JSON response with HTTP **449 Retry With** status code.

**Response payload:**

```json
{
    "type": "retry-action",
    "step": 0,
    "title": "Are you sure?",
    "message": "This will delete all related records.",
    "options": ["Yes", "No", "Cancel"],
    "defaultOption": "Yes",
    "persistentObject": null
}
```

> Note: `"Cancel"` is always present in `options[]`. If the developer didn't include it, the framework auto-appended it.

### 8. Request Payload (Re-invocation)

When the frontend re-submits after the user responds, it includes a `retryResult` property in the request body:

```json
{
    "persistentObject": { ... },
    "retryResult": {
        "step": 0,
        "option": "Yes",
        "persistentObject": null
    }
}
```

The endpoint deserializes this and sets `RetryAccessor.AnsweredStep` and `RetryAccessor.AnsweredResult` before re-invoking the actions hook. The RetryAccessor replays past steps automatically and only exposes `Result` when the current step matches the answered step.

**Chained retry flow example (2 confirmations):**

```
1st invocation:  step 0 throws → frontend shows modal A
2nd invocation:  retryResult.step=0, option="Continue"
                 step 0 returns (answered) → Result = { Option: "Continue" }
                 step 1 throws → frontend shows modal B
3rd invocation:  retryResult.step=1, option="Yes, downgrade"
                 step 0 returns (skip, step < answered)
                 step 1 returns (answered) → Result = { Option: "Yes, downgrade" }
                 action completes normally
```

## Developer Usage Examples

### Example 1: Simple Confirmation on Delete

```csharp
public partial class PersonActions : DefaultPersistentObjectActions<Person>
{
    [Inject] private readonly IManager manager;

    public override async Task OnBeforeDeleteAsync(Person entity)
    {
        // "Cancel" is auto-appended → options sent to frontend: ["Delete", "Cancel"]
        manager.Retry.Action(
            title: "Confirm deletion",
            options: ["Delete"],
            message: $"Are you sure you want to delete {entity.FirstName}?"
        );

        // After re-invocation, Result is always non-null
        if (manager.Retry.Result!.Option == "Cancel")
            throw new InvalidOperationException("Deletion cancelled by user.");

        await base.OnBeforeDeleteAsync(entity);
    }
}
```

### Example 2: Confirmation with Virtual PO for Extra Input

```csharp
public partial class InvoiceActions : DefaultPersistentObjectActions<Invoice>
{
    [Inject] private readonly IManager manager;

    public override async Task OnBeforeSaveAsync(PersistentObject obj, Invoice entity)
    {
        var dialog = manager.NewPersistentObject("Confirm Changes",
            new PersistentObjectAttribute { Name = "Reason", DataType = "string", IsRequired = true },
            new PersistentObjectAttribute { Name = "NotifyCustomer", DataType = "boolean", Value = true }
        );

        // "Cancel" auto-appended → ["Confirm", "Cancel"]
        manager.Retry.Action(
            title: "Invoice amount changed",
            options: ["Confirm"],
            defaultOption: "Confirm",
            persistentObject: dialog,
            message: $"Amount changed from {entity.OldAmount} to {entity.NewAmount}."
        );

        // After re-invocation:
        var result = manager.Retry.Result!;
        if (result.Option == "Cancel")
            throw new InvalidOperationException("Save cancelled by user.");

        // Read values from the dialog PO the user filled in
        var reason = result.PersistentObject!.Attributes
            .First(a => a.Name == "Reason").GetValue<string>();

        entity.ChangeReason = reason;

        await base.OnBeforeSaveAsync(obj, entity);
    }
}
```

### Example 3: Chained Confirmations (Automatic Step Tracking)

The framework tracks which retry step was answered. The developer simply writes
linear code — no manual step index management needed:

```csharp
public override async Task OnBeforeSaveAsync(PersistentObject obj, Invoice entity)
{
    // Step 0: amount change confirmation
    if (entity.AmountChanged)
    {
        manager.Retry.Action(
            title: "Amount changed",
            options: ["Continue"],
            message: "The invoice amount was modified. Continue saving?"
        );
        // On re-invocation, Action() returns here (step was answered)
        // "Cancel" is auto-appended by the framework

        if (manager.Retry.Result!.Option == "Cancel")
            throw new InvalidOperationException("Cancelled by user.");
    }

    // Step 1: status downgrade confirmation
    if (entity.StatusDowngraded)
    {
        manager.Retry.Action(
            title: "Status downgrade",
            options: ["Yes, downgrade"],
            message: "This will downgrade the invoice status. Proceed?"
        );

        if (manager.Retry.Result!.Option == "Cancel")
            throw new InvalidOperationException("Cancelled by user.");
    }

    await base.OnBeforeSaveAsync(obj, entity);
}
```

**What happens under the hood:**

| Invocation | Step 0 (amount) | Step 1 (status) | Outcome |
|------------|-----------------|-----------------|---------|
| 1st call | Throws (unanswered) | — | 449 → modal for step 0 |
| 2nd call (step=0, "Continue") | Returns, Result="Continue" | Throws (unanswered) | 449 → modal for step 1 |
| 3rd call (step=1, "Yes, downgrade") | Skipped (step < answered) | Returns, Result="Yes, downgrade" | Save completes |

## Automatic Retry Step Tracking

The `RetryAccessor` maintains an internal `currentStep` counter that increments each time `Action()` is called. Combined with the `AnsweredStep` (from the incoming request), this enables automatic replay of previously-answered steps:

**Algorithm in `RetryAccessor.Action()`:**

```
1. step = currentStep++
2. if AnsweredStep has value AND step < AnsweredStep:
     → Skip (previously answered step, not the latest) — return without setting Result
3. if AnsweredStep has value AND step == AnsweredStep:
     → Set Result = AnsweredResult, return (the developer reads Result after this call)
4. if step > AnsweredStep (or AnsweredStep is null):
     → New unanswered step — auto-append "Cancel" if missing, throw SparkRetryActionException
```

**Key invariant:** Each `Retry.Action()` call in the developer's code corresponds to a deterministic step index. As long as the developer's code is deterministic (same conditions → same `Action()` calls), the replay is safe.

**Conditional retry calls:** If a `Retry.Action()` call is inside an `if` block (e.g., only ask when amount changed), the step counter only increments when that branch is entered. This is safe because the same data is re-sent on re-invocation, so the same branches execute.

## Affected Files

### New Files
| File | Package | Visibility |
|------|---------|------------|
| `MintPlayer.Spark.Abstractions/IManager.cs` | Abstractions | public |
| `MintPlayer.Spark.Abstractions/Retry/IRetryAccessor.cs` | Abstractions | public |
| `MintPlayer.Spark.Abstractions/Retry/RetryResult.cs` | Abstractions | public |
| `MintPlayer.Spark/Services/Manager.cs` | Core | internal |
| `MintPlayer.Spark/Services/RetryAccessor.cs` | Core | internal |
| `MintPlayer.Spark/Exceptions/SparkRetryActionException.cs` | Core | internal |

### Modified Files
| File | Change |
|------|--------|
| `MintPlayer.Spark/Endpoints/PersistentObject/Create.cs` | Add try-catch for SparkRetryActionException; deserialize retryResult from request |
| `MintPlayer.Spark/Endpoints/PersistentObject/Update.cs` | Same |
| `MintPlayer.Spark/Endpoints/PersistentObject/Delete.cs` | Same |
| Request/response DTOs (if separate) | Add `RetryResult?` property to request models |

## HTTP Contract

### Retry Response (449 Retry With)

```
HTTP/1.1 449 Retry With
Content-Type: application/json

{
    "type": "retry-action",
    "step": 0,
    "title": "...",
    "message": "...",
    "options": ["Yes", "No", "Cancel"],
    "defaultOption": "Yes",
    "persistentObject": null | { ... }
}
```

### Re-invocation Request

The original request body is re-sent with an additional `retryResult` envelope:

```
POST /spark/po/{objectTypeId}
Content-Type: application/json

{
    "persistentObject": { ... },
    "retryResult": {
        "step": 0,
        "option": "Yes",
        "persistentObject": null | { ... }
    }
}
```

## Design Decisions

1. **HTTP Status Code** — Use **449 Retry With**. It semantically matches the pattern and avoids ambiguity with real validation errors (400/422).

2. **Chained retries** — The framework **automatically tracks retry step index**. See [Automatic Retry Step Tracking](#automatic-retry-step-tracking) section below.

3. **Custom Actions** — The retry flow is designed generically so it works in CRUD endpoints today and in future custom action endpoints. Any endpoint that calls an actions hook can wrap it with the `SparkRetryActionException` try-catch pattern.

4. **Cancellation semantics** — There is **always** a cancel option. If the developer does not include a cancel option in their `options[]` array, the framework automatically appends one. When the user closes the modal (X button) or clicks the cancel option, the frontend **always re-invokes** the action with `RetryResult.Option` set to `"Cancel"`. This ensures the developer's action method always completes and can perform cleanup if needed. The developer is responsible for handling the cancel case (typically by returning early or throwing).

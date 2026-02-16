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
    │               ├── .Result  → RetryResult? (null on first invocation,
    │               │               pre-populated on re-invocation)
    │               └── .Action(title, options[], ...)
    │                       → Options sent to frontend exactly as specified
    │                       → On unanswered step: throws SparkRetryActionException
    │                       → On answered step: sets Result and returns
    │
    ▼
Endpoint (Create/Update/Delete + future Custom Actions)
    │
    ├── Deserialize retryResults[] from request (if present)
    ├── Build AnsweredResults dictionary, pre-populate Result
    ├── try { actions.OnSaveAsync(...) }
    │   catch (SparkRetryActionException ex)
    │       → Return 449 with { step, title, message, options[], persistentObject }
    │
    ▼
Frontend (BsModalComponent)
    │
    ├── Receives 449 retry payload
    ├── Renders PersistentObject attributes in modal (if present)
    ├── Renders action buttons from options[] (exactly as specified by developer)
    ├── User clicks a button OR closes modal (→ "Cancel")
    └── Re-submits the original request with retryResults[] (accumulated)
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
    /// The result from the user's previous retry response.
    /// Null on the first invocation. Pre-populated on re-invocation with the
    /// latest answered step's result, so developers can use a guard pattern:
    /// <code>
    /// if (manager.Retry.Result == null)
    ///     manager.Retry.Action(...);
    /// </code>
    /// Also set by each Action() call for the matching answered step.
    /// </summary>
    RetryResult? Result { get; }

    /// <summary>
    /// Requests the frontend to display a confirmation/dialog modal.
    /// On the first pass (unanswered step) this method throws internally and never returns.
    /// On replay of an already-answered step it returns normally and populates Result.
    ///
    /// The options are sent to the frontend exactly as specified.
    /// "Cancel" is NOT auto-appended. However, when the user closes the modal
    /// (e.g. via the X button), the frontend sends "Cancel" as the chosen option.
    /// </summary>
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
    /// All answered retry results, keyed by step index.
    /// Set by the endpoint from the incoming request's retryResults array.
    /// </summary>
    internal Dictionary<int, RetryResult>? AnsweredResults { get; set; }

    /// <summary>
    /// Tracks the current step index during action execution.
    /// Incremented each time Action() is called.
    /// </summary>
    private int currentStep;

    public RetryResult? Result { get; internal set; }

    public void Action(
        string title,
        string[] options,
        string? defaultOption = null,
        PersistentObject? persistentObject = null,
        string? message = null)
    {
        var step = currentStep++;

        // If this step was already answered, expose the result and continue
        if (AnsweredResults?.TryGetValue(step, out var result) == true)
        {
            Result = result;
            return;
        }

        // New unanswered step — throw to interrupt and prompt the user
        throw new SparkRetryActionException(step, title, options, defaultOption, persistentObject, message);
    }
}
```

> **Result pre-population:** On re-invocation, the endpoint pre-populates `Result` with the latest answered step's result *before* the action hook runs. This enables the guard pattern (`if (Result == null) Action(...)`). When `Action()` is called for a specific answered step, it overwrites `Result` with that step's result, enabling correct per-step reads in chained scenarios.

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

**Retry state setup (all endpoints):**

```csharp
if (request.RetryResults is { Length: > 0 } retryResults)
{
    var accessor = (RetryAccessor)retryAccessor;
    accessor.AnsweredResults = retryResults.ToDictionary(r => r.Step);
    accessor.Result = retryResults.OrderByDescending(r => r.Step).First();
}
```

**Response payload:**

```json
{
    "type": "retry-action",
    "step": 0,
    "title": "Are you sure?",
    "message": "This will delete all related records.",
    "options": ["Yes", "No"],
    "defaultOption": "Yes",
    "persistentObject": null
}
```

> Note: `options[]` contains exactly what the developer specified. "Cancel" is NOT auto-appended. The frontend sends "Cancel" when the user closes the modal (X button).

### 8. Request Payload (Re-invocation)

When the frontend re-submits after the user responds, it includes a `retryResults` array in the request body. The frontend **accumulates** results across multiple retry round-trips:

**After first retry response:**

```json
{
    "persistentObject": { ... },
    "retryResults": [
        { "step": 0, "option": "Yes", "persistentObject": null }
    ]
}
```

**After second retry response (chained):**

```json
{
    "persistentObject": { ... },
    "retryResults": [
        { "step": 0, "option": "Continue", "persistentObject": null },
        { "step": 1, "option": "Yes, downgrade", "persistentObject": null }
    ]
}
```

**For DELETE (body is only sent when retryResults are present):**

```json
{
    "retryResults": [
        { "step": 0, "option": "Delete", "persistentObject": null }
    ]
}
```

The endpoint builds a dictionary from `retryResults` keyed by step index, enabling O(1) lookups in `Action()`. It also pre-populates `Result` with the latest answered step's result (highest step index) so the guard pattern works.

**Chained retry flow example (2 confirmations):**

```
1st invocation:  step 0 throws → frontend shows modal A
2nd invocation:  retryResults=[{step:0, option:"Continue"}]
                 Result pre-populated with step 0 answer
                 step 0 Action() returns → Result = { Option: "Continue" }
                 step 1 throws → frontend shows modal B
3rd invocation:  retryResults=[{step:0, option:"Continue"}, {step:1, option:"Yes"}]
                 Result pre-populated with step 1 answer
                 step 0 Action() returns → Result = { Option: "Continue" }
                 step 1 Action() returns → Result = { Option: "Yes" }
                 action completes normally
```

## Developer Usage Patterns

There are two patterns for using the retry system. Both are fully supported.

### Pattern A: Guard Pattern (single-step, recommended for simple confirmations)

Check `Result == null` before calling `Action()`. On the first invocation, `Result` is null, so `Action()` is called and throws. On re-invocation, `Result` is pre-populated, so `Action()` is skipped and the developer reads the result directly.

```csharp
public partial class CarActions : DefaultPersistentObjectActions<Car>
{
    [Inject] private readonly IManager manager;

    public override async Task OnBeforeDeleteAsync(Car entity)
    {
        // Guard pattern: only call Action() on first invocation
        if (manager.Retry.Result == null)
        {
            // Options sent to frontend: ["Delete"]
            // "Cancel" is NOT auto-appended, but closing the modal sends "Cancel"
            manager.Retry.Action(
                title: "Confirm deletion",
                options: ["Delete"],
                message: $"Are you sure you want to delete {entity.LicensePlate}?"
            );
        }

        // After re-invocation, Result is always non-null
        if (manager.Retry.Result!.Option == "Cancel")
        {
            // User closed modal or clicked Cancel — clean up and return
            return;
        }

        await base.OnBeforeDeleteAsync(entity);
    }
}
```

### Pattern B: Direct Call Pattern (multi-step, for chained confirmations)

Call `Action()` for each step. It either returns (answered step) or throws (unanswered step). The framework tracks step indices automatically.

```csharp
public partial class CarActions : DefaultPersistentObjectActions<Car>
{
    [Inject] private readonly IManager manager;

    public override async Task OnBeforeSaveAsync(PersistentObject obj, Car entity)
    {
        var statusAttr = obj.Attributes.FirstOrDefault(a => a.Name == nameof(Car.Status));
        if (statusAttr?.IsValueChanged == true && entity.Status == CarStatus.Stolen)
        {
            // Step 0: Confirm marking as stolen
            manager.Retry.Action(
                title: "Report vehicle as stolen",
                options: ["Confirm"],
                message: $"Are you sure you want to mark {entity.LicensePlate} as stolen?"
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
        }

        await base.OnBeforeSaveAsync(obj, entity);
    }
}
```

**What happens under the hood:**

| Invocation | Step 0 (report stolen) | Step 1 (notify) | Outcome |
|------------|----------------------|-----------------|---------|
| 1st call | Throws (unanswered) | — | 449 → modal for step 0 |
| 2nd call (step 0 answered) | Returns, Result="Confirm" | Throws (unanswered) | 449 → modal for step 1 |
| 3rd call (steps 0+1 answered) | Returns, Result="Confirm" | Returns, Result="Yes, notify" | Save completes |

### Example: Confirmation with Virtual PO for Extra Input

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

        manager.Retry.Action(
            title: "Invoice amount changed",
            options: ["Confirm"],
            defaultOption: "Confirm",
            persistentObject: dialog,
            message: $"Amount changed from {entity.OldAmount} to {entity.NewAmount}."
        );

        var result = manager.Retry.Result!;
        if (result.Option == "Cancel")
            return;

        // Read values from the dialog PO the user filled in
        var reason = result.PersistentObject!.Attributes
            .First(a => a.Name == "Reason").GetValue<string>();

        entity.ChangeReason = reason;

        await base.OnBeforeSaveAsync(obj, entity);
    }
}
```

## Automatic Retry Step Tracking

The `RetryAccessor` maintains an internal `currentStep` counter that increments each time `Action()` is called. Combined with the `AnsweredResults` dictionary (built from the incoming request's `retryResults` array), this enables automatic replay of previously-answered steps:

**Algorithm in `RetryAccessor.Action()`:**

```
1. step = currentStep++
2. if AnsweredResults contains step:
     → Set Result = AnsweredResults[step], return (the developer reads Result after this call)
3. otherwise:
     → New unanswered step — throw SparkRetryActionException
```

**Result pre-population:** Before the action hook runs, the endpoint pre-populates `Result` with the latest answered step's result (highest step index from `retryResults`). This enables the guard pattern where developers check `if (Result == null)` before calling `Action()`.

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
| `MintPlayer.Spark/Endpoints/PersistentObject/Create.cs` | Try-catch for SparkRetryActionException; deserialize `retryResults[]` from request |
| `MintPlayer.Spark/Endpoints/PersistentObject/Update.cs` | Same |
| `MintPlayer.Spark/Endpoints/PersistentObject/Delete.cs` | Same (reads body if ContentLength > 0) |
| `MintPlayer.Spark/Endpoints/PersistentObject/PersistentObjectRequest.cs` | `RetryResults` array property |

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
    "options": ["Yes", "No"],
    "defaultOption": "Yes",
    "persistentObject": null | { ... }
}
```

### Re-invocation Request

The original request body is re-sent with an accumulated `retryResults` array:

```
POST /spark/po/{objectTypeId}
Content-Type: application/json

{
    "persistentObject": { ... },
    "retryResults": [
        { "step": 0, "option": "Yes", "persistentObject": null },
        { "step": 1, "option": "Confirm", "persistentObject": null }
    ]
}
```

For DELETE requests, the body is only sent when retry results are present:

```
DELETE /spark/po/{objectTypeId}/{id}
Content-Type: application/json

{
    "retryResults": [
        { "step": 0, "option": "Delete", "persistentObject": null }
    ]
}
```

## Frontend Integration

### Accumulating Retry Results

The frontend accumulates retry results across multiple 449 round-trips. Each time a 449 is received and the user responds, the new result is appended to the `retryResults` array:

```typescript
private handleRetryError<T>(
    error: HttpErrorResponse,
    retryFn: () => Observable<T>,
    body: { retryResults?: RetryActionResult[] }
): Observable<T> {
    if (error.status !== 449 || error.error?.type !== 'retry-action') {
        return throwError(() => error);
    }
    const payload = error.error as RetryActionPayload;
    return this.retryActionService.show(payload).pipe(
        switchMap(result => {
            body.retryResults = [...(body.retryResults || []), result];
            return retryFn();
        })
    );
}
```

### DELETE with Retry

DELETE requests normally have no body. When retry results are present, the body is sent:

```typescript
private deleteWithRetry<T>(url: string, body: { retryResults?: RetryActionResult[] }): Observable<T> {
    const hasRetry = body.retryResults && body.retryResults.length > 0;
    return (hasRetry
        ? this.http.delete<T>(url, { body })
        : this.http.delete<T>(url)
    ).pipe(
        catchError((error: HttpErrorResponse) =>
            this.handleRetryError<T>(error, () => this.deleteWithRetry<T>(url, body), body))
    );
}
```

## Design Decisions

1. **HTTP Status Code** — Use **449 Retry With**. It semantically matches the pattern and avoids ambiguity with real validation errors (400/422).

2. **Accumulated retry results** — The frontend sends **all** previous retry results as an array (`retryResults[]`), not just the latest. This enables the backend to replay all answered steps correctly during chained retries. The backend builds an O(1) dictionary lookup from this array.

3. **No "Cancel" auto-append** — The `options[]` sent to the frontend are exactly what the developer specified. "Cancel" is never auto-appended. When the user closes the modal (X button), the frontend sends `"Cancel"` as the chosen option. This gives developers full control over button rendering while still supporting cancellation via modal close.

4. **Result pre-population** — On re-invocation, the endpoint pre-populates `Result` with the latest answered step's result before the action hook runs. This enables the guard pattern (`if (Result == null)`) for single-step scenarios without requiring `Action()` to be called.

5. **Two developer patterns** — The guard pattern (`if (Result == null) Action(...)`) is recommended for single-step confirmations. The direct call pattern (always calling `Action()`) is required for chained/multi-step flows where each step needs its own result.

6. **Custom Actions** — The retry flow is designed generically so it works in CRUD endpoints today and in future custom action endpoints. Any endpoint that calls an actions hook can wrap it with the `SparkRetryActionException` try-catch pattern.

# PRD: Spark Messaging System Overhaul

**Version:** 1.0
**Date:** April 3, 2026
**Status:** Draft
**Package:** `MintPlayer.Spark.Messaging`, `MintPlayer.Spark.Messaging.Abstractions`

---

## 1. Motivation

The current messaging system has two fundamental retry-correctness problems:

### Problem 1: Handler-level retry granularity is missing

When multiple `IRecipient<T>` implementations handle the same message type, they execute sequentially in a single try/catch block (`MessageSubscriptionWorker.cs:97-101`). If handler B throws, the entire message is marked `Failed` and retried — causing handler A (which already succeeded) to run again.

```
Message M → Handler A ✓ → Handler B ✗ → retry → Handler A (re-runs!) → Handler B (retried)
```

This causes **duplicate side effects** in handlers that already completed (sending emails twice, creating duplicate records, double-charging, etc.).

### Problem 2: No item-level progress tracking within handlers

When a handler internally iterates over a collection (e.g., syncing 100 records from an API response), failure on item 37 causes the entire message to retry from scratch. Items 1-36 are processed again.

```
Handler processes items [1..36] ✓ → item 37 ✗ → retry → items [1..36] re-processed!
```

The framework provides no mechanism for handlers to report partial progress or for the retry system to resume where processing left off.

---

## 2. Design Goals

1. **Per-handler retry isolation**: Each recipient's success/failure is tracked independently. A failed handler does not cause successful handlers to re-execute.
2. **Item-level progress tracking**: Handlers that process collections can checkpoint progress, enabling retry to resume from the failed item rather than replaying the entire collection.
3. **Backward compatibility at the simple case**: Handlers that don't need checkpointing should remain simple — just implement `IRecipient<T>` as today.
4. **No external dependencies**: Continue using RavenDB as the persistence layer.
5. **Observable**: Handler-level and item-level progress should be visible in the `SparkMessage` document for debugging.

---

## 3. Design

### 3.1 Per-handler execution tracking

#### New data model: `HandlerExecution`

Each handler invocation is tracked as a sub-record on the `SparkMessage` document:

```csharp
public class HandlerExecution
{
    /// <summary>
    /// Assembly-qualified type name of the IRecipient implementation.
    /// </summary>
    public string HandlerType { get; set; } = string.Empty;

    public EHandlerStatus Status { get; set; }

    public int AttemptCount { get; set; }

    public string? LastError { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Optional checkpoint data for handlers that support partial progress.
    /// Stored as JSON. The handler is responsible for serializing/deserializing.
    /// </summary>
    public string? Checkpoint { get; set; }
}

public enum EHandlerStatus
{
    Pending,
    Completed,
    Failed,
    DeadLettered
}
```

#### Updated `SparkMessage`

```csharp
public class SparkMessage
{
    // ... existing fields ...

    /// <summary>
    /// Per-handler execution state. Populated when the message is first picked up.
    /// </summary>
    public List<HandlerExecution> Handlers { get; set; } = new();
}
```

#### Execution flow

When processing a message:

1. **First attempt**: Discover all `IRecipient<T>` implementations via DI. Create a `HandlerExecution` entry for each one (status = `Pending`). Save to RavenDB.
2. **Execute handlers**: Iterate over `Handlers` entries. **Skip** any with `Status == Completed` or `Status == DeadLettered`. For each remaining handler:
   - Resolve the handler from DI
   - Invoke `HandleAsync`
   - On success: mark `HandlerExecution.Status = Completed`, set `CompletedAtUtc`
   - On `NonRetryableException`: mark `HandlerExecution.Status = DeadLettered`, set `LastError`
   - On retryable exception: increment `HandlerExecution.AttemptCount`, set `LastError`, mark `HandlerExecution.Status = Failed`
   - Save after each handler (not batched)
3. **Determine message status**:
   - All handlers `Completed` or `DeadLettered` → message `Completed`
   - Any handler `Failed` with `AttemptCount < MaxAttempts` → message `Failed`, schedule retry
   - Any handler `Failed` with `AttemptCount >= MaxAttempts` → mark that handler `DeadLettered`, then re-evaluate
4. **On retry**: Only handlers still in `Pending` or `Failed` status are re-executed. Completed handlers are skipped entirely.

This solves Problem 1: handler A's success is recorded and it is never re-invoked.

### 3.2 Item-level progress via checkpointing

#### New interface: `ICheckpointRecipient<TMessage>`

For handlers that process collections and want resume-from-failure semantics:

```csharp
public interface ICheckpointRecipient<in TMessage> : IRecipient<TMessage>
{
    /// <summary>
    /// Called instead of HandleAsync when a checkpoint exists from a previous attempt.
    /// The handler should resume processing from where it left off.
    /// </summary>
    Task HandleAsync(TMessage message, string checkpoint, CancellationToken cancellationToken = default);
}
```

#### Checkpoint context

Handlers need a way to save checkpoints during execution. This is provided via a scoped service:

```csharp
public interface IMessageCheckpoint
{
    /// <summary>
    /// Persists a checkpoint string for the current handler execution.
    /// Can be called multiple times — each call overwrites the previous checkpoint.
    /// The checkpoint is saved to RavenDB immediately.
    /// </summary>
    Task SaveAsync(string checkpoint, CancellationToken cancellationToken = default);
}
```

The `MessageSubscriptionWorker` registers a scoped `IMessageCheckpoint` implementation that knows which `HandlerExecution` it's associated with and writes directly to the RavenDB document.

#### Usage example

```csharp
public partial class SyncEmployeesRecipient : ICheckpointRecipient<EmployeeSyncMessage>
{
    [Inject] private readonly IMessageCheckpoint _checkpoint;
    [Inject] private readonly IEmployeeService _employeeService;

    public Task HandleAsync(EmployeeSyncMessage message, CancellationToken cancellationToken)
        => ProcessFromIndex(message, startIndex: 0, cancellationToken);

    public Task HandleAsync(EmployeeSyncMessage message, string checkpoint, CancellationToken cancellationToken)
        => ProcessFromIndex(message, startIndex: int.Parse(checkpoint), cancellationToken);

    private async Task ProcessFromIndex(EmployeeSyncMessage message, int startIndex, CancellationToken ct)
    {
        for (int i = startIndex; i < message.Employees.Count; i++)
        {
            await _employeeService.SyncAsync(message.Employees[i], ct);
            await _checkpoint.SaveAsync(i.ToString(), ct);
        }
    }
}
```

If processing fails at item 37, the checkpoint `"36"` is persisted. On retry, `HandleAsync(message, "36", ct)` is called and processing resumes from index 37.

This solves Problem 2: items already processed are skipped on retry.

#### Execution flow with checkpoints

When invoking a handler:

1. Check if the handler implements `ICheckpointRecipient<T>`
2. Check if `HandlerExecution.Checkpoint` is non-null (i.e., previous attempt saved progress)
3. If both: call the checkpoint overload `HandleAsync(message, checkpoint, ct)`
4. Otherwise: call the standard `HandleAsync(message, ct)`

### 3.3 Handler discovery changes

The current handler loop resolves `IRecipient<T>` from DI. This needs a small change:

- On **first attempt** (empty `Handlers` list): resolve all `IRecipient<T>` from DI, populate `Handlers` list with their type names, save
- On **retry** (non-empty `Handlers` list): iterate the persisted `Handlers` list. For each non-completed entry, resolve the handler type by name from DI. This ensures handler ordering is stable across retries and new handler registrations (deployed between retries) don't get picked up mid-flight.

### 3.4 Message status rollup

The message-level `Status` becomes a computed rollup:

| Handler states | Message Status |
|---|---|
| All `Completed` (+ any `DeadLettered`) | `Completed` |
| Any `Failed` (retries remaining) | `Failed` (schedule retry) |
| Mix of `Completed` + `DeadLettered` (none `Failed`/`Pending`) | `Completed` |
| All `DeadLettered` | `DeadLettered` |

The message `AttemptCount` is no longer the global retry counter — each `HandlerExecution` tracks its own `AttemptCount`. The message-level `AttemptCount` becomes the number of times the message was picked up for processing (informational).

### 3.5 Per-handler max attempts

The per-handler `AttemptCount` is compared against the message's `MaxAttempts`. When a handler exceeds this limit, it is individually dead-lettered while other handlers continue their retry cycles.

---

## 4. Migration & Breaking Changes

Since Spark is in preview, full breaking changes are acceptable.

### Breaking changes

1. **`SparkMessage` schema change**: New `Handlers` property. Existing `SparkMessage` documents in RavenDB won't have this field — they'll be treated as having an empty handler list and will be re-discovered on next processing attempt. No data migration needed.
2. **`IRecipient<T>` interface**: Unchanged. No breaking change for existing handlers.
3. **New `ICheckpointRecipient<T>` interface**: Opt-in. Existing handlers don't need to change.
4. **`IMessageCheckpoint` service**: New scoped service, available via DI in handlers. No breaking change.

### Removed fields

- `SparkMessage.LastError` — replaced by per-handler `LastError`
- `SparkMessage.CompletedAtUtc` — replaced by per-handler `CompletedAtUtc`. Can keep the message-level one as the timestamp of when the last handler completed.

---

## 5. Implementation Plan

### Phase 1: Per-handler tracking (solves Problem 1)

| # | Task | Files |
|---|------|-------|
| 1 | Add `HandlerExecution` model and `EHandlerStatus` enum | `Models/HandlerExecution.cs`, `Models/EHandlerStatus.cs` |
| 2 | Add `Handlers` property to `SparkMessage` | `Models/SparkMessage.cs` |
| 3 | Refactor `MessageSubscriptionWorker.ProcessBatchAsync` to execute handlers individually with per-handler try/catch, skip completed handlers on retry | `Services/MessageSubscriptionWorker.cs` |
| 4 | Implement message status rollup logic | `Services/MessageSubscriptionWorker.cs` |
| 5 | Update `SparkMessages_ByQueue` index if needed | `Indexes/SparkMessages_ByQueue.cs` |

### Phase 2: Checkpoint support (solves Problem 2)

| # | Task | Files |
|---|------|-------|
| 6 | Add `ICheckpointRecipient<T>` interface | `Abstractions/ICheckpointRecipient.cs` |
| 7 | Add `IMessageCheckpoint` interface | `Abstractions/IMessageCheckpoint.cs` |
| 8 | Implement `MessageCheckpoint` scoped service | `Services/MessageCheckpoint.cs` |
| 9 | Update `MessageSubscriptionWorker` to detect `ICheckpointRecipient<T>`, pass checkpoint on retry, register scoped `IMessageCheckpoint` | `Services/MessageSubscriptionWorker.cs` |
| 10 | Update source generator to also register `ICheckpointRecipient<T>` implementations | `RecipientRegistrationGenerator.cs` |

### Phase 3: Documentation & demo

| # | Task | Files |
|---|------|-------|
| 11 | Update message bus guide with per-handler retry explanation | `docs/guide-message-bus.md` |
| 12 | Add checkpoint example to demo app | `Demo/DemoApp/` |
| 13 | Update README | `MintPlayer.Spark.Messaging/README.md` |

---

## 6. Alternatives Considered

### A. Splitting messages per handler at broadcast time

Instead of one message with multiple handlers, broadcast N separate messages (one per handler). Each message targets a single handler and retries independently.

**Rejected because:**
- Breaks the current `IRecipient<T>` DI pattern — handlers don't know about each other
- Requires the broadcaster to know which handlers exist (tight coupling)
- N messages for N handlers wastes storage for the common case where all handlers succeed on the first try
- Would require a fundamentally different source generator approach

### B. Outbox pattern with separate handler dispatch table

Maintain a separate `HandlerDispatch` collection in RavenDB with one document per (message, handler) pair.

**Rejected because:**
- Introduces a second collection and join semantics
- More complex subscription queries
- The embedded `Handlers` list on `SparkMessage` is simpler and atomic (single document update)

### C. Making handlers idempotent (push the problem to users)

Document that handlers must be idempotent and let the retry-all-handlers behavior stand.

**Rejected because:**
- Idempotency is hard to guarantee for all side effects (external API calls, emails, etc.)
- Even with idempotent handlers, re-processing wastes compute
- The framework should handle this — it's the messaging system's responsibility

### D. Wrapping each handler in its own message automatically

When discovering handlers, create a synthetic message type per handler and dispatch to that.

**Rejected because:**
- Changes the observable behavior (separate RavenDB documents, separate retry schedules)
- Harder to reason about "did all handlers for this event succeed?"
- Over-engineered for the problem

---

## 7. Open Questions

1. **Should checkpoint data have a size limit?** Large checkpoints (e.g., serialized collections) could bloat the `SparkMessage` document. Consider limiting to ~64KB and documenting that checkpoints should be cursors/offsets, not data.

2. **Should there be a per-handler backoff schedule?** Currently `BackoffDelays` is global. If handler A fails on attempt 1 and handler B fails on attempt 3, the retry delay is based on which handler's attempt count? **Proposal**: use `max(handler.AttemptCount)` across all pending handlers to determine the next retry delay.

3. **Should `IMessageCheckpoint.SaveAsync` be transactional with the handler's RavenDB session?** If a handler uses its own RavenDB session for business logic, the checkpoint save is a separate write. If the handler's session commits but the checkpoint save fails, the checkpoint is lost. **Proposal**: Accept this as an edge case — checkpoints are best-effort progress markers, not transactional guarantees. The handler will redo some work but not all.

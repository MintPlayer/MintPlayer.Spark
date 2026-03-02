# Product Requirements Document: MintPlayer.Spark.SubscriptionWorker

**Version:** 1.1
**Date:** March 2, 2026
**Status:** Draft

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Problem Statement](#2-problem-statement)
3. [Goals and Objectives](#3-goals-and-objectives)
4. [Core Concepts](#4-core-concepts)
5. [Architecture Overview](#5-architecture-overview)
6. [Error Handling Strategy](#6-error-handling-strategy)
7. [Technical Design](#7-technical-design)
8. [Refactoring: Messaging](#8-refactoring-messaging)
9. [Refactoring: Replication](#9-refactoring-replication)
10. [Demo Application](#10-demo-application)
11. [Non-Functional Requirements](#11-non-functional-requirements)
12. [Implementation Plan](#12-implementation-plan)

---

## 1. Executive Summary

This document describes a **RavenDB subscription worker framework** delivered as a new independent NuGet package (`MintPlayer.Spark.SubscriptionWorker`). The package provides an abstract base class that developers subclass to create document subscription handlers with built-in retry logic, incremental backoff, proper ASP.NET Core lifecycle management via `IHostedService`, and full DI integration.

Additionally, this PRD covers how `MintPlayer.Spark.Messaging` and `MintPlayer.Spark.Replication` will be refactored to depend on the SubscriptionWorker package and use it internally for document processing.

---

## 2. Problem Statement

Applications built on RavenDB frequently need to react to document changes: recalculating computed fields, triggering side effects, processing ETL sync actions, or handling queued messages. The current approach in the Spark ecosystem has these limitations:

- **No subscription support in Spark**: RavenDB data subscriptions (server-push, guaranteed delivery, automatic batching) are not available as a framework feature
- **Messaging uses Changes API + polling**: The `MessageProcessor` in `MintPlayer.Spark.Messaging` uses a hybrid approach that is more complex and less reliable than server-managed subscriptions
- **No shared retry infrastructure**: Each consumer that needs per-document retry tracking must implement its own logic
- **CronosCore's implementation uses anti-patterns**: Static `SyncWorker` orchestrator, `ServiceLocator`, `async void`, no DI integration

---

## 3. Goals and Objectives

| Goal | Success Metric |
|------|----------------|
| Generic subscription worker base class | Single abstract method to override for batch processing |
| Built-in retry logic with incremental backoff | Per-document retry tracking with configurable attempts and delays |
| Proper ASP.NET Core lifecycle | Workers run as `BackgroundService` with graceful shutdown |
| Full DI integration | Workers receive dependencies via constructor injection |
| Source generator discovery | Auto-generated `AddSparkSubscriptionWorkers()` extension method |
| Independent NuGet package | No dependency on core `MintPlayer.Spark` or `MintPlayer.Spark.Messaging` (only RavenDB.Client + Hosting) |
| Messaging refactoring | `MessageProcessor` replaced by per-queue subscription workers internally |
| Replication refactoring | Sync action processing replaced by a subscription worker internally |

---

## 4. Core Concepts

### Subscription Worker

An abstract base class that developers subclass. Each worker connects to a named RavenDB data subscription and processes document batches. The worker manages its own lifecycle as a `BackgroundService`.

```csharp
public class CarRefreshWorker : SparkSubscriptionWorker<Car>
{
    public CarRefreshWorker(IDocumentStore store, ILogger<CarRefreshWorker> logger)
        : base(store, logger) { }

    protected override SubscriptionCreationOptions<Car> ConfigureSubscription()
        => new() { Filter = car => true };

    protected override async Task ProcessBatchAsync(
        SubscriptionBatch<Car> batch, CancellationToken ct)
    {
        using var session = batch.OpenAsyncSession();
        foreach (var item in batch.Items)
        {
            // Process each car...
        }
        await session.SaveChangesAsync(ct);
    }
}
```

### Retry Numerator

A helper that tracks per-document retry attempts using RavenDB counters and the `@refresh` metadata mechanism. When a document fails processing, it is scheduled for redelivery after an incremental delay. After exhausting max attempts, the document is flagged for manual review.

---

## 5. Architecture Overview

### Dependency Direction

```
MintPlayer.Spark.SubscriptionWorker          (NEW - base package, zero Spark deps)
    ^                    ^
    |                    |
    | depends on         | depends on
    |                    |
MintPlayer.Spark.Messaging         MintPlayer.Spark.Replication
(uses SubscriptionWorker           (uses SubscriptionWorker
 internally for per-queue           internally for sync action
 message processing)                processing)
```

The SubscriptionWorker package has **no knowledge** of messaging concepts (`IRecipient<T>`, queues, `SparkMessage`) or replication concepts (`SyncAction`, `[Replicated]`). It only provides the generic `SparkSubscriptionWorker<T>` base class. The Messaging and Replication libraries create their own internal workers that extend this base class.

### Runtime Flow

```
Application Startup
    |
    |-- AddSparkSubscriptionWorkers()     (source-generated, registers app-level workers)
    |-- AddSparkSubscriptions()           (registers infrastructure)
    |-- AddSparkMessaging()               (internally creates per-queue subscription workers)
    |-- AddSparkReplication()             (internally creates sync action subscription worker)
    |
    v
BackgroundService lifecycle starts all workers:
    |
    +---> CarRefreshWorker (app-level, SparkSubscriptionWorker<Car>)
    |       |-> Developer-defined batch processing
    |
    +---> [internal] MessageWorker for queue "PersonEvents"
    |       |-> SparkSubscriptionWorker<SparkMessage>, BatchSize = 1
    |       |-> Subscription query: QueueName = 'PersonEvents' and Status = 'Pending'
    |       |-> Dispatches to IRecipient<T> handlers
    |       |-> FIFO guaranteed (one message at a time per queue)
    |
    +---> [internal] MessageWorker for queue "BuildValidation"
    |       |-> Same pattern, independent subscription
    |       |-> Parallel to other queues
    |
    +---> [internal] SyncActionSubscriptionWorker
            |-> SparkSubscriptionWorker<SparkSyncAction>
            |-> Processes replication sync actions
```

---

## 6. Error Handling Strategy

### Two Approaches Considered

**Approach A: All-or-nothing batch processing**
- Iterate all items in the batch. Only call `SaveChangesAsync()` when all items succeed.
- On any failure, throw from the batch handler. RavenDB does not acknowledge the batch, so the entire batch is redelivered.
- Simple, but a single failing document blocks the entire batch repeatedly.

**Approach B: Per-item processing with status tracking**
- Iterate all items. Successfully processed items get a status update (e.g., via `@refresh` metadata removal or a status field).
- The subscription query filters out already-processed items, so they won't appear in the next batch.
- Failed items remain in the subscription and are redelivered.

### Recommended: Hybrid approach (B with safety net)

The recommended approach for **general-purpose workers** (custom app workers, replication) combines per-item tracking with batch-level safety:

1. **Per-item try/catch**: Each item in the batch is processed individually within a try/catch block
2. **Success tracking**: Successfully processed items are marked (via `@refresh` metadata scheduling or a status field update) so the subscription query excludes them on redelivery
3. **Failure tracking**: Failed items get their retry counter incremented via `RetryNumerator`. The numerator uses RavenDB counters to persist the attempt count and schedules a `@refresh` to redeliver the document after an incremental delay
4. **Batch-level SaveChanges**: `SaveChangesAsync()` is called once at the end, persisting both success markers and retry counters in a single round-trip
5. **Max attempts**: After exhausting retries (default: 5), the document is flagged as permanently failed (counter deleted, long refresh delay set) and optionally triggers a notification callback

**Why this approach?**
- A single failing document does not block the entire batch
- Successfully processed items are not reprocessed
- Retry state survives application restarts (RavenDB counters are durable)
- The subscription query naturally filters processed items via `@refresh`
- Batch-level `SaveChanges` keeps the round-trip count low

### Messaging: Approach A with BatchSize = 1

For the **Messaging library's internal workers**, a simpler model is used:

- **One subscription per queue** with `MaxDocsPerBatch = 1`
- Each batch contains exactly one message
- On success: acknowledge (batch handler returns normally), message status → `Completed`
- On failure: throw from the batch handler. RavenDB does not acknowledge, so the same message is redelivered after the retry delay. Message status → `Failed` with `NextAttemptAtUtc` set.
- **FIFO guaranteed**: only one message at a time per queue; a failed message blocks its queue until resolved or dead-lettered
- **Queue isolation**: each queue has its own independent subscription, so one queue's failure does not affect other queues

### Connection-Level Error Handling

Separate from per-item/message retry logic, the subscription worker handles connection-level errors with categorized exception handling:

| Exception | Category | Action |
|-----------|----------|--------|
| `OperationCanceledException` | Cancellation | Stop gracefully (application shutting down) |
| `SubscriptionInUseException` | Recoverable | Wait `RetryDelay * 2`, reconnect (another node holds the subscription) |
| `SubscriberErrorException` (general) | Recoverable | Wait `RetryDelay`, reconnect |
| `SubscriptionClosedException` | Non-recoverable | Log error, stop worker |
| `DatabaseDoesNotExistException` | Non-recoverable | Log error, stop worker |
| `SubscriptionDoesNotExistException` | Non-recoverable | Log error, stop worker |
| `SubscriptionInvalidStateException` | Non-recoverable | Log error, stop worker |
| `AuthorizationException` | Non-recoverable | Log error, stop worker |
| Other exceptions | Conditional | If `KeepRunning`: wait `RetryDelay`, reconnect. Otherwise: stop |

### Retry Defaults

| Setting | Default | Description |
|---------|---------|-------------|
| `MaxRetryAttempts` | 5 | Per-document retry attempts before flagging as failed |
| `RetryDelay` | 30 seconds | Base connection retry delay |
| `MaxDownTime` | 5 minutes | Max erroneous period before giving up on connection |
| `MaxDocsPerBatch` | 256 | Maximum documents per subscription batch |
| `KeepRunning` | true | Whether to reconnect after normal completion |
| `RetryCounterName` | "SparkRetryAttempts" | RavenDB counter name for tracking attempts |

**Per-document backoff schedule** (incremental):

| Attempt | Delay before redelivery |
|---------|------------------------|
| 1 | 30 seconds |
| 2 | 1 minute |
| 3 | 2 minutes |
| 4 | 5 minutes |
| 5 | 10 minutes |
| Final (exhausted) | 1 day (effectively parked) |

---

## 7. Technical Design

### 7.1 Project Layout

```
MintPlayer.Spark.sln
├── MintPlayer.Spark                              (unchanged)
├── MintPlayer.Spark.Abstractions                 (unchanged)
├── MintPlayer.Spark.SubscriptionWorker           (NEW - subscription worker framework)
├── MintPlayer.Spark.Messaging.Abstractions       (unchanged)
├── MintPlayer.Spark.Messaging                    (MODIFIED - depends on SubscriptionWorker)
├── MintPlayer.Spark.Replication.Abstractions     (unchanged)
├── MintPlayer.Spark.Replication                  (MODIFIED - depends on SubscriptionWorker)
├── MintPlayer.Spark.SourceGenerators             (EXTENDED - subscription worker discovery)
└── Demo/...                                      (EXTENDED - sample subscription workers)
```

| Project | Contents | Dependencies |
|---------|----------|-------------|
| `MintPlayer.Spark.SubscriptionWorker` (NEW) | `SparkSubscriptionWorker<T>`, `RetryNumerator`, `SparkSubscriptionOptions`, extension methods | `RavenDB.Client 7.1.5`, `Microsoft.Extensions.Hosting` |
| `MintPlayer.Spark.Messaging` (MODIFIED) | Internal per-queue `MessageSubscriptionWorker` replaces `MessageProcessor` | Adds reference to `MintPlayer.Spark.SubscriptionWorker` |
| `MintPlayer.Spark.Replication` (MODIFIED) | Internal `SyncActionSubscriptionWorker` for processing sync actions | Adds reference to `MintPlayer.Spark.SubscriptionWorker` |
| `MintPlayer.Spark.SourceGenerators` (EXTENDED) | `SubscriptionWorkerRegistrationGenerator` | (existing dependencies) |

**Key design principle:** `MintPlayer.Spark.SubscriptionWorker` has **zero dependency** on `MintPlayer.Spark`, `MintPlayer.Spark.Abstractions`, or `MintPlayer.Spark.Messaging`. It only depends on `RavenDB.Client` and `Microsoft.Extensions.Hosting`. This means it can be used independently of the Spark CRUD framework — same pattern as the Messaging package.

### 7.2 SparkSubscriptionWorker\<T\> (Abstract Base Class)

```csharp
namespace MintPlayer.Spark.SubscriptionWorker;

public abstract class SparkSubscriptionWorker<T> : BackgroundService where T : class
{
    protected IDocumentStore DocumentStore { get; }
    protected ILogger Logger { get; }

    // Configuration (overridable via property override)
    protected virtual string SubscriptionName
        => GetType().Name.Replace("SubscriptionWorker", "").Replace("Worker", "");
    protected virtual string? Database => null;
    protected virtual bool KeepRunning => true;
    protected virtual TimeSpan RetryDelay => TimeSpan.FromSeconds(30);
    protected virtual TimeSpan MaxDownTime => TimeSpan.FromMinutes(5);
    protected virtual int MaxDocsPerBatch => 256;

    protected SparkSubscriptionWorker(IDocumentStore store, ILogger logger);

    // Abstract: configure the subscription query/filter
    protected abstract SubscriptionCreationOptions<T> ConfigureSubscription();

    // Abstract: process a batch of documents
    protected abstract Task ProcessBatchAsync(
        SubscriptionBatch<T> batch, CancellationToken cancellationToken);

    // Virtual hooks for lifecycle events
    protected virtual Task OnWorkerStartedAsync() => Task.CompletedTask;
    protected virtual Task OnWorkerStoppedAsync() => Task.CompletedTask;
    protected virtual Task OnBatchCompletedAsync(int itemCount) => Task.CompletedTask;
    protected virtual Task OnNonRecoverableErrorAsync(Exception exception) => Task.CompletedTask;

    // BackgroundService implementation
    protected sealed override Task ExecuteAsync(CancellationToken stoppingToken);
}
```

### 7.3 ExecuteAsync Implementation (Connection Loop)

```
ExecuteAsync(stoppingToken):
    1. EnsureSubscriptionExistsAsync()
       - Calls ConfigureSubscription() to get options
       - Uses store.Subscriptions.UpdateAsync() with CreateNew = true (idempotent create-or-update)
    2. OnWorkerStartedAsync()
    3. while (!stoppingToken.IsCancellationRequested):
        a. Create SubscriptionWorkerOptions with Name, MaxDocsPerBatch,
           MaxErroneousPeriod, TimeToWaitBeforeConnectionRetry
        b. Get RavenDB subscription worker via store.Subscriptions.GetSubscriptionWorker<T>(options)
        c. try:
             await subscriptionWorker.Run(batch => ProcessBatchAsync(batch, stoppingToken))
             // Normal completion
             if KeepRunning:
                 await Task.Delay(RetryDelay, stoppingToken)
                 continue
             else:
                 break
           catch (categorized exceptions → see Section 6)
    4. OnWorkerStoppedAsync()
```

### 7.4 RetryNumerator

A utility class for per-document retry tracking. Used inside `ProcessBatchAsync` implementations.

```csharp
namespace MintPlayer.Spark.SubscriptionWorker;

public class RetryNumerator
{
    public int MaxAttempts { get; set; } = 5;
    public string CounterName { get; set; } = "SparkRetryAttempts";
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    // Returns true if the document should be retried, false if max attempts exhausted.
    // Increments the counter, schedules @refresh for redelivery.
    public async Task<bool> TrackRetryAsync(
        IAsyncDocumentSession session,
        object entity,
        Exception exception,
        ILogger? logger = null);

    // Clears retry counter after successful processing (optional, for explicit cleanup).
    public async Task ClearRetryAsync(
        IAsyncDocumentSession session,
        object entity);

    // Computes delay for given attempt number.
    public TimeSpan GetDelay(int attempt);
}
```

**Delay computation**: `BaseDelay * attempt` (linear incremental):
- Attempt 1: 30s
- Attempt 2: 60s
- Attempt 3: 90s
- Attempt 4: 120s
- Attempt 5: 150s
- Exhausted: 1 day

**`TrackRetryAsync` implementation**:
1. `session.CountersFor(entity).Increment(CounterName, 1)`
2. Get current count
3. If count < MaxAttempts: schedule `session.Advanced.GetMetadataFor(entity)["@refresh"] = DateTime.UtcNow + GetDelay(count)`
4. If count >= MaxAttempts: delete counter, set `@refresh` to `DateTime.UtcNow + TimeSpan.FromDays(1)`, return false
5. Return true/false

### 7.5 SparkSubscriptionOptions

```csharp
namespace MintPlayer.Spark.SubscriptionWorker;

public class SparkSubscriptionOptions
{
    /// <summary>
    /// Whether to wait for non-stale indexes before starting workers.
    /// </summary>
    public bool WaitForNonStaleIndexes { get; set; } = true;

    /// <summary>
    /// Timeout for waiting for non-stale indexes.
    /// </summary>
    public TimeSpan NonStaleIndexTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
```

### 7.6 Extension Methods

```csharp
namespace MintPlayer.Spark.SubscriptionWorker;

public static class SparkSubscriptionExtensions
{
    // Register the subscription infrastructure
    public static IServiceCollection AddSparkSubscriptions(
        this IServiceCollection services,
        Action<SparkSubscriptionOptions>? configure = null);

    // Register a single subscription worker as a hosted service
    public static IServiceCollection AddSubscriptionWorker<TWorker>(
        this IServiceCollection services)
        where TWorker : class, IHostedService;
}
```

### 7.7 Source Generator

A new `SubscriptionWorkerRegistrationGenerator` discovers all non-abstract classes extending `SparkSubscriptionWorker<T>` **in the consuming project** and generates:

```csharp
// <auto-generated />
namespace DemoApp;

internal static class SparkSubscriptionWorkersExtensions
{
    internal static IServiceCollection AddSparkSubscriptionWorkers(
        this IServiceCollection services)
    {
        global::MintPlayer.Spark.SubscriptionWorker.SparkSubscriptionExtensions
            .AddSubscriptionWorker<global::DemoApp.Subscriptions.CarRefreshWorker>(services);
        // ... one line per discovered worker
        return services;
    }
}
```

**Note**: This generator only discovers **application-level** subscription workers. The internal workers created by the Messaging and Replication libraries are registered by their own `AddSparkMessaging()` / `AddSparkReplication()` extension methods and are not discovered by this generator.

### 7.8 Revision Support

For workers that need change detection (comparing previous vs. current document state), extend with `Revision<T>`:

```csharp
public class CompanyChangeWorker : SparkSubscriptionWorker<Revision<Company>>
{
    protected override SubscriptionCreationOptions<Revision<Company>> ConfigureSubscription()
        => new() { Name = "CompanyRevisions" };

    protected override async Task ProcessBatchAsync(
        SubscriptionBatch<Revision<Company>> batch, CancellationToken ct)
    {
        using var session = batch.OpenAsyncSession();
        foreach (var item in batch.Items)
        {
            var previous = item.Result.Previous;
            var current = item.Result.Current;
            // React to changes...
        }
        await session.SaveChangesAsync(ct);
    }
}
```

---

## 8. Refactoring: Messaging

### 8.1 FIFO Ordering Challenge

The Messaging library requires **per-queue FIFO ordering**: messages within a queue must be processed in the order they were broadcast. A single RavenDB subscription across all queues cannot guarantee this because:

- If message M1 (queue A) fails and is retried via `@refresh`, message M2 (queue A, newer) could be delivered and processed before M1 retries — breaking FIFO
- Throwing from the batch handler to NACK the entire batch would block ALL queues, not just the failing one

### 8.2 Solution: One Subscription Per Queue, BatchSize = 1

The Messaging library creates **one subscription worker per queue** internally:

- **One subscription per queue**: `from SparkMessages where QueueName = '{queueName}' and Status = 'Pending' and (NextAttemptAtUtc = null or NextAttemptAtUtc <= now())`
- **`MaxDocsPerBatch = 1`**: Each batch contains exactly one message
- **On success**: Message status → `Completed`, batch acknowledged
- **On failure**: Message status → `Failed` with `NextAttemptAtUtc` set per backoff schedule. Throw from handler so batch is NACK'd and message is redelivered after delay.
- **FIFO guaranteed**: One message at a time per queue; a failed message blocks its queue until resolved or dead-lettered
- **Queue isolation**: Each queue has its own independent subscription, so failure in one queue does not affect others

### 8.3 Queue Discovery at Runtime

Queue names are determined by the `[MessageQueue]` attribute on message classes, which can reside in referenced libraries (e.g., `DemoApp.Library`). Queue discovery happens at **runtime** from the `RecipientRegistry`:

1. `AddSparkRecipients()` (source-generated) populates the `RecipientRegistry` with all `(MessageType, RecipientType)` mappings
2. During `AddSparkMessaging()` or at hosted service startup, the Messaging library iterates all registered message types in the `RecipientRegistry`
3. For each message type, it reads the `[MessageQueue]` attribute (or falls back to the type name) to determine the queue name
4. For each distinct queue name, it creates and registers an internal `MessageSubscriptionWorker` instance (extending `SparkSubscriptionWorker<SparkMessage>`) with the appropriate subscription query

This approach works regardless of which assembly the message class is defined in, since the `RecipientRegistry` already holds the resolved `Type` objects.

### 8.4 Changes

1. Add project reference from `MintPlayer.Spark.Messaging` to `MintPlayer.Spark.SubscriptionWorker`
2. Create internal `MessageSubscriptionWorker : SparkSubscriptionWorker<SparkMessage>`
   - Constructor receives the queue name to subscribe to
   - `ConfigureSubscription()` returns a query filtering by `QueueName`, `Status`, and `NextAttemptAtUtc`
   - `MaxDocsPerBatch` overridden to `1`
   - `ProcessBatchAsync`: Deserializes the single message, creates DI scope, resolves and invokes `IRecipient<T>` handlers, updates message status
3. Create internal `MessageSubscriptionManager` that discovers queues from `RecipientRegistry` and registers one `MessageSubscriptionWorker` per queue
4. Remove `MessageProcessor` class (the Changes API + polling BackgroundService)
5. Update `AddSparkMessaging()` to register `MessageSubscriptionManager` instead of `MessageProcessor`

### 8.5 What Stays the Same

- `IMessageBus` interface and `MessageBus` implementation (still stores `SparkMessage` documents)
- `SparkMessage` document structure and `EMessageStatus` enum
- `RecipientRegistry` and recipient dispatch logic
- `IRecipient<T>` interface and source-generated registration
- `SparkMessages_ByQueue` index
- Retry semantics: 5 attempts with exponential backoff, dead-lettering after max attempts
- `NonRetryableException` for immediate dead-lettering
- `DelayBroadcastAsync` with `NextAttemptAtUtc` support

### 8.6 Benefit

Eliminates the Changes API subscription management code, fallback polling timer, and the complex signal/wait logic in `MessageProcessor`. The RavenDB subscriptions provide guaranteed delivery, server-managed push semantics, and built-in reconnection handling — all of which are now delegated to the `SparkSubscriptionWorker<T>` base class.

---

## 9. Refactoring: Replication

### 9.1 Current State

The Replication library intercepts write operations on `[Replicated]` entities via `SyncActionInterceptor`, serializes changes to `SyncAction` objects, broadcasts them as `SyncActionDeploymentMessage` via the Messaging library's message bus, and the `SyncActionDeploymentRecipient` POSTs them to the owner module's `/spark/sync/apply` endpoint.

### 9.2 New State

Instead of routing through the message bus, sync actions are stored directly as `SparkSyncAction` documents in RavenDB, and an internal `SyncActionSubscriptionWorker` picks them up for processing.

### 9.3 Changes

1. Add project reference from `MintPlayer.Spark.Replication` to `MintPlayer.Spark.SubscriptionWorker`
2. Create `SparkSyncAction` document model (if not already existing as a standalone document):
   - Fields: Id, Collection, ActionType (Create/Update/Delete), EntityId, PayloadJson, TargetModule, Status, CreatedAtUtc
3. Modify `SyncActionInterceptor` to store `SparkSyncAction` documents directly (instead of broadcasting via message bus)
4. Create internal `SyncActionSubscriptionWorker : SparkSubscriptionWorker<SparkSyncAction>`
   - Subscription query: `from SparkSyncActions where Status = 'Pending'`
   - `ProcessBatchAsync`: Groups actions by target module, POSTs to each module's `/spark/sync/apply` endpoint
   - Uses `RetryNumerator` for per-action retry tracking
5. Remove `SyncActionDeploymentMessage` and `SyncActionDeploymentRecipient`
6. Update `AddSparkReplication()` to register `SyncActionSubscriptionWorker`

### 9.4 What Stays the Same

- `[Replicated]` attribute and ETL script collection
- Module registration (`ModuleRegistrationService`)
- ETL task deployment flow
- `/spark/sync/apply` endpoint on the owner module
- `ISyncActionInterceptor` interface (implementation changes)

### 9.5 Benefit

Removes the dependency on the Messaging library for sync action processing. Sync actions get dedicated retry tracking via `RetryNumerator` instead of relying on the message bus's generic retry logic. This also enables the Replication library to work without requiring the full Messaging infrastructure — the only shared dependency is the SubscriptionWorker base package.

---

## 10. Demo Application

### 10.1 Sample Subscription Worker (DemoApp)

```csharp
public class PersonAuditWorker : SparkSubscriptionWorker<Person>
{
    private readonly RetryNumerator _retryNumerator = new();

    public PersonAuditWorker(IDocumentStore store, ILogger<PersonAuditWorker> logger)
        : base(store, logger) { }

    protected override SubscriptionCreationOptions<Person> ConfigureSubscription()
        => new()
        {
            Name = "PersonAudit",
            Filter = person => true
        };

    protected override int MaxDocsPerBatch => 100;

    protected override async Task ProcessBatchAsync(
        SubscriptionBatch<Person> batch, CancellationToken ct)
    {
        using var session = batch.OpenAsyncSession();
        foreach (var item in batch.Items)
        {
            try
            {
                var person = item.Result;
                // Audit logic: log changes, update computed fields, etc.
                Logger.LogInformation("Processing person {Id}: {Name}",
                    item.Id, $"{person.FirstName} {person.LastName}");
            }
            catch (Exception ex)
            {
                var shouldRetry = await _retryNumerator.TrackRetryAsync(
                    session, item.Result, ex, Logger);
                if (!shouldRetry)
                {
                    Logger.LogError(ex, "Permanently failed processing person {Id}", item.Id);
                }
            }
        }
        await session.SaveChangesAsync(ct);
    }
}
```

### 10.2 Program.cs Registration

```csharp
// Existing Spark setup
builder.Services.AddSpark(builder.Configuration);
builder.Services.AddScoped<SparkContext, DemoSparkContext>();
builder.Services.AddSparkActions();

// Messaging (internally creates per-queue subscription workers)
builder.Services.AddSparkMessaging();
builder.Services.AddSparkRecipients();

// App-level subscription workers (source-generated discovers PersonAuditWorker etc.)
builder.Services.AddSparkSubscriptions();
builder.Services.AddSparkSubscriptionWorkers();
```

---

## 11. Non-Functional Requirements

| Requirement | Target |
|-------------|--------|
| Delivery guarantee | At-least-once (handlers must be idempotent) |
| Processing model | Server-push via RavenDB subscriptions (no polling) |
| Retry durability | Per-document retry state survives application restarts (RavenDB counters) |
| Lifecycle management | Proper `BackgroundService` with graceful shutdown via `CancellationToken` |
| DI integration | Full constructor injection, no `ServiceLocator` or static state |
| Scalability | One active consumer per subscription (RavenDB constraint); HA via competing consumers with `SubscriptionInUseException` handling |
| Observability | Structured logging via `ILogger`, retry counters visible in RavenDB Studio |
| Independence | No dependency on core Spark — usable standalone with any RavenDB application |

---

## 12. Implementation Plan

### Phase 1: SubscriptionWorker Package (NEW)

1. Create `MintPlayer.Spark.SubscriptionWorker` class library project (net10.0)
   - Dependencies: `RavenDB.Client 7.1.5`, `Microsoft.Extensions.Hosting`
   - NuGet metadata: PackageId, Version (10.0.0-preview.10), Description, Tags
2. Implement `SparkSubscriptionWorker<T>` abstract base class
   - `ExecuteAsync` with connection loop and categorized exception handling
   - Virtual configuration properties (SubscriptionName, Database, KeepRunning, RetryDelay, MaxDownTime, MaxDocsPerBatch)
   - Subscription creation via `ConfigureSubscription()` abstract method
   - Lifecycle hooks (OnWorkerStarted, OnWorkerStopped, OnBatchCompleted, OnNonRecoverableError)
3. Implement `RetryNumerator` utility class
   - RavenDB counter-based attempt tracking
   - `@refresh` metadata scheduling for redelivery
   - Configurable MaxAttempts, CounterName, BaseDelay
4. Implement `SparkSubscriptionOptions` configuration class
5. Implement `SparkSubscriptionExtensions` with `AddSparkSubscriptions()` and `AddSubscriptionWorker<T>()`
6. Add project to solution file

### Phase 2: Source Generator

7. Add `SubscriptionWorkerClassInfo` model to `MintPlayer.Spark.SourceGenerators`
8. Add `SubscriptionWorkerRegistrationGenerator` (discovers classes extending `SparkSubscriptionWorker<T>`)
9. Add `SubscriptionWorkerRegistrationProducer` (generates `AddSparkSubscriptionWorkers()`)

### Phase 3: Messaging Refactoring

10. Add project reference from `MintPlayer.Spark.Messaging` to `MintPlayer.Spark.SubscriptionWorker`
11. Create internal `MessageSubscriptionWorker : SparkSubscriptionWorker<SparkMessage>`
    - Per-queue subscription query with `MaxDocsPerBatch = 1`
    - Single-message dispatch to `IRecipient<T>` handlers via `RecipientRegistry`
12. Create internal `MessageSubscriptionManager` for runtime queue discovery from `RecipientRegistry`
13. Remove `MessageProcessor` class
14. Update `AddSparkMessaging()` registration

### Phase 4: Replication Refactoring

15. Add project reference from `MintPlayer.Spark.Replication` to `MintPlayer.Spark.SubscriptionWorker`
16. Create `SparkSyncAction` document model (if needed)
17. Create internal `SyncActionSubscriptionWorker : SparkSubscriptionWorker<SparkSyncAction>`
18. Modify `SyncActionInterceptor` to store documents directly
19. Remove message-bus-based sync action flow (`SyncActionDeploymentMessage`, `SyncActionDeploymentRecipient`)
20. Update `AddSparkReplication()` registration

### Phase 5: Demo Application

21. Add sample `PersonAuditWorker` to DemoApp
22. Update DemoApp `Program.cs` with `AddSparkSubscriptions()` and `AddSparkSubscriptionWorkers()`
23. Verify end-to-end: create/update person → subscription delivers → worker processes

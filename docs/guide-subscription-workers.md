# Subscription Workers

The `MintPlayer.Spark.SubscriptionWorker` package provides a framework for building RavenDB subscription workers with built-in retry logic, categorized exception handling, and ASP.NET Core lifecycle management. It runs independently of the core Spark CRUD framework -- any project with a RavenDB `IDocumentStore` can use it.

## Installation

```bash
dotnet add package MintPlayer.Spark.SubscriptionWorker
```

If you also use the Spark source generators (for auto-registration), ensure the `MintPlayer.Spark.SourceGenerators` package is referenced.

## Overview

A subscription worker continuously listens for document changes in RavenDB via the [Data Subscriptions](https://ravendb.net/docs/article-page/latest/csharp/client-api/data-subscriptions/what-are-data-subscriptions) mechanism. When documents match the subscription's RQL query, RavenDB delivers them in batches to the worker for processing.

`SparkSubscriptionWorker<T>` wraps this into an ASP.NET Core `BackgroundService` with:

- Automatic subscription creation/update on startup
- A connection loop that reconnects after errors or normal completion
- Categorized exception handling (retryable vs. fatal)
- Per-document retry tracking via `RetryNumerator`
- Lifecycle hooks for startup, shutdown, and batch completion

## Step 1: Create a Subscription Worker

Extend `SparkSubscriptionWorker<T>` and implement two abstract methods:

- `ConfigureSubscription()` -- returns the RQL query that filters which documents are delivered
- `ProcessBatchAsync()` -- handles each batch of documents

```csharp
using MintPlayer.Spark.SubscriptionWorker;
using Raven.Client.Documents;
using Raven.Client.Documents.Subscriptions;

public class OrderProcessingWorker : SparkSubscriptionWorker<Order>
{
    private readonly RetryNumerator _retryNumerator = new();

    public OrderProcessingWorker(IDocumentStore store, ILogger<OrderProcessingWorker> logger)
        : base(store, logger) { }

    protected override SubscriptionCreationOptions ConfigureSubscription()
        => new() { Query = "from Orders where Status = 'Pending'" };

    protected override async Task ProcessBatchAsync(
        SubscriptionBatch<Order> batch, CancellationToken cancellationToken)
    {
        using var session = batch.OpenAsyncSession();

        foreach (var item in batch.Items)
        {
            try
            {
                var order = item.Result;
                // Process the order...
                order.Status = "Processed";
                await _retryNumerator.ClearRetryAsync(session, order);
            }
            catch (Exception ex)
            {
                var willRetry = await _retryNumerator.TrackRetryAsync(
                    session, item.Result, ex, Logger);
                if (!willRetry)
                {
                    Logger.LogError(ex, "Permanently failed processing order {Id}", item.Id);
                }
            }
        }

        await session.SaveChangesAsync(cancellationToken);
    }
}
```

### Subscription Naming

By default, the subscription name in RavenDB is derived from the class name by stripping common suffixes:

- `OrderProcessingWorker` becomes `"OrderProcessing"`
- `OrderProcessingSubscriptionWorker` becomes `"OrderProcessing"`

Override `SubscriptionName` to set a custom name:

```csharp
protected override string SubscriptionName => "MyCustomSubscription";
```

## Step 2: Register the Worker

### Option A: Source-Generated Registration (Recommended)

If your project references `MintPlayer.Spark.SourceGenerators`, a source generator discovers all `SparkSubscriptionWorker<T>` subclasses in your project and generates an `AddSparkSubscriptionWorkers()` extension method:

```csharp
// Program.cs
builder.Services.AddSparkSubscriptions();
builder.Services.AddSparkSubscriptionWorkers(); // source-generated
```

The generated code calls `AddSubscriptionWorker<T>()` for each worker class found.

### Option B: Manual Registration

Register workers individually:

```csharp
builder.Services.AddSparkSubscriptions();
builder.Services.AddSubscriptionWorker<OrderProcessingWorker>();
```

## Configuration

Override virtual properties on your worker class to tune behavior:

```csharp
public class OrderProcessingWorker : SparkSubscriptionWorker<Order>
{
    // Target database (default: null = store default)
    protected override string? Database => null;

    // Max documents per batch (default: 256)
    protected override int MaxDocsPerBatch => 100;

    // Whether to reconnect after normal subscription completion (default: true)
    protected override bool KeepRunning => true;

    // Wait time before connection retry (default: 30 seconds)
    protected override TimeSpan RetryDelay => TimeSpan.FromSeconds(30);

    // Max erroneous period before giving up on connection (default: 5 minutes)
    protected override TimeSpan MaxDownTime => TimeSpan.FromMinutes(5);
}
```

### Global Options

`AddSparkSubscriptions()` accepts an optional configuration callback:

```csharp
builder.Services.AddSparkSubscriptions(options =>
{
    options.WaitForNonStaleIndexes = true;           // default: true
    options.NonStaleIndexTimeout = TimeSpan.FromMinutes(2); // default: 2 minutes
});
```

## Subscription Lifecycle

Each worker follows this lifecycle as a `BackgroundService`:

1. **Startup**: `EnsureSubscriptionExistsAsync` creates or updates the RavenDB subscription (idempotent -- if it already exists, the query is updated).
2. **`OnWorkerStartedAsync()`**: Lifecycle hook called after the subscription is ready.
3. **Connection loop**: Opens a subscription worker connection and starts receiving batches.
4. **Batch processing**: Calls `ProcessBatchAsync()` for each batch, then `OnBatchCompletedAsync(itemCount)`.
5. **Error recovery**: Catches and categorizes exceptions (see table below).
6. **Shutdown**: Triggered by `CancellationToken` cancellation (e.g., app shutdown). Calls `OnWorkerStoppedAsync()`.

## Categorized Exception Handling

The connection loop classifies exceptions into three categories:

### Retryable Errors

These errors cause the worker to wait and then reconnect:

| Exception | Wait Time | Description |
|---|---|---|
| `SubscriptionInUseException` | `RetryDelay * 2` | Another node holds the subscription |
| `SubscriberErrorException` | `RetryDelay` | Error in the subscriber callback |
| Other unexpected exceptions (when `KeepRunning = true`) | `RetryDelay` | Transient errors |

### Non-Recoverable Errors

These errors cause the worker to stop permanently and call `OnNonRecoverableErrorAsync()`:

| Exception | Description |
|---|---|
| `SubscriptionClosedException` | The subscription was deleted or disabled |
| `DatabaseDoesNotExistException` | The target database does not exist |
| `SubscriptionDoesNotExistException` | The subscription was removed |
| `SubscriptionInvalidStateException` | The subscription is in an invalid state |
| `AuthorizationException` | Authentication/authorization failure |
| Other unexpected exceptions (when `KeepRunning = false`) | Any error when auto-reconnect is disabled |

### Cancellation

`OperationCanceledException` when the `CancellationToken` is cancelled triggers a graceful shutdown.

## Lifecycle Hooks

Override these virtual methods to react to worker events:

```csharp
// Called after startup, before the first batch
protected override Task OnWorkerStartedAsync() => Task.CompletedTask;

// Called when the worker stops (graceful or error)
protected override Task OnWorkerStoppedAsync() => Task.CompletedTask;

// Called after each batch is successfully processed
protected override Task OnBatchCompletedAsync(int itemCount) => Task.CompletedTask;

// Called when a non-recoverable error occurs (before stopping)
protected override Task OnNonRecoverableErrorAsync(Exception exception) => Task.CompletedTask;
```

## RetryNumerator: Per-Document Retry Tracking

`RetryNumerator` tracks failed processing attempts for individual documents using RavenDB counters and the `@refresh` metadata mechanism.

### How It Works

1. When `TrackRetryAsync()` is called for a failed document, it increments a RavenDB counter on the document.
2. It sets the `@refresh` metadata to a future timestamp, which causes RavenDB to redeliver the document to the subscription at that time.
3. If the maximum number of attempts is exhausted, the counter is cleared and the document is "parked" for a longer delay (default: 1 day).

### Configuration

```csharp
var retryNumerator = new RetryNumerator
{
    MaxAttempts = 5,                              // default: 5
    BaseDelay = TimeSpan.FromSeconds(30),          // default: 30s
    CounterName = "SparkRetryAttempts",             // default
    ExhaustedDelay = TimeSpan.FromDays(1),          // default: 1 day
};
```

### Backoff Schedule

`RetryNumerator` uses linear incremental backoff (`BaseDelay * attempt`):

| Attempt | Delay |
|---|---|
| 1 | 30 seconds |
| 2 | 60 seconds |
| 3 | 90 seconds |
| 4 | 120 seconds |
| 5 | 150 seconds |
| Exhausted | 1 day (parked) |

### Usage in ProcessBatchAsync

```csharp
protected override async Task ProcessBatchAsync(
    SubscriptionBatch<Order> batch, CancellationToken cancellationToken)
{
    using var session = batch.OpenAsyncSession();

    foreach (var item in batch.Items)
    {
        try
        {
            // Process the document...
            await _retryNumerator.ClearRetryAsync(session, item.Result);
        }
        catch (Exception ex)
        {
            var willRetry = await _retryNumerator.TrackRetryAsync(
                session, item.Result, ex, Logger);
            // willRetry = false when max attempts are exhausted
        }
    }

    await session.SaveChangesAsync(cancellationToken);
}
```

Call `ClearRetryAsync()` after successful processing to remove any leftover retry counters from previous failures.

## Revision Subscriptions

For change detection (comparing previous vs. current document state), subscribe to `Revision<T>`:

```csharp
public class CompanyChangeWorker : SparkSubscriptionWorker<Revision<Company>>
{
    public CompanyChangeWorker(IDocumentStore store, ILogger<CompanyChangeWorker> logger)
        : base(store, logger) { }

    protected override SubscriptionCreationOptions ConfigureSubscription()
        => new() { Query = "from Companies (Revisions = true)" };

    protected override async Task ProcessBatchAsync(
        SubscriptionBatch<Revision<Company>> batch, CancellationToken cancellationToken)
    {
        using var session = batch.OpenAsyncSession();
        foreach (var item in batch.Items)
        {
            var previous = item.Result.Previous;
            var current = item.Result.Current;
            // React to changes between previous and current...
        }
        await session.SaveChangesAsync(cancellationToken);
    }
}
```

This requires RavenDB document revisions to be enabled on the collection.

## Real-World Example: Spark Messaging

The `MintPlayer.Spark.Messaging` package uses `SparkSubscriptionWorker<T>` internally for its message processing pipeline. `MessageSubscriptionWorker` subscribes to `SparkMessage` documents filtered by queue name and status:

```csharp
internal sealed class MessageSubscriptionWorker : SparkSubscriptionWorker<SparkMessage>
{
    protected override string SubscriptionName => $"SparkMessaging-{_queueName}";
    protected override int MaxDocsPerBatch => 1;

    protected override SubscriptionCreationOptions ConfigureSubscription()
    {
        return new SubscriptionCreationOptions
        {
            Query = $@"from SparkMessages
                       where QueueName = '{_queueName}'
                       and Status = 'Pending'
                       and (NextAttemptAtUtc = null or NextAttemptAtUtc <= now())"
        };
    }

    protected override async Task ProcessBatchAsync(
        SubscriptionBatch<SparkMessage> batch, CancellationToken cancellationToken)
    {
        foreach (var item in batch.Items)
        {
            var message = item.Result;
            var session = batch.OpenAsyncSession();
            // Mark as Processing, deserialize payload, resolve handlers,
            // handle retries, dead-lettering, and expiration...
        }
    }
}
```

This demonstrates a pattern where the subscription query does server-side filtering (only pending messages past their retry delay), and the worker handles retries, dead-lettering, and state transitions within `ProcessBatchAsync`.

## Source Generator Details

The `SubscriptionWorkerRegistrationGenerator` source generator scans your project for all non-abstract classes that inherit from `SparkSubscriptionWorker<T>` (at any depth in the inheritance chain). It generates a static extension method:

```csharp
// Auto-generated: SparkSubscriptionWorkerRegistrations.g.cs
namespace YourProject
{
    internal static class SparkSubscriptionWorkersExtensions
    {
        internal static IServiceCollection AddSparkSubscriptionWorkers(
            this IServiceCollection services)
        {
            SparkSubscriptionExtensions.AddSubscriptionWorker<OrderProcessingWorker>(services);
            SparkSubscriptionExtensions.AddSubscriptionWorker<CompanyChangeWorker>(services);
            return services;
        }
    }
}
```

This eliminates the need to manually register each worker in `Program.cs`.

## Requirements

- .NET 10.0+
- RavenDB 6.2+
- An `IDocumentStore` registered in the DI container (provided by `AddSpark()` or registered manually)

## Complete Example

See the following files for working implementations:
- `MintPlayer.Spark.SubscriptionWorker/SparkSubscriptionWorker.cs` -- abstract base class with connection loop and error handling
- `MintPlayer.Spark.SubscriptionWorker/RetryNumerator.cs` -- per-document retry tracking
- `MintPlayer.Spark.SubscriptionWorker/SparkSubscriptionExtensions.cs` -- DI registration helpers
- `MintPlayer.Spark.Messaging/Services/MessageSubscriptionWorker.cs` -- real-world usage in the messaging package
- `MintPlayer.Spark.SourceGenerators/Generators/SubscriptionWorkerRegistrationGenerator.cs` -- source generator for auto-registration

# MintPlayer.Spark.SubscriptionWorker

A RavenDB subscription worker framework with built-in retry logic, incremental backoff, and ASP.NET Core lifecycle management. Fully independent -- no dependency on the core Spark CRUD framework.

## Installation

```bash
dotnet add package MintPlayer.Spark.SubscriptionWorker
```

## Quick Start

### 1. Create a Subscription Worker

Extend `SparkSubscriptionWorker<T>` and implement two methods: `ConfigureSubscription()` for the RQL query, and `ProcessBatchAsync()` for the batch handler.

```csharp
using MintPlayer.Spark.SubscriptionWorker;
using Raven.Client.Documents;
using Raven.Client.Documents.Subscriptions;

public class CarRefreshWorker : SparkSubscriptionWorker<Car>
{
    private readonly RetryNumerator _retryNumerator = new();

    public CarRefreshWorker(IDocumentStore store, ILogger<CarRefreshWorker> logger)
        : base(store, logger) { }

    protected override SubscriptionCreationOptions ConfigureSubscription()
        => new() { Query = "from Cars" };

    protected override int MaxDocsPerBatch => 100;

    protected override async Task ProcessBatchAsync(
        SubscriptionBatch<Car> batch, CancellationToken ct)
    {
        using var session = batch.OpenAsyncSession();
        foreach (var item in batch.Items)
        {
            try
            {
                var car = item.Result;
                // Process the car document...
            }
            catch (Exception ex)
            {
                var shouldRetry = await _retryNumerator.TrackRetryAsync(
                    session, item.Result, ex, Logger);
                if (!shouldRetry)
                {
                    Logger.LogError(ex, "Permanently failed processing car {Id}", item.Id);
                }
            }
        }
        await session.SaveChangesAsync(ct);
    }
}
```

### 2. Register Services

```csharp
// Program.cs
builder.Services.AddSparkSubscriptions();
builder.Services.AddSparkSubscriptionWorkers(); // Source-generated: discovers all workers
```

`AddSparkSubscriptionWorkers()` is generated at compile time by the `SubscriptionWorkerRegistrationGenerator` source generator. It discovers all `SparkSubscriptionWorker<T>` subclasses in your project and registers them as hosted services.

## How It Works

### Subscription Lifecycle

Each worker runs as a `BackgroundService`:

1. **Startup**: Creates or updates the RavenDB subscription (idempotent)
2. **Connection loop**: Connects to the subscription and receives document batches
3. **Batch processing**: Calls `ProcessBatchAsync()` for each batch
4. **Error recovery**: Categorized exception handling with automatic reconnection
5. **Shutdown**: Graceful cancellation via `CancellationToken`

### Categorized Error Handling

| Exception | Category | Action |
|-----------|----------|--------|
| `OperationCanceledException` | Cancellation | Stop gracefully |
| `SubscriptionInUseException` | Recoverable | Wait `RetryDelay * 2`, reconnect |
| `SubscriberErrorException` | Recoverable | Wait `RetryDelay`, reconnect |
| `SubscriptionClosedException` | Non-recoverable | Log error, stop |
| `DatabaseDoesNotExistException` | Non-recoverable | Log error, stop |
| `SubscriptionDoesNotExistException` | Non-recoverable | Log error, stop |
| `SubscriptionInvalidStateException` | Non-recoverable | Log error, stop |
| `AuthorizationException` | Non-recoverable | Log error, stop |

### RetryNumerator

Per-document retry tracking using RavenDB counters and the `@refresh` metadata mechanism:

```csharp
var retryNumerator = new RetryNumerator
{
    MaxAttempts = 5,             // Default: 5
    BaseDelay = TimeSpan.FromSeconds(30), // Default: 30s
    CounterName = "SparkRetryAttempts",   // Default
};

// In your ProcessBatchAsync:
try
{
    // Process item...
}
catch (Exception ex)
{
    var willRetry = await retryNumerator.TrackRetryAsync(session, entity, ex, Logger);
    // willRetry = false when max attempts exhausted
}
```

**Backoff schedule** (linear incremental: `BaseDelay * attempt`):

| Attempt | Delay |
|---------|-------|
| 1 | 30 seconds |
| 2 | 60 seconds |
| 3 | 90 seconds |
| 4 | 120 seconds |
| 5 | 150 seconds |
| Exhausted | 1 day (parked) |

When an attempt fails, `TrackRetryAsync` increments a RavenDB counter on the document and sets `@refresh` metadata to schedule redelivery. After max attempts, the counter is deleted and the document is parked for 1 day.

## Configuration

Override virtual properties on your worker class:

```csharp
public class MyWorker : SparkSubscriptionWorker<MyDocument>
{
    // Subscription name in RavenDB (default: class name minus "Worker"/"SubscriptionWorker")
    protected override string SubscriptionName => "MyCustomSubscription";

    // Target database (default: null = store default)
    protected override string? Database => null;

    // Max documents per batch (default: 256)
    protected override int MaxDocsPerBatch => 100;

    // Whether to reconnect after normal completion (default: true)
    protected override bool KeepRunning => true;

    // Wait time before connection retry (default: 30s)
    protected override TimeSpan RetryDelay => TimeSpan.FromSeconds(30);

    // Max erroneous period before giving up (default: 5min)
    protected override TimeSpan MaxDownTime => TimeSpan.FromMinutes(5);
}
```

### Lifecycle Hooks

```csharp
protected override Task OnWorkerStartedAsync() => Task.CompletedTask;
protected override Task OnWorkerStoppedAsync() => Task.CompletedTask;
protected override Task OnBatchCompletedAsync(int itemCount) => Task.CompletedTask;
protected override Task OnNonRecoverableErrorAsync(Exception exception) => Task.CompletedTask;
```

## Revision Subscriptions

For change detection (comparing previous vs. current document state), use `Revision<T>`:

```csharp
public class CompanyChangeWorker : SparkSubscriptionWorker<Revision<Company>>
{
    protected override SubscriptionCreationOptions ConfigureSubscription()
        => new() { Query = "from Companies (Revisions = true)" };

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

## Extension Methods

| Method | Description |
|--------|-------------|
| `AddSparkSubscriptions(Action<SparkSubscriptionOptions>?)` | Register subscription infrastructure |
| `AddSubscriptionWorker<TWorker>()` | Register a single worker as a hosted service |

### Source-Generated

| Method | Description |
|--------|-------------|
| `AddSparkSubscriptionWorkers()` | Auto-registers all `SparkSubscriptionWorker<T>` subclasses in your project |

## Requirements

- .NET 10.0+
- RavenDB 6.2+
- An `IDocumentStore` registered in the DI container (provided by `AddSpark()` or registered manually)

## License

MIT License

# MintPlayer.Spark.SubscriptionWorker

Background workers that react to stored documents, on whichever store the application uses: RavenDB, SQL Server,
PostgreSQL or the in-memory store. A worker is an ASP.NET Core `BackgroundService` over the store's durable
subscriptions (`ISparkSubscriptions`): every document of a type that matches a filter, delivered in commit order as its
current state, the backlog included, at least once.

## Installation

```bash
dotnet add package MintPlayer.Spark.SubscriptionWorker
```

The base class lives in `MintPlayer.Spark.SubscriptionWorker.Abstractions`; a library that only defines workers can
reference that package alone. The host that registers them references this one.

## Quick start

### 1. Write a worker

Extend `SparkSubscriptionWorker<T>`, give it a filter, and handle each batch:

```csharp
using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.SubscriptionWorker;

public partial class OrderProcessingWorker : SparkSubscriptionWorker<Order>
{
    // The base class takes ISparkSubscriptions, ILoggerFactory and ISparkStoreReadiness through [Inject];
    // the source generator writes the constructor. Declare your own dependencies the same way, which is
    // why the class must be `partial`.
    [Inject] private readonly ISparkStore store;

    // Which documents are delivered; null (the default) delivers every Order.
    protected override Expression<Func<Order, bool>>? Filter => o => o.Status == "Pending";

    protected override async Task ProcessBatchAsync(SparkSubscriptionBatch<Order> batch, CancellationToken cancellationToken)
    {
        using var session = store.OpenSession();
        foreach (var item in batch.Items)
        {
            var order = item.Document;
            order.Status = "Processed";
            // The version the batch read: the save fails if someone changed the order since.
            await session.StoreAsync(order, item.Version, item.Id, cancellationToken);
        }
        await session.SaveChangesAsync(cancellationToken);
    }
}
```

A filter is a pure predicate over the document's fields. Never put the clock in it (`DateTime.UtcNow`): RavenDB
evaluates it once per change, not per read, so a time comparison silently never matches. Materialize "the time has
come" as a field that another component sets (see [Spark Messaging](#real-world-example-spark-messaging)).

### 2. Register it

With the Spark source generators referenced, every non-abstract `SparkSubscriptionWorker<T>` subclass in your project is
registered by one generated call:

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddSubscriptionWorkers();   // source-generated
});
```

Or register a worker yourself:

```csharp
builder.Services.AddSubscriptionWorker<OrderProcessingWorker>();
```

`AddSubscriptionWorker<T>()` also registers the store readiness the worker waits for.

## What a worker guarantees

The guarantees are the store's (`ISparkSubscriptions`), the same on every provider:

- **Exclusive.** One runner per subscription name at a time, across hosts. A second host waits without error and takes
  over when the first stops or dies (RavenDB's `WaitForFree`; on the other stores an `ISparkStoreLock` lease).
- **At least once.** A batch is acknowledged when `ProcessBatchAsync` returns. If it throws, or the host dies, the same
  batch is delivered again after `RetryDelay`. So a handler checks the state it acts on: write with the item's
  `Version`, or claim the document first.
- **Current state.** A document changed twice before its delivery is delivered once, as it is now. One that stopped
  matching the filter is not delivered.

RavenDB runs its own data subscriptions. The in-memory and relational stores keep a cursor over their commit order in
`ISparkAtomicValues`.

## Lifecycle

1. The worker waits until the store is ready: on a relational store, until `UseSpark` has applied the schema.
2. It creates the subscription, or updates its filter. A definition the store refuses (a licence cap, for example) is
   **thrown**, and it stops the host: an application whose queue is silently dead is worse than one that will not
   run.
3. `OnWorkerStartedAsync()` runs.
4. Batches arrive. After each `ProcessBatchAsync`, `OnBatchCompletedAsync(itemCount)` runs.
5. A handler that throws, or a lost connection, is retried after `RetryDelay` and logged as a warning.
6. A subscription that cannot go on (deleted under the worker, a missing database, refused authorization, or failing
   for longer than `MaxDownTime`) ends the worker: `OnNonRecoverableErrorAsync(exception)` runs, and the host keeps
   running.
7. On shutdown, or after a non-recoverable error, `OnWorkerStoppedAsync()` runs.

## Configuration

Override these on the worker:

```csharp
// The subscription's name: its cursor and its exclusivity are per name.
// Default: the class name without "SubscriptionWorker" or "Worker" (OrderProcessingWorker → "OrderProcessing").
protected override string SubscriptionName => "OrderProcessing";

// Which documents are delivered. Default: null, every document of T.
protected override Expression<Func<Order, bool>>? Filter => o => o.Status == "Pending";

// Most documents per batch. Default: 256.
protected override int MaxDocsPerBatch => 100;

// Wait before a failed batch or connection is retried. Default: 30 seconds.
protected override TimeSpan RetryDelay => TimeSpan.FromSeconds(30);

// How long the subscription may keep failing before the worker gives up. Default: 5 minutes.
protected override TimeSpan MaxDownTime => TimeSpan.FromMinutes(5);
```

`Logger` is an `ILogger` in the concrete worker's category.

### Lifecycle hooks

```csharp
protected override Task OnWorkerStartedAsync() => Task.CompletedTask;
protected override Task OnWorkerStoppedAsync() => Task.CompletedTask;
protected override Task OnBatchCompletedAsync(int itemCount) => Task.CompletedTask;
protected override Task OnNonRecoverableErrorAsync(Exception exception) => Task.CompletedTask;
```

### Writing your own constructor

The generated base constructor is
`SparkSubscriptionWorker<T>(ISparkSubscriptions subscriptions, ILoggerFactory loggerFactory, ISparkStoreReadiness storeReadiness)`.
A worker that writes its own constructor (for instance one that is built by hand rather than resolved from DI) calls
`: base(subscriptions, loggerFactory, storeReadiness)`.

## Real-world example: Spark Messaging

[`MintPlayer.Spark.Messaging`](../../messaging/MintPlayer.Spark.Messaging/README.md) runs on one worker, `MessageFeeder`.
It subscribes to the `SparkMessage` documents of **every** queue at once, claims each one, and hands it to the queue's
in-process lane:

```csharp
internal sealed class MessageFeeder : SparkSubscriptionWorker<SparkMessage>
{
    // No trailing hyphen: the startup cleanup deletes "SparkMessaging-*", and this name
    // escapes that prefix by exactly one character.
    protected override string SubscriptionName => "SparkMessaging";

    // No queue-name predicate: one subscription serves all queues.
    protected override Expression<Func<SparkMessage, bool>> Filter =>
        m => (m.Status == EMessageStatus.Pending && (m.NextAttemptAtUtc == null || m.WakeUp == true))
          || (m.Status == EMessageStatus.Failed && m.WakeUp == true);

    protected override async Task ProcessBatchAsync(SparkSubscriptionBatch<SparkMessage> batch, CancellationToken cancellationToken)
    {
        // Merge with the sorted page, claim each message under optimistic concurrency and save BEFORE
        // returning (which acknowledges the batch), then hand the id to its queue's lane.
    }
}
```

Two things in it are worth copying, because both were learned the hard way:

- **`WakeUp == true`, never `NextAttemptAtUtc <= now`.** The filter cannot evaluate time, so a message parked for a
  retry would never be delivered again. `MessageRetrySweeper` evaluates time and sets the boolean, and that write is
  also what makes the subscription look at the message again.
- **The claim is saved before the batch is acknowledged.** An acknowledged batch is never delivered again, so a status
  written after the acknowledgement, with nothing that reads it, would leave a crashed host's message stranded forever.

## Extension methods

| Method | Description |
|--------|-------------|
| `AddSubscriptionWorker<TWorker>()` | Registers one worker as a hosted service, with the store readiness it waits for |
| `AddSparkSubscriptions()` | Registers nothing: there is nothing to configure |
| `spark.AddSubscriptionWorkers()` | Source-generated: registers every `SparkSubscriptionWorker<T>` subclass in your project |

The generator (`SubscriptionWorkerRegistrationGenerator`) finds every non-abstract class that inherits from
`SparkSubscriptionWorker<T>`, at any depth, and emits into your root namespace:

```csharp
// SparkSubscriptionWorkerRegistrations.g.cs
internal static class SparkSubscriptionWorkersBuilderExtensions
{
    internal static ISparkBuilder AddSubscriptionWorkers(this ISparkBuilder builder)
    {
        SparkSubscriptionExtensions.AddSubscriptionWorker<OrderProcessingWorker>(builder.Services);
        return builder;
    }
}
```

## Requirements

- .NET 11
- A Spark store with subscriptions (`SparkStoreCapabilities.Subscriptions`): RavenDB, SQL Server, PostgreSQL and the
  in-memory store all have them.

## License

MIT License

# Durable Message Bus

Spark includes an opt-in durable message bus backed by RavenDB. Messages are persisted as documents, processed via RavenDB data subscriptions, and retried automatically on failure. Different queues run independently so a failing message in one queue never blocks another.

## Overview

The messaging system is split into two packages:

| Package | Purpose | Used by |
|---|---|---|
| `MintPlayer.Spark.Messaging.Abstractions` | Interfaces and attributes (`IMessageBus`, `IRecipient<T>`, `[MessageQueue]`) | Shared library projects that define messages |
| `MintPlayer.Spark.Messaging` | Implementation (message storage, subscription workers, retry logic) | Web application projects |

Messages are plain C# records or classes. Recipients are DI-scoped services that handle messages. The framework connects the two through named queues.

## Step 1: Define Messages

Create message classes in a shared library project so both the sender and recipients can reference them. Decorate related messages with `[MessageQueue]` to group them into a named queue:

```csharp
using MintPlayer.Spark.Messaging.Abstractions;

[MessageQueue("PersonEvents")]
public record PersonCreatedMessage(string PersonId, string FullName);

[MessageQueue("PersonEvents")]
public record PersonDeletedMessage(string PersonId);
```

Both message types above share the `PersonEvents` queue, which means they are processed in FIFO order within that queue.

Messages without `[MessageQueue]` use their full type name as the queue name (one queue per message type).

## Step 2: Create Recipients

A recipient implements `IRecipient<T>` for the message type it handles. Recipients are instantiated within a DI scope, so you can inject any scoped service:

```csharp
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.SourceGenerators.Attributes;

public partial class LogPersonCreated : IRecipient<PersonCreatedMessage>
{
    [Inject] private readonly ILogger<LogPersonCreated> _logger;

    public Task HandleAsync(PersonCreatedMessage message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Person created: {FullName} ({PersonId})",
            message.FullName, message.PersonId);
        return Task.CompletedTask;
    }
}
```

A single class can implement `IRecipient<T>` for multiple message types. Multiple recipients can handle the same message type -- all registered recipients are invoked for each message. Each recipient's success or failure is tracked independently (see [Per-Handler Retry Isolation](#per-handler-retry-isolation) below).

### Checkpoint Recipients

When a handler processes a collection of items, failure partway through would normally cause the entire message to be retried from scratch. To avoid this, implement `ICheckpointRecipient<T>` and inject `IMessageCheckpoint`:

```csharp
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.SourceGenerators.Attributes;

public partial class NotifyEmployeesRecipient : ICheckpointRecipient<CompanyUpdatedMessage>
{
    [Inject] private readonly ILogger<NotifyEmployeesRecipient> _logger;
    [Inject] private readonly IMessageCheckpoint _checkpoint;

    public Task HandleAsync(CompanyUpdatedMessage message, CancellationToken cancellationToken)
        => ProcessFromIndex(message, startIndex: 0, cancellationToken);

    public Task HandleAsync(CompanyUpdatedMessage message, string checkpoint, CancellationToken cancellationToken)
        => ProcessFromIndex(message, startIndex: int.Parse(checkpoint), cancellationToken);

    private async Task ProcessFromIndex(CompanyUpdatedMessage message, int startIndex, CancellationToken ct)
    {
        for (var i = startIndex; i < message.EmployeeIds.Count; i++)
        {
            // Process each employee...
            _logger.LogInformation("Notified employee {EmployeeId}", message.EmployeeIds[i]);

            // Save progress. On retry, HandleAsync(message, checkpoint, ct) is called
            // with the last saved value, so processing resumes from here.
            await _checkpoint.SaveAsync((i + 1).ToString(), ct);
        }
    }
}
```

The checkpoint is a free-form string -- use an index, offset, cursor, or any serialized state. Each call to `SaveAsync` overwrites the previous checkpoint and is persisted to RavenDB immediately.

## Step 3: Register Services

In your `Program.cs`, register the messaging services and deploy the RavenDB index:

```csharp
builder.Services.AddSparkMessaging();    // Register MessageBus and subscription workers
builder.Services.AddSparkRecipients();   // Source-generated: auto-discovers all IRecipient<T> classes

var app = builder.Build();

app.CreateSparkMessagingIndexes();       // Deploy the SparkMessages/ByQueue RavenDB index
```

`AddSparkMessaging()` reuses the `IDocumentStore` singleton already registered by `AddSpark()`. It does not depend on any Spark CRUD types.

`AddSparkRecipients()` is generated at compile time by the `RecipientRegistrationGenerator` source generator. It discovers all `IRecipient<T>` implementations in your project and registers them automatically.

## Step 4: Send Messages

Inject `IMessageBus` into your Actions class (or any other service) and call `BroadcastAsync`:

```csharp
using MintPlayer.Spark.Messaging.Abstractions;

public partial class PersonActions : DefaultPersistentObjectActions<Person>
{
    [Inject] private readonly IMessageBus messageBus;

    public override async Task OnAfterSaveAsync(PersistentObject obj, Person entity)
    {
        // Immediate: processed as soon as possible
        await messageBus.BroadcastAsync(
            new PersonCreatedMessage(entity.Id!, $"{entity.FirstName} {entity.LastName}"));
    }

    public override async Task OnBeforeDeleteAsync(Person entity)
    {
        await messageBus.BroadcastAsync(new PersonDeletedMessage(entity.Id!));
    }
}
```

Both `BroadcastAsync` and `DelayBroadcastAsync` store a `SparkMessage` document in RavenDB and return immediately (fire-and-forget). The subscription workers pick them up asynchronously.

### Delayed Messages

To schedule a message for later processing, use `DelayBroadcastAsync`:

```csharp
await messageBus.DelayBroadcastAsync(
    new SendReminderMessage(entity.Id!),
    TimeSpan.FromMinutes(30));
```

The message is stored immediately but the subscription worker will not pick it up until the delay has elapsed.

### Queue Name Override

For per-collection queue isolation (e.g., in the replication package), you can pass an explicit queue name that overrides the `[MessageQueue]` attribute:

```csharp
await messageBus.BroadcastAsync(message, "spark-sync-Cars");
```

## How It Works

### Message Processing

1. At startup, `MessageSubscriptionManager` discovers all queue names from registered `IRecipient<T>` types
2. For each queue, it creates a dedicated `MessageSubscriptionWorker` using a RavenDB data subscription
3. Each worker processes messages one at a time (`MaxDocsPerBatch = 1`) in FIFO order
4. Different queues run concurrently and independently
5. Each message is dispatched within a fresh DI scope

### Per-Handler Retry Isolation

When multiple recipients handle the same message type, each handler's success or failure is tracked independently. If handler A succeeds but handler B fails, only handler B is retried -- handler A is **not** re-executed.

```
Message M  →  LogCompanyUpdated ✓ (recorded)
           →  NotifyEmployeesRecipient ✗ (failed)
           →  retry
           →  LogCompanyUpdated ⏭ (skipped -- already completed)
           →  NotifyEmployeesRecipient ↻ (retried)
```

This prevents duplicate side effects in handlers that already completed (sending emails twice, creating duplicate records, etc.).

Each handler has its own `AttemptCount`. When a handler exceeds `MaxAttempts`, it is individually **dead-lettered** while other handlers continue their retry cycles. The message is marked completed only when all handlers have reached a terminal state (completed or dead-lettered).

### Retry with Incremental Backoff

When a handler throws an exception, retries are scheduled with increasing delays:

| Attempt | Delay |
|---|---|
| 1 | 5 seconds |
| 2 | 30 seconds |
| 3 | 2 minutes |
| 4 | 10 minutes |
| 5 | 1 hour |

After the maximum number of attempts (default 5), the handler is **dead-lettered** and the message continues processing remaining handlers.

When multiple handlers have different attempt counts, the retry delay is based on the highest `AttemptCount` among failing handlers.

### Non-Retryable Errors

If a recipient throws `NonRetryableException`, that handler is dead-lettered immediately without any retries:

```csharp
public async Task HandleAsync(MyMessage message, CancellationToken cancellationToken)
{
    var response = await httpClient.PostAsync(url, content, cancellationToken);

    if (response.StatusCode == HttpStatusCode.BadRequest)
        throw new NonRetryableException($"Request rejected: {response.StatusCode}");

    response.EnsureSuccessStatusCode();
}
```

Other handlers for the same message are unaffected -- they continue to execute normally.

### Queue Isolation

Messages in different queues cannot block each other. A failing message in the `ValidateBuild` queue will not prevent messages in the `PersonEvents` queue from being processed.

## Configuration

```csharp
builder.Services.AddSparkMessaging(options =>
{
    options.MaxAttempts = 5;                                  // Default: 5
    options.FallbackPollInterval = TimeSpan.FromSeconds(30);  // Default: 30s
    options.RetentionDays = 7;                                // Days before completed/dead-lettered messages expire
    options.BackoffDelays = new[]                              // Customizable retry delays
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
    };
});
```

## RavenDB Document Model

Messages are stored as `SparkMessage` documents in the `SparkMessages` collection:

| Field | Type | Description |
|---|---|---|
| `Id` | `string` | Document ID (`SparkMessages/{guid}`) |
| `QueueName` | `string` | Queue this message belongs to |
| `MessageType` | `string` | Assembly-qualified CLR type name |
| `PayloadJson` | `string` | JSON-serialized message payload |
| `CreatedAtUtc` | `DateTime` | When the message was broadcast |
| `NextAttemptAtUtc` | `DateTime?` | Earliest retry time (`null` = immediate) |
| `AttemptCount` | `int` | Number of times picked up for processing |
| `MaxAttempts` | `int` | Maximum attempts per handler before dead-lettering |
| `Status` | `EMessageStatus` | `Pending`, `Processing`, `Completed`, `Failed`, `DeadLettered` |
| `CompletedAtUtc` | `DateTime?` | When the last handler completed |
| `Handlers` | `HandlerExecution[]` | Per-handler execution state (see below) |

Each entry in the `Handlers` array tracks an individual recipient:

| Field | Type | Description |
|---|---|---|
| `HandlerType` | `string` | Assembly-qualified type name of the `IRecipient<T>` implementation |
| `Status` | `EHandlerStatus` | `Pending`, `Completed`, `Failed`, `DeadLettered` |
| `AttemptCount` | `int` | Number of attempts for this handler |
| `LastError` | `string?` | Exception message from last failure |
| `CompletedAtUtc` | `DateTime?` | When this handler completed successfully |
| `Checkpoint` | `string?` | Last checkpoint saved by `ICheckpointRecipient<T>` handlers |

Example document in RavenDB Studio:

```json
{
  "QueueName": "CompanyEvents",
  "Status": "Failed",
  "AttemptCount": 2,
  "Handlers": [
    {
      "HandlerType": "DemoApp.Recipients.LogCompanyUpdated, DemoApp",
      "Status": "Completed",
      "AttemptCount": 1,
      "CompletedAtUtc": "2026-04-03T10:00:01Z"
    },
    {
      "HandlerType": "DemoApp.Recipients.NotifyEmployeesRecipient, DemoApp",
      "Status": "Failed",
      "AttemptCount": 2,
      "LastError": "HttpRequestException: 503 Service Unavailable",
      "Checkpoint": "37"
    }
  ]
}
```

In this example, `LogCompanyUpdated` completed on the first attempt and will not be re-executed. `NotifyEmployeesRecipient` failed twice and will resume from checkpoint `"37"` on the next retry.

You can query message status directly in RavenDB Studio for observability. Completed and dead-lettered messages are automatically expired after `RetentionDays` (default 7) using RavenDB's built-in document expiration.

## Complete Example

See the DemoApp for a working example:

- `Demo/DemoApp.Library/Messages/` -- message definitions with `[MessageQueue]`
- `Demo/DemoApp/Recipients/LogPersonCreated.cs` -- simple `IRecipient<T>` handler
- `Demo/DemoApp/Recipients/LogPersonDeleted.cs` -- simple `IRecipient<T>` handler
- `Demo/DemoApp/Recipients/LogCompanyUpdated.cs` -- demonstrates per-handler retry isolation
- `Demo/DemoApp/Recipients/NotifyEmployeesRecipient.cs` -- `ICheckpointRecipient<T>` with batch progress tracking
- `Demo/DemoApp/Actions/PersonActions.cs` -- broadcasting messages from lifecycle hooks
- `Demo/DemoApp/Actions/CompanyActions.cs` -- broadcasting batch messages with employee IDs
- `Demo/DemoApp/Program.cs` -- service registration

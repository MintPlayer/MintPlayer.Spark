# MintPlayer.Spark.Messaging

A durable message bus for MintPlayer.Spark with RavenDB persistence, scoped recipients, lane isolation, partitioned ordering and automatic retry. Fully opt-in -- the core Spark library remains unchanged.

Messages are persisted as documents, delivered through **one** RavenDB data subscription however many queues exist, and retried automatically on failure. Lanes run independently, so a failing message in one never blocks another.

## Overview

The messaging system is split into two packages:

| Package | Purpose | Used by |
|---|---|---|
| `MintPlayer.Spark.Messaging.Abstractions` | Interfaces and attributes (`IMessageBus`, `IRecipient<T>`, `[MessageQueue]`) | Shared library projects that define messages |
| `MintPlayer.Spark.Messaging` | Implementation (message storage, the shared subscription, lane pumps, retry) | Web application projects |

Messages are plain C# records or classes. Recipients are DI-scoped services that handle messages. The framework connects the two through named lanes.

## Installation

```bash
# For message definitions (in your shared library project)
dotnet add package MintPlayer.Spark.Messaging.Abstractions

# For the full implementation (in your web application project)
dotnet add package MintPlayer.Spark.Messaging
```

## Quick Start

### 1. Define Messages

Create message classes in a shared library project so both the sender and recipients can reference them. Messages are plain C# records or classes. Use `[MessageQueue]` to group related messages into a named **lane**. Lanes never block each other. Ordering *within* a lane is opt-in and scoped to a partition key (see "Lanes and partitions").

```csharp
using MintPlayer.Spark.Messaging.Abstractions;

[MessageQueue("PersonEvents")]
public record PersonCreatedMessage(string PersonId, string FullName);

[MessageQueue("PersonEvents")]
public record PersonDeletedMessage(string PersonId);
```

Both message types above share the `PersonEvents` lane. Sharing a lane means they share its delivery mode and retry schedule; whether they are ordered depends on whether the lane declares `Ordered()` and a partition key.

Messages without `[MessageQueue]` use their full type name as the lane name (one lane per message type). This is now harmless; before lanes shared a subscription it silently cost one of the three the licence allows.

### 2. Create Recipients

Recipients handle messages. They are **always instantiated within a DI scope**, so you can inject any scoped service (e.g., `IAsyncDocumentSession`, `IMessageBus`, `ILogger<T>`, or application-specific services).

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

#### Checkpoint Recipients

When a handler processes a collection of items, failure partway through would normally cause the entire message to be retried from scratch. To avoid this, implement `ICheckpointRecipient<T>` and inject `IMessageCheckpoint`. On retry, the framework calls the checkpoint overload so the handler can resume where it left off:

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

### 3. Register Services

```csharp
// Program.cs
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddMessaging();     // MessageBus, the shared subscription, lane pumps, the reaper
    spark.AddRecipients();    // Source-generated: auto-discovers all IRecipient<T> classes
});
```

`AddMessaging()` reuses the `IDocumentStore` singleton already registered by `AddSpark()`, and
deploys the `SparkMessages/ByQueue` index for you — there is nothing to call after `Build()`. It
does not depend on any Spark CRUD types.

`AddRecipients()` is generated at compile time by the `RecipientRegistrationGenerator` source
generator. It discovers all `IRecipient<T>` implementations in your project and registers them
automatically.

### 4. Broadcast Messages

Inject `IMessageBus` into your Actions class (or any other service) and call `BroadcastAsync` or `DelayBroadcastAsync`:

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

Both `BroadcastAsync` and `DelayBroadcastAsync` store a `SparkMessage` document in RavenDB and return immediately (fire-and-forget). The lane pump picks it up asynchronously.

#### Delayed Messages

To schedule a message for later processing, use `DelayBroadcastAsync`:

```csharp
await messageBus.DelayBroadcastAsync(
    new SendReminderMessage(entity.Id!),
    TimeSpan.FromMinutes(30));
```

The message is stored immediately and carries `VisibleAtUtc`; the lane pump skips it until then. A delayed message does **not** hold its partition — a delay is a scheduling instruction, not a dependency — so messages broadcast during its delay window may run before it.

#### Lane Name Override

You can pass an explicit lane name that overrides the `[MessageQueue]` attribute:

```csharp
await messageBus.BroadcastAsync(message, "spark-sync");
```

This overload was documented before lanes shared a subscription but did **not** work: a lane name with no registered recipient never got a worker, so the message sat `Pending` forever. A pump is now created for whatever lane a message names, so it does what it always claimed to.

## How It Works

### Message Processing

Internally the library uses **one** RavenDB data subscription for the whole application, however many
queues exist. RavenDB allows three data subscriptions per database on the unlicensed and Community
tiers alike, so one per queue does not scale — and exceeding the limit fails *silently*, which is how
three of a production app's six queues sat dead for months.

1. `MessageFeeder` holds the single subscription. It does **no handler work**: RavenDB does not fetch
   the next batch until the callback returns, so a slow handler here would delay every other lane. It
   rings a lane's doorbell and returns.
2. Each lane has a **pump**. When rung, it queries its own backlog from `SparkMessages_ByQueue`,
   ordered by `Sequence`, and treats the first row of each partition as that partition's head.
3. Order therefore comes from a **sort**, not from delivery order. That matters because a failed
   message is written back, which bumps its change vector and moves it behind everything broadcast
   since — the reason the previous per-queue design did not actually preserve order across a retry.
4. Within a **partition** (see below) messages run one at a time, oldest first, and a failing head
   blocks only that partition. Different partitions, and different lanes, run concurrently.
5. Each message is dispatched within a fresh **DI scope**, so recipients get fresh scoped services.

### Lanes and partitions

A `[MessageQueue]` is a **lane**: an isolation unit. Lanes never block one another.

Ordering is scoped to a **partition key** carried on the message, not to the lane, because the two
have very different natural sizes. A lane is declared once, at compile time; an ordering domain is a
build, a pull request, a document — unbounded, and known only at runtime. Keying ordering by lane
conflates them, so it asserts a dependency between messages that have none, and one poisoned message
stalls unrelated work for the whole retry ladder.

```csharp
builder.AddMessaging(messaging: messaging => messaging
    .AddLane(lanes => lanes.Queue<ParseSessionMessage>()
        .Ordered()
        .PartitionBy<ParseSessionMessage>(m => m.BuildId)
        .PartitionBy<FinalizeBuildMessage>(m => m.BuildId)   // finalize cannot overtake ITS build's parses
        .MaxPartitionsInFlight(2))                            // …while other builds run in parallel

    .AddLane(lanes => lanes.Queue("spark-email")
        .Concurrent(maxConcurrency: 8)                        // no ordering; nothing waits
        .Retry(RetrySchedule.Ladder("1m 5m 1h 6h 1d 3d 7d"))));
```

`Ordered()` and `Concurrent()` return different builder types, so "strictly ordered, four at a time"
has no method to call rather than being rejected by a validator. A lane nobody declares is
`Concurrent(1)` — never ordered, because a silently-ordered lane with no partition key would serialize
everything on one key.

### Lanes can be configured from the container

A lane declaration is an ordinary service registration resolved **on first use**, not while services
are being registered. So a lane can be configured from anything the application has: options, a
resource probe, a service. Three shapes, all equivalent in when they run:

```csharp
messaging
    // A delegate.
    .AddLane(lanes => lanes.Queue("spark-email").Concurrent(8))

    // A delegate with the resolved container.
    .AddLane((lanes, services) =>
    {
        var options = services.GetRequiredService<IOptions<MailOptions>>().Value;
        lanes.Queue("spark-email")
             .Concurrent(options.Workers)
             .Retry(RetrySchedule.Ladder(options.RetryLadder));
    })

    // A class the container constructs, so it injects rather than locates.
    .AddLane<MailLaneConfigurator>();
```

```csharp
internal sealed class MailLaneConfigurator(IOptions<MailOptions> options) : ILaneConfigurator
{
    public void Configure(ILaneBuilder lanes) => lanes
        .Queue("spark-email")
        .Concurrent(options.Value.Workers)
        .Retry(RetrySchedule.Ladder(options.Value.RetryLadder));
}
```

A **framework package** declaring its own lane uses the same path —
`services.AddSparkLane<MyLaneConfigurator>()` — so registration order does not matter. Replication's
`spark-sync` lane is registered exactly this way.

Two consequences worth knowing:

- **Configurators are singletons**, so they may inject singletons and options but not scoped
  services. That is correct rather than a limitation: what a lane declares is process-wide. Anything
  request-shaped belongs in the *handler*, which is scoped and resolved per message.
- **A duplicate lane declaration surfaces on first use**, which in an application is the startup
  validation pass — so it still fails at startup, and still loudly, but not on the line that
  registered it.

Under `Ordered`, a failing head blocks its partition until it succeeds or dead-letters, so a lane's
retry schedule **is** that partition's worst-case downtime. Startup refuses an ordered lane whose
ladder outlasts `MaxPartitionBlock` (15 minutes by default) unless it says
`AcceptPartitionBlock(...)`.

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

Each handler has its own `AttemptCount`. When its lane's retry schedule runs out of rungs, that handler is individually **dead-lettered** while other handlers continue their retry cycles. The message is marked completed only when all handlers have reached a terminal state (completed or dead-lettered).

### Retry with Incremental Backoff

When a handler throws an exception, retries are scheduled with increasing delays:

| Attempt | Delay |
|---|---|
| 1 | 5 seconds |
| 2 | 30 seconds |
| 3 | 2 minutes |
| 4 | 10 minutes |
| 5 | 1 hour |

After the maximum number of attempts (default 5), the handler is **dead-lettered** and the message continues processing remaining handlers. The message completes when all handlers reach a terminal state.

When multiple handlers have different attempt counts, the retry delay is based on the highest `AttemptCount` among failing handlers.

#### How redelivery works

A subscription query cannot evaluate time — `NextAttemptAtUtc <= now()` in a where-clause is evaluated only when a document is written, which is the one moment it cannot be true. That used to require a sweeper patching a `WakeUp` boolean onto due messages so they would match again.

A lane's drain is an **ordinary index query**, which can compare times, so the sweeper, `WakeUp` and `LastWakeUpUtc` are all gone. A pump sleeps until the earliest of its parked retries and its delayed messages, and redelivery granularity is its own timer rather than a fixed 30-second sweep. Measured, a write rings the subscription doorbell in about 11ms, and a server-side patch rings it exactly the same way — which is what the sweeper was relying on, so it bought nothing the write did not already provide.

Redelivery granularity is the lane pump's own timer: it sleeps until the earliest of its parked retries and its delayed messages, so a due message is picked up when it is due rather than at a fixed sweep interval.

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

### Isolation

Messages in different lanes cannot block each other: a failing message in `ValidateBuild` never delays `PersonEvents`.

Within one `Ordered` lane, isolation is per **partition**. A failing head blocks its own partition — that is what ordering across the retry path costs — and every other partition keeps running. A `Concurrent` lane has no partitions, so nothing waits behind a parked message at all.

## Configuration

```csharp
spark.AddMessaging(options =>
{
    options.DefaultRetry = "5s 30s 2m";                  // Lanes that declare no schedule of their own
    options.RetentionDays = 7;                            // Days before terminal messages expire
    options.ProcessingLease = TimeSpan.FromMinutes(30);   // Before the reaper assumes a host died mid-handler
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
| `Status` | `EMessageStatus` | `Pending`, `Processing`, `Completed`, `Failed`, `DeadLettered` |
| `CompletedAtUtc` | `DateTime?` | When the last handler completed |
| `Handlers` | `HandlerExecution[]` | Per-handler execution state (see below) |
| `VisibleAtUtc` | `DateTime?` | When a *delayed broadcast* becomes eligible. Unlike `NextAttemptAtUtc` it does **not** block its partition — a delay is a scheduling instruction, not a dependency |
| `Sequence` | `long` | Broadcast order within a partition. The ordering key — not `CreatedAtUtc`, and emphatically not the document id, which sorts lexicographically |
| `PartitionKey` | `string` | The ordering domain (empty on unordered lanes). Resolved once, producer-side, and never recomputed |

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

## API Reference

### Interfaces (`MintPlayer.Spark.Messaging.Abstractions`)

| Type | Description |
|------|-------------|
| `IMessageBus` | `BroadcastAsync<T>()`, `DelayBroadcastAsync<T>()` |
| `IRecipient<TMessage>` | `HandleAsync(TMessage, CancellationToken)` |
| `ICheckpointRecipient<TMessage>` | Extends `IRecipient<T>` with `HandleAsync(TMessage, string checkpoint, CancellationToken)` for resume-from-checkpoint |
| `IMessageCheckpoint` | `SaveAsync(string)` -- saves progress during handler execution |
| `MessageQueueAttribute` | Assigns a message class to a named queue |
| `NonRetryableException` | Thrown by a recipient to dead-letter its handler immediately, with no retries |

### Extension Methods (`MintPlayer.Spark.Messaging`)

| Method | Description |
|--------|-------------|
| `spark.AddMessaging(Action<SparkMessagingOptions>?)` | Register messaging services and deploy the `SparkMessages/ByQueue` index |

### Source-Generated

| Method | Description |
|--------|-------------|
| `spark.AddRecipients()` | Auto-registers all `IRecipient<T>` implementations in your project |

## Complete Example

See the DemoApp for a working example:

- `../apps/DemoApp.Library/Messages/` -- message definitions with `[MessageQueue]`
- `../apps/DemoApp/Recipients/LogPersonCreated.cs` -- simple `IRecipient<T>` handler
- `../apps/DemoApp/Recipients/LogPersonDeleted.cs` -- simple `IRecipient<T>` handler
- `../apps/DemoApp/Recipients/LogCompanyUpdated.cs` -- demonstrates per-handler retry isolation
- `../apps/DemoApp/Recipients/NotifyEmployeesRecipient.cs` -- `ICheckpointRecipient<T>` with batch progress tracking
- `../apps/DemoApp/Actions/PersonActions.cs` -- broadcasting messages from lifecycle hooks
- `../apps/DemoApp/Actions/CompanyActions.cs` -- broadcasting batch messages with employee IDs
- `../apps/DemoApp/Program.cs` -- service registration

## Requirements

- .NET 10.0+
- RavenDB 6.2+
- An `IDocumentStore` registered in the DI container (provided by `AddSpark()` or registered manually)
- `MintPlayer.Spark.SubscriptionWorker` (referenced automatically)

## License

MIT License

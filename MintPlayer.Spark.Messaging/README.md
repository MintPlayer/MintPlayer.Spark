# MintPlayer.Spark.Messaging

A durable message bus for MintPlayer.Spark with RavenDB persistence, scoped recipients, queue-isolated processing, and automatic retry. Fully opt-in -- the core Spark library remains unchanged.

## Installation

```bash
# For message definitions (in your shared library project)
dotnet add package MintPlayer.Spark.Messaging.Abstractions

# For the full implementation (in your web application project)
dotnet add package MintPlayer.Spark.Messaging
```

## Quick Start

### 1. Define Messages

Messages are plain C# records or classes. Use `[MessageQueue]` to group related messages into a named queue. Messages within the same queue are processed in FIFO order; different queues run independently.

```csharp
using MintPlayer.Spark.Messaging.Abstractions;

[MessageQueue("PersonEvents")]
public record PersonCreatedMessage(string PersonId, string FullName);

[MessageQueue("PersonEvents")]
public record PersonDeletedMessage(string PersonId);
```

Messages without `[MessageQueue]` use their full type name as the queue name (one queue per message type).

### 2. Create Recipients

Recipients handle messages. They are **always instantiated within a DI scope**, so you can inject any scoped service (e.g., `IAsyncDocumentSession`, `IMessageBus`, `ILogger<T>`, or application-specific services).

```csharp
using MintPlayer.Spark.Messaging.Abstractions;

public class SendWelcomeEmail : IRecipient<PersonCreatedMessage>
{
    private readonly IEmailService _emailService;
    private readonly IAsyncDocumentSession _session;

    public SendWelcomeEmail(IEmailService emailService, IAsyncDocumentSession session)
    {
        _emailService = emailService;
        _session = session;
    }

    public async Task HandleAsync(PersonCreatedMessage message, CancellationToken cancellationToken)
    {
        await _emailService.SendWelcomeAsync(message.PersonId, message.FullName);
    }
}
```

A single class can implement `IRecipient<T>` for multiple message types. Multiple recipients can handle the same message type.

### 3. Register Services

```csharp
// Program.cs
builder.Services.AddSparkMessaging();    // Register MessageBus, MessageProcessor, RecipientRegistry
builder.Services.AddSparkRecipients();   // Source-generated: auto-discovers all IRecipient<T> classes

var app = builder.Build();

app.CreateSparkMessagingIndexes();       // Deploy the SparkMessages RavenDB index
```

`AddSparkMessaging()` reuses the `IDocumentStore` singleton already in the DI container. It does not depend on any Spark CRUD types.

`AddSparkRecipients()` is generated at compile time by the `RecipientRegistrationGenerator` source generator. It discovers all `IRecipient<T>` implementations in your project and registers them automatically.

### 4. Broadcast Messages

Inject `IMessageBus` and call `BroadcastAsync` or `DelayBroadcastAsync`:

```csharp
public class PersonActions : DefaultPersistentObjectActions<Person>
{
    private readonly IMessageBus _messageBus;

    public PersonActions(IMessageBus messageBus) => _messageBus = messageBus;

    public override async Task OnAfterSaveAsync(Person entity)
    {
        // Immediate: processed as soon as possible
        await _messageBus.BroadcastAsync(
            new PersonCreatedMessage(entity.Id!, $"{entity.FirstName} {entity.LastName}"));

        // Delayed: stored immediately, processed after 5 seconds
        await _messageBus.DelayBroadcastAsync(
            new SendWelcomeEmailMessage(entity.Id!),
            TimeSpan.FromSeconds(5));
    }
}
```

Both methods store a `SparkMessage` document in RavenDB and return immediately (fire-and-forget).

## How It Works

### Message Processing

The `MessageProcessor` is a `BackgroundService` registered via `AddHostedService`. It:

1. Subscribes to **RavenDB's Changes API** for near-instant detection of new messages
2. Falls back to periodic polling (default 30s) as a safety net
3. Processes each queue **independently and concurrently**
4. Within a queue, processes messages **sequentially in FIFO order**
5. Creates a **DI scope** per message and instantiates recipients via `ActivatorUtilities.CreateInstance`

### Retry with Incremental Backoff

When a recipient throws an exception, the message is retried with increasing delays:

| Attempt | Delay |
|---------|-------|
| 1 | 5 seconds |
| 2 | 30 seconds |
| 3 | 2 minutes |
| 4 | 10 minutes |
| 5 | 1 hour |

After the maximum number of attempts (default 5), the message is **dead-lettered** and the queue continues with the next message.

### Queue Isolation

Messages in different queues cannot block each other. For example, a failing message in the `ValidateBuild` queue will not prevent messages in the `PersonEvents` queue from being processed.

## Configuration

```csharp
builder.Services.AddSparkMessaging(options =>
{
    options.MaxAttempts = 5;                                 // Default: 5
    options.FallbackPollInterval = TimeSpan.FromSeconds(30); // Default: 30s
    options.BackoffDelays = new[]                            // Customizable
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
|-------|------|-------------|
| `Id` | `string` | Document ID (`SparkMessages/{guid}`) |
| `QueueName` | `string` | Queue this message belongs to |
| `MessageType` | `string` | Assembly-qualified CLR type name |
| `PayloadJson` | `string` | JSON-serialized message payload |
| `CreatedAtUtc` | `DateTime` | When the message was broadcast |
| `NextAttemptAtUtc` | `DateTime?` | Earliest retry time (`null` = immediate) |
| `AttemptCount` | `int` | Number of processing attempts |
| `MaxAttempts` | `int` | Maximum attempts before dead-lettering |
| `Status` | `EMessageStatus` | `Pending`, `Processing`, `Completed`, `Failed`, `DeadLettered` |
| `LastError` | `string?` | Exception message from last failure |
| `CompletedAtUtc` | `DateTime?` | When processing completed successfully |

You can query message status directly in RavenDB Studio for observability.

## API Reference

### Interfaces (`MintPlayer.Spark.Messaging.Abstractions`)

| Type | Description |
|------|-------------|
| `IMessageBus` | `BroadcastAsync<T>()`, `DelayBroadcastAsync<T>()` |
| `IRecipient<TMessage>` | `HandleAsync(TMessage, CancellationToken)` |
| `MessageQueueAttribute` | Assigns a message class to a named queue |

### Extension Methods (`MintPlayer.Spark.Messaging`)

| Method | Description |
|--------|-------------|
| `AddSparkMessaging(Action<SparkMessagingOptions>?)` | Register messaging services |
| `AddRecipient<TMessage, TRecipient>()` | Register a single recipient (usually called by generated code) |
| `CreateSparkMessagingIndexes()` | Deploy the `SparkMessages/ByQueue` RavenDB index |

### Source-Generated

| Method | Description |
|--------|-------------|
| `AddSparkRecipients()` | Auto-registers all `IRecipient<T>` implementations in your project |

## Requirements

- .NET 10.0+
- RavenDB 6.2+ (with Changes API support)
- An `IDocumentStore` registered in the DI container (provided by `AddSpark()` or registered manually)

## License

MIT License

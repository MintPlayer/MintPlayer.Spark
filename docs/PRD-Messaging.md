# Product Requirements Document: MintPlayer.Spark Messaging

**Version:** 1.2
**Date:** February 8, 2026
**Status:** Draft

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Problem Statement](#2-problem-statement)
3. [Goals and Objectives](#3-goals-and-objectives)
4. [Core Concepts](#4-core-concepts)
5. [Architecture Overview](#5-architecture-overview)
6. [Functional Requirements](#6-functional-requirements)
7. [Technical Design](#7-technical-design)
8. [Source Generator](#8-source-generator)
9. [Demo Application](#9-demo-application)
10. [Non-Functional Requirements](#10-non-functional-requirements)
11. [Implementation Plan](#11-implementation-plan)

---

## 1. Executive Summary

This document describes a **durable message bus** delivered as **separate class libraries** (`MintPlayer.Spark.Messaging.Abstractions` and `MintPlayer.Spark.Messaging`) that are fully independent from the core `MintPlayer.Spark` library. The core Spark library remains lean and focused on CRUD operations; messaging is an opt-in add-on.

The message bus allows application code to broadcast typed messages (immediately or with a delay) which are persisted in RavenDB and processed asynchronously by **scoped** recipient handlers. Recipients are instantiated within a DI scope, so they can freely inject any scoped service (e.g., `IAsyncDocumentSession`, `IMessageBus`, or application-specific services). Messages are organized into independent queues to prevent unrelated message types from blocking each other. New messages are detected near-instantly via **RavenDB's Changes API** (with a periodic fallback poll as safety net). Failed messages are retried with incremental backoff. A Roslyn source generator automatically discovers recipient classes and generates DI registration code.

---

## 2. Problem Statement

Applications built with Spark currently have no built-in way to perform asynchronous background work triggered by domain events. Developers need a mechanism to:

- Decouple the trigger of an action from its execution (e.g., send an email after a record is saved without blocking the HTTP response)
- Ensure durability: messages must survive application restarts
- Process messages reliably with retry logic for transient failures
- Prevent one failing message category from blocking unrelated work

---

## 3. Goals and Objectives

| Goal | Success Metric |
|------|----------------|
| Durable async messaging | Messages survive app restarts via RavenDB persistence |
| Ordered, queue-isolated processing | Messages within a queue are FIFO; different queues are independent |
| Automatic retry with backoff | Failed messages retry incrementally up to a configurable maximum |
| Minimal boilerplate | Source generator auto-registers all recipients |
| Separate from core Spark | Messaging lives in its own libraries; core Spark stays lean (CRUD only) |
| Opt-in with minimal hook-in | A single `AddSparkMessaging()` call is all that's needed to enable messaging |
| Scoped recipients | Recipients are created in a DI scope, enabling injection of any scoped service |
| Near-instant message detection | RavenDB Changes API triggers processing without polling delay |
| Delayed broadcasting | Messages can be queued immediately but deferred for processing |
| Consistent with Spark patterns | Uses same DI, RavenDB, and source generator conventions as existing framework |

---

## 4. Core Concepts

### Message

A plain C# class or record representing an event or command. Messages are serialized to JSON and stored in RavenDB.

```csharp
[MessageQueue("PersonEvents")]
public record PersonCreatedMessage(string PersonId, string FullName);
```

### Queue

A named channel that provides ordering isolation. Messages within the same queue are processed in FIFO order. Different queues are processed independently and concurrently. A message type's queue is determined by the `[MessageQueue]` attribute. Messages without this attribute use their full type name as the queue name (effectively one queue per message type).

### Recipient

A class that handles a specific message type. Implements `IRecipient<TMessage>`. **Recipients are always instantiated within a DI scope** using `ActivatorUtilities.CreateInstance()`, which means they can inject any scoped service via their constructor -- for example, `IAsyncDocumentSession`, `IMessageBus` (to chain messages), `ILogger<T>`, or any application-specific scoped service.

```csharp
public class SendWelcomeEmail : IRecipient<PersonCreatedMessage>
{
    private readonly IEmailService _emailService;
    private readonly IAsyncDocumentSession _session; // Scoped service injection works

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

### Message Bus

The entry point for publishing messages. Two methods are available:

- `BroadcastAsync<TMessage>(message)` -- stores the message for immediate processing
- `DelayBroadcastAsync<TMessage>(message, delay)` -- stores the message but defers processing by the specified duration (e.g., `TimeSpan.FromMilliseconds(500)` or `TimeSpan.FromSeconds(30)`)

Both methods serialize the message and store it as a `SparkMessage` document in RavenDB. The delayed variant sets `NextAttemptAtUtc` to `UtcNow + delay`.

### Message Processor

A `BackgroundService` (registered via `AddHostedService`) that detects and processes pending messages. It uses **RavenDB's Changes API** to react near-instantly when new `SparkMessage` documents are created or updated, with a periodic fallback poll as a safety net. It processes each queue independently and concurrently.

---

## 5. Architecture Overview

```
Application Code
    |
    |-- BroadcastAsync(message)        --> NextAttemptAtUtc = null (immediate)
    |-- DelayBroadcastAsync(msg, 5s)   --> NextAttemptAtUtc = UtcNow + 5s
    |
    v
MessageBus (stores SparkMessage document to RavenDB)
    |
    v
SparkMessages collection (RavenDB)
    |
    |--- RavenDB Changes API -----> notifies MessageProcessor (near-instant)
    |--- Fallback poll (30s) ------> safety net for missed notifications
    |
    v
MessageProcessor (BackgroundService via AddHostedService)
    |
    +---> Queue "PersonEvents"  (sequential FIFO)
    |       |-> SparkMessage #1 -> Scoped DI -> IRecipient<PersonCreatedMessage> handlers
    |       |-> SparkMessage #2 -> (waits for #1 to complete)
    |
    +---> Queue "BuildValidation" (sequential FIFO, independent from above)
    |       |-> SparkMessage #3 -> Scoped DI -> IRecipient<ValidateBuildMessage> handlers
    |
    +---> Queue "..." (each queue runs independently)
```

---

## 6. Functional Requirements

### FR-1: Message Broadcasting

`IMessageBus` exposes two methods:

- `Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)` -- immediate broadcast
- `Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)` -- delayed broadcast

Both methods:
1. Serialize the message to JSON
2. Determine the queue name (from `[MessageQueue]` attribute or type name fallback)
3. Store a `SparkMessage` document in RavenDB with status `Pending`
4. Return as soon as the document is stored (fire-and-forget from the caller's perspective)

The delayed variant additionally sets `NextAttemptAtUtc = DateTime.UtcNow + delay`. The processor will not attempt this message until that time has passed. This is useful for scenarios like "wait 5 seconds for an external API to sync before processing".

### FR-2: Message Persistence (RavenDB Document)

The `SparkMessage` document contains:

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` | RavenDB document ID (`SparkMessages/{guid}`) |
| `QueueName` | `string` | Queue this message belongs to |
| `MessageType` | `string` | Assembly-qualified CLR type name for deserialization |
| `PayloadJson` | `string` | JSON-serialized message payload |
| `CreatedAtUtc` | `DateTime` | When the message was broadcast |
| `NextAttemptAtUtc` | `DateTime?` | Earliest time the processor should attempt this message (null = immediately) |
| `AttemptCount` | `int` | Number of processing attempts so far |
| `MaxAttempts` | `int` | Maximum attempts before dead-lettering (from config) |
| `Status` | `EMessageStatus` | `Pending`, `Processing`, `Completed`, `Failed`, `DeadLettered` |
| `LastError` | `string?` | Exception message from the last failed attempt |
| `CompletedAtUtc` | `DateTime?` | When the message was successfully processed |

### FR-3: Queue-Isolated Processing

- The `MessageProcessor` discovers all distinct queue names with pending/failed messages
- Each queue is processed independently (concurrent across queues)
- Within a queue, messages are processed sequentially in `CreatedAtUtc` order (FIFO)
- A failed message in one queue does not block processing in other queues

### FR-4: Message Detection via RavenDB Changes API

The `MessageProcessor` uses **RavenDB's Changes API** to detect new or updated `SparkMessage` documents:

1. On startup, the processor subscribes to document changes on the `SparkMessages` collection via `documentStore.Changes().ForDocumentsInCollection<SparkMessage>()`
2. When a notification arrives (new message broadcast, or failed message updated for retry), the processor triggers a processing cycle for affected queues
3. A **fallback periodic poll** (default every 30 seconds, configurable) runs as a safety net to catch any notifications that may have been missed (e.g., during a brief connection interruption)
4. The processor is registered via `AddHostedService<MessageProcessor>()`, giving it standard .NET managed lifecycle (starts/stops with the application, supports graceful shutdown via cancellation token)

**Why Changes API instead of pure polling?** Near-zero latency for new messages (no polling delay), no wasted queries when the queue is empty, and lower load on RavenDB compared to frequent polling.

**Why not RavenDB Data Subscriptions?** Data Subscriptions process documents in a single serial stream, which conflicts with the requirement for parallel-across-queues processing. The Changes API provides notifications without imposing ordering constraints, leaving queue isolation logic to the processor.

### FR-5: Scoped Message Dispatch to Recipients

When processing a message:

1. Deserialize `PayloadJson` to the CLR type identified by `MessageType`
2. Look up all registered recipient types for that message type (from `RecipientRegistry`)
3. **Create a DI scope** for this message (all recipients for the same message share the scope)
4. For each recipient type, use `ActivatorUtilities.CreateInstance(scope.ServiceProvider, recipientType)` to instantiate the handler
5. Call `HandleAsync(message, cancellationToken)` on each recipient
6. Dispose the scope (cleans up scoped services like document sessions)
7. If all recipients succeed: mark message as `Completed`
8. If any recipient throws: mark message as `Failed` and schedule retry

**Why scoped?** Because recipients are created within a DI scope, they can inject any scoped service via their constructor. This is essential for real-world handlers that need database access (`IAsyncDocumentSession`), the message bus itself (`IMessageBus` for chaining), or any application-specific scoped service. Each message gets a fresh scope, ensuring isolation between message processing runs.

### FR-6: Retry with Incremental Backoff

When a handler throws an exception:

1. Increment `AttemptCount`
2. Store the exception message in `LastError`
3. If `AttemptCount < MaxAttempts`: set `Status = Failed`, compute `NextAttemptAtUtc` using backoff schedule, save
4. If `AttemptCount >= MaxAttempts`: set `Status = DeadLettered`, save. The queue continues with the next message.

**Default backoff schedule:**

| Attempt | Delay before retry |
|---------|-------------------|
| 1 | 5 seconds |
| 2 | 30 seconds |
| 3 | 2 minutes |
| 4 | 10 minutes |
| 5 | 1 hour |

The backoff schedule is configurable. The default `MaxAttempts` is 5.

### FR-7: Recipient Registration

- `IServiceCollection.AddRecipient<TMessage, TRecipient>()` registers a mapping from message type to recipient type in a singleton `RecipientRegistry`
- Multiple recipients can be registered for the same message type
- A single class can implement `IRecipient<T>` for multiple message types

### FR-8: Source Generator

- A Roslyn incremental source generator discovers all non-abstract classes implementing `IRecipient<TMessage>` in the consuming project
- Generates an `AddSparkRecipients()` extension method on `IServiceCollection`
- The generated method calls `AddRecipient<TMessage, TRecipient>()` for each discovered recipient
- Follows the same patterns as the existing `ActionsRegistrationGenerator`

---

## 7. Technical Design

### 7.1 Project Layout

Messaging is delivered as **two new class libraries**, keeping the existing Spark projects untouched. The source generator additions go into the existing `MintPlayer.Spark.SourceGenerators` project.

```
MintPlayer.Spark.sln
├── MintPlayer.Spark                          (unchanged - CRUD only)
├── MintPlayer.Spark.Abstractions             (unchanged)
├── MintPlayer.Spark.Messaging.Abstractions   (NEW - interfaces & attributes)
├── MintPlayer.Spark.Messaging                (NEW - implementation)
├── MintPlayer.Spark.SourceGenerators         (extended - new recipient generator)
├── Demo/DemoApp                              (extended - sample recipients)
└── Demo/DemoApp.Library                      (extended - sample messages)
```

| Project | Contents | Dependencies |
|---------|----------|-------------|
| `MintPlayer.Spark.Messaging.Abstractions` (NEW) | `IMessageBus`, `IRecipient<TMessage>`, `MessageQueueAttribute` | None (standalone, no Spark dependency) |
| `MintPlayer.Spark.Messaging` (NEW) | `MessageBus`, `SparkMessage`, `EMessageStatus`, `MessageProcessor`, `RecipientRegistry`, `SparkMessagingOptions`, extension methods | `MintPlayer.Spark.Messaging.Abstractions`, `RavenDB.Client`, `Microsoft.Extensions.Hosting` |
| `MintPlayer.Spark.SourceGenerators` (extended) | `RecipientRegistrationGenerator`, `RecipientRegistrationProducer`, `RecipientClassInfo` | (existing dependencies) |
| `Demo/DemoApp.Library` (extended) | Sample message classes | `MintPlayer.Spark.Messaging.Abstractions` |
| `Demo/DemoApp` (extended) | Sample recipients, registration in `Program.cs` | `MintPlayer.Spark.Messaging` |

**Key design principle:** `MintPlayer.Spark.Messaging.Abstractions` has **zero dependency** on `MintPlayer.Spark.Abstractions` or `MintPlayer.Spark`. The messaging system reuses the same `IDocumentStore` that Spark already registers, but does not depend on any Spark-specific types. This means messaging could theoretically be used independently of the Spark CRUD framework.

### 7.2 Messaging Abstractions (`MintPlayer.Spark.Messaging.Abstractions`)

A minimal, dependency-free library containing only the public contracts:

```csharp
// Interfaces
public interface IMessageBus
{
    Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default);
    Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default);
}

public interface IRecipient<in TMessage>
{
    Task HandleAsync(TMessage message, CancellationToken cancellationToken = default);
}

// Attribute
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class MessageQueueAttribute : Attribute
{
    public string QueueName { get; }
    public MessageQueueAttribute(string queueName) => QueueName = queueName;
}
```

### 7.3 Messaging Implementation (`MintPlayer.Spark.Messaging`)

#### SparkMessage Document

```csharp
public class SparkMessage
{
    public string? Id { get; set; }
    public string QueueName { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; }
    public EMessageStatus Status { get; set; }
    public string? LastError { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public enum EMessageStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
    DeadLettered
}
```

#### RecipientRegistry

```csharp
public class RecipientRegistry
{
    private readonly Dictionary<Type, List<Type>> _mappings = new();

    public void Register(Type messageType, Type recipientType) { ... }
    public IReadOnlyList<Type> GetRecipientTypes(Type messageType) { ... }
}
```

#### MessageBus

```csharp
internal class MessageBus : IMessageBus
{
    private readonly IDocumentStore _documentStore;
    private readonly SparkMessagingOptions _options;

    public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        => StoreMessageAsync(message, delay: null, cancellationToken);

    public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
        => StoreMessageAsync(message, delay, cancellationToken);

    private async Task StoreMessageAsync<TMessage>(TMessage message, TimeSpan? delay, CancellationToken cancellationToken)
    {
        // 1. Determine queue name from [MessageQueue] attribute or type name
        // 2. Serialize message to JSON (System.Text.Json)
        // 3. Create SparkMessage document with Status = Pending
        //    - If delay != null: set NextAttemptAtUtc = DateTime.UtcNow + delay
        //    - If delay == null: NextAttemptAtUtc = null (process immediately)
        // 4. Store in RavenDB via a new session
    }
}
```

#### MessageProcessor (BackgroundService)

Registered via `AddHostedService<MessageProcessor>()`. The processor creates a **DI scope for each message**, ensuring recipients get fresh scoped services. This is critical: recipients can inject `IAsyncDocumentSession`, `IMessageBus`, or any scoped application service via their constructor.

```csharp
internal class MessageProcessor : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IDocumentStore _documentStore;
    private readonly RecipientRegistry _recipientRegistry;
    private readonly SparkMessagingOptions _options;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe to RavenDB Changes API for SparkMessages collection
        var changes = _documentStore.Changes();
        await changes.EnsureConnectedNow();
        var subscription = changes
            .ForDocumentsInCollection<SparkMessage>()
            .Subscribe(change => Signal processing cycle);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for either:
            //   a. Changes API notification (near-instant new message detection)
            //   b. Fallback poll timer (default 30s, safety net)
            await WaitForSignalOrTimeout(stoppingToken);

            // 1. Query distinct queue names with actionable messages
            //    (Status == Pending && NextAttemptAtUtc is null or <= now)
            //    OR (Status == Failed && NextAttemptAtUtc <= now)
            // 2. For each queue (concurrently), process the oldest actionable message:
            //    a. Create a DI scope
            //    b. For each recipient type registered for this message type:
            //       - ActivatorUtilities.CreateInstance(scope.ServiceProvider, recipientType)
            //       - Call HandleAsync(message, cancellationToken)
            //    c. All recipients share the same scope (same session, etc.)
            //    d. Dispose scope after all recipients complete
            // 3. Update message status (Completed or Failed with retry)
        }

        subscription.Dispose();
    }
}
```

#### SparkMessagingOptions

```csharp
public class SparkMessagingOptions
{
    public int MaxAttempts { get; set; } = 5;
    public TimeSpan FallbackPollInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan[] BackoffDelays { get; set; } = new[]
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1)
    };
}
```

#### Extension Methods

```csharp
public static class SparkMessagingExtensions
{
    // Register the messaging infrastructure (MessageBus, MessageProcessor, RecipientRegistry)
    public static IServiceCollection AddSparkMessaging(
        this IServiceCollection services,
        Action<SparkMessagingOptions>? configure = null)
    { ... }

    // Register a single recipient mapping
    public static IServiceCollection AddRecipient<TMessage, TRecipient>(
        this IServiceCollection services)
        where TRecipient : class, IRecipient<TMessage>
    { ... }
}
```

#### RavenDB Index

```csharp
public class SparkMessages_ByQueue : AbstractIndexCreationTask<SparkMessage>
{
    public SparkMessages_ByQueue()
    {
        Map = messages => from msg in messages
            select new
            {
                msg.QueueName,
                msg.Status,
                msg.NextAttemptAtUtc,
                msg.CreatedAtUtc
            };
    }
}
```

This index is deployed by `AddSparkMessaging()` during startup (or via a separate `CreateSparkMessagingIndexes()` call on the application builder).

### 7.4 Registration Flow

In `Program.cs`, messaging is opt-in with minimal hook-in:

```csharp
// Existing Spark setup (unchanged)
builder.Services.AddSpark(builder.Configuration);
builder.Services.AddScoped<SparkContext, DemoSparkContext>();
builder.Services.AddSparkActions();

// Messaging opt-in (2 lines)
builder.Services.AddSparkMessaging();    // Register MessageBus, MessageProcessor, RecipientRegistry
builder.Services.AddSparkRecipients();   // Source-generated: registers all recipients
```

`AddSparkMessaging()` reuses the `IDocumentStore` singleton that `AddSpark()` already registers. It does not require any Spark-specific types -- only the RavenDB `IDocumentStore` from the DI container.

---

## 8. Source Generator

### 8.1 Generator: `RecipientRegistrationGenerator`

Follows the exact same pattern as `ActionsRegistrationGenerator`:

1. **Predicate:** Find `ClassDeclarationSyntax` nodes with base types/interfaces
2. **Transform:** For each class, check if it implements `IRecipient<T>` (from `MintPlayer.Spark.Messaging.Abstractions`)
3. **Extract:** For each `IRecipient<TMessage>` interface, capture the recipient type name and message type name
4. **Guard:** Only generate if the project references `MintPlayer.Spark.Messaging`
5. **Produce:** Pass collected `RecipientClassInfo` items to the producer

### 8.2 Producer: `RecipientRegistrationProducer`

Generates `SparkRecipientRegistrations.g.cs`:

```csharp
// <auto-generated />
using Microsoft.Extensions.DependencyInjection;

namespace DemoApp
{
    internal static class SparkRecipientsExtensions
    {
        internal static IServiceCollection AddSparkRecipients(
            this IServiceCollection services)
        {
            global::MintPlayer.Spark.Messaging.SparkMessagingExtensions
                .AddRecipient<global::DemoApp.Library.Messages.PersonCreatedMessage,
                              global::DemoApp.Recipients.SendWelcomeEmail>(services);
            global::MintPlayer.Spark.Messaging.SparkMessagingExtensions
                .AddRecipient<global::DemoApp.Library.Messages.PersonCreatedMessage,
                              global::DemoApp.Recipients.NotifyAdmins>(services);
            // ... one line per IRecipient<T> implementation
            return services;
        }
    }
}
```

### 8.3 Data Model

```csharp
public class RecipientClassInfo
{
    public string RecipientTypeName { get; set; } = string.Empty;
    public string MessageTypeName { get; set; } = string.Empty;
}
```

A single class implementing `IRecipient<A>` and `IRecipient<B>` produces two `RecipientClassInfo` entries.

---

## 9. Demo Application

### 9.1 Sample Messages (`DemoApp.Library`)

```csharp
[MessageQueue("PersonEvents")]
public record PersonCreatedMessage(string PersonId, string FullName);

[MessageQueue("PersonEvents")]
public record PersonDeletedMessage(string PersonId);
```

### 9.2 Sample Recipients (`DemoApp`)

```csharp
public class LogPersonCreated : IRecipient<PersonCreatedMessage>
{
    private readonly ILogger<LogPersonCreated> _logger;

    public LogPersonCreated(ILogger<LogPersonCreated> logger) => _logger = logger;

    public Task HandleAsync(PersonCreatedMessage message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Person created: {FullName} ({PersonId})", message.FullName, message.PersonId);
        return Task.CompletedTask;
    }
}

public class LogPersonDeleted : IRecipient<PersonDeletedMessage>
{
    private readonly ILogger<LogPersonDeleted> _logger;

    public LogPersonDeleted(ILogger<LogPersonDeleted> logger) => _logger = logger;

    public Task HandleAsync(PersonDeletedMessage message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Person deleted: {PersonId}", message.PersonId);
        return Task.CompletedTask;
    }
}
```

### 9.3 Integration with Actions

Broadcast messages from entity Actions hooks (immediate and delayed):

```csharp
public class PersonActions : DefaultPersistentObjectActions<Person>
{
    private readonly IMessageBus _messageBus;

    public PersonActions(IMessageBus messageBus) => _messageBus = messageBus;

    public override async Task OnAfterSaveAsync(Person entity)
    {
        // Immediate: log the creation right away
        await _messageBus.BroadcastAsync(
            new PersonCreatedMessage(entity.Id!, $"{entity.FirstName} {entity.LastName}"));

        // Delayed: send welcome email after 5 seconds (give external systems time to sync)
        await _messageBus.DelayBroadcastAsync(
            new SendWelcomeEmailMessage(entity.Id!, entity.Email!),
            TimeSpan.FromSeconds(5));
    }

    public override async Task OnBeforeDeleteAsync(Person entity)
    {
        await _messageBus.BroadcastAsync(new PersonDeletedMessage(entity.Id!));
    }
}
```

### 9.4 Program.cs Registration

```csharp
// Existing Spark setup (unchanged)
builder.Services.AddSpark(builder.Configuration);
builder.Services.AddScoped<SparkContext, DemoSparkContext>();
builder.Services.AddSparkActions();

// Messaging opt-in
builder.Services.AddSparkMessaging();    // From MintPlayer.Spark.Messaging
builder.Services.AddSparkRecipients();   // Source-generated
```

---

## 10. Non-Functional Requirements

| Requirement | Target |
|-------------|--------|
| Message delivery guarantee | At-least-once (handlers must be idempotent) |
| Processing latency | Near-instant via Changes API; < 30s worst-case via fallback poll |
| Throughput | Suitable for moderate volumes; not a high-throughput event streaming system |
| Durability | Messages survive application restarts (RavenDB storage) |
| Observability | Message status queryable via RavenDB Studio; logging in MessageProcessor |

---

## 11. Implementation Plan

### Phase 1: New Projects & Abstractions

1. Create `MintPlayer.Spark.Messaging.Abstractions` class library project (net10.0, no dependencies)
2. Add `IMessageBus` interface
3. Add `IRecipient<TMessage>` interface
4. Add `MessageQueueAttribute`
5. Add projects to solution

### Phase 2: Core Messaging Implementation

6. Create `MintPlayer.Spark.Messaging` class library project (net10.0, depends on abstractions + RavenDB.Client + Microsoft.Extensions.Hosting)
7. Add `SparkMessage` document model and `EMessageStatus` enum
8. Add `RecipientRegistry` singleton
9. Add `SparkMessagingOptions` configuration class
10. Add `SparkMessagingExtensions` with `AddSparkMessaging()` and `AddRecipient<,>()`
11. Implement `MessageBus` (stores messages to RavenDB)
12. Implement `MessageProcessor` background service (scoped recipient instantiation, polls, dispatches, retries)
13. Add `SparkMessages_ByQueue` RavenDB index

### Phase 3: Source Generator

14. Add `RecipientClassInfo` model to `MintPlayer.Spark.SourceGenerators`
15. Add `RecipientRegistrationGenerator` (discovers `IRecipient<T>` implementations)
16. Add `RecipientRegistrationProducer` (generates `AddSparkRecipients()`)

### Phase 4: Demo

17. Add `MintPlayer.Spark.Messaging.Abstractions` reference to `DemoApp.Library`
18. Add sample message classes to `DemoApp.Library`
19. Add `MintPlayer.Spark.Messaging` reference to `DemoApp`
20. Add sample recipient classes to `DemoApp`
21. Update `PersonActions` to broadcast messages on save/delete
22. Update `Program.cs` with `AddSparkMessaging()` and `AddSparkRecipients()`

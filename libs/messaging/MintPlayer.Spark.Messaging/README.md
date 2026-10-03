# MintPlayer.Spark.Messaging

A durable message bus for MintPlayer.Spark with RavenDB persistence, scoped recipients, queue-isolated processing, and automatic retry. Fully opt-in -- the core Spark library remains unchanged.

Messages are persisted as documents, processed via RavenDB data subscriptions, and retried automatically on failure. Different queues run independently so a failing message in one queue never blocks another.

## Overview

The messaging system is split into two packages:

| Package | Purpose | Used by |
|---|---|---|
| `MintPlayer.Spark.Messaging.Abstractions` | Interfaces and attributes (`IMessageBus`, `IRecipient<T>`, `[MessageQueue]`) | Shared library projects that define messages |
| `MintPlayer.Spark.Messaging` | Implementation (message storage, subscription workers, retry logic) | Web application projects |

Messages are plain C# records or classes. Recipients are DI-scoped services that handle messages. The framework connects the two through named queues.

## Installation

```bash
# For message definitions (in your shared library project)
dotnet add package MintPlayer.Spark.Messaging.Abstractions

# For the full implementation (in your web application project)
dotnet add package MintPlayer.Spark.Messaging
```

## Quick Start

### 1. Define Messages

Create message classes in a shared library project so both the sender and recipients can reference them. Messages are plain C# records or classes. Use `[MessageQueue]` to group related messages into a named queue. Messages within the same queue are processed in FIFO order; different queues run independently.

```csharp
using MintPlayer.Spark.Messaging.Abstractions;

[MessageQueue("PersonEvents")]
public record PersonCreatedMessage(string PersonId, string FullName);

[MessageQueue("PersonEvents")]
public record PersonDeletedMessage(string PersonId);
```

Both message types above share the `PersonEvents` queue, which means they are processed in FIFO order within that queue.

Messages without `[MessageQueue]` use their full type name as the queue name (one queue per message type).

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
    spark.AddMessaging();     // MessageBus, MessageProcessor, RecipientRegistry
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

public partial class PersonActions : DefaultPersistentObjectActions<Person>, IAfterSave<Person>, IAfterDelete<Person>
{
    [Inject] private readonly IMessageBus messageBus;

    // After the commit (persistence hooks, #482): never announce a write that may still be refused.
    public async ValueTask OnAfterSaveAsync(Person entity, SaveContext context)
    {
        // Immediate: processed as soon as possible
        await messageBus.BroadcastAsync(
            new PersonCreatedMessage(entity.Id!, $"{entity.FirstName} {entity.LastName}"));
    }

    public async ValueTask OnAfterDeleteAsync(Person entity, DeleteContext context)
    {
        await messageBus.BroadcastAsync(new PersonDeletedMessage(entity.Id!));
    }
}
```

Both `BroadcastAsync` and `DelayBroadcastAsync` store a `SparkMessage` document in RavenDB and return immediately (fire-and-forget). The subscription workers pick them up asynchronously.

#### Delayed Messages

To schedule a message for later processing, use `DelayBroadcastAsync`:

```csharp
await messageBus.DelayBroadcastAsync(
    new SendReminderMessage(entity.Id!),
    TimeSpan.FromMinutes(30));
```

The message is stored immediately but the subscription worker will not pick it up until the delay has elapsed.

#### Broadcast At Most Once

When the same logical event can arrive more than once, pass a stable key and it will be enqueued
only once:

```csharp
await messageBus.BroadcastOnceAsync(envelope, deliveryId);
```

The key becomes part of the message's document id, so uniqueness is enforced by the database rather
than by a query, and it holds regardless of which host receives the duplicate. Spark's GitHub
webhook processor uses this with `X-GitHub-Delivery`: GitHub re-sends a delivery automatically after
a `5xx` and manually from the repository's webhook UI, and the id is stable across those attempts, so
without a key one retried delivery runs every recipient twice.

Note that this deduplicates *enqueueing*, not handling. A duplicate arriving after the original has
been processed and expired by retention is a new message again — the correct trade-off for a
retention window measured in days.

The id is `SparkMessages/{readable}.{hash}`: the readable part is the key with every character other
than ASCII letters, digits, `-` and `_` replaced by `_` (at most 64 characters), and the hash is the
first 16 bytes of SHA-256 over the **versionless message type** and the **exact key**. So two message
types may use the same key, and keys that differ only in a character an id cannot hold (`a:b`,
`a/b`, `a_b`) or only in letter case (RavenDB ids are case-insensitive) are distinct. Before #460 the
id was `SparkMessages/{sanitized key}`, and all of those collided — the second publish was dropped
silently.

> **Upgrade note.** Messages enqueued before this version keep their old ids. A duplicate of such a
> message published *after* the upgrade gets a new-format id and is therefore enqueued once more.
> The window is the retention period of the old documents (7 days by default); for webhook
> redeliveries it only matters if GitHub re-sends a pre-upgrade delivery after the deploy.

#### Broadcast options

Every publish method is shorthand for `BroadcastAsync(message, BroadcastOptions)`:

```csharp
await messageBus.BroadcastAsync(new PasswordResetMail(userId, token), new BroadcastOptions
{
    DeduplicationKey = $"reset:{userId}:{tokenId}", // at most once (hashed, type-namespaced)
    Delay = TimeSpan.FromSeconds(10),               // not before
    MaxAttempts = 8,                                // overrides the queue's and the global value
    ExpiresAtUtc = tokenExpiresAtUtc,               // never handle it after this; dead-letter as Expired
    Queue = "mail-transactional",                   // must be declared under Spark:Messaging:Queues
    ScrubPayloadOnTerminal = true,                  // clear PayloadJson once Completed/DeadLettered
});
```

`ExpiresAtUtc` is checked at pickup, against the throttle slot, and before every retry: a message
whose next retry or slot would fall after it is dead-lettered with `DeadLetterReason = Expired`
rather than handled late.

`Queue` overrides the queue the message type declares — but only onto a queue declared in
`SparkMessagingOptions.Queues`; anything else throws at publish. An undeclared name would produce
documents no worker ever selects, whereas a declared queue is known to both sides (in
`SubscriptionPerQueue` mode it gets its own worker).

#### Publishing inside your own transaction (outbox)

`BroadcastAsync` saves the message in a session of its own, so "the data changed" and "the follow-up
is queued" are two commits, and a crash between them loses one. `IMessageOutbox.EnqueueAsync` stores
the message in **your** session instead; it commits with your `SaveChangesAsync`, or not at all:

```csharp
await session.StoreAsync(order);
await outbox.EnqueueAsync(session, new OrderPlaced(order.Id!), new BroadcastOptions { Delay = TimeSpan.FromMinutes(1) });
await session.SaveChangesAsync(); // the order and its message, together
```

It only stores, under a fresh id, and leaves the session's settings alone. Every option applies except
`DeduplicationKey`, which is refused: deduplication needs optimistic concurrency on the whole session,
and a duplicate would then roll back your own write.

Spark's durable after-commit hooks (`IAfterSaveCommitted` / `IAfterDeleteCommitted`, see the
[hooks guide](../../../docs/guide-hooks.md#5a-durable-after-commit-hooks)) are built on it:
`AddMessaging()` registers the outbox the framework writes them through, and the recipient that runs
them. A durable hook registered without `AddMessaging()` is a startup error.

## How It Works

### Message Processing

Internally the messaging library uses **one RavenDB data subscription for every queue** (via
`MintPlayer.Spark.SubscriptionWorker`), with per-queue ordering provided by in-process lanes:

1. At startup, `MessageSubscriptionManager` prunes any legacy `SparkMessaging-*` definitions, then
   discovers all queue names from registered `IRecipient<T>` types
2. It competes for a cluster-wide **messaging lease**; only the holder feeds
3. The holder runs a single `MessageFeeder` on the subscription `SparkMessaging`, whose query has
   **no `QueueName` predicate**, in batches of up to `FeederBatchSize`
4. On each batch the feeder merges it with the top of the sorted `SparkMessages_ByPriority` index and
   **claims** those messages highest priority first — `Status = Processing`, `OwnerId`,
   `ClaimExpiresAtUtc`, saved under optimistic concurrency *before* the batch is acknowledged — and
   routes each to a lane keyed by queue name (see *Priority lanes*)
5. Each lane is drained by one **pump** with at most one message in flight, so within a queue
   messages are processed **one at a time in FIFO order**
6. Lanes are independent tasks, so different queues are processed **concurrently and independently**
7. Each message is dispatched within a fresh **DI scope**, so recipients get fresh scoped services

> **Why one subscription.** RavenDB caps data subscriptions per database — three on a Community
> licence — and the previous design spent one per distinct queue name. That made "how many queues may
> this application have?" a licensing question, and exceeding the cap failed *silently*: the create
> was refused, the worker started against a subscription that did not exist, died as
> "non-recoverable", and the process stayed up looking healthy with a dead queue. Seven definitions
> accumulated in one production database and five were doing nothing. Queue names are a modelling
> decision, and this makes them free again.

Set `SubscriptionMode = SubscriptionPerQueue` to get the old model back — one subscription per queue,
one slot per queue — which is worth it only where the licence has headroom and server-side isolation
is genuinely wanted. Both modes share `MessageProcessor`, so the per-message contract is identical,
and both run a claimed message under the same guards — claim renewal every `ClaimRenewInterval` and
cancellation at `HandlerTimeout`. (Before #460 the per-queue worker had neither: a handler slower than
`ClaimTtl` was reclaimed underneath itself and ran twice, and a hung handler held its queue for ever.)
Queues declared in `SparkMessagingOptions.Queues` get a worker in this mode too.

### Crash recovery

A claim is the durable record that some process is working on a message. If a host dies between
pickup and completion, the message stays at `Processing` with a lapsing `ClaimExpiresAtUtc`, and
`MessageRetrySweeper` returns it to `Pending` with `AttemptCount` incremented so it is delivered
again.

That reader is new, and its absence was a real bug: `Processing` used to be written on pickup and
consulted by **nothing** — not the subscription query, not the sweeper — so a message interrupted
mid-handler was stranded permanently with no retry, no dead-letter and no log line. For webhook
traffic that meant a delivery accepted with a `200` and then silently dropped.

Because a claim lapses by wall clock, `ClaimTtl` must exceed your slowest handler *and* the
container's `terminationGracePeriodSeconds`. Claims are renewed every `ClaimRenewInterval` while a
handler runs, so a legitimately slow handler is not reclaimed underneath itself; the TTL bounds how
long an *abandoned* message waits, not how long a handler may take. Kubernetes' default 30-second
grace period is **not** enough for long handlers such as coverage report parsing — a pod stopped
mid-handler would be killed before it could drain.

On a **graceful** stop the feeder stops, the lanes drain, and only then is the lease released, so a
standby takes over on its next poll (≤ 5 s) with no duplicate. A batch that is still being claimed when
the lanes close releases the claims it could not route (`Pending`, `WakeUp`, the attempt not counted),
so they are served by the next leader at once rather than after `ClaimTtl`. After a **crash** the
standby takes over when the lease lapses (30 s), and the dead host's claims come back when they expire;
a handler that finished on the dead host without recording it runs again — at most one per lane,
which is the at-least-once window (measured in the #460 PRD §4.1, S-M8).

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

After the maximum number of attempts (default 5), the handler is **dead-lettered** and the message continues processing remaining handlers. The message completes when all handlers reach a terminal state.

When multiple handlers have different attempt counts, the retry delay is based on the highest `AttemptCount` among failing handlers.

#### How redelivery works

RavenDB subscriptions re-evaluate a document only when it is *written* — time passing does not re-run the subscription query, and the query itself cannot evaluate time (a `NextAttemptAtUtc <= now()` where-clause silently never matches). So a background sweeper does the time evaluation: every `FallbackPollInterval` (default 30s) it finds messages whose `NextAttemptAtUtc` has passed and patches `WakeUp = true` onto them. That write both makes the message match the subscription query again and triggers the re-evaluation that delivers it. The worker clears `WakeUp` on pickup, so a message parked for another backoff round waits for the sweeper again.

`FallbackPollInterval` is therefore the redelivery granularity: a due message is picked up at most that long after its scheduled retry time (or delayed-broadcast due time).

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

Messages in different queues cannot block each other. For example, a failing message in the `ValidateBuild` queue will not prevent messages in the `PersonEvents` queue from being processed.

## Configuration

```csharp
spark.AddMessaging(options =>
{
    options.MaxAttempts = 5;                                  // Default: 5
    options.FallbackPollInterval = TimeSpan.FromSeconds(30);  // Redelivery sweep granularity. Default: 30s
    options.RetentionDays = 7;                                // Days before completed/dead-lettered messages expire
    options.BackoffDelays = new[]                              // Customizable retry delays
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
    };
    options.HandlerTimeout = TimeSpan.FromMinutes(10);        // Cancels a hung handler (both subscription modes)
});
```

### Per-queue options and throttling

`SparkMessagingOptions.Queues` holds a `SparkQueueOptions` per queue name, bound from
`Spark:Messaging:Queues:{name}`:

| Property | Meaning |
|---|---|
| `MaxPerInterval` + `Interval` | Start rate N per interval — one every `Interval / N` — with a burst of N for an idle queue (GCRA). A rate, not a hard window cap: a burst plus the steady rate can put ~2N starts in one sliding interval. For no burst, state the rate alone (`1` per `3s` rather than `20` per minute). 0 = unlimited. |
| `BatchSize` + `MinDelayBetweenBatches` | Start at most N back to back, then pause. A batch is a pacing window only; each message keeps its own status and retries. |
| `MaxConcurrency` | Messages of this queue handled at once (default 1 = FIFO). `SingleSubscription` mode only; warned about and ignored in `SubscriptionPerQueue` mode. |
| `MaxAttempts` | Attempts per handler for messages published to this queue. |
| `Backoff` | Retry schedule for this queue (empty = the global `BackoffDelays`). |
| `Priority` | `Low`, `Normal` (default) or `High` (or `-1`/`0`/`1`): the order in which the single feeder serves queues, strictly, across the whole backlog — see *Priority lanes* below. Stamped on each message at publish. `SingleSubscription` mode only. |

`SparkMessagingOptions.FeederBatchSize` (default 256, `Spark:Messaging:FeederBatchSize`) is the
feeder's page: how many messages it reads from the sorted index per wake-up, and how many one
subscription batch may hold.

```json
"Spark": {
  "Messaging": {
    "Queues": {
      "mail-transactional": { "MaxPerInterval": 600, "Backoff": [ "00:00:30", "00:05:00", "00:30:00", "02:00:00" ] },
      "mail-bulk":          { "MaxPerInterval": 20, "Interval": "00:01:00", "BatchSize": 50, "MinDelayBetweenBatches": "00:05:00" }
    }
  }
}
```

**Configuration beats code for queue settings.** A queue declared in code (`AddMessaging(o =>
o.Queues["mail-bulk"] = …)`, or a library's own `Configure<SparkMessagingOptions>`) is a set of
defaults; `Spark:Messaging:Queues` is applied over it afterwards, property by property, from any
configuration source — appsettings, user secrets, or environment variables such as
`Spark__Messaging__Queues__mail-bulk__MaxPerInterval=20`. A configured `Backoff` replaces the code
schedule rather than being appended to it. Everything outside `Queues` keeps "code wins".

**How throttling works.** Admission runs at the top of `MessageProcessor.RunHandlersAsync`, shared by
both subscription modes — and, in `SingleSubscription` mode, once more in the feeder **before** the
claim, so a message its queue cannot start yet is deferred in the feeder's claim write and never
enters a lane (a message the feeder admitted keeps a due reservation, so the processor does not
charge it a second slot). It never waits inside a lane (a sleeping lane would block the bounded
channel, the one feeder, and every other queue). A message over budget is written back **once** —
`Pending`, no owner, `WakeUp = false`, `NextAttemptAtUtc` = its reserved slot, `AttemptCount`
restored — and the lane moves on; the sweeper wakes it at the slot and it starts without asking
again. Slots are reserved GCRA-style in memory (the leader lease means one process admits for every
queue), so each throttled message is deferred once, not once per poll. After a restart the
reservations are gone and a deferred message is deferred once more.

Accuracy: the long-run rate is exact; bursts are quantised by `FallbackPollInterval` (default 30 s),
because due messages are woken on the sweeper's tick. The measured figures are in the #460 PRD (§4.1,
S-M3).

**Pattern for mail:** `mail-transactional` generous (it carries password resets and confirmations,
which a user is waiting for — and give them `ExpiresAtUtc`), `mail-bulk` strict (it must never crowd
out the relay or trip a provider's rate limit). MailManager declares exactly that, with
`mail-transactional` at `High` priority and `mail-bulk` at `Low`.

### Priority lanes

Separate queues are separate lanes, so a slow bulk *handler* never delays a transactional one. But in
`SingleSubscription` mode every queue's documents reach the process through one subscription, in
etag order, and a feeder that follows that order must claim what is in front before it sees what is
behind: measured in S-M3 before M16 (#460 PRD §4.1), a transactional message published behind a 1,000-message bulk backlog waited up
to 5.8 s (9.4 s on a loaded machine), because each bulk message cost a load and a claim of its own and
then a second write when its lane deferred it.

The feeder therefore serves a **sorted page** on every wake-up (#460 M16b; M16's look-ahead window
before it could only reorder the 256 messages one batch held):

- **The subscription is the wake-up signal, not the order.** The one leader-elected `SparkMessaging`
  subscription is unchanged: same query, no `now()`, never deleted or recreated, and still what makes
  feeding exclusive (`WaitForFree`) and what claims are taken against.
- **Strict priority over the whole queue.** When a batch arrives, the feeder reads the top
  `FeederBatchSize` (default 256) claimable messages from the `SparkMessages_ByPriority` index,
  ordered **`Priority desc, Sequence asc`**, merges them with the batch, and claims the lot highest
  priority first: one load and one write per priority (claims and throttle deferrals together), then
  routing. A `High` message published behind any `Low` backlog is served on the next wake-up.
- **FIFO within a priority is server-assigned.** `Sequence` is the database server's `@last-modified`
  of the write that made the message claimable, captured into `SparkMessage.QueuedAtUtc` on the
  feeder's first write to it (its claim or deferral, so no extra write). Never the publisher's clock.
  A retry, a reclaim or a throttle wake-up keeps the message's place. Ties (none measured) break by id.
- **Nothing the index has not seen yet is skipped.** The page is read as the index stands, never
  waited for; a message that is not indexed yet is in the subscription batch and is served from there
  (measured index lag under a 1,000-message bulk load: tens of milliseconds, S-M7).
- **No starvation, and no aging needed.** Every message a batch delivers is served in that batch,
  whatever the page holds, and the subscription walks the queue in commit order. A `Low` message
  therefore waits at most for the subscription to reach it — its FIFO position — plus one page of
  higher-priority messages per batch pulled ahead of it. Strict priority can delay `Low`, never starve
  it; a test keeps several pages of `High` outstanding throughout and `Low` still completes.
- **Priority is stamped at publish.** `SparkMessage.Priority` is written from the queue's setting when
  the message is published (an index cannot read configuration), so a changed `Priority` applies to
  messages published after the change.
- **Why not a subscription per priority.** Community caps a database at 3 subscriptions, one of which
  Replication may already use.

Throttling is unchanged: a message its queue cannot start yet is deferred before its claim, and a
deferred message leaves the "pending now" set (and the index) until the sweeper wakes it at its slot.
What it still does not do: a lane filled to its capacity (512 claimed messages) by an **unthrottled**
slow low-priority queue back-pressures the feeder. Throttle bulk queues (MailManager's `mail-bulk` is).
The feeder needs the index, which `AddMessaging()` deploys; without it the feeder logs one warning and
serves each batch alone, priority first within it (M16's window). The measured figures are in the #460 PRD (§4.1, M16b).

`SubscriptionPerQueue` mode is unaffected: each queue has its own subscription, delivered one message
at a time, and nothing is shared to prioritise. The index is deployed in both modes; only the single
feeder reads it.

### Inside a handler: `IMessageContext` and `IMessageProgress`

Both are scoped services a recipient can inject.

- `IMessageContext` — `MessageId` (stable across retries; use it as an idempotency key, a mail
  `Message-ID`, a VERP token), `QueueName`, `AttemptCount`, `ExpiresAtUtc`.
- `IMessageProgress` — `IsDoneAsync(step)` / `MarkDoneAsync(step)` for handlers that do several
  externally visible things, so a retry skips the ones that already happened. Progress lives in a
  sidecar document `{messageId}/progress/{handlerIndex}` (collection `SparkMessageProgresses`),
  appended by patch, never on the message itself, and gets the same `@expires` as its message when
  the message becomes terminal. Mark a step *after* its effect: a crash in between repeats that one
  step.

```csharp
public partial class SendCampaignRecipient : IRecipient<Campaign>
{
    [Inject] private readonly IMessageProgress progress;
    [Inject] private readonly ICampaignMailer mailer;   // your own sender

    public async Task HandleAsync(Campaign campaign, CancellationToken ct)
    {
        foreach (var address in campaign.Recipients)
        {
            if (await progress.IsDoneAsync(address, ct)) continue;
            await mailer.SendAsync(address, ct);
            await progress.MarkDoneAsync(address, ct);
        }
    }
}
```

### Dead-letter reasons

A dead-lettered message carries `DeadLetterReason`: `MaxAttempts` (a handler failed `MaxAttempts`
times), `NonRetryable` (`NonRetryableException`, or an unusable message — type outside the
allow-list, unresolvable, empty payload) or `Expired` (`ExpiresAtUtc` passed, or the next retry or
throttle slot would have fallen after it). The status stays `DeadLettered`; there is no new status.

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
| `WakeUp` | `bool` | Redelivery gate: set by the sweeper when the message is due, cleared by the worker on pickup |
| `LastWakeUpUtc` | `DateTime?` | When the sweeper last woke this message (informational) |
| `ExpiresAtUtc` | `DateTime?` | Publish-time deadline (`BroadcastOptions.ExpiresAtUtc`) |
| `DeadLetterReason` | `EDeadLetterReason?` | `MaxAttempts`, `NonRetryable` or `Expired`; set only with `DeadLettered` |
| `ScrubPayloadOnTerminal` | `bool` | Clear `PayloadJson` once terminal |
| `Priority` | `int` | The queue's `SparkQueuePriority` at publish (`-1` Low, `0` Normal, `1` High); the single feeder's sort key |
| `QueuedAtUtc` | `DateTime?` | The server's `@last-modified` when the message became claimable, captured on the feeder's first write; its FIFO place within a priority |

Two static indexes cover the collection: `SparkMessages_ByQueue` (the sweeper's due and abandoned
messages) and `SparkMessages_ByPriority` (only the claimable-now messages, sorted
`Priority desc, Sequence asc`, read by the single feeder).

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
| `IMessageBus` | `BroadcastAsync<T>(message, BroadcastOptions)`; shorthands `BroadcastAsync<T>()`, `DelayBroadcastAsync<T>()`, `BroadcastOnceAsync<T>()` (default interface methods — a fake implements only the options overload) |
| `BroadcastOptions` | `DeduplicationKey`, `Delay`, `MaxAttempts`, `ExpiresAtUtc`, `Queue`, `ScrubPayloadOnTerminal` |
| `IMessageContext` | The message the current handler runs for (`MessageId`, `QueueName`, `AttemptCount`, `ExpiresAtUtc`) |
| `IMessageProgress` | `IsDoneAsync(step)` / `MarkDoneAsync(step)` — per-handler progress sidecar |
| `IRecipient<TMessage>` | `HandleAsync(TMessage, CancellationToken)` |
| `ICheckpointRecipient<TMessage>` | Extends `IRecipient<T>` with `HandleAsync(TMessage, string checkpoint, CancellationToken)` for resume-from-checkpoint |
| `IMessageCheckpoint` | `SaveAsync(string)` -- saves progress during handler execution |
| `MessageQueueAttribute` | Assigns a message class to a named queue |
| `NonRetryableException` | Thrown by a recipient to dead-letter its handler immediately, with no retries |

### Extension Methods (`MintPlayer.Spark.Messaging`)

| Method | Description |
|--------|-------------|
| `spark.AddMessaging(Action<SparkMessagingOptions>?)` | Register messaging services and deploy the `SparkMessages/ByQueue` index |

| Type (`MintPlayer.Spark.Messaging`) | Description |
|------|-------------|
| `IMessageOutbox` | `EnqueueAsync<T>(IAsyncDocumentSession, message, BroadcastOptions?)` — store in the caller's session; commits with it |

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

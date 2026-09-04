# Migration — one subscription, partitioned ordering

For anyone deploying `10.0.0-preview.72`, and for the `coverage.mintplayer.com` deploy in particular.

## What must happen on an existing database

### 1. Delete the old per-queue subscriptions

They are not reused, and every one left behind occupies a slot against a limit of **three per
database** (fifteen per cluster) on the unlicensed and Community tiers alike.

```
SparkMessaging-coverage-parse-session
SparkMessaging-coverage-publish-feedback
SparkMessaging-coverage-publish-pr-comment      ← may not exist; it was never created
SparkMessaging-coverage-open-pr-comment         ← may not exist
SparkMessaging-coverage-delete-pr-builds        ← may not exist
SparkMessaging-spark-github-all
SyncActions                                     ← replication, where enabled
```

Studio → Settings → Ongoing Tasks → Subscriptions, or
`store.Subscriptions.Delete(name)`.

**Delete, do not disable.** Measured: a disabled subscription still counts against the limit; only
deletion frees a slot, and it frees it immediately.

### 2. Expect a first-connect replay

The new `SparkMessaging` subscription starts at change vector zero and will be offered every
`Pending`/`Failed` document in `SparkMessages`. That is by design — the pump decides what actually
runs — but on the production `Coverage` database it includes an accumulated backlog of **typed webhook
messages that were never consumable**: `GitHubWebhookMessage<TEvent>` was broadcast with no recipient,
so those documents never reached a terminal status and never had `@expires` stamped.

They are now completed on sight (publishing to zero subscribers is a successful publish), which stamps
retention and lets them expire. Nothing needs deleting by hand. Watch the first few minutes of lane
logs rather than assuming.

### 3. The index gains fields, and queries on them throw during the swap

`SparkMessages_ByQueue` gains `PartitionKey`, `Sequence` and `VisibleAtUtc`.

RavenDB deploys the new definition side by side and swaps it in — the old shape keeps serving
throughout, so **no hand-rolled side-by-side deploy is needed**. But during the swap (~4.5s per 50k
documents, measured) a query touching a *new* field throws
`RavenException: The field 'PartitionKey' is not indexed` rather than waiting; `WaitForNonStaleResults`
does not block on it. The lane pump catches drain failures and retries, so this self-heals — expect a
few `Lane '…' drain failed; retrying` lines on the first deploy and nothing more.

### 4. `coverage-delete-pr-builds` will finally run

That lane has **never** executed in production, so merged-PR retention has never been enforced. On the
first successful deploy it processes its whole accumulated backlog. Nothing throttles it. Watch it, and
if the backlog is large enough to matter, drain it deliberately rather than discovering the load.

### 5. Replication, where enabled

`SparkSyncAction` documents and the `SparkSyncActions/ByStatus` index are no longer used. Any
`Pending`/`Failed` sync action left in the collection will **not** be delivered — it must be
re-broadcast, or accepted as lost.

`apps/CodeCoverage` does not enable replication, so its `Coverage` database has none. `apps/HR` and
`apps/Fleet` do; check before deploying either.

## Breaking API changes

No compatibility shims — these are preview packages.

| Removed | Replacement |
|---|---|
| `SparkMessagingOptions.MaxAttempts` | The lane's `IRetrySchedule`; a ladder derives its own limit |
| `.BackoffDelays` / `.DefaultBackoffDelays` / `.ResolvedBackoffDelays` | `DefaultRetry` (a scalar ladder string), or a per-lane `.Retry(...)` |
| `.FallbackPollInterval` | Gone with the sweeper; each pump schedules its own wake |
| `SparkMessage.MaxAttempts` | Policy resolves at scheduling time, so a config change reaches in-flight messages |
| `SparkMessage.WakeUp`, `.LastWakeUpUtc` | Gone with the sweeper |
| `MessageRetrySweeper`, `MessageSubscriptionManager`, `MessageSubscriptionWorker` | `MessageFeeder` + `MessageLanePump` + `MessageProcessor` |
| `SparkSyncAction`, `ESyncActionStatus`, `SparkSyncActions_ByStatus`, `SyncActionSubscriptionWorker`, `SyncActionRetrySweeper` | `SyncActionMessage` on the `spark-sync` lane |

Added: `IMessageBus.DelayBroadcastAsync(message, delay, queueName)` — the delayed broadcast had no lane
override, so a delayed message could only ever go to its derived lane.

`MintPlayer.Spark.Messaging.Abstractions` gains a dependency on
`Microsoft.Extensions.DependencyInjection.Abstractions`, since lane declaration is a service
registration and the types that describe it live there.

## What to do in an application

Nothing, to keep working: an undeclared lane is `Concurrent(1)`, which is close to the old
one-at-a-time behaviour, and no broadcast site changes.

Declare lanes where ordering or concurrency matters:

```csharp
spark.AddMessaging(messaging: messaging => messaging
    .AddLane(lanes => lanes.Queue<ParseSessionMessage>()
        .Ordered()
        .PartitionBy<ParseSessionMessage>(m => m.BuildId)
        .PartitionBy<FinalizeBuildMessage>(m => m.BuildId)
        .MaxPartitionsInFlight(2))

    .AddLane(lanes => lanes.Queue<PublishFeedbackMessage>().Concurrent(maxConcurrency: 4)));
```

A lane declaration is an ordinary service registration resolved **on first use**, so it can be
configured from anything the container holds — which the first draft of this API could not do,
because it ran while services were still being registered:

```csharp
messaging.AddLane((lanes, services) =>
{
    var options = services.GetRequiredService<IOptions<MailOptions>>().Value;
    lanes.Queue("spark-email").Concurrent(options.Workers).Retry(RetrySchedule.Ladder(options.RetryLadder));
});

// Or a class the container constructs, for constructor injection:
messaging.AddLane<MailLaneConfigurator>();
```

A framework package declares its own lane the same way — `services.AddSparkLane<TConfigurator>()` —
so registration order does not matter.

**Startup refuses** an ordered lane with a message type that has no partition selector, a lane declared
twice, and an ordered lane whose retry ladder can block a partition for longer than
`MaxPartitionBlock` (15 minutes by default; say `AcceptPartitionBlock(...)` if the wait is intended).
Each of those exists because its alternative is silent. Note these are raised the first time lanes are
needed — the startup validation pass — rather than at the `AddLane` call, so the failure names the
lane rather than pointing at the registering line.

## Verifying after deploy

```bash
# Exactly one subscription should exist on the database.
docker compose exec coverage-raven bash -c \
  'exec 3<>/dev/tcp/127.0.0.1/8080; printf "GET /databases/Coverage/subscriptions HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3; cat <&3'
```

Then open a pull request against a tracked repository and confirm the coverage comment appears on
`opened` — the symptom that started this work.

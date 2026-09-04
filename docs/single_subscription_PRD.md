# PRD — One subscription, partitioned ordering

**Status:** proposed, not implemented
**Date:** 2026-09-04
**Packages:** `MintPlayer.Spark.Messaging(.Abstractions)`, `MintPlayer.Spark.SubscriptionWorker(.Abstractions)`, `MintPlayer.Spark.Replication`
**Breaking:** yes, freely — preview packages, no backward compatibility required

## 1. The constraint

RavenDB allows **3 data subscriptions per database** (15 per cluster), and **that is not a licensing
question**. Measured 2026-09-04 against `ravendb:7.1.10`:

| Tier | `MaxNumberOfSubscriptionsPerDatabase` |
|---|---|
| AGPL / unregistered | 3 |
| Community (now active on `coverage.mintplayer.com`) | 3 |

Concurrent subscription workers are unavailable on both, so one subscription also means one worker.

Today the framework creates **one subscription per queue**, plus one for sync actions. A message type
without `[MessageQueue]` silently derives its own queue name, so every distinct message type is its
own subscription unless someone says otherwise.

| App | Queues | Subscriptions |
|---|---|---|
| **`apps/CodeCoverage`** (production) | 5 declared + framework `spark-github-all` | **6** vs cap 3 |
| `apps/WebhooksDemo` | `spark-github-all` + 2 derived from `GitHubWebhookMessage<TEvent>` | 3 — at the cap, nothing declared |
| `apps/HR` | messaging queues + `SyncActionSubscriptionWorker` | over |

### What this already cost

Three of CodeCoverage's six subscriptions were never created, silently:
`EnsureSubscriptionExistsAsync` (`SparkSubscriptionWorker.cs:96-123`) catches **every** exception from
`CreateAsync`, assumes "already exists", tries `UpdateAsync`, and logs a *warning*. It then opens a
worker on a subscription that does not exist and dies. No health signal.

- The sticky PR comment never appeared on `opened` — its queue had no subscription.
- **`coverage-delete-pr-builds` has never run.** Merged-PR retention has never been enforced.
- Typed webhook messages have no recipient in CodeCoverage, never reach a terminal status, never get
  `@expires`, and **accumulate forever**.

## 2. Goals

1. **One RavenDB subscription, one worker.**
2. **Messages that depend on each other are processed in broadcast order.**
3. **Independent work never blocks.** A wedged or poisoned message contains its own damage.
4. **Poison messages are bounded** and end in a dead-letter.
5. **Several handlers per message type, replaying only the ones that failed.**
6. **A subscription that cannot be created fails loudly** at startup.
7. Retry policy is **per queue** and tunable per environment.

## 2.1 Guarantees that must survive unchanged

1. **Several handlers per message type, replayed independently.** Each `HandlerExecution` has its own
   `Status`, `AttemptCount`, `LastError`, `Checkpoint`. On redelivery the loop skips handlers already
   `Completed` or `DeadLettered` (`MessageSubscriptionWorker.cs:162-166`), so a retry re-runs **only
   what failed**. `RollupMessageStatus` derives the message status from its handlers.
2. **Failure state lives in the document, not the subscription.** The batch is always acknowledged.
3. **`NonRetryableException` dead-letters that handler**, leaving the others alone.
4. **Allow-list checked before `Type.GetType`** (R2-H6) — a security control, not a nicety.
5. **`IMessageCheckpoint`** lets a long handler resume rather than restart.

## 3. Non-goals

- Multi-node processing (one subscription is single-consumer; see §9).
- Backward compatibility.
- Keeping `SparkSyncActions` as a separate collection (§7).

## 4. Measured constraints

`RavenDB.Client` 7.2.5 against an **unlicensed** container — the dev server misleads (see traps).

| Finding | Consequence |
|---|---|
| `from A, B` is a **parse error**; multi-collection means `@all_docs` + a `@collection` whitelist | Shapes §5.1 |
| `@all_docs` cold catch-up over 200k docs: **2934 ms vs 66 ms**; steady state 332 ms vs 4 ms | **`@all_docs` rejected**; sync actions move into `SparkMessages` |
| Progress is **per batch, never per document**; crash after 10 of 25 redelivers all 25 | No partial ack |
| An escaping exception raises `SubscriberErrorException` and **kills the worker; no self-heal** | Dispatcher must catch per document |
| `IgnoreSubscriberErrors = true` **acks and silently drops** the rest of the batch | **Ruled out** — turns a stall into data loss |
| Next batch is not fetched until the callback returns; a 4 s handler delayed an unrelated collection by **3.3 s** | The feeder must never do handler work |
| Fan-out inside a batch: 3633 ms → **38 ms** | Pumps, not inline processing |
| `LicenseLimitException` thrown at **create** time | Catch this exact type |
| **Delete frees a slot immediately; Disable does not** | Migration must delete |
| `SubscriptionOpeningStrategy.Concurrent` is licence-gated | One subscription = one worker |

**Two traps.** The cap is **unenforced for ~50–70 s after server start** (fresh servers accepted 6+),
which is probably how the per-queue design survived local runs. And **`localhost:8080` and CI both run
a Developer licence** with no cap at all — cap assertions there are vacuous.

## 5. Design

The decisive observation: **the subscription was never the queue — it is a doorbell.** Retry, backoff,
attempt counts, dead-lettering and terminal status all live in the document; the batch is always
acked; redelivery is done by a sweeper. Collapsing N subscriptions into one loses no durability.

### 5.0 Three domains, not two

`[MessageQueue]` currently carries three unrelated concerns:

| Domain | Question | Cardinality | Known at |
|---|---|---|---|
| **Isolation** | what can wedge what | ~10, declared | compile time |
| **Concurrency** | how many at once | ~10, declared | deployment |
| **Ordering** | which messages have happens-before | **unbounded** — one per build, PR, repo, document | runtime, in the payload |

Ordering has a cardinality four to six orders of magnitude larger than the others. Serving it with the
same key as isolation must be wrong for one of them, and queue-scoped FIFO picks the small one — so it
**over-orders**, asserting happens-before between messages that have none. The price of a false
happens-before is exactly head-of-line blocking against unrelated work.

**Queue-scoped FIFO satisfies zero consumers correctly.** `coverage-parse-session` needs ordering *per
build*. The four publish/delete queues exist explicitly *to escape* FIFO ("a slow GitHub API call must
never delay parsing"). `spark-github-all`, `spark-etl-deployment` and the demo queues state no ordering
requirement. And `spark-sync-{Collection}` **is a partition key already**, smuggled through the
queue-name parameter — the codebase invented the concept and had nowhere to put it.

**Ordering is a property of the message, not of the queue.** `FinalizeBuildMessage` depends on
`ParseSessionMessage` *for the same build*; that dependency lives in the data.

*Design-it-twice:* the honest alternative is dynamic queue names (`…/builds-123`), which §5.2 makes
work. It fails because lane lifecycle explodes into an unbounded registry of live tasks, configuration
becomes pattern-matched strings, and a concurrency cap has no domain to attach to. It **converges to
this proposal** — the best available evidence that the seam is in the right place.

### 5.1 The subscription

One subscription, `SparkMessaging`, over one collection (sync actions move in, §7):

```
from SparkMessages
where (Status = 'Pending' and (NextAttemptAtUtc = null or WakeUp = true))
   or (Status = 'Failed'  and WakeUp = true)
```

`QueueNames.IsValid` exists to stop RQL injection through the interpolated queue clause. With nothing
interpolated it must **not** be deleted — it moves to producer-side validation in `MessageBus`.

### 5.2 Feeder and lanes

One worker. It does **no handler work**: per delivered document it reads the lane name, rings that
lane's **capacity-1 `Channel<bool>` doorbell**, and acks. Dropping a redundant ring is safe by
construction. `MaxDocsPerBatch` rises from 1.

An earlier draft had the feeder enqueue message *ids* and drop on full. **Withdrawn** — a dropped M1
returns after an M2 enqueued behind it.

When rung, the pump drains its own backlog by query (§5.4). Ordering comes from a deterministic sort,
never arrival timing. Memory is O(lanes); backpressure stops being a concept.

### 5.3 Lanes, partitions, modes

- **Lane** (`[MessageQueue]`) = isolation and concurrency. Unchanged, one argument, all 12 existing
  declarations compile.
- **Partition** = the ordering domain, a key **on the message**, resolved by a selector declared once
  at lane registration.

Policy moves to the builder, where mode-specific types make illegal states **unspellable** — no
analyzer needed, because `IOrderedQueueBuilder` has no `MaxConcurrency` and `IConcurrentQueueBuilder`
has no `PartitionBy`:

```csharp
messaging.Queue<ParseSessionMessage>()                    // "coverage-parse-session"
    .PartitionBy<ParseSessionMessage>(m => m.BuildId)
    .PartitionBy<FinalizeBuildMessage>(m => m.BuildId)
    .PartitionBy<AssembleCommitMessage>(m => m.CommitId)
    .Ordered()
    .MaxPartitionsInFlight(4)
    .Retry(RetrySchedule.Ladder("5s 30s 2m 10m"));        // worst case 12m35s, per BUILD

messaging.Queue("spark-github-all")
    .PartitionBy<GitHubWebhookMessage>(m => m.RepositoryFullName)
    .Ordered().MaxPartitionsInFlight(8)
    .Retry(RetrySchedule.Linear(step: 15s, cap: 2m, attempts: 8));   // ~13m, per REPO

messaging.Queue("spark-email")
    .Concurrent(maxConcurrency: 8)
    .Retry(RetrySchedule.Ladder("1m 5m 1h 6h 1d 3d 7d")); // 11 days blocks nobody

messaging.Queue("coverage-heartbeat")
    .Unbounded()                                          // overlapping runs intended
    .Retry(RetrySchedule.None);                           // the schedule IS the retry
```

**The payoff that settles the decomposition:** under queue-scoped FIFO, "strictly ordered" and "run
four at once" genuinely contradict. Under partitions they range over different domains, so
**`Ordered` + `MaxPartitionsInFlight(4)` is meaningful and safe** — four builds parsing at once, each
internally FIFO. Queue FIFO can only offer "one build at a time" or "no ordering". This doesn't avoid
the illegal state; it deletes the conflict that made it illegal.

Note the three selectors express something queue-FIFO cannot: parse and finalize share `builds/…` so
finalize cannot overtake *its own* parses, while `AssembleCommitMessage` keys on `commits/…` because
its requirement is **mutual exclusion per commit**, not ordering against parses (causality already
orders it). One lane, two ordering requirements, both exact.

`RetrySchedule.None` on the heartbeat matters: retrying a failed minute races two minutes' work and a
stuck downstream builds a backlog that never drains. The next minute *is* the retry.

**Validation at `Build()`:** every message type on an `Ordered` lane must have a selector (fatal,
naming the type); a lane declared twice is fatal; an undeclared lane defaults to `Concurrent(1)` —
never `Ordered`, because a silently-ordered lane with no partition key is the exact failure this
design exists to prevent.

### 5.4 Index and drain

Add one field to `SparkMessages_ByQueue`: `PartitionKey` (never null; `""` on unordered lanes).

**No map-reduce and no per-partition query.** Because the window is ordered by `(CreatedAtUtc, Id)`,
**the first row seen for a partition is that partition's head**:

```csharp
var window = await session.Query<SparkMessage, SparkMessages_ByQueue>()
    .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
    .Where(m => m.QueueName == lane
             && (m.Status == Pending || m.Status == Failed)
             && (m.NextAttemptAtUtc == null || m.NextAttemptAtUtc <= now)
             && !excluded.Contains(m.PartitionKey))     // → RQL `not in`
    .OrderBy(m => m.CreatedAtUtc).ThenBy(m => m.Id)
    .Take(256).ToListAsync(ct);

var seen = new HashSet<string>(StringComparer.Ordinal);
foreach (var msg in window)
    if (seen.Add(msg.PartitionKey) && inFlight.Count < maxPartitionsInFlight)
        Dispatch(msg);                                   // this is its partition's head
```

Pushing `excluded` **into the query** rather than filtering client-side is what prevents window
starvation — otherwise 256 messages of blocked partitions hide every runnable one. That is why the cap
in §5.6 is structural, not hygiene: it bounds the `not in` list.

**The time predicate replaces `WakeUp`.** `WakeUp` exists only because subscription where-clauses
cannot evaluate `now()`. A drain is an ordinary query and can. So `MessageRetrySweeper` may not need
to write to documents at all — each pump schedules its own wake at `min(NextAttemptAtUtc)` among its
parked partitions. **A whole background service, a duplicated field pair, and 30-second redelivery
granularity, potentially deleted** (spike S11 — the largest simplification available here).

### 5.5 In-flight bookkeeping

```csharp
sealed class OrderedLaneState
{
    HashSet<string> inFlight;                  // ≤ MaxPartitionsInFlight
    Dictionary<string, ParkedPartition> parked; // ≤ MaxParkedPartitions
    PriorityQueue<string, DateTime> wakeOrder;  // one timer per lane
    Channel<bool> doorbell;                     // capacity 1
}
```

`excluded = inFlight ∪ parked.Keys`, both bounded. **Runnable partitions are discovered by query,
never held in memory** — so memory is O(inFlight + parked), not O(partitions ever seen).

**The invariant for every field ever added here:** everything in `OrderedLaneState` is a *cache of a
fact already in the documents* — `inFlight` mirrors `Status = Processing`, `parked` mirrors
`NextAttemptAtUtc > now`. Drop the object at any instant and correctness is unaffected, which is why
§5.7 is trivial. A field that would be authoritative in memory needs a recovery protocol and should be
rejected on those grounds.

### 5.6 A partition parked for hours

**The block is scoped to that partition only.** An email to X retrying for seven days delays nothing
addressed to Y; a poison parse of build A does not stall build B.

A per-lane cap is needed for two reasons, one structural: it **bounds the `not in` list**, and it is
the only lane-level signal that a dependency is down (per-partition retry means a dead SMTP host
produces N independent ladders, each locally healthy, with nothing saying "this lane is broken").

`MaxParkedPartitions` (default 256). On overflow the lane enters **degraded** mode: stop drawing new
partitions, emit `LaneDegraded`, keep servicing parked partitions as they come due. That is
back-pressure, not data loss. **Rejected:** dead-lettering to free capacity (data loss where the fault
is downstream), and evicting the oldest parked partition (it would be redrawn and retried early,
defeating the ladder).

Because the block is per partition, the guardrail becomes **`MaxPartitionBlock`, validated at
startup** — `.Retry()` computes the worst-case block as the ladder sum and fails startup above a
default 15 min unless the lane says `.AcceptPartitionBlock(TimeSpan.FromDays(11))`. Loud by default,
explicitly overridable. This also catches the unreachable-rung bug mechanically, since the sum makes
reachable rungs visible. Nothing is dead-lettered early, so **`RequeueAsync` leaves the critical
path** and stays a useful operator tool.

### 5.7 Crash recovery

Nothing needs reconstructing. After a restart: reap `Processing` documents older than the lease back
to `Failed` with `AttemptCount++` (partition-unaware); start every pump with empty state; **the first
drain re-establishes order**, because the window is ordered and the first row per partition is its
head. Parked state re-establishes itself lazily — a future `NextAttemptAtUtc` is excluded by the time
predicate, so the pump never needs to remember it was parked.

Two hazards recorded rather than hidden. The `(CreatedAtUtc, Id)` total order now only needs to hold
**within a partition** — one build, one PR — which is almost always one host in one causal chain, so
partitioning *buys headroom* on the PRD's most fragile assumption. And clock skew: `MessageBus` should
issue `CreatedAtUtc = max(UtcNow, lastIssued + 1 tick)` under one `Interlocked`, removing intra-process
inversion and ties, which demotes the hilo-monotonicity assumption to a backstop.

## 6. Retry policy (per queue)

**Code owns the policy *name*; ops owns the *numbers*.** Whether email retries slowly is a code fact;
how slowly is an environment decision — so the two surfaces never fight over one value.

Shape is chosen by a factory, so illegal combinations cannot be written:

```csharp
RetrySchedule.Ladder("1m 5m 1h 6h 1d 3d 7d");   // maxAttempts DERIVED = rungs + 1
RetrySchedule.Linear(step: 20s, cap: 2m, attempts: 8);
RetrySchedule.Exponential(initial: 5s, factor: 3, cap: 10m, attempts: 6);
RetrySchedule.None;
```

Deriving `maxAttempts` for a ladder makes today's bug — 5 rungs with `MaxAttempts = 5`, last rung
unreachable — **unrepresentable**. The ladder *is* the schedule.

**Precedence** (highest wins, policies taken whole, no field-level merging):
`RetryOverride` → `Queues:<q>:retry` → the lane's named policy (config body over code body) →
`"default"`. The test switch is one line — `"RetryOverride": "5s"` — replacing every delay *function*
with a constant while **keeping each queue's attempt count**, so tests still exercise the real
dead-letter path.

**The array-append trap is designed out:** config is dictionary-shaped, and a ladder is a **scalar
string** (`"1m 5m 1h"` — replaced by the binder, where a `TimeSpan[]` would be appended to).
`BackoffDelays` / `DefaultBackoffDelays` / `ResolvedBackoffDelays` disappear.

**One decision point.** The worker computes a delay in its outer catch (`:297`), again in
`RollupMessageStatus` (`:336`, from the max *handler* attempt), and decides dead-lettering in the
handler loop — three sites, two counters, already drifted. They collapse into
`IRetrySchedule.Next(...)` returning `RetryAt | DeadLetter(reason)`. Unifying these is roughly half
the value of this work. `SparkMessage.MaxAttempts` — a policy snapshot taken at broadcast — is
deleted, or `RetryOverride` would not reach in-flight messages.

**Ladder exhaustion dead-letters; it does not clamp** (`RepeatingLastRung` is opt-in). **Jitter**
defaults to 0, only ever shortens, and is rejected on `Ladder`; it earns its place across lanes — five
lanes backing off against one GitHub 5xx — not within one.

**New default:** `Ladder("5s 30s 2m")`, worst case ≈2m35s, every rung reachable.

## 7. Folding sync actions in — required, not optional

The spike removed the alternative: a subscription cannot name two collections, and `@all_docs` costs
44× on catch-up. Delete `SparkSyncAction`, `SparkSyncActions_ByStatus`, `SyncActionRetrySweeper`,
`SyncActionSubscriptionWorker`; `SyncActionInterceptor` broadcasts `SyncActionMessage` on **one
`spark-sync` lane partitioned by document id** (sync order matters per document) — which is what the
`BroadcastAsync(msg, queueName)` overload was reaching for all along. A framework
`IRecipient<SyncActionMessage>` holds the POST logic, mapping 400/404 to `NonRetryableException`.

Two retry engines become one, two sweepers become one. **Preserve verbatim:** mTLS module identity,
the `RequestingModule` certificate gate in `SyncApply`, terminal-on-400/404.

## 8. Answering the recorded objection

`docs/prd/PRD-SubscriptionWorker.md` §8 rejected this design; §8.2 is titled *"One Subscription Per
Queue, BatchSize = 1"*.

1. **The NACK argument is moot** — nothing NACKs; the worker catches everything and always acks.
2. **The reordering argument is real but misattributed.** It is caused by the wake-up mechanism, not by
   sharing a subscription, and applies today.
3. **Per-queue subscriptions never delivered the guarantee they were chosen for.**

### 8.1 The live bug this exposes

M1 fails → the worker writes `Status = Failed, NextAttemptAtUtc`, and **that save bumps M1's change
vector**, moving it behind M2 → the worker acks and processes M2 immediately → M1 returns later via the
sweeper. Per-queue subscriptions bought *serialization*, not *ordering*.

**This is a live production correctness bug.** `docs/code-coverage/upload-api.md:102` promises that
finishing "can never close a build on a half-computed number". It can: one transient
`ParseSessionMessage` failure lets `FinalizeBuildMessage` overtake it and publish a wrong percentage,
silently. Making ordering real is a **fix**, not a preservation requirement.

## 9. Deferred: no subscription at all

Once §5.4's drain replaces `WakeUp`, the subscription is only a latency accelerator. A `store.Changes()`
doorbell plus the poll would free **all three slots** and is the only route to multi-node processing.
Deferred, but the feeder must remain the sole component that knows how a notification arrives, so the
swap stays a one-class change. Spike S2 must confirm the changes API consumes no slot.

## 10. Bugs this fixes

| Bug | Where |
|---|---|
| Subscription create failure swallowed → silently dead queue | `SparkSubscriptionWorker.cs:96-123` |
| Dead-letter paths `return` instead of `continue`, abandoning the batch — harmless only at `MaxDocsPerBatch = 1`, which this raises | `MessageSubscriptionWorker.cs:95,100,109,117` |
| `BroadcastAsync(msg, queueName)` inert — message sits `Pending` forever | `MessageBus.cs:21` |
| `Processing` documents stranded by a crash never retried | no sweeper path |
| Unconsumed typed webhook messages never terminal → never `@expires` → accumulate | `SparkWebhookEventProcessor.cs:125-127` |
| Undisposed session per document | `MessageSubscriptionWorker.cs:74` |
| `SubscriptionWorkerRegistrationGenerator` emits a method nothing calls | inert; would spend a slot |
| Last backoff rung unreachable at `MaxAttempts = 5` | `MessageSubscriptionWorker.cs:297` |
| In-queue ordering violated on the retry path | §8.1 |

## 11. Migration

1. **Delete** old per-queue subscriptions everywhere including production `Coverage` (Disable does not
   free a slot).
2. **Choose the new subscription's start position deliberately** — a fresh subscription starts at
   change vector zero and replays every matching document, including the accumulated orphans (S4).
3. **Adding `PartitionKey` forces a full reindex** of `SparkMessages`, and with
   `WaitForNonStaleResults` the first drain blocks until it completes. Deploy a v2 index side-by-side
   and cut over, or accept a measured stall (S10). This is the largest new production risk.
4. Reap `Processing` once at startup.
5. **Throttle the `coverage-delete-pr-builds` backlog** — it has never run.
6. Convert or drop `SparkSyncAction` documents (likely empty on `Coverage`, which has no replication).
7. One lockstep version bump; major stays `10`.

## 12. Disposition of `fix/coverage-queue-licence-cap`

**Keep** the `EnsureSubscriptionExistsAsync` hardening and the producer-side webhook test. **Drop**
`CoverageQueues.cs` and its consolidation of five queues onto two (it destroys the ordering intent this
design restores at zero cost), `CoverageQueuesTests.cs` (asserts the invariant being abolished — invert
it into "N queues produce exactly 1 subscription"), and its 22 version bumps.

## 13. Risks

| Risk | Mitigation |
|---|---|
| The pump is the most concurrency-sensitive code in the framework (~200 lines) | Land the reaper first; §5.5's cache invariant keeps it recoverable |
| **Partition-key skew** — one key for everything is queue-FIFO again; a unique key per message is no ordering at all | Startup completeness check; per-partition backlog metric; document that the *wrong* key is only caught by the metric |
| Full reindex stall on deploy | S10; side-by-side index |
| At-least-once window widens from a batch to a lease | Handlers already tolerate redelivery; document it |
| One feeder may not keep up with ingestion bursts | S5; §9 stops being optional if it fails |
| Selector lives away from the message, so adding a field doesn't prompt re-partitioning | Say so in the docs rather than pretending otherwise |

## 14. Spikes

| # | Question |
|---|---|
| S2 | Does `store.Changes()` consume a subscription slot? (§9 rides on it) |
| S4 | First-connect replay volume on production-shaped data |
| S5 | One feeder's throughput at raised `MaxDocsPerBatch` vs an ingestion burst |
| S7 | Hilo id monotonicity per node (demoted to a backstop by §5.7's tick fix) |
| S8 | Cost of RQL `not in` at 1 / 64 / 256 / 1024 terms → `MaxParkedPartitions` default |
| S9 | Distinct actionable `PartitionKey` cardinality on production data at peak |
| S10 | Full-reindex duration for `PartitionKey` on Coverage-sized data |
| **S11** | **Can the drain's time predicate fully replace `WakeUp`?** Deletes the sweeper if yes |
| S12 | `MaxPartitionsInFlight = 4` against `ParseSessionRecipient`'s memory profile |
| S13 | Does `DelayBroadcastAsync` still need a carve-out, now partition-scoped? |
| S14 | Do `Concurrent` lanes need a separate pump implementation? |
| S-R1 | Above what delay must an `Ordered` partition park through a sweeper rather than sleep in place? |
| S-R2 | Per-message or per-handler retry policy? (three handlers, one `NextAttemptAtUtc`) |
| S-R4 | Env-var binding of the scalar ladder — verify "replaced, never appended" |

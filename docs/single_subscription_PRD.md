# PRD — One subscription, partitioned ordering

**Status:** ✅ **implemented** in `10.0.0-preview.72` — PR #363, branch
`feat/single-subscription-partitioned-ordering`. Deploy notes:
[single_subscription_migration.md](single_subscription_migration.md).

**Where the implementation differs from this document**, all decided while building and all
deliberate:

| This document said | What shipped | Why |
|---|---|---|
| A `QueueDelivery` enum with `Ordered` / `Serial` / `Concurrent` | Mode-specific **builder types**: `Ordered()`, `Concurrent(n)`, `Unbounded()` | An enum plus a `MaxConcurrency` property lets "strictly ordered, four at a time" be *written* and then rejected by a validator. Separate builder types give it no method to call. `Serial` was dropped: it named today's accidental behaviour (order preserved except across a retry), which is the bug, not a mode worth keeping |
| `MaxLaneBlock`, enforced at runtime by dead-lettering a head early | `MaxPartitionBlock`, validated at **startup** from the ladder sum | Once blocking is per partition rather than per lane, the budget is knowable before any message flows. Refusing a bad configuration beats silently discarding a message to escape one |
| `RequeueAsync` a blocking dependency | Not implemented; left out of scope | It was only load-bearing because `MaxLaneBlock` dead-lettered early. Nothing is dead-lettered early any more, so nothing needs replaying to be safe |
| A message with no handlers is dead-lettered | **Completed** | Publishing to zero subscribers is a successful publish. Dead-letter is kept for "we tried and failed", so a dead-letter view stays worth reading — a framework lane broadcasts typed messages most applications never subscribe to |
| `DelayBroadcastAsync` unchanged | Gained a `queueName` overload | `BroadcastAsync` had a lane override and the delayed variant did not, so a delayed message could only ever go to its derived lane |
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
    .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))  // MANDATORY — see below
    .Where(m => m.QueueName == lane
             && (m.Status == Pending || m.Status == Failed)
             && (m.VisibleAtUtc == null || m.VisibleAtUtc <= now)   // delayed broadcast only
             && !m.PartitionKey.In(excluded))                      // NOT .Contains() — see below
    .OrderBy(m => m.Sequence)                                       // monotonic long, NOT the id
    .Take(256).ToListAsync(ct);

var seen = new HashSet<string>(StringComparer.Ordinal);
foreach (var msg in window)
    if (seen.Add(msg.PartitionKey))                      // this IS the partition's head
    {
        if (msg.NextAttemptAtUtc > now) Park(msg);       // not due → the partition is blocked
        else if (inFlight.Count < maxPartitionsInFlight) Dispatch(msg);
    }
```

**The retry due-check is deliberately client-side.** Putting `NextAttemptAtUtc <= now` in the `WHERE`
would filter a *parked head* out of the result set, so the next-oldest message of that partition
would become "the first row for that partition" and be dispatched — reintroducing the §8.1 overtake
bug. Correctness would then depend on the pump *remembering* the park, contradicting §5.5's cache
invariant and making "let the drain rediscover it" unsafe. With the check client-side,
**"first row = head" holds unconditionally** and parked-ness is rediscovered from the document rather
than remembered.

`VisibleAtUtc` is a different field with the opposite meaning and therefore stays server-side — see
§5.4.1.

**Three measured constraints on this query — all three were wrong in an earlier draft:**

1. **Order by a monotonic `long`, never by the document id.** `.ThenBy(m => m.Id)` compiles to
   `order by id()`, a **lexicographic** sort over ids that are not zero-padded, so
   `SparkMessages/10-A` sorts before `SparkMessages/2-A`. Measured over 5000 messages sharing one
   `CreatedAtUtc`, server order diverged from insertion order at row 1. This is not a weak tiebreak,
   it is an anti-guarantee, and every ordering promise resting on it would be void. `SparkMessage`
   gains **`Sequence`** (a `long`), issued monotonically by `MessageBus` under one `Interlocked`, and
   the drain orders by it alone. A numeric tiebreak was verified to reproduce insertion order exactly.
2. **`!m.PartitionKey.In(excluded)`, not `!excluded.Contains(...)`.** The latter does not compile —
   `NotSupportedException: Expression type not supported: TypedParameterExpression`. The `In` form
   emits a **parameterized** `not PartitionKey in ($p1)`, so the RQL text is a constant 274 chars at
   every list size.
3. **`WaitForNonStaleResults` is load-bearing, not hygiene.** Without it the drain missed the
   just-written message **18 times out of 20**. With it: zero misses, ~3 ms median.

**The subscription over-delivers, deliberately.** It cannot evaluate time, so it hands the pump a
document with `NextAttemptAtUtc = +1h` about 12 ms after it is written. That is harmless *because the
drain decides due-ness, not the delivery* — so never "optimize" by dispatching straight from the
batch.

### 5.4.1 Two fields, because the meanings are opposite

A *delayed* message and a *backing-off* message both have a future timestamp, and the design needs
them treated in opposite ways:

| Field | Meaning | Filtered | Blocks its partition? |
|---|---|---|---|
| `NextAttemptAtUtc` | retry backoff | client-side, on the head | **Yes** — that is the §8.1 fix |
| `VisibleAtUtc` | delayed broadcast | server-side, in the drain | **No** — a delay is scheduling, not dependency |

Blocking on a delay would mean `DelayBroadcastAsync(m, 5m)` silently freezes everything in `m`'s
partition for five minutes, which no caller could intend. Splitting the field also fixes a latent
bug: today `DelayBroadcastAsync` writes `NextAttemptAtUtc`, so a delayed-but-never-attempted message
is treated as though it were already on a retry rung.

**What the docs promise:** within a partition, two messages both broadcast *without* a delay are
processed in broadcast order, and none is started while an older unfinished non-delayed message of
that partition exists. **No ordering is promised between a delayed message and messages broadcast
during its delay window** — for "run X, then Y five minutes later", broadcast Y from X's handler.
(`LaneOrderMonitor` must exclude not-yet-visible messages, or it reports false violations.)

### 5.4.2 `ParkHorizon` — how long to hold a park in memory

The durable write is unconditional: a failed handler always persists `Status`/`NextAttemptAtUtc`
*before* anything happens in memory (§2.1.2). So the in-memory timer is only an accelerator and a
restart costs at most one drain — the delay length has nothing to do with restart survival.

> **`ParkHorizon`, default 60 s** (≈2× the idle drain interval, per-lane overridable).
> - `delay ≤ ParkHorizon` → **park in memory**: stay in `excluded`, ring the doorbell on a
>   `TimeProvider` timer at `NextAttemptAtUtc`. Preserves rung fidelity for the whole default ladder.
> - `delay > ParkHorizon` → **forget the partition entirely**: drop from `parked` and `excluded`, arm
>   no timer, free the slot. The idle drain rediscovers it and parks or dispatches as due — at worst
>   one idle interval late (negligible on 1 h / 1 d / 7 d rungs).

A park timer rings the doorbell; it never blocks the pump loop. The payoff is on §5.6: long ladders
now cost **zero** memory and zero `not in` terms, so a lane on a 7-day ladder cannot exhaust the
parked set while perfectly healthy, and degraded mode sharpens into what it should mean — *this lane
is failing fast, right now*.

Pushing `excluded` **into the query** rather than filtering client-side is what prevents window
starvation — otherwise 256 messages of blocked partitions hide every runnable one. That is why the cap
in §5.6 is structural, not hygiene: it bounds the `not in` list.

**The drain replaces `WakeUp` — confirmed, so delete the sweeper.** `WakeUp` exists only because
subscription where-clauses cannot evaluate `now()`; a drain is an ordinary query and can. Measured
(S11): a parked message became visible **58 ms** after its due time (the probe's own poll granularity,
not the server's); a brand-new document rang the doorbell after **11 ms** of pump idleness, so a pump
sleeping on `min(NextAttemptAtUtc)` cannot miss a broadcast; and a server-side **patch** rings the
doorbell exactly like an insert — which is precisely the sweeper's own mechanism, so `WakeUp` buys
nothing the write did not already buy.

**`MessageRetrySweeper`, `SparkMessage.WakeUp` and `SparkMessage.LastWakeUpUtc` are deleted.**
Redelivery granularity improves from the sweeper's 30 s to whatever the pump's timer chooses, and
write amplification drops from two patches × 512 messages per sweep to zero.

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

A per-lane cap is needed for **one** reason: it is the only lane-level signal that a dependency is
down (per-partition retry means a dead SMTP host produces N independent ladders, each locally healthy,
with nothing saying "this lane is broken").

An earlier draft also called the cap *structural*, claiming it bounds the `not in` list. **Measured:
it does not need to.** Latency is flat from 1 to 8192 exclusion terms — 11.3 ms to 13.5 ms — even in
the adversarial shape where excluding 8192 contiguous partitions forces the engine to skip 41 000
leading rows of the sort order. The list is parameterized and costs nothing. Keep 256 as a judgement
about signal quality, not performance.

**The window has its own starvation property, and it is not the same as parking.** Distinct partitions
surfaced by one 256-row window over 50 000 messages:

| Partitions | round-robin arrival | contiguous bursts |
|---|---|---|
| 10 | 10 | **1** |
| 1 000 | 256 | **6** |
| 50 000 | 256 | 256 |

Bursty is the realistic shape — a build emitting 50 parse messages back to back — and at 1000 bursty
partitions the window surfaces **six**, of which `MaxPartitionsInFlight = 4` consumes most. The
`excluded` push-down does march the window forward correctly each round, but a partition that is
runnable and *not yet in flight* stays invisible until the older bursts ahead of it drain. So §5.6's
"the block is scoped to that partition only" is true of **parked** partitions and **not** of the
window: a large burst on partition A delays first-touch of partition B. That is FIFO-by-age across
partitions, which is defensible — but **window size and `MaxPartitionsInFlight` are coupled**, and
256/4 leaves no headroom under bursts.

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

### 5.8 Concurrency defaults, and two invariants CodeCoverage silently relies on

**Framework default for an `Ordered` lane that declares nothing:
`Math.Clamp(Environment.ProcessorCount / 2, 1, 4)`** — a fixed literal is a guess against an unknown
machine, and a 1-vCPU VPS must never get 4.

**`coverage-parse-session`: start at `MaxPartitionsInFlight(2)`**, raised only after measurement.
`ParseSessionRecipient.ReadAttachmentText` holds the gzip `byte[]`, a `MemoryStream` copy, the
decompressed `byte[]` and a UTF-16 `string` simultaneously — ~4× the report size, all on the LOH — and
then parses while the string is still alive. Parsing is CPU-bound, so on a small host four concurrent
parses do not raise throughput; they stretch each parse's wall clock, widening the reaper's lease
window for no gain. Two prerequisites before raising it: stream the attachment through `GZipStream`
straight into the parser (that removes the 4× multiplier and buys more than any concurrency setting),
and give `coverage-app` a `mem_limit`/`cpus` in `docker-compose.yml` — until the container has a
declared budget, Server GC sizes itself against the whole VPS and every concurrency default is
unfalsifiable, with RavenDB co-resident on the same host.

Two invariants that must be written down, because the refactor changes *why* they hold:

1. **`ParseSessionRecipient`'s class comment is about to become false.** It says the read-modify-write
   on `FileCoverage` needs no locking because "sessions of a queue are processed strictly FIFO
   (MaxDocsPerBatch=1)" — queue-FIFO is exactly what this design removes. What preserves it afterwards
   is different and must be stated: two `ParseSessionMessage`s for the same build share partition
   `BuildId`, and `FileCoverage` ids are `{buildId}/…`, so **distinct partitions touch disjoint
   document-id spaces**.
2. **A new concurrency that queue-FIFO used to make unrepresentable.** `AssembleCommitMessage` is on
   the same lane but partitioned by `CommitId`, so it can now run alongside a `ParseSessionMessage`
   for a *sibling build of the same commit*. It is safe only because
   `CommitAssembler.LoadContributingBuilds` filters `.Where(b => b.Status == "Finalized")` and a build
   mid-parse is not finalized. The race is closed by an incidental predicate, not by the partitioning
   — so it needs a test, or deleting that `.Where` silently reintroduces it.

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

**The array trap is designed out**, and it is worse than previously recorded. Measured against
`Microsoft.Extensions.Configuration` (JSON + env + binder), a `TimeSpan[]` fails **three** ways:

| Trap | Observed |
|---|---|
| Non-empty default survives binding | default `[5s,30s,2m]` + JSON `["1m","5m"]` → **5 elements** |
| **Two config layers overlay element-wise** | base `[1m,5m,1h]` + override `[7s]` → **`[7s,5m,1h]`** — a ladder nobody wrote |
| **Re-binding the same object doubles it** | `[1m,5m]` bound twice → **`[1m,5m,1m,5m]`** |

The second is the dangerous one: it needs no non-empty initializer at all, so a shorter
`appsettings.Production.json` override silently inherits the tail of the base. A **scalar string** is
immune to all three — replaced by the last source, idempotent under re-bind, no element positions to
overlay. `BackoffDelays` / `DefaultBackoffDelays` / `ResolvedBackoffDelays` disappear.

Two further measured facts. **"Policies taken whole" is not what the binder does by default**: it
reuses an existing dictionary entry *object* and merges fields into it, so a partial override of a
key the code seeded keeps the code's other fields while a newly-introduced key gets nulls. To mean
what §6 says, bind config into a **fresh** dictionary and resolve `configured[name] ?? codeDefault[name]`
at read time. And **env keys bind case-insensitively**
(`…RetryPolicies__email__delays` → `Delays`), so build the policy dictionary with
`StringComparer.OrdinalIgnoreCase` and document the lowercase form ops will actually type.

**One decision point**, deliberately narrow:

```csharp
public interface IRetrySchedule
{
    /// <param name="attempt">Attempts already made, INCLUDING the one that just failed. 1-based.</param>
    RetryDecision Next(int attempt);
}
public abstract record RetryDecision
{
    public sealed record RetryAfter(TimeSpan Delay) : RetryDecision;
    public sealed record DeadLetter(string Reason)  : RetryDecision;
}
```

No clock (returning a *delay* keeps the schedule a pure value — constructible from config, printable
in the startup table, and testable with no server and no fake clock); no exception (non-retryability
is already a separate working concern); no queue name (the instance is already resolved per lane).

**The schedule is per message; the attempt counter and the dead-letter decision stay per handler**,
and the message's `NextAttemptAtUtc` is the **`max`** over retrying handlers. Per-handler *timing* is
unobservable under `Ordered` — the partition unblocks only when the last handler is terminal, so the
moment it frees is the `max` either way — and per-handler policy would need a declaration surface
that contradicts "ops owns the numbers" (ops cannot name a CLR type in appsettings). `max` not `min`:
handlers can sit on different rungs, and `min` would shorten a ladder and invalidate the
`MaxPartitionBlock` sum.

This collapses the three drifted sites: `:266`'s `>= MaxAttempts` becomes
`schedule.Next(++handler.AttemptCount) is DeadLetter`; `:336` becomes `now + max(delay)`; `:297` uses
the same schedule with the message-level counter. `SparkMessage.MaxAttempts` — a policy snapshot
taken at broadcast — is deleted, or `RetryOverride` would not reach in-flight messages.

**Ladder exhaustion dead-letters; it does not clamp** (`RepeatingLastRung` is opt-in). **Jitter**
defaults to 0, only ever shortens, and is rejected on `Ladder`; it earns its place across lanes — five
lanes backing off against one GitHub 5xx — not within one.

**New default:** `Ladder("5s 30s 2m")`, worst case ≈2m35s, every rung reachable.

### 6.1 One pump, not two

**`Concurrent(n)` is exactly `Ordered` with the partition key set to the message's own id and
`MaxPartitionsInFlight = n`.** Every message its own partition ⇒ no two share a partition ⇒ no
ordering constraint ⇒ up to `n` in flight ⇒ the exclusion set is a set of message ids, which is
precisely "exclude in-flight messages". `Unbounded()` is the same with a large ceiling. That is not a
coincidence — it is the far end of the cardinality axis §5.0 argues from.

Implementation: unordered lanes write `PartitionKey = ""` and the pump substitutes the document id.
One `if`, in one place. The builder types stay separate — that is where "illegal states unspellable"
lives — but both compile down to one `LanePlan` and one pump.

The `Ordered` pump is the *deeper* module: same interface width, strictly more hidden (ordered
window, first-row scan, park/wake, exclusion, degraded mode, recovery). A separate `Concurrent` pump
would implement a subset behind an interface of equal width — the textbook signal for
parameterization over a second class. And with one implementation, every `Concurrent` lane in every
demo app is extra coverage for the `Ordered` path; with two, the rarer one rots. This repo has
already paid for that failure mode once.

### 6.2 Requeue semantics

A requeue **re-enters at requeue time**, as a new broadcast of an old payload. Rewinding
`CreatedAtUtc` would re-order a partition around a document the invariant has already stepped past,
and produce a state the head-scan is not designed for (a `Pending` row older than `Completed` rows in
its partition). Entering as the *youngest* message means it can never displace anything, by
construction. Issue the new `CreatedAtUtc` through the same monotonic counter; keep the original in
`OriginallyCreatedAtUtc`.

Message: `Status → Pending`; `NextAttemptAtUtc`, `CompletedAtUtc → null`; message-level
`AttemptCount → 0`; **`@metadata.@expires` removed** — the single easiest field to forget and the only
one whose omission loses data, since a dead-lettered message was stamped for retention deletion.
`PayloadJson`, `MessageType`, `QueueName`, `PartitionKey` **untouched** — never re-run the selector,
or a changed selector moves the message between partitions. Handler list membership untouched — do
not re-derive from DI.

Per handler, **only where `Failed` or `DeadLettered`**: `Status → Pending`, `AttemptCount → 0`,
`LastError → null`, and **`Checkpoint` kept** (that is §2.1.5 — a long handler resumes rather than
restarts; `resetCheckpoints: true` for when the checkpoint is the problem). Handlers already
`Completed` are untouched in every field — the §2.1 guarantee, enforced here as well as at
`:162-166`. Refuse a requeue while `Processing`; a requeue of an already-`Pending` message is a no-op.

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
swap stays a one-class change.

**S2 answered: `Changes()` consumes no subscription slot.** On an unlicensed server saturated at the
cap of 3, `Changes().EnsureConnectedNow()` succeeded in 48 ms, delivered collection notifications, did
not change the server's subscription count, and a second independent connection also worked. The
option is viable on the licence axis whenever we want it.

**Do not write a test asserting the cap trips at exactly N.** One unreproduced run created **ten**
subscriptions against a cap of three, on a server up 70+ minutes — outside the known ~60–70 s startup
grace, and not reproducible across eight subsequent attempts.

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
3. **Adding `PartitionKey`/`Sequence` needs a startup gate — not a hand-rolled side-by-side index.**
   Measured (S10): RavenDB *already* builds `ReplacementOf/SparkMessages/ByQueue` and swaps it in, and
   the old shape keeps serving throughout (302 drains, 0 failures, 6.2 ms median). But for the ~4.5 s
   swap window (50 000 documents) a query on the **new** field throws
   `RavenException: The field 'PartitionKey' is not indexed` in 3–21 ms — it does **not** wait and
   `WaitForNonStaleResults` does **not** block. So a deploy shipping new pump code and the new index
   together throws on *every* drain for that window, and the pump's error handling decides whether
   that is a blip or a crash loop. **Gate pump startup on the new field being queryable.**
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

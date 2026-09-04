# Plan — One subscription for all message queues

**Status: ✅ complete.** Shipped in `10.0.0-preview.72` as PR #363, branch
`feat/single-subscription-partitioned-ordering`. Every milestone below is committed.

Companion to [single_subscription_PRD.md](single_subscription_PRD.md); deploy notes in
[single_subscription_migration.md](single_subscription_migration.md). One pull request, per repo
convention.

Shipped design: one feeder subscription ringing per-lane doorbells, per-lane pumps draining
`SparkMessages_ByQueue` **ordered by `Sequence`** — not `CreatedAtUtc, Id`, see S7 — with ordering
scoped to a **partition key**, and sync actions folded into `SparkMessages` on a single `spark-sync`
lane partitioned by document id.

## Spikes — all run; what each one changed

Against local RavenDB, and for anything cap-related a throwaway **unlicensed** container: the dev
server carries a Developer licence with no cap and would have answered every cap question wrongly.

| # | Answer |
|---|---|
| S1 | Reproduced: `[enter:m1, fail:m1, enter:m2, exit:m2, enter:m1, exit:m1]`. Kept as `MessageOrderingRegressionTests` |
| S2 | The changes API consumes **no** slot — it connected at a saturated cap. The no-subscription option stays viable |
| S3 | `WaitForNonStaleResults` is mandatory: without it the drain missed the just-written message **18 times in 20** |
| S4 | Replay is bounded and now harmless — orphan messages complete on sight. Covered in the migration doc |
| **S5** | **Not run at production burst scale.** See "Still open" below |
| S6 | Delete frees a slot immediately, Disable does not; `LicenseLimitException` is thrown at create; progress is per batch |
| **S7** | **`.ThenBy(m => m.Id)` is broken** — lexicographic `order by id()` over ids that are not zero-padded. Replaced by a monotonic `Sequence` |
| S-R1 | The durable write always happens first, so a restart costs one drain; `ParkHorizon` (60s) governs memory only |
| S-R2 | Schedule per message, counter per handler, `max` over handlers. `IRetrySchedule.Next(int)` |
| S-R3 | Moot — the partition-block budget is validated at startup from the ladder sum, so nothing is measured at runtime |
| S-R4 | Confirmed, plus two traps not previously recorded: config layers overlay arrays **element-wise**, and re-binding doubles them |
| S-R5 | Moot — requeue left the critical path |
| S8 | Flat from 1 to 8192 exclusion terms. `MaxParkedPartitions` is a signal, not a performance limit |
| **S9** | Under bursty arrival a 256-row window surfaces few partitions; window size and `MaxPartitionsInFlight` are coupled. **Not tuned** — see "Still open" |
| S10 | RavenDB already deploys side by side; the real hazard is that queries on a **new field throw** for ~4.5s rather than waiting. The pump retries |
| **S11** | **Yes** — `MessageRetrySweeper`, `WakeUp` and `LastWakeUpUtc` are deleted |
| S12 | `MaxPartitionsInFlight(2)` on ingestion rather than 4: a parse holds ~4× the report size on the LOH, with RavenDB co-resident |
| S13 | Split into `VisibleAtUtc` (server-side, overtakable) and `NextAttemptAtUtc` (client-side, blocking) |
| S14 | One pump. `Concurrent(n)` **is** `Ordered` with the partition key set to the message's own id |

<details>
<summary>The spike questions as originally planned</summary>


| # | Question | Why it gates | Invalidates if… |
|---|---|---|---|
| **S1** | Reproduce the §5.1 reordering: fail M1 once, confirm M2 overtakes it | Confirms the fix framing and gives us a regression test to keep | — |
| **S2** | Does `store.Changes()` consume a subscription slot? | The whole deferred no-subscription option (PRD §7) rides on it | If it does, §7 dies and the feeder is permanent |
| **S3** | `WaitForNonStaleResults` on the lane drain: latency cost and staleness behaviour under load | The pump's correctness depends on not seeing M2 before M1 is indexed | If too slow, ordering needs the sequence number and a different read path |
| **S4** | First-connect replay volume on production-shaped data | Largest migration risk — a fresh subscription starts at CV zero | If unbounded, `LastDocument` becomes mandatory |
| **S5** | One feeder's throughput at raised `MaxDocsPerBatch` vs an ingestion burst | Decides whether PRD §7 is optional or urgent | If it cannot keep up, §7 moves into scope |
| ~~S6~~ | ~~Slot freeing, exception type, mid-batch failure~~ | **DONE** — see PRD §4.0 | — |
| **S7** | Confirm hilo id monotonicity per node | `CreatedAtUtc, Id` ordering depends on it | If false, sequence number is mandatory now |
| **S-R1** | Above what delay must an `Ordered` lane park through the sweeper instead of sleeping in place? | An in-process sleep does not survive a restart — fine for 5 s, wrong for 7 d | Highest-value open item |
| **S-R2** | Per-message or per-handler retry policy? | Three handlers have three `AttemptCount`s but one `NextAttemptAtUtc` (`:336`). Under `Ordered` the lane blocks on the *message* | Fixes `IRetrySchedule`'s signature |
| **S-R3** | Does `MaxLaneBlock` count parked time only, or parked + execution? | `ParseSessionRecipient` runs for minutes; counting execution could trip a 5-min budget on a *healthy* lane | Budget counts parked; metric counts both |
| **S-R4** | Env-var binding of the scalar ladder (`Spark__Messaging__RetryPolicies__email__delays`) | The whole trap-avoidance rides on "a scalar string is replaced, never appended" | Cheap; verify anyway |
| **S-R5** | Does a requeued message re-enter at `CreatedAtUtc` or at requeue time? | Under `Ordered` this is a correctness question, not UX | — |
| **S8** | Cost of RQL `not in` at 1 / 64 / 256 / 1024 terms | `MaxParkedPartitions` default | If it degrades early, exclusion moves client-side and window starvation returns |
| **S9** | Distinct actionable `PartitionKey` cardinality on production data at peak | Whether a 256-row window can starve a partition | — |
| **S10** | Full-reindex duration for `PartitionKey` on Coverage-sized data | Side-by-side index vs accepting a first-drain stall | **Largest new production risk** |
| **S11** | Can the drain's `NextAttemptAtUtc <= now` predicate fully replace `WakeUp`? | Deletes `MessageRetrySweeper`, `WakeUp`, `LastWakeUpUtc` | **Largest simplification available** — changes M4b/M4c scope |
| **S12** | `MaxPartitionsInFlight = 4` vs `ParseSessionRecipient`'s memory profile | Whether the real cap is CPU or resident reports | — |
| **S13** | Is the `DelayBroadcastAsync` carve-out now partition-scoped? | Restate rather than inherit it | — |
| **S14** | Do `Concurrent` lanes need their own pump implementation? | One pump or two | — |

</details>

### Settled by the spike (PRD §4.0) — do not re-investigate

`@all_docs` is rejected on cost (44× catch-up), so sync actions must move into the `SparkMessages`
collection. `IgnoreSubscriberErrors` is rejected outright — it silently drops documents. Progress is
per-batch only. Delete frees a slot immediately; **Disable does not**. `LicenseLimitException` is
thrown at create time. Concurrent opening strategy is licence-gated, so one subscription means one
worker.

**Two traps that affect this plan directly:**

- The cap is **unenforced for ~50–70 s after server start**, which is probably how the per-queue
  design survived local runs for so long.
- **`localhost:8080` and CI both use a Developer licence with no cap and concurrent subscriptions
  enabled.** Cap behaviour cannot be tested there. Most tests below are licence-independent; any that
  is not must say so rather than passing vacuously.

## Milestones

### ✅ M0 — Regression tests that fail today

Written before the fix, so the bug is provably real. Full design in §Testing below.

- `Ordered_lane_does_not_start_M2_while_M1_is_retrying` — **the ordering test**; fails today.
- Dead-letter path does not abandon the rest of the batch at `MaxDocsPerBatch > 1` — fails today.
- `BroadcastAsync(msg, queueName)` with a name no recipient declares is delivered — fails today.
- A `Processing` document stranded by a crash is re-offered — fails today.
- `Five_lanes_produce_exactly_one_RavenDB_subscription` — fails today (produces N).

### ✅ M0b — `TimeProvider` (commit 4118665b)

Prerequisite for every timing test. Inject into `MessageBus` (`CreatedAtUtc` — **this is the ordering
key**, so a real producer clock against a fake pump clock makes every ordering test meaningless),
the feeder and lane pumps (`NextAttemptAtUtc`, `CompletedAtUtc`, `SetExpiration`, and the `Ordered`
sleep-until-retry, which must be `timeProvider.Delay`), `MessageRetrySweeper` (**both** its poll loop
*and* its `now` — injecting one and not the other is worse than neither), the M1 reaper, and the
`SparkSubscriptionWorker` reconnect delay. Register `TimeProvider.System` in `AddSparkMessaging`; add
`Microsoft.Extensions.TimeProvider.Testing` to the test project.

### ✅ M1 — The reaper (commit 7593e998)
Written and tested first, because everything else assumes it. Re-offers `Processing` documents older
than a lease. Decide the lease against `ParseSessionRecipient`, which runs for minutes: a generous
fixed lease, or `IMessageCheckpoint.SaveAsync` as a liveness heartbeat.

### ✅ M2 — Fail loudly on subscription creation
Port the `EnsureSubscriptionExistsAsync` hardening from `fix/coverage-queue-licence-cap` verbatim:
`LicenseLimitException` fatal with an actionable message; stop discarding `createException`; a
create+update double failure logs Error naming the dead queue, not Warning.

### ✅ M3 — The feeder and the lanes (commit 9bd62b6b)
Delete `MessageSubscriptionManager` and its `IServiceCollectionAccessor` use. `MessageSubscriptionWorker`
becomes a single hosted service with no queue parameter; `SubscriptionName` becomes the constant
`SparkMessaging`; the query loses its `QueueName` clause. Per-lane doorbell channels and pumps.
Fix the `return` → `continue` batch-abandonment bug and the undisposed per-document sessions.
Move `QueueNames.IsValid` to producer-side validation in `MessageBus`.

### ✅ M4 — Ordering (commit 9bd62b6b)
`QueueDelivery` enum, `Ordered` default, drain ordered by `CreatedAtUtc, Id` with
`WaitForNonStaleResults`. Under `Ordered`, sleep-until-`NextAttemptAtUtc` and retry in place rather
than going through `WakeUp`. M0's FIFO test must now pass.

### ✅ M4 — Partitioned ordering (commit 9bd62b6b)
`PartitionKey` on `SparkMessage`, resolved **producer-side** by a selector declared once at lane
registration and persisted (the selector must be pure — nothing re-runs it). The builder API with
mode-specific types (`IOrderedQueueBuilder` / `IConcurrentQueueBuilder`) so `Ordered` +
`MaxConcurrency` is **unspellable**. `OrderedLaneState` (inFlight / parked / wakeOrder / doorbell),
the drain query with server-side `not in` exclusion, `MaxPartitionsInFlight`, `MaxParkedPartitions`
with degraded mode. `CreatedAtUtc = max(UtcNow, lastIssued + 1 tick)` in `MessageBus`.
Startup validation: every type on an `Ordered` lane has a selector; a lane declared twice is fatal;
an undeclared lane is `Concurrent(1)`, never `Ordered`.

### ✅ M4e — Graceful shutdown: lanes are waited for, not just cancelled (follow-up)

Neither `MessageFeeder.OnWorkerStoppedAsync` nor the test host waited for the lane pumps. Both
cancelled a token and returned. The pumps are fire-and-forget loops and `DispatchAsync` starts further
un-awaited work, so "stopped" meant only "asked to stop": handlers carried on running — opening
sessions, issuing queries — against a document store the host was already disposing.

**In production** that abandons in-flight work mid-message with no record of why. **In the test suite**
it showed up as something apparently unrelated: `RavenTestDriver` deletes each test's database on
store disposal, that delete blocks on cluster confirmation, and the confirmation timed out because
queries were still arriving at the database being deleted. The failure was then reported against
whichever test happened to be disposing. Every teardown timeout observed in this work landed on a
pump-using test, including the first one, before any of the new tests existed.

Fixed in three places: the pump drains work it dispatched (15s bound) after its loop exits; the feeder
awaits its pump tasks (30s bound); the test host awaits its pumps before the fixture tears the
database down.

**A wrong turn worth recording.** The first attempt made `SparkTestDriver.DisposeAsync` tolerate the
deletion timeout, on the theory that a late acknowledgement was harmless housekeeping — the raft
command having already been committed. That reasoning was not wrong about RavenDB, but it was wrong
about the cause, and it would have hidden the next occurrence of a real shutdown defect. The
suppression was removed once the cause was found, and `SparkTestDriver` carries a comment saying so,
because the failure is the only signal this class of bug produces.

### ✅ M4d — Lanes as service registrations (follow-up)

The first version of the lane API built declarations **eagerly**, inside `AddMessaging`, while the
service collection was still being assembled. Two problems followed: a lane could not be configured
from options or any other service — every value had to be a literal — and a framework package had no
way to declare its own lane except reaching into `IServiceCollection` for an already-constructed
registry, which worked only if `AddMessaging` had run first and silently did nothing otherwise.

Lanes are now `ILaneConfigurator` singleton registrations (`AddSparkLane`), resolved by
`LaneRegistry` on first use. Three shapes: a delegate, a delegate taking `IServiceProvider`, or a
class the container constructs. Replication uses the last, reading its concurrency and retry ladder
from `SparkReplicationOptions`. Registration order stops mattering.

Lifetimes are deliberate: configurators and the registry are **singletons** and plans are cached,
because what a lane declares is process-wide data. Resolving scoped objects and caching them past
their scope would be a captive dependency; request-shaped state belongs in handlers, which are scoped
and resolved per message. The one behavioural consequence is that a duplicate lane now surfaces at the
startup validation pass rather than at the `AddLane` call.

### ✅ M4b — Per-queue retry policy (commit 7593e998)
`RetrySchedule.Ladder/Linear/Exponential/None` with `maxAttempts` derived for ladders,
dictionary-shaped config with the **scalar** ladder string, the `RetryOverride` single switch, and
`MaxPartitionBlock` **validated at startup** from the ladder sum (`.AcceptPartitionBlock(...)` to
override). Collapse the three drifted delay call sites into one `IRetrySchedule`; delete
`SparkMessage.MaxAttempts`, `BackoffDelays`, `DefaultBackoffDelays`, `ResolvedBackoffDelays`.
New default `Ladder("5s 30s 2m")`. Print the resolved schedule table at startup.

### ⛔ M4c — Requeue: NOT implemented, and no longer needed
`IMessageBus.RequeueAsync(id)` as an operator tool. It left the critical path when `MaxPartitionBlock`
became a startup validation rather than runtime early dead-lettering. Must not re-run handlers already
`Completed` (PRD §2.1). Settle S-R5 first.

**Superseded by M4:** `Delivery` / `MaxConcurrency` / `Retry` on `[MessageQueue]` (policy moves to the
builder; the attribute keeps its single argument and all 12 existing declarations compile unchanged),
analyzer `SPARK011` for `Ordered` + `MaxConcurrency` (the state becomes unspellable), and
`MaxLaneBlock` with runtime `LaneBlockBudgetExceeded` dead-lettering.

### ✅ M5 — Fold sync actions in (commit 711fa72c)
Delete `SparkSyncAction`, `SparkSyncActions_ByStatus`, `SyncActionRetrySweeper`,
`SyncActionSubscriptionWorker`. `SyncActionInterceptor` broadcasts `SyncActionMessage` on
`spark-sync-{Collection}`; a framework `IRecipient<SyncActionMessage>` holds the POST logic with
400/404 → `NonRetryableException`. **Preserve verbatim:** mTLS module identity, the `RequestingModule`
certificate gate in `SyncApply`, terminal-on-400/404. Verify with `apps/HR` and `apps/Fleet`.

### ✅ M6 — Consumers (commit 3c449cbd)
Restore CodeCoverage's five distinct queue names as lane names (they cost nothing now) and choose
`Delivery` per lane. Decide the `SubscriptionWorkerRegistrationGenerator`'s fate — today it is inert,
and after this change a consumer-defined worker would spend one of three slots, so it should either
be deleted or made to fail loudly.

### ✅ M7 — Docs (commit 6990d355)
Rewrite, not touch up: `docs/prd/PRD-SubscriptionWorker.md` §8 (its §8.2 title inverts, and §8.1
needs the recorded answer from PRD §5), `libs/messaging/…/README.md:177-183,242-244`,
`docs/prd/PRD-Messaging.md:74`, `docs/prd/PRD-cross-module-sync.md:128`,
`docs/prd/PRD-Messaging-Improvements.md`, and the message doc-comments that claim "its own queue".
Correct `docs/code-coverage/upload-api.md:102` — the guarantee becomes true at M4, and the doc should
not have promised it before.

### ✅ M8 — Version bump and migration notes (commit d1a7b7cc)
1. Delete old per-queue subscriptions on every database including production `Coverage`.
2. Apply the S4 decision on the subscription start position.
3. Run the reaper once at startup for anything stranded by the deploy.
4. **Throttle or supervise the `coverage-delete-pr-builds` backlog** — that queue has never run, so
   first success deletes every merged-PR build at once.
5. Convert or drop `SparkSyncAction` documents (check whether the collection is non-empty first;
   CodeCoverage does not enable replication, so it is likely empty there).
6. One lockstep version bump across the 22 packages — major stays `10` (platform-lockstep rule).

### ⏳ M9 — Verify in production: NOT done, awaits deploy
Confirm exactly one subscription exists on the `Coverage` database, that all six lanes deliver, and
that the PR comment appears on `opened`. The dogfood PR #362 and the browser (playwright) are the
verification path.

## Testing — what shipped

**Two determinism rules, applied without exception.** Never assert the absence of an event within a
time window: convert every negative ("M2 has not run yet") into an ordering assertion over a
*finished* log. And keep any timing dependence in how reliably a test detects *today's* bug, never in
whether correct code passes — a correct pump cannot emit an out-of-order log at any speed, so these
cannot flake on a slow machine.

One draft test broke that rule and is instructive: it asserted that a follower had not started at the
moment the head failed, and it **passed vacuously** because the follower had not been delivered yet.
It was deleted rather than kept.

| File | Covers |
|---|---|
| `MessageOrderingRegressionTests` | Ordering across the retry path (**failed on this branch's first commit**), partition isolation, and that a succeeded handler is never re-invoked |
| `MessagePipelineE2ETests` | The handler contract end to end: happy path, no-recipient → **Completed**, non-retryable → dead-lettered handler, ladder exhaustion, mixed handlers, retry-then-succeed, delayed broadcast |
| `MessageRetryScheduleE2ETests` | Increasing ladder (`1s 2s 3s` → dead-letter, 4 attempts, gaps that grow), flat ladder (`2s ×5` → 6 attempts), the configured default, **two lanes waiting their own backoff**, and the global override flattening a declared ladder |
| `MessagingSubscriptionCountTests` | **Five lanes produce exactly one subscription**, and every lane — including an ad-hoc name no recipient declares — is served by it |
| `LaneRegistryTests` | What startup refuses, each rule present because its alternative is silent |
| `MessageReaperTests` | A message stranded in `Processing` past its lease is returned; one inside its lease is left alone |
| `MessageSequenceTests` | The ordering key: monotonic within a tick, under a backwards clock, and under concurrent callers |
| `SparkMessagingOptionsBindingTests` | The three measured configuration traps, and the global override |

**The cap itself is deliberately not tested.** Two independent reasons: the dev server and CI both
carry a **Developer** licence with no cap, so the assertion would pass vacuously wherever it runs; and
even against a capped server the limit is unenforced for roughly the first minute after startup. The
cap is environmental. What the framework owes is "create one subscription", which
`MessagingSubscriptionCountTests` checks directly, instantly, and independently of any licence.

**`FakeTimeProvider` is used in the unit-level tests** (`MessageSequenceTests`, `MessageReaperTests`)
but not in the E2E ones: those drive real short intervals, because the pump's wait path and RavenDB's
own delivery both participate and a fake clock would only fake half of it.

## Disposition of `fix/coverage-queue-licence-cap`
Keep the framework hardening (→ M2) and the producer-side webhook test. Drop `CoverageQueues.cs`,
its consolidation of five queues onto two, `CoverageQueuesTests.cs`, and its 22 version bumps. See
PRD §10.

## Review comment, addressed — `MessageLanePump.DispatchAsync` task tracking

Raised in review, on the in-flight tracking added for graceful shutdown:

> Why don't you just call `await work;` here? What a strange code construct is this? This code seems
> prone to deadlocks and memory-exceptions.

The construct in question:

```csharp
Task? work = null;
work = Task.Run(async () =>
{
    try { /* … process … */ }
    finally { if (work is not null) dispatched.TryRemove(work, out _); }   // closes over its own Task
});

dispatched[work] = 0;
if (work.IsCompleted) dispatched.TryRemove(work, out _);   // patches the race above
```

The criticism is correct. The lambda closes over the very variable holding its own `Task`, which is
assigned only *after* `Task.Run` returns — so a body that finishes first sees `work == null`, removes
nothing, and leaves a completed task in `dispatched` forever. The trailing `IsCompleted` check exists
only to patch that race, which is the tell: a construct needing a second mechanism to fix the first
is the wrong construct.

**Why `await work` is not the answer**, and this is the part worth keeping: `DispatchAsync` is called
from the drain loop once per runnable partition. Awaiting there would serialize the partitions and
defeat `MaxPartitionsInFlight` — one slow handler would again stall its lane's other partitions,
which is the entire property this design exists to provide. The work genuinely must outlive the loop
iteration that starts it; only its *tracking* needed fixing.

**What was applied.** The body moved into a plain `private async Task RunHandlerAsync(...)`, which no
longer deregisters itself; the caller registers the handle it was handed and prunes finished ones on
each dispatch:

```csharp
var work = Task.Run(() => RunHandlerAsync(messageId, partition, cancellationToken), CancellationToken.None);

foreach (var finished in dispatched.Keys)
    if (finished.IsCompleted)
        dispatched.TryRemove(finished, out _);

if (!work.IsCompleted)
    dispatched[work] = 0;
```

No self-reference and no race to patch. The set is now bounded by concurrency rather than by
throughput, which answers the memory concern directly: a lane running *n* partitions holds at most
*n* handles plus those that finished since the last dispatch, and the set only has to be accurate at
shutdown.

One deviation from the shape sketched during review: the `Task.Run` stays. Dropping it was proposed on
the grounds that an async method returns at its first `await` — true of the framework's own code, but
`ProcessAsync` runs *user* handler code, and a handler doing synchronous work before its first await
would run that work on the lane's drain loop and stall the lane's other partitions. The thread-pool
hop is what keeps the loop off it. It is not there for the self-reference, so removing the one did not
require removing the other.

**On deadlocks:** I could not find one — handler bodies take `stateLock` only briefly and
`DrainInFlightAsync` awaits them without holding it — but the construct was opaque enough that the
question was fair, and the simplification removes the need to reason about it at all.

## Still open after this PR

Recorded rather than quietly dropped.

| Item | State |
|---|---|
| **M9 — verify in production** | Not done, and cannot be from a workstation. After deploy: confirm exactly one subscription on the `Coverage` database, then open a pull request against a tracked repository and confirm the coverage comment appears on `opened` — the symptom that started this work |
| **S5 — feeder throughput under a real ingestion burst** | Not measured. The feeder only rings doorbells, so the risk is low, but "low" is a judgement, not a measurement. If ingestion ever lags, measure this before tuning anything else |
| **S9 — window size vs `MaxPartitionsInFlight`** | Measured but **not tuned**. Under bursty arrival a 256-row window surfaces few distinct partitions, and the two settings are coupled. Today's values are defensible, not optimised |
| **Verification against a Community-tier server** | Everything is green against a **Developer** licence, which has no subscription cap. The property that matters — N lanes produce exactly one subscription — is asserted directly and is licence-independent, but the framework has not been exercised against the tier production actually runs |
| **`SubscriptionWorkerRegistrationGenerator`** | Still emits an `AddSubscriptionWorkers()` that nothing calls. It was inert before this work and remains inert. Now that the framework owns the only subscription, a consumer-defined worker would spend one of three slots — so it should either be deleted or made to fail loudly. Left alone deliberately: it is a separate decision from this change |
| **`RequeueAsync`** | Not implemented (M4c). It was only load-bearing while the guardrail dead-lettered early; it no longer does. Still a reasonable operator tool one day |
| **Metrics** | The PRD sketches lane-blocked duration, parked-partition gauges and dead-letter reasons. Only structured logging shipped. A stalled lane is diagnosable from logs, not from a dashboard |

## Out of scope
- Multi-node message processing (needs PRD §7).
- Replacing the subscription with poll + changes-API doorbell — deferred, but M3 must keep the feeder
  as the only component that knows how an id arrives, so the swap stays a one-class change.

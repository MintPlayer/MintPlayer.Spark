# Plan — One subscription for all message queues

Companion to [single_subscription_PRD.md](single_subscription_PRD.md). One pull request, per repo
convention: every fix below lands together, including the production data cleanup.

Target design (PRD §4): **B′ + C** — one feeder subscription ringing per-lane doorbells, per-lane
pumps draining `SparkMessages_ByQueue` ordered by `CreatedAtUtc, Id`, `QueueDelivery.Ordered` by
default, sync actions folded in as `spark-sync-{Collection}` lanes.

## Spikes (do these first; they can invalidate milestones)

Run against the local RavenDB at `http://localhost:8080` (dev data, disposable databases).

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

### M0 — Regression tests that fail today

Written before the fix, so the bug is provably real. Full design in §Testing below.

- `Ordered_lane_does_not_start_M2_while_M1_is_retrying` — **the ordering test**; fails today.
- Dead-letter path does not abandon the rest of the batch at `MaxDocsPerBatch > 1` — fails today.
- `BroadcastAsync(msg, queueName)` with a name no recipient declares is delivered — fails today.
- A `Processing` document stranded by a crash is re-offered — fails today.
- `Five_lanes_produce_exactly_one_RavenDB_subscription` — fails today (produces N).

### M0b — `TimeProvider`

Prerequisite for every timing test. Inject into `MessageBus` (`CreatedAtUtc` — **this is the ordering
key**, so a real producer clock against a fake pump clock makes every ordering test meaningless),
the feeder and lane pumps (`NextAttemptAtUtc`, `CompletedAtUtc`, `SetExpiration`, and the `Ordered`
sleep-until-retry, which must be `timeProvider.Delay`), `MessageRetrySweeper` (**both** its poll loop
*and* its `now` — injecting one and not the other is worse than neither), the M1 reaper, and the
`SparkSubscriptionWorker` reconnect delay. Register `TimeProvider.System` in `AddSparkMessaging`; add
`Microsoft.Extensions.TimeProvider.Testing` to the test project.

### M1 — The reaper
Written and tested first, because everything else assumes it. Re-offers `Processing` documents older
than a lease. Decide the lease against `ParseSessionRecipient`, which runs for minutes: a generous
fixed lease, or `IMessageCheckpoint.SaveAsync` as a liveness heartbeat.

### M2 — Fail loudly on subscription creation
Port the `EnsureSubscriptionExistsAsync` hardening from `fix/coverage-queue-licence-cap` verbatim:
`LicenseLimitException` fatal with an actionable message; stop discarding `createException`; a
create+update double failure logs Error naming the dead queue, not Warning.

### M3 — The feeder and the lanes
Delete `MessageSubscriptionManager` and its `IServiceCollectionAccessor` use. `MessageSubscriptionWorker`
becomes a single hosted service with no queue parameter; `SubscriptionName` becomes the constant
`SparkMessaging`; the query loses its `QueueName` clause. Per-lane doorbell channels and pumps.
Fix the `return` → `continue` batch-abandonment bug and the undisposed per-document sessions.
Move `QueueNames.IsValid` to producer-side validation in `MessageBus`.

### M4 — Ordering
`QueueDelivery` enum, `Ordered` default, drain ordered by `CreatedAtUtc, Id` with
`WaitForNonStaleResults`. Under `Ordered`, sleep-until-`NextAttemptAtUtc` and retry in place rather
than going through `WakeUp`. M0's FIFO test must now pass.

### M4 — Partitioned ordering (PRD §5.3–5.7)
`PartitionKey` on `SparkMessage`, resolved **producer-side** by a selector declared once at lane
registration and persisted (the selector must be pure — nothing re-runs it). The builder API with
mode-specific types (`IOrderedQueueBuilder` / `IConcurrentQueueBuilder`) so `Ordered` +
`MaxConcurrency` is **unspellable**. `OrderedLaneState` (inFlight / parked / wakeOrder / doorbell),
the drain query with server-side `not in` exclusion, `MaxPartitionsInFlight`, `MaxParkedPartitions`
with degraded mode. `CreatedAtUtc = max(UtcNow, lastIssued + 1 tick)` in `MessageBus`.
Startup validation: every type on an `Ordered` lane has a selector; a lane declared twice is fatal;
an undeclared lane is `Concurrent(1)`, never `Ordered`.

### M4b — Per-queue retry policy (PRD §6)
`RetrySchedule.Ladder/Linear/Exponential/None` with `maxAttempts` derived for ladders,
dictionary-shaped config with the **scalar** ladder string, the `RetryOverride` single switch, and
`MaxPartitionBlock` **validated at startup** from the ladder sum (`.AcceptPartitionBlock(...)` to
override). Collapse the three drifted delay call sites into one `IRetrySchedule`; delete
`SparkMessage.MaxAttempts`, `BackoffDelays`, `DefaultBackoffDelays`, `ResolvedBackoffDelays`.
New default `Ladder("5s 30s 2m")`. Print the resolved schedule table at startup.

### M4c — Requeue (no longer blocking)
`IMessageBus.RequeueAsync(id)` as an operator tool. It left the critical path when `MaxPartitionBlock`
became a startup validation rather than runtime early dead-lettering. Must not re-run handlers already
`Completed` (PRD §2.1). Settle S-R5 first.

**Superseded by M4:** `Delivery` / `MaxConcurrency` / `Retry` on `[MessageQueue]` (policy moves to the
builder; the attribute keeps its single argument and all 12 existing declarations compile unchanged),
analyzer `SPARK011` for `Ordered` + `MaxConcurrency` (the state becomes unspellable), and
`MaxLaneBlock` with runtime `LaneBlockBudgetExceeded` dead-lettering.

### M5 — Fold sync actions in (PRD §6)
Delete `SparkSyncAction`, `SparkSyncActions_ByStatus`, `SyncActionRetrySweeper`,
`SyncActionSubscriptionWorker`. `SyncActionInterceptor` broadcasts `SyncActionMessage` on
`spark-sync-{Collection}`; a framework `IRecipient<SyncActionMessage>` holds the POST logic with
400/404 → `NonRetryableException`. **Preserve verbatim:** mTLS module identity, the `RequestingModule`
certificate gate in `SyncApply`, terminal-on-400/404. Verify with `apps/HR` and `apps/Fleet`.

### M6 — Consumers and the inert generator
Restore CodeCoverage's five distinct queue names as lane names (they cost nothing now) and choose
`Delivery` per lane. Decide the `SubscriptionWorkerRegistrationGenerator`'s fate — today it is inert,
and after this change a consumer-defined worker would spend one of three slots, so it should either
be deleted or made to fail loudly.

### M7 — Docs
Rewrite, not touch up: `docs/prd/PRD-SubscriptionWorker.md` §8 (its §8.2 title inverts, and §8.1
needs the recorded answer from PRD §5), `libs/messaging/…/README.md:177-183,242-244`,
`docs/prd/PRD-Messaging.md:74`, `docs/prd/PRD-cross-module-sync.md:128`,
`docs/prd/PRD-Messaging-Improvements.md`, and the message doc-comments that claim "its own queue".
Correct `docs/code-coverage/upload-api.md:102` — the guarantee becomes true at M4, and the doc should
not have promised it before.

### M8 — Migration and deploy
1. Delete old per-queue subscriptions on every database including production `Coverage`.
2. Apply the S4 decision on the subscription start position.
3. Run the reaper once at startup for anything stranded by the deploy.
4. **Throttle or supervise the `coverage-delete-pr-builds` backlog** — that queue has never run, so
   first success deletes every merged-PR build at once.
5. Convert or drop `SparkSyncAction` documents (check whether the collection is non-empty first;
   CodeCoverage does not enable replication, so it is likely empty there).
6. One lockstep version bump across the 22 packages — major stays `10` (platform-lockstep rule).

### M9 — Verify in production
Confirm exactly one subscription exists on the `Coverage` database, that all six lanes deliver, and
that the PR comment appears on `opened`. The dogfood PR #362 and the browser (playwright) are the
verification path.

## Testing

**Two determinism rules, applied without exception.** Never assert absence within a time window —
convert every negative ("M2 did not run yet") into an ordering assertion over a *finished* event log
plus an eager invariant monitor. And keep the timing dependence only in how reliably a test detects
*today's* bug, never in whether correct code passes: a correct pump cannot emit an out-of-order log
at any speed.

Two shared pieces, in the test project (not in `MintPlayer.Spark.Testing` until a second suite needs
them):

- **`LaneOrderMonitor`** — records `Enter`/`Exit` and checks "a lane may not start a message while an
  older unfinished message exists in the same lane". A violation is **recorded, never thrown** —
  throwing inside `HandleAsync` would be swallowed as an ordinary handler failure and would re-drive
  the retry path, corrupting the thing under test.
- **`MessagingTestHost`** — replaces the per-test `NewWorker`/`NewSweeper` helpers; owns the
  `FakeTimeProvider`, feeder, pumps, sweeper and reaper. Gated recipients use paired
  `TaskCompletionSource`s (`Entered` / `Release`), never `Task.Delay`.

| Test | Asserts | Red today? |
|---|---|---|
| `Ordered_lane_does_not_start_M2_while_M1_is_retrying` | Exact log `[M1, M1-fail, M1, M2, M3]` with a 30 s configured backoff | **Yes** — and in ~1 s, without waiting out the backoff |
| `Ordered_lane_retries_in_place_without_the_sweeper` | Sweeper not started, `LastWakeUpUtc` stays null | Proves §4.4's retry-in-place, which T1 alone would not catch |
| `Serial_lane_lets_a_parked_message_be_overtaken` | The carve-out is real and stays real | Pins `Serial` as a tested mode |
| `DelayBroadcastAsync` carve-out | A delayed M0 may legitimately run late | An untested exclusion becomes an accidental requirement |
| `A_blocked_lane_does_not_delay_another_lane` | Free lane drains while another is wedged mid-handler | Timeouts are *failure* bounds only |
| `A_head_of_line_blocked_lane_does_not_delay_another_lane` | Same, but wedged on a 10-minute backoff | Makes "isolation makes blocking tolerable" a checked claim |
| `Poison_message_dead_letters_after_MaxAttempts_and_releases_its_lane` | 3 attempts, `DeadLettered`, `@expires` set, then D2 runs | Covers both halves in one log assertion |
| `Ladder_entry_beyond_MaxAttempts_is_unreachable` | Observed delays = first `MaxAttempts-1` rungs | Forces the 1h-rung decision to be deliberate |
| `Five_lanes_produce_exactly_one_RavenDB_subscription` | Subscription name set == `{"SparkMessaging"}` | **Yes**; licence-independent and instant |
| `QueueRetryPolicyTests` (pure, no server) | Policy resolution + the global flat-5s override + no config-append | Where ~90% of policy coverage belongs |
| `MessageRetryPolicyE2ETests` | Each lane waits its *own* backoff; the override flattens all | Pins the knob every other test relies on |

**Do not test the cap.** A test asserting RavenDB refuses the 4th subscription is unwritable
honestly here: `localhost:8080` and CI both run a **Developer licence with no cap**, so it would pass
vacuously, and even on a capped server the limit is unenforced for ~50–70 s after start. The cap is
environmental fact; the framework's obligation is "create one subscription", which the T4 test checks
directly. If a probe is ever wanted, keep it out of the suite behind a `LicenceProbe` trait. The one
licence-sensitive behaviour worth testing — M2's `LicenseLimitException`-is-fatal — is a **unit** test
with a substituted store: no server, no licence, no warm-up.

**Fake-clock hazards.** `FakeTimeProvider.Advance` fires timers synchronously, so never assert
immediately after `Advance` — always `Advance`, then `await AsyncWait.ForAsync(...)`. Seed the clock
at real `UtcNow`, never the 2000-01-01 default: if a component is missed in the migration, a
real-epoch seed degrades to slow-but-correct, while a 2000 seed mis-fires silently.

**Cost:** ≈ +9 databases net (~1.7% on a suite already at ~530). None can use `SparkSharedDatabase` —
they start subscriptions, enumerate subscriptions, or query collection-wide, all of which that
fixture forbids.

**Existing tests:** delete `MessageSubscriptionManagerLifecycleTests.cs` (tests a class M3 deletes);
rewrite all 10 facts of `MessageSubscriptionWorkerE2ETests.cs` (the ctor loses `queueName`), splitting
the two retry facts into `Ordered` and `Serial` variants; amend `MessageBusTests.cs`,
`SparkMessagingExtensionsTests.cs`, `SparkBuilderMessagingExtensionsTests.cs`,
`SparkMessagingOptionsBindingTests.cs`; keep `QueueNamesTests.cs`, `MessageCheckpointTests.cs`,
`MessageTypeAllowListTests.cs`.

## Disposition of `fix/coverage-queue-licence-cap`
Keep the framework hardening (→ M2) and the producer-side webhook test. Drop `CoverageQueues.cs`,
its consolidation of five queues onto two, `CoverageQueuesTests.cs`, and its 22 version bumps. See
PRD §10.

## Out of scope
- Multi-node message processing (needs PRD §7).
- Replacing the subscription with poll + changes-API doorbell — deferred, but M3 must keep the feeder
  as the only component that knows how an id arrives, so the swap stays a one-class change.

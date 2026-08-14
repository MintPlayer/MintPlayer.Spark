# Issue #233 — Messaging: failed messages are never redelivered

**Issue:** https://github.com/MintPlayer/MintPlayer.Spark/issues/233
**Branch:** `fix/233-message-redelivery`
**Type:** Bug (at-least-once delivery is silently at-most-once)

## Problem statement (verified against this repo)

The durable message bus writes all retry bookkeeping correctly (`AttemptCount`,
`MaxAttempts`, backoff, dead-lettering, `NextAttemptAtUtc`) but **nothing ever wakes a
parked message up again**. RavenDB subscriptions are change-vector-driven: a document is
re-evaluated against the subscription query only when it is *written*; `now()` in the
query is evaluated only at those moments. Redelivery-after-backoff therefore needs an
active wake-up mechanism, and all three designed ones are missing:

1. **Query implements half the designed condition** —
   `MessageSubscriptionWorker.ConfigureSubscription()` (`Services/MessageSubscriptionWorker.cs:56`)
   matches only `Status = 'Pending'`. The PRD-designed
   `OR (Status == Failed && NextAttemptAtUtc <= now)` clause (`docs/prd/PRD-Messaging.md:402-403`)
   was dropped, so a message parked at `Failed` (rollup `:316-323`, outer catch `:282-284`)
   can never match again.
2. **No `@refresh` is scheduled on failure** — unlike the sibling
   `SyncActionSubscriptionWorker` + `RetryNumerator` pair, the messaging worker never sets
   `@metadata.@refresh`, so even a due document is never *touched* to trigger re-evaluation.
3. **Batches always ACK** — `ProcessBatchAsync` catches everything (`:277-287`), so RavenDB
   never redelivers via NACK either; the checkpoint advances past the failed document.

Corollary, same root cause: **`DelayBroadcastAsync` never fires.** The document is
evaluated once at creation (`Pending`, but `NextAttemptAtUtc > now()` → no match) and
never re-examined.

The vestige explaining the gap: `SparkMessagingOptions.FallbackPollInterval` is defined,
documented, and binding-tested, but **read by nothing**. The PRD designed a poller whose
pickup condition included Failed-and-due; the implementation moved to subscriptions and
the time-based wake-up was lost in translation.

### Additional finding (not in the issue)

`@refresh` is a **per-database opt-in feature**, exactly like `@expires`.
`CreateSparkMessagingIndexes` enables expiration (`ConfigureExpirationOperation`,
`SparkMessagingExtensions.cs:43`) but nothing anywhere in the repo sends
`ConfigureRefreshOperation` — so `@refresh` metadata written by messaging (or by
`RetryNumerator`, for that matter) is inert unless the operator enabled refresh out of
band. Worse, the community license floors the refresh sweep at the same 36-hour minimum
the expiration comment (`:46`) already records. **`@refresh` therefore cannot be the
load-bearing wake-up mechanism for 5s/30s/2m backoff.** The SyncAction E2E test
(`SyncActionSubscriptionWorkerE2ETests.cs:254`) is consistent with this: it pins that
redelivery is *gated* (state + `@refresh` present), never that a second delivery actually
happens.

## Fix design

### Decisive finding during implementation: `now()` never matches in a subscription query

The issue's suggested fix (add the `Failed`-and-due clause + write `@refresh`) was
implemented first and **did not work**: touched, due, `Failed` messages were still never
redelivered. A minimal diagnostic (subscription `from SparkMessages where Status = 'Failed'
and NextAttemptAtUtc <= now()`, document already 5 minutes due at creation) proved that
**a time comparison in a subscription where-clause silently never matches** — the due
document is not delivered even on its creation write, while `Status = 'Failed'` alone and a
boolean field gate (`WakeUp = true`) both deliver correctly.

Consequences:

- The PRD's designed pickup condition was never implementable as written; the existing
  `(NextAttemptAtUtc = null or NextAttemptAtUtc <= now())` clause in the shipped query only
  ever matched via its `null` half.
- `@refresh` is doubly useless here: besides being a per-database opt-in (see below), the
  server touch it produces would re-evaluate the document against a query whose time
  comparison can never become true. All `@refresh` writes were removed from the fix.
- The replication worker (`SyncActionSubscriptionWorker.cs:26`) has the same
  `NextAttemptAtUtc <= now()` clause, meaning **its redelivery is equally broken** — its
  E2E test only pins that a failed sync action is *not* immediately redelivered, which
  also passes when the filter can never match. Follow-up issue to file; out of scope here.

### Chosen design: sweeper-materialized boolean gate

Time evaluation lives in exactly one place — a component that *can* evaluate time — and is
projected into plain field state the subscription query *can* match:

1. **`SparkMessage.WakeUp` (bool) + `LastWakeUpUtc` (DateTime?, informational)** — the
   subscription-visible redelivery gate.

2. **Subscription query** (`MessageSubscriptionWorker.ConfigureSubscription`):

   ```
   from SparkMessages where QueueName = '{q}' and (
       (Status = 'Pending' and (NextAttemptAtUtc = null or WakeUp = true))
       or (Status = 'Failed' and WakeUp = true))
   ```

   `Failed` stays the parked status (issue option (a): operators keep at-a-glance
   forensics). `SparkSubscriptionWorker.EnsureSubscriptionExistsAsync` updates an existing
   subscription's query at startup, so this deploys cleanly to existing databases.

3. **`MessageRetrySweeper` hosted service** (new) — wires up the until-now-dead
   `FallbackPollInterval`. Every interval (default 30s) it queries due messages via the
   existing `SparkMessages_ByQueue` index (`Status in (Pending, Failed) &&
   NextAttemptAtUtc != null && NextAttemptAtUtc <= UtcNow`, ids only, capped at 512/sweep)
   and patches `WakeUp = true` + `LastWakeUpUtc` onto each. The patch simultaneously makes
   the message match the query again and bumps the change vector that triggers
   re-evaluation. Field-level server-side patches, never load-modify-save: with
   last-write-wins a full-document save could resurrect a message the worker completed
   after the sweeper's query ran. One sweeper for all queues, registered in
   `AddSparkMessaging`; sweep errors are logged and the loop continues.
   `FallbackPollInterval` is thereby the redelivery granularity.

4. **Worker clears the gate**: on pickup `WakeUp = false` is set alongside
   `Status = Processing`, so every subsequent save (parked-for-retry, dead-lettered,
   completed) persists the cleared gate — without this, a message parked for another
   backoff round would still match the query and spin in an immediate-redelivery loop.

   Not adopting `RetryNumerator` — per-handler `AttemptCount` bookkeeping already exists;
   reusing the numerator would double-track attempts (issue item 4). Its `@refresh`
   mechanism doesn't survive the `now()` finding anyway.

5. **Docs** — `libs/messaging/README.md`: "How redelivery works" section (sweeper +
   `WakeUp` gate, `FallbackPollInterval` = redelivery granularity, the `now()` caveat).
   `FallbackPollInterval` option comment updated from dead knob to real meaning.

### Explicitly out of scope

- Fixing the same `@refresh`-is-inert gap for `SyncActionSubscriptionWorker` /
  `RetryNumerator` (replication). Same disease, different limb — deserves its own issue so
  it isn't silently absorbed here (follow-up issue to file when this PR opens — note the
  broken clause is `now()`, so the replication fix likely wants the same sweeper pattern).
- NACK-based redelivery (throwing from the batch handler): rejected — it blocks the whole
  queue behind one failing message and fights the per-handler isolation design.

## Test plan

New tests in `MessageSubscriptionWorkerE2ETests` (adapted from the issue's repro, using
`FallbackPollInterval`-driven sweeping instead of 90s refresh-granularity timeouts):

1. `Failed_message_is_redelivered_after_backoff_and_completes` — handler throws once,
   succeeds on attempt 2. Asserts terminal `Completed`, `Calls == 2`, handler completed.
   **Fails on master** (times out with `Failed` + due `NextAttemptAtUtc`).
2. `DelayBroadcast_message_is_picked_up_after_the_delay` — `DelayBroadcastAsync(1s)`,
   asserts pickup + completion. **Fails on master** (never evaluated again).
3. Redelivery skips completed handlers (two recipients, one fails transiently) — pins the
   per-handler acceptance criterion across a real second delivery.
4. Sweeper unit-ish E2E: due `Failed` message gets `LastWakeUpUtc` stamped; terminal
   messages are not touched.

Existing suites must stay green, notably
`Retryable_handler_failure_with_MaxAttempts_1_dead_letters_the_handler_and_message`
(dead-letter path) and the options binding tests.

## Acceptance criteria (from the issue)

- [x] Retryable failure → redelivered after backoff → completes
      (`Failed_message_is_redelivered_after_backoff_and_completes` green)
- [x] Per-handler semantics preserved across redelivery
      (`Redelivery_skips_already_completed_handlers` green)
- [x] `MaxAttempts` exhaustion still dead-letters (existing tests green)
- [x] `DelayBroadcastAsync` messages picked up after the delay
      (`DelayBroadcast_message_is_picked_up_after_the_delay` green)
- [x] README updated: "How redelivery works" (sweeper + `WakeUp` gate, the `now()` caveat,
      `FallbackPollInterval` = redelivery granularity), document-model table extended
- [x] `FallbackPollInterval` implemented (option kept, now drives `MessageRetrySweeper`)

Full `MintPlayer.Spark.Tests` suite: 1308/1308 green.

## Demo verification (DONE, 2026-08-14)

Ran `Demo/DemoApp` against local RavenDB (`FallbackPollInterval=5s` via command line) with
a temporary transient failure in `LogPersonCreated` (first attempt per PersonId throws;
reverted after verification):

- **Retry path**: seeded a `PersonEvents` message → `TRANSIENT FAILURE (simulated)` →
  `Message ... has failing handlers, retrying at ...` (parked `Failed`) → sweeper log
  `Woke up 1 due message(s) for redelivery` → `Person created: ...` on attempt 2 →
  document ended `Status=Completed, AttemptCount=2, WakeUp=false, Handlers=[Completed:1]`.
- **Delay path**: seeded a message with `NextAttemptAtUtc = +10s` (what
  `DelayBroadcastAsync` writes) → untouched until due → swept → picked up (and it also
  rode the transient failure + retry) → `Status=Completed, AttemptCount=2`.
- The pre-existing `SparkMessaging-*` subscriptions in the local database were updated to
  the new query at startup by `EnsureSubscriptionExistsAsync` — confirms clean deploy to
  existing databases.

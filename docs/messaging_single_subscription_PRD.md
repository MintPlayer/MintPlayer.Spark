# PRD — One RavenDB subscription for all Spark messaging, and multi-replica safety

**Status:** proposed, not implemented
**Date:** 2026-09-07
**Supersedes:** the "one subscription per queue" design of record in
`docs/prd/PRD-SubscriptionWorker.md` §8.2 (`:459-479`)

---

## 1. Requirements (owner, verbatim)

- Multiple message queues
- Single RavenDB SubscriptionWorker, with multi-subscription-worker mode available if the downstream
  consumer wants it (a `SparkSubscriptionWorkerOptions` property)
- FIFO per message queue
- Parallelism is desired
- Remove legacy subscription workers using a Raven migration
- Spark applications should be able to run in a Kubernetes environment, so `SubscriptionInUseException`
  is an undesired problem for those consumers
- Breaking changes allowed
- **GitHub webhooks must not be dropped during delivery**

Decisions taken during design review: Kubernetes means **HA and rolling deploys with one active
consumer** (not throughput scale-out); failover budget is **≤30 s**.

---

## 2. Why

Spark creates one RavenDB data subscription per distinct queue name. Production RavenDB is a
registered **Community** licence with `MaxNumberOfSubscriptionsPerDatabase: 3`. This has already
caused a production outage: `apps/CodeCoverage` grew to seven queue names plus the framework's
`spark-github-all`, three won the race, **five were silently dead**, and merged-PR build retention
had never run in production at all. #367 consolidated to two names and made the licence error fatal,
but the cliff is still there — the next queue name is still the next outage.

Queue names should be a modelling decision, not a licence purchase.

---

## 3. Verified findings that shape the design

### F1 — `WaitForFree` is dead code, and multi-replica already half-works

`libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/SparkSubscriptionWorker.cs:168-171`:

```csharp
if (Database != null)
{
    workerOptions.Strategy = SubscriptionOpeningStrategy.WaitForFree;
}
```

`SubscriptionOpeningStrategy` occurs **exactly once** in the repository, and **nothing ever overrides
`Database`** — verified by grep across `libs/` and `apps/`. So every Spark subscription worker in
existence runs the client default `OpenIfFree`.

**Correction to an earlier draft of this document:** `SubscriptionInUseException` is *not* fatal
today. It is caught at `SparkSubscriptionWorker.cs:214-220` and becomes a hot-standby retry at
`RetryDelay * 2` (60 s) with a warning. And because each replica starts one worker **per queue** and
those races are independent, **different replicas already end up owning different queues** — today's
deployment has accidental per-queue distribution.

Making the assignment **unconditional** is still worth doing on its own merits: the standby then
blocks inside `Run()` server-side and RavenDB hands the subscription over the instant the owner's
connection drops, taking failover from a ≤60 s poll gap to immediate. It is valid under every design
option and does nothing for the licence cap.

The exclusivity it provides is stronger than any lease we could build, because it needs no clock.
That is why it remains the safety boundary in §4 even though a lease also exists.

### F2 — `Processing` is a black hole, today

`EMessageStatus.Processing` is written at `MessageSubscriptionWorker.cs:83` and **read by nothing** —
verified by grep over `libs/messaging`. The subscription query (`:66`) matches only `Pending` and
`Failed`; `MessageRetrySweeper` sweeps only `Pending` and `Failed` (`:67`). `SparkMessage` has no
owner field, no claim expiry and no lease.

So any crash, SIGKILL, or SIGTERM mid-handler **strands that message permanently**. Under Kubernetes,
routine pod churn turns a rare event into a steady leak. **This is the webhook-drop bug, and it is
live in production now.**

It is made worse by a second defect: the outer catch that parks a failed message
(`MessageSubscriptionWorker.cs:291-301`) calls `SaveChangesAsync(cancellationToken)` using the very
token that just fired, so on shutdown the park itself throws and the message is never parked.

### F3 — Ack already happens regardless of outcome

`ProcessBatchAsync` wraps every item in an unqualified `catch (Exception)` (`:77`/`:291`), so the
batch is acknowledged whatever happens. The "throw to NACK" path in the original design is
effectively dead — `docs/prd/PRD-SubscriptionWorker.md:180-186` records this deviation. Redelivery
comes entirely from the sweeper's `WakeUp` gate. This means moving to claim-then-ack is a smaller
semantic change than it appears: we are formalising what already happens, and adding the recovery
that is currently missing.

### F4 — `MaxDocsPerBatch = 1` is today's parallelism limiter, not a FIFO requirement

It is documented as FIFO enforcement, and production code depends on the ordering by name
(`ParseSessionRecipient.cs:13-16`, `AssembleCommitRecipient.cs:7-11`). But the ordering comes from
three stacked facts: the subscription's change-vector order, one worker per queue, and batch size 1.
Once the batch loop becomes a *feeder* that only claims and hands off, batch size stops gating
concurrency — the per-queue pumps provide it. Batch size then controls round-trips only.

**Landmine:** the dead-letter branches at `MessageSubscriptionWorker.cs:103,112,122` use `return`,
not `continue`, inside `foreach (var item in batch.Items)`. This is safe *only* because the batch is
always 1. Raising batch size before fixing it silently drops the rest of every affected batch.

### F4b — The cap is tighter than "3", and the framework can spend it alone

Community is **3 subscriptions per database *and* 15 per cluster** (ravendb.net/buy). Worse, a
full-featured Spark app spends all three before declaring a single queue of its own: replication's
`SyncAction`, replication's `spark-etl-deployment` message queue, and the webhooks
`spark-github-all`. `apps/CodeCoverage` only fits because replication is switched off there.

Under this design messaging costs **1** and replication's `SyncAction` costs **1**, leaving real
headroom for the first time.

### F5 — Concurrent subscriptions are not available

`SubscriptionOpeningStrategy.Concurrent` (RavenDB 5.3+) lets N workers share one definition, but the
pricing matrix lists it for Professional/Enterprise only — not Community — and it **explicitly
abandons ordering** ("you'll process the documents in the order in which they were modified. This is
now no longer the case"). Unusable for FIFO-per-queue regardless of licence.

### F6 — The deploy blocker: no code in this repo deletes a subscription

Production holds three definitions: `SparkMessaging-coverage-parse-session`,
`SparkMessaging-coverage-publish-feedback`, `SparkMessaging-spark-github-all`. Definitions persist in
the cluster whether or not a worker is connected, and the licence counts **definitions**. Grepping
`DeleteSubscription`, `Subscriptions.Delete`, `DropConnection` across the repo returns nothing; the
only write path is `CreateAsync` → `UpdateAsync(CreateNew = true)`.

A new subscription under a new name is therefore the **4th create → `LicenseLimitException` → hard
startup failure** (fatal since #367). The rework bricks the deploy unless the legacy definitions are
deleted first.

The API is `store.Subscriptions.GetSubscriptionsAsync(start, take, database)` +
`store.Subscriptions.DeleteAsync(name, database)` — there is no `DeleteSubscriptionOperation`.
Deletion must match the **`SparkMessaging-` prefix** rather than a hardcoded list: `MintPlayer.Spark`
is a published package, every deployment has these definitions, and their names are app-specific.
Delete is idempotent, so racing replicas are harmless and the operation is self-emptying.

### F7 — Nothing in this repo deploys to Kubernetes today

No manifests, no Helm, no kustomize; no file contains `kind: Deployment` or `replicas:`. Deployment
is a single container per app behind an externally-managed Traefik on one VPS, and the deploy does
`docker compose down` then `up` — i.e. **there is downtime on every deploy and rolling-deploy
correctness has never been exercised.** Treat every rolling-deploy claim in this document as
designed-but-untested until spiked.

---

## 3b. Design alternative considered and rejected

**Option B — a document-queue with per-queue compare-exchange leases and zero subscriptions.** Poll
the existing `SparkMessages_ByQueue` index, claim per queue with a renewable lease, use the RavenDB
Changes API purely as a non-durable latency nudge.

Its genuine advantages were weighed and are real: messaging would cost **zero** subscriptions, so
the cap stops mattering at all rather than merely stopping its growth; per-queue distribution across
pods survives instead of collapsing onto the leader; a `PartitionKey` would give intra-queue
parallelism that Community subscriptions cannot; and — the largest simplification — **a query can
evaluate `now()` where a subscription cannot**, so `WakeUp`, `LastWakeUpUtc`, `MessageRetrySweeper`
and their two replication twins would all delete, along with the bug class that has already bitten
twice (#233, #258).

**Rejected because** it trades two strong guarantees for one weaker pair: server commit ordering
(etag) becomes producer wall-clock ordering, which two pods with skewed clocks can get wrong; and
server-enforced exclusivity becomes a clock lease whose failure mode is *silent double-processing of
the head of a FIFO queue*. It also adds roughly 600 lines of bespoke coordination — lease renewal,
fencing, expiry-aware reads, takeover races, stale-index tolerance, head-of-line reclaim, a debounced
nudge — to a subsystem whose entire bug history is bespoke-coordination bugs: `now()` silently false,
`WakeUp` unwired so `FallbackPollInterval` was a dead knob for months, a licence exception swallowed
in a bare `catch` while the app reported healthy.

Owner decision: take the conservative transport, keep RavenDB owning exclusivity and ordering.

**Accepted cost, stated plainly.** Option A **collapses all queues onto the leader pod**, removing
the accidental per-queue distribution that `OpenIfFree` contention provides today (F1). This is a
throughput regression in principle. It is accepted because the current deployment is a single
container with a `down`/`up` deploy, so there is no distribution in practice to lose, and because
relying on a racy, unbalanced, undocumented distribution would be a poor foundation. Replicas buy
HTTP and read capacity; message processing stays single-executor by design.

## 3c. The `@refresh` alternative to the `WakeUp` sweeper — evidenced, not adopted here

There is a **server-side** way to express "redeliver after a delay" that needs no client polling at
all, and it is proven prior art: a comparable RavenDB framework runs it across **56 databases** in a
live deployment. Spark rejected it once, on a false premise (B10). It deserves recording properly.

### The mechanism

1. **Park** a document by writing a future UTC timestamp into `@metadata.@refresh`.
2. The subscription query filters `not exists(<alias>.@metadata.@refresh)`, so a parked document
   **fails the predicate** and is not delivered.
3. RavenDB's **Document Refresh** background sweep notices the timestamp has passed, **removes the
   `@refresh` key and rewrites the document**.
4. That rewrite is a real write, so it bumps the change vector — which is exactly what makes a
   subscription re-evaluate — and the document now **passes** the predicate and is delivered.

The writer that triggers redelivery is the **server**. Time is never evaluated by the query, so the
`now()` prohibition (invariant 7) is sidestepped rather than worked around. The RavenDB 7.1 refresh
documentation names subscriptions explicitly among what the change-vector bump activates.

Confirmed against the live local server: 56 of 88 databases have `Refresh` enabled with
`RefreshFrequencyInSec: null` (the **60 s server default**), and their subscription definitions carry
the filter verbatim, e.g. `from Mails as m where m.Status == 'Ready' and not exists(m.@metadata.@refresh)`.
`declare function` predicates and `include` clauses coexist with it, so subscription-query complexity
is not a constraint.

### Prior-art details worth copying

- **Apply the gate selectively.** Only subscriptions that can *park* a document carry the
  `not exists(@refresh)` clause. A first-delivery stage (`Status == 'Initial'`) deliberately does
  not — and the clause was actively *removed* from one such worker.
- **Exclude `@expires` too** where documents can be scheduled for deletion:
  `not exists(m.@metadata.@expires) and not exists(m.@metadata.@refresh)`. Spark sets `@expires` on
  terminal messages, so this matters if it adopts the pattern.
- **Age-based backoff avoids needing a durable counter.** One implementation derives the delay purely
  from document age (`<5 min → 1 min`, `<1 h → 5 min`, `<6 h → 30 min`, else `2 h`), so nothing has
  to survive a clone-and-re-store. The counter-based sibling stores attempts in a RavenDB *counter*.
- **A status machine sets `@refresh` to the next boundary only**, and the same static `SetStatus` is
  called from the document's lifecycle hook so an edit corrects the state immediately: the worker
  handles the passage of time, the hook handles edits, one implementation.
- **`@refresh` doubles as a debounce**, stamped at creation (`now + 1 min`) to collapse duplicate
  subscription wake-ups.

### The scars — all of these were hit in production by the prior art

1. **The 60 s sweep is a hard floor on any delay.** Their own code carries
   *"When changing Timespan consider Document Refresh Frequency on Database (default 60s)!"* and a
   standing TODO to lower it. Their default retry span is exactly 60 s, sitting on the floor. Spark's
   schedule starts at **5 s**, so adoption means setting `RefreshFrequencyInSec ≤ 5` — a
   **database-wide sweep frequency**, not a per-queue knob.
2. **⚠️ Correctness cliff: if Refresh is not enabled on the database, every parked document is
   stranded permanently and silently.** The prior art escalates this to a named precondition that
   each cutover must verify. **Spark is living in this cliff right now** — see B11.
3. **Forgetting the park causes a hot-loop**, hit in production: a re-enqueued document with no
   `@refresh` is redelivered every batch forever.
4. **The give-up park leaks.** Parking an exhausted document for 24 h means it returns every 24 h
   with a fresh budget and re-escalates forever; a separate `Parked` marker was needed. Lengthening
   the park to ~30 days was considered and rejected as "a magic number that only delays the storm".
5. **A parked document cannot un-park itself** — it is excluded from the subscription, so whatever
   clears the park must live outside the worker.
6. **Clearing a retry counter re-delivers the document.** The counter write bumps the change vector,
   so resetting it while the document still matches the query causes deliver → reset → deliver.
7. **Nested value objects have no collection to subscribe to**, so the prior art deliberately keeps a
   cron sibling for those rather than stamping `@refresh` on the parent — which would re-fire every
   one of the parent's other workers on a document whose business state never changed.
8. **Config plumbing is easy to get wrong**: their settings helper skips pushing a
   frequency-only change, and its hook fires only on database creation, so an existing database is
   never reconfigured. One app depends on refresh without configuring it at all.

### Four more ideas from the same prior art, independent of whether §3c is adopted

1. **Whole-subscription backoff for a global outage.** Throwing a `SubscriptionShouldWaitException`
   (with a delay) from the batch handler backs off *the entire subscription* rather than parking each
   message individually. Spark has no equivalent: when GitHub is down, every message on the
   publishing lane fails and parks separately, each burning an attempt from its own budget. A single
   "the upstream is down, wait two minutes" signal is the right granularity for that, and it would
   stop an outage from exhausting `MaxAttempts` across a whole queue.
2. **Compute the park time from the next state boundary, not a fixed delay.** A small helper —
   gather every timestamp at which a derived field could change, take the earliest still in the
   future, `null` meaning "nothing left to happen" — turns a status machine into two writes over a
   document's whole lifetime. Not needed for retry backoff, but it is the right shape for any
   Spark feature that derives state from dates.
3. **`@expires` and `@refresh` are mutually exclusive and must be managed as a pair.** Their code
   removes one when setting the other, and the subscription excludes both. Spark sets `@expires` on
   terminal messages, so adopting `@refresh` means every park/terminal transition has to clear the
   other key or a message can be simultaneously scheduled for redelivery and for deletion.
4. **An analyzer can guard the filter.** Their ETL layer ships a Roslyn analyzer that cross-checks a
   worker's subscription filter against the cases it actually handles. Spark already has SPARK004/009/010,
   so a rule asserting "a worker that parks documents must carry the `not exists(@refresh)` clause"
   is cheap insurance against scar 3 — the hot-loop they hit in production.

### Three scars found only in their production call sites

9. **Never park *on* a boundary — park an hour off it.** Not one call site stamps `@refresh` at
   midnight or at a period boundary; they offset deliberately, with comments naming the reason
   ("delay processing by 1 hour after midnight to avoid concurrency issues with <the other
   subscription>", "trigger at the 5th of next month at 23:00"). Everything due at the same instant
   wakes in the same sweep and contends. This is ad-hoc per call site in their code, not a framework
   policy — Spark should make it one, since a retry schedule of `5s, 30s, 2m, 10m, 1h` will align
   many messages onto the same sweep tick by construction.
10. **The un-park write happens outside your session, so a woken worker can hold a stale change
    vector** and `SaveChanges` throws. Their worker calls `session.Advanced.Refresh(entity)` on entry
    to reload. Any Spark adoption must reload the document at the top of the handler rather than
    trusting the copy the batch delivered.
11. **Re-stamping must only ever tighten the date, never loosen it** — and must skip the write when
    the value is unchanged. Their helper compares before writing, and one call site guards with
    `if (currentRefreshDate > newDate)`. An unconditional stamp dirties the document, which is itself
    a write, which re-delivers it — the same loop as scar 6.

Also worth knowing: `@refresh` carries only a *when*. One module adds a companion metadata key
holding the *why* (a delimited payload written beside the `@refresh`, parsed on wake, then cleared),
and excludes that key in the subscription too — so `not exists(@metadata.X)` works as a
general-purpose parking mechanism, with `@refresh` being the one variant the **server** un-parks.

And the terminal-state rule, which is the counterpart to scar 4: a document reaching a terminal state
must either **remove** `@refresh` or hand off to `@expires`. Never park it on a long timer.

### One more scar, and it argues *for* the single-subscription design

**The wake-up is a write, so every *other* subscription on that collection re-fires too.** Their code
calls this out explicitly as the reason a nested value object gets a cron sweeper instead: stamping
`@refresh` on the parent would re-fire all of the parent's other workers on a document whose business
state never changed. Under this PRD's design there is exactly **one** subscription over
`SparkMessages`, so the concern is structurally absent — a point in favour of collapsing to one
subscription that has nothing to do with the licence cap.

### Position for this PRD

**Not adopted in this rework.** The chosen transport keeps `WakeUp`, and the sweeper works today. But
the rejection reasoning in `docs/issue_233_plan.md:44-46` is wrong and must be corrected, because it
is the thing that would stop the next person from reconsidering.

If Spark ever does adopt it, the cliff in scar 2 must become a **startup assertion** — read the
database's refresh configuration and fail to start if it is disabled, exactly as
`LicenseLimitException` on subscription create is now fatal (invariant 12). A silent scheduling
failure is precisely the class of bug this codebase has already shipped twice.

**Immediate actions regardless** (B10, B11), plus the one-test spike in the plan (S4).

## 4. Architecture

Three layers. The critical property is that **the lease is a liveness mechanism, never the safety
boundary.**

```
                 ONE subscription definition: "SparkMessaging"
      from SparkMessages where <pending or woken>     <- no QueueName predicate
                                |
                    [feeder]  (leader pod only)
         claim durably: Status=Processing, OwnerNodeId, ClaimExpiresAtUtc
                    -> ack batch -> hand to channel
                                |
              /                 |                  \
     ch:parse-session    ch:publish-feedback    ch:github-all
              |                 |                  \
           pump                pump                pump      <- concurrent
        FIFO within          FIFO within         FIFO within
```

| Layer | Guarantees | Mechanism |
|---|---|---|
| **Per-message durable claim** | no lost message, no double execution | `OwnerNodeId` + `ClaimExpiresAtUtc` on `SparkMessage`, written under optimistic concurrency before the ack |
| **Subscription `WaitForFree`** | at most one feeder cluster-wide ⇒ **FIFO** | RavenDB, server-side (F1) |
| **Compare-exchange leader lease** | *which* pod feeds; gates the other singletons; observability; fast deliberate handover | new `spark/messaging/leader` key |

### Why all three

The lease alone cannot guarantee single-feeder — time-based leases always admit a two-leader window
(GC pause, clock skew, a stalled renewal). `WaitForFree` makes that window cost an **idle pod**
rather than an ordering violation, because RavenDB admits exactly one feeder and the other blocks.
The durable claim makes it cost **nothing** at the message level, because the second feeder's claim
of an already-claimed document fails on change vector.

**Use `WaitForFree`, never `TakeOver`.** With `TakeOver`, two pods that both believe they lead
ping-pong evicting each other for the whole overlap window, and each eviction drops an unacked batch.
`WaitForFree` is monotonic: the loser waits.

### The lease

- **Key** `spark/messaging/leader`, one key — not per-queue; per-queue leases would resurrect the
  fan-out we are removing.
- **Value** a record, not a bare `DateTime`: `MessagingLease(NodeId, AcquiredAtUtc, ExpiresAtUtc,
  Fence)`. `NodeId` = machine/pod name + per-process GUID, so a restarted pod with the same name is a
  different holder. `Fence` is a monotonic counter for logs and for refusing a stale writer. The
  migration lock's bare-expiry shape is precisely what makes its release unsafe (§6 B2).
- **TTL 30 s, renewed every 10 s** (3× headroom), meeting the ≤30 s failover budget. Renewal CASes on
  `current.Index` **and** on `NodeId == mine`; finding another `NodeId` means we were evicted, so tear
  down the feeder and pumps immediately and stop renewing.
- **Release** CAS-deletes guarded on `NodeId == mine` and `current.Index`.
- **Standbys** poll every 5 s, run no feeder, no pumps and no sweeper — but keep a `WaitForFree`
  worker **parked**, so if the lease machinery itself wedges, the queue still drains. Lease for
  liveness, RavenDB for safety.
- **Failover latency**: graceful SIGTERM ≈ 5 s (explicit release + standby poll). Ungraceful kill ≈
  TTL + poll + handover ≈ **35-40 s**. TTL must exceed `terminationGracePeriodSeconds`, which must
  exceed the longest handler.

### Rolling deploy: release the lease *last*

1. New pod B starts; standby loop sees a live lease; parked worker blocks server-side; B is ready and
   serves HTTP immediately.
2. A gets SIGTERM → **stop feeding** (no new claims) → **drain the pumps** → **release the lease** →
   close the subscription.
3. B acquires within one poll; its parked worker unblocks when A's connection closes.

Releasing the lease first would let B feed while A's pumps still run the same queues — the one thing
that breaks FIFO. `WaitForFree` saves us even then, but the shutdown order must not rely on it.

### Modes (`SparkSubscriptionWorkerOptions`)

| Mode | Behaviour |
|---|---|
| `SingleSubscription` (**default**) | one definition, feeder + per-queue pumps as above |
| `SubscriptionPerQueue` | today's behaviour — one definition per queue name, for deployments with licence headroom that want server-side isolation per queue |

---

## 5. Webhook durability

The requirement is that a GitHub webhook delivery is never lost. Four distinct risks, each needing
its own answer.

**W1 — Ingest.** The durability boundary is the `SparkMessage` document write, which happens before
the endpoint returns 200 (`MessageBus.StoreMessageAsync` opens its own session and saves;
`SparkWebhookEventProcessor` broadcasts then returns). This is already correct and unaffected by the
rework. **Do not move the broadcast after the response.**

**W2 — Stranded at `Processing` (F2).** This is the real live drop, and the claim + reclaim design
fixes it. Required: `OwnerNodeId` + `ClaimExpiresAtUtc` on `SparkMessage`, the sweeper (or feeder)
reclaiming expired claims back to `Pending`, and `UseOptimisticConcurrency` on the claim session —
nothing in `apps/CodeCoverage` currently enables it.

**W3 — GitHub does not automatically retry a failed delivery.** A 5xx or a timeout means the delivery
is recorded as failed and must be redelivered by hand. So the ingest path must stay fast and must not
depend on the leader: **any pod must accept a webhook**, whether or not it holds the lease. The feeder
is leader-only; the producer is not. Consequences: keep the endpoint off the readiness-gated path
during a rolling deploy, and ensure a terminating pod stops receiving traffic before it stops
accepting writes.

**W4 — No delivery deduplication exists.** `X-GitHub-Delivery` is carried at
`SparkWebhookEventProcessor.cs:183` and **never used as a key**; every enqueue gets a fresh
server-generated document id. With at-least-once delivery plus manual redelivery, the same webhook
can be processed twice. Recommended: derive the `SparkMessage` id from the delivery guid for webhook
messages so a redelivery is an idempotent upsert rather than a second message. This is a natural-id
pattern the repo already uses elsewhere.

---

## 6. Defects that must be fixed in the same change

Not scope creep — each one is either made worse by this rework or is the thing it claims to fix.

| # | Defect | Location |
|---|---|---|
| B1 | `Processing` is unreachable: nothing reclaims it, so a crash mid-handler strands the message forever | `MessageSubscriptionWorker.cs:83`; F2 |
| B2 | Shutdown park saves with the **cancelled** token, so the park always throws on SIGTERM | `MessageSubscriptionWorker.cs:300` — use `CancellationToken.None` |
| B3 | `return` instead of `continue` in the batch loop's dead-letter branches; safe only at batch size 1 | `MessageSubscriptionWorker.cs:103,112,122`; F4 |
| B4 | No drain on shutdown — `StopAsync` cancels mid-handler | `MessageSubscriptionManager.cs:65-81` |
| B5 | Migration lock: the loser **skips and serves an un-migrated database** | `SparkMigrationRunner.cs:37-41` — must block or refuse readiness |
| B6 | `ReleaseLockAsync` deletes unconditionally and can delete another holder's lease | `SparkMigrationRunner.cs:107-114` — CAS on holder identity |
| B7 | Migration lease is 30 min and never renewed — a coin flip on a large backfill | `SparkMigrationRunner.cs:16` |
| B8 | `MessageRetrySweeper` lacks the `WakeUp != true` guard its twin has, so it re-patches the same set every 30 s per replica | vs `SyncActionRetrySweeper.cs:88` |
| B9 | `CoverageQueues.cs` says AGPL where the licence is registered Community, and "five queues" where it was seven | `CoverageQueues.cs:8-9,18` |
| B10 | **Inverted licence limit.** `DeleteFrequencyInSec = 36 * 60 * 60, // 36 hours (community license minimum)` — the Community limit is a **ceiling on the interval** ("cannot be set higher than 36 hours"); the product default is **60 s**. Spark picked the slowest legal sweep believing it was mandatory, and the value is live in the database (`Coverage` and `Spark` both report `DeleteFrequencyInSec: 129600`, while every prior-art database leaves it `null`). Low impact — `RetentionDays` is 7, so this only delays deleting an already-expired document by up to 36 h — but the same misreading in `docs/issue_233_plan.md:44-46` is what wrongly ruled out `@refresh` (§3c) | `SparkMessagingExtensions.cs:54`, `docs/issue_233_plan.md:44-46` |
| B11 | **`RetryNumerator`'s `@refresh` writes are inert, and its doc comment is self-contradictory.** `ConfigureRefreshOperation`/`RefreshConfiguration` appear **nowhere** in the repo (verified by grep), and the live server confirms it: every `Spark*`/`Coverage*` database reports `Refresh: null`. So the writes at `:57` and `:74` do nothing. Meanwhile `:16` says it "schedules redelivery via the @refresh metadata mechanism" while `:11` tells callers "`@refresh` metadata alone doesn't gate change-vector-driven re-delivery" — and the RavenDB docs contradict `:11`. Replication's retry actually works via `SyncActionRetrySweeper`'s `WakeUp` patch; the `@refresh` beside it is decorative. **Decide it deliberately: either enable refresh and make it real, or delete the writes.** Inert code that reads as a working mechanism is how the last two silent-scheduling bugs happened | `RetryNumerator.cs:11,16,57,74` |

---

## 7. Multi-replica scope boundary — read this before claiming "runs on Kubernetes"

This PR makes **messaging** multi-replica-safe. It does not, by itself, make N replicas safe. The
audit found the following, and the honest position is that shipping messaging alone leaves the rest
broken.

**In scope here** (small, and directly implicated by the leader lease):

1. Leader-gate `MessageRetrySweeper` and `SyncActionRetrySweeper` (currently N× redundant patching).
2. Leader-gate `IndexCreation.CreateIndexes` (`SparkMiddleware.cs:649`) — during a rolling deploy old
   and new pods push different definitions of the same index name, flapping it into repeated full
   re-indexing.
3. Leader-gate `SmeeWebhookTunnelService` **and** tighten its gate to `IsDevelopment()` — smee.io
   broadcasts to every connected client, so N replicas process every webhook N times today, silently.
   Its gate is config-only despite the class comment saying dev-only.
4. Migration correctness: B5, B6, B7.

**Out of scope, and must be tracked separately** — these are pre-existing and unrelated to messaging:

- `PublishFeedbackRecipient.cs:94` `feedback.Attempts++` is a read-modify-write with no optimistic
  concurrency, and `:75-76` lets two first-time publishes both see a null check-run id and create
  duplicate check runs. Currently masked by single-consumer.
- `GitHubUserTokenService.cs:33` guards single-use rotating GitHub refresh tokens with a
  **process-local** `ConcurrentDictionary`; across pods, one pod spends the token and the other's copy
  is dead. The code already notes it can be "burned by a lost race with another instance".
- Data Protection keyring: CodeCoverage stores it in RavenDB (`Program.cs:194-197`) but every other
  app uses the container filesystem, so N replicas cannot decrypt each other's auth/antiforgery
  cookies. This belongs in Spark core, not one app's `Program.cs`.
- No real health checks exist — no `AddHealthChecks`, no `IHealthCheck` anywhere. Kubernetes needs a
  readiness probe reporting "migrations applied, model verified, indexes deployed"; `/health/ready`
  today reports only GitHub App key usability.
- `ISparkCronBuilder.cs:53` defaults the compare-exchange claim key to `typeof(TJob).Name` (short
  name), so two same-named jobs in different namespaces collide. Also, the cron claim is taken before
  execution and never repaired, so a winner that crashes mid-run **loses that occurrence** — fine for
  the four re-querying CodeCoverage jobs, not fine for a one-shot job.

---

## 8. Invariants the rework must preserve

Verified against current code. The four marked ⚠️ are **not covered by any test today** — exactly the
ones this change puts most at risk.

1. ⚠️ **Total FIFO order within a queue name**, across all message types sharing it.
   `ParseSessionRecipient` and `AssembleCommitRecipient` are *correctness*-dependent on it, not just
   latency-dependent.
2. ⚠️ **Exactly one consumer per queue at a time, cluster-wide.**
3. ⚠️ **Cross-queue failure isolation** — one poisoned message must not stop other queues.
4. ⚠️ **Crash mid-handler recovers** (the new claim/reclaim path).
5. At-least-once delivery, with per-handler completion persisted so a `Completed` handler never
   re-runs; the `Handlers` list is materialized **once**, from DI, on first pickup.
6. A message's handlers stay serial, in persisted order, in one DI scope and one Raven session.
7. **No `now()` in a subscription query, ever** — RavenDB 7.2.5 rejects it outright; 7.2.1 silently
   evaluated it false. Backoff elapse stays materialized as the `WakeUp` boolean written by a
   component that can read a clock.
8. `WakeUp` is consumed on every pickup and on every park.
9. The sweeper patches fields, never load-modify-save — a full save can resurrect a message the
   worker completed after the sweep query ran.
10. Retry accounting stays per handler; message backoff follows `Max(AttemptCount)` across failed
    handlers. `NonRetryableException` dead-letters on the first attempt, through one `InnerException`
    level and through `TargetInvocationException`. Mixed terminal handlers roll up to `Completed`;
    only all-dead-lettered rolls up to `DeadLettered`.
11. Message-type and handler-type allow-lists gate `Type.GetType` **before** it is called.
12. `LicenseLimitException` on subscription create stays fatal at startup.
13. **The queue-name → subscription-count coupling is what must break** — including derived names
    nobody declared, such as `GitHubWebhookMessage<TEvent>`, which carries no `[MessageQueue]` and
    mints a queue per event type.

---

## 9. Blast radius

`libs/messaging` is 1,237 lines across 23 files; the queue-scoped logic is `MessageSubscriptionManager.cs`
(112 lines, fan-out at 33-50) and two spots in `MessageSubscriptionWorker.cs` (`SubscriptionName` at
:20, RQL at :64-67). The remaining ~290 lines of the worker — dispatch, allow-listing, checkpointing,
retry rollup — are untouched. Both types are `internal`, so the published API *could* survive
unchanged — but per §9b it should not.

Expect roughly **8-12 source files**, **~14 test files** (all 9 messaging tests re-plumbed for the new
worker construction; `MessageSubscriptionManagerLifecycleTests` and `CoverageQueuesTests` rewritten
outright; `DeleteDataActionTests` re-motivated), **~10 doc files**, and **22 version bumps** shipping
as `10.0.0-preview.73` → `preview.74`. Per the repo's policy the major digit does not move: NuGet
major tracks the targeted .NET major, and an API break inside .NET 10 is a preview/minor bump.

Consumers to keep working: CodeCoverage (9 message types on 2 queues + the framework's
`spark-github-all`), DemoApp (2 queues), HR and Fleet (replication's `spark-etl-deployment` +
`SparkSyncAction`), and `libs/replication`, whose `SyncActionSubscriptionWorker` is the other
`SparkSubscriptionWorker<T>` subclass and gets `WaitForFree` from the same fix.

---

## 9b. No backward compatibility required — take the simplifications

Owner instruction, restated: **breaking changes allowed, no back-compat needed.** The default is
therefore to *simplify*, not to preserve. Anything kept must be kept for a reason other than
compatibility.

Take these while we are here:

1. **Give `GitHubWebhookMessage<TEvent>` a `[MessageQueue("spark-github-all")]`.** Today the generic
   envelope carries no attribute, so `QueueNames.Derive` mints a queue name **per closed generic** —
   the derived-queue trap that put WebhooksDemo at 3 subscriptions without anyone declaring one
   (invariant 13). Fixing it at the source is better than every consumer knowing to avoid typed
   envelopes, and it makes `docs/coverage_project_automation_PRD.md` FR5's "sibling recipient, never
   `IRecipient<GitHubWebhookMessage<T>>`" rule unnecessary rather than merely documented.
2. **Delete `IMessageBus.BroadcastAsync(message, queueName)`.** The explicit-override form is used by
   **no production code** — only tests and a README example. It exists to support "per-collection queue
   isolation", which the single-subscription design makes free anyway.
3. **Delete `SparkSubscriptionOptions`** — it is literally `public class SparkSubscriptionOptions;`,
   an empty type whose two former properties were removed because nothing read them.
4. **Delete `RetryNumerator`'s `@refresh` writes** (B11, `:57`/`:74`) and its false disclaimer at
   `:11`, unless S4 comes back green *and* refresh is deliberately enabled with the §3c startup
   assertion. Do not keep inert code that reads as a working mechanism.
5. **Collapse the two retry implementations.** `MessageRetrySweeper` and `SyncActionRetrySweeper` are
   near-duplicates — the latter's own comment says "two copies of this pattern is one more than
   ideal" — and `RetryNumerator` is a third, counter-based one used by exactly one worker. One
   implementation, shared.
6. **Redesign `SparkSubscriptionWorker<T>`'s protected surface** freely. Its `MaxDocsPerBatch`,
   `Database`, `KeepRunning`, `RetryDelay`, `MaxDownTime` virtuals were shaped around one worker per
   queue; the feeder needs a different shape. `Database` in particular exists only to gate the dead
   `WaitForFree` line (F1) and should go.
7. **Delete the `CoverageQueues` guard tests** rather than re-motivating them, and reduce
   `CoverageQueues` to whatever the app still genuinely needs — the two-name constraint is gone, so
   the seven queues that *should* exist can exist, with real FIFO isolation per concern.
8. **Rename freely.** `SparkMessaging-{queueName}` as a subscription name, `WakeUp` as a field,
   `FallbackPollInterval` as an option whose meaning has already drifted once — none of these need to
   keep their names.

### The one thing back-compat freedom does *not* cover

**Production data.** `SparkMessages` documents exist in the live `Coverage` database right now, some
of them non-terminal. API freedom is not document-shape freedom:

- Removing or renaming a field on `SparkMessage` orphans in-flight messages — they deserialize with
  defaults, and a message whose `Status`/`QueueName` no longer means what it did is a silently
  dropped message, which is exactly the failure class this PRD exists to end.
- The safe order is: **drain first, then change shape.** Either deploy the shape change only after
  the queues are empty, or write a migration that rewrites existing `SparkMessages` into the new
  shape before the messaging host starts — the same migration slot that removes the legacy
  subscription definitions (M6), and the same ordering question S1 has to settle anyway.
- Terminal messages can simply be left to expire; only non-terminal ones need care.

State explicitly in the PR which of the two routes was taken, and how it was verified.

## 10. Risks

| # | Risk | Mitigation |
|---|---|---|
| R1 | First deploy fails: 4th subscription create over the cap | F6; delete legacy definitions in a migration **before** the messaging host starts; verify that ordering rather than assuming it |
| R2 | Split-brain leader breaks FIFO | `WaitForFree` + durable claim; never `TakeOver`; spike it |
| R3 | Claim-then-ack loses messages if the claim write is not durable before the ack | order is claim → save → ack, in that order, with optimistic concurrency |
| R4 | Webhook dropped during a rolling deploy | W3; any pod accepts webhooks; terminating pod leaves the load balancer before it stops writing |
| R5 | Raising batch size trips the `return`-in-`foreach` bug | B3 first, then batch size |
| R6 | Rolling deploys are wholly unexercised in this deployment (F7) | spike the handover explicitly; do not infer it from a green test suite |
| R7 | Longest handler exceeds `terminationGracePeriodSeconds` (k8s default 30 s is too short for report parsing) | document the required grace period; drain before releasing the lease |
| R8 | Ordering-dependent recipients silently break | invariants 1-3 have **no test coverage today**; write those tests before touching the runtime |

---

## 11. Acceptance criteria

1. `apps/CodeCoverage` declares any number of queue names and creates **exactly one** subscription
   definition; `CoverageQueuesTests`' count/name guards are deleted as obsolete rather than worked
   around.
2. Two processes against one database: one feeds, the other stands by, **no `SubscriptionInUseException`
   is logged**, and killing the leader hands over within 40 s.
3. FIFO per queue proven by test: two messages on one queue always complete in order, with a slow
   handler in between.
4. Cross-queue isolation proven by test: a handler that blocks for 10 s on queue A does not delay a
   message on queue B.
5. Crash recovery proven by test: a message claimed then abandoned (process killed / lease expired) is
   reclaimed to `Pending` and completes. No message ever rests at `Processing` indefinitely.
6. A webhook delivery survives a leader kill mid-handler.
7. A redelivered GitHub webhook (same `X-GitHub-Delivery`) does not produce a second message.
8. Legacy `SparkMessaging-*` definitions are gone after deploy, verified against the server.
9. `SubscriptionPerQueue` mode still works and is covered.
10. Graceful shutdown drains in-flight messages; nothing is parked with a cancelled token.

---

## 12. Interaction with the WebhooksDemo migration

`docs/coverage_project_automation_PRD.md` §C1 is a hard constraint — "no new queue name" — and its FR5
lands project automation as a sibling recipient on the catch-all queue specifically to obey it. This
rework **removes that constraint**. Once it ships, project automation may take its own queue name and
get real FIFO isolation from coverage ingestion, which is strictly better than sharing.

Sequencing: this rework should land **first**, then that PRD's §C1 and FR5 can be relaxed. If the
project automation ships first, it must still obey §C1 as written.

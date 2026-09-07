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
retry rollup — are untouched. Both types are `internal`, so the published API can survive unchanged
even though breaking changes are permitted.

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

# Plan — One RavenDB subscription for all Spark messaging

Companion to [messaging_single_subscription_PRD.md](messaging_single_subscription_PRD.md). Labels
F1…F7 (findings), B1…B9 (defects), R1…R8 (risks), W1…W4 (webhook durability) and the numbered
invariants refer to that document.

**Transport decision: Option A** — leader-elected single subscription. Option B (document-queue,
zero subscriptions) was considered and rejected; see PRD §3b. Do not re-litigate it here.

**One pull request.** The transport rework, the message-stranding fix, the shutdown drain, the
migration-lock fixes and the leader-gating of the other singletons land together. Ships as
`10.0.0-preview.74` across all 22 packages — the major digit does not move (NuGet major tracks the
targeted .NET major).

**Test discipline.** Verify milestones by reading code and building. Run the suites **once**, in the
M11 sweep. Commit per milestone; batch the test *runs*.

---

## Spikes first

Three unknowns can each invalidate a milestone. Nothing from a spike is committed.

### S1 — Legacy subscription deletion, under the cap, in the right order

**The deploy blocker (F6, R1).** Questions: does `store.Subscriptions.GetSubscriptionsAsync` +
`DeleteAsync(name)` remove a definition such that a subsequent create succeeds on a 3-cap licence?
Is delete-then-create within one boot safe? Is it idempotent across restarts? And critically —
**does the migration stage actually complete before `MessageSubscriptionManager` starts**, or do they
race?

**Method:** embedded Raven via `SparkTestDriver`, licence-capped if possible. Create three
definitions, then run the intended cleanup + single create. Separately, instrument the real startup
order in `apps/CodeCoverage`: log from a throwaway migration and from `MessageSubscriptionManager.ExecuteAsync`
and compare. The PRD asserts migrations run in the `AfterSpark` stage before hosted services —
**verify, do not assume.**

**Kill criterion:** if the ordering cannot be guaranteed, the cleanup moves into the messaging
startup path itself (the same middleware slot that deploys the messaging index) rather than a
migration. That slot has no lock and no marker, which is acceptable only because the operation is
idempotent and self-emptying.

#### S1 — RESULTS (run 2026-09-07, RavenDB 7.2 at localhost:8080, database `Spike273`)

Ten scenarios, run against a live server. **Kill criterion not triggered** — delete-then-create in
one boot is safe. But the spike produced two new requirements for M6 and one measurement that
invalidates part of the test strategy.

**The cap cannot be reproduced locally.** The local licence is **Developer**, not Community, and it
reports `MaxNumberOfSubscriptionsPerDatabase: null` *and* `MaxNumberOfSubscriptionsPerCluster: null`.
Twelve subscriptions were created on one database without complaint. So the cap must be *simulated*
(the spike enforces a budget of 3 itself) or tested against a genuinely Community-licensed server,
and `EnsureSubscriptionExistsAsync`'s `LicenseLimitException` branch stays **unverified by any test**.

1. **Delete is idempotent.** `DeleteAsync` on a name that does not exist does not throw. The cleanup
   can run unconditionally — no existence check, no marker document.
2. **Delete frees the slot synchronously.** It returns in ~2 ms, and a list issued immediately
   afterwards never sees the name. Delete-then-create within one boot is safe. Measured on a single
   node; production is single-node, but this does not generalise to a 3-node cluster.
3. **Create is not an upsert.** Creating an existing name with a different query throws
   `RavenException` wrapping `RachisApplyException`: *"the name '…' is already in use in a
   subscription with different Id"*. This is the **normal path on every Spark restart**, absorbed by
   the existing `UpdateAsync { CreateNew = true }` fallback — so `EnsureSubscriptionExistsAsync` is
   correct, but its broad `catch (Exception)` is load-bearing, not defensive. Do not narrow it
   without replacing it with an explicit exists-check.
4. **`UpdateAsync` changes the query in place** — same `SubscriptionId`, same change vector. **No
   slot is spent to change a query.** The unified subscription's query can evolve across deploys
   without ever touching the budget.
5. **A filter change within one collection does not replay.** Narrowing `from Widgets` to
   `from Widgets where …` re-delivered **0 of 3** acknowledged documents. The unified query is always
   `from SparkMessages where …`, so this is the case that matters: **query evolution is
   non-replaying.**
6. **A collection newly entering scope *is* backfilled.** Widening to `from @all_docs` delivered all
   3 pre-existing `Gadgets` the old query never matched, *despite their being behind the
   acknowledged change vector.* The change vector does not gate documents in a newly-matched
   collection — the opposite of the obvious assumption.
7. **A connected worker survives a query change.** It takes one internal `SubscriptionClosedException`
   → `OnSubscriptionConnectionRetry` → reconnect, then serves the **new** query with no restart.
   Notably the exception did *not* propagate out of `Run`, so `SparkSubscriptionWorker`'s
   `catch (SubscriptionClosedException)` → non-recoverable → `break` never fired.
8. **⚠ Deleting a subscription under a live worker kills that worker permanently.** The worker sees it
   in ~16 ms as `SubscriptionDoesNotExistException`, which `SparkSubscriptionWorker.cs:234` treats as
   non-recoverable and **breaks the loop**. It does not recover once the new subscription exists.
   *New requirement for M6:* the rolling deploy must terminate old pods before the cleanup runs —
   it cannot rely on them healing.
9. **⚠ Two replicas racing to prune-and-create is not benign.** Both deletes succeed (idempotent),
   then the loser's *create* throws `RavenException`/`RachisApplyException` on the name collision.
   This happens at startup, **outside `Run`**, where nothing catches it — so replica B faults on
   boot. *New requirement for M6:* treat "already exists" at create as success.
10. **Field evidence for F6.** The local `WebhooksDemo` database carries **7** `SparkMessaging-*`
    definitions, 6 of them orphaned by generic-type-name churn (a `` GitHubWebhookMessage`1-… ``
    form plus assembly-qualified `Version=2.0.0.0` and `Version=3.0.0.0` variants). Nothing has ever
    deleted one. This is exactly the accumulation that exhausts a 3-slot budget.

**Still unanswered:** the startup-ordering half — whether migrations really complete before
`MessageSubscriptionManager` starts. That needs the `apps/CodeCoverage` instrumentation, not a
standalone spike.

### S2 — `WaitForFree` standby and handover

Does unconditional `WaitForFree` actually give clean active/standby? Two workers, one subscription:
the second must **block inside `Run()`** rather than throw, and must take over promptly when the
first's connection closes. Measure handover latency on a graceful close and on an abrupt one.

**Decides** whether the leader lease is needed for safety at all or only for liveness and for gating
the other singletons — which is the PRD's central claim (§4).

**Kill criterion:** if `WaitForFree` does not block cleanly, the lease becomes the sole exclusivity
mechanism and the design needs a fencing token the side effect can validate — a materially harder
problem. Find this out now, not in M4.

### S3 — Claim-then-ack preserves FIFO, isolation and crash recovery

Prototype the feeder + per-queue pumps against embedded Raven and assert the three invariants that
**no test covers today** (PRD invariants 1-4):

- two messages on one queue complete in order, with a slow handler between them;
- a handler blocking 10 s on queue A does not delay a message on queue B;
- a message claimed and then abandoned (kill the process / expire the lease) is reclaimed to
  `Pending` and completes — the same message, exactly once more.

Assert the retry **arrives**, never merely that it does not arrive early: the repo's own history
records retry tests that asserted a negative and stayed green when delivery was entirely dead.

**Decides** batch size, lease TTL/renewal ratio, and whether the pump needs its own session per tick.

### S4 — Does a server-side `@refresh` actually redeliver? (PRD §3c)

One test, and the harness already exists: `tests/MintPlayer.Spark.Tests/_Infrastructure/SubscriptionQueryCapabilityTests.cs`
has the `Widget` + delivery fixture that pinned the `now()` findings.

**Method:** send `ConfigureRefreshOperation(new RefreshConfiguration { Disabled = false,
RefreshFrequencyInSec = 5 })`; store a `Widget { Status = "Failed" }` with
`@refresh = UtcNow + 3s`; subscribe to `from Widgets where Status = 'Failed' and not exists(@metadata.@refresh)`;
assert **delivery** within ~30 s. Assert the positive — that it arrives — never merely that it does
not arrive early; the repo has already shipped retry tests that asserted a negative and stayed green
while delivery was entirely dead.

Refresh is not licence-gated (Community lists "Document Expiration & Refresh"), so the test driver
needs no licence opt-in.

**Why it is worth one test even though we are not adopting it here:** the repo's contrary conclusion
was reached with refresh *disabled* and a `now()` query, so it never actually tested this hypothesis.
Green settles B11 — either enable refresh and make `RetryNumerator` real, or delete its writes. Red
means the docs describe behaviour the pinned 7.1.10 server does not deliver, which is worth knowing
before anyone reconsiders §3c.

**⚠️ Note for S1:** the local server is **not** Community-capped — the local `Coverage` database
currently holds **8** `SparkMessaging-*` definitions, including the five that were silently dead in
production. So the subscription-cap spike cannot be reproduced against localhost as-is; it needs a
Community-licensed or artificially capped server, or it will pass locally and fail on deploy. That
same observation is direct confirmation of F6: nothing ever removes a definition.

---

## Milestones

### M0 — Branch and the free wins

- Branch `feat/messaging-single-subscription` off `master`.
- **Unconditional `WaitForFree`** (F1) — delete the `if (Database != null)` guard at
  `SparkSubscriptionWorker.cs:168-171`. Valid on its own merits; also fixes replication's
  `SyncActionSubscriptionWorker` for free, since it is the other `SparkSubscriptionWorker<T>` subclass.
- **B9**: `CoverageQueues.cs:8-9,18` — "AGPL/open-source licence" → registered Community; "previously
  declared five" → seven declared, five dead.
- **B3**: `return` → `continue` in the batch loop's three dead-letter branches
  (`MessageSubscriptionWorker.cs:103,112,122`). Must precede any batch-size change.
- **B10**: fix the inverted licence comment at `SparkMessagingExtensions.cs:54` and the reasoning it
  produced in `docs/issue_233_plan.md:44-46`. The 36 h Community limit is a **ceiling on the
  interval**, not a floor; the default is 60 s. Drop the explicit `DeleteFrequencyInSec` and take the
  default unless there is a reason not to.
- **B11**: resolve `RetryNumerator`'s inert `@refresh` writes after S4 — correct the false disclaimer
  at `:11`, and either enable refresh (with the startup assertion PRD §3c requires) or delete the
  writes at `:57`/`:74`. Do not leave inert code that reads as a working mechanism.

**Verify:** solution builds.

### M1 — Durable claim on the message (B1, W2)

The webhook-drop fix, and a prerequisite for the feeder.

- `SparkMessage` gains `OwnerId` (string?) and `ClaimExpiresAtUtc` (DateTime?).
- The claim writes `Status = Processing` + owner + expiry and **saves before anything else happens** —
  under optimistic concurrency, which nothing in `apps/CodeCoverage` currently enables. A second
  feeder's claim of an already-claimed document must fail on change vector.
- Reclaim path: expired claims go back to `Pending` with `AttemptCount++`. Put it where it will
  actually run — extend `MessageRetrySweeper`'s query to include `Processing` with an expired claim.
  Keep field-level patches, never load-modify-save (invariant 9).
- **B8**: add the `WakeUp != true` guard the sweeper's replication twin already has
  (`SyncActionRetrySweeper.cs:88`), so it stops re-patching the same set every 30 s per replica.

**Verify:** builds; grep that `Processing` now has a reader.

### M2 — Graceful shutdown (B2, B4)

- `MessageSubscriptionWorker.cs:300` — park with `CancellationToken.None`, not the token that just
  fired. Today the park always throws on SIGTERM, which is half of why messages strand.
- `MessageSubscriptionManager.StopAsync` — stop feeding, drain the pumps, *then* release the lease,
  *then* close the subscription. Order matters (PRD §4): releasing the lease first lets the incoming
  pod feed while this pod's pumps still run the same queues, which is the one thing that breaks FIFO.
- Document the required `terminationGracePeriodSeconds` — it must exceed the longest handler, and the
  lease TTL must exceed it in turn. The k8s default of 30 s is **not** enough for report parsing.

**Verify:** builds; re-read the shutdown ordering against PRD §4.

### M3 — The feeder and the per-queue pumps (F4, invariants 1-3)

- One subscription, `SubscriptionName = "SparkMessaging"`, RQL with **no `QueueName` predicate** —
  which also retires the RQL-injection surface `QueueNames.IsValid` exists to guard. Keep the
  status/`WakeUp` arms exactly as they are; **no `now()`, ever** (invariant 7).
- The batch callback becomes a feeder: claim (M1) → ack → hand to an in-process channel keyed by
  `QueueName`. It performs no handler work and makes no outbound calls.
- One pump per queue name, drained concurrently; strictly one in-flight message per pump (FIFO).
- Preserve per-message semantics wholesale: one DI scope and one Raven session per message, handlers
  serial in persisted order, `Handlers[]` materialized once from DI on first pickup, per-handler
  retry accounting, `NonRetryableException` first-attempt dead-letter, and the roll-up rules
  (invariants 5, 6, 10).
- Keep `MaxDocsPerBatch = 1` initially. Raise it only after S3 says the feeder is the bottleneck, and
  only with B3 already fixed.

**Verify:** builds; the ~290 lines of dispatch/allow-list/checkpoint/rollup logic should be untouched.

### M4 — Leader lease (liveness only)

- `spark/messaging/leader`, value `MessagingLease(NodeId, AcquiredAtUtc, ExpiresAtUtc, Fence)`.
  `NodeId` = pod/machine name + per-process GUID so a restarted pod is a different holder.
- TTL 30 s, renew every 10 s. Renewal CASes on `current.Index` **and** `NodeId == mine`; another
  `NodeId` means eviction → tear down feeder and pumps immediately, stop renewing.
- Release CAS-deletes guarded on holder identity — never the migration runner's unconditional delete.
- Standbys poll every 5 s, run no feeder/pumps/sweeper, but keep a `WaitForFree` worker **parked** so
  the queue still drains if the lease machinery wedges.
- Model it on `SparkMigrationRunner`'s lease shape, not the cron scheduler's occurrence marker — cron
  stores a monotonic stamp, which is a run-once dedup, not a renewable lease.

**Verify:** builds; confirm the lease is nowhere load-bearing for correctness (PRD §4).

### M5 — Modes (`SparkSubscriptionWorkerOptions`)

`SingleSubscription` (default) and `SubscriptionPerQueue` (today's behaviour, for deployments with
licence headroom wanting server-side per-queue isolation). Both must work and both must be tested.

### M6 — Legacy subscription cleanup (F6, R1, uses S1)

- Enumerate via `GetSubscriptionsAsync(0, 1024)`, delete every name starting `SparkMessaging-`
  (ordinal), log each deletion at Information. **Prefix, not a hardcoded list.**
- Home decided by S1: a migration in the `AfterSpark` stage (preferred — cluster-wide lock plus
  applied-once marker, and it replays on restored backups) or the messaging startup slot.
- If it ships as a migration inside `MintPlayer.Spark.Messaging`, first confirm the migration source
  generator discovers migrations from a *referenced package* and not only from the compilation being
  built.

- **Create must tolerate "already exists" as success** (S1 result 9). Two replicas booting together
  both prune successfully, then the loser's create throws `RavenException`/`RachisApplyException` on
  the name collision, at startup and outside `Run`, where nothing catches it. Unguarded, the second
  replica faults on boot.
- **The cleanup must not run while an old pod still holds a `SparkMessaging-*` subscription**
  (S1 result 8). Deleting it makes that pod's worker throw `SubscriptionDoesNotExistException`, which
  the worker treats as non-recoverable and **never recovers from**, even after the new subscription
  exists. Old pods must be terminated, not left to heal. Document this as a deploy step; it is not
  something the code can enforce.
- Deleting unconditionally is fine — `DeleteAsync` on a missing name does not throw (S1 result 1) —
  so no marker document is needed for correctness, only for auditability.

**Verify:** S1's assertions pass. Note that the licence-cap assertion **cannot** be verified locally
(S1: the Developer licence enforces no cap at all); it needs a Community-licensed server or an
explicit simulated budget.

### M7 — Webhook durability (W1-W4)

- **W1**: leave the broadcast-before-200 ordering alone. Re-read it and add a comment saying why.
- **W3**: any pod accepts webhooks whether or not it holds the lease — the producer is not
  leader-gated. GitHub does **not** auto-retry a failed delivery, so a 5xx is a lost webhook needing
  manual redelivery.
- **W4**: derive the webhook `SparkMessage` id from `X-GitHub-Delivery` (carried at
  `SparkWebhookEventProcessor.cs:183`, currently unused) so a redelivery is an idempotent upsert
  rather than a second message.

### M8 — Leader-gate the other singletons (PRD §7, in-scope items)

- `MessageRetrySweeper` and `SyncActionRetrySweeper` → leader-only.
- `IndexCreation.CreateIndexes` (`SparkMiddleware.cs:649`) → leader-only or version-gated; otherwise a
  rolling deploy has old and new pods pushing different definitions of the same index name, flapping
  it into repeated full re-indexing.
- `SmeeWebhookTunnelService` → leader-only **and** tighten its gate to `IsDevelopment()`. smee.io
  broadcasts to every connected client, so N replicas process every webhook N times today, silently,
  and the gate is config-only despite the class comment saying dev-only.

### M9 — Migration-lock correctness (B5, B6, B7)

- **B5**: the lock loser must not skip and serve an un-migrated database
  (`SparkMigrationRunner.cs:37-41`) — block on the applied-once markers, or refuse readiness.
- **B6**: `ReleaseLockAsync` (`:107-114`) must CAS on holder identity; today it can delete another
  pod's lease.
- **B7**: renew the migration lease, or give it a TTL a real backfill cannot outlive. 30 minutes
  unrenewed is a coin flip.

### M10 — Tests

Port and extend, don't reinvent. `MessageSubscriptionWorkerE2ETests` (579 lines, 11 facts) drives
real subscriptions plus the real sweeper against `SparkTestDriver` and is the behavioural guard.

- Re-plumb all 9 `tests/MintPlayer.Spark.Tests/Messaging/*` files for the new construction.
- Rewrite `MessageSubscriptionManagerLifecycleTests` — its premise ("a worker per discovered queue")
  is exactly what this change deletes.
- **Delete** `CoverageQueuesTests`' count and exact-name facts as obsolete, and re-motivate
  `DeleteDataActionTests:81-89`. Do not work around them.
- **New, covering the four untested invariants** (R8): FIFO within a queue; cross-queue isolation;
  crash-mid-handler reclaim; single-consumer exclusivity across two workers.
- New: a webhook delivery survives a leader kill mid-handler; a repeated `X-GitHub-Delivery` produces
  no second message; `SubscriptionPerQueue` mode still works.

### M11 — The single test sweep

`dotnet test` for `tests/MintPlayer.Spark.Tests` and `CodeCoverage.Tests`, the client vitest run, and
`nx run-many --target=build`. Read the numbers, not just pass/fail — zero failures on an idle machine
proves nothing about this subsystem, and a jsdom `_namespaceURI` rejection is a fixture-lifetime
problem, never a reason to re-run until green.

### M12 — Docs

- `docs/prd/PRD-SubscriptionWorker.md` §8.2 (`:459-479`) and `:180` — add a superseding section; this
  PRD is now the design of record.
- `libs/messaging/.../README.md` — roughly a third of its 372 lines assert one-subscription-per-queue
  (`:5,32,44,46,165-171,177-184,242-244,272,300,335,342`).
- `libs/subscription_worker/.../README.md:325` uses `$"SparkMessaging-{_queueName}"` as its worked
  example of `SubscriptionName`.
- `apps/CodeCoverage/.../CoverageQueues.cs:3-36` — the 33-line doc comment's whole argument is now
  obsolete. Replace it; don't delete the history, record that the cap no longer scales with queue count.
- `docs/coverage_project_automation_PRD.md` §C1 and FR5 — annotate that this rework removes the
  constraint, per PRD §12.

### M13 — Manual verification

- Two processes against one database: confirm one feeds and one stands by with **no
  `SubscriptionInUseException` logged**; kill the leader and time the handover (target ≤40 s
  ungraceful, ~5 s graceful).
- Confirm exactly one subscription definition exists on the server afterwards, and that no
  `SparkMessaging-*` legacy definitions remain.
- `dotnet run` CodeCoverage (never `ng serve` alongside; wait for the dev server's `➜ Local:` line),
  send a real webhook through the tunnel, kill the process mid-handler, and confirm the message is
  reclaimed and completes.

---

## PR checklist

- [ ] Exactly one subscription definition per app in `SingleSubscription` mode, verified on a server
- [ ] No `SubscriptionInUseException` in a two-process run
- [ ] `Processing` has a reader and a reclaim path; no message can rest there indefinitely (B1)
- [ ] Park uses `CancellationToken.None` (B2); `return` → `continue` fixed (B3); drain on SIGTERM (B4)
- [ ] Migration lock: loser blocks (B5), release CASes on identity (B6), lease renewed (B7)
- [ ] Sweepers, index creation and the smee tunnel are leader-gated (M8)
- [ ] No `now()` in any subscription query (invariant 7); `WakeUp` still consumed on pickup and park
- [ ] Legacy definitions deleted by **prefix**, idempotently (M6)
- [ ] Webhook broadcast still precedes the 200; any pod accepts webhooks (W1, W3)
- [ ] `LicenseLimitException` on create is still fatal (invariant 12)
- [ ] The four previously-untested invariants now have tests (R8)
- [ ] `SubscriptionPerQueue` mode covered
- [ ] **No back-compat hedging** — PRD §9b's eight simplifications taken, not deferred: typed webhook
      envelope gets `[MessageQueue]`, the `queueName` broadcast override deleted, empty
      `SparkSubscriptionOptions` deleted, `RetryNumerator`'s inert `@refresh` resolved, the three
      retry implementations collapsed to one, `SparkSubscriptionWorker<T>`'s virtuals reshaped,
      `CoverageQueues` guards deleted rather than re-motivated
- [ ] **In-flight production documents accounted for** (PRD §9b) — API freedom is not document-shape
      freedom. State in the PR whether the queues were drained first or a migration rewrites existing
      `SparkMessages`, and how it was verified
- [ ] **Version diff reviewed** — all 22 packages to `preview.74`, major digit unchanged. CI publishes
      on push to `master`; a wrong major is burned forever
- [ ] PRD §7's out-of-scope k8s items are recorded somewhere durable, and the PR does **not** claim
      that N replicas are safe

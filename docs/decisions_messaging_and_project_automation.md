# Decision register — messaging rework + project-automation migration

**Read this first.** It is the durable record for two in-flight initiatives. Everything here was
decided or measured on **2026-09-07**; the reasoning lives in the two PRDs, but the *decisions* and
the *corrections to previously-held beliefs* are consolidated here so they survive a lost session,
a context compaction, or a documentation rewrite.

Detail lives in:

- [messaging_single_subscription_PRD.md](messaging_single_subscription_PRD.md) + [plan](messaging_single_subscription_plan.md)
- [coverage_project_automation_PRD.md](coverage_project_automation_PRD.md) + [plan](coverage_project_automation_plan.md)

Neither is implemented. Branch: `feat/coverage-project-automation`.

---

## 1. Decisions taken

| # | Decision | Rationale in one line |
|---|---|---|
| **A1** | Absorb `apps/WebhooksDemo`'s Projects-V2 automation into `apps/CodeCoverage`, then delete the app | One production feature behind its own App, DB, container and deploy workflow |
| **A2** | **Reimplement, do not copy.** The bespoke Angular page, `GitHubProjectsController`, `GitHubAppInfoController` and `OrganizationAccessService` are dropped, not ported | Owner: "No code-copy, but migration". `GitHubAccessService` already does the org allow-list, with caching the dropped service lacked |
| **A3** | Boards become a **persisted, reconciled mirror** (`GitHubProject` entity + `AutomationEnabled` flag), not a live-API query | An enabled board must be a document either way — both recipients already query it. A flag also makes disable non-destructive of the user's rules |
| **A4** | Head-branch deletion becomes **opt-in per board**, default off | `Contents: write` is now granted, which is exactly why it needs a gate: it mutates other people's repositories |
| **A5** | Implement **all 19** event mappings, including the 3 previously inert ones | Owner decision; permissions and events already added and approved |
| **A6** | Messaging moves to **one subscription, leader-elected** (Option A) | Keeps RavenDB owning exclusivity and ordering; the cap stops scaling with queue count |
| **A7** | The **lease is liveness only**, never the safety boundary | Safety = per-message durable claim + RavenDB `WaitForFree`; a two-leader window then costs an idle pod, not an ordering violation |
| **A8** | `WaitForFree` becomes **unconditional** | It is currently dead code behind `if (Database != null)`, which nothing sets. Valid under every option; ≤60 s poll gap → immediate handover |
| **A9** | Kubernetes means **HA + rolling deploys with one active consumer**; failover budget **≤30 s** | Owner decision. Not throughput scale-out |
| **A10** | Two modes on `SparkSubscriptionWorkerOptions`: `SingleSubscription` (default), `SubscriptionPerQueue` | Owner asked for multi-subscription-worker mode to remain available |
| **A11** | The **message-stranding fix ships in the same PR** as the transport rework | It is a live webhook-drop bug, and claim-then-ack makes it strictly worse if left |
| **A12** | **No backward compatibility.** Default is to simplify; anything kept needs a non-compatibility reason | Owner, stated twice. Eight simplifications enumerated in messaging PRD §9b |
| **A13** | Legacy `SparkMessaging-*` definitions deleted **by prefix**, from a migration | Nothing in the repo deletes a subscription; three stale definitions occupy the whole 3-slot budget |
| **A14** | `@refresh` **evaluated and recorded, but not adopted** in this rework | Real and proven, but a database-wide switch with a sweep-frequency floor; the sweeper works today |

## 2. Rejected — do not re-litigate

| Rejected | Why | Where |
|---|---|---|
| **Live-API composed query** for board discovery | Pays no-row-filtering, no-redaction, no-detail-page; no timeout on the query path (GitHub 5xx = 500 + empty grid); re-calls GitHub on every action click; its disable destroys the user's rules | coverage PRD D2 |
| **Document-queue with per-queue leases, zero subscriptions** | Downgrades etag/commit ordering → producer wall-clock, and server-enforced exclusivity → clock lease whose failure is silent double-processing of a FIFO head; ~600 lines of bespoke coordination in a subsystem whose every bug has been a bespoke-coordination bug | messaging PRD §3b |
| **`SubscriptionOpeningStrategy.Concurrent`** | Professional+ only, **and** explicitly abandons ordering | messaging PRD F5 |
| **`TakeOver` for the feeder** | Two pods that both believe they lead ping-pong evicting each other, each eviction dropping an unacked batch | messaging PRD §4 |
| **Porting the bespoke discovery page** | See A2 | coverage PRD §2.2 |
| **A framework `OnFilterReference` / reference-filter hook in Spark** | Not needed, and would be invented rather than ported. `[Reference(typeof(X), "query")]` + a `Custom.*` query reading `CustomQueryArgs.Parent` already gives per-parent option scoping — including from an embedded `AsDetail` row, where the client sends the **root** document as the parent. Investigated the prior-art framework for a hook to copy: its `GetReferenceSecurityFilter` is **not** it (args carry only `Name`, `Column` and a `Filter`, no parent, and its own summary scopes it to *grid filter dropdowns* — "distinct values, text search, and data filter operations"), and `OnAdd/OnRemove/OnSelectReference` all fire **after** the pick. Its consumers achieve per-parent scoping the same way Spark can: the declared lookup query plus a `Custom.<Method>(CustomQueryArgs)`. So there is no proven design to port, one consumer is not enough to design an abstraction from, and `ISparkRowRule<T>` / the optional-service pattern in `Abstractions/Authorization` is ready if a second consumer ever appears. | this register; `docs/guide-reference-attributes.md` |
| **Activating the free *Developer* licence in production to lift the subscription cap** | Nothing technical prevents it — production licences Raven by POSTing `raven-license.json` to `/admin/license/activate` (`f4373ce5`), so it is a one-file change on the VPS, and it does remove every cap. But the Developer tier is free *because* it is restricted to development and testing; `coverage.mintplayer.com` is public and CI-facing, the licence is issued to a named company rather than anonymously, and it expires in early 2027. Decisive engineering point: it buys **only** the queue budget and leaves the actual webhook-drop bug (`Processing` written and read by nothing) untouched, so it cancels none of this rework. Pursue an OSS-project licence from RavenDB instead; failing that, Community + this rework. | §4, this register |

## 3. Corrections to previously-held beliefs

These were wrong in the repo, in its docs, or in my own earlier statements this session. **Each one is
the kind of claim that gets re-adopted after a compaction, so it is recorded with its evidence.**

| # | The wrong belief | The truth | Evidence |
|---|---|---|---|
| **C1** | *"#367 morphed all MessageQueues into a single RavenDB subscription."* | Unchanged. #367 **consolidated queue names** to fit the cap and made the licence error fatal. `git show 6adddec4 --stat` over `libs/messaging` + `libs/subscription_worker` = 4 version bumps + 38 lines in `SparkSubscriptionWorker.cs`; the manager and worker were untouched | `MessageSubscriptionManager.cs:35-50`, `MessageSubscriptionWorker.cs:20,63-67` |
| **C2** | *"`SubscriptionInUseException` makes N-1 replicas useless."* (my own earlier claim) | It is caught as a 60 s hot-standby retry, and because each replica starts one worker **per queue** with independent races, **different replicas already own different queues** today | `SparkSubscriptionWorker.cs:214-220` |
| **C3** | *"The Community licence floors the refresh/expiration sweep at 36 hours."* | It is a **ceiling on the interval**, not a floor; the product default is **60 s**. Live proof: `Coverage`/`Spark` report `DeleteFrequencyInSec: 129600`, every prior-art database reports `null` | `SparkMessagingExtensions.cs:54`, `docs/issue_233_plan.md:44-46` |
| **C4** | *"`@refresh` alone doesn't gate change-vector-driven re-delivery"* / *"`@refresh` is doubly useless here"* | The RavenDB 7.1 refresh docs name **Subscriptions** explicitly among what the change-vector bump triggers, and the mechanism runs in production across **56 databases** | `RetryNumerator.cs:11`, `docs/issue_233_plan.md:68-70` |
| **C5** | *(implicit)* that `RetryNumerator`'s `@refresh` writes do something | `ConfigureRefreshOperation`/`RefreshConfiguration` appear **nowhere** in the repo; every `Spark*`/`Coverage*` database reports `Refresh: null`. The writes are inert | `RetryNumerator.cs:57,74` |
| **C6** | The cap is "3" | 3 **per database** *and* **15 per cluster**. A full-featured Spark app spends all three before declaring its own queue (`SyncAction` + `spark-etl-deployment` + `spark-github-all`); CodeCoverage only fits because replication is off there | ravendb.net/buy |
| **C7** | *(implicit)* that `EMessageStatus.Processing` is recoverable | Written at `MessageSubscriptionWorker.cs:83`, **read by nothing**. Not by the subscription query, not by the sweeper. Any crash mid-handler strands the message forever | grep over `libs/messaging` |

## 4. Measured facts worth not re-deriving

- `SubscriptionOpeningStrategy` appears **exactly once** in the repo, and nothing overrides `Database`.
- **Zero** occurrences of `now()`/`today()` in any subscription, query or index RQL across 55 prior-art
  repos, ~30 of which float `RavenDB.Client 7.2.*` — the versions that throw `NotSupportedException`.
  Independent corroboration of Spark's own measurement.
- 56 of 88 databases on the local server run `@refresh` with `RefreshFrequencyInSec: null` (60 s default).
- The local `Coverage` database still holds **all 8** `SparkMessaging-*` definitions, including the five
  that were dead in production → nothing removes a definition.
- **The local server is not Community-capped**, so the subscription-cap spike cannot be reproduced
  against localhost — it would pass locally and fail on deploy. Now quantified: the licence is
  **Developer** and reports `MaxNumberOfSubscriptionsPerDatabase: null` *and*
  `MaxNumberOfSubscriptionsPerCluster: null`; 12 subscriptions were created on one database without
  complaint. Any cap behaviour must be simulated with a self-imposed budget, and
  `EnsureSubscriptionExistsAsync`'s `LicenseLimitException` branch is **unverifiable locally**.

### Reference option sources, established 2026-09-07 — full detail in `docs/guide-reference-attributes.md`

- **Only `[Reference]` can vary its options per object.** `TransientLookupReference<TKey>` is a
  static compile-time set; `DynamicLookupReference<TValue>` is persisted and runtime-editable but is
  **one global set per lookup name** (a single `LookupReferences/{Name}` document), so it would
  offer every parent's children under every other parent. This is the distinction I got wrong twice
  — by comparing only the two lookup shapes and generalising to "the model format cannot express
  it".
- **A reference target need not be a document type.** It needs a `clrType`, one `showedOn: Query`
  attribute, an Actions class, and a unique readable `Id` — all of which an **embedded value
  object** already has. So a child collection need not be promoted to documents to be pickable.
- **`Custom.*` may return a plain in-memory `IEnumerable<T>`.** Every Raven-specific step in the
  executor is guarded on the returned object actually being a Raven queryable; sorting, search and
  paging fall back in memory.
- **Entity-backed ≠ composed.** A `clrType` on the target means the query is *not* a composed
  (`clrType`-less) query, so none of the composed-query costs apply — no `ISparkOwnsRowSecurity`, no
  transferred row-security duty. This is what makes the pattern cheap.
- ⚠️ **An unlisted query yields a silently EMPTY dropdown.** `/spark/queries` only lists a query
  whose `entityType` the caller holds `Query` on, and the client treats an unknown query name as an
  empty result — no error, no log. The missing right is invisible.
- ⚠️ **Hand-added `lookupReferenceType` / `query` / `referenceType` are STRIPPED** by
  `--spark-synchronize-model`, which derives them from the C# attributes. `editMode` and
  `isReadOnly` survive; an inline `queries` entry on an embedded type is preserved.
- **`GetRowFilterAsync` cannot do parent scoping.** Signature is `(string action)`; the parent is
  never forwarded into row security. It answers "which rows may this *caller* see", never "which
  rows belong to this *parent*".
- **Spark gaps vs the prior art, for whoever picks this up later:** its query args carry only
  `Parent`/`ParentType`/`Query`/`Skip`/`Take`/`Search` — no *requesting attribute* and no *reason*,
  so one query cannot tell which reference asked; `args.Parent` is **re-loaded from the database**
  rather than being the client's unsaved in-memory object, which makes sibling-row de-duplication
  (a real prior-art pattern) impossible today; and there is no `RefreshOptions()`, no
  `SelectInPlace` mode, and no `Query.LookupSource` equivalent.

### Subscription lifecycle, measured 2026-09-07 (RavenDB 7.2, `Spike273`) — full detail in the plan's "S1 — RESULTS"

- **Delete is idempotent** (missing name does not throw) and **frees the slot synchronously** (~2 ms;
  a list issued immediately after never sees the name). Delete-then-create in one boot is safe. Single
  node only.
- **Create is not an upsert.** Same name + different query throws `RavenException` wrapping
  `RachisApplyException` *"already in use in a subscription with different Id"*. This fires on **every
  Spark restart** and is absorbed by the existing `UpdateAsync { CreateNew = true }` fallback — that
  broad `catch (Exception)` in `EnsureSubscriptionExistsAsync` is **load-bearing, not defensive.**
- **`UpdateAsync` changes a query in place**: same `SubscriptionId`, same change vector, **no slot
  spent.** The unified query can evolve across deploys without touching the budget.
- **Changing the filter within one collection does not replay** (0 of 3 acked docs re-delivered).
  Since the unified query is always `from SparkMessages where …`, query evolution is non-replaying.
- **But a collection newly entering scope IS backfilled** — widening to `from @all_docs` delivered 3
  pre-existing docs that sat *behind* the acknowledged change vector. The change vector does not gate
  a newly-matched collection.
- **⚠ Deleting a subscription under a live worker kills that worker for good.** It surfaces in ~16 ms
  as `SubscriptionDoesNotExistException`, which `SparkSubscriptionWorker.cs:234` treats as
  non-recoverable and breaks the loop — no recovery even after the new subscription exists. Rolling
  deploys must terminate old pods before pruning.
- **A two-replica prune-and-create race is absorbed by Spark's real path.** With a raw `CreateAsync`
  the loser throws on the name collision, but `EnsureSubscriptionExistsAsync`'s create → catch →
  `UpdateAsync { CreateNew = true }` survives it: measured, both replicas end up fine and one
  subscription remains. No guard needed — just don't add a second create site without the fallback.
  *(An earlier version of this bullet said replica B faults on boot. That was measured with the raw
  call and overstated the hazard.)*
- **`WaitForFree` gives clean, server-enforced active/standby with sub-second failover.** The standby
  blocks inside `Run()` — no exception, no retry churn. Handover measured at **962 ms** on a graceful
  `DisposeAsync` and **575 ms** on an abrupt kill of a *separate process* (no dispose, no FIN, 0
  reconnect retries). Confirms the PRD §4 claim: **the leader lease is for liveness, not safety.**
- **`@refresh` genuinely wakes a subscription** — proven, not inferred. With refresh at 5 s and a
  query gated on `not exists(w.@metadata.@refresh)`: not delivered at +2 s while `@refresh` was set,
  delivered **4,650 ms** after attaching once the sweep removed it. So `RetryNumerator`'s writes are
  inert *only* because `ConfigureRefreshOperation` is sent nowhere.
- **Startup ordering is guaranteed by construction, not by luck.** Migrations register as a
  `Registry.AddMiddleware` action; `SparkMigrationRunner.RunAtStartup` **blocks**
  (`.GetAwaiter().GetResult()`); `UseSpark` runs registry actions inline (`SparkMiddleware.cs:309`)
  at `Program.cs:324`, while `app.Run()` is at `:364` and hosted services start only inside it. So
  the legacy cleanup *may* be a migration — but see the next bullet for why it should not be.
- **The cleanup belongs on every boot, not in a migration.** Delete is idempotent and ~2 ms, so an
  every-boot prune self-heals if a stale definition ever returns; a migration's once-ever marker is
  exactly what would prevent that. This reverses the plan's original stated preference.
- ⚠ **The cleanup prefix and the unified subscription name are a matched pair.** `SparkMessaging`
  survives a `SparkMessaging-` prefix delete only because it lacks a trailing hyphen. Rename it with
  one and an every-boot cleanup deletes its own live subscription — the one failure mode here that a
  restart does **not** heal.
- **A connected worker survives a query *update*** — one internal `SubscriptionClosedException`, one
  reconnect, then it serves the new query with no restart. The exception does **not** propagate out of
  `Run`, so the worker's non-recoverable `SubscriptionClosedException` branch never fires.
- **Field evidence for the accumulation problem:** the local `WebhooksDemo` database holds **7**
  `SparkMessaging-*` definitions, 6 orphaned by generic-type-name churn (a `` `1-… `` form plus
  assembly-qualified `Version=2.0.0.0` and `Version=3.0.0.0` variants).
- **RQL trap that invalidated the first run of this spike:** `from Docs` means *the collection named
  "Docs"*, not all documents. A subscription on it delivers nothing, the change vector never advances,
  and a follow-up drain looks like a replay when it is really a first delivery. Use `from @all_docs`.
- Four build-time guards pin the queue rule: 3 in `CoverageQueuesTests.cs` + `DeleteDataActionTests.cs:81-89`.
- Query rows carry **no `can` block** (`QueryResult.cs:105-124`); a composed (`clrType`-less) type gets
  **no row filtering and no redaction**; a query returning `SparkQueryPage<T>` is **not re-executable**,
  so custom actions on its rows silently find nothing.

## 5. Sequencing

1. **Messaging rework first.** It removes the "no new queue name" constraint.
2. **Then relax** coverage PRD §C1 and FR5: project automation can take its own queue name and get
   real FIFO isolation from coverage ingestion, instead of riding the catch-all as a sibling recipient.
3. If project automation ships first, it **must** obey §C1 as written.

## 6. Open decisions

| # | Question | Recommendation |
|---|---|---|
| OD1 | Board freshness: nightly + manual resync only, or reconcile-on-view? | Nightly + resync |
| OD2 | Boards unit: top-level list, Account sub-query, or both? | Both |
| OD3 | Promote `TargetColumnOptionId` to a lookup over the board's cached `Columns`? | Yes |
| OD4 | `editMode: "inline"` for the rules sub-table? | Yes |
| OD5 | Adopt `@refresh` after spike S4? | **S4 is green** (delivered in 4.65 s), so the mechanism is proven. Still recommend *not* adopting it for this rework — the sweeper works — but B11 becomes a real either/or: enable refresh and make `RetryNumerator` real, or delete its inert `@refresh` writes. If enabled, the config **must** be asserted at startup |
| OD6 | Remove `SparkMigrationRunner`'s `.GetAwaiter().GetResult()`? | **Not in this rework.** All three sync-over-async sites in `libs/` are startup/config-time, none per-request, and `.GetAwaiter().GetResult()` is the correct form (preserves the exception; `.Result` would wrap it in `AggregateException`). ASP.NET Core has no `SynchronizationContext`, so there is no deadlock risk. The clean fix is `IHostedLifecycleService.StartingAsync` (.NET 10), whose phase runs before *any* `StartAsync` — but migrations sit at `SparkMiddleware.cs:309` **deliberately after** `CreateSparkIndexes` at `:296` because they may query indexes, so moving them means moving index creation too (and swapping `IndexCreation.CreateIndexes` for its async overload). That is a startup-model change, not a one-liner |

## 7. Documentation debt

Every item below is **verified stale** against the decisions above. This list is the docs work item;
it is deliberately separate from the plans' own milestones so it cannot be lost when they close.

### Asserts one-subscription-per-queue (contradicted by A6)

| File | What is wrong |
|---|---|
| `docs/prd/PRD-SubscriptionWorker.md:180`, `:459` (§8.2 heading), `:474` | "One subscription per queue with `MaxDocsPerBatch = 1`"; `:474`'s RQL still contains `NextAttemptAtUtc <= now()`, which cannot work |
| `libs/messaging/MintPlayer.Spark.Messaging/README.md:177,180` + `:5,32,44,46,165-171,242-244,272,300,335,342` | ~⅓ of 372 lines assert per-queue subscriptions and per-queue isolation |
| `libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker/README.md:325` | Uses `SparkMessaging-{_queueName}` + `MaxDocsPerBatch => 1` as the worked example of `SubscriptionName` |
| `docs/prd/PRD-Messaging.md`, `PRD-Messaging-Improvements.md`, `PRD-Messaging-Overhaul.md` | Describe the current runtime throughout |
| `apps/CodeCoverage/CodeCoverage/Feedback/CoverageQueues.cs:3-36` | 33-line doc comment whose whole argument ("Do not add a third name") is obsolete — and which also carries C3 and the AGPL/Community error |

### Carries a correction from §3

| File | What is wrong |
|---|---|
| `docs/issue_233_plan.md:39-49`, `:68-70` | The `@refresh` rejection: the inverted licence limit (C3) and "doubly useless" (C4). This is the reasoning that must not be inherited |
| `docs/issue_258_plan.md:91` | Calls `RetryNumerator`'s `@refresh` writes "redundant rather than load-bearing" — true today, but for the wrong stated reason |
| `libs/subscription_worker/.../RetryNumerator.cs:11,16` | Self-contradictory XML docs: `:16` says it schedules via `@refresh`, `:11` says `@refresh` doesn't gate redelivery |
| `libs/messaging/.../SparkMessagingExtensions.cs:54` | `// 36 hours (community license minimum)` — inverted (C3) |

### References `apps/WebhooksDemo`, which A1 deletes

`README.md:140` · `CLAUDE.md:20` · `libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/README.md:41,557-559,658,670-671`
(whole "Project board automation" section) · `libs/authorization/MintPlayer.Spark.Authorization/README.md:387`
· `docs/guide-docker-deployment.md` (built entirely on its compose file) · `docs/guide-nx-remote-cache.md:51,53,63`
· `docs/guide-row-security.md:38` (worked example is `GitHubProjectActions.GetRowFilterAsync` — becomes a
*better* example once it is a real row filter) · `docs/guide-authentication-schemes.md:440,473`
· `libs/node_packages/ng-spark-auth/sign-in/src/spark-sign-in.projection.spec.ts:104`

### Self-referential

`docs/coverage_project_automation_PRD.md` §C1 + FR5 — annotate once the messaging rework lands (§5).

### Leave alone

`libs/webhooks/**` itself (CodeCoverage and the Spark test suite both depend on it), and historical
PRD/plan/release-note mentions, which are prose about past work: `docs/actions_and_coverage_{PRD,plan}.md`,
`docs/prd/PRD-GitHub-Webhooks.md`, and the rest of the `docs/prd/` archive.

---

## 8. Standing constraints that outlive both initiatives

- **Never name the prior-art organisation, its repos or its file paths in committed files.** Describe
  it generically ("a comparable RavenDB framework", "prior art"). Grep before committing.
- **One PR.** Everything in scope for an initiative lands together — including fixes found along the
  way and changes in other repositories. Size is never a reason to split.
- **Version policy.** NuGet major = targeted .NET major; npm major = Angular major. An API break
  inside a generation is a minor/preview bump. CI publishes on push to `master`, so a wrong major is
  burned permanently. Messaging ships as `preview.74` across all 22 packages.
- **API freedom is not data freedom.** Live `SparkMessages` documents exist, some non-terminal.
  Drain the queues before a shape change, or migrate the documents before the messaging host starts.
- **Do not claim N replicas are safe.** The messaging rework makes *messaging* multi-replica-safe.
  Out of scope and still broken: `GitHubUserTokenService`'s process-local dictionary guarding
  single-use rotating refresh tokens, Data Protection keyrings on container filesystems in every app
  but CodeCoverage, and the total absence of `AddHealthChecks`/`IHealthCheck`.
- **Calibrate to the real deployment: one container, no external consumers.** Owner's steer, and it
  outranks theoretical robustness. The framework has no known third-party users and nothing else in
  production; the only deployment is a single container via `docker-compose` on one VPS. So a fault
  that is recoverable by `docker restart` is **not** a design constraint — record the symptom so it
  is recognisable, and move on. Do **not** add deploy ceremony, ordering requirements, guards or
  fencing for multi-replica or rolling-deploy scenarios that do not exist. The exceptions that still
  deserve engineering are faults a restart does *not* heal: data loss (the stranded `Processing`
  message), and self-inflicted state like a cleanup that deletes its own subscription on every boot.

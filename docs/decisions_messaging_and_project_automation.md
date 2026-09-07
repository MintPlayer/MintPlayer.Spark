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
- **The local server is not Community-capped** (it permits 8), so the subscription-cap spike cannot be
  reproduced against localhost — it would pass locally and fail on deploy.
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
| OD5 | Adopt `@refresh` after spike S4? | Decide on evidence; if yes, the config **must** be asserted at startup |

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

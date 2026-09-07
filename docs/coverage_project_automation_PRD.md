# PRD — Absorb WebhooksDemo's project automation into `apps/CodeCoverage`

**Status:** proposed, not implemented
**Date:** 2026-09-07
**Owner decision recorded:** reimplement, do not copy — "We should be able to re-implement the
github-projects ProgramUnit to the new system. No code-copy, but migration."
**Decisions, rejected alternatives and corrections to earlier beliefs:**
[decisions_messaging_and_project_automation.md](decisions_messaging_and_project_automation.md) —
read that first if you are picking this up cold. Note especially its §5: the messaging rework lands
first and **removes** §C1's "no new queue name" constraint below.

---

## 1. Why

`apps/WebhooksDemo` is a demo app carrying one genuinely useful production feature: GitHub
**Projects V2 board automation** — webhook events move issue and PR cards between board columns
according to per-board rules. It runs behind its own GitHub App, its own Raven database, its own
container, its own deploy workflow and its own smee channel.

`apps/CodeCoverage` is the production coverage server at coverage.mintplayer.com. It already owns
every subsystem the automation needs: a GitHub App with installation-token minting, a webhook
endpoint, an account/repository model reconciled against GitHub, a message bus, a cron host, a
generic model-driven UI, and a deployment pipeline.

Running two apps against two GitHub Apps to do two things to the same repositories is duplicated
operational cost with no benefit. The owner has already copied the old App's permissions and events
onto **CoverageDevelopment** and **CoverageProduction**, and has already granted `Contents: write`
and accepted the new permissions on the MintPlayer org installation.

**Goal:** the project-management configuration becomes a new ProgramUnit in CodeCoverage, expressed
through the generic Spark model — and `apps/WebhooksDemo` is deleted from the repository.

---

## 2. Scope

### 2.1 Functionality that moves

| Capability | Today | After |
|---|---|---|
| Discover Projects V2 boards reachable by the caller | bespoke Angular page + 3 MVC endpoints + raw GraphQL string | reconciled `GitHubProject` documents + generic query |
| Enable/disable automation per board | create/delete the document from a bespoke toggle | `AutomationEnabled` boolean on the generic form |
| Configure event → column rules | `AsDetail` embedded collections on the generic PO | unchanged idiom, carried over |
| Move issue cards on `issues` events | `HandleIssuesEvent`, typed envelope recipient | a case in a catch-all recipient |
| Move PR cards on `pull_request` events, incl. linked issues | `HandlePullRequestEvent` | same |
| Delete head branch on PR close | `DeleteBranchOnPullRequestClose`, org-wide unconditional | opt-in per board (**D3**) |
| Re-read board columns from GitHub | `SyncColumns` custom action | unchanged idiom, carried over |
| `pull_request_review`, `issue_comment`, `check_run` mappings | **declared but inert** | **implemented** (owner decision) |

### 2.2 Functionality that is dropped, deliberately

- `Recipients/LogAllWebhooks.cs`, `LogPullRequest.cs`, `LogIssues.cs` — logging only. `LogIssues`
  additionally posts a "Thanks for creating this issue" comment (demo behavior, not wanted on a
  production coverage bot) and ends in a dead `projectIds` GraphQL query whose result is discarded.
- `Controllers/GitHubProjectsController.cs` — replaced by the generic query. Also carries a defect:
  its GraphQL query is built by **string interpolation of `ownerLogin`**.
- `Controllers/GitHubAppInfoController.cs` — `[AllowAnonymous]`, returns the app slug. CodeCoverage
  already resolves the slug per request and ships it on `GET /api/me/accounts` as `GitHubAppUrl`,
  already rendered by `home-extras.component.ts`. Note `[AllowAnonymous]` beats `[SparkAuthorize]`,
  so this controller should not land on a production host regardless.
- `Services/OrganizationAccessService.cs` — a strict subset of CodeCoverage's
  `GitHubAccessService`: same `/user/installations` source, but request-lifetime cache only, no
  token-state, no 401 retry, no `Account` backfill.
- `ClientApp/src/app/pages/github-projects/*`, `pages/home/*`, `services/github-projects.service.ts`,
  `models/github-project.ts`, `app.spec.ts` — no bespoke Angular survives.
- `AddSmeeDevTunnel` / `AddWebSocketDevTunnel` wiring. CodeCoverage deliberately replaced the first
  with `Services/SmeeWebhookTunnelService.cs`, because the framework tunnel's Newtonsoft round-trip
  rewrites fractional-second timestamps and breaks signature validation. The second is the *client*
  side and belongs on a developer machine.
- WebhooksDemo's `Dockerfile`, `docker-compose.yml`, `.env.example`, `README.md`, `DEPLOYMENT.md`,
  `AGENTS.md`, `project.json`s, `launchSettings.json`, `App_Data/*`, both `.csproj`s and its
  `.slnx` folder entries.

### 2.3 Non-goals

- Changing how coverage ingestion, badges, gates or PR comments work.
- Changing the messaging architecture or the RavenDB licence.
- Preserving WebhooksDemo's database. Nothing is migrated; boards are rediscovered from GitHub.
- Rewriting the four docs guides that use WebhooksDemo as their worked example beyond repointing
  them (see §7).

---

## 3. Hard constraints

These are verified facts that the design is not free to ignore.

### C1 — ~~One RavenDB subscription per distinct queue name. Still.~~ **LIFTED**

> **Status: no longer binding.** The messaging rework has landed, so messaging runs a single
> subscription (`SparkMessaging`) for every queue with per-queue FIFO from in-process lanes. **A new
> queue name now costs no subscription**, and this constraint no longer shapes the design.
>
> What changes for this initiative:
> - **FR5** may take its own queue name instead of sharing `spark-github-all`, which gives project
>   automation genuine FIFO isolation: a slow board reconciliation cannot delay coverage feedback,
>   and its retry/dead-letter state is its own.
> - **R1** ("a new queue name slips in and kills a queue") is no longer a risk of that kind. The
>   remaining consideration is ordering, not capacity: one queue is one FIFO lane, so the question
>   is only whether two workloads should be serialised with each other.
> - The four build-time guards that pinned the queue count are deleted; `MessageSubscriptionManagerLifecycleTests`
>   asserts the property that matters now — however many queues are declared, one subscription
>   exists.
>
> The rest of this section is retained as the record of *why* the constraint existed and what it
> cost, because five silently dead queues in production is the reason the rework happened.

Re-verified 2026-09-07 on master `6adddec4`. `MessageSubscriptionManager` starts one worker per
discovered queue name (`libs/messaging/.../MessageSubscriptionManager.cs:35-50`) and each worker's
subscription is `$"SparkMessaging-{_queueName}"` with RQL `where QueueName = '{_queueName}'`
(`MessageSubscriptionWorker.cs:20,63-67`).

`git show 6adddec4 --stat` over `libs/messaging` + `libs/subscription_worker` touched **4 version
bumps and 38 lines in `SparkSubscriptionWorker.cs`** — the manager and worker were not modified.
What #367 actually did was *consolidate* seven queue names onto two constants to fit the cap, and
make `LicenseLimitException` on subscription create **fatal instead of swallowed**. Queue names are
not free; the failure is merely loud now instead of silent.

Production RavenDB is a registered **Community** licence, `MaxNumberOfSubscriptionsPerDatabase: 3`,
and all three are spoken for: `spark-github-all` (framework), `coverage-parse-session`,
`coverage-publish-feedback`.

**Therefore:** project automation adds **no new queue name**. It lands as a sibling
`IRecipient<GitHubWebhookMessage>` on the existing catch-all queue. Recipient registration is
additive (`RecipientRegistrationGenerator.Producer.cs:42` emits `AddScoped`, not `TryAdd`) and the
worker resolves `GetServices(...)` and records one `HandlerExecution` per recipient
(`MessageSubscriptionWorker.cs:140-151`), so a sibling gets **independent retry and dead-letter
state** from `GitHubEventsRecipient` — strictly better than extending its switch. Any deferred work
that needs its own message type goes on `CoverageQueues.Publishing`.

Four build-time guards pin this and must stay green: three in
`CodeCoverage.Tests/Feedback/CoverageQueuesTests.cs` (count ≤ 2, exact names, ingestion ≠
publishing) and `CustomActions/DeleteDataActionTests.cs:81-89` pinning `DeleteRepositoryDataMessage`
to `Publishing`.

**Cost accepted:** one queue is strict FIFO, so a slow Projects-V2 mutation delays coverage webhook
processing behind it. Mitigation is in FR7.

### C2 — One webhook URL, and it is already correct.

Neither app overrides `WebhookPath`; both take the library default `/api/github/webhooks`
(`GitHubWebhooksOptions.cs:11`). No GitHub-side URL change is needed. Signature validation is
fail-closed HMAC-SHA256 on the raw body (`SignatureService.cs:11-37`) and runs **before** the
dev-forwarding branch, so CoverageDevelopment and CoverageProduction must share one
`GitHub:WebhookSecret` value or forwarded dev deliveries fail silently with a warning.

### C3 — Query rows have no `can` block.

`QueryResultItem` is `Id`, `Breadcrumb`, `Values`, `TypeHints` and nothing else
(`Abstractions/QueryResult.cs:105-124`); `ICustomAction` has one member and the catalogue endpoint
is never told which row is open. Available granularity is type-level (the `{Action}/{Type}` right),
one-object (`PersistentObject.DisableActions`), and whole-result
(`CustomQueryArgs.DisableActions`). Per-row enforcement exists only at execute time, all-or-nothing.

**Therefore:** a per-board toggle button in a grid cell is not expressible. Board state is a
**column plus the generic checkbox**, which is what D1 buys.

### C4 — A `clrType`-less composed type pays a row-security tax and loses its detail page.

`QueryExecutor.cs:823-836` throws unless the actions class implements `ISparkOwnsRowSecurity` with a
non-empty rationale, and row filtering *and* redaction are skipped entirely for composed rows
(`:975-1004`) — the actions class is the only line of defence. The framework also nulls the row
link, so navigation needs a bespoke renderer.

### C5 — A composed query that owns its own paging cannot have row actions.

`ExecuteCustomAction.cs:341-384` re-runs the query narrowed to the posted ids rather than trusting
them, but only when `IsReExecutable(query)` (`:424-425`) — false for a streaming query **or one
returning `SparkQueryPage<T>`**. Those fall back to a document load, which finds nothing for a
`clrType`-less type. Any composed query that needs row actions must return a bare
`IQueryable`/`IEnumerable` and let the framework page in memory.

### C6 — There is no timeout on the query path.

`Endpoints/Queries/Execute.cs:32-146` catches only `SparkAccessDeniedException`. A GitHub 5xx or
hang inside a `Custom.*` method 500s the whole grid with no partial render. Resilience must live
inside the service, as `GitHubAccessService` and `GitHubStateReconciler` already do.

### C7 — Visibility is not administration.

`IGitHubAccessService.GetVisibilityAsync` returns the logins from `GET /user/installations` plus the
caller's own username, cached 5 minutes per user id with a single forced-refresh retry on 401.
`CanManageOwnerAsync` is literally `Owners.Contains(login)` — "the App is installed here and you can
see it". No `read:org`/`admin:org` scope is requested anywhere, and **no `project`/`read:project`
scope is requested either**. GitHub's Projects-V2 mutation is the real gate and will refuse a
non-admin, so failure is safe but late and must surface via `Notify`.

### C8 — Model sync is a build step with a startup hash gate.

`--spark-synchronize-model` rewrites `App_Data/Model/*.json` + `App_Data/modelHashes.json`;
`--spark-verify-model` exits 3 on drift and CI runs it for every app. `SparkMiddleware` refuses to
start when `modelHashes.json` disagrees with disk. A **hand-authored** model file (no `clrType`) is
not regenerated — synchronize only re-stamps its hash, so that file is the source of truth for its
own shape.

### C9 — Two hand-maintained closures fail late.

`apps/CodeCoverage/CodeCoverage/Dockerfile:21-35` has a selective csproj COPY list, and
`.github/workflows/code-coverage-deploy.yml:26-46` has a `paths:` trigger closure of 13 libs. A new
project reference must be added to **both** or the image build breaks / the deploy silently doesn't
fire.

### C10 — Feedback loops: this server is already a GitHub actor.

CodeCoverage **creates check runs** (`Feedback/PublishFeedbackRecipient.cs`) and **posts PR
comments** (`Feedback/PullRequestCommentGateway.cs`). Subscribing to `check_run` and `issue_comment`
therefore means the app receives webhook deliveries caused by **its own writes**. Unfiltered, a
`check_run` mapping moves a card every time coverage publishes a check, and an `issue_comment`
mapping fires on the app's own sticky comment. See FR6 — this is a correctness requirement, not
polish.

### C11 — Nothing tells us when a *column* changes. Not even on an organization.

**Measured live, 2026-09-07 — see §3b for the full run.** The original wording of this constraint
was partly wrong and is corrected here: the app *does* subscribe to `projects_v2` and an
organization installation *does* receive it. What no installation receives is an event for a
**column** change.

- `projects_v2` with action `created` **is delivered** to an organization installation.
- It is **not delivered** to a user-account installation.
- **Renaming a Status option — a "column" — produces no event at all, for either owner type.**
  Not `projects_v2`, not `projects_v2_status_update`. GitHub's `projects_v2` actions cover the board
  (created, edited, closed, …), not its field options.

So there is **no event path** by which we learn that a column was renamed, reordered or deleted, or
that a board was closed. The consequences are asymmetric and worth stating separately:

- **Automation triggers are unaffected.** Every event a rule fires on — `issues`, `pull_request`,
  `pull_request_review`, `issue_comment`, `check_run` — is a *repository* event, and those arrive
  normally for user-account installations. The feature works on a user account.
- **The cached column set can drift silently.** A rule stores an option **id**; when the option is
  deleted the rule stays syntactically valid and simply never matches, so automation stops with no
  error and a configuration screen that still looks correct.

This is the strongest argument for D1 over an event-driven mirror, and it makes two things
requirements rather than niceties:

1. **The nightly reconciliation must refresh columns**, not only board identity — the manual
   `SyncColumns` button cannot be the only correction path, because nobody presses a button for a
   problem they cannot see. Implemented for boards with `AutomationEnabled` only, so the sweep
   scales with boards *used* rather than boards *owned*.
2. **A rule pointing at a vanished column must be reported, never silently dropped.** Deleting it
   would destroy the user's configuration and hide the reason their automation stopped; the
   reconciler logs it at Warning and the recipient skips it loudly at dispatch time.

A column refresh that fails must leave the previously cached columns in place. Emptying them turns
a transient GitHub failure into "every rule on this board targets a column that does not exist".

---

## 3b. Measured webhook behaviour (live run, 2026-09-07)

Everything below was **observed**, not inferred, against the `CoverageDevelopment` app (id
`4567511`) with the app running locally behind its smee tunnel. Two throwaway private repositories
and two boards were used: `MintPlayer/spark-webhook-probe` (organization installation
`153539364`) and `PieterjanDeClippel/spark-webhook-probe-user` (user installation `159465567`).

### F1 — Organization versus user makes **no difference** to repository events

All seven `issues` actions (`opened`, `closed`, `reopened`, `labeled`, `unlabeled`, `assigned`,
`unassigned`) plus `issue_comment.created` were delivered **identically** to both installations.

**But the first run delivered zero of them to the user installation** — while `repository`, `push`
and `check_suite` had arrived for that same repository minutes earlier. The cause was a **pending
permission request** on that installation, visible at `/settings/installations` as *"Permission
updates requested."*; accepting it made all eight arrive on a byte-identical re-run, and the
acceptance itself arrived as `installation.new_permissions_accepted`.

> **Diagnostic lesson, and the reason this is written down.** An ungranted permission and "GitHub
> does not send this event" are **indistinguishable from the application side**: no error, no
> delivery, nothing in the log. A missing-event report must therefore start at
> `/settings/installations`, not in the code. This very session first mis-diagnosed it as an
> org-versus-user asymmetry.

### F2 — Projects V2: the board is announced, the columns are not

| Action | Organization install | User install |
|---|---|---|
| Board created | `projects_v2` / `created` **received** | **nothing** |
| Status option renamed (a "column") | **nothing** | **nothing** |

So the reconciliation path is not merely the *safest* correction mechanism for column drift, it is
the **only** one — for organizations too. See C11.

### F3 — `pull_request.closed` carries the merge distinction only in its payload

Proven directly, and this is the finding the resolver depends on:

```
event=pull_request action=closed merged=False   <- closed without merging
event=pull_request action=closed merged=True    <- squash merged
```

Same event, same action. A resolver keyed on `(event, action)` maps a **merge** onto the
`PullRequestClosed` rule and never fires `PullRequestMerged` at all — a rule that looks configured
and silently does nothing. `pull_request_review.submitted` collides the same way, separated only by
`review.state`.

Received: `opened`, `ready_for_review`, `converted_to_draft`, `closed`, `reopened`.

### F4 — Two of the eighteen event keys could not be verified, and cannot be from one account

`pull_request_review.submitted` **is** delivered — confirmed with a *comment* review. But GitHub
refuses `approve` and `request changes` on your own pull request (`Review Can not approve your own
pull request`), so `PullRequestReviewApproved` and `PullRequestReviewChangesRequested` are
**unverified**: they need a second GitHub account as reviewer. `PullRequestReviewRequested` is
likewise unverified for the same reason.

Also worth noting: there is no lookup key for a review with state `commented`, so such a review
matches no rule. That is intended, not an omission.

### F5 — `check_run` needs a workflow, and only reaches same-repository pull requests

`check_run.created` and `check_run.completed` arrived in volume once a trivial workflow existed in
the repository — a repository with no Actions produces none, so a `CheckRunCompleted` rule looks
broken on a repository that simply has no CI. The payload's `check_run.pull_requests` array is the
only route from a check to a card, and GitHub populates it **only for pull requests in the same
repository**, so the rule legitimately does nothing for a fork's contribution.

### F6 — End-to-end automation works, verified on the board itself

With a `GitHubProject` document seeded (automation on, three rules) the full path — router → its own
queue → recipient → GraphQL → board — was exercised:

| Rule | Result |
|---|---|
| `IssuesOpened` → *In Progress* | Card **added** and placed in *In Progress*, confirmed via `gh project item-list`. A deliberately non-default column, so it cannot be confused with GitHub's own placement. |
| `PullRequestMerged` → *Done* | Card in *Done* — which also proves F3's disambiguation end to end, since the trigger was `closed` + `merged=true`. |
| `IssuesClosed` → a **deleted** option id | Failed with *"Target column … no longer exists on this board"*, recorded as `LastError` **on the rule**, and the message reached `DeadLettered` at **`AttemptCount = 1`**. |

That last row is the `NonRetryableException` contract working as intended: one attempt, not five
retries with an hour of backoff, for a failure that no retry could fix.

**M3's discovery was also confirmed incidentally**: the reconciler had independently created a
document for a pre-existing board (`PVT_kwDOAug2bM4AthJv`) with `AutomationEnabled = false`, which
is the intended default — a discovered board is inert until someone enables it.

### F7 — Incidental observations worth keeping

- `installation_repositories.added` fires when a repository is created under either account, so
  both installations are in **selected-repositories** mode and new repositories are auto-added.
- Squash-merging a pull request whose body says `Closes #1` delivers `issues.closed` for the linked
  issue, so linked-issue movement has an observable trigger independent of
  `closingIssuesReferences`.
- **Unexplained:** JWT-signed calls to `GET /app` and `GET /app/installations` began returning
  `401 "A JSON web token could not be decoded"` partway through the session, with the *same* minting
  code that had succeeded earlier against `/app/installations`. Worked around by reading the
  installation state from the browser. Not diagnosed; flagged because it will look like a
  credential problem if it recurs.

### Still unverified after this run

- `PullRequestReviewApproved`, `PullRequestReviewChangesRequested`, `PullRequestReviewRequested`
  (need a second account).
- The `DeleteBranchOnPrClose` path (D3).
- Whether `CreateGraphQLConnectionAsync`'s token-refreshing handler behaves across a token
  expiry — the run was far shorter than an installation token's lifetime.
- The generic-UI editing surface (M7/S2): the board document was seeded directly into RavenDB
  rather than created through the form.

---

## 4. Design decision

### D1 — Persisted, reconciled board mirror (recommended)

Boards become real RavenDB documents, discovered by extending the existing reconciler, with an
`AutomationEnabled` boolean edited through the generic form. This is the shape that matches
CodeCoverage, and one fact decides it: **an enabled board must be a document either way** — both
webhook recipients already do `session.Query<GitHubProject>()`. The only question is whether the
document is a side effect of a button or always present with a flag.

What it buys:

- **Real row security.** A `GetRowFilterAsync` over `OwnerLogin`, with `WITH CHECK` and redaction —
  instead of C4's "the service is the only line of defence".
- **A free detail page.** `/po/{type}/{id}` is already mounted, so "Configure" is just the row's own
  link and C3/C4's renderer problem evaporates.
- **Non-destructive disable.** Today "Enabled → click" runs `sparkService.delete(...)`
  (`github-projects.component.ts:112`), destroying the user's `EventMappings` with the document. A
  flag makes disable reversible.
- **Zero bespoke Angular and zero new renderers.** Boolean → `bs-checkbox`, `AsDetail` + `isArray` +
  `editMode: "inline"` → an editable sub-table, `lookupReferenceType` → picker, all inside ng-spark
  (`po-form/src/spark-po-form.component.html:53-262`). Lookup discovery is an assembly scan, no
  registration.
- **No GitHub call on the render path**, so C6 does not apply.

Reconciliation machinery already exists and is battle-tested: `GitHubStateReconciler` (one account
at a time, mutates without saving, bounded at 1024 because the session budget is 30, **fails closed
on anything but a definitive 404/401**), the nightly `ReconcileGitHubStateCronJob` (`20 3 * * *`),
and `ResyncAction` as the manual counterpart. Soft deletion via `Disconnect(...)` rather than delete
is already the house pattern.

**The two objections, recorded honestly:**

1. *It persists a document per visible board, whether or not anyone enabled it.* An org with 200
   boards gets 200 documents nobody asked for, plus a nightly GraphQL call per account forever. The
   counter is that `Repository` already works exactly this way at much larger cardinality, and that
   the flag is what makes disable non-destructive. It remains a real, permanent cost.
2. *A board created five minutes ago is invisible until 03:20 UTC or a manual resync* — at exactly
   the moment the user is most likely looking, having just created the board. Mitigated by FR3
   (`ResyncAction` extended to boards, plus a board-scoped sync action), but it is a mitigation: the
   user must know to press a button. Putting reconciliation on the render path would buy freshness
   back at the price of a GitHub call with no timeout and a 500 as its failure mode (C6) — not a
   trade worth making silently.

### D2 — Rejected: live-API composed query

A `clrType`-less `GitHubProjectRow` type with `Custom.MyBoards` returning rows built from a live
GraphQL call, `MyAccountRow` being the precedent. Rejected because it pays C4 (no filtering, no
redaction, no detail page), C6 (no timeout; GitHub 5xx = 500 and an empty page), and re-runs the
GitHub call on **every action click** as well as every render (C5's re-materialization), while
*still* needing a document for the enabled board — and its disable is destructive.

### D3 — Head-branch deletion becomes opt-in per board

`DeleteBranchOnPullRequestClose` deletes the head branch on **every** PR close, org-wide and
unconditionally. `Contents: write` is now granted, so this would work — which is precisely why it
should be gated. On a multi-tenant coverage server this mutates other people's repositories. It
carries over behind a `DeleteBranchOnPrClose` flag on the board document, defaulting to **off**,
alongside `AutomationEnabled`.

---

## 5. Functional requirements

**FR1 — Board entity.** `GitHubProject` in `CodeCoverage.Library/Entities/`, `[GenerateIndex]` (never
with a type argument), natural document id from the GitHub node id so upserts are idempotent, with
`OwnerLogin`, `InstallationId`, `NodeId`, `Number`, `Name`, `StatusFieldId`, `Columns`
(`ProjectColumn[]`), `EventMappings` (`EventColumnMapping[]`), `AutomationEnabled`,
`DeleteBranchOnPrClose`, and `Connection` + `DisconnectedReason` mirroring `Repository`. Every `///`
summary becomes the model's `description`, and the description generator runs in
`CodeCoverage.Library` because that is where the summaries live. `[IgnoreForIndex]` is opt-out — any
field that must not become a queryable grid column needs it.

**FR2 — Registration.** An `IRavenQueryable<GitHubProject>` property on `CoverageSparkContext`, a
`GitHubProjectActions : DefaultPersistentObjectActions<GitHubProject>` whose `GetRowFilterAsync`
returns `p => p.OwnerLogin.In(owners)` from `ISparkVisibility` (per-request memoized). Use `In()`
not `Contains` — a `string[]` receiver binds to the untranslatable `MemoryExtensions.Contains`, and
`List<string>.Contains` throws inside an `OrElse` on .NET 10. Use `!= Disconnected`, never
`== Connected` — an absent JSON field does not satisfy an equality in RavenDB. A board is never
world-readable, so there is no public tier in the filter.

**FR3 — Discovery by reconciliation.** Extend `IGitHubStateReconciler.ReconcileAsync` to list
Projects V2 for the account's `InstallationId` through the **typed** `Octokit.GraphQL` builder via
the existing `IGitHubInstallationService.CreateGraphQLConnectionAsync` — never a raw interpolated
string. Upsert reachable boards, `Disconnect` vanished ones, never delete, never treat a transient
failure as absence. Bound it the way repositories are bounded and respect the 30-request session
budget. It then rides the nightly cron and `ResyncAction` for free.

**FR4 — Configuration UI, model-declared only.** One `programUnits.json` unit of
`type: "persistentObject"` (or `query`) in a new `GitHub` group; rights in `security.json` starting
at the next free block `c0e5a9e1-0000-4000-8000-000000000071`. `AutomationEnabled` and
`DeleteBranchOnPrClose` render as checkboxes; `EventMappings` as `AsDetail` + `isArray` +
`editMode: "inline"` with `WebhookEvent` on `lookupReferenceType`. Note `editMode: "inline"` appears
nowhere in CodeCoverage today — set it deliberately if the old inline feel is wanted.
`TargetColumnOptionId` is a plain string today and renders as a free-text box; promoting it to a
lookup over the board's own cached `Columns` is the one model change that makes it a real dropdown.
`anonymous` is not "Everyone" — a right both roles need is two grants; these grants go to
**authenticated only**.

**FR5 — Webhook handling on the existing queue.** A new
`Recipients/ProjectAutomationRecipient.cs : IRecipient<GitHubWebhookMessage>` switching on
`message.EventType`, deserializing `EventJson` itself, handling `issues`, `pull_request`,
`pull_request_review`, `issue_comment` and `check_run`. It must **not** be an
`IRecipient<GitHubWebhookMessage<T>>` (C1), and must not extend `GitHubEventsRecipient`'s switch
(failure isolation). Board lookup filters on `AutomationEnabled && Connection != Disconnected`.

**FR6 — Self-inflicted events are ignored.** Per C10, the `check_run` and `issue_comment` handlers
must drop deliveries caused by this app's own writes before evaluating any mapping — by app id on
the check run, and by the existing bot-author test on comments. Without this, publishing coverage
feedback moves cards, which then publishes more feedback.

**FR7 — Bounded work per delivery.** Replace the unpaged `session.Query<GitHubProject>().ToListAsync()`
that both old recipients do per delivery. Query by owner/repository through the generated index,
filter on the enabled flag, and bail before any GitHub call when no board maps the event. A slow
handler here delays coverage feedback (C1).

**FR8 — Column sync.** Carry `SyncColumns` over as the single custom action, right
`SyncColumns/GitHubProject` granted to authenticated. Actions attach **by right, not by
declaration** — `customActions.json` is evaluated against every type, so the grant is the only thing
scoping it. `_comment` is not a comment in that file: it is a flat map and an unknown key
deserializes as an action and fails.

**FR9 — Event vocabulary is honest.** `WebhookEventType` moves over with all 19 keys **and all of
them work** (owner decision). Anything that cannot be made to work is removed from the lookup rather
than shipped inert.

**FR10 — Deletion.** `apps/WebhooksDemo/` is removed entirely and every repo-wide reference updated
(§7). 69 tracked files match `WebhooksDemo` today.

---

## 6. Defects fixed in flight

Not scope creep — these are in the code being moved, and porting them verbatim would ship them to
production.

| # | Defect | Location |
|---|---|---|
| B1 | GraphQL query built by string interpolation of `ownerLogin` | `GitHubProjectsController.cs:58` — dies with the controller (FR3) |
| B2 | Unpaged "load every board" per webhook delivery | `HandleIssuesEvent.cs:40`, `HandlePullRequestEvent.cs:39` → FR7 |
| B3 | 6 of 19 event mappings silently inert | `WebhookEventType.cs:73-81` → FR9 |
| B4 | Disable destroys the user's rules along with the document | `github-projects.component.ts:112` → D1 |
| B5 | `catch (Exception ex) { throw; }` noise; dead `projectIds` query | `LogIssues.cs`, `GitHubProjectService.cs:54-57` — dropped |
| B6 | `GitHub:WebhookSecret` is the one key that is *not* environment-prefixed | verify both Apps share it (C2) |
| B7 | `CoverageQueues.cs:8-9,18` says AGPL and "five queues"; it is a registered Community licence and there were seven | doc-only |
| B8 | Rate limiter covers `/spark`, `/connect`, `/api/browse` — not `/api/github/*` | `Program.cs:149-150`; moot once the controllers are dropped, but the webhook endpoint stays uncovered |

---

## 7. Repo-wide deletion inventory

**Delete:** `apps/WebhooksDemo/**`, `.github/workflows/webhooks-demo-deploy.yml`.

**Edit:**

| File | Change |
|---|---|
| `MintPlayer.Spark.slnx:25-27` | drop the folder and both projects |
| `package.json:17` | drop the `ClientApp` workspace entry |
| `.github/workflows/pull-request.yml:91,115,126,148` | drop from the build list and both verify loops; fix the "all five apps" wording |
| `.github/dependabot.yml:26-30` | the docker ecosystem block becomes dead (CodeCoverage's Dockerfile is not registered) |
| `README.md:140`, `CLAUDE.md:20` | drop from the app inventory |
| `libs/webhooks/.../README.md:41,557-559,658,670-671` | remove the "Project board automation" section and demo paths; repoint to CodeCoverage |
| `libs/authorization/.../README.md:387` | external-provider example → CodeCoverage's `Program.cs` |
| `docs/guide-docker-deployment.md` | built entirely on WebhooksDemo's compose file → repoint to `apps/CodeCoverage` |
| `docs/guide-nx-remote-cache.md:51,53,63` | the cache-isolation section names webhooks-demo |
| `docs/guide-row-security.md:38` | worked example is `GitHubProjectActions.GetRowFilterAsync` → now a real filter in CodeCoverage |
| `docs/guide-authentication-schemes.md:440,473` | repoint |
| `libs/node_packages/ng-spark-auth/sign-in/src/spark-sign-in.projection.spec.ts:104` | stale comment |

**Leave:** `libs/webhooks/**` — CodeCoverage and `tests/MintPlayer.Spark.Tests` both depend on it.
Historical PRD/plan/release-note mentions are prose about past work and stay as-is.

**Unused after merge:** the `SLIPLANE_WEBHOOKS_DEMO_DEPLOY_HOOK` secret and the old App's smee
channel.

---

## 8. Manual GitHub steps for the owner

Already done: permissions and events copied to both Apps; `Contents: write` granted and accepted on
the MintPlayer org installation.

Still required:

1. **Confirm both Apps carry the identical webhook secret** (C2) — otherwise CoverageDevelopment's
   forwarded deliveries fail signature validation at the production endpoint, silently.
2. **Confirm the `Issues` event is subscribed** on both Apps, plus `pull_request_review`,
   `issue_comment` and `check_run` for FR9. Event subscriptions need no installation approval.
3. **Other installations** beyond MintPlayer must still accept the raised permissions; until they do,
   Projects/Issues/Contents-write calls 403 for them. Recovery is automatic — the existing switch
   already treats `new_permissions_accepted` as a reconnect trigger.
4. **The OAuth `project`/`read:project` scope** is not requested today (C7). Decide whether
   board discovery relies solely on installation tokens (recommended — the reconciler already runs
   as the App) or whether user-token board listing is wanted, which needs the scope added.
5. **Retire the old WebhooksDemo App** or repoint its webhook URL, so it stops delivering to a
   defunct host; delete its smee channel.
6. No webhook-URL change and no callback-URL change. Keep *Request user authorization (OAuth) during
   installation* unchecked.

---

## 9. Open decisions

| # | Decision | Recommendation |
|---|---|---|
| **OD1** | Board freshness: nightly + manual resync only, or reconcile-on-view? | Nightly + resync (D1 objection 2). Revisit only if it actually bites. |
| **OD2** | Does the boards ProgramUnit list boards directly, or hang off the existing Account page as a sub-query beside `account-repositories`? | Both: a top-level unit for the flat list, and the sub-query on Account, which is where users already are. |
| **OD3** | Promote `TargetColumnOptionId` to a lookup over the board's cached `Columns`? | **CLOSED — yes, and implemented**, though not as a *lookup*. ⚠️ An earlier revision of this row said "no, not expressible"; that was **wrong** and is corrected here. Neither lookup shape can scope to a parent — `TransientLookupReference<TKey>` is static, `DynamicLookupReference<TValue>` is one global set per lookup name — but `[Reference(typeof(X), "query")]` is a third mechanism whose named `Custom.*` query receives `CustomQueryArgs.Parent`, and it works from an embedded `AsDetail` row because the client sends the **root** document as the parent. It also needs **no new collection**: `ProjectColumn` stays embedded and `ProjectColumnActions.Project_Columns` returns the board's own `Columns` as a plain `IEnumerable`, which the executor accepts. Because the target declares a `clrType` it is entity-backed rather than composed, so no `ISparkOwnsRowSecurity` and no transferred row-security duty. The recipient's missing-column check stays regardless: a dropdown makes a wrong value unlikely, not impossible, since a column can be deleted on GitHub after a rule is saved. Mechanism now documented in `docs/guide-reference-attributes.md`. |
| **OD4** | `editMode: "inline"` for the rules sub-table? | **CLOSED — yes**, applied. Also retires spike S2: `HR/Person.json` already ships `editMode: "inline"` on an `AsDetail` array, so the mechanism is proven in-tree and needed no spike. How it *looks* on this particular form is M13 browser work, not a mechanism question. |

---

## 10. Risks

| # | Risk | Mitigation |
|---|---|---|
| R1 | A new queue name slips in and kills a queue | C1; the 4 build-time guards; review every `[MessageQueue]` in the diff |
| R2 | Feedback loop from `check_run`/`issue_comment` on the app's own writes | FR6, and a test that asserts a self-authored delivery is dropped |
| R3 | FIFO coupling: a slow board mutation delays coverage feedback | FR7; bail before any GitHub call when no board maps the event |
| R4 | Model hash gate blocks startup after a hand-authored file edit | C8; re-sync and commit `modelHashes.json` in the same commit |
| R5 | Dockerfile / deploy `paths:` closures not updated | C9; both lists in the same commit as the project reference |
| R6 | `Octokit.GraphQL` is a **beta** pin arriving transitively via the webhooks lib | declare it explicitly on `CodeCoverage.csproj` rather than inheriting it implicitly |
| R7 | Board mutations refused late because visibility ≠ admin | C7; surface GitHub's refusal via `Notify`, never fail silently |
| R8 | Deleting the app breaks four docs guides that use it as their example | §7; the guides are part of the same PR |

---

## 11. Acceptance criteria

1. `apps/WebhooksDemo/` does not exist; `git ls-files | grep -i webhooksdemo` returns nothing but
   historical prose in `docs/`.
2. Solution builds; `--spark-verify-model` and `--spark-verify-security` pass for all **four**
   remaining apps; `pull-request.yml` no longer names WebhooksDemo.
3. `CoverageQueuesTests` and `DeleteDataActionTests` stay green — no new queue name.
4. A board can be discovered, enabled, configured with rules, and disabled without losing its rules,
   entirely through the generic UI, with **zero new Angular components** (one optional badge renderer
   permitted if a bare checkbox reads as too editable).
5. All 19 event mappings demonstrably fire, each covered by a recipient test.
6. A `check_run` or `issue_comment` delivery authored by this app itself moves no card (FR6 test).
7. Row security: a caller who does not manage an owner sees none of its boards and cannot mutate
   them — asserted through `CoverageWebAppFactory`, which boots the real `Program` so
   `[SparkAuthorize]` filters actually execute.
8. No secret, no interpolated GraphQL, and no `[AllowAnonymous]` controller lands in the diff.

---

## 12. Out of scope / genuinely not being done

- Migrating WebhooksDemo's RavenDB data. Boards are rediscovered.
- A RavenDB licence change to buy queue isolation.
- Adding `read:org`/`admin:org` scopes to answer "can this user administer this org" properly (C7).
  Installation membership stays the proxy, as it is for every other authorization decision here.
- Rewriting `GateSettings`' `ProjectMode`/`ProjectBasis` string fields as lookups, which is the same
  latent gap that forced `RepoGatePanelComponent` to be 140 hand-written lines.

# PRD — Keeping the advertised accounts and repositories in step with GitHub

**Status:** written 2026-09-05, not implemented
**App:** `apps/CodeCoverage` (production, coverage.mintplayer.com) + `libs/webhooks/MintPlayer.Spark.Webhooks.GitHub`
**Plan:** [coverage_account_sync_plan.md](coverage_account_sync_plan.md)

## Problem

`MintPlayer/CodeCoverage` was transferred to the `MintPlayer-Archive` organization and archived.
The coverage app still advertises it on the account page, under an owner that no longer owns it.

The obvious diagnosis — "we don't subscribe to the right webhooks" — is wrong. The GitHub App
subscribes to repository events, the deliveries arrive, and `GitHubEventsRecipient` has a `case
"repository"`. The state is stale for three compounding reasons, none of which is the subscription:
the one event that reports a repository leaving an installation is discarded inside the Spark
webhooks library before any app can see it; the events that *do* arrive are all funnelled through a
single upsert that cannot express "this repository left"; and nothing in the system ever asks GitHub
what is actually true, so a single missed delivery is permanent.

That last property is the real defect. Everything else is a bug that can be fixed once; a
webhook-only design has no way back from a dropped delivery, and this one has been running with a
dropped delivery since the first App installation.

## Prior art

- `docs/coverage_branch_pr_badges_PRD.md` — the badge/PR-comment work, and the source of the
  `Repository.LatestCoverage` denormalization this design has to keep honest.
- `docs/prd/PRD-GitHubAppClientCache.md` and `docs/code-coverage/reauth-on-401.md` — the installation
  token/client cache and the 401-retry decorator that the reconciler will call through.
- Issue #232 — the smee dev tunnel corrupts `installation` payload bytes, so these events could not
  be exercised locally. `SmeeWebhookTunnelService` in the app is the lexical-minifier workaround;
  the library's `SmeeBackgroundService` still has the bug. This matters for how the spikes are run.

## Investigation findings

### F1 — The event that reports a repository leaving an installation is discarded by the library, and the app's handler for it is dead code

`SparkWebhookEventProcessor` overrides exactly ten `Process*WebhookAsync` methods
(`libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/Services/SparkWebhookEventProcessor.cs:87-115`):
`push`, `issues`, `issue_comment`, `pull_request`, `pull_request_review`,
`pull_request_review_comment`, `check_run`, `check_suite`, `installation`, `repository`.

`ProcessInstallationRepositoriesWebhookAsync` is not among them. Octokit's base implementations are
no-ops when unoverridden and there is no fallback path, so `installation_repositories` never becomes
a message. `GitHubEventsRecipient.cs:35` — `case "installation_repositories"` — and the whole of
`OnInstallationRepositories` (`:84-107`), including its `session.Delete` of removed repositories,
**have never executed in production**.

This is precisely the event GitHub sends when a repository leaves an installation's scope, which is
what a transfer to a different organization is.

### F2 — A dropped event is invisible: the library has no catch-all and no diagnostic

Adding support for an event means adding a method override. Nothing logs an unhandled event, nothing
enumerates what is subscribed versus what is handled, and no test asserts that a given event type
produces a message (`SparkWebhookEventProcessorTests` covers signature validation and dev-tunnel
routing only). Also unhandled today: `installation_target` (organization rename), `organization`,
`member`, `membership`, `github_app_authorization`, `public`, `create`, `delete`, `fork`.

### F3 — Every delivery broadcasts two messages, and one of them is never consumed

`HandleWebhookAsync` broadcasts both the typed `GitHubWebhookMessage<TEvent>` and the catch-all
`GitHubWebhookMessage` unconditionally (`SparkWebhookEventProcessor.cs:128-146`).
`MessageSubscriptionManager` starts a worker only for queues that have a registered `IRecipient<T>`
(`libs/messaging/MintPlayer.Spark.Messaging/Services/MessageSubscriptionManager.cs:20-30`), and
CodeCoverage registers no typed recipient. So every delivery writes a `SparkMessage` document to a
queue with no worker, forever. The trade-off is acknowledged at `GitHubEventsRecipient.cs:16-20`,
but it was a choice between two leaks rather than a design.

### F4 — `repository` is handled, but every action shares one upsert path

`OnRepository` (`GitHubEventsRecipient.cs:109-133`) branches on exactly one action, `deleted`, which
hard-deletes the `Repository` document. `transferred`, `renamed`, `privatized`, `publicized`,
`archived`, `unarchived`, `edited` and `created` all fall through to the same generic upsert:
`GetOrCreateAccount(ghRepo.Owner.Id)` then `UpsertRepository(...)` then `DefaultBranch`/`Archived`.

Two consequences. A transfer silently re-parents the repository to a freshly created `Account` for
the new owner and keeps advertising it there — the app is not wrong about the data, it is wrong
about *whether it should still be showing it at all*. And the `deleted` branch removes the
`Repository` while leaving every `Commit`, `Build`, `FileCoverage`, `BuildTreeSummary`,
`CommitAssembly`, `PullRequestFeedback` and repo-scoped `ApiToken` behind as unreachable orphans.

### F5 — An existing account never learns that its login changed

`OnRepository` refreshes the owner's `Login`, `AvatarUrl` and `Type` only when the account has no
login yet: `if (string.IsNullOrEmpty(account.Login))` (`GitHubEventsRecipient.cs:123`). For an
account we already know, a GitHub organization or user rename is ignored. `OnInstallation` (`:59-62`)
and `OnInstallationRepositories` (`:86-89`) do refresh it unconditionally — so whether a rename is
picked up depends on which event happens to arrive next.

### F6 — The document ids are already transfer-proof; only the name-shaped fields are not

Every persisted id is keyed on GitHub's numeric id, which survives renames and transfers:
`Accounts/{GitHubId}` (`Account.cs:34`), `Repositories/{GitHubId}` (`Repository.cs:78`),
`Commits/{repoGitHubId}/{sha}` (`Commit.cs:126`), and `Build`/`FileCoverage`/`BuildTreeSummary`/
`CommitAssembly` ids all derive from those. `Commit.Repository`, `Build.Commit`,
`PullRequestFeedback.Repository`, `CommitAssembly.Repository` are id references.

The only name-shaped state is `Repository.FullName`, `Repository.OwnerLogin`, `Account.Login` and
`ApiToken.AccountLogin` (`ApiToken.cs:21`). A transfer leaves the last one stale, and it is turned
into the `AccountClaim` used for upload authorization
(`ApiTokens/ApiTokenAuthenticationHandler.cs:63-64`, compared at `UploadsController.cs:522`).

**This is the good news of the investigation.** No data migration is needed and nothing has to be
re-keyed; the fix is about lifecycle and reconciliation, not about the schema.

### F7 — Every human-facing URL resolves by full-name string equality, with no alias

Five call sites do `.Where(r => r.FullName == $"{owner}/{name}")`: `BadgeController.cs:45`,
`BrowseController.cs:513`, `RepoSettingsController.cs:75`, `TokensController.cs:56`,
`UploadsController.cs:511`. The badge is `/badge/{owner}/{name}.svg` and the shareable page is
`/r/{owner}/{name}`, both baked into README markdown by `repo-badge-panel.component.ts:98-107` and
into GitHub PR comments by `PullRequestCommentRenderer.cs:106,113`.

A rename or transfer therefore breaks every published badge and every link already posted into a PR
comment, with no record anywhere of what the repository used to be called.

### F8 — There is no reconciler of any kind

The GitHub API is read for exactly one thing: the signed-in user's *owner* list, via
`GET /user/installations`, cached five minutes in `IMemoryCache`
(`Services/GitHubAccessService.cs:25,86,119`). Repositories are never read from GitHub. The three
registered cron jobs (`Program.cs:154`) — `PublishFeedbackCronJob`, `BackfillCommitDeltasCronJob`,
`FinalizeBuildsCronJob` — are all downstream of ingestion and none of them touches `Accounts` or
`Repositories`. `GitHubAccessService.BackfillInstallationIdsAsync` (`:183-239`) writes `Account`
documents, but only for the accounts the signed-in caller can already see, and only their
`InstallationId`.

### F9 — There are two different definitions of "the repositories of this account"

`RepositoryActions.Account_Repositories` (`Actions/RepositoryActions.cs:53-54`) scopes by the
`Account` **reference**; `MyAccountsService.cs:44-47` and `BrowseController.cs:63,177` scope by the
`OwnerLogin` **string**. They agree today only because `ApplyRepositoryFields` writes both from the
same payload (`GitHubEventsRecipient.cs:265-272`). Anything that re-parents a repository must write
both or the two surfaces will disagree.

### F10 — A second, webhook-free write path can create repositories

`UploadsController.ResolveOidcRepository` (`:536-577`) auto-provisions a public repository — and its
owning `Account` — from the GitHub OIDC token of a workflow run, with no App installation involved.
It never sets `DefaultBranch` (noted at `BrowseController.cs:132-136`). Any lifecycle state this
design introduces has to be correct on this path too, not only on the webhook path.

### F11 — Verified against GitHub: the id survived, the name redirects, the installation lost access

Measured 2026-09-05 with `gh api`:

| probe | result |
| --- | --- |
| `repos/MintPlayer-Archive/CodeCoverage` | id `1305831351`, `archived: true`, `private: false` |
| `repos/MintPlayer/CodeCoverage` | resolves to the same id — GitHub 301s the old full name itself |
| `user/orgs` | `MintPlayer` and `MintPlayer-Archive` are **separate organizations** |

The App is installed on `MintPlayer`, not on `MintPlayer-Archive`, so the installation genuinely lost
access to the repository at the moment of transfer. GitHub's own redirect surviving is what makes
D6's fallback strategy possible.

### F12 — `RepositoryVisibility` is a deliberate single seam, and it is the right place for this

`Services/RepositoryVisibility.cs` exists specifically because the "who may see this repository"
rule had been written three times and drifted. Its doc comment names the next change — "an org
allowlist, private-but-shared, an unlisted state" — and asks for it to land there. A disconnected
state is exactly that, and it must land in `Filter` (the RavenDB-translatable predicate used by
`RepositoryActions.GetRowFilterAsync`, `SparkVisibility` and the Browse queries) and `IsVisible`
together.

### F13 — There is no write right on `Repository` in `security.json`

`App_Data/security.json` grants `QueryRead` on `Account`/`Repository`/`Commit`/`Build` to both the
anonymous and authenticated groups, and `Manage/UploadToken`, `Manage/RepoSettings`,
`Upload/Coverage` to authenticated only. There is no `Edit`, `New` or `Delete` right on `Repository`
anywhere, and `RepositoryActions.cs:16-18,25-32` records that this is load-bearing. An owner-invoked
delete needs a new right, not a relaxation of an existing one.

### F14 — Nothing tests any of this

`CodeCoverage.Tests/Recipients/GitHubEventsRecipientTests.cs` has five tests, all about `ParentSha`
and PR base-sha semantics. There is no test for `OnInstallation`, `OnInstallationRepositories` or
`OnRepository`, and therefore none for any repository action. On the library side, no test asserts
that a given event type produces a message at all (F2).

## Options

### Where the lifecycle state lives

**A repository-level state field** — one `Repository.Connection` value covering connected /
disconnected, plus the reason. Chosen. It is the only place all four disconnection causes (transfer
away, App uninstalled, repository removed from the installation's selection, repository deleted on
GitHub) converge, and it is already in the generated index, so it can be filtered server-side.

*Rejected: inferring it from `Account.InstallationId`.* An account can hold an installation while a
particular repository is outside its selected set, so the account cannot answer the question. It
also cannot express "deleted on GitHub".

*Rejected: a separate `RepositoryConnection` document.* One extra load on every visibility check,
for a field that is three values wide.

### How a renamed or transferred repository's old URL resolves

**Stored aliases first, GitHub as the fallback.** Chosen — see D6. Stored aliases are free to read
and cover every rename we witnessed; the GitHub fallback covers renames that happened while we were
not looking, which is the entire class of bug this PRD exists to close.

*Rejected: aliases only.* Cannot repair a rename whose event we dropped — the same failure mode
again.

*Rejected: GitHub lookup only, storing nothing.* Elegant, and it self-resolves collisions, but it
puts a network call on the anonymous, rate-limited badge path for every unknown name, including for
names that will never resolve. Kept as the second step, not the first.

### How much of the Spark webhooks library to change

**Forward every event, and stop broadcasting typed envelopes nobody consumes.** Chosen — see D3.
The owner's guidance was that the packages are still in preview and the change can be as large as it
needs to be, so the choice is made on design grounds rather than on compatibility.

*Rejected: adding only `ProcessInstallationRepositoriesWebhookAsync`.* Fixes this bug and leaves the
mechanism that caused it — a drop that is invisible until someone notices missing data — in place
for `installation_target`, `organization` and every future event.

## Design

### D1 — `Repository` gains a connection lifecycle; nothing is deleted implicitly

```csharp
public enum RepositoryConnection { Connected, Disconnected }

public string? DisconnectedReason { get; set; }   // "TransferredAway" | "RemovedFromInstallation"
                                                  // | "AppUninstalled" | "DeletedOnGitHub"
public DateTime? DisconnectedAtUtc { get; set; }
```

`Connection` is a queryable field on `Repository` and therefore on `VRepository` /
`Repositories_Overview`. `DisconnectedReason` is informational and drives the wording in the UI.

The `deleted` branch of `OnRepository` (`GitHubEventsRecipient.cs:114-120`) stops deleting. Nothing
in the webhook or reconciler path ever removes a `Repository` document again; the only deletion is
the explicit, owner-invoked one in D8.

### D2 — Disconnected means "not advertised", not "not reachable"

Two rules, deliberately distinct, both in `RepositoryVisibility` (F12):

- **`ListingFilter(allowedOwners)`** — used by everything that *enumerates* repositories:
  `RepositoryActions.GetRowFilterAsync`, `SparkVisibility`, `BrowseController`'s
  `accounts/{login}/repos` and `sparklines`, and `MyAccountsService`. A disconnected repository
  appears only to a viewer who manages its owner.
- **`Filter(allowedOwners)`** — unchanged, used when *resolving one named repository*:
  `BadgeController`, `BrowseController.ResolveVisibleRepository`, `RepoSettingsController`,
  `TokensController`. Connection state is not consulted, so `/r/{owner}/{name}`, the report pages
  and the badge keep answering for a disconnected public repository.

The badge of a disconnected repository renders the last known `LatestCoverage`, unchanged — it is
frozen because nothing will ever update it again, not because the endpoint special-cases it.

Aggregate coverage and `RepoCount` on the account row (`MyAccountsService.cs:57-62`) are computed
from the listing rule, so a disconnected repository stops contributing to the owner's headline
number for viewers who cannot see it, and keeps contributing for the owner who can.

### D3 — The library forwards every event, and only to queues that have a consumer

Two changes in `SparkWebhookEventProcessor`:

1. Replace the ten hand-written overrides with a single hook that runs for every delivery. Octokit's
   `WebhookEventProcessor.ProcessWebhookAsync(headers, body, ct)` is already overridden for
   signature checking, and it holds the raw body and `headers.Event` before dispatch — the catch-all
   `GitHubWebhookMessage` can be broadcast from there, for every event type, without an override per
   event. The typed overrides remain only for the events that have a typed envelope worth offering.
2. Broadcast the typed `GitHubWebhookMessage<TEvent>` only when a recipient for that closed generic
   type is registered. `MessageTypeAllowList` (`libs/messaging/…/Services/MessageTypeAllowList.cs:27`)
   already builds the set of registered `IRecipient<T>` message types at startup; the processor asks
   it before broadcasting. This closes F3: no more undrained queue documents.

The consequence for CodeCoverage is that `case "installation_repositories"` starts running for the
first time, along with the new cases in D4 and D9.

### D4 — `OnRepository` branches per action, and disconnection is a state change

| action | effect |
| --- | --- |
| `deleted` | `Connection = Disconnected`, reason `DeletedOnGitHub`. No document is removed. |
| `transferred` | Re-parent (`Account`, `FullName`, `OwnerLogin`) and record the old full name in the alias list (D6). **Stays connected** — see the measurement below. |
| `renamed` | Rewrite `Name`/`FullName`, push the old full name onto the alias list. Connection unchanged. |
| `archived` / `unarchived` | Set `Archived`. Connection unchanged — an archived repo is still ours. |
| `privatized` / `publicized` | Set `IsPrivate`. |
| `created` / `edited` | Generic upsert, as today. |

**Measured 2026-09-05, and it inverted this decision.** The transfer was run for real — the archived
`MintPlayer/CodeCoverage` moved back into `MintPlayer` and out again — and the two directions are not
symmetric:

| direction | what the installation received |
| --- | --- |
| **gaining** (into an org where the App is installed) | `repository.transferred`, then `installation_repositories.added` one second later |
| **losing** (out of that org) | `installation_repositories.removed` — **and no repository event at all** |

So `repository.transferred` is a signal that we just *acquired* a repository, never that we lost one.
The original design had it disconnect, which would have marked a repository we can plainly see as
unreachable and then leaned on the `added` that follows to undo it — correctness resting on the
delivery order of two independently queued messages, on two queues, with no ordering guarantee.
Losing a repository is `installation_repositories.removed`'s job, and that event is the *only* thing
that can observe it. Which is the whole bug: it was the delivery the library discarded.

`installation_repositories.removed` sets `Connection = Disconnected`, reason
`RemovedFromInstallation`, instead of `session.Delete` (`GitHubEventsRecipient.cs:95-106`).
`installation.deleted`/`suspend` nulls `Account.InstallationId` as today *and* disconnects that
account's repositories, reason `AppUninstalled`.

Every reconnecting path (`installation.created`, `installation_repositories.added`, and D7) sets
`Connection = Connected` and clears the reason and timestamp.

### D5 — A nightly reconciler, and the existing Resync button

**`ReconcileGitHubStateCronJob`** — nightly, alongside the three existing jobs at `Program.cs:154`.
For each `Account` with an `InstallationId`:

1. `GET /installation/repositories` through `IGitHubInstallationService.CreateInstallationClientAsync`
   (paged), which is the authoritative list of what the App can currently see.
2. Upsert every repository returned, keyed on the numeric id, refreshing `Name`, `FullName`,
   `OwnerLogin`, `IsPrivate`, `DefaultBranch`, `Archived`, `Account`, and pushing the previous full
   name onto the alias list when it changed. Mark them `Connected`.
3. Any repository whose `Account` is this account and which the installation did **not** return is
   disconnected, reason `RemovedFromInstallation` — the sweep that would have caught the current
   production drift.
4. Refresh `Account.Login`, `Type` and `AvatarUrl` from the installation's account object (F5).
5. If the installation itself is gone (404 / 401 after refresh), null `InstallationId` and
   disconnect its repositories, reason `AppUninstalled`.

Repositories with no reachable installation — the OIDC-provisioned ones from F10 — are not visited;
they are reconciled by their own uploads (D7).

The existing `ResyncAction` (`CustomActions/ResyncAction.cs`) keeps its current job of dropping the
caller's 5-minute visibility cache, and additionally runs the reconciliation above for the accounts
that caller manages, so the button on the accounts page repairs what the viewer can actually see.
It already refreshes `AccountCount`, `RepoCount` and the `my-accounts` query afterwards.

The reconciler is the only mechanism that repairs the current production state; no migration is
written (owner's decision).

### D6 — Name aliases first, GitHub's own redirect as the fallback

`Repository` gains `PreviousFullNames : List<string>` (indexed), appended on every rename and
transfer, by both the webhook path and the reconciler.

Resolution of `{owner}/{name}`, in `IRepositoryResolver` — one service that replaces the five copies
of the `FullName ==` predicate from F7:

1. Exact match on `FullName`. **A live full name always wins**, which is what makes the collision
   case safe: once a new repository occupies `MintPlayer/CodeCoverage`, it is found at step 1 and the
   alias is never consulted.
2. Match on `PreviousFullNames`. If exactly one repository matches, 301 to its current `FullName`.
   If more than one matches — two repositories that were both once called this — treat it as a miss
   and go to step 3; the ambiguity is not ours to guess.
3. Ask GitHub: `GET /repos/{owner}/{name}` with an App JWT, which follows GitHub's own
   rename/transfer redirect and returns the numeric id (F11). Map the id to `Repositories/{id}`; on
   a hit, backfill the alias and 301. Results are cached, **including negative results**, so an
   unknown name costs one call per TTL and not one per request.
4. Miss. The badge endpoint renders its existing "unknown" badge (`BadgeController.cs:14-17,65-66`)
   rather than 404ing, unchanged.

Step 3 is skipped when the App JWT is unavailable, so the badge path degrades to steps 1-2 rather
than failing.

### D7 — A successful upload reconnects

`ResolveOidcRepository` (`UploadsController.cs:536-577`) sets `Connection = Connected` and clears the
reason when it resolves an existing disconnected repository, and refreshes `FullName`/`OwnerLogin`
from the OIDC claims (which are GitHub-signed and current). A workflow that still runs and still
uploads is proof the repository is alive and ours, and it is the only signal available for a repo
that moved to an organization where the App is not installed.

This makes the two directions symmetric: the reconciler disconnects on absence, an upload reconnects
on presence.

### D8 — Deletion is an explicit, owner-invoked, cascading action

A disconnected repository shows a `btn-danger` **Delete** on its detail page, offered only to a
viewer who manages the owner and only while `Connection == Disconnected`.

- New right `Manage/RepositoryData` in `security.json`, authenticated group only (F13), checked
  against `RepositoryVisibility`'s manage rule for that owner.
- A `DeleteRepositoryDataAction` custom action broadcasts a `DeleteRepositoryDataMessage`; a
  recipient deletes, by id prefix, every `Commit`, `Build`, `FileCoverage`, `BuildTreeSummary`,
  `CommitAssembly`, `PullRequestFeedback` and repo-scoped `ApiToken` for that repository, then the
  `Repository` itself. Deletion is queued rather than inline because the id-prefix sweep is
  unbounded and must not run inside the request.
- This is also the fix for the orphans the current `deleted` branch has already created (F4).

### D9 — Account identity is refreshed unconditionally, and org renames are handled

The `if (string.IsNullOrEmpty(account.Login))` guard at `GitHubEventsRecipient.cs:123` is removed;
every path that touches an account refreshes `Login`, `Type` and `AvatarUrl`.

The rename itself arrives as **`organization.renamed`, not `installation_target`**. Measured
2026-09-05: both Coverage apps subscribe to exactly eight events — `member`, `membership`,
`organization`, `pull_request`, `push`, `repository`, `team`, `team_add` — and `installation_target`
is not among them. An App receives only what it subscribes to, so a handler listening for
`installation_target` alone would never have run. `OnAccountRenamed` handles both event names (the
account object is under `account` in one and `organization` in the other) and ignores every
non-`renamed` action, since `organization` also fires for membership changes.

### D10 — `ApiToken.AccountLogin` stops being the authorization key

`ApiToken.AccountLogin` (F6) goes stale on a transfer and is compared against
`repository.OwnerLogin` to authorize an upload (`UploadsController.cs:522`). The token gains
`AccountGitHubId`, written on creation and used for the comparison; `AccountLogin` is kept for
display only. Existing tokens without the id fall back to the login comparison, so nothing breaks on
deploy.

### D11 — An installation change is a trigger, not a description

`installation_repositories` announces that an installation's repository set changed. It cannot be
trusted to say *how*. Measured 2026-09-05:

| change | `repository_selection` | `repositories_added` | `repositories_removed` |
| --- | --- | --- | --- |
| narrow `all` → 3 selected | `selected` | all 3 | **empty** |
| widen 3 selected → `all` | `all` | all 153 | **empty** |

Two things follow. The payload **states the resulting set rather than the difference** — the three
named on the narrowing were all reachable a moment earlier, so nothing became newly accessible. And
`repositories_removed` is empty on a narrowing, so the access lost to every other repository on that
account is reported **nowhere**. An app that believed the payload would keep advertising
repositories it can no longer see: this PRD's own bug, arriving by a second route.

So the handler applies the payload for the timely case and broadcasts `ReconcileAccountMessage`,
whose recipient asks GitHub for the authoritative set (D5's reconciler, scoped to one account). The
nightly sweep would find it eventually; this closes the window to seconds.

*The fast path was measured and deliberately not taken.* With the previous `repository_selection`
persisted, `all → selected` plus `repositories_added` gives the new set directly, with no API call —
that now works. It stays unimplemented because it is a second mechanism computing what the
reconciler computes, correct only for scope *transitions* (a genuine incremental add must not be read
as "the set is now exactly this", and the payload cannot distinguish the two), and its correctness
would rest on a stored field we would then have to keep accurate.

### D12 — Only the current owner may disconnect a repository

When the App is installed on both sides of a transfer, one move produces three events from two
installations — `installation_repositories.removed` from the old owner, `repository.transferred` and
`installation_repositories.added` from the new one — roughly two seconds apart, on queues with no
ordering guarantee. A removal applied last would disconnect a repository the App can plainly still
see, and leave it that way until the nightly reconciler.

`OnInstallationRepositories` therefore ignores a removal whose reporting account no longer owns the
repository: if it has already been re-parented, the old owner is describing a repository that is not
theirs any more. Ownership settles it without an ordering assumption, so both arrival orders end
correct. The same reasoning already protects `installation.deleted`, whose sweep is scoped by
`Repository.Account` for free.

A "selected" installation that loses its **last** repository is deleted outright by GitHub —
`installation.deleted`, not `installation_repositories.removed` — which D4's uninstall branch
already handles.

### D14 — Resolution must not become an existence oracle

The badge endpoint is `[AllowAnonymous]` and deliberately **never 404s**: an unknown repository and a
private one the caller may not see both render an "unknown" badge, and the cache header is derived
from the request rather than from the repository, so nothing about the response distinguishes them.
D6 routes that endpoint through `IRepositoryResolver`, which put two new holes in that guarantee.

**Rate-limit amplification.** Step 3 calls GitHub for any name steps 1 and 2 miss. An anonymous
caller probing distinct names turns each miss into an App API call, and exhausting that quota
degrades the reconciler, the PR bot and the diff service. Negative caching bounds repeats, not
distinct names.

**A timing oracle.** A name we hold answers from RavenDB in milliseconds; one we do not costs a
network round-trip. For a private repository, "we hold it" means it exists *and* the App is
installed on it — the exact fact the never-404 rule refuses to disclose.

Step 3 is therefore gated on the **owner already being an account we know**. That keeps the case it
exists for — the owner is known and only the repository name is stale, which is what a rename or a
transfer leaves in a published badge URL — and costs an indexed lookup instead of a network call for
everything else. The residual signal is whether an *account* is known, which is materially weaker:
it is already public for any account with a public repository, and it says nothing about any
particular repository.

The rest of the surface was checked and needs no change: `BrowseController.GetRepo`,
`RepoSettingsController` and `TokensController` all answer a uniform `NotFound` for "unknown" and
"not allowed" alike, and the `/spark` grids and detail pages are filtered by
`RepositoryVisibility`, which admits a private repository only to a viewer GitHub says may see it.

### D13 — The complete event matrix, and what is deliberately not covered

Written out because "did we handle every case" is otherwise unanswerable, and because two gaps were
found by writing it out rather than by testing.

| event / action | effect on our state |
| --- | --- |
| `repository.created` | upsert, connected |
| `repository.deleted` | disconnect, `DeletedOnGitHub` — never deletes |
| `repository.transferred` | re-parent + record old name, **stays connected** (only the gaining side hears it) |
| `repository.renamed` | rename + record old name |
| `repository.archived` / `unarchived` | `Archived`; connection untouched |
| `repository.privatized` / `publicized` | `IsPrivate`, which the visibility rules already key on |
| `repository.edited` | generic upsert (default branch, description) |
| `installation.created` | connect, upsert the payload's repositories |
| `installation.unsuspend`, `new_permissions_accepted` | restore `InstallationId` **and reconcile** — the payload lists no repositories |
| `installation.suspend` | disconnect the account's repositories, `AppSuspended` |
| `installation.deleted` | disconnect the account's repositories, `AppUninstalled` |
| `installation_repositories.added` | upsert + connect, **and reconcile** |
| `installation_repositories.removed` | disconnect *if the reporting account still owns it*, **and reconcile** |
| `organization.renamed` / `installation_target.renamed` | rewrite the account login and every full name under it |
| `push`, `pull_request` | commit and pull-request data (pre-existing) |
| OIDC upload | provision, or reconnect and refresh the name |
| nightly cron, Resync button | reconcile everything reachable |

**Subscribed but ignored on purpose.** `member`, `membership`, `team`, `team_add` change who may see
a private repository, and nothing in the database depends on that: `RepositoryVisibility` resolves
the viewer's owners from the GitHub API per request (5-minute cache), so a membership change is
picked up on the next read without a webhook. Storing it would create a second, staler answer to a
question we already answer correctly.

**Known gaps, accepted.**

1. *A repository whose owner has no installation is never reconciled.* These are the
   OIDC-provisioned ones (F10): the reconciler walks installations, and there is none to walk. If
   such a repository is deleted or made private on GitHub we never learn, and it stays advertised
   with its last coverage. Mitigated by uploads refreshing it, and by the owner's Delete. Closing it
   properly means polling `GET /repos/{owner}/{name}` per repository, which is a rate-limit problem
   for the benefit of a rare case.
2. *`github_app_authorization.revoked` is not handled* — the user revoking the OAuth grant affects
   their own visibility, which `GitHubAccessService` already discovers via its token-state check and
   the reauth prompt. No stored state is wrong.
3. *An organization deleted outright* is not handled as such; in practice it arrives as
   `installation.deleted`, which disconnects that account's repositories.

## Acceptance criteria

1. `MintPlayer/CodeCoverage` no longer appears in the `MintPlayer` account's repository grid for an
   anonymous visitor after one reconciler run, without any manual document edit.
2. It still appears to a `MintPlayer` manager, marked disconnected with reason `TransferredAway`.
3. `GET /badge/MintPlayer/CodeCoverage.svg` still renders its last known coverage, and
   `/r/MintPlayer/CodeCoverage` still resolves.
4. A repository renamed on GitHub keeps serving its old badge URL, 301ing to the new name.
5. Creating a new repository at a full name held by an alias resolves to the new repository, and the
   alias stops resolving.
6. An `installation_repositories.removed` delivery disconnects a repository and destroys no data; a
   following `.added` reconnects it.
7. An OIDC upload from a disconnected repository reconnects it and is accepted.
8. Deleting a disconnected repository removes every `Commit`, `Build`, `FileCoverage`,
   `BuildTreeSummary`, `CommitAssembly`, `PullRequestFeedback` and repo-scoped `ApiToken` for it, and
   leaves no document whose id begins with `Commits/{id}/` or `Repositories/{id}`.
9. No `SparkMessage` document is written for a message type that has no registered recipient.
10. A GitHub organization rename is reflected in `Account.Login` and in every `Repository.FullName`
    under it, and the old names resolve — via `organization.renamed`, which is an event the apps are
    actually subscribed to.
11. A repository transferred between two accounts that both have the App installed ends `Connected`,
    whichever order the three resulting events are processed in.
12. Narrowing an installation from "all repositories" to a selected few disconnects the repositories
    that silently left it, even though no webhook reports their removal.
13. Suspending the App hides an account's repositories and unsuspending brings them all back,
    without waiting for the nightly sweep.
14. An anonymous request for a repository under an owner the service has never heard of makes no
    GitHub API call, and `/badge/{owner}/{name}.svg` still answers "unknown" rather than 404 for an
    unknown repository and for a private one alike.

## Breaking changes

- **`MintPlayer.Spark.Webhooks.GitHub`**: apps that registered `IRecipient<GitHubWebhookMessage>` now
  receive *every* GitHub event, not ten. A recipient with a `default: return;` (which CodeCoverage
  and `WebhooksDemo/LogAllWebhooks` both have) is unaffected; one that assumes a closed set is not.
- Typed `GitHubWebhookMessage<TEvent>` envelopes are no longer broadcast unless a recipient is
  registered. Anything reading those documents out of RavenDB directly would stop seeing them.
- `Repository.Connection` defaults to `Connected`, so existing documents keep their behaviour with
  no migration; the reconciler's first run is what corrects them.
- The npm/NuGet majors do **not** move: no Angular or .NET major change is involved
  (`CLAUDE.md` — package majors track the platform). This is a preview-minor on
  `MintPlayer.Spark.Webhooks.GitHub`.

## Out of scope (genuinely not being done)

- Repairing `PullRequestCommentRenderer`'s already-posted comments. Their badge and report URLs are
  persisted on GitHub's side; D6's alias resolution is what keeps them working, and rewriting old
  comments is not worth an API call per comment.
- Unifying the two definitions of "repositories of this account" (F9). Both are written from the
  same source and this design keeps them so; collapsing `OwnerLogin` into the `Account` reference is
  a separate refactor with its own index implications.
- The smee dev-tunnel bug in the library (#232). The app has its own working tunnel service; the
  library's copy is untouched here, and the spikes route around it.
- Backfilling `DefaultBranch` for OIDC-provisioned repositories that never had one (F10). The
  reconciler fixes it for any repo whose owner has the App installed; the rest keep the existing
  `BrowseController.cs:132-136` fallback.
- A UI for browsing disconnected repositories across all owners. They appear on the owner's account
  page; there is no global list.

## Spikes

All seven answered — five planned, two that the work itself raised. The measurements are in the
plan. Every one of them was settled by running the thing against the real API, because GitHub's
documentation describes *what each event means* but not *which installation receives it* on a
transfer, and that omission is precisely where this feature's bugs lived.

- **S1** — Does GitHub deliver `repository.transferred` to an installation that is *losing* access?
  **No.** Only to the one gaining it. This inverted D4.
- **S2** — Does `installation_repositories.removed` fire on a transfer out of the organization?
  **Yes**, and it is the only event the losing installation receives.
- **S3** — Does `GET /repos/{owner}/{name}` follow the rename redirect? **Yes**, unauthenticated
  included, answering `301` with `Location: /repositories/{id}`; Octokit follows it.
- **S4** — Is a transferred-away repository absent from the installation's repository set? **Yes** —
  the installation is "All repositories" for the org and emitted `.removed` for it.
- **S5** — Can `MessageTypeAllowList` answer "is there a recipient for this type"? **Not as-is** —
  right shape, wrong question and `internal`; a public `IMessageRecipientRegistry` was added instead.
- **S2b** *(unplanned)* — Are the apps even subscribed to the events these handlers need?
  `repository` and `organization` yes; **`installation_target` no**, which would have shipped D9 as
  code that never runs.
- **S6** *(unplanned)* — Does an org-wide install behave differently from a "selected" one? **Yes**,
  and it exposed the hole D11 and D12 exist to close.

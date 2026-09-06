# Plan — keeping the advertised accounts and repositories in step with GitHub

PRD: [coverage_account_sync_PRD.md](coverage_account_sync_PRD.md)

One pull request, spanning `libs/webhooks/MintPlayer.Spark.Webhooks.GitHub`,
`libs/messaging/MintPlayer.Spark.Messaging` and `apps/CodeCoverage`. Nothing here is split off.

## Progress (2026-09-05)

M1-M11 are implemented on `coverage-account-sync`; M10 is this file plus the READMEs; M12 awaits
deploy. **CodeCoverage 307 passed**, **MintPlayer.Spark 1924 passed**, **ng-spark 398 passed**.

Four things were added after the milestones were first called done, each because a question was
asked rather than because a test failed:

- **D11/D12** — the transfer experiments showed the installation payload cannot describe its own
  change, and that a transfer between two installed accounts races. Reconcile trigger, ownership
  guard.
- **The unsuspend gap** — found by writing out D13's event matrix: the installation payload lists no
  repositories on `unsuspend`, so re-enabling the App reconnected nothing.
- **M9 as planned but not delivered** — an audit of the tests against D13 found the *delete cascade*
  untested, which is the only path in the application that destroys data. Also the upload
  authorization and the OIDC reconnect, both of which the plan had listed. Writing them found the
  reconnect firing on a GET.
- **D14** — routing the anonymous badge endpoint through the resolver had made it an existence
  oracle for private repositories, and a way to burn the App's GitHub quota.

**All spikes are answered**, S1/S2/S4 by running the transfer for real against the production
organization on 2026-09-05 and reading both apps' delivery logs. Two of them changed the code:

- **S1 inverted D4.** `repository.transferred` reaches only the installation that *gains* a
  repository, so disconnecting there was wrong. Removed.
- **S2b killed a handler.** `installation_target` is not among the apps' subscribed events, so the
  org-rename path now listens for `organization.renamed` as well — otherwise it would have shipped
  as code that never runs.

Four defects were found *by* the work rather than planned for: typed dispatch could fail a whole
delivery after the catch-all had already been broadcast, earning a redelivery and a duplicate
message; `ApiToken` authorized uploads on a renameable login, wrong in both directions after a
transfer; plus the two above.

## Milestones

| M | deliverable | touches |
| --- | --- | --- |
| M0 | Spikes S1-S5 | nothing committed but the answers |
| M1 | Library: forward every event; broadcast typed envelopes only when consumed | `libs/webhooks`, `libs/messaging` |
| M2 | `Repository.Connection` + alias list, model JSON, index | `CodeCoverage.Library`, `App_Data/Model` |
| M3 | `RepositoryVisibility.ListingFilter` and its call sites | `apps/CodeCoverage` |
| M4 | `GitHubEventsRecipient`: per-action repository handling, `installation_repositories`, account identity | `apps/CodeCoverage` |
| M5 | `IRepositoryResolver`: alias-first, GitHub fallback, 301s | `apps/CodeCoverage` |
| M6 | `ReconcileGitHubStateCronJob` + `ResyncAction` extension | `apps/CodeCoverage` |
| M7 | OIDC upload reconnects; `ApiToken.AccountGitHubId` | `apps/CodeCoverage` |
| M8 | Owner-invoked delete: right, action, cascading recipient, UI | `apps/CodeCoverage`, `security.json` |
| M9 | Tests (single batched run) | `tests/`, `CodeCoverage.Tests` |
| M10 | Docs | `docs/` |
| M11 | Versions | `.csproj` |
| M12 | Deploy and verify against the real production drift | — |

Sequencing note: M1 is first because M4 is unreachable without it —
`installation_repositories` does not exist as a message until the library forwards it. M2 before M3
before M4 for the same reason (the field must exist before anything filters on it). M5 and M6 are
independent of each other and of M4 once M2 has landed.

## M0 — Spikes

Run against the **CoverageDevelopment** GitHub App on the `MintPlayer` organization, whose webhooks
are already downstreamed to a local instance. Use the app's own `SmeeWebhookTunnelService` (the
lexical minifier), **not** the library's `AddSmeeDevTunnel` — issue #232 means the library's tunnel
corrupts `installation` payload bytes and every such delivery fails its signature check. Confirm the
app's tunnel is the one configured (`GitHub:SmeeChannelUrl`, `Program.cs:198-201`) before drawing any
conclusion from a *missing* delivery.

A throwaway repository under `MintPlayer` plus the existing `MintPlayer-Archive` org is enough
apparatus for S1, S2 and S4. Capture the raw delivery list from the App's advanced settings page,
not only what the app logged — the whole point is to distinguish "GitHub did not send it" from "we
dropped it".

### S1 + S2 — what each side of a transfer actually receives — **ANSWERED, and S1 inverted a design decision**

Run for real on 2026-09-05, on the actual repository, with the owner's approval: the archived
`MintPlayer-Archive/CodeCoverage` was transferred back into `MintPlayer` and then out again. Both
Coverage apps' delivery logs were read directly.

| time (local) | direction | delivery |
| --- | --- | --- |
| 10:25:20 | into `MintPlayer` | `repository.transferred` |
| 10:25:21 | into `MintPlayer` | `installation_repositories.added` |
| 10:26:14 | out to `MintPlayer-Archive` | `installation_repositories.removed` |

**There is no repository event on the way out.** GitHub tells the installation that *gains* a
repository; the one losing it hears only that its repository set shrank.

Two consequences, both acted on:

1. **S1 = no**, and the plan's own contingency applied — but in the opposite direction from the one
   anticipated. `repository.transferred` is not dead code; it is *live code that meant the wrong
   thing*. Disconnecting there would have marked a repository we had just acquired and could plainly
   see as unreachable, then relied on the `added` one second later to undo it — correctness resting
   on the processing order of two independently queued messages. The disconnect was removed, and
   `Gaining_a_repository_ends_connected_whichever_order_the_two_events_are_processed` pins the
   ordering independence in both directions.
2. **S2 = yes**, and it is the *only* signal. Which closes the investigation loop exactly: the
   transfer on 2026-09-01 12:47 UTC sent `installation_repositories.removed`, the library discarded
   it before any app could see it, and the repository went on being advertised. The retained
   delivery log only reaches back ~3 days (204 deliveries), so the original event is long gone —
   this re-ran it.

The repository was restored to `MintPlayer-Archive/CodeCoverage`, archived, public, id `1305831351`,
byte-identical to the recorded pre-state.

### S2b — the App's event subscriptions — **ANSWERED, and it killed a handler**

Read off the two apps' permission pages while the transfer evidence was being collected. Both
subscribe to exactly eight events:

```
member, membership, organization, pull_request, push, repository, team, team_add
```

`repository` is there, so M4's per-action handling runs. **`installation_target` is not** — and an
App receives only what it subscribes to, so `OnInstallationTarget` as written would never have
fired. `organization` *is* subscribed and carries action `renamed`, so the handler now accepts both
event names and ignores non-rename actions. Without this check the org-rename path would have
shipped as code that compiles, tests green, and never runs in production — the same failure mode as
the bug being fixed.

`installation` and `installation_repositories` are absent from that list because they are not
subscribable: GitHub always delivers App-lifecycle events. That is why `.removed` arrived despite
not being checked anywhere.

### S3 — Does `GET /repos/{owner}/{name}` follow the rename redirect? — **ANSWERED: yes, and unauthenticated too**

Measured 2026-09-05.

```
$ curl -s -D - -o /dev/null https://api.github.com/repos/MintPlayer/CodeCoverage
HTTP/2 301
Location: https://api.github.com/repositories/1305831351
X-RateLimit-Limit: 60          # unauthenticated, per IP

$ curl -sL  … → id 1305831351, full_name MintPlayer-Archive/CodeCoverage
```

Three things fall out. The endpoint needs **no authentication** for a public repository, so the
fallback works on a self-hosted instance with no App credentials. The redirect target *is* the
numeric id — `/repositories/{id}` — so GitHub is handing us exactly the key our documents are
already filed under. And the anonymous budget is 60 requests an hour per IP, which is why the
resolver caches misses as well as hits.

The half that could not be settled by reading: whether **Octokit** follows the 301. It does —
verified by `GitHubRenameRedirectContractTests`, which runs against the real API under
`COVERAGE_GITHUB_CONTRACT_TESTS=1` and is kept precisely because a mock would only ever confirm the
behaviour we assumed.

The name-takeover half of this spike is moot: resolution matches a live full name before any
remembered one, so a new repository at an old name is found without the alias ever being consulted.
Asserted in `RepositoryResolverTests.A_new_repository_at_an_old_name_shadows_the_alias`.

### S3b — the takeover case against GitHub itself (not run)

Two probes. First, `gh api repos/MintPlayer/CodeCoverage` already resolves to id `1305831351` for a
user token (PRD F11) — repeat it with an App JWT, since the fallback in D6 step 3 runs
unauthenticated-of-a-user. Second, and the one that actually matters: create a *new*
`MintPlayer/spike-transfer` after transferring the old one away, and check whether the API returns
the new repository's id or still redirects to the old one. D6 step 1 makes our own resolution safe
either way, but if GitHub keeps redirecting we must not let step 3 overwrite a correct step-1 hit.

### S6 — org-wide vs "selected repositories" installs — **ANSWERED, and it found a hole webhooks cannot cover**

Measured 2026-09-05 with a throwaway `spike-transfer-probe` repository, both Coverage apps installed
org-wide on `MintPlayer` and on a personal account.

**Transfer between two accounts that both have the App (org-wide).** One move, three events, two
installations:

```
10:58:49  installation_repositories.removed   installation 153617061 (MintPlayer)
10:58:51  repository.transferred              installation 153539439 (personal)
10:58:51  installation_repositories.added     installation 153539439 (personal)
```

Two seconds apart, no ordering guarantee, and the removal names the repository the other events have
just re-parented. An unguarded removal applied last would disconnect a repository the App can plainly
still see. `OnInstallationRepositories` therefore ignores a removal from an account that no longer
owns the repository — ownership decides, not arrival order, so both orders end correct.

**Narrowing an installation from "all" to "selected".** This is the hole:

```
action: "added"     repository_selection: "selected"
repositories_added:   [PieterjanDeClippel/spike-transfer-probe]
repositories_removed: []          ← empty
```

The App lost access to every other repository on that account, and the only event says *added*, with
nothing in `repositories_removed`. **No webhook reports the loss.** An app that believes the payload
keeps advertising repositories it can no longer see — the same failure as the original bug, by a
second route. This is why `installation_repositories` now also broadcasts `ReconcileAccountMessage`:
the payload is applied for the timely case, and GitHub is asked for the authoritative set.

**On a scope change the payload carries the complete set, not a delta.** Measured afterwards with a
fresh installation, deliberately with more than one repository so the two are distinguishable:

| change | `repository_selection` | `repositories_added` | `repositories_removed` |
| --- | --- | --- | --- |
| install on the account | `all` | *(`installation.created`, no lists)* | — |
| narrow `all` → 3 selected | `selected` | **all 3** | empty |
| widen 3 selected → `all` | `all` | **all 153** | empty |

The three named on the narrowing had all been reachable a moment earlier under `all`, so nothing
*became* accessible — GitHub is stating the new set, not the difference. Same on the way back up.

*An inference is therefore available, and was still not taken.* If the last-known
`repository_selection` were persisted on the account, `all → selected` plus `repositories_added`
would give the complete new set directly, and everything else on that account could be disconnected
with no API call. That is now measured rather than assumed, so it would work.

It is not implemented because it would be a **second** mechanism computing the same answer as the
reconciler, correct only for scope *transitions*: a genuine incremental add to an existing selection
must not be read as "the set is now exactly this", and the payload does not distinguish the two
cases — only our stored previous selection would, which makes the rule's correctness depend on a
field we would now have to keep accurate. The reconcile is one path, is authoritative for every
cause of drift rather than this one, and costs a paged API call on an event that fires a few times a
year. If that call ever becomes a problem, the fast path is documented here and ready.

**A "selected" installation that loses its last repository is deleted outright.** Transferring the
probe away when it was the only selected repository produced, from the personal installation:

```
11:04:56  installation.deleted   installation 153539439, repository_selection "selected"
```

— not `installation_repositories.removed`. `OnInstallation`'s `deleted` branch already handles it
(null the `InstallationId`, disconnect that account's repositories), and because it scopes by current
ownership it is order-independent against the transfer events that follow.

### S4 — Is a transferred-away repository absent from the installation's set? — **ANSWERED: yes**

Answered by the same experiment rather than by an installation-authenticated call. The
`coverageproduction` installation on `MintPlayer` (id `153617061`) is scoped to **All repositories**,
so its set is exactly "the org's repositories" — and the transfer out produced
`installation_repositories.removed` for this repository, which is GitHub stating that it left that
set. The reconciler's absence-detection is therefore sound: what it lists is what the installation
can see, and a transferred-away repository is not in it.

The paging question stands on its own and is handled defensively regardless — `InstallationRepositories`
pages explicitly to `TotalCount`, because a truncated page would read as "the rest were removed" and
disconnect an entire organization in one sweep.

### S5 — Can the processor cheaply ask whether a message type has a recipient? — **ANSWERED: yes, but not through that type**

`MessageTypeAllowList` has the right shape — a singleton, built once at startup from the registered
`IRecipient<T>` descriptors, keyed on closed generic types — but it is `internal` to
`MintPlayer.Spark.Messaging`, and its question is a different one: it decides what may be
*deserialized*, a security boundary that also tracks handler types. Widening it to answer "does
anything consume this" would conflate a safety gate with a routing hint.

So the spike's own fallback was taken: a public `IMessageRecipientRegistry` in
`MintPlayer.Spark.Messaging.Abstractions`, which the webhooks package already references. It also
takes ownership of the descriptor scan that `MessageSubscriptionManager` had a second copy of —
"which queues deserve a worker" and "does this type have a consumer" are the same question asked
twice.

## M1 — The library forwards every event

`libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/Services/SparkWebhookEventProcessor.cs`.

1. Broadcast the catch-all `GitHubWebhookMessage` from the already-overridden
   `ProcessWebhookAsync(headers, body, ct)` (`:35`), after signature validation and after the
   dev-tunnel short-circuit (`:55-76`), for **every** `headers.Event`. This is the change that makes
   `installation_repositories` reach an app for the first time.
2. Keep the typed overrides, but gate the typed broadcast in `HandleWebhookAsync` (`:119-147`) on
   S5's answer, and remove the now-duplicated catch-all broadcast from it (`:138-146`) so a delivery
   produces exactly one catch-all message.
3. Add the typed overrides that this work needs and that Octokit already models:
   `ProcessInstallationRepositoriesWebhookAsync`, `ProcessInstallationTargetWebhookAsync`,
   `ProcessOrganizationWebhookAsync`.
4. Log at debug the event type and whether a typed envelope was offered, so a future dropped event
   is visible rather than silent (PRD F2).

The dev-tunnel short-circuit must stay ahead of the broadcast: a delivery destined for a developer's
machine must not also be processed locally.

## M2 — The lifecycle field and the alias list

`apps/CodeCoverage/CodeCoverage.Library/Entities/Repository.cs`:

- `RepositoryConnection Connection { get; set; } = RepositoryConnection.Connected;`
- `string? DisconnectedReason`, `DateTime? DisconnectedAtUtc`
- `List<string> PreviousFullNames { get; set; } = [];`

All four are queryable, so none carries `[IgnoreForIndex]` — `Connection` and `PreviousFullNames` are
filtered on by M3 and M5 respectively. `DisconnectedReason` and `DisconnectedAtUtc` are shown in the
UI, not filtered, but stay in the index to avoid a second load on the detail page.

Then `App_Data/Model/Repository.json` — new attributes with descriptions (the repo requires every
attribute to carry one, per #348), `Connection` and `DisconnectedReason` visible on the detail page,
`PreviousFullNames` `isVisible: false`. Regenerate `modelHashes.json` with
`--spark-synchronize-model`; do not hand-edit it. Verify the generated `VRepository` picked the
fields up before moving on — an unregistered projection silently nulls computed fields.

No data migration: `Connected` is the default and matches every existing document's behaviour.

Two things this milestone got wrong on the first pass, both caught by existing guards rather than by
review. `Connection` was given `showedOn: "Query, PersistentObject"`, which put a fifth column on the
repository grid and failed `ModelColumnGuardTests` — a test that exists to make exactly that an
explicit product decision rather than a side effect of adding a field. It is detail-only now and the
curated grid is unchanged; if an owner should be able to spot disconnected repositories from the
grid, that is a deliberate change to make, with the guard updated to say so. And the hand edits to
`isVisible`/`showedOn` had to be verified as a fixed point: `--spark-synchronize-model` preserves
them, but only re-running it proves so, and the first run rewrote the file's formatting.

## M3 — `ListingFilter`, and every place that enumerates

`Services/RepositoryVisibility.cs` gains, beside the existing `Filter`/`IsVisible` pair:

```csharp
public static Expression<Func<Repository, bool>> ListingFilter(string[] allowedOwners)
    => repository => (repository.Connection == RepositoryConnection.Connected && !repository.IsPrivate)
                  || repository.OwnerLogin.In(allowedOwners);
```

`In()` rather than `Contains`, for the reason the existing doc comment gives — Raven's LINQ provider
fails on `string[].Contains` inside an `OrElse` on .NET 10. Extend that comment to explain why there
are now two rules rather than one, or the next reader will "simplify" them back together and
un-resolve every disconnected badge URL.

Switch to `ListingFilter`: `Actions/RepositoryActions.GetRowFilterAsync` (`:24-33`),
`Services/SparkVisibility.cs:31`, `Controllers/BrowseController.cs:62` and `:176`,
`Services/MyAccountsService.cs:43`. Leave `Filter` in place at
`BrowseController.ResolveVisibleRepository` (`:512`), `BadgeController.cs:44`,
`RepoSettingsController.cs:74`, `TokensController.cs:55`, `UploadsController.cs:510`.

## M4 — Per-action repository handling

`apps/CodeCoverage/CodeCoverage/Recipients/GitHubEventsRecipient.cs`.

- `OnRepository` (`:109`) gets the action table from PRD D4. The `deleted` branch (`:114-120`) stops
  calling `session.Delete` and disconnects instead.
- `OnInstallationRepositories` (`:84`) — now reachable for the first time — replaces its
  `session.Delete` loop (`:95-106`) with a disconnect, and marks added repositories `Connected`.
  Keep the batched `LoadAsync` shape: the 30-request session budget is why it exists.
- `OnInstallation` (`:57`) disconnects the account's repositories on `deleted`/`suspend`, reason
  `AppUninstalled`, and reconnects them on `created`/`unsuspend`.
- Remove the `if (string.IsNullOrEmpty(account.Login))` guard at `:123` (PRD D9).
- New `OnInstallationTarget` for the organization-rename event: rewrite `Account.Login` and every
  owned repository's `OwnerLogin`/`FullName`, pushing old names onto the alias lists.
- Alias appends go through one helper that de-duplicates and caps the list, so a repository renamed
  fifty times does not grow an unbounded array in an index.

The account's repositories are loaded through `Repositories_Overview` filtered on
`r.Account == account.Id` — the reference, not `OwnerLogin`, since a transfer changes the login
before this code runs.

## M5 — `IRepositoryResolver`

A new scoped service that owns the four-step resolution from PRD D6 and replaces all five copies of
`.Where(r => r.FullName == $"{owner}/{name}")` (`BadgeController.cs:45`, `BrowseController.cs:513`,
`RepoSettingsController.cs:75`, `TokensController.cs:56`, `UploadsController.cs:511`).

```csharp
Task<RepositoryResolution> ResolveAsync(string owner, string name, CancellationToken ct);
// record RepositoryResolution(Repository? Repository, bool Redirect);
```

- Step 3 (the GitHub call) is behind `IGitHubInstallationService.CreateAppClientAsync`, cached in
  `IMemoryCache` **including negative results**, with a short TTL. The badge endpoint is anonymous
  and rate-limited; an unknown name must cost one API call per TTL, not one per request.
- Step 3 is skipped when no App JWT is available, so the badge path degrades to steps 1-2.
- Step 3 is also skipped unless the **owner is an account we already know** (PRD D14, added after the
  fact). Without that gate the anonymous badge endpoint hands a caller the App's GitHub rate limit
  and a timing-based existence oracle for private repositories. The gate keeps the case the step
  exists for — known owner, stale repository name — and its own lookup is cached alongside the name
  lookups, misses included.
- `Redirect: true` makes `BrowseController` and the SPA vanity guards issue a 301 to the current
  full name. `BadgeController` does **not** redirect — a 301 on an image inside a README is a wasted
  round-trip for camo; it serves the badge directly at the old URL.

## M6 — The reconciler

`Ingestion/ReconcileGitHubStateCronJob.cs` (nightly), registered beside the existing three at
`Program.cs:154`. Steps exactly as PRD D5. Notes that will otherwise be rediscovered painfully:

- Page `GET /installation/repositories` per S4's answer; do not assume one page.
- Batch the RavenDB loads. A single account can hold hundreds of repositories and the session budget
  is 30 requests — follow the `UpsertRepositories` pattern already in the recipient (`:243`).
- A GitHub call failing for one account must not abort the sweep for the others.
- Treat 404/401-after-refresh on the installation as "installation gone", not as a transient error;
  anything else is transient and leaves state untouched. Disconnecting on a transient network error
  would blank the account page.
- Only accounts with a non-null `InstallationId` are visited; OIDC-provisioned repositories are
  reconciled by M7.

`CustomActions/ResyncAction.cs` additionally runs the same reconciliation for the accounts the
caller manages, after `InvalidateAsync` and before re-reading `myAccounts.GetAsync` — the existing
order already refreshes the counts and the `my-accounts` query afterwards, so the button repairs
what the viewer sees in one click.

## M7 — Uploads reconnect, and tokens stop keying on a login

- `UploadsController.ResolveOidcRepository` (`:536-577`): when it loads an existing repository, set
  `Connection = Connected`, clear the reason, and refresh `FullName`/`OwnerLogin` from the OIDC
  claims. Keep the `visibility == "public"` gate on the *provisioning* branch only — reconnecting an
  already-known repository is not the same decision as creating one.
- `ApiToken.AccountGitHubId` written at `TokensController.cs:70`, emitted as a claim by
  `ApiTokenAuthenticationHandler.cs:63-64`, and compared at `UploadsController.cs:522` in preference
  to the login. Tokens without the id fall back to the login comparison, so no token is invalidated
  by the deploy.

## M8 — Owner-invoked delete

- `App_Data/security.json`: new right `Manage/RepositoryData`, authenticated group only. There is no
  existing write right on `Repository` to relax (PRD F13).
- `CustomActions/DeleteRepositoryDataAction` — `selectionRule` on a single repository, offered only
  when `Connection == Disconnected` and the caller manages the owner. It broadcasts, it does not
  delete.
- `Recipients/DeleteRepositoryDataRecipient` — deletes by id prefix
  (`Commits/{repoId}/`, and the `Repositories/{repoId}` document), plus `PullRequestFeedbacks/{repoId}/`
  and repo-scoped `ApiToken`s by `RepositoryGitHubId`. Use RavenDB's delete-by-query rather than
  loading; the file-coverage documents alone can number in the tens of thousands.
- SPA: a `btn-danger` on the repository detail page with a confirmation naming the repository and
  the fact that it is irreversible.

## M9 — Tests (single batched run)

Nothing is run until every milestone above is implemented; intermediate milestones are verified by
reading and type-checking.

**Library** (`tests/MintPlayer.Spark.Tests/Webhooks/GitHub/`) — the gap PRD F2 names:
- every event type produces exactly one catch-all message, including types with no typed override
- a typed envelope is broadcast when a recipient is registered and not otherwise
- the dev-tunnel short-circuit still suppresses both

**App** (`apps/CodeCoverage/CodeCoverage.Tests/Recipients/GitHubEventsRecipientTests.cs`) — currently
five tests, all about `ParentSha`:
- `repository.deleted` disconnects and deletes nothing
- `repository.transferred` re-parents, records the alias, disconnects (subject to S1)
- `repository.renamed` records the alias and stays connected
- `installation_repositories.removed` disconnects; a following `.added` reconnects
- an organization rename rewrites the account login and every owned repository
- an existing account's login is refreshed

**Resolver** — alias hit 301s; a live full name beats an alias (the collision case); two aliases
matching is a miss; a negative GitHub result is cached.

**Visibility** — a disconnected public repository is absent from the account grid for an anonymous
viewer, present for its manager, and its badge and `/r/` URL still resolve.

**Reconciler** — a repository absent from `GET /installation/repositories` is disconnected; a
transient GitHub failure changes nothing; a 404 installation disconnects the whole account. Use the
existing WireMock apparatus (`_Infrastructure/WireMockGitHubClientFactory.cs`).

**Delete** — leaves no document with the repository's id prefix.

Per-class databases where the case does not write (see the shared-database migration note); the
suite's cost is dominated by the per-test database lifecycle.

### As delivered (2026-09-05)

The first pass covered the library, the recipient's lifecycle actions, the resolver, the visibility
split and the reconciler — but **not the delete, and not the upload paths**, both of which are
listed above. An audit against D13's matrix caught that. 307 CodeCoverage tests now, including:

- **`DeleteRepositoryDataRecipientTests`** — the gap that mattered, since this is the only path in
  the application that destroys coverage data and its sweep rests on an id convention rather than a
  query. Covers a **neighbouring repository that must survive** (what catches a prefix one character
  too greedy) and the re-check that refuses a repository which reconnected between authorization and
  application.
- **`UploadsControllerAuthorizationTests`** — an account token accepted on the owner's numeric id,
  refused when it names the previous owner of a transferred repository, and still accepted on the
  login alone when it predates the id. Asserted through the upload rather than `Status`, because
  `Status` answers `NotFound` both for "not allowed" and for "no build yet"; using it made two tests
  fail for a reason that was the oracle's fault, not the code's.
- The remaining matrix rows: `repository.created` / `privatized` / `publicized` / `unarchived`,
  `installation.created` / `deleted` / `suspend` / `unsuspend`, and that every
  `installation_repositories` event asks for a reconcile.
- **`An_unheard_of_owner_never_reaches_GitHub`** (D14), whose stand-in installation service *throws*
  if touched, so a regression fails rather than merely being slow.

Writing the upload tests found a defect: the OIDC reconnect ran before the `provision` check, so
polling `Status` — a GET that saves nothing — mutated the repository and appeared to work.

**Still untested, deliberately:** `ReconcileGitHubStateCronJob` (a loop and a try/catch over a
reconciler with six tests of its own), `ResyncAction`, and the ng-spark `variant` rendering.

## M10 — Docs

- `docs/code-coverage/README.md` — add this PRD/plan pair under *Live — still authoritative*.
- `docs/code-coverage/upload-api.md` — the reconnect-on-upload behaviour.
- `apps/CodeCoverage/README.md` — the repository lifecycle, what disconnected means, and the Delete
  button.
- A short note in the webhooks library README that every event is now forwarded.

## M11 — Versions

`MintPlayer.Spark.Webhooks.GitHub` and `MintPlayer.Spark.Messaging` take a **preview bump only**.
Neither the Angular nor the .NET major changes, so the major digit does not move — `CLAUDE.md` is
explicit that a wrongly published major is burned forever, and CI publishes on push to `master`.
Check the version diff in the PR review.

## Production state as measured, 2026-09-05

Recorded because M12 is a *verification* milestone and needs a before to compare against.

- `https://coverage.mintplayer.com/badge/MintPlayer/CodeCoverage.svg` renders **coverage: 41.5%**,
  under the name the repository no longer has. After deploy it must keep rendering — that is the
  point of D2 — while the repository leaves the account listing.
- `GET /api/browse/accounts/MintPlayer/repos` answers **401** to an anonymous caller, so the
  advertised list is only observable signed in or through `/spark`.
- The App delivery log retains roughly **3 days / 204 deliveries**, so the original 2026-09-01
  transfer is long gone from it. Any future post-mortem of this kind has that window to work in.

The transfer experiments left production unharmed: the deployed build predates M1, so it discards
`installation_repositories` exactly as it always did, and `repository.transferred` re-parented the
document to the owner it already believed in. `MintPlayer-Archive/CodeCoverage` was returned to its
recorded pre-state field by field, and the probe repository was deleted.

## M12 — Deploy and verify

The reconciler is the migration (owner's decision — no bespoke repair code). After deploy:

1. Trigger the reconciler, or press Resync as a `MintPlayer` manager.
2. Confirm `MintPlayer/CodeCoverage` leaves the anonymous account grid and stays visible, marked
   `TransferredAway`, to a manager.
3. Confirm `GET /badge/MintPlayer/CodeCoverage.svg` still renders its last coverage and
   `/r/MintPlayer/CodeCoverage` still resolves.
4. Confirm no `SparkMessage` documents accumulate on an unconsumed queue after a few deliveries.

Acceptance criteria 1-3 and 9 in the PRD are satisfied here, against the real drift, not a fixture.

## Risks carried into implementation

- **S1/S2 may both come back negative.** If GitHub tells a losing installation nothing at all, the
  nightly reconciler is the only detector and the cadence should move to hourly. Decide after M0, not
  during M6.
- **The reconciler can blank an account page if a transient GitHub failure is misread as absence.**
  The error classification in M6 is the single most dangerous line in this plan; test it before the
  happy path.
- **Two visibility rules will drift.** `RepositoryVisibility`'s doc comment exists because this
  exact rule drifted three ways before. Adding a second rule beside it is the very thing that comment
  warns about — the mitigation is that both live in the same file with the reason written down, and
  M9 asserts the difference in both directions.

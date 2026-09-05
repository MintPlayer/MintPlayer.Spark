# Plan — keeping the advertised accounts and repositories in step with GitHub

PRD: [coverage_account_sync_PRD.md](coverage_account_sync_PRD.md)

One pull request, spanning `libs/webhooks/MintPlayer.Spark.Webhooks.GitHub`,
`libs/messaging/MintPlayer.Spark.Messaging` and `apps/CodeCoverage`. Nothing here is split off.

## Progress (2026-09-05)

M1-M9 and M11 are implemented on `coverage-account-sync`; M10 is this file plus the READMEs; M12
awaits deploy. Suites run once at the end, as a single sweep: **CodeCoverage 277 passed**,
**MintPlayer.Spark 1919 passed**, **ng-spark 398 passed**.

S3 and S5 are answered below. **S1, S2 and S4 remain open** — they need the App's delivery log and
an installation-authenticated call, neither of which is reachable from here. They do not block what
is built: the reconciler detects a transfer whether or not a webhook reports one, and the
`transferred` branch is correct if the event arrives and inert if it never does. What they decide is
whether the nightly cadence is enough (S2) and whether the `transferred` branch is dead code worth
deleting (S1).

Two defects were found *by* the work rather than planned for, both recorded in the commits:
typed dispatch could fail a whole delivery after the catch-all had already been broadcast, earning a
redelivery and a duplicate message; and `ApiToken` authorized uploads on a renameable login, which
is wrong in both directions after a transfer.

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

### S1 — Does GitHub deliver `repository.transferred` to the installation losing access? (shapes M4)

Create `MintPlayer/spike-transfer`, confirm the App sees it, transfer it to `MintPlayer-Archive`,
and read the App's delivery log. Record: which events fired, in what order, and with what payload
`action`. If the losing installation gets nothing, D4's `transferred` branch is dead code and
disconnection rests entirely on S2 and the reconciler — say so in the plan and delete the branch
rather than shipping a second `OnInstallationRepositories`.

### S2 — Does `installation_repositories.removed` fire on a transfer out of the org? (shapes M4, M6)

Same experiment as S1, looking for the other event. Also do the control: manually deselect a
repository from the installation's repository selection, which is the case the event is documented
for. If a transfer produces no `installation_repositories` delivery, the nightly reconciler (M6) is
the *only* mechanism that can detect a transfer, and M6 stops being a safety net and becomes the
primary path — which raises the cadence question from nightly to hourly.

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

### S4 — What does `GET /installation/repositories` return after a transfer, and does it page? (shapes M6)

Call it for the `MintPlayer` installation now, in its already-drifted state. Confirm
`MintPlayer/CodeCoverage` is absent — that absence is the whole detection mechanism of D5 step 3. If
it is *present*, the reconciler cannot work as designed and the milestone needs rethinking before it
is written. Record the page size and whether `Octokit`'s client pages automatically, since the
production installation has more repositories than one page.

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

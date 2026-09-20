# PRD — De-couple CodeCoverage from GitHub, and rebuild sign-in around providers

**Issue:** [#422](https://github.com/MintPlayer/MintPlayer.Spark/issues/422) — "Should we also support
BitBucket and GitLab?" (opened 2026-09-19 with an empty body).

**Status:** in progress on `issue-422-forge-abstraction`. SP1 and SP2 are run (§7.1, §7.2). M1 and
M2 are built and committed (704ab22a, ecd91c3d); as-built notes live in the plan beside each
milestone. M3 onward is not started. The only decision still open is D6f (fork-PR uploads), which
gates M6.

**Scope of *this* document's plan:** stage 1 only — remove the GitHub coupling and rebuild sign-in,
account provisioning and account linking around a provider abstraction, **while GitHub remains the
only implemented provider**. GitLab and Bitbucket are designed for here and implemented in later
stages. This split is the user's own framing:

> "A first step is to get rid of this tight-coupling. Then we can, in later stages, add integration
> with other platforms more easily."

Everything in stage 1 lands in **one pull request**, per the repository's one-PR rule. The later
stages are separate units of work, not deferred parts of this one.

**Evidence base.** Two investigations, both 2026-09-19, against `421e2ff6`:
- the original four-track pass (GitHub coupling inventory, GitLab research, Bitbucket research,
  auth/identity coupling), whose combined PRD+plan is
  [issues/422#issuecomment-5743692719](https://github.com/MintPlayer/MintPlayer.Spark/issues/422) and
  is **superseded by this document**;
- a second four-track pass (Angular client inventory, Spark auth-flow audit, forge feature parity
  with competitor research, and a read of the MintPlayer reference implementation).

Claims below carry `file:line`. ⚠️ marks anything not verified against a live instance or a
running app.

---

## 1. Problem

CodeCoverage serves GitHub-hosted repositories only. The barrier is not the APIs — every feature it
ships has a GitLab and a Bitbucket Cloud equivalent, and in two cases theirs is better than ours.
The barrier is that "GitHub" is encoded as a structural fact in six subsystems rather than as one
provider among several, and that the sign-in flow has no concept of a user holding more than one
forge identity.

A scope note, stated accurately: `product-overview.md:23` lists non-GitHub forges as a non-goal, but
it sits under `### Non-goals (v1)` alongside carry-forward and PR comments, **both of which have
since been built**. This revisits a v1 scope boundary, which this product does routinely. It is not
overturning an architectural prohibition. What *is* standing policy is `:157` ("No parallel
permission system") and `:170` ("GitHub is the authority; no join workflow") — see D6.

---

## 2. Decisions

Decisions the user has already taken are marked **DECIDED**. The rest need answers before the
milestones they gate.

⚠️ **A decision here is a starting point, not a constraint.** The owner's standing instruction:
*"my decisions aren't a requirement. If other decisions turn out to be better, we can still change."*
So where building something has since surfaced a better option, it is written down beside the
decision as a **revisit candidate** (see §6.7 D6d) rather than quietly implemented or quietly
dropped. Changing a decision is cheap; discovering an undocumented one is not.

| | Decision | Status |
|---|---|---|
| **D1** | **Delivery shape.** De-coupling + auth rework is stage 1, one PR. Forge implementations are later stages. | **DECIDED** — user's framing, quoted above |
| **D2** | **Account linking must support both a link-while-signed-in mode and a confirm-by-email mode, as a Spark framework option. CodeCoverage opts into confirm-by-email.** | **DECIDED** — user, this session |
| **D3** | **The GitHub button in the shell is replaced by a link to a login page that renders one button per registered provider, driven by the server's capability report.** | **DECIDED** — user, this session |
| **D4** | **Provider-scoped, not unioned: one sidebar program unit per provider, and the provider as a URL path segment. Authorization stays separate per platform — a GitHub decision never consults GitLab state.** | **DECIDED** — user, this session. Rationale in §5.4 |
| **D5** | **Path prefix — `Repositories/github/{id}`, `Accounts/github/{id}`, `Commits/github/{repoId}/{sha}`.** Every provider looks identical; ids stay human-readable and `startsWith(id(), …)` stays a usable RQL filter. | **DECIDED** — user, this session |
| **D6** | **Resolved by interview, 2026-09-20. The owner set stays *derived live from the forge*, per provider — no stored authorization record.** Six sub-decisions in §6.7. | **DECIDED** — all six, D6f resolved 2026-09-20 |
| **D7** | **Existing GitHub documents are re-keyed by the migration.** No implicit `github` default, no permanent asymmetry. | **DECIDED** — user, this session |
| **D8** | Is GitHub Projects v2 automation GitHub-only forever, or do we model a generic board? | **Recommend GitHub-only** — neither forge has the primitive |
| **D9** | **Spark ships the contract and templates; CodeCoverage ships the transport — an SMTP server run as its own container in `docker-compose.yml` on the VPS, for isolation.** | **DECIDED** — user, this session. See §6.4 |
| **D10** | Bitbucket Data Center / Server: in or out? | **Recommend out** — different API, a fourth provider not a config flag |
| **D11** | **Provider code in ids and URLs: full names (`github`/`gitlab`/`bitbucket`), not `gh`/`gl`/`bb`.** One vocabulary shared by D5's ids and the routes. | **DECIDED** — user, this session |
| **D12** | One git repository pushed to two forges (github + bitbucket remotes) — one record or two? | **Recommend two independent records, no merging.** See §6.6 |
| **D13** | **No backward-compatible badge route.** The legacy two-segment URL is removed, not aliased; badge URLs are replaced at source. Sound only because the deployment has no external users. | **DECIDED** — user, this session |
| **D14** | Per-provider **assemblies**, yes; per-provider **NuGet packages**, not in stage 1. Three projects with `IsPackable=false` buy the whole architectural benefit; publishing adds a permanent public contract with no consumers. | **DECIDED** — owner, this session: *"Not yet i guess."* Split into assemblies now; revisit publishing when a second forge ships |
| **D15** | Which capabilities move into a provider library. | **Partly superseded by D17** — Projects V2 moves onto the interface behind `EForgeCapability.Boards`; App installations stay *internal to the GitHub library*, never on the interface. §6.9 table otherwise stands |
| **D16** | **One `IForgeIntegration` interface; three libraries, one `[Register]`ed implementation class each; the app calls three extension methods; injection sites take `IEnumerable<IForgeIntegration>` and never know which forge they are on.** Implementations are facades over per-concern services internal to each library. | **DECIDED** — owner, this session. See §6.10 |
| **D17** | **Capability gaps are expressed as a get-only `EForgeCapability[] Capabilities`; methods outside an implementation’s set throw.** A conformance test asserts the array and the behaviour agree, in both directions. | **DECIDED** — owner, this session. Reverses part of D15 |
| **D18** | **Vocabulary: "forge", not "platform".** The thing we integrate with is a *forge*; bare *provider* is reserved for ASP.NET Identity's external login provider, with `EForgeProvider` the qualified name. | **DECIDED** — owner, this session: *"Use Forge wherever you like."* Everything M1/M2 shipped keeps its name; only D16/D17's identifiers changed. No id, URL or route consequence |
| **D19** | **Suspended installations must stop conferring management rights.** The owner set is built from installations unfiltered (`GitHubAccessService.cs:105-109`) while the backfill already filters `!i.Suspended` (`:235`). Same array, one site filters. | **DECIDED** — owner, this session: fix in this PR. Carried by M2a |
| **D20** | **An upload that cannot happen reports `Neutral`, never a failure.** `fail-ci-if-error` stays `false`. | **DECIDED** — owner, this session: *"instead of a check, we can report neutral. But no error, that would be intrusive."* ⚠️ GitHub has a first-class `neutral`; GitLab and Bitbucket do not (§5.3) |
| **D21** | **Webhook recipients take a neutral `ForgeWebhookMessage<T>` carrying a normalised domain event; normalisation lives in each forge library. Forge-specific messages remain for events only one forge has.** One handler method, and adding a forge edits no consumer. | **DECIDED** — owner, this session: *"that design looks great."* ⚠️ Enlarges M8. See §6.11 |

---

## 3. Goals

1. No caller outside a provider implementation references Octokit types or `api.github.com`.
2. A user signs in from a **login page** that lists providers the server reports, not a hardcoded
   GitHub button.
3. A user who already has an account can attach a second provider's identity to it, under a policy
   the framework exposes and the app chooses.
4. Document ids, routes and badge URLs name their provider explicitly and cannot collide across
   forges. Badge URLs are **replaced at their source**, not aliased (§5.4).
5. Adding GitLab in stage 2 is writing a provider, not re-opening the data model.

### Non-goals

- Implementing the GitLab or Bitbucket providers (stage 2 and 3).
- GitHub Projects v2 automation on other forges (D8) — no equivalent primitive exists.
- Bitbucket Data Center / Server (D10).
- Cross-provider aggregate coverage numbers (§5.4) — coverage does not sum across unrelated orgs.
- Account *merge* (two existing accounts, one per provider, becoming one). Linking a provider to an
  account is in scope; merging two populated accounts is not.

---

## 4. Findings — sign-in and identity

### 4.1 The login page already exists; nothing links to it

`ClientApp/src/app/app.routes.ts:31` already mounts
`...sparkAuthRoutes(withExternalLogin(githubProvider()))`, which publishes `SparkSignInComponent` at
`/sign-in`. That component **already renders one button per server-reported provider**
(`ng-spark-auth/sign-in/src/spark-sign-in.component.ts:105-146`), and `githubProvider()` is purely
presentational — `externalProvider('GitHub', { iconClass: 'bi bi-github' })`.

The visible button bypasses all of it. `shell.component.html:24-26` calls a bespoke
`GitHubLoginService.login(HOME_URL)` (`services/github-login.service.ts:43`) which hardcodes the
scheme string `'GitHub'`. The same bespoke path is used by the "Reconnect GitHub" banner
(`spark/home-extras.component.ts:95`).

**So D3 is mostly deletion.** The work is removing the bespoke service, linking the mounted route,
and generalising the error copy — not building a login page.

**Answering "should the provider list come from the Identity configuration?" — it already does, and
that is not incidental.** `GET /spark/auth/capabilities`
(`Endpoints/GetAuthCapabilities.cs:43`) returns every registered ASP.NET `AuthenticationScheme` with
a non-empty `DisplayName`, minus Identity's own five
(`Extensions/ExternalAuthenticationSchemes.cs:73-91`). No config schema enumerates providers
anywhere in Spark. Adding GitLab is a registration line.

**Disabling local (email/password) login is also already built, and CodeCoverage already uses it.**
Measured 2026-09-19, because it was raised as a requirement:

- `SparkAuthenticationOptions.LocalCredentials` is a `SparkLocalCredentials` enum —
  `Full` / `SignInOnly` / `Disabled` — and its **default is already `Disabled`**
  (`Configuration/SparkAuthenticationOptions.cs:43`, `SparkLocalCredentials.cs:27-43`).
- Endpoints are removed, not merely hidden: `LocalCredentialEndpointFilter.cs:123-130` strips
  `/register` and `/resendConfirmationEmail` in both non-`Full` modes, and `/confirmEmail` in
  `Disabled`.
- The login page hides the fields: `spark-sign-in.component.html:30` gates the email/password block
  behind `@if (localCredentialsAvailable() && routePaths.login)`, set from the capability report at
  `spark-sign-in.component.ts:137`.
- The client cannot disagree with the server about this, because `localCredentials` is **derived
  from the live route table**, not from options (`Endpoints/GetAuthCapabilities.cs:32-41`): no
  `/login` route → `Disabled`, no `/register` → `SignInOnly`, else `Full`.
- CodeCoverage passes no `configure` at `Program.cs:87`, so it runs `Disabled` today — which is why
  `app.routes.ts:27-30` records "GitHub is the only provider — the server's LocalCredentials are
  Disabled".

**No milestone is needed for this.** The one adjacent gap is that `Disabled` also removes
`/confirmEmail`, which §6.3 needs back once external-login confirmation exists — see M4.

Two limits of the capability payload, both of which stage 1 should address:
- it carries `{ scheme, displayName }` and nothing else — no icon, no order, no localized label, and
  **no "already linked to the current user" flag**, which the manage-logins UI needs;
- ordering is `IAuthenticationSchemeProvider` registration order, not a declared order.

### 4.2 A live defect that lands exactly on this feature

`app.config.ts:31` calls `provideSparkAuth()` with no arguments, leaving `loginUrl` at its default
`'/login'` (`ng-spark-auth/models/src/auth-config.ts:11-15`), while the only mounted auth route is
`/sign-in`. The auth guard (`guards/src/spark-auth.guard.ts:15`), the 401 interceptor
(`interceptors/src/spark-auth.interceptor.ts:26`) and the auth bar all redirect to `loginUrl` — i.e.
to a route that does not exist. Dev-mode warning only. ⚠️ Not confirmed in a browser.

This is a fix, not a follow-up: stage 1 introduces the login page those redirects are supposed to
reach.

### 4.3 The three sign-in cases, measured against what exists

The desired flow is: (1) no user with that email → create, send confirmation, link; (2) user exists
unconfirmed → resend; (3) user exists confirmed → sign in.

This is indeed stock ASP.NET Core Identity. **None of it is wired up in this codebase.**

| Case | Today |
|---|---|
| (1) | User **is** created and signed in immediately. `SparkAuthenticationExtensions.cs:178` sets `user.EmailConfirmed = true` by fiat. **No confirmation mail is sent** — there is nothing to send it with. |
| (2) | **Unreachable.** The unconfirmed-user state cannot be entered via external login, because of the line above. |
| (3) | Works — *if the login is already linked*. If it is not, this is not a sign-in at all; it is case (2)'s failure. |

The supporting gaps:

- **No `IEmailSender` or `IEmailSender<TUser>` is registered anywhere in the repository** — no mail
  package, no SMTP config, no templates, in Spark or in CodeCoverage. A grep for
  `IEmailSender|SmtpClient|MailKit|System.Net.Mail` returns zero hits outside `docs/`. This is
  issue **#299**, deferred as "a feature, not a fix". D2 un-defers it.
- **Nothing calls `GenerateEmailConfirmationTokenAsync`** anywhere in Spark.
- CodeCoverage runs `LocalCredentials = Disabled`, and `LocalCredentialEndpointFilter.cs:126-130`
  **removes `/confirmEmail` entirely in that mode**; `:123-124` removes `/resendConfirmationEmail`
  in both non-`Full` modes. Case (2) has no endpoint to call even in principle.
- `SignIn.RequireConfirmedAccount` / `RequireConfirmedEmail` are set nowhere, and CodeCoverage passes
  no `configureIdentity` at all (`Program.cs:87`).

**Measured, because it is a silent-failure trap:** in
`C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App\10.0.12\Microsoft.AspNetCore.Identity.dll`
— the framework version this repo targets — both `NoOpEmailSender` and `DefaultMessageEmailSender`
are present. `AddApiEndpoints` `TryAdd`s the no-op sender, so **a "send confirmation email" step
will report success and send nothing** until a real sender is registered. Nothing logs an error.
This is why §8 requires an acceptance criterion that a mail *arrives*, not that a call returned, and
why §6.4 requires a startup guard.

### 4.4 Linking is a hard failure today, not a missing feature

Email uniqueness is enforced by the **store**, not by `IdentityOptions`: `UserStore.CreateAsync`
reserves the normalized email through a RavenDB compare-exchange key and returns
`IdentityError { Code = "DuplicateEmail" }` on collision (`Identity/UserStore.cs:68-95`).
`IdentityOptions.User.RequireUniqueEmail` is never set anywhere, and is irrelevant.

So a second provider presenting an email that already exists does not fall through to a link. It
hard-fails, and the callback returns `account_creation_failed`
(`SparkAuthenticationExtensions.cs:181-182`), whose client copy reads *"Signing in worked but
creating your local account failed — check the server logs"*
(`services/github-login.service.ts:30`) — actively misleading for precisely the case D2 addresses.

`AddLoginAsync` is called from exactly one site in product code
(`SparkAuthenticationExtensions.cs:184`), inside new-user provisioning. There is no
`/manage/logins`, no link-to-signed-in-principal path, and no UI in `ng-spark-auth`.

### 4.5 A second defect in the same method

A non-`Success` `ExternalLoginSignInAsync` result for an **already-linked** user falls through into
auto-provisioning (`SparkAuthenticationExtensions.cs:143-152`), because that is the only other
branch. So lockout, not-allowed and 2FA-required all surface as `account_creation_failed`, and the
token save at `:188-196` is skipped. Already recorded at `reauth-on-401.md:80-84`, never fixed.
Stage 1 fixes it, because D2's linking branch has to be added to exactly this decision point.

Also at that site: `AddLoginAsync` and `SignInAsync` results are not checked (`:184-185`), so a store
failure produces a "success" outcome; and the challenge endpoint does not validate the `provider`
query value against registered schemes (`:99-122`).

### 4.6 The verified-email gate blocks every new provider

Auto-provisioning requires `email_verified == "true"` **or** the literal
`urn:github:email_verified == "true"` (`SparkAuthenticationExtensions.cs:159-168`); otherwise the
flow ends with `email_not_verified` and no user is created. GitHub earns its claim through a bespoke
`/user/emails` call in `GitHubAuthenticationExtensions.cs:54-110`.

Until a new provider either emits a standard `email_verified` or gets the same treatment, **every
first-time sign-in on that provider is refused**. GitLab is fine (real OIDC, standard claim).
Bitbucket is the doubtful one: it exposes `is_confirmed` via `/2.0/user/emails` rather than as a
claim. ⚠️

### 4.7 The MintPlayer reference implementation

`C:\Repos\MintPlayer\MintPlayer.Web` has a working five-provider linking implementation (Facebook,
Microsoft, Google, Twitter, LinkedIn) on EF Core Identity. It is the reference for D2's
*link-while-signed-in* mode. What it does:

- Two parallel route families distinguished **by URL alone** — `Account/connect/{medium}/{provider}`
  signs in, `Account/add/{medium}/{provider}` links (`AccountController.cs:346-353`, `:478-491`).
  The bodies are identical but for the callback route name; the "this is a link" marker is the
  callback route plus `[Authorize]` on both add actions.
- The link callback calls `AddLoginAsync` on the signed-in principal
  (`AccountRepository.cs:306-323`). **No email, no interstitial, no re-authentication.** Trust rests
  on holding a valid auth cookie *and* having just completed the provider's OAuth round trip.
- Provider discovery is server-derived (`GetExternalAuthenticationSchemesAsync`,
  `AccountRepository.cs:204-214`) — but the UI hardcodes one Angular component per provider and uses
  the dynamic list only to `*ngIf` blocks away.

What **not** to copy, all confirmed in that codebase:

1. **No last-login guard on unlink** (`AccountRepository.cs:325-338`). A user provisioned through
   OAuth is passwordless and can remove their only login, locking themselves out permanently. The
   `HasPasswordAsync` building block exists and is even exposed to the SPA — it is simply never
   consulted.
2. **`EmailConfirmed = true` on auto-provision** (`:239`) regardless of provider verification —
   the same defect Spark has.
3. **Dead end when the email belongs to a local account** (`:259-263`): safe, but the user is told
   *"Please login with your {provider} account instead"* with no path forward.
4. Blanket `catch (Exception)` renders `LoginAlreadyAssociated` and a network blip identically.
5. Provider identity keyed off `DisplayName` rather than scheme name, then used as a route value.
6. Mail layer: `Task.Run` + `System.Net.Mail` + interpolated HTML bodies + swallowed failures.

---

## 5. Findings — the forge coupling

### 5.1 What CodeCoverage actually does with GitHub

18 distinct integration points. The load-bearing ones: GitHub **App** install (not plain OAuth,
`MyAccountsService.cs:30-34`); live `GET /user/installations` per request behind a 5-minute cache
(`GitHubAccessService.cs:119`); seven webhook event types (`GitHubEventsRecipient.cs:41-65`); two
**Check Runs** named `coverage/project` and `coverage/patch`, updated by stored id, with
success/failure/**neutral** conclusions (`PublishFeedbackRecipient.cs:75-160`); a sticky PR comment
published *after* the checks so it can never contradict them; badges with a per-PR HMAC capability
so the bot can embed a working image in a private repo's comment (`Badges/BadgePrSignature.cs`);
upload auth by stored `covt_` token **or** Actions OIDC whose claims override the request body
(`GitHubOidc.cs`, `UploadsController.cs:116-155`); file content and diff/compare for patch coverage;
`coverage.yml` read from the repo; Projects v2 GraphQL automation; and a periodic state reconciler.

**Retracted claim.** An earlier draft of this document called `Repository.DeleteBranchOnPrClose` a
dead flag. **That is wrong.** The feature is live and fully implemented:
`GitHubEventsRecipient.cs:393-465` resolves `Inherit` against the account (`:403-408`), skips forks
whose head lives in a repository we have no write access to, and calls
`client.Git.Reference.Delete(owner, name, "heads/{ref}")`, with distinct handling for 403 (an
unaccepted `contents: write` permission) and 404. What #369 removed was the *WebhooksDemo* recipient;
the implementation moved onto `Repository` in **#382**. It is ordinary GitHub behaviour to port.

One subtlety for stage 2, stated correctly: GitHub's own `delete_branch_on_merge` does the same job
and `:455` names the race between the two — but it is a **repository** setting, not an organization
one. Verified 2026-09-19 against the live API: `GET /orgs/MintPlayer` exposes no such field (its only
branch/merge-adjacent keys are `default_repository_branch`, `members_can_delete_issues`,
`members_can_delete_repositories`), while `GET /repos/MintPlayer/MintPlayer.Spark` returns
`delete_branch_on_merge: false`. So on this repository the race does not arise at all, and an
observed deletion is our implementation. GitLab and Bitbucket both have an equivalent
**repository/project**-level setting, and a port must decide whether we act when the forge already
will — per repository, not per org.

### 5.2 Client-side coupling is narrower than expected, except in two places

Only 12 files under `ClientApp/src` mention GitHub at all, and there is exactly **one** `github.com`
literal (`spark/home-extras.component.ts:59`, a per-environment fallback overwritten from
`/api/me/accounts`). No `api.github.com`, no forge commit/PR deep links — navigation is entirely
internal.

The two real concentrations:

- **The setup panel is GitHub Actions and nothing else.** `repo-setup-panel.component.ts` emits a
  workflow across seven language tabs — `uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@…`
  (`:65-70`), `permissions: id-token: write` (`:71-86`), `actions/setup-*` pins (`:93-187`), and
  "store it as a repository secret" copy (`:30`). There is no GitLab CI or Bitbucket Pipelines
  equivalent to switch to.
- **Two-segment `{owner}/{repo}` is assumed everywhere.** `po-detail-page.component.ts:56-61`,
  `commit-files-extras.component.ts:42` and `short-sha-renderer.component.ts:48` all
  `fullName.split('/')` destructured as `[owner, name]` — a three-segment path silently drops the
  tail and resolves to a **wrong repo**, with no error. Routes `r/:owner/:repo/c/:sha` are
  fixed-arity (`app.routes.ts:36-40`), and `repo-badge-panel.component.ts:98-106` builds
  `/badge/{owner}/{name}.svg`.

Also GitHub-shaped and client-visible: `AccountsResponse.gitHubAppUrl` and `gitHubReauthRequired`
(`services/accounts.service.ts:14-24`), `BuildInfo.runId/runAttempt/workflowName`
(`browse.service.ts:47-56`), the "GitHub App installed" badge renderer, and 7 of 13 `app.*` keys in
`App_Data/translations.json`. Six of those keys are **dead** — unreferenced in `src/`.

**A trap for the id migration:** Spark persistent objects are read by *string name* client-side
(`valueFor(item, 'GitHubId')`). Renaming a model attribute produces **no TypeScript error** — it
silently returns `undefined`. `repo-name-renderer.component.ts:11-19` documents this having already
happened once.

### 5.3 Feature parity — the API surface is not the problem

Every feature has an equivalent on both forges, and GitLab beats us in two places. The mismatches
that change the design:

| | GitLab SaaS | Bitbucket Cloud |
|---|---|---|
| Install analogue | **None.** Closest is a user OAuth grant + a group access token (Owner role, creates a bot user, you schedule rotation) | Forge app + Forge Remote. Connect is retiring; **app passwords were removed 2026-07-28** |
| Who owns the grant | A **user** — the grant dies with that user's account | Workspace (Forge) or user (OAuth) |
| Enumerating orgs | `GET /groups?min_access_level=40` — a **membership list, not a grant** | `GET /workspaces?role=…` ⚠️ |
| Identity | **Real OIDC.** Discovery at `gitlab.com/.well-known/openid-configuration`; standard `email_verified`; plus a `groups/owner` claim | OAuth 2.0; verification via `/2.0/user/emails` `is_confirmed`, ⚠️ not a claim |
| Coverage result | Commit status with a **native `coverage` float**; `description`/`target_url` ≤255 chars; 409 on concurrent same-sha update | Code Insights `PUT …/reports/{reportId}`, idempotent, `COVERAGE` type, **data capped at 10 elements** |
| Verdict vocabulary | pending/running/success/failed/canceled/skipped | SUCCESSFUL/FAILED/INPROGRESS/STOPPED |
| Webhook auth | **`X-Gitlab-Token`, a shared secret compared verbatim** — the credential is the header. Real HMAC only from **19.0** | **HMAC `X-Hub-Signature: sha256=`** — same shape as GitHub |
| Webhook scope | Per project on Free; **group webhooks are Premium/Ultimate only** | Per repository or per workspace |
| Lifecycle events | **None** — no install concept, so revocation is discovered only when a call fails | Forge install/uninstall events ⚠️ |
| Tokenless upload | **CI `id_tokens`** — audience-scoped RS256, verified offline. Maps 1:1 onto `GitHubOidc.cs` | ⚠️ **Unproven.** Assume a stored token |
| Path shape | Namespaces nest **up to 20 levels** | Two segments, but **slugs are renameable and reusable** — key on brace-wrapped UUIDs |
| Rate limit | Per user ⚠️ | **1,000 req/h per token** (to ~10k with paid seats) ⚠️ — forces batching |
| Projects v2 | No equivalent | No equivalent |

Three consequences worth naming now, because they shape stage-1 abstractions:

1. **`neutral` has no home outside GitHub.** `IForgeFeedbackPublisher` must define the verdict
   vocabulary as the *intersection plus an explicit mapping*, not pass GitHub's enum through.
2. **The markdown coverage table has nowhere to go** on either forge (255 chars / 10 cells). The
   sticky comment carries that weight everywhere; the commit status carries a number and a link.
3. **`GitHubStateReconciler` stops being a safety net.** With no lifecycle events on GitLab, periodic
   reconciliation becomes the only way to learn about revocation.

### 5.4 D4 — one merged list, or provider-scoped? (recommend: scoped)

**Every comparable product scopes by provider, three of four by putting it in the URL.**

| Product | Behaviour |
|---|---|
| Codecov | Provider is the first path segment: `app.codecov.io/gh`, `/gl`, `/bb`. Linking a second provider is an explicit action |
| Coveralls | Provider is in the **badge** URL: `coveralls.io/repos/github/{owner}/{repo}/badge.svg` |
| SonarQube Cloud | Forbids it outright: "does not support linking an organization to more than one DevOps platform… you will need to create a separate organization" |
| Qlty Cloud | GitHub only ⚠️ |

The decisive argument is badges. **A badge URL is pasted into a README and is then permanent and
public.** `coverage.mintplayer.com/badge/{owner}/{name}.svg` becomes ambiguous the instant a second
provider exists — and it resolves *silently to the wrong repo*, not to an error. `mintplayer` on
GitHub and `mintplayer` on GitLab are unrelated principals and anyone can register the free one.

Three supporting arguments: every route is already two-slot, so a merged view forces a third value
into it and ends up reinventing the path segment badly; document ids need the provider regardless
(§5.5), so it is already a first-class axis and hiding it is concealment rather than simplification;
and each provider's "who may manage this org" rule is genuinely different, so a merged table would
put three authorization meanings in one column with no way for the user to tell which is which.

**Decided (D4): provider-scoped, and the sidebar carries it.** After sign-in the user sees **one
program unit per provider** (GitHub, GitLab, Bitbucket) rather than a unioned list. Each unit owns
its own account list, its own repositories and its own authorization answer. This is the strongest
available form of the recommendation below: the provider is not merely a URL segment, it is the
navigational top level, so no screen ever has to render three different meanings of "you may manage
this" in one table.

**Authorization is kept separate per platform.** A GitHub decision never consults GitLab state and
vice versa. ⚠️ Whether the current code *forces* any union is being inventoried; the known risk is
`IForgeAccessService` being given a single cross-provider allowed-owner set instead of one set per
provider. The interface must be per-provider by construction (§6.5), or the separation is lost at
the seam even though the UI looks scoped.

**Routes (D11).** Routes become `/{provider}/{owner}/{name}` with the **full provider name** —
`github`, `gitlab`, `bitbucket` — not Codecov's `gh`/`gl`/`bb`. These strings land in badge URLs that
are pasted into public READMEs and can never be revisited, so legibility beats brevity:

```
coverage.mintplayer.com/badge/github/MintPlayer/MintPlayer.Spark.svg
coverage.mintplayer.com/github/MintPlayer/MintPlayer.Spark
```

Badges take the provider segment from day one. The same spelling is used in document ids (D5), so
there is one provider vocabulary, not two.

**No backward-compatible alias is kept** (user decision, this session): the legacy two-segment
`/badge/{owner}/{name}.svg` route is **removed**, not aliased, and the badge URLs are replaced at
their source. This is sound here specifically because the deployment has no external users — every
README carrying one of these badges is ours. It would be the wrong call on a public multi-tenant
service.

Two consequences to accept knowingly, both cosmetic: badge images embedded in **existing PR
comments** will 404 for old pull requests, and any badge cached by GitHub's image proxy will serve
until it expires. Neither affects a live coverage result.

The org picker is per provider. One account still holds several provider logins, with a switcher in
the chrome. No cross-provider aggregates.

### 5.5 D6 — the authorization model is the hard part

`GitHubAccessService` derives the allowed-owner set by calling `GET /user/installations` live with
the viewer's own token (`:119`). The installation is simultaneously **the credential and the
authorization grant**, and `product-overview.md:157` states this as intent ("No parallel permission
system"), `:170` as policy ("GitHub is the authority; no join workflow — GitHub membership *is* the
approval").

GitLab has no installation. `GET /groups?min_access_level=40` returns **what the user can see, not
what the user asked us to manage**. Treating them as equivalent would silently widen access — which
is exactly the thing `:157` forbids.

So each provider needs an explicit *connected org* record: an artifact of someone deliberately
connecting an org, distinct from the membership list used to decide who may administer it. That is
not a parallel permission system — the forge still decides *who*; we record *whether the org opted
in*. GitHub's installation already **is** that record; stage 1 makes it explicit rather than
inferred, which is what lets stage 2 supply a different one.

The saving grace: the visibility predicates are already centralised in
`RepositoryVisibility.cs:35-44,76-87` and `GitHubProjectVisibility.cs:45-56`, consumed via
`SparkVisibility.cs:19-36`. The policy is in one place; only its *source* is hardcoded.

⚠️ A related hazard recorded in `product-overview.md:170`: management rights today equal installation
visibility, with **no admin-role check**. Generalising must not silently widen that; each provider
needs an explicit answer to "who may manage this account's tokens and settings".

### 5.6 Measured: exactly what is forge-coupled, and what is not

A full inventory of every authorization/visibility decision (2026-09-19). **The conclusion is that
per-platform separation is achievable, and that nothing today forces a union** — the decision shape
is uniformly "is this row's owner in the caller's owner set", which composes per-platform cleanly.
Only one call actually talks to GitHub: `GitHubAccessService.GetVisibilityAsync:30-88`. Everything
downstream consumes a `string[]`.

**Three things would silently union two platforms if left unchanged:**

1. **The owner set is an unqualified `string[]` of logins.** `GitHubAccessService.cs:80-84` produces
   bare logins (installation account logins ∪ the user's own), compared against `Repository.OwnerLogin`
   and `Account.Login` by `RepositoryVisibility.cs:60-61`, `GitHubProjectVisibility.cs:219-221`,
   `AccountActions.cs:47`, `ApiTokenActions.cs:51`, `RepositoryActions.cs:149,181` and
   `MyAccountsService.cs:44,53`. **A GitLab group named `microsoft` and a GitHub org named
   `microsoft` are the same string.** This is the single place where two platforms would merge into
   one answer, and it does so silently and in the *permissive* direction.
2. **Document ids encode a bare numeric id — inside an authorization check.**
   `BuildActions.RepositoryIdFromCommitId:31-37` *parses* the `Commits/{repoGitHubId}/{sha}` shape to
   decide access, and `UploadsController.cs:642,647` compares against `Repositories/{gitHubId}`. D5's
   re-keying is therefore not only a storage change; it lands in an authz path.
3. **The cache key is per-user, not per-(user, platform)**: `github-owners/{user.Id}`
   (`GitHubAccessService.cs:40`, 5-minute TTL at `:25,86`).

**What needs no change at all:**

- **`security.json` is not forge-aware in any way.** It has exactly two groups — `anonymous` and
  `authenticated` — and every right is a plain role grant. No per-owner group, no admin group. Every
  forge concept lives in row filters and imperative checks, which `RepositoryActions.cs:37-41` states
  outright ("rights here are group-level and there is no group per GitHub owner").
- **The visibility predicates are already platform-neutral.** `RepositoryVisibility` and
  `GitHubProjectVisibility` read only local document fields (`IsPrivate`, `OwnerLogin`, `Connection`);
  the GitHub-ness is entirely in the *source* of the owner set.
- **The two upload credential paths are already separate schemes**, selected by
  `[Authorize(AuthenticationSchemes=…)]` (`UploadsController.cs:32`) and dispatched by claim presence
  (`:623`). A third is additive.
- **There are no `ISparkRowRule<T>` implementations in the app** — row rules are the `*Actions`
  `GetRowFilterAsync` overrides, driven by the framework's open-generic `SparkRowRule`.

**Genuinely per-platform work** (from the classification table): building the owner set; user-token
refresh; the installation backfill; `MyAccountRow` generation (it emits `IsAppInstalled` and an
"install the App" URL); webhook ingestion trust; the sign-in provider; and the whole OIDC upload path
including its claim overrides and public-repo auto-provisioning.

**Two live findings outside this PRD's scope, recorded because the inventory surfaced them:**

- ⚠️ **A suspended installation still confers full management rights.**
  `GitHubInstallation.Suspended` is honoured only in the backfill (`GitHubAccessService.cs:185`) and
  **not** when computing the owner set (`:80-84`). Observed in code; not tested.
- ⚠️ **The "no admin-role check" claim is confirmed.** Nothing anywhere reads a GitHub role,
  permission level, `role_name`, org-admin flag, repository `permissions` object or collaborator
  status. `CanManageOwnerAsync` (`SparkVisibility.cs:25-26`) is literally `IsOwnerAllowedAsync`. Any
  user who can see an org's installation is a full manager of every repository under that owner —
  able to mint upload tokens, rotate badge tokens and delete coverage data.

Two smaller ones: `SyncColumnsAction:42-60` performs no explicit ownership check and relies entirely
on the row filter having refused the load; and `BrowseController.GetAccount:240-248` applies no
visibility check at all (consistent with `QueryRead/Account` being anonymous, but it is the one
browse endpoint with no row scoping).

---

### 5.7 Measured: the seams built in M1/M2 are not yet load-bearing

Investigated 2026-09-20, when the owner proposed per-provider assemblies. Three findings, all
verified directly against the branch. Two of them are defects in work this branch already committed,
so they are recorded here rather than quietly fixed.

**(a) Two of the three seams cannot dispatch at all.** `IForgeClient` and `IForgeFeedbackPublisher`
both declare `EForgeProvider Provider { get; }` — and nothing reads it. Every consumer injects the
**singular** interface; there is no `IEnumerable<IForgeClient>` anywhere:

| Site | Interface |
|---|---|
| `Controllers/BrowseController.cs:38` | `IForgeClient` |
| `Feedback/PublishFeedbackRecipient.cs:25` | `IForgeFeedbackPublisher` |
| `Feedback/PublishFeedbackRecipient.cs:26` | `IForgeClient` |
| `Ingestion/CommitAssembler.cs:24` | `IForgeClient` |
| `Ingestion/FinalizeBuildRecipient.cs:12` | `IForgeClient` |
| `Ingestion/FinalizeBuildsCronJob.cs:20` | `IForgeClient` |
| `Services/BaseResolver.cs:14` | `IForgeClient` |
| `Ingestion/BuildFinalizer.cs:14` | parameter — inherits its caller's resolution |
| `Ingestion/PatchCoverageCalculator.cs:17` | parameter — inherits its caller's resolution |

`[Register]` emits plain `AddScoped` (verified from generator source and a checked-in generated
artifact — see §6.9), so registrations **stack** rather than dedupe, and MS.DI hands a singular
injection the **last** one registered. Adding `GitLabForgeClient` would therefore not add a provider;
it would silently redirect all nine sites to GitLab, including every GitHub status publish.

This is the failure mode §5.6 warned about — a silent widening — arriving through DI rather than
through an unqualified string. It is worse than the string case because it is invisible in the type
system: the code reads as provider-neutral and compiles either way.

**(b) The access seam is bypassed by five of its six consumers.** `IForgeAccessResolver` is used in
exactly one place, `Services/SparkVisibility.cs:13`. These still inject `IGitHubAccessService`
directly, naming the provider in their own field type:

`Controllers/BrowseController.cs:37` · `Controllers/MeController.cs:25` ·
`Controllers/RepoSettingsController.cs:23` · `CustomActions/ResyncAction.cs:35` ·
`Services/MyAccountsService.cs:15`

So M1's as-built note — that the owner set now goes through a per-forge interface — is true of the
row-level visibility path and of nothing else. The interface exists; the migration to it does not.

**(c) Five capability areas were never seamed.** The three seams cover diff/content reads, the
allowed-owner lookup, and status/comment publishing. Untouched, and each still wholly GitHub-shaped:
**webhook ingestion and event normalisation**, **App-installation management**, **org/account
reconciliation**, **Projects V2 board automation**, and **CI identity (OIDC upload credentials)**.
Eleven app files import `MintPlayer.Spark.Webhooks.GitHub.Services` for installation-token minting.

**What this changes.** Nothing about the design in §6.5 — the seams are the right seams. But the exit
criterion A1 is further away than the plan implied, and the honest statement of where the work stands
is: *one* of three seams dispatches, on *one* of six call paths, over *three* of eight capability
areas. The sub-milestones in the plan (M2a, M2b) exist to close that, and they are prerequisites for
any provider assembly — a second implementation added before (a) is fixed is actively unsafe.

**The fix, and its one dependency.** *(Owner, 2026-09-20: "Yes we just need to inject IEnumerable.")*
⚠️ The first draft of this paragraph gave each seam its own resolver; **D16 replaced the three seams
with one `IForgeIntegration`**, so there is one selection helper, not three (§6.10). Everything
below about *selection* is unchanged by that — inject the `IEnumerable`, select on the discriminator.
But selecting requires knowing *which* provider a
given `Repository` belongs to, and no entity carries a provider discriminator until M6 re-keys the
documents (D5/D7): `grep ForgeOwner|EForgeProvider` across `CodeCoverage.Library/Entities` returns
zero hits today. So until M6 lands, the selector resolves to `EForgeProvider.GitHub` for every
repository. That fallback must be **one explicit, commented line in the resolver** — not a default
argument, not a `FirstOrDefault()` that happens to pick GitHub because it is the only registration.
The difference matters: the explicit version starts throwing the moment a second provider is
registered before M6, which is exactly when a silent fallback would start handing GitHub
repositories to the GitLab client.

Related: `ForgeAccessResolver.For` uses `FirstOrDefault`, so a duplicate registration of the same
provider is tolerated silently. `Services/ActionsResolver.cs:145-155` throws on more than one match,
after a bug that made the case for doing so. The new resolvers should be loud in the same way.

---

## 6. Design

### 6.1 Sign-in (D3)

Delete `GitHubLoginService` and the shell's bespoke button. The shell links to `/sign-in`, which is
already mounted. `provideSparkAuth()` gets a `loginUrl: '/sign-in'` so guards and the 401 interceptor
land somewhere real (§4.2). `githubProvider()` stays as decoration and is joined by `gitlabProvider()`
/ `bitbucketProvider()` in later stages; the *list* still comes from the server.

The capability payload gains optional presentation and state metadata — a declared order, and a
`linked` flag when the request is authenticated — so the same endpoint drives both the login page and
the manage-logins screen.

### 6.2 Account linking (D2) — a Spark option with two modes

A second property on `SparkAuthenticationOptions`, which today has exactly one (`LocalCredentials`,
`Configuration/SparkAuthenticationOptions.cs:193`):

```
ExternalLoginLinking = Disabled | WhenSignedIn | ConfirmByEmail
```

CodeCoverage sets `ConfirmByEmail`.

Both modes hang off the **same decision point** — the duplicate-email branch that today hard-fails
(§4.4). That branch becomes: *this email belongs to an existing user* → dispatch by mode.

**`WhenSignedIn`** (the MintPlayer shape, §4.7): a link/unlink endpoint pair for the current
principal, `AddLoginAsync` on a signed-in user, plus manage-logins UI in `ng-spark-auth`. Carries a
**last-login guard** — the bug §4.7 says not to inherit. Distinguishes `LoginAlreadyAssociated` from
transport failure with distinct error codes.

**`ConfirmByEmail`**: the callback does not sign the user in. It writes a short-lived pending-link
record — `(userId, loginProvider, providerKey, expiresAt)`, keyed by a single-use token — and mails
a confirmation. Clicking it verifies the token, re-checks that the `providerKey` still matches what
was captured, calls `AddLoginAsync`, and signs in.

Two rules that make this mode safe, and without which it is an account-takeover vector:

1. **The mail goes to the address already stored on the existing account, never to the address the
   new provider just asserted.** This is what makes the mode independent of whether the provider's
   email claim can be trusted — which matters most on Bitbucket (§4.6).
2. **The `providerKey` is captured at callback time and re-verified on confirm.** Otherwise the
   confirmation link is a bearer token that attaches whatever provider identity is presented later.

The pending-link record is a new document; `SparkUserLogin` is only
`{ LoginProvider, ProviderKey, ProviderDisplayName }` and is not a place to park an unconfirmed state.

### 6.3 Provisioning and confirmation (the three cases)

`SparkAuthenticationExtensions.cs:178` stops setting `EmailConfirmed = true` by fiat. The flag
reflects reality: true when the provider attested it *and* the app trusts that provider's attestation,
false otherwise — which is what finally makes case (2) reachable.

Case (1) creates the user, sends confirmation, links the login. Case (2) resends. Case (3) signs in.
Whether an unconfirmed user may sign in meanwhile is `SignIn.RequireConfirmedAccount`, which Spark
now exposes deliberately rather than leaving unset.

The verified-email gate (§4.6) stops naming GitHub. Each provider extension normalises to the
standard `email_verified` claim; `GitHubAuthenticationExtensions` keeps its `/user/emails` call and
emits the standard claim instead of `urn:github:email_verified`.

**Confirmation must stop being tied to local credentials.** `LocalCredentialEndpointFilter.cs`
treats `/confirmEmail` and `/resendConfirmationEmail` as part of the "password recovery" family and
removes them in `Disabled` (`:126-130`) and `:123-124` respectively. That grouping was correct when
confirmation only ever followed a local registration. It is wrong once an *external* login can
produce an unconfirmed user: CodeCoverage runs `Disabled`, so the very endpoints case (2) needs are
the ones that get stripped.

Confirmation therefore becomes orthogonal to `LocalCredentials`. Spark keeps the confirm and resend
surfaces available whenever confirmation is required by configuration, regardless of whether local
password endpoints exist. Note the second-order effect: `GetAuthCapabilities` derives
`localCredentials` from the *presence of routes* (`:32-41`), so the filter and the capability report
must be changed together or the client will start reporting the wrong mode.

### 6.4 Mail (D9)

**Spark ships the contract and the templates; the app ships the transport.** Spark depends on
`IEmailSender<TUser>` (already in the framework) and must never register a transport. CodeCoverage
registers a real sender.

**Required: a startup guard.** If `ExternalLoginLinking == ConfirmByEmail` (or confirmation is
required) and the resolved `IEmailSender<TUser>` is the framework's no-op, **throw at startup**.
Measured in §4.3: without this, the feature silently sends nothing in production and logs nothing.
Precedent for the style of guard: `LocalCredentialEndpointFilter.cs:87-98`.

Not to be copied from MintPlayer: `Task.Run` + `System.Net.Mail` + interpolated HTML + swallowed
failures (§4.7). Bodies belong in templates, and the app is already fully localized through
`App_Data/translations.json`.

### 6.5 Forge abstractions

> ⚠️ **Superseded by §6.10 (D16).** The owner has since chosen a single
> `IForgeIntegration` interface rather than three typed seams. The seam *boundaries* below were
> right and their signatures carry over; the surface they are exposed through changed. Kept because
> M1/M2 shipped this shape and the as-built notes refer to it.

Three seams, each of which already has a natural boundary:

- **`IForgeAccessService`** — replaces the direct `/user/installations` call; returns the allowed-owner
  set per provider, derived live from the forge on every request (D6a rejected the stored
  connected-org record this section originally proposed). Slots in behind the
  already-centralised visibility predicates.
- **`IForgeClient`** — diff/compare, file content, default branch, branch list, PR/MR head SHA.
- **`IForgeFeedbackPublisher`** — the sticky comment and the commit status / check run, with an
  explicit verdict vocabulary (§5.3) rather than GitHub's enum.

Plus a **provider-qualified identity** (D5/D7) across document ids, the bus contract — which today
declares `required long InstallationId` and `RepositoryFullName`
(`GitHubWebhookMessage.cs:15-16`), so every recipient and the `spark-github-all` queue name depend on
a GitHub-shaped envelope — and routes.

`OwnerLogin`, today a bare string, becomes provider-qualified: `owner/repo` is not unique across
forges.

**Unchanged and not made forge-aware:** Spark core auth, `security.json`, the coverage parsers, the
merge engine, the report reaper.

### 6.6 One git repository, two remotes (D12)

Raised while deciding D5: what if the same working tree is pushed to both GitHub and Bitbucket?

**Recommendation: two independent records, and no attempt to detect or merge them.** The entity is
not "a git repository" — it is *a repository on a forge*. Two remotes means two `Repository`
documents, two badge URLs, two connection states, two authorization answers, and two sets of builds.
The D5 id scheme already expresses this: `Repositories/github/{id}` and
`Repositories/bitbucket/{uuid}` are unrelated documents, which is exactly right.

Why not merge them, despite the temptation that **commit SHAs are genuinely identical across mirrors**
(the same commit object hashes the same everywhere, so coverage data *could* in principle be shared):

- **Authorization would have to be unioned**, and D4 says it must not be. Merging means a viewer
  authorized on the Bitbucket workspace can see coverage produced from the GitHub side, which is a
  silent widening of exactly the kind §5.6 warns about.
- **Everything except the SHA diverges.** PR/MR numbers, build ids, default branch, visibility,
  branch protection and the CI that produced the report are all per-forge. A merged record would have
  to pick a winner for each, and every choice is arbitrary.
- **Detection is unreliable.** The only honest signal is a shared commit history, and "the same SHA
  appears on both" is also true of an unrelated fork, a vendored copy, or a rewritten mirror.
- **Competitors don't merge.** Codecov, Coveralls and SonarQube Cloud all treat a repository on each
  provider as its own project; SonarQube forbids a mixed tenant outright (§5.4).

The honest cost of not merging: a user mirroring to two forges uploads twice and sees two entries
with two coverage numbers, which can legitimately differ if the two CI pipelines run different test
subsets. That is not a defect — it is two pipelines being measured, and reporting one blended number
would be the misleading option.

If a grouping concept is ever wanted, it belongs **above** `Repository` as an explicit,
user-declared link, never as inference — and it is out of scope here.

### 6.7 D6 resolved — the authorization model per provider

Settled by interview on 2026-09-20. The headline: **the allowed-owner set stays derived live from
the forge, per provider. No stored authorization record is introduced.** Each sub-decision below was
taken against the alternatives, and the accepted risk is stated because that is the part that gets
forgotten.

**D6a — live derivation, not a stored grant.** The owner set is computed per request from the
provider's own API (GitHub installations; GitLab `GET /groups?min_access_level=40`; Bitbucket
workspaces by role), exactly as today. Rejected: a stored connected-org record, because the code is
already structured this way — `MyAccountsService.cs:60-73` builds rows straight from the live set and
`IsAppInstalled` is only a display flag, so "connected" is already decoration rather than
authorization.

*Accepted risk:* GitHub's set is `installations ∪ self`, so the install filters out orgs you merely
belong to. GitLab and Bitbucket have no such filter, so their sets are **strictly wider**. This is
tolerable because an unconnected org has no stored documents, so membership in the set grants access
to nothing until someone connects it — at which point "any Maintainer may manage" is the same bargain
`product-overview.md:170` already records for GitHub.

**D6b — derive lazily, per provider, only for providers the user has linked.** A user holding only a
GitHub login must never cost a GitLab API call. This composes with D4: the sidebar is per-provider, so
rendering the GitHub unit needs only GitHub's set. Without this, steady-state call volume triples.

**D6c — cache failures, not just successes.** Today only the successful result is cached
(`GitHubAccessService.cs:86`) and degraded results are deliberately *not*
(`:90-91`) — defensible at one provider, dangerous at three, because an outage then means every
request re-attempts every unreachable provider. Bitbucket's budget is **1,000 req/h per token**, so
failures alone can exhaust it and then keep it exhausted. Failures get a short TTL of their own
(~30s), separate from the 5-minute success TTL. The cache key becomes per-(user, provider) — today
`github-owners/{user.Id}` (`:40`) would collapse two providers into one entry.

**D6d — degraded behaviour is unchanged, and logging becomes a contract obligation.** On an
unreachable provider the set still collapses to the user's own login for that request. Nothing is
added for GitHub, which already logs all three cases (`:65`, `:129`, `:139`) — but `IForgeAccessService`
must *require* logging on degrade rather than trusting each implementation to remember.

*Accepted risk:* private repositories still silently vanish from a listing during an outage, and only
`ReauthRequired` is surfaced to the user (`MyAccountsService.cs:38`). An outage remains
indistinguishable from "you have no orgs".

⚠️ **Revisit candidate (raised 2026-09-20, not acted on).** Implementing D6c's failure cache made a
third option visible that was not considered when this was decided: keep serving the degraded set
**and** surface "this provider is unreachable" in the account list — rather than either hiding it
(the chosen A) or refusing to render (the rejected C). It is strictly better than both, because the
current behaviour now also persists for the 30-second negative-cache window, widening the interval in
which a viewer sees a silently wrong answer. The wiring already exists: `MyAccountsService.cs:38`
surfaces `ReauthRequired` to the client and simply ignores `Unavailable`. Cheap to add; needs an
explicit decision because it changes what users see.

**D6e — the owner set is qualified in the stored field, with a colon.** `Repository.OwnerLogin`,
`Account.Login` and `ApiToken.AccountLogin` (`ApiTokenActions.cs:51` — found via the authorization
inventory, not the id analysis) all become `provider:owner`: `github:mintplayer`,
`gitlab:group/subgroup`.

A colon rather than a slash **specifically because GitLab namespaces nest up to 20 levels and are
themselves slash-delimited**. A colon is not legal in a GitHub login or a GitLab namespace path, so
`IndexOf(':')` is unambiguous at any depth and a malformed value is detectable rather than silently
splitting in the wrong place. ⚠️ This deliberately diverges from the document-id spelling
(`Repositories/github/402741072`, D5), which is safe only because a numeric id contains no slashes.
**The divergence is intentional — do not "tidy" the two into one delimiter.**

Rejected: a separate `Provider` field with a bare `OwnerLogin`. It keeps the field clean for display
but lets a seventh call site write `r.OwnerLogin.In(owners)` without the `Provider ==` clause, which
compiles, passes review, and silently widens access across forges. Qualifying the value makes the
cross-provider match *impossible* rather than merely discouraged.

*Accepted risk:* `OwnerLogin` is no longer directly displayable or routable and needs an accessor;
three field rewrites ride along with the migration (one `PatchByQueryOperation` per collection, so a
partial failure names which collection stopped).

**D6f — fork pull requests: PARKED**, pending investigation. The requirement is that a PR from a fork
can still push coverage, or the coverage check is missing exactly where review matters most —
a contribution from outside the org.

⚠️ **Do not design this on the assumption that forks are always public.** An earlier draft of this
section did, and it was wrong. Measured 2026-09-20: forkability is a **toggle independent of
visibility**, at two levels — `allow_forking` on the repository (`true` on
`MintPlayer/MintPlayer.Spark`, which is public) and `members_can_fork_private_repositories` on the
organization (`false` on `MintPlayer`). Other organizations set both differently; a repository that
refuses forks reports exactly that, regardless of whether it is private. **A private repository is
forkable when its organization permits it**, so a permissive fork-upload rule can, in the general
case, touch private data.

What *is* true of this deployment today is narrower and should not be leaned on: the `MintPlayer`
organization currently disallows private forks and holds `total_private_repos: 0`. That makes the
present exposure zero; it does not make the rule safe.

The risks to weigh are therefore both integrity (fabricated coverage passing a gate, poisoned
history) **and** confidentiality (a fork upload attributed to a private base repository).

**Measured 2026-09-20 — what happens today: nothing, silently.** `action/src/main.ts:228-231` throws
*client-side*, before any network call, because neither credential can exist on a fork PR. The throw
is swallowed into `core.warning` at `:148-155` since `fail-ci-if-error` defaults to `false`
(`action.yml:59-62`, set explicitly at `.github/workflows/pull-request.yml:286`). **The step goes
green with a yellow annotation.** The server is never contacted, no build exists, and the base PR
shows no check run and no comment — indistinguishable from a build that produced no reports. There
is no `IsFork` field anywhere in the codebase, and the only production code comparing head to base
repo is the branch-deletion guard (`GitHubEventsRecipient.cs:411-418`). Production is clean: all 172
repositories belong to the two real installations, so the OIDC auto-provision path has never fired.

**Platform constraints, researched 2026-09-20 (these bound the solution space):**

- ⚠️ **GitHub can never mint an OIDC token for a fork PR.** `id-token: write` is downgraded to read
  for any `pull_request` from a fork, so `ACTIONS_ID_TOKEN_REQUEST_URL` is never set. This is a
  platform decision, not a configuration gap — **our existing OIDC path cannot be extended to forks.**
- **`GITHUB_TOKEN` *is* present** in fork runs (read-only, scoped to the base repo, not a repository
  secret). This is what Coveralls uses, and fork uploads work for them.
- ⚠️ **`pull_request_target` is categorically unsuitable here.** It grants secrets and a write token,
  but a coverage job executes the fork's test suite by definition — the "pwn request". Already
  recorded as a rejected footgun at `coverage_branch_pr_badges_PRD.md:312`.
- ⚠️ **Bitbucket fork PRs do not build at all** in the destination repository, with no setting to
  enable it. Fork coverage on Bitbucket is structurally impossible, not merely unimplemented — a
  documented product limitation, not a milestone.
- **GitLab fork MRs *can* mint an `id_token`**, but its `project_path` asserts the **fork**. That is
  still useful: a genuine cryptographic identity for an untrusted party, which GitHub does not give.

**The transferable idea from Codecov is not authentication, it is namespacing.** Unauthenticated
uploads are confined to a branch name containing a colon (`forkname:main`) — illegal in a git ref, so
an untrusted upload *cannot* collide with or overwrite a real branch. That holds regardless of how
strong the identity check is.

⚠️ **This has a deadline attached to it.** Untrusted coverage needs its own document space, and Raven
ids are immutable. M6 already re-keys 199,917 documents once; deciding later that fork coverage needs
a separate space means paying that migration a second time. **M6 is the cheap moment and there is not
another one.**

**Separable defect, independent of whichever option is chosen:** `fail-ci-if-error` defaults to
`false`, so *every* upload failure is a green step — not just a fork's. Same silent-success class as
the `NoOpEmailSender` trap in §7.2, and the reason this went unnoticed.

**The four patterns the competitors actually use**, distilled from the research:

1. **Namespaced tokenless + forge-API attestation** (Codecov). Accept an unauthenticated upload, but
   confine it to a namespace that cannot collide with a real branch, and verify the run exists via
   the forge API. ⚠️ Codecov makes that verification call *unauthenticated*, hence its notorious
   60 req/h ceiling — ours must be made as our own App.
2. **Use the forge-minted job credential instead of a stored secret** (Coveralls). Its action
   defaults `github-token` to `${{ github.token }}`; fork uploads work. Only their *write-back*
   degrades, which is not our problem because we post from our own App installation.
3. **`workflow_run` handoff** (SonarQube Cloud's documented recipe, py-cov-action). Untrusted half
   produces an artifact, trusted half consumes it. The only fully-safe GitHub-native path, but it
   doubles the workflow surface and hands the maintainer a foot-gun.
4. **Vendor pulls instead of being pushed to** (Sonar Automatic Analysis). Works for static analysis
   and **cannot work for coverage**, which requires executing the fork's tests.

#### D6f — RESOLVED 2026-09-20

⚠️ **The earlier recommendation on this page (pattern 2 — relay the forge-minted `GITHUB_TOKEN`) is
RETRACTED.** Investigated 2026-09-20 against GitHub's published API surface, and it does not work.

**Why it fails.** There is **no endpoint — documented or otherwise — that reflects which workflow
run, job or pull request a `GITHUB_TOKEN` was minted for.** It is a GitHub App installation access
token, and installation tokens carry no run-level identity. The strongest claim it can make to our
server is *"the bearer had read access to repo X."*

That is the same claim the repository owner's own CI token makes. A fork-PR token and the base
repo's `push`-on-`master` token are the same kind of credential for the same repository, so anyone
who can open a pull request — anyone at all, on a public repo — obtains something equivalent in what
it proves. Treating "token resolves to repo X" as authorization to write repo X's coverage would let
**any stranger overwrite the default-branch number and badge**, which today requires OIDC or an
owner-minted `covt_` token. Corroborating: Coveralls is the one vendor relaying `GITHUB_TOKEN`, and
its fork support is the flakiest of the three.

Only two distinguishability questions are actually closed by such a token (a PAT, and a token for a
*different* repo). The one that matters — a token from a *different run of the same repo* — is not.

**Decision (owner, 2026-09-20): make fork uploads work, unauthenticated, confined by namespace.**
The owner's framing: *"this should be possible, but our upload tokens (secrets) are not available for
forks."* Correct, and there is no substitute credential — so the design carries no credential and
bounds the blast radius structurally instead. This is Codecov's model, and Codecov is the only one of
the three competitors that ships fork coverage with no maintainer setup.

**The shape:**

- **Public base repository + fork PR ⇒ accept an unauthenticated upload.** It lands under the **base**
  repository's id space, so it is visible on the base PR — which is the entire point — but in a
  PR-scoped segment: `Commits/github/{baseRepoId}/pr/{n}/{sha}`.
- **That segment is structurally incapable of** becoming a branch baseline, moving a badge, or being
  carried forward into a protected branch's history. The guarantee is the id shape, not a code path
  that must remember to check.
- **It never gates a merge.** A fork PR's verdict is `EForgeOutcome.Neutral` (D20), never Failure.
- **Rendered as unverified**, labelled as contributed from a fork wherever it appears.
- **`provision: false`** — a fork upload must never auto-create a `Repository` document.
- **Its own rate-limit bucket** keyed on (repository, PR), a hard report-size cap, and a cap on
  distinct fork namespaces per repository. An unauthenticated write endpoint is a storage sink, and
  §7.1 measured how large this id space already is.
- **Optional, advisory only:** confirm the run exists via `GET /repos/{base}/actions/runs/{id}` made
  as **our own App**, never with a caller-supplied token. Advisory, not a gate — Codecov's notorious
  `"Tokenless has reached GitHub rate limit"` failure is a direct measurement of what happens when
  this call becomes a gate. For a public repo it needs no caller credential at all, which is a second
  reason relaying `GITHUB_TOKEN` buys nothing.
- **Private base repository + fork PR ⇒ no unauthenticated upload.** Same line Codecov draws (*"For
  private repositories, all uploads require a token"*), and it disposes of the
  forkability-independent-of-visibility hazard above: a forkable private repo never accepts an
  anonymous write. Those projects use the `workflow_run` recipe, which we document and ship a
  copy-pasteable snippet for. ⚠️ That snippet must never check out or execute fork code — the
  artifact is **data**.

**What the owner is accepting, stated plainly:**

1. **Anyone can post a fabricated coverage number on any public PR.** There is no credential; the
   protection is that the damage cannot leave that PR. This is why a fork's number must never gate a
   merge, and why it is labelled unverified. If a fork PR's number should ever gate a merge, this
   option is disqualified and `workflow_run` is the only answer.
2. **Private repositories do not get frictionless fork coverage.** Their contributors either use
   `workflow_run` or see nothing.
3. **We inherit Codecov's trust model, including its criticisms.**

**M6 consequence — this is why the decision could not wait.** The `pr/{n}/` segment is a document-id
shape, and Raven ids are immutable. M6 re-keys 199,917 documents once; the segment must exist in that
migration's target scheme or the whole re-key is paid twice. **M6a's id design must reserve it.**

**Unsolved, and explicitly open:** the fork-build → base-PR association. This design sidesteps it for
GitHub public repos by writing under the base repo's id space with no fork credential involved. But a
fork-owner-provisioned token (option d) and GitLab's fork-asserting `id_token` both identify the
*fork*, so a build made with either lands in the fork's id space and is invisible on the upstream PR.
Attaching it is a genuine authorization question — *who may attach a build to someone else's pull
request?* — and it is **not** answered here. It blocks GitLab fork coverage in stage 2.

### 6.8 Migrations — the rule that keeps them compiling

A database migration is **not** a risk to be minimised here; it is a certainty. Many document ids
are natural ids hard-linked to GitHub, and they cannot be provider-qualified without rewriting
stored data. Since a migration ships anyway, other model changes that want one should ride along in
the same PR rather than being contorted to avoid one.

**The standing rule: a migration must never reference a C# property that the same PR renames or
removes.** This repo already follows it, and the pattern is worth naming explicitly because it is
what makes the rule cheap. Migrations are `ISparkMigration` implementations that send a
`PatchByQueryOperation` carrying an **RQL/JavaScript string operating on the stored JSON**, not on
typed entities — e.g. `M_202609081600_MoveDeleteBranchFlagToRepository.cs:52`
(`"from GitHubProjects update { delete this.DeleteBranchOnPrClose; }"`) and
`M_202609092000_DeleteBranchFlagBecomesAPolicy.cs:64-67`, which reads and rewrites
`r.DeleteBranchOnPrClose` entirely inside the script. A property renamed in C# therefore cannot
break a migration, because the migration never named the C# member — only the JSON field, which is
a historical fact about data already written and must keep its old spelling forever.

Three corollaries, all of them learned the hard way in this repository:

- **Never re-type a migration against the current entity.** A migration is a statement about data as
  it was, and the entity will keep moving. `M_202609190900_BranchesBecomePerLineArmSets.cs` is the
  model to copy.
- **Make every migration re-runnable and no-op on already-converted documents.** The same file
  returns before any write when the old field is absent.
- ⚠️ **Patch scripts are capped at 10,000 statements per document** (`Patching.MaxStepsForScript`).
  `M_202609190900` documents the measurement: 421 edges converts, 5,263 faults, and production held
  ten documents at 5,263 — so `IgnoreMaxStepsForScript` is load-bearing, not a precaution. Without
  it the operation faults, `UpAsync` throws, **startup aborts and the deploy fails with the site
  down**. Any migration in M6 that walks a collection must be sized the same way, against a copy of
  production, before it is trusted.

`M_202609190900` also sets the standard for how to justify one: it states the measured population
(201,698 `FileCoverage` documents, 108,648 with branch data), the read time (7 s against a 180 s
readiness budget), and what happens if the migration never runs at all.

---

### 6.9 Per-provider assemblies (D14, D15)

Raised by the owner, 2026-09-20: register the forges as multiple services on one interface and ship
three packages — `MintPlayer.Spark.CodeCoverage.{Github,Gitlab,Bitbucket}Integration`.

**The premise holds.** `[Register(typeof(IFoo), ServiceLifetime.Scoped)]` emits plain `AddScoped`,
never `TryAddScoped` — verified in the generator source
(`MintPlayer.Dotnet.Tools/.../ServiceRegistrationsGenerator*.cs`; `grep TryAdd` over it returns no
source hits) and in a checked-in generated artifact showing three implementations of one interface
coexisting. Registrations stack; `IEnumerable<T>` returns all of them. Each assembly also gets its
own generated `AddXxxServices()` entry point for free, named from its namespace.

**And the extension point exists.** Eleven packages already extend `ISparkBuilder`.
`libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/Extensions/SparkBuilderExtensions.cs:13-62` is a
line-for-line template for `AddGithubIntegration(this ISparkBuilder, Action<Options>)`: options from
a configure delegate, the generated service registration, conditional extras, routes via
`builder.Registry.AddEndpoints(...)`.

#### What a provider assembly can and cannot carry

| Contribution | Cross-assembly? | Mechanism |
|---|---|---|
| Services | ✅ | generated `AddXxxServices()` |
| Raven indexes, `[FromIndex]` projections | ✅ | `SparkModuleRegistry.AddIndexAssembly` — which exists *because* a module shipped as a class library got neither. Must be declared from the `AddXxx` body; inside an `AddMiddleware` callback it is a documented silent no-op |
| `[GenerateIndex]` generation | ✅ | the generator walks `ReferencedAssemblySymbols` |
| Translations | ✅ | `LibraryTranslationsGenerator` emits `[assembly: SparkTranslations]`; the host generator merges every referenced assembly's payload |
| Webhook routes, credential schemes, middleware | ✅ | `Registry.AddEndpoints` / `AddCredentialScheme` |
| Entities / persistent objects | ⚠️ ships fine, but the **app** must name them on its own `SparkContext` |
| Model JSON + `modelHashes.json` | ❌ **app-only** — paths are `{ContentRootPath}/App_Data/Model`, no per-library model directory |
| `security.json` | ❌ **app-only by explicit design** — the path is a `const`, and the remarks state non-configurability is deliberate: *"a second place to put the file is a second place to fail to find it"* |

The last two are not engineering problems to route around; they are the framework's position. The
consequence to accept: **a provider package is not self-contained for authorization.** Installing one
is reference + author the grants in the app's `security.json` + re-run `--spark-synchronize-model`
and commit — a three-step operation that CI gates. That is tolerable for a closed set of three
providers maintained in this repo, and would be a poor experience for a third-party plugin. We are
building the former.

⚠️ One latent fragility, already burned once: the `[GenerateIndex]` cross-assembly filter keys on the
AssemblyRef of the attribute-host assembly. An assembly that references Spark *only* for attributes
is precisely the shape that once made HR's indexes vanish **with no diagnostic**. It is fixed, but it
fails silently, so a new provider assembly must be *verified* to emit its indexes, never assumed to.

#### D14 — split, but do not publish

Three projects with `IsPackable=false` deliver the entire architectural benefit. Exit criterion A1
("no caller outside a provider implementation references Octokit") is today a code-review promise;
an assembly boundary with no Octokit reference outside `…GithubIntegration` makes it a **compile
error**. `PackageId` and `GeneratePackageOnBuild` add nothing to that enforcement.

What publishing would additionally cost:

- **It forces a fourth package as the public contract.** The seams are typed on this app's domain
  entities — `IForgeClient` and `IForgeFeedbackPublisher` both take `CodeCoverage.Entities.Repository`
  — so shipping a provider package means shipping the RavenDB persistence model as public API, at the
  exact moment #422 is still reshaping it. `CodeCoverage.Library` also carries a `ProjectReference` to
  `MintPlayer.Spark.Authorization` that its own csproj comment (`:18-26`) flags as architecturally
  wrong; that would become a public dependency.
- **The first release is permanent and fast.** CI packs solution-wide and pushes globbed on merge to
  master, with `--skip-duplicate` — which turns a *rejected* push into a silent success. A mis-shaped
  first release is public within minutes and its version can never be reused.
- **There are no consumers.** One production app, one container.

Flipping `IsPackable` later is one line per project. Un-publishing a wrong public API is impossible.
So: split now, publish when a second forge actually ships and the entity contracts have stopped
moving.

**Placement.** Packable projects live under `libs/`, but the PR version gate filters by **path**, not
by packability — anything under `libs/**` demands a `<Version>` bump on every touch. Since these are
deliberately unpublished and will churn throughout stage 1, they belong in
`apps/CodeCoverage/CodeCoverage.{Github,Gitlab,Bitbucket}Integration/` as siblings of the app, and
move to `libs/` if and when they are published. ⚠️ Never name a directory `coverage/` — `.gitignore`
matches that path component case-insensitively and it would be silently untracked, which is why the
app is `CodeCoverage`.

**Dependency direction.** `CodeCoverage.Tests` → `CodeCoverage` (app) → `*Integration` →
`CodeCoverage.Library`. Linear, no cycles, and it keeps the contract where it already is.

#### D15 — what actually moves

Moving a capability into a provider assembly requires a neutral seam to call it through. Three exist;
five do not (§5.7c). So the split is staged by what is seamable, not done in one sweep:

| Capability | Disposition |
|---|---|
| Diff / compare, first parent, file content | **moves** — `IForgeClient` exists |
| Allowed-owner lookup, credential state | **moves** — `IForgeAccessService` exists, after M2a |
| Commit status / check run, sticky PR comment | **moves** — `IForgeFeedbackPublisher` exists; the comment gateway needs de-Octokit-ing first |
| Repository enumeration, `owner/name` resolution, branch delete | **moves after a seam is built** (M2b) |
| Webhook ingestion and event normalisation | **moves after a seam is built** (M2b) — the largest piece; 11 app files import the GitHub webhooks namespace |
| **App installations** | **stays GitHub-only.** No forge has "an installation the owner selects repositories into"; GitLab has group/project access tokens, Bitbucket has app passwords. This is not a capability to abstract — it is GitHub's *answer* to a question (`which owners may this human manage?`) that `IForgeAccessService` already asks neutrally |
| **Projects V2 boards** | ⚠️ **SUPERSEDED by D17 — see §6.10.** This row said "stays GitHub-only, forever", which held only while there was no capability channel. Boards now go **on the interface** behind `EForgeCapability.Boards`, and the app iterates and skips. What was right and still stands: the API surface is entirely GraphQL and entirely GitHub (`ProjectV2` node ids, single-select Status fields, `closingIssuesReferences`), GitLab issue boards do not map, Bitbucket has none — and the `GitHubProject` *entity*, its model JSON, its security grants and its custom actions stay in the app |
| **CI identity (OIDC)** | **per-provider, but pluggable rather than shared.** Every forge mints job tokens; the issuer, claim names and trust decision differ entirely. One scheme registered per provider assembly, not one abstraction |

The honest summary: about half the GitHub surface is a *provider implementation* and moves; the other
half is either GitHub's private answer to a neutral question (installations) or a feature no other
forge has (Projects V2). The second half staying in the app is the correct outcome, not a
shortcoming — and it is why the three assemblies will not be symmetric in size.

---

### 6.10 `IForgeIntegration` — the shape the owner asked for (D16, D17)

Decided by the owner, 2026-09-20, and it **supersedes the three-seam design of §6.5**. The goal, in
his words: one `IForgeIntegration` interface; three libraries each holding one implementation
class decorated with `[Register]`; the app calls three extension methods; **injection sites take
`IEnumerable<IForgeIntegration>` and do not know at all which forge they are working with**.

That last clause is the design constraint, and it is stronger than what §6.5 delivered. §6.5 gave
three *typed* seams that a caller still selected between; this gives one surface that callers iterate.

#### The two call shapes

The constraint holds in both cases, but they resolve differently, and conflating them is the easiest
way to get this wrong:

- **Fan-out** — `GetAccountsAsync`, `GetRepositoriesAsync`. Ask *every* registered integration and
  concatenate. The caller never learns which forges exist, or how many. This is what
  `SparkVisibility.QueryAllowedOwnersAsync` already wants to do.
- **Select-one** — `CompareAsync`, `PublishStatusAsync`, `GetFileContentAsync`, anything scoped to a
  repository. Exactly one integration can answer. The caller *still* does not choose: selection is
  driven off the entity (`repository.Provider`), so there is no `if (github)` anywhere — the forge
  is data, not control flow.

⚠️ Select-one depends on entities carrying a forge discriminator, which they do not today
(`grep ForgeOwner|EForgeProvider` across `CodeCoverage.Library/Entities` → zero hits). Until M6
re-keys the documents, selection answers GitHub from one explicit, commented line that throws as soon
as a second integration registers. See §5.7.

#### D17 — capability gaps

The forges are not feature-equivalent: GitHub has Projects V2 boards, GitLab has issue boards that
do not map, Bitbucket has no boards at all. The interface expresses this with a **get-only
`EForgeCapability[] Capabilities`** property; methods outside a given implementation's capability set
**throw** `NotSupportedException`.

```csharp
public interface IForgeIntegration
{
    EForgeProvider Provider { get; }
    EForgeCapability[] Capabilities { get; }

    Task<Account[]> GetAccountsAsync(...);
    Task<Repository[]> GetRepositoriesAsync(...);
    Task<CommitComparison?> CompareAsync(...);
    Task PublishStatusAsync(...);
    Task<Board[]> ListBoardsAsync(...);   // EForgeCapability.Boards
}

foreach (var integration in integrations.Where(i => i.Capabilities.Contains(EForgeCapability.Boards)))
    await integration.ListBoardsAsync(...);
```

This **reverses an earlier recommendation.** §6.9's D15 table said Projects V2 should stay app-side
because no other forge has boards. With a capability array that is no longer necessary: boards go on
the interface behind `EForgeCapability.Boards`, and the app iterates and skips. The earlier reasoning was
sound only under a one-size-fits-all interface with no capability channel.

**App installations still do not go on the interface** — not as a capability, not at all. They are
GitHub's *mechanism* for answering "which owners may this user manage?", and `GetAccountsAsync`
already asks that question neutrally. Putting installations on the interface would leak the answer's
implementation into the question. They stay internal to the GitHub library.

⚠️ **The failure mode to guard.** `Capabilities` and actual behaviour can drift: an implementation
that advertises a capability but throws, or implements a method it does not advertise, is a runtime
bug that no compiler catches and no ordinary test exercises. M2c therefore ships a **conformance test
that runs against every registered implementation** and asserts the two agree in both directions.
Without it the array is documentation, not a contract.

#### D16 — implementation shape: a facade

Each library's `XxxForgeIntegration` is a **thin class implementing the interface and delegating
to per-concern services kept internal to that library**. The registration and injection surface is
exactly as the owner described — one `[Register(typeof(IForgeIntegration), …)]` per library — while
the implementation stays decomposed.

Why not one real class: GitHub's surface is ~3,000 lines across accounts, repositories, OIDC, diffs,
content, status, comments, reconciliation and boards. Collapsing that into a single type makes the
one file nobody can review, and would force rewriting the ~30 existing test files that target the
current services. The facade keeps those tests pointed at the internals and adds a thin layer whose
own test is the conformance test above.

```csharp
[Register(typeof(IForgeIntegration), ServiceLifetime.Scoped)]
public partial class GitHubForgeIntegration : IForgeIntegration
{
    [Inject] private readonly IGitHubAccounts accounts;
    [Inject] private readonly IGitHubDiffs diffs;

    public EForgeProvider Provider => EForgeProvider.GitHub;
    public EForgeCapability[] Capabilities => [EForgeCapability.Boards, EForgeCapability.Oidc, /* … */];

    public Task<Account[]> GetAccountsAsync(...) => accounts.ListAsync(...);
}
```

#### One member that is not a plain async call

`oidcLogin` does not fit the shape of the others. Validating an upload's CI token is an
`AddJwtBearer` scheme registered **at startup**, not a method invoked per request — issuer, JWKS URL
and claim names are configuration, and the trust decision differs entirely per forge. It is
therefore exposed as *configuration the integration contributes* during `AddXxxIntegration(...)`,
not as a runtime method, with the per-request work staying in the existing authentication handler.
Recorded here because "all necessary async methods" is right for every member except this one.

#### What this changes in already-committed work

`IForgeAccessService`, `IForgeClient` and `IForgeFeedbackPublisher` (M1, M2) converge into
`IForgeIntegration`. Their method signatures are largely reusable — the work is consolidation, not
redesign, and the credential-free signatures that made them correct carry over unchanged. The
resolver-per-interface idea from the first draft of M2a is dropped: with one interface there is one
selection helper, not three.

---

### 6.11 Webhook recipients see a neutral event, not a forge's (D21)

Raised by the owner, 2026-09-20: can one recipient class handle the same logical event from several
forges — `IRecipient<GitHubWebhookMessage<IssueCreated>>` **and**
`IRecipient<BitbucketWebhookMessage<IssueCreated>>` — with lean implementations routing to one
private method? And: *"If you can suggest a better, less-consumer-code alternative, that would be
fine too (even better)."*

**Decided: normalise in the forge library, so consumers never see a forge-shaped message at all.**

The per-forge-interface version works, but its consumer cost scales with forge count — three forges
means three interface implementations and three lean methods **per recipient**, and adding GitLab
later edits every consumer that cares about any shared event. The neutral version costs one method
and adding a forge touches **zero** consumers:

```csharp
class LogIssueOpened : IRecipient<ForgeWebhookMessage<IssueOpened>>
{
    public Task Handle(ForgeWebhookMessage<IssueOpened> m, CancellationToken ct)
    {
        // m.Provider — which forge, when it matters
        // m.Event    — our IssueOpened, normalised
        // m.RawJson  — escape hatch for something only one forge sends
    }
}
```

#### Why, in one line: it stops an M×N expansion

*(The owner's framing, 2026-09-20: "Your proposal clearly prevents future M×N expansions.")*

With a message type per forge, a codebase with **M** recipients and **N** forges needs **M×N**
handler implementations, and every new forge edits **M** existing consumer classes. With a neutral
message, it needs **M** — and N is absorbed once, inside the forge libraries, where a new forge is
a new library rather than an edit to every consumer.

That is the whole argument, and it is also the test for any future addition to this design: if a
change makes consumer code grow with the number of forges, it is the wrong change.

**This is not new architecture.** Normalisation belongs in the forge integration libraries already
being built for D16/M15: "verify this forge's signature, parse its payload, publish a neutral event"
is the same shape as `CompareAsync` or `PublishStatusAsync`, and §5.7c already classified *"normalise
a push / PR-opened / PR-closed / repo-renamed event to a domain event"* as a neutral operation that
merely happens to have only a GitHub implementation. The webhook half of `IForgeIntegration`.

#### The rule

**Neutral message for events all three forges have; forge-specific message for the rest; never a
neutral name over a single-forge concept.** The last clause is D17's reasoning applied to messages:
GitHub's `check_run` and Projects V2 events have no counterpart, so they keep forge-specific message
types, and a recipient handling them is honestly forge-specific rather than pretending otherwise.

#### Costs, stated plainly

- **The canonical event set has to be designed, not discovered.** `PushReceived`,
  `PullRequestOpened`, `PullRequestClosed`, `IssueOpened`, `RepositoryRenamed` is the likely starting
  set — it is a real decision and it is not free.
- **It is lossy by construction.** A canonical event cannot carry everything each forge sends.
  `RawJson` covers the remainder, but a handler that reaches for it has become forge-specific again
  and should say so rather than quietly depending on a field only one forge populates.
- **Both shapes coexist permanently.** That is the correct outcome, not a transitional state.

#### What this does to M8

M8 was written as "de-GitHub the bus contract" — drop `required long InstallationId` and
`RepositoryFullName` from the envelope, rename the `spark-github-all` queue. **D21 makes it larger**:
the envelope splits into a neutral part and a forge-specific part, a canonical event model appears,
and each forge library gains a publisher. Recorded as a scope increase rather than folded into an
existing bullet, because it is one.

#### Constraints any implementation must respect

- ⚠️ **Queue names must stay pinned.** `GitHubWebhookMessage` and `GitHubWebhookMessage<TEvent>` both
  carry `[MessageQueue("spark-github-all")]` deliberately: without it the name derives from the CLR
  type, and for a constructed generic that embeds the argument's **assembly-qualified** name. Field
  evidence in that file's doc comment — one database accumulated **seven** `SparkMessaging-*`
  definitions, six of them orphans of exactly this shape, including separate `Version=2.0.0.0` and
  `Version=3.0.0.0` variants of the same event. A generic `ForgeWebhookMessage<TEvent>` is the same
  hazard and needs the same pin.
- ⚠️ **The subscription budget is a hard limit, not a tuning knob.** Production runs RavenDB
  **Community**, which caps subscriptions, and this repo has already hit that cap once and resolved
  it with a single-subscription mode. A design that wants one queue per forge must be checked against
  that budget before it is built, not after.
- ⚠️ **Producer-side silence multiplies with fan-out.** A wire type with no handler is dropped
  **silently** on the publish side. (The companion worry — that `Processing` was written and read by
  nothing, so a crash mid-handler dropped the webhook — turned out to be **fixed**; see the
  verification below.)


#### Verified against the machinery, 2026-09-20

Investigated after the decision, to confirm the design is buildable rather than merely appealing.
**No change is needed in `libs/messaging`.**

- ✅ **One class may implement `IRecipient<>` several times.** `RecipientRegistrationGenerator.cs:50-67`
  iterates `AllInterfaces` and emits **one `AddScoped` per closed `IRecipient<T>`**; the registry keys
  on the *message* type, never the implementation (`MessageRecipientRegistry.cs:39-55`). No
  `[Register]` attribute is involved — the generator triggers on the base list alone. So the
  fallback shape is available if ever needed, even though D21 means consumers should rarely want it.
  ⚠️ No class in the repo does this today, so it is untested in practice.
- ✅ **Three forge queues cost zero extra RavenDB subscriptions.** `SubscriptionMode` defaults to
  `SingleSubscription` (`SparkMessagingOptions.cs:101`), which runs **one** subscription named
  `SparkMessaging` regardless of queue count and restores per-queue FIFO in-process
  (`MessageQueueRouter.cs:10-28`). The Community cap that caused the seven-definition incident only
  binds under `SubscriptionPerQueue`. **And separate forge queues are better than one shared
  `spark-forge-all`**: three independent lanes mean a slow GitHub handler cannot stall a Bitbucket
  event, where one queue would serialise all three forges behind each other.
- ✅ **Exact-type dispatch is not an obstacle for this design** — it is why the design works.
  `MessageProcessor.cs:127-129` resolves `IRecipient<>` over the stored CLR type exactly, so a
  *base* record can never be a dispatch target and `IRecipient<Base>` would silently receive nothing
  (zero handlers, one warning, `MessageProcessor.cs:149-152`). D21 has no derivation: all three forge
  libraries publish the **same** concrete `ForgeWebhookMessage<TEvent>`, with the forge as a *field*
  rather than a type distinction. One type, one registration, exact match.
- ✅ **A bonus the decision did not anticipate.** `MessageType` is stored as an assembly-qualified
  name (`MessageBus.cs:35-42`) and compared ordinally by the allow-list
  (`MessageTypeAllowList.cs:57-58`). For `GitHubWebhookMessage<TEvent>` that name embeds **Octokit's**
  assembly version — so an in-flight typed message that survives an Octokit major bump is
  dead-lettered. A neutral envelope closed over *our own* event types has no such coupling. The
  normalisation removes a third-party version dependency from the wire format.

#### Traps to build around

- ⚠️ **`[MessageQueue]` is `Inherited = false`** (`MessageQueueAttribute.cs:3`) and the lookup is a
  plain `GetCustomAttribute`. A base record's attribute is **invisible** to a derived one, which then
  falls back to CLR-name derivation — the exact shape that produced the seven-queue incident.
  **Every concrete envelope carries its own `[MessageQueue]`.** A shared base is fine for properties
  and must never be relied on for the queue name.
- ⚠️ **Do not rename `spark-github-all`.** Renaming strands in-flight documents on the old queue.
  The GitHub queue keeps its name; new forges get new ones.
- ⚠️ **GitLab sends no delivery id at all**, so its envelope cannot use the `BroadcastOnceAsync`
  dedup path (`SparkWebhookEventProcessor.cs:160-168`) — idempotency has to come from the handler.
  And **Bitbucket's `X-Hook-UUID` identifies the *hook*, not the delivery**: wiring it in as a
  delivery id would make every Bitbucket delivery after the first look like a duplicate and be
  **silently swallowed**. This one would look exactly like "Bitbucket webhooks don't work" and
  nothing would log an error.
- ⚠️ **Declare both interfaces on one partial part** if a recipient ever does implement several. The
  generator fires per `ClassDeclarationSyntax` with a base list, so interfaces split across two
  partial parts would emit duplicate registrations and run the handler twice per message.
- ⚠️ **Producer-side silence is by design and survives.** A typed envelope with no recipient returns
  after a `LogDebug` (`:321-327`); a catch-all with none returns with **no log at all** (`:138-139`).
  Across a three-forge fan-out a mis-registered recipient therefore fails silently on the publish
  side, even though the consumer side now dead-letters unknown types loudly.

**Correction to a standing note:** *"`Processing` is written and read by nothing, so a crash
mid-handler drops the webhook"* is **no longer true**. `SparkMessage.OwnerId` / `ClaimExpiresAtUtc`
now exist, `ProcessAsync` verifies claim ownership (`MessageProcessor.cs:63-70`), the park uses
`CancellationToken.None` so a shutdown still records the retry (`:295-310`), and
`MessageRetrySweeper.ReclaimAbandonedAsync` (`:114-170`) returns abandoned messages to `Pending`. A
crash mid-handler is reclaimed. The warning above about this in earlier drafts is withdrawn.

---

## 7. Spikes

Run before the milestone each gates. None has been run.

- **✅ SP1 — Migration blast radius — RUN 2026-09-19, read-only, against production.** It did **not**
  follow #423's precedent of collapsing to nothing. It made M6 bigger. Results in §7.1.
- **SP2 — Does the `NoOpEmailSender` actually resolve?** *(gates M4)* §4.3 confirms both types exist
  in the 10.0.12 assembly; confirm the resolution order in *this* app's container, so the startup
  guard tests the right condition.
- ~~**SP3 — Badge alias behaviour**~~ — **dropped.** The legacy badge route is being removed rather
  than aliased (§5.4), so there is nothing to keep byte-identical. What remains is a mechanical task
  in M7: find and replace every badge URL we publish.
- **SP4 — Can a GitLab OAuth token mint group access tokens?** *(stage 2, but decides product shape)*
  Docs require Owner role and demonstrate with a PAT; whether an `api`-scoped OAuth token suffices is
  unverified. **This decides one-click connect vs "paste a token here."**
- **SP5 — Bitbucket verified-email signalling and Pipelines OIDC** *(stage 3)*. Whether
  `is_confirmed` is reachable at callback time, and whether tokenless upload is possible at all.

---

### 7.1 SP1 result — measured against production, 2026-09-19

Read-only. `GET /databases/Coverage/collections/stats` plus three sample ids per collection, run
inside the `coverage-raven` container. Database: **207,423 documents, 2.48 GB on disk, 16 indexes,
683 attachments (503 unique)**.

| Collection | Docs | Id shape | Contains the GitHub repo id? |
|---|---:|---|---|
| `FileCoverages` | 197,973 | `Commits/{repoId}/{sha}/builds/{runId}-{attempt}/files/{hash}` | **yes** |
| `SparkMessages` | 7,491 | — | no (framework queue) |
| `Commits` | 804 | `Commits/{repoId}/{sha}` | **yes** |
| `BuildTreeSummaries` | 482 | `…/builds/{runId}-{attempt}/tree` | **yes** |
| `Builds` | 303 | `Commits/{repoId}/{sha}/builds/{runId}-{attempt}` | **yes** (+ attachments) |
| `Repositories` | 172 | `Repositories/{repoId}` | **yes** |
| `CommitAssemblies` | 138 | `Commits/{repoId}/{sha}/assembly` | **yes** |
| `PullRequestFeedbacks` | 43 | `PullRequestFeedbacks/{repoId}/{pr}` | **yes** |
| `Accounts` | 2 | `Accounts/{ownerId}` | **yes** |
| `ApiTokens` | 2 | `ApiTokens/{raven-generated}` | no — GitHub ids are in *fields* |
| `GitHubProjects` | 1 | `GitHubProjects/{graphql node id}` | no — already globally unique |
| `SparkUsers`, `KeyDocuments`, `SparkMigrationRecords` | 12 | — | no |

**199,917 documents carry the GitHub numeric repo id in their key** — because `FileCoverages`,
`Builds`, `BuildTreeSummaries` and `CommitAssemblies` are all nested under the `Commits/{repoId}/…`
prefix. The entity classes hide this: only six `DocumentId(...)` helpers name a GitHub id, but four
collections inherit it through the path.

**Two consequences that change M6's method, not just its size:**

1. **RavenDB document ids are immutable.** A re-key is put-under-the-new-id plus delete-old for
   every document. The `PatchByQueryOperation` style that every existing migration in this repo uses
   (§6.7) **cannot do this**. M6 needs a different, slower mechanism, and it is not re-runnable in
   the same trivially-idempotent way.
2. **683 attachments hang off `Builds`** (`@flags: HasAttachments`). Attachments are bound to a
   document id, so they do not travel with a put — each needs an explicit copy/move alongside the
   re-key, and a half-finished migration leaves reports orphaned from their builds.

⚠️ **This reopens D7.** It was decided ("migrate explicitly, no implicit default") on the
understanding that the population was small. It is ~200k documents plus attachment moves on a live
2.48 GB production database. The alternatives are unchanged — an implicit `github` default costs a
permanent asymmetry and zero migration — but the trade has moved. **Do not start M6 until D7 is
re-confirmed against this number.**

Two smaller openings this created: `GitHubProjects` is keyed by a GraphQL node id that is already
globally unique, and `ApiTokens` keeps forge ids in fields rather than the key — neither obviously
needs a provider prefix, so D5 should say explicitly whether the prefix is universal or only applied
where a collision is possible.

---

### 7.1a Side effect of running SP1 against production

⚠️ Recorded because it was described as read-only and was not quite. The ad-hoc RQL used to sample id
shapes caused RavenDB to create two **auto-indexes** on the production `Coverage` database:
`Auto/Repositories/ByCountReducedByAccount` and `Auto/Repositories/ByAccountAndGitHubId`. They are
trivial against a 172-document collection and RavenDB retires idle auto-indexes on its own, so they
were left in place — deleting them is also a write. Noted so a later index audit does not treat them
as evidence of a code path that queries those fields.

The related check they were used for: all 172 repositories belong to the two real installations
(`PieterjanDeClippel` 159464742, `MintPlayer` 153617061) and none has a null `Account`, so the OIDC
auto-provision path at `UploadsController.cs:726-751` has **never fired in production**.

### 7.2 SP2 result — measured 2026-09-20

Run in a real DI container, and kept as a regression test at
`tests/MintPlayer.Spark.Tests/Authorization/Extensions/EmailSenderRegistrationTests.cs`.

⚠️ **The obvious guard would never fire.** `IEmailSender<SparkUser>` resolves to
`Microsoft.AspNetCore.Identity.DefaultMessageEmailSender<SparkUser>` **whether or not a transport is
registered** — it is an adapter that formats Identity's three messages and forwards them. Asserting
on that type tells you nothing about deliverability.

The real discriminator is the **non-generic** `Microsoft.AspNetCore.Identity.UI.Services.IEmailSender`,
which with no transport registered is `Microsoft.AspNetCore.Identity.UI.Services.NoOpEmailSender` — it
accepts every message and discards it, with no exception, no log line and no failed health check.

So M4's startup guard inspects the **non-generic** registration. The regression test also pins that an
application-registered transport still wins over the framework's `TryAdd`, because every application's
mail would silently revert to the no-op if that ever stopped holding.

---

## 8. Exit criteria (stage 1)

- **A1** — No caller outside a provider implementation references Octokit types or `api.github.com`.
- **A2** — A GitHub repo and a hypothetical second-provider project with the **same numeric id**
  coexist without collision, proven by a test that stores both.
- **A3** — No bus message, queue name or recipient signature names GitHub or `InstallationId`.
- **A4** — The shell has no provider-specific sign-in button; `/sign-in` renders one button per
  server-reported provider, and guard/interceptor redirects reach it.
- **A5** — With `ConfirmByEmail`, signing in with a provider whose email matches an existing account
  **sends a mail that arrives**, and the link is made only after the token is consumed. Asserted on
  a captured message, not on a method returning success.
- **A6** — With `ConfirmByEmail` configured and no real sender registered, **the app fails to start**.
- **A7** — With `WhenSignedIn`, a signed-in user links and unlinks a provider, and **cannot remove
  their last remaining credential**.
- **A8** — The verified-email gate accepts a standards-compliant `email_verified` from any provider;
  no Spark code names GitHub.
- **A9** — A non-`Success` `ExternalLoginSignInAsync` for a linked user produces a distinct, accurate
  error — not `account_creation_failed` — and does not skip the token save.
- **A10** — Every pre-existing production document is reachable after migration; counts before and
  after match, **verified against production**, not a fixture.
- **A11** — Every badge URL we publish (READMEs across our repos, the badge panel's copy-paste
  snippet, the PR-comment renderer) uses the `/{provider}/…` form, and no source in our control
  still emits the legacy two-segment URL.
- **A12** — No committed document asserts that GitHub is the only supported forge.

---

## 9. Risks

- **The id migration is the risk** (M6). It re-keys live documents in the `Coverage` database, behind
  coverage.mintplayer.com. Mitigations: SP1 read-only first; ship the verification script *with* the
  milestone; follow #423's precedent of measuring the affected population before assuming a migration
  is needed at all.
- **Renaming a model attribute is type-silent on the client** (§5.2). `valueFor(item, 'GitHubId')`
  returns `undefined` with no compile error. Every attribute rename in M6 needs a matching client
  sweep, and this has already bitten the repo once.
- **Badge URLs are public and permanent** (A11). There is no fix available after the fact.
- **Authorization widening** (§5.5). Making the grant explicit must not turn "member of the group"
  into "may administer the connection". Today GitHub's answer to "who may manage this" is "anyone who
  can see it" — do not generalise that answer, replace it.
- **Mail silently doing nothing** (§4.3, A6). The framework hands you a no-op by default.
- **Un-deferring #299** makes Spark a mail-sending framework for the first time: a new dependency
  surface, templates, localization. D9 keeps the transport out of Spark, which limits it.

---

## 10. Later stages

Designed for here, built later. Listed so that stage 1's abstractions are shaped correctly, and so
that "out of scope" does not become a parking lot.

- **Stage 2 — GitLab.** Preferred first forge: its CI `id_tokens` removes the stored upload token
  entirely, and its commit status has a native `coverage` float, so it exercises the abstraction
  without also paying a marketplace tax. Needs both webhook-auth paths (verbatim token, and 19.0
  HMAC), and per-project hooks for Free-tier customers.
- **Stage 3 — Bitbucket Cloud.** Forge app + Forge Remote; Code Insights reports rather than PR
  comments; a Pipelines pipe; batching, given the 1,000 req/h per-token budget. Most likely to need
  rework between planning and landing, given Connect's retirement.
- **Not planned:** Projects v2 on other forges (D8), Bitbucket Data Center (D10), cross-provider
  aggregates, account merge.

---

*M0–M2 are implemented (see the plan for commit hashes). D8, D10 and D12 remain recommendations
rather than decisions, and D6f — fork-pull-request uploads — is the one still parked, because it is
the only open question that can quietly change who can write coverage for a repository.*

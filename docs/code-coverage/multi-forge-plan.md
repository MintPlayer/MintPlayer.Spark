# Plan — Stage 1: de-couple CodeCoverage from GitHub, rebuild sign-in and linking

Companion to [multi-forge-PRD.md](multi-forge-PRD.md). Issue
[#422](https://github.com/MintPlayer/MintPlayer.Spark/issues/422).

**One PR** off `master`, branch `issue-422-forge-abstraction`. Commits per milestone; **the test
suite runs once, at M13** — intermediate milestones are verified by reading the code and building.
GitLab and Bitbucket providers are stages 2 and 3 and are *not* in this PR (PRD §1, D1).

**Blocked on decisions**: only **D6f** (fork-PR uploads) is still open, and it gates **M6** — not M9
as you might expect — because untrusted coverage needs its own document space and M6 is the one
migration that can create it cheaply. D1–D5, D6a–e, D7, D9, D11, D13, **D16 and D17** are decided;
D8, D10, D12 and D14 carry recommendations nothing in stage 1 depends on. D15 is partly superseded by
D17.

⚠️ **One naming collision is open** — see the note at the end of this file. `IPlatformIntegration`
(D16) says *platform*; the shipped `EForgeProvider` / `ForgeOwner` say *provider*. They mean the same
thing, and "provider" is also ASP.NET Identity's word for an external login provider.

⚠️ **Decisions here are revisitable.** The owner's standing instruction: *"my decisions aren't a
requirement. If other decisions turn out to be better, we can still change."* Where implementation
has since surfaced a better option, it is recorded as a **revisit candidate** next to the decision
rather than silently acted on.

Legend: 🟦 not started · 🟨 in progress · 🟩 done · ⛔ blocked

---

## M0 — Spikes 🟩 *(SP1, SP2 done; SP3 dropped)*

Run first; each can invalidate a later milestone's design. Read-only.

- **✅ SP1 — migration blast radius — DONE 2026-09-19** (read-only, against production). Result in
  PRD §7.1: **199,917 documents carry the GitHub repo id in their key**, plus 683 attachments on
  `Builds`. It did *not* collapse the way #423's did — it grew M6, and it invalidated the
  `PatchByQueryOperation` method, because Raven ids are immutable. **D7 was re-confirmed against this
  number: full re-key, rehearsed first (SP6, inside M6).**
- **✅ SP2 — no-op sender resolution — DONE 2026-09-20.** Measured in a real container via
  `tests/MintPlayer.Spark.Tests/Authorization/Extensions/EmailSenderRegistrationTests.cs` (kept as a
  regression test). ⚠️ **`IEmailSender<SparkUser>` is ALWAYS `DefaultMessageEmailSender<SparkUser>`**,
  transport or not — it is an adapter. The real discriminator is the **non-generic**
  `Microsoft.AspNetCore.Identity.UI.Services.IEmailSender`, which with no transport registered is
  `Microsoft.AspNetCore.Identity.UI.Services.NoOpEmailSender`. **A guard written against the generic
  type would never fire.**
- ~~**SP3 — badge alias**~~ — **dropped.** No backward-compatible badge route is being kept
  (PRD §5.4), so there is nothing to keep byte-identical.

**Exit:** findings written into the PRD (SP1 is recorded in §7.1). SP6, the migration rehearsal, is
scoped inside M6 because it needs the migration written first.

---

## M1 — `IForgeAccessService` behind the existing predicates 🟩 *(704ab22a)*

Extract the allowed-owner lookup. GitHub remains the only implementation. **No behaviour change.**

- Introduce `IForgeAccessService` returning the allowed-owner set *per provider*.
- Move `GitHubAccessService.cs:119-144` (`GET /user/installations` + 5-min `IMemoryCache`) behind it
  as `GitHubForgeAccessService`.
⚠️ **M1 touches no stored data.** The owner-value qualification of D6e is a *data* change and moves
to M6, so the whole PR ships **one** migration rather than two. This is safe only because GitHub is
the only provider in stage 1, which keeps an unqualified value unambiguous until a second one exists.
M1 introduces the per-provider *shape*; M6 rewrites the *values*.

- **Introduce the qualified owner type** (D6e) without changing what is stored. Today the set is an
  unqualified `string[]` of logins (`GitHubAccessService.cs:80-84`) compared against
  `Repository.OwnerLogin` / `Account.Login`, so a GitLab group named `microsoft` and a GitHub org
  named `microsoft` would be **the same string** (PRD §5.6). The type carries its provider; the
  GitHub implementation stamps `github:` on the way out and the comparison sites keep matching
  today's unqualified stored values until M6 rewrites them.
  Sites that will move in M6: `RepositoryVisibility.cs:60-61`, `GitHubProjectVisibility.cs:219-221`,
  `AccountActions.cs:47`, `ApiTokenActions.cs:51`, `RepositoryActions.cs:149,181`,
  `MyAccountsService.cs:44,53`.
- **Derive lazily, per provider, only for providers the user has linked** (D6b). A GitHub-only user
  must never cost a GitLab call; the per-provider sidebar (D4) means one unit needs one set.
- **Re-key the cache per (user, provider)** (D6c) — today `github-owners/{user.Id}`
  (`GitHubAccessService.cs:40`), which would collapse two providers' answers into one entry.
- **Cache failures on a short TTL of their own (~30s)** (D6c). Today only successes are cached
  (`:86`) and degraded results deliberately are not (`:90-91`) — fine at one provider, dangerous at
  three, because Bitbucket's 1,000 req/h per-token budget can be exhausted by failures alone and then
  stay exhausted.
- **Logging on degrade is part of the interface contract** (D6d), not left to each implementation.
  GitHub already logs all three cases (`:65`, `:129`, `:139`); nothing to add there, but
  `IForgeAccessService` must require it.
- ⚠️ While here, note but do **not** silently change: `Suspended` is honoured only in the backfill
  (`:185`), not in the owner set (`:80-84`), so a suspended installation still grants management
  (PRD §5.6). Fixing it is a behaviour change — raise it rather than folding it in.
- Leave `RepositoryVisibility.cs:35-44,76-87`, `GitHubProjectVisibility.cs:45-56` and
  `SparkVisibility.cs:19-36` where they are — the policy is already centralised; only its *source*
  moves.

**Verify:** build; no caller outside the GitHub implementation names an installation.

### As-built (704ab22a)

- `EForgeProvider` + `ForgeProviders` (canonical lowercase spelling, explicit mapping so renaming an
  enum member cannot change a published URL) and `ForgeOwner` (`provider:login`, colon-separated)
  live in `CodeCoverage.Library/Forge/`.
- `IForgeAccessService` / `IForgeAccessResolver` + `GitHubForgeAccessService` + `ForgeAccessResolver`
  in `CodeCoverage/Services/`. The GitHub-specific machinery (installation list, token refresh,
  backfill) deliberately stayed behind `IGitHubAccessService` — it has no counterpart on other forges.
- Negative caching landed as `FailureCacheDuration = 30s` with a `github-owners-failed/{userId}` key.
  Only the failure *state* is stored, never the degraded owner set; the short-circuit path does not
  extend the window; `InvalidateAsync` clears the memo so Resync isn't defeated by it.
- ⚠️ **Deviation worth knowing:** `SparkVisibility` still flattens `ForgeOwner` back to bare login
  strings, because its eight consumers compare against unqualified stored values. Commented as
  temporary; it disappears in M6f. Until then the safety property D6e buys is **not yet real** —
  registering a second provider before M6 would make two identically-named accounts
  indistinguishable.

---

## M2 — `IForgeClient` and `IForgeFeedbackPublisher` 🟩 *(ecd91c3d)*

Still GitHub-only.

- `IForgeClient`: diff/compare, file content, default branch, branch list, PR/MR head SHA. Extract
  from `GitHubDiffService.cs:94,134` and `GitHubContentService.cs:29`.
- `IForgeFeedbackPublisher`: the sticky comment and the commit status / check run. Extract from
  `PullRequestCommentGateway.cs:39-58` and `PublishFeedbackRecipient.cs:75-160`.
- **Define the verdict vocabulary explicitly** (PRD §5.3). GitHub's `neutral` has no equivalent on
  either forge; the interface declares its own set plus a per-provider mapping, rather than passing
  Octokit's enum through.
- **Split the payload by carrier**: the markdown table belongs to the comment; the status carries a
  number, a short description and a link. GitLab caps `description`/`target_url` at 255 chars and
  Bitbucket Code Insights allows 10 data cells, so a design that assumes rich markdown on the status
  cannot be implemented in stage 2.

**Verify:** build. A1 is not yet met (Octokit still reachable from the implementations, by design).

### As-built (ecd91c3d)

- `IForgeClient` (+ `ForgeAccess`) and `GitHubForgeClient` in `CodeCoverage/Services/`;
  `IForgeFeedbackPublisher` (+ `ForgeVerdict`, `EForgeOutcome`, `ForgeAccessDeniedException`) and
  `GitHubForgeFeedbackPublisher` in `CodeCoverage/Feedback/`.
- **No credential appears in any neutral signature.** The installation lookup moved inside
  `GitHubForgeClient`, removing five verbatim copies of the same five-line resolution from
  `CommitAssembler` (×2), `PatchCoverageCalculator`, `BaseResolver`, `PublishFeedbackRecipient` and
  `BrowseController`.
- `CheckAccessAsync` was added so callers can distinguish "no credential" from "the read found
  nothing" — the feedback pipeline needs that split to park a build as `Unavailable` without retry.
  The unavailable *message* is supplied by the provider, so it still names the GitHub App without
  the caller knowing which forge it is on.
- `ForgeAccessDeniedException` replaced catching `Octokit.ApiException` + status code in
  `PublishFeedbackRecipient`, which no longer references Octokit at all.
- ⚠️ **Behaviour change:** `GitHubForgeFeedbackPublisher.RequireInstallationAsync` **throws** when no
  installation exists, where the old code silently parked the build. Safe only because every caller
  now asks `CheckAccessAsync` first; a future caller that forgets gets a loud exception rather than
  a publish that reports success and posts nothing.
- `ScriptedDiffService` (test double) now implements `IForgeClient` and can script file content and
  the access check as well as diffs.
- **A1 status:** Octokit / `api.github.com` now appear only in GitHub-named implementation files,
  plus one explanatory doc-comment in `IForgeFeedbackPublisher`. Remaining stragglers are
  `PullRequestCommentGateway` / `PullRequestCommentPublisher` (GitHub implementations whose filenames
  are not GitHub-named — M11) and the installation/reconciler surface (M8).
- ⚠️ **Correction, 2026-09-20.** The line above overstated where this left things. The seams exist
  and are typed correctly, but **neither `IForgeClient` nor `IForgeFeedbackPublisher` dispatches** —
  both declare `Provider` and every one of their nine consumers injects the singular interface, so a
  second registration would silently win. `IForgeAccessService` is likewise bypassed by five of its
  six consumers. Measured and itemised in PRD §5.7; fixed in M2a. Read M1's and M2's as-built notes
  as *"the interface landed"*, not *"the migration to it is done"*.

---

## M2a — Converge the seams into `IPlatformIntegration` 🟦 *(D16/D17; blocks every platform)*

Supersedes the first draft of this milestone, which added one resolver per seam. With a single
interface there is one selection helper, not three.

Urgency is unchanged and independent of the redesign: until this lands, registering any second
implementation silently redirects nine call sites (PRD §5.7a), because `[Register]` emits plain
`AddScoped` and a singular injection receives the **last** registration.

- **Define the contract** in `CodeCoverage.Library`: `IPlatformIntegration` with `EPlatform Platform`,
  `ECapability[] Capabilities`, and the async members consolidated from `IForgeAccessService`,
  `IForgeClient` and `IForgeFeedbackPublisher`. Their signatures carry over — in particular the
  property that **no credential appears in any of them**, which is what made them real seams.
- **`GitHubPlatformIntegration` as a facade** (D16) over the existing per-concern services, which stay
  where they are for now and become internal to the GitHub library in M15. The ~30 existing GitHub
  test files keep targeting the internals and need no rewrite.
- **Convert the injection sites to `IEnumerable<IPlatformIntegration>`** — the 9 in PRD §5.7a plus the
  5 `IGitHubAccessService` bypasses in §5.7b. No call site names a platform after this milestone.
- **Two call shapes, one helper** (§6.10): fan-out (`GetAccounts`, `GetRepositories`) iterates all;
  select-one (compare, publish, file read) picks by `repository.Platform`. Until M6 gives entities a
  platform, selection answers GitHub from **one explicit, commented line** that throws the moment a
  second implementation registers — not a default argument, not a `FirstOrDefault` that picks GitHub
  because it is the only registration.
- **Resolve loudly.** A duplicate `Platform` among registrations is a wiring bug; throw like
  `ActionsResolver.cs:145-155` rather than `FirstOrDefault` as `ForgeAccessResolver` does today.

**Exit:** no singular injection of a platform interface anywhere; `grep -rn "IGitHubAccessService"`
in `CodeCoverage/` matches only the GitHub implementation's own internals.

---

## M2b — Conformance test for `Capabilities` 🟦 *(D17)*

`ECapability[]` is a runtime contract with no compiler behind it. An implementation that advertises a
capability it throws on, or quietly implements one it does not advertise, is a bug nothing else
catches — and the second direction is the dangerous one, because it works until a caller starts
trusting the array to decide what to skip.

- One test parameterised over **every registered `IPlatformIntegration`**, so a fourth platform is
  covered the day it registers without anyone remembering to extend the test.
- Assert both directions: every advertised capability's methods do **not** throw
  `NotSupportedException`, and every unadvertised capability's methods **do**.
- Assert `Platform` values are distinct across registrations — the duplicate-registration bug M2a
  guards against, caught at test time rather than in production.

---

## M2c — Fold the five unseamed capability areas into the interface 🟦

PRD §5.7c. Neutral operations with real cross-platform equivalents, implemented only for GitHub and
reachable only through GitHub-typed interfaces. Each becomes a member of `IPlatformIntegration`.

- **PR comment gateway** — already a narrow interface but Octokit-typed and sitting in neutral
  `Feedback/`. De-Octokit the signatures; `PullRequestCommentPublisher` drops `using Octokit;` and its
  status-code catch in favour of `ForgeAccessDeniedException`.
- **Repository enumeration** — `IInstallationRepositories.ListAsync(long installationId, …)` takes a
  GitHub installation id in a neutral-sounding signature. Becomes `GetRepositoriesAsync(owner)`, with
  the installation resolved inside the GitHub implementation.
- **`owner/name` → repository resolution** — `RepositoryResolver:104-131` calls GitHub directly and
  catches `Octokit.NotFoundException`. ⚠️ The rename-redirect behaviour it relies on is **not**
  portable: Bitbucket slugs are renameable *and reusable*.
- **Branch deletion** — `GitHubEventsRecipient.DeleteHeadBranchIfEnabled:393`. The policy is neutral;
  the ref delete is platform work. This is live, working production behaviour — do not remove it.
- **Webhook ingestion and event normalisation** — the largest piece; both recipients bind
  `IRecipient<GitHubWebhookMessage>` and 11 app files import the GitHub webhooks namespace. M8 already
  owns de-GitHub-ing the bus contract; treat M8 as the vehicle and this entry as its acceptance
  criteria.
- **Boards** — Projects V2 becomes `ListBoardsAsync` behind `ECapability.Boards` (D17), rather than
  staying app-side as the superseded D15 proposed. The `GitHubProject` *entity*, its model JSON and
  its security grants still live in the app; only the API surface moves.

⚠️ **Not on the interface, deliberately:** App installations. They are GitHub's mechanism for
answering a question `GetAccountsAsync` already asks neutrally; exposing them would leak the answer's
implementation into the question. They stay internal to the GitHub library.

---

## M3 — Login page, and delete the bespoke GitHub button 🟦

D3. Mostly deletion — `/sign-in` is already mounted and already renders server-reported providers
(PRD §4.1).

- Shell: remove the GitHub button (`shell.component.html:24-26`) and
  `shell.component.ts:12,19,42,50,53`; link to the sign-in route instead.
- Delete `services/github-login.service.ts` (the `'GitHub'` string literal at `:43,:46` and the
  GitHub-specific error map at `:25-32`).
- **Fix the dead redirect** (PRD §4.2): `app.config.ts:31` → `provideSparkAuth({ loginUrl: '/sign-in' })`,
  so the guard (`spark-auth.guard.ts:15`), the 401 interceptor (`spark-auth.interceptor.ts:26`) and
  the auth bar stop redirecting to a route that does not exist.
- Rework the "Reconnect GitHub" banner (`spark/home-extras.component.ts:11,27-42,79,91-106`) to
  re-challenge whichever provider needs it, from the same shared path.
- Generalise the copy: `App_Data/translations.json:14-91` — 7 of 13 `app.*` keys name GitHub.
  **Delete the six dead keys** first (unreferenced in `src/`, PRD §5.2) rather than translating them.
- Capability payload: add declared order and, when the request is authenticated, a `linked` flag —
  so one endpoint drives both the login page and M4's manage-logins screen
  (`GetAuthCapabilities.cs:43-50`).

**Not in scope here:** local email/password login is *already* switchable and already `Disabled` for
CodeCoverage (PRD §4.1). Nothing to build.

**Verify:** run the app (`dotnet run`, never `ng serve` — see CLAUDE.md) and drive `/sign-in` with
`playwright_node`. A4.

---

## M4 — Spark: confirmation mail, and the two linking modes 🟦

D2 and D9. All of this is Spark-side; CodeCoverage only chooses.

**4a — untie confirmation from local credentials** (PRD §6.3). `LocalCredentialEndpointFilter.cs:123-130`
strips `/confirmEmail` and `/resendConfirmationEmail`, which CodeCoverage needs precisely *because*
it runs `Disabled`. Make confirmation orthogonal to `LocalCredentials`, and change
`GetAuthCapabilities.cs:32-41` in the same commit — it derives the mode from route presence, so the
two must move together or the client reports the wrong mode.

**4b — the option.** Add to `SparkAuthenticationOptions` (today a single property, `:43`):
`ExternalLoginLinking = Disabled | WhenSignedIn | ConfirmByEmail`.

**4c — the duplicate-email branch.** Today `UserStore.CreateAsync` returns `DuplicateEmail` from the
compare-exchange reservation (`UserStore.cs:68-95`) and the callback turns it into
`account_creation_failed` (`SparkAuthenticationExtensions.cs:181-182`). Replace with an explicit
"this email belongs to an existing user" branch that dispatches by mode, and give `Disabled` an
honest error code of its own.

**4d — `WhenSignedIn`.** Link/unlink endpoints for the current principal; `AddLoginAsync` on a
signed-in user; manage-logins UI in `ng-spark-auth`. **Carry a last-login guard** — MintPlayer's
`AccountRepository.cs:325-338` has none and a passwordless OAuth user can lock themselves out
permanently (PRD §4.7). Distinguish `LoginAlreadyAssociated` from transport failure.

**4e — `ConfirmByEmail`.** The callback does **not** sign in. It writes a pending-link document
`(userId, loginProvider, providerKey, expiresAt)` keyed by a single-use token, and mails a
confirmation. Confirming verifies the token, **re-checks the `providerKey` against what was
captured**, links, signs in. Two non-negotiables (PRD §6.2):
1. the mail goes to the address **already stored on the existing account**, never the one the new
   provider just asserted;
2. the `providerKey` is re-verified on confirm, or the link is a bearer token for any identity.

**4f — provisioning and the three cases.** Stop setting `EmailConfirmed = true` by fiat
(`SparkAuthenticationExtensions.cs:178`). Case (1) create + send + link; case (2) resend; case (3)
sign in. Expose `SignIn.RequireConfirmedAccount` deliberately.

**4g — generalise the verified-email gate** (`:159-168`). Drop `urn:github:email_verified`;
`GitHubAuthenticationExtensions.cs:54-110` keeps its `/user/emails` call but emits the standard
`email_verified` claim. A8.

**4h — the two latent defects in the same method.** A non-`Success` `ExternalLoginSignInAsync` for a
linked user must not fall into provisioning (`:143-152`, recorded at `reauth-on-401.md:80-84`);
check the `AddLoginAsync`/`SignInAsync` results (`:184-185`); validate the `provider` query value
against registered schemes (`:99-122`). A9.

**4i — the SMTP transport (D9).** CodeCoverage's sender talks to an **SMTP server running as its own
container**, added to `docker-compose.yml` on the VPS for isolation. Work items: pick the image;
compose service + network so only the app can reach it; SPF/DKIM/rDNS for `coverage.mintplayer.com`
or mail lands in spam; credentials via the existing secret mechanism (never committed); a
health/readiness signal. ⚠️ Deliverability, not wiring, is the risk here — a self-hosted SMTP sender
on a VPS IP is routinely rejected or silently junked, and the symptom looks identical to the no-op
sender bug in 4i. Verify against a real external mailbox, not just a local capture.

**4k — mail contract.** Spark ships the contract and templates and **must not register a transport**.
CodeCoverage registers a real `IEmailSender<TUser>`. **Startup guard:** `ConfirmByEmail` (or
confirmation required) + a no-op sender ⇒ **throw at startup**, per SP2. Precedent for the style:
`LocalCredentialEndpointFilter.cs:87-98`. Templates, not interpolated strings; the app is already
localized through `translations.json`.

**Verify:** A5, A6, A7, A8, A9. A5 asserts on a **captured message**, never on a method returning
success — the framework hands you a silent no-op by default.

---

## ~~M5 — Connected orgs: make the grant explicit~~ ❌ **DISSOLVED by D6a**

This milestone proposed introducing an explicit connected-org record. **D6a decided the opposite** —
the owner set stays derived live from the forge, with no stored authorization record — so there is
nothing here to build. Removed rather than left in place, because a milestone nobody should implement
is worse than no milestone.

What the milestone was really worried about survives elsewhere and is not lost:

- *"Do not widen management rights"* — carried into M1's contract
  (`IForgeAccessService.IsOwnerAllowedAsync` documents that today's check is set membership with no
  role check) and recorded as an accepted risk in PRD §6.7 D6a.
- *"An org that never opted in"* — answered by D6a: an unconnected org has no stored documents, so
  membership in the owner set grants access to nothing.
- The credential a forge needs when no user is present is D6b/Q2, and is stage-2 work (GitLab group
  access token), not stage 1.

---

## M6 — Provider-qualified document ids + migration 🟦 *(D7 re-confirmed; gated by SP6)*

**D7 re-confirmed 2026-09-19 against the measured number: full re-key, rehearsed first.**

⚠️ **SP1 changed this milestone's shape.** 199,917 documents carry the GitHub repo id in their key
(`FileCoverages`, `Builds`, `BuildTreeSummaries` and `CommitAssemblies` all nest under
`Commits/{repoId}/…`), plus 683 attachments on `Builds`. Raven ids are immutable, so this is
put-new + move-attachments + delete-old per document — **not** the `PatchByQueryOperation` style
PRD §6.8 prescribes, and not trivially re-runnable.

⚠️ **It must not run in the startup path.** `ISparkMigration.UpAsync` runs at startup and a throw
aborts it — `M_202609190900` documents that failure taking the site down. A ~200k-document re-key
with attachment moves cannot sit there. **Decide the mechanism before writing code:** an
out-of-band admin command run against a stopped/quiesced app, or an accepted maintenance window with
a resumable migration. Do not default to "it's a migration, so it goes in `Migrations/`".

**Resumability is a requirement, not a nicety.** A half-finished run leaves reports orphaned from
their builds. Every step must be safe to re-enter: skip when the target id already exists, and never
delete the source until the target *and* its attachments are confirmed present.

### Sub-milestones

- **SP6 — rehearse.** Restore a copy of production, run the whole migration against it, and record
  wall-clock, peak memory, and what a mid-run kill leaves behind. Gate M6 on this. *(This is the
  step the user asked for; it is not optional.)*
- **M6a — small collections** (~1,944 docs): `Repositories` 172, `Accounts` 2, `PullRequestFeedbacks`
  43, `Commits` 804, `Builds` 303, `BuildTreeSummaries` 482, `CommitAssemblies` 138. `Commits` roots
  the nested tree, so sequence by id depth and keep parents and children consistent within a run.
- **M6b — `FileCoverages`** (197,973) in batches, with progress recorded so a restart resumes.
- **M6c — attachments**: 683 on `Builds` (503 unique). Copy to the new document id, verify, then
  delete the old. A missing report is silent data loss — verify by count *and* by unique hash.
- **M6d — verification script**, shipped with the milestone: per-collection counts before/after,
  zero documents left on a legacy id, zero attachments orphaned. Runs against production (A10).
- **M6f — the three owner-login field rewrites, moved here from M1** (D6e) so the PR ships one
  migration: `Repository.OwnerLogin` (172 docs), `Account.Login` (2), `ApiToken.AccountLogin` (2,
  found via the authorization inventory rather than the id analysis) all become `provider:owner`
  with a **colon** — `github:mintplayer`, `gitlab:group/subgroup`. A colon rather than a slash
  because GitLab namespaces nest 20 deep and are themselves slash-delimited; this deliberately
  diverges from the id spelling and must not be "tidied" to match. These are *field* changes, so
  unlike the re-key they **can** use `PatchByQueryOperation` — one per collection, so a partial
  failure names the collection that stopped. The six comparison sites listed in M1 flip in the same
  commit.
- **M6e — decide the prefix's scope** (D5 detail surfaced by SP1): `GitHubProjects` is keyed by a
  globally-unique GraphQL node id and `ApiTokens` uses Raven's id generator with forge ids in
  *fields*. State explicitly whether the provider prefix is universal or applied only where a
  collision is possible, and write the reason down.


**This is the milestone that can go wrong.** It re-keys live documents in the production `Coverage`
database.

- Re-key `Repositories/{gitHubId}` (`Repository.cs:165`), `Accounts/{gitHubId}` (`Account.cs:49`),
  `Commits/{repoGitHubId}/{sha}` (`Commit.cs:124`), `Build.DocumentId(...)` (`Build.cs:147`),
  `PullRequestFeedback/{repoGitHubId}/{prNumber}` (`PullRequestFeedback.cs:86`).
- Provider-qualify `OwnerLogin` — `owner/repo` is not unique across forges.
- Ships **with its verification script** (M6d), not after it.
- ⚠️ **PRD §6.8's `PatchByQueryOperation` rule does NOT apply to the re-key itself** — a patch cannot
  change a document id. It still applies to any *field* change riding along (e.g. provider-qualifying
  `OwnerLogin`), which should stay in JS-over-JSON so this PR's renames cannot break it.
- Since a migration ships regardless, fold any other wanted model change into the same PR rather
  than contorting the design to avoid one.
- ⚠️ **Sweep the client in the same commit.** Spark POs are read by string name
  (`valueFor(item, 'GitHubId')`); a rename produces **no TypeScript error**, just `undefined`. This
  has already bitten the repo (`repo-name-renderer.component.ts:11-19`).

**Verify:** A2 (a test storing two same-numeric-id repos from different providers) and A10 (counts
before/after, **against production**).

---

## M7 — Provider-segmented routes, sidebar units and badges 🟦 *(D4, D11, D13 decided)*

**One sidebar program unit per provider** (D4), each owning its own account list — not a unioned
list. GitHub is the only populated unit in stage 1, so this milestone proves the shape without a
second provider to fill it. `App_Data/programUnits.json` grows a unit per provider; ⚠️ per
`docs/code-coverage/program-units-PRD.md` there is no packaged sidebar and no object-id PO unit, so check
what the subsystem actually supports before designing the unit.


- Routes become `/{provider}/{owner}/{name}` using full provider names (D11).
  Touches `app.routes.ts:36-40`, `vanity-redirects.ts`, and every
  `fullName.split('/') → [owner, name]` site: `po-detail-page.component.ts:56-61`,
  `commit-files-extras.component.ts:42`, `short-sha-renderer.component.ts:48`.
- Server: `BrowseController`, `BadgeController`, `RepoSettingsController`, and `UploadsController`
  (which rejects anything that is not exactly `owner/name`).
- **Remove** the legacy two-segment badge route rather than aliasing it (D13, PRD §5.4), and
  update every badge URL we publish: our repos' READMEs, the badge panel's copy-paste snippet
  (`repo-badge-panel.component.ts:98,105-106`), and the PR-comment renderer.
- Accept knowingly: badges in **existing PR comments** will 404 on old PRs, and GitHub's image proxy
  may serve a cached copy until it expires.

**Verify:** A11 — grep that no source we control still emits the two-segment form.

---

## M8 — De-GitHub the bus contract 🟦

`GitHubWebhookMessage.cs:15-16` declares `required long InstallationId` and `RepositoryFullName`, so
every recipient and the `spark-github-all` queue name depend on a GitHub-shaped envelope.

- Replace `InstallationId` with an opaque per-provider tenancy key; generalise the queue name.
- ⚠️ Check `reference_spark_messaging_stranded_processing` before touching queue names, and the
  single-subscription mode now on master — a new queue name is an ordinary modelling decision again,
  but renaming one is not free.

**Verify:** A3. Build; no recipient signature names GitHub.

---

## M9 — Generalise the uploader surface 🟦

- `GitHubOidc.cs` + `UploadsController.cs:116-155`: the OIDC claim mapping
  (`repository`, `repository_id`, `repository_owner`, `repository_visibility`, `run_id`,
  `run_attempt`) becomes a per-provider claim map. GitLab's `id_tokens` maps 1:1 with renamed claims
  (`project_id`, `project_path`, `namespace_path`, `project_visibility`) — shape for that now, build
  it in stage 2.
- `BuildInfo.runId/runAttempt/workflowName` (`browse.service.ts:47-56`) is GitHub Actions run
  identity on the wire. Generalise or confirm it is unused — ⚠️ the client inventory could not find
  a template binding `runId`.
- Keep the stored `covt_` token path; it is the fallback for any forge without usable OIDC.

---

## M10 — The setup panel 🟦

`repo-setup-panel.component.ts` emits GitHub Actions YAML across seven tabs and nothing else
(`:27,65-70,71-86,93-187`, plus "repository secret" copy at `:30`).

Restructure so the snippet is chosen by **provider × language** rather than language alone, with
GitHub the only populated provider in stage 1. Without this, stage 2 has nowhere to put a GitLab CI
snippet.

---

## M11 — Rename the GitHub-shaped client surface 🟦

- `AccountsResponse.gitHubAppUrl` / `gitHubReauthRequired` (`accounts.service.ts:14-24`) → provider-neutral.
- `app-installed-renderer.component.ts` → a provider-appropriate "connected" state (M5's record).
- `account-avatar-renderer.component.ts:44` — `Type === 'User'` is GitHub's account-type vocabulary.
- These are hand-written DTOs with no codegen link to the server (PRD §5.2); change both ends together.

---

## M15 — Three platform libraries 🟦 *(D14, D16; after M2a, M2b, M2c, M8)*

Three projects as siblings of the app — `CodeCoverage.{Github,Gitlab,Bitbucket}Integration` — with
`IsPackable=false` (D14: split now, publish only when a second platform ships and the entity
contracts have stopped moving). Only the GitHub one has content in stage 1; the other two are created
with a stub implementation so the registration pattern and the capability contract are proven by more
than one case, and so M2b's conformance test has something to iterate.

- Dependency direction: `CodeCoverage` → `*Integration` → `CodeCoverage.Library` (which holds
  `IPlatformIntegration`). Linear, no cycles.
- Each library exposes **one extension method** — `AddGithubIntegration(this ISparkBuilder, …)` —
  modelled line-for-line on
  `libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/Extensions/SparkBuilderExtensions.cs:13-62`. The app
  calls all three.
- The OIDC scheme is contributed here as startup configuration, not as an interface method (§6.10).
- Remove the Octokit `PackageReference` from `CodeCoverage.csproj` — **the milestone's real exit
  criterion**, converting A1 from a review promise into a compile error.
- ⚠️ Verify the new assemblies' `[GenerateIndex]` indexes are actually emitted. The cross-assembly
  filter keys on the attribute-host AssemblyRef and once made HR's indexes vanish with **no
  diagnostic** (PRD §6.9). Check the generated output; assume nothing.
- ⚠️ `security.json` and model sync are **app-only by design** (PRD §6.9) — platform rights are
  authored in the app, and a library shipping entities forces a model re-sync the app must commit.
- Add all three to `MintPlayer.Spark.slnx`, the `Dockerfile` COPY lines, and
  `code-coverage-deploy.yml`'s `paths:` list — three hand-maintained closures, each failing quietly
  when missed.
- **Not** under `libs/` while unpublished: the PR version gate filters by path, not packability, so
  `libs/**` would demand a `<Version>` bump on every touch. ⚠️ Never name a directory `coverage`.

**Exit:** `grep -rn "Octokit" apps/CodeCoverage/CodeCoverage/` returns nothing.

## M12 — Docs 🟦

- `product-overview.md`: retire the v1 non-goal at `:23`; rewrite §6.1/§6.3, which assert
  GitHub-as-authority as **policy** (`:157`, `:170`) — restate what is still true (the forge decides
  who) and what M5 changed (the grant is now explicit).
- `apps/CodeCoverage/README.md` — 43 GitHub hits.
- Record as-built deviations in the PRD, per the `program-units-PRD.md` §9 convention.
- A12.

---

## M13 — Verification sweep 🟦

**The only full test run.** Everything before this is verified by reading and building.

- `dotnet test` for the solution; the Angular suite for the workspace.
- Capture to a file, never pipe the only copy (`cmd > log 2>&1; echo "EXIT: $?"`).
- Drive the real app with `playwright_node`: sign-in page, a provider round trip, and the
  `ConfirmByEmail` path end to end with a captured message.
- ⚠️ E2E shares one rate-limit bucket (150/10s on 127.0.0.1); non-deterministic failures here are
  pressure, not regression.
- Walk A1–A12.

---

## M14 — Version bumps and PR 🟦

- `libs/` version bump is a **CI-only gate** — a green `dotnet test` does not catch it.
- **Majors do not move.** npm major = Angular major, NuGet major = .NET major. These are
  preview-range changes to `MintPlayer.Spark.Authorization` and `@mintplayer/ng-spark-auth`; a
  wrongly published major is burned permanently.
- GitGuardian will flag `modelHashes.json` — always a false positive; dismiss, never "fix".

---

## Ordering

M0 done. SP1 did not shrink M6 — it grew it (199,917 documents carry the GitHub repo id in their
key; PRD §7.1).

**M1 → M2 → M2a → M2b → M2c/M8 → M15** is the abstraction spine and is strictly ordered. M2a is the
one piece that is *urgent* rather than merely sequenced: until it lands, registering any second
implementation silently redirects nine call sites (PRD §5.7a), so it gates all of stage 2 and 3, not
just this PR. M2b comes straight after M2a so the capability contract is enforced from the moment it
exists, rather than after three implementations have already drifted from it. M15 is last on the
spine because it can only move code the interface already covers.

**M6 → M7** is strictly ordered (ids before routes). M5 is dissolved, so M6 no longer waits on
anything but D6f. M6 also unblocks the *real* provider selector in M2a, which until then answers
GitHub from one explicit line.

M3 and M4 are independent of the forge spine and can land early; M4 is the largest single milestone
and the one most worth committing in pieces (4a–4k). M9/M10/M11 depend on M6's renames. M12, M13,
M14 last, in that order.

Only D6f (fork-PR uploads) still blocks work. D16 and D17 are decided and shape M2a/M2b/M2c/M15.
D14 remains a recommendation: if published NuGet packages are wanted after all, M15 grows a fourth
contracts project — because `IPlatformIntegration` is typed on this app's domain entities — and the
placement moves to `libs/`. PRD §6.9 has what that costs.

---

## Open: one vocabulary, two words (D18)

D16 introduced `IPlatformIntegration`, `EPlatform`, `ECapability`. M1 shipped `EForgeProvider`,
`ForgeProviders`, `ForgeOwner`, `ForgeAccess`, `ForgeVerdict`, `EForgeOutcome`,
`ForgeAccessDeniedException`. The PRD says "provider" roughly a hundred times and "forge" in its own
title. Three words for one concept, and one of them is overloaded.

**The overload is the real problem.** ASP.NET Identity already uses *provider* to mean an external
login provider — `SparkUser.Logins[].LoginProvider`, and D3's "one button per registered provider"
is literally that list. `ForgeAccessResolver` reads linked providers off exactly that field. So
"provider" currently means both *the ASP.NET authentication scheme* and *the forge we integrate
with*, which happen to be 1:1 today and will not obviously stay so — a user can sign in with GitHub
and have no GitHub repositories at all.

**Recommendation: `platform` for the integration, `provider` only for the ASP.NET login provider.**

- `IPlatformIntegration`, `EPlatform`, `ECapability` — the integration surface (D16's own words).
- `EForgeProvider` → `EPlatform`; `ForgeOwner` → `PlatformOwner`; `ForgeAccess`/`ForgeVerdict`/
  `EForgeOutcome`/`ForgeAccessDeniedException` → `Platform*`.
- `LoginProvider` stays untouched — it is ASP.NET's, and `EPlatform.TryParse(login.LoginProvider)`
  becomes an honest conversion between two different things rather than a redundant-looking one.
- Canonical strings stay `github` / `gitlab` / `bitbucket` (D11), so **no URL, document id or badge
  changes** — this is a rename of C# identifiers and prose only.

**Cost:** cheap now, expensive later. The affected types are a few files from M1/M2 plus their
usages, and nothing is published (D14). After M6 re-keys documents and M7 publishes routes, a rename
starts touching things with external consequences.

**The trap in the recommendation:** "forge" is the more precise word — it means specifically a
code-hosting platform, where "platform" means almost anything — and the PRD's title and filenames use
it. Renaming to the vaguer word loses a little precision, and leaves `multi-forge-PRD.md` named after
a term the code no longer uses. Accepted because D16's wording is the owner's and internal
consistency beats lexical precision.

**Alternative if this is not wanted:** keep `EForgeProvider` and name the property
`EForgeProvider Provider { get; }` on `IPlatformIntegration`. Cheapest possible, and leaves the
interface the only member of the "platform" vocabulary.

**Not decided — do not act on the recommendation without an answer.** M2a is the milestone that
would carry the rename, since it rewrites these types anyway.

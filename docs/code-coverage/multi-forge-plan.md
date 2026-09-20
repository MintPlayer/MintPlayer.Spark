# Plan — Stage 1: de-couple CodeCoverage from GitHub, rebuild sign-in and linking

Companion to [multi-forge-PRD.md](multi-forge-PRD.md). Issue
[#422](https://github.com/MintPlayer/MintPlayer.Spark/issues/422).

**One PR** off `master`, branch `issue-422-forge-abstraction`. Commits per milestone; **the test
suite runs once, at M13** — intermediate milestones are verified by reading the code and building.
GitLab and Bitbucket providers are stages 2 and 3 and are *not* in this PR (PRD §1, D1).

**Blocked on decisions**: nothing. D6f resolved 2026-09-20 — fork PRs upload unauthenticated into a
PR-scoped namespace on public repos only (PRD §6.7). ⚠️ **That decision lands on M6a**: the
`pr/{n}/` segment is a document-id shape and Raven ids are immutable, so it must be reserved in the
migration's target scheme or the 199,917-document re-key is paid twice. D1–D5, D6a–e, D7, D9, D11, D13, **D16 and D17** are decided;
D8, D10, D12 and D14 carry recommendations nothing in stage 1 depends on. D15 is partly superseded by
D17.

✅ **Vocabulary (D18): "forge", not "platform".** The thing we integrate with is a *forge*; bare
*provider* is reserved for ASP.NET Identity's external login provider. Everything M1/M2 shipped keeps
its name; only D16/D17's new identifiers changed. See the closing note.

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

## M2a — Converge the seams into `IForgeIntegration` 🟩 *(821f7b1d, ed9e8f2f, + conversion)*

Supersedes the first draft of this milestone, which added one resolver per seam. With a single
interface there is one selection helper, not three.

Urgency is unchanged and independent of the redesign: until this lands, registering any second
implementation silently redirects nine call sites (PRD §5.7a), because `[Register]` emits plain
`AddScoped` and a singular injection receives the **last** registration.

- **Define the contract** in `CodeCoverage.Library`: `IForgeIntegration` with `EForgeProvider Provider`,
  `EForgeCapability[] Capabilities`, and the async members consolidated from `IForgeAccessService`,
  `IForgeClient` and `IForgeFeedbackPublisher`. Their signatures carry over — in particular the
  property that **no credential appears in any of them**, which is what made them real seams.
- **`GitHubForgeIntegration` as a facade** (D16) over the existing per-concern services, which stay
  where they are for now and become internal to the GitHub library in M15. The ~30 existing GitHub
  test files keep targeting the internals and need no rewrite.
- **Convert the injection sites to `IEnumerable<IForgeIntegration>`** — the 9 in PRD §5.7a plus the
  5 `IGitHubAccessService` bypasses in §5.7b. No call site names a forge after this milestone.
- **Two call shapes, one helper** (§6.10): fan-out (`GetAccounts`, `GetRepositories`) iterates all;
  select-one (compare, publish, file read) picks by `repository.Provider`. Until M6 gives entities a
  forge, selection answers GitHub from **one explicit, commented line** that throws the moment a
  second implementation registers — not a default argument, not a `FirstOrDefault` that picks GitHub
  because it is the only registration.
- **Fix D19 while here — suspended installations still confer management rights.** The owner set is
  built from `installations` unfiltered (`GitHubAccessService.cs:105-109`); the backfill on the same
  array already filters `.Where(i => !i.Suspended)` (`:235`). Add the same filter at the owner-set
  site. ⚠️ **This is a behaviour change, not a refactor:** a user whose installation is suspended
  loses management rights the moment it ships. That is the intent — but it is the one change in
  M2a that can take access away from someone, so it gets its own commit and its own test.
- **Resolve loudly.** A duplicate `Provider` among registrations is a wiring bug; throw like
  `ActionsResolver.cs:145-155` rather than `FirstOrDefault` as `ForgeAccessResolver` does today.

**Exit:** no singular injection of a forge interface anywhere; `grep -rn "IGitHubAccessService"`
in `CodeCoverage/` matches only the GitHub implementation's own internals.


### As-built

- `IForgeIntegration` + `IForgeIntegrationResolver` live in `CodeCoverage.Library/Forge/`, where the
  forge libraries will need them. `CommitComparison` / `DiffFile` moved with them.
- All **14** consumers converted. Repository-scoped calls read `forges.For(repository)`; fan-out
  goes through `ForgeFanOut`, which iterates the viewer's **linked** forges, not every registered one.
- The resolver **throws** on a duplicate `Provider` rather than taking the first, and
  `ForgeAccessResolver` is deleted.
- `ProviderOf(repository)` answers GitHub from one explicit line until M6. Written so that
  registering a second forge before M6 fails loudly rather than mis-routing quietly.
- The temporary `ForgeOwner` → bare-login flattening is now in **one** extension method
  (`ForgeFanOut.GetAllowedOwnerLoginsAsync`) instead of four call sites — one thing for M6f to delete.
- ⚠️ **Behaviour change:** `MyAccountsService` reports reauth if **any** linked forge needs it,
  not GitHub specifically.
- **D19 shipped separately** (`821f7b1d`) because it takes access away from anyone whose
  installation is suspended. `BuildOwnerSet` extracted as a pure function so the rule is testable.
- Test doubles: `ScriptedDiffService` implements the integration **and** the resolver, so its six
  call sites pass one object. `SingleForgeResolver` wraps a *real* integration for the two fixtures
  that must keep production behaviour in the loop — `RepoSettingsControllerTests` counts the
  authorization call, and the feedback guard tests assert the reaction to a genuine "no
  installation" answer.
- ⚠️ **A pre-existing failure surfaced and was fixed.** `Refresh_failure_after_401_…` asserted a
  degraded lookup cached *nothing*; M1's negative cache (`704ab22a`) made that false, and because
  both halves landed in the same milestone the suite was never run between them. Now pinned to the
  distinction that matters: the failure *state* is remembered, the degraded owner *set* is not.
- **562 tests pass.**

---

## M2b — Conformance test for `Capabilities` 🟩

`EForgeCapability[]` is a runtime contract with no compiler behind it. An implementation that advertises a
capability it throws on, or quietly implements one it does not advertise, is a bug nothing else
catches — and the second direction is the dangerous one, because it works until a caller starts
trusting the array to decide what to skip.

- One test parameterised over **every registered `IForgeIntegration`**, so a fourth forge is
  covered the day it registers without anyone remembering to extend the test.
- Assert both directions: every advertised capability's methods do **not** throw
  `NotSupportedException`, and every unadvertised capability's methods **do**.
- Assert `Provider` values are distinct across registrations — the duplicate-registration bug M2a
  guards against, caught at test time rather than in production.


### As-built

- Implementations are **discovered by reflection**, not listed, so a fourth forge is covered the day
  its assembly is referenced.
- ⚠️ **The first draft passed vacuously.** With one implementation advertising every capability
  that has members, the "unadvertised must throw" direction had nothing to iterate. The rules are
  therefore extracted and also run against **three deliberately non-conforming doubles** — supports
  more than it admits, advertises more than it supports, declares a capability with no members — so
  each rule is *known* to bite.
- A separate test asserts discovery returns something, because a namespace move would otherwise make
  every theory pass vacuously and silently stop enforcing the contract.
- `Boards` and `CiIdentity` stay in the enum ahead of their members so the third rule has something
  to catch; declaring one early now fails.

---

## M2c — Fold the five unseamed capability areas into the interface 🟦

PRD §5.7c. Neutral operations with real cross-forge equivalents, implemented only for GitHub and
reachable only through GitHub-typed interfaces. Each becomes a member of `IForgeIntegration`.

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
  the ref delete is forge work. This is live, working production behaviour — do not remove it.
- **Webhook ingestion and event normalisation** — the largest piece; both recipients bind
  `IRecipient<GitHubWebhookMessage>` and 11 app files import the GitHub webhooks namespace. M8 already
  owns de-GitHub-ing the bus contract; treat M8 as the vehicle and this entry as its acceptance
  criteria.
- **Boards** — Projects V2 becomes `ListBoardsAsync` behind `EForgeCapability.Boards` (D17), rather than
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


### ⚠️ Two lines that MUST change with the forge segment — measured 2026-09-20

Verified by reading; both fail **silently**, and neither is in the re-key itself. The `pr/{n}/`
segment of D6f is harmless by comparison — every parser and prefix sweep in the app absorbs it — but
**`github/` is not**, because it shifts the positional meaning of the id's second segment.

1. **`Actions/BuildActions.cs:31-37` — `RepositoryIdFromCommitId`.** It recovers the repository by
   *position*: `parts[1]` must parse as a `long`. With `Commits/github/{repoId}/{sha}` it reads
   `"github"`, `long.TryParse` fails, the method returns null, and `IsAllowedAsync` (`:25-29`)
   returns **false for every build in the system**. It fails *closed*, so nothing leaks — the Builds
   grid simply goes blank app-wide, which is the kind of failure that gets diagnosed as a broken
   index.
2. **`Ingestion/DeleteRepositoryDataRecipient.cs:88`** sweeps the literal prefix
   `Commits/{RepositoryGitHubId}/`. With the forge segment that prefix matches nothing, so deleting
   a repository would **orphan every commit, build, file and tree document it owns** — silently, and
   permanently, since the owning `Repository` document is gone and nothing else references them.
   Its doc comment at `:23` hard-codes the old shape.

Everything else is safe and was checked: the other seventeen `'/'`-splitters parse `owner/name`, file
paths or attachment names rather than ids; `BuildFinalizer.cs:99-104` slices *relative* to a
composed prefix so upstream segments are absorbed; every prefix sweep composes its prefix from a real
id; no index parses ids at all; and `DeletePullRequestBuildsRecipient` selects by **field**
(`Commits_ByRepository` on `Repository` + `PullRequestNumber`), not by id shape — so a fork PR commit
is swept on merge provided its `PullRequestNumber` is set.

Nine document-id shapes gain the new segments, and all nine inherit it from **one** change to
`Commit.DocumentId` (`Commit.cs:124`) — every other helper is a suffix on a passed-in commit or build
id. The change is one line; the two above are what make it safe.


### Sub-milestones

- **SP6 — rehearse.** Restore a copy of production, run the whole migration against it, and record
  wall-clock, peak memory, and what a mid-run kill leaves behind. Gate M6 on this. *(This is the
  step the user asked for; it is not optional.)*
- **M6a — small collections** (~1,944 docs): `Repositories` 172, `Accounts` 2, `PullRequestFeedbacks`
  43, `Commits` 804, `Builds` 303, `BuildTreeSummaries` 482, `CommitAssemblies` 138. `Commits` roots
  the nested tree, so sequence by id depth and keep parents and children consistent within a run.
  - ⚠️ **Reserve the fork segment now** (D6f). The target id scheme must admit
    `Commits/github/{repoId}/pr/{n}/{sha}` alongside `Commits/github/{repoId}/{sha}`, and every id
    *parser* must tolerate the extra segment. **No existing document is re-keyed into it** — nothing
    in production is a fork upload today — but the scheme has to have room, because Raven ids are
    immutable and this migration touches 199,917 documents exactly once. Deciding later costs the
    whole re-key a second time. This is the only part of M6 that exists purely for a feature M6 does
    not itself ship.
- **M6b — `FileCoverages`** (197,973) in batches, with progress recorded so a restart resumes.
- **M6c — attachments**: 683 on `Builds` (503 unique). Copy to the new document id, verify, then
  delete the old. A missing report is silent data loss — verify by count *and* by unique hash.
- **M6d — verification script**, shipped with the milestone: per-collection counts before/after,
  zero documents left on a legacy id, zero attachments orphaned. Runs against production (A10).
- **M6e — decide the prefix's scope** (D5 detail surfaced by SP1): `GitHubProjects` is keyed by a
  globally-unique GraphQL node id and `ApiTokens` uses Raven's id generator with forge ids in
  *fields*. State explicitly whether the provider prefix is universal or applied only where a
  collision is possible, and write the reason down.
- **M6f — the three owner-login field rewrites, moved here from M1** (D6e) so the PR ships one
  migration: `Repository.OwnerLogin` (172 docs), `Account.Login` (2), `ApiToken.AccountLogin` (2,
  found via the authorization inventory rather than the id analysis) all become `provider:owner`
  with a **colon** — `github:mintplayer`, `gitlab:group/subgroup`. A colon rather than a slash
  because GitLab namespaces nest 20 deep and are themselves slash-delimited; this deliberately
  diverges from the id spelling and must not be "tidied" to match. These are *field* changes, so
  unlike the re-key they **can** use `PatchByQueryOperation` — one per collection, so a partial
  failure names the collection that stopped. The six comparison sites listed in M1 flip in the same
  commit.
- **M6g — delete the compatibility shims D22 releases.** Runs *after* the re-key. ⚠️ **See M17: the
  `LegacyBranchCompatibility` half should NOT ship in the same deployment as the re-key**, because it
  is what lets the app tolerate an unfinished migration.
  - `Services/LegacyBranchCompatibility.cs` + its hook at `Program.cs:313`. ⚠️ **Confirm
    `M_202609190900_BranchesBecomePerLineArmSets` completed in production first.** The shim exists so
    the app does not depend on migration state; deleting it re-couples them, and a half-migrated
    `FileCoverage` would then render wrong rather than render old.
  - `ApiToken.AccountLogin` (`:63-68`) and its comparison branch in `UploadsController`, after the
    migration backfills `AccountGitHubId`. ⚠️ Any token the backfill cannot resolve **stops working** —
    fine under D22, but **count and report them**, do not discover it from a support question.
  - ⚠️ **Not** `Commit.ParentSha`'s trust rules (`Commit.cs:55`, `CommitAssembler.cs:371-387`). That is
    a data-quality guard against values already stored, not backward compatibility, and deleting it
    would trust a value the code knows may be wrong.
  - Rule of thumb: *if deleting this shim would make something already in RavenDB read wrong, migrate
    first and delete second.*


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

## M8 — A neutral webhook contract 🟦 *(D21; grew 2026-09-20)*

⚠️ **This milestone grew.** It was "drop `required long InstallationId` and `RepositoryFullName`
(`GitHubWebhookMessage.cs:15-16`), rename the `spark-github-all` queue". D21 makes it a **multi-forge
recipient model**: one recipient class handles a logical event from any forge, with one method, and
adding a forge edits no consumer. That is a larger thing than a rename, and it is recorded as a scope
increase rather than folded into the old bullets. Design and rationale in PRD §6.11.

### The shape

- **`ForgeWebhookMessage<TEvent>`** carrying the forge, the normalised event and the raw payload:
  consumers write `IRecipient<ForgeWebhookMessage<IssueOpened>>` and nothing else.
- **A canonical event model** — likely `PushReceived`, `PullRequestOpened`, `PullRequestClosed`,
  `IssueOpened`, `RepositoryRenamed`. ⚠️ This has to be **designed, not discovered**, and it is the
  part of M8 most likely to be got wrong by enumerating GitHub's events and calling them canonical.
- **Normalisation in each forge library** (M15), not in the app: "verify this forge's signature,
  parse its payload, publish a neutral event" is the webhook half of `IForgeIntegration`.
- **Forge-specific messages stay** for events only one forge has — GitHub's `check_run`, Projects V2.
  A recipient handling those is honestly forge-specific. Never a neutral name over a single-forge
  concept (D17's reasoning, applied to messages).
- `RawJson` is the escape hatch. A handler that reaches for it has become forge-specific again and
  should say so.

### Constraints that bind the implementation

- ⚠️ **Pin the queue name.** Both existing records carry `[MessageQueue("spark-github-all")]`
  deliberately: without it the name derives from the CLR type, and for a constructed generic that
  embeds the argument's **assembly-qualified name**. One real database accumulated **seven**
  `SparkMessaging-*` definitions, six orphans of exactly this shape, including separate
  `Version=2.0.0.0` and `Version=3.0.0.0` variants of one event. `ForgeWebhookMessage<TEvent>` is the
  same hazard.
- ⚠️ **The subscription budget is a hard limit.** Production runs RavenDB **Community**, this repo has
  already hit the cap once, and single-subscription mode is what resolved it. Check a one-queue-per-forge
  design against the budget *before* building it.
- ⚠️ **Two live defects get worse here and neither is ours**: a wire type with no handler is dropped
  **silently**, and `Processing` is written but read by nothing, so a crash mid-handler drops the
  webhook. Three forges multiply both. Out of scope to fix — not out of scope to record.

**Verify:** A3. Build; no recipient signature names GitHub; one recipient handles an event from a
second forge with no consumer edit (provable with a test double even before GitLab exists).

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

## M15 — Three forge libraries 🟦 *(D14, D16; after M2a, M2b, M2c, M8)*

Three projects as siblings of the app — `CodeCoverage.{Github,Gitlab,Bitbucket}Integration` — with
`IsPackable=false` (D14: split now, publish only when a second forge ships and the entity
contracts have stopped moving). Only the GitHub one has content in stage 1; the other two are created
with a stub implementation so the registration pattern and the capability contract are proven by more
than one case, and so M2b's conformance test has something to iterate.

- Dependency direction: `CodeCoverage` → `*Integration` → `CodeCoverage.Library` (which holds
  `IForgeIntegration`). Linear, no cycles.
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
- ⚠️ `security.json` and model sync are **app-only by design** (PRD §6.9) — forge rights are
  authored in the app, and a library shipping entities forces a model re-sync the app must commit.
- Add all three to `MintPlayer.Spark.slnx`, the `Dockerfile` COPY lines, and
  `code-coverage-deploy.yml`'s `paths:` list — three hand-maintained closures, each failing quietly
  when missed.
- **Not** under `libs/` while unpublished: the PR version gate filters by path, not packability, so
  `libs/**` would demand a `<Version>` bump on every touch. ⚠️ Never name a directory `coverage`.

**Exit:** `grep -rn "Octokit" apps/CodeCoverage/CodeCoverage/` returns nothing.

## M16 — Fork-PR uploads 🟦 *(D6f, D20; after M2c. M6a must reserve the id segment)*

Today a fork PR uploads nothing and the step goes green with a yellow annotation. This builds the
credential-less path decided in PRD §6.7. Measured surface, 2026-09-20.

### Server

- **A new `[AllowAnonymous]` action**, not a branch inside the existing one. `UploadsController` carries
  three class-level gates — `[Authorize(schemes)]` at `:32`, `[SparkAuthorize("Upload","Coverage")]`
  at `:33`, `[EnableRateLimiting("uploads")]` at `:34` — and a credential-less caller must escape the
  first two. `BadgeController.cs:20-22` is the in-repo precedent for an anonymous controller.
- ⚠️ **Do not route it through `ResolveOidcRepository`** (`:677-752`). That method holds **two**
  mutations, not one: a reconnect/rename of an existing repository (`:689-717`) and creation of
  `Account` + `Repository` from OIDC claims (`:720-751`). A fork PR has no OIDC claims, so both are
  structurally unreachable *provided the new path resolves the base repository itself*. Passing
  `provision: false` is not the protection — not calling it is.
- **Verify `RepositoryResolver.ResolveAsync` does not store** before reusing it. It is already
  reachable from the anonymous badge endpoint, which is good evidence, but it makes an anonymous
  GitHub API call per unknown name (`RepositoryResolver.cs:24,64`) and its body was not read.
- **Private base repository ⇒ 404, never 403.** `UploadsController.cs:668` and `BadgeController.cs:17-18`
  both establish that unknown and unauthorized look identical. A 403 here would be a visibility oracle:
  it would confirm the repository exists.
- ⚠️ **The repository and PR number must be in the route or query string, not the body.** The rate
  limiter runs *before authentication and before model binding* (`Program.cs:217-221`), so a
  per-(repository, PR) partition key cannot come from claims or a bound model, and reading a multipart
  body inside a partition lambda is not viable. Falling back to client IP would partition every
  GitHub-hosted runner into one bucket. **This constraint decides the endpoint's shape**, so settle it
  before writing the action side.
- **Its own rate-limit policy and size cap.** `[EnableRateLimiting]` per action is established at
  `UploadsController.cs:280,304`. The existing `uploads` policy is 60/min (`Program.cs:251-258`) and
  `MaxReportBytes` is 50 MB (`:45`, applied at `:117`); the fork cap should be smaller.
- **Cap distinct fork namespaces per repository — genuinely new.** No such counter exists anywhere.
  Without it, an unauthenticated writer can inflate a real repository's document count indefinitely,
  and §7.1 measured how large that id space already is.
- **Verdict is `EForgeOutcome.Neutral`** (D20), never Failure. A fork's number must not gate a merge.
- **Run `--spark-verify-security` afterwards.** `App_Data/securityPosture.txt` is a CI-gated record of
  the anonymous surface; it does not currently list the anonymous badge controller, so a new
  `[AllowAnonymous]` MVC action probably does not move it — *probably* is not good enough for a file
  whose whole purpose is to make anonymous surface visible in review.

### Action

The D20 shape already exists by accident: `fail-ci-if-error` defaults false (`action.yml:46-49`), so
the catch at `main.ts:150-154` already yields one `core.warning` and a green step. What is wrong is
the *message* and the fact that nothing uploads.

- `collectContext` (`context.ts:33-61`) gains the fork flag and the base repository identity. It
  already destructures the PR payload, so the test costs no new plumbing —
  `pr.head.repo.id !== pr.base.repo.id`. `context.repo` is already the **base** repo on a
  `pull_request` event, which is the identity the new id space needs.
- `resolveCredential` (`main.ts:214-233`) gains a third exit. It has exactly two today, both
  terminal: return a `Credential`, or throw (`:228`). It must return "no credential" for a fork PR
  rather than throwing. An `anonymousCredential()` beside `staticCredential` / `oidcCredential`
  keeps the `get()`/`invalidate()` contract intact for every consumer.
- `postWithRetry` (`main.ts:498-527`) builds the `Authorization` header unconditionally at `:507`;
  it must omit it entirely rather than send an empty one.
- `fetchCapabilities` (`capabilities.ts:42+`) also sends it unconditionally, but already degrades to
  `BASELINE` and never throws — it is the one call that is already D20-safe.
- `setResultOutputs` (`main.ts:384+`) already emits `''` for unmeasured numbers rather than `0`. A
  Neutral fork upload follows that convention; emitting zeros would read as *measured zero coverage*.

**Exit:** a fork PR on a public repository shows a coverage comment on the base PR; the same PR on a
private repository says so in one line instead of failing; neither can move a badge or a branch
baseline.

---

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

## M17 — Deploy to the VPS, with the app still working 🟦 *(last; D22 does not relax this)*

The owner's acceptance criterion, stated 2026-09-20: *"Just make sure I can deploy this to my vps, and
have the app still working as best as possible."* D22 removed the obligation to old callers and wire
formats; it did **not** remove this one.

### ⚠️ The re-key is irreversible — this is the point of no return

RavenDB ids are immutable, so M6 is **put-new + delete-old**, not a patch. The moment it runs:

- **Rolling the code back does not roll the data back.** Old code looks for `Commits/{repoId}/{sha}`
  and finds `Commits/github/{repoId}/{sha}`. Every commit, build, file and tree reads as missing —
  the app comes up and serves an empty site rather than failing loudly.
- **There is therefore no rollback except a restore.** Nothing else in this PR has that property.

**So: take a backup immediately before, and verify it can be read back.** An unverified backup is a
belief, not a rollback. This is the single step whose absence turns a bad migration from an
inconvenience into data loss, and it is currently nowhere in this plan.

### Deployment shape (measured)

- **One container, no blue/green.** There is a downtime window by design; the question is only its
  length and what the app does inside it.
- **RavenDB runs as `coverage-raven` and publishes no host ports** — queries go through the container.
  The backup and the verification both have to happen from inside it.
- The deploy workflow's `paths:` list is hand-maintained (`code-coverage-deploy.yml`). If M15 adds
  projects, they must be added there too or the deploy silently does not rebuild.

### Order of operations

1. **Back up, and read the backup back.** Before any new code is running.
2. **Deploy the new code with the migration not yet run**, if the framework allows it to be triggered
   rather than run at startup. The app should come up on old data and still work — which is what
   `LegacyBranchCompatibility` does for the *previous* migration and is the pattern to copy.
3. **Run the migration**, resumably (M6b already records progress so a restart resumes).
4. **Run M6d's verification script** — per-collection counts before and after. This is exit criterion
   **A10**, and it is verified *against production*, not a fixture.
5. **Only then** delete the compatibility shims (M6g).

### What the app must do while half-migrated

Step 3 can stop: a crash, an OOM, a `Patching.MaxStepsForScript` cap, or simply being interrupted.
With 199,917 documents this is a real possibility, not a theoretical one, so **"half-migrated" is a
state the app has to survive**, and the honest goal is *degraded but not wrong*:

- A commit whose documents have moved and one whose documents have not must both render, or the
  un-migrated one must be visibly absent — never silently empty.
- ⚠️ **Never silently empty** is the specific failure to design against, because it is
  indistinguishable from "this repository has no coverage", which is a legitimate state.

### ⚠️ This tempers M6g

M6g deletes the shims D22 releases, and one of them —
`LegacyBranchCompatibility` — is precisely what lets the app tolerate an unfinished migration. Deleting
it in the same deployment that runs a 199,917-document re-key removes the tolerance at the exact
moment it is most needed.

**Recommendation: keep `LegacyBranchCompatibility` through this deployment and delete it once
production is verified migrated.** It costs a type check per loaded entity. That is a smaller price
than the failure it prevents, and it is not backward compatibility in the sense D22 released — it is
*migration-state tolerance*, which D22's own wording carves out.

`ApiToken.AccountLogin` has no such constraint and can go with the backfill, provided the tokens the
backfill cannot resolve are **counted and reported** rather than discovered later.

### Queue rename (if D22's licence is used)

Renaming `spark-github-all` strands in-flight `SparkMessage` documents on the old queue name. If the
rename happens, the migration rewrites their queue field, and the safer sequencing is to let the
queue drain before deploying rather than to migrate messages mid-flight.

**Exit:** the site serves at coverage.mintplayer.com, a fresh upload produces a build and a PR
comment, counts match A10, and a verified backup exists from before the migration ran.

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
M14 last, in that order — then **M17**, the deployment itself, which is the only milestone whose
failure mode is irreversible and which therefore starts with a verified backup.

**No decision blocks work.** D6f is resolved and built by **M16**, which is ordered *before* M6
only in the sense that M6a must reserve its id segment — the endpoint itself can land any time after
M2c. D16 and D17 are decided and shape M2a/M2b/M2c/M15.
D14 remains a recommendation: if published NuGet packages are wanted after all, M15 grows a fourth
contracts project — because `IForgeIntegration` is typed on this app's domain entities — and the
placement moves to `libs/`. PRD §6.9 has what that costs.

---

## Vocabulary: forge, not platform (D18, decided)

**Decided by the owner, 2026-09-20: "Use Forge wherever you like."** So *forge* is the single word
for the thing we integrate with, and the D16 names move to match rather than the shipped ones.

This is the cheaper direction. Everything M1 and M2 shipped stays exactly as it is —
`EForgeProvider`, `ForgeProviders`, `ForgeOwner`, `ForgeAccess`, `ForgeVerdict`, `EForgeOutcome`,
`ForgeAccessDeniedException` — and only the three identifiers introduced with D16/D17 change:

| D16/D17 draft | Final |
|---|---|
| `IPlatformIntegration` | **`IForgeIntegration`** |
| `EPlatform Platform { get; }` | **`EForgeProvider Provider { get; }`** — already shipped |
| `ECapability` | **`EForgeCapability`** |
| `GitHubPlatformIntegration` | **`GitHubForgeIntegration`** |

**The overload that prompted the question still needs a rule**, because *provider* remains
ASP.NET Identity's word for an external login provider (`SparkUser.Logins[].LoginProvider`, and the
list D3's login page renders). The rule:

- **Bare "provider"** in prose and in ASP.NET-facing code means the **login provider** — the
  authentication scheme.
- **The thing we integrate with is a "forge"**, and where a qualified name is needed it is a **"forge
  provider"** (`EForgeProvider`), never a bare "provider".
- `EForgeProvider.TryParse(login.LoginProvider)` is therefore an honest conversion between two
  different things that happen to be 1:1 today, and reads as one.

⚠️ Prose in these two documents still uses bare "provider" for the forge in many places written
before this rule existed — D4's "provider-scoped", D5's "provider-qualified ids", M1's "per provider".
Those are **not** being mass-renamed: the URL and id vocabulary is fixed by D11 (`github` / `gitlab` /
`bitbucket` as path segments) and unaffected either way, and a global search-and-replace across two
large documents would churn far more than it clarifies. The rule applies to **code identifiers and
new prose**; existing prose is corrected where a sentence is being rewritten anyway.

**No data, URL or route consequence.** Canonical strings stay `github` / `gitlab` / `bitbucket`
(D11), so no document id, badge URL or route changes. This is C# identifiers and prose only.

**Carried by M2a**, which rewrites these types anyway.


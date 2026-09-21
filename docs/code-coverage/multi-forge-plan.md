# Plan — Stage 1: de-couple CodeCoverage from GitHub, rebuild sign-in and linking

Companion to [multi-forge-PRD.md](multi-forge-PRD.md). Issue
[#422](https://github.com/MintPlayer/MintPlayer.Spark/issues/422).

**One PR** off `master`, branch `issue-422-forge-abstraction`. Commits per milestone; **the test
suite runs once, at M13** — intermediate milestones are verified by reading the code and building.
GitLab and Bitbucket providers are stages 2 and 3 and are *not* in this PR (PRD §1, D1).

**Blocked on decisions**: nothing. D6f resolved 2026-09-20 — fork PRs upload unauthenticated into a
PR-scoped namespace on public repos only (PRD §6.7). ⚠️ **That decision lands on M6a**: the
`pr/{n}/` segment is a document-id shape and Raven ids are immutable, so it must be reserved in the
migration's target scheme or the 199,917-document re-key is paid twice.

**Decided:** D1–D7, D9, D11, D13, D16–D23. **Recommendations only** (nothing in stage 1 depends on
them): D8, D10, D12, D14. **Partly superseded:** D15 by D17.

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

## M2c — Fold the unseamed capability areas into the interface 🟩 *(partly; the rest reassigned)*

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


### As-built — and three items reassigned

Two things landed here. Three turned out to belong elsewhere under decisions taken **after** this
milestone was written, and doing them here would have been work M7/M8/M15 then redid.

**Done:**

- ⚠️ **Two recipients were bypassing the forge seam entirely**, and each held its own copy of the
  installation lookup — a sixth and seventh copy of the five M2 claimed to have removed. They
  survived because they reach the *comment publisher* directly rather than the *client*, so neither
  the M2 sweep nor §5.7's audit caught them. `OpenPullRequestCommentRecipient` and
  `PublishPullRequestCommentRecipient` now resolve through `IForgeIntegrationResolver` and ask
  `CheckAccessAsync` instead of testing `Account.InstallationId` themselves.
- ⚠️ `PublishPullRequestCommentRecipient` no longer gates on the **stored** `feedback.InstallationId`.
  That field is a GitHub credential on a record that has to outlive GitHub-only (M8), and using it as
  the gate meant a repository whose installation id was never stamped could never retry a comment.
- Dead code in `BrowseController` (~`:474-479`): an installation lookup computed and never used, left
  behind when M2 moved resolution inside the client. It read as a live dependency in every grep.

**Reassigned, with reasons:**

- **`IInstallationRepositories` — no change needed.** M2c proposed reshaping it to enumerate *an
  owner's* repositories. D15 then decided installations stay **internal to the GitHub library**, and
  this interface is consumed only by `GitHubStateReconciler`, which is GitHub-only for the same
  reason. It is already honestly named. Reshaping it would have invented a neutral surface for a
  concept D15 says must not have one. It moves as-is in **M15**.
- **`IRepositoryResolver` — belongs to M7, not here.** It is already a neutral interface with a
  GitHub implementation, so the shape is right. The real problem is deeper: **`owner/name` means
  nothing without knowing the forge**, so resolution is provider-scoped by nature and
  `ResolveAsync` needs an `EForgeProvider` argument. Callers can only supply one once routes carry
  the provider (D4/D5), which is **M7**. Until then it resolves GitHub, like `ProviderOf`.
- **Branch deletion — belongs to M8.** `DeleteHeadBranchIfEnabled` lives inside
  `GitHubEventsRecipient`, which is entirely webhook handling. It moves when the webhook contract
  does, not before.

**Net:** the credential-free property now holds for every path that publishes feedback. What remains
is not "unseamed capability areas" but three things waiting on milestones that own them.

---

## M3 — Login page, and delete the bespoke GitHub button 🟩

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


### As-built

- **The dead redirect is fixed.** `provideSparkAuth({ loginUrl: '/sign-in' })`. The default is
  `/login`, this app mounts `/sign-in`, so the route guard, the 401 interceptor and the auth bar were
  all redirecting to a page that does not exist — and the symptom was a blank shell rather than an
  error, which is why it survived.
- The shell's GitHub button is a link to `/sign-in`, which already renders one button per
  server-reported provider. `services/github-login.service.ts` is **deleted**.
- The Home reconnect banner points at the same page. ⚠️ **Behaviour change:** re-challenging is now a
  full navigation rather than a popup. It costs the popup's "stay on the page" feel and in exchange
  works for every provider rather than only GitHub; the popup handshake, its blocked-popup fallback
  and its four-code error map all died with the service, along with the banner's own error and
  in-flight state.
- Translations: `signInWithGitHub` → `signIn`, `reconnectGitHub` → `reconnect`, retranslated in
  en/fr/nl.

### ⚠️ Two corrections to this milestone as written

1. **"Delete the six dead `app.*` keys" was wrong, and deleting them would have broken the Home
   page.** PRD §5.2 called them unreferenced in `src/` — true, and misleading: they are referenced
   **server-side** from `Actions/HomeActions.cs`. Nine keys look dead to a client-only grep
   (`welcomeTitle`, `welcomeSubtitle`, `signInPrompt`, `home`, `yourAccounts`, `loadingAccounts`,
   `noAccounts`, `resync`, `resyncTooltip`) and every one of them is live. **Nothing was deleted.**
2. **Bootstrap icons do not work the way this app uses them.** Owner, 2026-09-20: *"`bi bi-box-arrow-in-right`
   won't work. The styles are inside the `<bs-icon>` component — same is true for all other bootstrap
   components."* The two icons introduced here were removed rather than guessed at. ⚠️ **Nine
   pre-existing `<i class="bi …">` usages remain** across the badge, setup, trend and commit-files
   panels, and by that description they are all rendering nothing. Not fixed here — it is not this
   milestone's change and the correct API was not confirmed — but it is a real, visible defect and
   belongs in **M10**, which owns the panels most of them live in.

---

## M4 — Spark: link confirmation, and the two linking modes ✅ *(4a–4k built; mail verified end to end)*

D2 and D9. All of this is Spark-side; CodeCoverage only chooses.

**4a — ~~untie confirmation from local credentials~~ → DROPPED. Ship a link-confirmation mail of
our own instead.**

The original plan was to make Identity's `/confirmEmail` and `/resendConfirmationEmail` orthogonal
to `LocalCredentials`, because `LocalCredentialEndpointFilter` strips them in `Disabled` and
CodeCoverage runs `Disabled`.

⚠️ **The owner stopped this, correctly** (2026-09-20): *"How are you supposed to confirm an email
when local login isn't even enabled? … instead of using the confirmation email feature for this
specific scenario, we should just send a separate email, that most likely does quite the same, but
can contain different text."*

**Why it was the wrong tool.** Email confirmation is a password-world mechanism: it proves you own
an address before you are allowed to sign in with it. With external-only login the provider has
already vouched for the identity, so there is nothing for that flow to establish. Untying the
endpoints would have made a password feature reachable in an app with no passwords, purely to
borrow its plumbing.

**And the two are not the same message.** Confirmation says *"prove this address is yours."* Linking
says *"someone signed in with GitLab using your address — do you want it attached to your account?"*
Different text, different decision, and a different thing to get wrong.

⚠️ **The token semantics differ too, and that is the part worth being careful about.** An
account-confirmation token attests address ownership. A link-confirmation token authorises attaching
a *credential*. Reusing the first as the second conflates two authorisations: anyone holding a
confirmation token — issued for an entirely different purpose, possibly much earlier — could attach
a login. Separate tokens keep the two decisions separate.

**So 4a becomes:** ship a pending-link document and a link-confirmation mail with its own token,
its own single-use endpoint and its own template (this is 4e, which already described exactly that —
the untying was never needed to build it). `LocalCredentialEndpointFilter` is left alone, and
`GetAuthCapabilities` keeps deriving `localCredentials` from `/login` and `/register` presence,
which this no longer disturbs.

⚠️ **Open, and deliberately not decided here:** whether newly provisioned users still get an
*account* confirmation mail at all (PRD §6.3 cases 1 and 2). The argument for dropping it is the
same one above — the provider vouches. The argument for keeping it is that a provider asserting an
address has not necessarily *verified* it, which is what 4g's `email_verified` gate is about. Those
two should be settled together, since the answer to one decides the other.

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

**4f — provisioning and the three cases, simplified by D23.**

⚠️ **Cases (1) and (2) no longer send mail.** The owner settled it 2026-09-20: *"not necessary i
think. The platform's SSO proves the user owns the email address already."* A forge that has
authenticated someone and asserts a verified address has already established what a confirmation
mail would have established, and mailing anyway is ceremony the user has no reason to complete.

So the three cases collapse to two outcomes:

| Case | Then |
|---|---|
| No user with that email | Create, link the provider, **sign in**. No mail. |
| User exists, provider not linked | D2's linking mode decides — `ConfirmByEmail` mails the *link* request (4e), which is a different message entirely |
| User exists, provider linked | Sign in |

⚠️ **`EmailConfirmed` is still not set by fiat** (`SparkAuthenticationExtensions.cs:178`). It is set
because the provider *said the address is verified*, which is a fact about the token rather than an
assumption — and that distinction is the whole of 4g. Writing `true` unconditionally would make the
field mean nothing, and it is the field the next feature will trust.

**4g — generalise the verified-email gate** (`:159-168`). Drop `urn:github:email_verified`;
`GitHubAuthenticationExtensions.cs:54-110` keeps its `/user/emails` call but emits the standard
`email_verified` claim. A8.

⚠️ **D23 promotes this from a nicety to the only check there is.** With no confirmation mail, an
unverified provider address can no longer be resolved by asking the user — so the gate must
**refuse** rather than provision-and-confirm. That is a sharper rule than it was, and it has to fail
closed: a provider that does not say whether an address is verified counts as *not* verified, or the
gate is decoration. GitHub's `/user/emails` reports the flag explicitly; a forge that cannot must
not be trusted to assert identity by email.

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

### As-built — 4a, 4b, 4k

- **4b** — `SparkExternalLoginLinking` (`Disabled | WhenSignedIn | ConfirmByEmail`) on
  `SparkAuthenticationOptions`, defaulting to `Disabled`. The enum documents *what each mode
  proves*: `WhenSignedIn` by already holding the session, `ConfirmByEmail` by controlling the
  account's existing mailbox — which is what makes it usable by someone who cannot sign in at all.
- **4a** — ⚠️ **rewritten, see above.** No untying. `SparkPendingExternalLogin` stores only the
  token's *hash*; `ISparkLinkConfirmationSender<TUser>` is Spark's own contract and **takes no
  address parameter**, so mailing the provider-asserted address is not expressible.
- **4k** — startup guard: `ConfirmByEmail` with no sender registered throws. ⚠️ It began as
  type-name matching on `NoOpEmailSender` (ASP.NET `TryAdd`s a no-op, so absence is invisible) and
  became a **plain null check** once linking got its own contract, because Spark ships no default
  for it. The old approach failed *open* on an upstream rename; SP2's measurement survives as a test
  explaining why the contrast exists.
- ⚠️ A test caught the guard placed **after** the `LocalCredentials = Full` short-circuit, where it
  would never have fired for the most common configuration.
- All 23 packable .NET projects bumped to **`10.0.0-preview.84`** — the gate fires on any `libs/`
  change outside `node_packages`, and `dotnet pack` is solution-wide.

### As-built — 4c and 4e

**4c — the duplicate-email branch.** The callback now asks `FindByEmailAsync` **before**
provisioning, rather than inferring the case from a failed `CreateAsync`. The store's
`DuplicateEmail` arrives with no user attached and is indistinguishable from a validation error,
which is how this situation used to surface as `account_creation_failed` — the one answer that is
always wrong, because it reads as "this application is broken" rather than "you already have an
account". Each mode now gets its own code: `email_already_registered`, `sign_in_to_link`,
`link_confirmation_sent`.

⚠️ **`email_already_registered` admits that an account exists**, which the other codes deliberately
avoid doing. Taken knowingly: the asker already proved control of that address at the provider, so
they can learn the same fact from a password-reset form anywhere, and the alternative is the generic
failure above.

⚠️ **`link_confirmation_sent` travels on the failure channel** (`success: false`), because no
session was created. It is not an error and the client says so — but reporting it as success would
have the opener behave as though the user were signed in.

**4e — the confirmation, as `SparkExternalLoginLinker<TUser>`.** One service rather than code in two
endpoint lambdas, because the halves are separated by a mail round-trip: the token format, the
document key and the provider-key rule only stay one mechanism while they live in one type.

| Decision | Why |
|---|---|
| Document key **derived** from `(userId, provider, providerKey)` | Makes "is one already pending?" a load. As a random id it would have been a *query*, and an auto-index stale at the wrong moment answers "no" — which mails a second confirmation. |
| Token is `{keyPart}.{secret}`, one opaque value | The key half addresses the document, the secret is the capability. One parameter, so a link cannot be assembled half-correctly. ⚠️ The key half is interpolated into a document id and is therefore constrained to 32 lowercase hex characters — without that a crafted token addresses **any** document in the database. |
| Window of **1 hour** | The person who triggered it is at their keyboard now. A long window buys them nothing and only widens the period in which a mail obtained later still attaches a credential. |
| A usable pending link **suppresses a second mail**, reported identically | Otherwise repeated sign-ins with someone else's address are a mail flood aimed at them, every message reading as a takeover attempt. |
| The link attached is read from the **document** | This is what satisfies PRD §6.2's second non-negotiable. The ambient identity is never what gets linked, so a mismatch is not something the code must *catch* to be safe. It is checked anyway and refused, because a request carrying a different identity is the substitution itself. |
| Consumed **before** the login is attached | A failure to attach cannot leave a spendable token. |

⚠️ **A re-request rewrites the document in place.** Storing a second instance at the same id throws
`NonUniqueObjectException` — the dedupe load already associated one with the session — and the case
that reaches it is ordinary: confirm a link, then link another provider later. Caught by a test.

### ⚠️ A defect found while wiring 4c: the options were never reachable

`AddAuthentication` registered the configured `SparkAuthenticationOptions` with
`AddSingleton(options)` only. `IOptions<SparkAuthenticationOptions>` still **resolves** — the options
infrastructure constructs a default for any `T` — so a consumer asking for it gets a fully-formed
object with every setting at its default and nothing to indicate it is not the configured one.

Consequences, both silent: the ConfirmByEmail startup guard (4k) never fired in a real application,
and every linking decision would have read `Disabled` regardless of configuration. The guard's tests
passed because they configure through `Configure<T>`, which the application does not use.
`Options.Create(options)` now publishes the same instance behind both, so the two cannot disagree —
which is what the existing comment beside that line already claimed.

### ⚠️ And one in the callback's redirect branch

The non-popup path did `Results.Redirect(safeReturnUrl)` and dropped `error` entirely, so a
full-page sign-in landed back where it started with nothing to show. Survivable while every refusal
meant "it did not work"; not survivable now that one of them means "check your mail". It now carries
`?sparkExternalLogin=<code>`, and confirmation lands with `?sparkLinkConfirmation=<code>`.

**D24 — the message is plain strings.** No templating engine, no razor view, no resource file; a
mail manager is separate, future work, and until it exists the shortest thing that says the right
words beats a rendering pipeline with one message in it.

`SparkLinkConfirmationMessage.Subject/Body` ships **in Spark** all the same, because the *wording* is
part of the design rather than decoration. The reader who did not start the sign-in is the case that
matters — the mail is the only place they are told somebody else's sign-in matched their address —
and an application left to phrase it alone tends to write a notification ("your account has been
linked"), which is untrue when sent and useless to the person who needs to act. So the body says
nothing has happened yet, names the identity that asked, and says plainly that doing nothing is a
valid answer. Those four properties have tests; none of them pins a whole sentence.

Spark still ships **no transport**. An application implements `ISparkLinkConfirmationSender<TUser>`
and may ignore this text entirely.

### As-built — 4d

Four endpoints under `/spark/auth`, **mapped by mode**, because the modes differ in who may attach
a credential and how:

| Mode | `GET /external-logins` | `POST /external-logins/unlink` | `GET /external-logins/link` + callback |
|---|:--:|:--:|:--:|
| `Disabled` | — | — | — |
| `WhenSignedIn` | ✔ | ✔ | ✔ |
| `ConfirmByEmail` | ✔ | ✔ | — |

⚠️ **`ConfirmByEmail` gets the unlink but not the attach.** Its way in is the mailed confirmation;
without an unlink, links accumulate with no way to undo one — a worse position than not linking at
all. And `Disabled` maps nothing: an account page offering an action the deployment forbids is worse
than one that is absent.

**The last-credential guard**, as `SparkCredentialInventory.WouldRemoveLastCredential` — a pure
function, because the arithmetic is the whole of it and every branch is worth pinning. Removing an
account's last way in is permanent and silent: Identity does it without complaint, no password to
fall back on, no provider left to prove ownership, and the person finds out at their next sign-in,
by which point recovery means an operator editing the database. MintPlayer's own account page has
exactly this bug (`AccountRepository.cs:325-338`).

⚠️ **A password only counts where the application serves a way to use it.** Under
`LocalCredentials.Disabled` there is no login endpoint, so a stored hash is an artefact rather than
a credential. A guard that counted it would wave through the very lockout it exists to prevent —
and would do so in the application most likely to hit this, since external-only login is why the
account has no usable password in the first place. Both halves of that are pinned.

**The rest, briefly.** The attach challenge is keyed on the signed-in user (`XsrfId`), so the
identity coming back cannot land in another session. `LoginAlreadyAssociated` is told apart from a
store failure: the first is a fact the user can act on, the second is not, and one message for both
sends people looking for the wrong problem — it says no more than "taken", since naming the other
account would turn an account page into a lookup service. Both paths call `RefreshSignInAsync`, so
the change reaches other sessions through the security stamp. Unlink carries antiforgery metadata.

**Client** — `SparkAuthService` gains `externalLogins()`, `linkProvider()` and `unlinkProvider()`;
the popup handshake is now shared with `loginWithProvider` rather than duplicated, since the two
differ only in the URL. ⚠️ `unlinkProvider` **resolves** with `error: 'last_credential'` rather than
throwing: it is an expected answer to a reasonable request, and a caller forced to tell it from a
network fault inside a `catch` will eventually get it wrong — the failure mode being to tell
somebody their unlink worked. `canUnlink` is served per login so the UI can disable the control,
and is advisory: the server refuses regardless.

**Not built:** the manage-logins **UI component**. CodeCoverage has no account page and runs
`Disabled`, so the endpoints do not even map there; a component nobody mounts would be guesswork
about a page that does not exist. The service methods are the part that has a caller the moment one
does.

### As-built — 4f, 4g, 4h

**4g — one claim, and it fails closed.** `urn:github:email_verified` is gone; `AddGitHub` emits the
standard `email_verified`, and the gate reads that and nothing else. A per-provider term would have
meant a new vocabulary entry for every forge, and a forge whose entry nobody remembered to add
would fail closed in a way that reads like a broken provider rather than like missing code.

⚠️ **D23 makes this the only check there is.** With no confirmation mail there is no second chance
to establish that the address belongs to the person, so anything short of an explicit `"true"` —
absent, `false`, empty, `1`, or the old provider-specific claim — refuses. All five are pinned.
`docs/guide-authentication-schemes.md` updated to match.

**4f — provisioning states why.** `EmailConfirmed = true` stays, but it is set *because the provider
said the address is verified*, which the gate immediately above already enforced. That makes it a
fact about the token rather than a fiat, and it is the field the next feature will trust. No mail
follows (D23).

**4h — the three places the callback carried on when it should have stopped.**

| | Was | Now |
|---|---|---|
| A failed `ExternalLoginSignInAsync` for an **already-linked** account | fell through to provisioning | `locked_out` / `requires_two_factor` / `not_allowed` / `sign_in_refused` |
| `AddLoginAsync` / `SignInAsync` results after `CreateAsync` | discarded | checked; a failed link deletes the account it just made |
| An unregistered `?provider=` | reached `Results.Challenge` and threw → **500** | `400 unknown_provider` |

⚠️ The first is the serious one. A lockout is a deliberate security response, and the old code
routed around it into an account-creation path — after 4c it would at least have answered about the
user's *email* rather than about why they were refused, which is still the wrong question.

⚠️ The second creates and then **deletes**. An account created but not linked is unreachable by
anyone and holds the email reservation, so the same person cannot even try again; it exists only
because of this request and has nothing in it, so undoing it beats leaving a tombstone on the
address. Pinned in both directions — a successful provision is not undone.

### As-built — 4i

**Built, deployed as DNS + a key on the VPS, and verified against a real external mailbox.** Full
write-up in [`docs/guide-outgoing-mail.md`](../guide-outgoing-mail.md); the CodeCoverage-specific
half is in that app's README under "Outgoing mail".

`coverage-smtp` (`boky/postfix`) on the internal network, no published ports. The app hands the
message over and returns — **the send happens inside the external-login callback**, while somebody
is waiting on an HTTP response, so a local handoff that takes milliseconds regardless of whether
the receiving server is reachable is the whole point. A named queue volume keeps messages across a
redeploy.

⚠️ **The plan said deliverability, not wiring, was the risk. That was right, and understated.**
Measured 2026-09-21, in order:

| | |
|---|---|
| Outbound 25 **and 465 blocked** by Hetzner on every Cloud Server; 587 open | unblocked on request |
| First probe | `550 5.7.509 ... does not pass DMARC verification and has a DMARC policy of reject` |
| Cause | `_dmarc.mintplayer.com` publishes **`sp=reject`** — inherited by every subdomain, so failure is *rejection*, not junking |
| Fix | SPF + DKIM TXT records for `coverage.mintplayer.com`, **both address families** in SPF because the host has an AAAA |
| Second probe | `status=sent` — but the log said `Skipping DKIM`: delivered **unsigned**, passing on SPF alone |
| Third probe | signed; `opendkim-testkey` reports `key OK` / `key secure`; **inbox, not junk** |

⚠️ **The second probe is the one worth remembering: it looked like success.** SPF alone delivers,
so a misfiled DKIM key is invisible unless you read the container log or the received headers — and
SPF is the half that breaks the moment a recipient forwards the message. Two layout traps caused
it, both now documented in the compose file and the guide: the key must be **flat**
(`<domain>.private`, not `<domain>/<selector>.private`) and **owned by opendkim on the host**
(`101:104`), because it cannot chown through a read-only mount.

⚠️ Also pinned `smtp_address_preference=ipv4`. `coverage.mintplayer.com` has an AAAA, and the large
receivers hold IPv6 senders to a stricter standard — chiefly a valid PTR for the v6 address, which
Hetzner sets per address and which is not configured.

**Application side:** `CoverageMailOptions` + `SmtpLinkConfirmationSender`, registered **only when
`Host` and `FromAddress` are both set**. That conditional is what keeps Spark's "no default
transport" design honest: an unregistered sender is how a deployment says it cannot send, and it is
what makes the `ConfirmByEmail` startup guard a null check rather than a guess.

**Not switched on.** `MAIL_FROM_ADDRESS` is unset and `ExternalLoginLinking` stays `Disabled`: with
one forge the situation the modes exist for cannot arise. The infrastructure is proven and waiting.

**Not built:** the manage-logins UI component (see 4d), and the PTR is still Hetzner's generic name
— measured as *not* required by Outlook here, so it is hardening rather than a blocker.

---

## M6 — Provider-qualified document ids + migration 🟨 *(built, rehearsed, tested and applied to dev; M6d outstanding)*

**D7 re-confirmed 2026-09-19 against the measured number: full re-key, rehearsed first.**

⚠️ **SP1 changed this milestone's shape.** 199,917 documents carry the GitHub repo id in their key
(`FileCoverages`, `Builds`, `BuildTreeSummaries` and `CommitAssemblies` all nest under
`Commits/{repoId}/…`), plus 683 attachments on `Builds`. Raven ids are immutable, so this is
put-new + move-attachments + delete-old per document — **not** the `PatchByQueryOperation` style
PRD §6.8 prescribes, and not trivially re-runnable.

### D26 — it DOES run in the startup path. The earlier caution here was wrong.

This section previously said the re-key must not be an ordinary migration, because a throw in
`UpAsync` aborts startup and takes the site down. The owner pushed back on 2026-09-21 — *"shouldn't
the migrations be run automatically when the new application/docker container is deployed?"* — and
checking the runner rather than the note shows he is right:

| Property | Verified in |
|---|---|
| `RunAtStartup` **blocks** before the app serves (`.GetAwaiter().GetResult()`) | `SparkMigrationRunner.cs:19-20` |
| The applied-marker is written **only after `Up` succeeds**, so a failure leaves it pending and the next start retries | `SparkMigrationRunner.cs:63` |
| A **distributed lock** means a multi-instance deploy applies it once | `SparkMigrationRunner.cs:33-38` |

⚠️ **The argument inverts once you notice that startup blocks.** A failed re-key leaves the site
*down*, which the old note treated as the risk — but the alternative it was protecting is a site
that is *up and serving a half-migrated database*. Failing closed is the correct behaviour for an
irreversible re-key, and `restart: unless-stopped` turns a transient failure into an automatic
retry rather than an outage somebody has to notice.

**Three conditions, all of which still stand:**

1. **Intra-migration resumability is mandatory**, and for a sharper reason than before: a retry
   restarts `UpAsync` *from the top*. Every step must skip when the target id already exists, and
   must never delete a source until the target **and its attachments** are confirmed present.
2. **Wall-clock must be measured before this ships** (SP6). The container healthcheck allows
   `start_period: 60s`; if the re-key exceeds that, raise it in the same commit or the deploy marks
   itself unhealthy while working correctly.
3. **Traefik returns 502 for the duration** — the container is not listening yet. Acceptable for
   minutes, not for an hour, which is what makes (2) the gate rather than a formality.

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

- **SP6 — rehearse. 🟨 STARTED 2026-09-21, and it has already overturned two plan assumptions.**

  **Environment.** A verified production backup (228,059 docs / 708 attachments / 17 indexes)
  imported into a local RavenDB as `CoverageRekeyRehearsal` — **28 seconds** to restore. The local
  server is the developer's working instance with unrelated databases on it; the rehearsal database
  is separately named and hard-deleted afterwards. Do not rehearse against the dev `Coverage`
  database, which is a different, much smaller dataset.

  #### ⚠️ Finding 1 — a patch CAN re-key a document. This section previously said it could not.

  `PatchByQueryOperation` cannot *rename* in place, but a script can `put()` the new id and `del()`
  the old, which is a re-key by any useful definition — server-side, no streaming to the client, and
  naturally re-enterable because the script tests the target shape first:

  ```js
  from Commits as c update {
    var oldId = id(c);
    if (oldId.indexOf("Commits/github/") !== 0) {
      put("Commits/github/" + oldId.substring("Commits/".length), c);
      del(oldId);
    }
  }
  ```

  **Measured: 820 Commits re-keyed in ~6 seconds**, collection metadata and fields preserved. This
  removes the put-new/delete-old-per-document client loop the milestone was shaped around, and with
  it most of the argument that the re-key is too heavy for the startup path (D26).

  #### ⚠️ Finding 2 — the re-key is the SMALLER half. Cross-references were not accounted for.

  Re-keying `Commits` left every `Build.Commit` pointing at an id that no longer exists. The nesting
  is cosmetic: `Builds`, `FileCoverages`, `BuildTreeSummaries` and `CommitAssemblies` are **separate
  collections** whose ids merely *look* nested under a commit, so a patch over `Commits` does not
  touch them, and nothing warns.

  Every id-valued reference field has to be rewritten in the same migration:

  | Field | Holds |
  |---|---|
  | `Build.Commit` | commit id |
  | `FileCoverage.Build` | build id |
  | `BuildTreeSummary.Build` | build id |
  | `CommitAssembly.Commit`, `.Repository`, and its build reference | commit / repository / build id |
  | `Commit.Repository`, `Commit.LatestBuildId` | repository / build id |
  | `Repository.Account` | account id |
  | `ApiToken.RepositoryIds` | list of repository ids |

  These **are** field changes, so PRD §6.8's `PatchByQueryOperation` rule applies to them in full —
  the exception the plan carved out for the re-key does not extend here.

  ⚠️ **Ordering is now a correctness property, not a preference.** A reference rewrite that runs
  before its target is re-keyed points at a document that does not exist yet; one that runs after a
  source is deleted has nothing to derive the new value from. The safe shape is: **put the new
  documents, rewrite every reference, verify, and only then delete the sources** — which also means
  `del()` cannot stay inside the same script as `put()`, as it was in the probe above.

  #### Measured cost — the startup path is comfortably viable

  | Step | Documents | Wall-clock |
  |---|---:|---:|
  | Restore the production dump into a scratch database | 228,059 | **28 s** |
  | Re-key `Commits` (put + del in one script) | 820 | **~6 s** |
  | `FileCoverages` — put the new ids | 220,419 | **73 s** |
  | `FileCoverages` — delete the legacy ids | 220,419 | **14 s** |
  | Everything else (`Builds` 318, `BuildTreeSummaries` 514, `CommitAssemblies` 154, `PullRequestFeedbacks` 44, `Repositories` 172, `Accounts` 2) | 1,204 | seconds |

  **≈2 minutes end to end on a developer machine.** The production VPS is a shared-vCPU Hetzner
  box, so budget several times that and do not treat the local number as the answer.

  ⚠️ **Raise `start_period` on the `coverage-app` healthcheck in the same commit.** It is 60 s
  today, which this exceeds. The container would be working correctly and marked unhealthy.

  #### The reference map the migration is written against

  Collected by reading the entities, because the field names are not guessable — `FileCoverage`
  holds **`BuildId`**, not `Build`, and a migration written against the wrong name would patch
  nothing and report success.

  | Entity | Field | Holds |
  |---|---|---|
  | `Build` | `Commit` | commit id |
  | `Commit` | `Repository` | repository id |
  | `Commit` | `LatestBuildId` | build id |
  | `FileCoverage` | `BuildId` | build id |
  | `BuildTreeSummary` | `BuildId` | build id |
  | `CommitAssembly` | `Commit`, `Repository` | commit / repository id |
  | `CommitAssemblyFile` | `BuildId` | build id |
  | `Repository` | `Account` | account id |
  | `ApiToken` | repository id list (⚠️ confirm the exact name before writing the patch) | repository ids |

  **Each reference rewrite rides in the same script as its document's `put()`**, because the new
  value is derivable from the old one by the same string operation. That halves the passes over
  `FileCoverages`, the only collection where the cost is material. Only `ApiTokens` needs a pass of
  its own, since the document itself is not re-keyed (D25).

  #### Shape of the migration, in order

  1. **Put** the new documents per collection, rewriting reference fields in the same script.
  2. **Move attachments** for the 318 builds that have them. ⚠️ `put()` does **not** carry
     attachments — they stay on the source document and die with it. This step must complete before
     step 3 or 708 coverage reports are lost silently.
  3. **Verify**: per-collection counts, zero documents left on a legacy id, zero attachments
     orphaned (M6d).
  4. **Delete** the legacy documents, last.

  #### ✅ Rehearsed end to end, against a restored production copy

  The migration was run as the application runs it — `dotnet run` against the restored database, so
  the startup path, the runner, the lock and the marker are all the real ones.

  | | Baseline | After |
  |---|---:|---:|
  | Documents | 228,059 | 228,060 |
  | Attachments | 708 | 708 |
  | `FileCoverages` / `Commits` / `Builds` / `Repositories` | 220,419 / 820 / 318 / 172 | identical |

  **Nothing was lost.** The one extra document is this migration's own applied-marker.

  The whole reference chain resolves, which is the check that matters more than the counts:

  ```
  Commits/github/402741072/67262d58…/builds/31694883768-2/files/5f46…
    → BuildId   → Commits/github/402741072/67262d58…/builds/31694883768-2   ✔ resolves
    → Commit    → Commits/github/402741072/67262d58…                        ✔ resolves
    → Repository→ Repositories/github/402741072  (Provider: GitHub)         ✔ resolves
    → Account   → Accounts/github/48772716                                  ✔ resolves
  ```

  #### ⚠ A fourth defect the rehearsal did NOT catch, and it was fatal

  On 2026-09-21 the app was started against the ordinary dev database — **238 documents** — and the
  migration threw:

  ```
  Cannot perform bulk operation. Index is stale.
  ```

  The delete phase filters by id prefix, which RavenDB answers from an auto-index, and the put phase
  immediately before it has just rewritten every document. The index is therefore **guaranteed**
  stale at that moment, and `DeleteByQueryOperation` refuses outright rather than waiting. Since the
  applied-marker is written only after `Up` returns, the container restarts and fails identically.
  **Forever.** Production would have been left half-migrated behind a crash loop.

  The verify counts had the same defect and were the more dangerous half, because they do not throw.
  They feed the guard that refuses to delete when the put phase produced nothing — so a stale count
  would have had that guard deciding on a picture of the database taken *before* the migration
  started, which is precisely the case it exists to catch.

  **Why the rehearsal missed it, stated precisely rather than shrugged at:** a rehearsal exercises
  the phases with whatever timing it happens to have; the failure needs the delete to arrive while
  indexing is still behind. That is exactly what a container start does and exactly what a
  hand-driven rehearsal does not. It reproduced on the **first** ordinary run, on a database 1/1000
  the size.

  Both now wait (10 minutes, named and settable), and the count throws rather than returning a
  number it knows is stale. Never `AllowStale`: the point is to act on the database as it is, not to
  be permitted to proceed.

  **The migration now has tests**, which it did not despite being the riskiest thing in this PR.
  They run the phases back to back with no indexing pause — that pause is what would make them pass
  against the broken version — and cover the re-key, the reference rewrites, the OwnerKey/Provider
  backfill, re-entrancy (a second pass must not produce `Repositories/github/github/…`), and the
  timeout path.

  Writing the timeout test corrected a claim made in a comment here: **a throw does NOT leave the
  database untouched.** Phases run per collection, so an earlier collection is already fully
  migrated when a later one fails. What actually holds is that nothing is *destroyed* — every delete
  is preceded by its own collection's put — and re-running finishes the job.

  ✅ **Applied to the dev database, and re-entrancy demonstrated a second time:** the failed run left
  it half-migrated, the restart completed it (`12 kept, 12 legacy removed`), and the app then served
  every page against the migrated data.

  #### Three defects the rehearsal caught, none of which a test would have

  1. **`put()` cannot take the stream `GetAttachmentOperation` returns** — the client requires a
     seekable stream so it can rewind on failover. It threw *after* every put phase had succeeded.
     ⚠️ Note where it failed: before the delete phase, so the legacy documents were still present
     and the marker unwritten. The re-entrancy design was demonstrated rather than asserted.
  2. **`select count()` is not valid RQL** outside a group-by — the verification query, i.e. the one
     guarding the deletes, was the last thing to run and the last thing to fail.
  3. **`result.Total` counts documents *scanned*, not changed.** A second run legitimately reports
     roughly double the collection size, because the script skips already-qualified documents but
     the operation still matched them. Read as work done, a correct idempotent re-run looks like a
     duplication bug. The log now says "scanned".

  #### Consequence carried into the deployment

  ⚠️ `start_period` on the `coverage-app` healthcheck is raised **60s → 600s**. The migration blocks
  before the app listens, and the measured ~100s of work will be several times that on a
  shared-vCPU VPS. It is a *start* period rather than a timeout, so the generous value costs nothing
  on an ordinary deploy with no migration pending.

  #### ⚠️ The mid-run kill found a data-correctness bug in the Spark migration runner itself

  Killed the container during the `FileCoverages` put — the worst moment — and the half-migrated
  state was exactly as designed: 281,859 `FileCoverages` (220,419 legacy plus 61,440 new), earlier
  collections doubled, **no deletions**, 708 attachments intact, and the applied-marker unwritten.

  **Then the restart skipped the migration and served that database.**

  ```
  info: Spark migrations: another instance holds the migration lock; skipping on this node.
  info: Now listening on: http://localhost:5201
  ```

  ⚠️ **The lock outlives the process.** A migration that *throws* releases it in a `finally` and
  retries on the next start — which is what made D26's "a failure retries" reasoning look sound. A
  migration whose process is **killed** runs no `finally`, and OOM-killer, `docker kill`, a host
  reboot and a deploy timeout all take that path. Those are precisely the conditions a long
  migration invites.

  The lock has a 30-minute TTL, so it is not stuck forever — it is worse than that. For thirty
  minutes every restart comes up **serving a half-migrated database**, and nothing is logged as
  wrong, because from the application's point of view nothing is.

  **Fixed in `SparkMigrationRunner`** (`libs/migrations`), not worked around in this migration: a
  node that cannot take the lock now **waits** rather than serving, polling until either it takes
  the lock or every pending migration is marked applied, and failing startup after a bounded
  budget. Waiting is also the correct behaviour for the case the skip was written for — a second
  instance starting alongside a migrating one should come up *after* the migration, not race it.

  Verified: with the lock held the app logs a warning and **does not serve**; when the lock clears
  it resumes and completes.

  #### Recovery after kill + resume, against baseline

  | | Baseline | After kill + resume |
  |---|---:|---:|
  | `FileCoverages` / `Commits` / `Builds` / `Repositories` | 220,419 / 820 / 318 / 172 | **identical** |
  | Attachments | 708 | 708 |
  | Legacy ids remaining | — | **0** |

  Total documents are +17, fully accounted for: +1 migration marker and +16 `SparkMessages` the app
  enqueued while running. No domain document was lost, duplicated or left behind.

  #### ✅ Surveyed in the browser, signed in, against the migrated production copy

  The app was run against the migrated database and driven through the UI. Every read below crosses
  at least one re-keyed id, and the deepest one crosses all of them.

  | Surface | Result |
  |---|---|
  | Home, signed in | Accounts 2, Repositories 171, grid renders — **0 console errors** |
  | `/api/me/accounts` | `MintPlayer` 15 repos 82.7%, `PieterjanDeClippel` 156 — correct logins, ⚠️ **no `github:` prefix leaked** |
  | `/api/browse/accounts/{login}/repos` | ids are `Repositories/github/925854479` |
  | Repository detail | `latestCoverage` 26,714 / 31,942 lines across 736 files |
  | Commit + tree | `Commits/github/1006469943/421e…/assembly`, `libs` 21,922 / 25,066 |
  | **File source + coverage** | a `FileCoverage` resolved by its re-keyed id, 4 / 7 covered |

  ⚠️ **171 shown against 172 stored is correct**, and worth writing down because it reads like a
  lost document: exactly one repository is disconnected, and `MyAccountsService` excludes those
  deliberately so the headline counts do not include repositories the app can no longer reach.

  The `github:` check is the pointed one. `OwnerKey` is a comparison value, and an earlier version
  of this work let it reach the rendered account name — the browser survey is what would catch that
  again.
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

## M7 — Provider-segmented routes, sidebar units and badges ✅ *(routes, D4 sidebar, badges, A11; browser-verified)*

**One sidebar program unit per provider** (D4), each owning its own account list — not a unioned
list. GitHub is the only populated unit in stage 1, so this milestone proves the shape without a
second provider to fill it. `App_Data/programUnits.json` grows a unit per provider; ⚠️ per
`docs/code-coverage/program-units-PRD.md` there is no packaged sidebar and no object-id PO unit, so check
what the subsystem actually supports before designing the unit.


### D27 — the forge is the outermost scope; the provider always precedes the owner

D11 said `/{provider}/{owner}/{name}`. Taken literally that is ambiguous against the top-level
namespace — `home`, `query/:queryId`, `po/:type/…`, the auth routes — because a three-segment
`/{provider}/{owner}/{name}` and a three-segment `/po/:type/:id` differ only by what the first
segment happens to contain. Keeping the `r`/`a` discriminator in **second** position removes the
ambiguity while leaving the forge outermost, which is how people read it ("on GitHub, this repo").

| | Before | After |
|---|---|---|
| Account | `/a/{login}` | `/{provider}/a/{login}` |
| Repository | `/r/{owner}/{name}` | `/{provider}/r/{owner}/{name}` |
| Commit | `/r/{owner}/{name}/c/{sha}` | `/{provider}/r/{owner}/{name}/c/{sha}` |
| File | `/r/{owner}/{name}/c/{sha}/f` | `/{provider}/r/{owner}/{name}/c/{sha}/f` |
| Badge | `/badge/{owner}/{name}.svg` | `/badge/{provider}/{owner}/{name}.svg` |
| Browse API | `/api/browse/repos/{owner}/{name}` | `/api/browse/repos/{provider}/{owner}/{name}` |
| Accounts API | `/api/browse/accounts/{login}` | `/api/browse/accounts/{provider}/{login}` |
| Settings API | `/api/repos/{owner}/{name}/settings` | `/api/repos/{provider}/{owner}/{name}/settings` |

**One rule, stated so the next surface does not have to be argued about: the provider precedes the
owner, everywhere.** On the client it is also the first segment, because there the forge reads as
the scope rather than as a qualifier.

### ⚠️ The ordering rule — this paragraph said the opposite, and was wrong

An earlier version of this section claimed the provider routes are "declared before
`sparkRoutes(...)`, which fixes match order". **Being first is exactly what would have broken it.**

The invariant, stated by the owner and worth keeping because it generalises past this milestone:

> **Every route `sparkRoutes` contributes begins with a literal first segment — `po` or `query`. So
> an application route whose first segment is not one of those can never be shadowed by a Spark
> route.**

The consequence runs the other way, and that is the part that is easy to get backwards. Our routes
begin with a **parameterised** first segment, `:provider`, which matches *anything* — including
`po`. Declared first, `/po/r/123/edit` would bind `provider='po'` and shadow the persistent-object
editor. Declared **after** `sparkRoutes`, every Spark route is tried first, none of them can match
a forge-scoped URL, and the ambiguity disappears.

So the rule for this codebase is:

| The route's first segment is… | Where it must be declared |
|---|---|
| a literal that is not `po`/`query` | anywhere; it cannot collide |
| **a parameter** (`:provider`, `:anything`) | **after `sparkRoutes(...)`**, always |

That is the whole mechanism. No route constraint, no per-forge literal paths and no guard test —
each of which was tried and rejected, because each would have required remembering something when a
forge is added.

⚠️ Badge URLs are published into READMEs and pull-request comments, so this is effectively
permanent — which is why it was decided rather than assumed.

**One funnel.** Every repository read goes through `BrowseController.ResolveVisibleRepository` →
`RepositoryResolver.ResolveAsync`, so the provider threads through two signatures rather than
eleven. `RepositoryResolver` hard-codes `EForgeProvider.GitHub` at its GitHub-lookup fallback
(`:80`); that becomes the parameter.

**Retires** `ForgeOwner.KeyFromUnqualifiedLogin`, which exists only because routes carry no
provider. When M7 is done a grep for it should find no callers.

- Routes become provider-segmented using full provider names (D11, shaped by D27).
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

### As-built — the routes

| | |
|---|---|
| Client | `:provider/a/:login`, `:provider/r/:owner/:repo[/c/:sha[/f]]`, declared **after** `sparkRoutes` |
| Server | all 11 `BrowseController` routes, `BadgeController`, `RepoSettingsController` |
| Funnel | the provider threads through **two** signatures — `ResolveVisibleRepository` and `RepositoryResolver.ResolveAsync` — not eleven |
| Unknown forge | resolves to `null`, which every caller already answers as 404 |

### ⚠ The funnel took the provider and ignored it — found in the browser, not by a test

This table claimed the milestone was done. It was not. `ResolveAsync` accepted the parameter and
never used it, matching on `FullName`, which is unique only **within** a forge:

```
GET /api/browse/repos/gitlab/MintPlayer/MintPlayer.Spark
200 {"id":"Repositories/github/1006469943", ...}
```

That is the defect the whole issue exists to remove, still live in the main read path after the
milestone that was supposed to close it — and it fails in the **permissive** direction, because
anyone can register the free name on the other forge. Three more steps in the same method were
equally blind: the remembered-name lookup matched aliases across forges; the known-account gate
asked "do we know this login?" without a forge, which admitted a `/gitlab/` URL to the step that
asks **GitHub** what `owner/name` resolves to; and `BrowseController.GetAccount` compared
`a.Login == login`, so `/accounts/gitlab/MintPlayer` returned `Accounts/github/48772716`.

**The lesson, which generalises past this milestone:** *threading a parameter through a signature is
not the same as honouring it.* A grep for the parameter name finds the first and says the job is
done. Five cross-forge tests now exist because none did — the old seeder could not even express the
case, since it keyed every document as GitHub.

### ⚠ The client did not compile for three commits, and nothing said so

`CommitFilesPanelComponent` gained a required `provider` input; its only caller was not updated.
NG8008, whole bundle failing, dev server serving the last good build. The C# suite was green
throughout. **A green .NET suite says nothing about the SPA** — the only signal is the host's own
dev-server output, which has to be read rather than assumed.

Also recorded, because it cost two wrong attempts: a component implementing **both** renderer
contracts cannot give `item` a union type. `InputSignal<T>` is invariant in `T` (its `transformFn`),
the column contract offers a `QueryResultItem` and the detail contract a `PersistentObject`, so the
input must be `any` — like `value` beside it.

⚠️ **The badge endpoint is the deliberate exception.** An unrecognised forge takes the same path as
an unknown repository and still renders a badge. Its never-404 rule exists so a badge URL cannot be
used to probe which repositories exist; 404ing on the forge segment would have rebuilt that oracle
one level up.

⚠️ **Two places name GitHub out loud rather than defaulting to it**, which is the distinction this
milestone is about:

- `UploadsController` resolves from a repository full name and a credential, and neither carries a
  forge yet. When one can, it comes from the **credential** (M16) — not from a fallback here.
- ~~The coverage-sparkline renderer only ever sees its own attribute value, so it now renders
  **nothing** unless the forge arrives through the model's type hints.~~ ⚠ **This premise was
  false, and it made the column blank on every row.** A column renderer receives the whole row when
  it declares the `item` input — `spark-grid-renderers` passes it and `withDeclaredInputs` filters
  it back out for renderers that do not want it. Both this renderer and `account-link` now read the
  forge off the row, and `Repository.OwnerKey` ships on query rows to carry it (`isVisible: false`:
  **ShowedOn alone decides what ships, IsVisible only decides what is drawn**). Neither guesses when
  the row has no forge — a missing sparkline is a visual gap, a wrong one is a lie.

**The client sources the provider from `OwnerKey`** (`github:MintPlayer`), not from the `Provider`
enum. That serialises as `"GitHub"`, and lowercasing it to reach the URL spelling would work only by
coincidence of how these three happen to be spelled.

### As-built — D4, the per-forge page

`ForgeAccounts` is a JSON-only virtual PO whose **objectId is the forge** (`github`), reusing the
`my-accounts` query, which `MyAccountRowActions` narrows from `args.Parent`. One aggregation and one
row shape rather than two that drift. An unrecognised forge returns `null` — 404 — rather than
falling back to the only implemented forge, which would render GitHub's accounts under a GitLab
heading.

**Only the GitHub unit is declared.** A unit for a forge nobody can sign in to is an empty page in
the sidebar, and the unit is where the per-forge name and icon live, so M15 adds the other two
beside their libraries.

Three things this turned up that are worth keeping:

| | |
|---|---|
| `MyAccountRow.Id` | was the **login**, unique only per forge. With a second forge the projector's duplicate-row-id check throws and the home page fails to render. It is the owner key now; the displayed `Login` stays unqualified. |
| Type alias | a PO with an alias that is not simply its lowercased name could not have sub-queries at all — a Spark bug, fixed in `ModelLoader.ResolveEntityType`. See M7a below. |
| `--spark-synchronize-model` | **drops `_comment` from a generated model file** while preserving the `showedOn`/`isVisible` edits beside it. Rationale for a hand-edit there has to live elsewhere. |

### M7a — a Spark bug this milestone exposed

`ModelLoader.ResolveEntityType` resolved by id or **alias**. A sub-query request carries
`parentType`, which the client takes from the parent object's **name**. Those agree for every
generated type, where the alias is just the lowercased name, so nothing ever noticed — until
`ForgeAccounts` declared `forge-accounts` and its grid answered `404 "Parent not found"`. That
message reads as a missing document rather than as a name nobody looked up, which is what made it
slow to find. Resolution now falls back to the name; the alias still wins a collision, since it is
the addressable form. Two regression tests in `ModelLoaderTests`.

**Verified in the browser** against the migrated dev database, signed in (2026-09-21):

| | |
|---|---|
| Home, per-forge page, repository, commit | all render, **0 console errors** |
| Account links | `/github/a/MintPlayer` |
| Badge panel snippet | `/badge/github/{owner}/{name}.svg` |
| `repos/gitlab/…`, `accounts/gitlab/…`, `…/notaforge/…` | 404 |
| `/badge/{owner}/{name}.svg` (two-segment) | 404, as D13 intends |

**Verify:** A11 — grep that no source we control still emits the two-segment form. ✅ Done; it found
`PullRequestCommentRenderer` (the least recoverable surface — the comment is posted **into** the
forge and stays in the pull request forever) and `UploadsController`'s `CommitUrl`, which the CI
action prints into the job log.

---

## M8 — A neutral webhook contract 🟨 *(8a–8d built; connection state deferred)*

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

### As-built — 8a–8d

| | |
|---|---|
| **8a** | `ForgeEvents.cs` (six canonical events) + `ForgeWebhookMessage<T>`, in `CodeCoverage.Library/Forge/` |
| **8b** | push + pull-request-updated split into a GitHub normaliser and `ForgeEventsRecipient` |
| **8c** | merged pull requests, and `IForgeIntegration.DeleteBranchAsync` |
| **8d** | owner renames; `RememberPreviousFullName` moved onto `Repository` |

**The events are derived from what the app *does*, not from what GitHub sends.** That collapsed
`opened`/`reopened`/`synchronize` into one `PullRequestUpdated` — the app treats all three
identically — and refused to collapse `merged` into `closed`, because retention surrenders a merged
request's build data while a closed-unmerged one keeps it. A forge that cannot determine mergedness
must raise nothing rather than guess; deleting builds on a guess is unrecoverable.

⚠️ `BranchCommitPushed` **deliberately carries no parent sha**, pinned by a test because it reads
like an oversight. GitHub's `before` is the previous ref *tip*, which is not the commit's parent in
three of the six push shapes, and every forge's equivalent has the same defect.

**One class implements `IRecipient<>` several times** rather than becoming four classes — the
recipient it took work from warns against "splitting one cohesive handler into five classes", and
the generator emits one `AddScoped` per closed interface. ⚠️ Declare every interface on **one**
partial part or each handler runs twice per message.

**Two behaviour changes, both deliberate:**

1. The merged path **loads** the repository instead of upserting it. My first version upserted,
   which rewrote `Repository.Account` from the payload and lost the delete-branch policy it was
   inheriting — caught by a test whose payload owner id (99) differed from its seeded account (1).
   A close is not a reason to mint documents.
2. **The payload's `installation` node is no longer the gate** for branch deletion. The credential
   now resolves from the repository's own account, which every other GitHub call already used;
   reading one and authenticating with the other was the inconsistency. Its test moved to
   `GitHubForgeClientBranchDeleteTests` rather than being deleted with the code it covered.

⚠️ **Deferred, as a judgement rather than an omission: the installation and repository-connection
handlers.** That code carries *measured* behaviour — a transfer produces `repository.transferred` +
`installation_repositories.added` inbound but only `installation_repositories.removed` outbound —
and disconnecting on `transferred` would be a correctness bug resting on the delivery order of two
independently queued messages. A neutral event may not be able to express that asymmetry, and its
only consumer today is the recipient itself, so the M×N argument does not yet apply. Better for a
second forge to show the shape than to guess it into the contract.

---

## M9 — Generalise the uploader surface 🟩

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

## M10 — The setup panel 🟩

`repo-setup-panel.component.ts` emits GitHub Actions YAML across seven tabs and nothing else
(`:27,65-70,71-86,93-187`, plus "repository secret" copy at `:30`).

Restructure so the snippet is chosen by **provider × language** rather than language alone, with
GitHub the only populated provider in stage 1. Without this, stage 2 has nowhere to put a GitLab CI
snippet.

### ⚠️ Deferred: the app should not be loading `bootstrap-icons.css` at all

Owner, 2026-09-20: *"that's not supposed to. Remove that line, and use the `<bs-icon>` instead."*
**Left as-is for now, deliberately** — recorded here rather than done, because the gap is in the
libraries rather than in this app, and fixing only the app's half makes things worse.

**What is true today.** `apps/CodeCoverage/CodeCoverage/ClientApp/project.json:27` lists
`node_modules/bootstrap-icons/font/bootstrap-icons.css` in the build's global `styles`. That is why
every `<i class="bi …">` in this app renders, and it is the wrong layer — icons are supposed to come
from a component, not from a global font stylesheet the app happens to pull in.

**Why removing the line alone breaks more than it fixes.** `SparkIconComponent`
(`<spark-icon name="…">`) renders a registered SVG *and falls back to* `<i class="bi bi-{name}">`
when the name is unknown. Only **seven** icons are registered
(`SPARK_BUILT_IN_ICONS`: `arrow-left`, `pencil`, `plus`, `plus-lg`, `search`, `trash`, `x-lg`).
Everything else in play depends on the global CSS:

| Depends on the CSS | Where |
|---|---|
| `github`, `google`, `facebook`, `microsoft` | **`ng-spark-auth`**, hard-coded as `iconClass: 'bi bi-github'` in `spark-auth-routes.ts:198-216` — this is the sign-in page's provider button |
| `graph-up`, `house` | **`programUnits.json:32,42`**, server-supplied program-unit icons |
| `arrow-return-right`, `check2`, `patch-check`, `clipboard`, `arrow-repeat`, `rocket-takeoff`, `graph-up`, `exclamation-triangle` | this app's own templates (8 distinct) |

So dropping the stylesheet without the rest would blank the **GitHub button on the sign-in page** and
the **sidebar icons** — the two most visible icons in the product.

**What doing it properly involves**, roughly in dependency order:

1. **`ng-spark`** — add the provider and program-unit icons to `SPARK_BUILT_IN_ICONS`, so
   `spark-icon` resolves them without a CSS fallback. Any consumer benefits.
2. **`ng-spark-auth`** — `iconClass: 'bi bi-github'` becomes an icon *name* resolved through
   `spark-icon`, so a provider button stops depending on a stylesheet the app might not load.
3. **`apps/CodeCoverage`** — register the 8 app-specific SVGs, convert its templates to
   `<spark-icon>`, and only then remove the `styles` entry.

⚠️ Steps 1 and 2 are `libs/node_packages/` changes, so both packages need version bumps and the PR
grows a second library concern. That is the main reason this is recorded rather than folded into
#422, which is already about forges.

**Note the failure shape**, because it is the same one as several other findings on this branch: an
unregistered icon does not error, it renders nothing — and `spark-icon`'s CSS fallback is exactly
what hides the missing registration today.

---

## M11 — Rename the GitHub-shaped client surface ✅ *(DTOs #422; renderers and the dead keys in #435)*

- `AccountsResponse.gitHubAppUrl` / `gitHubReauthRequired` (`accounts.service.ts:14-24`) → provider-neutral.
- `app-installed-renderer.component.ts` → a provider-appropriate "connected" state (M5's record).
- `account-avatar-renderer.component.ts:44` — `Type === 'User'` is GitHub's account-type vocabulary.
- These are hand-written DTOs with no codegen link to the server (PRD §5.2); change both ends together.


### As-built

- `AccountsResponse.GitHubAppUrl` → `ConnectUrl` and `GitHubReauthRequired` → `ReauthRequired`, on
  the server record, the TypeScript interface and `MyAccountsResult`, in one commit — they are
  hand-written DTOs with no codegen link, so they move together or not at all.
- The doc comments moved too, and they were the part carrying a wrong assumption: reauth is set if
  **any** linked forge needs it, and resync drops **every** linked forge's cached owner set.
- ⚠️ **Deferred, deliberately:** `app-installed-renderer` and `account-avatar-renderer` (whose
  `Type === 'User'` is GitHub's account-type vocabulary). Both are grid renderers reading stored
  fields that M6 re-keys and M7 re-routes, so renaming them now would be rework. They move with the
  panels in M10, alongside the nine broken `bi` icon usages recorded against the same milestone.

---

## M15 — Three forge libraries ✅ *(Octokit out of the app; GitLab/Bitbucket stubbed)*

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

**Exit:** `grep -rn "Octokit" apps/CodeCoverage/CodeCoverage/` returns nothing. ✅ **Met.**

### As-built

| | |
|---|---|
| `CodeCoverage.GithubIntegration` | 20 files, all the Octokit code, plus its own `AddGithubIntegration()` |
| `CodeCoverage.{Gitlab,Bitbucket}Integration` | a no-op `AddXxxIntegration()` and the traps a port must know, in its doc comment |
| Registered | GitHub only. The stubs are **referenced** by the app but not called |
| Moved down to `CodeCoverage.Library` | `IForgeClient`, `IForgeAccessService`, `IForgeFeedbackPublisher`, `IRepositoryResolver`, `ISourceContentCache`, `ReconcileAccountMessage`, `CoverageQueues` |
| Verified | 622 tests green; home, badge, `/health/ready` and the accounts list all serve from the moved code |

**The stubs are referenced but not registered, deliberately.** A registered forge nobody can sign
in to advertises itself through `IForgeIntegrationResolver` and leads to empty pages. Referencing
them anyway means adding a forge is one line in `Program.cs` rather than that line *plus*
discovering that three hand-maintained closures — the app csproj, the Dockerfile restore layer and
`MintPlayer.Spark.slnx` — each need editing too.

### ⚠ The renderer did not move, and the cascade is why

`PullRequestCommentRenderer` looks like it belongs with the publisher. Moving it pulls
`CheckVerdict`, then `GateEvaluator`, then `BuildComparer`, then `IBaseResolver` — ending with half
the application in the library.

The boundary runs the other way: **rendering is forge-neutral presentation and stays with the
application; publishing is forge-specific transport and goes with the forge.** Only the comment
marker is genuinely shared, so only the marker moved, as `CoverageCommentMarker` — and it is a
**wire format**, not a constant: changing the string orphans every comment already posted, and the
publisher starts adding a second comment to every pull request that already has one.

### ⚠ Two source-generator behaviours that cost real time

Both are now written into the csproj and the builder extension, where the next person meets them.

**1. Generated registrations are per-compilation and `internal`.** `AddCodeCoverage()`,
`spark.AddRecipients()`, `AddCronJobs()`, `AddCustomActions()` and `AddActions()` each cover the
app assembly and nothing else. A moved type therefore drops off its registration **silently** — the
app compiles, starts and serves pages; it just stops publishing pull-request comments and handling
webhooks. The new assembly exposes `AddGithubIntegration()` which calls its own generated methods
from inside itself, and `Program.cs` calls that explicitly.

✅ This is what the `RegistrationInventoryTests` added just before the move exists to catch, and it
is the only instrument that could: no compiler, controller test or browser can see the absence of a
background effect.

**2. `[GenerateIndex]` emits into `{RootNamespace}.Indexes` in every compilation that references
the entities** — so the app, the library and the new project each generate their own copy.

The first attempt pinned `RootNamespace` to `CodeCoverage` so the moved code compiled unchanged.
That worked, and then broke `CodeCoverage.Tests` with **CS0433 on every index it touches**: a
project referencing both the app and the new assembly has no "own" copy to prefer, so the duplicate
is genuinely ambiguous there. The new assembly aliases `Indexes` to the **library's** copy instead
(`<Using Include="CodeCoverage.Library.Indexes" Alias="Indexes" />`), which keeps the number of
generated index sets at the two that already existed.

⚠ Confirmed at runtime: index deployment still reports one assembly, `CodeCoverage`, exactly as
before — the extra generated copies are never deployed.

**Analyzer `ProjectReference`s are not transitive**, so all three projects carry their own. Without
them `[Register]` and `[Inject]` are inert **with no diagnostic**, which is the same failure this
milestone's registration guard exists to catch.

## M8 tail + M11 tail — partly built 🟨 *(2026-09-21)*

### ✅ Built

- **The neutral connection-state write path.** `IForgeConnectable` + `MarkConnected()` /
  `MarkDisconnected(reason)` in `CodeCoverage.Library`. The tri-field write was duplicated **five**
  times — twice for `Repository` and once for `GitHubProject` as private helpers in two
  `CodeCoverage.GithubIntegration` classes, plus once inline in `UploadsController` — every copy in
  GitHub-specific code, so a second forge had no way to write connection state except a sixth copy.
- **M11 cosmetics.** `IsAppInstalled` → `IsConnected` (record, controller, model JSON, renderer,
  translations); `app-installed` renderer → `connected`; the avatar renderer's `Type === 'User'` now
  deny-lists known group vocabularies (`organization`, `group`, `team`, `workspace`) and defaults to
  the person icon, so an unknown forge's value is drawn rather than mis-drawn.
- **6 dead translation keys deleted**, verified dead in **both** directions. ⚠️ The four traps
  (`welcomeTitle`, `welcomeSubtitle`, `signInPrompt`, `forgeAccountsTitle`) are alive only via
  server-side `Translate(...)`; a client-only grep would have deleted them.
- **A regression the merge introduced:** `RepositoryVisibility.IsListed` compared `OwnerLogin`
  against a list of owner **keys**, so it could never match. It failed *closed* — an owner stopped
  seeing their own private and disconnected repositories through every imperative caller — which is
  why no security test caught it.

### ❌ Still open

- The two dead events (`RepositoryRenamed`, `RepositoryConnectionChanged`) are **still dead**. The
  de-duplication above removes the *reason* they were needed most, so the "wire or delete" decision
  is now genuinely open rather than forced.
- `ReconcileAccountMessage.AccountGitHubId` — a contract name a second forge cannot fill honestly.
- No per-forge reconciliation entry point on `IForgeIntegration`.
- `DisconnectedReasons` still speaks GitHub (`AppUninstalled`, `DeletedOnGitHub`). ⚠️ These strings
  are **stored on documents**, so renaming them is a migration, not a rename.
- The account-level `IsConnected` is still derived from `account.InstallationId is not null` in the
  neutral layer. It moves onto the forge seam with the repository-level half.

---

## M8 tail + M11 tail — the original investigation 📜 *(superseded by the section above; kept for its findings)*

Both milestones are marked partly done. The investigation separates **functional gaps that would
block a second forge** from **renames that would not**, because the two were being carried as one
list and they are not the same work.

### ⚠ Two neutral events are declared, documented, and completely dead

`ForgeEvents.cs` declares six records. Verified by grep over production code:

| Event | Producers | Consumers |
|---|---|---|
| `BranchCommitPushed`, `PullRequestUpdated`, `PullRequestMerged`, `OwnerRenamed` | ✅ | ✅ |
| **`RepositoryRenamed`** | **none** | **none** |
| **`RepositoryConnectionChanged`** | **none** | **none** |

Each has exactly one production hit — its own declaration.

This directly contradicts the deferral note beside it, which argues *"better for a second forge to
show the shape than to guess it into the contract"* — while the contract already contains two guessed
shapes. **Either wire them or delete them.** Leaving a declared event with no implementation is worse
than either: it makes the contract look complete, and nothing (no test, no analyzer) will say
otherwise.

### Blocking a second forge — functional, not cosmetic

1. **There is no neutral write path for connection state.** All six `Repository.Connection` writes
   live in `CodeCoverage.GithubIntegration`, as `private static` `Connect`/`Disconnect` helpers
   **duplicated across two classes**. Eight of nine *readers* are already in the app — so the app
   consumes connection state neutrally and only production is GitHub-only. A GitLab library would
   re-implement the tri-field write a third time, into a reason vocabulary that says
   `AppUninstalled`, `AppSuspended`, `DeletedOnGitHub`. **This is the largest single gap, and it is
   exactly what `RepositoryConnectionChanged` was declared to solve.**
2. **`RepositoryRenamed` has no consumer**, so a rename on a second forge silently does nothing. The
   rename logic sits in the GitHub recipient.
3. **`ReconcileAccountMessage.AccountGitHubId`** — the message type moved down to
   `CodeCoverage.Library`, its field name did not. A second forge cannot fill it honestly. A name,
   but a **contract** name.
4. **No per-forge reconciliation entry point.** `GitHubStateReconciler` is the only implementation
   and owns four of the six connection writes; nothing on `IForgeIntegration` lets the app's
   scheduler sweep a second forge.
5. **`MyAccountRow.IsAppInstalled`** — the *renderer's* name is cosmetic, the **boolean is not**.
   "Is the App installed" is not a question GitLab can answer. This is a data-model gap wearing a UI
   costume, and it is the only M11 item that is not cosmetic.

### The genuine open question M8 deferred

The transfer asymmetry is real: a transfer produces `repository.transferred` +
`installation_repositories.added` inbound but only `installation_repositories.removed` outbound, and
disconnecting on `transferred` would be a correctness bug resting on the delivery order of two
independently queued messages.

**So decide one of two things**, and the deferral note is really asking only this:

- the neutral event gains an *authoritative vs advisory* distinction, or
- the ownership guard stays GitHub-side and the neutral event is raised **only after** it passes.

### Cosmetic — real work, but blocks nothing

Renderer name `app-installed`; `Type === 'User'` in the avatar renderer (GitHub's account-type
vocabulary — picks a person-vs-people icon, wrong on GitLab, never broken); the "GitHub App"
translation values; residual GitHub wording in server doc comments.

### ✅ Already done, contrary to the marker

`gitHubAppUrl` → `connectUrl`, `gitHubReauthRequired` → `reauthRequired`, and
`BuildInfo.runId/runAttempt/workflowName` removed from the client interface entirely.

### ⚠ A trap for the dead-translation-key sweep

The PRD's "6 of 13 keys are dead" is **verified exactly**: `app.home`, `app.yourAccounts`,
`app.loadingAccounts`, `app.noAccounts`, `app.resync`, `app.resyncTooltip`.

But four more — `welcomeTitle`, `welcomeSubtitle`, `signInPrompt`, `forgeAccountsTitle` — are
unreferenced **in `ClientApp/src`** and alive via server-side `Translate(...)`. A "grep the client
and delete the rest" sweep breaks the home page and the per-forge title. **18 of 19 keys resolve by
string literal with no compile-time link in either direction.**

---

## M16 — Fork-PR uploads ✅ *(built 2026-09-21; the design changed — read this before the section below)*

The investigated section that follows is kept because its findings are what forced the change. **Its
central design is superseded.** What shipped is different in one decisive way, and better.

### The decision: the target repository must have the app installed

D6f tried to make fork uploads safe for *any* public repository, which is what made everything hard:
with no installation there is no webhook, so `Repository.DefaultBranch` is null — and null is exactly
the state in which `Promote` promoted every branch. The rule "refuse the default branch" could not
even be evaluated for the population it was written for.

Requiring the app on the **base** repository dissolves that. The fork itself needs nothing.

| | |
|---|---|
| Base repository | app installed, public, connected. Consent, and the credential for the forge read. |
| Fork repository | nothing. It is never resolved, never stored, never given a document. |

### What replaces the credential: a forge round trip

`IForgeClient.GetPullRequestAsync` is new. One read of the pull request turns four uploader-supplied
strings into forge-supplied facts:

| Read back | Replaces | Closes |
|---|---|---|
| `HeadSha` — must equal the uploaded sha | `form.CommitSha` | fabricated shas becoming baselines |
| `HeadRef` | `form.Branch` | `?branch=` badge poisoning |
| `HeadRepositoryId != BaseRepositoryId` | nothing — **no fork signal existed at all** | fork/first-party confusion |
| `BaseRepositoryDefaultBranch` | a field that is simply null for this population | `Promote`'s null escape |

**The pull request is the credential.** Only someone who actually opened it can name a number whose
head sha matches the report they hold.

⚠️ `GetPullRequestAsync` returns null for *"cannot read"* as well as *"does not exist"*, and callers
must treat null as a refusal. Never cache it across a head change.

### Why NOT "refuse uploads for the default branch"

It was the starting proposal and it is the right instinct, but the literal form breaks legitimate
use: a fork PR's head ref is very often `main`, because contributors fork and commit on their
default branch. Refusing on the name would reject ordinary contributions while still not protecting
the `?pr=` badge, which never looks at a branch at all.

What shipped achieves the same intent structurally: **a fork upload is always pull-request-scoped,
and a pull-request-scoped commit never promotes.** The default branch is protected by construction
rather than by a string comparison.

### `Commit.ContributedFromFork` — stored, not inferred

The investigation below assumed the `pr/{n}/` id shape would carry the meaning. It cannot: **no
query parses document ids** — every one of them filters on the `Repository` field — so inferring
would mean teaching each consumer a string shape, and the one that was missed would treat fork
coverage as first-party. A stored bool is greppable, queryable and indexed.

Old documents deserialize to `false`, which is correct: nothing could be fork-contributed before
there was a path to contribute it. **No migration.**

### What the flag gates

| Surface | Fork coverage |
|---|---|
| `Repository.LatestCoverage` (headline badge, repo page, account aggregate) | ❌ `Promote` refuses first |
| `BaseResolver` — all three tiers | ❌ refused at `UsableBuildIdAsync`, the shared chokepoint |
| Δ vs default branch, and the 200-commit restamp fan-out | ❌ |
| Commit list, history chart, sparklines, branch list | ❌ |
| `?branch=` badge | ❌ |
| **`?pr={n}` badge, and the PR comment** | ✅ **the point of the feature** |

⚠️ `BaseResolver` matters more than it looks: `CarryForward` copies a base's files *into* the
comparing commit's assembly, so a fork base would make a fork's numbers into a first-party commit's
numbers — and that commit may promote.

### ⚠️ A pre-existing production bug, fixed here

`Promote` used to be a three-way `&&` that engaged only once a repository had coverage **and** a
known default branch. So a repository missing either promoted every complete assembly on every
branch, with `commit.Branch` uploader-supplied. On an OIDC-provisioned repository — which never
learns its default branch from any webhook — that was permanent: **the badge tracked the last
complete upload, whatever branch it claimed.**

The `LatestCoverage is null` escape is gone. The unknown-default-branch case still promotes,
deliberately — refusing would leave such a repository with no badge at all — and the fork endpoint
now backfills the default branch from the pull request, so the case shrinks.

⚠️ **This is a visible behaviour change on production.** A repository whose first-ever coverage
lands on a feature branch no longer gets a badge from it.

### The anonymous surface

`ForkUploadsController`, a separate type — `UploadsController`'s own class comment says relaxing its
`[Authorize]` is "the one change here that would fail open", so an `[AllowAnonymous]` action inside
it was not an option.

`securityPosture.txt` **does not record this** (finding 2 below: it is 11 lines of Spark rights and
lists no MVC endpoint, not even `BadgeController`'s existing `[AllowAnonymous]`). Replaced by
`AnonymousSurfaceTests`, which asserts the **exact set** of anonymous actions by reflection, with a
falsification test proving the finder distinguishes an authenticated controller.

Every refusal is a 404 with one body: unknown repository, app not installed, no such pull request,
not a fork, and wrong sha are indistinguishable from outside.

### Bounds

`fork-uploads` rate-limit policy: **10/min, partitioned on the target repository**, read from the
path (the limiter runs before endpoint selection). Keying on the caller is meaningless — they are
anonymous — and keying on IP lets one actor spend every repository's budget. ⚠️ The trade: one
abusive fork can exhaust its target's own window.

64 reports per upload and a 1 MB file list, both an order of magnitude below the authenticated
endpoint's.

⚠️ **There is still no storage quota anywhere in the app** — verified: only per-request caps and
rate limits, no accumulating counter. The installation requirement bounds this to repositories that
opted in; it does not bound total bytes.

### Action side

`collectContext` now reports `isFork`, comparing `head.repo.id` to `base.repo.id` — **ids, never
names**, since a contributor can rename a fork. It reports `true` when it cannot tell.
`resolveCredential` gained a third exit returning `anonymousCredential()`, and all three
`Authorization` sites (upload, status poll, capabilities probe — the plan said two) go through
`authHeaders`, which yields **no header** rather than `Bearer undefined`.

### ✅ Feedback — built 2026-09-21

`ToVerdict` takes provenance and forces <b>Neutral</b> whatever the gate concluded (D20). Both
directions are tested, and the <b>failing</b> case is the one that matters: an implementation of
"never succeed" would leave red reachable, and a contributor able to turn a maintainer's pull
request red is exactly what D20 prevents. Green would be dishonest for the same reason pointed the
other way.

The check-run title carries `(from a fork)`, because a check list shows titles and not bodies. The
pull-request comment states it **above** the results table — a reader who takes the numbers at face
value and stops has still been told the one thing that changes how to read them.

⚠️ The badge signature stays keyed on `IsPrivate` rather than gaining a fork arm. Fork uploads can
only target public repositories, so the branch is unreachable today — but if that changes, minting a
badge capability on an anonymous contributor's behalf would publish access to a repository they
cannot otherwise see.

### Still open

1. **The `workflow_run` recipe** for private base repositories. Still unwritten. ⚠️ It must never
   check out or execute fork code — the artifact is data.
4. **A fork-namespace cap.** Open item 4 below, unchanged.
5. **GitLab/Bitbucket** `GetPullRequestAsync` — the stubs do not implement it. ⚠️ GitLab's `iid`,
   not `id`.

---

## M16 — Fork-PR uploads 📜 *(the original investigation; its design is SUPERSEDED — read the built section above)*

### Investigated 2026-09-21 — two findings that change the design

#### ⚠ 1. The id shape does NOT prevent a badge takeover. This is the highest-risk open item.

D6f's central safety claim is that the `pr/{n}/` segment is *"structurally incapable of … moving a
badge"*, and that **the guarantee is the id shape, not a code path that must remember to check**.

Read `CommitAssembler.Promote`:

```csharp
if (repository.LatestCoverage is not null
    && repository.DefaultBranch is not null
    && !string.Equals(commit.Branch, repository.DefaultBranch, StringComparison.Ordinal))
    return;

repository.LatestCoverage = assembly.Coverage;
```

`Promote` acts on the **`Repository` document**, and nothing in it looks at the commit's id. Two ways
through:

- **The `LatestCoverage is not null` short-circuit.** On a repository that has never had coverage the
  guard does not engage at all, and *any* branch promotes. An anonymous fork upload against a
  never-covered public repository would set its badge.
- **`commit.Branch` is uploader-supplied** (`commit.Branch ??= form.Branch`). On a covered
  repository, sending the default branch name satisfies the compare.

So the claim holds only if the fork path never reaches `Promote` — which is a code path that must
remember to check, exactly what D6f said it would avoid. **Decide explicitly**: skip promotion
structurally for PR-scoped ids, or add the guard and stop claiming the shape is sufficient.

⚠ Related surfaces the shape also does not protect: `?branch=` / `?pr=` badges query
`Commits_ByRepository` by branch name, so a fabricated commit renders under that branch's badge
regardless of `Promote`; and baseline resolution reads the non-PR id shape, so a poisoned commit
could become a future comparison base.

#### ⚠ 2. `securityPosture.txt` will not record the new anonymous endpoint

The plan hoped the anonymous-surface gate would make this reviewable — *"probably is not good
enough"*. Resolved, unfavourably: the file is **11 lines of Spark rights only**
(`Browse/Coverage`, `Query/…`, `Read/…`). It is computed from `security.json` and lists no MVC
endpoints at all — `BadgeController`'s existing `[AllowAnonymous]` does not appear in it either.

**A new `[AllowAnonymous]` MVC action moves nothing in that file.** M16 needs a different guard — a
test asserting the *set* of anonymous controllers and actions would do, and would have the falsifiable
shape M19 argues for.

### Already settled, or already true in the code

| | |
|---|---|
| Credential model | No relay. `GITHUB_TOKEN` relay formally retracted; OIDC is impossible on fork PRs. Unauthenticated + namespace confinement. |
| Scope | **Public base repo only.** Private base repo uses the `workflow_run` recipe. |
| Id shape | `Commits/github/{baseRepoId}/pr/{n}/{sha}` — ✅ **already implemented and reserved**; `Commit.DocumentId` and `Build.DocumentId` both take `pullRequestNumber` and nothing writes it. **M6a's prerequisite is done.** |
| Verdict | `Neutral`, never Failure (D20); `fail-ci-if-error` stays `false`. |
| Provisioning | `provision: false`, achieved structurally by not calling the OIDC path. |
| Unknown vs forbidden | 404, never 403 — precedent in both `UploadsController` and `BadgeController`. |
| A separate action, not a branch | Forced: three class-level attributes (`[Authorize]`, `[SparkAuthorize]`, `[EnableRateLimiting]`). |
| Repository/PR in the **route**, not the body | Forced: the rate limiter runs **before** authentication and before model binding. |
| Advisory run check | ✅ Safe to reuse `RepositoryResolver` — verified read-only, caches misses, gates the GitHub call on a known account, already reachable anonymously from the badge. The plan's open question is answered. |

### Genuinely open

1. **Whether `Promote` is skipped structurally or explicitly guarded** — see finding 1. Highest risk.
2. **The anonymous-surface guard** that replaces `securityPosture.txt` — see finding 2.
3. The concrete route, and whether the base repo is named by full name or numeric id (a numeric id in
   a public URL is the weaker oracle).
4. **The fork-namespace cap** — no counter, no storage, no chosen limit, and no decided behaviour on
   exceeding it. Entirely new.
5. The fork rate-limit numbers and report-size cap. *"Smaller than 50 MB"* is not a value.
6. **How "unverified / contributed from a fork" is rendered.** No provenance field exists on `Commit`
   or `Build`; whether it is stored or inferred from the id shape decides whether this needs a
   migration.
7. How the verdict is forced to `Neutral` — the feedback path maps from the gate's own conclusion and
   knows nothing about forks.
8. The `workflow_run` snippet the PRD promises. It does not exist. ⚠ It must never check out or
   execute fork code — the artifact is **data**.
9. Fork-build → base-PR association for token-bearing cases — declared unsolved by the PRD; blocks
   GitLab fork coverage in stage 2, not GitHub here.

### Action-side work

`resolveCredential` has exactly two exits today, both terminal throws — the fork-PR one is the second.
It needs a third. **There is no fork detection anywhere**: `collectContext` reads the PR payload but
never compares `pr.head.repo.id` to `pr.base.repo.id`, and `UploadContext` has no fork flag. Both
header sites must omit `Authorization` rather than send `Bearer undefined`.

⚠ **Line references throughout this milestone are stale** — the multi-forge merge shifted them and
moved `RepositoryResolver` into `CodeCoverage.GithubIntegration`. Re-locate by symbol, not by line.


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

## M12 — Docs 🟩 *(the A12 sweep; the rest follows the code)*

- `product-overview.md`: retire the v1 non-goal at `:23`; rewrite §6.1/§6.3, which assert
  GitHub-as-authority as **policy** (`:157`, `:170`) — restate what is still true (the forge decides
  who) and what M5 changed (the grant is now explicit).
- `apps/CodeCoverage/README.md` — 43 GitHub hits.
- Record as-built deviations in the PRD, per the `program-units-PRD.md` §9 convention.
- A12.

---

## M13 — Verification sweep 🟨 *(suites green; browser done on migrated data; production checks outstanding)*

**The only full test run.** Everything before this is verified by reading and building.

- `dotnet test` for the solution; the Angular suite for the workspace.
- Capture to a file, never pipe the only copy (`cmd > log 2>&1; echo "EXIT: $?"`).
- Drive the real app with `playwright_node`: sign-in page, a provider round trip, and the
  `ConfirmByEmail` path end to end with a captured message.
- ⚠️ E2E shares one rate-limit bucket (150/10s on 127.0.0.1); non-deterministic failures here are
  pressure, not regression.
- Walk A1–A12.


### As-built — 2026-09-20

**Green, measured:**

| Suite | Result |
|---|---|
| `MintPlayer.Spark.Tests` | 2,232 passed |
| `CodeCoverage.Tests` | 568 passed |
| `MintPlayer.Spark.SourceGenerators.Tests` | 278 passed |
| `MintPlayer.Spark.Client.Tests` | 91 passed |
| `MintPlayer.Spark.slnx` | builds, 0 errors |
| `@spark-apps/code-coverage` (Angular) | builds, 0 errors |

**3,169 tests, no failures.**

### ✅ A4 verified in a browser, 2026-09-20

Driven with `playwright_node` against `dotnet run --launch-profile https`:

- `/sign-in` renders one button per **server-reported** provider (GitHub), icon intact.
- The shell shows a **Sign in** link, not a GitHub button.
- A guarded route while signed out lands on `/sign-in?returnUrl=%2Fpo%2Fapitoken%2Fmain` — the fix
  preserves the destination as well as reaching a real page.
- Sign-in completes and lands on `/po/home/main` with real data: 2 accounts, 179 repositories,
  MintPlayer at 82.4%. **That is the proof the forge seam works** — the account list now flows
  through `IForgeIntegration` and `ForgeFanOut` and returns what it always did.

⚠️ **Three things the browser found that no suite did:**

1. **Sign-in did not leave the sign-in page.** In popup mode the `returnUrl` is consumed by the
   *popup*; the opener was never touched. Fixed in `ng-spark-auth` (the branch's only `libs/`
   change, hence its version bump) and pinned with two tests.
2. **The Home page still reads "across your GitHub repositories."** A server-side translation key —
   the client-side sweep could not see it. → **M10**.
3. **The accounts grid header still reads "GitHub App."** That is the `app-installed-renderer`
   deferred in M11. → **M10**.

⚠️ **Also: `dotnet run` bare picks the http profile on :5201, and the GitHub App registers
`https://localhost:5200/signin-github`.** Signing in locally needs
`dotnet run --launch-profile https`, or GitHub answers `Invalid Redirect URI`. Not a defect — but it
looks exactly like one.

⚠️ **Still not verified:**

- **E2E** (`MintPlayer.Spark.E2E.Tests`) — needs a running host; belongs with M17.
- **The repository, badge, setup and trend panels** — not opened, so the nine `bi` icon usages
  remain unconfirmed in either direction. The sign-in and sidebar icons *do* render, so the earlier
  assumption that all `bi` classes are dead is **not** supported.
- **A10** — counts before and after the migration, against production. The migration is not written,
  let alone run.
- The nine `bi` icon usages the owner flagged are **still broken** and are not covered by any test,
  because nothing asserts on rendered icons.

### Version bump: `10.0.0-preview.83` → `.84`, and it is required

⚠ **This section previously said "none needed, and that is deliberate", on the grounds that the
branch touched zero files under `libs/`. That stopped being true and the note was not updated** —
which matters, because it is the note somebody reads immediately before merging. The branch now
changes ~20 files under `libs/`, including framework code every app consumes:

| | |
|---|---|
| `libs/authorization/…` | the whole link-confirmation mechanism (M4) |
| `libs/migrations/SparkMigrationRunner.cs` | the lock-wait budget |
| `libs/spark/…/Services/ModelLoader.cs` | `ResolveEntityType` name fallback (M7a) |

The `libs/**` CI gate therefore **does** fire, and the bump is already in place. The major digit
stays at `10` because the targeted platform has not moved — per `CLAUDE.md`, an API change inside a
.NET generation is never a reason to move it.

⚠ **Merging publishes these packages.** CI publishes on push to `master`, so the moment this lands
the framework changes above are public on nuget.org under `preview.84` and cannot be unpublished
into reuse.

### ⚠ Merging is deploying — and deploying is the irreversible step

`code-coverage-deploy.yml` triggers on push to `master` under `apps/CodeCoverage/**`. So squashing
this PR does not merely land code: it builds the image, ships it to the VPS, and the container runs
`M_202609210900_ForgeQualifiedDocumentIds` against **production** before it serves a request. There
is no manual gate between the merge button and the re-key of ~224,000 live documents, and D22 keeps
no backward-compatible path.

**Therefore, immediately before the merge, not the evening before:**

| | |
|---|---|
| Fresh backup | the newest one in `~/Documents/coverage-raven-backups/` is from the morning of 2026-09-21; production has taken uploads since |
| Watch the first container start | the migration blocks before serving, and its own failure mode is a restart loop, so "the site is slow to come back" and "the migration is failing" look identical from outside |
| `start_period: 600s` | already raised in `docker-compose.yml`; the healthcheck must not mark the container unhealthy while it is doing exactly what it is supposed to |

**One cross-repository item, which the one-PR rule would normally have carried along:**
`MintPlayer/mintplayer-ng-bootstrap`'s README badge still uses the two-segment form
(`/badge/MintPlayer/mintplayer-ng-bootstrap.svg`). D13 removes that route rather than aliasing it,
so that badge breaks **at deploy**, not at merge. It must be updated in that repository as part of
the same operation.

---

## M14 — Version bumps and PR ✅ *(PR #434 squash-merged as `0a084191`, 2026-09-21)*

- `libs/` version bump is a **CI-only gate** — a green `dotnet test` does not catch it.
- **Majors do not move.** npm major = Angular major, NuGet major = .NET major. These are
  preview-range changes to `MintPlayer.Spark.Authorization` and `@mintplayer/ng-spark-auth`; a
  wrongly published major is burned permanently.
- GitGuardian will flag `modelHashes.json` — always a false positive; dismiss, never "fix".

---

## M18 — The E2E rate limiter, because CI could not go green without it ✅ *(unplanned; found by CI)*

**Not multi-forge work.** It is here because it blocks this PR's CI, and because the one-PR rule puts
what you find in the same unit of work as what you were doing.

### How it surfaced, and what that says about the rest of this plan

The `libs/` and `apps/CodeCoverage` suites were green locally — 622 and 2313 — and I reported the
branch as verified on that basis. **CI had been red all day.** The E2E project is the one I never ran,
and it is exactly where authentication changes surface.

Two different failures were hiding behind one red tick:

| | |
|---|---|
| `ExternalLoginReturnUrlTests` ×7 | **Real, and mine.** M4 made a refused sign-in carry *why* (`?sparkExternalLogin=<code>`); the tests asserted `Location == "/"` exactly. Not a security regression — `SanitizeReturnUrl` still substitutes `/` for every hostile input — but a real contract change. Fixed by asserting the property (path is the sanitized default **and** nothing caller-supplied survives) rather than the string, which is strictly stronger: the old assertion would have passed if the code appended the outcome to `/` while discarding a legitimate `/dashboard`. |
| Everything else | **429, and not mine.** See below. |

⚠ **Fixing the first made the second worse**, which is worth understanding rather than shrugging at:
seven tests that used to fail fast at an assertion now run to completion, consuming more of a shared
budget and tipping others over. Failure counts moved 7 → 15 between runs. A test suite where fixing a
test breaks other tests is reporting something about itself.

### The flake predates this branch — the repository says so

`tests/MintPlayer.Spark.E2E.Tests/Mapper/ViewerTimezoneRenderingTests.cs:26-31` records a 429 cascade
caused by adding **one** extra browser test, with `RateLimitTests` untouched:

> *"A browser test is expensive in that budget — one Angular boot is a dozen `/spark` calls — so a
> second near-duplicate test pushed unrelated tests elsewhere in the suite into 429s."*

That test's author worked around it by writing fewer tests. That is the cost this milestone removes.

### Measured

| | |
|---|---|
| Suite | 88 tests, one **serialized** collection (xunit v2 never parallelises within a collection) |
| Volume | ~600–900 requests, ~2–5 rps against a 15 rps allowance — **not** globally too small |
| Burst | ~25 fast API tests × ~6 requests = 150 inside one 10-second window |
| Amplifier | `BrowserSignIn` polls `/spark/auth/me` every 250 ms for up to 10 s — up to 40 extra requests |

So the budget fails on **bursts**, not volume. Raising it is therefore honest rather than a cover-up.

### The change

1. **`AddRateLimiter` binds `Spark:RateLimiter`**, configuration first then `configure`, matching
   `AddReplication` and `AddMessaging`. This supersedes **D5 of `issue_265_PRD.md`**, which deferred
   rather than forbade it — see that document for the reversal and the three spikes behind it.
2. **No `Enabled` flag.** A limiter that silently does nothing is what `NormalizePrefixes` already
   throws to prevent; a quiet second route to the same state would contradict the design and give a
   production app a way to ship unmetered by accident.
3. **`FleetTestHost` writes `"RateLimiter": { "PermitLimit": 1000 }`** into the
   `appsettings.{Environment}.json` it already generates. The limiter stays wired, in the same
   pipeline position, still returning 429 — with ~6× headroom over the worst observed burst.
4. **`RateLimitTests` bursts past the *configured* limit**, not a hard-coded 200, so it keeps proving
   audit finding L-3 and cannot be silently defeated by a future budget change.
5. **The drain moves into `try`/`finally`** — today it sits after the assertion, so the run that
   leaves the bucket most saturated is the one that skips the cooldown.
6. **`docs/guide-rate-limiting.md` is corrected.** It currently points at `RateLimitTests` as the
   pattern *"worth copying"*, which is how the flaw propagates.

### ⚠ Found on the way, and NOT fixed here — a production rate-limiter bypass

`apps/CodeCoverage/CodeCoverage/Program.cs:34-39` clears both `KnownNetworks` and `KnownProxies` while
honouring `X-Forwarded-For`. ASP.NET only runs its known-proxy check when one of those lists is
non-empty, so **any caller can set their own `Connection.RemoteIpAddress`** — and all three of
production's limiters key on it:

| Limiter | Key | Bypassable |
|---|---|---|
| Spark's (`/spark`, `/connect`, `/api/browse`) | `RemoteIpAddress` | yes |
| `browse` (300/min) | `RemoteIpAddress` | yes |
| `uploads` (60/min) | token hash, **else** `RemoteIpAddress` | yes, unauthenticated |

Deliberately out of scope here: the fix needs the real proxy address in `KnownProxies`, which is
deployment knowledge rather than a code change, and guessing it would break the limiter's ability to
see real client IPs at all. **It is also why the tempting test fix was rejected** — per-test
partitions via `X-Forwarded-For` work only because of this bug, and would make CI depend on it.

---

## ⚠ Open questions that only production can answer — 2026-09-21

These gate M6d and M6g. **Nothing below can be settled from the repository**, and each one is a
question whose wrong answer is expensive. RavenDB publishes no host ports, so every query runs from
inside the `coverage-raven` container.

| # | Question | What it gates | Why it matters |
|---|---|---|---|
| 1 | Did `M_202609190900_BranchesBecomePerLineArmSets` complete — **marker present AND zero `FileCoverage` docs with a legacy `BranchId`-shaped first entry**? | M6g step 1 | Deleting `LegacyBranchCompatibility` while any document is unconverted makes it read as **zero branch coverage on every line** — a wrong number, not a missing one. The marker alone does not prove every document converted. |
| 2 | Per collection, how many documents remain on a **legacy** id? | M6d, A10 | The migration's guard only catches *total* failure. A partial put followed by a successful delete is **silent**. |
| 3 | Was a **pre-deploy baseline** captured from the live database? | M6d | The plan's numbers come from a restored copy. Without a true baseline the script can only prove self-consistency, never "nothing was lost". |
| 4 | Attachments: **683 or 708**, and how many unique hashes? | M6d step 5 | This document states both. Asserting against the wrong one either passes vacuously or fails forever. |
| 5 | How many `ApiTokens` have **`AccountGitHubId == null`**? | M6g step 4 | Exactly the tokens the login fallback keeps alive. This count **is** the blast radius, and nothing backfills the field. |
| 6 | How many `ApiTokens` have `AccountLogin` set but `AccountOwnerKey` still null? | M6d step 4 | Would mean the migration's `ApiTokens` pass did not reach them. |
| 7 | Did the healthcheck `start_period` raise actually ship in the deployed compose file? | M17 | Put ~80s + delete ~20s exceeds a 60s `start_period`; a container can mark itself unhealthy while doing exactly what it should. |

⚠ **Question 5 is the one to ask first.** It is cheap, and if the answer is "many", M6g's
`ApiToken.AccountLogin` half is not a deletion at all — it is a backfill project.

---

## M6d — the verification script 🟦 *(investigated 2026-09-21; not written)*

The migration is deployed and ran. This is the script that proves it did what it claimed, and it
does **not** exist — confirmed: no `.ps1`/`.sh`/`.csx` under `apps/CodeCoverage`, and the only
`--spark-*` verbs are `synchronize/verify-model` and `synchronize/verify-security`, all of which
deliberately return **before `Build()`** and touch no database.

### ⚠ The gap that makes this worth writing rather than skipping

The migration's own guard refuses to delete only when `legacy > 0 && qualified == 0` — **total**
failure. A *partial* put (some documents re-keyed, some not) passes that guard, and the delete then
removes the originals of the ones that were missed. Nothing asserts
`qualified == originally-read-total`.

Worse, the put phase logs *"documents **scanned**"*, deliberately not a work counter — so the deploy
logs cannot serve as the evidence either. **A post-hoc script is the only thing that can detect a
partial migration**, and only against a baseline.

### Where it must live

Not beside the existing verbs. It needs a live store, so it belongs after the store is resolvable
(near the `LegacyBranchCompatibility.Enable` hook) with an early return. ⚠ `Program.cs`'s
`isSparkBuildCommand` flag matches **any** `--spark-` prefix and skips service registration — a
database-touching verb must be exempted from it or it will find no services.

⚠ RavenDB runs as container `coverage-raven` and **publishes no host ports**, so this runs from
inside the container.

### What it asserts

1. **Per collection, zero legacy survivors.** ⚠ Use the migration's own `Targets` table as the
   source of truth for *(collection, prefix)* — the prefix is **not** the collection name for five
   of them: `Builds`, `BuildTreeSummaries`, `CommitAssemblies` and `FileCoverages` all nest under
   `Commits/`. A naive "ids starting with the collection name" check reports zeros and passes.
2. **Counting technique:** `limit 0` + query statistics, not `select count()` (invalid RQL outside a
   group-by), and throw on `IsStale` rather than reporting a number known to be behind.
3. **Reference integrity — nothing checks this today.** Every `Commit.Repository`,
   `Commit.LatestBuildId`, `Build.Commit`, `BuildTreeSummary.BuildId`, `CommitAssembly.Commit` /
   `.Repository` / `.Builds[].BuildId`, `FileCoverage.BuildId`, `Repository.Account` and
   `ApiToken.GithubRepositories[]` must carry the qualified prefix. A dangling reference renders an
   **empty page, not an error**.
4. **Field backfills:** zero `Repositories`/`Accounts` lacking `Provider` or `OwnerKey`; zero
   `ApiTokens` with `AccountLogin` set and `AccountOwnerKey` null.
5. **Attachments** — the check M6c asked for and the migration never did: total count **and** the
   set of unique hashes, plus zero attachments left on any legacy-id document. The migration only
   probes per-attachment presence before copying.
6. Emit a before/after table for the A10 record, and exit non-zero on any failure.

⚠ **The attachment baseline is contradictory in this very document**: M6c says *683 on `Builds`
(503 unique)*, while the rehearsal and the migration's own remark say *708 across ~318 builds*.
**Settle which is the live number before asserting against either.**

⚠ **And the deeper problem: a "before" baseline may not exist.** The plan's figures (199,917 keyed
documents, 228,059 total, 708 attachments) come from a **restored copy**, not from the live database
at deploy time. Without a true pre-deploy baseline, this script can only prove the database is
*self-consistent* — not that nothing was lost.

---

## M6g — delete the compatibility shims 🟦 *(investigated 2026-09-21; two corrections to the plan)*

### ⚠ Correction 1 — the stated precondition is NOT satisfied

M6g says `ApiToken.AccountLogin` can go *"after the migration backfills `AccountGitHubId`"*.
**Nothing in this repository backfills `AccountGitHubId`.** Verified: the ForgeQualified migration's
`ApiTokens` pass backfills `AccountOwnerKey` only —

```js
if (d.AccountLogin && !d.AccountOwnerKey) { d.AccountOwnerKey = 'github:' + d.AccountLogin; }
```

`AccountGitHubId` is stamped only at token-save time. So deleting the login fallback today breaks
**every token whose `AccountGitHubId` is null** — precisely the case `ApiToken` documents as
deliberately supported. A backfill must be **written and run first**, and the tokens it cannot
resolve **counted and reported**, not discovered from a support question.

### ⚠ Correction 2 — `AccountLogin` is used far more widely than "a comparison branch"

M6g names one site in `UploadsController`. The real set also includes `ApiTokenActions` (four uses,
including a query that matches on login *because* `AccountGitHubId` is null on older tokens),
`RevokeTokenAction`, the `AccountClaim` emission in `ApiTokenAuthenticationHandler`,
`App_Data/Model/ApiToken.json` (so deleting it forces a model re-synchronize or CI fails), a prior
migration that still has to compile, and ~12 test references.

### ⚠ Correction 3 — `Program.cs:313` is stale; the hook is now at `:356`

### Ordered, with the blocking gate first

1. **Blocked on production state.** Confirm `M_202609190900_BranchesBecomePerLineArmSets` both
   recorded its marker **and** left zero `FileCoverage` documents carrying a legacy `BranchId`-shaped
   first entry. The marker alone does not prove every document converted.
   ⚠ Without the shim, a legacy document deserializes to **zero branch coverage on every line** — a
   *wrong number*, not a visibly missing one.
2. Run M6d; record A10 green. Shim deletion comes **after** verification, not beside it.
3. Delete `LegacyBranchCompatibility`, its hook, and its tests — and update the
   `BranchesBecomePerLineArmSets` remark, which cites the shim as the reason that migration is
   optional.
4. **Write and run the `AccountGitHubId` backfill.** Count and report the unresolvable tokens.
5. Only then remove the `UploadsController` fallback arm, the `AccountClaim` emission, and re-point
   `ApiTokenActions` / `RevokeTokenAction` onto `AccountOwnerKey` / `AccountGitHubId`.
6. Delete the property, re-run `--spark-synchronize-model`, fix the test references.
7. ❌ **Do not** touch `Commit.ParentSha`, `ParentShaSource`, or the trust predicate. Those guard
   against values **already stored** by old action builds that wrote a PR *base* sha into
   `ParentSha`. No migration can repair them — the correct value was never transmitted — so removing
   the check would trust a value the code knows may be wrong.

### ✅ Free win: `ForgeOwner.KeyFromUnqualifiedLogin` is already retired in production

M7 promised *"a grep for it should find no callers"*. Verified: the only non-test hit is the
declaration itself. Six test lines use it as a convenience to build `github:login` keys. Replace
those and delete it — this no longer waits on anything.

---

## ✅ Browser verification — 2026-09-21

Done by **clicking**, not by loading pages, because M19's bug was that the pages rendered fine and
the links inside them did not work.

| Step | Route reached | |
|---|---|---|
| accounts grid → account | `/github/a/acme` | ✅ |
| account → repository | `/github/r/acme/demo` | ✅ |
| repository → commit | `/github/r/acme/demo/c/{sha}` | ✅ |
| **commit → file** | `/github/r/acme/demo/c/{sha}/f?path=src%2Fx.ts` | ✅ **this is the production bug** |
| file → breadcrumb → commit | back to the commit page | ✅ |

No `NG04002`, no unmatched route, 0 console errors. The `Connected` column (renamed from
`IsAppInstalled`) renders on the accounts grid, and `Contributed From Fork` renders on the commit
page, so both model changes survived `--spark-synchronize-model` into the browser.

⚠️ **The dev database has no real coverage data.** Only 18 bare commits and a synthetic
`acme/demo` (repo 999001) fixture with a build tree. The click-through above used that fixture, made
visible by flipping `IsPrivate` in the local RavenDB. Its "Source unavailable" banner is correct —
there is no such file on GitHub — and the *navigation* is what was under test.

### ⚠️ What the browser caught that the test suites did not

The first fork commit wrote the exclusions as `!c.ContributedFromFork`. An absent JSON field does not
satisfy an equality in RavenDB, and every commit written before the field existed has no such
property — so the predicate matched **nothing**, emptying the commit list, history chart, sparklines,
branch list and `?branch=` badge for every pre-existing repository.

**658 .NET tests and a clean Angular build both passed through it**, because every fixture constructs
a `Commit` and therefore writes the field. The symptom was a repository reporting `commits=0` while
its own page linked to one. Fixed to `!= true` at eight sites, and pinned by
`AbsentFieldPredicateTests` (source sweep) and a behaviour test that patches the property out.

---

## M19 — The six links M7 missed and A11 could not see ✅ *(merged as `e0a2ef40`, PR #435)*

Found in production by the owner, minutes after the deploy. **Merged in #435 and browser-verified by clicking through** — see the browser-verification section above.

### What was broken

| Site | Link |
|---|---|
| `commit-files-panel.component.ts` | the file navigation — the reported symptom — **and** `openCommit` beside it |
| `file.component.html` | the page's entire breadcrumb: owner, repo **and** sha |
| `short-sha-renderer.component.ts` | the commit link in **every** grid |

So it was not only the sunburst: every route into or out of the file page was dead, and the
short-sha link was dead everywhere it appears.

### ⚠ Why A11 reported clean — the part worth keeping

A11 was *"grep that no source we control still emits the two-segment form"*. It grepped for the URL
as a **string**. Angular's router-array form is not a string:

```ts
this.router.navigate(['/r', owner, name, 'c', sha, 'f'])   // invisible to a "/r/" grep
```

The check ran, returned nothing, and was believed — **the same failure shape as the bug it was
meant to catch.** A verification that cannot fail is not evidence.

### ⚠ Every one of these components already HAD the forge

- `commit-files-panel` takes `provider` as a required input and used it for its API calls, not its
  navigation.
- `file.component` read `provider` from the route for its API call and never exposed it to the
  template.
- `short-sha-renderer` already declared `item` and read other row values through `valueFor`.

**A half-migrated component compiles perfectly.** The data was present in all three; only the links
were not updated. That is why nothing — compiler, tests, CI, or a browser survey — caught it.

### The guard

`CodeCoverage.Tests/Client/ForgeQualifiedRouteTests.cs` asserts a **property, not a spelling**: a
route whose *first segment* is the literal `r` or `a` is wrong by construction. It covers both the
array and the string form, and carries its own falsification cases — the exact shapes that shipped
broken must match, the correct shape must not, and the walk must find files. A scan that silently
matches nothing passes for the wrong reason, which is precisely how A11 gave a clean result.

It lives in the .NET suite because the Angular runner bundles for the browser, where there is no
filesystem to walk.

### Outstanding

- [ ] Open the PR and let CI run.
- [ ] **Verify in a browser by clicking through**, not by loading pages: sunburst → file → breadcrumb
      → back to commit, and a short-sha link from a grid. This bug is the reason that distinction is
      now written down.

---

## M17 — Deploy to the VPS, with the app still working 🟨 *(deployed 2026-09-21; the regression is fixed in #435 and awaiting the next deploy)*

### As-deployed

PR #434 squash-merged as `0a084191`, which triggered `code-coverage-deploy.yml`, which built the
image and ran the re-key against production before the container served a request. The app came
back up and serves.

⚠ **But the acceptance criterion — "the app still working" — was not met on the first try.** The
owner found it within minutes of the deploy: the **file page was unreachable**, from the sunburst,
from the file list, and from its own breadcrumb.

```
NG04002: 'r/MintPlayer/MintPlayer.AI/c/16a31b920f994427d87e493d79821b39316f32bc/f'
```

Six client navigation sites still built the pre-forge two-segment route. Fixed on
`fix/forge-route-navigation` — see **M19**.

**What this says about the verification that preceded it.** Three separate checks passed and none
could see it: CI was green, both .NET suites were green, and I had surveyed the app in a browser.
The browser survey is the one that should have caught it, and did not, because I checked that
**pages rendered** rather than that the **links between them worked**. A page that renders proves
its own route; it proves nothing about the routes it points at.


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


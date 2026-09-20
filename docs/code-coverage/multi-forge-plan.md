# Plan — Stage 1: de-couple CodeCoverage from GitHub, rebuild sign-in and linking

Companion to [multi-forge-PRD.md](multi-forge-PRD.md). Issue
[#422](https://github.com/MintPlayer/MintPlayer.Spark/issues/422).

**One PR** off `master`, branch `issue-422-forge-abstraction`. Commits per milestone; **the test
suite runs once, at M13** — intermediate milestones are verified by reading the code and building.
GitLab and Bitbucket providers are stages 2 and 3 and are *not* in this PR (PRD §1, D1).

**Blocked on decisions**: only **D6** (M5) is still open. D1-D5, D7, D9, D11 and D13 are decided;
D8, D10 and D12 carry recommendations that nothing in stage 1 depends on.

Legend: 🟦 not started · 🟨 in progress · 🟩 done · ⛔ blocked

---

## M0 — Spikes 🟦

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

## M1 — `IForgeAccessService` behind the existing predicates 🟦

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

---

## M2 — `IForgeClient` and `IForgeFeedbackPublisher` 🟦

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

## M5 — Connected orgs: make the grant explicit ⛔ *(D6)*

The hardest milestone and the only one that can quietly change who sees what.

- Introduce an explicit **connected-org** record: the artifact of someone deliberately connecting an
  org, distinct from the membership list used to decide who may administer it. GitHub's installation
  already *is* that record — make it explicit rather than inferred, so stage 2 can supply a
  different one.
- Keep the forge as the authority on *who* (`product-overview.md:157`, `:170`). This records
  *whether the org opted in*; it is not a parallel permission system.
- ⚠️ **Do not widen management rights.** Today management equals installation visibility with **no
  admin-role check** (`:170`). Replace that answer per provider; do not generalise it.

**Verify:** a test that a viewer who can *see* an org but whose org has not connected gets nothing.

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

M0 first — SP1 can shrink M6 to a re-ingest and re-cost the plan.

M1 → M2 → M8 is the abstraction spine and is strictly ordered. **M6 → M7** is strictly ordered (ids
before routes) and both depend on M5, because the connected-org record is part of what gets re-keyed.
M3 and M4 are independent of the forge spine and can land early; M4 is the largest single milestone
and the one most worth committing in pieces (4a–4i). M9/M10/M11 depend on M6's renames. M12, M13,
M14 last, in that order.

The blocked milestones (M5, M6, M7) are blocked on decisions, not on work — D4, D5, D6 and D7 can be
answered at any time.

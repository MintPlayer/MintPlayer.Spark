# Plan — branch and pull-request coverage badges, and the sticky PR comment

**Implements:** [`coverage_branch_pr_badges_PRD.md`](coverage_branch_pr_badges_PRD.md) Option B ·
**Issue:** none yet — file one first; design authority is `docs/code-coverage/roadmap-2026-08.md` T2.1 M11.5 ·
**Branch:** `feat/coverage-branch-pr-badges` · **Base:** `master` @ `c1c12b9b` ·
**Release:** server deploy only; no npm or NuGet version bump (the action changes, but `coverage-upload-v1` moves by its own workflow) ·
**Status:** Not started

All code lands as **one pull request** in `MintPlayer.Spark` — server, action, workflows, SPA, docs and
tests — per `CLAUDE.md`. No follow-up PR, no phase 2.

`apps/CodeCoverage` is production. Every server milestone below is deploy-safe on its own: new
document fields are additive and read as null, the badge keeps its never-404 contract at every step,
and no milestone can make an existing badge show a wrong number. The comment publisher is the only
outward-facing new behaviour and it lands last, behind the permission check S1 answers.

## Sequencing

```
S1..S4 (spikes, results recorded below)
  ├─> M1 badge: ?pr=, completeness, label set, escaping, ETag
  │     └─> M2 SPA branch picker + snippets
  ├─> M3 capture the PR base ref (action + server + contract)
  │     └─> M4 PullRequestFeedback + comment renderer
  │           └─> M5 publish on finalize (extends PublishFeedbackRecipient)
  │                 └─> M6 publish on PR opened/reopened   [gated by S1]
  └─> M7 security declaration + docs
        └─> M8 dogfood on a live PR of this repository
```

M1 and M3 are independent and can be built in either order. M2 depends only on M1. M4–M6 are strictly
sequential — the renderer must exist before anything publishes, and the finalize path must be proven
before the open path adds a second trigger against the same document. M7 can land any time after M1.
M8 is last by definition.

Only M6 needs a GitHub permission that may not be granted yet; if S1 says the production installation
lacks `Pull requests: write`, M4/M5 still land (the outbox records `Unavailable`) and M6's dogfood
moves behind the consent, which is a GitHub-side action, not code.

---

## Spikes

### S1 — the production installation's `Pull requests` permission — **ANSWERED: already granted**

App slug `coverageproduction` (id 4574022), read from the check-runs on
`MintPlayer/MintPlayer.AspNetCore.SpaServices#79`.

Declared on the App (`GET /apps/coverageproduction`):

```
checks: write   contents: read   emails: read
members: read   metadata: read   pull_requests: write
```

**Granted to the org installation** (`GET /orgs/MintPlayer/installations`), 2026-09-03:

```
checks: write   contents: read   members: read
metadata: read  pull_requests: write
repository_selection: all   suspended_at: null   updated_at: 2026-08-20
```

`pull_requests: write` is **accepted, not merely requested** — M6 is unblocked and needs no consent
dance. `repository_selection: all` means every org repository is covered.

Worth noting for M7's docs: `emails: read` is declared on the App but **absent from the installation
grant**, so there is a real pending un-accepted raise right now. It does not affect this work, but it
proves the accepted-vs-declared distinction is live in this installation and is exactly why H7's
"403 → `Unavailable`, never `Retry`" classification has to exist.

`apps/CodeCoverage/README.md:88-95` declares `Pull requests: Read & write` as required for check-run
feedback, and check-runs are live — but "declared in the manifest" and "accepted by the installation"
are different facts, and `GitHubEventsRecipient.cs:69` exists precisely because they diverge.

Mint an App JWT the way `Services/GitHubAppReadinessService.cs:88-89` does and read the installation's
`permissions` map for this repository's account. Record the exact map here.

If `pull_requests` is not `write`: M6's dogfood is blocked on re-consent, and the PRD's H7 handling
(403 → `Unavailable`, never `Retry`) becomes the behaviour users actually see until consent lands. It
does not block M4 or M5.

### S2 — does camo re-render an updated badge inside a comment?

The sticky comment carries an `<img>` for public repositories. GitHub proxies it through camo. Post a
`?pr=` badge into a scratch PR, change the underlying coverage (a second upload against that head sha),
and watch whether the rendered image changes within the 300 s `max-age`.

If it does not — if camo freezes the first fetch for materially longer — then an image in a sticky
comment is a stale number, which is worse than no image. In that case the comment carries text numbers
for **all** repositories, H1's answer becomes universal, and the badge image stays a README/panel
feature only. Record the observed behaviour and the decision.

### S3 — how often is PR identity actually present? — **ANSWERED, by a different route**

The intended route is closed: `/api/browse/*` now returns **401 anonymously** (verified 2026-09-03 for
`/repos/{o}/{n}`, `/commits`, `/branches`) — `[SparkAuthorize("Browse", "Coverage")]` at
`BrowseController.cs:32` plus the authz migration that moved those grants off the `anonymous` group.
So `PullRequestNumber` cannot be counted from outside, and **the badge is the only anonymous surface
this app has**. Two consequences:

- M7 step 1 is load-bearing, not cosmetic: `Badge/Coverage` really is an undeclared anonymous surface,
  and it is the *only* one.
- M2's branch picker is unaffected — the SPA caller is authenticated.

Measured instead through the badge itself, which is the surface users actually hit: for 71 non-fork PR
head branches across the 7 MintPlayer repositories whose headline badge resolves,
`?branch={headRefName}` returned a number for **18** and `unknown` for 53.

The ratio is not the finding; the *shape* is. Non-resolution is almost entirely **PRs that predate
coverage onboarding in their repository** — in every repo the resolving PRs are the newest ones, and
the boundary sits at the PR that introduced coverage (`feature/code-coverage` in
`MintPlayer.AspNetCore.SpaServices`, `feature/test-coverage` in `MintPlayer.AspNetCore.Tools`, and so
on). That is correct behaviour: no data, rendered `unknown`.

**No mislabelling was observed.** F3's failure mode — a branch serving the wrong branch's number —
did not appear once in 71 probes. The observed failure is absence, which the never-404 contract already
handles correctly. So F3 stays documented-not-fixed with evidence behind that choice, and `?pr=`'s
reliability is not in doubt for webhook-covered repositories (which, per S1's
`repository_selection: all`, is every org repository — the authoritative writer at
`GitHubEventsRecipient.cs:181` runs for all of them, so the action's best-effort PR number is a
fallback that rarely matters here).

**New finding — dependabot PRs can never resolve, and this changes M6.** `MintPlayer.Spark` uploads
coverage on every PR, yet all four sampled dependabot PRs (#344–#347) return `unknown`. Cause:
`.github/workflows/pull-request.yml` authenticates the upload with `secrets.COVERAGE_TOKEN` (`:205`)
and grants no `id-token: write` (`:18-21`), so OIDC is unavailable — and **dependabot-triggered runs do
not receive repository secrets**. No credential, no upload, ever. A publish-on-open comment would
therefore sit at "waiting for coverage for `abc1234`" on every dependabot PR permanently. Recorded as
PRD H12; M6 gains a step.

### S4 — comment adoption and notification behaviour

On a scratch PR, with the App credential: create a comment containing
`<!-- coverage-bot:pr-summary -->`, then (a) clear the stored id and confirm
`issues.listComments` + marker + author match re-adopts exactly that comment, (b) edit it and confirm
no new subscriber notification is produced, (c) delete it and confirm the adoption path creates
exactly one replacement.

This is the mechanism behind G6 and exit criteria 7–8. If an edit *does* notify, the publisher must
suppress no-op edits (compare the rendered body against the last published body before writing) —
record that as a required addition to M5.

---

## M1 — badge: `?pr=`, completeness, the closed label set, escaping, ETag

Files: `apps/CodeCoverage/CodeCoverage/Controllers/BadgeController.cs:30-77`,
`apps/CodeCoverage/CodeCoverage/Badges/BadgeRenderer.cs:11-53`,
new `apps/CodeCoverage/CodeCoverage.Tests/Badges/BadgeRendererTests.cs`,
new `apps/CodeCoverage/CodeCoverage.Tests/Controllers/BadgeControllerTests.cs`.

1. `BadgeRenderer`: change `Coverage(double? percent)` to `Coverage(double? percent, bool partial = false)`,
   selecting the label from the closed set `"coverage"` / `"coverage (partial)"`. Keep the colour scale
   and `unknown` semantics byte-identical. No `label` parameter reaches the public surface.
2. `BadgeRenderer.Render`: XML-escape `label` and `value` before interpolation (defence in depth — with
   the closed label set nothing caller-controlled arrives, and that ordering matters: the escape is not
   the control, the closed set is). Leave `TextWidth`'s 6.6px approximation alone but verify the longer
   label renders without clipping.
3. `BadgeController`: add `int? pr` to the action signature alongside `branch` and `token`. Reject the
   both-specified case by preferring `pr` and documenting it — never by erroring, which would break
   never-404's spirit. Note F5: until this lands, `?pr=` is silently dropped and the headline number is
   served instead (measured in production, PRD §3.1), so this step is what makes every `?pr=` URL
   written before the deploy stop lying.
4. Replace `LoadBranchCoverage` with a single resolver taking the selector (branch name or PR number)
   and returning `(CoverageSummary? summary, bool partial)`:
   - query `Indexes.Commits_ByRepository` on `Repository == id` plus either `Branch == branch` or
     `PullRequestNumber == pr`, filtered on `CompleteCoverage`, newest by `AuthoredAt` — this is F1's
     fix and needs no new index (`Indexes/Commits_ByRepository.cs:21,25`);
   - if that yields nothing, re-query on `HasCoverage` and return `partial: true`;
   - `OfType<Commit>()` then `commit?.Coverage`, as the existing code does.
5. Add the PR-scoped signature (PRD §5.5 Option 3): a `Badges/BadgePrSignature.cs` helper computing
   HMAC-SHA256 over `("badge-pr", repository.GitHubId, prNumber)` with `Coverage:BadgeSigningKey`,
   truncated to 128 bits, hex-encoded. Extend `MayView` (`:68-77`) to accept **either** a matching
   `token` (unchanged path) **or** a `sig` valid for the requested `(repository, pr)`, both under
   `CryptographicOperations.FixedTimeEquals`. A `sig` without a `pr`, or one computed for another PR or
   repository, is simply not a match — grey `unknown` at 200, never an error. No signing key configured
   → `sig` never matches, which is why M4 falls back to a text-only body.
6. Add a weak ETag over `(label, value, colour)` and honour `If-None-Match` with 304. Leave
   `[ResponseCache(300)]` and the imperative `Cache-Control` at `:51-53` **exactly** as they are — the
   header must keep depending only on whether a token was presented.
7. Tests (`BadgeRendererTests`, pure, no Raven base class): each of the seven colour stops at its
   boundary, `null` → grey `unknown`, `LinesCoverable == 0` → `unknown` not 0%, the partial label, and
   an escaping case asserting no raw `<`/`&` can reach the output.
8. Tests (`BadgeControllerTests`, deriving from `CoverageRavenTest` per the app's convention): the
   three invariants (unknown repo / wrong token / unknown branch and unknown PR all `200` + grey);
   header parity between public and private repos with and without a token; `?branch=` and `?pr=`
   resolution against seeded commits; the `Complete`-preferred-over-`Partial` ordering; a valid `sig`
   admitting a private repo's PR badge while the same `sig` on another PR or repository renders grey;
   and the default-branch agreement that is exit criterion 1. Seed with explicit ids via
   `Commit.DocumentId(...)`, `WaitForIndexing(store)`, and construct the controller directly through a
   `ServiceCollection` — the `BrowseControllerTests` shape, not `WebApplicationFactory`.

**Deploy-safe:** yes — additive query parameter, and the only behaviour change to an existing URL is
that `?branch=` stops reporting partial numbers as whole ones, which is the fix.

## M2 — the repository page offers the variants

Files: `apps/CodeCoverage/CodeCoverage/ClientApp/src/app/components/repo-badge-panel/repo-badge-panel.component.ts:20,57-71`,
`apps/CodeCoverage/CodeCoverage/ClientApp/src/app/services/browse.service.ts`.

1. Add a `getBranches(owner, name)` call to `browse.service.ts` against the existing
   `GET /api/browse/repos/{owner}/{name}/branches` (`BrowseController.cs:207-225`) — no server change.
2. Add a branch `<select>` to the panel, populated from that call, defaulting to `r.defaultBranch`.
   Remember: `.form-control` only works inside `<bs-form>` and grid classes only inside `<bs-grid>`;
   follow the panel's existing bs-card markup.
3. Drive both the preview `<img>` and `badgeMarkdown` from the selection. When the selection equals the
   default branch, emit the **parameterless** URL — the snippet every existing README already uses must
   not change shape.
4. Keep the private-repo `?token=` append exactly as it is, manager-only, and keep it last in the query
   string so a copied URL reads legibly.
5. Update the `:20` caption from the fixed "Default branch (…)" to reflect the selection.
6. Document `?pr={n}` in the panel as a short static note (a PR picker is not worth a server endpoint;
   the number is in the reviewer's URL bar).

**Deploy-safe:** yes — client-only, and the default selection reproduces today's output byte-for-byte.

## M3 — capture the pull request's base ref

Files: `apps/CodeCoverage/action/src/context.ts:21-42`, `apps/CodeCoverage/action/src/main.ts:69-94`,
`apps/CodeCoverage/action/src/context.test.ts`,
`apps/CodeCoverage/CodeCoverage/Controllers/UploadsController.cs:74,122-128,457-477`,
`apps/CodeCoverage/CodeCoverage.Library/Entities/Commit.cs:22-43`,
`apps/CodeCoverage/CodeCoverage/Recipients/GitHubEventsRecipient.cs:160-191`.

1. `context.ts`: on a `pull_request*` event, also read `GITHUB_BASE_REF` (falling back to
   `payload.pull_request.base.ref`) and `payload.pull_request.base.sha` — the latter is already in the
   test fixtures at `context.test.ts:43,54` and merely unconsumed. Leave the `commitSha` logic alone;
   the head-sha choice is deliberate and correct.
2. `main.ts`: append `baseRef` and `prBaseSha` to the multipart form, **omitted when empty**, matching
   how `branch` and `pullRequestNumber` are already conditionally appended.
3. `context.test.ts`: extend the existing push / PR / unreadable-payload cases to assert the two new
   fields, including that a push event sends neither.
4. `UploadsController`: add `BaseRef` and `PrBaseSha` to `UploadForm` (`:457-477`); write
   `commit.PullRequestBaseRef ??= form.BaseRef` and `commit.PullRequestBaseSha ??= form.PrBaseSha`
   beside the existing `??=` block at `:122-128`. Add `"pr-base-ref"` to `SupportedFeatures` (`:74`);
   **do not touch `contract`** — it stays `1`.
5. `Commit.cs`: add the two nullable properties with doc comments stating provenance (webhook
   authoritative, upload best-effort) and that they are the PR *target*, not the head.
6. `GitHubEventsRecipient.OnPullRequest`: set both with plain `=` from `pr.Base.Ref` / `pr.Base.Sha`,
   the way `:180-181` already sets `Branch` and `PullRequestNumber`. This is the authoritative writer.
7. Do **not** change `PatchCoverageCalculator`'s diff base (`:25`) or `BaseResolver` in this PR. The new
   fields are recorded and used by the comment; repointing patch coverage at the true merge-base is a
   behaviour change to a shipped number and belongs to the honest-numbers backlog (T1.x), not here.
   Note the discrepancy in the comment body instead, if S3 shows it matters.
8. The bundle is a committed artifact: `.github/workflows/pull-request.yml:224-254` runs
   `compile-ts-action` in `mode: verify` and will fail on drift. Rebuild `dist/index.js` in the same
   commit as the `src/` change.

**Deploy-safe:** yes — the server accepts old payloads unchanged (both fields optional), and an old
server ignores the two new multipart fields by model binding, as documented at `upload-api.md:282`.

## M4 — `PullRequestFeedback` and the comment renderer

Files: new `apps/CodeCoverage/CodeCoverage.Library/Entities/PullRequestFeedback.cs`,
new `apps/CodeCoverage/CodeCoverage/Feedback/PullRequestCommentRenderer.cs`,
new `apps/CodeCoverage/CodeCoverage.Tests/Feedback/PullRequestCommentRendererTests.cs`.

1. `PullRequestFeedback`: `Id` = `PullRequestFeedbacks/{repoGitHubId}/{prNumber}` with a static
   `DocumentId(long gitHubId, int pr)` helper — the app's convention (`Repository.cs:73`,
   `Commit.DocumentId`) — plus `Repository` (`[Reference]`), `PullRequestNumber`, `CommentId` (`long?`),
   `LastPublishedSha`, `LastPublishedBodyHash`, `LastPublishedAtUtc`, `State`, `Attempts`,
   `NextAttemptAtUtc`, `Error`. Mirror `BuildFeedback`'s state vocabulary exactly
   (Pending|Posted|Retry|Unavailable|Failed) so M5 can reuse its retry shape.
   `LastPublishedBodyHash` exists for S4's possible no-op-edit suppression; leave it unread if S4 says
   edits are silent.
2. `PullRequestCommentRenderer`: pure, no I/O, two entry points — `RenderPending(...)` and
   `Render(verdict, assembly, commit, repository, isPrivate)`. Both emit the marker
   `<!-- coverage-bot:pr-summary -->` as the first line.
3. Body content per PRD §5.4: the badge image — plain `?pr={n}` for a public repository, and
   `?pr={n}&sig=…` from M1's helper for a private one (PRD §5.5 Option 3), falling back to a text-only
   body when no signing key is configured or if S2 says camo freezes the image — then project + patch
   numbers from the `GateEvaluator` verdict, the delta versus
   `PullRequestBaseRef`, the assembly `Completeness` named plainly with its reason when `Partial`, links
   to `/r/{owner}/{name}` and the commit, and a footer naming the head sha.
4. Never interpolate a `BadgeToken` — assert it in a test, not just in review.
5. Tests: marker present and first; a private repo body carries a `sig=` URL and **no** `token=` and no
   `BadgeToken` value anywhere; with no signing key configured the private body carries no `/badge/`
   URL at all; a
   `Partial` assembly names its reason; the pending shape names the sha it is waiting for; a
   verdict-driven body reports the same percentage the verdict carries.

**Deploy-safe:** yes — a new entity nothing writes yet and a renderer nothing calls yet.

## M5 — publish on finalize

Files: `apps/CodeCoverage/CodeCoverage/Feedback/PublishFeedbackRecipient.cs:33-136`,
`apps/CodeCoverage/CodeCoverage/Feedback/PublishFeedbackCronJob.cs`,
new `apps/CodeCoverage/CodeCoverage.Tests/Feedback/PullRequestCommentPublishTests.cs`.

1. In `PublishFeedbackRecipient`, after the two check-runs are posted (`:108-136`) and inside the same
   invocation — so the comment and the checks cannot disagree (H6) — load or create the
   `PullRequestFeedback` for `commit.PullRequestNumber`, skipping entirely when it is null.
2. Upsert the comment: if `CommentId` is set, `Issue.Comment.Update`; on 404, or when it is null, run
   the adoption path (`Issue.Comment.GetAllForIssue` + marker + App author match) and adopt or
   `Issue.Comment.Create` — the call shape proven at
   `apps/WebhooksDemo/WebhooksDemo/Recipients/LogIssues.cs:25-29`. Store the resulting id,
   `LastPublishedSha` and `LastPublishedAtUtc`.
3. Reuse the installation lookup and the no-installation branch at `:44-53`: no installation →
   `State = "Unavailable"`, quietly (G8).
4. Classify a 403 from the comment call as `Unavailable` with the reason recorded — **never** `Retry`
   (H7). Every other transient failure follows the existing `MaxAttempts = 5` exponential backoff
   (`:29,82-95`).
5. Extend `PublishFeedbackCronJob`'s sweep to also pick up `PullRequestFeedback` rows in `Retry` whose
   `NextAttemptAtUtc` has passed, keeping the existing `Take(32)` bound. A queryable mirror on the
   document is enough; do not add an index unless the sweep's RQL demands one.
6. If S4 found that edits notify, compare the rendered body against `LastPublishedBodyHash` and skip
   the write when unchanged.
7. Tests: PR-less commit publishes nothing; first finalize creates one comment and stores the id;
   second finalize on a new head sha updates the same id and moves `LastPublishedSha`; a cleared id
   re-adopts by marker rather than creating; no installation yields `Unavailable`; a 403 yields
   `Unavailable` with `Attempts` unincremented past the first. Fake GitHub with hand-written fakes in
   the style of `Services/GitHubAuthTestFakes.cs` and `Services/ScriptedDiffService.cs` — no mocking
   framework.

**Deploy-safe:** yes, but this is the first outward-facing write. It only fires for commits that
already carry a PR number, and the worst failure mode is a recorded `Unavailable`.

## M6 — publish on PR opened / reopened

Files: `apps/CodeCoverage/CodeCoverage/Recipients/GitHubEventsRecipient.cs:160-191`,
new `apps/CodeCoverage/CodeCoverage/Feedback/PublishPullRequestOpenedRecipient.cs` (or a message
handled by the existing feedback recipient — pick whichever keeps `GitHubEventsRecipient` free of
Octokit writes),
`apps/CodeCoverage/CodeCoverage.Tests/Feedback/PullRequestCommentPublishTests.cs`.

1. In `OnPullRequest`, for actions `opened` and `reopened` only (`synchronize` is already covered by the
   finalize path), broadcast a message carrying repository id, PR number and head sha. Keep the
   webhook recipient a pure persister; the publish happens on the bus, so a GitHub outage cannot fail
   the webhook delivery.
2. The publisher gates on the repository having coverage history — `LatestCoverage is not null` or any
   commit with `HasCoverage` for that repository (H10). No history → do nothing, record nothing.
3. **Do not post on open when the PR author is a bot** (`pull_request.user.type == "Bot"`, which covers
   dependabot and renovate). S3 measured that dependabot runs get no secrets and no `id-token: write`,
   so they can never upload and the pending comment would strand permanently (H12). Such PRs still get
   a comment if coverage somehow arrives, because M5's finalize path is not gated on this.
4. Publish `RenderPending(...)` through the exact same upsert as M5, against the same
   `PullRequestFeedback` document, so the finalize path later edits this very comment (G6).
5. Tests: opened on a repo with history creates one pending comment; opened on a repo with no history
   creates none; **opened by a bot author creates none**; opened-then-finalized leaves exactly one
   comment with the same id; reopened after a comment exists adopts rather than duplicates.

**Deploy-safe:** yes, and it is the milestone S1 gates. If the installation lacks
`Pull requests: write`, this records `Unavailable` and nothing else happens.

## M7 — declare the badge surface, and the docs

Files: `apps/CodeCoverage/CodeCoverage/App_Data/security.json`,
`apps/CodeCoverage/CodeCoverage/Controllers/BadgeController.cs:19-22`,
`apps/CodeCoverage/docker-compose.yml:44-58`, `apps/CodeCoverage/README.md:74-100`,
`docs/code-coverage/upload-api.md`,
`docs/code-coverage/README.md`, `docs/code-coverage/roadmap-2026-08.md`.

1. Add a `Badge/Coverage` right granted to both well-known groups (`anonymous` `0…000` and
   `authenticated` `0…001`, matching the file's existing shape) and apply
   `[SparkAuthorize("Badge", "Coverage")]` to `BadgeController` beside its `[AllowAnonymous]`. This
   declares what is already true so the surface stops being invisible to the posture report (H11) —
   assert unchanged anonymous behaviour in the M1 controller tests.
2. App README: document all three badge variants with copy-pasteable examples, the never-404 and
   `unknown` semantics, the `coverage (partial)` label, and that a merged PR's badge goes `unknown` once
   `DeletePullRequestBuildsRecipient` runs (`:34-46`).
3. Add `Coverage__BadgeSigningKey=${COVERAGE_BADGE_SIGNING_KEY}` to `docker-compose.yml` beside the
   existing `GitHub__*` entries, and document in the README that the value is server-managed in `.env`
   (never written by a deploy, per `product-overview.md:290`), that rotating it only invalidates the
   badge images inside existing PR comments, and that leaving it unset degrades private-repo comments
   to text-only. **Do not print or echo the value anywhere**, including in a health probe.
4. App README permissions section (`:88-95`): state that the **sticky comment** needs
   `Pull requests: Read & write`, and record S1's finding about the production installation.
5. `upload-api.md`: document `baseRef` and `prBaseSha` as added fields, the `pr-base-ref` feature entry,
   and restate that `contract` stays `1` and that a 404 on `/api/uploads/capabilities` still reads as
   `contract: 0`.
6. `docs/code-coverage/README.md`: add this PRD/plan pair to the **Live** table.
7. `roadmap-2026-08.md`: mark T2.1 M11.5 as delivered for the sticky comment and still open for inline
   annotations, pointing at this pair.
8. State the known limitation plainly somewhere a user will find it: on repositories with no App
   installation the branch and PR labels are **client-asserted** by the uploader
   (`UploadsController.cs:122-123`, first-writer-wins), so a badge for a branch is only as trustworthy
   as the CI that uploaded it. Include S3's measured numbers.

**Deploy-safe:** yes.

## M8 — dogfood on a live pull request

Files: none (or a `docs/` note recording the evidence).

Two witnesses: this repository, and `MintPlayer/MintPlayer.AspNetCore.SpaServices` — which already
posts both check-runs with real numbers and already serves a working `?branch=` badge (PRD §3.1,
measured), so it exercises the comment against an installation that is not this workspace's own.

1. Open the PR for this very branch. It should receive a pending comment within a minute (M6), then the
   real numbers once `pull-request.yml`'s coverage job finalizes (M5). Repeat on a fresh PR in
   `MintPlayer.AspNetCore.SpaServices` to confirm the path works for a consumer repository.
2. Push twice more and confirm the comment count stays at one.
3. Delete the comment by hand, re-run the coverage job, confirm exactly one comment returns.
4. Paste the M2-generated markdown for a non-default branch into the PR body and confirm it renders.
5. `curl -sI` all three badge variants against a public and a private repository and diff the headers.
6. Paste the evidence for exit criteria 1–11 into the verification sweep below.

## Verification sweep (run once, at the end)

Batched deliberately — no test suite runs before this point; intermediate milestones are verified by
reading and type-checking.

- `dotnet test apps/CodeCoverage/CodeCoverage.Tests` — green. Note whether `RAVENDB_LICENSE` was
  present; absent means restricted mode, which is the fork-PR path and must also pass.
- `npm test` in `apps/CodeCoverage/action` (vitest) — green, including the extended `context.test.ts`.
- `npm run test:bundle` and `compile-ts-action` in `mode: verify` — no bundle drift.
- `dotnet build` across the solution — no new analyzer warnings, in particular none from the Spark
  source generators referenced as an `Analyzer` by the test project.
- Exit criteria 1–11 from the PRD, each with the observed evidence pasted here.
- Confirm no secret reached a committed file: grep the diff for `covt_`, `BadgeToken` interpolation
  into markdown, a literal `Coverage__BadgeSigningKey` value (the compose entry must reference
  `${COVERAGE_BADGE_SIGNING_KEY}`, never inline it), and any `.pem` content.

## Implementation status (2026-09-03)

| Milestone | Commit | Notes |
|---|---|---|
| S1–S4 | | |
| M1 | | |
| M2 | | |
| M3 | | |
| M4 | | |
| M5 | | |
| M6 | | |
| M7 | | |
| M8 | | |

## Decisions

- **The server posts the comment, not the action** — fork pull requests get a read-only
  `GITHUB_TOKEN`, so an action-side comment cannot serve the population a public coverage server exists
  for; the server's App credential is unaffected by fork status and already posts check-runs on fork
  PRs. PRD §4.
- **The badge label is a closed set of server constants** — `"coverage"` and `"coverage (partial)"`,
  with no `?label=`. `BadgeRenderer.Render` interpolates into SVG unescaped
  (`Badges/BadgeRenderer.cs:33-50`); keeping caller text out of the renderer is a stronger control than
  escaping it, and escaping is added anyway. PRD §5.3, H2.
- **Private repositories get a PR-scoped signed badge URL, not `BadgeToken`** — the owner is right that
  a private PR comment is only visible to repo readers, so this is not a public leak. But `BadgeToken`
  is redacted to non-managers today (`RepositoryActions.cs:36-40`, `BrowseController.cs:531`), so
  embedding it would widen it from managers to every reader, and it is a repo-wide never-expiring
  bearer credential whose rotation breaks every README badge. An HMAC over
  `(repoId, prNumber)` costs one derived secret, is scoped to the single PR, and is independent of
  `BadgeToken`. Text-only body is the fallback when no key is configured. PRD §5.5, H1.
- **Parameterized badges prefer a `Complete` assembly and label a `Partial` fallback** — today
  `?branch=` filters on `HasCoverage` while the headline badge is promoted only from `Complete`
  (`CommitAssembler.cs:348-366`), so the two disagree on the same branch. PRD F1, §5.3.
- **Stickiness is keyed on the pull request, not the build or the sha** — `BuildFeedback` is per-build,
  so a comment id stored there would duplicate on every push. Hence a separate
  `PullRequestFeedbacks/{repo}/{pr}` document. PRD §5.2, G6.
- **A 403 on the comment call is `Unavailable`, never `Retry`** — an installation that has not accepted
  the raised permission would otherwise burn five attempts per build forever. PRD H7.
- **Never-404 is preserved without exception** — an unknown branch or PR renders grey `unknown` at
  HTTP 200, and `Cache-Control` keeps keying only on whether a token was presented. Both rules exist to
  close a private-repository existence oracle (`BadgeController.cs:13-18,48-50`). PRD G3, H4.
- **`Commit.Branch`'s first-writer-wins model is documented, not fixed** — a branch-history model is a
  far larger change than this feature; S3 measures how often it bites and M7 states the limitation.
  PRD H8, §8.
- **Patch coverage's diff base is not repointed at the true merge-base** — `PullRequestBaseSha` is
  captured and used by the comment, but changing `PatchCoverageCalculator`'s base
  (`Ingestion/PatchCoverageCalculator.cs:25`) would move a shipped number and belongs to the
  honest-numbers backlog. Plan M3 step 7.

# PRD — a coverage badge for any branch or pull request, and a bot that posts it when a PR opens

**Status:** Built (M1–M7) in [#361](https://github.com/MintPlayer/MintPlayer.Spark/pull/361); M8 awaits deployment ·
**Date:** 2026-09-03 · **Plan:** [coverage_branch_pr_badges_plan.md](coverage_branch_pr_badges_plan.md)
**App:** `apps/CodeCoverage` (production, coverage.mintplayer.com)
**Origin:** Four-agent investigation of the badge endpoint, the upload pipeline, the GitHub App path
and the docs corpus, 2026-09-03. Every claim in §3 carries a `file:line` pointer; the ones marked
**[verified]** were read in the source this session.

> Written before the work. Where the build disagrees with it, the plan's milestone notes say so.

---

## 1. The question

The repository page advertises exactly one badge — `/badge/{owner}/{name}.svg` — and the panel labels
it *"Default branch"*. A user working on a feature branch, or reviewing a pull request, has no badge
they can point at, and nothing tells them one is possible. The ask has three facets:

1. **A badge for a branch that is not the default branch.**
2. **A badge for a pull request** — the unit a reviewer actually looks at, which is not the same thing
   as a branch (a PR has a head branch, but a branch may have no PR, several over its life, or one
   whose head has moved).
3. **A bot that posts that badge into the pull request automatically**, when the PR is opened, without
   the author wiring anything up.

The sentence the server must be able to say, in a PR, unprompted, within a minute or two of CI
finishing:

> *Coverage for this pull request is 71.4% — here is the badge, here is how it compares to the branch
> you are merging into, and here is the link to the full report.*

### 1.1 Premises the investigation corrected

Two things in the framing are already false, and the scope changes accordingly.

**"The badge only seems to be possible for the main branch" — the endpoint already supports
`?branch=`.** `BadgeController` takes a `branch` query parameter and resolves the newest covered
commit on that branch (`Controllers/BadgeController.cs:32,41-43,58-66`) **[verified]**. It has worked
since the badge shipped. It is undocumented (no mention in `docs/code-coverage/upload-api.md` or the
app README), untested (`grep -l Badge` over `CodeCoverage.Tests` returns nothing) **[verified]**, and
unreachable from the UI — the panel renders one hard-coded URL and offers no picker
(`ClientApp/src/app/components/repo-badge-panel/repo-badge-panel.component.ts:57-71`) **[verified]**.
So facet 1 is a **discovery, documentation, hardening and UI** problem, not a query problem. There
are real defects behind the parameter (§3.1), but they are defects in something that exists.

**"I would want the bot to comment" — the server is already a bot that writes to pull requests.**
`Feedback/PublishFeedbackRecipient.cs` posts and updates two check-runs, `coverage/project` and
`coverage/patch`, through a GitHub App installation token, behind a durable outbox with bounded
exponential retry swept every five minutes (`Feedback/PublishFeedbackRecipient.cs:25,29,71,82-95,108-136`;
`Feedback/PublishFeedbackCronJob.cs`; `Entities/BuildFeedback.cs`) **[verified]**. The App is
installed, its private key is mounted in production, the server is publicly reachable over TLS for
inbound webhooks, and the `pull_request` webhook already fires and already records the PR number
(`docker-compose.yml:44-58`; `Recipients/GitHubEventsRecipient.cs:160,180-181`) **[verified]**. So
facet 3 is **one more publisher on an existing outbox**, not new infrastructure. It is also already
specified: `docs/code-coverage/roadmap-2026-08.md` T2.1 M11.5 describes the sticky comment, and M11.0–
M11.4 around it are built. This PRD closes M11.5.

What is genuinely absent: a **PR-scoped badge** (`?pr=`), a **comment publisher and its identity
document**, the **PR base ref** (never captured anywhere, §3.3), and any **test coverage of the badge
surface at all**.

## 2. Goals and non-goals

### Goals

- **G1** — `GET /badge/{owner}/{name}.svg?branch={ref}` is documented, tested, and correct: it agrees
  with the headline badge on the default branch, and never silently reports a subset's number as a
  whole.
- **G2** — `GET /badge/{owner}/{name}.svg?pr={n}` renders the coverage of a pull request's newest
  covered head commit.
- **G3** — Both variants preserve the three badge invariants without exception: **never 404**,
  **`Cache-Control` keyed only on whether a token was presented**, **no user-controlled text in the
  SVG**.
- **G4** — The repository page lets a user pick a branch and copy a working markdown snippet for it,
  and does the same for the default-branch badge it already shows.
- **G5** — When a pull request is opened on an installed repository that has coverage history, the App
  posts one comment carrying the PR's coverage badge, the project and patch numbers, the delta against
  the base branch, and a link to the report.
- **G6** — That comment is **sticky**: subsequent pushes, re-runs and re-finalizes update the same
  comment. A PR with forty pushes has one coverage comment, not forty.
- **G7** — A private repository's PR comment carries a working badge image, without publishing
  `Repository.BadgeToken` into it (D4).
- **G8** — Repositories with no App installation (the OIDC-only population) degrade exactly as they do
  for check-runs today: outbox state `Unavailable`, quietly, no error surfaced to CI.

### Non-goals

- No change to the badge's visual design, colour scale or `unknown` semantics.
- No per-branch badge *capability*. A per-PR signed URL is in scope (D4); a token narrower than the
  repository for the general badge surface is not.
- No inline (line-level) PR annotations. T2.1 M11.5 pairs them with the sticky comment; they are a
  separate, much larger diff against the file-coverage model.
- No new gate semantics. `GateEvaluator` decides pass/fail today and keeps deciding it; the comment
  reports that verdict, it does not compute one.
- No badge for a tag, a commit sha, or an arbitrary ref. Branch and PR are what was asked for.

## 3. Measured starting state

### 3.1 The badge endpoint (`apps/CodeCoverage/CodeCoverage`)

One endpoint, one renderer, zero tests.

`Controllers/BadgeController.cs` — `[ApiController]`, `[AllowAnonymous]` (`:20`),
`[EnableRateLimiting("badges")]` (`:22`), route `[HttpGet("badge/{owner}/{name}.svg")]` (`:30`), query
`?token=` and `?branch=` (`:32`). It is the only controller in the app with **no `[SparkAuthorize]`** —
compare `BrowseController.cs:32` (`[SparkAuthorize("Browse", "Coverage")]`) and
`RepoSettingsController.cs:18`. Consequently the badge surface does not appear in the security posture
report at all, and `App_Data/security.json` has no `badge` entry (grep: zero hits) **[verified]**.

Three invariants are load-bearing and documented in the class comment (`:13-18`) and the cache comment
(`:48-50`) **[verified]**:

- **Never 404.** A missing repo, a wrong token, an unknown branch all render the grey `unknown` badge.
  A 404 would confirm a private repository exists.
- **`Cache-Control` keys off the *request*, never the resource.** `public, max-age=300` when no token
  was presented, `private, max-age=300` when one was (`:51-53`). Keying it on `IsPrivate` would
  reintroduce the existence oracle the never-404 rule closes.
- Rate limiting is per-IP with a roomy 600/min window because GitHub's camo proxy funnels every README
  render through a few addresses (`Program.cs:276-287`). `/badge` is also excluded from the SPA
  fallback (`Program.cs:347-349`) and antiforgery is `WarnOnly` for it (`Program.cs:72-79`).

Resolution (`:34-46`): look the repository up by `FullName` through `Indexes.Repositories_Overview`,
then either `repository.LatestCoverage` (no `?branch=`) or `LoadBranchCoverage` (`:58-66`), which
queries `Indexes.Commits_ByRepository` on `Repository == id && Branch == branch && HasCoverage`,
ordered by `AuthoredAt` descending. The percentage is **lines only** —
`LinesCovered * 100.0 / LinesCoverable`, and `LinesCoverable == 0` renders `unknown` rather than 0%.

`Badges/BadgeRenderer.cs` — 54 lines, static, single entry point `Coverage(double? percent)` (`:11`).
Seven-stop colour scale `#e05d44`→`#4c1`, `null` → `#9f9f9f` "unknown" (`:14-23`); label hard-coded
`"coverage"` (`:24`); shields-style flat SVG with fixed element ids `s` and `r` (`:27-51`); width
approximated as `ceil(len * 6.6) + 10` (`:53`). **`label` and `value` are interpolated into the SVG
unescaped** — safe today because both are server constants, an injection sink the moment a branch name
reaches either. No ETag anywhere in the badge path **[verified]**.

Four defects in the existing `?branch=` path:

- **F1 — completeness asymmetry.** The headline badge is promoted only from a `Complete` commit
  assembly (`Ingestion/CommitAssembler.cs:348-366`, with the doc comment "Never a partial assembly …
  the badge serves this number"), while `LoadBranchCoverage` filters on `HasCoverage`, which is true
  for `Partial` assemblies too. So `?branch=main` can report a subset's number while the tokenless
  badge for the same branch reports the whole. The index already exposes `CompleteCoverage`
  (`Indexes/Commits_ByRepository.cs:25`) **[verified]**, so the fix costs nothing.
- **F2 — the headline badge can be claimed by any branch.** `Promote` returns early only when
  `LatestCoverage is not null && DefaultBranch is not null` and the branch differs
  (`CommitAssembler.cs:355-365`). While either is null — and `DefaultBranch` is null for every
  OIDC-provisioned repository, stated at `BrowseController.cs:132` — the first assembly to land, from
  any branch or PR head, becomes the repository's headline number.
- **F3 — `Branch` is first-writer-wins.** `UploadsController.cs:122` is `commit.Branch ??= form.Branch;`
  and `:123` likewise for `PullRequestNumber`. A sha that appears on two branches, is cherry-picked, or
  is uploaded by a PR run before a push (or after), keeps whichever label arrived first, permanently.
  The webhook path writes with plain `=` and is authoritative when it runs
  (`GitHubEventsRecipient.cs:146,180-181`). Per-branch badges query exactly this scalar.
- **F4 — no tests.** Not the renderer, not the controller, not the invariants. The three rules in
  §3.1 are enforced by a code comment and nothing else.
- **F5 — an unrecognized selector silently serves the headline number.** Measured against production
  (below): `?pr=79` renders master's 48.7%, not `unknown`. Model binding drops the unknown parameter
  and the resolver falls through to `repository.LatestCoverage`. So a `?pr=` badge pasted into a README
  or a comment today shows the wrong number *confidently* — the worst failure mode a badge has. M1
  closes it by implementing the parameter; the transition hazard is that any URL written before the
  deploy looks like it works.

**Measured against the live server, 2026-09-03** (`coverage.mintplayer.com`, repository
`MintPlayer/MintPlayer.AspNetCore.SpaServices`, PR #79, head branch
`bugfix/prerendering-aborted-requests`) **[verified]**:

| URL | Renders |
|---|---|
| `/badge/…/MintPlayer.AspNetCore.SpaServices.svg` | 48.7% |
| `…?branch=master` | 48.7% — agrees with the headline, so G1 already holds on this repository |
| `…?branch=bugfix/prerendering-aborted-requests` | **60%** — the per-branch badge works in production |
| `…?pr=79` | 48.7% — F5 |

The same PR carries both check-runs with real numbers (`coverage/project`: *"59.9% (+11.2% vs base
48.7%)"*, `coverage/patch`: *"80.9% of added lines covered"*, both `neutral` because the repository's
gate has Blocking off and no patch target configured). That repository is therefore a complete
end-to-end witness for this PRD: the App is installed, uploads land, the assembly resolves a base, the
publisher posts — and the only thing absent is the comment. It is the natural target for M8's dogfood
alongside this repository.

### 3.2 What the SPA shows

`repo-badge-panel.component.ts` — an `app-repo-badge-panel` bs-card rendered from the *generic* Spark
PO detail page, gated on `entityType.name === 'Repository'`
(`spark/po-detail-page.component.ts:43,45`) **[verified]**. It self-fetches `RepoInfo` via
`GET /api/browse/repos/{owner}/{name}` and builds both the `<img>` and the copyable markdown entirely
client-side (`:57-71`):

```ts
const origin = r.baseUrl || location.origin;
const base = `${origin}/badge/${r.owner}/${r.name}.svg`;
const url = r.isPrivate && r.badgeToken ? `${base}?token=${r.badgeToken}` : base;
return `[![Coverage](${url})](${origin}/r/${r.owner}/${r.name})`;
```

`:20` already labels the image *"Default branch ({{ r.defaultBranch ?? 'unknown' }})"* — the UI frames
the badge as default-branch-scoped but never offers the parameter that would change that. A branch list
endpoint for a picker already exists: `GET /api/browse/repos/{owner}/{name}/branches`, distinct
`Branch` where `HasCoverage`, capped at 200, default branch sorted first
(`BrowseController.cs:207-225`) **[verified]**. `badgeToken` reaches the client only for managers
(`BrowseController.cs:531`, `Actions/RepositoryActions.cs:36-40`), and carries `[IgnoreForIndex]` on
the entity (`Entities/Repository.cs:42-51`) specifically to keep a live secret out of the anonymous
`/spark` grid.

### 3.3 What the upload pipeline knows about branches and PRs

`action/src/context.ts:21-42` is the only place GitHub Actions context is read. It sends `branch`
(`GITHUB_HEAD_REF` on PR events, `GITHUB_REF_NAME` otherwise), `pullRequestNumber`
(`payload.pull_request.number`), `commitSha` (`pull_request.head.sha` on PR events — deliberately the
head, not the ephemeral merge sha), plus run/job/workflow/event identity **[verified]**.

Never read, anywhere: **`GITHUB_BASE_REF` / `pull_request.base.ref`** and
**`pull_request.base.sha`** — the latter sits in the action's own test fixtures
(`context.test.ts:43,54`) but is never consumed. `github.ref` (the fully-qualified ref) is not read
either, so `refs/heads/x` and `refs/tags/x` are indistinguishable, as are a fork's `x` and the base
repo's `x`. The OIDC handler reads no `ref`/`base_ref`/`head_ref` claim (`ApiTokens/GitHubOidc.cs:14-20`)
though GitHub emits them **[verified]**.

Server-side, `UploadForm` (`UploadsController.cs:457-477`) binds every field the action sends;
`Branch` and `PullRequestNumber` land on the `Commit` under `??=` (F3). **`Build` carries no branch and
no PR number at all** (`Entities/Build.cs`) — the unit that holds the coverage numbers, the gate
snapshot and the feedback state has no ref identity, so everything must route through the `Commit`.
`POST /api/uploads/finish` and `GET /api/uploads/status` are keyed on
`(repository, commitSha, runId, runAttempt)` and carry no branch or PR either
(`action/src/main.ts:104-114`; `action/src/status.ts:92-98`).

There is no per-branch or per-PR aggregate document. `Repository.LatestCoverage` is the only
denormalized number; per-branch badges are a live index query per request.

The carry-forward work shipped in `0ea11110` (#356) and matters here: the unit that has a coverage
number is now the **commit assembly**, with `Completeness` ∈ {`Complete`, `Partial`} and reasons
(`noBase, baseWalked, baseMismatch, noFileList, noBlobIds, testsFailed, unmeasuredChanges`). Base
resolution (`Services/BaseResolver.cs:34-74`) tries declared `base-sha` → GitHub compare against
`repository.DefaultBranch` → newest covered commit of `DefaultBranch ?? commit.Branch` → none. Patch
coverage diffs against `build.DeclaredBaseSha ?? commit.ParentSha`
(`Ingestion/PatchCoverageCalculator.cs:25`) — where `DeclaredBaseSha` is this repo's `NX_BASE` from
`nrwl/nx-set-shas`, an *affected*-computation base that is not guaranteed to be the PR merge-base.

So a comment that wants to say *"vs `main`"* cannot honestly say it today: the PR's target branch is
not a thing the server has ever been told.

### 3.4 The bot path

`libs/webhooks/MintPlayer.Spark.Webhooks.GitHub` is a **published NuGet package**
(`MintPlayer.Spark.Webhooks.GitHub`, `10.0.0-preview.71`), not demo code, depending on Octokit 14 and
`Octokit.Webhooks.AspNetCore` **[verified]**. It provides HMAC-SHA256 signature validation that
fail-closes on an empty secret (`Services/SignatureService.cs:11,16,36`), dispatch onto the Spark
message bus as typed plus catch-all messages (`Services/SparkWebhookEventProcessor.cs:22,45,128-146`),
and `IGitHubInstallationService` (`Services/GitHubInstallationService.cs:16`) — per-installation token
cache with a 60 s freshness margin behind a `SemaphoreSlim` refresh gate (`:39`), invalidation plus a
401-remint handler (`:73`, `Services/Internal/`), RS256 App JWT minting over a cached key (`:92,181`),
and cached REST + GraphQL clients per installation (`:122-157`).

`apps/CodeCoverage` project-references it (`CodeCoverage.csproj:35-36`) and wires it at
`Program.cs:161-181`. Four consumers today: `Feedback/PublishFeedbackRecipient.cs` (check-runs),
`Services/GitHubContentService.cs`, `Services/GitHubDiffService.cs`, `Services/GitHubAppReadinessService.cs`
(the `/health/ready` probe) **[verified]**.

`Feedback/PublishFeedbackRecipient.cs` is the thing to extend. It loads Build → Commit → Repository →
`Account.InstallationId` (`:33-46`), records `State = "Unavailable"` when there is no installation
(`:49-53`), reads gate policy from the **base ref** so a PR cannot rewrite its own gate
(`:56-60`, `Feedback/CoverageYml.cs`), posts or updates by stored id (`:108-136`), and retries with
`MaxAttempts = 5` and exponential backoff (`:29,82-95`) swept by `PublishFeedbackCronJob` (Take(32),
5 min). The outbox is `Entities/BuildFeedback.cs`: `State` ∈ Pending|Posted|Retry|Unavailable|Failed,
`Attempts`, `NextAttemptAtUtc`, `ProjectCheckRunId`, `PatchCheckRunId`, `Error` — **and no comment
id**.

The exact call needed is already proven in the workspace:
`apps/WebhooksDemo/WebhooksDemo/Recipients/LogIssues.cs:25-29` posts a bot comment via
`githubClient.Issue.Comment.Create(repoId, number, body)` — a pull request *is* an issue for this API.
`Recipients/DeleteBranchOnPullRequestClose.cs:17,33` is the template for a `pull_request`-triggered
installation-authenticated write, fork detection included.

Repository identity is confirmed: `Repository.DocumentId(long gitHubId) => $"Repositories/{gitHubId}"`
(`Entities/Repository.cs:73`) — so the `/po/repository/Repositories%2F204431316` in the request is
GitHub's numeric repo id, chosen for idempotent webhook upserts. `Account.InstallationId` lives at
`Entities/Account.cs:29`.

**Permissions.** `apps/CodeCoverage/README.md:74-76` documents base grants Contents/Metadata/Pull
requests **read**; `:88-95` documents that check-run feedback "additionally needs **Checks: Read &
write** and **Pull requests: Read & write**", with the caveat that each installation must accept
raised permissions before feedback appears — and `GitHubEventsRecipient.cs:69` already handles
`new_permissions_accepted`. So `Pull requests: write`, which is what a comment needs, is **already
declared as required**; whether the production installation holds it is S1.

Subscribed events: `Repository`, `Push`, `Pull request` (`README.md:97`). Webhook URL
`https://<host>/api/github/webhooks`; the key is mounted at `/run/secrets/github-app.pem`
(`docker-compose.yml:54,58`) and Traefik terminates TLS for `coverage.mintplayer.com` (`:60-72`).

### 3.5 The action-side alternative, measured

`action/package.json:26` depends on `@actions/github`, but `src/` imports only the `context`
singleton (`context.ts:1`). Grep for `getOctokit`, `GITHUB_TOKEN`, `issues.`, `createComment` across
`action/src`, `action.yml` and the action README: **zero functional hits** **[verified]**. There is no
`github-token` input.

Actions cannot declare `permissions` — only workflows can. This repo's own PR workflow grants
`contents: read` + `packages: read` and nothing else (`.github/workflows/pull-request.yml:18-21`), with
a comment noting that an explicit block defaults every unlisted scope to `none`. And for a
`pull_request` event **from a fork, GitHub issues a read-only `GITHUB_TOKEN`** —
`pull-requests: write` cannot be granted, so the POST returns 403 and the comment silently never
appears. The action's own docs corroborate the adjacent restriction: OIDC is "unavailable to fork PRs"
(`action.yml:16`).

## 4. Options

### Option A — the action posts the comment with `GITHUB_TOKEN`

Add a `github-token` input, `getOctokit`, and comment upsert logic to the action; every consuming
workflow adds `permissions: pull-requests: write`.

Costs: **fork PRs cannot be served at all** without a `pull_request_target`/`workflow_run` shim, which
is a known privilege-escalation footgun and a second workflow to maintain. It requires
`wait-for-finalize: true`, holding a CI job hostage to server parse latency — something `action.yml:55`
explicitly discourages. It reimplements in TypeScript the idempotency and retry the server already has
in RavenDB. And with `nx affected`, N upload jobs would each want to comment. Five workflows in this
repo plus five external consumer repositories would need a permissions change.

### Option B — the server posts the comment through the existing App installation ★ recommended

One more publisher inside `PublishFeedbackRecipient`, one identity document, one new field. Fork status
is irrelevant — it is the App's own credential, already exercised on fork PRs by the check-runs.
Retry, backoff, `Unavailable` degradation and the cron sweep come for free. No consumer workflow
changes at all, in this repo or the five external ones.

Costs: comments are on GitHub's **500/hour content-creation** budget, where check-runs are on the
5,000/hr bucket — the sticky design is what keeps this cheap. It needs `Pull requests: write` accepted
per installation (S1). And repositories with no installation get nothing, exactly as they get no
check-runs today.

### Decision

**Option B.** The infrastructure exists, runs in production, and is the only one of the two that works
for fork pull requests — which is precisely the population a public coverage server serves. Option A
would be a capability *regression* dressed as a feature.

The badge work (facets 1 and 2) is orthogonal to this choice and lands the same way either way.

## 5. The design

### 5.1 Vocabulary

- **Badge variant** — one of exactly three: *repository* (no parameter, default-branch headline),
  *branch* (`?branch=`), *pull request* (`?pr=`). No other selector.
- **Sticky comment** — the single comment the App owns on a pull request, identified by an HTML marker
  comment in its body and by a stored id, created once and edited thereafter.
- **PR feedback document** — `PullRequestFeedbacks/{repoGitHubId}/{prNumber}`, the PR-scoped identity
  and outbox state for that comment. Distinct from `BuildFeedback`, which is per-build.
- **Base ref** — the branch a pull request targets (`pull_request.base.ref`). New to this server.

### 5.2 Storage

Three additive changes. No migration; absent fields read as null and behave as "not yet published".

- **`PullRequestFeedback`** (new entity, `CodeCoverage.Library/Entities/`) —
  `Id` = `PullRequestFeedbacks/{repoGitHubId}/{prNumber}`, `Repository` (`[Reference]`),
  `PullRequestNumber`, `CommentId` (`long?`), `LastPublishedSha`, `LastPublishedAtUtc`,
  `State` (Pending|Posted|Retry|Unavailable|Failed), `Attempts`, `NextAttemptAtUtc`, `Error`.
  Keyed on the **PR**, not the build or the sha — that is what makes G6 true across pushes. The state
  fields mirror `BuildFeedback` so the existing cron sweep shape applies unchanged.

  *(As built: three more fields, all for the retry path — `InstallationId`, `PendingBody` and
  `PendingSha`. The retry re-sends the stored body verbatim instead of re-deriving it, because
  re-deriving means re-resolving the base and re-fetching `coverage.yml` over the GitHub API, and
  drift between attempts would let the comment contradict the check-runs it was rendered beside. The
  entity also carries `[GenerateIndex]`: unlike `BuildFeedback` it is a document, so the sweep queries
  its own `State`/`NextAttemptAtUtc` and needs no queryable mirrors.)*
- **`Commit.PullRequestBaseRef`** (`string?`) and **`Commit.PullRequestBaseSha`** (`string?`) — the PR's
  target branch and base tip. Written with plain `=` by the webhook (authoritative) and `??=` by the
  upload (best-effort), matching the existing convention.
- **`UploadForm.BaseRef` / `PrBaseSha`** — two new optional multipart fields, sent by the action from
  `GITHUB_BASE_REF` and `payload.pull_request.base.sha`. Additive per the upload contract: fields are
  added, never removed, and `contract` stays `1`; a new `features` entry `pr-base-ref` advertises them
  (`upload-api.md`, and note that a 404 on `/api/uploads/capabilities` must keep reading as
  `contract: 0`).

### 5.3 The badge

**`?pr={n}`** resolves through the index that already carries PR identity —
`Indexes.Commits_ByRepository` exposes `PullRequestNumber` (`:21`) — as
`Repository == id && PullRequestNumber == n`, newest by `AuthoredAt`, mirroring `LoadBranchCoverage`.
No new index, no schema change. `DeletePullRequestBuildsRecipient` deletes PR builds for
non-default-branch commits after merge (`:34-46`), so a merged PR's badge goes `unknown` once cleanup
runs — correct behaviour, and stated in the docs rather than worked around.

**Completeness (F1).** Both parameterized variants prefer the newest commit whose assembly is
`Complete`, using the index's `CompleteCoverage` field. If none exists but a `Partial` one does, the
badge renders that number under the label **`coverage (partial)`** rather than pretending it is the
whole. This makes the parameterized badges agree with the headline badge on the default branch, which
is G1.

**The label is a closed set of server constants** — `"coverage"` and `"coverage (partial)"`, chosen by
the server, never by the caller. There is no `?label=`. This sidesteps the unescaped-interpolation sink
in `BadgeRenderer.Render` entirely; the branch name and PR number never reach the SVG. XML escaping is
still added to `Render` as defence in depth, and `TextWidth` is exercised for the longer label.

**Invariants unchanged (G3).** Unknown branch, unknown PR, wrong token, missing repository: all
`200 unknown`, grey. `Cache-Control` continues to key only on token presence. `[ResponseCache]` stays
at 300 s; a weak ETag over `(label, value, colour)` is added so camo revalidates cheaply as variants
multiply against the 600/min per-IP window.

**Authorization (D3).** For the general badge surface: still one `BadgeToken` per repository,
`FixedTimeEquals`, public repos open.
*(Revised during M7.)* No `Badge/Coverage` right is declared and no `[SparkAuthorize]` is applied.
`SparkAuthorizeAttribute` derives from `AuthorizeAttribute` and, per its own documentation,
`[AllowAnonymous]` wins over it — so the attribute would never be evaluated on this controller and
would misrepresent the badge as gated in the security posture report. Measured corroboration: the
sibling `BrowseController`, which carries `[SparkAuthorize("Browse", "Coverage")]` *without*
`[AllowAnonymous]`, answers anonymously with `401` + `Www-Authenticate: Bearer` even though
`security.json` grants `Browse/Coverage` to the anonymous group — the grant governs what an
authenticated caller may do, not whether authentication is demanded.

The badge's access control is therefore, in full: public repositories are open, a private
repository's own badge needs `BadgeToken`, and a private repository's PR badge needs the PR-scoped
signature. That is documented rather than declared.

### 5.4 The comment

**Body.** A markdown block opening with the marker `<!-- coverage-bot:pr-summary -->`, then:

- the badge image for the PR — for a public repository the plain `?pr={n}` URL, for a private one the
  PR-scoped signed URL of D4, never `?token={BadgeToken}`,
- project coverage with its delta against the base branch, and patch coverage, taken from the same
  `GateEvaluator` verdict the check-runs report, so the comment can never disagree with the checks,
- the assembly's `Completeness`, named plainly when it is `Partial`, with the reason,
- a link to the report at `/r/{owner}/{name}` and to the commit,
- a footer naming the head sha it describes.

**When it publishes.** Two triggers, one publisher:

1. **PR opened / reopened** (`GitHubEventsRecipient.OnPullRequest`, which already handles
   `opened`/`synchronize`/`reopened` at `:160`) — post the comment immediately, in a *pending* shape
   ("waiting for coverage for `abc1234`"), so the bot answers the ask literally: the comment appears
   when the PR opens. Gated on the repository having coverage history, so a repo that has never
   uploaded is not spammed.
2. **Build finalized** — the existing `PublishFeedbackRecipient` path, immediately after the two
   check-runs, edits the same comment with the real numbers.

*(As built: trigger 1 goes over the message bus rather than posting from the webhook —
`OpenPullRequestCommentMessage` on `coverage-open-pr-comment`, handled by an `IRecipient<>`, so the
webhook stays a pure persister and a GitHub outage cannot fail the delivery and cost us the event.
It also gained a third gate: **bot-authored PRs get nothing** (H12). Trigger 2 stays inline in
`PublishFeedbackRecipient`, where the verdicts already exist, which is what keeps H6 true. A **third**
path exists that this section missed: retries, on `coverage-publish-pr-comment`. Without it a comment
that failed after the check-runs had succeeded — the common case, since checks post first — was
stranded at `Retry` forever, because the sweep only ever queried `Build.FeedbackState`.)*

**Stickiness (G6).** `PullRequestFeedback.CommentId` is the primary key to edit. If it is null, or the
stored id 404s (comment deleted by a human), the publisher lists the PR's comments once and adopts any
whose body contains the marker and whose author is the App; only if none matches does it create.
Editing a comment does not re-notify subscribers, which is the whole point of stickiness — forty
pushes produce one notification, not forty. This also keeps the 500/hr content budget irrelevant in
practice: creates are once per PR, edits are not creates.

*(Confirmed by S4, and the author half of the match earned its keep: a human quoting the bot's body
carries the marker too, so marker-alone would have let the publisher edit somebody else's comment.
`A_humans_comment_carrying_the_marker_is_never_adopted` covers it.)*

**Degradation (G8).** No installation → `State = "Unavailable"`, quietly, no CI-visible error, exactly
as `PublishFeedbackRecipient.cs:49-53` does today. Missing `Pull requests: write` → the Octokit 403 is
caught and recorded as `Unavailable` with the reason, not retried into the `Failed` state, so an
installation that has not accepted the raised permission does not burn five attempts per build.

### 5.5 D4 — how a private repository's comment gets a badge image

The owner's position, and it is correct on audience: a pull request comment on a private repository is
visible only to people who can read that repository, so nothing here is a public leak.

Two facts push against embedding `?token={BadgeToken}` in the comment anyway:

- **It widens the token's audience from managers to every reader.** `BadgeToken` is redacted for
  non-managers today — `Actions/RepositoryActions.cs:36-40` protects the attribute unless
  `CanManageOwnerAsync`, and `BrowseController.cs:531` sends it to the client only when `canManage`
  **[verified]**. A collaborator who can open a pull request cannot currently see it.
- **It is a bearer credential with no scope and no expiry.** Repo-wide, every branch, forever, usable
  from anywhere once copied out of the comment — and rotating it (`RepoSettingsController.cs:28-38`)
  breaks every README badge at the same time.

#### Option 1 — text numbers only for private repos
No image, no credential. Loses the thing that was asked for.

#### Option 2 — embed `?token={BadgeToken}`
Simplest, and defensible on audience. Costs both bullets above.

#### Option 3 — a PR-scoped signed badge URL ★ recommended
`?pr={n}&sig={hmac}` where `hmac` is HMAC-SHA256 over `("badge-pr", repository.GitHubId, prNumber)`
with a server-side key, truncated to 128 bits and hex-encoded. `MayView` accepts **either** a valid
`token` (unchanged) **or** a `sig` matching the requested `(repository, pr)`, compared with
`CryptographicOperations.FixedTimeEquals`.

- Non-guessable, so the never-404 contract is untouched — a wrong `sig` renders grey `unknown` exactly
  like a wrong token.
- Scoped: worthless for another PR, another branch, or the repository headline.
- Independent of `BadgeToken`, so rotation does not break comments and comments do not devalue the
  token.
- Deterministic, so re-rendering the sticky comment produces a stable URL and camo keeps its cache.
- **What it does not hide** (measured in S2): GitHub rewrites the image to a
  `camo.githubusercontent.com` URL whose path is the **hex-encoded origin URL** under a digest, so the
  `sig` is recoverable from it, and camo URLs are not themselves access-controlled. That is no worse
  than the comment's own audience — but it is the reason the credential in a comment must be worth as
  little as possible. A leaked `sig` reveals one pull request's coverage percentage to someone who
  already had the comment; a leaked `BadgeToken` reveals every branch of the repository, forever, to
  anyone.
- The key is one new secret. It reuses the existing production secret channel — the App key is already
  mounted at `/run/secrets/github-app.pem` (`docker-compose.yml:54,58`), so a `Coverage__BadgeSigningKey`
  environment entry follows the established pattern. Absent key → fall back to Option 1's text body
  rather than failing the comment.

#### Decision

**Option 3.** It delivers the image the owner asked for in the population that needs it, at the cost of
one derived secret and about twenty lines in `MayView`, and it leaves `BadgeToken` exactly as
manager-only as it is today. Option 1 remains the automatic fallback when no signing key is configured
or when S2 shows camo freezes the image.

### 5.6 What the UI shows

`repo-badge-panel.component.ts` gains a branch `<select>` fed by the existing
`GET /api/browse/repos/{owner}/{name}/branches` (`BrowseController.cs:207-225`), defaulting to
`r.defaultBranch`. Choosing a branch re-renders the preview `<img>` and the copyable markdown against
`?branch=`; the default-branch selection produces the parameterless URL it produces today, so the
snippet users already have keeps working. A short note documents `?pr={n}` in the panel and in the app
README. The private-repo token continues to be appended only for managers, unchanged.

*(As built: `<bs-select>`, not a native `<select>` with `.form-select`. ng-bootstrap's
`_bootstrap.scss:39` has the bootstrap forms partial commented out, so `.form-select` and
`.form-control` have no global definition in this workspace and those classes would have been inert;
`BsFormControlDirective`'s selector covers only `bs-form input` and `textarea`, never `select`, so
wrapping in `<bs-form>` would not have helped either. The picker also only appears when there is more
than one branch to choose from — otherwise the original caption stands.)*

## 6. Hazards and how the design answers them

| # | Hazard | Answer |
|---|---|---|
| H1 | **A private repo's badge token published into a PR comment.** Not an audience leak — only repo readers see the comment — but it widens `BadgeToken` from manager-only (`RepositoryActions.cs:36-40`) to every reader, and it is a repo-wide, never-expiring bearer credential once copied out. | Private repositories get a **PR-scoped signed URL**, not the token (D4, Option 3). Text-only body is the fallback when no signing key is configured. |
| H2 | A branch name in the SVG label becomes an injection sink (`BadgeRenderer.cs:33-50` interpolates unescaped). | Label is a closed set of server constants; caller text never reaches the SVG. XML escaping added anyway. |
| H3 | The parameterized badge reports a `Partial` subset as if it were the whole (F1). | Prefer `Complete` via the index's `CompleteCoverage`; a `Partial` fallback is labelled `coverage (partial)`. |
| H4 | Adding a 404 for an unknown branch or PR would create a private-repo existence oracle. | Never-404 is preserved without exception; unknown selectors render grey `unknown`. |
| H5 | A comment per push, or per upload job, would be intolerable — and `nx affected` means several jobs per commit. | One `PullRequestFeedback` per PR, edited in place; marker-based adoption recovers from a lost id. |
| H6 | The comment disagrees with the check-runs. | Both read the same `GateEvaluator` verdict in the same recipient invocation. |
| H7 | `Pull requests: write` not accepted by an installation → five failed attempts per build. | 403 is classified `Unavailable`, not `Retry`. |
| H8 | `Branch`/`PullRequestNumber` are first-writer-wins (F3), so a badge can be served under the wrong branch. | Out of scope to fix the model; in scope to **stop the silent wrong answer**: the webhook path stays authoritative, and the docs state that on OIDC-only repos the branch is client-asserted. Recorded as a known limitation, with S3 measuring how often it actually bites. |
| H9 | Badge variants multiply cache keys through camo against 600 req/min/IP. | Weak ETag + unchanged 300 s `max-age`; variants are bounded (one per branch with coverage, one per open PR). |
| H10 | A pending comment on every opened PR of a repo that never uploads coverage. | Publish-on-open is gated on the repository having coverage history. |
| H11 | The badge surface is invisible to the security posture report. | **Documentation, not an attribute.** `SparkAuthorizeAttribute` derives from `AuthorizeAttribute` and `[AllowAnonymous]` wins over it, so a `Badge/Coverage` right applied here would never be evaluated — it would show in the posture report as a gate that is bypassed, which overstates enforcement. The badge is deliberately unauthenticated; its access control is the repository badge token and the per-PR signature, and the docs say so. |
| H12 | **A pending comment that never resolves.** Dependabot-triggered runs receive no repository secrets, and `pull-request.yml` grants no `id-token: write` (`:18-21`), so a dependabot PR can never upload coverage — measured: all four sampled dependabot PRs render `unknown`. Publish-on-open would strand "waiting for coverage" on every one of them, forever. | M6 does not post on open for a PR whose author is a bot (`pull_request.user.type == "Bot"`); such PRs get a comment only if coverage actually arrives, via the finalize path. |

## 7. Spikes (time-boxed, results recorded in the plan)

- **S1 — does the production installation already hold `Pull requests: write`?** Query the App's
  installation permissions with the mounted key (the `GitHubAppReadinessService` path already mints an
  App JWT against `GET /app`). Prove: whether a re-consent dance is needed before G5 can work at all,
  and for which installations. This gates M6, not the badge work.
- **S2 — does GitHub render and *re-render* our badge in a comment?** Post a `?pr=` badge into a
  scratch PR, then change the underlying coverage and confirm camo picks up the new SVG within the
  300 s window. Prove: that an image in a sticky comment is not permanently frozen by the proxy — if it
  is, the comment must carry text numbers for public repos too, and H1's answer becomes universal.
- **S3 — how often is PR identity actually present in production data?** Count commits with
  `PullRequestNumber != null` against builds whose `EventName` is a PR event, over the last 60 days.
  Prove: whether F3/`context.ts:34`'s silent PR-number loss (`context.test.ts:100-107`) is theoretical
  or routine. A high loss rate makes `?pr=` unreliable and shifts emphasis to `?branch=`.
- **S4 — comment adoption and notification behaviour.** On a scratch PR: create a marked comment, edit
  it, and confirm (a) `issues.listComments` + marker match reliably re-adopts it after the stored id is
  cleared, (b) an edit produces no new subscriber notification. Prove: G6 is achievable without
  notification spam.

## 8. Out of scope

- Inline line-level PR annotations (the other half of T2.1 M11.5).
- Fixing `Commit.Branch`'s first-writer-wins model (F3) — a branch-history model is a much larger
  change; this PRD documents the limitation and measures it (S3).
- **Fixing F2** — the bootstrap hole in `CommitAssembler.Promote` (`:355-365`), where any branch or PR
  head can claim the repository headline while `LatestCoverage` or `DefaultBranch` is still null.
  Stated explicitly because M1 fixed F1, F4 and F5 and a reader would reasonably assume all five went
  with them. It is untouched: closing it means deciding what a repository with no known default
  branch *should* show, which is a product question about the OIDC-only population rather than a
  badge bug, and it would change a number that is live today for those repositories.
- Badges for tags, arbitrary shas, or fully-qualified refs; fork-vs-base branch disambiguation.
- Per-branch or per-PR badge capabilities (a token narrower than the repository).
- Any change to the colour scale, the `unknown` semantics, or the check-run names `coverage/project`
  and `coverage/patch` (a compatibility promise, `upload-result-contract.md §4.4`).
- Roadmap T0.1 (backups) and T1.1–T1.4 (honest numbers), which remain the live backlog.

## 9. Exit criteria

1. `GET /badge/MintPlayer/MintPlayer.Spark.svg?branch=master` and the parameterless URL render the
   **same** percentage, and both come from a `Complete` assembly.
2. `GET /badge/MintPlayer/MintPlayer.Spark.svg?pr={n}` for a live PR of this repository renders that
   PR's head-commit coverage; for `?pr=999999` it renders grey `unknown` with **HTTP 200**.
3. For a private repository, every one of `?branch=`, `?pr=` and no-parameter returns
   `Cache-Control: public, max-age=300` without a token and `private, max-age=300` with one — identical
   headers to a public repository, verified with `curl -sI`.
4. For a private repository, `?pr={n}&sig={valid}` renders the badge; the same `sig` against a
   different `pr` or a different repository renders grey `unknown` at HTTP 200; and the PR comment's
   body contains no `Repository.BadgeToken` value (asserted in a test, not only by inspection).
5. A branch whose newest covered commit has a `Partial` assembly renders the label
   `coverage (partial)`; forcing that state in a test asserts it.
6. The repository page's branch picker lists this repo's covered branches, and the copied markdown for
   a non-default branch renders a correct badge when pasted into a scratch PR.
7. Opening a PR on this repository produces exactly **one** App comment within a minute, in the pending
   shape; after CI finalizes, that **same comment id** carries the real numbers, and its project
   percentage equals the `coverage/project` check-run's.
8. Pushing three more commits to that PR leaves the comment count at one, with `LastPublishedSha`
   tracking the newest head.
9. Deleting the comment by hand and re-finalizing re-adopts or recreates exactly one comment (S4's
   mechanism), and `PullRequestFeedback.CommentId` is updated.
10. A repository with no App installation, uploading by OIDC, records
    `PullRequestFeedback.State == "Unavailable"` and surfaces no error to the CI step.
11. `dotnet test apps/CodeCoverage/CodeCoverage.Tests` is green, and badge tests exist where there were
    none: renderer colour stops, the three invariants, `?branch=`/`?pr=` resolution, the signed-URL
    accept/reject cases, and the partial label.
12. `docs/code-coverage/upload-api.md` documents `baseRef`/`prBaseSha` and the `pr-base-ref` feature
    flag; the app README documents all three badge variants; `docs/code-coverage/README.md`'s index
    lists this PRD/plan pair.

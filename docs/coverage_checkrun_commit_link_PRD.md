# PRD — A check run that links to the commit it measured

**Status:** proposed, not started. Investigation complete; five spikes outstanding, two of them
blocking.

**Scope:** `apps/CodeCoverage` (production, coverage.mintplayer.com) — the `coverage/project` and
`coverage/patch` check runs published by the GitHub App, and the PR comment that accompanies them.

---

## Problem

A reviewer reading a pull request sees two coverage check runs and a summary comment. Both tell them
*a number*. Neither takes them to the page that explains it **for the commit that was measured**.

The ask: the check run should carry a full link to the coverage page for that exact commit, with the
host coming from configuration rather than a literal.

The interesting part is that this is not the "add a missing link" task it appears to be. The check
run already renders a **Details** link today, and it already points at `coverage.mintplayer.com` —
just at the site root. Nothing in this repository puts it there. Working out where it comes from,
and whether the page it should point to is actually reachable by the people who click it, is most of
this document.

---

## Prior art

- The PR comment renderer already builds absolute URLs from `Coverage:BaseUrl` and already deep-links
  a commit — but back to *GitHub*, not to us. `PullRequestCommentRenderer.AppendFooter`
  (`apps/CodeCoverage/CodeCoverage/Feedback/PullRequestCommentRenderer.cs:146-169`) emits
  ``<sub>Head [`91b3fae`](https://github.com/…/commit/{sha}) · [full report]({baseUrl}/{provider}/r/{owner}/{name})</sub>``.
  The "full report" href stops at **repository** level. The commit is right there in the same line,
  linked to the forge, and dropped from our own link.
- That method already carries a `⚠` comment (`:151-156`) noting that the *forge-side* commit URL
  cannot be a pure function of owner/name/sha across forges, and deferring a commit-URL builder on
  `IForgeIntegration` to "M15". That is a different URL from the one this PRD adds (theirs points at
  the forge, ours at us) but it is the same shape of problem and the two should land in one place.
- `IForgeFeedbackPublisher`'s own doc-comment
  (`apps/CodeCoverage/CodeCoverage.Library/Feedback/IForgeFeedbackPublisher.cs:12-27`) already says
  the status "carries a headline, a number and a **link**". The link was designed for and never
  implemented.
- The multi-forge work (`docs/code-coverage/multi-forge-PRD.md`) established `ForgeVerdict` as the
  provider-neutral status DTO and `EForgeProvider.ToCanonicalString()` as the fixed lowercase URL
  spelling.

---

## Investigation findings

Measured 2026-09-25 against the repository at `935a9ee4` and against live production. Every claim
below that says "measured" was produced by running the thing, not by reading about it.

### F1 — The check run already has a Details link, and it is not ours

Measured, `GET repos/MintPlayer/MintPlayer.Dotnet.Tools/check-runs/108082314474`:

| field | value |
| --- | --- |
| `name` | `coverage/project` |
| `conclusion` | `neutral` |
| `details_url` | `https://coverage.mintplayer.com` |
| `output.title` | `87.0% (+2.5% vs base 84.5%)` |
| `output.summary` | 138 chars, plain text, no links |
| `output.text` | empty |
| `external_id` | empty |
| annotations | 0 |

`coverage/patch` on the same commit is identical in shape, same root `details_url`.

But a repo-wide grep for `DetailsUrl` / `details_url` / `TargetUrl` finds **no assignment anywhere in
`apps/CodeCoverage`** (the only `TargetUrl` hits are unrelated, in `libs/replication`). The payload
builder sets six fields and no URL — see F2.

The explanation, measured: `GET /apps/coverageproduction` returns
`"external_url": "https://coverage.mintplayer.com"`. **GitHub falls back to the App's registered
homepage URL when a check run omits `details_url`.**

Three consequences, and they are the reason this finding leads:

1. The link users click today is configured in the **GitHub App settings UI**, outside version
   control, per app — production and development are separate App registrations
   (`GitHub:{Environment}:AppSlug`, `appsettings.json:25` = `coveragedevelopment`;
   `docker-compose.yml:177` = `GITHUB_APP_SLUG`). That is why no hardcoded host was ever found in the
   publisher: there isn't one.
2. "The check run has no link" — the conclusion two of the three investigations reached from the
   grep alone — is **wrong**. It has a link. It is a *useless* link, which is a different defect and
   is not fixed by the same reasoning.
3. Omitting `details_url` is therefore **not** a safe degradation. Where the PR comment degrades to
   plain text when `Coverage:BaseUrl` is unset (F7), a check run that omits the field silently gets
   the App homepage instead. "No link" is not an available behaviour.

### F2 — The check-run payload sets six fields and nothing else

`GitHubForgeFeedbackPublisher.PostCheckRunAsync`
(`apps/CodeCoverage/CodeCoverage.GithubIntegration/Feedback/GitHubForgeFeedbackPublisher.cs:57-92`)
is the entire payload:

```csharp
var output = new NewCheckRunOutput(verdict.Title, verdict.Summary);   // :72
// update path :76-82   client.Check.Run.Update(owner, name, id, new CheckRunUpdate { … })
// create path :85-90   client.Check.Run.Create(owner, name, new NewCheckRun(name, sha) { … })
```

Set: `name`, `head_sha`, `status` (always `Completed`), `conclusion`, `output.title`,
`output.summary`. Unset: `details_url`, `external_id`, `output.text`, annotations, `started_at`,
`completed_at`, `actions`.

`output.text` being unused matters — it is a second, larger markdown region GitHub renders **below**
the summary on the check page, and it is free.

### F3 — `ForgeVerdict` is the provider-neutral seam, and it has no URL member

`ForgeVerdict(EForgeOutcome Outcome, string Title, string Summary)` —
`apps/CodeCoverage/CodeCoverage.Library/Forge/IForgeIntegration.cs:292`, with a doc-comment at
`:285-291` warning "GitLab caps a status description at 255 characters and Bitbucket's Code Insights
allows ten data cells."

All three forges have a target-URL field, so widening this record maps losslessly:

| forge | API | URL field |
| --- | --- | --- |
| GitHub | check run | `details_url` (optional) |
| GitLab | `POST /projects/:id/statuses/:sha` | `target_url` (optional) |
| Bitbucket | `POST /repositories/:ws/:slug/commit/:sha/statuses/build` | `url` (**required**) |

Bitbucket requiring it is the argument for putting the URL on the neutral DTO rather than in the
GitHub publisher: the Bitbucket integration cannot be written without it.

GitLab and Bitbucket integrations are deliberate no-op stubs today
(`CodeCoverage.GitlabIntegration/Extensions/SparkBuilderExtensions.cs:42-51`,
`CodeCoverage.BitbucketIntegration/Extensions/SparkBuilderExtensions.cs:34-38`).

### F4 — The per-commit URL already exists, client-side only

`apps/CodeCoverage/CodeCoverage/ClientApp/src/app/app.routes.ts:58-62`:

```ts
{ path: ':provider/a/:login',                 canActivate: [accountRedirectGuard],    children: [] },
{ path: ':provider/r/:owner/:repo',           canActivate: [repositoryRedirectGuard], children: [] },
{ path: ':provider/r/:owner/:repo/c/:sha',    canActivate: [commitRedirectGuard],     children: [] },
{ path: ':provider/r/:owner/:repo/c/:sha/f',  loadComponent: () => import('./pages/file/file.component') }
```

So the target is `{baseUrl}/{provider}/r/{owner}/{repo}/c/{sha}`. `commitRedirectGuard`
(`ClientApp/src/app/spark/vanity-redirects.ts:45`) calls
`GET /api/browse/repos/{provider}/{owner}/{name}/commits/{sha}` and forwards to `/po/commit/{id}`,
the generic Spark detail page, which mounts the Files card (sunburst, folder drill-down, per-file
links into the line-level code viewer) via `po-detail-page.component.ts:47-49`.

**The commit-level URL is built nowhere in C#.** Only the client knows this shape. The PR comment
stops at repository level (Prior art).

### F5 — The base URL already exists and needs no new key

`Coverage:BaseUrl` (env `Coverage__BaseUrl`):

| where | file:line | value |
| --- | --- | --- |
| dev default | `apps/CodeCoverage/CodeCoverage/appsettings.json:10` | `https://localhost:5200` |
| production | `apps/CodeCoverage/docker-compose.yml:167` | `https://coverage.mintplayer.com` |
| test host | `CodeCoverage.Tests/_Infrastructure/CoverageWebAppFactory.cs:81` | `http://localhost` |

Already read in six production places, including **on this exact pipeline**:
`PublishFeedbackRecipient.cs:131` passes it to the comment renderer. Also `Program.cs:169`
(passkey domain), `Program.cs:318` (OIDC `ValidAudience`), `BrowseController.cs:601`,
`UploadsController.cs:299`.

`coverage.mintplayer.com` appears as a literal in **zero** lines of production C#. The only
production-config occurrences are `docker-compose.yml:167` (the value itself) and `:205` (the Traefik
host rule). The "don't hardcode this" requirement is already satisfied by reusing the key.

### F6 — There is no HttpContext on the publish path, and the house rule is against deriving hosts anyway

The publisher runs from a cron sweep and a message recipient, both resolved from the **root**
provider inside a `BackgroundService`:

- `Feedback/PublishFeedbackCronJob.cs:31` — `ISparkCronJob`, `*/5 * * * *`.
- `Feedback/PublishFeedbackRecipient.cs:22` — `IRecipient<PublishFeedbackMessage>`; injected fields
  (`:24-29`) are session, forge resolver, `IConfiguration`, logger. No `IHttpContextAccessor`.
- `libs/messaging/MintPlayer.Spark.Messaging/Services/MessageProcessor.cs:134` — `CreateScope()` off
  the root provider.

So `IHttpContextAccessor` would be null there. Separately, forwarded headers *are* configured
correctly (`Program.cs:35-60`, `:516`) so a request-bound path could trust `Request.Host` — but
`Program.cs:160-168` carries an explicit ⚠ arguing against exactly that for `PasskeyServerDomain`:
pinned to `Coverage:BaseUrl` because it is "the same value the GitHubOidc audience already trusts, so
the two cannot drift apart." Config is the only correct source here, twice over.

### F7 — The existing degradation contract, and why the check run cannot copy it

`PullRequestCommentRenderer` takes `string? baseUrl` and omits links rather than emitting relative
ones (`:122`, `:159`), asserted by
`PullRequestCommentRendererTests.No_base_url_configured_omits_links_rather_than_emitting_relative_ones`
(`:219`). New code should preserve that intent — but per F1, for a check run "omit" means "GitHub
substitutes the App homepage", so the two cannot behave identically. This needs an explicit decision
(D4).

### F8 — ⚠ BLOCKING: on production today, the destination page is not anonymously reachable

This is the finding that changes the size of the work.

The committed configuration says the read surface is anonymous.
`apps/CodeCoverage/CodeCoverage/App_Data/securityPosture.txt` lists exactly eleven rights as the
anonymous surface — `Browse/Coverage`, `Query/{Account,Build,Commit,Home,Repository}`,
`Read/{Account,Build,Commit,Home,Repository}` — and `BrowseController.cs:25-33` carries a comment
stating the right exists "so the vanity pages keep working for anonymous visitors and the posture
report says so out loud."

Measured against production, anonymously, 2026-09-25:

| request | result |
| --- | --- |
| `GET /badge/github/MintPlayer/MintPlayer.Dotnet.Tools.svg` | **200** |
| `GET /api/browse/repos/github/MintPlayer/MintPlayer.Dotnet.Tools` | **401**, `WWW-Authenticate: Bearer` |
| `GET /api/browse/repos/…/commits/91b3fae8…` | **401** |
| `GET /api/browse/repos/github/MintPlayer/NonExistentRepoXyz` | **401** (not 404 — no existence oracle) |
| `POST /spark/po/load` | **401** |

The `WWW-Authenticate: Bearer` challenge says this is rejected at **authentication**, before
`SparkAuthorize` consults the anonymous grant at all — consistent with the Bearer/JWT scheme (added
for GitHub OIDC uploads, `Program.cs:318`) acting as the default challenge scheme.

This is not deploy lag: `[SparkAuthorize("Browse", "Coverage")]` and `securityPosture.txt` both
landed in `e748eee5` (2026-09-01) and production is running code from well after that (it posted
fork-aware neutral verdicts today).

Two consequences:

1. **Linking deeper without fixing this ships a link that dead-ends** for any logged-out reader — the
   majority of people who read a public PR. The guard's catch branch bounces them to `HOME_URL`
   (`vanity-redirects.ts:56`), which reads as "page missing", not "sign in".
2. **The `[full report]` link already posted in every PR comment is already broken** for logged-out
   readers, and has been since it shipped. This PRD did not introduce that; it found it. It is in
   scope here because it is the same link, one path segment shorter, and per the repository's
   one-PR rule it is fixed in this unit of work.

Note also that the SPA serves `index.html` with **HTTP 200 for every unknown path** (measured: site
root, the repo page and a fabricated commit URL all returned an identical 5419-byte body). HTTP
status can therefore never be used to verify one of these links — only rendered DOM can. That is why
SP1 and SP4 are browser spikes, not `curl` spikes.

### F9 — ⚠ A fork/PR commit is stored under an id the lookup never tries

`Commit.DocumentId(provider, repositoryId, sha, pullRequestNumber = null)`
(`CodeCoverage.Library/Entities/Commit.cs:172-177`) yields two shapes:

```
Commits/{provider}/{repoId}/{sha}            // pullRequestNumber omitted
Commits/{provider}/{repoId}/pr/{pr}/{sha}    // pullRequestNumber supplied
```

Every `BrowseController` lookup uses the **three-argument** call — the non-PR shape only
(`BrowseController.cs:291`, `:297`, `:362`, `:442`, `:501`).

But `UploadIngestor.cs:88` calls it **with** a `prSegment`:
`Commit.DocumentId(EForgeProvider.GitHub, repository.GitHubId, request.CommitSha, prSegment)`.

So if `prSegment` is non-null for fork uploads (the anonymous `ForkUploadsController` path), those
commits are written under `…/pr/{n}/{sha}` and `GetCommit` — which only ever tries `…/{sha}` — cannot
find them. The vanity URL carries no PR number and structurally cannot address that document.

The entity doc-comment (`Commit.cs:165-169`) states "**Nothing writes it today**", which contradicts
the `UploadIngestor` call site. One of the two is stale. Resolving which is SP2, and it is blocking:
if fork PR commits use the PR shape, the new link breaks precisely on fork contributions — which are
also the builds that get the "(from a fork)" neutral verdict, i.e. the ones a reviewer most wants to
open.

### F10 — Nothing tests the check-run payload

`PostCheckRunAsync` (`GitHubForgeFeedbackPublisher.cs:57-92`) has no test at all. `status`,
`conclusion` and `output` mapping are unverified today, so a new `details_url` would be unverified
too unless a test is added at that level.

What does exist, and what a change here touches:

- `GateEvaluatorTests.cs` — summary/title text; `Informational_mode_posts_the_same_numbers_but_never_fails` (:52), `No_patch_target_means_informational_numbers` (:158), `A_walked_base_is_disclosed_in_the_summary` (:113).
- `ForkFeedbackPublishTests.cs` — the only end-to-end assertion that check-run *names* and outcomes reach the publisher (:137-148, :174-183). Natural home for a `DetailsUrl` assertion.
- `PullRequestCommentRendererTests.cs` — link rules, incl. `Both_published_links_carry_the_forge_segment` (:112), `A_non_github_repository_does_not_deep_link_to_github_com` (:131), and the no-base-url rule (:219).
- Test double `CodeCoverage.Tests/Services/ScriptedDiffService.cs:69` holds
  `List<(string Sha, string Name, ForgeVerdict Verdict)> Statuses` — **widening `ForgeVerdict`
  changes this tuple**, plus `ForgeIntegrationConformanceTests.cs:43, :183, :207`.

### F11 — "This check is informational" lives in the gate evaluator, not the publisher

`GateEvaluator.cs:105-106`, appended to the **project** summary only:

```csharp
if (!gate.Blocking)
    lines.Add("This check is informational (Blocking is off in the repository's coverage gate).");
```

The patch check has a sibling at `:66`. `GateEvaluator.Project`/`Patch` are **pure** and receive
`GateSettings`, `Build`, `BuildComparer.Result`, `CommitAssembly?` — **no repository, no commit, no
sha, no base URL**. Appending a link inside `Describe` would mean widening a pure function's
signature and updating every `GateEvaluatorTests` expectation. The cheaper seam is
`PublishFeedbackRecipient.ToVerdict` (`:175-187`), which already post-processes title and summary for
forks and already sits beside `repository`, `commit` and `configuration`.

### F12 — Config is read two ways; `Coverage:BaseUrl` uses the raw one

Strongly-typed binding exists but only for mail: `Program.cs:83`
`Configure<CoverageMailOptions>(GetSection("Coverage:Mail"))`, injected as `IOptions<T>` via Spark's
`[Inject]` (`Services/SmtpLinkConfirmationSender.cs:34`). `Coverage:BaseUrl` itself is read as a raw
`IConfiguration["…"]` string in all six call sites; there is no `CoverageOptions` class.

There is a settings *entity* (`GateSettings.cs:13`) but it is per-repository gate policy embedded on
`Repository`, and overridable by an in-repo `coverage.yml`. A per-deployment public URL does not
belong there: it is deployment topology, in the same class as the Traefik host rule and the OIDC
audience, and it must stay identical to that audience (`Program.cs:318`) or the two drift — the exact
failure `Program.cs:164-168` exists to prevent.

---

## Options

### Where the URL is composed

| | Approach | Verdict |
| --- | --- | --- |
| A | String-concatenate in `GitHubForgeFeedbackPublisher` | **Rejected.** Puts a URL shape the client owns inside a forge-specific adapter, and leaves GitLab/Bitbucket to re-derive it. Bitbucket *requires* the field (F3). |
| B | Add `DetailsUrl` to `ForgeVerdict`, composed by a shared helper in `CodeCoverage.Library` | **Chosen.** The DTO is already the provider-neutral seam; all three forges have the field; the helper is shared with the PR comment so the two cannot drift. Costs a test-double tuple update (F10). |
| C | Give `IForgeIntegration` a full URL-builder service now (the deferred "M15") | Rejected *for this PR*. M15 is about **forge-side** commit URLs (github.com/…), a different problem needing per-integration knowledge. Our report URL is a pure function of baseUrl + canonical provider + owner/name/sha. Conflating them widens the diff without removing duplication. |

### Where the link is surfaced on the check run

| | Approach | Verdict |
| --- | --- | --- |
| A | `details_url` only | Insufficient alone — it is a chrome button ("Details"), easy to miss, and it is what is already there-but-wrong. |
| B | A markdown line in `output.summary` only | Leaves the Details button still pointing at the App homepage (F1). |
| C | **Both** — `details_url` *and* a visible `[full report]` line | **Chosen.** The user asked for "a full link ... for that exact specific commit"; the visible link is the ask, and fixing `details_url` is required anyway because its current fallback is wrong. |

### What to do when `Coverage:BaseUrl` is unset

Discussed under D4 — this is the one place the check run cannot copy the comment's contract (F7).

---

## Design

### D1 — `ForgeVerdict` gains `DetailsUrl`

```csharp
public sealed record ForgeVerdict(
    EForgeOutcome Outcome,
    string Title,
    string Summary,
    string? DetailsUrl = null);
```

Optional with a default, so the stub integrations and the conformance fakes compile unchanged; the
test-double tuple in `ScriptedDiffService.cs:69` still needs revisiting (F10). Doc-comment records
the per-forge mapping from F3 and that Bitbucket requires it.

### D2 — One shared URL builder, in `CodeCoverage.Library`

A small static helper — repository-level and commit-level — beside the entities, so the renderer and
the verdict call the same code:

```csharp
public static string? Repository(string? baseUrl, Repository repo);
    // {baseUrl}/{provider}/r/{owner}/{name}
public static string? Commit(string? baseUrl, Repository repo, string sha);
    // {baseUrl}/{provider}/r/{owner}/{name}/c/{sha}
```

Both return `null` for a null/empty `baseUrl`, both `TrimEnd('/')`, both spell the provider with
`EForgeProvider.ToCanonicalString()` (F5 — lowercase, fixed, already in document ids and badge URLs).

`PullRequestCommentRenderer.BadgeMarkdown` (`:125`, `:132`) and `AppendFooter` (`:161`) are refactored
onto it. This is the anti-drift measure: the commit URL shape currently lives only in
`app.routes.ts:60`, and a second hand-rolled copy in C# would be free to rot.

### D3 — The publisher sets `details_url` on **both** paths

`PostCheckRunAsync` sets `DetailsUrl = verdict.DetailsUrl` on `NewCheckRun` (create, `:85-90`) **and**
`CheckRunUpdate` (update, `:76-82`). Omitting it on the update path would leave re-published runs
carrying whatever the create set — or, worse, clear it. Octokit's update semantics for a null here
are SP3.

### D4 — When `Coverage:BaseUrl` is unset, the check run says so rather than silently pointing home

Per F1, omitting `details_url` is not "no link" — GitHub substitutes the App homepage. So:

- `DetailsUrl` is left null (nothing better is available; the App homepage is at least *a* coverage
  page), **and**
- the visible `[full report]` line is omitted, exactly as the PR comment omits its links (F7), **and**
- the publisher logs a warning once per publish, because a production deployment with this unset is a
  misconfiguration and today it fails silently.

This keeps the comment's "never emit a relative URL" intent while being honest that the check run's
degraded state is not link-free.

### D5 — The visible link goes in `output.summary`, composed at `ToVerdict`

`PublishFeedbackRecipient.ToVerdict` (`:175-187`) appends a final line to the summary:

```
[View the full report for 91b3fae](https://coverage.mintplayer.com/github/r/OWNER/REPO/c/91b3fae…)
```

Chosen over `GateEvaluator.Describe` per F11: `Describe` is pure and forge-unaware, and widening it
would churn every `GateEvaluatorTests` expectation for no gain. `ToVerdict` already post-processes
the summary for forks, already has `repository`, `commit` and `configuration` in scope.

`output.text` (F2, currently empty) is deliberately **not** used — one link does not justify a second
markdown region, and the summary is what renders in the PR's checks strip.

### D6 — The PR comment's `[full report]` link moves to commit level

`AppendFooter` (`:159-161`) switches from `UrlBuilder.Repository(...)` to `UrlBuilder.Commit(...)`.
Same line, same label, one segment deeper — the sha is already in scope there (it is already being
linked to github.com on the preceding line). The badge's link target stays repository-level: a badge
is a repository-lifetime artifact and pinning it to one commit would be wrong.

### D7 — F8 is fixed before, or with, the link — not after

Deep-linking to a page that 401s for logged-out readers makes the feature worse, not better: it
replaces a useless-but-harmless link to the site root with a confident link to a bounce. The
authorization gap is therefore part of this unit of work, not a follow-up.

The fix depends on SP1's root cause. The likely shape, given `WWW-Authenticate: Bearer`, is that the
JWT scheme added for OIDC uploads became the default challenge scheme, so anonymous requests are
rejected before `SparkAuthorize` is reached. If so the correction is scheme selection — the upload
endpoints name the Bearer scheme explicitly and the read surface falls back to the cookie scheme /
allows anonymous — not a change to `security.json`, whose grants are already correct.

Row-level scoping is unchanged and stays in the method bodies: `RepositoryVisibility.cs:35-36` —
`!repository.IsPrivate || OwnerKey ∈ allowedOwners`. Public repo → anonymous reader sees it; private
repo → refused. That is the intended behaviour and D7 does not touch it.

### D8 — A private repository's link is honest about why it failed

For a private repo, an unauthorized clicker currently lands on the guard's catch branch and is
bounced to `HOME_URL` (`vanity-redirects.ts:56`), which reads as "this page does not exist". With a
check-run link now pointing there from a PR, that becomes a routine experience rather than an edge
case. The guard should distinguish "not signed in" (→ sign-in, with return URL) from "signed in and
not permitted" (→ a refusal that does not confirm existence — the 401-not-404 property measured in
F8 must be preserved; the page must not become an existence oracle).

---

## Acceptance criteria

1. `coverage/project` and `coverage/patch` on a **public, first-party** PR carry
   `details_url = {Coverage:BaseUrl}/{provider}/r/{owner}/{repo}/c/{sha}` — verified by reading the
   check run back from the API, not from logs.
2. The same URL appears as a visible `[full report …]` markdown link at the end of `output.summary`.
3. A **logged-out** browser opening that URL sees the commit's coverage report — verified by rendered
   DOM, never by HTTP status (F8).
4. The same holds for a **fork** PR's check run (F9).
5. The PR comment's `[full report]` link is commit-level; its badge link stays repository-level.
6. With `Coverage:BaseUrl` unset: no relative URL is emitted anywhere, the visible link is omitted,
   and a warning is logged (D4).
7. A private repository's report URL refuses an unauthorized viewer without revealing whether the
   repository exists.
8. `PostCheckRunAsync` has a test asserting the payload — including `details_url` on **both** the
   create and update paths (F10).
9. `coverage.mintplayer.com` gains no new occurrence in C#.
10. All five test projects pass (`.slnx` solution).

---

## Breaking changes

None to any published package. `ForgeVerdict` is app-internal (`CodeCoverage.Library`) and gains an
optional trailing parameter, so the stub integrations and conformance fakes compile unchanged. The
test-double tuple in `ScriptedDiffService.cs:69` and `ForgeIntegrationConformanceTests.cs` are
in-repo updates.

The `details_url` on existing check runs changes from the App homepage to a commit URL. Already-posted
check runs are not rewritten; the next publish for a commit updates its own run.

If D7's fix is scheme selection, it alters who may call `/api/browse/*` and `/spark/po/*`
anonymously — which is a *restoration* of the committed posture (F8), not a widening of it. The
posture report must be re-generated and reviewed in the PR regardless.

---

## Out of scope (genuinely not being done)

- **The M15 forge-side commit-URL builder** on `IForgeIntegration` (`PullRequestCommentRenderer.cs:151-156`).
  Different problem, different data source; the `github.com/…/commit/{sha}` link stays GitHub-only.
- **GitLab and Bitbucket publishers.** `ForgeVerdict.DetailsUrl` is designed so they can use it (F3),
  but the integrations remain stubs.
- **`output.text`, annotations, `external_id`** (F2). Per-line annotations on uncovered lines are a
  real feature and a separate one.
- **A patch/diff-only page.** Patch coverage exists as data (`Build.Patch`) and is surfaced in the
  comment and the check title; there is no per-PR diff view and this PRD does not add one.
- **Binding a `CoverageOptions` class** for the whole `Coverage` section (F12). Tempting alongside
  this work, unrelated to it.

---

## Spikes

Five. **SP1 and SP2 are blocking** — each can invalidate the design, and both concern whether the
link we are about to publish actually resolves for the people who will click it.

### SP1 (BLOCKING) — Why does production 401 an anonymous read, when the committed posture says it should not?

F8 measured `401` + `WWW-Authenticate: Bearer` on `/api/browse/*` and `/spark/po/load`, against a
`securityPosture.txt` that lists both as anonymous, on code that is not stale.

Reproduce locally (`dotnet run --launch-profile https`, per
`docs/code-coverage/`): does a cookie-less request to `/api/browse/repos/github/{owner}/{name}`
return 200 or 401? If 200, the divergence is environmental (production config, an env var, a
reverse-proxy rule) and must be found there before anything is published. If 401, confirm the
hypothesis that the Bearer scheme added at `Program.cs:318` became the default challenge scheme and
pre-empts `SparkAuthorize`.

**Answers:** whether D7 is a scheme-selection fix, a config fix, or something else — and whether
acceptance criterion 3 is reachable at all in this PR.

### SP2 (BLOCKING) — Is a fork/PR commit stored under the PR-scoped document id?

F9 found `UploadIngestor.cs:88` passing a `prSegment` to `Commit.DocumentId`, while the entity's own
doc-comment (`Commit.cs:165-169`) insists "Nothing writes it today", and every `BrowseController`
lookup uses the non-PR overload.

Determine what `prSegment` actually is at `UploadIngestor.cs:88` for (a) a first-party PR build and
(b) a fork upload via `ForkUploadsController`. Then confirm against real data — production holds fork
builds (the check run that prompted this PRD is neutral-from-a-fork-capable) — whether any
`Commits/github/{repoId}/pr/{n}/{sha}` documents exist.

**Answers:** whether the commit link resolves for fork PRs (acceptance criterion 4), and whether
`GetCommit` needs a second lookup or the vanity route needs a PR segment. If PR-scoped documents
exist, the route in `app.routes.ts:60` structurally cannot address them and the design needs a
fourth option.

### SP3 — Octokit update semantics for `DetailsUrl`

Does `CheckRunUpdate` with `DetailsUrl = null` **clear** an existing `details_url`, leave it, or
restore GitHub's App-homepage fallback? And does an explicitly-set `details_url` reliably override
that fallback (F1)?

**Answers:** whether D3 must always set the field on the update path, and whether D4's "leave it
null" degradation is safe on a re-publish of a run that previously had a URL.

### SP4 — What a logged-out visitor actually sees at the commit URL

The SPA returns **200 with an identical body for every path**, including fabricated ones (F8), so
`curl` cannot answer this. Drive a real browser (Playwright MCP — note the app's components live in
shadow DOM; use a recursive `deepFind` over `shadowRoot`s and return only primitives from
`browser_evaluate`) at `{baseUrl}/github/r/{owner}/{repo}/c/{sha}`, logged out, and record the
rendered outcome: report, sign-in, or bounce to home.

**Answers:** acceptance criterion 3, and the true current state of the already-published
repository-level `[full report]` link.

### SP5 — Private-repository click-through

With a private repository and a viewer lacking access, what does the commit URL render, and does it
distinguish "not signed in" from "not permitted" without confirming existence?

**Answers:** D8's shape, and whether criterion 7 needs client work or is already satisfied by the
guard's catch branch.

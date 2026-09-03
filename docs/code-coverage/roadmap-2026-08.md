# Coverage — next-phase roadmap (2026-08-14)

Status: **proposed** (this document is the PRD + plan for the next phase; no implementation yet).

Companion documents: [PRD.md](PRD.md) (product & architecture), [PLAN.md](PLAN.md) (milestones M0–M10
and the M9 backlog), [reauth-on-401.md](reauth-on-401.md) (the in-flight GitHub token-expiry fix).
This document does **not** replace them. It answers one question — *what should we build next, and
why that rather than the rest of the M9 backlog* — and it re-prioritizes several already-tracked
items rather than inventing new ones.

> Research basis: a four-agent investigation (2026-08-14) covering competitor feature sets, a
> full read of this codebase (~9.7k lines), an ops/security/multi-tenancy audit of the live
> deployment, and a PR-integration design strand. Claims below carry file:line evidence; the ones
> marked **[verified]** were re-checked first-hand against the source rather than taken from an
> agent report. Where two strands disagreed (§3.5), the disagreement was adjudicated against the
> source and the reasoning is recorded rather than averaged away.

---

## 1. The finding

The product's entire value is **one number that a developer trusts**. The investigation found
that the browse/ingest machinery around that number is in good shape — and that the number itself
can be confidently wrong, silently, through at least three independent code paths, with the UI
presenting the wrong answer identically to a right one.

That reframes the roadmap. The most valuable next work is not a new feature surface (components,
test analytics, more parsers). It is **making the number honest, and making its failures visible**.
Everything else in this document is sequenced behind that.

A second, unrelated finding: the deployment has **no backups**. That is the highest-consequence
item discovered and it is not a feature at all.

### The pattern

The live incident of 2026-08-13/14 — *the App is installed on the MintPlayer org, but /home shows
only the signed-in user's own account* — is not a one-off bug. It is one instance of a pattern that
recurs across the codebase: **a failure path that returns a plausible-looking wrong answer instead
of an explicit unknown.**

In that instance the explicit unknown *already exists one frame down* and is discarded at the
boundary: `QueryGitHubInstallationsAsync` correctly returns `null` for "don't know"
(`Coverage/Services/GitHubAccessService.cs:91-116`, and its doc-comment at `:89-90` says exactly
that), and `GetAllowedOwnersAsync` collapses it to `[username]` (`:52-58`) **[verified]**. Every
consumer — private-repo visibility, `canManage`, the token and badge-management gates — then cannot
distinguish degraded from authoritative.

A register of every other occurrence is in §8. Three of them corrupt the coverage number itself.

### The constraint that shaped everything outbound — FIXED upstream 2026-08-14

**Spark's message bus never redelivered a failed message.** Filed as
[MintPlayer.Spark#233](https://github.com/MintPlayer/MintPlayer.Spark/issues/233), fixed by
[#234](https://github.com/MintPlayer/MintPlayer.Spark/pull/234) → **`10.0.0-preview.43`**. All the
retry *bookkeeping* (per-handler isolation, `AttemptCount`, backoff, dead-lettering) had shipped;
no wake-up mechanism existed, so any recipient exception stranded its message forever and
`DelayBroadcastAsync` never fired at all.

The fix's diagnostic surfaced a deeper finding worth remembering here: **a `now()` time comparison
in a RavenDB subscription where-clause silently never matches**, so the PRD's designed pickup
condition was never implementable and `@refresh`-based redelivery (the pattern #233 originally
suggested) is equally inert. The shipped fix is a **sweeper-materialized boolean gate**: a
`MessageRetrySweeper` hosted service (finally wiring the previously dead `FallbackPollInterval`,
default 30s) patches `WakeUp = true` onto due messages, the subscription gates on the boolean, and
the worker clears it on pickup. Redelivery granularity = the sweep interval. Existing databases
pick up the new subscription query automatically at startup. Known follow-up left upstream:
`SyncActionSubscriptionWorker` (replication — unused by Coverage) has the same dead `now()` clause.

**Consequence for this roadmap:**

1. **Coverage must upgrade to `10.0.0-preview.43`** before building any outbound feature — and the
   upgrade retroactively arms *existing* recipients too: failed webhook messages will now retry,
   so recipients must tolerate redelivery (they were written to be idempotent; verify at upgrade).
   *(Done: master shipped preview.43; branch `adopt-spark-generic-ui` is on **preview.51** — see
   [adopt-spark-generic-ui.md](adopt-spark-generic-ui.md) M11. Note preview.51 added a model-hash
   startup gate, so any future preview bump now also needs a regenerated, committed
   `App_Data/modelHashes.json` — outside Development a mismatch refuses to start.)*
2. **The outbox design in T2.1 stays.** With bus retry now real it is belt-and-suspenders for
   delivery, but it also carries the check-run/comment ids that make republish idempotent and it
   solves the PR-opened-later backfill — both reasons stand independent of bus reliability.

---

## 2. Tier 0 — urgent, and not features

These come first. None of them adds a user-visible capability; all of them are either a live risk
or a precondition for trusting anything built later.

### T0.1 — Backups 🟦 · cost S · **highest consequence**

`docker-compose.yml:84-85` declares a bare `raven-data` named volume. `README.md:138-140` states
there is no automated backup. `PLAN.md` M9.14 leaves it ⏳. There is no RavenDB Periodic Backup
task configured anywhere in the repo.

The only copy of all production data — every account, build, coverage document and retained raw
report — is one Docker volume on one VPS. Note the deploy pipeline itself is *not* destroying it:
`publish.yml:119` runs `docker compose down --remove-orphans`, which does not remove named volumes,
and `docker image prune -f` touches images only **[verified]**. The exposure is the absence of
backups, not the deploy.

Configure RavenDB Periodic Backup to a second volume plus an offsite target, and **rehearse a
restore** — an unrehearsed backup is a hypothesis, not a backup.

### T0.2 — Finish the token-expiry fix, and unmask it 🟦 · cost M

[reauth-on-401.md](reauth-on-401.md) is the design and it is unimplemented: a grep for
`refresh_token|RefreshToken|expires_in|ExpiresAt|reauth` across all C# and TypeScript in
`Coverage/` and `Coverage.Library/` returns **zero hits**. Commit `543169d` added the document only.

The investigation added three things that document does not yet account for:

1. **The failure is silent at three layers, not one.** Server degrades to `[username]` with HTTP 200
   (`GitHubAccessService.cs:52-57`); the client renders any API failure as an empty list
   (`home.component.ts:44-46`); and **the resync button swallows its own failure**
   (`home.component.ts:53-58`) while `MeController.Resync` (`MeController.cs:74-79`) only clears an
   in-process cache **[verified]**. The one remedy the UI offers is architecturally incapable of
   reporting that it failed. This is why reinstalling the App on the org changed nothing and told
   the user nothing.
2. **`OnRemoteFailure` would mask the fix.** `Program.cs:64-69` redirects *every* OAuth remote
   failure to `/home` with no message. It was written for one known case (App-initiated
   authorization with no OAuth state), but as written a failed **Reconnect** is indistinguishable
   from a successful one. Fixing this belongs in the same change as the reconnect banner, or the
   banner's happy path cannot be told from its sad path.
3. **Verify the App setting first.** GitHub's 8-hour user-token expiry is a per-App opt-in
   ("Expire user authorization tokens"). If it is *disabled* on `coverageproduction`, tokens do not
   expire and the observed 401 has a different cause (a revoked authorization), which needs a
   different permanent remedy. This is already `reauth-on-401.md` M1.3; it should be step one, not
   step three.

Add to that document's M2: a **last-successful-sync timestamp** next to the resync control. Codecov
receives a high volume of "my org doesn't show up" reports *despite* shipping a resync button,
precisely because a failed sync looks identical to "you have no access"; none of the competitors
display a last-sync time. It is a one-field addition to `AccountsResponse` and it would have
answered this incident without reading a log line.

### T0.3 — Bound the front door 🟦 · cost S–M

Three unbounded surfaces, in severity order:

- **Nothing ever deletes coverage data.** The only `session.Delete` calls in the repo delete a
  `Repository` document (`GitHubEventsRecipient.cs:103,117`). Builds, `FileCoverage`,
  `BuildTreeSummary` and retained raw attachments accumulate forever. Meanwhile
  `ResolveOidcRepository` (`UploadsController.cs:209-247`) auto-provisions **any public repository
  on GitHub** from an OIDC claim — no App install, no allowlist, no quota **[verified]** — at 50 MB
  per request and 60 requests/minute. The limiter bounds rate, not cumulative bytes (`PLAN.md`
  M9.23 concedes this). Needs a per-account storage quota at upload, a raw-attachment TTL, and a
  stricter tier for auto-provisioned repos.
- **Unbounded decompression.** `ParseSessionRecipient.ReadAttachmentText:176-184` inflates a whole
  gzip attachment into a `byte[]` and then again into a UTF-8 string, uncapped; `docker-compose.yml`
  sets no memory limit on `coverage-app`, so a zip bomb takes the container on a single-instance
  host. Cap the inflated size and set `mem_limit`.
- **`BrowseController` has neither `[Authorize]` nor `[EnableRateLimiting]`** **[verified]**, and
  `GetFile` triggers a live GitHub fetch per uncached path into a process-wide `IMemoryCache`
  registered with no `SizeLimit` (`Program.cs:33`). An anonymous crawler over public repos exhausts
  memory and burns the installation's GitHub rate limit for every tenant.
  **This bullet is resequenced to ship early, as N5 of
  [upload-result-contract.md](upload-result-contract.md)** — an outside consumer confirmed it
  independently, and a CI gate polling the service is exactly the traffic shape a later limit would
  break, so the machine-readable path (`/api/uploads/status`) ships before or with the bound. Note two
  corrections found there: `/spark/*` is a **second** unmetered read surface over the same data and
  must be covered in the same change, and the fix is **not** a `SizeLimit` on the shared cache (that
  would make every unsized `Set` in the process throw) but a dedicated bounded cache for source
  content.

### T0.4 — A readiness endpoint that can fail 🟦 · cost S

`/health` is `Results.Ok()` (`Program.cs:207`) **[verified]** — it reports green with RavenDB down,
and the compose healthcheck (`docker-compose.yml:74`) probes exactly that. Add `/health/ready`
covering RavenDB reachability, message-bus backlog depth, cron last-run age, and **GitHub App JWT
auth working**. That last probe detects this week's incident class directly.

Two configuration landmines belong with it, both of which fail silently today: a missing or
unparseable `AppId` means **webhooks are silently not processed while still returning 200 to
GitHub** (`Program.cs:98-99`), and a missing `Coverage:BaseUrl` falls back to
`https://localhost:5200` as the OIDC audience, so every tokenless upload 401s in production with no
configuration error (`Program.cs:140`). Both should fail startup in Production instead.

---

## 3. Tier 1 — the feature answer: make the number honest

### T1.1 — Fix "everything matched" 🟦 · cost S · **do this first of all features**

`PathNormalizer.Normalize:46-48`: when the uploaded file list is empty, every relative path is
declared `Matched: !stillAbsolute` — matched **without verification** **[verified]**. The action's
`gitLsFiles` returns `null` on any failure (`action/src/main.ts:123-131`) and the field is then
simply omitted from the upload **[verified]**, so a shallow checkout, a non-git workspace or any
`git ls-files` failure in CI silently becomes *"everything matched"*: real percentages computed over
paths that may not exist, **and no unmatched-files warning at all**.

This is the worst instance of the §1 pattern in the codebase — total confidence, zero evidence — and
it directly attacks the product's core claim. The correct shape already exists twenty lines below:
`:64-67` refuses an ambiguous suffix match and returns unmatched. Copy it. With no file list, paths
are *unverified*, which is a third state and must not render as `Matched: true`.

The action should also surface a loud failure rather than `core.warning` when it cannot produce a
file list.

### T1.2 — Stop reporting partial parse failure as success 🟦 · cost S

`ParseSessionRecipient.cs:52-63`: an unrecognized format or a missing attachment logs a warning and
`continue`s; then `:112-113` marks the whole session **"Parsed"** with `Error = null` provided any
*other* file in the session parsed. The user sees a green badge and a silently lower number.

A session where some reports failed is not `Parsed`. Introduce the partial state and surface it.

### T1.3 — Make failed and in-flight builds reachable 🟦 · cost S–M (✅ BUILT 2026-09-02 — `withCoverageOnly` defaults to false; the commit page shows assembly state. Shipped with the carry-forward work, `../coverage_carryforward_PRD.md`.)

The repo commit list is the only navigation into a commit, and it filters on coverage:
`BrowseController.GetCommits` defaults `withCoverageOnly = true` **[verified]** and the SPA never
overrides it (`browse.service.ts:146-151`). `Commit.Coverage` is only assigned at finalize
(`BuildFinalizer.cs:30`) from `Build.Coverage`, which is only computed inside the try block at
`ParseSessionRecipient.cs:120`. **A session that throws leaves the commit permanently invisible.**

So the single case where a user most needs feedback — *my upload didn't work* — is the one case with
no URL that reaches it. The error text is already stored (`BuildSession.cs:17`) and already rendered
(`commit.component.html:62-64`); it is merely unreachable. Add a per-repo **Recent builds** list that
does not filter on coverage. Pair it with live refresh of in-flight builds (`PLAN.md` M9.22): right
after the first upload — peak attention — the page currently looks broken.

### T1.4 — Reprocess the retained raw uploads 🟦 · cost S–M

`PRD.md` §5 retains raw reports *specifically* so the merged view can be recomputed; no trigger
exists (`PLAN.md` M9.30). `BuildSession.RawFileNames` is read only by the parser
(`ParseSessionRecipient.cs:49`). One endpoint that re-broadcasts `ParseSessionMessage` retroactively
unlocks parser fixes, failed-parse retry and newly added formats — and it is the recovery lever for
every defect in T1.1–T1.2 on data already ingested. **Highest value per line of code in this
document**, and a prerequisite for T1.5.

### T1.5 — Per-repo configuration: ignore, path fixes, thresholds 🟦 · cost M · ⚠️ decision required

This reverses a documented decision: `PRD.md` §1 lists *"Codecov-style YAML config files in the repo,
path fixes, ignore rules"* under **Non-goals (v1)**. Adopting it is a deliberate change of position,
not the filling of an oversight. It needs your explicit call — see §7.

**Two strands disagreed about how load-bearing this is, and the disagreement was productive.** The
competitive strand called it a hard prerequisite for all PR integration. The design strand pushed
back, and its objections hold up:

- *"Thresholds need a config file"* is **false**. A threshold is a repo setting; `RepoSettingsController`
  and a settings surface already exist to hang `ProjectTarget`/`PatchTarget`/`Blocking` on. This
  argument does not support a file at all.
- *"`ignore` is needed or patch coverage reports noise"* is **true but narrower than claimed** — it is
  a prerequisite for a **blocking** patch check, not for patch coverage as a whole. Display-only patch
  coverage is informative and harmless even with migration noise. Gating it behind a config feature
  would delay the risky work behind the easy work.

The strongest argument for a file is one neither strand led with: **`ignore` must version with the
code.** A PR that introduces a generated directory wants *that PR's* ignore list; a repo that
restructures its layout must not have its historical numbers silently rewritten by a UI edit today.
Targets do not have that property — a target is the repo's current standard, not a property of any
particular tree.

**Resolution: the per-repo settings document is the contract; the file is an optional overriding
writer of it, field by field.** Scope to a subset — `ignore`, `coverage.status.project|patch`
(`target`/`threshold`/`informational`), `comment` — ignoring unknown keys, and do **not** advertise
Codecov schema compatibility (most of the real schema maps onto features we lack). YamlDotNet is the
only new dependency. `Blocking` defaults to **false** so installing the App can never break someone's
merges — Codecov's opposite default is its single most-complained-about behaviour.

**Where the config is read from — split by what the setting describes:**

- **Path config (`ignore`, `fixes`) comes from the head — the uploaded ref.** It describes the tree
  that produced the report, so it must match that tree or it is simply wrong.
- **Policy config (`target`, `threshold`, `blocking`) comes from the base / default branch.** Reading
  policy from head would let a PR lower its own gate — trivially, and on a public repo via a fork PR,
  by someone with no write access at all. This is a security property, not a preference.

**How to read it: prefer the upload over a fetch.** The action already ships `fileList` and `rootDir`;
adding `coverage.yml` to the multipart body is a few lines, costs no API call, guarantees the config
matches the exact tree measured, and works for **OIDC-only repos with no App installed**, which cannot
be fetched from at all. A server-side fetch is the fallback for non-action uploaders and is the only
path for the base-branch policy half — `GitHubContentService.GetFileContentAsync` already does
content-at-a-sha with installation token plus raw fallback, so that half is essentially built.

**Snapshot onto the Build, unconditionally**: `ConfigSnapshot { Source, Sha, ContentHash,
EffectiveSettings }`. The outbox cron may publish long after the ref moved; a number cited in a check
must be explainable months later; recomputation must be deterministic; and historical coverage must
not change silently when someone edits the config today.

**Where `ignore` is applied — adjudicated at finalize, not parse** **[verified]**. The two strands
proposed parse-time (an `Ignored` flag on `FileCoverage`) and finalize-time respectively. Finalize
wins: parse-time bakes the list into per-file documents and forces a full reprocess to change it,
while `MaterializeTreeSummary` (`BuildFinalizer.cs:52-72`) already streams every `FileCoverage`, so
the filter is free there.

But the finalize-time proposal is **incomplete as stated**, and reading the finalizer shows why:
`BuildFinalizer.cs:30` promotes `build.Coverage` onto the commit, and `build.Coverage` is computed at
**parse** time (`ParseSessionRecipient.cs:162`) — finalize never recomputes it. Applying `ignore` only
to the tree summary would leave the badge, `Commit.Coverage` and `Repository.LatestCoverage` computed
*without* the filter while the tree has it: the headline number and the file tree would disagree.

So the correct shape is: **recompute `build.Coverage` inside `MaterializeTreeSummary`'s existing
stream**, from the same filtered set that builds the summary. That makes finalize the single point of
truth for every published number, costs one extra pass over data already being streamed, and removes
the current parse-time/finalize-time split as a side benefit. Keep ignored files as their own visible
bucket alongside unmatched ones — silent disappearance is how coverage tools lose trust.

**Path fixes belong in the normalizer, not the filter.** `PathNormalizer`'s constructor already takes
exactly the right shape (`:17-23`); a `fixes:` list is a fourth argument applied inside `Normalize`
between steps 2 and 3. This also gives users a way to *compensate* for T1.1-class path problems.

**Size the parts separately**: `ignore` + `fixes` + thresholds = **M**. Components = **L**, excluded
— see §5.

### T1.6 — Surface degraded state and diagnose uploads 🟦 · cost S–M

The server already knows visibility is degraded (`GitHubAccessService.cs:52-57`) and throws that
knowledge away. Return it as a flag on `AccountsResponse` (`MeController.cs:81`), render a banner,
and fix `home.component.ts:44` so a failed API is an error state rather than an empty list. This is
the same change as [reauth-on-401.md](reauth-on-401.md) M2 and should ship with it.

Beyond that, the minimum viable production diagnostic surface:

- **Structured logs carrying tenant identity.** `GitHubAccessService.cs:104` logs the status code but
  **not the user id or login**, so "which users are degraded right now" is unanswerable today. The
  upload log (`UploadsController.cs:125-126`) is already good; promote `sessionId` to a correlation
  id carried into the parse and finalize logs so one grep spans accept → parse → finalize.
- **An "recent uploads for this account" view.** `BuildSession.ParseStatus` and `.Error` are already
  written and are reachable only by navigating to the right commit page. This answers *why didn't my
  upload show up* without SSH. Note that a `covt_` upload for an unknown-or-unauthorized repo returns
  an indistinguishable 404 by design (anti-enumeration) — so a legitimate user with a typo'd repo
  name currently has no diagnostic path at all. This view is where that distinction should live.

---

## 4. Tier 2 — real value, sequence after Tier 1

*(One exception: T2.1's first milestone, display-only patch coverage, is sequenced earlier than the
rest of this tier — see §10. It needs no permission change and de-risks everything built on it.)*

### T2.1 — PR feedback: patch coverage, then checks 🟦 · cost L total, but splittable

`PRD.md` §1 promises it; `PLAN.md` M9.11/M9.12 defer it. Nothing exists — no checks or statuses code
anywhere. This remains the largest gap between the product and its competitors: coverage never
reaches where the decision is made.

**The key sequencing discovery: the two halves have very different unblock costs.** Patch coverage
(M9.12) needs **no new App permission** — `GET /repos/{o}/{r}/compare/{base}...{head}` is
Contents:read and `GET /pulls/{n}/files` is Pull requests:read, both already granted (`README.md:31-53`).
Only the checks/comments half (M9.11) is gated on a permission decision. **Ship patch coverage first**:
it is independently useful, and it retires the hardest design risk before anything depends on it.

#### Patch coverage

Compute, of the lines **added** in `base...head` that are coverable, what percentage is covered.
Added-only is the right definition — a modified line appears as an addition in new-file space, so
"changed" is subsumed, and deleted lines contribute nothing.

Use the compare endpoint's default JSON media type and read `files[].patch`, rather than the `.diff`
/`.patch` media types: JSON hands you `filename`, `previous_filename`, `status` and `additions`
alongside the hunks, and hunks must be parsed either way. Three-dot compare also returns
`merge_base_commit.sha` — the same base GitHub's own *Files changed* tab uses. Walk `@@ -a,b +c,d @@`,
track the new-file counter, collect `+` lines only.

The intersection is a fortunate structural fit: `FileCoverage.DocumentId(buildId, path)`
(`FileCoverage.cs:37`) is content-addressed, so each changed file is a **point-load** — no query, no
index — and diff paths are already the repo-relative shape `PathNormalizer` produces. A diff line
present in `Lines` is coverable and its `Status` classifies it; a diff line absent is non-coverable
(blank, brace, comment) and leaves the denominator.

Store `PatchCoverage { BaseSha, HeadSha, Source, Truncated, LinesCovered, LinesCoverable,
ComputedAtUtc }` on the Build, promoted onto the Commit the way `Coverage` already is, with per-file
added-line detail in its own `{buildId}/patch` document mirroring `BuildTreeSummary`.

Compute **at finalize, in its own message** — never on demand at render, where a network call sits in
the request path and the base can move underneath you; a number cited in a check must be stable. Do
not inline it into `BuildFinalizer.Finalize`, which the cron path runs for up to 128 builds on one
shared session (`FinalizeBuildsCronJob.cs:32-66`). Cost is 1–3 API calls per build against a
5,000/hour budget — negligible.

**Traps, in severity order:**

- **`Commit.ParentSha` cannot serve as the diff base — it means two different things.** **[verified]**
  The push webhook writes `evt.Before`, the previous ref tip (`GitHubEventsRecipient.cs:146`); the
  upload writes `pull_request.base.sha` (`UploadsController.cs:68`). Worse than a race: the upload
  uses `??=` (write only if unset) while the webhook uses an unconditional `=`, so a later push
  **clobbers** a PR base with a ref tip. Patch coverage needs its own explicit `BaseSha`. This is a
  live defect in the field as it exists today, not merely a gap in M9.12 — see §8.
- The action's `parentSha` is the base tip **at event time** (`action/src/context.ts:36`). If the base
  branch advances, our number silently diverges from what GitHub shows. Resolve the base at compute
  time and use compare's `merge_base_commit`.
- **300-file cap**: compare returns changed files only on page 1, capped at 300 for the whole
  comparison; `pulls/{n}/files` pages to 3,000 at 100/page. Record `Truncated` and say so in the UI
  rather than publishing a wrong denominator.
- **0/0 reads as 100%**: a brand-new file no tool instrumented yields an empty denominator. Count
  added lines in files present in the uploaded `fileList` but with no `FileCoverage` document as
  *unreported*, and surface that separately.
- Renames: `previous_filename` is display-only — hunk line numbers are already new-file, so the lookup
  uses `filename`.
- A force-push produces a new head sha → new Commit/Build → recompute, correct by construction. A
  rebased **base** silently stales the number: store and display the `BaseSha` actually used.

#### Checks vs statuses vs sticky comment

| Surface | Permission | Updatable | Payload |
|---|---|---|---|
| Commit status | Commit statuses: write | append-only, but latest-per-context renders and gates | 140-char description + `target_url` |
| Check run | Checks: write | yes, via `PATCH` if you hold the id | `output.title/summary/text` markdown **+ up to 50 annotations per request** |
| Sticky comment | Issues: write (PR: write covers PR comments) | yes, marker + `PATCH` | full markdown |

**Recommendation: check runs as the sole gate, sticky comment opt-in, skip commit statuses entirely.**
Annotations — inline per-line marks in the *Files changed* tab — are the one thing statuses
structurally cannot do, and checks are requireable in branch protection exactly as statuses are.
Statuses would buy only a smaller-sounding permission ask, and the ask is unavoidable either way
(statuses need Commit statuses:write). So request **Checks: write** and upgrade **Pull requests:
read → write**; leave Commit statuses ungranted. Publish two runs — `coverage/project` and
`coverage/patch` — so they can be required independently. **These two names are a commitment, not a
preference** ([upload-result-contract.md §4.4](upload-result-contract.md)): the first consumer names
its interim workflow step `coverage/project` precisely so that branch protection carries over
unchanged when M11.3 lands, with no coordination at cutover. Renaming them later breaks every
consumer that required the interim step. Note check runs are App-only: OAuth and
user tokens cannot create them.

The permission upgrade has a product consequence: **every existing installation must accept the new
permissions before anything appears.** `new_permissions_accepted` is already handled
(`GitHubEventsRecipient.cs:68`), so the code path exists, but design for silent absence — treat
`Account.InstallationId == null` as *no feedback possible*, not an error.

**Edge cases.** OIDC auto-provisioned public repos (`UploadsController.cs:209-247`) have **no
installation token**, so neither a check nor a comment is possible — record a `FeedbackUnavailable`
reason and surface "install the App to get PR feedback" on the commit page. This is a genuine gap in
the OIDC-only story, not a bug. Fork PRs are moot today (they get neither `id-token: write` nor a
secret, so they cannot upload at all); when M9.13 lands, the base repo's installation can post a check
for the fork's head sha, but the Checks API does not detect pushes in forks, so the `pull_requests`
array comes back empty — don't depend on it.

#### Triggering and delivery

Broadcast `PublishFeedbackMessage { BuildId }` at the two `BuildFinalizer.Finalize` call sites —
`FinalizeBuildRecipient.cs:22` and `FinalizeBuildsCronJob.cs:62` — after `SaveChanges`, collecting ids
in the cron case rather than broadcasting inside the loop. Not inside the finalizer itself: it takes
no bus and is batched.

The case that needs explicit handling is **a PR opened after the upload** ("push, then open PR"),
which is common. `OnPullRequest` (`GitHubEventsRecipient.cs:152`) already stamps `PullRequestNumber`;
extend it to enqueue a publish when the commit has a `LatestBuildId`. A `synchronize` means the base
may have moved → recompute patch. A rerun produces a new Build id, so **key the stored check-run id on
`(sha, checkName)`, not on the build**, or reruns stack duplicate check runs instead of updating one.

Per the §1 house rule, the bus cannot deliver this. Use an outbox on the Build — `Feedback { State,
Attempts, NextAttemptAtUtc, LastError, CheckRunId, CommentId }` — with a cron sweep as the retry
engine, structurally identical to `FinalizeBuildsCronJob`. The recipient must catch everything and
never throw. This also makes the "PR opened later" backfill nearly free: the sweep picks the build up
once its commit gains a PR number. Idempotency comes entirely from the stored ids plus the comment
marker — GitHub offers no natural idempotency key for check runs, so holding the id is mandatory. On
403/429 set `NextAttemptAtUtc` from `x-ratelimit-reset`/`Retry-After` rather than the backoff curve;
on 401 invalidate Spark's cached installation token. Rate limits are not a concern at 1–4 calls per
build (5,000/hr per installation; the 500/hr content-generating limit applies to **comments only**).

While editing that recipient, two latent landmines are worth fixing in passing: it deserializes
`pull_request` **before** filtering on action, and Octokit.Webhooks' converter throws on unknown
action strings — so one new GitHub action name permanently kills the message, permanently being
literal given the no-retry constraint. Filter on the raw JSON first. And the
`installation_repositories` case there is dead code; Spark never broadcasts that event.

#### Positioning

Codecov's most-cited operational complaint is status checks arriving 25 minutes to 2+ hours late,
blocking merge queues. A single-node install can post a check within seconds of finalize. **Latency
is the differentiator here** — treat a slow check as a bug and instrument it from day one.

#### Milestones

- **M11.0** (S) — App permission upgrade: Checks: write, Pull requests: read→write; README table;
  `FeedbackUnavailable` when no installation. **First**, because installation owners need lead time
  to accept.
- **M11.1** (M) — patch coverage compute + storage + commit-page display, incl. the explicit `BaseSha`
  fix. No GitHub write, no new permission — independently shippable.
- **M11.2** (M) — config subset: schema, action upload, base-branch policy fetch, snapshot, `ignore`
  applied at finalize (T1.5).
- **M11.3** (M) — check-run publisher + outbox + retry cron + targets from the snapshot. First
  milestone that can gate a merge; ships non-blocking by default.
- **M11.4** (M) — `pull_request` backfill, rerun and late-upload republish semantics.
- **M11.5** (M) — sticky comment ✅ **delivered** (see
  [`../coverage_branch_pr_badges_PRD.md`](../coverage_branch_pr_badges_PRD.md) ·
  [`plan`](../coverage_branch_pr_badges_plan.md)): one comment per PR, keyed on a
  `PullRequestFeedback` document, posted pending on `opened` and edited with the real numbers at
  finalize, re-adopted by marker if deleted. Inline annotations remain **open** — they are a
  separate diff against the file-coverage model.
- **M11.6** (L) — fork PRs, after the quarantine policy exists.

### T2.2 — Offboarding, deletion and export 🟦 · cost M

There is no delete path, no export, and no retention policy. Uninstalling the App only nulls
`Account.InstallationId` (`GitHubEventsRecipient.cs:74-76`); every commit, build, raw report and
private-repo **file path** stays resident forever — invisible in the UI, present in backups. Worse,
re-adding a repository resurrects it attached to stale history, because the document id is derived
from the GitHub id. `ApiToken`s and `BadgeToken`s survive an uninstall too.

For an EU-operated public service with no privacy policy, no terms, and users' GitHub email plus
OAuth tokens stored in cleartext, this is also the GDPR gap. Pair the delete/export path with the
legal surface.

Cheapest honest answer to "data export": **a documented read API** over the JSON endpoints the
Angular app already calls, authenticated with existing `covt_` tokens, plus raw-report download —
not a bespoke CSV feature. (Coveralls does this by appending `.json` to any web URL.)

### T2.3 — Upload-token lifecycle and admin gating 🟦 · cost M

`ApiToken` has **no expiry and no last-used stamp**; the handler checks only existence and
`RevokedAtUtc` (`ApiTokenAuthenticationHandler.cs:51-55`), and authorization never re-checks the
creator (`UploadsController.cs:171-202`). A token minted by someone who has since left the org keeps
write access indefinitely; uninstalling the App does not invalidate it. `PLAN.md` M0.2 promised
expiry and it was not built.

Alongside it, `PLAN.md` M9.29 / `PRD.md` §6.3: token and badge management currently gate on
*installation visibility* (`TokensController.cs:37,81,108`, `RepoSettingsController.cs:33`), which is
owner-granular — so any org member who can reach the installation, including read-only on one repo,
can mint an account-scoped token covering the whole org and revoke everyone else's.

---

## 5. Deferred, with reasons

- **Components / flags breakdown.** Codecov now recommends components over flags for new projects,
  which is convenient — components are pure path-glob config with no upload-protocol change. But
  both are blocked by the same thing: contrary to `PRD.md` §5's "per-session storage" wording,
  per-session coverage is **not** retained — `FileCoverage` stores only the merged max
  (`FileCoverage.cs:31-33`). Flags are captured (`BuildSession.cs:10`), split server-side
  (`UploadsController.cs:99`) and rendered as decorative badges today. Cost **L**; revisit after
  T1.5 ships the config surface it would hang off.
- **Carryforward flags.** Real problem, but only for teams already running path-filtered CI with
  flags. Easy to get subtly wrong. Defer until someone reports it.
- **Notifications on coverage drop** (one generic outbound webhook + email, filtered by threshold and
  branch). Cost S–M once T1.5 gives it a threshold. Build the webhook only — **not** per-vendor Slack
  or Teams apps.
- **Dark stored fields**, each ~S and individually cheap: `Build.EventName` (push vs PR builds are
  indistinguishable in the UI), `Build.WorkflowName` (reaches the client, no column),
  `Repository.Archived` (never filtered or badged), `Repository.LatestCoverageAtUtc` (written at
  `BuildFinalizer.cs:45`, never read — a "stale coverage" badge is nearly free),
  `Build.FinalizedAtUtc` (no build duration shown). Good filler work.
- **Unmatched-path diagnostics** beyond T1.1: today the UI shows a count and **one** example, root
  level only, capped at 50 (`BrowseController.cs:251-253`), with no per-session attribution.
- **Account-page empty state** says "No repositories with coverage data yet" while listing all known
  repos, and unlike the home page offers no install-the-App link
  (`account.component.html:8` vs `home.component.html:60-64`). Trivial, high confusion value.
- **Scale ceilings** (`PLAN.md` M9.21 and neighbours): repos `Take(1024)`; account sparklines take the
  newest 1000 commits *across the whole account* then group, so a busy org silently loses repos;
  `MeController.cs:47` pulls 4096 repos and aggregates in memory on every home load;
  `FinalizeBuildsCronJob.cs:32` queries builds with no static index. Not urgent at current volume;
  all are §8-class silent truncation and should at least become visible.
- **Distributed visibility cache.** The 5-minute TTL is a sound revocation window, but it is
  per-process, so `InvalidateAsync` clears only the instance that served the request — resync breaks
  silently the day a second replica exists.
- **More parsers** (Istanbul JSON, Clover, OpenCover, Go), **file-view virtualization**,
  **`bs-datatable` tree mode**: unchanged from `PLAN.md` M9.

## 6. Explicitly rejected

- **Test analytics / flaky-test detection / JUnit ingestion.** Popular, and a genuinely different
  product: new ingestion format, storage shape, history model and UI. **L–XL**; it would consume the
  whole roadmap. Out of scope.
- **Multi-forge (GitLab, Bitbucket) and LDAP.** The entire identity and visibility model *is* the
  GitHub App, deliberately — there is no internal ACL. A second forge means building the ACL layer
  `PRD.md` §6.3 was written to avoid. **Rejects a core architectural bet; say no.**
- **Arbitrary build-vs-build comparison UI.** What users actually want is base vs head, which patch
  coverage delivers.
- **A CLI.** The Action covers the upload path; add a `curl` example to the docs instead.
- **Browser / VS Code extensions, AI reviewer.** Separate distribution channels with their own
  release chores. Not before the core is complete.
- **Historical retention/purging as a feature.** RavenDB will hold years of single-tenant history
  without complaint; a prune job is S whenever it is actually needed. (Distinct from T0.3, which is
  about abuse bounds, not age.)

---

## 7. Decisions required before implementation

1. ~~**Reverse the `PRD.md` §1 non-goal on repo config files?**~~ (T1.5) — **RESOLVED yes, 2026-08-18**,
   exactly as recommended: settings document as the contract, file as an optional per-field overriding
   writer, `Blocking` defaulting to `false`, and policy read from the **base ref for every repository**
   rather than only public/fork ones. The deciding data point came from the first real consumer on
   [issue #9](https://github.com/MintPlayer/CodeCoverage/issues/9): thresholds are a standing decision
   that must not be rewritten by checking out an old commit, while `ignore` genuinely versions with the
   tree (their workspace generates `*.styles.ts`, `*.generated.ts` and a `metadata/**` subtree, and
   *which* patterns are generated changes commit to commit). Rationale in
   [upload-result-contract.md §4.2](upload-result-contract.md). T1.5 / M11.2 is unblocked.
   *(Original wording: recommended yes, scoped to `ignore` + `fixes` + thresholds, components excluded.
   If no, thresholds still work via per-repo UI settings and display-only patch coverage still ships —
   but `ignore` has no home, so a blocking patch check stays off the table indefinitely.)*
2. **Grant the GitHub App `checks: write` + `pull requests: read → write`?** (T2.1) Gates the
   checks/comment half only. Note this is now a *smaller and later* decision than it looked:
   **patch coverage needs no permission change at all** and can ship first. When you do decide, do it
   early in the milestone — existing installations must click accept before anything appears, and
   until they do the feature is silently absent.
3. **Is `coverage.mintplayer.com` a personal instance or a service others may sign into?** This
   changes the priority of T0.3 (quotas), T2.2 (deletion/export/legal) and T2.3 (admin gating) from
   *should* to *must*. The OIDC auto-provisioning path means strangers can already create data.

---

## 8. Register: failure rendered as a plausible answer

The pattern from §1, ranked by how wrong the answer is. Fixing the class is worth more than fixing
any single instance; the register exists so the class stays visible.

| # | Site | Failure renders as |
|---|---|---|
| 1 | `PathNormalizer.cs:46-48` **[verified]** | No file list → *everything matched*, no warning (T1.1) |
| 2 | `ParseSessionRecipient.cs:52-63,112-113` | Partial parse failure → green **Parsed**, `Error = null` (T1.2) |
| 3 | `GitHubAccessService.cs:52-58` **[verified]** | GitHub 401 → *you belong to no organizations* (T0.2) |
| 4 | `home.component.ts:44-46,53-58` **[verified]** | Any API failure → empty list; resync failure → nothing at all (T0.2) |
| 5 | `Program.cs:98-99` | Bad/missing `AppId` → webhooks silently unprocessed, 200 returned to GitHub (T0.4) |
| 6 | `Program.cs:140` | Missing `Coverage:BaseUrl` → every OIDC upload 401s in production (T0.4) |
| 7 | `Program.cs:64-69` | Any OAuth remote failure → silent redirect to `/home` (T0.2) |
| 8 | `BrowseController.cs:43,138-143`, `MeController.cs:47` | Silent truncation → *this repo has no history* |
| 9 | `repo.component.ts:213-214,228` | Failed `/branches` → *one branch*; failed `/history` → *no trend* |
| 10 | `account.component.ts:63-71` | Any error loading tokens → token card hidden as if unauthorized |
| 11 | `GitHubAccessService.cs:208-212` | Backfill failure → stale/wrong "App installed" badge |
| 12 | `GitHubContentService.cs:30-74` | Fetch failure → *source unavailable* (mildest; message exists, cause omitted) |
| 13 | `action/src/main.ts:86-93` | `fail-ci-if-error` defaults false → upload failure is a warning on a green build |
| 14 | `publish.yml:80` | ghcr visibility PATCH `|| echo` → green run regardless |

**A related defect of a different shape — one field, two meanings** **[verified]**:
`Commit.ParentSha` is written by the push webhook as `evt.Before`, the previous ref tip
(`GitHubEventsRecipient.cs:146`), and by the upload as `pull_request.base.sha`
(`UploadsController.cs:68`). The upload uses `??=` and the webhook an unconditional `=`, so a later
push to the ref **overwrites a PR base with a ref tip**. Nothing reads the field today, so nothing is
currently wrong — but it is a trap armed for the first consumer, and patch coverage (T2.1) is that
consumer. Give patch coverage its own explicit `BaseSha` rather than fixing the overload in place.

A second armed trap in the same file: `OnPullRequest` deserializes the `pull_request` payload
**before** filtering on action, and Octokit.Webhooks throws on unknown action strings — so the day
GitHub adds an action name, that message dies, and given the no-retry constraint (§1) it dies
permanently and silently. Filter on raw JSON first.

**Counterexamples to preserve** (do not "fix" these — they are the correct shape):
`PathNormalizer.cs:64-67` refuses an ambiguous suffix match; `BadgeController.cs:68-77` renders an
"unknown" badge rather than 404 (existence oracle, `PRD.md` §6.4); `FinalizeBuildsCronJob.cs:56-60`
converts an implicit unknown into an explicitly labelled failure; `GitHubAccessService.cs:52-57` is
*safe* — its degradation direction is correct, it is merely mute; `publish.yml:102`'s `set -e`.

Also verified sound and not worth re-auditing: no browse endpoint skips the visibility check
(`BrowseController.ResolveVisibleRepository:382-390`, invoked at `:55,66,97,163,182,218,267,326`);
the badge's constant-time token compare and request-derived `Cache-Control`; and there are **zero**
TODO/FIXME/HACK markers in code — every such reference is prose in `docs/`.

---

## 9. Gaps in this investigation

- Not covered by any strand: the `action/` TypeScript beyond `main.ts` and `context.ts`,
  MintPlayer.Spark's own source (read only for the message-bus constraint and via
  `reauth-on-401.md`'s citations), the ClientApp beyond the pages named above, and anything
  requiring VPS access or running the app.
- Competitor research is documentation- and issue-tracker-based; no competitor product was operated
  directly.
- The GitHub API behaviours in T2.1 (compare pagination caps, checks-vs-statuses semantics, rate
  limits) are from current GitHub documentation, not from calls made against the API. Verify the
  300-file compare cap and the annotation batch size empirically during M11.1/M11.5 rather than
  trusting the doc.
- Nothing here was measured on production data — every "silent" claim is a code-reading, not an
  observed incident, except the token-expiry one (§T0.2), which has a logged occurrence.

## 10. Suggested sequencing

1. **T0.1 backups** — unrelated to everything else, do it now, rehearse the restore.
2. **T0.2 token expiry** (incl. `OnRemoteFailure` and last-sync timestamp) — closes the live
   incident; verify the App's expiry setting first.
3. **T1.1 + T1.2** — the number stops being silently wrong. Small, and they invalidate any
   measurement taken before them.
4. **T1.4 reprocess** — the recovery lever for data already ingested under T1.1/T1.2 defects.
5. **T1.3 + T1.6** — failures become reachable and legible.
6. **T0.3 + T0.4** — bounds and readiness (independent; can run in parallel with 3–5).
7. **M11.0 permission ask** — cheap, and it starts the clock on installation owners accepting.
8. **M11.1 patch coverage, display-only** — no permission needed, independently useful, and it
   retires the hardest design risk in the whole roadmap before anything is built on top of it.
9. **T1.5 / M11.2 config** — *after* the §7.1 decision.
10. **M11.3+ checks and the rest of PR feedback** — once thresholds and `ignore` exist.

Steps 1–5 are roughly one milestone's worth of work and, taken together, change the product from
*"a number you hope is right"* to *"a number that tells you when it isn't"*. Step 8 is the first one
that adds a genuinely new capability, and it was moved this early precisely because it turned out not
to need the permission upgrade everyone assumed gated it.

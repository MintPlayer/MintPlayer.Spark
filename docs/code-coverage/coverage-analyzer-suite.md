# Coverage analyzer suite — partial uploads, patch coverage, check-runs, thresholds, flags

**Status: ✅ BUILT 2026-08-19 (M1–M10) · branch `coverage-analyzer-suite` · one squash-merged PR by explicit decision.**

As-built deviations from the plan below, all conscious:

- **M4**: GitHub's 300-file comparison cap is *disclosed* (`Truncated` → `patch-diff-truncated`
  output, called out in the check-run summary) rather than paginated past — SP-A remains open for a
  consumer that actually hits it.
- **M6/M7**: the `coverage.yml` override landed inside M7's publisher as M6's text already said;
  the settings API and panel shipped in M6.
- **SP-C** resolved by decision: the walk-back does **not** verify ancestry with a compare call —
  the substitution is disclosed instead (`baseResolution: walked`), which is cheaper and honest.
  Revisit only if a force-pushed default branch bites someone in practice.
- **D4 hardened**: `BuildFinalizer` now refuses to promote a partial build to
  `Repository.LatestCoverage` under *any* branch condition — closing the
  `DefaultBranch is null` hole OIDC-provisioned repos had.
- Flag names sanitize to `[a-z0-9._-]` and per-flag totals key by the sanitized name.

This is the umbrella PRD/plan for everything a coverage analyzer still needs on top of
[#11](https://github.com/MintPlayer/CodeCoverage/issues/11) (scoped baseline for partial
uploads). The owner has decided the remaining deliverables land in **one pull request**
rather than sequentially — the plan below is therefore ordered so every milestone leaves
the branch green and committable, but nothing merges until all of it does.

Grounded in three investigations run 2026-08-19: a codebase audit (what is stored, what
the GitHub App can do), a competitor study (Codecov worker/shared source, Coveralls, Qlty,
SonarQube docs), and a feasibility pass over the four candidate features. Prior art in
this repo: `docs/roadmap-2026-08.md` (§T2.1 patch coverage, §T1.5 config, §5 flags),
`docs/upload-api.md` (published status contract, check-run naming commitment), issue #11
(scoped baseline design, carryforward rejection).

## 0. Decisions (owner Q&A, 2026-08-19)

| # | Decision |
|---|----------|
| D1 | **Both comparison modes, consumer chooses.** The status endpoint returns the scoped baseline (#11 as written) *and* a patched whole-workspace projection; the gate config picks which one to ratchet on. |
| D2 | **Hybrid base declaration.** The uploader declares the base sha `nx affected` actually ran against (required for a partial upload). The server independently resolves the PR merge-base via GitHub's compare API and reports both when they disagree. |
| D3 | **Missing base → walk back + disclose.** When the declared base has no usable coverage, resolve to the nearest covered default-branch ancestor and return `resolvedBaseSha ≠ requestedBaseSha` so the substitution is visible. Null (gate abstains) only when nothing at all resolves. |
| D4 | **PR build data is deleted when the PR merges** (pull_request closed+merged webhook), not on a timer. Repos without the App installed get no webhook and therefore no cleanup — accepted gap, documented, revisit if storage hurts. |
| D5 | **Flags, full.** Per-flag totals from upload-time labels, which requires per-flag storage at parse time (the merger destroys attribution). Components (path-glob grouping) rejected in favour of the real thing. |
| D6 | **Patch coverage is head-report + diff, added lines only** — the Codecov model, computed from data we already store. It never needs the base's coverage. |
| D7 | **Check-runs** `coverage/project` and `coverage/patch` (names already published in `docs/upload-api.md`). Requires the App permission upgrade (Checks RW, Pull requests RW) the README already anticipates. |
| D8 | **Thresholds config**: settings document is authoritative, optional `coverage.yml` in the repo overrides per field, policy read from the **base ref**, `Blocking` defaults false (roadmap §7.1, resolved). |
| D9 | Additive only: `baseline` keeps its #11/whole meaning for non-partial uploads; every new response field is new surface. |

## 1. The partial-upload comparison model

A PR workflow runs `nx affected --base=<B>` and uploads coverage for only the affected
projects, plus `partial: true` and `base-sha: B`. The server then owns three numbers:

1. **Scoped baseline** (#11 D1): base build's tree restricted to the paths present in this
   build, vs the same paths at head. Honest, never synthetic.
2. **Patched projection** (new): take the base build's `BuildTreeSummary`, overwrite the
   entries for every path this build measured, prune paths deleted by the PR (from the
   uploaded `fileList` — a file at base absent from the current tree list is gone), sum.
   A whole-workspace *projection* computed at read time — zero documents copied, nothing
   passes through the max-only `CoverageMerger`, fully recomputable. This is what the
   owner asked for with "patch the affected report onto the stored main-branch-base
   report"; it is Codecov's pseudo-compare adjustment in reverse, minus line-shifting
   (we patch at file granularity, which is exactly the granularity `nx affected` re-tests).
3. **Patch coverage** (D6): coverage of the PR's added lines, from the head build's
   per-line `FileCoverage` + the merge-base diff. Independent of the base's coverage.

The response labels every number with its scope (`mode`, base shas, files in scope) — D5
of #11: a number whose denominator is implicit is how this goes wrong.

### Projection completeness (owner directive, 2026-08-19)

Several inputs can degrade the reconstructed tree, and the server can only do its best:
the base may have resolved by walk-back instead of exactly (`baseResolution: walked`),
there may be no `fileList` so deleted files can't be pruned, the upload may contain
unverified/unmatched paths (PathNormalizer had no file list to check against), a session
may have failed to parse (`CompleteWithErrors`), or no base resolved at all. None of
these is an error — but a projection built on any of them must not present itself as a
confident whole-workspace number.

So the projection carries an explicit verdict: `projection.complete` (bool) plus
`projection.incompleteReasons[]` (machine-readable: `baseWalked`, `noBase`, `noFileList`,
`unmatchedPaths`, `parseErrors`). The UI renders a **danger `bs-badge` ("coverage
incomplete")** on the commit/build page whenever a partial build's projection is
incomplete, and the action exposes `projection-complete` as an output so a gate can
choose to abstain rather than trust a degraded reconstruction.

### Base resolution (D2 + D3)

```
declared = form.BaseSha                    # what nx affected used — required if partial
merge    = compare(default-branch, head).merge_base   # when API access exists
resolved = first of:
  declared,  if a finalized build with a live BuildTreeSummary exists for it
  merge,     same condition
  walk:      newest default-branch commit (Commits_ByRepository, AuthoredAt desc)
             with HasCoverage AND a live BuildTreeSummary, bounded to ~50 candidates
  null       # gate abstains; never an error (SP3 of #11: routine ~5% case)
```

Every response carries `requestedBaseSha`, `resolvedBaseSha`, and `baseResolution`
(`exact | mergeBase | walked | none`). The **live tree summary check** is load-bearing:
D4 deletes PR builds' data at merge, and `Commit.Coverage`/`HasCoverage` deliberately
survive as display denormalizations — so the resolver must verify the underlying document
still exists, never trust the flag. (This also self-heals dangling data from any source.)

### Rebases — analysis, no special handling needed

A rebase *helps* this design rather than hurting it: after rebasing a PR onto the default
branch tip, the merge-base becomes that tip — a real default-branch commit whose full
workflow run most likely uploaded whole-workspace coverage. The residual edges:

- **Base = a default-branch commit whose run was cancelled** (`cancel-in-progress`,
  ~5% per #11 SP3) → the walk-back handles it, disclosed.
- **Stacked PR whose lower branch was rebased**: the merge-base becomes an orphaned
  commit of the old lower branch. If that commit's PR build was uploaded it still
  resolves (data lives until the lower PR merges, per D4); after the lower PR merges and
  its data is deleted, the live-summary check fails and the walk-back widens the base to
  the default branch, disclosed. Degradation, not corruption.
- **Force-pushed default branch**: an abandoned tip keeps `HasCoverage` and possibly the
  newest `AuthoredAt`, so the walk could pick a base that is no longer an ancestor of
  head. Mitigation: when API access exists, verify ancestry with one compare call
  (status `behind`/`identical` ⇒ ancestor); without API access, accept and disclose.
  Spike SP-C decides whether the verification is worth its rate-limit cost by default.

### Stacked branches (branch-of-branch-of-branch)

`BuildFinalizer` already promotes `Commit.Coverage` branch-agnostically, so a feature-
branch base *can* resolve exactly — whenever the lower branch's CI uploaded for the
precise merge-base sha. When it can't (most of the time: PR workflows upload against PR
head shas, and heads advance), the resolver falls through to the default-branch walk and
says so. Scoped-baseline and patch numbers stay honest either way; only the patched
projection widens its meaning, and its `baseResolution` field states that. No competitor
handles stacks better than this (Codecov silently widens; Coveralls shows "FIRST BUILD").

## 2. Milestones

Each milestone = one commit, branch stays green. Costs: S < ½ day, M ≈ 1 day, L > 1 day.

**M1 — Declare partiality and the base sha · S.**
`UploadForm` gains `Partial` (bool) and `BaseSha`; `Build` gains `Partial`,
`DeclaredBaseSha`. A **dedicated field, not `ParentSha`** — that field still has two
writers with `??=` semantics and a history of meaning drift; it stays a hint. Action
gains `partial` and `base-sha` inputs, passed through. Absent ⇒ today's behaviour.

**M2 — Base resolver · M.**
`IBaseResolver` (new service, interface + `[Register]` impl + scripted test fake, same
seam pattern as `IGitHubAccessService`): the resolution chain above, including the
live-tree-summary existence check and the bounded walk. No GitHub API use yet (that
arrives in M4 and slots in as the `mergeBase` step). Unit-tested against embedded Raven.

**M3 — Scoped baseline + patched projection in `GET /api/uploads/status` · M.**
Extends the #11 N2 design: for a partial build, `baseline` (scoped totals) plus new
`projection` (patched whole-workspace totals, `complete` flag, `incompleteReasons[]`)
plus `baselineScope { mode, requestedBaseSha, resolvedBaseSha, baseResolution,
filesInScope, prunedFiles }`. Whole uploads unchanged (D9). Angular: a danger `bs-badge`
("coverage incomplete") on the commit/build page whenever a partial build's projection
is incomplete; action outputs gain `projection-complete`. Tests: the #11 N2 list, plus
deleted-file pruning on both sides, plus projection = base total when the partial upload
measured nothing new, plus each incompleteness reason surfacing exactly when its input
degrades.

**M4 — Diff + merge-base service · M.**
`IGitHubDiffService`: three-dot compare via Spark's `IGitHubInstallationService` Octokit
client where an installation exists; unauthenticated REST fallback for public repos
(60 req/h/IP — cached on the Build once fetched). Returns merge-base sha + per-file
added-line lists (hunk-header mapping, Codecov's algorithm). Handles the 300-file page
cap by paginating. Build records `ResolvedMergeBaseSha`. `FeedbackUnavailable`-style
null result when neither path is available (private repo, no App).

**M5 — Patch coverage · M.**
At finalize (and on demand for the status endpoint): for each diff file present in the
head build, point-load `FileCoverage` by path hash, classify each added line
(hits/misses; partials count as hits, Codecov formula). Diff files absent from the report
are **skipped, not zeroed** — same as Codecov; with nx-affected uploads an unaffected
project's changed lines must not read as misses. Stored as `Build.PatchCoverage
{ LinesCovered, LinesCoverable, FilesInDiff, FilesMatched }`; surfaced in the status
response and as action outputs `patch-rate`, `patch-lines-covered`, `patch-lines-coverable`.

**M6 — Thresholds / gate config · M.**
`Repository.Settings` sub-object (`[IgnoreForIndex]` — the BadgeToken lesson):
`ProjectMode (auto|fixed)`, `ProjectTarget?`, `ProjectThreshold`, `ProjectBasis
(scoped|projection)` ← D1, `PatchTarget?`, `PatchThreshold`, `Blocking = false`.
GET/PUT on `RepoSettingsController` behind the existing ownership gate; Angular
`repo-settings-panel` beside the badge panel. Optional `coverage.yml` read from the
**base ref** via `IGitHubContentService`, overriding per field, snapshotted onto the
Build (a base-dependent verdict needs its inputs stored — roadmap ConfigSnapshot rule).

**M7 — Check-runs `coverage/project` + `coverage/patch` · L.**
`PublishFeedbackMessage { BuildId }` broadcast after both `Finalize` call sites;
`PublishFeedbackRecipient` computes verdicts from M3/M5/M6 and posts check-runs via the
installation client. `Build.Feedback` outbox `{ State, Attempts, NextAttemptAtUtc,
CheckRunIds }` + a cron sweep (FinalizeBuildsCronJob pattern) for retries and
PR-opened-after-upload backfill. Idempotency: find-or-create by `(head sha, name)`,
update in place on re-finalize. `Blocking: false` ⇒ neutral conclusion with the numbers
(Codecov `informational`); no data ⇒ neutral "N/A" (Qlty precedent), **never a red X for
a missing baseline**. Repos without an installation: `FeedbackUnavailable`, recorded, no
retry storm. README: permission upgrade steps (Checks RW, PR RW), `new_permissions_accepted`
already handled.

**M8 — Flags, full (D5) · L.**
Parse time: alongside the build-level merge, `ParseSessionRecipient` merges each parsed
file **additionally into per-flag documents** `{buildId}/flags/{flag}/files/{pathhash}`
(reusing `CoverageMerger` per flag — max within a flag is correct for retries). Finalize:
per-flag totals `Build.FlagCoverage { flag → CoverageSummary }` + per-flag tree summaries
`{buildId}/flags/{flag}/tree`. Surfaced: status response `flags` map, commit page
per-flag panel, `GetTree`'s optional `?flag=` filter. No reprocessing of historical
builds (raw attachments exist; a replay lever stays out of scope — roadmap T1.4).
Write amplification bound: sessions carry ≤ a handful of flags; docs scale by
flags-per-file, measured in SP-D before committing to the doc-per-flag-per-file shape.

**M9 — Delete PR data on merge (D4) · S–M.**
`pull_request` closed + `merged` webhook → broadcast → recipient deletes, for every
commit of that PR (`PullRequestNumber == pr.Number`, branch ≠ default), each build's
document tree: Build (attachments die with it), `{buildId}/files/*`, `{buildId}/flags/*`,
`{buildId}/tree` — prefix streams, batched deletes. `Commit.Coverage` (summary) survives
for display; `LatestBuildId` is cleared so nothing dangles. The resolver's existence
check (M2) makes deleted bases degrade to walk-back automatically.

**M10 — Documentation · S.**
`docs/upload-api.md`: partial semantics, the three numbers and their scopes, base
resolution disclosure, check-run states, threshold config, flags, the D4 retention rule
— plus #11's independent ask verbatim: a null baseline is routine (cancelled base runs),
not only a first-upload condition; a gate must treat it as *abstain*, not error.
README: App permission table update.

Suggested order of implementation: M1 → M2 → M3 → M4 → M5 → M6 → M7 → M9 → M8 → M10
(M8 last among features: architecturally disjoint, touches the parse path; everything
else composes without it).

## 3. Spikes

- **SP-A** — Three-dot compare shape end-to-end: Octokit + unauthenticated; pagination
  past 300 files; hunk-header line mapping against a real nx-repo PR diff. (Feeds M4.)
- **SP-B** — Check-run idempotency: list-by-ref + name vs stored ids; behaviour on
  re-run of the same sha (run attempt 2). (Feeds M7.)
- **SP-C** — Walk-back ancestry verification cost/benefit: one compare call per status
  poll vs caching on the Build vs skipping entirely. Default to *cache the verdict on
  the Build at first resolution*. (Feeds M2/M4.)
- **SP-D** — Per-flag write amplification on a real consumer upload (ng-bootstrap:
  14 projects, flags today `pr`/`master`): doc count and total bytes per build under
  doc-per-flag-per-file. If pathological, fall back to per-flag *tree summaries only*
  (totals stay exact, per-flag line detail dropped). (Feeds M8.)
- **SP-E** — `fileList`-based pruning correctness for the projection: confirm the
  uploaded `git ls-files` is present on partial uploads from the action, and decide the
  no-fileList behaviour (no pruning + disclosed flag). (Feeds M3.)

## 4. Hazards, stated up front

- `nx affected` earns "sources and transitive deps unchanged", nothing more: workspace-
  root config edits mark nothing affected while changing every denominator (#11 §7).
  Instrumentation config changes are a re-baseline trigger; documented, not solved.
- The App permission upgrade is **silently absent** until each installation accepts it —
  check-runs simply don't appear. `FeedbackUnavailable` recorded per build makes the
  absence observable instead of mysterious.
- OIDC-only private repos get no diff (no installation, no unauthenticated access):
  patch coverage and merge-base resolution degrade to declared-sha-only, disclosed.
- D4 leaves PR data forever on repos without the App (no webhook). Accepted; documented.
- The patched projection is a *projection*: it asserts unmeasured files unchanged since
  base. `nx affected` is precisely the tool that justifies that assertion — but the
  response labels the number so nobody mistakes it for a measurement (D5 of #11).

## 5. Out of scope

~~Carryforward in any form (#11 §3 stands — the projection is computed, never stored).~~
**Superseded 2026-09-02:** the server now assembles every commit from all its builds and carries
a file forward from the declared base only when its git blob OID is identical at both ends — a
verified copy, not an assertion. The projection remains as the unverified estimate for old
clients. See [`../coverage_carryforward_PRD.md`](../coverage_carryforward_PRD.md).
Historical reprocessing of raw attachments (roadmap T1.4). Components/path-glob grouping
(superseded by D5 flags). Coveralls-style "indirect changes" detection. Badge/UI
theming for flags beyond the totals panel.

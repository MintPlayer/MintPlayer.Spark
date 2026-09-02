# PRD — Complete coverage from incomplete uploads (carry-forward + multi-step uploads)

**Status:** Implemented on branch `coverage-carry-forward` (2026-09-02); the live-server checks of exit criteria 1–5 and 7 run after deploy — see the plan's implementation status · **Date:** 2026-09-02 · **Plan:** [coverage_carryforward_plan.md](coverage_carryforward_plan.md)
**App:** `apps/CodeCoverage` (production, coverage.mintplayer.com) + `apps/CodeCoverage/action`
**Origin:** investigation by four parallel research agents on 2026-09-02 (server ingestion model, upload
action + this repo's workflows, the six consumer workflows, Nx/Codecov/Coveralls semantics). Findings
are recorded in §3 with `file:line` pointers so the plan can be checked against them.

> Written before the work. Where the build disagrees with it, the plan's milestone notes say so.

---

## 1. The question

A coverage upload can be incomplete for two independent reasons, and both must hold at once:

1. **`nx affected` measures a subset.** A PR run (and, if we ever want it, a master run) only tests the
   projects Nx considers affected since a base commit. The reports it produces cover *those* projects'
   files. Every other file in the workspace is simply absent — not zero, absent. Any whole-workspace
   percentage computed from that upload alone is wrong, and any comparison of it against a
   whole-workspace baseline reads as a coverage collapse.
2. **One commit is measured in several steps.** This repository produces a .NET solution's Cobertura
   reports *and* several Angular libraries'/apps' Cobertura reports; other repositories split .NET and
   Angular across jobs. Each step uploads what it has; the server must accumulate them and only then
   compute the commit's number. This is what the action's `partial` / `finish` inputs exist for, and
   the two scenarios must compose: a multi-step upload where *each* step is also an `nx affected` subset.

The server must therefore be able to say, for every commit, **"the whole-workspace coverage of this
commit is X%"** with the same meaning whether the commit was measured in full, in part, or in
several parts — and it must be able to say *how much* of X% it actually measured.

### 1.1 Two premises corrected

The request cited a "pesky workaround from yesterday's PR that invokes `npx nx affected` for each
non-affected project to restore cached reports". The investigation found:

- The step added in #349 (`.github/workflows/pull-request.yml:97-115`) is
  `npx nx run-many --target=build --projects=DemoApp,HR,Fleet,WebhooksDemo,CodeCoverage`. It restores
  the apps' **`bin/Debug` binaries** from the Nx cache so the `--spark-verify-model` /
  `--spark-verify-security` loops can `dotnet run --no-build`. It has nothing to do with coverage.
- The `test` target is still a bare `npx nx affected --target=test` (`pull-request.yml:170`). No
  coverage-restore workaround exists in this repository or in any of the six consumers (§3.4).
- The *mechanism* the request describes is real, though: `nx.json:46-55` declares `test` as cacheable
  with `outputs: ["{projectRoot}/coverage"]`, so naming an unaffected project in `run-many` would
  replay its cached `coverage/` directory. That is Option A in §4 and it is rejected as the primary
  design for the reasons given there.

---

## 2. Goals and non-goals

### Goals

- **G1 — One number per commit, honestly labelled.** Every commit with at least one finalized build
  has an *assembled* whole-workspace report: the union of what was measured on that commit plus,
  for every file that was not measured, that file's coverage carried forward from the base commit
  the subset was computed against — **but only when the file's content is provably identical**.
  Badge, trend, commit lists, deltas, check runs and the next commit's base all read the assembled
  report.
- **G2 — Carry-forward is a measurement, not an assertion.** A carried file is admitted only when
  its git blob OID at the head equals its blob OID at the source commit. No "we assume it didn't
  change".
- **G3 — Multi-step uploads accumulate, across jobs *and* across workflow runs.** All uploads for a
  commit contribute to its assembled report, whether they share a run id or not. Re-runs (same run
  id, higher attempt) supersede; distinct runs merge.
- **G4 — Zero-report runs still produce a report.** An `nx affected` run where nothing needs testing
  must yield an assembled report that is 100% carried, not a commit with no coverage.
- **G5 — Provenance is visible.** For every file the UI and the status API can say *measured on this
  commit* or *carried from `<sha>`*, and the commit shows how much of its number is measured vs
  carried and how old the oldest carried data is.
- **G6 — Contract compatibility.** Existing consumers pinned to `coverage-upload-v1` keep working
  unchanged; they simply don't get carry-forward until they take the new action build. Old
  `fileList` payloads are still accepted.
- **G7 — Uses stored processed data.** Assembly works from the per-file `FileCoverage` documents the
  server already stores; it never re-parses retained raw attachments.

### Non-goals

- Making Spark's *master* workflow use `nx affected`. It is `run-many` today and stays so; the value
  of this work lands in PR runs here and in `mintplayer-ng-bootstrap`, and unlocks `affected` on
  master later if wanted.
- Historical reprocessing of builds finalized before this ships (roadmap T1.4). Carry-forward chains
  start from the first assembled report.
- Branch coverage in tree summaries (they are line-based by design, `PartialComparison.cs:19-21`).
- Any change to the max-merge semantics *within* one build (`CoverageMerger.cs`).

---

## 3. Measured starting state

### 3.1 Server ingestion (apps/CodeCoverage)

- `POST /api/uploads` (`Controllers/UploadsController.cs:87-189`), multipart, ≤50 MB. Form fields
  include `commitSha, branch, pullRequestNumber, parentSha, runId, runAttempt, jobName, flags,
  rootDir, fileList, partial, baseSha, files` (`:419-437`). Reports are stored **as RavenDB
  attachments on the Build** (`:166-176`), the `fileList` (`git ls-files` output) as another
  attachment (`:172-175`).
- **Document model:** `Repository` → `Commit` (`Commits/{repoId}/{sha}`, holds denormalized
  `Coverage` + `LatestBuildId`, `Commit.cs:47,67`) → `Build` (`{commitId}/builds/{runId}-{runAttempt}`,
  `Build.cs:102-103`; `Status Open|Finalized`, `Partial`, `DeclaredBaseSha`, embedded `Sessions`) →
  `FileCoverage` (`{buildId}/files/{sha256(path)[..20]}`, per-line hits + branches,
  `FileCoverage.cs:35-36`; per-flag copies under `{buildId}/flags/{flag}/files/`) →
  `BuildTreeSummary` (`{buildId}/tree`, path + covered/coverable per file). Percentages are never
  stored; only counts (`CoverageSummary.cs:7-14`).
- **Within one build** every session merges into the same `FileCoverage` docs with **max** semantics
  (`Ingestion/CoverageMerger.cs:6-25`); retries are idempotent. Uploads sharing
  `(repo, sha, runId, runAttempt)` are sessions of one Build (`UploadsController.cs:179`);
  `build.Partial |= form.Partial`, `DeclaredBaseSha ??= form.BaseSha` (`:151-152`). A late upload
  re-opens a Finalized build (`:138-145`).
- **Different run id ⇒ different Build.** `BuildFinalizer.Finalize` writes `commit.Coverage =
  build.Coverage; commit.LatestBuildId = build.Id` (`Ingestion/BuildFinalizer.cs:38-39`) — the commit
  headline is **last-finalize-wins**. Two workflows uploading halves of the same commit end with the
  headline of whichever finished second. ⚠ This is the multi-step gap.
- **Finalize triggers:** explicit `POST /api/uploads/finish` (`:192-209`) or the cron: 2 min debounce
  after last upload once all sessions parsed, 30 min hard timeout (`FinalizeBuildsCronJob.cs:26-65`).
- **Partial builds today:** `Ingestion/PartialComparison.cs` computes, on the fly and unstored, a
  *scoped baseline* (base restricted to measured paths) and a *projection* (base tree with measured
  files overwritten and files absent from the head `fileList` pruned) — documented as "a
  whole-workspace number that *asserts* unmeasured files unchanged" (`:12-16`). `BuildComparer.cs`
  wraps it with a completeness verdict (`baseWalked | noFileList | unmatchedPaths | parseErrors`).
  Only the status endpoint and the check-run publisher read it. Partial builds are **never** promoted
  to `Repository.LatestCoverage` (`BuildFinalizer.cs:51-55`).
- **Base resolution** (`Services/BaseResolver.cs:20-77`): declared `baseSha` exactly → PR merge-base
  via GitHub compare API (`IGitHubDiffService`) → bounded walk down the default branch's covered
  commits → none. A candidate is usable only if its finalized build's tree summary still exists.
- **Git graph knowledge:** none beyond `Commit.ParentSha`, written only by the `pull_request` webhook
  as the PR base tip (`GitHubEventsRecipient.cs:183-190`); `push` deliberately does not write it.
- **Known defects touching this work** (`docs/code-coverage/roadmap-2026-08.md:199-227`): T1.2 a
  session where only some reports parsed is reported `Parsed`; T1.3 unfinished/failed commits are
  invisible in the UI (`withCoverageOnly = true`); T1.4 raw attachments retained but never reprocessed.
- `docs/code-coverage/coverage-analyzer-suite.md:262-265` states as out of scope: "no carryforward in
  any form". This PRD reverses that decision and says why (§4).

### 3.2 The upload action (apps/CodeCoverage/action)

- Inputs (`action.yml:8-71`): `url, token, use-oidc, files, directory, flags, partial, base-sha, name,
  finish, fail-ci-if-error, disable-search, wait-for-finalize, …`. Commit/branch/PR come from the
  Actions context (`src/context.ts:22-43`); on `pull_request` the PR **head** sha is used.
- Flow (`src/main.ts:20-101`): capabilities probe → glob reports → `git ls-files` → multipart POST →
  optional `finish` → optional status poll. Reports are gzipped client-side, never parsed.
- ⚠ **Zero report files ⇒ `throw`** (`main.ts:41-45`), which with the default
  `fail-ci-if-error: false` is a warning: **nothing is uploaded and no `finish` is sent.** A fully
  cache-hit `nx affected` run therefore produces no Build at all. This defeats G4 and must change.
- Spark PR upload (`pull-request.yml:190-209`): four globs, `partial: true`, `base-sha: ${{ env.NX_BASE }}`
  (from `nrwl/nx-set-shas@v5`), `finish: true`. Master upload (`dotnet-build-master.yml:121-135`): same
  globs, no `partial`, `finish: true`. Master's `hashFiles` guard only checks `tests/*/coverage/**`
  while the PR guard checks all globs (minor inconsistency, fix in passing).

### 3.3 Nx facts that shape the design

- `nx affected` never schedules unaffected projects, so their cached `coverage/` outputs are never
  restored. `run-many` over all projects with a warm remote cache would restore them (§1.1), because
  `test` declares `outputs` (`nx.json:52-54`).
- The affected set is relative to `--base`. With `nx-set-shas`, on `pull_request` that is the
  merge-base with the target branch; on push to master it is the **last successful run of the same
  workflow**, falling back to `HEAD~1`. A root-level change (`package.json`, `nx.json`,
  `tsconfig.base.json`, lockfile) affects every project.
- **Consequence that fixes the design:** the set of files *not* in an affected upload is exactly the
  set of files in projects Nx judged unchanged since `base-sha`. The right commit to carry forward from
  is therefore **the declared `base-sha`**, not the git parent — the same commit the server already
  receives and resolves first.

### 3.4 The six consumers (all on `@coverage-upload-v1` except Spark, which uses `./apps/CodeCoverage/action`)

| Repo | Master tests | `nx affected`? | Uploads/run | `partial` |
|---|---|---|---|---|
| MintPlayer.Dotnet.Tools | `dotnet test` whole solution | no | 1 | PR: no |
| MintPlayer.AspNetCore.Tools | same (+ "assert tests ran") | no | 1 | PR: no |
| MintPlayer.AspNetCore.SpaServices | same | no | 1 | PR: no |
| MintPlayer.AI | same, OIDC auth | no | 1 | PR: no |
| MintPlayer.Spark | `nx run-many -t test` (.NET + vitest) | **PR only** | 1 | PR: yes + `base-sha=NX_BASE` |
| mintplayer-ng-bootstrap | `nx run-many -t test` + `dotnet test` api, lcov | **PR only** (`--base/--head` = PR base/head) | 1 | PR: yes, `flags: pr` |

Nobody uploads in more than one step today. Nobody has a coverage-restore workaround. Both Nx repos
rely on `partial: true` and accept that the PR number is a like-for-like comparison, not a total.

### 3.5 Prior art (for vocabulary, not for copying)

- **Codecov carryforward flags:** flag = partial identity (one flag per upload); when a commit has no
  upload for a flag, the flag's file coverage is copied from the nearest ancestor that has it; origin
  commit shown; requires one full baseline upload. Base = first parent / PR base, preferring commits
  with successful CI.
- **Coveralls parallel builds:** `parallel: true` + `flag-name` per job; a `parallel-finished` call
  merges; `carryforward: "flagA,flagB"` on the finish step lists flags to reuse from previous builds
  when jobs are missing.

Both carry forward by **flag granularity**. Neither verifies file content. §4 explains why we carry by
**file granularity** with content verification instead.

---

## 4. Options

### Option A — make CI upload a complete report (`nx run-many` + cache replay)

Replace `nx affected -t test` with `nx run-many -t test` and let Nx restore unaffected projects'
cached `coverage/` directories. Zero server changes.

- Pro: uniform uploads; correctness by content hash (Nx's hash includes inputs of dependencies).
- Con: depends on a *warm remote cache* — GitHub-hosted runners have no local cache, and every
  `sharedGlobals` change (root `package.json`, lockfile, `nx.json`) is a full cold re-run of ~30 test
  projects. Con: only works for Nx repositories; a repo whose .NET and Angular halves are in separate
  jobs still needs the server to merge. Con: does nothing for scenario 2. Con: silently reverts to
  "partial" the moment the cache is cold, and the server cannot tell.
- **Verdict:** rejected as the design. Remains a legitimate *consumer-side* choice for repos that
  prefer it; the server must be correct regardless of which they pick.

### Option B — Codecov-style flag carry-forward

Require each upload to carry one flag identifying the project; carry whole flags forward from the
base when absent.

- Con: `nx affected` produces *all* affected projects' reports in one step and one glob; a flag per
  project would require one upload step per project, which no consumer does and which the Nx graph
  makes dynamic. Con: the .NET solution is one `dotnet test` run producing one report per test
  project, not per source project. Con: flags cannot express "this file is unchanged".
- **Verdict:** rejected. Flags stay what they are today (a display grouping).

### Option C — file-level carry-forward from the declared base, verified by git blob OID ★ recommended

The action sends the head's file list **with blob OIDs** (`git ls-files --stage` → `<mode> <oid>
<stage>\t<path>`, we send `<oid> <path>`). The server stores it on the Build as today. At assembly
time, for every path in the head file list that has **no measured `FileCoverage` on this commit**, the
server looks up the same path in the base commit's assembled report; if the base's recorded blob OID
for that path equals the head's, the file's coverage is **copied** into the head's assembled report
with provenance `{carriedFromSha, carriedFromBuildId, originSha}` (origin = where it was last actually
measured, so chains report their true age). Files whose OID differs or which are absent from the head
file list are not carried. Measured files always win over carried ones — no max-merge across
measured/carried.

- Pro: correctness is per file and content-verified (G2). Pro: works for any CI shape — Nx or not,
  one step or many. Pro: no remote cache dependency. Pro: uses stored `FileCoverage` docs only (G7).
  Pro: renames, deletions and moves are handled by the file list, exactly as the current projection
  prunes them. Pro: `base-sha` is already sent and resolved first — the carry source equals the Nx
  comparison base by construction (§3.3).
- Con: the action must change to send OIDs; until a consumer upgrades, the server falls back to
  **Option C′** (below) or to "no carry, projection as today". Con: storage — the assembled report
  per commit is a full copy; sized in Spike S2.
- Semantics of *"a file whose content is unchanged but whose behaviour under a changed dependency
  differs"*: if the file's project depends on the changed project, Nx marks it affected and it is
  measured, so it is never carried. If the changed project depends on *it*, the changed project's
  test run may instrument it too; that measurement is on the head and wins. The only stale case is
  a dependency change Nx cannot see (an unpinned external package) — the same blind spot Nx itself
  has, accepted.

### Option C′ — fallback for old-action payloads (plain `fileList` without OIDs)

Use the GitHub compare API between base and head (already wired: `IGitHubDiffService.CompareAsync`)
to obtain the changed-file set; carry forward files that are in the head file list, unmeasured, and
**not** in the changed set. Only available when a GitHub App installation exists for the repo;
otherwise no carry-forward and the completeness verdict says so (`noBlobIds`). This keeps G6 while
making the upgrade path obvious.

### Decision

**Option C with C′ as the degraded path.** Completeness is reported, never guessed: a commit's
assembled report carries a `completeness` verdict that is `Complete` only when every path in the head
file list that exists in the base's assembled report is either measured or carried with an OID match,
and the base resolved `exact`.

---

## 5. The design

### 5.1 Vocabulary

- **Measured** — a `FileCoverage` produced by parsing a report uploaded *for this commit*.
- **Carried** — a `FileCoverage` copied from the base commit's assembled report because the path is
  unmeasured here and its blob OID matches.
- **Assembled report** — per **commit** (not per build): measured ∪ carried, plus provenance and a
  completeness verdict. It is what the commit's headline, badge, trend, deltas, check runs and any
  later commit's carry-forward read.
- **Base** — the commit resolved by `BaseResolver` from the build's declared `baseSha`
  (`exact | mergeBase | walked | none`), unchanged.

### 5.2 Storage

- `Commit.Coverage` keeps meaning "the headline" but now equals the assembled totals.
- New `CommitAssembly` document `{commitId}/assembly` with: `Builds[]` (the build ids that
  contributed and which attempt), `BaseSha`, `BaseResolution`, `Measured`/`Carried` counts,
  `Completeness` + `IncompleteReasons[]`, `OldestOriginSha` + age in commits/days, `AssembledAtUtc`.
- Assembled files live at `{commitId}/assembly/files/{hash}` (same `FileCoverage` shape plus
  `Origin { Kind: Measured|Carried, FromSha, FromBuildId, OriginSha }` and `BlobOid`), and an
  assembled tree summary at `{commitId}/assembly/tree`. Build-level `FileCoverage`/tree documents stay
  as the record of *what was uploaded*; browse endpoints switch to the assembly.
- The head file list with OIDs is stored where `fileList` is stored today (attachment); the assembly
  indexes `path → oid` into the assembled files so the *next* commit can compare without re-reading
  the attachment.
- Retention: PR builds are deleted at merge today (`DeletePullRequestBuildsRecipient`); the PR
  commit's assembly goes with them. Default-branch assemblies are kept — they are the chain.

### 5.3 When assembly runs

Assembly is a **commit-level** step triggered every time any build of the commit finalizes (or is
re-opened and re-finalized). It replaces the `commit.Coverage = build.Coverage` line in
`BuildFinalizer`:

1. Collect the commit's finalized builds; for each `runId` keep only the highest attempt.
2. **Measured set** = max-merge of those builds' `FileCoverage` docs (same merger as within a build —
   two workflows measuring the same file on the same commit are the same situation as two jobs).
3. Resolve the base from the builds' declared `baseSha` (they must agree; disagreement is an
   `IncompleteReason: baseMismatch`, and the first-finalized wins). A commit with **no** partial build
   and no declared base skips carry-forward: its measured set *is* the assembly (full uploads behave
   exactly as today).
4. **Carried set** = for each `(path, oid)` in the head file list not in the measured set: look up
   `path` in the base's assembled files; copy if `BlobOid` matches (C) or if the compare API says
   unchanged (C′); otherwise record it as `unmeasured`.
5. Materialize the assembled files and tree, compute totals and provenance, write `CommitAssembly`,
   set `Commit.Coverage`, then apply the existing promotion rule to `Repository.LatestCoverage` with
   `!build.Partial` replaced by `assembly.Completeness == Complete`.
6. Publish check runs / status from the assembly (`BuildComparer` reads assembled trees).

### 5.4 The two scenarios, composed

- **Multi-step, one run:** sessions of one build, as today; assembly happens at finalize.
- **Multi-step, several runs (or several workflows):** each build finalizes on its own schedule;
  assembly re-runs on each finalize, so the commit's number converges to the union. The status
  endpoint reports `assembly.Builds` so a poller can see whether the other half has landed.
- **Every step is itself `nx affected`:** each build carries `partial: true` and the same `base-sha`;
  step 4 fills in whatever the union of all steps left unmeasured. The result is identical whether the
  subset was split across one step or five.
- **Nothing affected:** the action uploads a session with zero reports and the file list (see §5.5);
  the build finalizes with an empty measured set; the assembly is 100% carried and `Complete`.

### 5.5 Action contract changes (still `contract: 1`, additive)

- `fileList` gains OIDs: lines are `<40/64-hex-oid> <path>`; the server accepts both this and the
  legacy path-per-line format by sniffing the first line. Capability feature `carry-forward` is
  advertised; the action warns when `partial: true` and the server lacks it (mirrors the existing
  `partial-uploads` warning, `capabilities.ts:88-95`).
- **Zero report files with `partial: true` is not an error:** the action still uploads (file list
  only) and still sends `finish`. Without `partial` it remains the current warning/error.
- `finish` semantics documented plainly: *"this job is the last uploader **of this run**"*. It never
  claims to be the last uploader of the commit; assembly handles that.
- New input `carry-forward` (default `true`), sent as a form field on the upload. Workflows wire it to
  the test step's outcome (`${{ steps.test.outcome == 'success' }}`). When `false` the server stores
  the measured files as usual but carries nothing into that commit's assembly and records the
  `testsFailed` reason — a crashed suite must never be papered over with the base's numbers (S1).
- Outputs gain `assembly-completeness`, `assembly-measured-files`, `assembly-carried-files`,
  `assembly-oldest-origin-sha`.

### 5.6 What the UI shows

- Commit page: assembled number, a measured/carried split, `carried from <sha>` per file and folder,
  and a completeness badge with the reasons. Files that are neither measured nor carried are listed
  as *unmeasured* rather than silently dropped.
- Commit list: unfinished and failed commits become visible with their state (this is roadmap T1.3;
  it is in scope because a multi-step commit is *legitimately* unfinished for a while and hiding it
  makes the feature undebuggable).
- Badge: unchanged rendering; it reads `Repository.LatestCoverage`, which is now the assembled number
  and promoted only when `Complete`.

### 5.7 The Δ columns on the repository page

**Defect (observed on the live CodeCoverage repository page, 2026-09-02):** `Commit.CoverageDelta` is
stamped by `CommitActions.StampCoverageDeltas` as *this row minus the chronologically next row of the
whole repository*, across branches. A master commit that follows a PR-branch commit with equal
coverage shows `0.0` although master had never been measured before; a PR commit's delta is taken
against whatever branch happened to land just before it. The number answers "what changed since the
previous upload to this server", which nobody asks.

**Replacement — two persisted deltas, computed once at assembly time, never from neighbouring rows:**

| Column | Meaning | Source |
|---|---|---|
| Δ parent | change versus the commit's **git first parent** (`<sha>^1`). On a PR branch that is the previous commit of the PR; on a squash-merged default branch it is the previous default-branch commit. | `Commit.CoverageDeltaVsParent` |
| Δ base branch | change versus the default branch's most recent **complete** assembly at or before this commit's authored time — "what would merging this do to master's number". For a default-branch commit the two columns coincide. | `Commit.CoverageDeltaVsDefaultBranch` |

The carry-forward base (`base-sha`, the Nx comparison point) is deliberately **not** the reference for
either column: on PRs `nx-set-shas` yields the merge-base, which is neither the parent nor the
base-branch tip, and a Δ against it would be a third, confusing number.

**Where the parent comes from.** Today the action's `parentSha` field carries the PR's *base* sha on
`pull_request` events and nothing on pushes (`context.ts:36`), and `Commit.ParentSha` is documented
as meaning two different things depending on the writer. The action instead sends the true first
parent, `git rev-parse <commitSha>^1` (the workflows already check out with full history for
`nx-set-shas`); the server falls back to the GitHub commits API (`parents[0].sha`, anonymous for
public repos, App token otherwise) when the field is absent, and stores it in `Commit.ParentSha` with
that single meaning. The old PR-base value is no longer sent; the resolver never used it.

Rules: both deltas are `null` (rendered as `—`) when the reference commit has no complete assembly,
never `0.0`; both are stored on the `Commit` document by the assembler, so the list query does no
per-row work and the grid can sort on them; when a commit is (re-)assembled, the commits whose
`ParentSha` is its sha, plus — if it is a default-branch commit — the later commits that referenced the
previous default-branch assembly, are re-stamped. The in-memory stamping in `CommitActions` is deleted.

---

## 6. Hazards and how the design answers them

| Hazard | Answer |
|---|---|
| Base's own report is incomplete (cold start, first run after enabling) | Carry only from an assembly; if the base has none, `IncompleteReasons: noBase`, no promotion to the badge. The chain starts from the first full upload, which every consumer's master workflow already produces. |
| `nx-set-shas` picked a base whose CI run was green but whose upload silently failed | Resolver falls through to `mergeBase`/`walked`; carry-forward still content-verified per file, but `baseWalked` keeps the verdict at `Partial`. |
| Force-push / rewritten history | Same as above; nothing is carried without an OID match. |
| A file deleted or renamed on the head | Absent from the head file list ⇒ never carried (renamed path is measured if its project was affected, which a rename guarantees). |
| Carried data very old (a project never affected for months) | `OldestOriginSha` and age are stored and displayed; a repo-level `coverage.yml` knob `carryforward.maxAgeCommits` can downgrade the verdict. Content is still identical, so the number is still true. |
| Measured and carried both exist for a path | Measured wins outright; no max-merge across the boundary (a carried higher hit count must never mask a regression). |
| Two workflows disagree on `base-sha` | `baseMismatch` reason; first finalized base wins for that assembly. |
| Old action build (no OIDs) | C′ via compare API when the App is installed; else no carry and `noBlobIds`. Never wrong, only less complete. |
| Assembly storage per commit | Sized in S2; PR assemblies deleted at merge with their builds; default-branch assemblies kept. |
| A test suite crashes and emits no report while the upload step still runs (`if: always()`) | The server cannot distinguish "affected but crashed" from "unaffected", so the action carries an explicit `carry-forward` input that workflows wire to the test step's outcome. When false, measured files are stored but nothing is carried and the assembly records `testsFailed`. (Found in S1: the ng-spark-auth suite at `7ad2e306` produced no report at all.) |
| Sources imported by no spec are absent from vitest reports (`coverage.all` unset) | Not a carry-forward defect — the OID gate never carries a changed file — but it under-reports the denominator. Fixed alongside the dogfood: `all: true` on both vitest configs. |
| Assembly races (two builds finalize within the same second) | Assembly runs in the single-consumer finalize recipient; a compare-exchange on `{commitId}/assembly` guards concurrent finalizers, and the loser re-queues. |

---

## 7. Spikes (time-boxed, results recorded in the plan)

- **S1 — OID invariant on real commits.** For three real Spark PR merges, run `nx affected -t test
  --base=<merge-base>` locally, collect the report file set, and diff it against `git diff --name-only`
  between base and head. Prove: every file whose blob changed appears in the measured set (or its
  project has no coverage at all); count files that would be carried. Also measure `git ls-files -s`
  payload size for Spark and ng-bootstrap (gzipped).
- **S2 — Assembly cost.** Count `FileCoverage` docs and bytes for the latest master build of each of
  the six repos on the live server (read-only RavenDB query); estimate per-commit assembly storage and
  the write time of copying them with a bulk insert. Decide copy-vs-reference (§5.2 assumes copy).
- **S3 — Cross-run behaviour today.** Integration test: two builds with different run ids on one
  commit, finalized in either order; confirm last-finalize-wins (§3.1) so the fix has a red test.
- **S4 — C′ viability.** For a repo with the App installed, call `CompareAsync(base, head)` for a PR
  with >300 changed files and confirm the changed-file list is complete (GitHub caps compare
  responses); decide whether C′ needs pagination or a hard cap that forces `noBlobIds`.

---

## 8. Out of scope

- Switching Spark master to `nx affected`, or changing any consumer's *test* commands.
- Re-parsing retained raw attachments (T1.4).
- Per-flag assemblies (flags remain a display grouping of measured data).
- A second contract version; everything here is additive to `contract: 1`.

## 9. Exit criteria

1. A Spark PR that touches only `libs/node_packages/ng-spark-auth` shows an assembled number within
   0.05 pts of master's number, with `Completeness: Complete`, measured files = that lib's files,
   everything else carried from the merge-base with matching OIDs.
2. A commit uploaded by two separate workflow runs (one .NET, one Angular) shows the union; the
   status endpoint lists both builds; finalization order does not change the result.
3. A PR run with nothing affected produces an assembled report that is 100% carried and `Complete`.
4. An upload from an action build without OIDs on a repo with the App installed yields carry-forward
   via C′; on a repo without the App it yields `noBlobIds` and no promotion — and the wrong number is
   never shown as the headline.
5. Existing full-upload consumers see no behaviour change (same numbers, same badge) — verified by
   comparing the six repos' badges before and after deploy.
6. Every new state is visible in the UI and the status API, including unfinished commits (T1.3).
7. On the CodeCoverage repository page the first measured master commit (`7fc84af`) shows `—` in both
   Δ columns, not `0.0`; `f48ec22` shows `+1.3` against its base `14a3277` and its own delta against
   master's latest complete assembly at that time; no row's Δ changes when the grid is re-sorted or
   another branch's commit is inserted chronologically next to it.

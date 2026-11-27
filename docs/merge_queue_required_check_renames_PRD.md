# Merge queue: renaming a required CI job (#714) — PRD and plan

Issue #714 (bug), with both of its comments. One pull request. This file holds the findings, the options, the owner's
questions, the spikes and the milestones; the decisions and the log follow as they are made. Paths are under
`apps/CodeCoverage/` unless they start with `docs/`, `.github/` or `CLAUDE.md`.

Status: **draft, 2026-11-27.** Nothing is decided yet. §2 corrects part of the issue (F1) and finds two more stalls
(F3, F4). §5 holds the owner's questions. Spikes S1, S3 and S5 are done (§6.1). S1 found that `/release` itself is not
at risk, so nothing here has a deadline any more.

## 1. What happened

- PR #697 joined two matrix jobs in `.github/workflows/test.yml` (#536 M9a, `2ca3ed499`). `tests / Library suites` and
  `tests / CodeCoverage git suites` became `tests / Library + git suites`, and `tests / SQL Server` and
  `tests / PostgreSQL` were added, to stay at 20 concurrent jobs.
- Ruleset 26719109 (ruleset 2, "merge-queue: reviews and checks") still required the two old names. On head
  `6b25997d7` every reported check had completed. GitHub showed the two old names as "Expected — waiting for status to
  be reported", and the "Merge queue" check stayed in progress. Nothing would ever report them.
- The owner fixed the ruleset by hand at 06:59 UTC (its live `updated_at` is 2026-11-27T07:59:51+01:00). The entry
  still waited: it keeps the rules it read when it was enabled (`entry.RequiredChecks`), and every 2-minute poll
  checks against that copy until `checks.timeout` (60 min).
- `/dequeue` (07:03:38 UTC) and `/queue` (07:04:25) started a new entry at 07:04:33. It read the fixed ruleset, found
  every check reported, and merged at 07:04:44.

The issue proposes three directions:
1. The setup accepts check names the default branch has not reported yet.
2. The queue names the deadlock.
3. Docs.

Comment 1 adds a fourth: re-read the required checks when a poll finds every check suite completed and a required
check still missing.

## 2. Findings (2026-11-27)

Three read-only investigations looked at the setup, the engine, and CI with the live rulesets. Every claim below was
checked again against the code or the API before it went in.

### F1. The setup can already take a name the default branch never reported

The issue's "Why the setup can't fix it" is out of date.
- Since #554 (2026-11-07) the planner no longer guesses from the default branch's head. That head carried push-only
  jobs, so ruleset 26719109 once required checks no pull request could pass (`MergeQueueRulesets.cs:205-209`).
- The form starts from ruleset 2's own contexts, or else classic protection's (`MergeQueueRulesets.cs:210-227`).
- Its picker offers the checks seen on the repository's pull requests (`Repository.KnownChecks`). "Add new" takes a
  typed name (`MergeQueueRulesetSetup.cs:157-162`).

So direction 1 exists already. Four things are missing:
- **Which names changed.** The confirmation says only that `required_status_checks` changed. It doesn't say which
  names were added or dropped.
- **Stale names.** Nothing flags a required name that no recent pull request reported. The form's list replaces the
  ruleset's (`MergeQueueRulesetSetup.cs:176-185`), so an old name stays until the admin removes it.
- **A prompt.** Nobody is told to run the setup. The pull request that renames a job has no idea a ruleset names it.
- **The doc.** `docs/code-coverage/merge-queue.md:598-599` still says "Ruleset 2 requires the checks the default
  branch's head reports".

### F2. The engine knows which checks are missing, but it never says so and never re-reads

- **Both lists are set once per queue run.** The gate waits for two lists, and both are set in the enable step:
  - `RequiredChecks`, from `GetBranchRulesAsync` (`MergeQueueEngine.cs:534-535, 558`);
  - `ExpectedChecks`, the checks that reported on the head pinned at enqueue (`:560-561`, merge-queue PRD D45).

  Only the enqueue reset clears them (`:463-464`). Rebases, re-runs and restarts keep them. `MergeQueueEntry` is a
  stored RavenDB document, so its properties are never renamed.
- **The gate already computes the missing names.** `CheckGate.Evaluate` puts every required or expected name that
  never reported in `Missing` (`CheckGate.cs:95-97`), and counts that as pending (`:118-122`).
- **The engine says nothing about them.** The stuck state is `Verdict == Pending` with `Pending` and `PendingSuites`
  empty and `Missing` not empty. In that state the engine's `default:` branch schedules the next poll and says nothing
  (`MergeQueueEngine.cs:1788-1790`). The check run's title stays "Waiting for checks" (`MergeQueueCheckRenderer.cs:35`).
- **The timeout message is wrong.** At the timeout, the eviction says the checks "did not finish within 60 minutes"
  (`MergeQueueEngine.cs:1806-1807`), although every check had finished.
- **The data to name the ruleset is already read.** GitHub's `rules/branches/{branch}` returns a `ruleset_id` with
  each rule, and the forge reads it (`GitHubForgeMergeQueue.cs:117-136`). `ForgeBranchRules` keeps only the merged
  `RequiredChecks` and each ruleset's rule types, so it cannot say which ruleset requires which name.
- **A re-read costs 3 or more calls.** `GetBranchRulesAsync` makes the paged rules read, one `rulesets/{id}` read per
  ruleset, and the classic-protection read (`GitHubForgeMergeQueue.cs:108-145`).
- **One forge.** `GitHubForgeMergeQueue` is the only `IForgeMergeQueue` implementation (engine investigation, not
  re-checked).

### F3. The expected list stalls an entry queued behind a rename, even with a correct ruleset

This comes from reading the code. It has not been observed.
1. Entry B is enabled with the check names of its own head, so with the old name.
2. Pull request A, the rename, merges first.
3. The queue rebases B onto the new base. B's CI on the rebased commit runs the base's workflows and reports the new
   name.
4. `ExpectedChecks` still holds the old name. It stays in `Missing` for good, and the entry ends with
   `checks-timeout` after 60 minutes.

The engine already recognises this case. The git writer reports `BaseChangedWorkflows` when the prepared commit's
`.github/workflows` differs from the pinned head's (`MergeQueueEngine.cs:1123`, `GitWriterSidecarClient.cs:378-380`).
Comment 2 says "an entry already in the queue also needs `/dequeue` and `/queue`". That is right, and F3 is a second
reason for it, besides the ruleset.

### F4. Ruleset 3 still requires the old names; `/release` is not at risk (S1)

> **Superseded by S1 (2026-11-27).** This finding first said "Ruleset 3 is likely to stall the release lines at the next
> `/release`". S1 showed that `/release` never goes through ruleset 3 (§6.1). The exposure is any pull request against
> a line after the next promotion, and a past line cut later. Both are rare today; A1 removes them for good.

- **What ruleset 3 requires.** Ruleset 27018553 ("merge-queue: release-line checks", `refs/heads/release/*-*.x`,
  updated 2026-11-12) requires `tests / Library suites` and `tests / CodeCoverage git suites` (live GET, 2026-11-27).
- **What the lines produce.** Both lines still produce those names: `release/dotnet-11.x` at `abeeb94cd` and
  `release/angular-22.x` at `55d7dfffb` (from `git show origin/<line>:.github/workflows/test.yml`).
- **Why a line PR would stall.** The queue holds pull requests on a line to ruleset 3's checks itself, even where
  the App is exempt from that ruleset (`MergeQueueEngine.cs:542-547`: "the queue waits for the required checks
  itself"). A line pull request that brings #697's `test.yml` onto a line reports `tests / Library + git suites`
  instead, so it would wait like #697 did. S1 answers whether a promotion takes that path or only later backports do.
  **S1's answer:** a promotion is a direct push, so neither path applies today (§6.1). Only a pull request opened
  against a line after the next promotion would stall.
- **Why no list of job names can fit.** One ruleset covers both lines, and the lines receive the rename at different
  times. In between, no list of job names fits both lines. Only a name that never changes does.

### F5. Spark already has a check whose name never changes: the aggregate `pull-request` job

- **The job.** `pull-request.yml:288-301`: "One check that is green only when every job is".
  - It `needs: [build, angular, tests, coverage, actions]`.
  - It runs `if: always()`.
  - It fails when any of those jobs failed, was cancelled or was skipped.
- **What it covers.** `tests` is the call to the reusable `test.yml`, so its result covers every matrix leg, SQL
  Server and PostgreSQL included.
- **Ruleset 2's 24 contexts** (live GET, 2026-11-27):
  - 21 are jobs the aggregate already covers: `build + verify`, `Angular apps`, `coverage upload`, `action bundles`
    and the 17 `tests / …` jobs.
  - The other 3 are `pull-request` itself and CodeCoverage's checks `coverage/patch` and `coverage/project`. These
    two are check runs of the CodeCoverage App (4574022) that end `neutral`, not commit statuses (S5).
  - No context pins an `integration_id`.
- **Ruleset 3** has the same shape with the old names, plus `Line policy`, which is pinned to the App (4574022).
- **What requiring only the aggregate would mean.** Rulesets that require only `pull-request`, `coverage/patch` and
  `coverage/project` (plus `Line policy` on ruleset 3) give the same protection from GitHub, and no job rename touches
  a ruleset again. The queue's own gate is not weakened: it waits for every check that reports, required or not
  (`CheckGate.cs:84-87`), and for the expected list.
- **What it costs.**
  - The aggregate's name becomes the one name that matters: renaming `pull-request` recreates #714.
  - A new job must be added to the aggregate's `needs`, or GitHub will not enforce it (the queue still does). Today a
    new job has to be added to the ruleset instead, so this is no worse.

### F6. Catching the rename inside the pull request is feasible, with one open point

- `GET /repos/{o}/{r}/rules/branches/{branch}` answers an authenticated `gh` call (live, 2026-11-27).
- Whether a workflow's default `GITHUB_TOKEN` (`contents: read`) may read it is untested (S4).
- A static test needs no API at all (D1).

## 3. Goals and non-goals

Goals:
- **G1.** Renaming, joining or splitting a CI job never stalls a pull request, on master or on a line.
- **G2.** When a required or expected check can never report, the queue says so while it waits: which names, and
  which ruleset requires each. It no longer waits 60 minutes without a word and then says the checks "did not
  finish".
- **G3.** A ruleset fixed while an entry waits frees that entry at the next poll, without `/dequeue` and `/queue`.
- **G4.** An entry queued behind a rename does not wait for the old names (F3).
- **G5.** The docs say what to do.

Non-goals and constraints:
- **The App never writes rulesets.** Administration was removed from both Apps on 2026-11-06, and GitHub has no
  narrower permission (owner; see the "#554 redesign" in `docs/coverage_backport_plan.md`). Every ruleset change stays
  with a person: the "Set up rulesets" action's one-shot OAuth token, or GitHub's UI.
- **Not GitHub's native merge queue.** The engine refuses it (`MergeQueueEngine.cs:536-537`).
- **One pull request** for everything (global rule 8). CodeCoverage is production.

## 4. Options

### Part A: Spark's own rulesets (F4, F5)

| | Option | For | Against |
|---|---|---|---|
| A1 | Rulesets 2 and 3 require only `pull-request`, `coverage/patch` and `coverage/project` (plus `Line policy` on ruleset 3) | No job rename touches a ruleset again. One list fits both lines, whatever their tree. GitHub protects exactly as much as today (F5) | The aggregate's name is the one that matters. GitHub's merge box lists 3 required checks instead of 24 |
| A2 | Keep a context per job, and change the rulesets in the same change as every rename | Nothing to do now | Cannot fit F4 (two lines, one ruleset). Every rename needs an admin at the right moment |
| A3 | One ruleset per line, each with its own list | Lines can differ | The setup writes one ruleset 3 for the pattern. More rulesets to keep in step. Still work on every rename |

**Recommendation: A1.** The owner applies it, through "Set up rulesets" or GitHub's UI. There is no deadline (S1).

> **Superseded (2026-11-27):** "applied before the next `/release` (S1 says how urgent)". S1 found no release risk.

**A1 hardening: pin each context to its App.** Under A1, the whole of GitHub's protection rests on one name. Pinning
`pull-request` to GitHub Actions (`integration_id` 15368) and the two coverage checks to the CodeCoverage App (4574022)
means no other App can satisfy them. The App ids were read from #697's head (S5), and `Line policy` is pinned this way
already. Re-running the setup keeps a pin: the planner keeps the `integration_id` of a check that is already required
(`MergeQueueRulesets.cs:107, 373-378`).

### Part B: the setup (direction 1, F1)

- **B1.** The confirmation names, per ruleset, the checks it adds and the checks it drops. Today it says only "rule
  changed: required_status_checks". *Recommended.*
- **B2.** The form marks required checks that no pull request reported in the last N days. `KnownChecks` keeps
  `LastSeenUtc` and `OnPullRequest` for each name. *Recommended.*
- **B3.** The form's help text recommends requiring one aggregate check, and says why. That brings A1 to every
  onboarded repository, not only Spark. *Recommended.*
- **B4.** Prefill the form from a chosen pull request's head, the issue's first example. *Not recommended:* typed
  names already work (F1), and with B2 and B3 a rename rarely needs the setup.

### Part C: the engine (directions 2 and 4; F2, F3)

- **C1. Detect the stuck state:** `Pending` and `PendingSuites` empty, `Missing` not empty (F2).
- **C2. Re-read the rules at that point** (comment 1). Call `GetBranchRulesAsync` again. If `RequiredChecks`
  changed, replace it, note the change on the entry, and evaluate again. At most once per N minutes per entry, since a
  read is 3 or more calls (F2). *Recommended.*
- **C3. Name the deadlock.** *Recommended.*
  - The check run's title becomes "Waiting for checks that never reported".
  - Its summary lists each missing name with where it comes from: the ruleset (name and id) that requires it, or
    "reported on the queued head" for an expected name.
  - The timeout eviction says the same, instead of "did not finish".
  - `ForgeBranchRules` has to keep each ruleset's required checks for this. The `ruleset_id` is already read (F2).
- **C4. Drop stale expected names (F3).** When `BaseChangedWorkflows` is set and every suite of the App that
  reported the name has completed, a missing expected name is dropped, with a note. Names from a commit status or
  from another App are still waited for. S3 settles the exact rule. *Recommended, subject to S3.* **S3's rule** is
  in §6.1. It adds one condition: the commit must have been stuck at the previous poll as well.
- **C5. Evict early** instead of waiting for `checks.timeout`. *Not recommended.* A workflow started by
  `workflow_run` can start after the first suites complete, and external statuses arrive on their own (`CheckGate.cs:18-25`
  describes what `RunsFollow` already covers). With C2 and C3, the wait is visible, and it ends as soon as someone
  fixes the ruleset.

### Part D: catching the rename inside the pull request (F6)

- **D1. A static guard test, with no API.** It parses `pull-request.yml` and asserts that the aggregate is named
  `pull-request` and `needs` every other job. Under A1 this is the whole contract. *Recommended.*
- **D2. A dynamic CI step.** It reads `rules/branches/<base>` with the default token and fails when a required
  context is not produced at this head. It needs S4, and it must work out the matrix jobs' names. *Not recommended
  under A1.*
- **D3. A hint from the queue.** When C1 fires and the pull request's own diff touches `.github/workflows`
  (`entry.WorkflowPaths`, `MergeQueueEngine.cs:1124`), C3's summary adds: "this pull request changes workflows: a
  renamed job may still be required by the ruleset". It is cheap. *Recommended, together with C3.*

### Part E: docs (direction 3)

- **`CLAUDE.md`**, "Running the test suites": the "CI layout" paragraph and the bullet on 20 concurrent jobs. Say
  that the rulesets require only the aggregate, and that renaming `pull-request` (or a coverage check) means
  changing rulesets 2 and 3 first.
- **`docs/code-coverage/merge-queue.md` §3**, "Recommended GitHub setup":
  - replace the stale lines 598-599;
  - recommend one aggregate check;
  - add a troubleshooting entry, "a required check never reports": what C3 shows, and what to do.
- **`docs/code-coverage/onboarding.md`**, "Left to you": the aggregate recommendation.

## 5. Questions for the owner

| # | Question | Recommended | Risk if the answer is wrong |
|---|---|---|---|
| Q1 | Should Spark's rulesets 2 and 3 require only `pull-request`, `coverage/patch` and `coverage/project`, plus `Line policy` on ruleset 3 (A1)? And should each be pinned to its App (A1 hardening)? | Yes to both, with no deadline (S1) | Without it, every rename needs a ruleset edit at the right moment, and a pull request against a line after the next promotion stalls (F4) |
| Q2 | In the stuck state, re-read the rules and name the deadlock while keeping the 60-minute timeout (C2, C3), or also evict early (C5)? | Re-read and name it; no early eviction | A 60-minute wait remains when nobody fixes the ruleset, but now it says why |
| Q3 | Drop stale expected names after the base changed its workflows (C4)? | Yes, with the rule from S3 | Too loose: an entry merges before a late workflow reports. Too strict: F3 stays |
| Q4 | Setup: B1, B2 and B3, but not B4? | Yes | None worth noting |
| Q5 | Guard: the static test (D1) and the queue's hint (D3), without the dynamic CI step (D2)? | Yes | Without D1, a renamed aggregate slips through |

## 6. Spikes

| # | Question | Where | Done when |
|---|---|---|---|
| S1 | How do a promotion and a backport reach `release/*-*.x`, and which list holds them? Confirm F4: does the first line pull request that carries #697's `test.yml` wait for `tests / Library suites` for good? Does a promotion go through the queue's gate at all, or does the exempt App push it directly, so that only later backports stall? | This repo, read-only (`MergeQueueEngine.cs:542-547` and the promote path), plus GETs on the lines' latest pull requests | A trace with file:line, and an answer to "must ruleset 3 change before the next `/release`?". **Done 2026-11-27: no (§6.1).** |
| S2 | End-to-end repro on a sandbox. A ruleset requires `ci / job-a`; pull request 1 renames that job to `job-b`; pull request 2 is queued behind it. Before the fix, both stall (F2, F3). After C2–C4 (dev App), fixing the ruleset frees pull request 1 within one poll, and pull request 2 drops `job-a` with a note. Under A1 (only the aggregate required), pull request 1 merges without any ruleset change | `PieterjanDeClippel/SparkTest` with the dev App. This writes to GitHub, so it needs the owner's approval first | The four outcomes recorded, with check-run titles and timestamps |
| S3 | The rule for C4: which signals prove that an expected name is gone for good? Candidates: `BaseChangedWorkflows`; the name's App (`KnownChecks` keeps `AppSlug` for each name); every suite of that App completed on `FinalSha`; `RunsFollow`. What happens with a workflow started by `workflow_run`, and with a commit status? | This repo, read-only, then `CheckGate` table tests | The rule written down, with a test for each case, including the two "keep waiting" cases. **Rule done 2026-11-27 (§6.1); the tests belong to M2.** |
| S4 | Can a workflow's default `GITHUB_TOKEN` (`contents: read`) read `GET /repos/{o}/{r}/rules/branches/{b}` on a public repository? | A throwaway workflow run on SparkTest | Answered, with the response. **Only if Q5 picks D2.** |
| S5 | Does today's aggregate already cover everything ruleset 2 requires, on a real head? Compare the check runs of a recent master pull request's head with the aggregate's `needs` and the coverage statuses | This repo, plus GET `commits/<sha>/check-runs` | An empty "required but not covered" list, so A1 loses nothing. **Done 2026-11-27: empty (§6.1).** |

### 6.1 Results (2026-11-27)

**S1: `/release` does not go through ruleset 3 or the queue.**
- `/release` (promote mode) does three things:
  - it picks the newest default-branch commit whose `versioning.lines.workflow` run succeeded;
  - it gets the App's writer access (`pushesWorkflowChanges: true`);
  - it has the git writer's `PromoteService` push the promotion straight to the line.

  Evidence: `PackageVersioning/ReleaseRecipients.cs:43-57, 179-196`.
- Ruleset 3 exempts that App: `bypass_actors` = actor 4574022, `bypass_mode: exempt` (live GET of ruleset 27018553).
- The last promotion went this way: `abeeb94cd` "Promote release/dotnet-11.x to master fc95abe3820e", 2026-11-18,
  followed by that day's package tags (`MintPlayer.Spark.Templates/v11.0.0` and others).
- **No pull requests reach a current line.** A `backport:` label on a current line opens no pull request: the bot
  answers that the change ships with the next `/release` (seen on #696, 2026-11-19). The latest pull requests against
  either line are from 2026-11-07 to 2026-11-09 (#581–#610), before the lines switched to promote mode.
- **So the exposure is:**
  - a pull request opened against a line by hand after the next promotion, which brings #697's `test.yml` there;
  - a past line (`dotnet-10.x`, `angular-21.x`) cut later, whose tree has other job names than the current lines
    under the same ruleset 3.

  A1 covers both, so A1 is no longer urgent.

**S3: the rule for dropping a stale expected name (C4).** A missing expected name is dropped, with a note on the entry
and in the check run, only when all of these hold:
1. `entry.BaseChangedWorkflows` is set. The base changed `.github/workflows` since the head pinned at enqueue. Without
   that, a name that reported on the pinned head can only be late, never gone. The pull request's own workflow change
   is already in the pinned head's CI.
2. The name was a check run of GitHub Actions (`github-actions`). A commit status, or another App's check run (an
   external scanner, CodeCoverage's own checks), is never dropped: it may report late, and C3 names it instead.
3. The gate's stuck state holds: no pending check, no pending suite (`RunsFollow` included), only `Missing`.
4. The same stuck state held at the previous poll too, one `ChecksPollInterval` (2 min) earlier. This covers a
   workflow that `workflow_run` starts: its suite appears only once the run that triggers it has completed, so for a
   moment every suite on the commit can look complete. This repository has no such workflow (no `workflow_run`,
   `check_suite` or `repository_dispatch` trigger under `.github/workflows`), but other repositories on the queue may.

A required name (from a ruleset) is never dropped: GitHub would refuse the merge anyway. For it, the fix is C2's
re-read.

Implementation constraints found:
- `ExpectedChecks` stores names only. `GetCheckNamesOnCommitAsync` returns the names of check runs and statuses,
  without their App (`GitHubForgeMergeQueue.cs:352-365`).
- Condition 2 therefore needs each expected name's App, captured at enable time. Two ways:
  - return `ForgeReportedCheck` (name, kind, App; `IForgeMergeQueue.cs:383`) from that call;
  - read `Repository.KnownChecks`. That list may have dropped old names (`ForgeCheck.MaxKnown`), so it is the weaker
    source.
- `MergeQueueEntry` is stored. A new property is safe. Changing the type of `ExpectedChecks` would change its stored
  format, which needs a RavenDB data migration (feedback: write a migration, don't freeze the format).
- Condition 4 needs a "stuck since" time on the entry: one more new property.

The tests (M2), one per case:
- each of the four conditions failing alone, and the entry waits;
- all four holding, and the name is dropped and the entry merges;
- the two "keep waiting" cases: a commit status, and another App's check run.

**S5: nothing ruleset 2 requires falls outside the aggregate.** Head `6b25997d7` (#697's last head, merged) has 36
check runs and no commit status:
- 30 from `github-actions` (App 15368):
  - the 21 per-job contexts ruleset 2 requires, every one of them in `pull-request.needs`;
  - `pull-request` itself;
  - 8 "Build the … image (no push)" jobs from path-filtered workflows. Ruleset 2 does not require those today, and the
    queue waits for them anyway, because it waits for everything that reports (`CheckGate.cs:84-87`).
- 5 from `coverageproduction` (App 4574022): `coverage/patch` and `coverage/project` (both `neutral`), `Merge queue`,
  `PR size` and `Backport targets`.
- 1 from GitGuardian, which ruleset 2 does not require.

So the "required but not covered" list is empty, and A1 loses nothing. One correction to F5: the coverage checks are
check runs, not commit statuses. Evidence: GET `commits/6b25997d7…/check-runs` and `…/status`, 2026-11-27.

## 7. Milestones (one pull request)

- **M0.** S1, S3 and S5 are done (§6.1). The owner answers Q1–Q5. S2 and S4 only after approval.
- **M1. Spark.** The owner changes rulesets 2 and 3 (Q1). Add the D1 guard test and the `CLAUDE.md` CI-layout note.
- **M2. Engine.**
  - `ForgeBranchRules` keeps each ruleset's required checks, in the GitHub forge and in the fake.
  - C1 detection, C2's throttled re-read, and C4 as S3 settles it.
  - C3's title, summary and timeout message, and D3's hint.
  - Tests in `MergeQueueEngineTests` (the fake's `Rules` and `CheckStates` can change between polls) and
    `GitHubForgeMergeQueueTests`.
- **M3. Setup.** B1's list of added and dropped checks, B2's stale flag, B3's help text. Tests in
  `MergeQueueRulesetPlannerTests` and `SetUpRulesetsActionTests`.
- **M4. Docs.** `merge-queue.md` §3 with its troubleshooting entry, and `onboarding.md`.
- **M5.** Run the touched test classes once, at the end. The owner deploys. Then the "after" half of S2 on SparkTest.

## 8. Until this ships: the workaround

From comment 2, with F3 and F4 added.
- When a pull request renames, joins or splits a required job, update ruleset 2 by hand to the new names first. Do the
  same for ruleset 3 when the change reaches the lines (F4). That only matters for a pull request against a line,
  because `/release` pushes past ruleset 3 (S1).
- Then `/dequeue` and `/queue` every pull request that is already queued. Its entry holds the old required list (F2)
  and the old expected list (F3).
- Other open pull requests: their finished CI carries the old names. The new name appears with their next CI run (a
  push, a re-run or the queue's rebase), because a `pull_request` run takes its workflows from the merge with the
  base.

## 9. Log

- **2026-11-27.** Read issue #714 and both comments. Ran three read-only investigations: the setup, the engine, and CI
  with the rulesets. Checked F1–F6 again against the code, the lines' `test.yml`, and the live rulesets 26719109 and
  27018553. Wrote this file.
- **2026-11-27, later.** Ran spikes S1, S3 and S5 (read-only, §6.1):
  - S1 found that `/release` is a direct push by the exempt App, so F4 has no deadline (marked superseded).
  - S5 found nothing ruleset 2 requires outside the aggregate, and corrected F5 (the coverage checks are check runs).
    It also gave the App ids behind A1's hardening.
  - S3 wrote C4's rule.

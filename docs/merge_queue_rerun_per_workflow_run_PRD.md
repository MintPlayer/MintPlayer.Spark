# Merge queue: re-run failed checks once per workflow run (#589)

Issue #589. A bug fix with one pull request. This file is the plan, the decisions and the log.

## 1. The defect

When checks fail and `checks.retryFlaky` allows another try (D9), `MergeQueueEngine.EvaluateChecksAsync` re-ran
each failed check run through `IForgeMergeQueue.RerunCheckAsync`. On GitHub, an Actions check run is a job, and that
call was `POST /actions/jobs/{id}/rerun`.

When two failed jobs belong to one workflow run, the first re-run starts attempt 2 of the **whole run**. GitHub then
refuses the second one with `403 The workflow run containing this job is already running`. `RerunCheckAsync`
read every 403 as a missing `actions: write` permission and threw `ForgeAccessDeniedException`, so the queue
evicted with "The forge refused a call the queue needs (it asks for `actions=write`)". The real failure, a red test,
never reached the pull request.

**Evidence (PR #586, 2026-11-07).** The table comes from `gh api …/actions/runs/{run}/attempts/1/jobs` and `…/actions/jobs/{id}`:

| Eviction | Run | Failed jobs, attempt 1 | Job the 403 named | Run attempts |
|---|---|---|---|---|
| 06:07:44Z | 42344913315 | CodeCoverage.Tests, build + verify, Spark.Tests 4/4, `pull-request` | 128320601538 = Spark.Tests 4/4 | 2 |
| 06:57:37Z | 42348601700 | CodeCoverage.Tests, `pull-request` | 128331842364 = CodeCoverage.Tests | 2 |

Attempt 2 exists in both runs: the queue's first re-run started it.

The existing D9 guard (wait until no check or suite is pending, added after PR #579's 403 on 2026-11-06) is not at
fault. Both runs had completed before the queue re-ran their jobs. PR #579's 403 was the original run still in
progress; this one is a run the queue itself restarted a moment earlier.

**Same pattern elsewhere.** `DependencyUpdateEngine`'s bisect pre-run (old §7.4) re-ran each failed check run
through the same call, best effort. It silently re-ran only the first failed job of each workflow run.

## 2. Decisions

- **D1. The forge re-runs a set of failed check runs, not one.** `IForgeMergeQueue.RerunCheckAsync(checkRun)`
  becomes `RerunChecksAsync(checkRuns)`, which returns how many of them are running again. Grouping jobs into
  workflow runs is GitHub's model, so it stays inside `GitHubForgeMergeQueue` (the callers keep passing failed
  check runs). The old method is removed outright, not kept as an overload (preview, no backward compat).
- **D2. GitHub: one `POST /actions/runs/{id}/rerun-failed-jobs` per workflow run.** It re-runs every failed and
  cancelled job of the run, plus the jobs that depend on them, in **one** new attempt (GitHub's REST docs, "Re-run
  failed jobs from a workflow run"). `pull-request`, the aggregate job that `needs` the test jobs, re-runs as well. Another App's
  check run is still re-requested one by one (`POST /check-runs/{id}/rerequest`): those have no shared run.
- **D3. The run id comes from the check run's `details_url`**, `https://github.com/{owner}/{repo}/actions/runs/{run}/job/{job}`.
  Verified on this repository's own check runs, 2026-11-07, for example
  `…/actions/runs/42348601700/job/128333608393`. When the URL does not parse, it comes from the check suite: an
  Actions check suite is one workflow run (check suite 114738795090 = run 42348601700, same listing), and
  `GET /actions/runs?check_suite_id={id}` returns it. When neither is available, the job is re-run alone, as before.
- **D4. A 403 "already running" means "cannot re-run now", not a missing permission.** It returns false (that set
  is not re-running) and logs, on both the job and the run endpoints. A real missing permission still throws
  `ForgeAccessDeniedException`: GitHub's message for that is "Resource not accessible by integration", together with
  `X-Accepted-GitHub-Permissions`. If nothing else re-runs, the queue then evicts with `checks-failed` and names the
  real failed checks, which is the honest outcome.

## 3. Milestones

1. Interface and GitHub implementation (D1–D4). Engine and dependency-saga callers. Fake.
2. Tests:
   - `GitHubForgeMergeQueueTests`: two jobs of one run give one `rerun-failed-jobs` call; jobs of two runs give
     two calls; the suite fallback; the job fallback; another App's check is re-requested; "already running" is
     false; a missing permission throws.
   - `MergeQueueEngineTests`: the existing re-run tests on the new method.
   - `DependencySagaTests`: the pre-run passes every failed run at once.
3. Docs: `docs/code-coverage/merge-queue.md` (the `retryFlaky` row and the "failed check run" row).

## 4. Log

- 2026-11-07: defect found on PR #586's two evictions, root cause confirmed from the runs' attempt listings (§1).
- 2026-11-07: M1–M3 done. Targeted run of `GitHubForgeMergeQueueTests`, `MergeQueueEngineTests`, `DependencySagaTests`
  and `ForgeIntegrationConformanceTests`: 306/306 green.
  - New tests: one call per workflow run (4 check runs give 3 calls); the suite and job fallbacks; the URL parser;
    "already running" counts 0 while a missing permission still throws; the engine passes the failed set in one call.
  - Superseded text is marked in `coverage_merge_queue_PRD.md` D48.

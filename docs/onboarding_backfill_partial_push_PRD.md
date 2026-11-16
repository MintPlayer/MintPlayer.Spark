# History backfill: a partly refused tag push — PRD and plan

Second follow-up of #638 (after `docs/onboarding_merge_backfill_PRD.md`, PR #640), found when Tools #56 merged on
2026-11-12. One PR.

## 1. What happened

Tools #56 merged at 15:08 UTC. The merge handler ran the history backfill with the 29 tags the owner chose to move.

| Fact | Evidence |
|---|---|
| 42 of the 87 planned tags reached GitHub, all in one push (tagger date 15:08:58). | `git for-each-ref` on a fresh fetch of Tools |
| 45 declared versions are still untagged (9.0.0 @1034d2b ×12, 10.0.0-rc.1 @9bab92d ×12, 11.0.0 @d60f87e ×14, 11.0.1-rc.0 @5ddf048 ×3, 9.1.x ×4). | `git ls-remote --tags` vs the first-parent history |
| None of the 29 tags moved: 14 still at 8b1472c, 15 at ea63582. | `git ls-remote --tags` |
| The planner is right: `HistoryBackfill.PlanAsync` on a local copy of Tools plans exactly the 45 tags and the 29 collisions, with no notes. | a file-based `dotnet run` of the planner, 2026-11-12 |
| The writer threw on every attempt: `GitException: git push did not report MintPlayer.AspNetCore.ChangePassword/v10.0.0-rc.1: error: failed to push some refs`, at `VersioningRunService.cs:121`, and the merge message was retried with the same result. | `docker compose logs coverage-git-writer`, 15:05-15:20 UTC |
| GitHub's per-tag reason is lost: `TagWriter.PushAsync` reads the porcelain lines but keeps only the ref names. | `TagWriter.cs:150-176` |

So GitHub refused part of one non-atomic push. `TagWriter` turned the refusal into an exception, the job aborted
before the moves (`VersioningRunService.cs:127`), and the App's note never said which tags or why.

**Wrongly ruled out at first:** the token's missing `workflows: write`. The argument was that accepted tags sit on
commits that change workflow files too (ae97c2c) and that the push brings no new commits. The deployed fix disproved
it (§6). Rightly ruled out: tag rulesets (Tools has none, and org rulesets need GitHub Team) and releases holding the
names (none of the 45 has one).

**Why the tests missed it:** the App's tests fake the git writer (`FakeGitWriter` scripts every outcome), and the writer's
real-git tests push to a remote that accepts everything. No test had a remote that refuses some refs, and no test ran
the App's merge path against the real writer.

## 2. Decisions

- **D1. A refused tag is an outcome, not an exception.** `TagWriter` gains `TagOutcome.Rejected` (wire `rejected`), with
  the remote's own summary from the porcelain line as `Detail`. The other tags of the push keep their outcomes. A tag
  the push did not report at all still throws: that is a broken git, not a refusal.
- **D2. The moves run whatever the writes did.** They are independent refs, each with its own lease.
- **D3. A refused move says so.** `TagMover` reports the remote's summary verbatim; "changed since it was read" is right
  only for a failed lease (`stale info`).
- **D4. The App's note already lists every tag that was not created, with its detail** (`BackfillCoreAsync`, "not
  tagged: …"), and marks the merge handled, so a refusal is reported once instead of retried forever.

## 3. Milestones (TDD: red first)

- **M1 (D1-D3), writer:** red: `VersioningBackfillTests` — the remote's `update` hook refuses one planned tag. Expected:
  the job answers `tagged`; the refused tag is `rejected` with the hook's message; the other tag is `created`; the chosen
  move is `moved`. Today: the job fails with "did not report".
- **M2 (D4), end to end:** red: `OnboardingBackfillEndToEndTests` (CodeCoverage.Tests, on `CoverageRavenTest`) —
  `OnboardingService.MergedAsync` with the real git writer (its `GitWriterHost` on a TestServer, as
  `DevelopmentGitWriter` runs it) against the local smart-HTTP git server. A Tools-shaped history: a default branch and
  two release lines, several versions per package, App-made tags to move, and an `update` hook that refuses one tag.
  Expected: every other version tagged on its line, the chosen tags moved, the note names the refused tag and the hook's
  reason, `BackfilledAt` set.
- **M3:** docs (`docs/code-coverage/versioning.md`, the backfill paragraph), this file's status.

## 4. After the deploy

Run Set up features on Tools again and choose **Move them**: with nothing to change it backfills at once and shows the
note, which now names GitHub's reason for any tag it refuses.

## 5. Status

- ✅ **M1.** Red observed: both new `VersioningBackfillTests` failed, the first with the production error
  (`GitException: git push did not report Lib/v1.0.0 … failed to push some refs`), the second with "Core/v1.0.0 changed
  since it was read ([remote rejected] (hook declined))". Green with D1-D3. The fixture's `Origin.Refuse` installs a
  per-ref `update` hook, since a `pre-receive` hook refuses a whole push and GitHub refused only part of one.
- ✅ **M2.** `OnboardingBackfillEndToEndTests`: green with the fix. Red observed with the M1 changes stashed: the same
  `git push did not report Acme.B/v11.0.0` as production. The writer's git server and fixtures are linked into
  CodeCoverage.Tests (Debug only, like the app's writer reference); `InternalsVisibleTo` lets them reach
  `JobRunner.DeleteTree`. `rejected` also reaches the local-mode CLI (`Wire.cs`) and the action's type union, a
  type-only change that leaves the committed `dist` unchanged.
- ✅ **M3.** `docs/code-coverage/versioning.md`: a refused tag is reported with the forge's reason, and the rest lands.
- ✅ PR #641, merged and deployed 2026-11-12.

## 6. GitHub's reason, and the token (second PR)

Set up features on Tools after #641's deploy (2026-11-12, 16:09 UTC) reported every refusal with GitHub's words:
`[remote rejected] (refusing to allow a GitHub App to create or update workflow '.github/workflows/publish-release.yml'
without 'workflows' permission)` (and `pull-request.yml`), for the new tags and the 29 moves alike.

| Fact | Evidence |
|---|---|
| The App holds `workflows: read and write` on both the dev and the production App. | owner, 2026-11-12 |
| Every App tag push asked for a contents-only token: `GetWriterAccessAsync(repository, null, false, …)`. | `VersioningOnboardingService.cs:159,189`, `VersioningController.cs:313,374`, `ReleaseVersions.cs:133`, `ReleaseRecipients.cs:142` |
| `workflows: write` is added to the job token only when the caller passes `pushesWorkflowChanges: true`. | `GitHubForgeMergeQueue.cs:1370` |

- **D5. Every App push of tags or of a line asks for `workflows: write`.** A tag can sit on any commit of the history,
  and a promotion fast-forwards a line onto default-branch commits that may change workflow files; the App cannot know
  in advance which ones GitHub checks. The six call sites above pass `pushesWorkflowChanges: true`.
- **M4 (D5):** red: `VersioningControllerTests` (run, seed tags, release) and `ReleaseFlowTests` (promotion, hand tags)
  expect a workflows token; `OnboardingBackfillEndToEndTests` hands the origin's credential only to a workflows token,
  as GitHub does. Red observed: 11 failures. Green with D5.

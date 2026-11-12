# Onboarding merge: the history backfill without seeds — PRD and plan

Follow-up defect of #638 (PR #639, merged as `3f3829c5a`), found on its first production use, 2026-11-12. One PR.

## 1. What happened

Set up features on MintPlayer.AspNetCore.Tools (already versioned) showed the #638 D9 prompt "Move 29 version tag(s)?";
the owner chose **Move them**. The plan had files, so the App opened onboarding pull request Tools #56 and stored the answer
in `VersioningOnboardingState.MoveTags`. Its body lists every package as "already tagged": the plan has **no seeds**.

When #56 merges, `MergedAsync` calls `PushSeedsAsync`, which returns at once unless `SeedsPending`
(`SeedCommit is null && Seeds.Count > 0 && PullRequestNumber is not null`, `VersioningOnboardingState.cs:63`). The history
backfill and the 29 moves would never run. Evidence: Tools' tags still at `8b1472c`/`ea63582` after the PR opened
(`git ls-remote`, 14:25 UTC); the body of #56; `VersioningOnboardingService.cs:62`.

The #638 tests missed it: every merge-path test had a seed (the fixture's csproj declares 11.2.0 and nothing tags it,
`OnboardingServiceTests.cs:29`, `Collisions_D9…` at :228).

## 2. Investigation (three read-only investigators + two spikes, 2026-11-12)

| # | Finding | Evidence |
|---|---|---|
| G1 | Zero seeds on the merge path → no backfill, no moves. | above |
| G2 | A versioning run that beats the merged webhook calls `PushSeedsAsync` itself (#153 D17) and so backfills **before** the line cuts, against #638 D7. The webhook's `MergedAsync` then finds nothing pending. | `VersioningController.cs:159`; `OnboardingService.cs:262-266` |
| G3 | The no-files path (`OnboardingService.cs:156-169`) never opens the line-workflow pull requests; only the merge path does (`:268`). A repository whose re-run has nothing to change gets its lines cut but not their regenerate workflows. | both paths side by side |
| — | `MoveTags` is never cleared; the next pull request overwrites it (`:233`); a stale value is harmless once handled. | `OnboardingService.cs:233` |
| — | Widening the gate cannot loop: with no seeds `TagAsync` is skipped and the method always reaches its "done" marker; a failed backfill only adds a note. | `VersioningOnboardingService.cs:72-82` |
| — | The backfill is a no-op for a repository without a `versioning:` section, so running it is safe for any onboarding. | `RunBackfillAsync`, `:138` |

**S1 (lock).** `VersioningSeedsRecipient` takes the run lock and calls `MergedAsync`, which takes none; a versioning run
already holds it. The run *could* call `MergedAsync`, but that puts the cuts, workflow and narrowing pull requests (up to
500 file reads) inside a CI request. Rejected for D2.

**S2 (line workflows on the no-files path).** `LineWorkflowsAsync` reads only the default branch's `coverage.yml` and the
lines' heads (`OnboardingService.cs:337-353`): nothing merge-only. It can run on the no-files path as is.

## 3. Decisions

- **D1. The merge backfills whenever it is asked for, seeds or not.** The state gets `Backfill` (set when an onboarding
  pull request opens, from now on) and `BackfilledAt` (the merge commit it ran for, reset with `SeedCommit` when a pull
  request opens). `BackfillPending` = `BackfilledAt is null && PullRequestNumber is not null && (Backfill || MoveTags.Count > 0)`.
  `MoveTags` is in the condition so the state Tools #56 already holds (written by the deployed code, without `Backfill`)
  is honoured without a re-run. States from before #638 have neither, so a deploy never starts a backfill nobody asked
  for. *Why not drop the `Seeds.Count` test alone:* every pre-#638 state with an unhandled pull request would backfill on
  its next run.
- **D2. Only the merge handler backfills while line cuts are pending.** `PushSeedsAsync(repository, afterCuts)`:
  `MergedAsync` passes `true`; the versioning run and `OnboardAsync`'s pre-step pass `false`, and then skip the backfill
  while `MergeHandledAt is null && Cuts.Count > 0`. They still push the seeds (#153 D17 unchanged), so a run never waits
  on the webhook. The cost, accepted: in that race the seeds are pushed before the backfill could cover them, as before
  #638 (a seed the history places earlier then shows up as a collision on the next Set up features).
- **D3. The no-files path runs the merge path's steps in the merge path's order:** settings and renames, cuts,
  backfill, line-workflow pull requests (when Release lines is chosen), narrowing.

## 4. Milestones (TDD: red first)

- **M1 (G1, D1):** red: `OnboardingServiceTests` — a re-onboarding whose package is already tagged (no seeds) with
  `MoveTags`, merged → one non-plan backfill request with the moves, no tag request, the note says "moved". Second red:
  a pre-#638 state (no `Backfill`, no `MoveTags`, no seeds) merged → no backfill. Green: D1.
- **M2 (G2, D2):** red: a merged onboarding with a pending cut, `PushSeedsAsync(afterCuts: false)` (the run's call) →
  no backfill request, seeds pushed; then `MergedAsync` → the backfill runs after the cut. Green: D2.
- **M3 (G3, D3):** red: a no-files re-run with Release lines whose line misses the default branch's regenerate workflow
  → a workflow pull request into the line; the backfill runs before it. Green: D3.
- **M4:** docs (`docs/code-coverage/versioning.md` onboarding paragraph, `onboarding.md` on-merge), this file's status.

## 5. After the deploy

Merge Tools #56. Expect its "merged" note to say "moved 29 tag(s) onto their earliest occurrence" and the history
backfill's tags; then continue `docs/release_line_first_release_PRD.md` §6 step 2.

## 6. Status

- [ ] M1 · [ ] M2 · [ ] M3 · [ ] M4 · [ ] targeted runs · [ ] PR, queued

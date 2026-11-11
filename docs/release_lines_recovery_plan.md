# Release lines: recovery first, then small segments — PRD and plan

Status: **draft, 2026-11-11.** Replaces the one-PR scope of `docs/coverage_backport_resilience_PRD.md` (issue #621).
That PRD keeps the research (§1, §1b, S4 replay) and the grilling decisions (§4 "Decided in the grilling session");
this file splits the work into small segments, each its own PR, and puts repairing the repository first.

Owner, 2026-11-11: "Improving the merge-queue just like that seems to be too complex for a first-go. … split the
entire work into multiple smaller segments … We probably also have to rectify the missteps on this github repository
… (probably first job)."

Evidence below comes from three read-only investigations on 2026-11-11 (raw output in the session scratchpad
`audit/`), checked against the code where cited.

## 1. The repository today (audit, 2026-11-11)

| # | Finding | Evidence |
|---|---|---|
| A-1 | **Lines miss most master commits.** Master has 22 commits after the cut 5fd324b13. `release/dotnet-11.x` misses 17 (among them #620, #622, #619, #616/#617, #614, #612, #611, #608, #605, #603, #607, #596, #593, #587-#589); `release/angular-22.x` misses 21 (among them #609, #620, #611, #608, #605, #593). Some may be legitimately irrelevant to a line (parked Q11 decides). | `git cherry` per line |
| A-2 | **Losses are silent.** Only 9 backport PRs ever existed, all merged (#581, #582, #584, #585, #590, #592, #595, #597, #610). There are no open backport PRs and no `backport/*` branches. #620 and #593 carry a `backport:` label, and #608 carries both, but none of them has a backport PR or a `backported:` label. | `gh pr list --search head:backport/` |
| A-3 | **Both lines carry a "Set up rulesets (#554)" backport whose patch differs from master's** (a9743b376 = #592 on dotnet, c2e5e9e32 = #590 on angular, master 3121cdda9 = #586); its follow-up #587 (194fe8b3a, "App's own backports pass the approval floor") never reached a line. | patch-id mismatch |
| A-4 | **`release/dotnet-11.x` is red**: `publish-release` run 42622887029 at da8be3a91 (#610, 2026-11-09) failed in `tests / E2E 2/2` (Playwright `TimeoutException` 30000 ms), so that commit was never released. | `gh run list --branch` |
| A-5 | **Line PRs have no required checks.** Ruleset 26719109 "merge-queue: reviews and checks" (active again) targets only `~DEFAULT_BRANCH`. 26211184 "restrict updates" covers the default branch and `release/*-*.x`, exempting the queue App. `pull-request.yml` already runs on `release/**` (`:6`), so the checks exist on a line PR; only the requirement is missing. | `gh api …/rulesets` |
| A-6 | **Tags are consistent.** 70 tags since 2026-11-06, no duplicates, no package tag on a master-only commit; `versioning-v1` is reachable from nothing. Issue #600 (the pack stamps a higher major's tag) does **not** apply to Spark's lines: no higher major is reachable from them (#600's own text). | `git tag --merged` |

## 2. Segments (each one PR, in this order)

**Segment R, recover the repository (first job).** Mostly operations, not code.
1. **R1** Diagnose A-4 (spike SP1). The fix lands on master and reaches the line with the catch-up.
2. **R2** Require the PR checks on the lines (A-5): extend 26719109's target to `release/*-*.x`, or let "Set up
   rulesets" do it. SP2 first confirms which of its 23 checks a line PR actually reports (the 2026-11-06 defect: default
   checks taken from master's push-only jobs made PRs unmergeable).
3. **R3** Reconcile A-3: diff a9743b376 / c2e5e9e32 against 3121cdda9 and decide #587 (SP3).
4. **R4** Catch the lines up, in master order (SP4 replays it on today's tips first). Content and mechanism are open
   (O2, O3).
5. **R5** The first green line run releases; check that the versions it tags follow from the last line tags.

**Segment B, no silent losses.** Every `backport:` target ends in an open backport PR (a draft on conflict) or a
recorded, visible failure (comment on the source PR and a row in the app). The source merge sha is stored on
`BackportPullRequest` (today it stores none, `CodeCoverage.Library/Entities/BackportPullRequest.cs:24-43`). SP5 finds
why #593, #608 and #620 produced nothing, although the conflict path does open drafts
(`CodeCoverage/PackageVersioning/BackportRecipients.cs:243-252`).

**Segment C, hold instead of evict on lines.** This is the owner's "Blocking or Evicting" (see O1).
- **What happens to the PR:** a red or pushed-over backport PR on a line goes to a new `Held` state instead of
  `EvictAsync` (`CodeCoverage/MergeQueue/MergeQueueEngine.cs:1647-1662`). `lane.Current` is kept, and the held PR
  resumes on a push or on green checks (HeadPushed handling, `:129-143`).
- **What stays as it is:** the decision is per PR by base branch (`MergeQueueBranches.IsLine`, `:1021`, and
  `IsAppsOwnBackportAsync`, `:546`), so other PRs keep being evicted.
- **Order:** backports are inserted in master order (`:317`) by the stored source sha from B.

**Segment D, backport branches protected, no approval.**
- **The ruleset:** a new ruleset on `refs/heads/backport/*` that only the queue App and repository admins can update
  (`CodeCoverage.Library/Forge/MergeQueueRulesets.cs:107-179`, `MergeQueueRulesetSetup.cs`).
- **No approval:** with that ruleset in place, the approval floor no longer applies to the App's backports
  (`MergeQueueEngine.cs:494-503`, `QueueApprovedBackportAsync` `:555-583` removed).

**Segment E, "Line policy" check.** An App-bound required check on `release/*`, judged from the SDK majors:
current lines take only bot picks, past lines take human PRs (grilling Q1b, Q1c, Q1d).

**Segment F, attention.**
- **Who is asked:** one auto re-run, then the author, with the code owners as fallback; a reminder after 2 working
  days.
- **What they see:** a comment linking to a CustomQuery of held PRs, a `backport: needs attention` label, and a
  "Line blocked by #N" check on the waiting PRs (Q8, Q12).
- **Order:** needs C.

**Segment G, trial pick.** `trialPick: warn | refuse | off` per line in `coverage.yml` (`BackportYml.cs`), default
warn (Q10).

**Segment H, CI cache.**
- **Cache writes:** same-repo PR runs write the Nx cache, except the dependency-update App's branches (Q16).
- **Release build:** the line's `build:release` runs with `--skip-nx-cache` (`dotnet-build-master.yml:195`).
- **Docs:** `docs/guide-nx-remote-cache.md` states that members who can push a branch can poison test results.
- **Order:** independent of everything else.

Dependencies: R before all; B before C; D before E; C before F. G and H are independent.

## 3. Open questions for the owner

- **O1, where "Blocking or Evicting" lives.** The merge queue's lanes are entities (`MergeQueueLane`,
  `CodeCoverage.Library/Entities/MergeQueue.cs:101-122`), not Spark message queues. Its work does run on Spark
  messaging, but every repository's merge-queue steps share **one** serial message queue, `coverage-merge-queue`
  (`CodeCoverage.Library/Feedback/CoverageQueues.cs:58-63`, `MergeQueueMessages.cs:16,82`). Spark messaging is
  "evicting" today by design:
  - a failing message is parked and the queue continues (`MessageProcessor.cs:564-579`);
  - this is recorded in `docs/messaging_single_subscription_plan.md:255-271` and
    `docs/messaging_single_subscription_PRD.md:499-503` ("one poisoned message must not stop other queues");
  - it is pinned by `MessagingInvariantsTests.A_message_that_keeps_failing_does_not_block_the_rest_of_its_queue`.

  The two readings:
  - **(a)** a Blocking/Evicting behavior on the **merge-queue lane** (Segment C, no messaging change);
  - **(b)** `[MessageQueue("x", Behavior = Blocking)]` on the C# record, which reverses that messaging decision for
    opted-in queues.

  Recommended: (a). Blocking `coverage-merge-queue` would stop every repository's queue on one bad message, while
  the hold must be per PR on one line. Option (b) can still be its own messaging feature later, with a resume/skip
  action (none exists).
- **O2, what the catch-up brings to each line.** This is parked Q11: every missing commit whole, so line tree =
  master tree and there are no conflicts (S4: 21/21 clean in order), or only each line's ecosystem. A commit left out
  conflicts later (S4: skipping #605 gave 3 conflicts).
- **O3, how the catch-up runs.**
  - **Per PR:** `/backport <line>` on each missing PR in master order, one queue run each, waiting for each merge.
  - **One catch-up PR per line:** all picks in order. One CI run, but the repository merges by squash only, so the
    line gets one commit for all of them. The individual picks stay visible in the PR, and the PR body lists them.
- **Parked from the grilling:** Q11 (O2) and Q6b (a tag ruleset would reverse versioning D3's "push a tag" gesture).

## 4. Spikes

| Spike | Question | How | Feeds |
|---|---|---|---|
| SP1 | Does `E2E 2/2`'s timeout at da8be3a91 also fail on master, and which test is it? | `gh run view 42622887029 --log-failed`, compare with master's run of the same tree content | R1 |
| SP2 | Which checks does a PR into a line report, and are they the 23 the ruleset would require? | read #610's check runs | R2 |
| SP3 | What differs between the #586 backports and master, and is #587 needed on the lines? | `git diff 3121cdda9 a9743b376` (and c2e5e9e32), read #587 | R3 |
| SP4 | Do the missing commits apply cleanly to today's tips in master order (S4 repeated with #620-#622)? | local replay in a scratch clone, no push | R4, O2 |
| SP5 | Why did #593, #608 and #620 get no backport PR? | the app's backport records and logs on production (read-only), `BackportRecipients.cs` | B |
| SP6 | Does a held lane survive an App restart and the messaging retry (the step message for a held PR must not be dead-lettered)? | code reading of `MergeQueueStepMessage` handling | C |
| SP7 | GitHub sandbox repository with the App installed, for C-F end to end | needs owner approval first | C-F |

## 5. Not in scope here

- Past lines (`dotnet-10.x`, `angular-21.x`): still deferred (`docs/coverage_backport_plan.md` M11).
- Issue #600 (pack major on older lines) and #599 (onboarding with release lines): separate issues; #600 matters only
  once a past line is cut.

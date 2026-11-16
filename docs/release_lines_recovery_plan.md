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

**R done by hand, 2026-11-11 (owner: "Fix the repository manually"), by the Q-A recommendation, a promotion merge.**
- **The commits:** each line got one commit whose tree is exactly master 5b71146ae's (#622, green in run 42818431316),
  with the line and that master commit as parents. `release/dotnet-11.x`: da8be3a91 → 6e7481da6; `release/angular-22.x`:
  92f44ccee → 55d7dfffb.
- **What was checked before the push:**
  - a trial `git merge-tree` of each line into master;
  - its conflict hunks were only the lines' older copies of master commits that #620 later reworked;
  - the lines' #586 copies (a9743b376, c2e5e9e32) differ from 3121cdda9 only in JSON key order in
    `apps/HR/HR/App_Data/Model/Person.json`.

  So nothing that existed only on a line was dropped.
- **How it was pushed:** a fast-forward push from this device, through a temporary *Repository admin* "always" bypass
  on ruleset 26211184. The bypass was added in the browser (GitHub asked the owner to confirm access) and removed
  through the API right after. The ruleset was verified back to the queue App only (exempt), active, with the same
  targets and rules.
- **What it covers:** this covers R1 (the red E2E at da8be3a91 is replaced by master's green tree), R3 and R4.
- **R5 done:** release runs 42983233148 (dotnet-11.x) and 42983232601 (angular-22.x) both succeeded and tagged the
  packages.
- **R2 decided (owner, 2026-11-11: "B - Yes, you can modify the rulesets if you also update the onboarding code
  accordingly"):** a separate **ruleset 3, "merge-queue: release-line checks"**, on the lines' glob, with only a
  strict `required_status_checks` rule and no bypass. Not 26719109 extended: `merge-queue.md` documents that ruleset 2
  stays on the default branch, and ruleset 2's `pull_request` rule would hold the App's backports for a review. The
  planner (`MergeQueueRulesets.cs`, `LineChecksName`) and the "Set up rulesets" form (`LineRequiredChecks`, shown
  only with lines, prefilled from ruleset 3, else ruleset 2) build it, so every onboarded repository gets it, not just
  Spark. SP2: #610 (head da4e1ee81) reported all 23 of ruleset 2's checks, so a line can require the same set.

**Segment B, no silent losses.** Every `backport:` target ends in an open backport PR (a draft on conflict) or a
recorded, visible failure (comment on the source PR and a row in the app). The source merge sha is stored on
`BackportPullRequest` (today it stores none, `CodeCoverage.Library/Entities/BackportPullRequest.cs:24-43`). SP5 finds
why #593, #608 and #620 produced nothing, although the conflict path does open drafts
(`CodeCoverage/PackageVersioning/BackportRecipients.cs:243-252`).

**B done (2026-11-11, branch `feat/backport-no-silent-losses`).**
- **SP5 answer:** none of the three was lost silently. Each got a "conflicts; nothing was pushed" comment from the
  App: #593 at 2026-11-07T11:02:59Z on `merge-queue-setup.snippets.ts`, #608 at 2026-11-09T11:55:56Z on both lines,
  and #620 at 2026-11-10T07:37:45Z on `EntityMapper.cs`, `QueryExecutor.cs` and others. Spark's `backport.onConflict`
  is the default `comment`, so a conflict opens no draft. The loss was that a comment was the only trace: no pull
  request, nothing in the app.
- **The silent paths in the code:** a forge refusal or error before any comment was only logged (`HandleAsync`'s
  catches). A line the writer did not report produced nothing.
- **What changed:**
  - **A new `BackportAttempt` record**, `BackportAttempts/{provider}/{repoId}/{source}/{line}`, one per backported
    pull request and line. It is written on every outcome:
    - opened, or opened with stale regenerated files;
    - a draft, or regenerating (moved to opened when the saga opens the pull request);
    - already on the line, or the branch exists;
    - conflicts, or failed (writer unavailable or failed, push rejected, the line not reported, a forge refusal or
      error).
  - **A retry overwrites the record**, so the latest outcome holds.
  - **The repository page lists the records** (`Custom.Repository_BackportAttempts`, streamed by id prefix, no index;
    `QueryRead` only, rows filtered to visible repositories like versioning runs).
  - **The source merge sha** is stored on `BackportPullRequest.SourceSha` (and carried by `BackportRegeneration`),
    for segment C's master order. A record from before B has none.
- **The default stays `onConflict: comment`.** B's goal is that nothing is lost unseen, and the record does that.
  Switching Spark to drafts is a coverage.yml choice, not code.

**Segment C, hold instead of evict on lines.** This is the owner's "Blocking or Evicting" (see O1).
- **What happens to the PR:** a red or pushed-over backport PR on a line goes to a new `Held` state instead of
  `EvictAsync` (`CodeCoverage/MergeQueue/MergeQueueEngine.cs:1647-1662`). `lane.Current` is kept, and the held PR
  resumes on a push or on green checks (HeadPushed handling, `:129-143`).
- **What stays as it is:** the decision is per PR by base branch (`MergeQueueBranches.IsLine`, `:1021`, and
  `IsAppsOwnBackportAsync`, `:546`), so other PRs keep being evicted.
- **Order:** backports are inserted in master order (`:317`) by the stored source sha from B.

**C done (2026-11-11, branch `feat/backport-hold-on-lines`, stacked on B).**
- **Who is held:** `EMergeQueueEntryState.Held` applies only to a backport the App opened (`IsAppsOwnBackportAsync`)
  into a branch the line pattern matches (`MergeQueueEngine.HoldsAsync`). A person's pull request into a line, and
  everything on the default branch, is evicted as before. Test: `A_persons_pull_request_into_a_line_is_still_evicted`.
- **What holds:** an eviction the pull request's branch can fix: `checks-failed`, `checks-timeout`, every writer
  conflict (`EvictWriterAsync`) and `author-push`. Everything else still evicts: permissions, base missing, retargeted,
  workflow guards, the merge's refusals. `HoldAsync`:
  - keeps `lane.Current`, the label and the queue branch, and does not call `StartNextAsync`;
  - abandons the regenerate run;
  - turns the check red as `Held: <reason>`, with the **Remove from queue** button;
  - comments what the backport waits for.
- **What resumes it:**
  - **A push** (`HeadPushed`) restarts from prepare on the new head (`ResumeAsync`). This also replaces the
    `author-push` eviction for a running line backport.
  - **Checks that pass after a re-run** (`ChecksChanged` on `FinalSha`, `ResumeOnGreenAsync`) go on to the merge. Only
    for a hold on red or late checks.
  - **A base move** (`BranchPushed`) tries it again.
  - `/dequeue` ends it and starts the next.

  Tests: `A_red_backport_on_a_line_is_held_keeps_its_lane_and_merges_once_its_checks_pass`,
  `A_push_to_a_held_backport_starts_it_again_on_the_new_head`, `A_conflicting_backport_on_a_line_is_held_and_…`.
- **Order:** `SourceOrderPositionAsync` puts a new App backport before the first waiting one whose source the forge's
  compare shows to be later (`merge-base(source, other) == source`). With no recorded `SourceSha` (records from before
  B), or a forge error, it goes last. Test: `A_backport_waits_in_the_order_its_source_merged`.
- **Not done here:** who is asked, reminders and the "blocked by" check are segment F (done, below). A hold whose `author-push` came
  from a writer step waits for the push's webhook to resume it.

**Segment D, backport branches protected, no approval.**
- **The ruleset:** a new ruleset on `refs/heads/backport/*` that only the queue App and repository admins can update
  (`CodeCoverage.Library/Forge/MergeQueueRulesets.cs:107-179`, `MergeQueueRulesetSetup.cs`).
- **No approval:** with that ruleset in place, the approval floor no longer applies to the App's backports
  (`MergeQueueEngine.cs:494-503`, `QueueApprovedBackportAsync` `:555-583` removed).

**D done (2026-11-11, branch `feat/backport-branches-protected`, stacked on C).**
- **Ruleset 4:** `MergeQueueRulesetPlanner` plans "merge-queue: backport branches" whenever release lines are chosen:
  `refs/heads/backport/*`, rules `creation`, `update`, `deletion`, bypass App `exempt` and repository admin (role 5)
  `always`. An existing one keeps its other rules and other exempt Apps; a person's bypass is dropped. Tests:
  `Release_lines_get_a_backport_branches_ruleset_only_the_App_and_admins_pass` (idempotent second run),
  `A_backport_branches_ruleset_loses_a_persons_bypass_and_keeps_another_App`,
  `Without_lines_there_is_no_backport_branches_ruleset`.
- **No approval:** `ApprovalFloorRefusalAsync` skips the floor for the App's own backport (`IsAppsOwnBackportAsync`)
  when `BackportBranchProtectedAsync` sees, on the head branch's live rules, one ruleset holding all three
  restrictions with the App `exempt`. Bypass actor lists are not readable with metadata access, so the admin entry is
  not checked; a ruleset that lets more people write still needs an admin to set it up. Tests:
  `A_backport_the_App_opened_onto_a_protected_branch_needs_no_approval_under_the_always_floor`,
  `A_backport_branch_ruleset_that_leaves_a_gap_keeps_the_approval` (partial rules, bypass Never/Unknown),
  `A_protected_branch_does_not_excuse_a_pull_request_the_App_did_not_open`.
- **Deviation: `QueueApprovedBackportAsync` is kept, not removed** (owner, 2026-11-11: "Keep it"). A repository that has not re-run "Set up rulesets"
  (or set them up by hand, without the OAuth app) has no ruleset 4, and its backports under the `Always` floor would
  otherwise have no way to merge. The live check makes the approval path dead code once ruleset 4 exists.
- **Consequence:** a writer can no longer push a `backport/*` branch; hand conflict resolution is an admin's (PRD Q13:
  "an admin's conflict resolution"). backports.md "Who may ask" says so.
- **After deploy:** re-run "Set up rulesets" on Spark and SparkTest to create ruleset 4.

**Segment E, "Line policy" check.** An App-bound required check on `release/*`, judged from the SDK majors:
current lines take only bot picks, past lines take human PRs (grilling Q1b, Q1c, Q1d).

**E done (2026-11-11, branch `feat/backport-line-policy`, rebased onto master after D merged as #633).**
- **The check:** `LinePolicyEventsRecipient` forwards every pull request event whose base is not the default branch
  (bots included: the queue's pushes to a backport need the check on each new head) to `LinePolicyRecipient`, on the
  versioning lane. It reads coverage.yml's line pattern. A base the pattern does not match gets no check. Otherwise
  `LinePolicy.Check` passes a past line, passes the App's own backport on the current line, and fails anything else
  there. The App's own backport means a `BackportPullRequest` record for this number onto this base, plus a
  `backport/` head. Tests: `LinePolicyRecipientTests` (6).
- **Current = newest line of its ecosystem** (owner, 2026-11-11, chose this over a literal reading of Q1c's "from the
  SDK versions") (`ReleaseLinePattern.CurrentLines`, D15, the same rule segment P's
  promotion uses), not a fresh read of the SDK majors. Q1c's split is the upgrade cutting the new line, so the two agree
  once the line exists. Reusing it keeps one definition of "current" across backports, promotions and this check.
- **Ruleset 3** (owner, 2026-11-11: "Always with lines", reversing "empty checks create no ruleset 3") always carries
  `Line policy` with `integration_id` = the App, so it exists whenever lines do, even
  with no other checks picked (it used to be skipped then). The form never offers it (`Defaults`/`Clean`). A binding
  to another App is rebound. Tests: `Empty_line_checks_still_require_the_line_policy`,
  `The_form_never_offers_the_line_policy_and_a_second_run_changes_nothing`,
  `A_line_policy_bound_to_another_App_is_bound_to_this_one`, updated contexts in 5 existing tests.
- *(Superseded 2026-11-12: ruleset 3 now exempts the App in every repository, promoting or not;
  `docs/lockfile_notice_and_line_cut_PRD.md` D2.)*
- **Defect fixed (owner: "y", 2026-11-11):** with `current: promote`, ruleset 3 exempts the App on every line. The
  queue refused any ruleset with checks or reviews that the App bypasses (`MergeQueueEngine.cs`, `bypass`), so every
  App backport onto a past line in a promote repository would have been refused. Spark has no past lines yet, so it
  never showed. On a line base, a ruleset without `pull_request` may now exempt the App, because the queue's own gate
  waits for `RequiredChecks`. Test: `Only_a_checks_only_ruleset_on_a_line_may_exempt_the_App` (line accepted; review
  rule or default branch still `bypass`).
- **After deploy:** re-run "Set up rulesets" on Spark and SparkTest (ruleset 3 gains the line policy, ruleset 4 is
  created). Open pull requests into a line get the check on their next push.

**Segment F, attention.**
- **Who is asked:** one auto re-run, then the author, with the code owners as fallback; a reminder after 2 working
  days.
- **What they see:** a comment linking to a CustomQuery of held PRs, a `backport: needs attention` label, and a
  "Line blocked by #N" check on the waiting PRs (Q8, Q12).
- **Order:** needs C.

**F done (2026-11-11, branch `feat/backport-attention`, on master after G).** `IBackportAttention`
(`CodeCoverage/PackageVersioning/BackportAttention.cs`) is called by the queue's hold, its end and its reminders, and
never fails the queue: `MergeQueueEngine.AttentionAsync` logs what it cannot do.
- **The re-run (Q8):** `checks.retryFlaky` defaults to 1, so a line backport already gets one automatic re-run before it
  is held. A repository that sets `retryFlaky: 0` still gets one for a line backport: `EvaluateChecks` re-runs once when
  `HoldsAsync` says the entry would be held.
- **Who is asked (Q8):**
  - The backported pull request's author (`ForgePullRequestText.AuthorLogin`), unless that pull request came from a bot
    (`AuthorIsBot`) or a fork (`HeadIsFork`, new: head and base repository ids differ, or the head repository is gone).
  - Otherwise, the code owners of its files: the line's own CODEOWNERS (`CodeOwners.Locations`, GitHub's order), over the
    conflict's paths (`EvictionPaths`) or else the pull request's first 300 files. People are assigned
    (`IForgeMergeQueue.AddAssigneesAsync`, new); teams are only mentioned.
  - Neither: the comment says nobody is named, and the held list still shows it to the repository's admins.
- **Reminders (Q8, "a reminder after 2 working days"):** a `HeldReminder` step 2 working days (Monday–Friday, UTC,
  `MergeQueueEngine.AddWorkingDays`) after the hold asks the same people again. A second one, 2 working days later,
  escalates to the code owners (minus the author) and assigns them. Then it stops: two reminders, no nagging. The step is
  gated on `Held`, so a hold that ended makes it a no-op. Reading of the PRD: "a reminder after 2 working days" asks
  again; the escalation is the code-owner fallback applied once the author did not act.
- **What they see (Q12):**
  - The hold comment gains "@author, this backports your #N, so it is yours to fix." and a link to
    `{Coverage:BaseUrl}/query/held-backports`.
  - The `backport: needs attention` label on the held pull request; the title is not touched.
  - A neutral check "Line blocked by #N" on each waiting pull request's head (at the hold, and when one joins the lane
    behind a held one). Neutral, so it never blocks a merge; a required-checks ruleset cannot pick it up by accident.
  - The held list: `Custom.BackportAttempts_Held` (`BackportAttemptActions`), program unit "Held backports" in the
    Coverage group. The segment B row of the backport (`BackportAttempt`) turns `held`, with `SourceAuthor`. The list shows
    the held rows in repositories the viewer can see, when the viewer authored the source (by the forge's own answer of
    who they are, T1), administers the repository, or manages its owner. Streamed by id prefix, no index.
- **The end of a hold** (resume on a push, a base move, green checks, `/dequeue`, the run's end): the label goes, the
  row goes back to `opened`, and each blocked check is completed as success "#N no longer holds `<line>`".
- **Tests:** `CodeOwnersTests`, `BackportAttentionTests`, `MergeQueueEngineTests` (segment F block, with
  `ScriptedAttention`), `GitHubForgeVersioningTests` (author and fork), `ReadRowFilterTests` (`BackportAttempts_Held`).

**Segment G, trial pick.** `trialPick: warn | refuse | off` per line in `coverage.yml` (`BackportYml.cs`), default
warn (Q10).

**G done (2026-11-11, branch `feat/backport-trial-pick`).** Owner, 2026-11-11, on when and what: "In the queue, pre-merge"
and "Labelled lines, PR comment". The labels are the ones set when the pull request opens from the supported SDK lines it
touches (`BackportTargets.Labels`, owner: "the labels which are applied when a pull-request is opened would probably be
determined by the supported versions of an SDK").
- **When:** `MergeQueueEngine.TrialPickRefusedAsync`, at the start of `EnterAwaitingChecksAsync`, so once the commit to
  merge (`FinalSha`) is known and while its checks run. That leaves time to remove a label before the merge. Only entries
  into the default branch, and each commit once (`MergeQueueEntry.TrialPickedSha`).
- **What is tried:** `BackportTrialPicks` reads the pull request's `backport: <line>` labels and resolves them as the
  backport does (`BackportRecipient.ResolveLine`/`Resolve`). It skips promoted current lines (`PromotedBranchesAsync`,
  extracted from the backport) and lines whose mode is `off`. The writer's new `POST /jobs/versioning/backport/trial`
  merges `BaseSha..FinalSha`, fetched through `refs/pull/N/head`, onto each line. It uses the backport's own per-line step
  (`BackportService.PickAsync`, shared with `RunAsync`: markers, auto-resolve rules, line-side paths), and pushes nothing.
- **What it does:** `warn` posts one comment naming each conflicting line and its files and the label to remove; the run
  goes on. `refuse` evicts with reason `trial-pick`. One conflicting line under `refuse` is enough.
- **Per line (Q10):** `backport.trialPick` is the default and `backport.trialPicks: { <line>: mode }` overrides it. A
  misspelt mode is reported and warns: it never refuses a merge nobody asked to refuse.
- **Fails open:** a trial that cannot run lets the merge go on: no writer, no access, a head that moved, a refusal, or an
  exception. The backport after the merge reports the conflict as before. This is the fallback because `refs/pull/N/head`
  may lag a publish briefly (head mismatch).
- **Tests:** `BackportTrialTests` (writer), `BackportTrialPicksTests`, `BackportYmlTests`, `MergeQueueEngineTests`
  (segment G block), and the route in `GitWriterSidecarClientTests`.

**Segment H, CI cache.**
- **Cache writes:** same-repo PR runs write the Nx cache, except the dependency-update App's branches (Q16).
- **Release build:** the line's `build:release` runs with `--skip-nx-cache` (`dotnet-build-master.yml:195`).
- **Docs:** `docs/guide-nx-remote-cache.md` states that members who can push a branch can poison test results.
- **Order:** independent of everything else.

**H done (2026-11-11, branch `feat/ci-cache-writes`).** The owner confirmed Q16 = B on 2026-11-11: "PR runs write - as
long as it's not from a fork". The PRD's earlier "rejected" line is marked superseded.
- **Cache writes:** `pull-request.yml` and `test.yml` give `NX_CACHE_RW_TOKEN` to master pushes and to `pull_request`
  runs whose head is in this repository (`github.event.pull_request.head.repo.full_name == github.repository`) and not
  `mintplayer/dependencies*` (`DependencyUpdateRun.RollingBranch`/`WorkBranch`). Forks get no secrets from GitHub
  either way. `dotnet-build-master.yml` and the deploy workflows keep master-push-only writes.
- **Release build:** in `dotnet-build-master.yml`, `build:release` and the ng-spark build add `--skip-nx-cache` on
  every branch but the default one, so a line's packages and its npm `dist` are built from source. The default branch
  keeps the cache: with lines on it releases nothing (#628), and it populates the cache for PRs.
- **Docs:** `docs/guide-nx-remote-cache.md`: the expression, the trigger table, and "members who can push a branch here
  can poison test results".
- **Verification:** this PR's own run is a same-repository PR, so its jobs should show remote cache writes (no "writes
  discarded"). The line part shows on the next line push.

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
- **Decided 2026-11-11 (owner):**
  - **O2 = everything, whole commits, in master's order.** A current line's tree equals master's, labels are
    ignored on current lines, and nothing is stripped (settles Q11, Q3 and Q4 for current lines; labels nominate only
    on past lines).
  - **O3 = B′.**
    1. A script cherry-picks the missing commits in master order onto `catch-up/<line>` from the line, and a PR runs
       the full CI on it.
    2. When it is green, the owner gets a temporary bypass entry on ruleset 26211184 (set in the browser, not the
       ruleset disabled).
    3. `git push origin <tested sha>:release/<line>` from this device, a fast-forward of exactly the green commit.
    4. The bypass is removed and verified gone, and the PR is closed.

    The line keeps one commit per pick with its cherry-pick trailer, so no "synced to" marker is needed, and one push
    means one release. These picks have no queue record.
- **Superseded the same day by Q-L (owner, 2026-11-11), which reverses D16.**
  - **The challenge:** the owner asked "if you're just hardcoding the release lines to contain all the same commits
    from master, then what's the point of the release lines?", and pointed out that the queue serves other
    repositories and self-hosters too.
  - **Elsewhere, a line always holds back something master has:**
    - Angular: features;
    - Node.js: semver-majors and changes not yet baked;
    - Rust: everything newer than the last promotion;
    - Kubernetes, TypeScript, Electron and Go: everything after the cut;
    - git.git and Cassandra: features, with fixes merged upwards from the oldest branch.

    Spark locks the major to the platform, so a current line has nothing to hold back except *time*.
  - **Decided, Q-L = A: two line modes per line pattern in `coverage.yml`.**
    - **`mode: promote`:** fast-forward only, to a default-branch commit that went green. The trigger is `manual`
      (`/release` or the app), `schedule` (cron) or `each-commit`. The line must be an ancestor of the default
      branch, and the app refuses `promote` otherwise.
    - **`mode: backport`:** ordered cherry-picks, held rather than evicted. `select` is `nominated`, `fixes` or
      `all`.
    - **The split:** repositories that anchor majors on the platform (versioning D7) switch a `promote` line to
      `backport` when master's platform major moves, and cut a new `promote` line. Other repositories switch only by
      config.
    - **No lines configured:** releases come from the default branch.
  - **Spark:** current lines `promote` + `manual`, past lines `backport` + `nominated`. O2, O3 (B′) and R3 no longer
    apply to current lines. Their catch-up becomes a one-time alignment that makes each line an ancestor of master
    (see the next open question), followed by a promotion.
- **Q-A, how a `promote` line moves: decided B (owner, 2026-11-11).** Each promotion is a merge commit whose tree is
  exactly the green master commit's, with the line and that commit as parents (`git commit-tree`). This supersedes
  Q-L's "fast-forward only, the line must be an ancestor of the default branch": the line is not an ancestor; the
  promotion commit is a fast-forward *of the line*. Lookups use ancestry, not sha equality. A reset (orphans tags) and
  a one-time `-s ours` merge on master were rejected.
  - **Ruleset 3 refuses a direct push** of that commit (its new sha has no checks, the App has no bypass, and squash
    merges would drop master from the ancestry). **Decided (Q-A = B, second round): the App bypasses ruleset 3 for
    the promotion push only**, after verifying that the commit's tree sha equals a green master commit's tree sha.
    Not a PR run per promotion: the tree already passed all checks, and the line's own `publish-release` still runs
    the tests before it tags or publishes (that is what held back da8be3a91). Trap accepted: ruleset 3's guarantee
    now rests on the App's tree check.
- **O1 = (a) only (owner, 2026-11-11: "A only").** The hold lives on the merge-queue lane; Spark messaging stays
  evicting. On a backport line a held conflict waits for the author or a maintainer to resolve it on the cherry-pick
  branch (owner). Holding keeps the order but does not add a commit that was skipped for that line, so an
  angular+dotnet PR after an angular-only one still conflicts on the dotnet line; on promote lines this cannot happen.
- **Q6b = C (owner, 2026-11-11), see segment P.** Human version tags stay allowed, recorded and validated by the App.

## 2b. Segment P, `mode: promote` with `trigger: manual` (decided 2026-11-11, second grilling)

- **P1, line-only changes (Q-P1 = A):** the App diffs the line's tree against its merge base with the default branch
  and drops changes that are older copies of default-branch commits. Anything left refuses the promotion, with a
  comment listing the files and commits. Nothing is ever dropped silently.
- **P2, which commit (Q-P2 = B):** the newest default-branch commit whose `publish-release` run concluded `success`
  ("green"), or `/release <sha>` when that commit is green and newer than the line's last promotion. The comment names
  the promoted commit and PR, and what was left out.
- **P3, who (Q-P3 = B):** a configurable lowest repository role, default `maintain`, per account with a per-repository
  override (the same shape as `MergeQueueBypassRole`). The role is read live from GitHub on every request, never
  cached.
- **P4, which lines (Q-P4 = C):** by default only the promote lines whose ecosystem has changes since their last
  promotion (the versioning engine's own change detection); `/release <line>` moves one line even without changes,
  `/release all` moves every promote line. Skipped lines are listed with the reason.
- **P5, versions (Q6b = C, owner: "if we detect tag-pushes and record them in the database, then we can pick option C";
  "Yes, invalid tags can be refused"):**
  - `/release [<line>] <PackageId>@<version>` names a version. It must keep the platform major, be higher than the
    last tag, and not exist on the registry.
  - A human push of `<PackageId>/v<version>` is detected from the webhook, validated the same way and recorded as
    that package's next version. An invalid tag is deleted, with a comment to the pusher.
  - At the next promotion of a line that releases the package, the App puts the tag on the promotion commit in the
    same step, so the line's release run cannot race it.
  - The line's versioning step asks the server and refuses a tag the App has not recorded as valid (one call from the
    action; if the server is unreachable the step fails rather than publishing).
- **P0, configuration (my call, not grilled; reason: no SDK detection needed, and the auto-cut of a new line already
  exists):** `versioning.lines.current: promote | backport` and `past: backport`, with `trigger: manual` (the only
  trigger for now). A line is *current* when it has the highest major among its ecosystem's existing line branches.
  The default stays `backport`, so other repositories keep today's behaviour. A promote line takes no cherry-picked
  backports: the backport targeting skips it.
- **Milestones (one PR):**
  - **M1** config (`current`, `past`, `trigger`) and the `/release` parser, with tests.
  - **M2** the release role setting on Account and Repository (model, translations, goldens).
  - **M3** the server side of `/release`: role, green commit lookup, line selection, the comment.
  - **M4** the GitWriter promote job: fetch, line-only check, `commit-tree`, push with tags.
  - **M5** backport targeting skips promote lines; ruleset 3 gets the App bypass when `current: promote`.
  - **M6** versions: `Pkg@ver`, tag-push detection, validation and recording, placement at promotion, and the
    server check the versioning action calls.
  - **M7** docs, the sandbox trial on SparkTest, the tests once, the PR.
    - Docs done: versioning.md "Promoted lines: `/release`", merge-queue.md (ruleset 3's App bypass with `promote`).
    - Spark's coverage.yml: `lines: { current: promote, trigger: manual, workflow: dotnet-build-master.yml }`. After the
      deploy, "Set up rulesets" must be re-run on Spark so ruleset 3 (27018553) gets the App's bypass; until then the
      ruleset rejects a `/release` push (line outcome `rejected`).
    - The versioning action's `dist` rebuilt from the main checkout.
    - **The SparkTest trial runs after the deploy, not before the PR.** `/release`, the tag webhook and the writer job
      are handled by the production App, which runs master. Order: tests once → PR → merge queue → deploy → re-run
      "Set up rulesets" on SparkTest and Spark → trial on SparkTest (`/release`, `/release Pkg@ver`, a hand tag, a
      line-only refusal) → only then the first `/release` on Spark. Until the trial passes, nobody comments
      `/release` on Spark; with `promote`, Spark's current lines take no backports in the meantime, which delays
      releases but publishes nothing wrong.
- **Status (2026-11-11):**
  - **M1 done:** `VersioningLinesConfig.Current/Past/Trigger/Workflow` with validation (`workflow` is required with
    `current: promote`: a commit is green when that workflow's run on the default branch concluded `success`, my call,
    because "publish-release" is a workflow *name* and the forge lists runs by file); `ReleaseLinePattern.CurrentLines`;
    `ReleaseCommand` (parser) and the `ReleaseRequested` event, raised from `OnIssueCommentAsync` on issues and pull
    requests alike.
  - **M2 done:** `Account.ReleaseRole` (`EReleaseRole`, zero value `Maintain` so old documents need no migration) and
    `Repository.ReleaseRole` (`EReleaseRolePolicy`, `Inherit`), `Repository.ResolveReleaseRole` / `MayRelease`; model
    synchronized. The translations golden still needs `SPARK_UPDATE_GOLDEN=1` (done with the tests at the end).
  - **M3 done:** `ReleaseRecipient` (`PackageVersioning/ReleaseRecipients.cs`): role check against the forge per
    request, `coverage.yml` read from the default branch, current lines from the line branches, the green run from
    `ListWorkflowRunsAsync(workflow)`, the writer call, the comment. The comment names the commit and links its run, not
    the PR (my call: a squash commit's PR is in its subject, and a merge-queue commit can carry several); "left out"
    counts only newer commits without a successful run, so naming an older green sha does not call newer green ones
    "not passed".
  - **M4 done:** `PromoteService` + `POST /jobs/versioning/promote`. The line-only check is "suspect paths" = changed
    on the line since the merge base ∩ different from the commit; a suspect is an older copy when its blob (or its
    absence) is one the default branch wrote between the merge base and the commit (`git log --raw base..commit --
    path`). P4's change detection runs in the writer, from the line's merge base (= its last promotion) to the commit,
    with the engine's `ChangedEcosystems`. The push carries a lease on the checked tip (outcome `moved`).
  - **M5 done:** a promoted current line is *unsupported* in `SupportedLines.Evaluate(..., promotesCurrent)` (reason
    `PromotedReason`), my call: besides backport labels and the "Backport targets" check, that also stops per-line
    dependency updates and the onboarding's line-workflow PRs, each of which would be a line-only change blocking the
    next promotion (P1). `BackportRecipient` drops promote lines from explicit `backport:` labels and refuses with a
    pointer to `/release` when none is left. `MergeQueueRulesetChoices.PromotesLines` (from `coverage.yml` in
    "Set up rulesets") puts the App as an `exempt` bypass on ruleset 3 only.
  - **M6 keeps P5's "same step":** a recorded tag goes in the promotion's own push (one atomic push of the line and
    its tags). A separate tags job right after would race the line's release run, whose checkout fetches the tags
    within seconds of the push.
  - **M6 design (my calls, each with its reason):**
    - *Record:* `ReleaseVersion` documents (`ReleaseVersions/{repo}/{sha256(package@version)[..16]}`), plain documents
      like `BackportPullRequest`, no model: state `Pending` (asked for, not placed) or `Placed` (with the commit).
    - *`/release … Pkg@ver`:* the server refuses a version that nuget.org or npm already has (both registries are
      asked, because the server does not know the package's ecosystem before the writer's graph; an id on the wrong
      registry is at worst a needless refusal). The writer then checks, at the green commit's graph: the id is a
      package, a promoted line releases its ecosystem, the major is that line's, and the version is above every
      `<id>/v*` tag. A failing version refuses its line (outcome `version-refused`), so a release never ships at an
      unintended version. The tag is lightweight (no engine annotation), so the line's engine sees a hand tag on the
      commit and releases exactly that version (`ReleasePlanner`'s `byHand`).
    - *Human tag pushes:* only in repositories with `current: promote`, only human senders (the App's and the action's
      own tag pushes are bots), only names of the form `<id>/v<semver>`. A writer job reads the tag's commit's graph:
      a tag whose id is no package (Spark's `schemas/v*`) is left alone. A package tag is always deleted (lease on its
      oid), because left in place it becomes reachable from the line at the next promotion and moves every later
      version; a valid one is recorded `Pending` and placed at the next promotion of its line, an invalid one gets a
      commit comment that names the pusher and the reason.
    - *The action's check:* with `versioning.lines.current: promote`, a release the engine reports `taggedByHand`
      is sent to `POST /api/versioning/tags/verify` (OIDC, like `run`); a tag the server has not recorded as `Placed`
      on that commit fails the step, and so does an unreachable server. Limit, documented: a human tag that carries a
      copied engine annotation is a re-run to the engine, not a hand tag, so it is not checked; a tag ruleset that
      lets only the App and `github-actions` create `*/v*` tags closes that, and is left to the owner.
  - **M6 done (code, tests written, not run):** writer `PromoteJob.Versions` (atomic push `WriterService.PushAtomicAsync`,
    outcome `version-refused`, `Unplaced`), `POST /jobs/versioning/hand-tag` (`PromoteService.CheckHandTagAsync`);
    server `ReleaseVersion`, `PublishedVersions`, `TagPushed` from the push webhook → `HandTagRecipient` (commit comment
    via the new `IForgeVersioning.CommentOnCommitAsync`, needs `contents: write`, which the App has), `ReleaseRecipient`
    passes the comment's and the pending versions and records the placed tags, `POST /api/versioning/tags/verify`
    (CSRF list and route snapshot updated); action `verifyHandTags` (+ tests). The action's `dist` is rebuilt from the
    main checkout before the PR.
    - A pending version whose package the comment also names is left pending (the comment's wins this time).
    - A version the comment asks for, on a line that holds the commit already, is `version-refused` (no promotion
      commit to tag). A recorded (pending) version there leaves the line `contained` and stays pending.
    - A recorded version is flagged `Recorded` on the writer request. When it no longer fits (its package is gone, or
      the line released past it since it was recorded) the writer reports it in `Unplaced` and promotes the line
      without it, and the server deletes its record. Only a version the comment names refuses its line. Why: a stale
      record would otherwise refuse every later promotion of its line, with no command to clear it (found while
      writing M6; test `A_recorded_version_the_line_released_past_is_reported_and_does_not_hold_the_line`). A recorded
      version whose line the release does not take stays pending without a word.
- **P6, sandbox (SP7 = A):** `PieterjanDeClippel/SparkTest`, two lines and a `publish-release` without publish
  secrets. Standing permission to push branches and tags and change rulesets there; ask before deleting anything that
  was there before.

## 2c. Segment T: hand tags follow their own line, and onboarding promotes (2026-11-11)

**Why.** Owner, 2026-11-11, after PR #626: "We probably need to make sure that tags pushed to a specific
release-branch actually belong to that branch (v8.1.2 should be pushed for branch dotnet-8 and not for dotnet-7) - and
per branch, tags should be increasing, never react to a downgrading tag on a release branch". Then: "I was thinking
about just validating the pushed tag(s), see if these increment, not check all tags in the repo", and "But I'll let you
decide". T1–T6 below are therefore my decisions, made under that delegation.

Before T, a hand tag was compared with **every** tag of its package across majors (`is not above the package's tag`).
That refused a valid `v8.1.2` once a `v9.0.0` existed elsewhere. It only ran in `current: promote` repositories
(`HandTagRecipient` returned early otherwise), so a backport repository's `v7.0.4` pushed onto the dotnet-8 line was
never looked at. Found reading `PromoteService.CheckHandTagAsync` and `ReleaseVersions.cs` at master 537da108.

Re-onboarding SparkTest (2026-11-11, onboarding PR #5, ruleset 27049210) showed the second gap. Onboarding wrote
`lines: {}` only, so no new repository could get `/release` from the chooser. Ruleset 3 was created without the
App's bypass, because `MergeQueueRulesetSetup.ReadAsync` reads `PromotesLines` from the default branch's coverage.yml
(the one before the onboarding pull request).

- **T1, the predecessor.** A tag is compared with one tag: its package's highest tag reachable from the tagged commit
  (`git tag --merged <commit> --list <id>/v*`). The pack reads that same tag (`tag --merged HEAD`,
  `MintPlayer.Versioning.MSBuild.cs:91`), so "increasing per branch" means exactly "above what this commit would
  version from". For a tag the next promotion places (T4), the line's tip counts as well, because the promotion
  commit has both as parents. Rejected: every tag of the package (the old rule, wrong across majors); the tags on the
  line branch only (a tag on a default-branch commit has none).
- **T2, the rule.** Any higher version with the predecessor's major is valid. "Exactly the next patch/minor" was
  rejected: a skipped version harms nothing, and the engine skips none itself. Without a predecessor (a package's
  first tag) the major must be that of a release line of its ecosystem. This is the "belongs to its branch" half: a
  new major starts with its release line, never with a hand tag.
- **T3, scope.** Every repository with `versioning.lines`, promoted or not. The owner's rule names release branches,
  not promotion. A repository without `lines` is not touched (test
  `A_repository_without_release_lines_never_asks_the_writer_about_its_tags`).
- **T4, outcomes.**
  - Invalid: deleted with a lease on the tag object, plus a commit comment naming the pusher and the reason.
  - Valid, in a promote repository, with the current line's major: deleted and recorded for `/release`, as in M6.
  - Any other valid tag: left in place (`accepted`), and recorded as placed on its commit with no comment, so a
    promote repository's older-line run passes `tags/verify`.
  - nuget.org and npm are asked only on the recorded path: a tag left in place publishes through the line's own run,
    which skips published versions.
- **T5, promotions** use the same predecessor rule from the promotion's parents (tip, green commit), for the asked
  versions and the recorded ones. A recorded version that fails it is reported in `Unplaced` and forgotten, as in M6.
- **T6, onboarding defaults to promote.** When it adds `versioning.lines` and exactly one push workflow runs the
  versioning step, it writes `current: promote, trigger: manual, workflow: <that file>`. With none or several the bot
  cannot tell which green run proves a commit, so it writes `lines: {}` and a note that says how to switch. The
  chooser now plans before the rulesets step, and `ChooseAsync(promotesLinesComing:)` gives ruleset 3 the App's
  `exempt` bypass when the onboarding pull request turns promotion on.

**Code:**
- Writer: `PromoteService` (`PredecessorAsync`, `FollowProblem`, `AboveProblemsAsync`, `HandTagAccepted`), and
  `HandTagRequestWire.Promotes` with all lines (at most 50).
- Server: `HandTagRecipient` (every `lines` repository, `RecordPlacedAsync`).
- Onboarding: `OnboardingPlanner.PromoteLinesKey`, `VersioningOnboarding.VersioningWorkflowsAsync`,
  `SetUpFeaturesAction`, `MergeQueueRulesetSetup.ChooseAsync`.
- Tests: PromoteTests (hand-tag theories for promote and backport, the tip as predecessor, a past line, a first tag),
  ReleaseFlowTests, OnboardingPlannerTests, SetUpRulesetsActionTests.

**After deploy:** re-run "Set up rulesets" on MintPlayer.Spark, so ruleset 27018553 gets the bypass. Re-onboard
SparkTest: close PR #5 and run the chooser again, so it gets `current: promote` and the bypass. Then the SparkTest
trial, and only then the first `/release` on Spark.

**Done 2026-11-11:** PR #627 merged as 93e1ae75 and was deployed. Ruleset 27018553 has the App's `exempt` bypass
(read back after "Set up rulesets"). SparkTest was re-onboarded (onboarding PR #8, its ruleset 27049210 got the bypass).

## 2d. The SparkTest trial and the gaps it found (2026-11-11)

**Trial** (`PieterjanDeClippel/SparkTest`, issue #10; its packages are packed only and pushed to no registry, owner
2026-11-11: "avoid publishing these test-packages"):
- `/release dotnet-11.x` promoted `release/dotnet-11.x` to 20b9c4ad. Its parents are the tip 1c5337e and the green
  commit c68f08f, and its committer is "CodeCoverage versioning". The line's run released `SparkTest.Lib 11.0.0-rc.1`
  ("first release") and packed it.
- Hand tags pushed under the owner's account: `v11.0.0-beta.1` (below its predecessor) and `v10.5.0` (no line has
  major 10) were refused, deleted, and commented with the pusher's name. `v11.0.0` on main was recorded.
- A bare `/release` after PR #11 promoted to 74d61290 and placed `SparkTest.Lib/v11.0.0`. The line's run packed
  11.0.0, and `tags/verify` passed.
- Not tried end to end: the line-only refusal. It is covered by PromoteTests.

**G1, a red default branch (fixed in this PR).** With `versioning.lines` on, the default branch tags nothing. A
package that declared no `<Version>` gets no seed tag, so `dotnet pack` of its project on the default branch fails
with MPV005. SparkTest's main went red that way (worked around by hand in SparkTest PR #9: pack only on `release/*`).
A red default branch leaves `/release` no green commit to promote.
- **Fix:** a new onboarding rule, "The default branch packs and publishes only what it released"
  (`VersioningOnboarding.PublishGate`). It adds the step Spark's own `dotnet-build-master.yml:210-221` has ("Decide
  whether this run publishes", `id: publishes`, false on the default branch when `released` is empty). Every step
  from the first pack or publish to the last gets `if: steps.publishes.outputs.value == 'true'`.
- A step with its own `if:`, one that starts with a block scalar, or a versioning step without an `id` gets a note
  instead.
- The rule applies with or without lines. Without lines, the default branch releases every changed package, and an
  empty `released` means nothing changed, so skipping the pack loses nothing.
- Evidence: SparkTest main's red run before PR #9, and `VersioningOnboardingTests` (four byte-for-byte expectations
  plus `A_publish_step_with_its_own_condition_or_a_versioning_step_without_an_id_is_left_to_the_merger`). The
  idempotency theory still passes.

**G2, no hint for existing lines (fixed in this PR).** SparkTest's coverage.yml already had `lines: {}`, so T6 never
fired and nothing said promotion was possible. An existing `versioning.lines` is still left alone (T6). When it takes
backports and exactly one push workflow runs the versioning step, a note now gives the exact `{ current: promote,
trigger: manual, workflow: <file> }`. Test: `Existing_lines_that_could_promote_are_left_alone_and_a_note_says_how_to_switch`.

**Left to the owner:** the first `/release` on Spark. It publishes real NuGet and npm packages, which are permanent,
and nothing in segments B-H needs it. Spark's lines release when a maintainer comments `/release`.

## 2e. The Tools onboarding and the gaps it found (2026-11-12)

The owner chose release lines over multi-targeting for `MintPlayer/MintPlayer.AspNetCore.Tools` (2026-11-12: "I'll
use the release-lines over multi-targetting. Let's just set it up as intended"). "Set up features" opened onboarding
PR MintPlayer.AspNetCore.Tools#53 and created rulesets 27099665-27099669. Reading #53 against the repository found
two gaps, both fixed in one PR (owner: "fix the gaps in a single pull-request here. add the necessary tests").

**G3, the lines would never publish or be checked.** Tools' `publish-release.yml` ran on `push` to `master` only
(`# - 'release/**'` commented out), and `pull-request.yml` ran for pull requests into `master` only. With lines on,
the default branch tags nothing, so no package would ever have been released, and ruleset 3's required checks could
never report on a backport.
- **Fix:** `ReleaseLineTriggers` (new). When the plan has lines, the `push` filter of each workflow that runs the
  versioning step, and the `pull_request` filter of every workflow, take the lines' glob (`'release/*-*.x'` for the
  default pattern) when they name the default branch and no line yet. The edit is text, so comments and layout stay:
  a flow list gets `, 'release/*-*.x'` before its `]`, a block list a new item under its last one (CRLF kept).
- Left alone: a trigger without a branch filter (it runs everywhere), a filter without the default branch, and every
  other workflow's `push` (a deploy on master must not run on a line). A `branches-ignore` that matches the lines,
  or a negated entry, gets a note instead.
- YamlDotNet 18.1 gives a flow sequence an `End` equal to its `Start`, so the `]` is found after the last item's
  `End` (measured with a throwaway test, 2026-11-12: `start=54 end=54`).

**G4, an existing `mergeQueue:` without `branches` queued only the default branch.** Tools' coverage.yml had
`mergeQueue:\n  enabled: true`, so backports into the lines could not go through the queue. The planner only added a
note. Now a section without `branches` gets `branches: [<default>, supported-lines]`; one with its own list keeps the
note.

**G5, one required approval a solo maintainer can never give.** The rulesets form started at 1 when the repository
had neither ruleset 2 nor classic protection, so Tools' ruleset 27099667 required an approval nobody but its author
could give (GitHub never counts the author's own). `MergeQueueRulesetPlanner.Defaults` now starts at 0 there;
ruleset 2's and classic protection's counts still win. The owner also proposed moving the field into the feature form,
enabled by the Merge queue checkbox through a refresh (2026-11-12). Not taken: the rulesets form already appears only
with Merge queue, and it is the one that can prefill the repository's current count (the feature form comes before
the GitHub popup). `RefreshInvoker` also runs `OnRefresh` only for a type with a CLR class, which `SetUpFeatures`
(a virtual type) has not. Test: `The_approvals_start_from_ruleset_2_then_classic_protection_then_none`.

Tests (`OnboardingPlannerTests`): the Tools shape end to end with a deploy workflow left alone and planning again
changing nothing; four filters left alone; the `branches-ignore` and negation notes; CRLF; no lines, no edits; the
merge-queue key added, and kept as a note for an owner's own list. All 160 onboarding tests pass.

**G3-G5 shipped in #636** (347fd05b6, deployed). #53 was rebuilt and merged as bd6559cb8; ruleset 27099667 was saved
at 0 approvals.

**G6, no person can create a line, and the onboarding did not.** After #53 merged, Tools had only
`release/dotnet-6.x` and `7.x`: run 43186708348 cut nothing (`ReleaseBranchCuts.ForLinesAsync` cuts only an untagged
major, and every Tools tag is at 11), and `POST /git/refs` for `release/dotnet-10.x`/`11.x` answered
`422 Reference update failed` because ruleset 3 requires "Line policy" and only the App bypasses it. Owner: "Shouldn't
the bot be creating the release lines? why does the user need to do that?". Fixed in the line-cuts PR
([onboarding_line_cuts_PRD.md](onboarding_line_cuts_PRD.md)): the onboarding creates the current lines, and the lower
`netX.0` lines the owner chooses, at the merge or, with nothing to change, at once.

**Next for Tools:** after that deploy, run Set up features again on Tools (S2): expect `release/dotnet-11.x` and the
chosen `release/dotnet-10.x` created at once; then .NET 11 GA on master and `/release`.

**G7, the first release on the older line published nothing (#638, closes #600).** After the line cuts, Tools' first
run on `release/dotnet-10.x` tagged `v10.0.0` for 14 packages that were already on nuget.org. The pack then read the
reachable `v11.*` tags and packed 11.x, and `--skip-duplicate` let the run finish green with nothing published. Four
causes, each fixed in the #638 PR ([release_line_first_release_PRD.md](release_line_first_release_PRD.md)):
- the pack took the highest reachable tag of any major (now per package, the line's major, MPV006);
- the hand-tag rule followed the newer major's tag the same way;
- versions shipped before the onboarding had no tags (the onboarding now tags every version at its earliest
  occurrence, and Set up features moves the App's own misplaced tags when the owner agrees);
- the publish never checked that it packed what it planned (the publish step now fails such a run).

Backports that only move the platform off an older line now keep the line's value (D13). An older line cut from a
multi-target commit gets a narrowing pull request (D15). Tools' recovery is that PRD's §6.

## 4. Spikes

| Spike | Question | How | Feeds |
|---|---|---|---|
| SP1 | Does `E2E 2/2`'s timeout at da8be3a91 also fail on master, and which test is it? | `gh run view 42622887029 --log-failed`, compare with master's run of the same tree content | R1 |
| SP2 | Which checks does a PR into a line report, and are they the 23 the ruleset would require? | read #610's check runs | R2 |
| SP3 | What differs between the #586 backports and master, and is #587 needed on the lines? | `git diff 3121cdda9 a9743b376` (and c2e5e9e32), read #587 | R3 |
| SP4 | Do the missing commits apply cleanly to today's tips in master order (S4 repeated with #620-#622)? | local replay in a scratch clone, no push | R4, O2 |
| SP5 | Why did #593, #608 and #620 get no backport PR? | the app's backport records and logs on production (read-only), `BackportRecipients.cs` | B. **Answered:** they conflicted, and `onConflict: comment` left only a comment (see B done) |
| SP6 | Does a held lane survive an App restart and the messaging retry (the step message for a held PR must not be dead-lettered)? | code reading of `MergeQueueStepMessage` handling | C |
| SP7 | GitHub sandbox repository with the App installed, for C-F end to end | needs owner approval first | C-F |

## 5. Where it stands and what is next (2026-11-11)

1. **R5:** done, both runs green.
2. **R2:** done. PR #624 (c627a9197) shipped ruleset 3; "Set up rulesets" created it on Spark as ruleset 27018553
   (23 checks, strict, no bypass), 2026-11-11. Segment R is complete.
3. **No owner decision is open.** Segment P (§2b, PR #626) and segment T (§2c, PR #627) are merged and deployed. The
   SparkTest trial passed; its gaps G1 and G2 are fixed in the trial-gaps PR (§2d).
4. **The segments to build, re-ordered for Q-L:**
   - **B (no silent losses):** done, PR #629 (backport attempt records, SP5 answered).
   - **P, a new segment: `mode: promote` with `trigger: manual`** (`/release`): done (#626). Spark's current lines
     take no backports since.
   - **C (hold, not evict):** done, stacked on B (its PR follows #629's merge). It matters only for `backport` lines.
   - **Then D to H as listed.**
5. **Do not** run the manual promotion again without the same checks: a trial `merge-tree`, a review of the line-only
   diff, a fast-forward check, and the bypass removed and verified.

## 6. Not in scope here

- Past lines (`dotnet-10.x`, `angular-21.x`): still deferred (`docs/coverage_backport_plan.md` M11).
- Issue #599 (onboarding with release lines): a separate issue. #600 (pack major on older lines) is fixed by #638
  (G7 in §2e).

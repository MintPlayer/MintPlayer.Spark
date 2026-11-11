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

## 5. Where it stands and what is next (2026-11-11)

1. **R5:** done, both runs green.
2. **R2:** done. PR #624 (c627a9197) shipped ruleset 3; "Set up rulesets" created it on Spark as ruleset 27018553
   (23 checks, strict, no bypass), 2026-11-11. Segment R is complete.
3. **No owner decision is open.** Segment P (§2b) is being built on `feat/release-lines-promote`.
4. **The segments to build, re-ordered for Q-L:**
   - **B (no silent losses), smallest first:** still needed for past lines and `backport` repositories.
   - **P, a new segment: `mode: promote` with `trigger: manual`** (`/release` and an app button). It turns today's manual
     recovery into the App's own action. Until P exists, the bot keeps cherry-picking labelled master PRs onto Spark's
     lines, which apply cleanly now that each line equals master.
   - **Then C to H as listed.** C (hold) matters only for `backport` lines.
5. **Do not** run the manual promotion again without the same checks: a trial `merge-tree`, a review of the line-only
   diff, a fast-forward check, and the bypass removed and verified.

## 6. Not in scope here

- Past lines (`dotnet-10.x`, `angular-21.x`): still deferred (`docs/coverage_backport_plan.md` M11).
- Issue #600 (pack major on older lines) and #599 (onboarding with release lines): separate issues; #600 matters only
  once a past line is cut.

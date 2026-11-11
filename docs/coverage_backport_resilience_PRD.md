# Backport resilience: release lines without friction — PRD and plan

Status: **draft, 2026-11-10.** Nothing is decided yet; §4 lists the decisions for the owner. The owner wants further
discussion and investigation before any implementation starts (2026-11-10). Tracked in #621.

**Where the discussion stands (2026-11-10, end of session):** a grilling session on §4 started with Q1 and was paused
by the owner, who first wants to explain how they see some things. Q1 was put as three options (§4, "Q1 as asked");
no answer yet. The cache side of §3.7 moved on separately: the Nx remote cache now serves public reads (PR #622). Builds on
[coverage_backport_PRD.md](coverage_backport_PRD.md) (D1–D18) and
[coverage_package_versioning_PRD.md](coverage_package_versioning_PRD.md). Paths below are under `apps/CodeCoverage/`
unless they start with `docs/`, `.github/` or `libs/`.

> **2026-11-11:** the work continues in `docs/release_lines_recovery_plan.md`. The owner split it into small segments,
> repaired the lines by hand (promotion merge), and chose per-line modes `promote` / `backport` (Q-L), which reverses
> D16. Where this PRD and that plan differ, the plan wins. Status: segments R, P (#626) and T (#627) are shipped,
> the SparkTest trial passed, and its gaps are fixed (plan §2d). Segments B-H are next.

## 1. Why

PR #620 (#551) merged on 2026-11-10 and its backport to `release/dotnet-11.x` conflicted in 15 files. #620 was not
the cause. The line was missing earlier master changes that #620 builds on. This is the first visible case, but
history shows it is the normal outcome, not an exception.

**Measured, 2026-11-06 (cut at `5fd324b13`, #579) to 2026-11-10:**
- 21 commits on master; **11 never reached any line**.
- 6 backport conflicts in 5 PRs (#593, #603, #607, #608 on both lines, #620). **None was ever finished**; the lines
  hold only the 9 clean bot backports (4 dotnet, 5 angular).
- Root cause of most conflicts: **#605 (generic slugs) got no `backport:` label** although it changed 54 `.cs` and
  38 ng-spark files. Every later PR touching the same files conflicts (#603, #607, #620; `QueryExecutor.cs`,
  `QueryResultProjector.cs`, `spark-routes.ts`, …). #603 then blocks #607 and #608.
- Evidence: `git log 5fd324b13..origin/master`, `gh pr view N --json labels`, bot comments; conflict cascade
  confirmed by cherry-picking #605 onto the line (conflicts on `SparkSitemapReaderTests.cs`, which #593 changed
  first).
- **Replay (spike S4, 2026-11-10, `C:\Repos\backport-resilience-sandbox\spikes\S4\RESULT.md`):** the 21 master
  commits picked onto the cut point (`5fd324b13`) one by one with `cherry-pick -x`:

  | Run | Result |
  |---|---|
  | E1: all 21 in master order | 21 clean, 0 conflicts |
  | E2: the 15 that touch more than `apps/`, `docs/`, `.github/`, in master order, those paths stripped | 15 clean, 0 conflicts |
  | E3: as E2, #605 skipped (the forgotten label) | 3 conflicts: #603 (`po-detail`, `spark-routes.ts`), #607 (presenter + spec), #620 (`QueryExecutor.cs`, `QueryResultProjector.cs`, ~10 test files) |
  | E4: from the **real line tip**, the unported ones in master order | 12 clean, 0 conflicts; 3 already on the line |
  | E5: as E2, #603 and #605 swapped | 2 conflicts (#603, #607) |

  **Master order removes every conflict; one skip or one swap produces them.** E4 also means today's backlog,
  #620 included, applies cleanly from the line's tip if picked in order (§8).
  **"Already on the line" detection is broken today:** the line holds #580, #586 and #609 as squashed backport PRs
  titled `[dotnet-11.x] … (#581)` / `(#592)` / `(#610)`, with **no `cherry picked from` trailer** and the backport
  PR's number, not the original's. Trailer matching and exact-subject matching both miss them (#580 then conflicts
  in `SparkReferences.cs`, `PersistentObjectSavePath.cs`); only subject matching with the prefix and number stripped
  finds them (§3.4).

This matches what the backport PRD already measured on Angular's history (66 of 72 conflicts from one missing PR,
`docs/coverage_backport_PRD.md:404`) and accepted under D6 "lines are independent".

## 1b. How Angular and ASP.NET Core do it (local clones, 2026-11-10)

**Angular (`C:\Repos\angular`):**
- Every PR carries exactly one `target:` label (`.ng-dev/pull-request.mjs`, `contributing-docs/branches-and-versioning.md`).
  The merge tool merges into `main` and **cherry-picks to every target branch in the same run**.
- **Features never go to a patch branch:** `target: minor`/`major` land on `main` only. The label decides, not the
  commit type (`fix(compiler): scope nested CSS rules`, #69885, is `target: minor` and never reached `22.1.x`).
- `main` is linear (0 merge commits in 3,437 commits since 2026-01-01). Patch-branch commits are `cherry-pick -x`
  copies.
- **Order is preserved:** on `22.1.x`, 337 cherry-picked originals appear in exactly `main`'s order, 0 inversions.
- **Forgotten ports:** 26 `fix`/`perf` commits on `main` in `22.1.x`'s window are absent by SHA. 3 are present under
  another SHA, and 5 spot-checked ones are `target: minor`, so they were left out on purpose. No missed port found.
- **Same run, seconds apart:** fix #70716 is on `main` at 15:51:04 UTC and on `22.1.x` / `22.2.x` at 15:51:09/10,
  same patch-id, same author date, `(cherry picked from commit 37196053…)`. The LTS lines (`21.2.x`, `20.3.x`) got
  a separate, adapted commit 2.5 h later without the trailer (different patch-id): a manual port.
- **How the merge tool works** (`angular/dev-infra`, `ng-dev/pr/merge/strategies/strategy.ts` and `api-merge.ts`,
  read 2026-11-10), in order:
  1. `check()` → `_assertMergeableOrThrow()` cherry-picks the PR onto every target branch locally with
     `dryRun: true`. A failed pick is `cherry-pick --abort`ed and the merge is refused before anything reaches GitHub.
  2. The PR is merged into `main` through the GitHub merge API (`githubApiMerge`, rebase by default, squash with the
     `merge: squash commits` label), so it shows **Merged**.
  3. The real picks are pushed directly to the targets with `git push --atomic` (`pushTargetBranchesUpstream`).
     There is no PR per target.
- **Picks are not tested before they land.** The dry run only proves the patch applies textually. The PR's CI ran
  against `main` only. CI on the patch branches runs **after the push** (`.github/workflows/ci.yml:4-7`,
  `push: branches: '[0-9]+.[0-9]+.x'`), so a patch branch can go red and is fixed forward.

**ASP.NET Core (`C:\Repos\aspnetcore`):**
- `main` is still squash-merged, about 90% of first-parent commits even in 2026-11. The merge commits are
  mostly **release → main flow merges** by a bot (`[automated] Merge branch 'release/11.0' => 'main'`, "use a merge
  commit, not a squash or rebase"), plus some feature-branch merges in Oct–Nov 2026 (#70348, #70084, #69871, …).
  The claim that they switched to merging feature branches is not confirmed: those are individual PRs, not yet a policy.
- **Release branches get servicing only after the cut**, plus dependency-flow PRs. Backports are one PR per original
  (`backport/pr-NNNNN-to-release/X.Y`, titled "Title (#orig) (#backport)").
- **Order is not preserved and nobody cares**: #69927 and #69926 landed on `release/11.0` reversed; #70017 (main
  10-26) after #70049 (main 10-29). Lag ranges from days to weeks (#69398: main 10-07 → release 10-14).
- **Release-branch fixes can't go missing from main**: the bot merges each release branch forward into main.

**What this means for Spark:**
- **Neither project ships features from a branch fed by backports.** Features reach users from `main` (Angular's
  next minor; ASP.NET Core's previews). Backports carry fixes to branches that are no longer current. Spark's D15+D16
  makes **every** change a backport to the current line, which is why one missing label cascades. This is the
  strongest argument for Q1.
- Angular's model is the reference for the lines that do need backports (after the split): decide targets at merge,
  and keep master's order on every target, so nothing is forgotten silently (Q8).
- ASP.NET Core shows that per-PR independent backports work when they carry **only fixes, not features**. Their order
  doesn't matter because fixes rarely overlap.
- **Other projects (web research, 2026-11-10):**

  | Project | Forgotten target | Order | Conflict made visible by | Tested before landing |
  |---|---|---|---|---|
  | Node.js ([backporting](https://github.com/nodejs/node/blob/main/doc/contributing/backporting-to-release-lines.md), [releases](https://github.com/nodejs/node/blob/main/doc/contributing/releases.md)) | Can't be lost: the releaser lists **every** main commit not on staging (`branch-diff`), excluding only explicit labels (`dont-land-on-vN.x`, `backport-requested-vN.x`, …) | main's (`--reverse`) | `backport-requested-vN.x` + comment, then `backport-open-`, `backported-to-`; **`backport-blocked-vN.x`** marks PRs waiting on another PR's backport | yes, `make test` before pushing staging |
  | Electron ([trop](https://raw.githubusercontent.com/electron/trop/main/docs/usage.md)) | `target/*` label or `/trop run backport-to <branch>` after merge | merge order | `target/<b>` swapped for **`needs-manual-bp/<b>`** + comment; the conflicting PR is left out, the rest ship | backport PR's CI |
  | CPython ([miss-islington](https://github.com/python/miss-islington)) | label before merge; later: unverified | not documented | comment + the merger assigned | CI + core-dev approval |
  | Kubernetes ([cherry-picks](https://github.com/kubernetes/community/blob/master/contributors/devel/sig-release/cherry-picks.md)) | picks only after merge; a skipped branch must be explained on the PR | newest branch first | author resolves; release-manager approval label | pick PR's CI |
  | Rust ([backporting](https://forge.rust-lang.org/release/backporting.html)) | `beta-nominated` any time | not specified | release team's rollup PR | yes |

  | Go ([minor releases](https://go.dev/wiki/MinorReleases)) | "@gopherbot please backport" opens one `[1.N backport]` issue per release on the minor-release milestone; open ones roll to the next milestone, so they stay visible | — | issue + `CherryPickCandidate` → `CherryPickApproved` | not stated |
  | LLVM ([GitHub](https://llvm.org/docs/GitHub.html)) | release milestone + `/cherry-pick <sha…>` comment | — | comment with the failed job; a human opens the PR | PR CI |
  | ClickHouse ([backports](https://clickhouse.com/docs/development/backports)) | **an hourly sweep** finds merged, labelled PRs whose commit isn't reachable from the branch and lacks `pr-backports-created`; `v25.3-must-backport` = that release **and every newer one** | — | assigned to author + merger, pinged at 3 days, closed at 7 | a reduced CI set |
  | TypeScript ([script](https://github.com/microsoft/TypeScript/blob/786e36ecaf68ddb9d1e1cdd14c7c43cf059678fc/scripts/open-cherry-pick-pr.ts)) | `@typescript-bot cherry-pick this to release-X` (allow-listed users) | — | bot says to pick manually | PR CI |
  | Grafana ([backporting](https://grafana.com/docs/writers-toolkit/review/backporting/)) | `backport <branch>` label before or after merge | — | follow-up PR per label; copied labels have broken later pipelines ([Loki](https://grafana.com/docs/loki/next/community/maintaining/release/backport-commits)) | PR CI |
  | GitLab ([handbook](https://handbook.gitlab.com/handbook/engineering/releases/backports)) | release managers decide per request; multi-release only for security | — | release tracker | yes |

  Only Node.js makes forgetting impossible: the default is "everything on main lands", and **not landing** is the
  explicit act. ClickHouse closes the gap a different way, with a periodic sweep. **Of the 13 projects, Angular is the only
  one known to land picks untested** (Go's practice is not documented) (it pushes them directly and relies on CI after the push plus Google-internal tests,
  `requiredStatuses: google-internal-tests`, `.ng-dev/pull-request.mjs:32`). None of them backports features
  routinely; Spark (D16) is the exception.
- **Tools and merge queues** (same research):
  - Backport tools ([sorenlouv/backport](https://github.com/sorenlouv/backport), [korthout/backport-action](https://github.com/korthout/backport-action),
    [Mergify backport](https://docs.mergify.com/workflow/actions/backport)): label → PR per branch. Mergify creates
    the PR **even on conflict** (`ignore_conflicts: true`), labels it `conflicts`, and assigns `{{ author }}` /
    `{{ merged_by }}`.
  - **Zuul** ([gating](https://zuul-ci.org/docs/zuul/latest/gating.html)): dependent pipelines test each change
    together with the ones ahead of it; a failing change drops out and the ones behind restart without it.
    `Depends-On:` keeps a dependent change from merging first.
  - **Mergify / Trunk / Bors** ([batches](https://docs.mergify.com/merge-queue/batches),
    [Trunk](https://docs.trunk.io/merge-queue/batching)): test a batch together, bisect on failure. Trunk's "pending
    failure depth" holds a failed item while its successors test, so a transient failure isn't evicted early.
  - **GitHub's own queue** ([docs](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/configuring-pull-request-merges/managing-a-merge-queue)):
    removes a failing PR and rebuilds the next group without it; no wildcard branches.
- **Spark does not copy Angular's untested direct push.** A pick reaches a line only through a backport PR that
  passes the merge queue's checks on that line (owner, 2026-11-10: "cherry-picks into the release branches aren't
  tested before they arrive, and they can go red... I'm not sure about this"). What Spark copies is the **order**:
  picks land in master's order (§3.2).

## 2. Friction points (all verified, file:line)

### F-A. Selection: which lines a PR goes to
1. **Labels are decided once, when the PR opens.** `CodeCoverage/PackageVersioning/BackportTargetsRecipients.cs:89-97`
   labels only on open and only if no `backport` label exists; later pushes refresh a neutral check only
   (`:99`, `BackportTargets.cs:59`). #605 opened as a tiny draft (`size/XS`), its library changes came later.
2. **Labels are never corrected or removed** after open (`BackportTargetsRecipients.cs:56`).
3. **Tests don't count.** Ecosystems come from changed package inputs
   (`CodeCoverage.Versioning/Releases/ChangedEcosystems.cs:16-20`); `tests/` is outside every package
   (`ChangedEcosystemsTests.cs:47-51`). #593 changed .NET tests, got only `angular`.
4. **Master-only paths are backported.** `apps/**` deploys from master, yet #608's dotnet/angular conflicts were in
   `apps/MintPlayer/.../song-header.*`, #593's in a CodeCoverage app file; #583 (workflows/tools only) was labelled
   for both lines.
5. **A failed ecosystem lookup only logs** and labels nothing (`BackportTargetsRecipients.cs:117-143`).

### F-B. Execution and tracking
6. **A conflict leaves only a comment** (`BackportRecipients.cs:238-239`): no label, no stored record (records exist
   only for opened PRs, `:224,252`), no retry, no backlog. Later backports run blind.
7. **No ordering.** Each backport cherry-picks one commit onto the tip (`BackportRecipients.cs:143,322-324`); nothing
   notices that it depends on an unported predecessor.
8. **A leftover `backport/N-line` branch blocks a retry** ("exists already; nothing was pushed",
   `BackportRecipients.cs:232-233`).
9. **Retry by label needs remove + re-add**: the trigger is the `labeled` event
   (`CodeCoverage.GithubIntegration/Recipients/GitHubEventsRecipient.cs:519`).
10. **Drafts are never reminded**; a closed one keeps its record forever (`BackportLinkRecipients.cs:79`). A
    hand-pushed branch opens its PR only on the first push (`:45`).
11. **Errors from GitHub or the git writer are only logged** (`BackportRecipients.cs:87-94`).
12. **No view of what master has that a line lacks.** `backported:` labels appear only when a backport merges
    (`BackportLinkRecipients.cs:97-99`).
13. **Platform markers in added files** aren't normalised: #620's new `InMemory`/`RavenDB` csprojs add `net11.0`
    to the line (D8's rule only covers values the PR left unchanged).
14. **Every shipped PR runs CI twice** (master + line), accepted under D16.

### F-C. Tags and versioning
15. **Nothing reacts to a tag push.** No workflow has `on: push: tags` or `on: create`
    (`.github/workflows/dotnet-build-master.yml:3-9`); the app has no `create` case (`GitHubEventsRecipient.cs:42-93`)
    and its push handler stops at anything not under `refs/heads/` (`:337-343`). By design:
    "a manual tag on a commit no workflow runs on is never published" (`docs/coverage_package_versioning_PRD.md:149-154`).
16. **A hand tag on a master commit is not a supported gesture, but it still has effects.** The engine reads every tag,
    reachable or not (`CodeCoverage.Versioning/Releases/TagHistory.cs:18,45`), so the tag becomes the line's "last"
    version (`ReleasePlanner.cs:209`) and the line diffs against master's commit (`:218`). The pack reads only
    reachable tags (`MintPlayer.Versioning.MSBuild.cs:91`), so engine and pack disagree.
17. **The hand-tag branch runs before the lines-only refusal** (`ReleasePlanner.cs:185` before `:306`): re-running
    master's workflow on a hand-tagged commit would publish from master, bypassing D15.
18. **A re-cut or force-pushed line orphans its tags** from the pack's reachable set while the engine still counts
    them (fingerprint changes, `TagHistory.cs:29-34`). Line tags such as
    `MintPlayer.Spark.Abstractions/v11.0.0-preview.117` are not reachable from master today (321 tags reachable from
    `release/dotnet-11.x`, 288 from master).
19. Pushing more than three tags at once creates no push event (`docs/coverage_package_versioning_PRD.md:44`).

### F-D. The scenario asked about: a commit is assigned to a line after its child is already there
Master has C then D (D a descendant of C). D was already cherry-picked onto the line; later someone realises C
should have gone too (by tag, label or `/backport`).
- **Today:** a tag does nothing (F15/F16). A label or `/backport` cherry-picks C onto the line tip, i.e. **after** D:
  reverse order (`CodeCoverage.GitWriter/Jobs/BackportService.cs:28-41,213-215`). There is no duplicate detection by
  provenance or patch-id; `CherryPickedFrom` only links the source PR after merge (`BackportLinkRecipients.cs:172`).
  The only guard is "the result changes nothing" (`Empty`, `:127-129`).
- **What can go wrong:** if D's port was hand-resolved without C, applying C afterwards conflicts or silently
  reintroduces code D's port had adapted. If D's port already pulled C's lines in, C applies as a partial duplicate.
- Proposed behaviour: §3.4.

## 3. Proposed design

### 3.1 The current line follows master (removes F6–F14 for the current majors)
*This reverses D16's "fast-forward rejected"; owner decision Q1.*

While master's platform major for an ecosystem equals a line's major, that line **is** master: the app advances it by
**fast-forward** to master's latest green commit. No cherry-picks, no backport PRs, no conflicts, CI once.
- **Trigger:** master's `dotnet-build-master.yml` run goes green → the app moves `release/<line>` to that SHA, only if
  the line's tip is an ancestor (lease). Optional batching; a "release now" gesture moves it on demand.
- **CI:** the `release/**` run skips build/test when the same SHA is already green on master and runs only versioning.
- **Rulesets:** the App needs a bypass on the line's `update` rule (the queue App is already exempt on the
  restrict-updates ruleset for the merge queue).
- **The split:** the merge that moves master to net12 auto-cuts `release/dotnet-12.x`, which follows master;
  `release/dotnet-11.x` stops following and from then on receives cherry-picks (§3.2). Per line, so
  `angular-22.x` keeps following until Angular 23.
- **Diverged line:** a hand-pushed commit on a following line breaks fast-forward. The app marks the line `diverged`,
  refuses to move it and says why; never auto-merges.
- **Why not merge commits (b'):** the merge queue always squashes (`MergeQueue/MergeQueueEngine.cs:1506`), which
  drops master as a parent, so every sync would conflict again; direct merge pushes add a new commit per sync (CI
  twice) and still need a bypass.
- **Why not release from master and cut lazily (c):** undoes D15, couples releases to app deploys, and line-only tags
  are unreachable from master.
- **Migration (once):**
  1. `git range-diff 5fd324b13..<line>` against the master originals proves the lines add nothing master lacks
     (the 9 backports are copies). A real `git merge master` into a line conflicts in ~13 files (backports resolved
     differently), so the migration must not be a content merge.
  2. An admin pushes one `-s ours` merge commit to master: master's tree, parents master + both line tips. Master's
     content is unchanged and every line tag becomes reachable from master.
  3. Fast-forward both lines to master.

### 3.2 Lines that no longer follow master: a ledger, ordered and self-healing (F6–F12)
- **Ledger:** one persistent record per (line, source PR), seeded from master's first-parent log since the split.
  States `pending`, `blocked`, `conflict`, `draft`, `done`, `skipped`, `error`. `done` by `-x` trailer or
  "Backport of #N" body; patch-id as fallback only (marker normalisation changes patch-ids). `error` replaces
  "only logged".
- **In-order delivery per line (owner direction, 2026-11-10; to be discussed further, not decided).** Backport PRs
  into a release line merge in master's first-parent order, one after the other. A red or conflicting one is **not
  evicted**: it keeps its position and nothing behind it on that line merges until it is fixed or explicitly skipped.
  PRs into master and other branches keep today's eviction on red.
  - **Why order:** a pick applies cleanly when the line already holds every earlier master change, because the
    context it was written against is there. Reordering is what produced the #620 cascade (#605 skipped, so #603,
    #607, #620 conflicted; #605 itself conflicts on a file #593 changed first, §1). Angular keeps the order (337/337
    on `22.1.x`, §1b). ASP.NET Core reorders, but only fixes.
  - **Only that line stops.** Master, the other lines and app deploys continue; the line's package releases wait.
  - **Loud:** label `backport: needs attention`, the source PR's author assigned, the line shown as blocked in the
    app with a link, and a failing check on master PRs: "release/dotnet-11.x blocked by #N".
  - **A conflict becomes a PR too**, a draft with the conflict markers in the same position, instead of the comment
    it gets today (`BackportRecipients.cs:238-239`). The developer resolves it on that branch.
  - **The PRs waiting behind it** get `backport: blocked by #N` (Node.js's `backport-blocked-vN.x`), so every one
    of them says why it isn't moving.
  - **Board column (owner idea, 2026-11-10):** a setting in the app picks a project-board column (a Projects v2
    Status option) that held backport PRs are moved to, and one they return to when merged. This reuses the existing
    board automation (`ProjectAutomationRecipient.cs`, rules with a `TargetColumnOptionId`, "Sync columns") with new
    triggers "backport held" and "backport merged" next to `EWebhookEventType`'s pull-request events. Limits: Apps
    can set a single-select field with `updateProjectV2ItemFieldValue`, but the `projects_v2_item` webhook exists
    only for organization boards ([docs](https://docs.github.com/en/webhooks/webhook-events-and-payloads#projects_v2_item)),
    and App access to **user-owned** boards fails in community reports (unverified). MintPlayer is an organization.
  - **Title marker (owner idea, 2026-11-10):** the app prefixes a held PR's title with `⚠️` and removes it when the
    PR turns green. The marker is owned by the app (a person's own edit is kept). The queue must squash with the
    title **without** the marker, or it ends up in the line's commit subject and release notes. The label stays the
    source of truth; the title and board are views of it.
  - **One automatic re-run** before a red result counts as blocking.
  - **Explicit way out:** `/backport skip <line>` records that the change deliberately doesn't go to that line
    (state `skipped`) and releases the line. A skip is a visible decision, never a silent loss like #605.
  - **Later ones wait prepared:** their ledger items exist (`pending`), but each is cherry-picked and tested on the
    line only after the one before it merged, so it applies in master's order on the real tip.
  - **Cost:** one forgotten red PR freezes that line's releases; the attention signals above are what keeps it from
    being forgotten. D6 still holds between lines: one line blocking never stops another.
  - **Interaction with Q1:** on the current line every change goes in (D16) in master's order, so with nothing
    skipped the line ends at master's exact tree, one PR at a time with CI run twice. That is the result of a
    fast-forward, reached more slowly. In-order delivery is therefore the D16-kept variant; Q1 is the D16-reversed one.
- ~~**Ordering by path overlap:** an item is `blocked` only when an earlier, targeted, unfinished master commit touches
  the same paths; it unblocks itself when that one finishes.~~ **Superseded 2026-11-10 by in-order delivery above:**
  a Q that depends on P's behaviour without touching its files would pass the overlap rule and fail.
- **Retry:** `/backport retry <line>` and a `requested_action` button on a "Backports" check run on the master
  commit. No more remove + re-add.
- **Stale branches:** a leftover `backport/N-line` with only bot commits and no open PR is deleted and redone; with
  human commits it is opened as their resolution.
- **Drafts:** a scheduled sweep reminds after N days.
- **Visibility:** a "line drift" check on each line (master PRs since the split neither `done` nor `skipped`), a
  pinned issue per line kept current, and a Spark page over the ledger.
- **Added files:** platform markers in files the PR **adds** are set to the line's value when they equal master's
  current value (#620's `net11.0` csprojs).

### 3.3 Selection decided at merge, from the real diff (F1–F5)
- On every push the app adds/removes **only labels it added itself** (it records which); a label a human added stays,
  a line a human removed stays excluded.
- At merge (`OnMergedWithBackportLabelsAsync`) the targets are: lines computed from the **squash commit's diff**
  + human-added − human-removed. A stale label can no longer lose a backport (#605).
- **What counts:** package inputs; **test projects count for the ecosystem of the packages they reference**
  (project graph; fixes #593); shared build inputs count for every ecosystem (`Directory.Build.*`, `global.json`,
  root `package.json`/lockfile).
- **Master-only paths** (`backport.masterOnly` in coverage.yml, default `apps/**`, `.github/workflows/**`, `docs/**`)
  count for nothing **and are removed from the patch** by resetting them to the line's copy (the D9 step,
  `BackportService.cs:85`).
- A failed ecosystem lookup makes the check **fail visibly** instead of logging.
- **A forgotten target can't be lost (owner question, 2026-11-10: "what happens when a user (or we) forgot to apply
  the correct target labels on a pull-request before it's squashed?").** Proposed, after Node.js (§1b):
  - **The default is "lands".** Every master commit since the split is a ledger item per line. One whose diff is
    relevant to the line (the rule above) is targeted automatically, label or not. Labels only override.
  - **Not landing is the explicit act:** `/backport skip <line>` (or removing the label the app added), recorded as
    `skipped` with who and when. A commit with no relevant paths is recorded `not applicable` by the app, with the
    reason (e.g. "only `apps/`, `docs/`").
  - **With in-order delivery (§3.2) nothing can pass an undecided item.** If the computation fails or an item is
    neither targeted nor skipped, the line holds at that item and shows "needs a decision". Forgetting becomes a
    visible stop, never a silent gap.
  - **Adding a line late keeps working:** a `backport:` label or `/backport <line>` on a merged PR already starts a
    backport today (`GitHubEventsRecipient.cs:519`, `:580`). If later master commits are already on the line, it is
    picked on the tip (§3.4) and conflicts become a held draft PR like any other.

### 3.4 A commit assigned after its descendant is on the line (F-D)
Steps, in order:
1. **Already present?** `-x` trailers on line commits since the split, then patch-id of the normalised diff, then
   "the three-way merge leaves the tip's tree unchanged". Any yes → mark `done` / label `backported:` without a PR.
   **S4 found that today's line commits carry no trailer** (#580, #586, #609 are squashed backport PRs titled
   `[dotnet-11.x] … (#581)` with the backport PR's number, §1). The cause: the backport branch's commit does carry
   `(cherry picked from commit …)` (`CodeCoverage.GitWriter/Jobs/BackportService.cs:340-351`), but the queue's squash
   message is `<title> (#backportPR)` plus `Co-authored-by` only (`MergeQueueEngine.cs:1936-1945`), so it is lost. So: the backport squash message must carry a
   `Backport-of: <master sha> (#<source PR>)` trailer, the ledger records source PR → backport PR, and for the
   existing commits a one-off match on the subject with the `[line]` prefix and `(#N)` stripped.
2. **Otherwise pick C on the tip.** The cherry-pick's merge base is C's parent, so reverse order is harmless unless
   the edits overlap.
3. **On conflict, pick the closure:** the master commits since the split that are not on the line, are targeted or
   eligible for it, and touch the conflicting paths, in master order, each its own commit with its own trailer, in one
   PR. Capped (e.g. 3 commits / 30 files); beyond the cap or still conflicting → draft.
4. **Never rebuild or force-push the line.** Its commits are tagged and published.

With §3.1 this scenario cannot arise on a following line: the line is a prefix of master.

### 3.5 Tags (F15–F19)
- **The gesture to add a forgotten commit is the label or `/backport <line>` on the merged PR, not a tag.** Tags mean
  "this version" and belong to the engine.
- **A tag-push webhook** (handle `create`/`push` with `refs/tags/`): a version-shaped tag pushed by a human on a commit
  that no line contains gets a warning (commit comment or issue) pointing to `/backport`, and is **ignored** by the
  engine. Never deleted automatically.
- **Version history from reachable tags only:** `history` and `lastOnLine` (`ReleasePlanner.cs:177,208-209`) use tags
  reachable from the line (`for-each-ref --merged`). Uniqueness and the D17 fingerprint keep reading all tags; the
  create-only push stays the backstop. Pre-split master tags stay reachable from the line, so they still count.
- **Lines-only check first:** move it before the hand-tag branch (`ReleasePlanner.cs:306` before `:185`).

### 3.6 What the queue holds today, and the smallest change (spike S8, code reading 2026-11-10)
- **Model:** one `MergeQueue` document per repository with **one lane per base branch**, each with one `Current`
  entry, an ordered `Waiting` list and a `Paused` flag (`CodeCoverage.Library/Entities/MergeQueue.cs:3-30,95-121`).
  Lanes are strictly serial, no batching (`MergeQueueEngine.cs:1692-1715`). Lanes are already independent, so a
  held release line never stops master.
- **Eviction today:** red checks (after `RetryFlaky` re-runs, default 1, `:1414-1434`) → `EvictAsync("checks-failed")`
  (`:1436`); a conflict → `EvictWriterAsync` (`:890`) → `EvictAsync` (`:1647`). `EndAsync` clears the entry, deletes
  `mq/{base}/pr-{n}`, removes the label, comments "Queue it again with `/queue`" and starts the next
  (`:1653-1688`).
- **Smallest change for in-order delivery (§3.2):**
  1. In `EvictAsync`, for a base matching `entry.Config.LinePattern` (`MergeQueueEntry.cs:327`), set a new `Held`
     state, keep `lane.Current`, and don't call `StartNextAsync`.
  2. Resume automatically on a push to the PR's branch or when its checks turn green; no `/queue` needed.
  3. Insert into `Waiting` by master order instead of appending (`:317`). This needs the source merge SHA on
     `BackportPullRequest` (today only `Source`, `Branch`, `OpenedUtc`: `BackportPullRequest.cs:24-43`).
  4. Keep the `Backport-of:` trailer in the squash message (`:1936-1945`, §3.4).
- **No backport list exists in the app** (no Spark query or model over backports); the repository page has only the
  merge-queue panel (`ClientApp/src/app/components/repo-merge-queue-panel/`).

### 3.7 Testing and CI cost (measured 2026-11-10)
- **Every backport PR runs the full suite, and so does the line push after it.** `pull-request.yml:4-6` triggers on
  `master` and `release/**` without path filters and calls `test.yml` in full; `dotnet-build-master.yml:4-9` runs
  the tests again on the push before the release job (#544 D3).
- **Measured (job-minutes = the sum of all jobs' durations):**

  | Change | Master PR | Master push | Backport PR | Line push |
  |---|---|---|---|---|
  | #609 → #610 (code) | 73.2 (run 42619954148) | 69.5 | 80.3 (42621020791) | 83.7 (42622887029, failed) |
  | #583 → #585 (tooling) | 37.6 | 38.5 | 39.1 | 33.2 |

  A code change that reaches one line costs about 300 job-minutes; each extra line adds about 160.
- **Minutes are free, the runner cap is not.** Standard GitHub-hosted runners are free for public repositories;
  larger runners are billed even there ([billing](https://docs.github.com/en/billing/managing-billing-for-your-products/managing-billing-for-github-actions/about-billing-for-github-actions)).
  Every job here is `ubuntu-latest`. A free organization gets **20 concurrent jobs**
  ([limits](https://docs.github.com/en/actions/reference/limits)), and one run is ~21 jobs (CLAUDE.md), so each
  backport run occupies the whole cap and makes master PRs wait. The real cost is wall time.
- **The Nx cache doesn't help the tests.** #610's run had 107 cache hits, all `:build`; 0 `:test`. Tests are
  cacheable (`nx.json:59-60`), but no earlier run had the same inputs: the line's tree differs from master's (it
  lacks the unported commits), and only master pushes may write the remote cache (`test.yml:26`), so the line push
  can't reuse the queue run of **the same tree**.
- **Cache change done 2026-11-10 (PR #622, owner request):** the cache moved to `public.nx-cache.mintplayer.com`
  (VPS `/var/www/public-nx-cache`): reads need no token, a `PUT` without `NX_CACHE_RW_TOKEN` goes to a sink and is
  discarded, the RO token is retired, and the leaked tokens were rotated. Fork PRs now get cache hits (PR #622's
  "build + verify": 71 remote hits, 0 errors). ~~Writes stay master-push only. Letting PR or queue runs **write** was
  rejected for the cache-poisoning risk~~ ([CREEP](https://nx.dev/blog/cve-2025-36852-critical-cache-poisoning-vulnerability-creep)).
  **Superseded by Q16 = B** (owner, grilling 2026-11-10, confirmed 2026-11-11: "PR runs write - as long as it's not
  from a fork"). Same-repository PR runs write too, except the dependency-update App's `mintplayer/dependencies*`
  branches, and the line's release build skips the cache, so a poisoned entry can at worst fake a green test
  (segment H). "Test each tree once" (item 1 below) reuses the queue's **verdict**, not cache entries.
  Open gap, owner's call: `NX_CACHE_RW_TOKEN` is an org secret passed by `secrets: inherit`, so a same-repository PR
  run (e.g. an App's dependency update) can reach it by editing its own workflow; an environment limited to `master`
  would close it (`docs/guide-nx-remote-cache.md`).
- **Proposed:**
  1. **Test each tree once.** The queue tests `mq/{base}/pr-{n}` and squashes exactly that tree. The push run on the
     line, and on master, re-tests an already-tested tree. Let the release job accept "a green queue run exists for
     this tree hash" instead of re-running (Q14). Saves ~80 job-minutes per backport and the same on every master
     merge.
  2. **Under Q1, a following line costs nothing:** it moves to a SHA master already tested.
  3. **On a diverged line, batch in order:** test the next N waiting picks together in one run (Zuul/Mergify, §1b),
     bisect on red. In-order delivery makes a batch natural: it is just the next N in master order.
  4. Keep the full suite before every release. A reduced set per pick (ClickHouse) is not proposed: the per-tree
     rule above already removes the duplicate runs without lowering the bar.

### 3.8 The code owner's work, today and proposed
**Today**, per merged PR and line (S8 code reading):
1. Label the PR `backport: <line>` (or rely on labels set at open, `BackportTargets.cs:9,46`), or `/backport` after merge.
2. A writer **approves each clean backport PR** before the App queues it (approval floor `Always`,
   `MergeQueueEngine.cs:555-583`).
3. A backport with stale regenerated files is not auto-queued; someone queues it (`BackportRecipients.cs:222-223`).
4. On conflict: read the comment, then resolve by hand and push `backport/<pr>-<line>`, or `/backport <line> --draft`
   (`BackportRecipients.cs:330-340`); resolve the markers, mark ready, approve (`:246,251`).
5. After an eviction: fix, then `/queue` again (`MergeQueueEngine.cs:1655`).

**Proposed: zero steps on the happy path, one place to look otherwise.**

| Today's step | Proposed | Section |
|---|---|---|
| 1. labels | targets computed at merge, default lands; `/backport skip` is the only gesture; an hourly sweep (ClickHouse) re-checks every line against the ledger in case a webhook was lost | §3.3, Q11 |
| 2. approve each clean pick | no second approval when the pick's patch equals the approved source's (patch-id, after stripping master-only paths); a pick that needed changes still needs one | Q13 |
| 3. stale regenerated files | the regeneration finishes, then the App queues it | §3.2 |
| 4. conflicts | always a draft PR in its position, assigned to author + merger, board column, ⚠️, reminders at 3 and 7 days (never auto-closed: the line waits on it) | §3.2, Q12 |
| 5. `/queue` after eviction | no eviction on lines; the held entry resumes on push or green checks | §3.6 |
| — finding out what's stuck | one **Backports** page per repository: per line, every master commit since the split as done / held / pending / skipped / not applicable, with *retry* and *skip* buttons; and a failing "line blocked by #N" check on master PRs | §3.2 |

## 4. Decisions for the owner

| # | Question | Recommendation | Trap of the recommendation |
|---|---|---|---|
| Q1 | Reverse D16: the current line follows master by fast-forward (§3.1)? | Yes. History: 11/21 commits never shipped, 0/6 conflicts finished; D16's cost (CI twice) is also gone | One mechanism becomes two (follow, then cherry-pick after the split); a diverged line must be handled |
| Q2 | Migration by one `-s ours` admin merge on master (§3.1)? | Yes, after S3 proves the lines add nothing | Needs a ruleset bypass once; a wrong range-diff would hide a line-only fix |
| Q3 | Labels recomputed at merge from the squash diff, human choices kept (§3.3)? | Yes | Without the "who added it" record, recompute erases human choices |
| Q4 | `apps/**`, workflows, docs are master-only and stripped from backports? | Yes, configurable | A docs fix that should ship with a line's package README is dropped; packages' own READMEs live under `libs/` so stay |
| Q5 | On conflict, pick the closure of unported predecessors automatically (§3.4)? | Yes, capped | The closure can grow into a whole feature |
| Q6 | Tag gesture: warn + ignore, gesture stays `/backport`? | Yes | People keep pushing tags if the warning isn't seen |
| Q7 | Version history from reachable tags only (§3.5)? | Yes | Must keep pre-split master tags (filter by reachability, never by branch name) |
| Q8 | Release-line backport PRs delivered **in master's order and never evicted**: a red or conflicting one keeps its place and blocks only its own line until fixed or `/backport skip` (§3.2). Owner direction 2026-11-10, open for further discussion | Yes, with the attention signals, one auto re-run and the skip gesture | One forgotten red PR freezes that line's releases; a long-blocked line accumulates a queue that all needs re-testing |
| Q8a | ~~Angular-style atomic backport (dry-run pick on every target before the master merge, then direct push)~~ | **Dropped 2026-11-10:** Angular's picks are pushed untested (§1b); Spark keeps queue-tested backport PRs. The dry-run idea can return as an early warning only | — |
| Q10 | Should the master merge be refused when the trial pick onto a line conflicts (Angular's `_assertMergeableOrThrow`), or only warn on the PR? | Open; warn only, since in-order delivery handles the conflict on the line | Refusing ties master's pace to the lines; warning only means the conflict is found later |
| Q11 | Default "lands": every relevant master commit is targeted automatically, and `/backport skip <line>` is the only manual act; an undecided item holds the line (§3.3, Node.js model) | Yes | A change someone meant to keep off a line ships unless they skip it; with D16 that is today's intent anyway |
| Q12 | Held backport PRs: board column picked in the app + `⚠️` title prefix + `backport: blocked by #N` on the ones waiting (§3.2) | Yes; the label is the source of truth | The title prefix leaks into the line's commit subject unless the queue strips it; org boards only |
| Q13 | A clean pick whose patch equals the already-approved source PR's needs no second approval (§3.8) | Yes | A clean pick can still behave differently on an older line; CI on the line is then the only guard |
| Q14 | Test each tree once: the line/master push skips the suite when a green queue run exists for the same tree hash, and the release job relies on it (§3.7). Changes #544 D3's "tests before release in the push" | Yes, after a spike that proves the tree hash matches | A run that was green for the tree but on a different runner image is trusted; a cache bug would ship untested code |
| Q15 | Batch in-order picks on a diverged line (test the next N together, bisect on red) (§3.7) | Later, only if lines diverge (Q1 rejected or past lines cut) | Bisection costs extra runs when failures are frequent ([Bors](https://forum.bors.tech/t/optimized-bisection/514)) |
| Q9 | Past lines take **fixes only** by default (`defaultLabels: fixes`, D5), as Angular and ASP.NET Core do | Yes | A feature someone wants on an old line needs an explicit label |

**Q8 and Q12 as built (segment F, 2026-11-11, `docs/release_lines_recovery_plan.md` "F done").** Three points differ
from the rows above, and the plan's segment F text is the one that holds:
- **The title prefix is dropped (Q12's trap):** a `⚠️` prefix would reach the line's commit subject unless the queue
  stripped it, so the held pull request gets only the `backport: needs attention` label.
- **The waiting PRs get a neutral check, not a label:** "Line blocked by #N" (the plan's name). It is completed as
  success when the hold ends, so nothing has to be removed by hand.
- **The board column is replaced by the app's held list** (`/query/held-backports`), which the hold comment links to.
  It works for user-owned repositories too, where an org board does not exist.
- **Reminders:** after 2 working days, and then once more to the code owners. The "3 and 7 days" in row 4 of the
  states table is superseded.

**Q1 as asked in the grilling session (2026-11-10, unanswered).** Q1 is asked first because Q8, Q11, Q13, Q14 and Q15
matter for the current line only if D16 stays.
- **A. Fast-forward (reverses D16):** while master's platform majors equal the line's, the App fast-forwards each line
  to every green master commit; cherry-picks start only after the split. The line keeps existing, so #568 M11 ("release
  only from lines") stays true. Needs the one-time `-s ours` migration (Q2).
- **B. Keep D16, made robust:** every change stays a backport PR, with in-order delivery (Q8), default lands (Q11), no
  second approval (Q13) and test each tree once (Q14).
- **C. No current line (reverses #568 M11):** release from master while the majors match; the App cuts a line only at
  the moment master moves to the next major. Release workflows and rulesets then switch branch at every cut.
- **Recommended: A.** B reaches master's exact tree anyway (S4: in-order picks, 0 conflicts), one PR at a time at about
  160 extra job-minutes per change. C reverses a decision made on 2026-11-06.
- **Trap of A:** it only holds while the line never gets a commit of its own. A line-only hotfix makes it diverge, and
  B's machinery is needed for the current line too. A therefore needs a hard rule, enforced by a ruleset: **nothing lands
  on a following line except by fast-forward**; a fix for the line goes to master first.
- **Also asked:** if A, does the owner accept "no line-only commits while the line follows master"?

**Decided in the grilling session (owner, 2026-11-10).** These supersede the rows above where they differ.
- **Q1 = B, D16 stays.** Every change reaches a line as a bot backport PR. Q2 (`-s ours` migration) is dropped: it
  existed only for fast-forward.
- **Q1b, current lines take no line-only commits.** Only bot picks of master commits land on a line whose platform major
  equals master's; a fix goes to master first. **Past lines accept human PRs** (version-specific fixes). Enforced by an
  App-bound required check "Line policy" on one static ruleset over `refs/heads/release/*`: the App decides per line
  from the SDK majors, so a platform upgrade edits no ruleset, and the .NET and Angular lines are judged independently.
- **Q1c, the App detects the split** from the SDK versions it already resolves: the upgrade's squash commit cuts the new
  line and makes the old one past. **Q1d, previews count** (owner: "I'm already publishing packages for preview-sdks, so
  A is pretty much a must"), matching how `dotnet-11.x` was cut on .NET 11 previews.
- **Q8, who fixes a held pick:** one automatic re-run, then the source PR's author; fork authors and dependency-update
  PRs fall back to the code owners of the changed paths. Reminder after 2 working days, then escalation to the code
  owners.
- **Q10, trial pick at the master merge: configurable per line, default warn** (`trialPick: warn | refuse | off`).
  Built in segment G (2026-11-11): owner, "In the queue, pre-merge" and "Labelled lines, PR comment". `backport.trialPick`
  is the default and `backport.trialPicks` sets it per line. The trial runs while the queued commit's checks run, and a
  trial that cannot run never blocks (`docs/release_lines_recovery_plan.md`, "G done").
- **Q12, attention signals (replaces the board column and the ⚠️ title):** the database is the source of truth. The bot
  posts a comment with a link to a Spark CustomQuery in the app that lists, for the signed-in user, the held backport
  PRs of source PRs they authored plus all held backport PRs in repos they administer (filtered server-side by current
  GitHub access). GitHub also shows a `backport: needs attention` label on the held PR and an informational (not
  required) check "Line blocked by #N" on each waiting PR, both re-applied from the database. No title change, so the
  queue strips nothing.
- **Q13, no approval for backport PRs** (owner: "I really don't like manual approval … backporting features into
  previous/current versions should be effortless"). Made safe by a new ruleset on `refs/heads/backport/*` (restrict
  creation/updates/deletion, bypass: queue App and repository admins), so a backport PR's content is always the bot's
  pick or an admin's conflict resolution; set up by "Set up rulesets". A developer's own PR into a past line goes
  through the queue by `/queue` with master's approval rules.
- **Release build from source:** the line push's `build:release` / pack runs with `--skip-nx-cache`, so published
  packages and deploys never come from a cache entry; tests keep their cache hits (owner: "Publish and deploy is more
  reliable that way"). Q14's custom tree-hash verdict is replaced by Nx's own input hash.
- **Parked: Q11** (default lands, whole commits on current lines, nominations on past lines), with Q3, Q4 and Q9 that
  depend on it.

## 5. Spikes

Sandbox: a blank local repository at `C:\Repos\backport-resilience-sandbox` for offline git spikes; S1, S2 and S6
additionally need a throwaway GitHub repository with the CodeCoverage App installed, rulesets and the merge queue
(outward-facing: owner approval before creating it).

| # | Question | Where | Done when |
|---|---|---|---|
| S1 | Can an admin push an `-s ours` merge to a queue-protected master (linear-history and non-fast-forward rules)? Can the App fast-forward a line through an `update`-rule bypass? Do master's checks still count on the line after a fast-forward (checks attach to the SHA)? | GitHub sandbox | each answered with the API responses recorded |
| S2 | Versioning on a fast-forwarded line: both lines on one SHA, each releasing only its own ecosystem, no double tags; `TagWriter` ancestry holds; MSBuild sees `preview.117` after the `-s ours` merge | local sandbox + GitHub sandbox | the plan for both lines and the packed versions match expectations |
| S3 | Migration check: `git range-diff 5fd324b13..release/<line>` vs the master originals shows no line-only change | this repo, read-only | an empty "line-only" list, or the list to port to master first |
| S4 | Offline census: replay the 21 commits since the cut through §3.2–§3.4 (recomputed labels, master-only stripping, in-order delivery, closure picks). How many are ported, blocked, conflicted vs reality (7 PRs ported as 9 backport commits, 6 conflicts, 0 finished)? | local sandbox (clone) | a table per commit; target: no unfinished conflict caused by an unlabelled predecessor. **Git half done 2026-11-10** (§1: master order 0 conflicts, one skip/swap 2–3); label recompute and closure picks not replayed yet |
| S5 | Label recompute on #605, #593, #583, #608 against their squash commits | unit tests over the real diffs | #605 → both, #593 → both, #583 → none, #608 without its `apps/` files |
| S6 | `requested_action` retry button round-trip and permissions | GitHub sandbox | button re-runs one line's backport |
| S8 | In-order delivery in the merge queue (Q8): how `MergeQueueEngine` would hold a red or conflicting release-line PR in place instead of evicting it, per base branch, while master and other lines keep moving; how the PRs behind it wait and get re-picked on the new tip; where the "blocked" signals come from | this repo, read-only, then the GitHub sandbox | a design note with file:line of the eviction path and the per-branch ordering, and a sandbox run: P red → Q waits → P fixed → P, Q merge in order |
| S7 | Reachable-only tag history: compare both lines' release plans with and without the filter; time `--merged` over the full tag set | this repo, read-only | plans identical except the master hand-tag case; timing recorded |

## 6. Milestones (one PR)

- **M0** Spikes S3, S4, S5, S7 (offline), then S1, S2, S6, S8 (sandbox, after approval). Owner decisions Q1–Q15.
- **M1** Tag hygiene: reachable-only history, lines-only check first, tag-push webhook warning (§3.5).
- **M2** Selection at merge: label ownership record, recompute on push and at merge, tests by project graph,
  `backport.masterOnly` stripping, visible lookup failure (§3.3).
- **M3** Ledger and states, retry gesture and check-run button, stale-branch handling, error state (§3.2).
- **M4** In-order delivery without eviction on release lines, attention signals, `/backport skip`, conflicts as
  draft PRs; closure picks; already-present detection (§3.2, §3.4).
- **M5** Follow-master mode: green-master trigger, lease fast-forward, `diverged` state, CI skip on a green SHA, split
  handover to cherry-pick mode (§3.1).
- **M6** Migration on Spark: S3 check, `-s ours` merge, fast-forward both lines; backlog of today's unported
  commits resolved by it.
- **M7** Visibility: line-drift check, pinned issue per line, Spark ledger page, draft reminders; added-file marker
  normalisation; docs (`docs/code-coverage/`, backport and versioning PRDs marked superseded where they change).

## 7. Out of scope
- AI conflict resolution (backport PRD D7).
- Cross-repository chained backports (backport PRD D11).
- Cutting the past lines `dotnet-10.x` / `angular-21.x` (deferred by the owner, 2026-11-06).

## 8. Open items for the PR #620 backport itself
Under Q1 + Q2 the pending #620 conflict disappears with the migration (M6). If Q1 is rejected, the stopgap is to
backport the unported commits in master order. S4's E4 confirmed it: from the real `release/dotnet-11.x` tip, the 12
unported commits apply in master order with 0 conflicts (#580, #586, #609 are already there). The angular line was
not replayed.

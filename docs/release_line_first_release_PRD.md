# First release on an older release line — PRD and plan (#638, closes #600)

Status: **planned and grilled (§7), nothing built** (2026-11-12). One pull request. Issue: #638. Its Problem 1 is issue #600 (open
since 2026-11-07, deferred in [release_lines_recovery_plan.md](release_lines_recovery_plan.md) §6 "until a past line
is cut"). Tools has now cut one, so this plan closes #600 as well.

Paths are relative to `apps/CodeCoverage/` unless they say otherwise. Line numbers were checked at `835a40b67` by a
three-agent read-only investigation on 2026-11-12. Where they differ from the issue, this file gives the right ones.

## 1. Why

On MintPlayer/MintPlayer.AspNetCore.Tools, the first publish-release run on `release/dotnet-10.x` released nothing and
still went green (Tools run `43198625715`, `8b1472c`):

1. The engine planned `10.0.0` for 14 packages ("first release on the 10.x line") and pushed `<id>/v10.0.0` tags.
   All 15 packages already have `10.0.0` on nuget.org, and 6 have `10.0.1`.
2. `dotnet pack` stamped the v11 versions (`11.0.1-rc.1`, Endpoints `11.4.1-rc.1`), because master's v11 tags are
   reachable from the line.
3. Every `dotnet nuget push --skip-duplicate` answered "already exists".
4. The run finished green with nothing published.

The same migration found two more gaps. A PR that moved master to `net11.0` only was auto-labelled
`backport: dotnet-10.x`. An older line cut from a multi-targeting master inherits the newer TFM, and nothing narrows
it or warns. The issue has the full evidence tables. This file does not repeat them.

## 2. Verified root causes (2026-11-12)

| # | Problem | Where (verified) | Notes from the investigation |
|---|---|---|---|
| P1 | The pack resolver ignores the line's major | `CodeCoverage.Versioning.MSBuild/build/MintPlayer.Versioning.MSBuild.cs:91` (`git tag --merged HEAD`), `:106`, `TagVersion.Highest` at ~`:188` (issue: `:192`). MPV005 at `.targets:85-86` fires only on the placeholder path. | Tests live in **`CodeCoverage.Versioning.Tests/MSBuild/`** (`TagVersionTests.cs:30-82`, `RealPackTests.cs:44-54`), not `CodeCoverage.Tests/MSBuild` as the issue says. |
| P1b | The same blind spot in `all-versions` and the hand-tag rule | `versioning-action/src/main.ts:226-231` (`allVersionsOf`). Hand-tag rule: **`CodeCoverage.GitWriter/Jobs/PromoteService.cs:232` `FollowProblem`, `:247-252` `PredecessorAsync`** (`tag --merged`), also used from `CodeCoverage/PackageVersioning/ReleaseVersions.cs:160-166`. | The issue named only the docs (`docs/code-coverage/versioning.md:316-328`). |
| P1c | The action exports no line major | `main.ts:82-84` (`buildEnvironment`, only `MintPlayerVersioningDependencyRanges`), `exportVariable` ~`:393-395`. | The line pattern is configurable (`versioning.lines.pattern`, `CodeCoverage.Versioning/Releases/ReleaseLine.cs:34-113`, `TryMatch` `:87`). Parsing `GITHUB_REF_NAME` in the targets would hard-code the default pattern. |
| P2 | The first release on a line ignores published versions | `CodeCoverage.Versioning/Releases/ReleasePlanner.cs:208-212` (`lastOnLine` from tags), `:238-241` (`Existing` = tags only). `Versions/VersionCalculator.cs:6` documents `Existing` as tags **and published versions**. `:59-60` picks `major.0.0`, and the `:75` guard never sees published versions. | `PublishedVersions.ExistsAsync` (`CodeCoverage/PackageVersioning/ReleaseVersions.cs:29`) already downloads flat-container `index.json` and throws away the list. It lives in the **App** assembly. The planner runs in the **engine** (`CodeCoverage.Versioning`), next to `PublicPackageRegistry` (`Build/PackageRegistry.cs`, anonymous, public only). |
| P2b | No API-hash baseline without a tag | `Build/BuildManifest.cs:46-56` fetches the published hash only for `last.Version` from tags. With no tag, `PreviousApiHash` is null and `ReleasePlanner.cs:236` sets `apiChanged = true`. | The hash is computed from the nupkg's `lib/<tfm>/*.dll` (or npm `.d.ts`), so versions published before manifests existed work too. |
| P3 | A run that published nothing goes green | `CodeCoverage/PackageVersioning/VersioningOnboarding.cs:553-600` (`PackAndPush`, `--skip-duplicate`), `:614-674` (`PublishGate`). | **Spark's own `.github/workflows/dotnet-build-master.yml` has the same gap:** gate `:216-220`, pack `:233`, pushes `:351`/`:354` with `--skip-duplicate`, and no packed-vs-planned check. `outputs.versions` is JSON `{"<id>":"<version>"}` for released packages, npm included (`main.ts:263-264`, `action.yml:59-60`). |
| P4 | Auto backport labels ignore platform moves | `CodeCoverage/PackageVersioning/BackportTargetsRecipients.cs:90-98` labels only on `Opened` when no label exists, and `:56` never removes one. `BackportTargets.cs:23-26`, `:62` `Applies` works on ecosystems only (`:125-129`). `BackportYml.cs:16-17,78` opts out per repository only. | `CodeCoverage.Git/Backports/PlatformMarkers.Normalize` already diffs TFM, framework package versions, `global.json` SDK, workflow setup versions and npm ranges (`PlatformChanges`, `Raises`). The git-writer sidecar already clones `refs/pull/N/head`. The check re-runs on every head, but the labels don't. Nothing records which labels the App added. |
| P5 | Older lines inherit the newer TFM | `CodeCoverage/Onboarding/OnboardingLineCuts.cs:51-60` uses the TFMs only to offer a line. [onboarding_line_cuts_PRD.md](onboarding_line_cuts_PRD.md) D3/D4 (`:36-37`) cut it as-is. | Reusable: `IForgeVersioning.CreateBranchAsync` (`CodeCoverage.Library/Forge/IForgeVersioning.cs:47`), `OpenPullRequestAsync` (`:51`), and branch pushes in `CodeCoverage.GitWriter`'s `BackportService`. `PlatformMarkers` can rewrite TFM, framework packages and SDK, but not `#if NETxx` or TFM-conditioned items. |
| P6 | Docs | `release_lines_recovery_plan.md` §2e (`:572`, G3-G6, so the next entry is **G7**). `docs/code-coverage/merge-queue.md:220`. `docs/code-coverage/versioning.md:105` ("What reaches the artifacts"). | Confirmed. |

## 3. Decisions (grilled with the owner, 2026-11-12)

| # | Decision | Evidence / reason |
|---|---|---|
| D1 | **The pack resolves the highest tag of the line's major, per package.** New optional MSBuild property `MintPlayerVersioningMajors` (`id=major;…`). A package it names takes its highest tag of that major only (`TagVersion.Highest(tags, prefix, major)`); a package it does not name (a free netstandard package, another ecosystem's) keeps its highest tag. *Superseded:* the single `MintPlayerVersioningMajor` of the first draft would have pinned free packages and other ecosystems to the line's major. | P1. A solution-wide `-p:Version` is out: it would give every package the same version. Owner, 2026-11-12: "Per-package map — enforcement only for .NET Core and angular/react/vue packages, and only on their own lines"; "a .net standard class library its major version shouldn't follow the sdk version". Red→green: `TagVersionTests.On_a_line_takes_the_highest_tag_of_the_packages_major_only`, `RealPackTests.On_a_line_the_pack_takes_the_highest_tag_of_the_packages_major`. |
| D2 | **The action sets the map, and the targets only fall back.** The planner records `ReleasePlan.LineMajors`: each package whose anchor is not free on a line of its own ecosystem (`branchMajor` non-null, `ReleasePlanner.cs:217`). The CLI (`LocalRunResponse.LineMajors`) and the server (`VersioningRunResponse.LineMajors`, sealed with a plan) carry it; the action exports `MintPlayerVersioningMajors` through `buildEnvironment`, and `allVersionsOf` and the npm stamps use the same filter (an npm package with only another major's tag is withheld). Without the action (empty map), the targets read `GITHUB_BASE_REF`, else the `gh-readonly-queue/<base>/pr-N-sha` base, else `GITHUB_REF_NAME`, against the **default** `release/dotnet-N.x` pattern only, and prefer each package's `N.x` tag when it has one, with no warning. | P1c: the pattern is configurable, and on a pull request `GITHUB_REF_NAME` is `123/merge`. Red→green: `ReleaseLinesTests.A_line_tells_the_pack_the_major_of_each_anchored_package_of_its_own_ecosystem`, `VersioningPlanReleaseTests.A_run_on_a_line_tells_the_pack_…`, `main.test.ts` "#638 D2" (both), `TagVersionTests.Without_the_action_a_dotnet_lines_major_comes_from_the_branch`. |
| D3 | **The same filter applies to `allVersionsOf` and to `PredecessorAsync`/`FollowProblem`.** On a line, a hand tag follows the highest tag of the line's major. | P1b. Without it, a person cannot place a `v10.x` tag on Tools 10.x: every one "follows" `v11.0.1-rc.1`. |
| D4 | **Git tags are the only source of truth.** The planner reads no registry. A line's last version, `Existing`, and the API-hash baseline all come from tags, as today. | Owner, Q1: "always git-tags. Git-tags are the source-of-truth". This replaces the registry-fed planner the issue proposed. |
| D5 | **Onboarding backfills tags from history.** The git-writer walks the history, reads each package's declared version per commit (`ProjectGraphBuilder`/`DeclaredVersion`, as `VersioningOnboarding.Seed` does today at `:194-207`), and tags **every version** at its **earliest occurrence**. The tagged check moves from per package (`:90-94`) to per (package, version). | Owner, Q1 ("the onboarding process needs to seek the libraries in the repo, determine the version per-commit and tag the earliest occurance") and Q11 (A, every version, not only the highest per major). |
| D6 | **History walked:** the default branch plus every branch that matches the line pattern (renamed old lines included). Feature branches are never walked. | Owner, Q2 (B). Versions of 6.x/7.x were declared only on those lines. A feature branch can declare a version that never shipped. |
| D7 | **Where a tag goes:** for a version of major N, the earliest occurrence on `release/*-N.x` when that line exists (its ancestry includes the default branch up to the cut), otherwise on the default branch. A version found on neither falls back to the earliest commit on any walked line, with a note. The backfill runs **after** the line cuts at the onboarding merge (line-cuts D7 order: settings, renames, cuts, **backfill**, workflow PRs). | Owner, Q3 (B). This guarantees every N.x tag is reachable from the N.x line, which the pack (`tag --merged HEAD`) needs. |
| D8 | **Existing repositories get it by re-running Set up features.** It is idempotent: it plans a backfill whenever a (package, version) is missing its tag. With no files to change, it runs at once. There is no lazy backfill in the versioning run and no separate action. | Owner, Q4 (A). History scanning needs full clones and the App's bypass, which the git-writer has and a shallow `mode: local` CI clone does not. |
| D9 | **App-made collisions are moved after the owner confirms.** When an App-made tag (tagger "CodeCoverage versioning") names a version that history declares at an earlier commit, Set up features lists it ("`Hsts/v10.0.0` was released again at 8b1472c; history declares it at abc123") with **Move it** (the default) or **Leave it**. Move **force-pushes** the tag onto the earliest occurrence. Tags pushed by a person are never touched. No registry hint is shown. | Owner, Q5: "B - force-push tags allowed". This fixes Tools' 14 bad `v10.0.0` tags without hand work, which ruleset protection would block anyway. |
| D10 | **MPV006, a warning:** "on line N.x, `X` has no tag of major N; run Set up features to backfill". It applies when the major is known and no tag of it is reachable, and it is opt-in as an error through MSBuild's standard `MSBuildWarningsAsErrors`. Spark's `Directory.Build.props` and the onboarding template turn it on for now. Removing that line once things are stable makes it a warning again. | Owner, Q6: "WarningAsError behavior, let the user opt-in … enable this flag at first, and disable it when everything stabilized". |
| D11 | **Publish assertion, before the push.** In the onboarding template **and** in Spark's `dotnet-build-master.yml`, a step after the pack reads `outputs.versions` and fails when a released NuGet package has no `.nupkg` at its planned version. The expected file name is built from the planned map, not parsed. **NuGet only**: npm ids (the keys of `outputs.dist-tags`) are skipped, because the action stamps the released version into the npm dist copy itself, so an npm pack cannot carry another version. `--skip-duplicate` stays. | P3. The investigation found Spark's own workflow open to the same silent no-op. |
| D12 | **No registry check after the push.** | Owner, Q7 (B). With D1, D5 and D11, "already exists" means a tag was missing, and the backfill plus MPV006 catch that. A post-push check would add nuget.org indexing delays and flakiness. |
| D13 | **Platform moves are fixed in the backport, not at labelling.** New `PlatformMarkers` case: when the pull request's value **no longer contains the line's major** (drops `net10.0`, moves the SDK or a framework package off 10), base and change take the line's value, and the change is reported as "kept the line's net10.0". The App keeps labelling as today. A mixed PR backports its fix cleanly. A backport that comes out **empty** after normalization closes with "nothing left to backport after normalization", never as an empty PR or a failure. | Owner, Q8: "we should be perfectly able to determine whether the targetframeworks identifier changed", then Q8b (A). It refines `PlatformMarkers.cs:36-38` ("left to git") for the drop-the-line's-major case only. An edit that keeps the line's major (`net10.0` → `net10.0;net11.0`) is still left to git. |
| D14 | **No per-PR opt-out and no label re-evaluation.** A person removes a label by hand. The App never re-adds one (`BackportTargetsRecipients.cs:90-98` labels only on open). | Owner, Q9: "If you're certain we can drop both … you can do so". D13 makes a wrong label harmless. |
| D15 | **Narrowing PR for an older line.** After an older line is cut from a commit that also targets a higher major, the App opens a pull request into the line. It drops the higher TFMs, sets `global.json` to the latest SDK of the line's major, and moves framework packages (including `Directory.Packages.props`) to the line's major. The PR body lists every `#if NETxx_OR_GREATER` and TFM-conditioned item for a person to resolve. The cut confirmation (line-cuts D6) and the onboarding PR body say so too. | Owner, Q10 (B). Tools #55 was this PR, done by hand. |

## 4. Spikes

| Spike | Question | How | Feeds | Result |
|---|---|---|---|---|
| S1 | How does `MintPlayerVersioningMajor` reach the RoslynCodeTaskFactory task? | Red test first: a `RealPackTests` case with `X/v11.0.1-rc.1` on the base, `X/v10.0.0` at HEAD and the major set to 10, expecting `10.0.0`. | D1, D2 | — |
| S2 | Which ref names appear on PR and merge-queue runs into a line (`GITHUB_REF_NAME`, `GITHUB_BASE_REF`, `gh-readonly-queue/…`)? | Read a Spark run into `release/dotnet-11.x` (logs only, no new run). | D2 | **Answered (GitHub's documented variables):** `push` → `GITHUB_REF_NAME` is the branch. `pull_request` → `GITHUB_REF_NAME` is `<n>/merge` and `GITHUB_BASE_REF` is the target branch. `merge_group` → `GITHUB_REF_NAME` is `gh-readonly-queue/<base>/pr-<n>-<sha>` and `GITHUB_BASE_REF` is empty. The fallback reads `GITHUB_BASE_REF` first, then the `gh-readonly-queue/<base>/` prefix, then `GITHUB_REF_NAME`. |
| S3 | How expensive is the backfill walk? Can it limit itself to commits that touch version-bearing files (`*.csproj`, `*.props`, `package.json`) and build the project graph only there? | Time a `git log --format=%H -- <paths>` plus one `ProjectGraphBuilder` per hit on Spark's and Tools' histories (read-only clones). | D5, D6 | **Cheap (measured 2026-11-12, Spark master):** 342 commits, 184 of them touching `*.csproj`/`*.props`/`package.json`, listed in 0.23 s. The walk builds the graph only at those commits. |
| S4 | How many tags does D5 create on Spark, and does `tag --merged HEAD --list <id>/v*` per package stay fast? | Count the distinct (package, version) pairs from S3. Time the resolver on a scratch clone with those tags (local only, never pushed). | D5, Q11 trap | **Acceptable (measured):** about 1,716 distinct (project file, version) pairs, mostly `10.0.0-preview.N`/`11.0.0-preview.N`, so Spark goes from 368 to about 2,100 tags. `git tag --merged HEAD --list 'MintPlayer.Spark/v*'` takes 0.15 s today, and a plain `--merged` takes 0.16 s, so the per-package cost hardly depends on the count. |
| S5 | Can the git-writer force-push a tag past the tag ruleset with the App's bypass? | Code reading of the git-writer's tag push, then SparkTest (ruleset 27049210) after deploy. | D9 | **Answered:** `TagWriter` pushes "never with force (M5)" (`CodeCoverage.Versioning/Releases/TagWriter.cs:48`), a design rule that **stays**. The move is a separate operation: `git push --force-with-lease=refs/tags/<t>:<old object> <new>:refs/tags/<t>`, so it never clobbers a tag that changed after Set up features read it. Tools has branch rulesets only (27099665-27099669, `target: branch`), so the App's token is not blocked. |
| S6 | Does `PlatformMarkers` handle a removed TFM, not just a changed value? Does it cover `Directory.Packages.props`? | Read `PlatformMarkersTests.cs`. The red tests for D13 answer it. | D13, D15 | — |
| S7 | What does the backport flow do today with an empty result after normalization? | Code reading of `CodeCoverage.GitWriter`'s `BackportService` and the attempt records (#629). No empty-result handling was found in `CodeCoverage.Git/Backports`. | D13 | **Already handled:** `BackportService` returns `Empty` when the merged tree equals the line's tree (`CodeCoverage.GitWriter/Jobs/BackportService.cs:125-129`, trial `:181-182`). The App has the `empty` outcome (`GitWriterVersioning.cs:181,205`) with its translation. D13 needs no new outcome. |
| S8 | Does the merge queue admit an App PR into a past line, given that a person's PR there is "evicted as before" (`merge-queue.md:220`)? | Code reading of the eviction rule. | D15 | **Yes.** "Evicted as before" is about failure only: the PR is evicted instead of held. Admission goes through "Line policy" (`CodeCoverage/PackageVersioning/LinePolicy.cs:20`), which refuses only on the **current** line (`docs/code-coverage/backports.md:13`), and an older line is never current. |
| S9 | Where does "the latest SDK of major N" come from? | `dependency-action/src/platform/feeds.ts` already reads the SDK release feeds. Check whether the App (C#) has the same source. | D15 | **The App has it:** the platform-lifecycle cron reads `releases-index.json` and stores `LatestSdk` per channel (`CodeCoverage/PlatformLifecycle/PlatformLifecycleSources.cs:49`, `PlatformLifecycleCronJob.cs:212`). |

## 5. Milestones (one PR, in the issue's order)

**Test-driven (owner, 2026-11-12: "Prefer test-driven development - write a red test, make it green").** Every
milestone opens with the failing test that reproduces its defect. Run it alone (`--filter` on the one test or class)
and see it **red for the right reason**, then write the fix and see the same test green. Record each red→green pair
in the milestone's line below (test name, the red message, the commit). These targeted single-test runs are the
owner's requested exception to "tests run once, at the end". The full affected suites still run once, after M7.

- **M1, publish assertion (D11).** Red: `VersioningOnboardingTests` expects the assertion step in the template and
  inserted after the pack of an existing workflow (`:28-35`, `:198-205`). Then add the same step to Spark's
  `dotnet-build-master.yml`.
  ✅ Red: `The_pack_is_followed_by_a_check_that_every_released_nuget_package_was_packed_at_its_version`, "Expected
  checkedText to contain … but found <null>" (against a stub). Green: rule `PackedVersions`, which runs before
  `PublishGate` so the gate also covers the new step. Two end-to-end expectations were updated. A local bash run against
  fake `.nupkg`s found a real bug: jq on Windows ends lines with CRLF, so a correctly packed package was reported
  missing. The script now strips the `\r`.
- **M2, line-aware resolver (D1-D3, D10).** Red: the `TagVersionTests` major filter (an 11.x prerelease reachable,
  10.x expected), the S1 `RealPackTests` case, the MPV006 case, `main.test.ts` (export plus `allVersionsOf`), and a
  `PromoteService` hand tag `v10.0.2` on a line with `v11.0.1-rc.1` reachable, which must be accepted. Turn MPV006 on
  in Spark's `Directory.Build.props`. **Rebuild `versioning-action/dist` in the main checkout only.**
  ✅ Red: the new `TagVersionTests` (`Highest(…, major)`, `MajorOf`, `BranchMajor`, the task's `Majors`/`GitHubRefName`
  and MPV006) and `RealPackTests.On_a_line_the_pack_takes_the_highest_tag_of_the_packages_major` did not compile (no
  such members). Green: 34/34 with the planner test. S1 answered: the environment variable `MintPlayerVersioningMajors`
  reaches the task as a property, and with `-p:MSBuildWarningsAsErrors=MPV006` the pack exits non-zero. `main.test.ts`:
  red "2 failed" (export, `allVersionsOf`), green 16/16, plus `tsc` clean. GitWriter wire:
  `VersioningPlanReleaseTests.A_run_on_a_line_tells_the_pack_…`, red "expected {Core: 10}, found <null>". The fixture
  lacked the request's `Line`, and the planner reads `context.Line`. D3: `PromoteTests.On_an_older_line_a_tag_follows_the_highest_tag_of_its_own_major`,
  red "Core/v1.3.0 is not above Core/v2.0.1-rc.1, the tag it follows". Green with a major filter in `PredecessorAsync`
  (the line's major on promotion; on a hand tag, the tag's own major when a line has it, otherwise every tag, so a new
  major with no line of its own is still refused). 24/24 PromoteTests. MPV006 is on in Spark's
  `Directory.Build.targets`, after the import, and the onboarding adds the opt-in to each `Directory.Build.props` root
  (rule "A release line that cannot pack its own major fails the build (MPV006)", 27/27 VersioningOnboardingTests).
- **M3, history backfill (D5-D8).** Red: a planner/service test on a scripted repository: master declares
  `Hsts 10.0.0` → `10.0.1` → placeholder plus a v11 seed tag; `release/dotnet-10.x` is cut after that. Expect
  `Hsts/v10.0.0` and `v10.0.1` at their earliest commits, both reachable from the line. Also: a 7.x version declared
  only on `release/dotnet-7.x` is tagged there, feature branches are ignored, and re-running plans nothing.
  ✅ Engine: `HistoryBackfill.PlanAsync` (`CodeCoverage.Versioning/Releases/HistoryBackfill.cs`), test
  `HistoryBackfillTests.Every_declared_version_is_tagged_at_its_earliest_occurrence_on_the_line_of_its_major`, which covers
  every case above plus "without lines, only the default branch". It walks `log --first-parent` limited to project files
  and reads each commit's graph once (at most 5000 commits, else refused). ⚠️ Honest TDD record: this test and the
  GitWriter test below were red only as compile failures (the types did not exist yet). The implementation was written
  before a behavioural red run, and both went green on their first run. Writer: `POST /jobs/versioning/backfill`
  (`GitWriterVersioningBackfillEndpoint`, `VersioningRunService.BackfillAsync`). It lists heads with `ls-remote` and fetches
  only the default branch and the pattern's lines, with every tag. It writes through `TagWriter` (never forced, M5) in
  batches of 500, each tag reachable from the walked branches. `planOnly` writes nothing. Test:
  `VersioningBackfillTests.The_history_is_tagged_at_each_versions_earliest_occurrence_and_a_plan_writes_nothing` (plan →
  nothing on the origin; write → `Core/v1.0.0`/`v1.1.0` at their commits; re-run → no tags). App:
  `OnboardingServiceTests.Backfill_D7_the_merge_tags_the_history_after_the_line_cuts_and_seeds_only_what_it_did_not_tag`
  was red for the right reason: "Expected h.Writer.BackfillRequests to contain a single item, but found 0". `LineCuts_D2_D3_…`
  (D8, no files → backfill after both cuts) was red with "Expected linesAtBackfill not to be empty". Green: `PushSeedsAsync`
  runs the backfill first and pushes only the seeds it did not tag. `MergedAsync` calls it after `CutAsync` (D7 order;
  the early returns still push seeds for a #153 pull request). The no-files path of `OnboardAsync` runs the backfill after
  the cuts. A failed backfill is noted and leaves the seeds to carry the current versions, which was the behaviour before
  #638. 129/129 in the onboarding, controller and sidecar classes.
- **M4, collision moves (D9).** Red: the Tools shape, an App-made `Hsts/v10.0.0` at a later commit than history's
  declaration. Set up features offers Move, Move force-pushes it, Leave keeps it, and a person's tag is never offered.
- **M5, backport normalization (D13).** Red: `PlatformMarkersTests` with `net10.0;net11.0` → `net11.0` on a 10.x
  line, where the line keeps `net10.0` and it is reported, and a mixed PR (a fix plus the TFM drop) that merges the
  fix only. Then a backport test where a pure platform move closes as "nothing left to backport" (S7).
- **M6, narrowing PR (D15).** Red: `OnboardingServiceTests` with an older line cut from a `net10.0;net11.0` commit,
  which opens a PR into the line with TFMs, `global.json` and framework packages narrowed and the conditionals listed.
  `OnboardingPlannerTests` checks the confirmation and the PR body text.
- **M7, docs:** G7 in `release_lines_recovery_plan.md` §2e, and remove #600 from its §6; `versioning.md:47-49`
  (seeds become the history backfill), `:105` (line's major), `:316-328` (hand-tag rule); `merge-queue.md:220`
  (narrowing PRs); a multi-targeting → lines how-to, including the `$(TargetFrameworks)` → `$(TargetFramework)`
  pitfall Tools hit; and MPV006 in the warnings list.
- After M7: one run of the affected suites (`npm run test:affected`, Developer licence), then the PR.

## 6. Recovery for Tools (after the deploy and a new action/MSBuild release)

1. Re-run **Set up features** on Tools. Expect the backfill (D5-D8) to tag every historical 10.x version at its
   earliest commit, and to offer **Move** for the 14 App-made `<id>/v10.0.0` tags at `8b1472c` (D9). Accept them.
2. Tools picks up the new action SHA and `MintPlayer.Versioning.MSBuild` (Tools #52 bumps 1.0.1 → 1.1.0), which
   also brings MPV006 as an error through the template.
3. Re-run publish-release on `release/dotnet-10.x`. Expected: `10.0.2`/`10.1.0` for Hsts, LoggerProviders,
   MustChangePassword, NoSniff and SitemapXml, and `10.0.1`/`10.1.0` for the other 10. The M1 assertion confirms it.
4. `/release` on `release/dotnet-11.x` is independent of 1-3. Expected: `11.0.1-rc.1` → `11.0.1`, Endpoints
   `11.4.1-rc.1` → `11.4.1`, Endpoints.Generator → `11.4.2`/`11.5.0`.

## 7. Grilling record (2026-11-12)

| Q | Question | Answer |
|---|---|---|
| Q1 | Source of a line's last version: registry each run, seed once from history, or both? | **Tags only, and onboarding seeds them from history** (D4, D5). |
| Q2 | Which history does the backfill walk? | Default branch plus the line branches (D6). |
| Q3 | Which commit gets a version's tag? | Earliest occurrence on the line that owns the major, else the default branch (D7). |
| Q4 | How do onboarded repositories get it? | Re-run Set up features, idempotent (D8). |
| Q5 | App-made tags at the wrong commit? | Offer Move, force-push allowed (D9). |
| Q6 | No tag of the line's major at pack time? | Warning MPV006, opt-in as an error, on for now (D10). |
| Q7 | Verify the registry after the push? | No (D12). |
| Q8 | Which platform changes stop an auto label? | None. Fix it in the backport normalization instead (D13). |
| Q9 | Per-PR opt-out, label re-evaluation? | Both dropped (D14). |
| Q10 | Older line inherits the newer TFM? | Warning plus an App narrowing PR (D15). |
| Q11 | Backfill every version, or only the highest per major? | **Every version** (D5). S4 measures the tag-count cost. |

# Lockfile notices and release-line cuts: PRD and plan

Status: M1-M4 **merged and deployed** as PR #645 (b35573d37, closes #643 and #644). Its after-deploy check on
SpaServices (2026-11-12) found three more defects (§1c = #647, §1d = #646): M5-M6 fix them on the same branch, in a
second pull request.
S3 is answered (§3). Left: S4, then "After deploy" (§4). S1, S2 and S5 are open and outside these pull requests.
M5-M6 **merged and deployed** as PR #648 (159b2d418, 2026-11-12 18:44 UTC). Its after-deploy check found #649 (§1e):
the git writer refuses every narrowing that touches a workflow. M7-M13 fix it in a third pull request. Grilled with the
owner on 2026-11-12: D10-D17 decided. M7-M13 **implemented** as PR #650 (`fix/issue649-narrowing-workflows`, closes
#649), not merged yet. Its first CI run failed only "build + verify" (`--spark-verify-security`, run 43302397158): the
new `New/SetUpFeatures` right was missing from `securityPosture.txt` and out of order in `security.json`; fixed with
`--spark-synchronize-security` (M11). The second run (43304478261) failed "build + verify" and two CodeCoverage upload
tests with "The server exited with 150": the CodeCoverage binary asked for runtime `11.0.0`, while the runner had only
`11.0.0-rc.2.26504.105` (global.json's SDK). Run 1 built `CodeCoverage:build` itself; run 2 took it from the
`[remote cache]` (log line 1112). An entry built with a GA SDK (this machine resolves `dotnet --version` = `11.0.100`,
runtime `11.0.0`, and holds the cache write token) had the same hash, because no cache input named the SDK. Fixed in
`nx.json`: `sharedGlobals` now holds `global.json` and `{ "runtime": "dotnet --version" }`. S8 answered in code (§3), S11 partly (the checkout order); the rest of S11, S7, S9
and S10 come after the deploy.
Owner request, 2026-11-12, after re-onboarding MintPlayer/MintPlayer.AspNetCore.SpaServices (PR #108): "SDK +
dependencies bijwerken" ended "Failed", and `release/dotnet-10.x` was not created.

## 1. Why

### 1a. #643: a deprecation notice refused the whole dependency patch

- SpaServices runs 80 and 81 (2026-11-12) both ended `patch-rejected`, reason `lockfile-source`: "Line 2261 adds a URL
  on www.npmjs.com, which is neither an allowed registry nor in the base lockfile.
  (Demo/Xsrf/ClientApp/package-lock.json)". Source: run document `DependencyUpdateRuns/github/204431316`, History.
- The line, from apply run 43271441753 (artifact `dependency-apply-204431316-81-2`, `dependency.patch:132`):
  `"deprecated": "Package no longer supported. Contact Support at https://www.npmjs.com/support for more info.",`
  on `node_modules/@mintplayer/ng-swiper` 21.1.3 → 21.9.0. Its `resolved` is on `registry.npmjs.org` with an
  `integrity` value.
- Why it is there: every `@mintplayer/ng-bootstrap` 21.x names `@mintplayer/ng-swiper` as a peer (`npm view`:
  `^21.3.0` up to ~21.30, `^21.9.0` through 21.47.0; 22.0.0 `^22.0.0`; 22.29.0 none). ng-bootstrap removed it in
  207d85f7c ("delete @mintplayer/ng-swiper (#392)"). Every ng-swiper version is deprecated on npm. SpaServices' demos
  are on Angular 21 (`Demo/Xsrf` `^21.0.0`, `Demo/Prerendering` `~21.6.0`).
- The base lockfile (`Demo/Xsrf/ClientApp/package-lock.json` at 16141c16a) has 0 `deprecated` lines.
- **Cause:** `LockfileScanner.Scan` (`CodeCoverage.GitWriter/Jobs/Dependencies/LockfileScanner.cs:137-144`) checks
  every `https://` URL on every added line. A URL in free text counts as a source.

### 1b. #644: the onboarding's line cut answered 422

- After PR #108 merged, the app logged `refused creating release/dotnet-10.x`, `POST .../git/refs answered 422:
  Reference update failed`, at `OnboardingService.CutAsync` (`OnboardingService.cs:327`).
- `SetUpFeaturesAction.cs:171-175` applies the rulesets right after it opens the pull request. That created ruleset 3
  (27139872) on `refs/heads/release/*-*.x` (`required_status_checks`: Line policy, node 20, node 24, pull-request)
  with **no bypass actors** (`GET /repos/.../rulesets/27139872`, 2026-11-12).
- `MergeQueueRulesetPlanner.LineChecks` (`CodeCoverage.Library/Forge/MergeQueueRulesets.cs:357` before this change)
  added the App only `if (promotes)`. SpaServices takes backports.
- `docs/onboarding_line_cuts_PRD.md` D4 assumed "ruleset 3's exempt bypass admits" the cut; its S2 was still pending.
  Its evidence came from Tools, which promotes.
- A refused cut is noted and not retried (`OnboardingService.cs:330-333`, then `MergeHandledAt` at `:280`).

### 1c. After the deploy, the first re-run still answered 422

- Re-running Set up features on SpaServices after #645 deployed (2026-11-12, ~18:19 UTC): the confirmation listed
  "Update ruleset "merge-queue: release-line checks": bypass added: App 4574022 (exempt)" and "release/dotnet-10.x" to
  create now. The app log then shows `refused creating release/dotnet-10.x … 422: Reference update failed` at
  `OnboardingService.CutAsync`, followed by `PUT …/rulesets/27139872` (200).
- **Cause:** with no files to change, `OnboardAsync` cuts at once (`OnboardingService.cs:156-161`), and
  `SetUpFeaturesAction` applied the rulesets only after `OnboardAsync` returned (`SetUpFeaturesAction.cs:161`, then `:173`).
- A second run (rulesets "set up already") created `release/dotnet-10.x` at 16141c16a, which answers S3.
- The first click of that session ended "Operation failed": GitHub answered **504** minting the installation token
  (`GitHubForgeMergeQueue.MintJobTokenAsync`, `GitHubForgeMergeQueue.cs:1398`). A GitHub timeout; a retry passed.

### 1d. The narrowing pull request left the higher SDK in the workflows and in `tools.dotnet`

- The cut opened SpaServices #110 (`mintplayer/narrow-dotnet-10.x` → `release/dotnet-10.x`). Its checks node 20, node 24,
  pull-request and build-any failed (run 43279290622): `Requested SDK version: 10.0.404` … `A compatible .NET SDK was
  not found` (exit 155). The runner had 10.0.403 and `11.0.100-rc.1.26425.128` installed.
- `global.json`'s `sdk.version` was narrowed to 10.0.404, but `setup-dotnet` still installed the 11 rc:
  `dotnet-version: 11.0.100-rc.1.26425.128` in `pull-request.yml:23,97`, `build-any.yml:29`, `build-master.yml:36`
  on the line. `dependency-apply.yml` reads `global-json-file: global.json` and was fine.
- `global.json:7` (Arcade layout) also kept `"tools": { "dotnet": "11.0.100-rc.1.26425.128" }` (owner, 2026-11-12).
- **Cause:** `LineNarrowing.Plan` read MSBuild files, `global.json` and sources only, and rewrote only `sdk.version`;
  `OnboardingService.NarrowAsync` read no workflows (`OnboardingService.cs:406`).

### 1e. #649: since #648 the git writer refuses every narrowing that touches a workflow

- **Steps (2026-11-12, after #648's deploy):** SpaServices #110 closed; `release/dotnet-10.x` deleted twice (ruleset
  "Restrict deletions" lifted and restored by the owner), `mintplayer/narrow-dotnet-10.x` deleted, then **Set up
  features → Create it**. The bot recreated `release/dotnet-10.x` at `16141c1` both times; no narrowing branch, no pull
  request.
- **App log** (`code-coverage-coverage-app-1`, 19:22-19:32 UTC): `POST http://coverage-git-writer:8080/jobs/onboarding`
  → `400` in 5 ms, then only the token revocations. Nothing logs the refusal. The git writer's log has no job for it:
  the endpoint refuses the input before `JobRunner`.
- **The refusal was shown, but buried.** Production `VersioningOnboardingStates/github/204431316` (read 2026-11-12):
  `"Note":"Applied without a pull request: created release/dotnet-10.x; no narrowing pull request into
  release/dotnet-10.x: The git writer refused the onboarding request: files must be for a line's narrowing: 1 to 256
  distinct MSBuild files (.csproj, .fsproj, .vbproj, .props, .targets) or global.json of at most 256 KiB, no
  deletions.. Nothing to change in the repository: coverage.yml has `versioning.lines` already, …"`. That is the toast
  (`OnboardingService.cs:170`, `SetUpFeaturesAction.cs:204`): one long sentence that reads as success.
- **Cause 1, the input check:** `NarrowFiles` (`CodeCoverage.GitWriter/Endpoints/OnboardingEndpoints.cs:57-72`, rule
  text :53) takes only MSBuild extensions and `global.json` (:66-67). #648 made `LineNarrowing.Plan` emit
  `.github/workflows/*.yml` (`LineNarrowing.cs:125-157`) and changed nothing in `CodeCoverage.GitWriter`. The writer's
  test `OnboardingWriterJobTests.D15_a_narrowing_job_writes_msbuild_files_and_global_json_only` (:120-136) even asserts
  that a workflow gets the 400; no test sends `LineNarrowing`'s output through the check.
- **Cause 2, the token (would fail next):** `LinePullRequestAsync` asks for `pushesWorkflowChanges: !narrow`
  (`OnboardingService.cs:460`), so `MintJobTokenAsync` (`GitHubForgeMergeQueue.cs:1367-1370`) mints `contents: write`
  only. GitHub refuses a push that changes `.github/workflows/` without `workflows: write` (S7). The installation has
  it: `coverageproduction` (4574022) lists `workflows: write` (`gh api orgs/MintPlayer/installations`, 2026-11-12).
- **Not a cause:** `.cs` sources. `Plan` reads them for conditionals (:159-166) and never writes them.
- **Cause 3, no second chance:** the narrowing runs only for a line cut in this run (`plan.Cuts`,
  `OnboardingService.cs:168`; `state.Cuts` at merge, :278). A line that exists unnarrowed is never offered again, so
  the only retry is delete-and-recut, which needs the deletion rule lifted by hand.
- **The merge path is fully silent:** `VersioningSeedsRecipient` drops `MergedAsync`'s outcome
  (`VersioningOnboardingRecipients.cs:48`); `VersioningOnboardingState.Note` is written (`OnboardingService.cs:577`) but no page reads it.
- **What it costs:** every push to `release/dotnet-10.x` fails at "Version the packages" with `plan-refused` ("computes
  major 11 (targets net11.0) on the release branch for 10.x"), e.g. SpaServices run 43289875451.

## 2. Decisions

| # | Decision | Evidence / reason |
|---|---|---|
| D1 | In `package-lock.json` and `npm-shrinkwrap.json`, a line that is **only** `"deprecated": "<one JSON string>"` (anchored over the whole line) is skipped by the source checks. Every other line is checked as before. | §1a. npm never fetches from the message. Anchoring the whole line means a `"resolved"` cannot ride on it (test `A_source_hidden_on_a_deprecation_line_is_still_refused`). Messages with `http:`/`git:` are common (e.g. request's "see https://github.com/request/request/issues/3142"), so the forbidden-source check skips it too. Scope kept to the evidence (owner preference for minimal diffs): `funding` and pnpm are spikes S1/S2. |
| D2 | Ruleset 3 **always** lists the App as `exempt`, promoting or not. `MergeQueueRulesetChoices.PromotesLines` and `ChooseAsync(..., promotesLinesComing)` are removed. | Owner, 2026-11-12: "Always exempt App" (picked over "exempt only with cuts" and "spike first"). §1b. The App uses the bypass to create lines (onboarding and the versioning run's `ReleaseBranchCuts`) and to push promotions. Backports still go through pull requests, and the queue waits for the line's required checks itself (merge-queue.md, "The queue accepts this one bypass"). Reverses merge-queue.md's "no bypass for the App" (superseded text updated there, in onboarding.md, onboarding_line_cuts_PRD.md S2 and release_lines_recovery_plan.md). |
| D3 | No automatic retry of a refused cut. After the deploy, re-running **Set up features** re-applies the rulesets (adding the bypass) and cuts a missing line at once through the no-files path. | `OnboardingService.cs:156-161` already cuts at once when nothing needs a pull request. Documented in onboarding.md. |
| D5 | With **no files to change**, Set up features applies the rulesets **before** `OnboardAsync`; with a pull request, after it as before. | §1c. Without files the workflows that report ruleset 2's checks are on the default branch already, so the reason to wait (a refused onboarding leaves none) does not apply. Corrects D3, which assumed one re-run is enough. Test `SetUpFeaturesRulesetsTests.With_no_files_to_change_the_rulesets_apply_before_the_onboarding_cuts_the_lines` (red first). |
| D6 | The narrowing also rewrites `global.json`'s `tools.dotnet` and every scalar `setup-dotnet` `dotnet-version` in `.github/workflows/*.yml` above the line: an exact pin takes the line's latest SDK (as `sdk.version`), a wildcard (`11.0.x`) keeps its shape with the line's major. A block list (`dotnet-version: \|`) naming a higher major, or an exact pin with no known SDK, is a note for a person. | §1d. Rewriting the list could drop an SDK a step needs; the evidence has only scalars. Tests `LineNarrowingTests.D15_the_workflows_sdk_pins_and_the_tools_sdk_follow_the_line` (SpaServices' real step and global.json), `D15_a_workflow_pin_without_a_known_sdk_or_in_a_list_is_left_for_a_person`, and `OnboardingServiceTests.Narrowing_D15_…` (the service reads the workflows). |
| D7 | The git writer's narrowing check also takes workflow files. One predicate, `LinePaths.IsNarrowable(path)` in **CodeCoverage.Git** (both CodeCoverage and CodeCoverage.GitWriter reference it, `CodeCoverage.GitWriter.csproj:36,38`), decides what a narrowing may write: MSBuild extensions, `global.json`, and `.github/workflows/<name>.yml\|yaml` **one level deep**. `NarrowFiles` and `LineNarrowing` (`IsMsBuild`/`IsGlobalJson`/`IsWorkflow`) both call it, and `NarrowFilesRule` names workflows. | §1e cause 1. The two lists drifted because they were two lists. One level deep matches the onboarding's own `IsPath` (`VersioningEndpoints.cs:235-239`) and GitHub, which loads no workflow from a subdirectory (S9); `LineNarrowing.IsWorkflow` accepting subdirectories was the outlier. Tests (planned red first; as done, see M7): the writer's `D15_a_narrowing_job_writes_msbuild_files_and_global_json_only` flips its workflow case and a new case writes a workflow pin plus `tools.dotnet`; `LineNarrowingTests` asserts every emitted path passes `IsNarrowable`. **Implemented (PR #650), plus a second check:** `NarrowFiles` also called `PatchValidator.IsSafePath`, which refuses every `.github/` path; it now calls `IsWritablePath` and leaves `.github/` to `IsNarrowable` (M7). |
| D8 | A narrowing asks for `workflows: write` when its files include a workflow: `pushesWorkflowChanges: !narrow \|\| files.Any(f => LinePaths.IsWorkflow(f.Path))` at `OnboardingService.cs:460`. | §1e cause 2. Owner, 2026-11-12: "workflows: write is not a problem". Least privilege stays for a narrowing without workflows. Test: an `OnboardingServiceTests` narrowing with a workflow records `WriterAccesses` with `pushesWorkflowChanges: true` (`MergeQueueFakes.cs:308`). **As implemented:** `pushesWorkflowChanges: files.Any(f => LinePaths.IsWorkflow(f.Path))` for every line pull request, the same answer for the line-workflows path (its files are all workflows) and the D8 answer for a narrowing. |
| D9 | A refusal is **said**, not just noted. (a) `LinePullRequestAsync` and `NarrowAsync` log each refusal at warning with the repository, the line and the writer's reason. (b) The outcome lists failures as their own sentence ahead of the successes ("Not done: release/dotnet-10.x was not narrowed: …"), and an outcome with a failure is a warning toast, not "Applied". (c) `VersioningSeedsRecipient` logs `MergedAsync`'s outcome like `RebuildAsync` does (`OnboardingRecipients.cs:50`). (d) The writer's rule text loses its doubled period. | §1e: the refusal was in the toast but read as success, and the merge path dropped it. No new UI page: the repository page showing `Note` is S5's territory. Test: the outcome note for a refused narrowing starts with the failure (planned red first; **as done:** `OnboardingServiceTests.Issue649_D9_a_refused_line_pull_request_is_reported_first`, written after the code, M7-M13 note in §4). |
| D10 | **Owner, 2026-11-12: yes ("A").** Set up features narrows **every** supported .NET line that still targets above its major, not only the lines it cuts now: `NarrowAsync` over `SupportedLines.ReadAsync` (as `LineWorkflowsAsync` does, `OnboardingService.cs:359`), with `TargetsAbove` (:408-411) as the detector. | §1e cause 3: today the only retry is delete-and-recut, which needs a ruleset lifted by hand. A rerun is idempotent: the writer force-pushes `mintplayer/narrow-<line>` and an open pull request is found (:492-503). Cost: each run commits with `time.GetUtcNow()` (:463), so a rerun with an open narrowing pull request force-pushes a new SHA and reruns its CI (S8). Alternative: keep cut-only and document delete-and-recut. A line above its major can never release (`plan-refused`), so a prompt would only ever be answered "Yes". ~~The commit takes the base commit's date, so a rerun is the same SHA and pushes nothing (S8).~~ **Superseded by S8's answer (§3):** the commit keeps its date (now); the git writer keeps the branch's existing commit when its only parent is the line head and its tree is the same (`ExistingSameAsync`), so a rerun pushes nothing. |
| D11 | **One SDK source, offered, never forced** (owner, 2026-11-12: "I won't be the only one who uses coverage.mintplayer.com, so forcing everyone to use the global.json is not preferred"). A new Set up features checkbox, **"SDK from global.json"** (`EOnboardingFeature.SdkFromGlobalJson`; planned as "Workflows read the SDK from global.json", shortened for the form), rewrites `setup-dotnet` steps in the onboarding pull request to `global-json-file: global.json`. The narrowing keeps rewriting pins for everyone who does not tick it (D6-D8 stay the fallback). | Owner picked "D" (convert, with the pin rewrite as fallback) over making `global.json` mandatory. `dependency-apply.yml` already reads `global-json-file` and was the only SpaServices workflow #110 did not break (§1d). Today three places treat a pin as a second SDK source: `LineNarrowing`, the SDK updater (`dependency-action/src/platform/locations.ts:44`) and the backport markers (`PlatformMarkers.cs:83`). |
| D12 | The checkbox is **unchecked by default**. It is **visible** when the stored `RepositoryManifest` says .NET (`Produces`/`Consumes` with `ManifestEcosystems.NuGet`, or `LookupPlatforms` with dotnet), and also when no manifest exists yet. It is **enabled** through `TriggersRefresh` on Release lines and Dependency updates, in an `OnRefreshAsync` like `GateSettingsActions`. | Owner, 2026-11-12: Q2 "A", Q3 "we can show or enable the checkbox using a TriggersRefresh and OnRefresh override", Q4 "use the existing mechanism". No forge read before the form, which opens before `PreviewAsync` (`SetUpFeaturesAction.cs:51-56,79`). A `.sln`/`.slnx` check was considered and dropped: an SDK-style project needs no solution file. With no manifest it shows: a wrong "shown" costs a Plan note, a wrong "hidden" costs the feature. |
| D13 | It converts only where `global.json` has `sdk.version`; otherwise Plan says so in a note and nothing changes. The bot never **creates** a `global.json`. | Owner, Q5 "A". A `global.json` pins every contributor's local SDK and IDE, a far wider change than a CI step. |
| D14 | **Every** scalar `dotnet-version` pin converts, whatever its major: the build uses one SDK, `global.json`'s, which the SDK updater keeps on the latest release. | Owner, Q6: "Always use the same major. The goal is to automatically update to the latest .NET sdk version … There's no point in leaving some versions to an older version one." |
| D15 | In a block list (`dotnet-version: \|`), the entry on `global.json`'s major is replaced by `global-json-file: global.json` and the lower entries stay, as runtime installs for multi-target tests. setup-dotnet installs both inputs. | Owner, Q7 "B". SpaServices' test projects target `net10.0;net11.0`; the 11 SDK builds `net10.0` but needs the 10 runtime to run its tests. Which SDK setup-dotnet makes the default with both inputs is S11. |
| D16 | Existing release lines get the conversion through **a bot pull request into each supported line** ~~(`LinePullRequestAsync`, the line-workflows path and its `LineFiles` check)~~, not through a backport. **As implemented (M12):** the pull request is the line's narrowing pull request (`mintplayer/narrow-<line>`, the narrowing's writer check), carrying the conversion beside the narrowing, or alone with the title "Read the .NET SDK from global.json on this release line". A separate line-workflows pull request would edit the same `dotnet-version` lines as the narrowing and conflict with it. | Owner left it to the spikes (Q10). S12 answered no: backport labels come from `ChangedEcosystems` (`CodeCoverage.Versioning/Releases/ChangedEcosystems.cs:16-20`), the ecosystems of packages whose **inputs** changed, and a package's repository inputs are `global.json` and `nuget.config` (`DotnetProjectReader.cs:15`). A workflow-only onboarding pull request gets no `backport:` label. The line pull request reads the line's own `global.json`. |
| D17 | **Done means a release from the line** (owner, Q9 "A"): the SpaServices narrowing pull request merges green, the line's versioning run tags and publishes `10.x.y`, and one real backport (`backport: release/dotnet-10.x`) lands and publishes the next 10.x. Releasing older versions from backports is the existing design (#568/#570 backports, `PlatformMarkers` normalization, #638 first release on an older line), and #649 is the step that blocked it. | Owner, 2026-11-12: "we should probably be able to release older versions backports too. master already on .net 11 => bot should still be able to release .net 10 targetting version". Trap: the first 10.x release ships master's code at the cut (16141c1); the owner checks that before merging the narrowing. |
| D18 | **#651 D1:** the narrowing lowers a numeric `<LangVersion>` above the line's C# to it (C# = .NET major + 4, so .NET 10 → `14`; `15.0` counts as 15). `latest`, `latestMajor`, `preview` and `default` follow the compiler and stay. A file whose only problem is the language version still needs the pull request (`TargetsAbove`). | SpaServices #114 (2026-11-13): TFMs narrowed, `<LangVersion>15</LangVersion>` kept, `CSC : error CS1617: Invalid option '15' for /langversion` (run 43382800307, node 20/24). Test `LineNarrowingTests.Issue651_D1_a_language_version_above_the_line_takes_the_lines_csharp_version`, red first. |
| D19 | **#651 D3, narrows D16:** no line pull request into the **current** line of an ecosystem (`ReleaseLinePattern.CurrentLines` over the line branches, the Line policy's own rule). With the conversion pending there, the note says to comment `/backport <line>` on the pull request that converted the default branch. | SpaServices #113 (2026-11-13): the conversion-only pull request into `release/dotnet-11.x` failed the bot's own Line policy check ("takes only backports"). The current line targets the default branch's major, so it never needs narrowing. The bot's onboarding pull request gets no backport label (D16, S12), hence the hint instead of an automatic backport. Test `OnboardingServiceTests.Issue651_D3_the_current_line_gets_no_line_pull_request`, red first. |
| D20 | **#651 D6:** when a pull request changes no package, the "Backport targets" check names each supported line's label and command (`backport: dotnet-10.x` / `/backport dotnet-10.x`). Labels stay as they are: no default label for non-package changes. | SpaServices #115 (2026-11-13) updated a demo's packages that `release/dotnet-10.x` needed to restore (NU1107); the check said "No line applies", the author added a label named `release/dotnet-10.x`, which the backport does not read, and the merge backported nothing; `/backport dotnet-10.x` afterwards worked (#116). Whether a non-package change is needed on a line can't be told from the files, so the person decides; the check makes the right name visible. Test `BackportTargetsTests.Issue651_D6_when_nothing_applies_the_check_says_how_to_backport_anyway`, red first. |
| D4 | The SpaServices follow-up (ng-bootstrap upgrade, Angular 22 for the demos, SDK on its release line) is a SpaServices issue, not code here. | Other repositories get an issue, never a commit from here (owner rule). |

## 3. Spikes

| Spike | Question | How | Result |
|---|---|---|---|
| S1 | Does npm's `funding` field refuse a patch the same way? `"funding": "https://…"` or `"funding": { "url": "https://…" }` on a host the base lockfile does not name. | Evidence: the SpaServices base lockfile has 128 `funding` entries on github.com (93), opencollective.com (48), tidelift.com (4), paulmillr.com (2). They pass only because the base names them. Next: find a real update that adds a new funding host (a package whose author moves to e.g. `buymeacoffee.com`), keep that patch as a fixture. | **Open.** Expected yes. Not fixed without a real fixture (parsers need real fixtures). |
| S2 | Does pnpm's `pnpm-lock.yaml` record `deprecated:` the same way, and with what quoting? | Run `pnpm install` of a deprecated package (e.g. `request@2.88.2`) in a scratch dir and diff the lockfile. | **Open.** If yes, add a `Notice` regex for pnpm from that real line. |
| S3 | Does the App's `exempt` bypass on ruleset 3 admit a branch **creation** that the required checks refuse for a person? (onboarding_line_cuts_PRD S2) | After the deploy: re-run Set up features on SpaServices, expect `release/dotnet-10.x` created at once. Fallback sandbox: PieterjanDeClippel/SparkTest. | **Yes (2026-11-12).** The second re-run on SpaServices created `release/dotnet-10.x` at 16141c16a with the bypass in place; the first failed only because the rulesets came after the cut (§1c, D5). |
| S4 | Which other repositories have a ruleset 3 with no App bypass today? | `GET /repos/{r}/rulesets` for each onboarded MintPlayer repository with lines; list those without `Integration 4574022 exempt` on "merge-queue: release-line checks". | **Open.** Each needs one Set up run after the deploy. |
| S5 | Should "SDK + dependencies bijwerken" say why a run ended at once (`workflow-outdated`) instead of "started"? | Read the repository page's run panel. | **Open**, separate from this PR. The SpaServices runs 72-79 ended in 4 s and the page looked idle. |
| S6 | Was #649's refusal shown to the person at all? | Read production `VersioningOnboardingStates/github/204431316` (`curlimages/curl` as `--user 0` on `code-coverage_coverage-db` with `raven-certs`). | **Yes, buried (2026-11-12).** The `Note`, and so the toast, held "no narrowing pull request into release/dotnet-10.x: The git writer refused …" in the middle of an "Applied without a pull request" sentence (§1e). Leads to D9. |
| S7 | Does GitHub refuse a push that changes `.github/workflows/` with an installation token minted without `workflows: write`? | Expected yes (GitHub's "refusing to allow a GitHub App to create or update workflow … without `workflows` permission"). Prove it with the first narrowing after M7 on SpaServices: with M8 it must push; the M8 test pins the flag. Fallback sandbox: PieterjanDeClippel/SparkTest, a hand-minted `contents: write` token pushing a workflow edit. | **Answered (2026-11-13).** Without the permission: #649's refusal ("The git writer refused …", S6). With M8: the narrowing rerun after #652's deploy pushed `mintplayer/narrow-dotnet-10.x` as `980bd73`, which converts the workflows to `global-json-file`; #114 merged it (`5b16c44`). |
| S8 | Does rerunning a narrowing with an open pull request churn CI? | Read the writer's push (`WriterService.Dependencies.cs:224-226`): does it skip when the new tree equals the branch's? If not, take the commit date from the base commit (`tree.Commit`) so a rerun is the same SHA. | **Yes, it churned; fixed in the writer (2026-11-12).** `FilesOnBranchAsync` force-pushed a new commit on every run, because the line jobs are dated now (`time.GetUtcNow()`). Instead of moving the date, the writer now fetches the bot branch (`--filter=blob:none`) and keeps its tip when that tip's only parent is the job's `ExpectedSha` and its tree equals the new one (`ExistingSameAsync`). Test: `OnboardingWriterJobTests.Issue649_a_rerun_with_the_same_files_on_the_same_head_keeps_the_branch_s_commit` (dates a day apart, same SHA). |
| S9 | Does any onboarded repository keep a workflow below `.github/workflows/` (a subdirectory) that pins `dotnet-version`? | GitHub runs only `.github/workflows/*.yml\|yaml`; check with `gh search code "dotnet-version path:.github/workflows" org:MintPlayer` and look for a deeper path. | **None (2026-11-13).** `gh search code "dotnet-version" --owner MintPlayer`: every workflow hit is directly in `.github/workflows/` (Dotnet.Tools ×5, AspNetCore.Tools ×2, DotnetDesktop.Tools, Dotnet.Templates, Polyglot ×2, AI ×2, Spark); D7's one-level predicate holds. Composite actions (`.github/actions/setup/action.yml` in ng-bootstrap and Spark) pin `dotnet-version` too; a narrowing does not convert them, and neither repository has an older .NET line (`docs/refused_property_and_packagemanager_edits_PRD.md` §1c). |
| S11 | With `global-json-file`, does setup-dotnet honour `global.json`'s `rollForward` (`latestFeature`, `latestPatch`), and with `dotnet-version` **and** `global-json-file` in one step, does `dotnet` at the repository root still pick `global.json`'s SDK? | Read setup-dotnet's README and source for the version used in the workflows (`actions/setup-dotnet@v4`/v5); then one throwaway run on PieterjanDeClippel/SparkTest with `rollForward: latestFeature` plus a lower `dotnet-version`, printing `dotnet --version` and `dotnet --list-sdks`. | **Partly.** `global-json-file` is a path in the workspace, so a step can read it only after the job's own `actions/checkout`: `GlobalJsonSdk` converts a step only when that job checks out before it, and notes the rest (`GlobalJsonSdkTests.S11_*`). The `rollForward` and mixed-input questions are **open**: the D17 acceptance run on SpaServices (§4 step 2) answers them, green or not. It decides whether D14 and D15 hold as written; if `rollForward` is ignored, the conversion also needs the SDK updater to keep `sdk.version` on the latest band (it does, #606). |
| S12 | Does an onboarding pull request that converts only workflow steps get `backport:` labels? | Read `BackportTargets`/`ChangedEcosystems`. | **No (2026-11-12).** Labels follow the ecosystems of packages whose inputs changed; workflows are not inputs (D16). |
| S10 | Which other repositories got an older line cut after 18:44 UTC on 2026-11-12 and a refused narrowing? | Query production `from VersioningOnboardingStates where search(Note, "narrowing")` (parameterised), list repositories whose `Note` has "no narrowing pull request". | **Open.** Each needs D10's rerun, or delete-and-recut, after the deploy. |

## 4. Milestones (one PR)

- **M1** ✅ #643: `LockfileScanner` `Format.Notice` + `NpmDeprecated()`; tests
  `LockfileScannerTests.A_deprecation_notice_with_a_link_is_not_a_source` (3 cases, red first: 3 failed) and
  `A_source_hidden_on_a_deprecation_line_is_still_refused`. 22/22 green.
- **M2** ✅ #644: `MergeQueueRulesetPlanner.LineChecks` always exempts the App; `PromotesLines`/`promotesLinesComing`
  removed. Tests: `MergeQueueRulesetPlannerTests.Release_lines_get_ruleset_1_and_their_own_checks_ruleset`
  (now expects the App), `An_existing_line_checks_ruleset_without_the_App_gains_its_bypass_and_keeps_its_people`
  (replaces `Promoted_lines_let_the_App_bypass_…`), `SetUpRulesetsActionTests.An_onboarding_that_turns_on_release_lines_gives_the_App_the_line_checks_bypass_before_it_merges`
  (replaces the promote theory). Red first: 3 failed; then 45/45 green across the planner, Set up rulesets and Set up features ruleset tests.
- **M3** ✅ Docs: merge-queue.md, onboarding.md, onboarding_line_cuts_PRD.md S2, release_lines_recovery_plan.md (superseded note).
- **M4** ✅ Issue on SpaServices for what remains there (D4): MintPlayer/MintPlayer.AspNetCore.SpaServices#109
  (ng-bootstrap ≥ 22.6.0, the first without the ng-swiper peer; SDK rc → GA; the ambiguous SDK pin on
  `release/dotnet-11.x`; `release/dotnet-10.x`; `release/dotnet-8.x`; held updates).
- **M5** D5: `SetUpFeaturesAction` applies the rulesets before `OnboardAsync` when the plan has no files
  (`ApplyRulesetsAsync`). The fixture's default plan now has a file, since its tests describe an opened pull request.
- **M6** D6: `LineNarrowing` (`ToolsDotnet`, `DotnetVersion`, `IsWorkflow`, `BlockAbove`); `NarrowAsync` reads the workflows.
- **M7** ✅ #649 D7: `LinePaths` in CodeCoverage.Git (`IsMsBuild`, `IsGlobalJson`, `IsWorkflow` one level deep,
  `IsNarrowable`); `NarrowFiles` and `LineNarrowing` call it; `NarrowFilesRule` names workflows. Writer tests:
  `Issue649_a_narrowing_job_writes_a_workflow_beside_global_json`, the theory renamed to
  `D15_a_narrowing_job_writes_msbuild_files_global_json_and_workflows_only` (a nested workflow, a README under
  workflows, coverage.yml and a line-less job are refused); `GlobalJsonSdkTests.D7_*` checks every narrowing path.
  The first test run found a **second refusal** behind the predicate: `NarrowFiles` also called
  `PatchValidator.IsSafePath`, which refuses every `.github/` path, so widening the predicate alone still answered 400
  (`Issue649_a_narrowing_job_writes_a_workflow_beside_global_json`, red). It now calls `IsWritablePath`, the same check
  without the `.github/` rule, and leaves `.github/` to `IsNarrowable`, which admits only the top-level workflows.
- **M8** ✅ D8: `LinePullRequestAsync` asks for `workflows: write` when a written file is a workflow
  (`files.Any(LinePaths.IsWorkflow)`). Pinned in the narrowing test and the D16 test (`WriterAccesses[^1].Workflows`).
- **M9** ✅ D9: warning logs on a refused token, writer refusal or non-pushed outcome; the outcome note leads with
  "Not done: …" and sets `VersioningOnboardingOutcome.HasFailures`, which turns the toast into a warning;
  `VersioningSeedsRecipient` logs `MergedAsync`'s outcome. Test: `Issue649_D9_a_refused_line_pull_request_is_reported_first`.
- **M10** ✅ D10: `NarrowAsync` takes the cuts plus every supported line `SupportedLines` reads. S8 changed the plan:
  the writer keeps an identical commit rather than the job taking the base's date (§3). Tests:
  `Issue649_D10_a_supported_line_that_is_not_a_cut_is_narrowed`, the writer rerun test.
- **M11** ✅ D11-D15: `EOnboardingFeature.SdkFromGlobalJson`; on the `SetUpFeatures` virtual type the attributes
  `SdkFromGlobalJson` and the hidden `DotnetRepository` (the action sets it from `RepositoryManifest`: no manifest yet,
  NuGet produced/consumed, or a dotnet lookup platform), with `triggersRefresh` on DependencyUpdates and ReleaseLines.
  `SetUpFeatures.json` is a hand-authored virtual type (no CLR class for the synchronizer to read), so it is edited by
  hand like its existing attributes. `SetUpFeaturesActions.OnRefreshAsync` shows the box only for .NET and enables it
  only with dependency updates or release lines (`SetUpFeaturesActionsTests`). `GlobalJsonSdk` and the planner section
  "SDK from global.json" (`GlobalJsonSdkTests`, on SpaServices' real `pull-request.yml` and `global.json`).
  **Framework change this needed:** Spark's refresh endpoint skipped a virtual type (no CLR type). `IRefreshInvoker.InvokeVirtualAsync`
  resolves `{Name}Actions` by name, like the virtual load and write hooks, and duck-types
  `public Task OnRefreshAsync(SparkRefreshArgs)`; a misshapen method of that name throws (`RefreshInvokerTests.Issue649_*`).
  A refresh needs `New` (or `Read`) on the type, so security.json grants `New/SetUpFeatures` to **Signed-in users**,
  the group that already holds `SetUpFeatures/Repository` (the action checks the person's access to the repository
  itself). It opens nothing else: the create endpoint saves a class-less type only through an `OnSaveAsync` on its
  actions class (`DatabaseAccess.cs:233-252`), and `SetUpFeaturesActions` has none, so a create is refused; the
  scaffold is the form the action shows anyway. Recorded in `securityPosture.txt` through `--spark-synchronize-security`
  (CI's `--spark-verify-security` refused the first push: the posture lacked the right, and the hand-placed right was out
  of the synchronizer's order). `OnboardingFeatureDetection` never reports `SdkFromGlobalJson`: it is a conversion, not
  a setting the repository keeps, and starts unchecked (D12). CI's CodeCoverage.Tests caught the one test that expected
  every enum value (`OnboardingFeatureDetectionTests.Item615_…`, now excludes it).
- **M12** ✅ D16, **one deviation:** the line conversion rides in the narrowing pull request (`mintplayer/narrow-<line>`)
  instead of a separate `LineWorkflowsAsync` pull request, so two bot pull requests never edit the same workflow lines.
  A line that needs no narrowing gets the same branch with the title `OnboardingPlanner.SdkLineTitle`. The conversion
  runs before the narrowing, so the narrowing moves only `global.json` for the converted steps. Test:
  `Issue649_D16_sdk_from_global_json_opens_a_line_pull_request_that_converts_the_pins`.
- **M13** ✅ Docs: onboarding.md and dependency-updates.md; this PRD's status.
- **Tests were written after the code** for most of M7-M13 (owner allowed "if you wish, write the tests"); none was
  seen red against the old code. The writer test went red against the first fix (the `IsSafePath` refusal above), and
  #649 itself recorded the refusal in production.
- **After deploy (M7-M13), the D17 acceptance on SpaServices:**
  1. Check that master at the cut (16141c1) may ship as 10.x (owner).
  2. Run **Set up features** (ticking the new checkbox), which narrows `release/dotnet-10.x` (D10). Expect a narrowing
     pull request with the TFMs, EF Core and `global.json` (`sdk.version`, `tools.dotnet`); with the box, the same pull
     request switches the workflows to `global-json-file: global.json` (D16 as implemented), without it they carry
     rewritten pins. On master, the onboarding pull request (or the no-files path) converts the default branch's
     workflows. Green checks. That push answers S7 and the open half of S11.
  3. The narrowing merges through the queue; the line's versioning run tags `10.x.y` and the packages publish.
  4. One real fix merged on master with `backport: dotnet-10.x` (~~`backport: release/dotnet-10.x`~~: the label names
     the line, not the branch) lands on the line and publishes the next 10.x.
  **Progress (2026-11-13):** step 2 ran; #114 (narrowing) went red on CS1617 and NU1107, and #113 (current line) on Line
  policy. NU1107 was fixed in SpaServices (#115, backported as #116); the rest is #651 (D18-D20). Tracked in the
  untracked `docs/spaservices_dotnet10_line_PRD.md`.
  5. Then S9 (`gh search code`) and S10.
- **After deploy (M1-M4, done 2026-11-12):** S3 answered on SpaServices (§1c); `release/dotnet-10.x` exists.
- **After deploy (M5-M6):** the narrowing branch is rebuilt only by a new cut, so close SpaServices #110 and delete
  `mintplayer/narrow-dotnet-10.x`, or narrow the workflows there by hand; then S4, and click "SDK + afhankelijkheden
  bijwerken" on SpaServices and expect a pull request.

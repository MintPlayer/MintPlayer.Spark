# Onboarding creates the release lines — PRD and plan

Status: **merged and deployed** as #637 (835a40b67). Its first real cut on a repository that takes backports failed
(SpaServices, 2026-11-12: S2 below), fixed by PR #645 (`docs/lockfile_notice_and_line_cut_PRD.md` D2). Owner request, 2026-11-12:
"Shouldn't the bot be creating the release lines? why does the user need to do that?", then "Yes, implement both in
one PR".

## 1. Why

The Tools onboarding (MintPlayer/MintPlayer.AspNetCore.Tools#53, merged as bd6559cb8 on 2026-11-12) turned release
lines on and renamed `release/dotnet-6` and `release/dotnet-7` to their `.x` names, but left Tools without the line it
needs most: `release/dotnet-11.x`. With lines on, the default branch tags nothing, so until the current line exists
`/release` has nothing to promote and backports have nowhere to go.

Evidence:
- The first default-branch run after the merge (Tools run 43186708348, green) ran the versioning step with
  `cut-release-branches: true`, released nothing (`RELEASED: []`) and cut no line. Branches after it:
  `release/dotnet-6.x`, `release/dotnet-7.x` only.
- **Why no cut:** `ReleaseBranchCuts.ForLinesAsync` (`CodeCoverage.Versioning/Releases/ReleaseBranchCuts.cs:56-69`)
  cuts the line of a major only when no package has a tag at that major (`:60`). All 30 Tools tags are at major 11
  (the rc releases published from master before lines), so 11 counts as released. Spark had the same gap: its two lines
  were created by hand on 2026-11-06 (`docs/coverage_backport_plan.md` M11).
- **Why 10.x is invisible:** a `net10.0;net11.0` project anchors at the highest target, 11
  (`MajorAnchor.FromTargetFrameworks`, `CodeCoverage.Versioning/Versions/MajorAnchor.cs:25-49`). Nothing records that
  the owner still supports 10.
- **A person cannot create the line either.** `POST /git/refs` for `release/dotnet-10.x` and `release/dotnet-11.x` at
  bd6559cb8 answered `422 Reference update failed` (2026-11-12). Ruleset 3 (27099668) applies to those names with its
  required check "Line policy" (`GET /rules/branches/release/dotnet-10.x`), the commit has no such check, and only the
  App (Integration 4574022, `exempt`) bypasses it.

## 2. Decisions

| # | Decision | Evidence / reason |
|---|---|---|
| D1 | With *Release lines* picked, the onboarding creates the **current line** of every line the packages release on (`ReleasePlanner.LinesOf`: one per anchored package's ecosystem and major) that has no branch yet, counting the branches its own renames produce. | The owner's question above. `LinesOf` is the set `ForLinesAsync` cuts from, so the onboarding and the versioning run agree on what a line is. |
| D2 | **Older lines are offered, never guessed:** the lower majors a package still targets (`<TargetFrameworks>` below its anchor, e.g. `net10.0` in `net10.0;net11.0`) that have no branch. Set up features asks once per offered line, **Create it** (the default) or **Skip**, after the rulesets step and before the confirmation; the chosen ones are cuts, the skipped ones are listed in the pull request as "Not created, as chosen". Nothing else is offered. | Owner, 2026-11-12: "(2) the plan lists older lines for you to tick". A multi-target is the only place a repository records that it still supports a lower major. **Trap:** a library that multi-targets forever (compatibility, not support) is asked on every run of Set up features; skipping is one click. |
| D3 | **Where from:** at the onboarding pull request's merge, from its merge commit (`merged.MergeCommitSha ?? merged.HeadSha`, `OnboardingService.cs:240`), so the line carries the onboarding's own workflow triggers (#636 G3). With **no files** to change (an onboarded repository), at once, from the default branch's head, like the renames already are (`OnboardingService.cs:145-152`). | A line cut before the merge would lack `'release/*-*.x'` in its triggers and never publish. Zero files means the planner found the triggers in place already (#636). |
| D4 | **How:** `IForgeVersioning.CreateBranchAsync(repository, branch, sha)` (`CodeCoverage.Library/Forge/IForgeVersioning.cs:47`), the App's own token, which ruleset 3's exempt bypass admits. `Exists` counts as done; a refusal (`ForgeRequestException`) becomes a line in the outcome, like a refused rename. | The versioning run's cut uses the same call and treats `Exists` as done (`ReleaseBranchCutRecipient.cs:54-55`), so a first run racing the onboarding's cut is harmless. |
| D5 | **Recorded** on `VersioningOnboardingState` beside `Renames` (a `Cuts` list of branch names, and the chosen `OlderLines`), written when the pull request is opened or rebuilt, run once under `MergeHandledAt`. | Same lifecycle as the renames (`OnboardingService.cs:212-213`, `:236-240`). |
| D8 | **What is left to the versioning run:** a package anchored by `versioning.packages.<id>.anchor` (free, or a framework package) gets no line from the onboarding. | Its major needs the build's resolved versions, which the planner does not read. Test `LineCuts_a_line_that_exists_or_that_a_rename_produces_is_not_created_and_an_anchored_package_is_left_to_the_run`. |
| D6 | **Shown** in the confirmation: "Release lines created when it merges" (or "created now" with no files), each with why (`current line`, `still targets net10.0`). | The renames are listed the same way. |
| D7 | **Order at merge:** settings, renames, then cuts, then the regenerate-workflow pull requests (`LineWorkflowsAsync`), so a new line is never offered a workflow it already has. | `OnboardingService.cs:236-238`. |

## 3. Spikes

| Spike | Question | How | Result |
|---|---|---|---|
| S1 | Can a retry prompt offer a computed, tickable list? Is one needed? | Code reading: `SetUpRulesets.json:41-70` (Reference, `isArray`, query `Repository_PullRequestChecks`), `ForgeCheckActions.cs:95-118` (rows computed from `Repository.KnownChecks`, parent = the repository), `MergeQueueRulesetSetup.cs:158-182` (prefill a `string[]`, read back with `Names`). ng-spark renders a multi-select only for a Reference array (`po-form/src/spark-po-form.component.html:87`). | Yes, the rulesets form's pattern, but it needs rows a query can read with only the parent id (S1b). **Not used:** one yes/no retry prompt per offered line does the same with no new type, query, rows or security rights, and offered lines are few (one per lower `netX.0`). `SetUpFeaturesAction`, test `LineCuts_each_offered_older_line_is_asked_and_the_chosen_ones_are_created`. |
| S1b | Where would the picker's rows live? | Options: recompute in the query (a tree read per keystroke), or store the offered lines where the query finds them. | Moot after S1. The chosen lines are stored as `VersioningOnboardingState.OlderLines`, so a rebuild plans them again. |
| S2 | Does the App's exempt bypass on ruleset 3 admit a branch **creation** that the required "Line policy" check refuses for a person? | First real use: Tools after the deploy (re-run Set up features, D3's no-files path). SparkTest's ruleset 27049210 is the fallback sandbox. | **Answered in reverse (2026-11-12):** D4 assumed the bypass, but the planner gave it only to promoting repositories. SpaServices (backports) had ruleset 3 with no bypass and its `release/dotnet-10.x` cut answered 422. Fixed in [lockfile_notice_and_line_cut_PRD.md](lockfile_notice_and_line_cut_PRD.md) D2: ruleset 3 always exempts the App. Whether the bypass admits the creation is still to be seen on SpaServices (that PRD's S3). |
| S3 | Does the onboarding's cut race the first versioning run's `ForLinesAsync` cut? | Code reading. | **Harmless:** both call `CreateBranchAsync`; the second gets `Exists` (`ReleaseBranchCutRecipient.cs:54-55`). |
| S4 | Is a newly created older line (10.x) "supported", so backports go there? | `SupportedLines.Evaluate` (`CodeCoverage/PackageVersioning/SupportedLines.cs:35-69`). | An older line is supported while its platform release is not `eol` (.NET 10 is LTS). `backport.lines: retire` still removes it. |
| S5 | npm: does an Angular package record older supported majors the way a multi-target does? | Read `Ecosystems.Of` and the npm anchor. | Out of scope for D2: only .NET target frameworks are offered. An npm line is created by D1 only. |

## 4. Milestones (one PR)

- **M1** ✅ Planner: `OnboardingLineCuts.Plan` (`CodeCoverage/Onboarding/OnboardingLineCuts.cs`), `OnboardingPlan.Cuts`
  (current lines plus the chosen older ones) and `OfferedLines`, `OnboardingInputs.OlderLines`; text in the body.
- **M2** ✅ State + service: `VersioningOnboardingState.Cuts`/`OlderLines`; `OnboardAsync(repository, features, olderLines)`;
  created at the merge (D3-D5, D7) and at once with no files.
- **M3** ✅ Set up features: one Create/Skip prompt per offered line (S1), the confirmation lists "created when it merges"
  or "created now" (D6); translations `onboarding.olderLine.*`, `onboarding.summary.cut*`.
- **M4** ✅ Tests: `OnboardingPlannerTests.LineCuts_*`, `OnboardingServiceTests.LineCuts_*` plus the merge test,
  `SetUpFeaturesRulesetsTests.LineCuts_*`. Docs: `docs/code-coverage/onboarding.md`, this file,
  `docs/release_lines_recovery_plan.md` §2e.
- **After deploy** ✅ Tools has `release/dotnet-10.x` and `release/dotnet-11.x` (branch list checked 2026-11-12). Tools
  promotes, so the App was exempt there. A repository that takes backports failed the cut until PR #645 (S2).

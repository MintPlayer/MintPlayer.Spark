# Lockfile notices and release-line cuts: PRD and plan

Status: **implemented, in review**: PR #645 (branch `fix/onboarding-lockfile-deprecated-and-line-cut`) closes #643
and #644. M1-M4 are done. Left after the merge and deploy: S3 and S4 (§3), then the steps under "After deploy" (§4).
S1, S2 and S5 are open and outside this PR.
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

## 2. Decisions

| # | Decision | Evidence / reason |
|---|---|---|
| D1 | In `package-lock.json` and `npm-shrinkwrap.json`, a line that is **only** `"deprecated": "<one JSON string>"` (anchored over the whole line) is skipped by the source checks. Every other line is checked as before. | §1a. npm never fetches from the message. Anchoring the whole line means a `"resolved"` cannot ride on it (test `A_source_hidden_on_a_deprecation_line_is_still_refused`). Messages with `http:`/`git:` are common (e.g. request's "see https://github.com/request/request/issues/3142"), so the forbidden-source check skips it too. Scope kept to the evidence (owner preference for minimal diffs): `funding` and pnpm are spikes S1/S2. |
| D2 | Ruleset 3 **always** lists the App as `exempt`, promoting or not. `MergeQueueRulesetChoices.PromotesLines` and `ChooseAsync(..., promotesLinesComing)` are removed. | Owner, 2026-11-12: "Always exempt App" (picked over "exempt only with cuts" and "spike first"). §1b. The App uses the bypass to create lines (onboarding and the versioning run's `ReleaseBranchCuts`) and to push promotions. Backports still go through pull requests, and the queue waits for the line's required checks itself (merge-queue.md, "The queue accepts this one bypass"). Reverses merge-queue.md's "no bypass for the App" (superseded text updated there, in onboarding.md, onboarding_line_cuts_PRD.md S2 and release_lines_recovery_plan.md). |
| D3 | No automatic retry of a refused cut. After the deploy, re-running **Set up features** re-applies the rulesets (adding the bypass) and cuts a missing line at once through the no-files path. | `OnboardingService.cs:156-161` already cuts at once when nothing needs a pull request. Documented in onboarding.md. |
| D4 | The SpaServices follow-up (ng-bootstrap upgrade, Angular 22 for the demos, SDK on its release line) is a SpaServices issue, not code here. | Other repositories get an issue, never a commit from here (owner rule). |

## 3. Spikes

| Spike | Question | How | Result |
|---|---|---|---|
| S1 | Does npm's `funding` field refuse a patch the same way? `"funding": "https://…"` or `"funding": { "url": "https://…" }` on a host the base lockfile does not name. | Evidence: the SpaServices base lockfile has 128 `funding` entries on github.com (93), opencollective.com (48), tidelift.com (4), paulmillr.com (2). They pass only because the base names them. Next: find a real update that adds a new funding host (a package whose author moves to e.g. `buymeacoffee.com`), keep that patch as a fixture. | **Open.** Expected yes. Not fixed without a real fixture (parsers need real fixtures). |
| S2 | Does pnpm's `pnpm-lock.yaml` record `deprecated:` the same way, and with what quoting? | Run `pnpm install` of a deprecated package (e.g. `request@2.88.2`) in a scratch dir and diff the lockfile. | **Open.** If yes, add a `Notice` regex for pnpm from that real line. |
| S3 | Does the App's `exempt` bypass on ruleset 3 admit a branch **creation** that the required checks refuse for a person? (onboarding_line_cuts_PRD S2) | After the deploy: re-run Set up features on SpaServices, expect `release/dotnet-10.x` created at once. Fallback sandbox: PieterjanDeClippel/SparkTest. | **Pending (after deploy).** Tools (promote, App exempt) is indirect evidence. |
| S4 | Which other repositories have a ruleset 3 with no App bypass today? | `GET /repos/{r}/rulesets` for each onboarded MintPlayer repository with lines; list those without `Integration 4574022 exempt` on "merge-queue: release-line checks". | **Open.** Each needs one Set up run after the deploy. |
| S5 | Should "SDK + dependencies bijwerken" say why a run ended at once (`workflow-outdated`) instead of "started"? | Read the repository page's run panel. | **Open**, separate from this PR. The SpaServices runs 72-79 ended in 4 s and the page looked idle. |

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
- **After deploy:** S3 on SpaServices (re-run Set up features), then S4; click "SDK + afhankelijkheden bijwerken" and
  expect a pull request.

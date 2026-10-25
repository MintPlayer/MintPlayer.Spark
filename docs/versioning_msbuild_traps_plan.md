# Versioning traps — plan

**PRD:** [versioning_msbuild_traps_PRD.md](versioning_msbuild_traps_PRD.md) · **Issue:** #544 · **Parent:** #153 ·
**Branch:** `fix/issue544-versioning-traps` · **one pull request**. After the deploy and the MSBuild release, the
Dotnet.Tools onboarding (#202) is regenerated.

## Status

2026-10-25: investigated, grilled, then the approach was revisited. Angular and .NET were investigated (PRD, Prior
art). Decided: tag after the tests, accept dangling tags, recover by re-running; no cleanup mechanism or setting of any
kind (owner: "sounds good to me"). PRD and plan rewritten. D5 added (release-run status + re-run button on the commit
graph). Q1 = A, Q2 = A, Q5 moot (the package stays on GitHub Packages), Q9–Q11 settled (D4: `nuget.config` mapping
only, `source-url` stays, Spark's publish unchanged). D5's two remaining points left to me by the owner: the re-run
button is disabled while a newer run is unfinished, and server mode passes the run id to the app. Grill closed.
Next: the spikes (S0, S1, S3, S4, S5). Next: a grill round, then the spikes.

## Spikes

Each spike ends with a written result in this file (numbers, file:line, or a red→green test) before the milestone
that depends on it starts.

| # | Question | Time box | Blocks |
|---|---|---|---|
| S0 | **Re-run after a dangling tag.** On a scratch copy of Dotnet.Tools (local mode, no push to a real remote: a bare repo as `origin`): run the engine, push the tags, skip the package push. (a) Re-run at the same commit: the outcome is `AlreadyPresent` for every tag, no new version is planned, and `dotnet pack` packs at the tagged versions. (b) Commit a change to one package only, run again: the unchanged packages pack at the dangling versions, the changed one at the next version. Cite the planner's file:line for both. | 0.5 day | M3, M5 |
| S1 | **Detecting a global version.** In the inline RoslynCodeTaskFactory task, does `((IBuildEngine6)BuildEngine).GetGlobalProperties()` list `Version` for `dotnet pack -p:Version=9.9.9`? Check it in the outer and inner builds of a multi-targeted project, and in a ProjectReference build. Compare with the probe trick in the target, and with `TreatAsLocalProperty`. Check the oldest SDK used by an onboarded repo (net8). Result: the chosen mechanism + a minimal repro project. | 0.5 day | M1 |
| S3 | **`source-url` + mapping.** A test repo with setup-dotnet `source-url` = the MintPlayer feed, a root `nuget.config` listing that feed (under `<clear />`) and mapping MintPlayer.Versioning.MSBuild to nuget.org, and a repo NOT on the package's Actions access list. Restore must succeed with no request to GitHub Packages for the package, and a GitHub-only package mapped to the feed must still restore with the parent-directory credentials. Then the same without the feed listed (key `Source`): confirm it goes unused. | 0.5 day | M4 |
| S4 | **A real-pack test harness.** How fast can a test evaluate the real `.targets` (Spark imports them from source through `MintPlayerVersioningTaskSource`) with `dotnet pack` on a temp git repo? It has to cover single- and multi-target, tagged, untagged, push and pull-request env, and explicit version. Target: under 60 s for the class, or it joins an existing CI group (CLAUDE.md, the 20-runner cap). | 0.5 day | M2 |

| S5 | **Tag messages in the graph.** Can `CommitGraphReader` (via the sidecar mirror, `CodeCoverage/CommitGraph/`) read annotated tag messages for the refs it already returns, in one `git for-each-ref` call, on the treeless mirrors? Measure on Spark's mirror (~300 tags). And: does `rerun-failed-jobs` restart cancelled jobs, and what does it answer for a run older than GitHub's re-run window? | 0.5 day | M6 |

## Milestones

| # | Milestone | Covers |
|---|---|---|
| M1 | Target/task: an explicit global `Version`/`PackageVersion` skips resolution and keeps `IsPackable`. `PackageVersion`-only sets `Version`. MPV001 never fires for an explicit version. MPV005 (error) when a project packed directly (`$(SolutionPath)` unset) would get the placeholder; check the onboarded repos' workflows for a per-csproj pack loop. | D1, D2, R1–R3 |
| M2 | Real-pack test class (S4) with every R1–R3 case, plus a regression for the multi-targeting placeholder incident. Verify `tools/layer-transport-check.mjs:74` now packs at its version. | R8 |
| M3 | Onboarding workflow rewrite (`VersioningOnboarding.Workflow`): the Version step goes after the last test step (not merely above the first pack step), `--no-build` stays stripped, a comment above the pack step explains the second build, and a `concurrency` group with `cancel-in-progress: true` is added when the workflow has none. `VersioningOnboardingTests` expectations updated, including an existing `concurrency` key left alone. If S0 finds a re-run gap, fix it in the engine here. | D3, R4, R5 |
| M4 | Onboarding (Spark's publish job is unchanged, Q11): detect GitHub Packages restore sources (workflows + every `nuget.config`) and add or update the root `nuget.config` (PRD D4: the feed listed under the exact `source-url` URL, the exact-id mapping of MintPlayer.Versioning.MSBuild to nuget.org); workflows untouched; a `notes` warning with file:line for any shape it cannot rewrite. Tests: no `nuget.config`, one without mapping, one with `MintPlayer.*` mapped to GitHub, one with `<clear />`, a feed URL differing only by trailing slash or case. | D4, R6, R7 |
| M5 | Spark's `dotnet-build-master.yml`: `release` needs `tests` and runs build → version → pack linearly; concurrency unchanged. | D3, R4 |
| M6 | Release status on the commit graph: `TagAnnotation` gains `run:` (format + parse, unknown-line tolerance kept); the action passes the run in local and server mode; the graph reader returns each tagged commit's run; a status endpoint (lazy, rows on screen, cached while unfinished); a forge `RerunFailedJobsAsync` (`rerun-failed-jobs`, same 403/404/422 handling as `RerunCheckAsync`); a write-access-only re-run action, refused while a newer run of the workflow is unfinished; the graph overlay and button (ng-bootstrap components). Rebuild the action's `dist` from the main checkout. | D5, R9, R10 |
| M7 | Docs: #153 PRD D9 (superseded bullets marked), `docs/code-coverage/versioning.md` (explicit version; workflow order; the second-build comment; troubleshooting row "failed or cancelled after Version: re-run it, a tag without a package is harmless", plus the NU1603 case; GitHub Packages), this plan's Status. | — |
| M8 | Sweep (`npm run test:affected`, Developer licence) once, at the end. Then, after merge and the MSBuild release: regenerate Dotnet.Tools #202 without the `MintPlayerVersioningSkip` workaround. Check the root `nuget.config` of AspNetCore.Tools and SpaServices. | — |

## Test list (written per milestone, run once in M8)

- Explicit `-p:Version` packs at that version: tagged / untagged × push / pull_request × single / multi-target.
- `-p:PackageVersion` alone: package and assembly versions both equal it.
- No explicit version: tag, `0.0.0-untagged` and placeholder cases unchanged (existing `TagVersionTests` + real pack).
- MPV005: a direct pack of an untagged project on a push build fails; the same project in a solution pack warns;
  `IsPackable=false` skips both.
- Onboarding workflow rewrite: Version after the tests, the second-build comment present, `concurrency` added only
  when absent.
- Re-run after a dangling tag (S0's scenarios, as an engine test): same commit plans nothing new; a later commit packs
  unchanged packages at the dangling version.
- Onboarding detects `source-url: https://nuget.pkg.github.com/...` and a nuget.config `<add ... nuget.pkg.github.com>`,
  writes the feed + exact-id mapping into the root `nuget.config`, leaves the workflows byte-identical; no change
  when neither is present.
- `TagAnnotation`: `run:` round-trips; a message without it parses (seed tag); a malformed URL counts as absent.
- Release status: a tagged commit gets its run's status; an untagged commit and a tag without `run:` get none; the
  re-run is refused for a reader (403/404 per visibility) and while a newer run is unfinished; `rerun-failed-jobs`
  403 maps to the permission notice.

## Deliberately not doing

- Removing dangling tags, automatically or on request, and any setting for it (PRD D3, Out of scope).

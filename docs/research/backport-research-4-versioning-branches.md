# Research 4: versioning engine, platform markers and CI vs release branches (issue #568)

All paths relative to C:\Repos\MintPlayer.Spark. The engine is C#: `apps/CodeCoverage/CodeCoverage.Versioning` (pure logic),
`CodeCoverage.Versioning.Cli` (the `manifest` / `run` / `push-tags` commands), driven by the Node action
`apps/CodeCoverage/versioning-action/src/{main,engine,git,semver,npm}.ts`. The same engine also runs inside the CodeCoverage
server (mode `server`); Spark uses `mode: local` (coverage.yml:38).

IMPORTANT: release branches and backports are ALREADY DESIGNED AND LARGELY BUILT (PRD D10, D12, D11; `BackportCommand.cs`,
`BackportRecipients.cs`, `ReleaseBranchCutRecipient.cs`, `GitWriter/Jobs/BackportService.cs`, `ReleaseBranchCuts.cs`).
Issue #568 is mostly about enabling/adapting them for this repo, not a new engine. docs/coverage_package_versioning_PRD.md:339-354 (D10).

## 1. How the next version is computed

Pure arithmetic: `CodeCoverage.Versioning/Versions/VersionCalculator.cs:33-79` (`Next`). Caller: `Releases/ReleasePlanner.cs:150-262`.

- **Major**: `MajorAnchor` (`Versions/MajorAnchor.cs`; PRD D7 table, PRD:225-243): NuGet net5+ = highest `netX` from the *evaluated*
  `TargetFrameworks` (build manifest facts); Angular library = resolved `@angular/core` in the lockfile; netstandard/net4x and plain
  node libs = "free" (keep last tag's major, only tagged from the default branch). Override `versioning.packages.<id>.anchor`.
  Used at ReleasePlanner.cs:178-184 (`AnchorOf`), 261-270. VersionCalculator.cs:44 `major = anchor.Major ?? LastTag?.Major ?? 1`.
- **Previous version**: NOT "highest reachable tag". `TagHistory.ReadAsync` (`Releases/TagHistory.cs:41-58`) reads ALL tags of the
  repository (`git for-each-ref refs/tags`). Planner: `history = Tags.Of(id)` (all tags, sorted). Default branch: `LastTag =
  history.Last` (global highest, ReleasePlanner.cs:231). Release branch: `lastOnLine = highest tag whose Major == lineMajor`
  (ReleasePlanner.cs:178-180, 231), where `lineMajor = anchor.Major ?? BranchMajor ?? last tag major`. `Existing` = every tag version
  (+ registry versions) of the package, used for collision/"not above existing" checks (VersionCalculator.cs:57-58, 71-75).
  Reachability matters only in two other places: `MintPlayer.Versioning.MSBuild` (`git tag --merged HEAD --list "<id>/v*"`,
  `CodeCoverage.Versioning.MSBuild/build/MintPlayer.Versioning.MSBuild.cs:91`, i.e. highest reachable tag at pack time) and
  `TagWriter.ReachableAsync` (TagWriter.cs:111-123: a tag is only written on a commit that is an ancestor of the run's branch ref).
- **What is released**: only packages with changed inputs since `lastOnLine` (tree-sha diff; inputs = project dir + `Directory.Build.props/targets`,
  `Directory.Packages.props`, `global.json`, `nuget.config`, lockfile, minus `ignore` globs; PRD D4:156-176), or a dependency released
  in the same run (NuGet dependents always follow), lockstep group, or a hand-pushed tag on HEAD. No `lastOnLine` => "first release on the N.x line" (ReleasePlanner.cs:186-188).
- **Minor vs patch** (VersionCalculator.cs:52-60, PRD D8): open prerelease line (`highest` is prerelease) => continue same core;
  no stable yet => `M.0.0`; `Upstream is not null || ApiChanged` => minor+1; else patch+1. API hash is stored in the annotated tag
  (D6; `ReleasePlanner.cs:219-220`: `lastApiHash = lastOnLine?.Annotation?.ApiHash ?? facts.PreviousApiHash` (registry)). A breaking change is
  also only a minor. PR titles play no part.
- **Prerelease**: label pinned (`versioning.prerelease: preview`, coverage.yml:44; ng-spark and MintPlayer.Versioning.MSBuild exempt, `upstream`
  mirror) else mirrored from upstream deps; also the SDK joins as upstream if package major == SDK major (D8, PRD:245-278). Result
  `M.m.0-preview.N`, counter N = max existing for that core+label (VersionCalculator.cs:62-69). NOTE `Upstream is not null` forces the
  minor branch, so with the `preview` pin every release is `minor+1`, never a patch.
- Refusals: major would go down (VersionCalculator.cs:46-47), major != BranchMajor (48-49), next <= highest/existing (71-75). ALL-OR-NOTHING: any
  refused package stops the entire run, no tags (`ReleaseRun.cs:96`).

## 2. Running the same engine on a release branch

Dispatch: `Cli/Program.cs:64-101`. `branch != defaultBranch` and the branch must match `versioning.releaseBranches` (default `release/*.x`, ReleaseRun.cs:176-200
`ReleaseBranches.IsMatch`), else `branch-not-versioned` refusal (Program.cs:90-91). `BranchMajor` = numeric capture of the `*` (`release/10.x` => 10).
Config (`coverage.yml`, incl. `releaseBranches`, `prerelease`, anchors) is ALWAYS read from the DEFAULT branch (`main.ts:153`, `git.ts:22`, PRD D17).
`prepareHistory` fetches `--tags` plus the release branch and default branch (git.ts:15-19). Tagger/reachability: `refs/remotes/origin/<branch>` only (Program.cs:95, 154).

Findings for `release/10.x` forked before the net11 move:
- **Previous tag**: picks highest tag with Major == 10 anywhere in the repo, so master's 11.x tags do NOT confuse it (ReleasePlanner.cs:178-180, 231 uses
  `lastOnLine` when BranchMajor != null). Major computed from the branch's own csproj TFM = 10; = BranchMajor 10 => OK => `10.x.y`.
- **Name must be `release/10.x` (numeric)**. A name like `release/net10` is not matched by the default pattern (=> `branch-not-versioned`). If a
  pattern such as `release/net*` were added, `BranchMajor` stays null (captured text not digits, ReleaseRun.cs:187-195), then the planner passes
  `history.Last` (the global highest, 11.x) as `LastTag` and VersionCalculator.cs:46 refuses "major would go down 11 -> 10". So only `release/<N>.x`
  works, or the engine must be changed.
- **One major per branch, one ecosystem per branch**: BranchMajor applies to every changed package, NuGet and npm alike (VersionCalculator.cs:48).
  `release/10.x` (net10.0) with a changed ng-spark (Angular 22) is refused; `release/21.x` with a changed net11 NuGet package is refused; and the whole
  run is refused (all-or-nothing). Danger: shared inputs (`package-lock.json`, `Directory.Build.props`, `global.json`) count as inputs of every
  package (D4), so a backport touching them re-releases the other ecosystem's packages and trips the refusal. A repo with both ecosystems
  needs either disjoint backport diffs or an engine change (per-ecosystem major / branch). Free packages are skipped off the default branch
  (VersionCalculator.cs:40-41).
- **Cutoff/bootstrapping**: today there are NO `<id>/v10.*` or `v21.*` tags and no `release/*` branch exists (git tag -l, git branch -a). All Spark NuGet
  packages are tagged `11.0.0-preview.N` (highest MintPlayer.Spark/v11.0.0-preview.120), ng-spark `22.48.0`. First engine run on release/10.x => no `lastOnLine`
  => "first release on the 10.x line" => `10.0.0-preview.1` (preview pin, coverage.yml:44) and it releases EVERY package present on that branch. Before that,
  MSBuild packs at `0.0.0-untagged` / not packed (no reachable tag; targets.cs:115-121). Registry versions also count in `Existing` (D8/PackageRegistry),
  so any 10.x previously published to nuget.org collides/continues.
- **Backport = minor or patch?** Per D8: patch only if the API hash equals the line's last tag hash AND no upstream label applies. Because coverage.yml pins
  `prerelease: preview` for Spark packages, `Upstream` is always set => always a minor (10.1.0-preview.N etc.), and while the pin remains a 10.x line can
  never go stable. A feature backport changes the hash => minor (unpinned); a pure fix => patch. ng-spark is exempt from the pin (stable) => patch/minor by hash.
- **Tag from master confusing it**: no for version choice. But `StalePlan` fingerprint covers ALL tags (`TagHistory.Fingerprint`, TagHistory.cs:27-31): if master
  (or another release branch) pushes a tag between a release branch's plan and release job, the release job refuses "version tags changed"
  (ReleaseRun.cs:143-148). Concurrency groups are per ref (dotnet-build-master.yml:14-16 `github.workflow-github.ref`), so master and release runs overlap.
- **Default-branch assumptions**: (a) config from default branch only; (b) `OnDefaultBranch` gate: free packages, and `ReleaseBranchCuts` (D12 auto-cut of
  `release/<M(first parent)>.x` when master's major moves, ReleaseRun.cs:102-103) run only on the default branch; (c) `movedFrom` major detection only on default
  (ReleasePlanner.cs:225, 255); (d) the action requires `repository.default_branch` in the event (main.ts:98-105); (e) `coverage.yml` must have `versioning:` on
  default; (f) npm dist-tag logic (below). There are no seal checks keyed to a branch name other than the `releaseBranches` match.
- Anchor override `versioning.packages.<id>.anchor` is global (read from default); an override valid on master applies on every branch.
- Backport mechanism (already built): label `backport: 10.x` or `/backport 10.x` on a merged PR; resolved against `releaseBranches` (BackportRecipients.cs:107-120);
  dry-run merge in memory for every line, all-or-nothing, pushes `backport/<pr>-<line>` and opens a PR into the release branch (BackportRecipients.cs:171-189).
  PRD D10 says CI on that PR "is the compile check against the old TFM" (PRD:353).

## 3. Platform marker files (conflict candidates when applying a master commit to a release branch)

No `Directory.Packages.props` (central package management) exists in the repo itself (only in test fixtures). Versions of library package references are inline in each csproj.
- `global.json:3` sdk `11.0.100-rc.2.26504.105` (and `rollForward`). Also a D4 input of every package.
- `<TargetFramework>`: inline in each csproj. Non-fixture csproj: 89 x `net11.0`, 5 x `netstandard2.0`, 2 x `net8.0`, (+1 multi `net6.0;net8.0` in fixtures). Libs e.g.
  `libs/spark/MintPlayer.Spark/MintPlayer.Spark.csproj`, every `libs/**`, `apps/**`, `tests/**` csproj. Per-csproj `Microsoft.AspNetCore.*`/`Microsoft.Extensions.*` PackageReference versions also track the TFM (inline).
- `Directory.Build.props` / `Directory.Build.targets`: no TFM and no version there (props only sets NpmInstallWorkingDirectory/SparkAgentsGuideMode; targets imports `MintPlayer.Versioning.MSBuild`). Low conflict risk.
- Library `Targets/*.props|*.targets` (spark.props, spark-authorization.*, etc.) - check for TFM-conditioned items; not markers themselves.
- Root `package.json:28-37,63-75`: `@angular/*` 22.2.2 (animations, cdk, common, compiler, core, forms, platform-browser, platform-server, router, service-worker),
  `@angular/build|cli|compiler-cli|ssr` 22.2.2, `@angular-devkit/build-angular` 22.2.2, `@nx/angular` 23.3.0, `nx` 23.3.0, `ng-packagr` 22.2.4, `@analogjs/*`; `package-lock.json` (root; very conflict-prone, 1 workspace lockfile resolves `@angular/core`, which is the Angular anchor).
- `libs/node_packages/ng-spark/package.json`: `version` 22.44.0 (stale-by-design, "don't bump"), peer `@angular/core ^22.0.0` (line 16). Only one library package.json contains `@angular/core`. Also the `@spark-demo/*` / `@spark-apps/*` ClientApp package.json files.
- `nx.json` (nx plugin versions/targets; `build:release` inputs) and `tsconfig.base.json` (paths), `.config/dotnet-tools.json`.
- Generated/regenerated files likely conflicting regardless of platform: `apps/*/*/App_Data/**` (modelHashes, securityPosture, translations, security.json, Model/*.json), route snapshots `**/RouteSnapshots/*.txt`, layering goldens. A release branch's merge queue has `regenerate.yml` auto-resolve rules for `regeneratedPaths`, but coverage.yml `mergeQueue.branches: [master]` (coverage.yml:8) means the queue/regeneration does not apply to release branches.
- Workflow/action files with platform pins: `.github/actions/setup/action.yml:18` (dotnet 11.0.100-rc.2.26504.105), `:27` (node 24); `.github/workflows/dotnet-build-master.yml:66, 288` (dotnet-version 11.0.100-rc.2...), `:230` (node 24); `pull-request.yml:217` (node 24), `:323,345,362` (node 24.x); `coverage-action-publish.yml:168`, `versioning-action-publish.yml:115`, `dependency-action-publish.yml:115` (node 24.x); Dockerfiles `apps/*/**/Dockerfile` (dotnet SDK/runtime images, Node) and `.github/dependabot.yml`.
- No `.nvmrc`/`.node-version`. `docs/*` not markers.
- Versions: not markers (csproj `0.0.0-PLACEHOLDER`, tags only; D1 "bot never commits a version change"), which is why the issue's "version numbers never conflict" holds. Prerelease/`SparkSchemaRevision` env is a separate concern (below).

## 4. CI workflows and what a release branch needs

Triggers (grep of `.github/workflows`):
- `pull-request.yml:5` `pull_request: branches: [ master ]` -> a backport PR into `release/10.x` gets NO CI. (PRD D10 relies on CI on that PR.) Needs `[master, 'release/*.x']`.
  It also reuses `test.yml` (reusable, `workflow_call`; no branch filter of its own) and the Nx cache token logic (`default_branch` => RW token; release branches get RO).
- `dotnet-build-master.yml:5-6` (`publish-release`) `push: branches: ['master']` -> release branch pushes build/tag/publish nothing. Needs `'release/*.x'` added.
  Jobs: `schema-revision` (:36-120; creates `schemas/v{n+1}` GitHub releases + dispatches `spark-schemas-deploy.yml` at `default_branch`), `release` (:132-215; builds with Nx, `Version the packages` uses `./apps/CodeCoverage/versioning-action` from the checkout = the branch's own source, `dotnet pack`), `tests` (test.yml), `coverage`, `publish` (:267-325).
  Problems on a release branch: `schema-revision` would compare/create schema releases from the old code and could publish a wrong/older `schemas/v{n+1}` (must be skipped or branch-scoped on non-default branches, and the old branch's embedded revision must not collide; `SparkSchemaRevision` env is read by MintPlayer.Spark.csproj); `coverage` upload says "whole workspace baseline" for the default branch only; nx remote cache RW token only for default branch (fine, RO elsewhere).
  `release` runs the versioning action from the branch's own source, so the release branch carries its own (old) versioning-action/engine: either fine or backport engine fixes. The action reads `coverage.yml` from the default branch (git.ts:22) so branch edits to `versioning:` are ignored.
  `runs-on` dotnet/node versions in this workflow and `.github/actions/setup` come from the branch's own copy, so a release/10.x branch must set dotnet 10.x SDK and keep global.json there (these are the platform-marker conflicts).
- Deploy workflows `code-coverage-deploy.yml:13`, `mintplayer-deploy.yml:9`, `seotester-deploy.yml:11`, `mail-relay-deploy.yml:9`, `spark-schemas-deploy.yml:19`, `versioning-action-publish.yml:21`, `dependency-action-publish.yml:22` : `branches: master` (+ `if: github.ref == 'refs/heads/master'` at deploy jobs). Production apps must stay master-only: no change. Because `branches: [master]` they also will not misfire on release pushes.
- `regenerate.yml`: workflow_dispatch only, driven by the merge queue; `coverage.yml mergeQueue.branches: [master]` (coverage.yml:8) means backport PRs into release branches are not queued (they can be merged normally).
- `coverage-action-publish.yml`: its own branch filters (line 28) and "MUST NOT run on every master push" note; leave on master.
- Rulesets: PRD D12 says to add a `release/*` ruleset (Queue App exemption per memory `reference_merge_queue_ruleset_exempt`).

Publishing and npm `latest`:
- NuGet: `dotnet nuget push --skip-duplicate` (dotnet-build-master.yml:305,308). "highest version wins" on nuget.org, so 10.x does not hurt 11.x (PRD D11:356-358). Trusted publishing policy on nuget.org is keyed to the workflow file name `dotnet-build-master.yml` (comment at :294-296); policies may also be ref-scoped, verify (not visible in repo). The workflow name stays the same on a release branch, so a ref-less policy works.
- npm: dist-tag is computed by the versioning action: `semver.ts:35-50 distTag`: any prerelease => `next`; stable of the highest stable major => `latest`; stable of an older major => `v<major>-lts`. Stamped as `publishConfig.tag` into package.json/dist copy (`npm.ts:20-23`, `main.ts:323-338`), output `dist-tags` (README:137). So a stable `21.x` ng-spark backport gets `v21-lts` and does not move `latest`, because the global tag history (`released` = all tags) contains 22 stable. CAVEATS: (1) a prerelease backport would take `next` away from master's line (any prerelease = `next`, regardless of major); (2) the publish step `MintPlayer/github-actions/publish-npm-packages@b08fbdd...` (dotnet-build-master.yml:315) must honor `publishConfig.tag` - external action, unverified here; (3) it pushes GitHub Packages too (no dist-tag concept there).
- `dotnet-build-master.yml:313` publish folder is `libs/node_packages/ng-spark/dist` only.

## Recommendations / open points for the issue
1. Name release branches `release/<major>.x` (e.g. `release/10.x` for .NET, `release/21.x` for Angular). `release/net10` would need engine changes (BranchMajor parsing, ReleasePlanner LastTag selection).
2. Because BranchMajor applies to all ecosystems, either keep one ecosystem per branch and make sure backports do not touch shared inputs (package-lock.json, global.json, Directory.Build.*), or extend the engine to apply BranchMajor only to packages whose ecosystem matches the branch (a refused package currently aborts the whole run).
3. Add `releaseBranches` (explicit list) to coverage.yml on master; remove/override the `preview` pin if a 10.x line should be able to ship stable and patch-level releases.
4. Workflows: add `release/*.x` to `pull-request.yml` and `dotnet-build-master.yml` triggers; guard `schema-revision` (and the coverage baseline upload) to the default branch; put a `release/*` ruleset; make dist-tag handling for prereleases not use `next` on older majors; add a cross-branch concurrency group for the tag-writing (plan/release fingerprint is global).
5. Seeding: no `10.x`/`21.x` tags exist; first run will "first release on the line" every package on the branch, at 10.0.0-preview.1.

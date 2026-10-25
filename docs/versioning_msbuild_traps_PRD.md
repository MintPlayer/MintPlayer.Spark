# Versioning traps: an explicit Version is ignored, onboarded repos pack untested binaries, GitHub Packages 403s

**Issue:** #544 · **Parent:** #153 ([coverage_package_versioning_PRD.md](coverage_package_versioning_PRD.md), D9)
· **Plan:** [versioning_msbuild_traps_plan.md](versioning_msbuild_traps_plan.md) · **one pull request**

> **Rewritten 2026-10-25 (second pass).** The first draft and its grill spent most of their effort on keeping a
> cancelled or failed run from leaving tags behind (protected tails, a post-step rollback, a bot-dispatched cleanup
> workflow, a split push phase). A proposal to go further — tags only after a confirmed publish, with a release
> record and a plan/publish/finalize state machine — was also considered. The owner rejected that direction as "a
> significantly more inconvenient solution" for an unbothersome problem. Angular and .NET were investigated instead
> (Prior art, below). The decision now is: **a dangling tag is harmless, and the recovery is re-running the run**
> (D3). Superseded decisions are listed at the end.

## The problem, stated plainly

The three defects below were found while onboarding MintPlayer.Dotnet.Tools (#202). They are independent, but every
onboarding PR hits all three, so they ship together.

1. **`dotnet pack -p:Version=9.9.9` produces no package and exits 0.** The `MintPlayerVersioning` target always
   resolves a version from the tags. On a push build with no reachable tag it gets `0.0.0-PLACEHOLDER` and sets
   `IsPackable=false`, so `GenerateNuspec` is skipped. A pull-request build packs `0.0.0-untagged` instead of `9.9.9`:
   the target's assignment overrides even the global `Version` at execution time (spike S1). Either way, the only
   signal is warning MPV001.
2. **The publish workflow tags before the tests have passed.** Spark's `release` job pushes the tags while the
   sharded `tests` job is still running, so a red run leaves release tags behind. (The onboarding's second build —
   `--no-build` stripped — is deliberate and stays; see D3.)
3. **Referencing MintPlayer.Versioning.MSBuild can break restore through GitHub Packages.** A repository whose CI
   restores from `nuget.pkg.github.com` (setup-dotnet `source-url`, or a nuget.config entry) gets NU1301 403. Spark
   pushes the package to GitHub Packages, and a `GITHUB_TOKEN` can read it there only when the package's *Manage
   Actions access* lists the repository. The onboarding gives no warning.

## Evidence (investigation 2026-10-25)

### Problem 1: the target

- `build/MintPlayer.Versioning.MSBuild.targets:31-51`: the condition is `DesignTimeBuild`, `MintPlayerVersioningSkip`,
  `IsPackable` and the done flag. Nothing looks at an explicit version. `:44-45` assigns `Version`/`PackageVersion`
  (which override even a global value at execution time, S1). `:48` sets `IsPackable=false` on the placeholder (always takes effect).
- MPV001 is raised in `build/MintPlayer.Versioning.MSBuild.cs:83` (untagged) and `:89` (placeholder). The
  untagged-vs-placeholder decision is `PacksUntagged` (`.cs:153`). MPV004 (`targets:57-61`) does not fire, because
  `IsPackable` is already false.
- `buildMultiTargeting/…targets:7` imports `build/`, so a fix in `build/` covers the outer build too. Global
  properties flow to the inner builds and to ProjectReference builds, so every build sees the same explicit version.
- Callers: Dotnet.Tools' `PackAndConsumeTests` (`-p:Version=9.9.9`; failing runs 40137704671, 40141843868 PR /
  40141839945 push) and Spark's `tools/layer-transport-check.mjs:74`. No in-repo caller passes `PackageVersion`,
  `VersionPrefix` or `VersionSuffix`.
- Tests: `CodeCoverage.Versioning.Tests/MSBuild/TagVersionTests.cs` covers the task only (`task.Execute()` with a fake
  `IBuildEngine`, `:128`). **No test evaluates the `.targets` file or runs a real `dotnet pack`**, which is why this
  defect, and the multi-targeting incident before it, reached a release.

### Problem 2: the workflow

- Onboarding rewrite: `VersioningOnboarding.Workflow` (`apps/CodeCoverage/CodeCoverage/PackageVersioning/VersioningOnboarding.cs:268-349`).
  It inserts "Version the packages" above the first pack/publish step (`:303-315`), strips `--no-build` from every
  later `dotnet pack` (`:327`, regex `NoBuild()` `:534-535`), and adds `--skip-duplicate` (`:328-330`). It emits no
  `concurrency` key. The expected output is pinned in `CodeCoverage.Tests/PackageVersioning/VersioningOnboardingTests.cs:155-170`.
- Spark: `.github/workflows/dotnet-build-master.yml`. The `release` job runs Build (`:166-170`), then Version, which
  **pushes the tags** (`:176-180`), then `dotnet pack` without `--no-build` (`:187-188`, rationale `:182-185`). The
  tests run in a separate job (`:202-207` → `test.yml`). `publish` needs both (`:266-268`), so nothing *ships* on
  red, but the tags do land. Concurrency: `group: ${{ github.workflow }}-${{ github.head_ref || github.ref }}`,
  `cancel-in-progress: true` (`:12-14`).
- The rebuild is unavoidable: the engine picks minor vs patch from an API hash of the **built** output
  (`versioning-action/src/engine.ts:79-85`; #153 PRD D8), and the MSBuild target stamps the version into the
  assemblies at compile time (`targets:41-45`). So the version is only known after one build, and the assemblies
  only carry it after another. `--no-build` also implies `--no-restore`, which leaves the nuspec's dependency
  versions stale (`docs/code-coverage/versioning.md:60`).
- Re-run behaviour already in place:
  - `TagWriter.PushAsync` (`CodeCoverage.Versioning/Releases/TagWriter.cs:142-175`) is a non-force push. A rejected
    push is read back: the same commit is `AlreadyPresent`, another commit is `Conflict` (the run fails loudly).
  - Packages are pushed with `--skip-duplicate` (NuGet) and only for versions the registry lacks (npm, #153 PRD:310).
  - A Release that failed the first time is created by a re-run (#153 plan:155).
- Timings, Spark master run 40140487503: `build release` 9.3 min, the test shards 6.4 min, in parallel; the Version
  step 91 s, Pack 42 s, `publish` 46 s.

### Problem 3: the restore source

- Root cause and the repo-side fix: Dotnet.Tools #200/#201 removed `source-url`/`NUGET_AUTH_TOKEN` from Setup .NET,
  so CI restores from nuget.org only, and PushGithub now names `--source` explicitly.
- State of the onboarded repos: the workflows of AspNetCore.Tools (`publish-release.yml:110`), SpaServices
  (`build-master.yml:78`) and Dotnet.Tools (`dotnet-build-master.yml:86`) reference `nuget.pkg.github.com` only in
  an explicit push `--source`. Their root `nuget.config` files were **not** checked.
- Spark itself still sets `source-url` + `NUGET_AUTH_TOKEN` on the publish job (`dotnet-build-master.yml:286-290`).
  PushGithub (`:296-298`) relies on it and pushes `nupkgs/*.nupkg`, **including MintPlayer.Versioning.MSBuild**.
  Spark's `nuget.config:7-9` lists `github-mintplayer`, with packageSourceMapping.
- The onboarding has a warning channel already: `notes.Add(...)`, used for MinVer at `VersioningOnboarding.cs:237,244`,
  is rendered into the PR body by `Body(...)` (`:446`).

### Prior art: how Angular and .NET handle a half-failed release (2026-10-25)

Read from the local clones `C:\Repos\angular`, `C:\Repos\angular-cli`, `C:\Repos\aspnetcore`, and dev-infra's source
at the commit angular's workflow pins (`05f2940`).

- **Angular, CI release** (`angular/.github/workflows/release.yml`, since June 2026 → dev-infra `reusable-release.yml`
  → `publish-ci.ts`):
  - The version is decided **before** the build, in a merged `release: vX` commit (`release.yml:68-76`).
  - Order: build and pack, **then tag**, then the GitHub release, then `npm publish` per package. Tags come first.
  - **Recovery is a re-run.** An existing tag or release (HTTP 422) is accepted when it points at the expected commit
    and is an error otherwise (`publish-ci.ts:293-306, 335-353, 400-437`). Before each publish it checks npm and skips
    a version that is already there (`:485-497`). Nothing is published when tagging failed (`:124-128`).
  - **No rollback, no cleanup and no recovery runbook.** Tags are never deleted.
  - Serialisation: `concurrency: release-${{ github.ref }}`, `cancel-in-progress: false` (`release.yml:9-12`). Beyond
    that, GitHub refuses a duplicate tag and npm a duplicate version.
- **angular-cli** still releases from a caretaker's machine (`docs/process/release.md:90-98`). That tool tags, then
  publishes, and throws on any npm failure (`ng-dev/release/publish/actions.ts:740-778, 814-819`). It has no
  idempotence and never rolls tags back.
- **.NET (aspnetcore + Arcade)** has no tags in its pipeline at all:
  - The version comes from source: `eng/Versions.props:11-19, 37, 41`. Pre-release versions carry the official build
    id (`.azure/pipelines/ci.yml:55`), so two builds never share a version.
  - Builds are registered in Maestro's build asset registry and promoted to feeds by channel
    (`eng/common/post-build/publish-using-darc.ps1:49-58`). Nothing in the repo pushes to nuget.org or creates a tag.
  - Recovery: the post-build stage re-runs for an existing build id without rebuilding (`post-build.yml:14-22`,
    `setup-maestro-vars.yml:37-56`). No rollback.

**Conclusion.** Neither project treats a tag without a package as something to clean up. Our engine already behaves
like Angular's CI tool (tag first, idempotent re-run, a tag on another commit is a conflict). The .NET model — the
version in source, no tags — is a different system altogether, not a fix for ours.

## Decisions

### D1: An explicit global version wins, and stays packable

When `Version` or `PackageVersion` is a **global** property (`-p:` on the command line, or passed down from a parent
build), the target resolves nothing: no git call, no MPV001, no `IsPackable` change. The caller owns the version.
- Detection (spike S1): the task asks the engine through `IBuildEngine6.GetGlobalProperties()` and outputs
  `ExplicitVersion` / `ExplicitPackageVersionOnly`. ~~The probe trick in the target~~ fails: an assignment inside a
  target overrides a global property at execution time (S1 measured `-p:Version=9.9.9` packing the tag's 1.2.3).
  `$(MSBuildGlobalProperties)` does not exist.
- Only `PackageVersion` global: `Version` is set to `$(PackageVersion)`, so the assembly and package agree (Q1, owner 2026-10-25). Passing both keeps both; a csproj
  `AssemblyVersion` is never touched, since the target only sets `Version`.
- A `<Version>` in a csproj does **not** win. Onboarded csprojs carry `<Version>0.0.0-PLACEHOLDER</Version>`, and
  that is exactly the value the target exists to replace. A value alone cannot tell a csproj from the SDK default.
- `MintPlayerVersioningSkip` stays for "no versioning at all". Dotnet.Tools' `-p:MintPlayerVersioningSkip=true`
  workaround (97ec3e0) becomes unnecessary.

### D2: A pack that produces nothing is loud

Today a push build packs an untagged project silently (warning only), by design: the workflow packs everything and
untagged projects drop out. That stays. What changes is that an explicit version can no longer reach that path (D1).

**A project packed directly errors instead** (Q2 = A, owner 2026-10-25: "we will almost never invoke the
pack-target on a single project"). When `$(SolutionPath)` is unset (the project, or a directory holding one csproj,
was packed on its own) and the result would be the placeholder, the target raises **MPV005**: "<id> has no version
tag on this push build; pass -p:Version or tag it". A solution pack keeps warning MPV001. A project that is never
released opts out with `IsPackable=false` in its csproj, which skips the target. Trap accepted: a workflow that
loops `dotnet pack` over each csproj turns red on its unreleased projects; M1 checks the onboarded repositories'
workflows for such a loop.

### D3: Tag after the tests, accept a dangling tag, recover by re-running (owner, 2026-10-25)

The order (owner, Q6: "build - test - compute versions - rebuild - push"):

```
Build
Test                    # sharded where the repo shards (Spark): "tests should remain sharded"
Version the packages    # one action step: the engine decides and pushes the tags (and Releases, branch cuts)
Pack                    # rebuilds: the target reads the new tags, so the assemblies carry the new versions
Push packages           # --skip-duplicate (NuGet); npm publishes only versions the registry lacks
```

- **Nothing is tagged before every test has passed.** That is the whole of problem 2. A failure *after* the tags
  (pack, push, cancellation) leaves a tag whose package may be missing. That is accepted, as Angular and .NET accept
  it (Prior art).
- **Why a dangling tag is harmless** (to be confirmed by spike S0):
  - *Re-run the failed run* (same commit): the tags are already there and point at this commit (`AlreadyPresent`),
    so the engine plans nothing new, Pack builds at the tagged versions, and the push publishes what is missing.
    This is the documented recovery.
  - *A later run gets there first*: a package unchanged since the dangling tag is packed at that version (the
    highest reachable tag) and published by the later run, from identical source. A package that changed again
    moves on to the next version, and the dangling version is never published: a gap, which no registry or consumer
    cares about.
  - A tag on *another* commit at the same version is `Conflict` and fails the run, so a version never names two
    contents.
- **No cleanup of any kind.** No post-step rollback, no protected tail (`if: always()`), no bot-dispatched cleanup
  workflow, no plan/publish/finalize record. Deleting a tag after its package may have shipped would reuse a version
  number, which is the one outcome that is actually harmful.
- **The rebuild stays.** Dropping `--no-build` was deliberate (owner: "I think --no-build was removed
  intentionally"). The packed assemblies are a rebuild of the tested commit, not the tested binaries; the owner
  accepts that (Q6: "I'll just go for the rebuild").
- **The onboarding writes a comment above the pack step explaining the second build** (owner, 2026-10-25): the
  versions are only known after the tests, the assemblies must carry them, so pack builds again and `--no-build` is
  deliberately absent. Without it, the next maintainer "optimises" `--no-build` back in, and every package ships with
  the previous version in its assemblies.
- **Concurrency: cancel superseded runs** (owner, Q7: "cancel-in-progress on 3 subsequently merged pull-requests would
  be great"). With dangling tags harmless, cancelling a run between its tag push and its package push costs at most a
  version gap, so `cancel-in-progress: true` is safe. Spark keeps its group (`dotnet-build-master.yml:12-14`). The
  onboarding adds `concurrency: { group: ${{ github.workflow }}-${{ github.ref }}, cancel-in-progress: true }` to the
  publish workflow when it has no `concurrency` key, and leaves an existing one alone. (Angular uses
  `cancel-in-progress: false`; it releases from a dedicated `release:` commit, where cancelling would only lose work.)
- **Spark: `release` needs `tests`** (owner, Q4 = A). The job runs the order above linearly, so no tag crosses a job
  boundary. Cost: master wall time from about 10.7 to about 16.5 min (run 40140487503). Pull-request runs are
  unaffected.
- **Consumers:** the onboarding emits this order. The already-onboarded repositories (Dotnet.Tools,
  AspNetCore.Tools, SpaServices) are out of scope here: the owner fixes them by hand or re-runs the onboarding.
- **The consumer guide** (`docs/code-coverage/versioning.md`) gains a troubleshooting row: "A run failed or was
  cancelled after Version: re-run it. A tag without a package is harmless."

### D4: The package stays on GitHub Packages; the onboarding warns about GitHub restore sources

- **MintPlayer.Versioning.MSBuild keeps being pushed to both nuget.org and GitHub Packages**, like every other
  package (owner, 2026-10-25: "The package still needs to be pushed to nuget.pkg.github.com"). Dropping the GitHub
  copy was proposed (a feed without the package answers 404, which NuGet skips) and rejected. Q5 (delete the old
  copies) is therefore moot.
- Spark's publish job is unchanged (Q11 = A, owner 2026-10-25): Spark imports the MSBuild targets from source and
  never restores the package, so it cannot hit the 403.
- **The onboarding PR fixes the consumer through `nuget.config` alone; setup-dotnet keeps `source-url`** (owner,
  2026-10-25, Q9 and Q10). The 403 happens only because NuGet *asks* GitHub Packages for this package. Package source
  mapping stops it asking, so `source-url`, its token and the push steps can stay as they are.
  - **How `source-url` works** (`actions/setup-dotnet` `src/authutil.ts`, fetched 2026-10-25): it reads the
    repository's root `nuget.config`. If that file already lists the feed URL, it reuses that source's **key**;
    otherwise it adds a source with key `Source`. It writes `packageSourceCredentials` for those keys
    (`Username` = owner, `ClearTextPassword` = `NUGET_AUTH_TOKEN`) and `defaultPushSource` = the feed into a
    **separate** `nuget.config` in the workspace's parent directory (`authutil.ts:19-23`). The repository's file is
    never modified, and the token never lands in the repository.
  - **So the onboarding adds or updates the root `nuget.config`** (owner said "Directory.Packages.props";
    packageSourceMapping lives in `nuget.config`, Directory.Packages.props holds Central Package Management versions
    only):
    1. The GitHub feed is listed as a source, with **exactly the URL `source-url` uses**, so setup-dotnet reuses its
       key and the credentials attach to it. (An unlisted feed gets key `Source`, which no mapping names, so NuGet
       would never use it: GitHub-only packages would then fail to restore.)
    2. `<packageSourceMapping>`: `MintPlayer.Versioning.MSBuild` (exact id) under nuget.org. An exact id beats a
       prefix (`MintPlayer.*`) and `*`, so GitHub Packages is never asked for it. A repository with no mapping yet
       gets one that keeps today's resolution for everything else: every source keeps `*`.
  - **Nothing else changes:** setup-dotnet keeps `source-url` and `NUGET_AUTH_TOKEN`, and a push without `--source`
    still goes to the feed through `defaultPushSource`. No workflow YAML is touched for D4.
  - "If possible": a `nuget.config` it cannot parse, or a feed configured some other way (a script, a composite
    action), gets a `notes` entry with file:line and the manual fix instead, as for MinVer
    (`VersioningOnboarding.cs:237,244`).
  - Spike S3 confirms the two NuGet behaviours this relies on: the credentials from the parent-directory file attach
    to a source declared in the repository's file even under `<clear />`, and the exact-id mapping keeps restore from
    querying GitHub Packages for the package (no NU1301 without Actions access).
  - The existing GitHub copies of the package stay (Q5 moot).

### D5: The commit graph shows the release run of a tagged commit, with a re-run button (owner, 2026-10-25)

D3 makes "re-run it" the recovery. D5 makes that one click: the commit graph on the repository page (#504) shows,
on a commit that carries version tags, the status of the workflow run that created those tags, and offers to re-run
its failed jobs.

- **The tag records its run.** `TagAnnotation` (`CodeCoverage.Versioning/Versions/VersionTag.cs:53-89`) is already a
  list of `key: value` lines (`api-hash:`, `engine:`), and `Parse` skips unknown lines. A third line is added:
  `run: <GITHUB_SERVER_URL>/<GITHUB_REPOSITORY>/actions/runs/<GITHUB_RUN_ID>`. Local mode reads the runner's
  environment. Server mode: the action sends the run id with its request, and the app writes the same line. A tag
  without the line (a seed tag, a hand-made tag, every tag from before this change) shows no status.
- **The run, not the job that ran the action** (Q8, owner 2026-10-25).
  - In Spark the action runs in `release`, but the packages are pushed by the later `publish` job
    (`dotnet-build-master.yml:266-298`). A failed push leaves `release` green; only the run is red.
  - A re-run keeps the run id and adds an attempt; the run's status follows the latest attempt. The tag is not
    rewritten on a re-run (`AlreadyPresent`), so its link stays valid and the mark turns green.
  - The alternative, finding runs by the commit's SHA, returns every workflow on the commit (tests, CodeQL, …) and
    cannot tell which one tagged when a repository has two publish workflows.
- **Re-run failed jobs**, through `POST /actions/runs/{id}/rerun-failed-jobs`, which re-runs failed and cancelled
  jobs. This is a new forge method beside the merge queue's `RerunCheckAsync`, which re-runs one job
  (`CodeCoverage.GithubIntegration/Services/GitHubForgeMergeQueue.cs:426-439`, `POST /actions/jobs/{id}/rerun`). It
  uses the same permission need, `RerunActions` = `actions: write` (`GitHubInstallationPermissions.cs:241`), and
  the account-permissions notice already handles an installation that has not accepted it.
- **Who sees what.** Everyone who can read the repository sees the status. The button is for users with write access,
  checked on the server. Private repositories keep the existing rule: anonymous 401, signed-in without access 404.
- **The cancel trap.** With `cancel-in-progress: true` (D3), re-running an older commit's run while a newer run of the
  same workflow is in progress cancels the *newer* one. The button is disabled, with the reason, while a newer run of
  that workflow is queued or in progress (owner left the choice to me, 2026-10-25; rejected: allowing it and
  cancelling the newer run, which trades a safe release for an older one). A re-run after the newer run finished is safe: unchanged packages are
  skipped as duplicates, changed ones publish their older, dangling version.
- **Status is read lazily**, for the rows on screen only, and cached briefly while the run is unfinished (a
  finished attempt does not change until someone re-runs it). The graph rows already carry their refs, tags included
  (`ClientApp/src/app/services/browse.service.ts:228-234`), and a per-node coverage overlay (`:236-247`); the release
  status is a second overlay of the same kind.
- **Not shown:** whether each tagged version is actually on the registry. That needs a registry lookup per tag, and a
  red run already says "re-run it".
- **Scope:** only commits with version tags. A run that failed before its Version step tagged nothing and released
  nothing, so there is nothing to recover; a commit's ordinary checks are a different feature.

## Requirements

- R1: `dotnet pack -p:Version=X` packs at X, with or without tags, on push and pull-request builds, single- and
  multi-targeted.
- R2: `-p:PackageVersion=X` alone packs at X, and the assembly version follows.
- R3: Without an explicit version, behaviour is byte-for-byte as in 1.1.0 (D9's tag, untagged and placeholder cases).
- R4: An onboarded workflow and Spark's own workflow push nothing (tags, Releases, branches, packages) before every
  test has passed. The tags are pushed before the packages.
- R5: Re-running a run that failed or was cancelled after its Version step publishes the missing packages at the
  tagged versions and creates no new version (S0).
- R6: Spark keeps pushing every package, MintPlayer.Versioning.MSBuild included, to nuget.org and GitHub Packages;
  its publish job is unchanged.
- R7: An onboarding PR into a repository with a GitHub Packages restore source restores green: the root
  `nuget.config` lists the feed under the URL `source-url` uses and maps MintPlayer.Versioning.MSBuild to nuget.org;
  the workflows are unchanged. A shape it cannot rewrite gets a warning naming the file and line.
- R8: A test evaluates the real `.targets` with a real `dotnet pack` (the missing coverage that let this and the
  multi-targeting incident ship).
- R9: A version tag written by the engine, in local and server mode, names the workflow run that created it; a tag
  without that line still parses.
- R10: The commit graph shows the release run's status on every commit whose version tags name a run, and a user with
  write access can re-run its failed jobs, except while a newer run of the same workflow is unfinished.

## Out of scope

- Cleaning up dangling tags, in any form (D3). This is a decision, not a deferral.
- Fixing the already-onboarded repositories' workflows (owner: by hand or by re-onboarding).

## Questions for the owner

- ~~Q1 (D1): when only `-p:PackageVersion` is passed, does the assembly `Version` follow it?~~ **Answered
  2026-10-25 (A):** yes. An explicit `-p:Version` alongside it still wins; a csproj `AssemblyVersion` is untouched.
- ~~Q2 (D2): should a single-project pack that produces nothing be an error?~~ **Answered 2026-10-25 (A):** yes,
  MPV005 when packed directly; a solution pack keeps the warning.
- ~~Q5 (D4): delete the existing GitHub Packages copies?~~ **Moot 2026-10-25:** the package stays on GitHub
  Packages (D4).
- ~~Q9 (D4): warn, rewrite, or refuse?~~ **Answered 2026-10-25:** the onboarding PR fixes it, with a warning as the
  fallback.
- ~~Q10 (D4): how does restore get the token once `source-url` is gone?~~ **Answered 2026-10-25:** it isn't gone.
  `source-url` stays; the `nuget.config` mapping alone prevents the 403 (D4).
- ~~Q11 (D4): change Spark's publish job?~~ **Answered 2026-10-25 (A):** no, unchanged.
- ~~Q8 (D5): the run's status, or the job that ran the action?~~ **Answered 2026-10-25:** the run (owner accepted the
  proposal: "yes, add it as D5").
- Answered: Q3 (one action step), Q4 (Spark `release` needs `tests`), Q6 (build, test, version, rebuild, push), Q7
  (cancel superseded runs; dangling tags accepted, D3).

## Risks

- **A partly pushed set whose dependency is missing.** Package Q at vX depends on P ≥ vX; the push published Q, then
  failed or was cancelled before P; a later commit changed P again, so P vX never ships. NuGet restores Q with the
  nearest higher P and warning NU1603 (an error under warnings-as-errors). The recovery is to re-run the failed run
  *before* the next one, which publishes P vX. Rare (a cancel must land inside the ~1 min push, and P must change
  again); accepted, and named in the consumer guide.
- `IBuildEngine6` under RoslynCodeTaskFactory on older SDKs (the package targets net8+ consumers). S1 checks the
  oldest SDK an onboarded repo uses.
- The second build doubles compile time on publish runs (Spark: Pack 42 s).
- An `actions/checkout` with shallow history: the tags need `fetch-depth: 0`, already required by MPV002/003.

## Superseded (first draft and grill, 2026-10-25)

- ~~Compute versions as **local** tags, rebuild, then push tags in a later phase (`push: false` input, engine/CLI
  split, spike S2).~~ One step is enough once tagging happens after the tests (Q3) and a dangling tag needs no undo.
- ~~A post-step that deletes the tags when a later step fails.~~ Rejected: a `post:` only sees its own job, and
  deleting a tag whose package shipped reuses a version.
- ~~Protect the tag-to-package tail from cancellation with `if: always() && …`.~~ Unnecessary: a cancelled tail is
  re-run or superseded (D3).
- ~~The bot dispatches a cleanup workflow ~15 min later to remove dangling tags.~~ Unnecessary, and new machinery: the
  app cannot delete a remote tag, and the engine reads every local tag (`TagHistory.cs:26-42`).
- ~~Plan → publish → finalize, with tags created only after a confirmed publish and a release record.~~ Rejected by the
  owner as far more inconvenient than the problem it solves; neither Angular nor .NET does it.

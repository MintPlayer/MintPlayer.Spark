# Automated package versioning — the GitHub App versions every package of a repository through git tags

**Status:** Grilled 2026-10-24 (Q1–Q14, all answered by the owner). Not started. Spikes S1–S8 are open, see
[coverage_package_versioning_plan.md](coverage_package_versioning_plan.md).
**Issue:** #153 (its description is rewritten from this PRD) · **App:** `apps/CodeCoverage` (production,
coverage.mintplayer.com) · **one pull request**, which also carries PR-title correction (D14) and is followed by
onboarding PRs in four MintPlayer repositories (D17).

## The problem, stated plainly

The owner's libraries are versioned by hand. Every packable project carries a hand-edited `<Version>` or
`package.json` `version`, and every release is a commit that edits it. Measured 2026-10-24:

| Repository | Hand-edited versions | Git tags |
|---|---|---|
| MintPlayer.Dotnet.Tools | `11.0.0-rc.1` ×28, `13.0.0` ×11 (the `netstandard2.0` source generators) | 0 |
| MintPlayer.AspNetCore.Tools | `11.0.1-rc.0` ×24, `11.4.1-rc.0` ×3, `11.2.0-rc.0` ×3 | 0 |
| MintPlayer.AspNetCore.SpaServices | `11.0.0-rc.3` ×6, `11.0.0-rc.2` ×6 | 0 |
| MintPlayer.Spark | `11.0.0-preview.103/.104/.107` (drifting), npm `22.x` | schema tags only |

This causes three problems:

- **Merge conflicts.** Every PR that releases a package touches the same version lines.
- **Releases are forgotten or wrong.** Nothing tells the author that a change needs a minor rather than a patch, or that
  a dependent must be re-released.
- **No backports.** A version commit on `master` makes cherry-picking a fix onto an older SDK line awkward, and nothing
  supports the cherry-pick itself.

#153 asks the CodeCoverage GitHub App to take this over. The consumer's effort must stay **minimal** (owner,
2026-10-24): one onboarding PR, opened by the app, and nothing to maintain afterwards.

## What the issue got wrong (corrected here)

| Issue text | Correction | Evidence |
|---|---|---|
| "We already have a repository-dependency-tree builder" | It is **repository-to-repository** within one account. `DependencyGraph.Build` links repos that produce a package to repos that consume it. `NuGetManifestParser` does not read `ProjectReference`, and nothing reads Nx `project.json` or npm `workspaces`. The in-repo **project** graph is new work (D5). | `CodeCoverage/Dependencies/DependencyGraph.cs`, `CodeCoverage.Library/Dependencies/NuGetManifestParser.cs` |
| ".NET always requires the entire dependency tree to get a new version released" | NuGet does not require it. A `ProjectReference` becomes a `>= x` floor in the nuspec, and NuGet resolves the **lowest** applicable version. A dependent re-release is what raises that floor, so consumers only get the fix transitively if the dependent is re-released. It is a **policy**, and the owner keeps it (D4). | NuGet dependency resolution, "lowest applicable version" |
| npm example 2: "requiring a version bump" | Whether LibraryB must be re-released depends on whether its range still admits the new version. `~6.2.0` rejects `6.3.0`; `^6.0.0` accepts it. The rule in D4 decides by the range. | npm semver ranges |
| "Compute PublicApiHash and add it to nuspec" | The hash travels in the **annotated tag** (D6), not the nuspec. The previous hash is then a local git read, and needs no package download. | owner, Q5 |
| "Compute PublicApiHash" | A hash of raw reference-assembly bytes or `.d.ts` output is not stable across compiler or ng-packagr versions. An SDK update alone would bump every package. It must hash a **normalized API listing** (S3). | |
| "So maybe we need to start with a cherry-picker…" | A worktree-less cherry-pick already exists (`RollingBranchReplay`), and so do the conflict resolvers. Backports are in scope (D10). | `CodeCoverage.Git/Dependencies/RollingBranchReplay.cs` |
| (missing) | `netstandard*` packages have no platform major. The 13.0.0 generators show the owner sets those by hand (D7). | Dotnet.Tools csprojs |
| (missing) | npm needs **dist-tags** per release line, or publishing `22.3.1` after `23.0.0` moves `latest` back to 22 (D11). | npm `publish --tag` |
| (missing) | GitHub creates **no `push` event when more than three tags are pushed at once**, so "trigger the build on the tag" cannot work for a multi-package release (D3). | GitHub docs, "Events that trigger workflows" |

## Prior art (research 2026-10-24)

| Tool | Bump decided by | Dependents | Tags | What we take |
|---|---|---|---|---|
| MinVer | the nearest reachable tag with a prefix (`MinVerTagPrefix`); `x.y.(z+1)-alpha.0.{height}` after it | — | per prefix | the .NET reader until 2026-10-24; replaced by our own exact-tag MSBuild package (D9) |
| Nerdbank.GitVersioning | `version.json` + git height, `pathFilters` | — | not the source of truth | nothing: it edits files |
| changesets | intent files per PR | patch bump on internal dependents; major on a peer-dep change | `pkg@x.y.z` | the peer-dependency rule (D4) |
| release-please (manifest) | conventional commits, path-filtered | `node-workspace` plugin | `pkg-vx.y.z` | path-filtered change detection (D4) |
| Nx Release | conventional commits | `updateDependents: auto` | `releaseTagPattern` | the graph walk (D5) |
| angular / ng-dev | one `target:` label per PR → computed set of branches | — | `vX.Y.Z` | **semantic labels, not branch names; dry-run every cherry-pick before pushing any** (D10) |
| aspnetcore / Arcade | `eng/Versions.props` per branch | — | `vX.Y.Z-label.N.build` | `release/X.Y` lines; a backport is a **PR**, not a direct push (D10) |

What we deliberately don't take: version files in source (all of them except MinVer), conventional-commit types as the
bump source (Q3, D8), and Angular's caretaker-only merge mode.

## What exists to build on

All verified against `master` @ `162c1fa3`. Paths are under `apps/CodeCoverage/`.

| Need | Already there |
|---|---|
| Git writes | `GitRunner` (`CodeCoverage.Git/GitRunner.cs`), a hardened git-CLI runner over bare repositories. `WriterService` (`CodeCoverage.GitWriter/Jobs/WriterService.cs`) creates commits with `commit-tree` and pushes **any** ref with `--force-with-lease`. Every caller pushes branches today, so tags are new. |
| Cherry-pick | `RollingBranchReplay` replays commits with `git merge-tree --merge-base`: a cherry-pick without a worktree. |
| Conflict resolution | `IConflictResolver` (`CodeCoverage.Git/MergeQueue/Resolution/`) with `TakeBase`, `Evict`, `KeepBoth`, `LineSet`, looked up through `ConflictResolverRegistry`. |
| Tag reading | `CodeCoverage.GitMirror/Mirror/MirrorRefs.cs` lists `refs/tags`; `IForgeDependencyUpdates` resolves a tag to a commit. Nothing creates tags or Releases. |
| Webhooks | `GitHubEventsRecipient.HandleAsync` (`CodeCoverage.GithubIntegration/Recipients/`) handles `push`, `pull_request`, `issue_comment` and `release` (the producer release that feeds the dependency updater, so our Releases will drive downstream updates for free). |
| Permissions | `GitHubInstallationPermissions`: `contents`, `pull_requests`, `checks`, `actions`, `workflows`, `issues` are already **write**. Tags, Releases, PR edits and `workflow_dispatch` need nothing new. |
| CI identity | The coverage upload already authenticates GitHub Actions runs by OIDC token (`Controllers/UploadsController.cs`). |
| Manifest parsing | `CodeCoverage.Library/Dependencies/`: `NuGetManifestParser` (XML with `DtdProcessing.Prohibit`, `XmlResolver = null`) and `NpmManifestParser` (`JsonDocument`). They read `PackageReference` and literal values only. |
| Settings | Inherit / Enabled / Disabled policy enums, e.g. `EPrSizeLabelPolicy` (`CodeCoverage.Library/LookupReferences/PrSizeLabelPolicies.cs`), with an Account default. Repo-side config in `coverage.yml` (`mergeQueue:`, `dependencies:`, see `Dependencies/Policy/DependencyPolicyConfig.cs`). |
| Onboarding PRs | `DependencyOnboardingService` + `DependencyOnboardingRowActions` list the repositories a viewer manages and open a setup PR from a row action. |
| Committed actions | `action/` and `dependency-action/` are ncc bundles with a committed `dist`, rebuilt and byte-compared in CI, behind `tools/ncc-build-guard.mjs`. |

## Decisions

Each decision gives the grill question it came from. "Owner" means the owner's answer on 2026-10-24.

### D1 — One annotated git tag per package is the only version record (Q1)

- Tag name: `<PackageId>/v<version>`, e.g. `MintPlayer.Mapper/v13.2.0`, `@mintplayer/ng-spark/v22.4.0`.
- Source holds no version. .NET projects carry `0.0.0-PLACEHOLDER` (or nothing), npm `package.json` carries
  `0.0.0-PLACEHOLDER`.
- The bot never commits a version change, to any branch.

**Why:** "The version number should not be present in the code, because it will cause merge-conflicts all the time"; a
version commit on `master` "prevents backporting features to older SDK versions"; tags "can also live in the
git-repository locally, which allows `dotnet build` targets etc to set the library versions during the build phase,
even with no internet connection" (owner). A repository holds several libraries that are **not** versioned in lockstep,
hence one tag per package.

**Trap accepted:** a build outside a tagged commit (local `dotnet pack`, forks) gets the last tagged version
(`MintPlayer.Versioning.MSBuild`, D9), or `0.0.0-PLACEHOLDER` and is not packable when nothing was tagged yet.

### D2 — One engine, two hosts, chosen by `versioning.mode` (Q14)

The versioning logic is a pure library, `CodeCoverage.Versioning`, with no RavenDB and no server dependency. It owns
the graph, change detection, the version rules, annotated-tag writing and Release creation. Two hosts run it:

1. **server** (default): the action posts to coverage.mintplayer.com, the server computes and pushes the tags as the
   App, and records the run in RavenDB.
2. **local**: the action runs the engine in the workflow and pushes with the workflow's `GITHUB_TOKEN`. The engine
   is built from source on the runner: `CodeCoverage.Versioning(.Cli)` target `net8.0` with `RollForward=Major` and no
   package references, and the action runs `dotnet run` (project mode) from `$RUNNER_TEMP` so this repository's
   `global.json` doesn't apply (S4: 18.7 s cold, 2.3 s warm on SDK 10.0.113). No binaries are committed, so nothing
   can go stale.

```yaml
# coverage.yml
versioning:
  mode: local          # server | local, default server
```

- **Automatic fallback.** In `server` mode, when the server is unreachable or returns 5xx, the action runs the local
  engine instead (owner: "keep the automatic fallback"). This is safe because the engine is **idempotent**: tags already
  on HEAD are fixed versions, so a run the server half-finished is completed, not duplicated. The onboarding PR grants
  the workflow `permissions: contents: write` for this.
- **MintPlayer.Spark uses `mode: local` with the in-repo action** `./apps/CodeCoverage/versioning-action` (owner). Its
  publish job is what deploys CodeCoverage, so it must never depend on the deployed app. A broken engine on `master` is
  fixed by the next commit, with no deploy in between.
- **The mode does not affect the webhook features.** Auto-cut branches (D12), backports (D10), title correction (D14)
  and trigger B (D3) run on the server from GitHub events in either mode. When the server is down they pause; publishing
  doesn't.
- **Conformance:** one fixture repository runs through both hosts and must produce identical tags.

**Why not server-only:** "keep in mind that this app may have some downtime" (owner). **Why not local-only:** the server
adds tags and Releases authored by the App, per-repository serialization, and history on the dashboard.

### D3 — Two triggers: the action step (default) and bot dispatch (Q2)

- **A, the action step (default).** The consumer's publish workflow calls `MintPlayer/versioning-action@v1` once,
  ~~**after build and before pack**~~ **after the tests and before pack** (*superseded 2026-10-25 by #544 D3*: a
  tag made before failing tests left a version that never shipped, so the onboarding now puts the step after the
  last test step and adds `concurrency: cancel-in-progress`; Spark's `release` job `needs: tests`). *Proposed by
  #552 ([versioning_plan_release_split_PRD.md](versioning_plan_release_split_PRD.md)): `phase: plan` after the build
  gives the versions without tagging, and `phase: release` after the tests writes exactly those tags; the single
  step stays the default.* The API hash needs the built assemblies and `.d.ts` files (D6), and `dotnet pack`
  (without `--no-build`) rebuilds with `MintPlayer.Versioning.MSBuild` reading the new tags (S1, D9). For npm the action writes the versions into
  `package.json` before the library build that copies them to `dist` (D9). The step computes and tags (D2), runs
  `git fetch --tags` and deepens a shallow clone itself, and exposes the versions as outputs. The consumer controls *when* a release happens: gate the step with `if:`, make the
  workflow manual, or push tags by hand.
- **B, bot dispatch.** `versioning: { dispatch: publish.yml }`. On a merge to the default branch or a release branch,
  the server checks whether any package's inputs changed (D4). If one did, it fires `workflow_dispatch` on that workflow,
  and the action step inside that run versions as in A. **No package changed, no dispatch.**
- **Not done: triggering on tag pushes.** GitHub creates no event when more than three tags are pushed at once, and one
  run per tag would build the same commit N times in parallel (owner: "C indeed loses").
- **Manual tags are not debounced.** The server ignores tag webhooks. A tag a human pushed is read when the release
  workflow runs: every tag on HEAD is a fixed version, and dependents still follow it (D4).

**Trap accepted:** a manual tag on a commit no workflow runs on is never published.

### D4 — Release only what changed; dependents follow by ecosystem rule (Q2)

A package gets a new tag in a run only when one of these is true:

1. **Its inputs changed** since its last tag on this line. Inputs are the project directory, plus the shared build
   files that reach it (`Directory.Build.props`/`.targets`, `Directory.Packages.props`, `global.json`, `nuget.config`,
   the lockfile), minus `versioning.ignore` globs (default `**/*.md`, test projects). It is compared by tree sha in the
   bare repository, with no checkout.
2. **An in-repo dependency is released in the same run.**
   - **.NET:** the dependent is always released. This raises its `>=` floor, so consumers get the fix transitively
     (owner: "Yes, that's exactly what I meant").
   - **npm:** the dependent is released only when its declared range no longer admits the new version (`~6.2.0` vs
     `6.3.0`, or an exact pin), or a **peer** dependency's major moves. Otherwise `npm update` already picks the new
     version up (the issue's examples 1 and 3).
3. **A lockstep group member is released** (D13).
4. **A human tag on HEAD names it** (D3).

**Trap:** inputs outside the project directory (`<Compile Include="..\Shared\*.cs">`, Nx `inputs`) would be missed,
and a missed input is a change that never ships. The static parser follows linked items it can see; S1 measures what it
can't, and the action's packed-package list (D5) is the safety net for whole packages, not for files.

### D5 — The server parses the graph statically; the action supplies only what needs a build (Q5)

**The server never executes consumer code.** Running `dotnet msbuild` or `nx graph` on a consumer's repository runs
their code in our container. So:

- **The server** reads, at the commit, statically:
  - `.csproj`: `PackageId`, `IsPackable`, `TargetFramework(s)`, `ProjectReference`, `PackageReference`, linked
    `Compile`/`None` items, and simple `$(Prop)` values resolved from the `Directory.Build.props` chain.
  - `package.json`: `name`, `private`, `version`, `dependencies`/`peerDependencies`/`devDependencies`, `workspaces`.
  - Nx `project.json` and `ng-package.json`.
  - Lockfiles (`project.assets.json` is not committed, so .NET resolution comes from the action, see below;
    `package-lock.json` is).
- **The action** uploads a **release manifest** from the build it just ran:
  - per packed package: id, kind, the API hash (D6), the evaluated `TargetFrameworks`
    (`dotnet msbuild -getProperty`), and the resolved prerelease-relevant versions (D8);
  - the list of packages the run actually packed.
- **Cross-check:** a packed package missing from the server's graph fails the run loudly. TFMs the server could not
  resolve statically are taken from the manifest, validated against the TFM grammar.
- **Auth:** the manifest is accepted only with a GitHub OIDC token whose `repository`, `ref` and `workflow` claims
  match, the same way coverage uploads are (`UploadsController`). Tags are only placed on commits reachable from the
  default branch or a release branch (D10).

**Untrusted input, sanitized:**

| Input | Rule |
|---|---|
| XML | `DtdProcessing.Prohibit`, `XmlResolver = null` (already so), file-size cap |
| JSON | `JsonDocument` depth 64 (default), file-size cap |
| Paths (`ProjectReference`, `file:`, `link:`, linked items) | normalize, reject anything outside the repository, never follow symlink entries (mode `120000`) |
| Globs (`workspaces`, Nx) | bounded matcher (`PathGlob`), never a regex built from the glob |
| Package ids → tag names | NuGet id / npm name grammar, then `git check-ref-format` |
| Versions | `^\d+\.\d+\.\d+(-(alpha\|beta\|next\|preview\|rc)\.\d+)?$` |
| API hashes | `^sha256:[0-9a-f]{64}$` |
| Counts | caps on projects, edges, packages per run |
| Shell | none: every process gets an argument array (`execFile`, `GitRunner`), never a shell string |
| RQL | parameters only, per the repository rule |

### D6 — The API hash is stored in the annotated tag (Q5)

- The action computes the hash of a **normalized API listing** after the build (S3): a PublicAPI.txt-style listing for
  .NET, a sorted declaration listing for TypeScript.
- The tag is **annotated**; its message carries `api-hash: sha256:…` and the engine version. The server also keeps a
  copy in RavenDB (`server` mode).
- **The previous hash** comes from the last tag's annotation (a local git read, offline). If that tag carries none, as
  with a human's lightweight tag or a seed tag, the action hashes the previously published package from the registry.
  If that fails too, the package counts as changed and gets a minor. Over-bumping a minor is harmless under D8; missing
  one isn't.

### D7 — Where the major comes from (Q6, Q12)

| Package | Anchor | Major |
|---|---|---|
| .NET, `net5.0+` (incl. `net8.0-windows`) | `TargetFramework(s)` | the highest `netX` |
| .NET, `netcoreapp3.1` | same | 3 |
| `netstandard*`, `net4x` | none | **free** |
| Angular library | resolved `@angular/core` in the lockfile; the peer range must include it | its major |
| React / Vue library | resolved `react` / `vue`, same rule | its major |
| Plain node library | none | **free** |

- **Free** packages keep the major of their last tag; the owner raises it by pushing a tag (owner: "for .NET Standard,
  here the user decides the major version"). Free packages are tagged only from the default branch, so two branches
  never tag the same free line.
- **Override:** `versioning.packages.<id>.anchor: <package>|free` for exceptions.
- **A major never goes down.** A computed major below the last tag's, or a version at or below an existing tag or
  published version, fails the run with a check-run error. (Anchoring the 13.0.0 generators to .NET would compute 11
  and is refused.)
- **New major:** anchor major M > last tag's major K → `M.0.0`, or `M.0.0-<label>.N` while upstream is in preview.

### D8 — Minor vs patch by API hash; previews mirror upstream (Q3, Q8)

- API hash changed since the last tag → **minor**; otherwise → **patch**. A breaking change is also a minor: the major
  belongs to the platform (CLAUDE.md "Versioning"). PR titles play no part (owner: "I don't think consumers will ever
  need B").
- **Prerelease** when the anchor or any runtime dependency resolves to a prerelease. "Resolved" means the lockfile or
  the action's evaluated `project.assets.json`, not the declared range. `PrivateAssets="all"` references and npm
  `devDependencies` don't count.
- **The label is mirrored from upstream** (`preview`, `rc`, `next`), with the least mature label winning; the
  allow-list `alpha < beta < next < preview < rc` sorts correctly. The number is our own counter.
  - No stable of the major yet: `<major>.0.0-<label>.N`.
  - A stable line exists (`11.2.0`): `11.3.0-<label>.N`.
  - Everything stable again: the next release is stable.
- **Amended 2026-10-24 after the first live run (40124565898).** Mirroring alone was wrong for Spark in three ways:
  - Webhooks.GitHub resolves Octokit.GraphQL `0.4.0-beta`, so it computed `11.0.0-beta.1` and was refused
    ("11.0.0-beta.1 is not above 11.0.0-preview.105"). One refusal refuses the run, so nothing was tagged.
  - Packages with only stable dependencies computed a stable `11.0.0`, though Spark itself is in preview. Others got
    `rc` from MintPlayer.AspNetCore.Endpoints `11.4.1-rc.0`.
  - The platform's own maturity was never an input: a `net11.0` package anchored on .NET 11 ignored that the SDK is
    `11.0.100-rc.2`.

  Two additions (owner: "spark is still in preview. Additionally, it targets .NET 11 which is also in preview"):
  - **A pin.** `versioning.prerelease: <alpha|beta|next|preview|rc|upstream>` and per package
    `versioning.packages.<id>.prerelease`. A pin sets the label outright; upstream never overrides it. A package pin
    wins over the repository pin, and `upstream` restores the mirroring above. Removing the pin is how a project goes
    stable. Spark pins `preview`, and exempts ng-spark (a stable 22.x Angular package) and
    MintPlayer.Versioning.MSBuild (the onboarding PRs reference `1.0.0`).
  - **The SDK as an upstream.** For a NuGet package anchored on its TFM (no `anchor` override) whose major equals the
    SDK's major, the build's `dotnet --version` joins the mirrored versions. It travels in the manifest as `sdk`.
    SDK 12 against a `net11.0` package, or a stable SDK, adds nothing.
  - Evidence: `ReleaseLabelTests` (Versioning.Tests) red→green for each case above, and
    `VersioningSettingsTests.Prerelease_pins_survive_the_wire_from_the_app` (server mode).
  - The same run showed the generator packages as "not on the public registry": they ship their assembly under
    `analyzers/dotnet/cs/`, not `lib/<tfm>/`. The registry hash now falls back to that entry for a single-TFM package
    (`ReleaseLabelTests`, the analyzer-nupkg case).

### D9 — How the version reaches the artifacts (Q4)

- **.NET: our own `MintPlayer.Versioning.MSBuild`** (owner decision 2026-10-24, M13; *supersedes MinVer*). The
  onboarding PR adds the package to `Directory.Build.props`. It ships a `build/` target (not `buildTransitive`, so only
  the projects that reference it are versioned) that sets `Version`/`PackageVersion` to the **highest reachable
  `<PackageId>/v*` tag, exactly**: no height suffix. Why not MinVer: MinVer gives an untagged-since commit
  `x.y.(z+1)-alpha.0.{height}`, so every package the run did *not* release would pack as a fresh prerelease and be
  pushed. With the exact tag, an unchanged package re-packs at the version the feed already has and
  `dotnet nuget push --skip-duplicate` skips it, so the consumer's workflow can pack and push *everything*.
  - The prefix is resolved **inside the target** (S1: as a plain property it read an empty `PackageId` for projects
    relying on the default and gave `/v`). `MintPlayer.Mapper/v` never matches `MintPlayer.Mapper.Abstractions/v…`.
  - ~~A project with no tag gets `0.0.0-PLACEHOLDER`, a warning and `IsPackable=false`, so a placeholder can never
    reach a feed.~~ *Superseded 2026-10-25 by the next bullet; it still holds for a build that may publish.*
  - **A project with no tag packs at `0.0.0-untagged` in a build that cannot publish** (owner decision 2026-10-25):
    locally (`GITHUB_ACTIONS` unset) and in a GitHub Actions run of `pull_request`, `pull_request_target` or
    `merge_group`. Any other CI run (push, schedule, dispatch, release) keeps `0.0.0-PLACEHOLDER` and
    `IsPackable=false`, and `MPV004` stays the backstop. Both cases warn `MPV001`. `untagged` is outside the tag
    grammar, so the engine never reads the version as a release. The owner rejected pushing the seed tags when the
    onboarding PR opens ("not sure why we would create a tag for a pull-request that's not even merged yet … the code
    should just notice that no tag exists, and choose the correct defaults"). Evidence: Dotnet.Tools' onboarding PR
    #202 had no tag anywhere, so nothing packed. Its pack-and-consume test failed with NU1101 (run 40137704671), and
    after a per-repo workaround its `Assert-PackageVersions.ps1` failed with "No .nupkg found" (run 40138296322).
    A package added to an onboarded repository is untagged until its first release and hit the same failure. Tests:
    `TagVersionTests.Packs_an_untagged_package_only_where_nothing_is_published`, `The_untagged_version_is_never_read_as_a_tag`,
    and the git-backed task test now runs the push and the pull-request case.
  - **An explicit global `Version` or `PackageVersion` wins** (#544 D1, 2026-10-25). Before this, the target overrode
    `-p:Version=…` with the tag, because an assignment inside a target beats a global property at execution time
    (#544 S1: `-p:Version=9.9.9` packed the tag's 1.2.3). The task now reads the engine's global properties and
    resolves nothing when one is set. `PackageVersion` alone also sets `Version`. A csproj `<Version>` still does not
    win. Evidence: `RealPackTests` (real `dotnet pack`, 13 cases). See `docs/versioning_msbuild_traps_PRD.md`.
  - **A project packed on its own that would get the placeholder errors `MPV005`** (#544 D2) instead of warning;
    a solution pack keeps the MPV001 warning.
  - Knobs: `MintPlayerVersioningSkip`, `MintPlayerVersioningTagPrefix`, `MintPlayerVersioningTaskSource` (Spark imports
    the targets from source, M14).
  - The task is an inline `RoslynCodeTaskFactory` class (`build/MintPlayer.Versioning.MSBuild.cs`), so the package
    carries no assembly. Its version grammar and ordering mirror the engine's `PackageVersion`.
  - The package is free (not platform-locked), so it starts at 1.0.0.
  - Evidence: `CodeCoverage.Versioning.Tests/MSBuild/TagVersionTests`. It covers parity with the engine's grammar and
    ordering, the highest tag of its own package only, and refusal of a non-package-id prefix. It also runs the task
    against a real git repository and reads the highest tag reachable from HEAD.
- **npm publishing** goes through `MintPlayer/github-actions/publish-npm-packages` (owner, 2026-10-24), which
  publishes only the versions the registry does not have yet: the npm counterpart of `--skip-duplicate`.
  *(2026-10-25, mintplayer-ng-video-player #101: `JS-DevTools/npm-publish` is recognised as a publish step too. Its
  default `strategy: all` skips a version the registry has, and it reads its dist-tag from `publishConfig.tag`. The
  onboarding had not seen its 40 steps and left the workflow alone. The same onboarding read the repository's 1.6 MB
  `package-lock.json` as empty: GitHub's contents API returns no content over 1 MB, so such a file is now fetched with
  the raw media type.)*
- **npm/Nx:** the action writes the version into the working-tree `package.json` of every publishable package by
  editing the JSON itself (validated version, formatting and trailing newline kept), and rewrites internal
  `dependencies` / `peerDependencies` ranges (`workspace:*`, `file:`, plain ranges) to `^<released>`. Not
  `npm version`: inside a workspace it also rewrites the root lockfile, and `npm pkg set` accepts any string (S2).
  When a built `<ng-package dest>/package.json` already exists, it is written too. Nothing is committed.
  *(2026-10-25, mintplayer-ng-bootstrap #446: so is `<outputPath>/package.json` of a library's Nx `build` target, which
  `@nx/js:tsc` and `@nx/vite:build` copy there. ng-bootstrap's seven non-ng-packagr libraries would otherwise have
  published their dist copy as `0.0.0-PLACEHOLDER`.)*
  Publishable = not `"private": true`; for Angular, the library has an `ng-package.json`.
- No dependency on Nx Release (owner: "my bot can make it easier, without depending on them").

### D10 — Release branches and backports (Q7)

- Branches matching `versioning.releaseBranches` (default `release/*.x`) are release lines, versioned exactly like the
  default branch, within the major that the branch's own content computes. If `release/8.x` computes major 9, the run
  fails.
- **Single-target and multi-target both work** (owner): `master` on `net10.0` and `release/8.x` on `net8.0` give `10.2.3`
  and `8.1.2` side by side; `master` on `net10.0;net11.0` gives `11.x`, which also serves .NET 10 users.
- **Backport:** a `backport: 8.x` label or a `/backport 8.x` comment on a merged PR makes the bot:
  1. dry-run the cherry-pick of the merge commit onto every requested branch with `RollingBranchReplay` and the
     `IConflictResolver` registry;
  2. if **every** target applies, push `backport/<pr>-<line>` branches and open one PR per line, with
     `(cherry picked from commit …)` trailers;
  3. if any target conflicts, push nothing and comment the failing files and the exact commands for a manual backport.

  The backport is a PR, never a direct push, because the CI run on it is the compile check against the old TFM.
- A package that doesn't exist on the branch isn't versioned there.

### D11 — npm dist-tags per line (Q7)

The action outputs, per npm package, the dist-tag to publish under: `latest` only for the highest stable major,
`v<major>-lts` for older majors, `next` for prereleases. Without it, publishing `22.3.1` after `23.0.0` points `latest`
at 22. NuGet needs nothing (highest version wins).

### D12 — The bot cuts release branches when a platform major moves (Q12)

On a push to the default branch, when any package computes `M(merge commit) > M(first parent)`, the server creates
`release/<M(first parent)>.x` at the first parent if it doesn't exist, and comments on the PR. Free packages never cut a
branch. The first package to move cuts the branch for the whole platform line. Policy: Inherit / Enabled / Disabled,
Account default **Enabled**.

**Trap accepted:** branches pile up for lines nobody patches, and a bot-created branch is unprotected unless a ruleset
matches `release/*`. Rulesets aren't files, so the guide tells users to add one.

### D13 — Independent by default, lockstep groups in coverage.yml (Q9)

```yaml
versioning:
  groups:
    - name: mapper
      packages: [MintPlayer.Mapper, MintPlayer.Mapper.*]
```

Members of a group release together at the group's highest computed version whenever one of them releases; tags stay
per package. `packages: ["*"]` is a whole-repo lockstep. A group whose members compute different majors is rejected.

### D14 — PR-title correction, milestone 1 of the same PR (Q10)

Prior art: one of the owner's private apps.

- **Format:** `<type>: <title> (#issue/#issue)`, listing only linked issues of the same repository. A squash merge then
  appends `(#PR)`: `feat: Implemented some feature (#123) (#124)`.
- **Type**, the first that gives one:
  1. the ticked box in the PR template;
  2. the type already in the title;
  3. a `bug` label on a linked issue → `fix`;
  4. the issue number in the branch name (`issues/#123`), whose labels are then checked as in step 3;
  5. `feat`.

  Bot PRs (`chore(deps)`) keep their own type.
- **When:** `opened`, `edited`, `reopened` on unmerged PRs. A check run reports the result. The bot ignores `edited`
  events caused by its own edit.
- **Setting:** Inherit / Enabled / Disabled, Account default **off**.
- The titles become the release notes (D15); they never decide a version (D8).

### D15 — One GitHub Release per tag, behind a policy (Q11)

- Title `<PackageId> <version>`. Notes: the corrected titles of the PRs that touched the package since its previous
  tag, grouped `feat` / `fix` / other. A dependent-only release says "Dependency update: <id> <version>". Prereleases
  are marked as such.
- **Policy** release-on-tag: Inherit / Enabled / Disabled, Account default **Enabled** (owner).
- Created sequentially with backoff (secondary rate limits). The guide warns that a consumer workflow `on: release`
  fires once per Release.

### D16 — GitHub only, behind `IForgeVersioning` (Q13)

A new `IForgeVersioning` interface with an `EForgeCapability.Versioning` flag covers tags, Releases, backport PRs, title
edits and dispatch, the same shape as `IForgeMergeQueue`. Only GitHub implements it; the UI hides the versioning
settings for forges without the capability. The owner wants GitLab and Bitbucket later. What they need is recorded on
#422.

### D17 — Proven on a test repository, then onboarded onto four repositories (Q14)

- **Development:** CodeCoverage runs locally with the CodeCoverageDevelopment App, whose webhooks for the
  PieterjanDeClippel repositories are forwarded to the development machine. A test repository with two .NET packages
  (one single-target, one multi-target), an Angular library and a `netstandard2.0` generator exercises every flow:
  tagging, skipping unchanged packages, the dependent cascade, a preview, an auto-cut branch, a backport with a
  conflict, Releases, title correction, and the fallback (server stopped mid-run).
- **Onboarding** (M13) is the repository page's detail action **Set up versioning** (`SetUpVersioning`), not a row
  action: it runs synchronously, so the person sees the outcome (PR opened, seeded, nothing to do, refused). It needs
  the same managed-repository check as dependency onboarding (`IDependencyOnboardingService.CheckAsync`). It plans from the
  default branch's tree (`VersioningOnboarding.PlanAsync`). Packages that already have a `<PackageId>/v*` tag are left
  alone. The writer (`/jobs/versioning/onboarding`) builds the branch `mintplayer/versioning-onboarding`, and that PR:
  - adds `MintPlayer.Versioning.MSBuild` to `Directory.Build.props` (.NET, D9);
  - replaces hand-edited versions with `0.0.0-PLACEHOLDER`;
  - adds the action step and `permissions: contents: write` to the publish workflow;
  - adds a `versioning:` section to `coverage.yml`.

  The commit's identity and date are kept in `VersioningOnboardingStates/{provider}/{id}`, and the date resets only when
  the base sha changes, so a retry rebuilds the same commit. When there is nothing to change, the seeds are pushed at
  once.

  When the PR merges, the server pushes a **seed tag** per package from the version it replaced, so nobody restarts at
  0.0.0. A free package seeds from its own version (13.0.0). The seeds go on the merge commit, read from the forge
  rather than the webhook, through `/jobs/tags`. Two paths can push them, and whichever comes first does it:
  - the merged-PR webhook (`VersioningSeedsMessage`);
  - the first versioning run (`VersioningController.Run`).

  Both hold `VersioningRunLocks`, so the other finds nothing pending. A run whose seeds cannot be pushed answers 503
  `seeds` and waits, so an untagged package is never released from scratch. Evidence:
  - `VersioningOnboardingTests` (plan content on Dotnet.Tools- and Nx-shaped trees);
  - `VersioningControllerTests.A_run_after_the_onboarding_merged_pushes_its_seed_tags_at_the_merge_commit_first`;
  - `VersioningControllerTests.A_run_waits_when_the_merged_onboardings_seed_tags_are_refused`.
- **After the deploy:** onboarding PRs for MintPlayer.AspNetCore.SpaServices, MintPlayer.AspNetCore.Tools,
  MintPlayer.Dotnet.Tools and one Nx repository. Each must publish one correct release. Under the one-PR rule these are
  part of this unit of work, sequenced after the deploy.
- **MintPlayer.Spark** adopts it in this PR in `local` mode (D2), by hand rather than through the onboarding action,
  because the server that would onboard it is deployed by the same workflow (M14):
  - `coverage.yml` gets `versioning: mode: local` and `ignore: ["**/fixtures/**", "**/Fixtures/**"]`. The ~50 fixture
    csproj/package.json files under test projects are not packages. `**/*.md` is not ignored, because a package's
    README ships inside it.
  - `Directory.Build.targets` imports the targets of `CodeCoverage.Versioning.MSBuild` from source (Spark builds the
    package, so it cannot consume it). Every packable `libs/**` csproj, and the MSBuild package itself, holds
    `0.0.0-PLACEHOLDER`. Evidence: with no tag, `dotnet pack` of `MintPlayer.Spark.Attributes` printed MPV001 and wrote
    no package; with a local tag `MintPlayer.Spark.Attributes/v11.0.0-preview.104` it wrote
    `MintPlayer.Spark.Attributes.11.0.0-preview.104.nupkg` (2026-10-24).
  - **The package ships `buildMultiTargeting/` too, and a placeholder nuspec is an error (`MPV004`).** 1.0.0 had only
    `build/`, which NuGet imports in a multi-targeted project's inner builds but not in the outer build that packs.
    AspNetCore.Tools (`net10.0;net11.0`) therefore packed and pushed 14 packages at `0.0.0-PLACEHOLDER` to nuget.org
    and GitHub Packages (master run 40132026912, 2026-10-24; unlisted on nuget.org the same day). Spark's packages
    are single-target and import the targets from source, so Spark never saw it. Evidence: a local pack of
    `MintPlayer.Timestamps` at Tools' `ea63582d` ran the target only in the two inner builds and wrote
    `0.0.0-PLACEHOLDER.nupkg`; with the fixed package it wrote `11.0.1-rc.0.nupkg`, with the tag deleted it printed
    MPV001 and wrote nothing, and with `MintPlayerVersioningSkip=true` it failed with MPV004 (2026-10-24).
  - **The onboarding references the latest stable `MintPlayer.Versioning.MSBuild` on nuget.org**, read from the
    flat-container index when the pull request is planned. `VersioningOnboarding.PackageVersion` is only the fallback
    when nuget.org cannot be read (10 s bound). Evidence: the fix above needed a hand bump of that constant from
    1.0.0 to 1.0.1 (PR #541), which only matched the release because the patch bump was predicted; 1.0.1 appeared in
    the flat container about 330 s after it was pushed (2026-10-24). A reference without a version is no alternative:
    NuGet resolves `>= 0.0.0` to the lowest version (1.0.0, the broken one), and central management rejects it (NU1010).
    Prereleases are never taken.
  - **`@mintplayer/ng-spark` keeps `22.44.0` in source, not the placeholder.** Seven app ClientApps depend on it with
    `^22.3.0`…`^22.43.0`, and npm links a workspace package only when its version satisfies the range; a prerelease
    `0.0.0` would make npm fetch the published package instead. The action stamps the seeded package whatever its
    source version, so nothing is lost, and `package-lock.json` is unchanged.
  - The `release` job (which, since #544, `needs` the test jobs) builds, runs the action (`token: github.token`, job permission `contents: write`, checkout with
    `persist-credentials: false` so only that step holds the token), then packs **without** `--no-build`
    (D9). The publish job pushes npm through `MintPlayer/github-actions/publish-npm-packages` with `skipDuplicate`.
  - **Seed tags** come from `tools/versioning-seed-tags.mjs`, which reads each package's version from `master` before
    the merge (46: 45 NuGet plus `@mintplayer/ng-spark/v22.44.0`) and creates the tags at that commit, so they are
    reachable from the merge commit. The owner pushes them right before merging; the script never pushes.
  - **The PR "version-bumped" gate is removed** (owner decision, 2026-10-30, PR #555). It failed every PR that
    changed a `libs/**` package unless the PR hand-bumped `<Version>`/`package.json`, which the placeholder and the
    tags now forbid. It never met this world: no PR between #537 and #555 touched `libs/**` (`git log
    36eee549..origin/master -- libs` is empty). On #555 it failed run 41090885790 for MintPlayer.Spark, .Abstractions,
    .Authorization, .Boards and ng-spark. What it guarded, "a changed package publishes nothing", is the engine's job
    now (D4).
  - **Not applicable:** the plan's "action-publish workflows call the in-repo action". The committed ncc actions are
    versioned by moving git refs, not published to a registry, so they have no package version to compute.
  - ~~**Expected first-release change:** the engine mirrors the upstream prerelease label (D8), so Spark's
    `11.0.0-preview.N` line follows the .NET SDK's label.~~ *Superseded by the D8 amendment:* Spark pins `preview`,
    so its packages continue `11.0.0-preview.N` until the pin is removed.

**A repository is "versioned"** when its default-branch `coverage.yml` has a `versioning:` section. The webhook features
(D10, D12, D3 B) act only on those. This is my call, not grilled: it keeps the opt-in in a file the owner reviews in the
onboarding PR, and needs no extra setting.

## Requirements

| # | Requirement | Decision |
|---|---|---|
| R1 | No version is stored in source; one annotated tag per released package | D1 |
| R2 | One engine library; server and local hosts produce identical tags | D2 |
| R3 | In `server` mode the action falls back to local when the server is unreachable; reruns are idempotent | D2 |
| R4 | Spark's publish never calls the deployed app | D2 |
| R5 | Only packages whose inputs, in-repo dependencies (per ecosystem rule), group or human tag require it are released | D4, D13 |
| R6 | The server never executes consumer code; every untrusted input is validated per the D5 table | D5 |
| R7 | Manifests are accepted only with matching OIDC claims | D5 |
| R8 | API hash travels in the tag; fallbacks registry → minor | D6 |
| R9 | Major from the anchor table; free packages manual; a major never goes down | D7 |
| R10 | Minor iff API hash changed; prerelease and label mirrored from resolved upstream | D8 |
| R11 | `MintPlayer.Versioning.MSBuild` for .NET (offline, exact tag, `--skip-duplicate`); `package.json` written for npm, internal ranges rewritten | D9 |
| R12 | Release branches; backports dry-run all targets, open PRs, never push partially | D10 |
| R13 | npm dist-tag output per package | D11 |
| R14 | Auto-cut release branch on a major move (policy) | D12 |
| R15 | PR-title correction (policy, default off), loop-safe | D14 |
| R16 | One Release per tag (policy, default on) | D15 |
| R17 | GitHub only behind `IForgeVersioning`; UI hidden elsewhere | D16 |
| R18 | Onboarding PR + seed tags; live-tested on a test repo; four repos onboarded after deploy | D17 |

## Out of scope

- **A PR-level "this is a preview feature".** A PR merged into a stable line ships stable; per-feature previews need a
  feature flag in code. Owner: "a can of worms I don't want to dig into for now".
- **Conventional-commit types as the bump source** (Q3). Consumers who want a specific version push a tag.
- **GitLab and Bitbucket** (D16). The interface is ready; the work is listed on #422.
- **Rebasing release branches.** A release line keeps its old TFMs and never follows `master`; fixes arrive as backports.
- **A breaking change bumping the major.** The major belongs to the platform.

## Questions — all answered 2026-10-24

Q1–Q14 were grilled with the owner and are folded into D1–D17 above.

## Risks

| Risk | Mitigation |
|---|---|
| A missed input file means a change is never released | S1 measures the static parser against real repos; ignore globs are explicit; the packed-package cross-check catches whole packages |
| API hash unstable across SDK/compiler versions → spurious minors | S3: normalized listing, tested across two SDK versions |
| Server and local hosts drift | conformance test with one fixture through both hosts |
| npm `latest` moved by a backport release | D11 dist-tag output, tested |
| Bot-created release branches unprotected | guide: add a `release/*` ruleset |
| Fallback grants the workflow `contents: write` | it could already push code with it; tags are only placed on reachable commits |

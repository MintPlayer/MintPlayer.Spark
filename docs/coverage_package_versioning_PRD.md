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
| MinVer | the nearest reachable tag with a prefix (`MinVerTagPrefix`); `x.y.(z+1)-alpha.0.{height}` after it | — | per prefix | **the .NET reader** (D9) |
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

**Trap accepted:** a build outside a tagged commit (local `dotnet pack`, forks) shows `0.0.0-PLACEHOLDER` or MinVer's
`x.y.z-alpha.0.N`.

### D2 — One engine, two hosts, chosen by `versioning.mode` (Q14)

The versioning logic is a pure library, `CodeCoverage.Versioning`, with no RavenDB and no server dependency. It owns
the graph, change detection, the version rules, annotated-tag writing and Release creation. Two hosts run it:

1. **server** (default): the action posts to coverage.mintplayer.com, the server computes and pushes the tags as the
   App, and records the run in RavenDB.
2. **local**: the action runs the engine in the workflow (a CLI, packaging per S4) and pushes with the workflow's
   `GITHUB_TOKEN`.

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
  before build and pack. The step computes and tags (D2), runs `git fetch --tags` and deepens a shallow clone itself,
  and exposes the versions as outputs. The consumer controls *when* a release happens: gate the step with `if:`, make the
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

### D9 — How the version reaches the artifacts (Q4)

- **.NET: MinVer.** The onboarding PR adds the `MinVer` package and `<MinVerTagPrefix>$(PackageId)/v</MinVerTagPrefix>`
  to `Directory.Build.targets` (`.targets`, because `PackageId` must be evaluated first; S1). Offline builds read local
  tags.
- **npm/Nx:** the action writes the version into the working-tree `package.json` of every publishable package with
  `npm version <v> --no-git-tag-version` (argument array, validated version), and rewrites internal `dependencies` /
  `peerDependencies` ranges (`workspace:*`, `file:`, plain ranges) to `^<released>` (S2). Nothing is committed.
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
- **Onboarding** is a row action on the dependency onboarding page's pattern. Its PR:
  - adds MinVer and the tag prefix to `Directory.Build.targets` (.NET);
  - replaces hand-edited versions with `0.0.0-PLACEHOLDER`;
  - adds the action step and `permissions: contents: write` to the publish workflow;
  - adds a `versioning:` section to `coverage.yml`.

  When it merges, the server pushes a **seed tag** per package from the version it replaced, so nobody restarts at
  0.0.0. A free package seeds from its own version (13.0.0).
- **After the deploy:** onboarding PRs for MintPlayer.AspNetCore.SpaServices, MintPlayer.AspNetCore.Tools,
  MintPlayer.Dotnet.Tools and one Nx repository. Each must publish one correct release. Under the one-PR rule these are
  part of this unit of work, sequenced after the deploy.
- **MintPlayer.Spark** adopts it in this PR in `local` mode (D2). Its hand-edited versions go, and seed tags are pushed
  from the current versions.

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
| R11 | MinVer for .NET (offline); `package.json` written for npm, internal ranges rewritten | D9 |
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

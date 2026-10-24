# Package versioning

> **Status:** built in PR for #153, not yet deployed. Design and decision log:
> [`../coverage_package_versioning_PRD.md`](../coverage_package_versioning_PRD.md) (D1–D17) and
> [plan](../coverage_package_versioning_plan.md). The decision numbers below point there.

CodeCoverage versions the NuGet and npm packages of a repository for you. Nobody edits a version number. Every
release is an annotated git tag `<PackageId>/v<version>`, for example `MintPlayer.Mapper/v13.2.0` or
`@mintplayer/ng-spark/v22.4.0`, and the tag is the only record of the version (D1). Your publish workflow runs one
action step after the build. That step tags every package that changed, then the pack reads the tags.

- **Only what changed is released** (D4). A package is released when a file it is built from changed since its
  last tag. A .NET package whose in-repo dependency is released is released too. An npm package is released only
  when its declared range no longer admits the new version.
- **The major follows the platform** (D7). It comes from the highest `netX` target framework, or from the resolved
  `@angular/core` / `react` / `vue`. `netstandard`, `net4x` and plain node libraries are **free**: they keep the
  major of their last tag, and you raise it by pushing a tag yourself.
- **Minor or patch comes from the public API** (D8). If the API listing changed, it is a minor. Otherwise it is a
  patch. A breaking change is a minor too, because the major belongs to the platform.
- **Prereleases mirror upstream** (D8). When the platform or a runtime dependency resolves to a prerelease, the
  package gets the same label (`preview`, `rc`, `next`, …) with its own counter: `11.0.0-rc.3`. For a .NET package
  that follows its target framework, the .NET SDK counts as an upstream too: `net11.0` built by SDK `11.0.100-rc.2`
  releases as `rc`.
- **A pin overrides the mirroring.** `prerelease: preview` in `coverage.yml` releases every package as `-preview.N`,
  whatever its upstreams are. Use it while the project itself is in preview, and remove it to go stable.

## Onboarding

On the repository page in CodeCoverage, run **Set up versioning**. It plans from the default branch and opens a
pull request from `mintplayer/versioning-onboarding` (D17) that:

- adds `MintPlayer.Versioning.MSBuild` to `Directory.Build.props` (.NET), at the latest stable version on nuget.org
  (a built-in version when nuget.org cannot be read);
- replaces hand-edited versions with `0.0.0-PLACEHOLDER`;
- adds the action step, and `permissions: contents: write` and `id-token: write`, to the publish workflow;
- adds a `versioning:` section to `coverage.yml`.

Review and merge it. At the merge, the app pushes one **seed tag** per package at the merge commit, from the
version the pull request replaced, so no package restarts at `0.0.0`. If the seeds cannot be pushed, the first
versioning run waits (`503 seeds`) rather than release an untagged package from scratch.

A repository is versioned when its **default branch's** `coverage.yml` has a `versioning:` section, even an empty
one. A branch cannot change the section for itself.

## The workflow step

Run the action after the build and before the pack. Then pack and publish **every** package: a package this commit
did not release packs at the version the feed already has, and the publish skips it.

```yaml
permissions:
  contents: write   # push the version tags
  id-token: write   # server mode: proves to the app which repository and workflow is calling

steps:
  - uses: actions/checkout@v7
  - run: dotnet build -c Release            # and/or your npm library builds
  - id: versioning
    uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action@versioning-v1
  # No --no-build: it implies --no-restore, which leaves the dependency versions in the nuspec stale.
  - run: dotnet pack -c Release -o nupkgs
  - run: dotnet nuget push 'nupkgs/*.nupkg' --source https://api.nuget.org/v3/index.json --api-key "${{ secrets.NUGET_API_KEY }}" --skip-duplicate
  - uses: MintPlayer/github-actions/publish-npm-packages@b08fbddf0b285a8633ac138b89d79ceb8cc8c888
    with:
      folder: dist/libs
      registries: |
        [{ "url": "https://registry.npmjs.org", "token": "${{ secrets.NPM_TOKEN }}" }]
```

The action's inputs and outputs (`released`, `versions`, `dist-tags`) are listed in
[its README](../../apps/CodeCoverage/versioning-action/README.md). It does nothing on pull requests and tag builds,
and it fetches the tags and the history it needs, so `fetch-depth: 0` is not required.

**What reaches the artifacts** (D9):

- **.NET:** `MintPlayer.Versioning.MSBuild` sets `Version` to the highest `<PackageId>/v*` tag reachable from
  HEAD, with no height suffix. A local `dotnet pack` therefore gets the last released version. A project with no tag
  yet (on the onboarding pull request, or a package not released yet) gets warning `MPV001`. In a local build, or
  in a GitHub Actions run of a pull request or merge queue, it packs at `0.0.0-untagged`. In any other CI run it
  gets `0.0.0-PLACEHOLDER` and `IsPackable=false`, so an untagged package never reaches a feed. Keep package pushes
  out of pull-request workflows. A
  multi-targeted project gets the same targets in its outer build, the one that packs (`buildMultiTargeting/`), and a
  pack that would still write a placeholder nuspec fails with `MPV004`.
- **npm:** the action writes the version into each publishable `package.json`, and into an already-built
  ng-packagr `dist/package.json`, and rewrites internal dependency ranges to `^<released>`. Nothing is committed.
  A package that has never been tagged keeps `0.0.0-PLACEHOLDER` and is marked `"private": true`, so it is not
  published.
- **npm dist-tags** (D11): `latest` only for the highest stable major, `v<major>-lts` for older majors, `next` for
  prereleases. Pass the `dist-tags` output to your publish if you publish more than one line.

### When a release happens (D3)

- **The action step** (the default): every run of the workflow on the default branch or a release branch versions
  that commit. Choose *when* with `if:`, a manual `workflow_dispatch`, or by pushing tags by hand.
- **Bot dispatch:** with `versioning.dispatch: publish.yml`, the app fires `workflow_dispatch` on that workflow
  after a merge that changed a package. No package changed means no dispatch. The App needs `actions: write`.
- **A tag you push by hand** is a fixed version and is read on the next run. The app ignores tag webhooks, so a tag
  on a commit no workflow runs on is never published.

### server or local (D2)

`mode: server` (the default) asks coverage.mintplayer.com, which computes the versions, pushes the tags as the App
and records the run. If the app is unreachable or answers 5xx, the action runs the same engine on the runner
instead. This is safe because a run is idempotent: tags already on HEAD are fixed versions. `mode: local` always runs
on the runner and pushes with the workflow's token. MintPlayer.Spark itself uses local mode, because its publish
workflow is what deploys the app.

## `coverage.yml` reference

```yaml
versioning:
  mode: server                       # server | local; default server (D2)
  dispatch: publish.yml              # bot dispatch (D3); default none
  releaseBranches: ["release/*.x"]   # release lines (D10); default release/*.x, at most 16
  ignore: ["**/*.md"]                # changed files that release nothing (D4); this list REPLACES the default
  prerelease: preview                # D8: alpha | beta | next | preview | rc | upstream; default upstream
  groups:                            # lockstep groups (D13)
    - name: mapper
      packages: [MintPlayer.Mapper, MintPlayer.Mapper.*]
  packages:
    MintPlayer.SourceGenerators: { anchor: free }   # D7: free, or the id of the package whose major it follows
    "@acme/stable-lib": { prerelease: upstream }    # a package pin wins over the repository pin
```

- **`prerelease`** sets the label outright; upstream prereleases never override it. `upstream` (the default) mirrors
  the least mature upstream label, the SDK included. A per-package pin wins over the repository's.

- **`ignore`** globs also keep matching project files out of the package graph. Test fixtures that contain
  `.csproj` or `package.json` files belong here. Leave out `**/*.md` if your packages ship their README.
- **`groups`**: members release together at the group's highest computed version whenever one of them releases,
  but each keeps its own tag. `packages: ["*"]` makes the whole repository one lockstep group. A group whose members
  compute different majors is refused.
- **An invalid section refuses the run** with a check-run error naming the problem. It never falls back to the
  defaults, because a wrong published version cannot be taken back.

## Release branches and backports (D10, D12)

Branches matching `releaseBranches` are release lines. They are versioned like the default branch, within the major
their own content computes. `master` on `net10.0` and `release/8.x` on `net8.0` release `10.2.3` and `8.1.2` side
by side.

**Auto-cut:** when a merge into the default branch moves a platform major, for example from `net10.0` to
`net11.0`, the app creates `release/10.x` at the merge's first parent and comments on the pull request. Free
packages never cut a branch.

**Backport:** label a pull request `backport: 8.x`, or comment `/backport 8.x`. Once it is merged, the app
dry-runs the cherry-pick onto every requested line.

- If every line applies, it pushes `backport/<pr>-<line>` and opens one pull request per line. The CI run on that
  pull request is the compile check against the old target.
- If any line conflicts, it pushes nothing and comments the failing files and the commands for a manual backport.

> **Protect release branches yourself.** A branch the app creates is unprotected unless a ruleset matches it. Add a
> repository ruleset targeting `release/*` with at least *Restrict deletions* and *Block force pushes*, and require
> your CI checks if you merge backports through it.

## GitHub Releases and pull request titles (D14, D15)

- **One Release per tag**, titled `<PackageId> <version>`. Its notes are the titles of the pull requests that
  touched the package since its previous tag, grouped into features, fixes and other.

  > ⚠️ A workflow `on: release` fires once **per Release**. A run that releases ten packages starts that workflow
  > ten times. Trigger publishing from the push, not from `release`.

- **Title correction** rewrites a pull request title to `<type>: <title> (#issue)`. The type comes from the
  template's ticked box, the title, a `bug` label on a linked issue, or the branch name, and otherwise defaults to
  `feat`. The titles become the release notes; they never decide a version.

Both are settings on the account and repository **Versioning** tab (Inherit / Enabled / Disabled):

| Setting | Account default |
|---|---|
| Release on tag | Enabled |
| Auto-cut release branches | Enabled |
| Pull request title correction | Disabled |

In local mode, the action's `releases` and `cut-release-branches` inputs control the same features.

## Forges

Versioning is GitHub only (D16). On GitLab and Bitbucket repositories the Versioning settings are hidden.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `MPV001: No tag '<id>/v<version>' is reachable from HEAD` | The package has no tag yet, or the checkout is shallow and has no tags (`fetch-depth: 0`, or let the action fetch before the pack). |
| `MPV004: <id> would be packed at 0.0.0-PLACEHOLDER` | The project skipped the versioning target (`MintPlayerVersioningSkip`) but is still packable, or it uses `MintPlayer.Versioning.MSBuild` 1.0.0, whose targets a multi-targeted project's outer build never imports. Update the package. |
| A package restarted at `X.0.0` | It had no seed tag. Push `<id>/v<last published>` on a commit before the release and re-run. |
| The run fails: "a major never goes down" | The computed major is below the last tag's, or the version already exists (D7). Anchor the package `free`, or fix the target framework. |
| The run answers `503 seeds` | The onboarding's seed tags could not be pushed yet. The run waits instead of releasing untagged packages. |
| Nothing is released after a merge | No input of any package changed: only ignored files did, or only files outside the project directories. |

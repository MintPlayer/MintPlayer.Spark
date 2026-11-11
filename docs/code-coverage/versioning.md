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

On the repository page in CodeCoverage, run **Set up features** and tick **Package versioning** (or **Release
lines**, which includes it). It plans from the default branch and opens one pull request from
`mintplayer/onboarding` (see [onboarding.md](onboarding.md)) that, for versioning:

- adds `MintPlayer.Versioning.MSBuild` to `Directory.Build.props` (.NET), at the latest stable version on nuget.org
  (a built-in version when nuget.org cannot be read);
- replaces hand-edited versions with `0.0.0-PLACEHOLDER`;
- adds the action step, and `permissions: contents: write` and `id-token: write`, to the publish workflow;
- gives the checkout of every job that runs `dotnet` `fetch-depth: 0`;
- adds a `versioning:` section to `coverage.yml`.

Each of these is a rule that checks before it edits, so **Set up features** can be run again on a repository that
was onboarded before. It then opens a pull request with only what is still missing (for example a checkout added
since), listed per rule under "Changes", or answers that there is nothing to change.

Review and merge it. At the merge, the app pushes one **seed tag** per package at the merge commit, from the
version the pull request replaced, so no package restarts at `0.0.0`. If the seeds cannot be pushed, the first
versioning run waits (`503 seeds`) rather than release an untagged package from scratch.

A repository is versioned when its **default branch's** `coverage.yml` has a `versioning:` section, even an empty
one. A branch cannot change the section for itself.

## The workflow step

Run the action **after the tests** and before the pack (#544 D3): a tag is a promise that the package ships, so it
is only made once the commit passed. Then pack and publish **every** package. A package this commit did not release
packs at the version the feed already has, and the publish skips it. When the tests run in another job, the job with
the action `needs:` it. The onboarding pull request moves the step there and says so in its notes when it cannot.

```yaml
permissions:
  contents: write   # push the version tags
  id-token: write   # server mode: proves to the app which repository and workflow is calling

# A newer push cancels the older run; a run cancelled after its tags is recovered by re-running it (see Troubleshooting).
concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true

steps:
  - uses: actions/checkout@v7
  - run: dotnet build -c Release            # and/or your npm library builds
  - run: dotnet test -c Release --no-build
  - id: versioning
    uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action@versioning-v1
  # No --no-build: the versions are decided after the tests, so pack builds again to stamp them into the
  # assemblies (--no-build also skips restore, which leaves the nuspec's dependency versions stale).
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
and it fetches the tags and the history it needs. A .NET build that runs before it, or in a pull request, still reads its
version from the tags reachable from HEAD, so every checkout of a job that runs `dotnet` needs `fetch-depth: 0`
(`fetch-tags: true` is not enough: a depth-1 clone reaches no tag). The onboarding pull request adds it (Dotnet.Tools#203).

**What reaches the artifacts** (D9):

- **.NET:** `MintPlayer.Versioning.MSBuild` sets `Version` to the highest `<PackageId>/v*` tag reachable from
  HEAD, with no height suffix. A local `dotnet pack` therefore gets the last released version. A project with no tag
  yet (on the onboarding pull request, or a package not released yet) gets warning `MPV001`. In a local build, or
  in a GitHub Actions run of a pull request or merge queue, it packs at `0.0.0-untagged`. In any other CI run it
  gets `0.0.0-PLACEHOLDER` and `IsPackable=false`, so an untagged package never reaches a feed. Keep package pushes
  out of pull-request workflows. A
  multi-targeted project gets the same targets in its outer build, the one that packs (`buildMultiTargeting/`), and a
  pack that would still write a placeholder nuspec fails with `MPV004`.
- **An explicit version wins** (#544 D1). `dotnet pack -p:Version=1.2.3` (or a `Version` passed down from a parent
  build) skips the target entirely: no git call, no MPV001, and the project stays packable. `-p:PackageVersion`
  alone sets `Version` too, so the assembly and the package agree. A `<Version>` in a csproj does **not** win, because
  that is where the placeholder lives. `MintPlayerVersioningSkip` still turns versioning off altogether.
- **A project packed on its own fails loudly** (#544 D2). On a push build, `dotnet pack path/to/One.csproj` of an
  untagged project is error `MPV005` instead of a silent placeholder. A solution pack still only warns MPV001, so
  untagged projects drop out. A project that is never released sets `IsPackable=false`.
- **npm:** the action writes the version into each publishable `package.json`, and into an already-built
  copy of it (ng-packagr's `dest`, or the Nx `build` target's `outputPath`), and rewrites internal dependency ranges to `^<released>`. Nothing is committed.
  A package that has never been tagged keeps `0.0.0-PLACEHOLDER` and is marked `"private": true`, so it is not
  published.
- **npm dist-tags** (D11): `latest` only for the highest stable major, `v<major>-lts` for older majors, `next` for
  prereleases. Pass the `dist-tags` output to your publish if you publish more than one line.

### Plan beside the tests, tag after them (#552)

The single step above computes the versions after the tests, so nothing that needs them can start before the tests
end. When the tests run in their own job, split the step in two with `phase`. `phase: plan` runs right after the
library build, in parallel with the tests. It decides the versions and stamps every publishable package.json (source
and build output), but writes no tag. A demo or docs site built after it reads the right version, and the
`all-versions` output gives every package's version, released or not. `phase: release` runs after the tests: it
writes exactly the tags the plan decided and stamps again, and the pack follows it. No tag exists before the tests
pass. `phase: all`, the default, is the single step. Several publish jobs may each release the same plan; the later
ones find the tags and Releases already there.

```yaml
jobs:
  plan:
    runs-on: ubuntu-latest
    permissions: { contents: read, id-token: write }
    outputs:
      plan: ${{ steps.plan.outputs.plan }}
    steps:
      - uses: actions/checkout@v7
        with: { fetch-depth: 0 }
      - run: dotnet build -c Release
      - id: plan
        uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action@versioning-v1
        with: { phase: plan }
  test:
    runs-on: ubuntu-latest
    steps: [ ... ]            # runs at the same time as plan
  publish:
    needs: [plan, test]
    runs-on: ubuntu-latest
    permissions: { contents: write, id-token: write }
    steps:
      - uses: actions/checkout@v7
        with: { fetch-depth: 0 }
      - uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action@versioning-v1
        with:
          phase: release
          plan: ${{ needs.plan.outputs.plan }}
      - run: dotnet pack -c Release -o nupkgs   # builds again, now with the tags
      - run: dotnet nuget push ...
```

- **The plan travels as a job output.** It holds the versions, the tags and the Release texts. In server mode it also
  holds the app's seal over them. `POST api/versioning/plan` answers with the plan and the seal, and
  `POST api/versioning/release` takes only the seal. The release's OIDC token must name the same repository, the same
  workflow run (any attempt), the same commit and the same branch. The seal holds for a day.
- **A stale plan is refused.** The release checks that no version tag was added, moved or deleted since the plan, its
  own tags aside. If one was, nothing is written and the step fails with `stale-plan`. **"Re-run all jobs"** plans again;
  "Re-run failed jobs" reuses the old plan and fails the same way. A release whose tags are already there (a re-run
  after a failed pack) succeeds and releases nothing new.
- **Expect this friction:** the check covers every version tag in the repository, not only the plan's packages. A
  release-branch run that tags while a master run is between plan and release makes the master release refuse, even
  when its versions would not have changed. Re-run it. This is the strict check the owner chose (#552 D3).
- **A hand-pushed tag goes in before the run.** To raise a major by hand (`<PackageId>/v5.0.0` on a .NET Standard
  library, whose major no platform sets), push the tag first and then start the workflow. A tag pushed while a run is
  between plan and release makes that release stale. "Re-run all jobs" then plans with it.
- **Fallback:** if the app answers 5xx to the plan, the plan is made on the runner. If it answers 5xx to the release,
  the runner writes the same plan's tags. A 409 (stale) or a 4xx fails the step.

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
  dependencyRanges: minimum          # #568 D10b: minimum (>= x, the default) | major-bounded ([x, nextMajor))
  groups:                            # lockstep groups (D13)
    - name: mapper
      packages: [MintPlayer.Mapper, MintPlayer.Mapper.*]
  packages:
    MintPlayer.SourceGenerators: { anchor: free }   # D7: free, or the id of the package whose major it follows
    "@acme/stable-lib": { prerelease: upstream }    # a package pin wins over the repository pin
    Acme.Generators: { line: dotnet }               # #568 D19: the ecosystem whose lines release it
  lines:                             # #568: present (even empty) = release only from release lines
    pattern: "release/{ecosystem}-{major}.x"        # the default; both placeholders, once each
    prerelease: { dotnet-10.x: upstream }           # D14: a label per line, between the package pin and `prerelease`
```

- **`prerelease`** sets the label outright; upstream prereleases never override it. `upstream` (the default) mirrors
  the least mature upstream label, the SDK included. A per-package pin wins over the repository's.

- **`dependencyRanges`**: a `ProjectReference` packs as a NuGet dependency on the referenced package's version. With
  `minimum` (the default) that's `>= x`, where x is the referenced package's highest tag reachable from the commit. On
  a release line that's the line's own tag, never the default branch's newer major. The target framework already
  stops a net10 consumer from restoring the net11 package (NU1202). `major-bounded` writes `[x, nextMajor)`. The
  versioning step sets the MSBuild property `MintPlayerVersioningDependencyRanges` for the pack that follows it, so a
  pack in another job needs the variable passed on.
- **`ignore`** globs also keep matching project files out of the package graph. Test fixtures that contain
  `.csproj` or `package.json` files belong here. Leave out `**/*.md` if your packages ship their README.
- **`groups`**: members release together at the group's highest computed version whenever one of them releases,
  but each keeps its own tag. `packages: ["*"]` makes the whole repository one lockstep group. A group whose members
  compute different majors is refused.
- **An invalid section refuses the run** with a check-run error naming the problem. It never falls back to the
  defaults, because a wrong published version cannot be taken back.

## Release branches and backports (D10, D12)

Moved to [backports.md](backports.md): auto-cut release branches, `/backport`, the `backport:` section of
`coverage.yml`, and where a change was backported.

## Ecosystem release lines (#568)

Add a `lines:` section (an empty `lines:` is enough) and packages release **only from release lines**, one per
ecosystem and platform major: `release/dotnet-10.x`, `release/dotnet-11.x`, `release/angular-21.x`. The default
branch then tags nothing; a change ships once its pull request is merged or backported onto a line. Without `lines:`
everything above still applies unchanged.

- **Which line releases a package.** A NuGet package is `dotnet`, netstandard included. An npm package takes its
  framework's ecosystem (`@angular/core` → `angular`, also `react`, `vue`); a plain node library follows the
  framework that all of the repository's npm packages share. An anchored package releases on the line of its major.
  A free package (netstandard, generators, plain node) releases on the **newest** line of its ecosystem.
  `packages.<id>.line` moves a package to another ecosystem.
- **Auto-cut.** A run on the default branch cuts every line its anchored packages need and that has never released:
  moving `Core` to `net11.0` creates `release/dotnet-11.x` at that commit and comments on the pull request. A line
  whose major already has a tag is never cut again, so a deleted line is recreated by hand.
- **Prereleases per line.** `versioning.prerelease` still applies to every line; `lines.prerelease` overrides it for
  one line, so `dotnet-10.x: upstream` goes stable while `dotnet-11.x` stays on `preview`. A package's own pin wins
  over both.
- **npm dist-tags.** A stable release on an older line goes out as `v<major>-lts`; a prerelease of an older major as
  `v<major>-next`, so it never moves `next` away from the newest line.
- **Workflows.** Run the versioning workflow on `release/**` pushes and pull requests too (MintPlayer.Spark's
  `dotnet-build-master.yml` and `pull-request.yml` do), and protect `release/**` with a ruleset ([backports.md](backports.md)).

### Promoted lines: `/release` (segment P)

With `current: promote`, the **current** line of each ecosystem (the highest major among its line branches) no longer
takes cherry-picked backports. A maintainer moves it to a green default-branch commit with a pull request or issue
comment instead; older lines keep taking backports.

```yaml
versioning:
  lines:
    current: promote                    # backport (the default) cherry-picks onto every line
    trigger: manual                     # the only trigger for now: a /release comment
    workflow: dotnet-build-master.yml   # its successful run on the default branch marks a commit green
```

- **`/release`** promotes every current line whose ecosystem changed since the line's last promotion, to the newest
  default-branch commit whose `workflow` run succeeded. `/release <sha>` takes that green commit instead;
  `/release dotnet-11.x` (or the branch name) and `/release all` promote the named lines, or all of them, changed or
  not. The App answers with a comment that lists each line and any newer commit left out.
- **Who may.** The commenter needs the repository's release role (maintain by default; *Release role* on the
  repository or the account).
- **The commit.** A promotion is a merge commit (the green commit's tree, parents the line's tip and the green commit)
  that the App pushes with a lease. Ruleset 3 gives the App an `exempt` bypass for it (merge-queue.md). The line's own
  run then tests the commit and publishes.
- **Line-only changes refuse.** A line that holds a change the default branch does not (a hotfix made on the line)
  is not promoted; the comment names the paths and the commits. Bring the change to the default branch first.
- **Versions.** `/release Acme.Core@11.4.0` releases that exact version: the App places a `Acme.Core/v11.4.0` tag on
  the promotion commit in the same push. The version must be unpublished (nuget.org and npm are asked), carry the
  line's major, and lie above the tag it follows: the package's highest tag reachable from the line's tip or the green
  commit (the promotion's two parents). Otherwise the line is not promoted.
- **Tags pushed by hand for the current line.** A valid package tag (`<id>/v<version>`) of the current line's major
  is removed at once, recorded, and placed on the next promotion of its line. Left in place, it would become reachable
  from the line at that promotion and move every later version. The App comments on the tagged commit and names the
  pusher. A recorded version that no longer fits by the time of the promotion (the line released past it) is reported
  in the comment and forgotten. Tags of older lines follow the rules below and stay where they are.
- **The release run checks.** With `current: promote`, the versioning step of a line's run asks
  `POST /api/versioning/tags/verify` about every hand tag on the commit, and fails on one the App did not place there,
  or when the server cannot be reached. The check sees tags, not who copied an annotation onto one: a ruleset that
  lets only the App and `github-actions` create `*/v*` tags closes that gap, and is left to the repository owner.

### Tags pushed by hand on release lines (segment T)

In every repository with `lines:`, promoted or not, the App checks each package tag a person pushes. It compares the
tag with **the one tag it follows**: the package's highest tag reachable from the tagged commit, the same tag the pack
reads (`tag --merged HEAD`). Other tags in the repository, on other branches or lines, play no part.

- **Valid:** above the tag it follows, with the same major (`Acme.Core/v8.1.2` after `Acme.Core/v8.1.1`). Any higher
  version is accepted, not only the next one, so a skipped patch is fine.
- **Without a tag to follow** (a package's first tag), the major must be one of a release line of its ecosystem:
  `v8.1.2` needs `release/dotnet-8.x`.
- **Invalid tags are deleted**, with a commit comment that names the pusher and the reason: a lower or equal version
  (`v8.1.0` after `v8.1.1`), or another major (`v7.0.4` after `v8.1.1`, or `v9.0.0` on the dotnet-8 line). A new
  major starts with its release line, never with a hand tag. Nothing builds on a refused tag.
- **Valid tags stay** on their commit and are recorded as placed there (no comment), so a promoted repository's
  older-line run passes `tags/verify`. The current line's tags in a promoted repository are the exception above.
- A tag whose id is no package at its commit (`schemas/v3`) is left alone.

**Platform lifecycle page.** The app collects each platform's support phases nightly (.NET from
`releases-index.json`; Angular, React, Vue and Node.js from endoflife.date; a framework's preview major from its npm
`next` dist-tag). Open *Guides → Platform lifecycle* to see which majors are in preview, active, maintenance or end of
life, and so which lines are worth keeping.

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

### GitHub Packages as a restore source (#544 D4)

`MintPlayer.Versioning.MSBuild` is published to nuget.org and to MintPlayer's GitHub Packages feed. A repository
whose setup-dotnet has `source-url: https://nuget.pkg.github.com/<owner>/index.json` asks every source for every
package. GitHub Packages then answers **403** for this package, because the repository is not on the package's
Actions access list, and the restore fails. The onboarding pull request adds a `nuget.config` at the repository root,
or updates the one that is there:

- the feed, listed under exactly the URL `source-url` uses, so setup-dotnet attaches its credentials to that key;
- a `packageSourceMapping` that sends the exact id `MintPlayer.Versioning.MSBuild` to nuget.org. An exact id beats a
  prefix and `*`, so the feed is never asked for it. A file with no mapping yet gets one in which every source keeps
  `*`, so everything else resolves as before.

The workflow is not touched. A `source-url` built from an expression other than `github.repository_owner`, or a feed
in a nested `nuget.config`, gets a note in the pull request with the fix to make by hand.

### The release on the commit graph (#544 D5)

Each version tag records the workflow run that wrote it, as a `run:` line in its annotation. On the repository page,
the commit graph marks a tagged commit with that run's status: *released*, *release running*, *release failed* or
*release cancelled*. The mark links to the run. A viewer whose own GitHub login has write access to the repository
gets a **re-run** button on a failed or cancelled run, which re-runs its failed and cancelled jobs. It is refused
while a newer run of the same workflow is unfinished, because the re-run would cancel that newer run (`concurrency`
above). The App needs `actions: write` for the button. Seed tags, hand-made tags and tags from before this change
have no `run:` line and show no mark.

### The published packages on the pull request

When a server-mode publish run finishes **green**, the pull request its commit was merged from gets one comment: a
table of the packages and versions the run tagged (`created` or `already-present`; a conflicting tag is left out). The
app reads the pull request from GitHub's commit-to-pulls API, keeps only merged ones whose base is the run's branch,
and comments on at most five. The outcome is saved on the run (`commented`, `none` or `failed-run`), so a redelivered
`workflow_run` comments nothing twice. A failed or cancelled run comments nothing; its re-run is a new attempt with its
own record and comments when it goes green. Local-mode repositories have no run record and get no comment.

## Forges

Versioning is GitHub only (D16). On GitLab and Bitbucket repositories the Versioning settings are hidden.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `MPV001: No tag '<id>/v<version>' is reachable from HEAD` | The package has no tag yet, or the checkout is shallow and has no tags (`fetch-depth: 0`, or let the action fetch before the pack). |
| `MPV004: <id> would be packed at 0.0.0-PLACEHOLDER` | The project skipped the versioning target (`MintPlayerVersioningSkip`) but is still packable, or it uses `MintPlayer.Versioning.MSBuild` 1.0.0, whose targets a multi-targeted project's outer build never imports. Update the package. |
| A package restarted at `X.0.0` | It had no seed tag. Push `<id>/v<last published>` on a commit before the release and re-run. |
| The run fails: "a major never goes down" | The computed major is below the last tag's, or the version already exists (D7). Anchor the package `free`, or fix the target framework. |
| `phase: release` fails with `stale-plan` | A version tag was added, moved or deleted between the plan and the release (#552 D3). Use **"Re-run all jobs"**, which plans again. "Re-run failed jobs" reuses the stale plan. |
| `phase: release` says the plan was made for another commit | The `plan` input is from another run, or the branch moved. Use "Re-run all jobs". |
| The run answers `503 seeds` | The onboarding's seed tags could not be pushed yet. The run waits instead of releasing untagged packages. |
| Nothing is released after a merge | No input of any package changed: only ignored files did, or only files outside the project directories. |
| The run failed or was cancelled after the Version step | The tags exist but some packages were not pushed. **Re-run it** (the commit graph's re-run button, or GitHub's "Re-run failed jobs"). The re-run finds its own tags, releases the same versions and pushes what is missing. A tag whose package never ships is harmless: the next release of that package gets the next version. |
| `MPV005: <id> has no version tag on this push build` | A single project was packed on its own on a push build, and it has no tag. Pass `-p:Version`, tag it, pack the solution instead, or set `IsPackable=false` on a project that is never released. |
| `-p:Version` is ignored | `MintPlayer.Versioning.MSBuild` older than the #544 release. Update the package. |
| Restore warns `NU1603: <Q> depends on <P> (>= X) but <P> X was not found` | A run was cancelled or failed partway through its push, and a later release of P skipped the dangling version X. Re-run the failed run **before** the next one, so that it publishes P X. |
| Restore fails with **403** from `nuget.pkg.github.com` for `MintPlayer.Versioning.MSBuild` | The repository restores from GitHub Packages without a mapping (see "GitHub Packages as a restore source" above). Merge the onboarding's `nuget.config`, or map the id to nuget.org yourself. |

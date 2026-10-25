# MintPlayer package versioning: the GitHub Action

`MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action` is the workflow half of CodeCoverage's automated
package versioning (#153, `docs/coverage_package_versioning_PRD.md`). It decides the next version of every
publishable NuGet and npm package from what changed. It then tags the release commit `<package id>/v<version>` and
writes the npm versions into the package.json files your publish step reads. NuGet versions come from those tags
through the `MintPlayer.Versioning.MSBuild` package.

## Usage

Run it after the build and the tests, and before pack (a tag promises that the package ships), on pushes to the default branch and to release branches. Pack and publish
**every** package, every time: a package this commit did not release packs at the version it already published, and
the publish skips it as a duplicate.

```yaml
permissions:
  contents: write   # push the version tags
  id-token: write   # server mode: prove to the app which repository and workflow is calling

steps:
  - uses: actions/checkout@v7
  - run: dotnet build -c Release          # and/or your npm library builds
  - id: versioning
    uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action@versioning-v1
  # No --no-build: the build has to run again, so the assemblies carry the version the new tags give them.
  - run: dotnet pack -c Release -o nupkgs
  - run: dotnet nuget push 'nupkgs/*.nupkg' --source https://api.nuget.org/v3/index.json --api-key "${{ secrets.NUGET_API_KEY }}" --skip-duplicate
  # Publishes every non-private package under the folder, skipping versions the registry already has.
  - uses: MintPlayer/github-actions/publish-npm-packages@b08fbddf0b285a8633ac138b89d79ceb8cc8c888
    with:
      folder: dist/libs
      registries: |
        [{ "url": "https://registry.npmjs.org", "token": "${{ secrets.NPM_TOKEN }}" }]
```

The action does nothing on pull requests and tag builds. It fetches the tags and the history it needs itself, so
`fetch-depth: 0` is not required.

## npm: every package is stamped

The action writes a version into every publishable (non-private) package.json, and into the build's copy of it:
`<ng-package.json dest>/package.json`, else `<outputPath>/package.json` of the library's Nx `build` target
(`@nx/js:tsc`, `@nx/vite:build`):

- a package this commit releases gets its new version, and its dist-tag as `publishConfig.tag`, which `npm publish`
  and `publish-npm-packages` use by default: `next` for a prerelease, `latest` for the highest stable major,
  `v<major>-lts` for an older major;
- any other package gets the highest version tagged in HEAD's history, which the registry already has;
- a package with no such tag that is still at `0.0.0-PLACEHOLDER` is marked `"private": true`, so nothing publishes
  the placeholder.

Only those two outputs are found and stamped, and only when the build ran before this step. With another builder,
build the npm libraries after this step, so they copy the stamped package.json.

It reads the `versioning:` section of `coverage.yml` from the **default branch**, the same file the app reads. A
repository without that section is refused.

## Inputs

| Input | Default | Meaning |
|---|---|---|
| `mode` | `versioning.mode`, else `server` | `server`: the app plans and pushes the tags. `local`: the engine runs on the runner, from this action's source. |
| `url` | `https://coverage.mintplayer.com` | The app, for server mode. |
| `configuration` | `Release` | The build configuration whose output the API hashes are read from. |
| `working-directory` | `.` | The checkout root. |
| `token` | (none) | Local mode only: a token to push the tags with. Without it the checkout's own credentials push (`contents: write`). |
| `registry-fallback` | `true` | When the last version's tag has no API hash (a tag made by hand), compare against the package on nuget.org / npmjs.org. |
| `releases` | `true` | Local mode only: create one GitHub Release per new tag, notes grouped from the commit subjects. Server mode follows the app's release-on-tag setting. |
| `cut-release-branches` | `true` | Local mode only: when a platform package's major moves on the default branch, cut `release/<old major>.x` at the merge's first parent and comment on the merged pull request. Server mode follows the app's auto-cut setting. |
| `github-token` | `${{ github.token }}` | Local mode only: the token the Releases and release branches are created with (`contents: write`; `pull-requests: write` for the cut comment). |

Every tag gets its own Release, so a workflow triggered `on: release` runs once per released package.

In server mode the action falls back to local mode when the app cannot answer: no OIDC token (`id-token: write`
missing), a network error, a timeout or a 5xx. A 4xx is the app's answer, and the action fails on it.

## Outputs

| Output | Content |
|---|---|
| `outcome` | `versioned`, or `skipped` (a pull request or tag build). A refusal fails the step and names the packages. |
| `mode` | The host that ran: `server` or `local`. |
| `released` | JSON array of the package ids this commit released. |
| `versions` | JSON object, package id → version, for every released package. |
| `dist-tags` | JSON object, npm package → dist-tag: `next` for a prerelease, `latest` for the highest stable major, `v<major>-lts` for an older major. |

## NuGet: versions from the tags (MintPlayer.Versioning.MSBuild)

Reference the package once, in `Directory.Build.props`:

```xml
<ItemGroup>
  <PackageReference Include="MintPlayer.Versioning.MSBuild" Version="1.0.0" PrivateAssets="all" />
</ItemGroup>
```

Every packable project then takes its `Version` from the highest `<PackageId>/v<version>` tag in HEAD's history,
exactly: no height suffix, no prerelease of its own. A prerelease comes from the tag (`1.4.0-preview.2`). A project
with no such tag yet gets warning MPV001. It packs at `0.0.0-untagged` in a local build or a pull-request or
merge-queue run, and is versioned `0.0.0-PLACEHOLDER` and **not packed** in any other CI run, so it never reaches a
feed. `<MintPlayerVersioningSkip>true</MintPlayerVersioningSkip>` leaves a project alone, and
`<MintPlayerVersioningTagPrefix>` reads another id's tags. The package ships its targets and the task's source, which
MSBuild compiles in your build; it adds no dependency to your packages.

## Versions of this action

Pin the moving `versioning-v<major>` tag, or a commit with that tag as a comment. `versioning-action-publish.yml`
moves the tag on every change to the action or to the engine it runs from source in local mode. `dist/` is the
committed ncc bundle; build it from the main checkout only (see the repository's `CLAUDE.md`).

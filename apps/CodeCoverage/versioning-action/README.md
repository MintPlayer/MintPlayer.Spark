# MintPlayer package versioning: the GitHub Action

`MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action` is the workflow half of CodeCoverage's automated
package versioning (#153, `docs/coverage_package_versioning_PRD.md`). It decides the next version of every
publishable NuGet and npm package from what changed. It then tags the release commit `<package id>/v<version>` and
writes the npm versions into the package.json files your pack step reads. NuGet versions come from those tags
through MinVer.

## Usage

Run it after the build and before pack, on pushes to the default branch and to release branches:

```yaml
permissions:
  contents: write   # push the version tags
  id-token: write   # server mode: prove to the app which repository and workflow is calling

steps:
  - uses: actions/checkout@v7
  - run: dotnet build -c Release          # and/or your npm library builds
  - id: versioning
    uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action@versioning-v1
  - run: dotnet pack -c Release --no-build
  - run: npm publish dist/my-lib --tag "${{ fromJSON(steps.versioning.outputs.dist-tags)['@acme/my-lib'] }}"
```

The action does nothing on pull requests and tag builds. It fetches the tags and the history it needs itself, so
`fetch-depth: 0` is not required.

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
| `github-token` | `${{ github.token }}` | Local mode only: the token the Releases are created with (`contents: write`). |

Every tag gets its own Release, so a workflow triggered `on: release` runs once per released package.

In server mode the action falls back to local mode when the app cannot answer: no OIDC token (`id-token: write`
missing), a network error, a timeout or a 5xx. A 4xx is the app's answer, and the action fails on it.

## Outputs

| Output | Content |
|---|---|
| `outcome` | `versioned`, or `skipped` (a pull request or tag build). A refusal fails the step and names the packages. |
| `mode` | The host that ran: `server` or `local`. |
| `released` | JSON array of `{ id, kind, version, tag }`. |
| `versions` | JSON object, package id → version, for every released package. |
| `dist-tags` | JSON object, npm package → dist-tag: `next` for a prerelease, `latest` for the highest stable major, `v<major>-lts` for an older major. |

## NuGet: versions from the tags (MinVer)

Add MinVer to every packable project, and make it read the package's own tags. In `Directory.Build.targets`:

```xml
<Target Name="SetMinVerTagPrefix" BeforeTargets="MinVer">
  <PropertyGroup>
    <MinVerTagPrefix>$(PackageId)/v</MinVerTagPrefix>
  </PropertyGroup>
</Target>
```

## Versions of this action

Pin the moving `versioning-v<major>` tag, or a commit with that tag as a comment. `versioning-action-publish.yml`
moves the tag on every change to the action or to the engine it runs from source in local mode. `dist/` is the
committed ncc bundle; build it from the main checkout only (see the repository's `CLAUDE.md`).

# Companion version maps: which package version belongs to which SDK — PRD and plan

Status: **M1–M2 done, M3 next** (2026-11-15). Branch `fix/align-platform-packages-to-tfm`, PR #676 (one pull request with
[follower-cooldown-PRD](follower-cooldown-PRD.md) H1, owner: "Single pull-request").

## 1. The problem

The bot knows one relation between an SDK and its packages: *package major = channel major*
(`DependencyCompanions.cs`, #606 D2; H1 extends it to older majors in a project on the channel). That is right for
`Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`, EF Core and `dotnet-ef`, and wrong for families that number
differently but are still tied to the SDK:

- **MSBuild** (`Microsoft.Build*`): a task assembly referencing a newer MSBuild than the SDK ships may not load.
  Dotnet.Tools #215 moved `Microsoft.Build.Utilities.Core` 18.4.0 → 18.6.3 without checking the SDK band.
- **Roslyn** (`Microsoft.CodeAnalysis*`): a source generator or analyzer must reference a compiler no newer than the
  oldest SDK it supports. #215 had 5.9.0 → 5.12.0 waiting only on the cooldown.
- **`Microsoft.NET.Test.Sdk`** (18.x) matches the `Microsoft.NET.*` glob but never the channel major, so it is silently
  no companion.

## 2. Owner scope (2026-11-15)

- "Do you think we could perhaps let the relations between the SDK version (net8.0 - net9.0 - net10.0 - net11.0) and
  some package versions (Microsoft.AspNetCore.* or Microsoft.AspNetCore.**) specified in the coverage.yml file too? Then
  the bot knows what package versions it should pick"
- "Single pull-request" · "Pick the most advanced capabilities" · "The no-longer-needed parts of the configuration can be
  removed without problem" · "If necessary, launch a new team to further investigate. Then update the PRD/Plan files,
  which may include several spikes"

## 3. Proposal (to be confirmed by the spikes)

A companion entry is a glob (today's rule) or an object with a version map:

```yaml
dependencies:
  platforms:
    dotnet:
      companions:
        - "Microsoft.AspNetCore.**"                  # major = channel major
        - match: "Microsoft.Build*"
          versions: { "10.0.1xx": "<=18.0", "11.0": "18.x" }
```

- Keys: a channel (`11.0`) or an SDK feature band (`11.0.1xx`); the band wins over its channel.
- A map is a **ceiling and a target**: the highest release inside the range for the project's SDK, never above it.
- Built-in maps for the well-known families; coverage.yml entries override or add.
- Validation at read time; a malformed key/range or an entry that never matches is reported in the pull request.

## 4. Findings

### 4.1 F-C: the code today (code map, 2026-11-15, master `677f27b56` + H1)

- **Parse.** `FilePlatform.Companions` is `object?` (`Dependencies/DependenciesYml.cs:490`), read with `Strings(...)`
  (`:207`, `:351-380`) into `DependencyPlatformSettings.Companions` (`:540-542`): null = built-in, `[]` = off (`:535`).
- **Errors never reach the pull request.** A `follow:` error is added at `:160-161`; any error drops the whole section to
  defaults (`:231-239`); `DependencyUpdateEngine.cs:267-269` writes the first five to the run timeline only. The body shows
  only lookup-time follow problems (`DependencyPullRequestBody.cs:129-130`). No JSON schema for coverage.yml exists.
- **Consumers.** `DependencyPolicyConfig.Companions(sdk)` (`Policy/DependencyPolicyConfig.cs:188-189`) → `PackageTrackOf`
  (`:163`), `UnstableGlobs` (`:199-205`), `DependencyPolicyEngine.FindCompanions` (major equality at `:221-223`),
  `LaggingRiders` (`:652`), the major-move riders (`:769-789`). The action sees companions only through the `unstable`
  glob list (`DependencyPlatformTable.cs:52-57` → `feeds.ts:12-34` → Renovate packageRules `renovate.ts:40`).
- **The SDK band.** `PlatformCurrent.Channels` holds channels only (`CodeCoverage.Git/Dependencies/DependencyContracts.cs:223-230`).
  The repository's full SDK version is only in `PlatformLocation.Value` of kind `global.json` / `setup-action`
  (`locations.ts:712-716`); `PlatformCandidate.LatestSdk` is the feed's newest, not the repository's. Nothing models a band.
  The SDK is repository-wide (one `global.json`), so a band is a property of the repository, a channel of each project.
- **Ranges.** `CodeCoverage.Versioning/Versions/NpmRange.cs` (`TryParse :45`, `Admits :73,83`: `<=`, `x`, `^`, `~`, `||`,
  hyphen) is already referenced by `CodeCoverage` (`CodeCoverage.csproj:45`). No NuGet interval parser exists.
- **Tests touching companions:** `PlatformInChannelMoveTests.cs:142`, `PlatformTrackCompanionTests.cs:118,150`,
  `DependencyOnboardingTests.cs:250-263`, `DependenciesYmlTests.cs`, `DependencyPreviewToReleaseEndToEndTests.cs:73`,
  `DependencyFleetViewTests.cs:100`, `GitWriter.Tests/DependencyPlatformPatchTests.cs:155`.
- **Docs:** `dependency-updates.md:166,232-247,351,439-445`; `sdk-updates-PRD.md:72-96,183`;
  `follower-cooldown-PRD.md:75-78,95,205-221`; samples `DependenciesYml.cs:54-57`, `DependencyOnboarding.cs:99`,
  setup page `dependency-updates-setup.snippets.ts:40`.

### 4.2 F-V: package versions per SDK band (research, 2026-11-15)

From `dotnet/sdk` `eng/Version.Details.xml` at release tags (band research), first → last release of each band:

| SDK band | MSBuild | Roslyn (`Microsoft.CodeAnalysis*`) | NuGet | TestPlatform CLI |
|---|---|---|---|---|
| 8.0.1xx / 2xx / 3xx / 4xx | 17.8 / 17.9 / 17.10 / 17.11 | 4.8 / 4.9 / 4.10 / 4.11 | 6.8 – 6.11 | 17.8 – 17.11 |
| 9.0.1xx / 2xx / 3xx | 17.12 / 17.13 / 17.14 | 4.12 / 4.13 / 4.14 | 6.12 – 6.14 | 17.12 – 17.14 |
| 10.0.1xx / 2xx / 3xx / 4xx | 18.0 / 18.3 / 18.6 / 18.9 | 5.0 / 5.3 / 5.6 / 5.9 | 7.0 / 7.3 / 7.6 / 7.9 | 18.0 – 18.7 |
| 11.0.1xx | 18.12 | 5.12 | 8.0 | 18.11 – 18.12 |

- **Tied to the band, not the major:** MSBuild, Roslyn and NuGet move with every feature band. A generator or analyzer
  referencing a newer `Microsoft.CodeAnalysis` than the host compiler is not loaded (CS8032); an MSBuild task built against
  a newer MSBuild API than the host fails with MissingMethod/TypeLoad (task libraries reference the oldest supported
  MSBuild with `ExcludeAssets="runtime"`). *(Load behaviour from the researcher's knowledge, not fetched.)*
- **Tied to the runtime major:** `Microsoft.NET.ILLink.Tasks`, `Microsoft.DotNet.ILCompiler` (11.0.0, 10.0.15).
- **Independent:** `Microsoft.NET.Test.Sdk` / `Microsoft.TestPlatform.*` (18.x line, no verified coupling).
- **.NET 8 and 9 are end of life** since 2026-11-10 (`releases-index.json` `support-phase: eol`); 10.0 and 11.0 are active.
- **Dotnet.Tools #215 was safe:** on SDK 11.0.1xx, MSBuild 18.4 → 18.6.3 and Roslyn 5.9 → 5.12 stay within the SDK's 18.12
  / 5.12. A ceiling matters for libraries that support an older SDK than the one they build with; that is what an explicit
  map is for.

## 5. Spikes

| # | Question | Result |
|---|---|---|
| S1 | Where does a bot read what an SDK release bundles? | **Done.** The VMR `dotnet/dotnet` tag `v<sdk>` exists from 10.0.100 (`v10.0.100`, `v10.0.300`, `v11.0.100`: 200; `v8.0.201`, `v9.0.300`: 404). Its `src/sdk/eng/Version.Details.xml` is **stale** (10.0.300 says MSBuild 18.0.11), but each component's own source is right: `src/msbuild/eng/Versions.props` `VersionPrefix` (18.0.2 / 18.6.3 / 18.12.2), `src/roslyn/eng/Versions.props` Major/Minor/Patch (5.0.0 / 5.6.0 / 5.12.0), `src/nuget-client/build/config.props` Major/Minor/PatchNuGetVersion (7.0.0 / 7.6.0 / 8.0.0). The releases JSON exposes none of these. |
| S2 | Pre-10 SDKs? | **Done.** 8.0 and 9.0 are EOL (4.2); no built-in table. A repository on them gets no bundled ceiling, and the pull request says why. |
| S3 | Does `NpmRange` admit NuGet versions (`18.12.0-1.26473.115`, four-part)? | **Done** (`CompanionVersionMapTests.S3_npm_ranges_over_nuget_versions`). `<=18.12` admits 18.12.2 and refuses 18.13.0. A prerelease build (`18.12.0-1.26473.115`) is refused, by npm's rule that only a range naming that prerelease line admits it. That is wanted: a stable repository never takes a daily build. A four-part version (`4.0.0.1`) is no SemVer, so no range admits it, and it is listed as above the range. |
| S4 | Which SDK version is "the repository's"? | `global.json`'s `sdk.version` when present (the build honours it; `rollForward` only goes up, so it is the conservative floor); else the lowest exact `setup-dotnet` version; else the newest SDK of the repository's channel in the platform table (what a floating `11.0.x` installs). |

## 6. Design

### 6.1 coverage.yml

```yaml
dependencies:
  platforms:
    dotnet:
      companions:
        - builtin                                   # the built-in entries below, after the repository's own
        - match: "Microsoft.CodeAnalysis*"          # a generator that must load on SDK 10.0.1xx
          versions: { "10.0": "<=5.0", "11.0.1xx": "5.12.x" }
        - match: "MyCompany.Platform.*"             # an own family numbered like .NET
```

An entry is a glob string (**channel rule**: package major = channel major), `builtin`, or an object with `match` and at
most one of:

- `versions:` a map from a **channel** (`11.0`) or an **SDK band** (`11.0.1xx`) to an npm-style range (`5.12.x`, `<=18.6`,
  `^4.14`, `4.8 - 4.14`, `a || b`, `NpmRange`).
- `bundled: msbuild | roslyn | nuget`: the range is `<= major.minor.x` of what the repository's SDK ships (S1).

First match wins, in list order. Without `companions:` the list is `[builtin]`; `[]` turns companions off. The old plain
glob list still parses (each string is a channel-rule entry) but no longer implies the built-ins: write `builtin` to keep
them (V3, the owner allowed the break).

**Built-in** (`DependencyCompanions.cs`): channel rule for `Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`,
`Microsoft.EntityFrameworkCore*`, `Microsoft.NET.*`, `System.*`, `dotnet-ef`, `Microsoft.DotNet.ILCompiler`; `bundled: msbuild` for
`Microsoft.Build*`; `bundled: roslyn` for `Microsoft.CodeAnalysis*` and `Microsoft.Net.Compilers.Toolset`;
`bundled: nuget` for `NuGet.*`. Node: channel rule for `@types/node`.

### 6.2 Which key applies

- The **band** key of the repository's SDK (S4) first, then the **channel** key of the record's project: a project
  that targets one channel uses it; a multi-target project uses its **lowest** channel (it must load there too); a file
  with no TFM (`Directory.Packages.props`, `dotnet-tools.json`) uses the repository's channel.
- No key applies → the entry gives no range for this record (it is an ordinary package for that record).

### 6.3 What a range does

- **Ceiling.** An ordinary update above the range is not planned; it is listed under *Available, not applied* as
  "above `<range>` for SDK `<band>` (companions)".
- **Target.** Inside the range, the highest release is the target. A record below the range's floor is aligned like H1
  (rides with the platform group, else a `platform:<sdk> <channel>` group of its own, patch level, no approval).
- **On a platform move** (a new band or channel), the range is recomputed for the target SDK, and the record rides to
  the highest release inside the new range. It never holds the move.
- The channel rule is the range `<major>.x` of the channel, so H1 and #606 D2 become this mechanism's special case.

### 6.4 Bundled versions: collected, not guessed

`PlatformLifecycleCronJob` (daily) also collects, for every SDK release of every active .NET channel, the MSBuild,
Roslyn and NuGet versions from the VMR tag (S1) into an immutable `SdkComponents/<sdk version>` document. Only
releases without a document are fetched, a failed fetch is retried the next day, and a release whose tag is missing is
recorded as such. The planner batch-loads the documents it needs (repository SDK + target SDKs) in one request.

### 6.5 Errors reach the pull request

A coverage.yml error (bad key, bad range, unknown `bundled`, `versions` and `bundled` together, …) is listed in the
pull request body under *Configuration problems*, not only on the run timeline (F-C: errors never reach the PR today).

## 7. Plan (one pull request, #676)

- **M1** Config model + parse/validate (`DependenciesYml`, `DependencyPlatformSettings`, `DependencyCompanions`):
  entry kinds, `builtin`, keys, ranges (S3). Red: `DependenciesYmlTests` per error and per form.
- **M2** Range resolution (6.2) + engine (6.3): `FindCompanions` by entry, ceiling filter, alignment generalised from H1,
  riders on a move. Red: `CompanionVersionMapTests` (map ceiling, band beats channel, multi-target lowest channel,
  bundled, move recomputes, never holds).
- **M3** `SdkComponents` collection in the cron + planner load (6.4). Red: parser tests on the real VMR files (fixtures
  from S1), cron test with a fake HTTP handler, planner test (one batch load).
- **M4** Errors in the pull request body (6.5). Red: `DependencyPullRequestBody` test.
- **M5** Action/unstable globs, setup page snippet, onboarding sample, docs (`dependency-updates.md`, sdk-updates-PRD D2
  mark), PRD status.
- Gate: CI on the pull request (no local sweep), then patch coverage via the codecoverage MCP.

### 7.1 Progress

*(per milestone: evidence, red → green)*

- **M1 done** (2026-11-15). `DependencyCompanionEntry` + `EDependencyBundledComponent` (`DependenciesYml.cs`), parsed by
  `CompanionEntries`/`CompanionEntry` (glob, `builtin` expanded in place, `{ match, versions | bundled }`; keys channel
  `^\d+\.\d+$` or band `^\d+\.\d+\.\dxx$`, band and `bundled` dotnet-only; ranges through `NpmRange.TryParse`). Built-ins
  (`DependencyCompanions.cs`) gain `Microsoft.DotNet.ILCompiler` and bundled `Microsoft.Build*`, `Microsoft.CodeAnalysis*`,
  `Microsoft.Net.Compilers.Toolset`, `NuGet.*`. Until M2 the engine treats only a channel-rule first match as a companion
  (`DependencyPolicyConfig.CompanionEntry`, `FindCompanions`), so the bundled built-ins change no plan yet. Red:
  `CompanionEntriesYmlTests` did not compile (types absent) → green 15/15; with `DependenciesYmlTests`,
  `DependencyOnboardingTests` and all policy tests 339/339.
- **M2 done** (2026-11-15). `DependencyPolicyEngine`:
  - `FindCompanions` records each ranged record (`Ranged`: entry, key channel, repository SDK, current range).
    - The key channel is the lowest of the project's TFMs, else the repository's.
    - The repository SDK (S4, `RepositorySdk`) is `global.json`'s version, else the lowest exact setup-action version, else the
      channel row's `LatestSdk`.
  - `RangeOf` tries the band key (`Band`: `11.0.105` → `11.0.1xx`) before the channel key. For `bundled`, the range is
    `<=major.minor` of `DependencyPolicyInput.SdkComponents` (filled in M3; an SDK absent there gives no range, and the
    record stays ordinary).
  - `ApplyCompanionRanges` (after the platforms) keeps only the in-range candidates for the package groups and lists the
    highest one above as `AboveCompanionRange` under Available.
  - A record below its range aligns with H1's `AlignLagging`.
  - On a move, `PlanMove` recomputes the range for `target.LatestSdk` and the target channel, and the record rides to the
    highest release inside it (`RangeRider`). The record never joins `missing`.
  - Finding: globs match case-insensitively, so `Microsoft.NET.*` would take `Microsoft.Net.Compilers.Toolset` first. The
    bundled built-ins now come first (test `The_compiler_toolset_is_a_roslyn_companion_not_a_channel_rule`).
  - Limit: "the highest release inside the range" is chosen among the lookup's candidates. Renovate reports the newest per
    update bucket, so a range that cuts inside a bucket can find nothing to take. That case is listed, never guessed.
  - Red: `CompanionVersionMapTests` 6 of 10 failed (ceiling, multi-target, below-floor alignment, bundled, move
    recomputes, never holds) → green 15/15 (with S3). Policy + yml + onboarding + preview/fleet classes: 370/370.

## 8. Decision log

| # | Decision / evidence | Source |
|---|---|---|
| V1 | One pull request with H1 (#676). | Owner, 2026-11-15: "Single pull-request" |
| V2 | The most capable design: band and channel keys, ranges as ceiling and target, built-in maps, validation. | Owner, 2026-11-15: "Pick the most advanced capabilities" |
| V3 | Configuration the maps make obsolete is removed outright, no compatibility shim. | Owner, 2026-11-15: "The no-longer-needed parts of the configuration can be removed without problem"; memory: no backward compat in preview |
| V4 | Bundled versions come from the VMR's component sources at tag `v<sdk>`, collected daily into `SdkComponents/<sdk>`; never `src/sdk/eng/Version.Details.xml` (stale). | S1 |
| V5 | No built-in table for .NET 8/9 bands. | S2: both EOL 2026-11-10 |
| V6 | The repository's SDK: `global.json` > lowest exact setup version > channel's newest SDK. | S4 |
| V7 | A multi-target project's channel key is its lowest channel. | It must load on the oldest target (F-V: CS8032, MissingMethod) |
| V8 | A range never holds a platform move; a record without a release in it stays and is listed. | Same as H1 ("it is behind already") |

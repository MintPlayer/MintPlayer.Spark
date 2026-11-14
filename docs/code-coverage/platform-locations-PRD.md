# Platform moves that leave nothing behind: PRD and plan

Status: **implemented 2026-11-14 (M1-M7 and the S4 golden, progress in §8.1), on `fix/versioning-stable-pin` with the
stable pin.** Grilled 2026-11-13 (decisions P1-P11 in §10). Left, after deploy: the Dotnet.Tools issue for its GraphQL
`prerelease: stable` pin, and onboarding Dotnet.Templates.

Scope: the CodeCoverage dependency bot's **platform groups** (`platform:dotnet <channel>`, `platform:node <major>`), which
move a repository from one .NET or Node release to the next (#606 SDK updater, #617 preview to release). Paths are relative
to `apps/CodeCoverage/` unless they start with `docs/`, `.github/` or a repository name. "TS" = `dependency-action/src`.

## 1. Why

On 2026-11-13, MintPlayer.Dotnet.Templates was re-onboarded (#665 rollout). Reading what a .NET 10 → 11 move would do to
it showed that the bot would change one line, the packaging project's `<TargetFramework>`, and leave the template itself
on .NET 10. Its Dockerfile and publish workflow were already one major behind, on 9, and nothing told anyone.

The owner then asked (2026-11-13), verbatim:

> We should at least be able to update
> (1) Dockerfile => Both for .NET target-framework + nodejs version
> (2) .template.config / template.json => for the dotnet updater
> ...
> The other points you mention probably too
>
> Launch a team to investigate, then create a new PRD/Plan file, which may include several spikes.
> Also determine what repositories only got partial updates (like a forgotten dockerfile, ...)

And on the audit's findings: "rc versions are most likely non-intended and wrong" (for Spark, DotnetDesktop.Tools and
SpaServices; for SpaServices the rc packages turned out to be the newest published, §3.1). An earlier standing preference also applies: "I won't be the only one who uses coverage.mintplayer.com, so
forcing everyone to use the global.json is not preferred." The fix cannot be "move everything to `global-json-file`".

The investigation used three read-only agents (code map, .NET audit, Node audit over all 14 non-archived MintPlayer
repositories, every one of which has `.github/workflows/dependency-lookup.yml`), plus direct reads. Section 2 is the code,
section 3 the repositories.

## 2. How a platform move works today, and where it drops locations

### 2.1 The flow (file:line, read 2026-11-13 at master `7c6e7f33d` + #665)

1. **Lookup.** `scanLocations` (TS/platform/locations.ts:459-503) routes files to scanners. `buildPlatformRecord`
   (TS/platform/feeds.ts:67-70) takes the *editable* locations (non-computed, channel known, not `package-version` or
   `dotnet-quality`) and sets **`fromChannel` = the highest channel among them**. The record's `locations` carries every
   location found, computed ones included.
2. **Fold (R31).** `foldPlatformLocations` (TS/lookup/foldPlatform.ts:62-87) drops a Renovate record that is also a
   platform location **only when it is on the highest channel** (:71). A lagging `FROM node:20` stays its own Renovate
   docker record.
3. **Policy.** `DependencyPolicyEngine.EvaluatePlatform` / `EvaluatePlatformMove`
   (CodeCoverage/Dependencies/Policy/DependencyPolicyEngine.cs:505, :528). Computed locations become
   `DependencyPlatformReport`s (:530-532, DependencyPolicyModels.cs:193), stored in `DependencyRunReport.PlatformReports`.
4. **Apply.** `applyPlatform` (TS/platform/applyPlatform.ts:191-229). **Line 199 skips every non-TFM location whose
   channel is not `fromChannel`, without reporting it.** A TFM on another channel is skipped inside `nextTfmList` (:38-41),
   also without a report. Computed locations go to `reported`.
5. **Trusted writer.** `DependencyPatchValidator` (CodeCoverage.GitWriter/Jobs/Dependencies/DependencyPatchValidator.cs:86-105)
   admits a platform edit only in a file the lookup reported, and `PlatformSubstitution` (PlatformSubstitution.cs:37-83)
   only as a same-line one-token substitution whose new token **contains** `toChannel`, `toVersion` or `sdkVersion` (:75-79).
   `DependencyFiles.IsDenied` (DependencyFiles.cs:84-99) refuses every `*.props`/`*.targets` except
   `Directory.Packages.props`. `Directory.Build.props/.targets` are open to substitutions only (`IsSubstitutionOnly`, :109).
   `IsWorkflowFile` (:143-149) admits `.github/workflows/*.yml` and **any `action.yml`**, so composite actions are covered.
6. **Pull request body.** `DependencyPullRequestBody.cs` renders platform warnings (:106, :135) and failures (:79-80).
   **Neither `PlatformReports` nor the apply's `platform.Reported` (`ApplyPlatformResult`,
   CodeCoverage.Git/Dependencies/DependencyContracts.cs:430-441) is rendered anywhere.** Even the locations the code means
   to report, it reports to nobody. `docs/code-coverage/dependency-updates.md:298` ("a computed TFM is reported") is
   therefore untrue in effect. (Spike S1 confirms.)

### 2.2 What the scanner sees

| Location | Scanned? | Where |
|---|---|---|
| `<TargetFramework(s)>` in `*.csproj`/`*.fsproj`/`*.vbproj`, `Directory.Build.props` | yes | locations.ts:467 |
| the same in `*.csproj.in`, `eng/Versions.props`, any other `*.props`/`*.targets` | **no** | (routing) |
| `$(Prop)` / `${Prop}` TFM | reported as computed, never resolved | locations.ts:103 |
| `<Version>`, `<VersionPrefix>` | yes (`lockPackageMajor` only) | locations.ts:173 |
| `<PackageVersion>10.0.0</PackageVersion>` (the package's own) | **no** | locations.ts:165, :173 |
| `global.json` `sdk.version`, Arcade `tools.dotnet` | yes | locations.ts:470-475 |
| Dockerfile `FROM mcr.microsoft.com/dotnet/*:<tag>`, `FROM node:<tag>` | yes, **on the highest channel only** | locations.ts:208-225, applyPlatform.ts:199 |
| Dockerfile `ARG NODE_VERSION=22` + `FROM node:${NODE_VERSION}` | computed, not resolved | locations.ts:216 |
| Dockerfile `RUN curl …/setup_24.x \| bash` (NodeSource) | **no** | |
| workflow / `action.yml` `setup-dotnet` / `setup-node` single value | yes | locations.ts:293-345 |
| the same as a multi-line block `dotnet-version: \|` | **computed: never moved** | locations.ts:325 (`raw === '\|'`) |
| matrix entries feeding a setup step | yes | locations.ts:253-287 |
| `action.yml` `runs.using: node20` | **no** | |
| `.template.config/template.json` | **no** | |
| `.config/dotnet-tools.json` | **no** (a NuGet tool, not the platform) | |

The .NET audit stated that composite actions (`.github/actions/*/action.yml`) are not scanned. **That is wrong:** both
`isWorkflowFile` (TS/util/repoFiles.ts:78) and the writer's `IsWorkflowFile` (DependencyFiles.cs:148) take any
`action.yml`. Spark's composite action still pins rc.2 for the reason in §3.3, not because it is invisible.

## 3. Repositories with partial updates (audit, 2026-11-13, default branches)

Full tables: the agents' notes are not committed. The rows below are the ones that matter, re-checked where marked.

### 3.1 Partial today

| Repository | What is behind | Why the bot did not or will not move it |
|---|---|---|
| **MintPlayer.Dotnet.Templates** (major 10) | `content/WebApplication/Web/Dockerfile:10,26` `sdk:9.0`/`aspnet:9.0`; `.github/workflows/publish-master.yml:27` `dotnet-version: 9.0.101`, `:22` `node-version: 20` (Node 20 EOL 2026-04-30; the template's own Dockerfile and `@types/node` are 22) | Older channel than the repository (§2.1 step 4). The generated app cannot build in Docker today (NETSDK1045). **Filed as MintPlayer/MintPlayer.Dotnet.Templates#4** (with the non-bot remarks: package version never changes, no PR validation, pinned `x.0.0` framework packages, committed `.claude/settings.local.json`). |
| **MintPlayer.Spark** (major 11) | `global.json:3`, `.github/actions/setup/action.yml:18`, `.github/workflows/dotnet-build-master.yml:69,357` on `11.0.100-rc.2.26504.105`; 12 framework references (`Microsoft.AspNetCore.Mvc.Testing` ×8, JwtBearer, TestHost, Authentication.*) on `11.0.0-rc.1.26425.128` | All scanned kinds and #617 (#619) is merged, yet Spark has **never received an update PR**: its lookups succeed (2026-11-13 08:00, 08:55, 18:45) and nothing follows. Spike S2. Owner: unintended. |
| **MintPlayer.DotnetDesktop.Tools** (major 11) | `global.json:3` and `.github/workflows/_build.yml:55-57` on `11.0.100-rc.1.26425.128` | Its only lookup (2026-11-09, run 42655969292) failed on a server bug since fixed (empty channel, S3). The next one is the weekly Monday run. And `_build.yml` lists two SDKs in a `dotnet-version: \|` block, which the scanner treats as computed, so the rc.1 line could never move anyway. Spike S3. Owner: unintended. |
| **MintPlayer.AspNetCore.SpaServices** | Node: three build workflows pin `'22'` while the test matrix is `['20','24']`, demo `@types/node ^20` | (Its `MintPlayer.Pagination 11.0.0-rc.1` and `MintPlayer.Assertions 11.0.0-rc.6` references are **not** behind: they are the newest versions on nuget.org, checked 2026-11-13. They wait for Dotnet.Tools to release 11.0.0.) Node: the platform is read as 24 (the matrix maximum), and SpaServices #126 proposes `platform:node 26`; it would move the matrix and **leave the three `'22'` pins**, a partial update by construction. |
| **MintPlayer.Polyglot** | `release.yml:105,186` and the `ci.yml:142` matrix on Node 22, `publish-vscode.yml:63` on 24 | Two channels in one repository; a move from 24 leaves 22. Whether 22 is deliberate is a question for that repository. |
| **karnaugh-map-app** | `ci.yml:31` Node 20 and `@types/node 20.19.9`, while `apps/shop/Dockerfile:2,19` is on `node:22-alpine` | Older channel. (`apps/api/Dockerfile:7` `lts-alpine` and `Dockerfile:4` `latest` float: nothing to move.) |
| **github-actions** | `cherry-pick`, `delay`, `get-gpr-version`, `get-npmjs-version` `action.yml` `runs.using: node16`; `publish-npm-packages` `node20`; `compile-ts-action` input default `'20.x'`; repository CI on 24.x | `runs.using` is not scanned. node16 is no longer available on GitHub's runners. |

### 3.2 Latent: consistent today, partial on the next move

- **MintPlayer.Spark (Node):** five NodeSource `setup_24.x` lines in `apps/{CodeCoverage,DemoApp,SeoTester,MintPlayer}/…/Dockerfile`
  and three `runs.using: node24` in the bot's own actions. A move to Node 26 changes setup-node and leaves all eight.
- **mintplayer-ng-bootstrap, ng-seo, ng-video-player:** consistent on Node 24. `@types/node` lags (^22, and ^14 in the
  snippets library), which the bot reports by design (#606 R7), so today nobody sees it.
- **MintPlayer.AI:** consistent on .NET 10 and Node 22. Its `10.0.x` wildcard pins are a shape the move must keep
  (`11.0.x`); already tested (sdk-updates-PRD §12).

### 3.3 Not partial (checked)

- **MintPlayer.Dotnet.Tools:** five workflows on the rc.1 SDK, but **Dotnet.Tools #208 already plans them** as
  `dotnet-sdk` records (`11.0.100-rc.1.26425.128` → `11.0.100`, rows 10-14 of its body). About a dozen `net11.0` projects
  reference `Microsoft.Extensions.*`/`Microsoft.AspNetCore.*` at `10.0.5`. The owner suggested these are source-generator
  packages targeting netstandard. Re-checked 2026-11-13: they are not. They are executables (SlnLaunch, Solve, the
  `TestProjects`), test projects and `Beid/DemoWebApp`, all `net11.0`. They are the Microsoft 11.0.0 NuGet groups #208
  carries, whose apply failed on NETSDK1045 before #664 installed the SDK. They are ordinary dependency updates, already in
  flight, not a platform-move gap. `FolderHasher.Targets.csproj:42-50` keeps 10.0.5 on purpose (commented).
- **AspNetCore.Tools, Dagucar (MAUI, net9.0 throughout), ng-bootstrap (.NET 10 `apps/api`, `10.0-alpine` images):**
  consistent.
- **Bot history:** apart from SpaServices, no repository has had a bot platform PR. In SpaServices, #121 moved
  `global.json` and missed Arcade `tools.dotnet`, which #662/#123 fixed. That is the only past partial *bot* update.

## 4. Requirements

Owner-mandated (2026-11-13):

- **R1. Dockerfiles follow both platforms.** A .NET move moves every `mcr.microsoft.com/dotnet/*` stage, and a Node move
  every Node stage, including stages on an older channel than the repository (owner: "Both for .NET target-framework +
  nodejs version"; the Templates Dockerfile is on 9 under a 10 repository).
- **R2. `template.json` follows the .NET move.** `.template.config/template.json` framework values move with the TFM.

"The other points you mention probably too" (owner, 2026-11-13). Each still needs a decision in §9:

- **R3. Nothing is skipped silently.** Every location the move does not edit is listed in the pull request, with its file,
  value and reason. That includes computed locations, older channels and ranges.
- **R4. Computed TFMs are followed to their definition** when the chain is plain text in the repository (`*.csproj.in` →
  `GeneratedContentProperties` → `eng/Versions.props`), and the defining line moves.
- **R5. The package's own `<PackageVersion>`** is a `package-version` location like `<Version>`.
- **R6. Locations on an older channel** move with the repository (or are reported, per Q1). This is R1's rule applied to
  every location kind: workflows, matrices, `action.yml`.
- **R7. Multi-line setup inputs** (`dotnet-version: |` with one version per line) are scanned per line.
- **R8. Node runtimes the bot cannot see today:** `action.yml` `runs.using: nodeNN`, NodeSource `setup_NN.x` in
  Dockerfiles, `ARG` defaults that feed a `FROM`.
- **R9. The two stuck repositories are unstuck** (Spark and DotnetDesktop.Tools never got their rc → GA move: §3.1).

Out of scope: free text such as cache keys (`node24-…`) and README snippets; `dotnet-tools.json` (a NuGet tool version,
not the platform: a separate issue if wanted); `@types/node` stays reported only (#606 R7), but R3 makes the report visible.

## 5. Design

Constraints carried over: the scanner stays line/token based and the apply edits only what the lookup reported
(locations.ts:7-10); computed locations are reported, never guessed (#606 R8); the writer admits only one-token same-line
substitutions in reported files; one pull request (repository rule 7).

### 5.1 Render what is reported (R3), first

Render `DependencyRunReport.PlatformReports` and the apply's `platform.Reported` as one "Not moved" table in the pull
request body: `file:line | value | reason`. Without this, every "report instead" option below is invisible. Lagging
locations need their own reason: "on 9.0, older than the repository's 10.0".

### 5.2 Older channels (R1, R6): who decides what lags

Today `fromChannel` is one channel per SDK and everything else is "not ours" (applyPlatform.ts:198 comment: "a net48 test
project, an older Docker stage"). No decision record says lagging locations must stay; it is the R7 design by construction.

Options:
- **A. Follow.** A location on an older channel than `fromChannel` moves to `toChannel` too. Its value is rewritten the
  same way (`rewriteVersionValue`, channels.ts:63-80): `sdk:9.0` → `sdk:11.0`, `node:20-alpine` → `node:24-alpine`.
- **B. Report.** As today, but listed (5.1).
- **C. Per SDK and kind:** follow for Docker, setup actions, version files and `action.yml`, report for TFMs and matrices.
  A TFM on an older channel is usually deliberate (multi-targeting, a `net8.0` tool with `RollForward=Major` as in Spark's
  versioning CLI, `net48`). A matrix's older entries are deliberate too (#606 M6, applyPlatform.ts:159).

**Recommendation: C**, with a per-SDK setting to override it (`dependencies.platforms.<sdk>.lagging: follow | report`),
default as in C. It satisfies R1 without bumping the deliberate older TFMs and matrix entries. The fold (foldPlatform.ts:71)
must then also absorb Renovate records on lower channels that the platform group moves, or both edit the same line.

What can go wrong with C: an older stage kept on purpose (a build stage pinned to an older toolchain) is bumped across
majors, and a target tag may not exist (`sdk:11.0-alpine3.19`). Mitigation: the per-repository override, and spike S5
checks tag existence against the registry before planning.

### 5.3 `template.json` (R2)

New scanner for `**/.template.config/template.json`. Inside a `symbols` entry whose `datatype` is `choice`, a value
matching `^net\d+\.\d+$` under `choice`, `replaces` and `defaultValue`, plus the same token inside that choice's
`description`, becomes a `tfm` location (no element rename).

- `replaces` and `defaultValue`: moved like a TFM.
- `choices`: the question is whether the template should offer **only** the new framework (replace the token) or **both**
  (append a choice: a structural JSON edit the writer does not admit today). Recommendation: replace, consistent with the
  default `strategy: replace`. `strategy: add` reports "add the `net11.0` choice by hand".
- `replaces` must equal the TFM in the template's generated or source project files, or `--Framework` silently stops doing
  anything. With R4, both move in one pull request. Without R4 (a template whose project files are plain `.csproj`),
  they're already scanned.

### 5.4 Computed TFM chains (R4)

MintPlayer.Dotnet.Templates, read 2026-11-13:
- `src/MintPlayer.Dotnet.Templates/*.csproj.in`: `<TargetFramework>${DefaultNetCoreTargetFramework}</TargetFramework>`,
  and framework packages at `${DefaultNetCoreTargetFrameworkVersion}.0`.
- `MintPlayer.Dotnet.Templates.csproj:35-44`: `GeneratedContentProperties` passes `DefaultNetCoreTargetFramework=$(DefaultNetCoreTargetFramework)`.
  Arcade's `GenerateFileFromTemplate` (`eng/GenerateContent.targets:27-34`) substitutes the `${…}` placeholders.
- `eng/Versions.props:3-6`: `AspNetCoreMajorVersion` 10, `AspNetCoreMinorVersion` 0,
  `DefaultNetCoreTargetFramework` = `net$(AspNetCoreMajorMinorVersion)`.
- `src/Directory.Build.props:20` imports `eng/Versions.props` explicitly.

Design: when a TFM value is `$(X)` or `${X}`, look up X's single definition across the repository's `*.props`/`*.targets`
(no import evaluation, as #663's `propertyFiles` decided; nuget.ts:84). Substitute recursively, up to a small depth with a
cycle guard. Refuse if a property has more than one definition, or a `Condition` on it or its `PropertyGroup`. Emit the
**defining** location as a new kind `tfm-property` with the evaluated channel (`net10.0` → 10.0). Move only the token that
carries the major (`10` → `11`); report a non-zero minor.

Writer: admit a substitution in a lookup-reported `*.props`/`*.targets` (extend `IsSubstitutionOnly` to exactly the
reported files), and let `NamesTarget` accept a bare major for `tfm-property` (`11` does not contain `11.0` today).
A security review of the widened file set is part of the milestone.

Interaction: the same property can feed package versions (`${DefaultNetCoreTargetFrameworkVersion}.0`). Renovate does not
see `.csproj.in` files, so no double edit. But a Renovate `sharedVariableName` record on the same property, in a normal
csproj, would also edit it (#663). The fold must drop it.

Alternative if the writer change is refused: report the chain precisely ("computed via `eng/Versions.props`
`AspNetCoreMajorVersion` = 10"), with no edit.

### 5.5 `<PackageVersion>` (R5)

In `scanPackageVersion`, accept `<PackageVersion>` when the tag has no attributes. The CPM form
`<PackageVersion Include=… Version=…>` stays a dependency. The edit stays behind `lockPackageMajor` (applyPlatform.ts:141-147).
Tag-versioned repositories (`0.0.0-PLACEHOLDER`) are unaffected.

### 5.6 Multi-line setup inputs (R7)

`dotnet-version: |` followed by one version per line: each line becomes its own `setup-action` location, with its own
channel. That's how DotnetDesktop.Tools' `10.0.x` (deliberate) and `11.0.100-rc.1…` (stale) separate. The writer's
same-line substitution rule already fits.

### 5.7 Node runtimes (R8)

- `action.yml` `runs.using: nodeNN`: kind `action-runtime`, channel NN. Moved only to a runtime GitHub supports (node20,
  node24 in 2026-11; spike S6 for the list and where it comes from). Never to a major GitHub does not offer: then reported.
- NodeSource `setup_NN.x` in a Dockerfile `RUN`: kind `docker` with channel NN, token `NN` inside `setup_NN.x`.
- `ARG NODE_VERSION=22` used by a `FROM node:${NODE_VERSION}` in the same file: the ARG's default is the location.

## 6. What this changes per repository (expected, after implementation)

| Repository | Next move would now also change |
|---|---|
| Dotnet.Templates | Dockerfile (both stages, even from 9), `publish-master.yml` SDK and Node, `eng/Versions.props` `AspNetCoreMajorVersion`, `template.json` |
| Spark | five NodeSource lines, three `runs.using` (on a Node move); unstuck rc.2 → GA (R9) |
| DotnetDesktop.Tools | the rc.1 line of `_build.yml`, `global.json` (after S3) |
| SpaServices, Polyglot, karnaugh-map-app | the lagging Node pins (`'22'`, 20) |
| github-actions | `runs.using: node16/node20` to a supported runtime |

## 7. Spikes (before the milestones)

- **S1. Reports reach nobody.** Render a pull request body in a test from a run with one computed TFM and one older-channel
  Dockerfile, and show neither appears. That gives the red test for M1.
- **S2. Why Spark gets no update PR.** Its lookups succeed and #619 is merged, yet nothing is planned for `global.json`
  rc.2 → 11.0.100 or the rc.1 references. Read the latest lookup's artifact (platform record, `current.channels`,
  candidates) and the policy's decision for it. Suspects: `versioning.lines`/release-line handling on master, the track,
  the schedule.
- **S3. The 2026-11-09 lookup failures. Mostly answered 2026-11-13.** Nine repositories (ng-bootstrap, ng-video-player,
  ng-seo, DotnetDesktop.Tools, Dagucar, github-actions, karnaugh-map-app, Dotnet.Templates, Polyglot) last ran a lookup on
  2026-11-09 between 16:03 and 16:44. Every one failed on `platforms.sdks.dotnet[0].channel must be a channel such as
  10.0 or 24`. The server had sent `"channel":""` (karnaugh run 42657726841, action pin `5c99269`). It's fixed: AI's run
  43493439334 on 2026-11-13 received `"channel":"10.0"`. They have not run since because the schedule is weekly on Monday
  (dependency-updates.md:138); 2026-11-09 was the onboarding Monday. Left for the spike: find the fixing commit for the
  record. Decide whether a lookup that fails on server input should be retried, not left until the next week.
- **S4. The Templates chain end to end. Answered 2026-11-14 (§8.1): the golden found two defects, both fixed.** Copy the repository's `eng/Versions.props`, `*.csproj.in`,
  `MintPlayer.Dotnet.Templates.csproj`, `template.json` and Dockerfile into
  `dependency-action/test/fixtures/platform/dotnet-templates/`. Run `scanLocations` on it, and write the expected locations
  of §6 as the golden for M3-M5.
- **S5. Docker tags that exist.** Can the lookup check that `mcr.microsoft.com/dotnet/sdk:<to>-<suffix>` and
  `node:<to>-<suffix>` exist (registry API, anonymous) before planning a follow (5.2)? Cost and rate limits.
- **S6. `runs.using` values.** Where to read the runtimes GitHub currently supports, or whether a fixed table in the
  feed is enough (the SDK table already exists: sdk-updates-PRD §3.6).

## 8. Milestones (one pull request, in this order)

1. **M1:** render "Not moved" in the pull request body (5.1), plus the same-channel hold (P11). Red test from S1.
2. **M2:** older-channel handling with the per-SDK `lagging` setting and the fold change (5.2, P6), and the registry tag
   check (P7). Amend `platform.test.ts:203` (the `net9.0` project stays: a TFM, so `report`).
3. **M3:** multi-line setup inputs (5.6) and `<PackageVersion>` (5.5).
4. **M4:** TFM property chains plus the writer change and its security review (5.4, P9).
5. **M5:** `template.json` (5.3, P8). Comes after M4: it moves only together with its projects' TFM.
6. **M6:** Node runtimes: `runs.using` (P10), NodeSource, `ARG` (5.7).
7. **M7:** whatever S2 and S3 find (R9). If it's a server or dispatch bug it belongs in this pull request too.
8. Docs: `docs/code-coverage/dependency-updates.md` §platform (what moves, what is reported, the `lagging` setting) and
   `sdk-updates-PRD.md` (a §13 pointing here). The ncc `dist` is rebuilt in the main checkout only (repository CLAUDE.md).

Tests (red first, per milestone): TS `platform.test.ts`, `foldPlatform.test.ts`, `sharedPropertyAndPackageManager.test.ts`
(model for the chain), the S4 golden; C# `DependencyPlatformPatchTests` (writer: reported `.props`, bare major,
`template.json`, `action.yml` runtime), `PlatformRecordPolicyTests`, a body-rendering test. A new location kind needs
`contracts/dependency-update/platform-record.schema.json` and `types.ts:101` (`PlatformLocationKind`).

### 8.1 Progress

Branch `fix/versioning-stable-pin`: one pull request with the stable pin
([versioning-beta-upstream-PRD](versioning-beta-upstream-PRD.md), committed first). Owner, 2026-11-14: "Don't you want to
include the remaining work in this same pull-request?"

- **S1 (by reading, 2026-11-14):** `DependencyRunReport.PlatformReports` was written (DependencyLifecycleRules.cs:46) and
  never read by `DependencyPullRequestBody`. The apply's `platform.reported` (`ApplyGroupResult.Platform`) was read by
  nothing in the app at all. The M1 body test was written together with the fix, so it was not run red first. The
  grep above is the evidence for the gap.
- **M1 (implemented):**
  - `## Not moved` table (`Platform | File | Value | Why`, de-duplicated rows);
  - `DependencyRunReport.ApplyPlatformReports` merges the apply's reports in `DependencyUpdateEngine` after the apply;
  - P11 in `DependencyPolicyEngine.PlanMove`: a computed location whose channel is the current one, and which is in
    `locations`/`exclude` scope (`InPlatformScope`, the same glob rule as applyPlatform.ts:27-31), sends the move to
    awaiting approval. Its reason names `platforms.<sdk>.exclude`.
  - A digest-pinned image is "computed" but planned as its own docker record (locations.ts:220,242), so it does not
    hold. Found by `A_platform_minor_line_change_may_auto_merge` (S12 python fixture), which went red on the first draft.
  - **Bug found on the way:** `DependencyHolds.Approve` bound every approval to the target major (`dotnet@10`). The
    policy binds an in-channel platform move to the exact version (`dotnet@10.0.105`, #617), so approving such a move
    never took effect. Unseen until now because no in-channel platform move waited for approval. Fixed with
    `DependencyHolds.ApprovalTarget`. Red: the P11 test's "approved → planned" step found 0 groups.
  - A computed location without a channel (today's `$(Prop)` TFMs) does not hold: its channel is unknown until M4
    evaluates the chain.
  - Tests: `DependencyPullRequestBodyTests.Platform_locations_that_were_not_moved_are_listed_with_their_reason`,
    `The_apply_s_skipped_platform_locations_are_reported_under_their_sdk`,
    `PlatformRecordPolicyTests.A_same_channel_location_the_bot_cannot_edit_holds_the_move_for_approval`. All three were
    written with the fix, not run red first.
- **M2 (implemented, 2026-11-14):**
  - `dependencies.platforms.<sdk>.lagging: auto | follow | report` (DependenciesYml.cs; invalid values are an error).
    `auto` is written to the plan as nothing (`PlanPlatform.Lagging` null), so an older action never sees the key;
    `plan.schema.json` and `types.ts` take `follow | report`.
  - applyPlatform.ts: a location on an older channel than `fromChannel` follows the move when `lagging` is `follow`, or
    under `auto` when its kind is docker, setup-action, version-file or tool-versions (`FOLLOWS_WHEN_AUTO`). It is
    rewritten from its own channel. A TFM or matrix entry never follows (P6), an in-channel move never takes older
    locations along, and everything that does not follow is reported ("on 9.0, older than the repository's 10.0").
  - foldPlatform.ts: a following older location keeps only its own channel's Renovate candidates; its channel move is
    the platform group's now. A non-following kind keeps the record whole. The lookup does not see `lagging`, so this is
    the `auto` split; under `report` the stage is listed as not moved and its patches stay Renovate's. Two existing
    expectations changed with it, both because the location now follows: `foldPlatform.test.ts` "S12" (the workflow
    `container: node:18.19.0` keeps 18.20.8, loses 26.12.0) and the "below the current channel" case.
  - **P7 deviation:** the tag check runs at apply time, not "before planning". The apply is where the new tag is
    computed (suffix kept, `-alpine3.18` and all), and the lookup would have to predict it. A missing tag is reported,
    not written; the rest of the move goes ahead. The cache is per apply run (one HEAD per `image:tag`).
  - **S5 (answered):** `platform/registry.ts` does an anonymous OCI `HEAD /v2/<repo>/manifests/<tag>`. On a 401 it reads
    the Bearer challenge (realm, service, scope), gets an anonymous token, and retries once. Docker Hub images are
    `library/<name>` on `registry-1.docker.io`. A HEAD does not count against Docker Hub's pull limit. Unreachable,
    private, or any other status → null → reported as "could not check". Live check, 2026-11-14:
    `node:22-alpine` true, `node:22-alpine-does-not-exist` false, `mcr.microsoft.com/dotnet/sdk:10.0` true (no token),
    `…:10.0-nope` false, `ghcr.io/renovatebot/renovate:latest` true (token), `python:3.13-slim` true, about 1 s in
    total. Q5 is moot: the check is cheap.
  - Tests:
    - red → green: `platform.test.ts` "older channels (P6, P7)" (4 red / 54 passed → 72/72);
    - `registry.test.ts` (4 red: not a function → green);
    - `foldPlatform.test.ts` (the S12 case went red on the fold change, and its expectation was updated as above);
    - with the change: `DependenciesYmlTests` (`lagging` read, `lagging: always` refused) and `PlatformRecordPolicyTests`
      (`Lagging` null for auto, `report` carried).
- **M3 (implemented, 2026-11-14):**
  - R7: a literal block (`dotnet-version: |` or `|-`, unquoted) gives one `setup-action` location per deeper-indented
    line (locations.ts `scanSetupAction`), with the line's own channel and token span. An empty block, or a folded `>`,
    stays the one computed location it was.
  - **Decision (implementation, follows P6's reasoning):** a listed SDK is `Found.listed`. Under `lagging: auto` an
    older listed SDK does not follow a channel move (`followsWhenAuto`, shared by the apply and the fold). Two SDKs
    installed side by side are deliberate, like a matrix: DotnetDesktop.Tools' `10.0.x` next to its 11.0 line would
    otherwise become a second `12.0.x` on the next move. `lagging: follow` still takes it along.
  - R5: `<PackageVersion>x</PackageVersion>` without attributes is a `package-version` location. The CPM
    `<PackageVersion Include=… Version=…/>` stays a dependency. It is still edited only under `lockPackageMajor`.
  - Writer: no change. `PlatformSubstitution` is file-kind agnostic: one reported value → one token naming the target.
  - Tests: `platform.test.ts` "M3", 4 red (scan, in-channel move, auto with a listed older SDK, `<PackageVersion>`) →
    green. The platform, lookup and orchestrator suites stay green.
  - **Writer gap found in M4's writer tests:** the YAML comparison (`GitHubWorkflowValidator.Compare`) refused every
    changed block scalar ("a changed value whose scalar style changed or is a block scalar"). The M3 edit was therefore
    refused at the writer, so the scanner change alone would have produced a failing apply. Fixed in
    `IsPlannedVersionList`: a literal block at a setup step's version input may change only line for line (same count,
    each changed line one version token on both sides, `PlatformChange` explains it, at least one changed). A `run: |`
    script stays refused. Red: `DependencyPlatformPatchTests.One_line_of_a_multi_line_setup_input_moves`
    (invalid-content) → green; `GitHubWorkflowValidator` tests green.
- **M4 (implemented, 2026-11-14):**
  - Scanner (locations.ts):
    - `*.csproj.in` (and fs/vb) get the TFM scan, and `${X}` is computed;
    - every `*.props`/`*.targets` contributes its one-line PropertyGroup definitions (plain text, no import evaluation);
    - `resolveTfmChains` follows a TFM that is exactly `$(X)`/`${X}` through single, unconditioned definitions (depth 8,
      cycle guard) to `netN.N`;
    - the token carrying the major becomes one `tfm-property` location (value `10`, or `10.0` when the minor is in the
      same token), deduplicated across projects; the resolved TFMs leave the list;
    - a chain that does not resolve keeps its TFM computed, with the reason: defined N times, conditioned, not defined,
      not a netN.N TFM, a cycle, the major spread over properties.
  - Apply: a bare major becomes the target major (reported if the target's minor differs, since the minor lives
    elsewhere); a channel becomes the target channel. Under `strategy: add` it is reported. A property never follows a
    lagging move.
  - New kind in `platform-record.schema.json` and `types.ts`. `dotnetSdk.ts` counts it for the SDK major.
  - Writer (P9):
    - `DependencyPatchValidator` opens a deny-listed `*.props`/`*.targets` only for a reported `tfm-property` location,
      in both the path policy and the per-file admission. Other kinds in that file still admit nothing.
    - `PlatformSubstitution.PropertyMove` admits only `<Name>old</Name>` → `<Name>new</Name>`: same name, indentation and
      tail, no attributes. old/new must be the from-major/target-major (same minor on both) or the from-channel/target
      channel (bare or as `netN.N`).
  - **Security review (P9, 2026-11-14).** Threat: the lookup runs in the repository's CI and a pull request can shape
    its locations, so any `*.props` path can be named.
    - What a hostile location gains: one numeric token on a plain property line in an existing `.props`/`.targets`,
      changed from exactly the move's from-major (or channel) to exactly its target.
    - What it cannot add or remove (each a test): no `<Import>`, `<Target>`, `<Exec>` or `Condition`, no second element,
      no expression, no new file. `nuget.config` and scripts stay denied: only `.props`/`.targets` open, and only for
      this kind.
    - **Accepted residue:** the location names no property, so another plain property with the same value moves too
      (`<LangVersion>10</LangVersion>` → 11, test `A_plain_property_of_the_same_value_is_the_accepted_residue`). That
      is a value change with no structure, inside a pull request that a human or the auto-merge threshold still gates.
    - Rejected alternative: carrying the property name in the location (a contract change for one name check).
  - Not done (§5.4 interaction): dropping a Renovate `sharedVariableName` record on the same property. Renovate reads no
    `.csproj.in`, and no record in today's repositories names a TFM property. Left as reported risk, not code.
  - Tests:
    - `platform.test.ts` "M4": 10 red → green (Templates chain, channel form, five refusals, a cycle, strategy add);
    - `DependencyPlatformPatchTests`: 9 red (DeniedPath, and invalid-content for M3) → 53/53 green.
- **M5 (implemented, 2026-11-14):**
  - Scanner (`scanTemplateJson`): in `**/.template.config/template.json`, a `symbols` entry with `"datatype": "choice"`
    that offers a `netN.N` choice is the framework symbol. Each `netN.N` token under its `choice`, `description`,
    `replaces` and `defaultValue` keys is one location of the new kind `template-framework` (not `tfm`, as §5.3 first
    said: a `tfm` would follow the TFM rules, the element rename of `add` and `olderTfmChannel` among them). Symbols are
    found by brace depth outside strings; other symbols and the symbol's own description without a TFM are left alone.
  - Apply (P8): replace by default (`net10.0` → `net11.0`). Under `strategy: add`, `replaces` and `defaultValue` move,
    and the `choice` and its description are reported: "strategy add: add the net11.0 choice by hand".
  - **"Its own projects" (P8), as implemented:** the project files under the folder that holds `.template.config`. The
    template moves only when a TFM in one of them moves in the same apply, or when a `tfm-property` that one of them
    resolves through moves (`Found.readers`, filled by `resolveTfmChains`, is the Dotnet.Templates shape: the TFM is in
    `content/*/*.csproj.in`, the token in `eng/Versions.props`). Otherwise every token is reported: "a template
    framework moves only with a TFM this move changes, and none changes". Template locations are therefore applied
    after the loop over every other location.
  - A template framework never follows a lagging move and never counts as an in-channel change.
  - Writer: `PlatformSubstitution` already admits one token per line. **Gap found by the writer test:** a choice and its
    description on one line (`{ "choice": "net10.0", "description": "Target net10.0" }`) change two tokens on the line,
    which the one-swap rule refused. `EveryOccurrence` admits a line whose every occurrence of the reported value is
    replaced by the same target token, the text between them byte-identical. A second added choice (structure) stays
    refused. The path is not deny-listed, so no path change was needed.
  - Tests: `platform.test.ts` "M5" (5; the first 4 red → green, the readers case written after); writer
    `The_template_framework_tokens_move` (red: "Line 6: … not a planned platform move" → green) and
    `A_second_choice_added_to_the_template_is_refused`; 50/50 in the patch suites.
- **M6 (implemented, 2026-11-14):**
  - **S6 (answered):** GitHub publishes no feed of the `runs.using` runtimes; they are in the runner's source and the
    metadata-syntax docs. So the table is a constant in the bot, `DependencyPolicyEngine.GitHubActionRuntimes`
    (`node20`, `node24`), sent with every node move as the plan's `actionRuntimes` (`plan.schema.json`, `types.ts`,
    `PlanPlatform.ActionRuntimes`). That honours P10's "a table in the bot's feed" in the one sense available: changing
    it is a bot deploy, not an action release. A dotnet (or any non-node) move carries none.
  - Scanner:
    - `action.yml`/`action.yaml` `runs.using: nodeNN` under `runs:` is the new kind `action-runtime` (value `node20`,
      channel `20`). One under `.github/` is found and reported ("under .github/, which the bot's pull requests never
      change"): the writer refuses every non-workflow file there, so writing it would fail the whole patch. It is not
      `computed`, so it does not hold the move under P11.
    - NodeSource: `deb.nodesource.com/setup_NN.x` on any Dockerfile line (a `RUN` or its continuation) is a node
      `docker` location, token `NN`.
    - `ARG X=22` read by `FROM image:${X}` (or `$X`, with a suffix such as `-alpine`): when X has one unquoted default
      in the file, that default is one `docker` location, and the FROM is no longer reported as computed. Two
      definitions, or none, keep the FROM computed. `Found.tagSuffix` keeps the FROM's suffix, so P7 checks the real tag
      (`node:24-alpine`).
  - Apply (P10): `runs.using` moves to the highest listed runtime not above the target, from any older channel (it
    follows under `auto`, like a Docker stage; `lagging: report` still lists it). When the target is above every listed
    runtime it moves as far as it can and reports "GitHub runs node24 at most for actions; node26 is not offered yet".
    Without the table it is reported, never guessed.
  - Writer: `PlatformSubstitution.RuntimeMove` admits one occurrence of the reported runtime replaced by a runtime in
    the plan's `actionRuntimes`, nothing else on the line. `NamesTarget` is not used: node24 is right for a Node 26 move.
    `GitHubWorkflowValidator` (action.yml goes through the workflow YAML check) admits `runs.using` `nodeNN` → `nodeNN`
    when the platform move explains it.
  - Tests:
    - `platform.test.ts` "M6": 6 red → green (scan + `.github/`, follow from node16/node20, the cap report, no table,
      NodeSource, ARG with the `-alpine` tag check); the seventh (ARG twice/bare stays computed) passed from the start,
      as it pins the old behaviour;
    - writer: `An_action_runtime_moves_to_a_runtime_GitHub_runs_below_the_target` red ("fails github-workflow at line
      3 … a change other than a planned uses: or setup-action version value") → green; `node26` (not in the list),
      an added `pre:` and a plan without the list are refused; 54/54;
    - policy: `PlatformRecordPolicyTests` node move carries the list (and writes a valid plan), a dotnet move does not:
      written with the code (compile-red only); 49/49 with `DependenciesYmlTests`. `GitHubWorkflowValidator` tests 33/33.
- **S2 (answered 2026-11-14, production `DependencyUpdateRuns`, read-only query):** Spark's run 47 ended
  `patch-rejected`: "The apply patch was refused (unsupported-mode): Line 463: mode 100755". Every Spark run since
  onboarding plans and applies; nothing reaches a pull request because the writer refuses the whole patch. Apply run
  43520577835 (`dependency-apply-1006469943-47-2`):
  - 81 groups: 38 applied, 41 unresolvable, 2 failed; a 1.9 MB patch over 114 files, 89 of them under
    `apps/CodeCoverage/CodeCoverage.GitWriter.Tests/Fixtures/` and 21 under `dependency-action/test/fixtures/`.
  - **Cause 1, fixtures are scanned as the repository's own projects.** Renovate and the platform scanner both read the
    test fixtures: `platform:rust 1.99`, `platform:go 1.27`, `platform:php 8.5` and `platform:python 3.15` were planned
    and applied on fixture files only, and `platform:dotnet 11.0` edited a fixture `global.json`. Installing the yarn
    fixture created `…/Fixtures/DependencyApply/yarn/base/.pnp.cjs` (`new file mode 100755`, patch line 463).
  - **Cause 2, one bad group sinks every group.** The writer refuses the patch as a whole (new file, mode), so the 38
    good groups never ship either.
  - **Cause 3, the .NET groups cannot restore.** Every NuGet group, `platform:dotnet 11.0` included, is unresolvable on
    `NU1301 … nuget.pkg.github.com/MintPlayer … 401 (Unauthorized)`: the apply has no GitHub Packages credentials for
    the `nuget.config` source.
  - Not Spark: MintPlayer.AI (1266490237) run 102 ended `patch-rejected (group-mismatch)`: the patch applies
    `@babel/core` and `vite`, which `result.json` does not claim (lockfile-only side effects of other groups).
  - S3 state, same query: Polyglot, ng-seo, github-actions, Dagucar and ng-bootstrap are still at run 1
    `lookup-failed` from 2026-11-09 (the weekly schedule has not come round); Dotnet.Tools' two line runs are
    `workflow-differs`.
- **M7 (2026-11-14), the S2 fixes, one per cause:**
  - **Cause 1:** `dependencies.exclude` now reaches platforms. `DependencyPolicyEngine.EvaluatePlatformMove` drops the
    excluded locations before anything else, and recomputes `Current.Channels` from what is left, so a fixture on an older
    channel no longer sets the repository's channel. The plan's `platform.exclude` carries the top-level exclude after the
    per-SDK one, because the apply scans the tree itself. Spark's `coverage.yml` gains `dependencies.exclude:
    ["**/fixtures/**", "**/Fixtures/**"]`, the same globs as `versioning.ignore`. Evidence:
    `PlatformRecordPolicyTests.A_platform_found_only_under_dependencies_exclude_is_not_moved` and
    `…Dependencies_exclude_reaches_the_platform_move_and_its_current_channel`, red ("found … platform:node 26";
    `{"docs/**"}`) and then green; 51/51 with `DependenciesYmlTests` and the policy tests.
  - **Cause 2:** the orchestrator resets and fails a group that creates or deletes a file, with the message
    "`<tool>`: created X; a dependency pull request adds and deletes no files". Every other group still ships. The writer
    refuses added files (`DependencyPatchValidator`, AddedOrDeleted), so such a group could never have shipped anyway.
    Evidence: `orchestrator.test.ts` (`.pnp.cjs`) and `apply.test.ts` (a first `go.sum`, previously expected as
    "included"); 432/432.
  - **Cause 3:** Spark's `dependency-apply.yml` adds `packages: read` and passes `GH_PKG_USER`/`GH_PKG_TOKEN` (job
    token), with `feed-secrets: 'true'`. These are the credentials `test.yml` restores with. NuGet restore is on D-S14b's
    safe list, Spark has no Gradle, and `DependencyWorkflowGuard` allows secrets for apply and refuses only write
    permissions. This is the repository's own workflow, not the onboarding template: the template stays secret-free.
  - **AI group-mismatch:** `DependencyPatchValidator` counted a lockfile-only entry as applied whenever its lockfile
    changed, so another group's relock "applied" `@babel/core` and `vite`, which the action had not claimed. With claims
    present, a lockfile-only entry now applies only when its group is claimed. A lockfile change that nothing claimed
    still has no owner, and is refused as `unowned-lockfile`. Evidence:
    `DependencyPatchProfileTests.An_unclaimed_lockfile_only_entry_does_not_apply_through_another_group_s_lockfile_change`
    (red: "applies [express, vite] but result.json claims [express]") and
    `A_lockfile_only_change_nothing_claims_is_still_refused` (red: group-mismatch), then green; 77/77.
- **S4 golden (2026-11-14):** `dependency-action/test/fixtures/platform/dotnet-templates/before/` holds
  MintPlayer.Dotnet.Templates' platform-bearing files, unchanged, at `6f039d1f` (`SOURCE.md`). `platform.test.ts`
  "S4 golden" asserts the scan (10 locations, the property read by all four `*.csproj.in`) and the net10.0 → net11.0
  move: `publish-master.yml`, the package project, the Dockerfile's two 9.0 stages, `eng/Versions.props` and
  `template.json` change; nothing is reported; `node:22-alpine` stays. The M4 and M5 tests were synthetic, and the
  real files showed two defects they could not:
  - **`Label` taken for a condition.** `propertyDefinitions` marked a definition conditioned on *any* attribute, and
    Versions.props opens `<PropertyGroup Label="Version settings">`. The whole chain stayed computed ("is conditioned in
    …/eng/Versions.props"). Only a `Condition` attribute counts now.
  - **The template's projects live outside its folder.** The `*.csproj.in` sit in `src/MintPlayer.Dotnet.Templates/`,
    next to `content/`; `GenerateContent.targets` writes them into `content/` at pack time. P8's root (the folder
    holding `.template.config`) never contained a moved project, so `template.json` would only have been reported. The
    root is now the folder of the nearest template package project (`<PackageType>Template</PackageType>`), the package
    project excepted, because it packs and is not generated. Without a template package, the root is still the
    template's own folder (the M5 tests are unchanged and green).
  - Red: "is conditioned" for all four readers, then `edited` without `template.json`. Green: 86/86 platform, 434/434
    action, tsc clean.

## 9. Questions for the owner (answered 2026-11-13, see §10 P6-P11)

The original questions are kept below for the record. The grill asked them in a different order (P6-P11 name the grill
question). For `runs.using`, the grill recommended a separate record instead of this list's "move". The owner chose to
move it (P10).

- **Q1. Older channels (5.2):** follow for Docker, setup actions, version files and `action.yml`, and report for TFMs and
  matrices, with a per-SDK `lagging` override? *Recommended: yes.*
- **Q2. `template.json` choices (5.3):** replace the framework choice (the template offers only the new framework), or
  keep the old one and add the new? *Recommended: replace; `strategy: add` reports.*
- **Q3. Writer scope (5.4):** may the writer substitute in a lookup-reported `eng/*.props`, and accept a bare major for
  `tfm-property`? *Recommended: yes, limited to reported files and one token.*
- **Q4. `runs.using` (5.7):** move an action's runtime with the Node platform, or only report? Changing a published
  action's runtime changes what its users run. *Recommended: move, since the repository's CI already tests on that major.*
- **Q5. Docker follow without a tag check (S5):** if the registry check turns out too costly, plan the follow anyway and
  let the apply's build fail on a missing tag? *Recommended: decide after S5.*

## 10. Decision log

| # | Decision | By | Evidence |
|---|---|---|---|
| P1 | Dockerfiles follow both the .NET and the Node move, including older stages (R1). | owner, 2026-11-13 | Templates Dockerfile `sdk:9.0` under a `net10.0` repository; §3.1 |
| P2 | `template.json` follows the .NET move (R2). | owner, 2026-11-13 | `template.json:38-50` four `net10.0` literals |
| P3 | The rc pins in Spark and DotnetDesktop.Tools are unintended: fixing them is in scope (R9). SpaServices' rc packages have no GA yet, so they're correct today. | owner, 2026-11-13; nuget.org check 2026-11-13 | §3.1 |
| P4 | Dotnet.Tools' `10.0.5` references on `net11.0` are ordinary NuGet groups (#208), not a platform gap. | investigation, 2026-11-13 | 11 csproj read: executables, tests and a web app, all `net11.0`, none netstandard |
| P5 | Composite actions are already in scope of scanner and writer; no change needed. | investigation, 2026-11-13 | repoFiles.ts:78, DependencyFiles.cs:143-149 |
| P6 | **Older channels split by kind (Q1 → C).** Docker stages (incl. NodeSource `setup_NN.x` and `ARG` defaults), setup-action pins, version files and `action.yml` follow the move. Older TFMs and matrix entries are reported. Override per SDK: `dependencies.platforms.<sdk>.lagging: auto \| follow \| report`, default `auto` (= this split), beside `strategy`/`keepPrevious` (dependency-updates.md:158). The fold (foldPlatform.ts:71) also absorbs lower-channel Renovate records that the group now moves. | owner, 2026-11-13 (grill Q1) | deliberate older TFMs: Spark's `net8.0` versioning CLI, SpaServices `net10.0;net11.0`; matrices: #606 M6 |
| P7 | **Followed Docker tags are checked against the registry (Q5 → A).** Anonymous manifest check before planning, with a short-lived cache. A missing tag is reported, not moved. Where the cache lives (per action run, or a server endpoint) is spike S5. | owner, 2026-11-13 (grill Q2) | `node:20-alpine3.18` → `node:24-alpine3.18` does not exist; no MintPlayer PR check builds images (Templates has no PR workflow) |
| P8 | **`template.json` replaces the framework choice (Q2 → C).** Default: `choice`, its `description`, `replaces`, `defaultValue` move. Under `strategy: add`, `defaultValue`/`replaces` move and the missing old choice is reported. `template.json` moves only when the TFM of the template's own projects moves in the same PR; otherwise it's reported (so M4 cannot ship without M5). | owner, 2026-11-13 (grill Q3) | `replaces` must equal the generated projects' TFM or `--Framework` silently does nothing |
| P9 | **The writer may substitute in a lookup-reported `*.props`/`*.targets` (Q3 → A, with an extra check).** Only on the defining line of the reported property, one token. The writer re-checks that the old value is the old channel's major or channel and the new value the target's. A bare major (`11`) is accepted only for kind `tfm-property`. `<Import>`, `<Target>`, `<Exec>`, conditions and every other line stay refused. | owner, 2026-11-13 (grill Q4) | DependencyFiles.cs:84-109, PlatformSubstitution.cs:75-79; the lookup runs in the repository's CI and can be shaped by a pull request |
| P10 | **`runs.using` moves with the Node platform (Q4 → A, against the recommendation C).** It moves to the highest runtime GitHub supports that does not exceed the target. Anything still missing is reported. Owner: "actions must be moved along". The supported-runtime list is a table in the bot's feed (spike S6). | owner, 2026-11-13 (grill Q5) | github-actions: four actions on `node16`, which GitHub no longer runs |
| P11 | **A same-channel location the bot could not edit holds the group for `approve`.** Older-channel and range reports are information only. The hold reason names the `exclude` override. Majors already wait for approval (R7, DependencyPolicyEngine.cs:481). This changes in-channel moves (rc → GA, patch), which otherwise merge through the queue unseen. | owner, 2026-11-13 (grill Q6: "probably C") | a half-done rc → GA move would otherwise merge with nobody reading the list |

# Multi-target libraries: one dependency tree per target framework — PRD and plan

Status: **owner decided M4–M7 (2026-11-15); next: spikes S1–S8, then plan.** Branch `feat/multi-target-dependency-split`
(from master after #676, merged + deployed in run 43743477315).
Predecessors: [follower-cooldown-PRD](follower-cooldown-PRD.md) H1, [companion-version-maps-PRD](companion-version-maps-PRD.md),
[sdk-updates-PRD](sdk-updates-PRD.md) D2/D13 (strategy add, keepPrevious, lockPackageMajor),
[platform-locations-PRD](platform-locations-PRD.md).

## 1. The problem (observed 2026-11-15, MintPlayer.Dotnet.Tools #216)

The first run after #676 deployed planned what the owner asked on Tools #215 (net11.0-only projects and `dotnet-ef` to
11.0.0), but five unrelated groups were refused by NuGet (NU1605, "Detected package downgrade"):
`xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `coverlet.collector`, `MintPlayer.Versioning.MSBuild`,
`Microsoft.Build.Utilities.Core`. #215 had no such section.

- Cause: H1 splits one package over two groups. A multi-target library (`FolderHasher/MintPlayer.FolderHasher`,
  `Pagination/MintPlayer.Mapping`) takes the 10.0.15 patch in an ordinary group; a `net11.0`-only project that references
  it (`MintPlayer.FolderHasher.Targets`, `MintPlayer.Pagination.Tests`) takes 11.0.0 in the platform group.
- The action applies groups in plan order, each on top of the previous, restoring after each
  (`dependency-action/src/apply/orchestrator.ts:226-232`). Platform groups go last
  (`DependencyPolicyEngine.cs:1022`; documented in `dependency-updates-PRD.md:1204`: "because they move the TFM under
  everything else"). In between, the net11-only project references 10.0.5 directly while the library needs >= 10.0.15:
  NU1605 for every group restored in that window.
- The multi-target library's own 11.0.0 waits for `/approve` (#606 D2: a project that also targets an older channel keeps
  its major), so the library keeps 10.0.x for its `net11.0` target as well.

## 2. Owner request (2026-11-15)

"for multi-target libraries, we can split out the dependency tree, and let the handler remove the no-longer-supported
targets for that library + add the newly supported targets for that library (`<ItemGroup Condition="...">`)". Launch a
team to investigate; new PRD/plan with spikes; keep the files up to date (context compaction).

Reading: in a project with `<TargetFrameworks>net10.0;net11.0</TargetFrameworks>`, each TFM gets its own platform package
versions in a conditioned `ItemGroup` (net10.0 → `Microsoft.Extensions.*` 10.x, net11.0 → 11.x). On a platform move the
handler adds the new TFM with its own `ItemGroup`, and drops a TFM that is no longer supported together with its
`ItemGroup`.

## 3. Open questions (for the investigation)

- Q1 How do the lookup (Renovate nuget manager) and the bot's records see a `PackageReference` inside a conditioned
  `ItemGroup`, and the same package twice in one file? Central package management (`Directory.Packages.props` with
  conditioned `PackageVersion`)?
- Q2 Which edits does the apply action make today for TFMs (`applyPlatform.ts`, D21 `TargetFramework`→`TargetFrameworks`
  for `strategy: add`), and how would it add/remove a conditioned `ItemGroup`?
- Q3 What counts as "no longer supported": EOL from the platform lifecycle, `keepPrevious`, or something else?
- Q4 Is the split opt-in (a `coverage.yml` key) or the default for a multi-target project? What about netstandard /
  net4x targets (no platform major)?
- Q5 Does this replace the immediate NU1605 fix (one package, one group), or ship with it?
- Q6 Real layouts: Tools, Spark, SpaServices multi-target projects and how they reference platform packages today.
  *(answered §4.2)*
- *Q1–Q5 answered by the owner on 2026-11-15: decisions M4–M7 in §8.*
- Q7 (open) Once a repository is on CPM (through M7's action), does the updater split per TFM in `Directory.Packages.props`
  (conditioned `PackageVersion`), or report and leave it? Proposed: support it, since the action produces exactly that
  layout. S7 decides it.
- Q8 (open) The CPM action's details: which repositories (multi-select on the repository query), what it moves (every
  `PackageReference Version` into `PackageVersion`, `VersionOverride` where projects disagree), the branch and pull request
  names, and the reverse direction (inline the versions, delete `Directory.Packages.props`). See S8.

## 4. Findings

### 4.1 Bot planner (investigator 1, 2026-11-15; paths under `apps/CodeCoverage`, not yet re-verified by hand)

- **A record has no TFM and no condition.** `LookupRecord` (`CodeCoverage.Git/Dependencies/DependencyContracts.cs:121-146`) is
  one depName in one package file. Two references to the same package in one file are distinguishable only by
  `CurrentValue`/`ReplaceString`.
- **TFMs live on the platform record.** `PlatformRecord.Locations` (`DependencyContracts.cs:219, 232-254`): `Kind == "tfm"`,
  `Value` (`net10.0;net11.0`), Channel, Computed, Reason, matched to a record by `File == PackageFile`.
  - A conditioned `<TargetFramework(s)>` is computed with reason "conditioned TFM (Condition attribute)"
    (`dependency-action/src/platform/locations.ts:112-124`).
  - `scanTfmConditions` (`locations.ts:147`) finds `'$(TargetFramework)'=='net10.0'` group conditions, but only as notes beside
    the move. Nothing ties a `PackageReference` to the `ItemGroup` condition above it.
- **strategy/keepPrevious/lockPackageMajor.**
  - The bot passes them into `PlanPlatform` unchanged (`DependencyPolicyEngine.cs:997-999`; `DependencyContracts.cs:359-367`).
  - `FindCompanions` skips any SDK with `strategy != replace`, `keepPrevious > 0` or `lockPackageMajor` (`DependencyPolicyEngine.cs:211`).
    Under `add` there are no companions and no riders.
  - **The action decides which TFMs stay** (`dependency-action/src/platform/applyPlatform.ts:25-44`, `nextTfmList`):
    - `replace` swaps the token;
    - `add` prepends the new TFM, keeps the newest `max(keepPrevious, 1)` modern TFMs and drops the rest. **No EOL check.**
  - platform-locations-PRD:196: under `add`, the report says "add the net11.0 choice by hand".
- **H1 never applies to a multi-target project.** Lagging needs every non-computed tfm token to equal the channel
  (`DependencyPolicyEngine.cs:222-228, 258`). Ranged records use the lowest TFM channel (`:229-234, :246`).
- **Plan items carry no TFM either.** `AlignLagging` (`:742-765`) and move riders (`:904-907, :989`) emit
  `DependencyPlannedItem(Record, Candidate, Level)`. The `PlanItem` fields are depName, packageName, ecosystem, datasource,
  manager, depType, packageFile, lockFiles, current/new value/version/digest, replaceString, sharedVariableName and
  updateType (`Policy/DependencyPolicyModels.cs:137-161`; `DependencyContracts.cs:309-344`). A per-TFM version needs a new
  field on the record, on the plan item and in the action.
- **Grouping.** `GroupOf` (`:98-103`): the configured group, else the lookup's groupName, else the depName, so one group per
  package across files (`:71-84`). `ForGrouping` (`:354-365`) pulls riders and lagging records out of that group, and that is
  the #216 split. `Outcome()` orders platform groups last (`:1021-1026`; `DependencyPolicyModels.cs:212-213`).

### 4.2 Real layouts and NuGet facts (investigator 3, 2026-11-15)

- **No repository uses central package management.** None of the three has a `Directory.Packages.props`.
- **MintPlayer.Dotnet.Tools.** 23 projects are `net10.0;net11.0`, and none has a netstandard or net4x multi-target. The
  netstandard2.0 projects are single-TFM generators. Four multi-TFM projects reference platform packages, all
  **unconditioned** at 10.0.5:
  - `Pagination/MintPlayer.Mapping` (DI, `PublishAot`);
  - `FolderHasher/MintPlayer.FolderHasher` (FileSystemGlobbing);
  - `FolderHasher/MintPlayer.FolderHasher.Abstractions` and `SeasonChecker/MintPlayer.SeasonChecker.Abstractions`
    (DI.Abstractions).

  `MintPlayer.FolderHasher.Targets` is single `net11.0`, an MSBuild task package: Build.Utilities.Core 18.4.0,
  FileSystemGlobbing and DI.Abstractions 10.0.5, and it packs FolderHasher's DLLs into `build/`. No multi-TFM project has a
  conditioned `ItemGroup`, and none references `System.*` or `Microsoft.AspNetCore.*`.
- **SpaServices.** 7 projects are `net10.0;net11.0`. They have no platform `PackageReference` at all (only `MintPlayer.*`); the
  tests use `<FrameworkReference Include="Microsoft.AspNetCore.App"/>`, which the SDK resolves per TFM.
- **Spark.** No multi-TFM project. Single TFMs: net11.0 (96), net10.0 (38), net8.0 (3), netstandard2.0 (8).
- **Microsoft.Extensions.* 11.0.0 still supports net10.0.** Verified from the nuspec at
  `api.nuget.org/v3-flatcontainer/microsoft.extensions.dependencyinjection/11.0.0/…nuspec`: groups net10.0, net11.0, net462,
  netstandard2.0, netstandard2.1. This matches sdk-updates-PRD S2 ("Extensions and System.* 11.x still target net10.0;
  AspNetCore and EF Core 11.x need net11.0"). So for Extensions the split is a **choice** (keep net10.0 consumers on 10.x),
  not a necessity. For library-style `Microsoft.AspNetCore.*` / EF Core it is a necessity: 11.x can't load on net10.0.
- **Unverified (from the investigator's memory; spikes S1–S3 below):**
  - `'$(TargetFramework)' == 'net10.0'` conditions and `IsTargetFrameworkCompatible` are evaluated per inner TFM at restore.
  - CPM accepts conditioned `PackageVersion`.
  - NU1605 is per TFM graph.
  - Conditioned `ItemGroup`s produce per-TFM nuspec dependency groups.

### 4.3 Apply action and lookup (investigator 2, 2026-11-15; paths under `apps/CodeCoverage/dependency-action`)

- **Lookup can't tell two references apart.** `mapReport` (`src/lookup/mapReport.ts:152-187`) keeps packageFile, depName,
  currentValue, depType, replaceString, sharedVariableName and candidates. It keeps no position, condition or TFM, and
  `RenovateDep` (`:19-35`) doesn't type `fileReplacePosition`.
  - The nuget fixture (`test/fixtures/renovate/nuget/report.json`) has no conditioned `ItemGroup` and no duplicate nuget depName.
  - Renovate runs as a pinned Docker image (`src/lookup/renovate.ts:11`, 44.125.2), so its extractor wasn't read.
    Unverified: it ignores `Condition` and emits one dep per `PackageReference` (spike S1).
- **NuGet edits are by regex, not position.** `editMsBuild` (`src/apply/adapters/nuget.ts:38-64`) matches the
  `Include`/`Update="<depName>"` line with `Version`/`VersionOverride="<currentValue>"`, and refuses unless exactly one line
  matches (`:40-41`).
  - Two conditioned references on the **same** version are refused as ambiguous.
  - Two on **different** versions are each edited by their own currentValue.
  - The condition is never read. The replaceString adapter (`replaceString.ts:89-94`) edits all identical occurrences and
    doesn't handle the nuget datasource.
- **Platform edits only swap tokens.**
  - `nextTfmList` (`src/platform/applyPlatform.ts:30-44`) and `replacementFor` (`:121-131`) make two structural changes
    only: `TargetFramework`→`TargetFrameworks`, and removing a `dotnet-quality` line (`:311, :365-367`).
  - Nothing inserts or removes an `ItemGroup`.
  - `$(TargetFramework)` conditions are only reported (`src/platform/locations.ts:138-165`).
  - The git writer admits a platform line change only as a one-token swap without `<` or quotes
    (`CodeCoverage.GitWriter/Jobs/Dependencies/PlatformSubstitution.cs:14-25`), so an inserted `ItemGroup` is refused
    today (spike S4).
- **Restore.**
  - Each group restores as it is applied (`orchestrator.ts:197-201`).
  - The whole-tree check (`orchestrator.ts:204-223`; nuget `dotnet restore --locked-mode`, `nuget.ts:205-209`) runs only
    with a `packages.lock.json`, and bisects on failure (`:240-261`).
  - Blockers are the `level=error` logs in `obj/project.assets.json`, else `error NU\d{4}` lines that name a package
    (`blockers.ts:163-200`).

## 5. Spikes

| # | Question | How | Status |
|---|---|---|---|
| S1 | What does Renovate 44.125.2 report for (a) a `PackageReference` in a conditioned `ItemGroup`, (b) the same package twice in one csproj under two conditions, on the same and on different versions? Does it give `fileReplacePosition`? | run the pinned image on a fixture repo (a lib `net10.0;net11.0` with both layouts) and capture a real `report.json` + SOURCE.md | **done** (§5.1) |
| S2 | MSBuild/NuGet with per-TFM `ItemGroup`s: restore, pack (one nuspec dependency group per TFM?), and a `net11.0` consumer referencing 10.0.x directly while the lib's net11.0 asset needs 11.0.0 (NU1605 per TFM graph?) | a throwaway solution in the scratchpad, SDK 11.0.100, `dotnet restore`/`pack`, read the nuspec and `project.assets.json` | **done** (see below) |
| S3 | Which condition forms do we read and which one do we write? Candidates: `'$(TargetFramework)' == 'net11.0'`, `$([MSBuild]::IsTargetFrameworkCompatible(...))`, `!=`, `StartsWith`. The parser must refuse anything it can't map to a TFM set (report, never guess) | grep the three repos + nuget.org samples; extend `scanTfmConditions` (`locations.ts:147`) | open |
| S4 | How does the git writer admit a structural edit? Today it admits one-token swaps only (`PlatformSubstitution.cs:14-25`). Option: the plan carries the exact block to insert/remove (TFM, packages, versions), and the writer re-renders it and compares byte for byte | read `PlatformSubstitution` and the writer's validation; prototype one rule | open |
| S5 | "No longer supported": which data decides that a TFM leaves? Today the action drops beyond `max(keepPrevious,1)` with no EOL check (`applyPlatform.ts:30-44`). Candidates: EOL from `PlatformReleases/dotnet/<ch>` (bot side, already loaded for the channel table), `keepPrevious`, or both | read the channel table the lookup receives (`DependencyPlatformTable.JsonAsync`) | open |
| S7 | CPM + per-TFM: does NuGet accept conditioned `PackageVersion` in `Directory.Packages.props` (and two `PackageVersion` items for one package under different conditions)? What does Renovate report for them? | the S2 solution with CPM on; the S1 fixture with a `Directory.Packages.props` | **done** (§5.1) |
| S8 | CPM conversion action: how existing custom actions open a pull request across selected repositories (the onboarding pull request, the "Set up rulesets" multi-select), and how the conversion edits are made and validated (git writer, or the repository's own workflow like the dependency apply) | read `DependencyOnboarding`, the onboarding chooser and a multi-select custom action | open |
| S6 | How do companion ranges (companion-version-maps), H1 and upstream companions (follower-cooldown G2) key on a per-TFM record? Expected: the `ItemGroup`'s TFM replaces "the lowest TFM" as the key channel | read `FindCompanions` `:211-260` and `RangeOf` | open |

### 5.1 Spike results

- **S2 (2026-11-15, SDK 11.0.100).**
  - Setup: a lib `net10.0;net11.0` with `<ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">` FileSystemGlobbing
    10.0.5 and a `net11.0` group with 11.0.0.
  - `dotnet pack` writes one nuspec dependency group per TFM: `<group targetFramework="net10.0">` → 10.0.5 and
    `net11.0` → 11.0.0. **Verified.**
  - A `net11.0` app with a `ProjectReference` to the lib and a direct FileSystemGlobbing 10.0.5 fails restore:
    `error NU1605: … downgrade … from 11.0.0 to 10.0.5 … App -> Spike.Lib -> … (>= 11.0.0)`. NU1605 is per TFM graph.
    **Verified.**
  - Consequence: a split lib's in-repository consumers on the new channel must move in the **same** group as the split
    (their H1 alignment). Otherwise the next group's restore fails exactly as #216 did. D-c therefore covers the package's
    consumers in the repository, not only the lib.
- **S1 (2026-11-15, the pinned Renovate 44.125.2 image).** Real capture in
  `dependency-action/test/fixtures/renovate/nuget-conditions/` (input, report.json, SOURCE.md).
  - **One dep per `PackageReference`, and the `Condition` is ignored.** FileSystemGlobbing twice (10.0.5, 11.0.0), and
    DependencyInjection twice on the same 10.0.5, as two identical deps.
  - **No position and no replaceString.** `fileReplacePosition` and `replaceString` are absent for every nuget dep.
  - **Not in document order.** Split.csproj came back as Options, DI, DI, FSG 11.0.0, FSG 10.0.5, the reverse of the file.
    So D-a can't match by order. The action must read the csproj itself and join Renovate's candidates by
    `(file, depName, currentValue)`. Identical duplicates get identical candidates, so the join is lossless.
  - Updates are not filtered per TFM: the net11.0 group's 10.0.5 DI is offered `major:11.0.0`, the same as the net10.0 one.
    The bot decides per TFM.
  - CPM: both conditioned `PackageVersion` items are reported (10.0.5 → patch 10.0.15 / major 11.0.0; 11.0.0 → none). The
    versionless `PackageReference` comes back with a `skipReason`.
- **S7 NuGet side (2026-11-15, SDK 11.0.100).** A CPM project `net10.0;net11.0` with Hosting as two conditioned
  `PackageVersion` items restores. `project.assets.json` resolves `net10.0` → Hosting 10.0.5 and `net11.0` → 11.0.0.
  **Verified.** The only message was NU1507 (two package sources without source mapping, from this machine's
  configuration). Combined with S1, the updater can support CPM repositories, which answers Q7 as "support".

## 6. Design (draft, pending owner answers in §3 and spikes)

- **D-a Per-TFM records.** The lookup step reads each csproj / `Directory.Packages.props` itself and emits one record per
  occurrence, with the TFM set of its enclosing `ItemGroup` condition (`null` = unconditioned). Renovate's candidates join
  by `(file, depName, currentValue)`. Not by order, which S1 showed is reversed.
  `LookupRecord` and `PlanItem` gain `TargetFrameworks` (or `ConditionTfm`).
- **D-b A platform package in a multi-target project moves per TFM, in the platform group.** Each TFM's `ItemGroup` takes
  that TFM channel's major, as H1 does for single-TFM projects: no approval, because the older TFM keeps its own major.
  This replaces #606 D2's "multi-target keeps its major, next one waits for approval" for split projects.
- **D-c The split itself.** An unconditioned platform reference in a multi-target project becomes one conditioned
  `ItemGroup` per channel TFM, each with that channel's version.
  - A TFM without a channel (netstandard, net4x) keeps an unconditioned or own-condition reference with today's version.
  - This one group carries every version change of that package in that project, the patch too. The #216 split over two
    groups then can't happen, and the ordinary groups restore against an unchanged tree.
- **D-d Adding and dropping TFMs.** A platform move that adds `net12.0` adds its `ItemGroup` (the platform packages at
  12.x). A TFM that leaves `<TargetFrameworks>` takes its conditioned `ItemGroup`s with it. Which TFMs leave: S5 and Q3.
- **D-e Edits are structural, validated exactly.** The action inserts and removes whole `ItemGroup`s, and the writer admits
  only the blocks the plan names (S4).
- **D-f Reporting.** Any condition the parser can't map is listed under *Not moved*, and its references stay ordinary.

## 7. Plan

*(after the owner's answers and S1–S6; milestones M1… with red tests first. One pull request.)*

## 8. Decision log

| # | Decision / evidence | Source |
|---|---|---|
| M1 | The investigation found no existing per-TFM support anywhere on the path (record, plan item, apply edit, writer validation); every layer needs a change (§4.1, §4.3) | investigation, 2026-11-15 |
| M2 | Microsoft.Extensions.* 11.0.0 supports net10.0 (nuspec groups), library-style AspNetCore/EF Core 11.x does not (sdk-updates-PRD S2): for Extensions the split is a policy choice, for AspNetCore/EF Core a necessity | nuspec on api.nuget.org; sdk-updates-PRD §5 S2 |
| M3 | Don't fix #216 by ordering platform groups first: `dependency-updates-PRD.md:1204` documents "platform groups last, because they move the TFM under everything else" | documented decision |
| M4 | **Automatic split.** Every multi-target project's unconditioned platform reference becomes one conditioned `ItemGroup` per channel TFM on its next run, with no coverage.yml key. It also fixes #216 (D-c: the package's whole move is one group). Answers Q4/Q5 | owner, 2026-11-15 (AskUserQuestion: "Automatic") |
| M5 | **Scope: all .NET companions** (`Microsoft.Extensions.*`, `System.*`, AspNetCore, EF Core, … the companion list): each TFM takes its own channel's major, Extensions included although 11.x still runs on net10.0 | owner, 2026-11-15 ("All .NET companions") |
| M6 | **A TFM leaves at end of life.** The bot drops a TFM (and its `ItemGroup`s) once its channel is EOL in the platform lifecycle data; `keepPrevious` stays a minimum count. Answers Q3; changes `nextTfmList`, which has no EOL check today | owner, 2026-11-15 ("End of life") |
| M7 | **CPM is a separate custom action, in this pull request.** No automatic CPM in the updater. A new custom action switches one or more selected repositories between non-CPM and CPM (both directions) and opens a pull request with the conversion. It is separate from the dependency/TFM updater. | owner, 2026-11-15: "I wouldn't do it automatically, but just provide a new CustomAction that lets the user switch between non-CPVM and CPVM on a (one or more selected) repository - it also creates the pull-request with the changes for from/to CPVM. Include in this pull-request - seperate action from the dependency/tfm updater" |

# Follow-ups after the SpaServices .NET 11.0.100 move: four items, one PR

Date: 2026-11-13. Owner request: "Let's plan all 4 items you listed in a single pull-request. Launch a team to
investigate, then create a new PRD/Plan file, which may include several spikes. Probably start by writing the red
tests, and make them green. Make sure the code is covered properly."

The four items, and what the investigation found for each (§1). Only item 4 is code; items 1-3 end in doc
corrections, spike answers and owner actions.

## 1. Findings

### 1a. #153 package versioning: done, nothing left

The "RESUME" note (a consumer's global `-p:Version` lost to the versioning target, Dotnet.Tools #202) was stale:

- The fix shipped in #545 (#544 D1): `apps/CodeCoverage/CodeCoverage.Versioning.MSBuild/build/MintPlayer.Versioning.MSBuild.targets:44-60`
  lets an explicit global `Version`/`PackageVersion` win; `CodeCoverage.Versioning.Tests/MSBuild/RealPackTests.cs:23-31,74`
  packs for real with `-p:Version=9.9.9` and `-p:PackageVersion=9.8.7`.
- Dotnet.Tools #202 and ng-bootstrap #445 merged 2026-10-25, checks green. Issue #153 is closed.
- Only `docs/coverage_package_versioning_plan.md`'s Status line (line 7) still stops at 2026-10-24: refreshed in M3.

### 1b. #638 first release on an older line: done, nothing left

The repository was `MintPlayer/MintPlayer.AspNetCore.Tools`, not Dotnet.Tools (Dotnet.Tools has no `release/dotnet-10.x`).
AspNetCore.Tools #58 put `MintPlayer.Versioning.MSBuild` 1.3.0 (latest on nuget.org) into `Directory.Build.props` on
`release/dotnet-10.x`; `publish-release` run 43263052114 (2026-11-12 16:50 UTC) succeeded and pushed packages (e.g.
`Endpoints` 10.1.0 is on nuget.org). Issue #638 is closed.

### 1c. SpaServices .NET 10 line (`docs/spaservices_dotnet10_line_PRD.md` §4)

Steps 2-6 are done; the §4 text still shows steps 2 and 3 as ⏳ (fixed in M3):

| Step | Evidence |
|---|---|
| 2 SourceGenerators ≥ 12 in the demos | SpaServices #115, backported as #116, merged |
| 3 D1 + D3 | Spark #652 merged and deployed |
| 4 #114 green, queued | merged on `release/dotnet-10.x` |
| 5 close #113, backport #108/#112 | #113 closed; #117, #118 merged |
| 6 first 10.x release | 10.8.1 published |
| 7 one real fix backported | **open, owner**: a fix on SpaServices master with the `backport: dotnet-10.x` label |

The S-items of step 8 are the parent PRD's (`docs/lockfile_notice_and_line_cut_PRD.md` §3; `sdk-updates-PRD.md` and
`coverage_backport_plan.md` reuse the numbers for other things):

| Item | State |
|---|---|
| S7 | Answered there already (2026-11-13). |
| S9 | **Answered in this PR (spike, 2026-11-13).** `gh search code "dotnet-version" --owner MintPlayer`: every workflow hit is directly in `.github/workflows/` (Dotnet.Tools ×5, AspNetCore.Tools ×2, DotnetDesktop.Tools, Dotnet.Templates, Polyglot ×2, AI ×2, Spark); none in a subdirectory, so D7's one-level `LinePaths.IsWorkflow` holds. Side finding, no change: `.github/actions/setup/action.yml` (composite actions) pins `dotnet-version` in ng-bootstrap and Spark. A narrowing does not convert composite actions; neither repository has an older .NET line today. |
| S10 | **Open, spike with production read.** A parameterised read of production `VersioningOnboardingStates` (`search(Note, "narrowing")`), listing repositories whose `Note` says "no narrowing pull request". Runs only with the owner's go (production database). Each hit is an owner rerun of Set up features. |
| S11 | **Open, owner.** The `rollForward`/mixed-input question needs one throwaway run on PieterjanDeClippel/SparkTest (another repository: an owner action, never a push from here). Partial evidence: SpaServices #114 is green with `global-json-file`. |
| D4 | Done (Spark #654/#656; SpaServices #111, #121-#123 merged). SpaServices issue #109 is the owner's to close. |

### 1d. Two updates SpaServices never applies (the code of this PR)

Every SpaServices dependency PR (#121-#123) lists them under "Not applied":

```
MintPlayer.FolderHasher.Targets: edit refused: MintPlayer.AspNetCore.NodeServices/MintPlayer.AspNetCore.NodeServices.csproj:
  no MintPlayer.FolderHasher.Targets version 11.0.0-rc.1 (0 matches)
npm: edit refused: Demo/Xsrf/ClientApp/package.json: no line matches "npm"\s*:\s*"(?<v>10\.9\.2)" (0 matches)
```

The records, from the real lookup of run 204431316-104-1 (GitHub run 43467453083, artifact `lookup.json`):

```
{"datasource":"nuget","packageFile":"MintPlayer.AspNetCore.NodeServices/MintPlayer.AspNetCore.NodeServices.csproj",
 "depName":"MintPlayer.FolderHasher.Targets","currentValue":"11.0.0-rc.1","sharedVariableName":"FolderHasherTargetsVersion", ...}
{"datasource":"npm","packageFile":"Demo/Xsrf/ClientApp/package.json","lockFiles":["Demo/Xsrf/ClientApp/package-lock.json"],
 "depName":"npm","depType":"packageManager","currentValue":"10.9.2", ...}
```

And the files on SpaServices master:

- csproj line 82: `<PackageReference Include="MintPlayer.FolderHasher.Targets" Version="$(FolderHasherTargetsVersion)" … />`;
  the value lives in the same csproj, line 6: `<FolderHasherTargetsVersion>11.0.0-rc.1</FolderHasherTargetsVersion>`.
  (Corrected after merge: this PRD first said `Directory.Build.props:6`, an investigator's misreading. The diff of
  SpaServices #126, made by this PR's code, edits the csproj's line 6. D1 covers both shapes, so the code was right.)
- `Demo/Xsrf/ClientApp/package.json:24`: `"packageManager": "npm@10.9.2"`.

So the lookup is right both times; the **apply edit** looks for the wrong shape:

- `editMsBuild` (`dependency-action/src/apply/adapters/nuget.ts:36`) only looks for `Version="cur"` on the
  `PackageReference` line, and ignores `sharedVariableName`. The Maven adapter already follows its property
  (`jvm.ts:27`).
- `editPackageJson` (`js.ts:17`) builds `"npm": "10.9.2"`; `depType: packageManager` has no special case. Even with
  the edit fixed, the npm adapter's `pin` would run `npm install npm@x --package-lock-only` (adding npm to the
  lockfile) and `verify` would look for `node_modules/npm` in it: the field is not a dependency and has no lock entry.
- The git writer (`DependencyPatchValidator.cs:55-70`) admits only the entry's own `PackageFile`. A
  `Directory.Build.props` edit for a record whose `packageFile` is the csproj is refused
  (`denied-path`, "Directory.Build.props/.targets change only by a planned NuGet swap …") even when the action makes it.
  The npm case needs no writer change: the line `"packageManager": "npm@10.9.2"` names `npm` (`SubstitutionCheck.Anchors`),
  and `@` is not a version character, so `10.9.2 → 10.9.3` is at a token boundary.

## 2. Decisions

| # | Decision | Evidence / reason |
|---|---|---|
| D1 | A NuGet record with `sharedVariableName` edits the **property**: exactly one `<Prop>cur</Prop>` (attributes allowed, e.g. `Condition`) across the package file and the MSBuild files that can define it for that project: every `Directory.Build.props`, `Directory.Build.targets` and `Directory.Packages.props` from the project's directory up to the root. Zero or more than one → `EditRefused`, as today. | Mirrors Maven (`jvm.ts:27`). MSBuild resolves `$(Prop)` from the project and the `Directory.*` files it imports; the action does not evaluate imports, so "exactly one definition in the candidate set" is the safe rule — two definitions (e.g. a csproj override) refuse instead of guessing. |
| D2 | The candidate files are part of `files()`, so the orchestrator's snapshot/reset covers them and the result lists the props file. | `orchestrator.ts:81-86`: only `files()` is snapshotted; an edit outside it is still reported, but a failed group would not restore it. |
| D3 | The git writer admits, for a `nuget` entry with a `SharedVariableName`, the existing `Directory.Build.props`/`.targets`/`Directory.Packages.props` at or above the entry's `PackageFile` directory, under the same strict `SubstitutionCheck` (the line must name the shared variable and differ only by `cur → new`). Nothing else changes in those files. | The deny-list stays: a Directory.Build.* edit is still substitution-only (#613 item 3), and a new/deleted one is still refused. The line carries the property name, which `Anchors` already yields (`SubstitutionCheck.cs:119`). |
| D4 | A `depType: packageManager` record is its own adapter (`package-manager`, before pnpm/yarn/npm): it edits `"packageManager": "<depName>@<cur>"` and nothing else — no pin, relock or verify, no consistency check, no tool. | The field is corepack's pin of the package manager, not a dependency: no lockfile records it. Running npm on it would add `npm` to `package-lock.json` (pin) and fail `verify`. |
| D5 | A `packageManager` value with a `+<hash>` suffix (`"pnpm@9.1.0+sha512.…"`) is refused (`EditRefused`). | The action cannot compute the new integrity hash, and keeping the old one would make corepack refuse the download. |
| D6 | Items 1-3 change only docs (§1a-c), plus the S9 answer. S10 runs only with the owner's go; S11 and step 7 are owner actions. | One PR per task; other repositories are never edited from here. |

## 3. Spikes

| # | Question | How | Status |
|---|---|---|---|
| P1 | Does Renovate's lookup report `sharedVariableName` for an MSBuild property in `Directory.Build.props`? | The real SpaServices lookup artifact (run 43467453083). | **Yes**: `"sharedVariableName":"FolderHasherTargetsVersion"` with `packageFile` = the csproj. |
| P2 | What does Renovate report for `"packageManager": "npm@10.9.2"`? | Same artifact. | `depName: npm`, `depType: packageManager`, `currentValue: 10.9.2`, `lockFiles: [package-lock.json]`. |
| P3 | Would the writer accept the props edit today? | Read `DependencyPatchValidator.cs:55-70,125-127`. | **No**: the props file is not the entry's `PackageFile` → `denied-path`. D3. |
| S9 | See §1c. | `gh search code`. | **Answered.** |
| S10 | See §1c. | Production read. | **Open** (owner go). |
| S11 | See §1c. | Throwaway run on SparkTest. | **Open** (owner). |

## 4. Milestones (one PR)

- **M1** Action, red then green (`dependency-action/src/apply/adapters/edit.test.ts`):
  property in an ancestor `Directory.Build.props`, property in the csproj itself (the real SpaServices shape, §1d),
  two definitions refused, no definition refused, `files()` lists the props file; `packageManager` for npm (the
  SpaServices line), for pnpm, a `+sha` suffix refused, and the orchestrator applying a `packageManager` item without
  running a tool. Rebuild `dist` in the main checkout.
- **M2** Git writer, red then green (`CodeCoverage.GitWriter.Tests`): the FolderHasher props edit accepted and
  applied; the same edit refused for an entry without `SharedVariableName`; a props file that is not an ancestor
  refused; a props line that changes the value but does not name the property refused; the `packageManager` line
  accepted.
- **M3** Docs: this PRD's results; `docs/spaservices_dotnet10_line_PRD.md` §4 statuses; `docs/lockfile_notice_and_line_cut_PRD.md`
  S9 answered; `docs/coverage_package_versioning_plan.md` Status line; `docs/code-coverage/dependency-updates.md` (the
  two shapes the apply step now handles).
- After deploy: the next SpaServices run should apply both (owner: "SDK + afhankelijkheden bijwerken").

## 5. Results

- **M1 (action).** `src/apply/adapters/sharedPropertyAndPackageManager.test.ts`, 17 tests. Red before the change:
  10 failed (every acceptance case: the SpaServices props shape, a property in the project with a `Condition`,
  `Directory.Packages.props`, two definitions → "ambiguous" (was "no …"), `files()` listing the props file, the npm and
  pnpm/yarn `packageManager` edits, the `+sha512` refusal message, a dependency named `npm` not taken for the field,
  and the group applied with no tool run). The 7 refusal guards passed before and after. Green after: 17/17, `tsc`
  clean. Code: `nuget.ts` `propertyFiles`/`editProperty` (D1/D2), `js.ts` `packageManagerAdapter` (D4/D5), registered
  first among the npm-datasource adapters in `registry.ts`.
- **M2 (git writer).** `CodeCoverage.GitWriter.Tests/DependencySharedPropertyPatchTests.cs`, 10 tests on real
  `git diff`. Red with the validator stashed: 6 failed (the four property-file locations were `denied-path`; the two
  "still refused" guards got `denied-path` instead of `unplanned-change`). The `packageManager` line, the entry without a
  shared variable, the sibling directory and the added props file passed before and after (the npm line needed no
  writer change, P3). Green after: 10/10. Code: `DependencyFiles.PropertyFilesOf`, `DependencyPatchValidator` admits
  those files for a NuGet entry with a `SharedVariableName` (D3).
- **M3 (docs).** Done: `docs/spaservices_dotnet10_line_PRD.md` §2 status and §4 steps 2-8,
  `docs/lockfile_notice_and_line_cut_PRD.md` S9, `docs/coverage_package_versioning_plan.md` Status,
  `docs/code-coverage/dependency-updates.md` §6 (two new bullets). `dist/index.js` rebuilt in the main checkout.
- **Sweep (2026-11-13, once, local).** dependency-action: 387/387 (`vitest run`). CodeCoverage.GitWriter.Tests: 451/451
  (11 m 20 s). No other project is touched.
- **Coverage.** Every new branch has a test: `propertyFiles` (project directory, ancestors, root, sibling excluded),
  `editProperty` (0, 1, 2 definitions), `files()` with and without `sharedVariableName`, the literal `Version=` still
  winning over the property; `packageManagerAdapter` (match, field edit, hash refusal, wrong manager/version, a
  dependency of the same name, orchestrated apply with no tool); `PropertyFilesOf` and the validator branch (NuGet with
  and without a shared variable, non-NuGet skipped, missing files skipped, an added props file still refused).
  The PR's codecoverage check confirms the line numbers after CI.

## 6. Status and what is left

- **Done:** merged as #663 and deployed (2026-11-13). SpaServices pin bump #125 merged; the next apply run applied
  both updates in SpaServices #126 (`MintPlayer.FolderHasher.Targets` via the csproj's line 6, and `npm` via
  `Demo/Xsrf/ClientApp/package.json:24`).
- **Open, outside this PR:** S10 (production read, owner's go), S11 (throwaway run on SparkTest, owner), SpaServices
  §4 step 7 (owner), SpaServices issue #109 (owner closes).

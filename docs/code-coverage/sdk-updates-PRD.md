# SDK updates: a pull request when a repository can move to the next platform major — PRD + plan (#606)

Issue #606 ("sdk updater") has no body. The owner's brief (2026-11-09): the bot already updates packages, and a cron
job already fetches the released SDKs (.NET, Node, PHP, …), so the bot should also prepare a pull request when a
repository can take a new SDK (`net10.0` → `net11.0`). Combining it with the package updater is an option; this
document decides the path.

Status: **M1–M7 merged 2026-11-09 (PR #611, §10); spikes S1–S7 recorded (§5). M8 rollout: all 14 MintPlayer
repositories onboarded 2026-11-09; its seven problems (issue #613) are fixed in PR #614 (§11). Left of M8: after #614
deploys, re-trigger the failed lookups and see one `platform:dotnet 11.0` move through on DependencyUpdaterTest.** .NET 11 goes GA the night of 2026-11-09; the owner migrates their repositories to the current
action the same day (D9).

Related: [dependency-updates-PRD.md](dependency-updates-PRD.md) (#419, the rolling updater; §13 is its current design),
[dependency-updates.md](dependency-updates.md) (user guide).

---

## 1. Finding: the SDK updater already exists, inside the package updater

The investigation (two read-only passes over `apps/CodeCoverage`, 2026-11-09) found that #419 already built platform
moves as a **group kind** of the rolling dependency PR. Evidence:

| What | Where |
|---|---|
| SDKs covered: `dotnet`, `node`, `python`, `java`, `go` (no PHP, Ruby, Rust) | `dependency-action/src/contracts/types.ts:74`, `lookup/lookup.ts:31`, `lookup/foldPlatform.ts:6` |
| Per-repo detection of where each SDK is pinned: `TargetFramework(s)`, `global.json`, Dockerfile `FROM`, `setup-*` `with:`, `engines.node`, `.nvmrc`/`.node-version`/`.python-version`, `.tool-versions`, `go.mod`, Maven release, `requires-python`, `@types/node`, package `<Version>` | `dependency-action/src/platform/locations.ts:80-300` |
| New channels fetched by the action at run time: .NET `releases-index.json`, endoflife.date (v0 API) for the others; previews only from the .NET index | `dependency-action/src/platform/feeds.ts:8-13,30-36,57,102-108` |
| One atomic edit of every location of one SDK (`replace`/`add`, `keepPrevious`, `lockPackageMajor` for the repo's own `<Version>`); computed values (`$(Prop)` TFMs, `requires-python`) are reported, never edited | `dependency-action/src/platform/applyPlatform.ts:38-169` |
| Renovate's own records for those locations folded into the platform group (R31, S18-D1) | `dependency-action/src/lookup/foldPlatform.ts:8-79`; PRD #419 R31 |
| Policy: ignore, hold, fleet-red, ceiling, cooldown; a platform major **never auto-merges** and waits for `approve platform:<sdk> <channel>`; previews (and package prereleases, PRD #419 :203-204) only followed when already on one | `CodeCoverage/Dependencies/Policy/DependencyPolicyEngine.cs:338-429` |
| Red CI: bisect within a budget, hold the culprit **group** | PRD #419 §7.4 |
| Readiness per platform in the fleet view (display only) | `CodeCoverage/Dependencies/Lifecycle/DependencyFleetView.cs:100-104,295` |
| Per-repository `platforms.<sdk>` settings (`strategy`, `keepPrevious`, `lockPackageMajor`, `locations`, `exclude`) | `dependency-updates.md:142-143` |
| Release lines never take a major; a TFM move on the default branch auto-cuts the new line | `dependency-updates.md:152`; `versioning.md:248` |

The cron job the owner refers to is a **separate** feed: `PlatformLifecycleCronJob` (daily 04:17 UTC,
`CodeCoverage/PlatformLifecycle/PlatformLifecycleCronJob.cs:16-50`) stores `PlatformReleases/{platform}/{major}`
(`CodeCoverage.Library/Entities/PlatformRelease.cs:16-52`): .NET from `releases-index.json`, Angular/React/Vue/`nodejs`
from endoflife.date **v1**, previews from npm `next` dist-tags. Its only reader is `PackageVersioning/SupportedLines.cs:28-70`
(which backport release lines are still supported). The dependency updater does not read it.

## 2. Gaps

| # | Gap | Evidence |
|---|---|---|
| G1 | **No PHP, Ruby or Rust platform.** Their packages are updated (composer, bundler, cargo adapters), the runtime is not | `types.ts:74`; `apply/adapters/` |
| G2 | **Two feed readers that can disagree.** Bot and action fetch separately, through different endoflife.date API versions; the bot names Node `nodejs`, the updater `node`; .NET has no GA date in `PlatformRelease` (`ReleasedOn` null) | `feeds.ts:107`; `PlatformLifecycleCronJob.cs:16-34`; `PlatformLifecycleSources.cs:17` |
| G3 | **A TFM move ignores the packages whose major tracks the platform** (`Microsoft.AspNetCore.*` 11.x ships `net11.0` only). Each group fails alone | `applyPlatform.ts:107-113` (`lockPackageMajor` touches only the repo's own `<Version>`) |
| G4 | **"Can get the update" is not judged.** Readiness counts holds only | `DependencyFleetView.cs:295`; PRD #419 :398-402 |
| G5 | **No choice of track.** Previews are never offered to a GA repository, LTS is read from the feeds but used by no policy | `DependencyPolicyEngine.cs:349,353`; `feeds.ts:38,59` |
| G6 | **Locations not yet folded:** workflow `container:` images, `uses: docker://`, CI matrix lists (`node-version: [20, 22]`) | `foldPlatform.ts:50-54`; `locations.ts:186` |
| G7 | **No stored per-repo current platform** (P12 drops platform records from the manifest) | PRD #419 :1372 |
| G8 | **Rollout:** repositories pinned to an old action (this one ran v1.0.0 after R31 shipped) | PRD #419 :1612 |

## 3. Design

### 3.1 Where an SDK move lives (D1)

A platform group of the rolling dependency PR, as #419 built it. No second bot, no separate SDK branch.

### 3.2 Tracks (D8)

`platforms.<sdk>.track: lts | stable | preview`, default **`stable`**, an account default with a per-repository
override (D5). One setting; it replaces today's implicit "follow a preview only when already on one" rule, with no shim.

| Track | Offered |
|---|---|
| `lts` | only a major the feed flags LTS. Refused (listed as an invalid setting in the PR, `dependency-updates.md:118`) for an SDK whose feed has no LTS flag (PHP, Rust, …). Example: .NET 11 is STS, so an `lts` repository on `net10.0` waits for `net12.0` |
| `stable` | the newest GA major |
| `preview` | the newest major, preview phase included (.NET `preview`/`go-live`) |

- The SDK's track governs the SDK **and its companions** (D2), so the TFM and `Microsoft.*` never disagree; with
  `preview`, the D3 readiness check counts prerelease companions, otherwise never.
- Ordinary packages: `stable | preview` per package rule in `dependencies:` (`lts` is refused there). Default `stable`.
- A track decides what is **offered**; a repository already on a newer phase is never moved back.

### 3.3 Companion packages (D2)

An explicit per-SDK companion list in the account lookups, overridable per repository. dotnet default:
`Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`, `Microsoft.EntityFrameworkCore*`, `Microsoft.NET.*`, `System.*`;
node: `@types/node`. S2 split them in two (§5): AspNetCore and EF Core 11.x need `net11.0`; Extensions and System.* 11.x
still target `net10.0` and stay companions for the lockstep (D13). Only a package on the SDK's current major is a
companion (`System.IO.Abstractions` 22.x matches `System.*` and is not). Matching packages leave their own groups and move
with the platform group, at the target major, in one commit; their next major is listed as "moves with the platform"
until then. A platform kept beside the previous one (`strategy: add`, `keepPrevious`, `lockPackageMajor`) has no
companions: its packages must keep building for the older target. A package that locks to the TFM but is not listed (Npgsql, Pomelo) goes red and is added to the list;
owner, 2026-11-09: new packages usually follow a new SDK, so a missing release means *wait* (D3), not red.

### 3.4 Readiness (D3)

A platform group is **pending** — shown in the PR body and the fleet view ("dotnet 11.0 waiting on
Npgsql.EntityFrameworkCore.PostgreSQL"), not offered for approval — while a companion has no release at the target
major on the SDK's track. Re-judged at every lookup. A dependency's own declaration of support (npm `engines`,
Packagist `require.php`) is not read: S3 found no per-release compatibility in Renovate's lookup, and a registry call per
dependency per run is not worth it while .NET, the urgent case, is forward-compatible (D14).
`approve platform:<sdk> <channel> --force` takes the move anyway: missing companions stay at their major and a person
fixes the build on the branch (the human-commit replay keeps it). Phase is read through one interface (D7 trap).

### 3.5 Libraries without release lines (D4)

`strategy: replace` stays the default. For a repository that produces packages and has no `versioning.lines`, the
platform group's PR body warns that the previous platform's consumers stop receiving releases and points at
`versioning.lines` or `strategy: add`. A preview move on a producer also says it auto-cuts the new release line at merge.

### 3.6 One feed (D6, D9)

The bot's daily `PlatformRelease` is the truth, **one row per channel** (`PlatformReleases/dotnet/10.0`,
`PlatformReleases/python/3.14`): **Microsoft's `releases-index.json` for .NET** (released channels and previews), and
**endoflife.date v1 for every other SDK** (php, ruby, rust, python, java = `eclipse-temurin`, go, node). Names unified
(`nodejs` → `node`); a source that answered deletes the rows it no longer lists, so the per-major ids go. Previews
outside .NET and the npm frameworks come from the S6 feeds (§5). The .NET GA date is **not** read: a channel's
`releases.json` weighs 1.15 MB (10.0, 2026-11-09) for one date, so a candidate's age is its latest release's date
(`latest-release-date`), which is also what the cooldown needs (D15).

Evidence for keeping .NET off endoflife.date (2026-11-09, the day of .NET 11 GA): endoflife.date's `dotnet` product had
no 11 at all and still listed `10.0.13`, while Microsoft's index had shipped `10.0.14` on 2026-10-25 and listed `11.0`
as `go-live` `11.0.0-rc.3`. A same-day migration needs the vendor feed.

The bot passes the table to the lookup dispatch as a **required input**; the action no longer fetches feeds. A
repository pinned to an action too old to take it gets no lookup: the bot says so on the repository page and in the
PR ("update the dependency action to ≥ vX"). No fallback (owner: no backward compatibility while in preview; the owner
migrates all their repositories on 2026-11-09).

### 3.7 SDKs (D10, D11)

dotnet, node, python, java, go, plus:
- **PHP:** `config.platform.php` (exact), `setup-php` `php-version`, `php:*` images, `.php-version`/`.tool-versions`
  are moved; `composer.json` `require.php` (a range) is reported. No LTS.
- **Ruby:** channels are minors (`3.3` → `3.4`), like Python: `.ruby-version`, an exact `Gemfile` `ruby`, `setup-ruby`
  `ruby-version`, `ruby:*` images, `.tool-versions` are moved; a `Gemfile` range and gemspec `required_ruby_version` are
  reported. No LTS.
- **Rust (D11):** the channel is `1.x`. A pinned `rust-toolchain(.toml)` channel and CI `toolchain:` inputs move
  together; `Cargo.toml` `rust-version` (MSRV) is reported, never raised; a floating `stable`/`nightly` is reported.
  `track: preview` maps to `beta`, written as the dated toolchain the table names (`beta-2026-11-08`); `lts` is refused.
  **Editions** (`2021` → `2024`) are out of scope; they can come later through the `migrate:` hook without changing the
  model.
- **Ranges are reported, never edited** (D16): `require.php`, a Gemfile range, `required_ruby_version`, `rust-version`,
  `requires-python`. Widening a range needs each ecosystem's range grammar, and the PR's report already names the file.

## 4. Milestones (one pull request, in this order: D7)

1. **M1 Companions** (§3.3, after S2).
2. **M2 Readiness + `--force`** (§3.4, after S3).
3. **M3 Tracks** (§3.2): `track` for platforms and package rules; replaces the implicit prerelease rule.
4. **M4 One feed** (§3.6, after S1/S6): `PlatformRelease` grows every SDK and .NET GA dates; the table becomes a
   required lookup input; the action's feed code is removed; the outdated-action refusal.
5. **M5 New SDKs** (§3.7, after S4/S5): PHP, Ruby, Rust.
6. **M6 Folding** the remaining locations (G6).
7. **M7 Fleet view: current platform per repository** (G7): persist sdk → channels per repository, show the next
   offered major per track.
8. **M8 Rollout** (G8): one live platform PR on `PieterjanDeClippel/DependencyUpdaterTest`, approved, CI green, merged
   by the queue; a red platform group is bisected and held without holding routine groups (D1 trap); the bot's own
   action pin is never held back by the `github-actions` minor ceiling (`DependencyPolicyConfig.cs:18`, D9 trap).

The PR #609 documentation carry-over (§8) ships in the same pull request.

### Until then (.NET 11 GA, 2026-11-09)

Nothing above is needed for tonight's move on an onboarded repository: once Microsoft's index says `active`, the next
lookup offers `platform:dotnet 11.0` (approval-only). Approve the `Microsoft.*` 11.x package majors **in the same run**:
approved groups land on the one rolling branch and build together (what M1 automates). This repository is already on
`11.0` (preview), so rc.3 → 11.0.0 is an in-channel update.

## 5. Spikes

| Spike | Question | How | Decides |
|---|---|---|---|
| **S1 table as input** | Does the channel table fit a dispatch input (65,535 characters, 25 inputs)? If not, a signed bot endpoint is a new public surface (CSRF + route snapshot guards) | Serialize today's table for every SDK; measure | M4's transport |
| **S2 companions** | Does a TFM-only move build? Move a real repository `net10.0` → `net11.0` with and without the Microsoft packages | Two apply-engine runs + `dotnet build`, outputs saved | M1's default list |
| **S3 compatibility data** | Where to read "supports the target" cheaply: NuGet registration, npm `engines`, Packagist `require.php`, rubygems `required_ruby_version`, crates.io `rust_version`. Does Renovate's lookup already return it? | Lookup output of the S18 fixture repo, plus a few registry calls per ecosystem | M2's data source and cost |
| **S4 new SDK locations** | Real-world shapes of `require.php`, `config.platform.php`, `.ruby-version`, `Gemfile ruby`, `rust-toolchain.toml`, `rust-version`, and their `setup-*` inputs | Real fixtures (parsers need real fixtures), the widening rule tested on each | M5's edit rules |
| **S5 multi-target** | `TargetFrameworks` `net10.0;net11.0` and conditional `PropertyGroup`s with `strategy: add` + `keepPrevious` | Apply-engine tests on two fixture projects | `add`'s correctness |
| **S6 preview feeds** | Release-candidate sources for node, php, python, ruby, rust (beta), and whether endoflife.date v1 exposes LTS for each SDK | One fetch per source, saved as fixtures | §3.6 previews, §3.2 `lts` refusals |
| **S7 feed agreement** | Today's `PlatformRelease` vs the action's `feeds.ts` output, per SDK and major | One diff | the migration's expected changes |

No spike runs CI or paid resources; S2 and S5 run locally on fixtures.

### Results (2026-11-09)

| Spike | Result | Evidence |
|---|---|---|
| S1 | **Fits; no bot endpoint.** The five SDKs the action fixture carries serialize to 10,325 characters; the table from every real capture (eight SDKs, end-of-life channels left out) is asserted under half the 65,535 cap | `dependency-action/test/fixtures/platform-feeds/platform-table.json`; `PlatformLifecycleTests.The_lookup_table_from_every_real_source_fits_one_dispatch_input` |
| S2 | **Two kinds of companion.** Restoring 11.0.0-rc.3.26522.148 into a `net10.0` project (SDK 11.0.100-rc.3): `Microsoft.AspNetCore.Authentication.JwtBearer` and `Microsoft.EntityFrameworkCore` fail with NU1202 ("supports: net11.0"); `Microsoft.Extensions.Hosting` and `System.Text.Json` restore and build (they ship `lib/net10.0`, `net462`, `netstandard2.0`). The list stays, for the lockstep (D13) | `DependencyCompanions.cs` remarks; the restore ran in the job's temp dir, not committed |
| S3 | **Renovate carries no per-release compatibility.** Its reports have the repository's own constraints (`extractedConstraints`, an `engines` dep, `requires-python`) and nothing per candidate, in all 14 real report fixtures | `CodeCoverage.Tests/Fixtures/Dependencies/renovate/*/report.json`; D14 |
| S4 | The PHP, Ruby and Rust locations are scanned on the real fixtures; ranges are reported (D16) | `platform.test.ts` S4 tests |
| S5 | `add`/`keepPrevious` lists were already right (`platform.test.ts:216`). **Gap found and closed:** a `PropertyGroup`/`ItemGroup` `Condition` testing `'$(TargetFramework)' == 'net10.0'` went silently dead on `replace`; it is now reported beside the move. `IsTargetFrameworkCompatible` conditions survive a move and are left alone | `locations.ts` `scanTfmConditions`; `platform.test.ts` "S5" |
| S6 | Preview feeds: node `download/rc/index.json`, python.org `downloads/release/?pre_release=true`, php.net `pre-release-builds.php?format=json` (`release-candidates.php` now redirects there), ruby-lang.org `_data/releases.yml`, Rust `channel-rust-beta.toml`. LTS is flagged for node (12 releases) and java (5) by endoflife.date v1 and for .NET by Microsoft's index; go, php, python, ruby and rust flag none (`DependenciesYml.LtsSdks`). **php.net writes `"enabled": false` on a withdrawn RC and nothing on a published one**: a reader that required `enabled: true` would never see a PHP preview | `CodeCoverage.Tests/Fixtures/PlatformLifecycle/SOURCE.md`; `PlatformLifecycleTests.A_php_candidate_is_skipped_only_when_it_says_it_is_disabled` |
| S7 | **Overtaken by M4: not run as a diff.** The action no longer fetches feeds, so there is no second reader left to disagree. Its test table was generated from the same five real captures with the old `feeds.ts` mapping (`git show 5a06a0b5b:…/feeds.ts`); no channel-by-channel comparison with the bot's rows was made | `dependency-action/test/fixtures/platform-feeds/SOURCE.md` |

## 6. Test plan (outline)

- Action (vitest): companions in a platform group (M1), the table as the only feed input (M4), PHP/Ruby/Rust locations
  and edits on S4 fixtures (M5), folding of matrix/container locations (M6).
- Bot (C#): pending vs offered vs `--force` (M2); `track` per SDK and per package rule, `lts` refused without an LTS
  flag, a preview never offered under `stable`, never moved back (M3); `PlatformRelease` for every SDK, .NET GA date,
  `node` naming, the outdated-action refusal (M4); D4's warning; platform channels persisted and shown (M7).
- Live (M8): as listed in §4.

## 7. Risks

- **A wrong move is a major.** Majors never auto-merge (unchanged); the companion list is per SDK and explicit, so no
  unrelated major rides along.
- **Constraints.** `require.php`, `requires-python`, `required_ruby_version`, `rust-version` are ranges or
  minimums; narrowing or raising one breaks consumers of a library. They are reported, never edited (D16).
- **No fallback (D9).** A repository with an old action pin gets no dependency updates at all until the pin moves.
- **`apps/CodeCoverage` is production.** The committed `dist` bundles of the action are rebuilt from the main checkout
  only (CLAUDE.md).

## 8. Also in this pull request: the PR #609 documentation carry-over

PR #609 (#251) was squash-merged as `b690be479` before its guides were updated. Done on this branch (2026-11-09):

- `docs/release-notes-609.md` (new).
- `docs/guide-reference-attributes.md`: an embedded target must be a `[ValueObject]` (D12), a new section "The save
  checks the target", and the dangling-value trap limited to values already stored.
- `docs/guide-cross-module-sync.md`: "Reference ids are checked by the owner".
- `docs/guide-asdetail-attributes.md`: references in AsDetail rows are checked; the error names the AsDetail attribute.
- `docs/guide-row-security.md`: the known-gap note now says what a save does check.

## 9. Decision log (grilled 2026-11-09; "owner" = decided by the repo owner)

| # | Decision | Rejected | Evidence | By |
|---|---|---|---|---|
| D1 | An SDK move is a platform group of the rolling dependency PR | a separate SDK PR (two sagas conflicting on the same `.csproj`s); a split-on-demand hybrid | §1; red-CI bisect holds a group, PRD #419 §7.4 | owner |
| D2 | Companion packages: an explicit per-SDK list, overridable per repository, moving with the TFM | a major-equals-platform heuristic (false positives, Spark's own packages); list + heuristic; no coupling (each group fails alone) | `Microsoft.AspNetCore.*` 11.x ships `net11.0` only | owner ("usually if a new sdk is released, new packages will follow suit") |
| D3 | Pending until ready, with `approve … --force` | pending only (an abandoned package blocks forever); offer + warn (red CI costs money); fixed cooldown | .NET is forward-compatible, so mostly companions block | owner |
| D4 | `replace` by default; a producer without release lines is warned in the PR body | always `replace` silently; inferred `add` (doubles the matrix, contradicts major = TFM) | `versioning.md:248` auto-cut | owner |
| D5 | Track settings: account default + per-repository override; the account default ships `stable` | per repository only; never | — | owner |
| D6 | Microsoft's `releases-index.json` for .NET, endoflife.date for every other SDK, stored daily in `PlatformRelease` | endoflife.date for .NET too | 2026-11-09 fetch, §3.6 | owner |
| D7 | Milestone order: companions + readiness first | feed first; PRD order (PHP first) | .NET 11 GA 2026-11-09 | owner |
| D8 | One `track: lts\|stable\|preview` per SDK (default `stable`), governing the SDK and its companions; `stable\|preview` per package rule; `lts` refused without an LTS flag; replaces the implicit prerelease rule, no shim | a `previews` boolean; one track per ecosystem for all packages; two unlinked settings | `feeds.ts:38,59`; PRD #419 :203-204 | owner ("no backward compat, packages are still in preview") |
| D9 | The channel table is a required lookup input; no fallback fetch; outdated actions refused with a message | fallback for one release; permanent fallback | — | owner ("we'll migrate all my repos today") |
| D10 | SDKs: add PHP, Ruby and Rust | PHP only; none (a separate PR) | — | owner |
| D11 | Rust: channel `1.x`, MSRV widened only, `preview` = `beta`, `lts` refused, editions out of scope | editions as the platform major (code migration per crate); toolchain only | — | owner |
| D12 | Embedded reference targets must be `[ValueObject]`; documentation narrowed, `ReferenceTargetValidation` unchanged | skip collection-less targets in code | #609 save check; no app affected (checked 2026-11-09) | owner |
| D13 | `Microsoft.Extensions.*` and `System.*` stay dotnet companions although their 11.x still targets `net10.0` | narrowing the list to the net11-only families | S2 (§5): they build under `net10.0`; ASP.NET Core 11 depends on Extensions 11, and a `net10.0` library taking Extensions 11 alone pushes it onto its consumers early | implementation, 2026-11-09 (owner may revisit) |
| D14 | Readiness reads only the companions' releases, not each dependency's declared support | npm `engines` / Packagist `require.php` per dependency | S3 (§5): not in Renovate's output; a registry call per dependency per run | implementation, 2026-11-09 |
| D15 | No .NET GA date; a candidate's date is its latest release's | the channel's `releases.json` | 1.15 MB for 10.0 (2026-11-09) for one date; the cooldown needs the latest release anyway | implementation, 2026-11-09 |
| D16 | Ranges and minimums are reported, never edited (supersedes "widen only" in the first draft of §3.7 and §7) | widening per ecosystem's range grammar | a range edit needs four grammars; the PR's report names the file, and D11 already forbids raising the MSRV | implementation, 2026-11-09 |
| D17 | A CI matrix entry that feeds a `setup-*` `${{ matrix.x }}` is moved like any pin on the from-channel; older entries stay | adding the new channel beside the old | `strategy: replace` semantics; `add` users list their channels | implementation, 2026-11-09 |
| D18 | "Producer" for D4 = the manifest says it produces packages, or coverage.yml has a `versioning:` section; the warning goes to platform majors of the SDKs on `strategy: replace` | warning every repository | `DependencyUpdateEngine` `FileUnlinedSdks` | implementation, 2026-11-09 |

## 10. Implementation log (2026-11-09)

All milestones in one change on `feat/issue606-sdk-updater`, after `5a06a0b5b` (this document).

- **M1 Companions:** `DependencyCompanions` (built-in lists), `platforms.<sdk>.companions` in coverage.yml,
  `DependencyPolicyConfig.Companions`; the engine finds them before grouping (`FindCompanions`, `ForGrouping`), they ride
  in the platform group's items and are listed `MovesWithPlatform` otherwise. Tests: `PlatformTrackCompanionTests`.
- **M2 Readiness + `--force`:** `EDependencyReportReason.Pending`; `approve <group> --force` (`DependencyUpdateEngine`,
  `DependencyHolds.Approve(force)`, `DependencyApproval.Force`); the fleet view counts pending as blocked.
- **M3 Tracks:** `track` on file rules, platforms and account `DependencyRule`s (`EDependencyTrack`, Inherit = 0);
  `PlatformTrack`/`PackageTrack`, an enforced rule narrows; `lts` refused for packages and for SDKs outside `LtsSdks`.
  The lookup's `unstable` globs come from `UnstableGlobs()`.
- **M4 One feed:** `PlatformRelease` per channel (`Channel`, `LatestReleasedOn`, `LatestSdk`), `PlatformLifecycleOptions.Platforms`
  with the S6 preview feeds; `DependencyPlatformTable` is the lookup's required `platforms` input; the action's feed code
  is gone (`parsePlatformTable`); a lookup workflow without the input ends the run as `workflow-outdated`
  (`DependencyWorkflowGuard.MissingInputs`).
- **M5 PHP, Ruby, Rust:** scanners and edits in `locations.ts`/`applyPlatform.ts` (Rust beta writes the table's dated
  toolchain); `DependenciesYml.PlatformSdks`.
- **M6 Folding:** workflow `container:`/`image:`/`uses: docker://` are docker locations; matrix entries are the `matrix`
  kind (D17).
- **M7 Fleet view:** `RepositoryManifest.LookupPlatforms` from the lookup ingest; the repository panel lists the SDKs
  and their next stable/preview channel; SDK readiness reads it.
- **D4:** `DependencyUpdateRun.FileUnlinedSdks`, the PR body's warning (`UnlinedProducerNotes`).
- **Fixed on the way:** the collector asked npm for `/-/package/<package>` without `/dist-tags`; npm answers 404
  (`curl` 2026-11-09: 404 without, 200 with), so since #568 M3 no framework preview major (Angular 23) was ever stored.
  The PHP preview reader required `"enabled": true`, which php.net never writes (S6). Within this change (never
  shipped): M4 kept the cron's `Ecosystems.IsName` guard (`[a-z0-9]` only) on the endoflife.date product, which skipped
  the new `eclipse-temurin`; the product now has its own slug check (caught by the S1 table test, which found no `java`). The
  composer manager's `php` dependency (`require.php`, a range) was briefly mapped to `platform:php` by the M5 change;
  it stays `github-tags` (D16), as the existing `mapReport` test required.
- **Refactors on the way** (owner, 2026-11-09: allowed in this PR): `scanQuoted`/`reported` in `locations.ts`; the
  YAML `Root` helper shared by the workflow guard; `Day`/`IsFalse` in `PlatformLifecycleSources`; `ToPlatforms` uses
  `ChannelComparer` instead of its own version parse; `MajorOf` in the policy engine; the `dependencyOnboardingUi` translations
  nested (`panel: { … }`) instead of dotted keys.
- **CI on PR #611 (run 42640595247):** two red suites, both from this change. The setup page's `DEPENDENCIES_YML` must
  equal §4 of `dependency-updates.md` byte for byte and lacked the new `track`/`companions` lines. The CodeCoverage
  translations golden (`SparkTranslationLayersTests`) lacked the 7 new keys; regenerated with `SPARK_UPDATE_GOLDEN=1`,
  and the diff is additions only (the nested `panel: { … }` composes to the same dotted keys). "coverage upload" and
  `pull-request` failed only as a consequence.
- **M8 Rollout** is the owner's: update the action pin in each repository (the bot refuses an outdated lookup
  workflow), then one live platform PR on `PieterjanDeClippel/DependencyUpdaterTest` as listed in §4.

## 11. Rollout fixes (#613, 2026-11-09)

The M8 rollout onboarded the 14 MintPlayer repositories on 2026-11-09 and found seven problems (issue #613). One pull
request, on `fix/issue613-sdk-rollout`.

| # | Decision | Rejected | Evidence | By |
|---|---|---|---|---|
| D19 | The lookup table skips a `PlatformRelease` row whose channel the action refuses (`DependencyPlatformTable.IsChannel`, the action's `^\d{1,4}(\.\d{1,4})?$`). A host whose stored rows are not in that shape collects once at startup (`PlatformLifecycleStartup`); an empty table is left to the daily slot | waiting for the 04:17 UTC slot; a run-now endpoint | 10 of 10 first lookups failed: the deploy kept the per-major rows written before #606, which have no channel. Spark Cron has no catch-up (`libs/cron/MintPlayer.Spark.Cron/README.md`). Tests: `PlatformLifecycleTests.A_row_without_a_channel_never_reaches_the_lookup_table`, `…A_collection_deletes_the_rows_in_the_old_shape` | implementation |
| D20 | A platform group's edits travel to the writer as `PlatformLocations` in the validate, work-ref and rolling requests, built from the lookup's non-computed `PlatformRecord.Locations`. The plan file is unchanged | a new field in the plan | `plan.schema.json` has `additionalProperties: false`, and the fleet's runners are pinned at `5c992693d`: a new plan field fails every apply | implementation |
| D21 | A platform edit is admitted only as a token swap: the line's prefix and suffix stay, the old value was reported at that file, and the new token names the target channel, version or SDK version (`PlatformSubstitution.Explain`); `TargetFramework`→`TargetFrameworks` only for `strategy: add` | admitting any edit of a reported file | every SDK move was refused as `OutsidePlannedFiles`. Tests: `DependencyPlatformPatchTests` | implementation |
| D22 | Completeness is not enforced: a reported location may stay unedited (ranges and minimums are reported only, D16), but an applied platform group needs at least one platform edit | every reported location must change | `applyPlatform.ts` edits only the value span and never the reported-only kinds | implementation |
| D23 | Gap A: an **edit** of an existing `Directory.Build.props`/`.targets` is admitted only as a strict substitution: a planned NuGet `current → new` on a line naming the package (`SubstitutionCheck`), or a reported platform location. Adding, deleting or renaming one, or any other change, stays refused | admitting the files outright; keeping them refused | `MintPlayer.Versioning.MSBuild` is referenced from `Directory.Build.props` in several repositories, and refusing the file refused the whole patch | owner, 2026-11-09 ("Strict substitutions") |
| D24 | Workflow platform edits: a `setup-*` input, a matrix entry (`jobs.*.strategy.matrix.*`, flow style included) and an image (`container`, `container.image`, `services.*.image`) accept a platform substitution; a `run:` line never does | treating workflow platform edits as unplanned | `WorkflowChangeRules.PlatformChange`; test `DependencyPlatformPatchTests` (setup action + flow matrix accepted, `run:` refused) | implementation |
| D25 | `workflow-outdated` opens *Update the MintPlayer dependency workflows* on the onboarding branch, with the current template pinned as onboarding pins it. A workflow without a template (a custom `lookupAction`) gets no pull request, and a line's run opens none | a message only | SpaServices and DependencyUpdaterTest were stuck: onboarding skips a repository whose lookup workflow exists. Tests: `DependencySagaTests.Item613_*` | implementation |
| D26 | The onboarding's commented section shows `platforms:` with no track pinned (the built-in track inherits the account's) | `track: stable` in the comment | uncommenting must keep the defaults, and `DependencyPlatformSettings.Default.Track` is `Inherit`. Test: `The_commented_section_shows_platforms_and_uncommented_keeps_the_defaults` | implementation |
| D27 | GitHub's own merge queue is also detected on a **classic** protection rule, through GraphQL `repository.mergeQueue(branch:)`. The onboarding pull request names it, and the `/queue` "not enabled" refusal says that a pull request adding the section is merged without the queue | REST only | mintplayer-ng-video-player #109: REST `rules/branches/master` = `[]` and the classic protection says nothing about a queue, while GraphQL answered `mergeQueue { configuration { mergeMethod: SQUASH } }` on 2026-11-09. Tests: `GitHubForgeMergeQueueTests.A_classic_rule_that_requires_githubs_merge_queue_is_reported`, `DependencyOnboardingTests.Item613_…` | implementation |
| D28 | The approval rule is stated (onboarding and refresh pull request bodies, guide §2 step 4): someone with write access approves a bot pull request before `/queue` | — | Spark #612, mintplayer-ng-bootstrap #469: "…[bot] does not have write access, so an approval … is needed" | implementation |

**Fixed on the way:** three R16 saga tests read the repository's own `.github/dependabot.yml`, which #612 deleted
(`e27ad5bbe`), so they have failed on master since that merge (`FileNotFoundException`). They now read the file as it was
before #612, kept as a fixture (`CodeCoverage.Tests/Dependencies/Lifecycle/Fixtures/dependabot-spark-before-612.yml`).

**Verified locally (2026-11-09):** the affected classes only. GitWriter.Tests (Dependency*, WorkflowRef*,
GitHubWorkflowValidator, Hardening) 187/187. CodeCoverage.Tests (PlatformLifecycle, DependencyOnboarding, DependencySaga,
DependencyLifecycleRules, GitHubForgeMergeQueue, MergeQueueEngine, DependencyUpdateEngine*) 356/356 after the fixture fix.

**After the deploy (operational):** a repository whose first lookup failed on the old table runs again when its
dependency switch is turned off and on (D-O15 expedites it). SpaServices and DependencyUpdaterTest get the D25 pull
request on their next run.

**Follow-up (PR #615, 2026-11-09):** #614 merged 13 s after its CI started, and master run 42661277346 went red on
`OnboardingPrTests`, which still read the deleted `.github/dependabot.yml`. That class now reads the fixture too. The
merge itself was the merge queue's gap (#616, merge-queue PRD D119): an empty GitHub Actions suite was not waited for,
and Spark's ruleset 26719109 was disabled. The gate fix and the re-enabled ruleset are in that PR's description.

## 12. A preview superseded by its release on the same channel (#617, 2026-11-09)

.NET 11 goes GA on 2026-11-10. Spark and every repository on the 11.0 RC then need one pull request that moves the
exact SDK pins (`global.json` `sdk.version`, every `actions/setup-dotnet` `dotnet-version`) from the RC to the GA SDK,
the RC companions (`Microsoft.AspNetCore.*` `11.0.0-rc.*`) to `11.0.0`, and drops `dotnet-quality: preview`. One pull
request, on `fix/issue617-preview-to-ga`.

### 12.1 Findings (investigated 2026-11-09, read, not run)

- **The platform group stays silent, correctly.** It offers only channels newer than the repository's highest
  (`feeds.ts:66-67`); 11.0 GA is not newer than 11.0, and `net11.0` does not change.
- **The pins move as Renovate records.** R31 keeps the in-channel candidates (`foldPlatform.ts:58-81`); both kinds of
  pin map to ecosystem `platform:dotnet`, depName `dotnet-sdk` (`mapReport.ts:65,89,103-113`), so they share the group
  key `dotnet-sdk` (`DependencyPolicyEngine.cs:79-83`) and cannot land in separate pull requests.
- **G1, `global.json` gets no candidate.** In the recorded Spark lookup
  (`CodeCoverage.Tests/Fixtures/Dependencies/renovate/mintplayer-spark/report.json`) the three `setup-dotnet` pins at
  `11.0.100-rc.1.26425.128` (manager `github-actions`, versioning `npm`) are offered `11.0.100-rc.2.26504.105`;
  `global.json` at the same value (manager `nuget`, versioning `semver-coerced`) is offered **nothing**. It is never in
  the group, so the pull request moves the workflows and leaves `global.json` behind. Whether `semver-coerced` offers
  the GA `11.0.100` is unknown (S8).
- **G2, one group, several targets.** Each member picks its own best candidate (`DependencyPolicyEngine.cs:303-310`)
  and falls back on its own under cooldown (`:367-374`). Different candidate lists give one group different targets.
- **G3, `dotnet-quality` is never touched.** Nothing in `apps/CodeCoverage` reads or edits it;
  `SETUP_ACTIONS.dotnet.keys` is `['dotnet-version']` (`locations.ts:44`). The line editor has no helper that removes a
  line (`core/lineEdit.ts`).
- **G4, the phase is not in the plan.** The bot keeps `support-phase` (`PlatformLifecycleSources.cs:38-45`, `go-live` →
  `preview`) and sends the action `preview` per channel (`DependencyPlatformTable.cs:33-48`); a Renovate record's plan
  item carries no phase.
- **Tests.** Planning ("one group") is asserted in C# (`DependencyTestKit.Evaluate`, `PlatformTrackCompanionTests`);
  the fold and the edits in vitest (`foldPlatform.test.ts`, fixtures under `test/fixtures/renovate/<name>`). The
  action's platform table still has 11.0 as `go-live` rc.2.

### 12.2 Spikes

S8 was planned on Renovate's `semver-coerced` versioning. D29 takes in-channel moves away from Renovate, so it was
replaced before it ran.

| # | Question | Method |
|---|---|---|
| S8 | Does the stored table carry what an in-channel move needs: a channel's latest version, its latest SDK and a release date for the cooldown, for .NET (Microsoft's index) and for the endoflife.date SDKs? | read `PlatformLifecycleSources` and `DependencyPlatformTable` against the real fixtures; fetch Microsoft's `releases-index.json` today |
| S9 | What does `setup-dotnet` do with `dotnet-quality` beside an exact `dotnet-version`, and beside `11.0.x`? Is removing the line equal to `ga`? | `actions/setup-dotnet` README and source at the version the fleet pins |
| S10 | Can a runner on an older pinned action receive an in-channel platform plan it does not understand? | read the bot's lookup-workflow pin check (D25) and `applyPlatform` with `fromChannel == toChannel` |

#### Results (2026-11-09)

- **S8: yes, nothing to add.** `DependencyPlatformTable.Json` (`DependencyPlatformTable.cs:38-47`) already sends each
  channel's `latestVersion`, `latestSdk` and `releaseTimestamp` (`LatestReleasedOn ?? ReleasedOn`). Microsoft's
  `releases-index.json` fetched on 2026-11-09: 11.0 is `go-live`, latest `11.0.0-rc.3`, SDK `11.0.100-rc.3.26522.148`,
  released 2026-10-25; 10.0 is `active`, 10.0.14 / SDK 10.0.403. The day 11.0 ships, its row turns `active` with
  `11.0.0` / `11.0.100`, which is the move.
- **S9: removing the line is `ga`.** setup-dotnet v6's README: `dotnet-quality` is ignored for a full `A.B.C` version, it
  applies to `A.B`, `A.B.x`, `A`, `A.x` and `A.B.Cxx`, and its default is GA. So the line only matters beside a
  floating pin, and removing it is the same as writing `ga` (D31).
- **S10: an older runner degrades gracefully.** The bot checks the lookup workflow's required inputs
  (`workflow-outdated`), not the action version. An older action never builds an in-channel candidate (the lookup and
  the apply are the same action), so it is never planned one; were it handed one, its `applyPlatform` with
  `fromChannel == toChannel` still moves `global.json` and the exact `setup-dotnet` pins to `sdkVersion`, and leaves
  `dotnet-quality` alone.
- **S11 (2026-11-13): Arcade's `tools.dotnet` is a `global.json` SDK pin.** SpaServices #121 moved `sdk.version` from
  `11.0.100-rc.1.26425.128` to `11.0.100` and left `"tools": { "dotnet": "11.0.100-rc.1.26425.128" }` (line 7), the SDK
  Arcade-style build scripts install. `scanLocations` read only the `sdk` object. It now also reads `tools.dotnet`, as a
  `global.json` location that moves with `sdk.version`; the `runtimes` entries (`"dotnet/x64"`) never match the key. The
  line narrowing already moved both (onboarding.md, versioning.md). The git writer's line rule needs no change: it admits
  a substitution of any reported location's value (`PlatformSubstitution`). Red → green:
  `previewToGa.test.ts` "moves an Arcade tools.dotnet SDK with sdk.version, and leaves the runtimes", on SpaServices'
  own `global.json`.

### 12.3 Milestones (one pull request)

- **M1 Red tests.** A hand-written recorded lookup shaped like the real Spark capture: `net11.0`, `global.json` and two
  `setup-dotnet` steps at an rc SDK (one with `dotnet-quality: preview`), an rc `Microsoft.AspNetCore.*` package, and a
  platform table with 11.0 GA. Red first, including one end-to-end red/green test on a `SparkTestDriver` (the real
  composition root and database, owner request 2026-11-09): the lookup goes in, the stored plan comes out with one
  `platform:dotnet 11.0` group that moves `global.json` and every `setup-dotnet` pin to the GA SDK, drops
  `dotnet-quality`, and carries the companions at `11.0.0`.
- **M2 Action: the in-channel candidate (D29).** `buildPlatformRecord` also offers the current channel when an exact pin
  is below its latest version or SDK, or a `dotnet-quality` line is stale on a GA channel; the fold drops every
  candidate of a platform location (in-channel ones too).
- **M3 Bot: the in-channel group (D29, D32, D33).** `EvaluatePlatformMove` plans it at patch level, with riders on
  preview → GA.
- **M4 `dotnet-quality` (D31).** Scanned beside `dotnet-version`, removed by `applyPlatform`, admitted by the writer's
  platform check as a removal of that one key.
- **M5 Docs.** This section's decision and implementation logs.

### 12.4 Decision log (grilled 2026-11-09)

| # | Decision | Rejected | Evidence | By |
|---|---|---|---|---|
| D29 | **The platform table is the single source for SDK moves inside a channel too.** The platform record offers the current channel as a candidate when a pin is below the channel's latest (`latestSdk` for .NET, `latestVersion` otherwise); R31 then folds away every candidate of a platform location, in-channel ones included. The move is a platform group (`platform:<sdk> <channel>`), which edits every location to one target in one pull request | keeping Renovate for in-channel moves with a `versioning` rule for `global.json` plus one-target enforcement in the policy engine; `.NET` only | G1/G2: the recorded Spark lookup offered rc.2 to the three `setup-dotnet` pins and nothing to `global.json` at the same value. §3.6 (D6/D9) already makes the stored table the one feed for channel moves | owner, 2026-11-09 |
| D30 | *Superseded by D29:* a platform group has one target, so no separate rule for one target per SDK group is needed | forcing one target among Renovate records | `PlanPlatform` has one `SdkVersion`/`ToVersion` (`DependencyPolicyEngine.cs:548-560`) | follows from D29 |
| D31 | **`dotnet-quality` is removed** from every `setup-dotnet` step of the group once the target channel is GA. A stale quality line alone (a floating `11.0.x` with `preview`) is enough to offer the group | setting it to `ga`; resetting only beside a pin move | owner, 2026-11-09; setup-dotnet's default per S9 | owner, 2026-11-09 |
| D32 | **An in-channel move is a patch** (same channel, same TFM): it auto-merges or waits for approval as the account's rules say for a patch. A channel move keeps its minor floor, and a major one never auto-merges (R7) | minor like a channel move; patch that never auto-merges | owner preference | owner, 2026-11-09 |
| D33 | **Companions ride on preview → GA.** When a location is on a preview and the target is GA, every companion rides at its GA version in the same group, waiting for it as D3 does (or `--force`). A GA → GA patch carries no riders | never riding in-channel; always riding | the D2 lockstep; owner, 2026-11-09 | owner, 2026-11-09 |
| D34 | **Renovate's in-channel candidates of a platform location are kept only when the table has no row for that channel.** With a row, the fold drops the record whole (`foldPlatform.ts`, `tabled`); without one (an SDK the bot does not collect), the R31 filter of #606 still applies, so nothing goes unoffered | dropping them always (an untabled SDK would lose its patches); keeping them beside the platform group (two groups editing one pin, G2) | D29; the S12 fold test now expects every tabled platform-location record folded (`foldPlatform.test.ts`) | implementation, follows from D29 |
| D35 | **An in-channel move is keyed on the version it moves to,** not the channel: its approval target is `platform:<sdk>@<sdk or version>`, and its cooldown, fleet-red check and first-seen time use that token (`PlatformToken`). An approval of the channel move or of an earlier patch never carries over | the channel token (`platform:dotnet@11`), which every later patch on the channel would share | `DependencyPolicyEngine.cs` `PlatformToken`; `PlatformInChannelMoveTests.The_cooldown_names_the_version_inside_the_channel` | implementation, follows from D32 |
| D36 | **The current channel's row is on every track unless it is a preview;** a preview row only on the `preview` track. An `lts` repository on an STS channel still takes that channel's patches, and a stable repository already on an RC is not moved to the next RC. When the channel move is not planned (awaiting approval, pending, cooling down), the current channel's patch is offered instead; never both, since the two edit the same pins | `OnTrack` unchanged (an `lts` track would leave a repository on 9.0 without its patches) | `PlatformInChannelMoveTests.An_lts_track_still_takes_the_patches_of_the_sts_channel_it_is_on`, `..._a_newer_rc_inside_the_channel_is_a_preview_the_stable_track_does_not_follow` | implementation, follows from D8/D29 |
| D37 | **The writer admits the removal of a `dotnet-quality` line, and nothing else that removes lines.** The line rule sets aside a removed line only when it is `dotnet-quality: <value>` (not `ga`), the lookup reported that location in the file, and a dotnet move targets a release (`WorkflowRefPatchRule.RemovedQuality`); every other line is still paired 1:1. The YAML comparison then lets a setup-dotnet `with:` mapping lose exactly that entry (`GitHubWorkflowValidator.RemovableWithKeys`, a closed list in code), and the change must show up as a removal | a general "removable keys" setting; admitting it in the line rule alone | `DependencyPlatformQualityPatchTests` (five cases: admitted, a move to another preview, an unreported quality, another removed line, another action's input) | implementation, follows from D31 |
| D38 | **A computed quality is reported, not removed** (R8): the quality as a step's only `with:` input, an expression, or an empty value. A companion already on its release has nothing to ride and does not hold the move | removing it anyway; counting the companion as missing | `previewToGa.test.ts` (the scan test); `DependencyPolicyEngine.cs` (the `toRelease` branch) | implementation, follows from R8/D33 |

### 12.5 Implementation log (2026-11-09)

- **Action (M2, M4).** `locations.ts` scans `dotnet-quality:` beside a setup-dotnet version (new kind `dotnet-quality`
  in `types.ts` and `platform-record.schema.json`; the C# `PlatformLocation.Kind` is a string). `channels.ts`
  `isNewerVersion` orders an RC below its release and RCs by their numbers, and never moves a pin back.
  `applyPlatform.ts`: `inChannelMoveChanges` decides whether the current channel is a candidate; an in-channel plan only
  moves a pin forward, and removes a stale quality line when the target is a release. `feeds.ts` offers the current
  channel's row after the newer ones. `foldPlatform.ts` takes the table (D34).
- **Bot (M3).** `DependencyPolicyEngine.EvaluatePlatformMove`: patch level in-channel (D32), version token (D35), the
  in-channel track rule and the fallback to the patch when the channel move is not planned (D36; found by the lts test, which first failed with "Expected ... to contain \"platform:dotnet 9.0\", but found {empty}"), riders on preview → release (D33, D38).
- **Writer (M4).** `WorkflowRefPatchRule` and `GitHubWorkflowValidator` (D37); `DependencyPatchValidator` passes
  `RemovableWith` and checks every removal is a change the comparison found.
- **Tests.** Fixture `dependency-action/test/fixtures/renovate/dotnet-preview-to-ga/` (hand-written, `SOURCE.md` says
  why: 11.0 GA is not out). `previewToGa.test.ts` (lookup red on an RC table and green on a GA one, apply, scan,
  `isNewerVersion`); `PlatformInChannelMoveTests` (policy); `DependencyPlatformQualityPatchTests` (writer);
  `DependencyPreviewToReleaseEndToEndTests` on `SparkTestDriver`: stored `PlatformRelease` row → the lookup dispatch's
  `platforms` input → plan. **Red before M3** (engine at `04f191aee`): "Expected plan.Groups to contain a single item,
  but found 2: Microsoft.AspNetCore.OpenApi, platform:dotnet 11.0", the RC package in a group of its own beside the SDK
  move.

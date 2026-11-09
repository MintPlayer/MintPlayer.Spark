# SDK updates: a pull request when a repository can move to the next platform major — PRD + plan (#606)

Issue #606 ("sdk updater") has no body. The owner's brief (2026-11-09): the bot already updates packages, and a cron
job already fetches the released SDKs (.NET, Node, PHP, …), so the bot should also prepare a pull request when a
repository can take a new SDK (`net10.0` → `net11.0`). Combining it with the package updater is an option; this
document decides the path.

Status: **investigated and grilled 2026-11-09 (D1–D11, §9); spikes S1–S7 next, no code yet.** .NET 11 goes GA the
night of 2026-11-09; the owner migrates their repositories to the current action the same day (D9).

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
`Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`, `Microsoft.EntityFrameworkCore.*`, `Microsoft.NET.*`, `System.*`
(S2 confirms the list). Matching packages leave their own groups and move with the platform group, at the target major,
in one commit. A package that locks to the TFM but is not listed (Npgsql, Pomelo) goes red and is added to the list;
owner, 2026-11-09: new packages usually follow a new SDK, so a missing release means *wait* (D3), not red.

### 3.4 Readiness (D3)

A platform group is **pending** — shown in the PR body and the fleet view ("dotnet 11.0 waiting on
Npgsql.EntityFrameworkCore.PostgreSQL"), not offered for approval — while a companion has no release at the target
major on the SDK's track, or a dependency declares it does not support the target (npm `engines`, Packagist
`require.php`; .NET is forward-compatible, so there it is mostly the companions). Re-judged at every lookup.
`approve platform:<sdk> <channel> --force` takes the move anyway: missing companions stay at their major and a person
fixes the build on the branch (the human-commit replay keeps it). Phase is read through one interface (D7 trap).

### 3.5 Libraries without release lines (D4)

`strategy: replace` stays the default. For a repository that produces packages and has no `versioning.lines`, the
platform group's PR body warns that the previous platform's consumers stop receiving releases and points at
`versioning.lines` or `strategy: add`. A preview move on a producer also says it auto-cuts the new release line at merge.

### 3.6 One feed (D6, D9)

The bot's daily `PlatformRelease` is the truth: **Microsoft's `releases-index.json` for .NET** (released majors and
previews; the GA date from the channel's `releases.json`, `release-date` of `x.0.0`), **endoflife.date v1 for every
other SDK** (php, ruby, rust, python, java = `eclipse-temurin`, go, node). Names unified (`nodejs` → `node`). Previews
outside .NET and the npm frameworks: node, php, python, ruby, rust release-candidate feeds, decided by S6.

Evidence for keeping .NET off endoflife.date (2026-11-09, the day of .NET 11 GA): endoflife.date's `dotnet` product had
no 11 at all and still listed `10.0.13`, while Microsoft's index had shipped `10.0.14` on 2026-10-25 and listed `11.0`
as `go-live` `11.0.0-rc.3`. A same-day migration needs the vendor feed.

The bot passes the table to the lookup dispatch as a **required input**; the action no longer fetches feeds. A
repository pinned to an action too old to take it gets no lookup: the bot says so on the repository page and in the
PR ("update the dependency action to ≥ vX"). No fallback (owner: no backward compatibility while in preview; the owner
migrates all their repositories on 2026-11-09).

### 3.7 SDKs (D10, D11)

dotnet, node, python, java, go, plus:
- **PHP:** `composer.json` `require.php` (a range: widen only), `config.platform.php` (exact), `setup-php`
  `php-version`, `php:*` images, `.php-version`/`.tool-versions`. No LTS.
- **Ruby:** channels are minors (`3.3` → `3.4`), like Python: `.ruby-version`, `Gemfile` `ruby`, `setup-ruby`
  `ruby-version`, `ruby:*` images, `.tool-versions`, gemspec `required_ruby_version` (widen only). No LTS.
- **Rust (D11):** the channel is `1.x`. A pinned `rust-toolchain(.toml)` channel and CI `toolchain:` inputs move
  together; `Cargo.toml` `rust-version` (MSRV) is widened only, never raised; a floating `stable`/`nightly` has nothing
  to edit. `track: preview` maps to `beta`; `lts` is refused. **Editions** (`2021` → `2024`) are out of scope; they can
  come later through the `migrate:` hook without changing the model.

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
- **Constraint widening.** `require.php`, `requires-python`, `required_ruby_version`, `rust-version` are ranges or
  minimums; narrowing or raising one breaks consumers of a library. Only widen.
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

# Prerelease packages and the platform: every scenario — PRD and plan

Status: **implemented (2026-11-15), branch `feat/prerelease-track-scenarios`.** All milestones below are done; the
dependency tests pass (709/709 in `CodeCoverage.Tests.Dependencies`, plus the new E2E).

## 1. The problem (observed 2026-11-15)

- #679 (follower-cooldown-PRD H2) moves a prerelease of a released channel's major (`11.0.0-rc.1` in a `net11.0`-only
  project) to the release. Without that, Tools #218 refused its platform group: Hosting rode to 11.0.0 and
  DependencyInjection stayed on rc.1 (NU1605).
- H2 does not consult the package's **track**. A repository that set `track: preview` on the package in `coverage.yml`
  says "I want prereleases here", yet H2 would still pull it to the stable release.
- The owner's rule (2026-11-15): "if a release within the channel is available, and the library itself also is shipped as
  stable, it should pick that one." That rule applies by default, not over an explicit `track: preview`.
- **Found during this work:** after #679 was deployed, Tools #218 was *still* refused. The plan was right, but the
  platform group carried the R2 blocker hold taken on the pre-#679 refusal. A blocker hold lifts only when the lookup
  offers the blocker something new, or after four scheduled slots, so the fixed engine never got to apply (PR #218 body,
  2026-11-15 20:56Z: "Held: platform:dotnet 11.0 … the resolver refused it: blocked by Microsoft.Extensions.DependencyInjection (R2)").

## 2. Owner scope (2026-11-15)

"Create a branch, launch a new team to further and thoroughly investigate all scenarios, update the PRD/Plan files,
include several spikes, build the necessary (e2e) tests, and implement the solution."

## 3. Findings

Three read-only investigations (track resolution, the scenario matrix against existing tests, the end-to-end path).

### 3.1 How a package's track is decided

`DependencyPolicyConfig.PackageTrackOf`, in order: a `coverage.yml` rule with a track; else a non-enforced account
rule; else **preview if the current version is a prerelease** (prerelease-dependencies-PRD H1); else preview for a
companion of a preview-track SDK; else stable. An enforced account rule caps the result. The screen in `Candidates()`
drops a prerelease candidate on the stable track (`PreviewNotFollowed`).

Consequence: a package on `11.0.0-rc.1` is on the preview track **by inference**. That is indistinguishable from a
chosen `track: preview` unless the config says which it was.

### 3.2 Where the platform logic ignored the track

| Site | Behaviour before | Respects an explicit `track: preview`? |
|---|---|---|
| H2 `preReleaseBehind` (`FindCompanions`) | rc of a released channel's major → behind → release | no |
| `LaggingRiders` | prerelease only towards a preview channel | no |
| #617 D33 rider pick (RC → release move) | stable only | no |
| `SplitItem` (multi-target TFM) | a TFM on a prerelease follows prereleases, even with a release out | n/a: it ignored the owner's rule instead |
| ordinary groups, `RangeRider` | the track screen | yes |

### 3.3 Scenario matrix

| # | Scenario | Expected | Before | Test |
|---|---|---|---|---|
| T1 | rc, released channel, release + newer preview exist, no rule | release `11.0.0` | ✔ | `PrereleaseScenarioTests.T1` |
| T2 | same, `track: preview` rule | newest prerelease `11.1.0-preview.1` | ✘ `11.0.0` | T2 |
| T3 | rc, no release yet | next rc | ✔ | T3 |
| T4 | rc, no release, `track: stable` | nothing, `PreviewNotFollowed` | ✔ | T4 |
| T5 | `Directory.Packages.props` over net11-only projects, Hosting 10.0.5 + DI rc | both `11.0.0` in one platform group | ✘ DI in its own group (NU1605 under CPM) | T5 |
| T6 | `Directory.Packages.props` over net10 + net11 projects | split per TFM (P6) | ✔ (the spike's expectation was wrong) | T6 |
| T7 | `dotnet-tools.json` rc tool | release | ✔ | T7 |
| T8 | RC → release move, companion with `track: preview` | newest prerelease | ✘ `11.0.0` | T8 |
| T9 | RC → release move, no rule | release | ✔ | T9 |
| T10 | multi-target `net10.0;net11.0`, net11 TFM on rc | `net11.0=11.0.0` | ✘ `11.1.0-preview.1` | T10 |
| T11 | T10 with `track: preview` | `net11.0=11.1.0-preview.1` | n/a | T11 |
| T12 | platform hold whose refused items differ from today's plan | lifts, group planned | ✘ held | T12 |
| T13 | platform hold for the same items | stays held | ✔ | T13 |
| T14 | platform hold from before D5 (no items recorded) | lifts once | ✘ held | T14 |
| T15 | refusal records the items; same lookup re-planned | held, no retry loop | n/a | T15 |
| E2E | saga on Raven: Tools #218 shape with the stale hold | one `platform:dotnet 11.0` group, Hosting + DI at 11.0.0 | ✘ | `DependencyPreviewToReleaseEndToEndTests.A_platform_hold_from_before_a_fix_lifts_when_the_group_is_planned_with_its_blocker` |

Left as they are, by decision (§6): an rc of the **next** major on an older channel (`12.0.0-preview` on channel 11, or
an `11.0.0-rc` on channel 10) is above the channel's major and is no companion; it follows its track as an ordinary
package. An enforced stable account rule over a companion on an rc keeps it from moving to a newer rc, and reports why.

### 3.5 The owner's expectations, one test each

Owner, 2026-11-15: "packages matching specific globs follow the SDK version, preview-sdk allow preview packages, stable
sdk only allows stable packages, unless they're on an exclusion list in the coverage.yml file". `PlatformExpectationTests`:

| # | Expectation or documented exception | Result |
|---|---|---|
| E1 | major move (approved): companions ride to the new major; an ordinary package stays in its own group | ✔ |
| E2 | `net11.0`-only project: an older-major companion takes the channel's major | ✔ |
| E3 | project on an older channel than the SDK's: keeps its major (patch planned, major awaits approval) | ✔ |
| E4 | `netstandard2.0` project: ordinary packages | ✔ |
| E5 | **SDK on its RC, `net11.0` project, Hosting 10.0.5: takes `11.0.0-rc.2`** | ✘ before D8 (got 10.0.15) |
| E5b | same, with `track: stable` on Hosting: 10.0.15 | ✔ |
| E5c | same SDK, `net10.0` project: 10.0.15 | ✔ |
| E6 | preview SDK, ordinary stable package: stables only, preview listed | ✔ |
| E7 | `platforms.dotnet.track: preview`: companions get previews | ✔ |
| E8 | released SDK, stable companion: stable patch, preview listed | ✔ |
| E9 | released SDK: a next-major preview is not offered | ✔ |
| E10 | exception: a package already on a next-major prerelease follows its prereleases as an ordinary package | ✔ |
| E11 | `ignore` in coverage.yml: not moved | ✔ |
| E12 | `track: stable` on an rc: takes the release | ✔ |
| E13 | enforced stable account rule beats the file's `track: preview` | ✔ |
| E14 | a non-enforced account `track: preview` is an explicit choice | ✔ |
| E15 | `hold: true` in coverage.yml: not moved | ✔ |

The non-platform track rules are `PackagePrereleaseTrackTests` A1–A5 (beta package, preview rule, enforced stable).

### 3.4 Documentation contradictions found

- "H1" names two decisions: prerelease-dependencies-PRD H1 (the default track) and follower-cooldown-PRD H1 (channel
  major). The engine comment on H2 was labelled "H1 (Tools #218)". Fixed; follower-cooldown-PRD H2a notes the naming.
- `dependency-updates.md` said a `Directory.Packages.props` reference "can't be traced to a project and is still a
  companion", while P6 already traces it for multi-target splitting. Rewritten (D4).

## 4. Spikes

| Spike | Question | Result |
|---|---|---|
| S1 | Do the scenarios T1–T10 hold on master `df1a755dd`? | 5 red (T2, T5, T6, T8, T10). T6 red on a wrong expectation: P6 splits a central file over two channels, which is right. |
| S2 | Can the existing track tuple tell a chosen track from an inferred one? | No: `PackageTrackOf` returned `(Track, Why)`. A third member is needed (D1). |
| S3 | Why was T2 still red after D2? | The test kit labelled `11.0.0-rc.1 → 11.1.0-preview.1` a patch. Renovate labels a minor move a minor, and `Candidates()` orders by level, then index. Fixed in the kit; not an engine defect. |
| S4 | Why is Tools #218 still refused after #679 deployed? | The R2 blocker hold from the pre-#679 refusal: lookup run 43760657459 changed nothing about DI, so `Lifts` stayed false; PR #218 updated 20:56:31Z with "Held … (R2)". |
| S5 | Does the alignment group check holds? | No: `AlignLagging` builds a group named like the move (`platform:dotnet 11.0`) and never read `next.Holds`. T13 was green only once the alignment honours the hold. |
| S6 | Do the new E2E and unit tests guard D5? | Mutating `StandsFor` to always hold: T12, T14 and the E2E go red; T13 stays green. |

## 5. Plan (all done)

| M | Change | Where |
|---|---|---|
| M1 | `PackageTrackOf` returns `Explicit`: true when a `coverage.yml` or account rule (or an enforced cap) set the track | `DependencyPolicyConfig.cs` |
| M2 | `ExplicitPreview(record)`; H2, `LaggingRiders` and the D33 pick let prereleases through for it | `DependencyPolicyEngine.cs` |
| M3 | `SplitItem`: a TFM on a prerelease follows prereleases only until a release on its major exists, unless the track is explicit preview | `DependencyPolicyEngine.cs` `SplitItem` |
| M4 | `TargetsOnlyTheChannel` accepts a `Directory.Packages.props` whose projects under its folder all target only the channel | `FindCompanions` |
| M5 | Bot holds on a platform group record the refused items (`DependencyHold.Refused`); the move and the alignment group lift such a hold when the group is planned with other items, and hold it otherwise | `DependencyHolds.cs`, `DependencyUpdateState.cs`, `EvaluatePlatformMove`, `AlignLagging` |
| M6 | Tests T1–T15 + the Raven saga E2E | `PrereleaseScenarioTests.cs`, `DependencyPreviewToReleaseEndToEndTests.cs` |
| M7 | Docs: `dependency-updates.md`, follower-cooldown-PRD H2a, this PRD | `docs/code-coverage/` |

Not done, on purpose: a TypeScript integration test that restores the two-package group against a local NuGet feed. The
apply side is unchanged by this work; the defect was only ever in what the plan contained and whether it was held.

## 6. Decision log

| # | Decision | Evidence |
|---|---|---|
| D1 | A track is **explicit** when a `coverage.yml` rule, an account rule or an enforced cap set it; inferred from the current version or the SDK's track otherwise. Only an explicit preview overrides the platform's preference for a release. | S2; owner, 2026-11-15: the rule applies "if a release within the channel is available", and the user asked how a repository says a package may be a preview (answer: `rules: - match … track: preview`) |
| D2 | H2, `LaggingRiders` and the #617 D33 rider pick keep prereleases for an explicit preview track. | T2, T8 red → green |
| D3 | Under an inferred track, a TFM in a multi-target project on a prerelease takes the release on its major once there is one (the owner's rule, applied per TFM). | T10 red → green; T11 for the explicit case |
| D4 | A `Directory.Packages.props` targets only the channel when every project with a `tfm` under its folder does (and at least one does). It then aligns like a project; otherwise P6 splits it. | T5 red → green; T6 |
| D5 | A bot hold (R2 blocker or unattributed) on a platform group binds to the **items it refused**, not only the group's name. A plan with other items lifts it; the same items stay held, so an unchanged lookup does not loop. A hold from before D5 has no items and lifts once; the next refusal records them. The alignment group under the platform's name honours the same hold, and waits while the move is held. | S4, S5; T12–T15; E2E red → green; S6 mutation |
| D6 | An rc of a major above the channel's (`12.0.0-preview` on channel 11) stays an ordinary package on its track. | Unchanged behaviour; #606 D2 ("moves with the platform" applies to the next major only with the SDK's move) |
| D8 | **On an SDK the repository is on a preview of, a companion's prerelease on the channel's major is offered** (the platform's own phase), unless a rule chose `track: stable`. Only companions count (projects on the channel only, or companions on the channel's major), so a `net10.0` project and ordinary packages stay on stables. Before, the track screen dropped the rc before the lagging riders saw it, so `LaggingRiders`' `preview` flag never applied to a package on a stable. | Owner, 2026-11-15 ("preview-sdk allow preview packages"); E5 red (10.0.15) → green; E5b, E5c boundaries; dependency tests 728/728 |
| D7 | No apply-side (TypeScript) change or test. | §5; the refusal message in PR #218 shows the apply resolving exactly what was planned |

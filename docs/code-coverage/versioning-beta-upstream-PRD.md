# A dependency that never went stable blocks a whole release (MintPlayer.GraphQL)

Status: investigated 2026-11-14; owner chose **A** (G2). Implementation: §6.

## 1. What happened

MintPlayer.Dotnet.Tools' first versioning run after the onboarding PR #209 merged was refused, and nothing was tagged.

- Run: https://github.com/MintPlayer/MintPlayer.Dotnet.Tools/actions/runs/43509498988 (2026-11-13 21:22 UTC, `master`
  7cae0fa, job `publish-release`, action `versioning-v1` = fb5d5bf, `mode: server`).
- Versioning run record on coverage.mintplayer.com: outcome `plan-refused`, refused package `MintPlayer.GraphQL`.
- The action's log, verbatim:

```
##[error]Versioning (server) did not tag the commit:
plan-refused
  MintPlayer.GraphQL: 11.0.0-beta.1 is not above 11.0.0-rc.1, which already exists (upstream is less mature than the open prerelease line).
  MintPlayer.AdminHelper/v11.0.0: not written
  ... (40 tags, every package's 11.0.0 / 13.x GA release): not written
```

So the .NET 11 GA release of 40 packages is blocked by one package.

## 2. Cause (evidence)

1. `GraphQL/MintPlayer.GraphQL.Tools/MintPlayer.GraphQL.Tools.csproj` (Tools master) references
   `Octokit.GraphQL` `0.4.0-beta` (runtime dependency, not `PrivateAssets`).
2. nuget.org: Octokit.GraphQL has **only** prereleases: `0.1.0-beta` … `0.4.0-beta`, never a stable version.
3. nuget.org: MintPlayer.GraphQL shipped `8.0.0`, `9.0.0`, `10.0.0-rc.1`, `10.0.0`, `11.0.0-rc.1`. It has always gone
   stable despite the beta dependency.
4. D8 (`docs/coverage_package_versioning_PRD.md` §D8): a package is prerelease when any runtime dependency resolves to a
   prerelease, with the least mature label winning (`alpha < beta < next < preview < rc`). `ReleasePlanner.UpstreamLabel`
   (`CodeCoverage.Versioning/Releases/ReleasePlanner.cs:427`) therefore gives GraphQL `beta`.
5. `VersionCalculator.Next` (`CodeCoverage.Versioning/Versions/VersionCalculator.cs:56-74`): the open prerelease line
   `11.0.0-rc.1` (seed tag) continues as core `11.0.0`; with label `beta` the next is `11.0.0-beta.1`, which is not
   above `rc.1` → `Refuse`.
6. `ReleaseRun.cs:96-98`: "All or nothing: a refused package … stops the run before any tag is pushed, so a rerun after
   the fix starts from the same history." A documented design choice; this PRD does not change it.

The same thing hit Spark on 2026-10-24 (D8 amendment, run 40124565898: Webhooks.GitHub, Octokit.GraphQL). Spark was
fixed with a repository pin `versioning.prerelease: preview`, which suits Spark (in preview) but not Tools (going GA).

## 3. What exists today

- Pins: `versioning.prerelease` and `versioning.packages.<id>.prerelease` take `alpha|beta|next|preview|rc|upstream`
  (`VersioningConfig.cs:44,200-203`). **There is no "stable" pin**: a pin can only make a package *less* stable.
- So without a code change the only unblock is pinning GraphQL to `rc` in Tools' `coverage.yml`
  (`versioning.packages.MintPlayer.GraphQL: { prerelease: rc }`) → `11.0.0-rc.2` now, and rc for as long as the pin stays.

## 4. Options (owner decision)

- **A. A `stable` pin value** (`versioning.packages.<id>.prerelease: stable`): the package releases stable whatever its
  upstream says. Tools adds it for MintPlayer.GraphQL (an issue in Tools; the bot does not edit `coverage.yml`). GraphQL
  then ships `11.0.0` with the others, matching its history. Cost: small engine + schema + docs change, tests.
  NuGet emits NU5104 (stable package with a prerelease dependency) as a warning, as it did for 8.0.0-10.0.0.
- **B. Ignore an upstream that has no stable release at all** (Octokit.GraphQL): automatic, no config. Needs a registry
  lookup per prerelease dependency during planning; a dependency's first stable would silently change behaviour.
- **C. No code change: pin `rc` in Tools.** GraphQL stays `11.0.0-rc.N`; every other package goes GA at once.

Recommendation: **A**, plus the Tools issue. It's explicit, matches GraphQL's release history, and needs no registry
calls; C leaves GraphQL as rc forever, B hides a decision in a heuristic.

## 5. Decision log

| # | Decision | Evidence / source |
|---|---|---|
| G1 | The all-or-nothing refusal stays. | `ReleaseRun.cs:96` comment; not questioned by the owner. |
| G2 | Option A: a `stable` pin value; an issue on Dotnet.Tools sets it for MintPlayer.GraphQL. | Owner, 2026-11-14 (question answered "Stable pin"). |

## 6. Implementation plan and progress

Branch `fix/versioning-stable-pin` (from origin/master 2b4c681, 2026-11-14). Design: a value type `PrereleasePin`
(upstream | a label | stable; wire text `upstream`/`stable`/label) replaces the bare `PrereleaseLabel?` (null = upstream)
in `VersioningSettings.Prerelease`, `PrereleasePins`, `LinePrerelease` and `VersioningSettingsWire`;
`ReleasePlanner.ReleaseLabel` returns no label for a stable pin. Tests written: `ReleaseLabelTests` (3 new),
`VersioningSettingsTests.Stable_pins_survive_the_wire_from_the_app`.

Red, 2026-11-14: `dotnet test CodeCoverage.Versioning.Tests --filter ReleaseLabelTests` → Failed 3, Passed 17. All three:
"versioning.prerelease / versioning.packages.Core.prerelease must be one of alpha, beta, next, preview, rc or upstream."
The wire test is a compile red (`PrereleasePin` did not exist).

Green, 2026-11-14: Versioning.Tests (ReleaseLabel, ReleaseLines, VersioningConfig filters) 83/83; GitWriter.Tests
`VersioningSettingsTests` 21/21. The writer's `Enum.IsDefined` checks went away: `PrereleasePin`'s JSON converter is
the only way in and refuses anything but a label, `stable` or `upstream` (`An_unknown_label_on_the_wire_is_not_read`).
`docs/coverage_backport_PRD.md` D14's "no new `stable` keyword" is marked superseded.

- [x] M1 (red first): `ReleaseLabelTests`-style test: a package pinned `stable` with a `beta` runtime dependency on an
      open `11.0.0-rc.1` line releases `11.0.0`; repository-level `prerelease: stable` likewise; `upstream` still
      mirrors. Config test: `stable` parses, round-trips over the wire (server mode).
- [x] M2: engine: `VersioningConfig` accepts `stable` (`Pin`, `IsPrerelease`, validation); `ReleasePlanner.ReleaseLabel`
      returns no label for it, distinct from "no pin".
- [x] M3 (no schema enumerates the values; template comment, D8 amendment, versioning.md done): schema/docs: coverage.yml schema (if it enumerates the values), the onboarding template comment
      (`# prerelease: upstream …`), `docs/coverage_package_versioning_PRD.md` D8 (second amendment, link here), the
      versioning guide.
- [ ] M4: issue on MintPlayer.Dotnet.Tools: set `versioning.packages.MintPlayer.GraphQL: { prerelease: stable }` after
      the deploy, then re-run publish-release on master.

# s18-dependency-updater-test

Regression fixture for **S18-D1 / R31** (docs/code-coverage/dependency-updates-PRD.md §13.13): the live
rolling PR on `PieterjanDeClippel/DependencyUpdaterTest` offered `node` 20 → 24.22.0 and
`platform:node 26`, and `dotnet-sdk` 8.0.x → 10.0.402 and `platform:dotnet 10.0`, as separate groups.

- `input/.github/workflows/ci.yml` and `input/src/Lib/Lib.csproj` are the repository's files as fetched
  from GitHub on 2026-10-16 (`gh api repos/PieterjanDeClippel/DependencyUpdaterTest/contents/...`).
- `report.json` is **reconstructed, not captured**: Docker was not running, so the pinned Renovate image
  could not be run against the repository. Each dep entry copies the shape of the matching entry in
  `../docker-actions-platform/report.json` (the real S12 capture, renovate 44.125.2), with the values the
  S18 rolling PR body showed (`node` 20 → 24.22.0, `dotnet-sdk` 8.0.x → 10.0.402). The `newValue`s of
  those two (`24`, `10.0.x`) follow Renovate's range-preserving replacement for a channel-level value.
  Only the deps the test needs are present; the npm deps of the repository are left out.

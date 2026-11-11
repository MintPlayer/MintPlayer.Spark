# dotnet-preview-to-ga

Regression fixture for **#617** (docs/code-coverage/sdk-updates-PRD.md §12): a repository on the .NET 11.0 RC on the
day after 11.0 went GA. `net11.0`, `global.json` and an exact `setup-dotnet` pin at `11.0.100-rc.2.26504.105`, a floating
`11.0.x` step with `dotnet-quality: preview`, and an RC `Microsoft.AspNetCore.OpenApi`.

- `input/` is **hand-written**, shaped like MintPlayer.Spark's own files (`global.json`,
  `.github/workflows/dotnet-build-master.yml`), which pin the same RC SDK.
- `report.json` is **reconstructed, not captured**: 11.0 GA is not released when this is written (2026-11-09), so no
  Renovate run can report it. Each entry copies the shape of the matching entry in the real capture
  `CodeCoverage.Tests/Fixtures/Dependencies/renovate/mintplayer-spark/report.json` (renovate 44.125.2), including what
  made #617 G1: the `setup-dotnet` pin (manager `github-actions`, versioning `npm`) is offered the next SDK, and
  `global.json` at the same value (manager `nuget`, versioning `semver-coerced`) is offered **nothing**. The candidates
  are the GA versions (`11.0.100`, `11.0.0`) dated 2026-11-10.
- The test gives the action's platform table (`../../platform-feeds/platform-table.json`) an 11.0 GA row in code.

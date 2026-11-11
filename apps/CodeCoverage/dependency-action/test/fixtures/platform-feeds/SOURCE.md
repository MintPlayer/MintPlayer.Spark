# Source: platform feeds (D-S12c)

- Captured: 2026-10-16 with `curl -sS -o <file> <url>` (no credentials), for milestone N3.
- `releases-index.json`: https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json
- `nodejs.json`, `python.json`, `go.json`, `eclipse-temurin.json`: `https://endoflife.date/api/<product>.json`

`platform-table.json` (#606 D9) is the `platforms` input the bot dispatches, derived from the five
captures above with the mapping the action applied itself before #606 (`git show 5a06a0b5b:apps/CodeCoverage/dependency-action/src/platform/feeds.ts`,
`candidatesFromReleasesIndex` / `candidatesFromEndOfLife`). The bot now builds it from its `PlatformRelease` rows; the
action fetches nothing.

Frozen point-in-time answers: at capture time .NET 11.0 is `go-live` with `11.0.0-rc.2` (a preview
channel), 10.0 is the active LTS. Tests must treat these as fixtures, never as "today".

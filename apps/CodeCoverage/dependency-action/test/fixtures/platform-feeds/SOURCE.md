# Source: platform feeds (D-S12c)

- Captured: 2026-10-16 with `curl -sS -o <file> <url>` (no credentials), for milestone N3.
- `releases-index.json`: https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json
- `nodejs.json`, `python.json`, `go.json`, `eclipse-temurin.json`: `https://endoflife.date/api/<product>.json`

Frozen point-in-time answers: at capture time .NET 11.0 is `go-live` with `11.0.0-rc.2` (a preview
channel), 10.0 is the active LTS. Tests must treat these as fixtures, never as "today".

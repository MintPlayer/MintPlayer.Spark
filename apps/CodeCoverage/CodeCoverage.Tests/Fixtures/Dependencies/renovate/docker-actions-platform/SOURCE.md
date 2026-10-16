# Source: docker-actions-platform

- Tool: Renovate 44.125.2 (npm publish 2026-09-30T19:22Z), Docker image `renovate/renovate:44.125.2`
- Image digest: `renovate/renovate@sha256:a7dec3432d62b39872058424f0490829413b3f597f5d72fb41b9b7bd57ccac77`
- Captured: 2026-10-15T19:16Z on Windows 11 + Docker Desktop 29.8.1 (linux/amd64)
- Input: samples/docker-actions-platform (synthetic sample written for S12; input/ is its full tree)
- Run: `name=docker-actions-platform exit=0 seconds=50 image=renovate/renovate:44.125.2` (wall seconds, includes container start and all registry lookups, cold cache)
- Side effects: sentinel files created = 0; `Executing command` debug log lines = 0; repo git status before/after identical

Exact command (from spikes/s12/run-one.sh; GITHUB_COM_TOKEN passed by name only, value never written):

```sh
GITHUB_COM_TOKEN="$(gh auth token)" docker run --rm \
  -v <repo>:/usr/src/app -v <out>:/out -v <sentinel>:/sentinel -w /usr/src/app \
  -e GITHUB_COM_TOKEN -e LOG_LEVEL=debug \
  -e GIT_CONFIG_COUNT=1 -e GIT_CONFIG_KEY_0=safe.directory -e GIT_CONFIG_VALUE_0='*' \
  renovate/renovate:44.125.2 \
  --platform=local --dry-run=lookup --binary-source=install \
  --report-type=file --report-path=/out/report.json
```

No renovate.json in the input: Renovate defaults (no presets). The report is a point-in-time capture;
registry answers (newVersion, releaseTimestamp) change daily, so tests must treat it as a frozen fixture.

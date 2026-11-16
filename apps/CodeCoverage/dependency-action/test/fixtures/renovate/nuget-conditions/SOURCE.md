# Source: nuget-conditions

- Tool: Renovate 44.125.2, Docker image
  `renovate/renovate:44.125.2@sha256:a7dec3432d62b39872058424f0490829413b3f597f5d72fb41b9b7bd57ccac77` (the lookup's pin,
  `src/lookup/renovate.ts:11`)
- Captured: 2026-11-15 on Windows 11 + Docker 29.9.2, for multi-target-dependency-split-PRD spike S1
- Input: synthetic, written for S1 (`input/` is the full tree, committed once as a local git repository):
  - `Split/Split.csproj`: `net10.0;net11.0`. FileSystemGlobbing appears twice under `'$(TargetFramework)' == '…'`
    conditions with different versions (10.0.5, 11.0.0), DependencyInjection twice with the same version (10.0.5), and
    Options under an `IsTargetFrameworkCompatible` condition.
  - `Cpm/`: central package management, with Hosting as two conditioned `PackageVersion` items (10.0.5, 11.0.0) and a
    versionless `PackageReference`.
- Config: `renovate-config.json` (`onboarding: false`, `requireConfig: ignored`, `enabledManagers: ["nuget"]`)

Exact command (no token; nuget.org needs none):

```sh
docker run --rm -v <repo>:/usr/src/app -v <out>:/out -v <config>:/config/renovate.json:ro -w /usr/src/app \
  -e LOG_LEVEL=info -e RENOVATE_CONFIG_FILE=/config/renovate.json \
  -e GIT_CONFIG_COUNT=1 -e GIT_CONFIG_KEY_0=safe.directory -e GIT_CONFIG_VALUE_0='*' \
  renovate/renovate:44.125.2@sha256:a7dec… --platform=local --dry-run=lookup --report-type=file --report-path=/out/report.json
```

Registry answers (newVersion, timestamps) are a point-in-time capture: tests must treat this as a frozen fixture.

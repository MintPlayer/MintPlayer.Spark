# Source: custom-managers

- Tool: Renovate 44.125.2, Docker image `renovate/renovate:44.125.2@sha256:a7dec3432d62b39872058424f0490829413b3f597f5d72fb41b9b7bd57ccac77`
  (the digest `lookup/renovate.ts` pins)
- Captured: 2026-11-15 on Windows 11 + Docker Desktop (engine 29.9.2, linux/amd64), no token
- Input: `input/` is the full tree, a synthetic sample of SeoTester's Chromium image at Playwright 1.63.0
  (dependency-companion-pins-PRD spikes S1 and S3):
  - `chromium/Dockerfile`: `FROM mcr.microsoft.com/playwright:v1.63.0-noble` and `playwright-core@1.63.0`;
  - `docker-compose.yml`: the own image `ghcr.io/mintplayer/seotester-chromium:1.63.0`;
  - `argstage/Dockerfile`: the same base through `ARG PLAYWRIGHT=1.63.0` and `FROM …:v${PLAYWRIGHT}-noble` (S3);
  - `renovate.json`: two regex `customManagers` that tie `playwright-core@` and the compose tag to
    `mcr.microsoft.com/playwright` (datasource `docker`, `extractVersionTemplate: ^v(?<version>[0-9.]+)-noble$`) (S1).
- Config: the bot's own `renovateConfig()` with no tracks, mounted as `/config/renovate.json`.

Exact command (the bot's `dockerArgs`, `lookup/renovate.ts`):

```sh
docker run --rm -v <repo>:/usr/src/app -v <out>:/out -v <config>:/config/renovate.json:ro -w /usr/src/app \
  -e LOG_LEVEL=info -e RENOVATE_CONFIG_FILE=/config/renovate.json \
  -e GIT_CONFIG_COUNT=1 -e GIT_CONFIG_KEY_0=safe.directory -e GIT_CONFIG_VALUE_0='*' \
  renovate/renovate:44.125.2@sha256:a7dec3432d62b39872058424f0490829413b3f597f5d72fb41b9b7bd57ccac77 \
  --platform=local --dry-run=lookup --binary-source=install --report-type=file --report-path=/out/report.json
```

What it shows:

- S1: the regex deps are reported under manager `regex`, datasource `docker`, with `replaceString`
  `playwright-core@1.63.0` / `seotester-chromium:1.63.0` and undecorated new values (`1.64.1`, `1.65.1`).
- S3: the `ARG` stage is reported with the *expanded* `replaceString` `mcr.microsoft.com/playwright:v1.63.0-noble`,
  which is not in the file.

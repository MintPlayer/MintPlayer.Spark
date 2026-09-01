# `coverage-upload` action

Uploads coverage reports to a [CodeCoverage](../README.md) server and, optionally, waits for the
build to finalize and publishes the result as step outputs.

```yaml
- uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@coverage-upload-v1
  with:
    url: https://coverage.mintplayer.com
    token: ${{ secrets.COVERAGE_TOKEN }}
    files: |
      tests/*/coverage/**/coverage.cobertura.xml
    disable-search: true
    finish: true
```

The full input and output surface is declared in [`action.yml`](action.yml); the HTTP contract it
speaks is [`docs/code-coverage/upload-api.md`](../../../docs/code-coverage/upload-api.md).

## Why it lives here

Beside the server it talks to, so a change to the upload API and the change to the action that
consumes it are the same pull request — and so
[`pull-request.yml`](../../../.github/workflows/pull-request.yml) can run this action against a
locally-hosted CodeCoverage instance with `uses: ./apps/CodeCoverage/action`, which no other
arrangement allows. The rationale is [`docs/coverage_action_home_PRD.md`](../../../docs/coverage_action_home_PRD.md).

It is deliberately **outside** the repo's npm workspaces: a CommonJS node20 bundle with its own
lockfile and its own TypeScript version, none of which should move when the Angular workspace moves.
`npm install` here, not at the repo root.

## Building

`dist/index.js` is **committed, and it is what `runs.main` executes** — editing `src/` without
rebuilding ships nothing.

```bash
npm ci
npm test          # 50 tests, vitest
npm run build     # ncc -> dist/index.js
```

The entry point is `src/index.ts`, which exists only to call `run()` from `src/main.ts`. Bundling
`main.ts` directly produces a bundle that defines `run` and never calls it — an action that exits 0
having done nothing. `pull-request.yml` rebuilds and fails on any drift, so a stale bundle cannot
merge.

## Talking to a server older than the action

The action is consumed from a git ref while the server ships as a docker image a VPS pulls, so the
two are never guaranteed to match — not even when they share a commit. On start-up the action asks
`GET /api/uploads/capabilities` what the server supports and treats **404 as contract 0**, which is
what every image deployed before that endpoint existed reports. Inputs the server cannot honour are
reported as warnings rather than silently ignored; see [`src/capabilities.ts`](src/capabilities.ts).

# Handoff: coverage uploads from mintplayer-ng-bootstrap

Change set for a session in `C:\Repos\mintplayer-ng-bootstrap`. Goal: run test
coverage in that repo's CI and upload the lcov reports to the self-hosted
service at https://coverage.mintplayer.com (first external consumer). The
Codecov/Codacy GitHub Apps have already been removed from the org.

Everything below was verified against both repos on 2026-08-13.

## Prerequisites (server side — mostly done)

- ✅ GitHub App "coverageproduction" is installed on the MintPlayer org, so the
  repo is already synced server-side (a token upload 404s for unknown repos;
  this one is known).
- ⚠️ **Verify the `COVERAGE_TOKEN` org secret**: a `covt_` token is bound to
  the account page it was created on. It must come from the **org** page
  https://coverage.mintplayer.com/a/MintPlayer ("Upload tokens" card, scope
  "All repositories of MintPlayer" or repo-scoped to mintplayer-ng-bootstrap).
  A token minted on the personal account page (`/a/PieterjanDeClippel`)
  **cannot** upload for org repos — it fails with a 404 that misleadingly
  reads like "repository unknown". If in doubt: revoke, re-create on the org
  page, update the secret. Tokens never expire; rotation is manual (Revoke).
- Note: since the repo is **public**, OIDC needs no secret at all (see the
  auth choice below). The token is still the right thing for private repos
  and non-Actions CI later.

## 1. Vitest config edits (3 files)

Ten of the thirteen JS test projects already emit lcov. Three don't — two have
no `coverage` block, one has a block without a `reporter` (so Vitest's default
`['text','html','clover','json']` applies and **no lcov.info is written**;
clover/json don't parse server-side):

**`libs/mintplayer-react-bootstrap/vite.config.mts`** — inside the existing
`test: { … }` block (after `setupFiles`):

```ts
    coverage: {
      provider: 'v8' as const,
      reporter: ['lcov'],
      reportsDirectory: '../../coverage/libs/mintplayer-react-bootstrap',
    },
```

**`libs/mintplayer-vue-bootstrap/vite.config.mts`** — same block after its
`setupFiles`, with `reportsDirectory: '../../coverage/libs/mintplayer-vue-bootstrap'`.

**`libs/mintplayer-web-components/vite.config.mts`** — the `coverage` object
exists (~line 76); add one line:

```ts
        reporter: ['lcov'],
```

No `project.json` changes needed anywhere: all 13 test targets already declare
the coverage dirs as `outputs`, so Nx remote-cache replays restore the lcov
files too.

## 2. The test command

```
npx nx run-many --target=test --exclude=api --coverage --parallel=2 --output-style=stream
```

Two load-bearing choices:

- **`--exclude=api`**: `api:test` is `nx:run-commands` → `dotnet test …`, and
  run-commands forwards unknown flags verbatim (`forwardAllArgs` defaults to
  true), so `--coverage` would reach VSTest and fail the step. The API keeps
  its own dedicated `dotnet test` step that both workflows already have.
- **`run-many`, not `affected`** for any run that uploads: an affected subset
  emits lcov for only some projects, which the service records as a coverage
  collapse for that commit.

Result: 13 lcov files at `coverage/apps/*/lcov.info` + `coverage/libs/*/lcov.info`.

## 3. Workflow edits

### `publish-master.yml` (job `build`) — the primary target

Replace the existing `Test` step (`npx nx affected --target=test --watch=false
--parallel=true`, ~line 54) and add the upload right after, before "Upload
dist artifact":

```yaml
    - name: Test (with coverage)
      timeout-minutes: 15
      # run-many, not affected: a partial set reads as a coverage collapse.
      # --exclude=api: nx:run-commands forwards --coverage verbatim to
      # `dotnet test` (VSTest), which rejects it; the API has its own step.
      run: npx nx run-many --target=test --exclude=api --coverage --parallel=2 --output-style=stream

    - name: Upload coverage
      uses: MintPlayer/CodeCoverage/action@master
      with:
        url: https://coverage.mintplayer.com
        token: ${{ secrets.COVERAGE_TOKEN }}
        files: |
          coverage/apps/*/lcov.info
          coverage/libs/*/lcov.info
        disable-search: true
        flags: unit
        finish: true
        fail-ci-if-error: true
```

Notes:

- All matched files go in **one** request (rate limit is 60/min per token —
  nowhere close).
- `disable-search: true` matters: with search on, a `files` glob that matches
  nothing silently falls back to auto-detection, which would also happily
  upload unparsable stray reports.
- `fail-ci-if-error: true` while wiring this up (the default silently turns
  failures into warning annotations). Once stable, consider relaxing it so a
  coverage-service outage can't block `deploy` (which `needs: build`).
- `finish: true` finalizes immediately; without it the build closes on its own
  2 minutes after the last upload.

**Tokenless alternative (recommended eventually)**: the repo is public, so the
upload can authenticate with the workflow's own OIDC identity — no secret to
manage. The `build` job's existing `permissions` block **already grants
`id-token: write`** (it's there for the artifact attestations), so this is a
one-line difference: replace the `token:` line with `use-oidc: true`. The
`url` doubles as the OIDC audience and must be exactly
`https://coverage.mintplayer.com` (any deviation → 401).

Incidental fix that comes free with the step swap: the current `Test` step
runs `nx affected` with no `--base`/`--head` and **before** any .NET SDK
setup, so a push whose affected set includes `api` would fail the step before
the dedicated API test step further down. `run-many --exclude=api` removes
that hazard.

### `pull-request.yml` — optional, second step

Leave it alone for the first iteration. If PR coverage is wanted later:

- The `Unit tests` step must switch `affected` → `run-many --exclude=api
  --coverage` (partial-set problem above), which costs PR time.
- Fork PRs get neither secrets nor an OIDC token, so guard the upload step:
  `if: github.event_name != 'pull_request' || github.event.pull_request.head.repo.full_name == github.repository`.
- The action automatically sends the PR head SHA + head ref + PR number, so
  commit association is correct out of the box.

## 4. README badge

`README.md` lines 7–9 ("Version info" table): replace the dead codecov badge
(`codecov.io/gh/MintPlayer/mintplayer-ng-bootstrap/...`) in the "Code
coverage" column with:

```markdown
[![Coverage](https://coverage.mintplayer.com/badge/MintPlayer/mintplayer-ng-bootstrap.svg)](https://coverage.mintplayer.com/r/MintPlayer/mintplayer-ng-bootstrap)
```

Public repo → no badge token needed. `?branch=…` exists but is unnecessary:
the plain badge follows the default branch. That was the **only** codecov
remnant in the repo (no codecov.yml, no workflow steps, no packages — the
codecov mentions under `docs/prd/*.md` are design prose citing prior art;
leave those).

The badge shows "unknown" until the first master build finalizes and promotes
to the repo's latest coverage.

## 5. Verification

1. Merge to master → `publish-master` runs. The upload step logs each file
   and ends with `202 Accepted`; the action exposes `build-id`/`session-id`
   outputs.
2. https://coverage.mintplayer.com/r/MintPlayer/mintplayer-ng-bootstrap — the
   commit appears immediately; per-file coverage and the tree view appear once
   parsing finishes (seconds; `finish: true` skips the 2-min debounce).
3. Badge URL renders a percentage instead of "unknown".
4. If the upload 404s with "unknown here … or the token doesn't grant it":
   that's the token-account binding from the prerequisites — re-mint on
   `/a/MintPlayer`.

## Change list (summary)

| File | Edit |
|---|---|
| `libs/mintplayer-react-bootstrap/vite.config.mts` | add `coverage` block (v8, lcov) |
| `libs/mintplayer-vue-bootstrap/vite.config.mts` | add `coverage` block (v8, lcov) |
| `libs/mintplayer-web-components/vite.config.mts` | add `reporter: ['lcov']` to existing block |
| `.github/workflows/publish-master.yml` | Test step → `run-many --exclude=api --coverage`; add upload step (snippet above) |
| `README.md` (lines 7–9) | codecov badge → coverage.mintplayer.com badge |
| `pull-request.yml` | leave for iteration 2 (see §3) |

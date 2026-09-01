# Coverage Upload action

Uploads coverage reports from a workflow run to a self-hosted
[Coverage](https://github.com/MintPlayer/MintPlayer.Spark/tree/master/apps/CodeCoverage) instance. The action is a thin,
format-agnostic uploader — parsing happens server-side (lcov, Cobertura, JaCoCo, …),
so new report formats need no action release.

Multiple invocations from one workflow run (matrix jobs, split suites) bundle into a
single build keyed by `run_id` + `run_attempt`; the server merges them (max semantics).

## Tokenless (OIDC) — preferred

Public repositories need no secret at all: grant the job `id-token: write` and the
action authenticates with a GitHub-signed OIDC token (audience = the server URL).

```yaml
jobs:
  test:
    runs-on: ubuntu-latest
    permissions:
      id-token: write
      contents: read
    steps:
      - uses: actions/checkout@v4
      - run: dotnet test --collect:"XPlat Code Coverage"
      - uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@master
        with:
          url: https://coverage.example.com
          use-oidc: true
          flags: unit
          finish: true   # on the last (or only) upload job
```

## With an upload token

Create a token in the web UI (account page → Upload tokens; account- or repo-scoped)
and store it as a repository secret:

```yaml
      - uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@master
        with:
          url: https://coverage.example.com
          token: ${{ secrets.COVERAGE_TOKEN }}
          flags: unit
          finish: true
```

Without `finish`, the server auto-finalizes ~2 minutes after the last upload
(30 minutes max).

## Inputs

| Input | Description |
|---|---|
| `url` | Base URL of the Coverage server (required) |
| `token` | Upload token `covt_…` (account- or repo-scoped, created in the web UI) |
| `use-oidc` | Authenticate with GitHub Actions OIDC instead of a token (`false`; needs `id-token: write`) |
| `files` | Explicit files/globs (comma- or newline-separated); auto-detects well-known names when omitted |
| `directory` | Auto-detection root (default: workspace) |
| `flags` | Comma-separated labels for this upload |
| `name` | Session name (default: job name) |
| `finish` | Finalize the build after this upload (`false`) |
| `fail-ci-if-error` | Fail the step on upload errors (`false`) |
| `disable-search` | Only use explicitly listed `files` (`false`) |
| `wait-for-finalize` | Wait for parsing to finish and publish the result as outputs (`false`) |
| `wait-timeout` | Seconds to wait (`1800` — the server's own ceiling) |
| `wait-poll-interval` | Seconds between polls (`5`, easing off after the first minute) |

## Gating a PR on the result

By default the upload is fire-and-forget: parsing is asynchronous and an upload should
not hold a CI job hostage to parser latency. Set `wait-for-finalize: true` when you
want the number in the same job — typically to fail a PR whose coverage dropped.

```yaml
      - uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@master
        id: coverage
        with:
          url: https://coverage.example.com
          use-oidc: true
          finish: true              # without this you wait out the ~2-minute debounce
          wait-for-finalize: true
          fail-ci-if-error: true    # a failed parse should fail the gate, not pass it

      - name: Coverage may not decrease
        if: steps.coverage.outputs.baseline-line-rate != ''
        env:
          NOW: ${{ steps.coverage.outputs.line-rate }}
          WAS: ${{ steps.coverage.outputs.baseline-line-rate }}
          BASE: ${{ steps.coverage.outputs.baseline-sha }}
        run: |
          echo "$NOW% now, $WAS% on $BASE"
          awk -v now="$NOW" -v was="$WAS" 'BEGIN { exit (now >= was - 0.1) ? 0 : 1 }' ||
            { echo "::error::coverage dropped from $WAS% to $NOW%"; exit 1; }
```

**Pair it with `finish: true`.** Without it the wait includes the server's ~2-minute
debounce, which will dominate everything else.

`baseline-*` is the latest coverage on the default branch excluding this commit — so a
no-decrease check needs no second API call. It is **empty on a first upload**, where a
ratchet has nothing to compare against and must pass; the `if:` above is what makes
that case pass rather than crash.

`line-rate` is empty when there are no coverable lines. `0/0` is no data, not 100% —
treating it as a percentage is how an uninstrumented file scores perfectly.

Outputs: `state`, `build-status`, `finalize-reason`, `lines-covered`,
`lines-coverable`, `line-rate`, `branches-covered`, `branches-total`, `branch-rate`,
`files-count`, `commit-url`, `baseline-sha`, `baseline-lines-covered`,
`baseline-lines-coverable`, `baseline-line-rate` — plus `build-id` and `session-id`,
which are set without waiting.

Branch on **`state`** and nothing else: `Complete`, `CompleteWithErrors` (a real number
that under-counts, because a report failed to parse) or `InFlight` (never seen after a
successful wait). With `fail-ci-if-error: true`, `CompleteWithErrors` and a timeout both
fail the step. The full contract, including how to poll it yourself, is in
[docs/upload-api.md](../docs/upload-api.md).

On `pull_request` events the action reports the PR **head** SHA (never the ephemeral
merge commit), and it sends `git ls-files` so the server can match report paths that
carry CI-machine prefixes or unstated source roots.

## README badge

```markdown
[![Coverage](https://coverage.example.com/badge/OWNER/REPO.svg)](https://coverage.example.com/r/OWNER/REPO)
```

The repo page has a ready-to-paste snippet (including the `?token=` for private
repositories, and `?branch=` for a non-default branch).

## Versioning

Pin `MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@master` for now; a `v1` tag is cut from master
once the input surface settles — after that, pin `@v1`.

## Development

`npm run build` regenerates `dist/` (committed — node20 actions run the bundle). CI
fails when `dist/` is stale. This folder is consumed as
`MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@<ref>`; if it ever moves to the Marketplace it needs
its own repository with `action.yml` at the root.

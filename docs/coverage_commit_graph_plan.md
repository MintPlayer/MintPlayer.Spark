# Plan — commit graph on the repository page

**Implements:** [`coverage_commit_graph_PRD.md`](coverage_commit_graph_PRD.md) (option C: treeless
bare mirror + git CLI, server-side lane layout) ·
**Issue:** [#504](https://github.com/MintPlayer/MintPlayer.Spark/issues/504) ·
**Branch:** `feat/504-commit-graph` · **Base:** `master` @ `b0395764` ·
**Release:** server deploy only, with a new Docker volume and `git` in the runtime image. No npm or
NuGet version bump, because nothing under `libs/` is published by this work. ·
**Status:** Proposed — spikes not run

All code lands as **one pull request** in `MintPlayer.Spark`: image, compose, server, SPA, docs and
tests, per `CLAUDE.md`. There is no follow-up PR and no phase 2.

`apps/CodeCoverage` is production. Every milestone is deploy-safe on its own:
- the state document is additive
- the endpoint returns `unavailable` until a mirror exists
- the panel renders nothing harmful in that state
- no existing document is re-keyed and no existing behaviour changes, unless Q2 says otherwise

## Open questions for the owner

These are decisions, not research. Each changes what gets built.

| # | Question | Default if unanswered |
|---|---|---|
| Q1 | Disk budget on the VPS for mirrors, and the per-repository size/ref quota | 2 GB per repository, 50k refs, total budget alerted at 20 GB |
| Q2 | `Commit.ParentSha` is the **PR base tip** for PR commits (`ForgeEventsRecipient.cs:82`), so `CoverageDeltaVsParent` is measured against it. Defect or intended? | Leave as is; the graph draws true parents from the mirror regardless |
| Q3 | Which of the PRD §4.5 "later" features to pull into this PR (hover path, search, first-parent view, sparkline, drop filter) | MVP only |
| Q4 | Default view: all branches + tags, or default branch + its ancestry with a toggle? | All refs, like SourceTree's "All branches" |
| Q5 | Clone public repositories that have no installation, anonymously? | Yes. No API budget is used, and they are already listed. |

## Sequencing

```
S1, S2, S3, S4, S7 (mirror spikes)       S5 (layout)      S9 (render prototype)
  └─> M1 image + volume + GitRunner        │                 │
        └─> M2 mirror service + triggers ──┤                 │
              (S8 runs here)               │                 │
              └─> M3 snapshot + layout <───┘                 │
                    └─> M4 endpoint (S6 runs here)           │
                          └─> M5 panel <─────────────────────┘
M6 tests accompany each milestone; M7 docs any time after M4; M8 deploy + dogfood last
```

The spikes are throwaway: scratch code in the session scratchpad, results recorded below, nothing
committed except the numbers. S5 and S9 can run in parallel with the mirror spikes. **If S1 fails**,
for example because the App lacks `contents: read` and the owner declines the permission change, stop
and revisit PRD §3 before M1. Option A becomes the fallback for GitHub only.

## Spikes (criteria in PRD §7)

| # | Result | Date | Evidence |
|---|---|---|---|
| S1 token auth, no leak, `contents: read` | — | | |
| S2 clone size and time on the VPS | — | | |
| S3 commit-only walk, no lazy fetch | — | | |
| S4 incremental fetch correctness | — | | |
| S5 server lane layout speed and stability | — | | |
| S6 page + overlay read time | — | | |
| S7 hostile repository | — | | |
| S8 claim TTL vs long clone | — | | |
| S9 per-row SVG scroll, palette contrast | — | | |

## Milestones

### M1 — `git` in the image, a volume, and a safe runner

- `CodeCoverage/Dockerfile` runtime stage: `apt-get install --no-install-recommends git`, then
  create `/data/git` owned by `app`, then `USER app`.
- `docker-compose.yml`: a named volume mounted at `/data/git`. Mirror path configurable through
  `CodeCoverage:CommitGraph:Root`, so tests use a temp directory.
- `GitRunner`: the only class that starts `git`.
  - Argument array, never a shell string.
  - Credentials only through `GIT_CONFIG_COUNT`/`KEY_n`/`VALUE_n`.
  - `GIT_TERMINAL_PROMPT=0`.
  - Timeout with process-tree kill.
  - stderr scrubbed before logging.
  - A per-repository file lock.
- `IForgeIntegration.GetGitAccessAsync(repository)`: GitHub implementation via
  `GitHubInstallationService`, plus an anonymous result for public repositories without an
  installation. GitLab and Bitbucket stubs declare the capability unsupported, so the conformance
  test passes.

**Done when:** S1 and S7 criteria hold as automated tests against a local bare repository, with no
network. The token-absence assertions check without printing.

### M2 — Mirror service, triggers and deletion

- `CommitGraphMirror`:
  - `EnsureClonedAsync` / `FetchAsync`, using `--filter=tree:0`, heads and tags refspecs only,
    `--prune`, and `fetch.writeCommitGraph=true`
  - quota checks
  - `DeleteAsync`
- `RepositoryGraphState` document (`RepositoryGraphStates/{provider}/{repoId}`), additive.
- `FetchCommitGraphRecipient`, broadcast with `BroadcastOnceAsync("graph-fetch:{repoId}")` from:
  - the push handler (`GitHubEventsRecipient.cs:301-327`)
  - repository-added events
  - the endpoint, when the mirror is stale
- An hourly `ISparkCronJob` safety net.
- Deletion wired into:
  - disconnect
  - `installation` / `installation_repositories` removal
  - `DeleteDataAction`
  - `ReconcileForgeState`

**Done when:** S4 and S8 criteria hold, and a test per deletion trigger asserts the directory is gone.

### M3 — Snapshot and lane layout

- `CommitGraphReader`: parses `git log --branches --tags --date-order -z` and `git for-each-ref`;
  streams rows. Date order is newest first with children before parents, as SourceTree does
  (PRD §4.3).
- `LaneLayout`: the straight-branch algorithm (PRD §4.3), written from Vigier's description, as a
  streaming top-down pass. **No code from VS Code Git Graph**, whose licence forbids derivative works.
- `CommitGraphSnapshot`:
  - **lazy:** it lays out only up to the furthest row requested and extends on demand
  - a lane checkpoint every 500 rows, plus a sha → row map of the rows laid out so far
  - keyed `(repoId, refsHash, filter)`, held in a bounded in-memory cache

**Done when:** S5 criteria hold, including that page-by-page extension equals one full pass. Golden-file
tests cover these topologies, each built with the git CLI in a temp directory using fixture identities
such as `Test Author <author@example.invalid>`, never real people. Fixed `GIT_COMMITTER_DATE` values
make date order deterministic, and include one skewed clock where a child is older than its parent:
- linear
- one merge
- octopus merge
- criss-cross merge
- a branch that ends
- a tag on a merge
- force-push before and after

### M4 — Endpoint

- `GET /api/browse/repos/{provider}/{owner}/{name}/graph` in `BrowseController`:
  - `[SparkAuthorize("Browse","Coverage")]`, `[EnableRateLimiting("browse")]`
  - owners fetched first, then `ResolveAsync(…IsVisible)`, then `Refuse()` on null (PRD §5.1)
  - `from` / `count ≤ 200` / `ref` / `at`
  - coverage overlay by `LoadAsync` of deterministic commit ids
- `services/browse.service.ts` client method and response types.

**Done when:** S6 criteria hold, and visibility tests prove a hidden private repository, a missing
repository and an unknown provider return identical responses. That means 401 for anonymous and 404
for signed-in with the same body, including while the hidden repository's mirror is syncing.

### M5 — The panel

- `app-repo-commit-graph-panel` in the Repository branch of `po-detail-page.component.ts:44-48`.
- Virtual datatable with fixed rows and one SVG per row (PRD §4.5).
- Coverage-encoded nodes, ref pills, merge styling.
- An all-refs / single-branch selector.
- Row click goes to the commit page.
- Keyboard navigation.
- Light and dark palettes via `--bs-*` and a `MutationObserver`.
- Syncing, empty and unavailable states.
- Respects ng-bootstrap rules: `.form-control` only inside `<bs-form>`, grid classes only inside
  `<bs-grid>`.

**Done when:** S9 criteria hold on the prototype-turned-panel. Vitest covers the row-SVG path builder
and the states. A manual check through the Playwright MCP on the local app covers this repository's
graph in both themes.

### M6 — Tests (alongside each milestone, run once at the end)

`CodeCoverage.Tests` for M1–M4 and vitest for M5. The full sweep runs **once**, after M5, through
`npm run test:affected` with the Developer licence (`CLAUDE.md`). No intermediate full runs.

### M7 — Docs

- `docs/code-coverage/commit-graph.md`: what it shows, how it syncs, quotas, deletion.
- `docs/code-coverage/hosting.md`: the new volume and the disk budget (Q1).
- `docs/code-coverage/README.md`: a link.
- Update the PRD status line and fill in the spike table above.

### M8 — Deploy and dogfood

- Add the volume on the VPS compose file.
- Deploy.
- Confirm on `coverage.mintplayer.com/po/repository/Repositories%2Fgithub%2F1006469943`:
  - the graph renders this repository's full history
  - a push appears within a minute
  - a private repository's graph is refused to an anonymous browser exactly as its page is
- Record the clone size and time next to S2's numbers.

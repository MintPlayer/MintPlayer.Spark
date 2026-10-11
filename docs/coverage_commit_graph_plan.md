# Plan — commit graph on the repository page

**Implements:** [`coverage_commit_graph_PRD.md`](coverage_commit_graph_PRD.md) (option C: treeless
bare mirror + git CLI, server-side lane layout) ·
**Issue:** [#504](https://github.com/MintPlayer/MintPlayer.Spark/issues/504) ·
**Branch:** `feat/504-commit-graph` · **Base:** `master` @ `b0395764` ·
**Release:** server deploy, plus a new sidecar image `codecoverage-git` (D1), a two-host compose
network (D4, D18) and a named volume for the mirrors (D8). **Packages:** a new NuGet
`MintPlayer.Spark.Dashboards` at `11.0.0-preview.*`, and a **22.x minor** of `@mintplayer/ng-spark`
for the new entry points (D11). Neither major moves. Check the version diff in review. ·
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
| ~~Q1~~ | ~~Disk budget and quotas~~ | **Decided (D7, D8):** numbers set from S2; mirrors on the 10 GB Hetzner volume |
| ~~Q5~~ | ~~Anonymous clones~~ | **Decided (D6):** anonymous, stricter quotas |
| ~~Q6~~ | ~~Where the mirrors live~~ | **Decided (D8):** a named volume on the root disk, with a software budget and a free-space floor |
| ~~Q7~~ | ~~Dashboard package~~ | **Decided (D11–D14):** a Spark dashboard package built on live virtual POs; SeoTester migrates onto it; RavenDB statistics are a built-in provider; optional time-series history |
| ~~Q2~~ | ~~`ParentSha`~~ | **Decided (D15):** not a defect; only a stale comment to fix |
| ~~Q3~~ | ~~Later features~~ | **Decided (D17):** all of them |
| ~~Q4~~ | ~~Default view~~ | **Decided (D16):** all refs, lanes capped |

The rows below are the original wording, kept for the record.
| Q2 | `Commit.ParentSha` is the **PR base tip** for PR commits (`ForgeEventsRecipient.cs:82`), so `CoverageDeltaVsParent` is measured against it. Defect or intended? | Leave as is; the graph draws true parents from the mirror regardless |
| Q3 | Which of the PRD §4.5 "later" features to pull into this PR (hover path, search, first-parent view, sparkline, drop filter) | MVP only |
| Q4 | Default view: all branches + tags, or default branch + its ancestry with a toggle? | All refs, like SourceTree's "All branches" |

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
| S1 token auth, no leak, `contents: read` | ✅ **Permission:** the production App (`coverageproduction`) already has `contents: write`, which includes read (public `GET /apps/coverageproduction`), so no permission change is needed. Token clone and the leak assertions are automated tests against a local authenticated git server (M1). | 2026-10-11 | `gh api apps/coverageproduction` |
| S2 clone size and time on the VPS | ✅ **VPS (alpine/git 2.47.2 in docker; times include about 0.4 s of container start):** Spark 1.28 MB, clone 1.1 s; aspnetcore 65k commits 34.5 MB, clone 2.2 s, graph 1.0 s, walk 1.3 s; vscode 192k commits 119.5 MB, clone 5.7 s, graph 2.3 s, walk 2.9 s. 0.53 KB/commit, so 100k ≈ 53 MB: passes. Local proxy, for comparison: Spark 371 commits 1.3 MB 1.5 s; aspnetcore 65k commits 34.5 MB 5.4 s; vscode 192k commits 119.5 MB 16.9 s. About 0.6 KB/commit, so 100k ≈ 55–62 MB, at the 60 MB limit. The VPS run is still owed. | 2026-10-11 | scratchpad `spikes/mirrors/*.s2.log` |
| S3 commit-only walk, no lazy fetch | ✅ **with D25** (`--no-mailmap`). Without it the first walk lazily fetched all HEAD trees. Full walk ≈ 13 µs/row (100k ≈ 1.3 s; vscode 192k 2.5 s). The commit-graph does not speed up the full walk; it speeds up the first rows 6–30×. First 200 rows: 80 ms aspnetcore; **213 ms vscode all refs** (5.9k tips; 13 ms over target, accepted), 50 ms one branch. `rev-list --count` 159 ms on vscode. | 2026-10-11 | `spikes/mirrors/*.s3.log` |
| S4 incremental fetch correctness | ✅ All 5 cases plus tag deletion correct; each fetch 0.3–0.55 s (git daemon, protocol v2, `filter tree:0`). The explicit tag refspec makes `--prune` remove deleted tags. | 2026-10-11 | `spikes/fetch/s4.sh`, `*.out` |
| S5 server lane layout speed and stability | ✅ **JoinLeft (D27).** vscode 192k: layout 0.1–0.25 s, parse+layout < 1 s, peak 151 MB working set. First page < 50 ms cold, 0.01 ms warm. Paged equals full pass on 0 mismatches over 192k + 100k rows. Deterministic SHA-256; golden test passes. Width 92 vs git 90, within the relaxed criterion. | 2026-10-11 | `spikes/layout/src`, `out/` |
| S6 page + overlay read time | — | | |
| S7 hostile repository | ✅ **with D26.** Submodule `file://`: 0 accesses (control proved the path leaks without the recipe). Hooks: 0 markers (controls fire). 60k refs: clone 13.8 s, 7.5 MB, log 251 ms once `--no-tags` is dropped. Tree kill: no orphans. | 2026-10-11 | `spikes/fetch/s7*.sh`, `*.out` |
| S8 claim TTL vs long clone | — | | |
| S9 per-row SVG scroll, palette contrast | — | | |
| S10 host-disk accuracy from the container, live virtual PO | ✅ **Disk (VPS):** `df -B1 /` inside `coverage-app` reports exactly the host's numbers (same total, used and available bytes: an overlay on the host root fs). `DriveInfo("/")` in a container therefore measures the host disk; no bind mount is needed. **CPU/RAM:** cgroup v2 `memory.max` and `cpu.max` are `max` (no limits), so the provider reads `memory.current` and falls back to `/proc/meminfo` for the host total. The live-PO half is covered by M5a tests. | 2026-10-11 | VPS script, `df` inside and outside the container |
| S11 dashboard history on RavenDB time series | ✅ **with D29** (single-valued series only; 6 values per entry silently disabled rollups and retention). Retention and rollup ran; 7-day read 2.2 ms, 30-day/200 buckets 3–4.5 ms warm; 0 duplicates under the lease; 0 revisions. Developer licence only. | 2026-10-11 | `spikes/timeseries/*.log` |
| S12 avatars without email leakage | ✅ **with D28.** No-reply ids parsed locally (0 calls); one batched GraphQL call ≈ 1 point per 60 authors; needs the installation token. Bot logins map to the wrong account, so the numeric id is used; GitLab returns Gravatar URLs, treated as negative. The no-email test is a sketch until M3/M4. | 2026-10-11 | `spikes/avatars/*.log` |

## Milestones

### M1 — The sidecar image, the two-host network, the volume, and a safe runner

> Rewritten for D1, D3, D4, D5 and D8. `git` goes into a **new sidecar image**, not into the
> CodeCoverage runtime image.

- New project `apps/CodeCoverage/CodeCoverage.GitMirror`: a minimal ASP.NET Core app on
  `mcr.microsoft.com/dotnet/aspnet:11.0`, with `git` installed (`--no-install-recommends`), running as
  non-root. It hosts the internal API from PRD §4.1. It references **no** RavenDB or GitHub App
  package, which a test checks on the assembly's references.
- CI builds and publishes `ghcr.io/mintplayer/codecoverage-git:master` next to the app image. This
  needs a nx project and an image workflow following `codecoverage`'s.
- `docker-compose.yml`:
  - Service `codecoverage-git`.
  - Service `coverage-git` (D24 names). Network `coverage-git` (`internal: true`), holding only
    `coverage-app` and `coverage-git`, with no published port. A comment on the network and on both
    services says it must hold exactly these two hosts (D4).
  - Network `coverage-git-egress`, holding **only** `coverage-git`, for outbound clones (D18). Its
    comment says nothing else may join it.
  - `coverage-git` is **not** on `coverage-db`, `seo-raven` or `web` (D24). Add two rows to the
    networks comment's member table.
  - Extend `tools/verify-backend-networks.test.mjs`: assert both new networks' exact members, and that
    no network holds both `coverage-raven` and `coverage-git`.
  - Sidecar `mem_limit` (D22).
  - A named volume `codecoverage-git-data` at `/data/git`, mounted in the sidecar only (D8).
- `DiskGuard` in the sidecar enforces the total mirror budget and the host free-space floor before
  every clone and fetch, and measures each repository directory's size after a fetch (D8).
- Mirror path configurable (`GitMirror:Root`), so tests use a temp directory.
- Hooks off: clone with an empty `--template=`, and set `core.hooksPath` to an empty read-only
  directory (D5).
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
- An hourly `ISparkCronJob` safety net, covering **mirrored repositories only**.
- **D10:**
  - The endpoint is the only trigger that creates a mirror (lazy-on-view). Repository-added and push
    events act only when a mirror exists.
  - `LastViewedAtUtc` is stamped on every graph read.
  - A daily `EvictCommitGraphMirrorsCronJob` removes mirrors not viewed for N days.
  - Before a clone, the app evicts least-recently-viewed mirrors until the clone fits the budget,
    and enforces the per-installation cap.
  - An eviction counter for HostMetrics.
- **Tests:** eviction by age; eviction under budget pressure, oldest view first; a capped
  installation is refused while another installation still clones; an evicted repository re-clones
  on its next view.
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

### M5a — The Spark dashboard package (D11–D14; [spark_dashboards_PRD.md](spark_dashboards_PRD.md))

- **NuGet:** `libs/dashboards/MintPlayer.Spark.Dashboards`, shaped like the Settings package:
  - `net11.0`, version `11.0.0-preview.*`
  - `App_Data/{Model,programUnits,security,translations}.json`
  - an `ISparkBuilder` extension `spark.AddDashboards(...)`
  - the security slot `dashboards:administrators`
- **Package contents:**
  - `IDashboardProvider`, which fills a dashboard's attribute values
  - a hosted notifier pushing through `ISparkLiveNotifier`
  - a sampler with a compare-exchange lease that writes RavenDB time series to `SparkDashboards/{name}`,
    with revisions disabled and rollup/retention policies (D14)
  - a built-in **RavenDB statistics** provider: database size, document count, and per-collection
    count and size (D13)
- **npm:** new `@mintplayer/ng-spark` sub-entry points for the dashboard tile layout and the new
  renderers (meter, stat-with-threshold). `ng-spark` goes up a **22.x minor**.
- **SeoTester migrates (D13):**
  - `OperatorDashboard` becomes a package dashboard bound to its Administrators group.
  - Its custom endpoint and panel are deleted.
  - Update the route snapshot, `ReportSurfaceTests`, the model, translations, security and posture
    files, and `docs/seotester/README.md`.
- **Done when:** S10 (local half) and S11 pass. SeoTester's dashboard shows the same numbers as
  before the migration. A guide page is added at `docs/guide-dashboards.md`.

### M5b — CodeCoverage host metrics on the package ([spark_dashboards_PRD.md](spark_dashboards_PRD.md) §9.2)

> Updated for D11. HostMetrics is now a dashboard *provider* on the package (M5a), not a hand-written
> live PO.

- An `Administrators` group in CodeCoverage's `security.json`, bound to `dashboards:administrators`.
  It is seeded by the owner's **stable forge user id**, never by login. Regenerate the security
  posture.
- `HostMetrics.json`: virtual, `"live": true`, object `hostmetrics/main`.
- `HostMetricsActions.OnLoadAsync`: host disk from `DriveInfo("/")`, container CPU and memory, and mirror totals from `RepositoryGraphState`.
- A notifier `BackgroundService` running every 15 s, with a 5 s read cache.
- A program-unit entry visible to Administrators only, plus a `RepositoryGraphState` query with a
  size column.

**Done when:** S10 passes. Tests show an anonymous user and a signed-in non-admin get the standard
denial on load and on live subscribe.

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

# CodeCoverage — the commit graph

A SourceTree-style commit graph on the repository page (`/po/repository/Repositories%2F{provider}%2F{id}`), with
coverage on every node. Issue [#504](https://github.com/MintPlayer/MintPlayer.Spark/issues/504). The design and its
decision log (D1–D39) are in [`../coverage_commit_graph_PRD.md`](../coverage_commit_graph_PRD.md); the build record and
spike results are in [`../coverage_commit_graph_plan.md`](../coverage_commit_graph_plan.md). This page describes what
shipped. Where it and the PRD's body disagree, this page and the PRD's §0 table win.

Paths below are relative to `apps/CodeCoverage/`.

## 1. What it shows

The panel `app-repo-commit-graph-panel` (`CodeCoverage/ClientApp/src/app/components/repo-commit-graph-panel/`) sits in
the Repository branch of the detail page, next to the badge, trend and setup panels.

- **Every branch and tag** by default (D16), newest first in `git log --date-order`: no parent is ever shown above one
  of its children, even under clock skew. A selector narrows the view to one branch and its ancestry.
- **Lanes** laid out on the server by the JoinLeft rule (D27): the default branch's chain stays in one lane. Visible
  lanes are capped at 12 (4 below 576 px wide) with an overflow marker (`LANE_CAP`, `LANE_CAP_MOBILE`).
- **The node encodes coverage:** filled on a sequential scale by coverage %, a ring for "no report", hollow grey for
  "pending or partial". The row shows coverage %, its delta against the parent, ref pills, the author (avatar or
  initials), the date and the short SHA. A row click opens the existing commit page.
- **Tools:** hover highlights a branch path; first-parent view; search by message or author; a coverage sparkline
  along the selected (or default) branch; a **Coverage drops** list (commits whose coverage fell by more than a
  threshold, each with "show in graph"); keyboard up/down/Enter.
- **States:** `ready`, `syncing` (the mirror is being cloned), `unavailable` with a reason, and an empty graph.

Search and first-parent views differ from the full graph (D30): a search is a **flat list**, each hit its own row
with no lanes, and a first-parent walk draws only first-parent edges. The row's `parents` on the wire still lists all
of them (`CodeCoverage.GitMirror/Graph/CommitGraphSnapshot.cs:178-187`).

## 2. How it works

```
browser ── GET /api/browse/repos/{provider}/{owner}/{name}/graph ──> coverage-app ──(coverage-git network)──> coverage-git
                                                                     gate, overlay,                       git, mirrors,
                                                                     state, triggers,                     lane layout,
                                                                     tokens, avatars                      lazy snapshots
```

- **`coverage-git`** (`CodeCoverage.GitMirror`, image `ghcr.io/mintplayer/codecoverage-git:master`) is the only
  container that runs `git`. It holds one bare, **treeless** mirror per repository at
  `/data/git/{provider}/{repoId}.git` on the named volume `codecoverage-git-data`, lays out lanes, and keeps lazy
  snapshots in a byte-bounded cache. It references no RavenDB, GitHub App or Spark runtime assembly
  (`The_sidecars_compiled_references_hold_no_database_client_no_GitHub_App_and_no_Spark_runtime`).
- **`coverage-app`** owns the visibility gate, the coverage overlay, the `RepositoryGraphState` documents, the
  triggers, and the forge credential, which it mints per fetch and sends in the request body.

The sidecar's API (`CodeCoverage.GitMirror/Endpoints/GitMirrorEndpoints.cs`), reachable only on the internal
`coverage-git` network:

| Call | Does |
|---|---|
| `GET /healthz` | Liveness. |
| `GET /stats` | The mirror volume's total and free bytes, the mirror count and their total size (the host-metrics dashboard reads it). |
| `POST /repos/{provider}/{repoId}/fetch` | Clones or fetches under the per-repository operation; a second call for the same repository **joins** the running one (D19). Returns the outcome, the reason, the size, the refs hash and the commit count. |
| `GET /repos/{provider}/{repoId}/graph?from&count&ref&at&snapshot&firstParent&grep&author` | One page of rows. 404 `missing` without a mirror, 404 `not-found` for an unknown ref or an `at` outside the walk, 410 `stale` for an old snapshot id (D21, D33). |
| `DELETE /repos/{provider}/{repoId}` | Aborts an in-flight operation, closes the snapshot readers and removes the mirror. Idempotent. |

The app's endpoints (`CodeCoverage/Controllers/BrowseController.cs`), all `[SparkAuthorize("Browse","Coverage")]` and
rate-limited with the rest of browse:

| Endpoint | Returns |
|---|---|
| `GET /api/browse/repos/{provider}/{owner}/{name}/graph` | One page (`count` ≤ 200) with `state`, `snapshotId`, `totalRows`, `laneCount`, the lanes at the page's top, `refs[]` and `rows[]`, each row with its coverage overlay and an opaque `avatarKey`. |
| `GET /api/browse/repos/{provider}/{owner}/{name}/coverage-drops?threshold&branch&take` | The commits whose `CoverageDeltaVsParent` fell below `-threshold`, a parameterised query over `Commits_ByRepository` (D17); `take` ≤ 200. |
| `GET /api/browse/avatar/{avatarKey}` | A cached avatar image, or a bare 404. |

**Reads never wait for git.** A missing mirror queues a clone and answers `syncing`; a stale one serves the rows it has
and queues a fetch (`CodeCoverage/CommitGraph/CommitGraphReader.cs:98-110`). After the gate every outcome is HTTP 200
with a `state`, including `stale` (D33). The coverage overlay is one `LoadAsync` of the page's deterministic
`Commits/{provider}/{repoId}/{sha}` ids and no query (`The_coverage_overlay_is_one_load_and_no_query`).

## 3. How mirrors sync

All fetches go through the Spark Messaging queue **`coverage-commit-graph`** (`CoverageQueues.CommitGraph`) to
`FetchCommitGraphRecipient`, which calls the sidecar with an `HttpClient` timeout of the quota's timeout plus a margin.

| Trigger | Does | Dedupe key (`CommitGraphTriggers`, D32) |
|---|---|---|
| **A graph read, no mirror** | The only path that **creates** a mirror (D10, lazy-on-view). The state is marked syncing and a clone queued. | `graph-fetch:{repo}:clone:{syncingSince ticks}` |
| **A graph read, mirror older than `StaleAfterMinutes`** | Serves the old rows and queues a fetch (D23). | `graph-fetch:{repo}:view:{window}` |
| **`push` webhook** | Fetches, only when a mirror exists. | `graph-fetch:{repo}:push:{ref}:{after}` |
| **Repository added to an installation** | Fetches, only when a mirror exists (a re-install of a mirrored repository). | `graph-fetch:{repo}:added:{yyyy-MM-dd}` |
| **Hourly safety net** `RefreshCommitGraphMirrorsCronJob` (`17 * * * *`) | Refreshes existing mirrors with an old fetch; covers dropped tag events and public repositories without webhooks. | `graph-fetch:{repo}:hourly:{yyyy-MM-ddTHH}` |

A fetch is `fetch --prune` with heads and tags refspecs only, then `pack-refs --all` and
`commit-graph write --reachable --split` (D26). Public repositories without an installation get no webhooks, so they
are refreshed only on view and hourly (D23).

`LastViewedAtUtc` is stamped by graph reads, at most once a minute so that scrolling does not write a document per
page (D36; `CommitGraphReader.cs:124`, `A_read_stamps_the_view_for_the_eviction_order`). A clone marked syncing longer
than the longest quota timeout plus 5 minutes counts as lost and is asked for again (`CloneLostAfter`).

## 4. Quotas, budget and eviction

| Limit | Default | Enforced by |
|---|---|---|
| Per repository, installed | 2 GB, 50k refs, 600 s | sidecar; the app sends the quota with every fetch |
| Per repository, anonymous (public, no installation) | 500 MB, 10k refs, 180 s | sidecar |
| Host free-space floor (**hard**) | 15 GiB (`GitMirror:MinFreeBytes`) | sidecar, before every clone and fetch: refused `disk-floor` without starting git |
| Total mirror budget (**soft**) | 20 GB (`CommitGraph:BudgetBytes`) | app, after each completed clone: evicts least-recently-viewed mirrors until under budget (D20) |
| Per installation | 25 % of the budget (`CommitGraph:InstallationShare`) | app, before a clone: the installation is refused, others still clone |
| Idle mirror | 60 days unviewed (`CommitGraph:EvictAfterDays`) | daily `EvictCommitGraphMirrorsCronJob` (`40 3 * * *`) |

- The ref quota is checked **before** cloning by counting `ls-remote --heads --tags` lines, stopping at the limit.
- A clone is built in a partial directory, measured, and only then moved into place; anything refused, failed or
  timed out deletes it. A timeout kills the whole process tree.
- **A mirror that outgrows its quota after a fetch is deleted and recorded as refused** (D34;
  `MirrorService.cs:211-223`). It is never left half-served.
- A refused or failed clone is not retried by reads for `RetryAfterMinutes` (60); the panel shows `unavailable` with
  the reason (`too-many-refs`, `too-large`, `disk-floor`, `timeout`, `auth`, `not-found`, `error`).
- A re-clone after eviction is the same path as a first clone: the next view shows `syncing`, then the graph
  (`An_evicted_repository_is_cloned_again_on_its_next_view`).

The numbers are the PRD's placeholders (D7, D8). S2 measured about 0.53 KB per commit on the VPS (100k commits ≈
53 MB, vscode's 192k commits 119.5 MB cloned in 5.7 s), well inside them. Production numbers are recorded at plan M8.

## 5. Deletion

The mirror dies with the access (PRD §5.2). Each trigger queues a `DeleteCommitGraphMirrorMessage` through
`CommitGraphDeletion.RequestAsync`, which queues **only when a `RepositoryGraphState` exists**, so the common case costs
one load by id and queues nothing (D37).

| Trigger | Test |
|---|---|
| Repository disconnected | `A_disconnect_deletes_the_mirror` |
| `DeleteData` action | `DeleteData_deletes_the_mirror` |
| Nightly `ReconcileForgeState` finds no access | `The_nightly_reconcile_finding_no_access_deletes_the_mirror` (and `A_reconcile_that_keeps_access_deletes_nothing`) |
| `installation` / `installation_repositories` removal | `GitHubRepositoryLifecycleTests` |
| Daily eviction finds a repository that lost access unnoticed | `The_daily_eviction_also_removes_a_mirror_whose_repository_lost_access_without_the_queue_hearing_of_it` |
| Idle or budget eviction | `CommitGraphMirrorTests` |

## 6. Security

- **Visibility gate first.** The graph endpoint resolves the repository exactly like its neighbours: allowed owners,
  then `ResolveAsync(…IsVisible)`, then `Refuse()`. A hidden private repository, a missing one and an unknown forge
  answer identically (401 anonymous, 404 signed in, one body), also while a mirror is syncing. The mirror's state is
  looked up only after the gate, so `syncing` is not an existence oracle
  (`The_graph_refuses_every_variant_with_the_canonical_refusal`).
- **Sidecar isolation by network** (D4, D18, D24). The sidecar's API has **no authentication**. It sits on exactly two
  networks: `coverage-git` (internal, shared with `coverage-app` only) and `coverage-git-egress` (its own way out,
  shared with nothing). It is never on `coverage-db`, `seo-raven` or `web`, and no network carries both RavenDB and the
  sidecar. `tools/verify-backend-networks.test.mjs` asserts the members. **Adding any container to `coverage-git`
  exposes the private commit history of every mirrored repository.**
- **No credential at rest.** The sidecar holds no App key, no Raven certificate and no stored token. The app sends an
  installation token per fetch; git gets it only through `GIT_CONFIG_COUNT`/`KEY_n`/`VALUE_n` as an
  `http.extraHeader`, never in arguments or `.git/config`; stderr is scrubbed of it before logging
  (`A_token_clone_and_fetch_succeed_and_the_token_is_nowhere_at_rest_in_arguments_or_logs`,
  `Stderr_is_scrubbed_of_the_credential_before_it_is_returned_or_logged`). Clone URLs must be plain `https://` with no
  user-info (D35).
- **Hardened git** (D25, D26): `GIT_CONFIG_NOSYSTEM`, `GIT_CONFIG_GLOBAL=/dev/null`, `GIT_TERMINAL_PROMPT=0`,
  `protocol.file.allow=never`, `protocol.ext.allow=never`, `submodule.recurse=false`, `maintenance.auto=false`,
  `gc.auto=0`, reads with `--no-mailmap` and `GIT_NO_LAZY_FETCH=1`. The scalar keys are written into the mirror by
  `clone --config` (D31).
- **Hooks off** (D5): clone with an empty `--template=`, and `core.hooksPath=/opt/git-hooks-empty`, a root-owned
  read-only empty directory baked into the image
  (`No_template_hooks_are_copied_and_a_hook_planted_in_the_mirror_never_fires`).
- **Bare, treeless, heads and tags only.** Nothing is checked out; `refs/pull/*` and fork-only branches never enter.
- **No emails to the browser** (D28). The sidecar returns author emails to the app only. A row carries an opaque
  `avatarKey`, an HMAC of the email under `CommitGraph:AvatarKeySecret` (or, unset, a key derived from
  `Coverage:BadgeSigningKey` under the purpose `commit-graph-avatar-key`; neither set means initials only). Images come
  only from `https://avatars.githubusercontent.com`, at most `AvatarMaxBytes`, SVG refused, cached as RavenDB attachments and served
  by the app (`No_email_reaches_the_response_and_no_reply_authors_cost_no_forge_call`,
  `An_svg_avatar_is_refused_because_it_can_carry_script`).

## 7. The admin dashboard

Administrators (the `Coverage:Administrators` list, by stable forge user id) see **Administration → Host metrics**
(`/po/host-metrics/main`) and **Administration → Mirrors** (`/query/mirrors`). The *Commit-graph mirrors* section shows
the mirror count and total size, evictions and refused clones (`CommitGraphMetrics/mirrors`), and the volume's used
and free bytes from the sidecar's `GET /stats`. The Mirrors query lists every `RepositoryGraphState` largest first.
Details: [`hosting.md`](hosting.md) § "Administrators and the host metrics dashboard".

## 8. Configuration

App (`coverage-app`), section `CommitGraph` (`CodeCoverage/CommitGraph/CommitGraphOptions.cs`). Only `SidecarUrl` is set
in compose; the rest keep their in-code defaults.

| Key | Default | Meaning |
|---|---|---|
| `CommitGraph:SidecarUrl` | unset; compose sets `http://coverage-git:8080` | The sidecar. Unset turns the feature off: the graph answers `unavailable`/`disabled` and nothing is queued. |
| `CommitGraph:BudgetBytes` | 20 GB | Soft total of all mirrors (D20). |
| `CommitGraph:InstallationShare` | 0.25 | One installation's share of the budget. |
| `CommitGraph:EvictAfterDays` | 60 | Unviewed this long, evicted by the daily job. |
| `CommitGraph:StaleAfterMinutes` | 10 | A read of an older mirror queues a fetch. |
| `CommitGraph:RetryAfterMinutes` | 60 | A refused or failed clone is not retried by reads for this long. |
| `CommitGraph:Installed:{MaxBytes,MaxRefs,TimeoutSeconds}` | 2 GB, 50,000, 600 | Quota with a credential. |
| `CommitGraph:Anonymous:{MaxBytes,MaxRefs,TimeoutSeconds}` | 500 MB, 10,000, 180 | Quota for anonymous public clones. |
| `CommitGraph:AvatarKeySecret` | unset → derived from `Coverage:BadgeSigningKey` | The avatar-key HMAC secret. |
| `CommitGraph:AvatarMaxBytes` | 256 KB | Largest avatar fetched. |

Sidecar (`coverage-git`), section `GitMirror` (`CodeCoverage.GitMirror/GitMirrorOptions.cs`, `appsettings.json`,
`Dockerfile`):

| Key | Default | Meaning |
|---|---|---|
| `GitMirror:Root` | `/data/git` | The mirrors' volume. |
| `GitMirror:MinFreeBytes` | 16106127360 (15 GiB); compose: `${GIT_MIRROR_MIN_FREE_BYTES:-16106127360}` | Hard host free-space floor. |
| `GitMirror:SnapshotCacheBytes` | 256 MB | Snapshot cache budget (D22); subjects and authors are counted. |
| `GitMirror:HooksPath` | `/opt/git-hooks-empty` (image) | The empty read-only hooks directory. |
| `GitMirror:ReaderIdleSeconds` | 60 | An idle `git log` reader is closed; the snapshot resumes with `--skip`. |
| `GitMirror:AllowInsecureHttp` | `false` | Test seam only. Never set in production. |

## 9. Operations

On the VPS, in `/var/www/code-coverage`. The volume is `code-coverage_codecoverage-git-data` (compose prefixes the
project name).

- **Is the sidecar up?** `docker compose ps coverage-git`; `docker compose logs --tail 100 coverage-git`.
- **Volume size:** `docker compose exec coverage-git du -sh /data/git`, or per repository
  `docker compose exec coverage-git du -sh /data/git/github/<repoId>.git`. The Host metrics dashboard shows the same.
- **Inspect a mirror** (read-only):
  `docker compose exec coverage-git git --git-dir=/data/git/github/<repoId>.git for-each-ref --count=20`.
  Never fetch by hand: that bypasses the quotas, the floor and the credential handling.
- **Clear one mirror:** prefer the app's own path (disconnecting the repository, or waiting for eviction), which also
  updates `RepositoryGraphState`. By hand: `docker compose exec coverage-git rm -rf /data/git/github/<repoId>.git`.
  The state document still says `HasMirror`; the next read finds the mirror missing and re-clones.
- **Clear all mirrors:** `docker compose stop coverage-git`, `docker volume rm code-coverage_codecoverage-git-data`,
  `docker compose up -d coverage-git`. Mirrors are a cache of the forges: losing them costs re-clones on the next
  views, nothing else. Nothing in the volume needs a backup.
- **Turn the feature off:** remove `CommitGraph__SidecarUrl` from the app's environment; the panel shows
  `unavailable` and nothing is queued.

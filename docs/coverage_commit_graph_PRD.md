# PRD — a SourceTree-style commit graph on the repository page, with coverage on every node

**Status:** Proposed — spikes S1–S9 not yet run ·
**Date:** 2026-10-11 · **Issue:** [#504](https://github.com/MintPlayer/MintPlayer.Spark/issues/504) ·
**Plan:** [coverage_commit_graph_plan.md](coverage_commit_graph_plan.md)
**App:** `apps/CodeCoverage` (production, coverage.mintplayer.com)
**Origin:** Three-agent investigation, 2026-10-11:
- the CodeCoverage codebase
- how a server obtains a commit DAG from GitHub, GitLab and Bitbucket
- how graph tools lay out and render lanes

Claims about this repository carry a `file:line` pointer (paths relative to `apps/CodeCoverage/`
unless stated). **[verified]** means read in the source; **[cited]** means taken from the linked
external page; **[inferred]** means reasoned, not measured. Every inferred number that a decision
rests on has a spike in §7.

> Written before the work. Where the build disagrees with it, the plan's milestone notes say so.

## 0. Decisions (owner grill, 2026-10-11)

These supersede the text below where they disagree. Superseded passages are marked **[superseded by Dn]**.

| # | Decision | Evidence / reason |
|---|---|---|
| D1 | **The mirror runs in a separate sidecar container** (`codecoverage-git`), not in the app container. The sidecar holds no GitHub App key and no RavenDB certificate. | Owner choice. It isolates git's hostile-input surface from the process holding the App private key, the Raven certificate and the session keys (§5). |
| D2 | **No work on new GitHub App permissions.** Users accept a raised permission themselves. S1 proves only that the token works and doesn't leak. | Owner. `contents` is already requested (`GitHubForgeClient.cs:193-224` expects `contents: write`; manifest scanning uses `GetFileContent`/`GetTree`) **[verified]**. |
| D3 | **The sidecar owns git, lane layout and lazy snapshots.** The app owns the visibility gate, the coverage overlay, `RepositoryGraphState`, the triggers, and minting tokens (passed per request). | Owner, option A. Layout stays next to the data and the snapshot memory stays out of the app process. Raven credentials never enter the sidecar. |
| D4 | **App ↔ sidecar is secured by network isolation only, with no key.** A dedicated compose network holds exactly two hosts, `codecoverage` and `codecoverage-git`, with no published port. Comments in `docker-compose.yml` (on the network and on both services) and in `hosting.md` say that adding any other container to it exposes private commit history. | Owner, option B, with the explicit comments as the safeguard. |
| D5 | **Git hooks are disabled explicitly**: clone with an empty `--template=`, and set `core.hooksPath` to an empty read-only directory. Fetch never runs remote hooks, but git does run *local* hooks such as `reference-transaction` on ref updates. | Owner. S7 adds a planted-hook check. |
| D6 | **Public repositories without an installation are cloned anonymously, under stricter quotas** than installed ones. | Owner, option C. A clone is already how commits are determined (D3), so anonymous repositories need no other path. |
| D7 | **The quota numbers are set after S2**, from measurements on the VPS. Until then the placeholders are: installed 2 GB / 50k refs / 10 min; anonymous 500 MB / 10k refs / 3 min. | Owner, option C. VPS on 2026-10-11 (`df -h` on the host): `/` 150 GB with **93 GB free**; an **empty 10 GB Hetzner volume** at `/mnt/HC_Volume_105198417`; 4 cores; 7.6 GB RAM with about 3 GB available **[verified]**. |
| D8 | **Mirrors live on a named Docker volume on the root disk** (`codecoverage-git-data`, mounted only into the sidecar). The Hetzner block volume is not used. **Guards enforced in software**, because RavenDB shares this disk: (1) a total mirror budget, the sum of `RepositoryGraphState.SizeBytes`, and (2) a host free-space floor. Below either, the sidecar refuses clones and fetches with `DiskGuard` and the app records it. Each mirror's size is measured by the sidecar per repository directory after each fetch, and the app has no mount. | Owner, option B. statfs on a named volume reports the host root (§9.2 **[cited]**), so it cannot isolate mirror usage and per-repository measurement replaces it. Budget and floor numbers come with D7 after S2; placeholders: 20 GB budget, 15 GB floor. |
| D10 | **Clone only, mirrored lazily, evicted by LRU under a budget; full history.** <br>• **Source:** GraphQL-first with a clone fallback was considered and rejected. <br>• **Lazy:** a mirror is created only when someone opens the graph panel. Installing the App or pushing never clones, and a push to an unmirrored repository does nothing. <br>• **Eviction:** `RepositoryGraphState.LastViewedAtUtc` is stamped on each graph read. A daily eviction job deletes mirrors not viewed for N days (placeholder 60). When a clone would exceed the total budget (D8), the least-recently-viewed mirrors are evicted first instead of refusing. <br>• **Caps:** per repository (D7) and per installation (placeholder: 25% of the budget). <br>• **Re-clone:** a re-clone after eviction is the same path as a first clone ("syncing", then the graph), so eviction adds only the existing `DELETE`. <br>• **Visibility:** the eviction count is shown on HostMetrics (§9). | Owner, option A, after asking how mirrors are kept from growing with organizations × repositories. GraphQL still has to store the DAG (heavier, as Raven documents) and cannot serve public repositories without an installation (GraphQL requires authentication), Bitbucket (1,000 commit-list calls/h vs 60,000 git operations/h **[cited]**) or the shared check-run budget. Storage now grows with repositories *viewed*: about 0.2–0.5 KB per commit **[inferred → S2]**, so about 1–2.5 GB for 1,000 viewed repositories of 5k commits each. Rejected: a history horizon (`--shallow-since`), held in reserve in case S2 measures much more. |
| D11 | **Spark ships a dashboard package; CodeCoverage is its first consumer.** Supersedes D9. Rights go to a slot (e.g. `dashboards:administrators`), so CodeCoverage still gets a new `Administrators` group to bind it. Membership is seeded by stable forge user id, never by login. Versions: NuGet `11.0.0-preview.*`; npm as a `@mintplayer/ng-spark` entry point in a 22.x **minor**. The package's model and shape are decided by further grill questions (D12+). | Owner, 2026-10-11: *"The dashboard package will very very likely be used elsewhere too."* |
| D12 | **A dashboard is a live virtual PersistentObject.** The package adds only: <br>• `IDashboardProvider` to fill attribute values <br>• a hosted notifier streaming through `/spark/live` <br>• a dashboard tile layout for the detail page <br>• the `dashboards:administrators` slot <br>New renderers (meter, stat-with-threshold, …) ship as **new `@mintplayer/ng-spark` sub-entry points**. Tables and series use sub-queries or JSON attributes. There is no separate widget endpoint, authorization path or push channel. | Owner, option A: *"Makes full leverage of the Spark pipeline, least amount of custom code."* Prior art: Hangfire's dashboard ran its own authorization path, and a later release left it open to remote requests (CVE-2021-41238 **[cited]**). |
| D13 | **SeoTester's operator dashboard moves onto the package in this PR, and RavenDB statistics become a built-in package provider.** <br>• **Built-in provider:** database size, document count, and per-collection count and size. Any app can switch it on. <br>• **SeoTester:** its `OperatorDashboard` becomes a package dashboard. Its custom `GET /seotester/admin/dashboard` endpoint and hand-made tile panel are deleted, and its existing Administrators group binds the slot. | Owner, option C: *"document-count and collection information is also very interesting information."* A second consumer in the same PR proves the API generic before it is published. Callers of the endpoint: only SeoTester's own panel, `ReportSurfaceTests`, the route snapshot (`SeoTester.Tests/RouteSnapshots/SeoTester.txt`) and docs. No workflow, compose file or monitor references it (grep, 2026-10-11) **[verified]**. |
| D14 | **Optional history per dashboard value.** <br>• **Opt-in:** a provider value declares `SampleEvery`. <br>• **Sampler:** one hosted sampler, guarded by a RavenDB compare-exchange lease (no double-writes across instances), appends to **RavenDB native time series** on a backing document `SparkDashboards/{name}`. The dashboard is otherwise virtual. <br>• **Retention:** rollup and retention policies, not hand pruning. <br>• **Rendering:** the existing `spark-trend-sparkline`. <br>• **Revisions:** the backing collection has revisions disabled. | Owner, option B. "When will the disk fill up" needs a trend. Time series, the sparkline and the lease pattern all exist already. Revisions are disabled because of the earlier licence lesson (Community licence: "revisions 1000 > licensed 2"; `CLAUDE.md`). Spike S11 covers it. |
| D15 | **The `ParentSha` question (plan Q2) is not a defect, and §6 is answered.** The PR webhook writes the base tip (`ForgeEventsRecipient.cs:82`). `CommitAssembler` then overwrites it with GitHub's first parent and sets `ParentShaSource = "api"`, and the delta trusts only `api` parents, or `upload` parents on non-PR commits (`CommitAssembler.cs:407-433`) **[verified]**. The only fix is the stale comment at `ForgeEventsRecipient.cs:79-81`, which calls the webhook "the sole writer of ParentSha". It is corrected in this PR. The graph reads parents from the mirror, never from `ParentSha`. | Read in the source, 2026-10-11. |
| D16 | **The default view is all branches and tags**, with a selector to narrow to one branch and its ancestry. Visible lanes are capped (placeholder: 12 on desktop, 4 on mobile) with an overflow marker, so the graph column never takes more than a fixed share of the row. | Owner, option A ("just like SourceTree"). |
| D17 | **Every "later" feature from §4.5 is in this PR:** hover-highlight of a branch path, search (message / SHA / author), the first-parent view, a coverage sparkline along the selected branch, the coverage-drop filter, and avatars. Two of them have designs of their own: <br>• **Search:** sidecar `git log --grep/--author` (fixed-string and case-insensitive; arguments passed as an array, never through a shell). Results jump to their row with `at=`. <br>• **Coverage-drop filter:** a **list mode**, not a graph filter. A parameterised query over `Commits_ByRepository` (`CoverageDeltaVsParent < $threshold`; no RQL splicing) returns the commits, and each row has "show in graph" (`at=`). The lazy, mirror-ordered graph stays untouched. <br>• **Avatars:** author emails **never reach the browser** and are never sent to Gravatar. GitHub no-reply emails (`ID+login@users.noreply.github.com`) give the login with no API call. Other authors are resolved through the forge, lazily and cached per author. Images go through a server-side cache. Spike S12 covers it. | Owner, option D. |
| D18 | **Review fix to D4: the sidecar needs internet egress, and that must not share a network with anything else.** <br>• **The flaw:** the two-host network can't reach GitHub if it is `internal: true`. Putting the sidecar on the default network for egress would expose its unauthenticated API to every container there (`coverage-raven`, mail, …). <br>• **The fix:** two networks. `git-graph` is `internal: true` and holds the app and the sidecar. `git-egress` holds **only the sidecar** and is not internal, giving outbound access. No container other than the app ever shares a network with the sidecar. Both get the D4 comments. | Self-review, 2026-10-11. It follows from D4 plus the need to clone. |
| D19 | **Review fix: the fetch call is synchronous, joins an in-flight fetch for the same repository, and is bounded by the quota timeout.** <br>• **The app's call:** the app's `FetchCommitGraphRecipient` calls `POST /fetch` with an `HttpClient` timeout of the repository's quota timeout (D7) plus a margin. <br>• **Joining:** a second call for the same repository joins the running operation and gets its result. It does not queue a second clone. <br>• **Redelivery:** a messaging claim that expires mid-clone (TTL 5 min < the 10 min placeholder quota) redelivers. The redelivered call joins, so the work is never done twice. S8 is rewritten to assert the join. <br>• **Token lifetime:** installation tokens live about 1 h **[inferred]**, more than any quota timeout. | Self-review. §4.1 placed the lock in the app before D1 moved git into the sidecar. |
| D20 | **Review fix: the budget is soft, the floor is hard.** <br>• **Floor (hard):** the sidecar refuses any clone or fetch when host free space is below the floor. It can measure that itself with statfs, and it knows nothing about views. <br>• **Budget (soft):** a new clone's size is unknown until it finishes. So the **app** trims after each completed clone, evicting least-recently-viewed mirrors (`RepositoryGraphState`) until the total is back under the budget. <br>• **Corrects** D8 and D10, which had both the sidecar and the app "enforcing the budget before cloning". | Self-review. The size of a not-yet-cloned repository can't be known in advance (D7). |
| D21 | **Review fix: snapshots and paging under a moving repository.** <br>• **Total rows:** the virtual scroll needs a total height, and a lazy snapshot has no `totalRows` until its walk ends. `totalRows` therefore comes from `git rev-list --count --branches --tags`, cheap with a commit-graph (S3 measures it). <br>• **Stale snapshots:** a fetch changes the refs hash and starts a new snapshot. A page request carrying an old `snapshotId` gets **410 Gone**, and the panel reloads at the same top SHA (`at=`). <br>• **Search arguments:** they are passed as `--grep=<value>` / `--author=<value>` in the argument array with `--fixed-strings -i`, so a value beginning with `-` cannot be read as an option. | Self-review. |
| D22 | **Review fix: the sidecar's memory is bounded.** The VPS has about 3 GB available (D7). The sidecar gets a compose `mem_limit` (placeholder 1 GB) and a snapshot cache bounded by bytes, not by count. S5's < 200 MB per 100k-row snapshot is the input to that bound. | Self-review. |
| D23 | **Review fix: public repositories without an installation get no webhooks.** <br>• **Freshness:** for these, it comes only from stale-on-view (the endpoint refetches when the mirror is older than N minutes) and from the hourly safety net. <br>• **Avatars:** for these, they come only from GitHub no-reply emails; other authors get initials. Anonymous API calls (60/h) are never spent on avatars. | Self-review. Webhooks are delivered only to App installations. |
| D9 | **[superseded by D11]** **No dashboard package now.** Host and mirror metrics are app code in CodeCoverage: a live virtual `HostMetrics` PO (§9). | Two-agent investigation: every building block exists (§9.1), and there is no second consumer. **Pending owner confirmation (Q7).** |

---

## 1. The question

The issue says, in full: *"Add a web-based git-tree visualizer, just like SourceTree."* The owner added
one constraint: **the graph lives on the repository page**,
`/po/repository/Repositories%2Fgithub%2F{id}`, not on a page of its own.

The thing the page must be able to show, for any repository the viewer is allowed to see:

> *Every branch and tag of this repository as a vertical commit list with coloured lanes, merge and
> branch curves and ref pills, as SourceTree draws it. On every node: whether CI uploaded coverage
> for that commit, what the coverage was, and how much it moved against its parent.*

The second sentence is the reason to build this here rather than tell users to install a git client.
SourceTree, GitKraken, GitLens and VS Code Git Graph draw the graph, but none of them knows coverage.
A graph whose nodes are coloured by coverage, with the drop-causing commit visible at a glance, is
something only this app can offer.

### 1.1 Premises the investigation corrected

**"We already have the commits, we just need to draw them" — we do not.** CodeCoverage stores only
commits that received an upload or a webhook:

- The `push` handler keeps the head commit and discards the intermediate ones
  (`CodeCoverage.GithubIntegration/Recipients/GitHubEventsRecipient.cs:301-327`) **[verified]**.
- There is no Branch entity: branches exist only as `Commit.Branch` strings
  (`CodeCoverage/Controllers/BrowseController.cs:266-285`) **[verified]**.
- Tags are ignored entirely.
- Parents are a single `Commit.ParentSha` with three writers and two meanings
  (`CodeCoverage.Library/Entities/Commit.cs:78-93`) **[verified]**:
  - the PR webhook writes the **PR base tip**, which is not a git parent
    (`CodeCoverage/Recipients/ForgeEventsRecipient.cs:82`)
  - the upload writes its claim (`CodeCoverage/Ingestion/UploadIngestor.cs:114-120`)
  - assembly writes the GitHub API's `parents[0]` (`CodeCoverage/Ingestion/CommitAssembler.cs:411-417`)
- **Second parents are never stored**, so no merge can be drawn.

`docs/coverage_carryforward_PRD.md:126` already said it: *"Git graph knowledge: none beyond
`Commit.ParentSha`."* The graph therefore needs a **new source of topology**. That is most of this
PRD; the drawing is the smaller half.

**"Ask the forge API for the history" — too expensive, and different on every forge.**
- GitHub lists commits at most 100 per call, so a 100k-commit history is about 1,000 calls **[inferred]**
  ([REST commits](https://docs.github.com/en/rest/commits/commits#list-commits) **[cited]**).
- The budget is 5,000–12,500 per hour per installation
  ([rate limits](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api) **[cited]**),
  shared with every check-run and compare call the app already makes.
- Public repositories without an installation fall back to anonymous calls
  (`GitHubDiffService.cs:51-52,80-90`) **[verified]**, which allow 60 per hour **[inferred]**.
- Bitbucket Cloud allows 1,000 commit-listing calls per hour but 60,000 git operations per hour
  ([Bitbucket limits](https://support.atlassian.com/bitbucket-cloud/docs/api-request-limits/) **[cited]**).
- Each forge would need its own adapter, and the GitLab and Bitbucket integrations are still empty
  stubs (`CodeCoverage.GitlabIntegration/Extensions/SparkBuilderExtensions.cs:5-16`) **[verified]**.

**"Push webhooks can maintain the DAG" — they cannot.**
- `commits[]` is capped at 2,048.
- No event fires when more than three tags are pushed at once
  ([push payload](https://docs.github.com/en/webhooks/webhook-events-and-payloads#push) **[cited]**).
- Bitbucket caps push payloads at 5 commits
  ([event payloads](https://support.atlassian.com/bitbucket-cloud/docs/event-payloads/) **[cited]**).
- Payload commits are believed to omit parent SHAs **[inferred]**.

A webhook is good as a **"fetch now" trigger**, not as a source.

**"Reuse a graph library" — none is usable.**
- VS Code Git Graph's licence is MIT *modified to forbid derivative works*
  ([LICENSE](https://raw.githubusercontent.com/mhutchie/vscode-git-graph/develop/LICENSE) **[cited]**).
- GitLens's renderer, `@gitkraken/gitkraken-components`, is proprietary **[cited]**.
- gitgraph.js was archived on 2024-07-13 and is scripted rather than data-driven **[cited]**.
- The maintained MIT ones (`commit-graph`, `react-git-log`) require React **[cited]**.
- d3-dag lays out general DAGs, not one commit per row **[cited]**.
- `cytoscape` + `dagre`, already in the repo for the dependency graph
  (`account-dependency-graph-panel.component.ts:16-25`) **[verified]**, is the wrong layout model for
  the same reason.

We write the lane layout ourselves, from Pierre Vigier's published description of the
straight-branch algorithm ([article](https://pvigier.github.io/2019/05/06/commit-graph-drawing-algorithms.html) **[cited]**).

---

## 2. Goals and non-goals

**Goals**

- **G1.** A commit graph panel on the repository page, in the Repository branch of
  `po-detail-page.component.ts:44-48` next to the badge, trend and setup panels **[verified]**.
- **G2.** Complete topology: all branches and tags, true parent lists including merges, correct after
  force-pushes and branch deletions.
- **G3.** Coverage overlay per node:
  - present / absent / partial
  - coverage %
  - `CoverageDeltaVsParent`
  - PR number
  - "latest coverage" marker

  All of these exist on `Commit` today (`Commit.cs:26-150`) **[verified]**.
- **G4.** Scales to 100k-commit repositories: paged, virtual-scrolled, no full-history payload.
- **G5.** Same pipeline for GitHub, GitLab and Bitbucket, so the stubs gain the graph the day they
  gain credentials.
- **G6.** **Zero new visibility surface.** A private repository the viewer may not see is
  indistinguishable from a missing one, exactly as `/api/browse` does today (§5).

**Non-goals**

- File trees, diffs or blame. The treeless clone (§4.1) deliberately has no trees: a path-scoped walk
  would lazily download a tree per commit **[cited]**
  ([partial clone](https://github.blog/open-source/git/get-up-to-speed-with-partial-clone-and-shallow-clone/)).
  The existing commit page keeps serving files from coverage reports.
- Any write to the repository: no branch creation and no checkout.
- Fork branches and `refs/pull/*`. Fork-contributed commits are already excluded from repository-level
  listings (`BrowseController.cs:139-141,274-276`) **[verified]**. The mirror fetches only
  `refs/heads/*` and `refs/tags/*`, so fork-only commits never enter it.

---

## 3. Options considered

| | A. Forge REST/GraphQL → Raven docs | B. Webhooks only | **C. Treeless bare mirror + git CLI** |
|---|---|---|---|
| Backfill of 100k commits | about 1,000 calls per forge **[inferred]** | impossible | 1 fetch, about 20–50 MB **[inferred → S2]** |
| Incremental update | compare per pushed ref | lossy (cap, tag storms) | `git fetch --prune` per push |
| Merges and second parents | yes | no **[inferred]** | yes |
| Multi-forge | three adapters | three payload shapes | **identical**; only the credential differs |
| API budget used | high, shared with check-runs | none | negligible |
| Public repo without installation | 60 calls/h anonymous | n/a | anonymous HTTPS clone, no API |
| New infrastructure | none | none | `git` in the image, a data volume |
| Storage | a document per commit (×100k per repository) | Raven | git objects on disk, plus one small state document |

**Decision: C.** It is the only option that is complete, cheap and forge-agnostic. Its cost is
production infrastructure:
- the runtime image `mcr.microsoft.com/dotnet/aspnet:11.0` has no `git` (`CodeCoverage/Dockerfile:1-11,75-79`) **[verified]**
- the only volume is the data-protection key ring (`Dockerfile:6`) **[verified]**

That cost is paid once, and spikes S1–S4 and S7 measure it before any code lands.

**Rejected: a document per commit in RavenDB.** The app already holds about 224k documents
(`Commit.cs:166-167`) **[verified]**. A second per-commit collection for topology would double that
for a structure that git, with a commit-graph file, already stores compactly and walks quickly
([git-commit-graph](https://git-scm.com/docs/git-commit-graph) **[cited]**).

**Rejected: LibGit2Sharp.** libgit2 has no partial-clone support; the issue has been open since 2020
([libgit2#5564](https://github.com/libgit2/libgit2/issues/5564) **[cited]**). We shell out to the `git` CLI.

---

## 4. Design

### 4.1 The mirror

> **[superseded by D1, D3, D4, D8]** The mirror and everything below now run in the **sidecar**
> `codecoverage-git`, not in the app. "The app runs `git`" becomes "the app calls the sidecar's
> internal API over the two-host network". The data volume is the dedicated Hetzner block volume. The
> runner rules below (environment-only credentials, quotas, lock, hooks per D5) apply unchanged inside
> the sidecar.

**The sidecar's internal API (D3).** It is reachable only on the two-host network (D4). The token is
minted by the app and sent in the request body, never stored:

| Call | Does |
|---|---|
| `POST /repos/{provider}/{repoId}/fetch` `{ cloneUrl, credential?, quota }` | Clones or fetches under the per-repository lock. Returns the outcome, the size on disk, the refs hash and the volume's free bytes. |
| `GET /repos/{provider}/{repoId}/graph?from&count&ref&at` | Extends the lazy snapshot (§4.3) and returns rows. Coverage is not included; the app adds it. |
| `DELETE /repos/{provider}/{repoId}` | Removes the mirror and its snapshots. |

- **One bare, treeless clone per repository** under a data volume, e.g.
  `/data/git/{provider}/{repoId}.git`, keyed by the stable repository id rather than the full name
  (renames are tracked in `PreviousFullNames`, `Repository.cs`).
- **Initial clone:** `git clone --bare --filter=tree:0` with refspecs limited to
  `+refs/heads/*:refs/heads/*` and `+refs/tags/*:refs/tags/*`.
- **Repository config:**
  - `fetch.writeCommitGraph=true`, so every fetch keeps the commit-graph current
  - `submodule.recurse=false`
  - `protocol.file.allow=never`
- **Process environment:** `GIT_TERMINAL_PROMPT=0`.
- **Credentials come through the environment, never the command line or `.git/config`.** Use
  `GIT_CONFIG_COUNT` / `GIT_CONFIG_KEY_n` / `GIT_CONFIG_VALUE_n` to set an `http.extraHeader` per
  process.
  - The project rule forbids printing process command lines because they can carry secrets.
  - A token in an argument would also show up in any process listing.
- **New forge capability `GetGitAccessAsync(repository)`** on `IForgeIntegration`
  (`CodeCoverage.Library/Forge/IForgeIntegration.cs:38-210`) **[verified]**:
  - returns `(username, token)` or "anonymous" for public repositories
  - GitHub: `x-access-token` + the installation token from `GitHubInstallationService`
    (`libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/Services/GitHubInstallationService.cs:43-53`) **[verified]**
  - GitLab (`oauth2`) and Bitbucket (`x-token-auth`) stubs declare the capability unsupported until
    they have credentials
- **Quotas:** a repository above a size or ref-count limit (S7) is refused. It gets a recorded state,
  not a partial graph.
- **One process per repository at a time:** a per-repository file lock makes a second fetch wait or
  skip, because a Spark Messaging claim can expire (TTL 5 min, `docker-compose.yml:184-191`)
  **[verified]** before a large initial clone finishes (S8).

### 4.2 Keeping it fresh, and deleting it

> **[superseded in part by D10]** The triggers below apply **only to repositories that already have a
> mirror**:
> - "Repository added to an installation" no longer clones.
> - The panel-opened row is now the *only* path that creates a mirror.
> - Push and the cron safety net skip repositories without one.
> - A daily eviction job joins the deletion triggers.

| Trigger | Action |
|---|---|
| `push` webhook (`GitHubEventsRecipient.cs:301-327`) | `BroadcastOnceAsync` with dedupe key `graph-fetch:{repoId}` → `FetchCommitGraphRecipient`, modelled on `RequestManifestScanAsync` → `ScanRepositoryManifestsRecipient` (`:341-353`) **[verified]** |
| Repository added to an installation | Same message, which performs the initial clone |
| Panel opened while the mirror is missing or older than N minutes | Same message; the panel shows "syncing" |
| Cron safety net (hourly) | Fetches mirrors of listed repositories whose last fetch is older than the newest push it knows of; covers dropped tag events |
| Repository disconnected, uninstalled, `DeleteData`, or made private with no access left | **Delete the mirror directory** — non-negotiable, see §5 |

State lives in one small document per repository, `RepositoryGraphStates/{provider}/{repoId}`. It
holds the last fetch time, outcome, size on disk, refs hash, and a failure reason when there is one.
It is additive and never re-keys an existing document.

### 4.3 From the mirror to rows

**This is how SourceTree works (owner, 2026-10-11).** SourceTree does not load or render all commits.
It pages, through virtual scrolling, over every commit sorted by date, newest first. Each commit is
drawn in its lane, with the branch lines between rows. The design copies that model:

- **Row order is date order: `git log --date-order`.** Commits come newest first by commit
  timestamp, with one guarantee: no parent is shown before all of its children. A raw timestamp sort
  breaks that under clock skew, rebases or imported history. The streaming layout depends on it,
  because a commit's lane is decided by the children above it.
  - The order is still essentially "sorted by datetime".
  - `--topo-order` is rejected. It groups each branch's commits together and moves away from the
    date order that SourceTree users expect.
- **Read the DAG** with one commit-only walk:
  `git log --branches --tags --date-order -z --format=%H%x00%P%x00%an%x00%aI%x00%s`, plus
  `git for-each-ref` for the ref pills.
  - Commit-only walks on a treeless clone trigger no lazy fetch **[cited]**; S3 measures it.
  - `git log` streams its output, so the first rows arrive without walking the whole history.
- **Lane layout runs on the server, in C#**, as one streaming top-down pass of the straight-branch
  algorithm.
  - The lanes of row *i* depend only on rows 0..*i*. So does every branch line passing through row
    *i*: a line is a lane that expects a not-yet-shown parent.
  - Layout therefore needs to go only **as far as the viewer has scrolled**. It never has to go to
    the end of history.
- **A snapshot is built lazily**, keyed by `(repoId, refsHash, filter)`. It holds:
  - the rows laid out so far: sha, parents, lane, colour, refs, and the pass-through lanes
  - a lane-state checkpoint every 500 rows
  - the open `git log` reader position, or enough to restart from the last checkpoint

  How a request is served:
  - A request for rows past the laid-out frontier extends the snapshot. It never redoes the rows
    above.
  - The first view of a 100k-commit repository costs about the first page, not the whole history.
  - When the refs hash changes, a new snapshot starts from the top.
  - **"Jump to SHA"** (`at=`) for a commit far down extends the snapshot up to that commit. That
    cost is linear and bounded by S5.
  - Snapshots live in a bounded in-memory cache.

  > **Why the server, not the client.** The rendering research recommended client-side layout, so
  > that branch filters relayout without a round trip. The acquisition research recommended
  > server-side. Server-side wins on three counts:
  > - the panel can open **at any row**: deep-linking from a commit page, or "jump to SHA", without
  >   first downloading every row above it
  > - each page is a stateless range read
  > - the algorithm has one implementation, tested by xUnit golden files
  >
  > A branch filter costs one more snapshot, i.e. one more `git log <ref>`, which S3 bounds.
  > If S5 shows server layout is too slow or too memory-hungry, the fallback is client-side layout
  > over the same topo-ordered feed, carrying lane state across pages.

### 4.4 The endpoint

`GET /api/browse/repos/{provider}/{owner}/{name}/graph?from={row}&count={n}&ref={optional}&at={sha}`
in `BrowseController`. It carries the same attributes as its neighbours:
`[SparkAuthorize("Browse","Coverage")]` and `[EnableRateLimiting("browse")]` (`BrowseController.cs:15-28`) **[verified]**.

- **No new security resource.** A graph read is a browse read, and anonymous users already hold
  `Browse/Coverage` (`App_Data/securityPosture.txt`) **[verified]**.
- **Response:**
  - `snapshotId`, `totalRows`, `laneCount`, `refs[]`
  - `rows[]`: sha, short message, author, date, lane, parent edges, refs, coverage overlay
  - a `state` (ready / syncing / unavailable + reason)
- **`count` cap:** 200, matching `GetCommits` **[verified]**.
- **Coverage overlay with no query at all:** commit ids are deterministic,
  `Commits/{provider}/{repoId}/{sha}` (`Commit.cs:172-176`) **[verified]**. The overlay is one
  `LoadAsync` over the page's ids, so no RQL is built and nothing is spliced (S6).

### 4.5 The panel

`app-repo-commit-graph-panel`, mounted in the Repository branch of `po-detail-page.component.ts:44-48`,
loads through `services/browse.service.ts` like the trend panel does.

**Layout**
- Fixed row height.
- ng-bootstrap's virtual datatable.
- **One small inline SVG per row**, holding:
  - the lane segments through the row
  - the half-row Bézier edges into and out of the node
  - the node itself

  DOM size stays proportional to visible rows × lanes. One SVG for the whole graph would defeat
  virtualisation; canvas would lose hover and accessibility. Vigier measured drawing everything at
  106 ms against 0.58 ms for visible-only **[cited]**.
- Columns: graph · message with ref pills · coverage % and Δ · author · date · short SHA.
- **The node encodes coverage:**
  - fill on a sequential scale
  - ring for "no report"
  - hollow grey for "pending or partial"
  - readable without hue
- A row click navigates to the existing commit route `/:provider/r/:owner/:repo/c/:sha`
  (`app.routes.ts:17-74`) **[verified]**.

**MVP features**
- All refs, or one branch with its ancestry.
- Ref pills; the default branch and `LatestCoverageSha` are marked.
- Merge nodes are styled distinctly.
- Load-more via virtual scroll.
- Keyboard up/down/Enter.
- Light and dark lane palettes, read from `--bs-*` variables and restyled through a `MutationObserver`,
  as the dependency graph panel does (`account-dependency-graph-panel.component.ts:30-44,289-293`) **[verified]**.
- Empty, syncing and unavailable states.

**Later (not this PR unless the owner says so; see plan Q3)**
- Hover-highlighting a branch path.
- Search by message, SHA or author.
- First-parent-only view.
- Coverage sparkline along a branch.
- "Commits that dropped coverage by more than X" filter.
- Avatars.

---

## 5. Security and privacy

A mirror is a **copy of private source history on our disk**, which is a new kind of data for this app.
Today the app holds coverage numbers and file paths; with the mirror it holds every commit message
and author of every connected private repository. Rules:

1. **Reads go through the visibility gate, first.** `GetAllowedOwnerKeysAsync()` runs
   unconditionally before resolving. Then `ResolveAsync(..., RepositoryVisibility.IsVisible)`. On null,
   `SparkDenial.Refuse`: 401 for anonymous, 404 for signed-in, one constant body
   (`BrowseController.cs:607-638`; `Services/RepositoryVisibility.cs`) **[verified]**.
   - A snapshot is looked up only **after** this gate.
   - The "syncing" state is not an existence oracle, because it is also only reachable after the gate.
   - See memory `project_coverage_private_repo_existence_oracle.md` (#453).
2. **The mirror dies with the access.** Delete the directory on:
   - disconnect
   - uninstall (`installation` / `installation_repositories` removed)
   - the `DeleteData` action
   - `ReconcileForgeState` (03:20) finding no access

   A test asserts the directory is gone in each case.
3. **No token at rest, none in arguments, none in logs.** Credentials are per-process environment
   only (§4.1). Git's stderr is captured and scrubbed of the configured header before it is logged.
   A test asserts the token is absent from `.git/config` and from captured output; it does not print
   the value.
4. **Hostile repositories:**
   - bare, never checked out
   - no submodule recursion, `protocol.file.allow=never`
   - size and ref-count quotas
   - `git` kept patched through the base image

   Past clone CVEs such as CVE-2024-32002 went through checkout or submodules, which a bare fetch
   avoids **[inferred]**; S7 tests it.
5. **Fork isolation:** only `refs/heads/*` and `refs/tags/*` are fetched, so `refs/pull/*` and
   fork-only branch names never enter the mirror.

---

## 6. What this does not fix, and what it might

`Commit.ParentSha` is written by the PR webhook as the **PR base tip** (`ForgeEventsRecipient.cs:82`)
**[verified]**. `CoverageDeltaVsParent` is computed against it. Once the mirror exists, the server
knows the true first parent of every commit. Whether the current behaviour is a defect (the delta of a
PR head commit measured against the base tip instead of its parent) or intended (the delta against
the PR base) is **an owner decision** (plan Q2). If it is a defect, its fix belongs in this same PR,
per the one-PR rule.

---

## 7. Spikes

Each spike has one question, a method, and a falsifiable success criterion. Results are recorded in
the plan before the milestone that depends on them starts.

| # | Question | Method | Pass |
|---|---|---|---|
| **S1** | Does git-over-HTTPS work with an installation token, without leaking it? Does the production App have `contents: read`? | Treeless bare clone of a private test repository with the token in `GIT_CONFIG_*`; read the App's permissions through the API | Clone succeeds. The token occurs **0** times in `.git/config`, in captured stdout/stderr and in argv (checked by a test, never printed). `contents: read` is granted, or the owner approves the permission change. Anonymous clone of a public repository also succeeds. |
| **S2** | Disk and time of a treeless clone on the VPS | Clone this repository, `dotnet/aspnetcore` and a ~100k-commit repository with `--filter=tree:0`, then write a commit-graph | 100k-commit repository: **≤ 60 MB** on disk and cloned in **< 60 s** on the VPS. Results recorded per repository. |
| **S3** | Do commit-only walks avoid lazy fetches, and how fast are they? | `GIT_TRACE_PACKET=1 git log --branches --tags --date-order --format=…` on S2's clones, with and without a commit-graph | **0** fetch-pack requests during the walk. 100k rows in **< 2 s** with a commit-graph. The **first 200 rows in < 200 ms** (streaming). A single-branch walk also < 2 s. |
| **S4** | Is the incremental fetch correct and cheap? | Against a scratch repository: push 1 commit, push 50, force-push a branch, delete a branch, push 5 tags at once; `git fetch --prune` after each | Each fetch **< 3 s**. The DAG and refs are correct in all five cases: the force-pushed branch loses its old tip, the deleted branch disappears, all 5 tags appear. |
| **S5** | Is server-side lane layout fast, compact, stable and incremental? | C# straight-branch layout over S2's 100k DAG and a synthetic merge-heavy DAG, in `--date-order`; extend page by page versus one full pass | Full layout **< 1 s** using **< 200 MB**. The first page laid out **< 50 ms** after its rows arrive. Paged extension produces lanes **identical** to one full pass for 100% of rows. Lane count **≤** `git log --graph`'s width on the same input. Golden-file output deterministic across runs. If it fails, switch to the client-side fallback (§4.3). |
| **S6** | Is the page read plus coverage overlay fast? | Endpoint over a 100k-row snapshot: 200-row pages at the top, middle and end; overlay via `LoadAsync` of 200 commit ids | **< 100 ms** server time per page (p95 over 50 reads), with 0 RQL queries in the overlay. |
| **S7** | Is a hostile repository harmless? | Fetch a scratch repository with a submodule, a `.gitmodules` `file://` URL and 60k refs; one above the size quota | No submodule fetch and no local file access. A planted local hook (`reference-transaction`, `post-checkout`) writes **no** marker file on clone or fetch (D5). Quota rejection is recorded in `RepositoryGraphState` with a reason. The process is killed at the timeout with no orphan left. |
| **S8** | Does a long initial clone survive the messaging claim TTL (5 min)? (D19) | Initial clone throttled to exceed 5 min. A second `POST /fetch` for the same repository mid-way, simulating the redelivered message. | Exactly **one** git process per repository at any time. The second call **joins** and returns the same result. The clone completes once. No duplicate or corrupt mirror. |
| **S9** | Does per-row SVG in the virtual datatable scroll well, in both themes? | Panel prototype over 10k and 100k rows; scripted fast scroll with a performance trace; contrast check of an 8-colour lane palette on light and dark backgrounds | p95 frame **< 16.7 ms**. First rows painted **< 200 ms** after data arrives. **< 2,000** live DOM nodes. Every lane colour **≥ 3:1** against both backgrounds. |

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| The VPS disk fills with mirrors | Quotas (S7), a size column in `RepositoryGraphState`, deletion on disconnect (§5.2) |
| The image grows and gains a CVE surface (`git`) | Install only `git` with `--no-install-recommends` in the runtime stage; rebuilt on base-image updates through the existing grouped Dependabot (#419) |
| The installation token lacks `contents: read` in production | S1 checks first; a permission change needs the owner to approve it on each installation |
| Messaging claim expiry causes a double clone | Per-repository file lock (S8) |
| The production deploy needs a new volume | Add the volume to `docker-compose.yml` and `docs/code-coverage/hosting.md`; plan M8 is the deploy step |
| A large repository makes the first view slow | The panel renders "syncing" from `RepositoryGraphState`; the mirror is never fetched on the request thread |

---

## 9. Host metrics for administrators (D8, D9)

The mirrors add a disk cost that someone must be able to see. The owner asked two things: whether
the **host's** disk can be measured from inside a container, and whether Spark should ship a package
for administrator dashboards. Two investigators answered on 2026-10-11.

### 9.1 No dashboard package. A live virtual PersistentObject instead.

Spark already has every piece **[verified]**:
- **Virtual POs:** SeoTester's `OperatorDashboard` has no `clrType` and no documents. It is filled in
  `OnLoadAsync` (`apps/SeoTester/SeoTester/Admin/OperatorDashboard.cs:21-33`).
- **Push:** `/spark/live` is a multiplexed WebSocket.
  - It accepts PO subscriptions only for types with `"live": true` (`libs/spark/MintPlayer.Spark/Endpoints/Live/LiveConnection.cs:243-251`).
  - It re-evaluates through the normal read path on Raven changes, on `ISparkLiveNotifier.Changed(id)`
    (`MintPlayer.Spark.Abstractions/Live/ISparkLiveNotifier.cs:19`), or every 1 min by default
    (`SparkLiveOptions.cs:60`).
  - `spark-po-detail` follows live types automatically (`ng-spark/models/src/entity-type.ts:126-130`).
- **Renderers:** `spark-gauge`, `spark-threshold-bar`, `spark-trend-sparkline`
  (`ng-spark` `score-renderers/src/index.ts`).
- **Rights:**
  - A `Read/HostMetrics` right granted to a group in `security.json` hides the menu entry.
  - The live subscription refuses callers without that right, giving one answer for unknown,
    forbidden and not-live (`LiveConnection.cs:229`).

There is **no second consumer**. SeoTester's dashboard is deliberately app code
(`docs/seo_analyzer_PRD.md:676-682`, D28), SparkId has no admin dashboard, and the demos don't drive
framework design. A widget package would add an API with one user. That goes against the owner's
standing preferences (compose existing blocks, a lean API). **Revisit when a second app needs the
same metrics.** At that point, extract the parts both apps share (e.g. `formatBytes` into
`ng-spark/pipes`, and a library virtual PO under an `xyz:administrators` slot).

> **[superseded by D11–D14]** The block below described app code. HostMetrics is now a *provider* on
> the Spark dashboard package (plan M5a/M5b). The package owns the notifier, the read cache and the
> slot. The attributes and sources in §9.2 are unchanged.

**The design (app code in CodeCoverage):**
- `App_Data/Model/HostMetrics.json`: `"live": true`, no `clrType`, object id `hostmetrics/main`.
  Attributes are rendered with `spark-gauge` / `spark-threshold-bar`.
- `HostMetricsActions.OnLoadAsync` reads the sources in §9.2.
- A `BackgroundService` calls `ISparkLiveNotifier.Changed("hostmetrics/main")` every 15 s while
  anyone is subscribed. Each read is cached for 5 s, so that concurrent viewers trigger one statfs
  between them.
- A query over `RepositoryGraphState` with a size column answers "which repositories use the disk",
  with no new widget.
- **An `Administrators` group in CodeCoverage's `security.json`.** Today it has only "Anonymous
  visitors" and "Signed-in users" (`apps/CodeCoverage/.../App_Data/security.json:7-10`)
  **[verified]**. This is a production authorization change. The security posture must be
  regenerated (`--spark-synchronize-security`).

**Untested, so S10 covers it:** `"live": true` on a type with no `clrType`, and a notifier id with no
backing document.

### 9.2 Which numbers come from where

Owner, 2026-10-11: *"Some of the measurements can probably be the container measurements. But the
disk usage should probably be from the host."*

| Attribute | Source | Measures |
|---|---|---|
| Host disk: total / free / used % | `DriveInfo("/")` in the app container | **The host root filesystem** (`/dev/sdb1`, 150 GB). overlay2 reports its backing filesystem, `/var/lib/docker` on the host root **[cited]**. Exception: overlay2 on XFS with `pquota`, which this host does not appear to use **[inferred → S10]**. |
| Mirror count, total mirror size vs budget, largest mirrors, `DiskGuard` refusals | `RepositoryGraphState` documents (size measured per repository directory by the sidecar after each fetch, D8) | App data. The total is exact as of each repository's last fetch. |
| App CPU % | `System.Runtime` meter (`process.cpu.time`) over the sampling interval, or ResourceMonitoring `container.cpu.limit.utilization` | **The container** |
| App memory | cgroup v2 `memory.current` / `memory.max`; GC heap from `GC.GetGCMemoryInfo` | **The container** (`memory.current` includes page cache) |
| Host RAM / CPU | **Not shown** | A container sees only host totals in `/proc` without its own share. True host-wide figures need the host's `/proc` mounted in, a security cost not worth paying when Hetzner's console already graphs them. |

No `docker.sock`, no host `/proc`, and no writable host mount in the app.

### 9.3 Security

- The PO is readable by Administrators only.
- Without the right, the live subscription and the load both give Spark's single denial answer, so
  the type's existence doesn't leak.
- No mount paths, device names, exception text or environment values go into attribute values.
  Labels are fixed strings ("Host disk", "Mirror volume").
- The 5 s read cache plus the live hub's 500 ms throttle bound the statfs cost per viewer.

### 9.4 Spikes S10–S12

S11 and S12 sit here with S10 because they were added during the grill.

| # | Question | Method | Pass |
|---|---|---|---|
| **S11** | Does dashboard history work on RavenDB time series (D14)? | One value sampled every 60 s on the embedded test server, keeping raw points for 1 day and hourly rollups for 30 days. Simulate 2 days of points through timestamps. Two sampler hosts run concurrently. | Raw points older than 1 day are gone and rollups exist. A 7-day history read returns **≤ 300** points in **< 100 ms**. **0** duplicate points from the two hosts (lease holds). **0** revisions created on the backing collection. |
| **S12** | Can avatars be resolved without leaking emails or burning budget (D17)? | Resolve this repository's authors: no-reply emails parsed locally, the rest through the forge with a per-author cache. | **0** author emails in any response body sent to the browser (asserted by a test). A second view of the same page makes **0** forge calls. No-reply authors make **0** forge calls. |

### S10

| # | Question | Method | Pass |
|---|---|---|---|
| **S10** | Do the metrics measure what §9.2 claims, and does a live virtual PO stream them? | On the VPS: log `DriveInfo` for `/` from inside the app container and for the named volume from inside the sidecar, then compare both with `df -B1 /` on the host. Compare the sidecar's per-repository size with `du -sb` of that directory. Locally: a virtual `"live": true` PO with no `clrType`, notified every 15 s. | Totals and free bytes within **1%** of the host's `df` for both, and no 0 values. Per-repository size within **1%** of `du`. The detail page updates **≥ 3** times in 60 s without a reload. An anonymous user and a signed-in non-admin get Spark's standard denial on both load and live subscribe. |

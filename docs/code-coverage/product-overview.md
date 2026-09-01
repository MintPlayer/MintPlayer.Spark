# Coverage — Product Requirements Document

A self-hosted code-coverage analyzer for GitHub (in the spirit of codecov.io / coveralls.io), built on **MintPlayer.Spark** (ASP.NET Core + RavenDB + Angular 22) with **mintplayer-ng-bootstrap** as the UI framework, plus a **GitHub Action** that uploads coverage reports from workflows.

> Companion document: [PLAN.md](PLAN.md) (milestones and sequencing).
> Research basis: a four-agent investigation (2026-08-07) of the MintPlayer.Spark and mintplayer-ng-bootstrap codebases, the Codecov open-sourced backend (`codecov/umbrella`), Coveralls' API/action, and coverage-format specifications. Key claims below carry their source.

---

## 1. Product overview

### What it does

- Users **sign in with their GitHub account**, see their organizations and repositories, and browse coverage per commit.
- CI (GitHub Actions) **uploads coverage report files** (lcov, cobertura, …) for a commit using an **upload token** or — preferred — **tokenless via GitHub Actions OIDC**.
- Multiple uploads from a single workflow run are **bundled into one build** and merged.
- Coverage is browsable: **organization → repository → commit → file/folder tree → file view** with per-line green/red/orange highlighting and syntax highlighting.
- Repos get an **SVG badge** for their README; private-repo badges are protected by a scoped badge token.
- Optional later: PR comments and commit status checks.

### Non-goals (v1)

- Non-GitHub forges (GitLab, Bitbucket).
- Codecov-style YAML config files in the repo, path fixes, ignore rules.
- Carryforward flags (design for it — per-session storage makes it retrofittable — but don't build it).
- PR diff ("patch") coverage — stretch goal after MVP.

---

## 2. Hard architectural rule: generic code goes upstream

Anything not specific to the coverage domain is implemented in the appropriate upstream repository and consumed from there — **one PR per repo**:

| Repo | Generic work that belongs there |
|---|---|
| `MintPlayer.Spark` | API-token (PAT) authentication library; missing webhook events (`workflow_run`, `status`); bug fixes found during investigation (see §10) |
| `mintplayer-ng-bootstrap` | Coverage-annotatable code viewer; circle-packing/sunburst chart; radial progress; datatable column filtering (if needed) |
| `Coverage` (this repo) | Coverage domain only: parsers, normalized model, merge, upload API, badge endpoint, app UI, and the GitHub Action under `action/` |
| `apps/CodeCoverage/action/` | The GitHub Action, consumed as `MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@coverage-upload-v1` |

We are **not confined to Spark's `/spark/*` endpoints**: the app freely adds its own controllers/minimal-API endpoints (`/api/uploads`, `/badge/…`) alongside Spark — the sanctioned pattern WebhooksDemo already uses (its SPA fallback excludes both `/spark` and `/api`). The rule cuts the other way: whenever a piece of such an endpoint turns out to be generic (token authentication, a file-upload primitive, a webhook event), it is extracted into Spark rather than kept app-local.

---

## 3. What Spark already provides (verified in source)

The Spark repo has **no dotnet template** — a new app is scaffolded by copying the `Demo/WebhooksDemo` anatomy (the most complete demo: GitHub auth + webhooks). Verified building blocks:

- **GitHub OAuth login**: hand-rolled `IdentityBuilder.AddGitHub(...)` on the generic OAuth handler, full ASP.NET Identity with a RavenDB `UserStore`/`RoleStore` (compare-exchange e-mail uniqueness), auto-provisioning gated on verified e-mail, popup + `postMessage` flow. (`libs/authorization/.../GitHubAuthenticationExtensions.cs`)
- **GitHub App webhooks**: `Octokit.Webhooks.AspNetCore` endpoint at `/api/github/webhooks`, HMAC validation (constant-time, fail-closed), typed events dispatched as durable `GitHubWebhookMessage<TEvent>` over a RavenDB-backed message bus to source-registered `IRecipient<T>` handlers; JWT app-auth + cached installation tokens; smee.io and WebSocket dev tunnels. Events currently modelled: push, issues, issue_comment, PR, PR review(+comment), check_run, check_suite, **installation**, repository.
- **Org access discovery**: `OrganizationAccessService` in WebhooksDemo calls `GET /user/installations` with the user's saved OAuth token — exactly the "mirror GitHub permissions" pattern we need (promote to a Spark lib if reused verbatim).
- **Declarative model**: entities as POCOs in a `*.Library` project + `App_Data/Model/*.json` metadata; generic Angular UI (`sparkRoutes()`: query list, PO detail/edit/create) with **custom attribute renderers** as the extension point; custom pages are plain Angular routes calling `SparkService`.
- **Data layer**: RavenDB via `SparkContext`; messaging (durable queue with retries/dead-letter), cron jobs (cluster-safe), subscription workers.
- **Frontend stack**: Angular 22, zoneless, standalone, signals, Vitest; ng-bootstrap consumed at `^22.4.0` (current: 22.13.0 — an upgrade is needed, see §10).

**Confirmed gaps in Spark** (things we must build upstream):

1. **No API-token/PAT infrastructure whatsoever** — no store, no `AuthenticationHandler`, no endpoints. The only non-cookie path is the implicit (unused, undocumented) Identity bearer-token flow. → Coverage built it app-local; the planned Spark library was cancelled (§6, §10).
2. No `workflow_run`/`status` webhook events (only needed if we react to CI runs server-side; not required for MVP).
3. **No file-upload/attachment/blob support at all** (verified: zero `IFormFile`/attachment/multipart hits in the framework). RavenDB itself supports attachments via the raw session API, which is what we'll use for raw report storage (§7); a Spark-level storage abstraction is *not* needed for v1.
4. No badge/SVG rendering (app concern — domain-specific, stays in Coverage).
5. Rate limiting exists but is **opt-in and scoped to `/spark/*` paths only** (fixed-window per client IP, 150 req/10 s default). Our `/api/uploads` and `/badge` endpoints live outside `/spark`, so they need their own ASP.NET `RateLimiter` policies.
6. Useful extras confirmed: `MintPlayer.Spark.Migrations` (cluster-safe, forward-only, compare-exchange locked) for seed/schema migrations; `MintPlayer.Spark.Testing` (embedded-RavenDB test harness — requires a RavenDB license via `RAVENDB_LICENSE` env var or `raven-license.log`); Spark query paging happens **in memory** after materialization — our commit lists and file trees must use custom endpoints/indexes, not Spark queries, once data grows.
7. ~~Open security finding R4-H1~~ — **fixed in preview.42** (Spark#231): row-level rules now apply to lists, custom queries and streams via a shared `IRowSecurity`. Historical note: this finding originally motivated Coverage's DenyAll + custom-`/api` architecture, which we keep regardless.

---

## 4. How the incumbents do it (research synthesis)

Full detail with URLs in the research reports; the load-bearing findings:

- **Codecov's backend is public** at `github.com/codecov/umbrella` — but licensed **FSL-1.1-Apache-2.0** (no competing use; each version becomes Apache-2.0 after 2 years). Fine to *read*; do not vendor. Coveralls' `coverage-reporter` (Crystal) is **MIT** — safe to port parser logic from. `danielpalme/ReportGenerator` is **Apache-2.0** — safe to port/reference (its format-sniffing dispatch and LCOV parser details are worth copying).
- **Codecov OIDC verification**: validate the workflow's JWT against GitHub's JWKS (`token.actions.githubusercontent.com/.well-known/jwks`), require `aud` == your own service URL, then resolve the repo from the `repository` + `repository_owner` claims. No stored secret at all.
- **Coveralls' GITHUB_TOKEN trick** (replaying a live GitHub token to prove repo access) is strictly weaker than OIDC — skip it.
- **Access control**: Codecov has *no internal ACL* — permissions mirror the git provider, synced on login + manual resync. This deletes most of the "join request" problem space (see §6.3).
- **Bundling**: Coveralls = explicit build + explicit `done` webhook; Codecov = implicit merge + `after_n_builds` count heuristic (a known misfeature; they later added an explicit completion endpoint anyway). Recommended hybrid: explicit build keyed by run id, auto-finalize on debounce, optional explicit finish.
- **Merging**: LCOV/ReportGenerator *sum* counts; Codecov takes the *max*. **Max wins** — idempotent under job retries, re-runs, and duplicate uploads.
- **Badges**: Codecov: private badges use a separate opaque `?token=` (scoped to the badge only, rotatable). Coveralls: private badges are simply unauthenticated (known issue since 2014). Follow Codecov.
- **The SHA trap**: on `pull_request` events `GITHUB_SHA` is the ephemeral merge commit; the action must send `github.event.pull_request.head.sha`.

---

## 5. Domain model

RavenDB documents — **as built** in `Coverage.Library/Entities/` (deterministic ids so webhook
and upload upserts are idempotent):

```
Account            id: Accounts/{gitHubId}
  GitHubId, Login, Type (User|Organization), AvatarUrl
  InstallationId?          // GitHub App installation, when installed
  // no Admins list — GitHub is the authority (§6.3), nothing cached

Repository         id: Repositories/{gitHubId}
  Account (ref), GitHubId, Name, FullName, OwnerLogin, IsPrivate, DefaultBranch, Archived
  BadgeToken?              // random, only set for private repos; rotatable
  LatestCoverage?, LatestCoverageSha?, LatestCoverageAtUtc?   // denormalized at finalize

ApiToken           id: ApiTokens/{sha256hex}          // uniqueness by construction
  Scope: Account | Repository, AccountLogin? | RepositoryGitHubId?
  Description?, CreatedByUserId, CreatedAtUtc, RevokedAtUtc?
  // token value (covt_…) shown once at creation; only the SHA-256 hash exists here

Commit             id: Commits/{repoGitHubId}/{sha}
  Repository (ref), Sha, Branch?, PullRequestNumber?, ParentSha?, Message?, AuthoredAt?
  FirstSeenAtUtc?, Coverage? (promoted at finalize), LatestBuildId?
  // AuthoredAt only arrives via webhooks; lists sort AuthoredAt ?? FirstSeenAtUtc (M9.1)

Build              id: Commits/{repoGitHubId}/{sha}/builds/{runId}-{runAttempt}
  Commit (ref), Status: Open | Finalized
  CiRunId, CiRunAttempt, WorkflowName?, EventName?
  CreatedAtUtc, LastUploadAtUtc?, FinalizedAtUtc?, FinalizeReason? (Explicit|Debounce|Timeout)
  Sessions: [ { SessionId, JobName?, Flags[], UploadedAtUtc,
                ParseStatus (Pending|Parsed|Failed), Error?, RawFileNames[], RootDir?, FilesCount } ]
  Coverage?                // raw report files live as attachments on this document

FileCoverage       id: {buildId}/files/{pathHash}
  BuildId, Path (normalized repo-relative), Matched (path resolved against git ls-files)
  BranchFormat?    // the format that produced Branches — branch detail merges within it only
  Lines: [ { Number, Hits?, Status } ]   // merged across sessions (max)
  Branches: [ { Line, BlockId, BranchId, Taken? } ]

CoverageSummary    (embedded) LinesCovered, LinesCoverable, BranchesCovered, BranchesTotal,
                   FilesCount            // rates always derived, never stored
```

**Normalized line model** (dictated by the formats — JaCoCo and VS coveragexml have *no hit counts*):

```
Line   { Number, Hits: int?,  Status: NotCovered | PartiallyCovered | Covered }
Branch { Line, BlockId, BranchId, Taken: int? }   // totals derive by counting edges
```

Non-coverable lines are simply absent from the data (no `NotCoverable` member). All percentages derive from `Status`, never from `Hits`. Merge across sessions = **max** per line/branch key; branch detail never merges *across different formats* (identity schemes differ — lcov's real ids vs Cobertura/JaCoCo's synthesized edges): `FileCoverage.BranchFormat` records who owns the branch set, and a session in another format contributes line status only.

**Raw uploads are retained** (the uploaded report files, gzipped) so the merged view *can* be lazily recomputed — late uploads and re-runs work today; a reprocess-after-parser-fix endpoint is backlog, the raw data for it is already there. Storage medium: RavenDB attachments on the Build document to start (they replicate/backup with the database); revisit if size becomes a problem.

**Folder-tree aggregation** reads a `BuildTreeSummary` document (`{buildId}/tree`, per-file line totals) materialized at finalize — one point-load per request for both the `/tree` folder-level endpoint and the `/hierarchy` sunburst endpoint. Builds finalized before the summary existed fall back to streaming their `FileCoverage` docs. Note: a late upload re-opens a finalized build, and the summary only refreshes at the next finalize — the tree can serve pre-late-upload numbers in that window.

---

## 6. Authentication & authorization

### 6.1 Interactive users
Spark's existing GitHub OAuth login, with `SaveTokens = true`. Repo/org *visibility* mirrors GitHub: on login (and on manual "resync", and on a 5-min TTL) the server queries `GET /user/installations` with the user's own OAuth token (plus the user's own login) and caches the resulting **owner list** per user. Visibility is owner-granular: reaching an installation grants every repo of that owner we know about — there is no per-repo check (`GET /user/repos` is not called). **No parallel permission system.** Private repo pages require the viewer's GitHub access; public repo pages are world-readable.

### 6.2 Upload credentials — two, and only two

1. **GitHub Actions OIDC (preferred, tokenless).** The action requests an ID token with `audience=<our base URL>`; the server validates it as a standard JWT bearer (`Authority = https://token.actions.githubusercontent.com`, JWKS cached by `ConfigurationManager`). Claims usage, precisely: `run_id`/`run_attempt` **override** the body; `repository` is **validated-equal** against the body (mismatch → 404); `repository_id` is claim-only (no body field); the `sha` claim is **deliberately NOT used** — on pull_request events it is the ephemeral merge commit, while the body carries the PR head. Caveat: fork PRs never get `id-token: write` → fall back to token or accept quarantined unauthenticated uploads (v2).
2. **Upload token (fallback for forks, other CIs, local runs).** Scoped to an account (org/user) or a single repository. Value = 43-char random urlsafe string with a recognizable prefix (`covt_`), stored only as SHA-256 hash (which is also the document id → globally unique by construction). Shown once. Revocable, listable, auditable.

Deliberately **app-local** (the planned Spark ApiTokens library was cancelled upstream in favor of `client_credentials` — §10): token entity + store, issuance/listing/revocation via `TokensController`, and an `AuthenticationHandler` resolving `Authorization: Bearer covt_…` / `Token covt_…` to a principal with scope claims, registered as a Spark **credential scheme** (`spark.AddCredentialScheme<…>` inside the AddSpark callback — non-ambient, so it joins the composite authenticate without widening antiforgery).

### 6.3 The "two users, one organization" problem

The requirement as stated: tokens are unique per GitHub account/org, so a second member of an already-registered org must send a "join request".

**Recommended design — GitHub is the authority; no join workflow.** An organization is registered once (first user installs the GitHub App on it — the `installation` webhook creates the `Account`). Any user who can reach that installation (their own OAuth token's `GET /user/installations`) automatically *sees* it — and, as built, can also **manage** its tokens/settings: there is no admin-role check today, so management rights equal installation visibility (an org-admin gate via `GET /user/memberships/orgs/{org}` is backlog, PLAN M9.29). A second member never creates a duplicate org and never needs anyone's manual approval — GitHub membership *is* the approval. This is Codecov's model, it removes an entire approval-queue feature, and it can't drift out of sync with reality.

**Fallback (only if wanted later):** a manual join-request flow for edge cases where membership can't be verified (e.g. user declined `read:org` scope). Kept out of v1.

### 6.4 Badges without leaking private repos

- Public repo: `GET /badge/{owner}/{repo}.svg` — unauthenticated, `Cache-Control: max-age=300`, its own per-IP rate-limit policy (GitHub's camo proxy funnels README renders through few IPs). Renders the repo's denormalized default-branch coverage; `?branch=` renders the newest covered commit of that branch instead.
- Private repo: same URL + `&token={BadgeToken}` — an opaque, repo-scoped, independently rotatable secret that grants **only the rendered SVG** (never report data, file lists, or API access). Anyone who can read the README can already see the code, so the badge number leaks nothing *to them*; the forwarding risk is acceptable because the capability is so narrow. Wrong/missing token → a generic "unknown" badge (don't 404 — that confirms existence). The `Cache-Control` header follows the same rule: it depends only on whether a token was presented, never on whether the repo exists.

---

## 7. Ingestion pipeline

```
POST /api/uploads   (Bearer: OIDC JWT or covt_ token)
  multipart form: flat metadata fields + N gzipped report files (+ fileList = `git ls-files` output)
      │
      ▼
  resolve repo (claims/token) → upsert Commit → upsert Build (repoId, sha, runId, runAttempt)
  → store raw files as attachments → append Session → enqueue ParseSession message → 202
      │                                    (Spark durable message bus)
      ▼
  ParseSessionRecipient: sniff format → parse → normalize paths → merge into FileCoverage (max)
      │
      ▼
  Finalize: explicit POST /api/uploads/finish  OR  debounce (~2 min no new uploads)
            OR timeout (~30 min) → Build.Status = Finalized → recompute Commit.CoverageSummary
            → (later) notify checks/PR comment
```

### Parsers (server-side; the action never parses)

`ICoverageParser` implementations behind a sniffing factory modelled on ReportGenerator's root-element dispatch (`coverage`→Cobertura/Clover/…, `report`→JaCoCo, `CoverageSession`→OpenCover, text starting `TN:`/`SF:`→LCOV, JSON with `statementMap`→Istanbul, `mode:` header→Go).

Priority order (≈80 % of real uploads come from the first two):
1. **LCOV** (`.info`) — built. Mind lcov 2.x records (`FNL`/`FNA`, `e|f|U` block prefixes, `BRDA` taken=`-`).
2. **Cobertura** — built. Also covers coverage.py XML and coverlet; branch data in `condition-coverage="… (c/t)"`; group multiple `<class>` by `@filename`.
3. **JaCoCo** — built. Validates the nullable-hits design (`mi`/`ci`/`mb`/`cb` only): executed lines carry `Hits = null`, unexecuted a genuine 0.
4. Istanbul JSON, Clover, OpenCover, Go cover (backlog).
5. Long tail via an **opt-in ReportGenerator.Core adapter** (Apache-2.0) mapping `ParserResult` into our model — never as the core dependency (assembly-shaped model, undocumented API, sum-merge semantics).

### Path normalization (the real hard part)

Every parser output goes through one normalizer: strip `rootDir` (= `GITHUB_WORKSPACE`, sent by the action), unify slashes, resolve Cobertura `<source>` roots — and as the universal fallback, **suffix-match against the uploaded `fileList` (git ls-files)**, which is also what resolves JaCoCo's package-relative paths (`com/example/Foo.java` → `src/main/java/…`; the match gives up when two repo files share a basename and both match). Unmatched files land in an "unmatched" bucket visible in the build UI instead of silently vanishing. Go module-prefix stripping comes with the Go parser (backlog).

---

## 8. GitHub Action (`action/` in this repo)

Lives at `action/` in MintPlayer/CodeCoverage, consumed as `MintPlayer/CodeCoverage/action@<ref>` (extract to its own repo only if a Marketplace listing is ever wanted — `action.yml` must sit at a repo root for that). **node20 JavaScript action, TypeScript, bundled with `@vercel/ncc`** (dist/ committed + CI check for staleness). Composite/bash is what Codecov uses only because they ship a compiled CLI; we have no CLI because parsing is server-side. Node gives `@actions/glob` (discovery), hand-rolled fetch retries, `core.getIDToken(audience)` (OIDC) portably on all three OSes.

- **Inputs**: `url` (server base), `token` (optional), `use-oidc` (default false; auto-chosen only when NO token is supplied and `id-token: write` is available), `files`/`directory` (globs; else auto-detect using Codecov's proven glob + ignore lists), `flags`, `name`, `fail-ci-if-error` (default false), `finish` (boolean → calls the finalize endpoint, for users who want deterministic completion), `disable-search`.
- **Sends**: gzipped report files + flat form fields: `repository`, `commitSha` (= `pull_request.head.sha` on PR events — **never** the merge `GITHUB_SHA`), `parentSha` (the PR base SHA on PR events), `branch` (`GITHUB_HEAD_REF` on PRs else `GITHUB_REF_NAME`), `pullRequestNumber`, `eventName`, `runId`, `runAttempt`, `jobName`, `workflow`, `flags` (comma-joined, split server-side), `rootDir` (`GITHUB_WORKSPACE`), `fileList` (`git ls-files`). The repository *id* travels only as an OIDC claim, never as a body field.
- **Multiple invocations per run** are the *designed* case: each call = one session appended to the same Build (`runId`+`runAttempt` key).
- **Versioning**: two tags, and only one of them moves. `coverage-upload-v<major>` follows the
  newest commit of that major and is what consumers pin; `coverage-upload-v<full version>` is cut
  once and never moves, so any upload stays reproducible and a bad release can be pinned backwards.
  Both names are derived from `apps/CodeCoverage/action/package.json`, so a major bump retires the
  old moving tag by itself. **Never pin `@master`** — this is a monorepo whose default branch moves
  many times a day for unrelated reasons.
- **Releasing**: Actions → *coverage-action-publish* → **Run workflow**, choosing
  `patch`/`minor`/`major`; or `npm version <bump> --no-git-tag-version` in the action folder as part
  of a code PR. Full flow, including the new-major playbook, in
  [`apps/CodeCoverage/action/README.md`](../../apps/CodeCoverage/action/README.md#releasing-a-new-version).

Usage sketch:

```yaml
permissions:
  id-token: write        # tokenless OIDC
steps:
  - uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@coverage-upload-v1
    with:
      url: https://coverage.mintplayer.com
      use-oidc: true
      files: '**/coverage.cobertura.xml'
      flags: unit
```

---

## 9. Website UI

Structure (Spark app with custom pages; generic Spark PO/query UI used for admin-ish screens, custom Angular pages for the browsing experience):

1. **Home** — the user's accounts (orgs + personal), each with repo count and aggregate coverage, plus a manual "Resync" of GitHub visibility. Public "explore" list optional.
2. **Account page** — repositories with latest default-branch coverage %, sparkline per repo, and the upload-token management card (create account- or repo-scoped, list, revoke; the card renders only when the server's token list loads — fail-closed on any error, not 403 specifically).
3. **Repository page** — branch selector, coverage-over-time trend chart (80% goal line), commit list with coverage % and delta; badge snippet (markdown, copy button, built on the server's `Coverage:BaseUrl`) with inline badge-token create/rotate for private repos (public badges need no token); per-SDK workflow setup card.
4. **Commit/build page** — summary header (ring, coverage bar, lines/branches/files) + a builds table (sessions/flags with parse status), **file/folder tree** and **sunburst diagram** side by side (both click-through), unmatched-files warning.
5. **File view** — syntax-highlighted source with per-line coverage gutter: green (covered), red (uncovered), orange (partial branch), hit counts, deep-linkable line anchors (`#L42`).

### ng-bootstrap: use vs build

**Ready to use** (as of ng-bootstrap **22.14.0** / web-components **2.11.0**, charts added by [PR #401](https://github.com/MintPlayer/mintplayer-ng-bootstrap/pull/401)):

- `bs-datatable` tree mode (expandable rows — https://bootstrap.mintplayer.com/enterprise/datatables; the as-built file browser uses a plain `bs-table` drill-down, upgrade tracked PLAN M9.28), `bs-shell`, `bs-navbar`, `bs-breadcrumb`, `bs-progress-bar` (linear % cells), `bs-badge`, `bs-card`, `bs-tab-control`, `bs-typeahead`, `bs-tooltip`/`bs-popover`, `bs-modal`/`bs-toast`, theming.
- **`bs-hierarchy-chart`** (`@mintplayer/ng-bootstrap/charts/hierarchy`) — the coverage diagram, purpose-built: `layout="sunburst" | "icicle" | "treemap"`, `HierarchyNode {id,name,value,colorValue,children,hasChildren}` where arc size = summed leaf `value` (lines) and color = `colorValue` (coverage %) with folder colors derived as value-weighted means; lazy `loadChildren`; `(zoom)` for folders / `(nodeSelect)` for leaves with the full ancestor `path`; two-way `[(rootId)]` to sync an external tree; full `role="tree"` keyboard/SR support. Feed per-file `{id: path, value: coverableLines, colorValue: coveredPct}` — no server-side folder rollup needed. Set `colorMin/colorMax` ≈ 60/80, not the 0–100 default.
- **`bs-trend-chart`** — coverage-over-time with `goal` line; **`bs-sparkline`** for inline table trends.
- **`charts/core`** exports `arcPath` + `colorScale` publicly — the sanctioned way to hand-roll the headline radial ring (~20 lines; pass `ringGap: 0`).

**Code viewer — shipped upstream and adopted** ([PR #402](https://github.com/MintPlayer/mintplayer-ng-bootstrap/pull/402), 2026-08-11 → `22.15.0` / web-components `2.12.0`): `bs-code-snippet` was **extended into the viewer** (no separate `mp-code-viewer`): per-line subgrid DOM, `annotations: CodeLineAnnotation[]` (`{line, kind, label, secondaryLabel, description}` — style via `::part(annotation-<kind>)`), `lineNumbers`, `lineHref` (fragment-safe with `<base href>`), `activeLine` + `scrollToLine()` method, `data-bs-theme`-following `light-dark()` theme. The file page uses it since PLAN.md **M10** (highlight.js as a direct dependency, `scrollToLine()` for deep links since the rows live in a shadow root, extension→grammar map gated on `canHighlight`).

`mp-progress-circle` remains **declined upstream** — Coverage's hand-rolled `CoverageRingComponent` on the public `arcPath`/`colorScale` is the design.

---

## 10. Upstream work — RESOLVED

All upstream blockers have landed:

- **[MintPlayer.Spark#231](https://github.com/MintPlayer/MintPlayer.Spark/pull/231)** (merged 2026-08-09 → `10.0.0-preview.42`, `ng-spark-auth 22.1.0`): queue-name bug fixed (typed `GitHubWebhookMessage<TEvent>` recipients now work — names derived via `QueueNames.Derive`, route by CLR type only), popup handshake fixed (+ `loginWithProvider(provider, {mode})` client API), ng-bootstrap bump, R4-H1 row-level authz fixed across query/stream paths, doc fixes. The ApiTokens library was **deliberately cancelled** in favour of OAuth2 `client_credentials` via the new audited `MintPlayer.Spark.IdentityProvider`; Coverage keeps its app-local `covt_` tokens (GitHub OIDC remains the preferred CI path).
- **[mintplayer-ng-bootstrap#401](https://github.com/MintPlayer/mintplayer-ng-bootstrap/pull/401)** (merged 2026-08-10 → `22.14.0` / web-components `2.11.0`, purely additive): hierarchy/trend/sparkline charts (§9).

- **[mintplayer-ng-bootstrap#402](https://github.com/MintPlayer/mintplayer-ng-bootstrap/pull/402)** (merged 2026-08-11 → `22.15.0` / `2.12.0`): the unified code-snippet viewer (§9) — the last upstream ask.

**Nothing remains upstream.** Only cosmetic nit left: `_bootstrap.scss` Sass `@import` deprecation noise. (Two earlier nits were investigated upstream and closed: `bsShellTopbar` needs no promotion — `<div slot="topbar">` works directly; the `bs-progress-bar` host-class clobbering was measured not-real.)

---

## 11. Repo & deployment shape

- **Coverage app**: standalone repo (`C:\Repos\Coverage`), scaffolded by copying the WebhooksDemo anatomy (built — see PLAN.md M1). **Versions (2026-08-12, post-M10)**: on `MintPlayer.Spark.*` **10.0.0-preview.42** (latest), `@mintplayer/ng-spark` 22.0.8, `@mintplayer/ng-spark-auth` ^22.1.0, `@mintplayer/ng-bootstrap` **22.15.0** + `@mintplayer/web-components` **2.12.0** (pinned exact), `highlight.js` **^11.11.1** as a direct dependency (optional peer upstream, but statically imported by the published code-snippet module).
- **GitHub App** (one per environment, prod + dev, as WebhooksDemo does): repository permissions — contents: read (source display + `push` events), metadata: read (mandatory), pull requests: read (`pull_request` events); account permissions — email addresses: read (Spark's first-sign-in auto-provisioning requires a GitHub-attested verified primary email via `GET /user/emails`); checks: write + PR: write only later, with M9.11. No org permissions: viewer visibility comes from `GET /user/installations` with the user's own OAuth token. Webhook events — repository, push, pull_request (installation events are always delivered). Full setup table: README "GitHub App settings".
- **Dev loop**: RavenDB local, smee.io tunnel for webhooks, `dotnet run` (host spawns the Angular dev server — never run `ng serve` manually), `Synchronize` launch profile for model sync.
- **Deployment**: docker-compose (app + pinned RavenDB on an internal network, Traefik labels) following WebhooksDemo's `docker-compose.yml`/Dockerfile — including its supply-chain notes (selective csproj COPY closure). Target host **coverage.mintplayer.com**. Fully automated pipeline (`publish.yml`): master push (docs/action/markdown-only pushes are path-filtered out) → test → ghcr.io image → SSH redeploy on the VPS (compose refetched from master; `.env` + `github-app.pem` are server-managed and never written by deploys). RavenDB data persists in the `raven-data` named volume; backup is out-of-band. One-time VPS/DNS checklist: README "Deployment".

---

## 12. Risks & open questions

| # | Item | Position |
|---|---|---|
| 1 | ~~Spark NuGets published?~~ | **Resolved**: published & current (`10.0.0-preview.42`); use PackageReference. |
| 1b | ~~R4-H1 row-level auth gap~~ | **Resolved upstream** (Spark#231: `IRowSecurity` enforced on query/custom/stream paths). Coverage keeps DenyAll + custom `/api` anyway (defense in depth, matches usage). |
| 1c | Scaling flags from the Spark#231 review of this repo | Tree/hierarchy streaming fixed (materialized `BuildTreeSummary`, M9.20). Remaining backlog: file view renders one DOM node per line (no virtualization — upstream viewer also renders plain, 2000 rows measured fine); no live refresh of in-flight builds. |
| 2 | Report size / RavenDB attachments | Fine for typical reports (KB–MB). Very large monorepo lcov files may need a blob-storage abstraction later. |
| 3 | Join-request flow | Recommended *out* (GitHub is the authority, §6.3) — confirm with owner. |
| 4 | Fork PR uploads | v1: token fallback documented; quarantined tokenless uploads later. |
| 5 | Source display in file view | Fetch file content from GitHub at view time via installation token (contents: read), raw.githubusercontent.com fallback for public repos — we never store source; 30-min `IMemoryCache` keyed by the immutable (repo, sha, path) — no conditional requests needed. Private repo view already requires GitHub-verified access. |
| 6 | Rate limiting on upload/badge endpoints | ASP.NET `AddRateLimiter` per token/IP (Spark's built-in limiter only covers `/spark/*`); badge endpoint additionally cached. |
| 7 | `after_n_builds`-style config | Explicitly rejected; debounce+timeout+optional explicit finish instead. |

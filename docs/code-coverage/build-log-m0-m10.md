# Coverage — Development Plan

Companion to [PRD.md](PRD.md). Milestones are ordered so that every stage produces something demonstrable, upstream PRs are unblocked early, and the layering rule (generic → upstream repo) is respected. Target: **one PR per repo** per milestone group.

> **Where the work is now (2026-08-18).** M0–M10 are built and deployed; this document is the record
> of how they were sequenced. The generic-UI adoption
> ([adopt-spark-generic-ui.md](adopt-spark-generic-ui.md)) shipped in #8. Active work is
> [upload-result-contract.md](upload-result-contract.md) — the upload result contract, a status
> endpoint a CI gate can poll, and the `ParentSha` disarm — on branch `upload-status-contract`,
> answering [issue #9](https://github.com/MintPlayer/CodeCoverage/issues/9) from the first real
> consumer. It also records the resolution of roadmap §7.1 (repo config files: **yes**). Two things below read as stale: the
> **R4-H1** row-level-auth finding referenced in M0 step 0 turned out to be a fabricated identifier
> (Spark's own audit says so) and row-level security has since shipped upstream in
> `10.0.0-preview.44`–`.46`; and the preview pins named throughout are historical — the current
> target is **`10.0.0-preview.51`**. The adoption doc carries the live upstream scoreboard.

Legend: 🟦 Coverage repo · 🟩 MintPlayer.Spark PR · 🟨 mintplayer-ng-bootstrap PR · 🟪 coverage-action repo

---

## M0 — Upstream groundwork 🟩

Goal: unblock everything else; one Spark PR.

0. **Confirm the base branch** — the local checkout is on `security-audit` (one commit ahead of master); agree with the maintainer where M0 branches from, and whether the open **R4-H1** finding (row-level auth missing on `/spark/queries/{id}/execute` and `/stream`) is fixed in this PR or worked around app-side (PRD §12 1b).
1. **Verify & fix the typed-webhook queue-name bug**: boot WebhooksDemo; if `GitHubWebhookMessage<TEvent>` queue names (`FullName` of a closed generic → contains `[ ] , =`) indeed fault `MessageSubscriptionWorker.IsValidQueueName`, fix by sanitizing/hashing generic queue names (or a `[MessageQueue]`-style naming hook) + regression test.
2. **New library `MintPlayer.Spark.Authorization.ApiTokens`** (name TBD — align with maintainer taste):
   - `SparkApiToken` document: hash-as-id, scope claims, created-by, expiry, revocation; store beside `UserStore` conventions (compare-exchange not needed — hash id is unique by construction).
   - Token format `covt_`-style prefix + 256-bit urlsafe random; only SHA-256 stored; value returned once.
   - `ApiTokenAuthenticationHandler` (`Authorization: Bearer <token>` / `Token <token>`) → `ClaimsPrincipal` with scope claims; registered through the existing `configureProviders: Action<IdentityBuilder>` hook.
   - Issuance/list/revoke endpoints under `/spark/auth/tokens` (XSRF-protected, cookie-authenticated).
   - Consuming app supplies the scope vocabulary; library stays domain-agnostic.
3. **External-login popup fix**: `external-login` must propagate `popup` to the callback URL so the `postMessage` handshake fires; fix the demo's listener leak.
4. **ng-bootstrap dependency bump** in Spark: `^22.4.0` → current (22.13.x), adding new peer deps (`@mintplayer/web-components ^2`, `lit ^3.3`); fix fallout in ng-spark/ng-spark-auth and demos.
5. Opportunistic doc-drift fixes (only the cheap ones listed in PRD §10.4).

**Exit criteria**: WebhooksDemo boots clean with typed recipients receiving events; a demo app can mint and authenticate with an API token; Spark tests green.

## M1 — Scaffold the Coverage app 🟦

Copy the WebhooksDemo anatomy (the 27-item checklist from the investigation):

1. `Coverage.Library` (entities: Account, Repository, UploadToken-scope holder if app-side, Commit, Build) + `Coverage` host + `ClientApp` (shell, home page), `App_Data/Model/*.json` via `--spark-synchronize-model`, `Synchronize` launch profile (WebhooksDemo lacks it — HR is the reference).
2. Consume Spark via **published NuGets** (`10.0.0-preview.41`) and `@mintplayer/ng-spark*` from npm — Coverage is the first out-of-tree consumer; upstream any packaging bugs found. Use `MintPlayer.Spark.Testing` (embedded RavenDB; needs `RAVENDB_LICENSE`) for integration tests. Test trap to remember: hand-written `session.Query<TView, TIndex>()` needs `.ProjectInto<TView>()` or index-computed fields come back null.
3. GitHub App (dev) + OAuth login + webhooks wired (`installation`, `repository`, `push`, `pull_request` recipients that upsert Accounts/Repos/Commits); smee.io dev tunnel.
4. Org/repo visibility sync on login (port `OrganizationAccessService` pattern; consider promoting to Spark later). Fix its two known flaws when porting: use `IHttpClientFactory` instead of a bare `HttpClient` per call, and cache beyond per-request (TTL + manual resync) to stay under GitHub rate limits.
5. docker-compose + Dockerfile (WebhooksDemo template, pinned RavenDB).

**Exit criteria**: sign in with GitHub → home page lists your orgs/repos (empty coverage), webhook keeps repo list current.

## M2 — Ingestion pipeline 🟦

The heart of the product; testable without any UI.

1. Normalized model (`Line {Number, Hits?, Status}` etc. — PRD §5) + merge (max semantics, per-session).
2. `ICoverageParser` + sniffing factory (root-element/text dispatch). Parsers: **LCOV**, **Cobertura** (then JaCoCo in M2.5). *(As built: inline-fixture unit tests covering the tricky records — lcov 2.x `BRDA -`/block prefixes included; a corpus of real coverlet/nyc/coverage.py/gcovr files never materialized, tracked with M9.26.)*
3. Path normalizer: rootDir strip, slash unification, Cobertura `<source>` resolution, `fileList` suffix-matching fallback, unmatched-bucket.
4. `POST /api/uploads` (multipart: metadata + gzipped files + fileList) authenticating via API token (M0 lib); store raw files as RavenDB attachments on the Build; Build/Session bookkeeping keyed `(repoId, sha, runId, runAttempt)`; parse via Spark message-bus recipient.
5. Finalization: explicit `POST /api/uploads/finish` + debounce (~2 min) + timeout (~30 min) via cron/subscription worker; recompute `Commit.CoverageSummary`.
6. Rate limiting on `/api/*`.

**Exit criteria**: `curl` two lcov+cobertura uploads for one fake run → one finalized build with correctly merged per-file line data (verified idempotent under re-upload).

## M3 — GitHub Action MVP 🟪

1. ~~New repo `coverage-action`~~ *(as built: lives in this repo under `action/`, consumed as `MintPlayer/CodeCoverage/action@<ref>`)*: node20 + TypeScript + ncc bundle (dist/ committed + CI staleness check).
2. v1 inputs: `url`, `token`, `files`/`directory` globs (auto-detect fallback using Codecov's glob/ignore lists), `flags`, `name`, `finish`, `fail-ci-if-error`.
3. Correct metadata (PR-head SHA, branch, runId/runAttempt, `rootDir`, `git ls-files`).
4. Dogfood: run it in the Coverage repo's own CI (and optionally mintplayer-ng-bootstrap's — both already emit cobertura).

**Exit criteria**: a real workflow uploads real coverage to a deployed dev instance, multiple jobs bundling into one build.

## M4 — Browse UI 🟦

1. **Home** (accounts + aggregate %), **Account** page (repos + latest default-branch coverage), **Repository** page (branch selector, commit list with % and delta).
2. **Commit/build** page: summary header, sessions/flags with parse status, unmatched-files warning, and the **file/folder tree** ~~via `bs-datatable` tree mode~~ *(as built: plain `bs-table` + breadcrumb drill-down; the datatable upgrade is M9.28)* with coverage-% cells.
3. Custom endpoints + RavenDB static indexes for the commit list and tree aggregation (not Spark generic queries — paging happens in-memory there).
4. Private-repo pages gated on the viewer's synced GitHub access.

**Exit criteria**: click-through org → repo → commit → folder → file list matches uploaded data.

## M5 — File view + code-viewer component 🟨🟦

1. 🟨 ~~**`mp-code-viewer`** in ng-bootstrap~~ *(as delivered by ng-bootstrap#402: `mp-code-snippet` was **extended into the viewer** — no separate component or `bs-code-viewer` wrapper exists; see M10)*: line numbers, generic per-line annotation API, line anchors, theme-following, keyboard/a11y.
2. 🟦 File view page: fetch source from GitHub at view time (installation token, contents: read, ETag cache — we never store source), overlay line coverage (green/red/orange + hit counts), `#L42` deep links.

**Exit criteria**: viewing a covered file for a commit shows highlighted source identical to the report.

## M6 — Badges 🟦

1. `GET /badge/{owner}/{repo}.svg?branch=…` — self-rendered SVG (shields.io-style flat badge is ~30 lines of templated SVG), color scale red→green.
2. Private repos require `&token={BadgeToken}` (scoped, rotatable; wrong/missing → "unknown" badge, never 404).
3. Repo page shows the ready-to-paste markdown snippet *(as built: badge-token create/rotate
   sits inline in the README-badge box, shown for private repos only — public badges need no
   token; there is no separate settings tab)*.
4. `Cache-Control: max-age=300` + rate limiting.

## M7 — OIDC tokenless uploads 🟦🟪

1. 🟦 JWT bearer validation (`Authority = token.actions.githubusercontent.com`, `aud` = our base URL); claims usage as built: `run_id`/`run_attempt` override the body, `repository` is validated-equal, `sha` deliberately NOT used (merge-commit trap — the body carries the PR head).
2. 🟪 Action: `use-oidc` input (default false; auto-chosen only when NO token input is supplied and `id-token: write` is available), `core.getIDToken(url)`.
3. Policy: public repos may auto-provision on first OIDC upload; private repos must be known (App installed).

## M8 — Dependency upgrade + coverage diagram 🟦 (UNBLOCKED 2026-08-10)

The upstream halves landed: Spark#231 (→ `10.0.0-preview.42`, `ng-spark-auth 22.1.0`) and
ng-bootstrap#401 (→ `22.14.0` charts). All remaining work is in this repo.

**Step 1 — upgrade (required):**
1. Bump all `MintPlayer.Spark.*` NuGets `10.0.0-preview.41` → `10.0.0-preview.42`.
2. ClientApp: `@mintplayer/ng-spark-auth` → `^22.1.0`; **pin** `@mintplayer/ng-bootstrap` `22.14.0` + `@mintplayer/web-components` `2.11.0` (the old `^22.13.0` caret resolves to 22.14 silently — make it a deliberate commit).
3. Runtime verification: GitHub OAuth sign-in (the new composite default-authenticate scheme must not disturb the cookie path), one `covt_` upload, one OIDC-JWT upload. A "refused by every registered scheme" log warning is cosmetic (see step 2.1).

**Step 2 — upgrade follow-ups (optional, recommended):**
1. Register the **ApiToken** scheme as a Spark credential scheme (as built: the generic overload `spark.AddCredentialScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(SchemeName)` inside the AddSpark callback, non-ambient by default) — silences the per-upload warning + earns the non-ambient antiforgery exemption. Deliberately do NOT register GitHubOidc (it would widen where workflow JWTs are accepted).
2. Shell: replace the full-page-redirect login workaround with `authService.loginWithProvider('GitHub')` (ng-spark-auth 22.1.0 owns the whole popup handshake incl. blocked/closed/refused paths).
3. ~~Convert `GitHubEventsRecipient` to typed recipients~~ — **deliberately skipped**: the webhook processor broadcasts BOTH the catch-all and the typed envelope per event regardless of subscribers, so converting would only swap which family of unconsumed messages accumulates while splitting one cohesive handler into five classes. Revisit if Spark ever broadcasts only to subscribed queues (possible upstream ask).

**Step 3 — coverage diagram (the feature):**
1. Commit page: `bs-hierarchy-chart` (`layout="sunburst"`, `colorMin≈60`/`colorMax≈80`) fed from a new full-tree endpoint variant returning per-file `HierarchyNode {id: path, value: coverableLines, colorValue: coveredPct}` — folder colors derive upstream (value-weighted mean), no server rollup. `(zoom)` → `openFolder(path)`, `(nodeSelect)` → file view, `[(rootId)]` two-way-bound to the existing folder drill-down so tree and chart stay in sync (that pairing is also the documented WCAG target-size story). Bound column width (aspect-ratio 1 fills width).
2. Headline radial ring: hand-rolled `CoverageRingComponent` (~20 lines) on the public `arcPath` + `colorScale` from `@mintplayer/web-components/charts/core` (`ringGap: 0`) — upstream declined a donut/gauge component; contribute `mp-progress-circle` later only if this shape proves general.
3. Later, once history is queryable: `bs-trend-chart` (with `goal` line) on the repo page; `bs-sparkline` in tables.

## M10 — Adopt the unified code viewer 🟦 (✅ BUILT 2026-08-12, unblocked by ng-bootstrap#402)

[PR #402](https://github.com/MintPlayer/mintplayer-ng-bootstrap/pull/402) extended
`bs-code-snippet` into the full viewer (per-line DOM via subgrid rows, `annotations:
CodeLineAnnotation[]` with `{line, kind, label, secondaryLabel, description}`,
`lineNumbers`, `lineHref`, `activeLine`, `scrollToLine()`, `data-bs-theme`-aware
`light-dark()` theming, roving-tabindex a11y). No separate `mp-code-viewer` exists.
Upstream's own migration checklist for OUR file page: mintplayer-ng-bootstrap
`docs/prd/code-snippet-viewer.md` §12; working coverage-shaped demo under
`apps/ng-bootstrap-demo/.../advanced/code-snippet/`.

1. Pin `@mintplayer/ng-bootstrap` **22.15.0** + `@mintplayer/web-components` **2.12.0**; add
   **`highlight.js@^11.11.1` as a direct dependency** (declared optional peer, but the published
   module has a static `import 'highlight.js/lib/core'` — the Angular build fails to resolve
   without it). Breaking changes in 22.15 are confined to code-snippet (`codeToCopy`→`code`,
   `lineActivate` payload, theme default) — Coverage uses none of it yet, clean upgrade.
2. Replace the hand-rolled renderer in `pages/file/` with `<bs-code-snippet>`:
   map the existing `RenderedLine[]` → `CodeLineAnnotation[]` (`kind` = covered/partial/uncovered,
   `label` = hits ("0×" renders — label shows when present), `secondaryLabel` = branch ratio,
   `description` for the tooltip/SR text); `[lineHref]="(l) => '#L' + l"` (bare fragments are
   rewritten against `location.pathname + location.search`, so `?path=` survives — no routerLink
   needed); tint via `::ng-deep mp-code-snippet::part(annotation-<kind>)` (parts, not CSS vars —
   deliberate upstream deviation). Source-unavailable case: annotations may exceed `code`'s
   extent, so `code: ''` still renders a full gutter.
   **The one silent breaker**: `scrollToTarget()` uses `document.getElementById('L'+n)` which
   returns null into a shadow root — must become `viewer()?.scrollToLine(n)` (viewChild).
   Write our own extension→language map (grammar keys cover cs/ts/html/json/scss/sql/yaml/vb/md;
   razor/fsharp/xaml absent → plain text + console.warn for unmapped extensions (a mapped key
   failing `canHighlight` falls back silently); `canHighlight`/`registerLanguage`
   exported for gating/extending). Layout trap: `code { min-width: max-content }` propagates —
   flex ancestors need `min-width: 0` or phones get body-level horizontal scroll.
3. Cleanup while there: `bs-shell-topbar.directive.ts` is unnecessary — upstream confirmed a plain
   `<div slot="topbar">` works and the directive's "promote upstream" TODO points at nothing;
   delete it in shell.component. Also fix the stale comment in
   `Recipients/GitHubEventsRecipient.cs:14-19` (cites the FIXED queue-name bug; the real reason
   for keeping the catch-all is the dual-broadcast note in M8 step 2.3).

## M9 — Verified backlog (from the 2026-08-12 code-vs-docs audit) 🟦

Status legend: ✅ built 2026-08-12 (`feature/m10-m9-backlog`) · ⏳ deferred (reason inline).

### Correctness fixes (do first)
1. ✅ **Commit-ordering bug**: `FirstSeenAtUtc` stamped at document creation in BOTH webhook and
   upload paths; `Commits_ByRepository` sorts on `AuthoredAt ?? FirstSeenAtUtc`.
2. ✅ Zero delta renders blank: `@if (delta(i); as d)` — `0` is falsy → `@let` + null check.
3. ✅ Badge markdown: repo response carries `Coverage:BaseUrl`; the snippet is built from it
   (`location.origin` remains only as a defensive fallback when the server sends none).
4. ✅ Stale "designed for extraction" comment in `ApiToken.cs` replaced with the cancelled-upstream note.

### Missing product features (PRD promises, verified unbuilt)
5. ✅ **Upload-token management UI** — account-page card (create/list/revoke, plaintext shown
   once); `TokensController.Create` now accepts `Scope=Repository` + `repositoryFullName`
   (validated as owned by the account), making the repo scope reachable.
6. ✅ Branch selector on the repo page (new `/branches` endpoint, default branch first).
7. ✅ Home-page aggregates (repo count + value-weighted aggregate coverage per account).
8. ✅ Manual "resync" of GitHub visibility (`POST /api/me/accounts/resync` + home-page button).
9. ✅ Coverage-over-time: `/history` endpoint + `bs-trend-chart` (80% `goal` line) on the repo
   page; `/accounts/{login}/sparklines` + `bs-sparkline` column in the account table.
10. **JaCoCo parser** ✅ (validates nullable-Hits: executed lines get `Hits=null`, unexecuted 0).
    ⏳ Istanbul JSON, Clover, OpenCover, Go; opt-in ReportGenerator.Core fallback adapter.
11. ⏳ PR comments + commit statuses/checks — needs checks:write + PR:write added to the GitHub
    App first (a permission/product decision, not just code).
12. ⏳ Patch/diff coverage (inputs exist: `ParentSha` stored; the action's wire field is
    `parentSha`) — needs the GitHub compare API + a diff-mapping design of its own.
13. ⏳ Fork-PR quarantine flow (today forks simply can't upload) — policy design needed.

### Ops / deployment
14. ✅ **Publish + deploy workflow** (`publish.yml`: test-gated ghcr push with OCI source
    label + best-effort visibility PATCH, then SSH deploy to the VPS — compose refetched
    from master, server-managed `.env`/pem never touched; modeled on ng-bootstrap's
    pipeline with Spark WebhooksDemo's refinements). VPS/DNS prerequisites: README
    "Deployment". ⏳ RavenDB volume backup remains out-of-band.
15. ✅ Traefik port pinned (`…server.port=8080` label; EXPOSE 8081 dropped) +
    `traefik.docker.network=web` (two-network container — without it Traefik can route to
    the unreachable internal IP).
16. ✅ Compose healthchecks (bash `/dev/tcp` probes; `depends_on: service_healthy`).
17. ✅ `.env.example` documents the `./github-app.pem` bind-mount.
18. ⏳ CI dogfood OIDC leg — needs a deployed instance reachable from CI first.
19. ✅ Action README (OIDC-first usage, token usage, badge snippet, `@master`/`@v1` pinning story);
    unused `check-dist` script removed. ⏳ The actual `v1` tag: cut from master once the input
    surface settles.

### Performance / scale / hardening
20. ✅ Tree + hierarchy endpoints read a `BuildTreeSummary` (`{buildId}/tree`) materialized at
    finalize; pre-existing builds fall back to streaming FileCoverage.
21. ⏳ File-view virtualization (upstream viewer renders plain too; 2000 rows measured fine —
    watch item for giant generated files).
22. ⏳ Live refresh of in-flight builds (no polling/SSE; Pending sessions need manual reload).
23. ⏳ OIDC auto-provisioning quota/retention (rate limiter bounds rate, not cumulative storage).
24. ✅ Badges moved to their own per-IP "badges" rate-limit policy (camo-proxy safe).
25. ✅ Per-branch badges (`?branch=` renders the newest covered commit of that branch).

### Testing
26. ⏳ Integration tests via `MintPlayer.Spark.Testing` (embedded RavenDB; needs `RAVENDB_LICENSE`
    provisioning in CI) — upload endpoint, auth handlers, finalization FIFO, browse API. Current
    suite is pure-unit (parsers/merger/normalizer/smee-minifier), all inline fixtures.
27. ✅ Dead `"test": "ng test"` script dropped (no runner installed).

### UI upgrades (components exist upstream, adoption optional)
28. ⏳ Folder list → `bs-datatable` tree mode (expandable rows + sortable coverage columns + lazy
    child fetch; https://bootstrap.mintplayer.com/enterprise/datatables) replacing the plain
    `bs-table` + breadcrumb drill-down — pairs naturally with the `[(rootId)]`-synced sunburst.

### Added by the 2026-08-12 doc-vs-code re-audit
29. ⏳ Admin-role gating: token/badge management currently requires only installation
    *visibility* (any org member who can reach the installation can mint/revoke tokens);
    gate on `GET /user/memberships/orgs/{org}` role=admin — PRD §6.3.
30. ⏳ Reprocess-after-parser-fix endpoint/job replaying the retained raw attachments
    (PRD §5 keeps them for exactly this; no trigger exists yet).
31. ✅ Cross-format branch-merge guard (`FileCoverage.BranchFormat`): branch detail merges
    within one report format only; a session in another format contributes line status only
    (PRD §5's rule, previously unimplemented). ✅ Uploads rate-limiter partitions on the
    presented `covt_` token hash again (the limiter runs before authentication, so the old
    claims-based key silently degraded to per-IP). ✅ Badge `Cache-Control` no longer keyed
    on repo existence (was an existence oracle).

---

## Status (2026-08-12)

| Milestone | State |
|---|---|
| M0 Spark groundwork | ✅ Resolved upstream by [Spark#231](https://github.com/MintPlayer/MintPlayer.Spark/pull/231) (ApiTokens lib cancelled → app keeps `covt_`; see PRD §10) |
| M1 Scaffold · M2 Ingestion · M3 Action · M4 Browse UI · M6 Badges · M7 OIDC | ✅ Built, verified E2E, on `develop` |
| M5 File view | ✅ Built; renderer swapped to the upstream viewer in M10 |
| M8 Upgrade + diagram | ✅ Built (preview.42 + 22.14 upgrade, ApiToken credential scheme, popup login, sunburst + ring on commit page) |
| M10 Code-viewer adoption | ✅ Built (`feature/m10-m9-backlog`): 22.15.0/2.12.0 + highlight.js pinned, file page on `bs-code-snippet` (annotations/parts/`scrollToLine`), topbar directive deleted |
| M9 Verified backlog | ✅ 17 of 28 built (`feature/m10-m9-backlog`); the rest deferred with reasons inline (list below) |

**Nothing remains upstream.** All three repos delivered (Spark#231, ng-bootstrap#401 + #402);
the only open upstream nit is cosmetic Sass `@import` deprecation noise. Two audit corrections to
older claims: upstream found `bsShellTopbar` needs no promotion (`<div slot="topbar">` works
directly — our directive is deleted since M10.3) and the `bs-progress-bar` host-class clobbering
was measured NOT real.

**As-built deviations from this plan:** the file browser is a plain `bs-table` with breadcrumb
drill-down — `bs-datatable` tree mode exists upstream (see
https://bootstrap.mintplayer.com/enterprise/datatables, "Tree mode — expandable rows") and
adopting it is M9.28; only the commit list has a static index (tree/hierarchy read the
materialized `BuildTreeSummary` since M9.20); typed webhook recipients were deliberately
skipped (M8 step 2.3 note).

**Static indexes (2026-08-19): `Build`, `Repository` and `Account` are `[GenerateIndex]`-generated**;
their grids and every hand-written lookup run through `*_Overview` indexes instead of auto-indexes.
`Commit` stays hand-written (`Commits_ByRepository` — the coalesced sort field is not expressible). See
[adopt-generated-indexes.md](adopt-generated-indexes.md) for the as-built record and the upstream defects
it surfaced.

**Spark `preview.56` (2026-08-20):** all four defects this repo filed have shipped and been adopted —
`showedOn` preservation (#274, `.54`), index coexistence + complex-field indexing + breadcrumb rework +
synchronizer preservation (#272/#273/#275/#276, `.55`), and query-declared index bindings with
`IIndexRegistry` deleted (#279, `.56`). The local complex-field workaround partial is gone (the generator
emits it now) and the one-index-per-entity ceiling no longer exists. `Commit` coexistence is therefore
unblocked but deliberately not taken — the chosen route remains one index, via persisting the two computed
fields. See [adopt-spark-preview-57.md](adopt-spark-preview-57.md) (§5 for that next deliverable) and
[spark-issue-279-PRD.md](spark-issue-279-PRD.md).

## Sequencing notes

- M0 (Spark PR) and M3 (action) can proceed in parallel with M1/M2 once the token library's *interface* is agreed — the Coverage app can stub token auth briefly.
- M5.1 and M8.1 (ng-bootstrap PRs) are independent of the Coverage backend; they can start any time after M0's dependency bump, but sequence them M5 before M8 (code viewer is core UX; the diagram is delight).
- Test policy per global instructions: verify milestones by build/type-check + targeted fixture tests during development; full suites batched at the end of each milestone before its PR.

## PR map (one per repo)

| Repo | PR contents | Status |
|---|---|---|
| MintPlayer.Spark | M0 (queue-name fix, popup fix, ng-bootstrap bump, R4-H1, doc fixes; ApiTokens→client_credentials) | ✅ [#231](https://github.com/MintPlayer/MintPlayer.Spark/pull/231) |
| mintplayer-ng-bootstrap | Charts (hierarchy/trend/sparkline + public charts/core) | ✅ [#401](https://github.com/MintPlayer/mintplayer-ng-bootstrap/pull/401) |
| mintplayer-ng-bootstrap | Unified code-snippet viewer (M5's component half) | ✅ [#402](https://github.com/MintPlayer/mintplayer-ng-bootstrap/pull/402) (22.15.0/2.12.0) |
| Coverage | Everything (M1–M8 history + M10 + M9 17/28 + auth/webhook fixes + deploy pipeline) in [PR #2](https://github.com/MintPlayer/CodeCoverage/pull/2) → `master` | 🔄 |
| coverage-action | Lives in this repo under `action/` (extract only for Marketplace) | ✅ |

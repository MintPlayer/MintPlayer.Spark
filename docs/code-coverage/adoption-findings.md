# Adoption findings — flag filter, unmatched cap, readiness, FeedbackState, grid columns

**Status: ✅ BUILT 2026-08-19 (M1–M7) · branch `adoption-findings` · one squash-merged PR for all of
[#13](https://github.com/MintPlayer/CodeCoverage/issues/13), by the issue's own request.**

As-built notes, all conscious:

- **SP6 went decisive against the optimistic reading**: synchronize re-derives `showedOn` on
  *every* run for `[FromIndex]` entities (measured — hand-edits reverted by a re-run). M6 ships
  the curated JSON + `ModelColumnGuardTests` as the loud guard; upstream filed as
  [Spark#274](https://github.com/MintPlayer/MintPlayer.Spark/issues/274).
- **M6 needed no `order` renumbering** — `45354c0` never changed `order`, so restoring `showedOn`
  alone reproduces the curated grids.
- **SP5's residual browser check** (chip click issues only `tree?flag=pr`, stays selected,
  `aria-pressed` flips) is left for review against a running host; the static half is confirmed
  and `tsc --noEmit` + the suite (141/141) are green.

Issue #13 collects five findings from MintPlayer/mintplayer-ng-bootstrap adopting this service
(their PR #405). This plan is grounded in a four-agent investigation run 2026-08-19 against the
working tree at `45354c0`. Ordering follows the issue's priority (U4 and U3 first), and every
milestone leaves the branch green and committable; nothing merges until all of it does.

One investigation result changes the shape of an item materially and is worth reading first:

> **U5 is not a column-order cosmetic — it is a synchronize regression introduced by `45354c0`.**
> Adding `[GenerateIndex]` to `Repository` (and `Build`) made `--spark-synchronize-model` re-derive
> `showedOn` from index-projection membership, silently wiping the curated per-surface trims that
> `049fe9b`'s M6 had hand-set (`docs/adopt-generated-indexes.md:20-23` documents the mechanism;
> `docs/adopt-spark-generic-ui.md:608-614` relied on a precondition — "neither Repository nor
> Account has a `[FromIndex]` projection" — that `45354c0` invalidated). The column *order*
> survived; the *hiding* did not. Bonus severity: the generic grid puts the row link on the
> **first** column only, so the account page's only clickable link now sits on the constant
> `Account` cell. CI's `--spark-verify-model` deliberately does not hash `showedOn`, so the wipe
> was invisible to the gate.

## 0. Decisions

Made autonomously (owner not in the loop mid-task); each is the conservative reading of the
issue's ask, recorded here so review can overturn any of them cheaply.

| # | Decision |
|---|----------|
| D1 | **U4 core fix is the full-body `untracked`, not the one-liner.** The reset effect's body (reset + reload) is extracted to a private method and run via `untracked()`, with `owner/name/sha` read first as the only tracked dependencies. Wrapping only line 101's `selectedFlag()` read would fix the symptom but leave the effect writing signals it also reads (`currentPath`, `chartRootId`, `tree` at lines 97–99). |
| D2 | **Two request tokens, not one.** `openFolder`'s tree fetch and the effect's hierarchy/commit fetches get separate monotonic counters — a breadcrumb click legitimately supersedes a tree fetch without invalidating an in-flight hierarchy. Stale responses (success *and* catch paths) are dropped. |
| D3 | **The flag becomes a `?flag=` query param** on the `/po/commit/:id` route, read via `ActivatedRoute` in the panel (which already injects `Router`), written with `{ queryParamsHandling: 'merge', replaceUrl: true }`. The URL subscription stays **separate** from the owner/name/sha effect (reading it there would re-create U4 in a new costume); the reset sets the flag *from the URL* rather than unconditionally to `null`. The vanity redirect guards (`vanity-redirects.ts`) currently **drop query params** — fixed in the same milestone so `/r/{o}/{n}/c/{sha}?flag=pr` survives the redirect. |
| D4 | **U3: disclose, don't raise.** Keep the 50-entry sample cap (named constant), add the true count as a defaulted trailing `int UnmatchedTotal` on `TreeResponse`, and label the alert "showing 50 of 314" only when actually truncated. `/api/browse` is explicitly not a public contract (`docs/upload-result-contract.md:70`), so no compatibility ceremony. |
| D5 | **U2: `feedbackState` as a defaulted trailing `string?`** on `UploadStatusResponse` — the endpoint's established growth pattern. Documented in `docs/upload-api.md` as **informational, do not gate on it**, with the publish-after-finalize race called out (a poller can see `state: completed` with `feedbackState: null`/`Pending` briefly) and `Failed` documented as terminal ("a new build is required" — the cron never re-sweeps it). `BuildFeedback.Error` is **not** exposed: it carries raw Octokit/server configuration detail (the U1 incident message itself). A `feedback-state` action output is deferred — it requires rebuilding `action/dist` and the issue asks only for the API field. |
| D6 | **U5: restore the curated trims globally and accept the trade.** Order + `showedOn` edits in `Repository.json` (Repository grid → `Repository | Coverage | Trend | Latest commit`) and `Build.json` (same wipe, same commit — this is also what put `Feedback State` on the commit page). The only surface that genuinely wants `Account` to vary, `/query/GetRepositories`, is unlinked from every page in the SPA, and Spark has no per-query column model (verified against preview.53 / ng-spark 22.0.11), so per-view columns are an upstream feature, not something to fake here. A model regression test pins the curated Query column set/order so the *next* synchronize fails loudly instead of silently. Upstream ask (Spark: `showedOn` hand-edits must survive synchronize on `[FromIndex]` entities; per-query columns) recorded in §4, filed separately per the repo's layering rule. |
| D7 | **U1: readiness probes the App JWT against GitHub, tri-state, cached.** `GET /health/ready`: **NotConfigured** (no `PrivateKeyPath`/`AppId` — the normal dev state) → 200 with the check reported `skipped`; **Ready** (JWT round-trip `GET /app` succeeds) → 200; **Unusable** (decisive 401 `AuthorizationException`, or key file missing/unreadable/a directory) → 503. Transient GitHub 5xx/timeouts report `degraded` but stay 200 — a GitHub outage must not take the container down. Result cached ~5 min so a 15 s probe cadence doesn't burn GitHub calls (App-JWT budget is 5 000/h, so even uncached would be fine — the cache is politeness). The compose healthcheck keeps probing `/health` (liveness); flipping it to `/health/ready` would turn a bad key into a restart loop. Instead the **deploy workflow polls `/health/ready` after `up -d`** and fails the job — which is exactly where the U1 incident should have surfaced. RavenDB/message-bus/cron probes from T0.4 stay on the roadmap; this ships the incident-class probe. |
| D8 | **Ops docs land with U1**: README deployment section gains the PEM fingerprint verification (`openssl rsa -in github-app.pem -pubout -outform DER \| openssl sha256 -binary \| openssl base64`), the single-file bind-mount inode rule (`cat new.pem > github-app.pem`, never `mv`/`scp` over it), and "retries do not resume — after fixing the key a new build is required". |
| D9 | **Testing**: server changes get xunit facts in the existing suites' style; the model regression guard is a new test reading `App_Data/Model/*.json`. There is **zero** frontend test infrastructure (no spec files, no test target, no karma/vitest) — standing one up is out of scope for these fixes; U4 is verified by TypeScript compilation plus the browser decision rule in SP5. Test suites run **once**, after all milestones (house rule). |

## 1. Spikes

### SP5 — does `untracked` alone stop the snap-back? (from the issue)

**Question:** is the snap-back caused solely by `selectedFlag()` being read inside the constructor
effect at `commit-files-panel.component.ts:101`?

**Static resolution (done, 2026-08-19):** yes, confirmed by code reading. `await this.openFolder('')`
at line 82 enters `openFolder` synchronously; the entire argument list of line 101 — including
`this.selectedFlag()` — evaluates before the first `await` suspends, inside the effect's reactive
consumer context. `selectFlag` writes the signal → effect re-runs → line 80 resets it to `null` and
line 82 refetches unflagged. The chip flash is the template's `@if (flagEntries().length > 0)` gate
going false while line 81 blanks `flagTotals`. Every observation in the issue's network table is
accounted for; no Spark host re-emission is involved (the panel's inputs come from a template
binding in `commit-files-extras.component.ts`, which only re-resolves when the PO changes).

**Residual browser check (at review):** run the host, click the chip, confirm only `tree?flag=pr`
is issued and the chip stays selected; measure `aria-pressed` while there.

### SP6 — do the U5 model edits survive synchronize?

**Question:** `order` edits provably survive (`45354c0` changed none), but the doc that claimed
`showedOn` survives was wrong for `[FromIndex]` entities. Does *anything* downstream keep the
curated trim alive?

**Method:** after editing `Repository.json`/`Build.json`, run `--spark-synchronize-model` (it
returns before `builder.Build()`, so it needs no database) and `git diff` the model files.

**Decision rule:** if synchronize re-wipes `showedOn`, the milestone still ships (the running app
reads the committed JSON), but the regression test becomes the *only* guard and the upstream issue
becomes urgent rather than nice-to-have. Either way the result is recorded in §4.

**Resolved (2026-08-19, measured at M6): hand-edits do NOT survive.** Two runs settle it:

1. A bare `--spark-synchronize-model` on the clean tree produced zero diff — misleadingly, because
   the committed values already *matched* the derived ones.
2. After hand-editing the curated `showedOn` values back, a re-run **reverted every one of them**.

So synchronize re-derives `showedOn` from index-projection membership on **every** run for
`[FromIndex]` entities, exactly as `docs/adopt-generated-indexes.md` recorded. The owner confirms
this re-derivation is not supposed to happen at all — upstream ask §4.1 is a real Spark defect, not
a nice-to-have. Spark preview.53 has no attribute-level lever (`[IgnoreForIndex]` is the only knob
and is unusable here). Consequences for M6: the curated JSON is committed and works at runtime
(`ModelLoader` reads the file; `--spark-verify-model` passes — confirmed — because it doesn't hash
`showedOn`), and `ModelColumnGuardTests` is the **only** thing standing between the next
synchronize run and a silent re-wipe. Until the Spark fix ships, anyone running synchronize must
re-apply the M6 `showedOn` edits when the guard trips.

## 2. Milestones

Commit after each. Highest user-visible impact first, per the issue.

### M1 — U4 core: `untracked` reset effect + stale-response tokens 🐞

`Coverage/ClientApp/src/app/components/commit-files-panel/commit-files-panel.component.ts` only.

- Import `untracked` from `@angular/core` (line 1 — not currently imported; available in v22).
- Constructor effect: read `owner/name/sha` (lines 72–74) as the only tracked dependencies, then
  run the reset+reload body (lines 76–92) through `untracked()` via an extracted private async
  method.
- Two monotonic counters: one for `openFolder`'s `tree.set` (success line 101 *and* catch line
  103 — a stale failure must not blank a fresh tree), one for the effect's `hierarchy.set` (84/86)
  and `flagTotals.set` (89/91). Five triggers can re-enter `openFolder` (effect, `selectFlag`,
  `onChartZoom`, breadcrumb clicks, folder-row clicks); every one increments the tree counter.
- Fix the stale doc comment (lines 14–18): the vanity commit page is a redirect since
  `app.routes.ts:19-20`; the generic `/po` page is the only host.

### M2 — U4: flag in the URL 🐞

- Panel injects `ActivatedRoute` (inherited from the `po/:type/:id` routed parent —
  `PoDetailPageComponent`), subscribes to `queryParamMap` **outside** the reset effect (idiom:
  `pages/file/file.component.ts:151-177`, including its "did anything actually change?" early-out),
  and `selectFlag` writes `router.navigate([], { relativeTo, queryParams: { flag }, queryParamsHandling: 'merge', replaceUrl: true })`.
- The reset-on-sha-change initializes the flag from the current URL instead of hard `null`.
- `Coverage/ClientApp/src/app/spark/vanity-redirects.ts:18,32` — both guards' `createUrlTree`
  gain `{ queryParams: route.queryParams }` so shared links keep their flag through the redirect.

### M3 — U4: chips accessible ♿

`commit-files-panel.component.html:4-20`:

- `[attr.aria-pressed]` mirroring the exact `btn-primary` expressions (`!selectedFlag()` on All,
  `selectedFlag() === entry.flag` on each chip).
- Wrapper `role="group"` + `aria-label="Filter by flag"`; per-chip `attr.aria-label`
  ("{{flag}} coverage {{rate}}") so AT doesn't read "pr 76.2%" as one blob.

### M4 — U3: disclose the unmatched-files cap 🐞

- `Coverage/Controllers/BrowseController.cs`: named constant for the cap; `TreeResponse` (line 40)
  gains defaulted trailing `int UnmatchedTotal = 0`; `GetTree` (lines 301–305) computes
  `files.Count(f => !f.Matched)` (free — `files` is fully in memory) and passes it.
- `browse.service.ts:81-85`: `unmatchedTotal: number` on the interface;
  `commit-files-panel.component.ts:103` catch-literal gains the field.
- `commit-files-panel.component.html:57-63`: count from `unmatchedTotal`; when
  `unmatchedFiles.length < unmatchedTotal`, append "(showing {{length}} of {{total}})".
- Known, disclosed limitation kept as-is: the warning only renders at the root folder
  (`string.IsNullOrEmpty(path)` guard) — the total now makes the root warning honest, which is the
  issue's ask.
- Test: first-ever `GetTree` fact in `Coverage.Tests/Controllers/BrowseControllerTests.cs` —
  seed >50 unmatched files, assert 50 returned and `UnmatchedTotal` exact.

### M5 — U2: `feedbackState` on `GET /api/uploads/status` 🔎

- `Coverage/Controllers/UploadsController.cs`: `UploadStatusResponse` (lines 303–318) gains
  `string? FeedbackState = null`, passed as `build.FeedbackState` (line 235). Plain string
  (`Pending | Posted | Retry | Failed | Unavailable`, nullable before the first publish attempt) —
  matches every other status vocabulary on the endpoint. `Error` is not exposed (D5).
- `docs/upload-api.md`: field in the example body; a row in "the informational fields" table;
  the race note and the `Failed`-is-terminal note (D5).
- `docs/upload-result-contract.md` §3.2: field added to the restated body.
- Tests in `UploadsControllerStatusTests.cs` style: `Posted` round-trips; unset stays `null`.

### M6 — U5: restore the curated grids + pin them 💄

- `Coverage/App_Data/Model/Repository.json`: `Account`, `OwnerLogin`, `IsPrivate`, `DefaultBranch`,
  `Archived`, `LatestCoverageAtUtc`, `GitHubId` → `"showedOn": "PersistentObject"`, `FullName` →
  `"Query"` — byte-for-byte the `049fe9b` curated values. *(As built: no `order` renumbering —
  the relative order already puts `Name` first once `Account` leaves the grid, and `45354c0`
  never touched `order`, so restoring `showedOn` alone reproduces master's grid.)* Row link lands
  back on the repository name.
- `Coverage/App_Data/Model/Build.json`: same restore to M6-of-`049fe9b`'s curated set
  (`Run | Status | Sessions | Coverage | Created`), demoting `FeedbackState`, `GateSnapshot`,
  `FinalizeReason`, etc. to detail-only.
- Run SP6 (synchronize + diff), commit `modelHashes.json` if it moves.
- New test (e.g. `Coverage.Tests/Model/ModelColumnGuardTests.cs`) reading the model JSON and
  asserting the exact visible Query column name/order list for `Repository` and `Build` — the
  loud-failure guard CI currently lacks (`ModelFileShape` doesn't hash `showedOn` by design).

### M7 — U1: readiness that can fail + ops docs 🔑

- New probe service (house style: `[Register]`-generated partial class behind an interface so
  tests can script it) using `IGitHubInstallationService.CreateAppClientAsync()` +
  `GitHubApps.GetCurrent()` — the App-JWT-only path nothing in production code currently exercises,
  which is exactly why the bad key hid for a week. Tri-state per D7, result cached ~5 min.
- `Program.cs`: `MapGet("/health/ready", …)` beside the existing `/health` (line 272), returning
  the tri-state as JSON, 503 only on **Unusable**.
- `.github/workflows/publish.yml`: after `up -d`, poll `/health/ready` (bounded retries within
  the compose `start_period` budget) and fail the deploy on 503 — the existence-only
  `test -f github-app.pem` gate stays.
- README `## Deployment`: fingerprint check, inode rule, retries-don't-resume (D8); one-line
  pointer in `.env.example`'s pem block.
- Tests for the tri-state mapping (NotConfigured / Ready / decisive-401 / transient-5xx) against
  a scripted probe.

### M8 — sweep: full test suite + TS check, PRD as-built notes

`dotnet test` once for everything (house rule), `tsc --noEmit` for the ClientApp (no `ng build` —
the host owns the dev server), update this document's status header, then PR.

## 3. Out of scope, recorded

- Frontend test harness (no spec infra exists; a first spec means standing up the whole harness).
- `feedback-state` as a GitHub Action output (needs `action/dist` rebuild; API field ships now).
- T0.4's remaining probes (RavenDB, message-bus backlog, cron age) and the two fail-startup
  landmines (`AppId` parse, `Coverage:BaseUrl`) — still roadmap items; M7 ships the incident-class
  probe only.
- Raising the 50-entry unmatched sample (disclosure chosen; revisit if a consumer needs the list).
- Per-request `BuildFeedback.Error` diagnostics (server config detail; `Attempts`/`NextAttemptAtUtc`
  are the safe candidates if ever needed).

## 4. Upstream asks (Spark)

1. **✅ SHIPPED in `10.0.0-preview.54` (Spark PR #277) — [MintPlayer.Spark#274](https://github.com/MintPlayer/MintPlayer.Spark/issues/274)** —
   synchronize now *narrows but never widens* a hand-trimmed `showedOn`, so the curated trims survive.
   Adopted here on 2026-08-20 (`docs/adopt-spark-preview-57.md`); `ModelColumnGuardTests` is kept as a
   regression pin rather than deleted. The three sibling asks that came out of the same audit also
   shipped: **#272 + #273 + #275 + #276 in `preview.55`** (index coexistence, complex-field indexing and
   the breadcrumb rework, synchronizer preservation of authored `query`/`source`), and
   **[#279](https://github.com/MintPlayer/MintPlayer.Spark/issues/279) in `preview.56`** (query-declared
   index bindings; `IIndexRegistry` deleted — see `docs/spark-issue-279-PRD.md`). Original text:
   `showedOn` hand-edits must survive `--spark-synchronize-model` on `[FromIndex]`-projected
   entities. Index membership is a storage fact; `showedOn` is presentation. Same defect class as
   Spark#253 ("Synchronize must not delete attributes"). `[IgnoreForIndex]` is not a lever here —
   `OwnerLogin`/`IsPrivate`/`Account` are load-bearing in `RepositoryVisibility.Filter` and
   `Account_Repositories`. SP6 measured the wipe as **every-run**, not one-time — see §1; until
   this is fixed upstream, the downstream model JSON can only be defended by a test.
2. **Filed 2026-08-20 as [MintPlayer.Spark#284](https://github.com/MintPlayer/MintPlayer.Spark/issues/284)
   — per-query column selection/ordering** — `SparkQuery.Columns` server-side or a
   `columns`/`hiddenColumns` input on `spark-sub-query`/`spark-query-list` — so a global
   repositories view can keep `Account` while account-scoped views drop it. Until then D6's
   global trim stands. The issue carries the Vidyano prior art (shared attribute pool + per-query
   `Columns` with `Offset`/`IsHidden`/`Width`) and notes that #279 sharpened it: a query bound to a
   non-default index gets that projection's rows but still the entity's column set.
3. Optional: the generic grid's row link could span the row (or a designated column) instead of
   hard-coding "first visible column" — that coupling is what turned a column reorder into a
   navigation regression.

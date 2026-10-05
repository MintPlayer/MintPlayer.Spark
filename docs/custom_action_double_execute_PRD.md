# #319: a custom action on a detail page fires the sub-query's `/execute` twice

Issue: https://github.com/MintPlayer/MintPlayer.Spark/issues/319 · Branch: `fix/319-custom-action-double-execute`

## 1. Problem

On a persistent-object detail page, clicking a custom action that has `refreshOnCompleted: true`
sends **two identical `POST /spark/queries/execute` requests** for every sub-query grid on the page. This
happens when the action's server code also emits a `refreshQuery` client operation, through
`manager.Client.RefreshQuery(...)`. The single click should produce one request.

## 2. What the investigation established (evidence)

Two people investigated, separately: one read the code, the other reproduced the bug in a browser
against QnA on 2026-10-05.

| # | Claim in the issue | Verdict | Evidence |
|---|---|---|---|
| C1 | `onCustomAction` bumps the refresh tokens on both sides of an `await`, so the grid re-fetches twice | **Confirmed** | `spark-po-detail.component.ts:518-529`. The browser repro measured **2, 2, 2, 2** `/execute` per click (refresh by alias) and **2, 2, 2** (refresh by id). Instrumenting `reload()` traced both calls to the grid's token effect: #1 fires during `await this.loadItem()`, #2 fires after the loop. |
| C2 | Moving the loop before the `await` makes it one request | **Confirmed** | With the loop moved, the same repro measured **1, 1, 1** for both variants. |
| C3 | It is intermittent, because the refresh service matches on the exact string (id vs alias), so a server refresh sent by id never reaches a grid that holds the alias | **Wrong** | `tokenFor()` reads the whole `tokens()` record, so **every** grid's effect re-runs on **any** key's bump, and the result is never compared (`spark-query-grid.component.ts:697-703`). The repro's token map read `{"question-answers":3,"4e13a000-…0001":1}` and the id variant still made 2 requests. The double happens on every click. |
| C4 | Reproduce it with Fleet `CarCopy` | **Not possible** | Fleet `Car.json` has `"queries": []`, so its detail page has no grid. `CarCopyAction` emits no operations. Measured: 0 query requests per click. |
| C5 | Endpoint `/spark/queries/{id}/execute` | **Outdated** | It is now `POST /spark/queries/execute` with `queryId` in the body. |

Side findings that are part of this fix (not deferred):

- **F1. Any refresh reloads every grid.** Because of C3, a `refreshQuery` for query A also
  re-fetches unrelated grid B on the same page (or anywhere in the app shell). Wasted requests.
- **F2. Matching is case-sensitive while the server is not.** `QueryLoader.ResolveQuery` parses a Guid
  (any case) or looks the alias up in a `StringComparer.OrdinalIgnoreCase` dictionary
  (`SparkQueryAliasIndex.cs:31`). A grid holding `question-answers` therefore must also answer
  `Question-Answers` and an upper-case Guid. Today that works only by accident (F1).
- **F3. No app hits the bug today.** Only CodeCoverage's `Resync` and `RevokeToken` call
  `RefreshQuery`, and both have `refreshOnCompleted: false`. The bug is latent, but the combination is
  legitimate and documented, so it must work.

Mechanics (spike S2): every app uses `provideZonelessChangeDetection()`. A signal write schedules
`tick()` through `scheduleCallbackWithRafRace`, a macrotask (setTimeout/rAF). Bumps made in the same
synchronous run, or across awaits that resolve as **microtasks** within one HTTP-response task,
therefore coalesce into one effect run. An `await` on a real network round trip (`loadItem()`)
lets the flush happen in between.

## 3. Goals

- G1. One click → **one** `/execute` per affected grid, whether the server emits `refreshQuery` (by
  id or alias, any case) or not.
- G2. A grid re-fetches only for refreshes addressed to **it**, by its input key, its query's id, or its
  query's alias, matched case-insensitively like the server matches them.
- G3. No change to the wire format or the server's public API. `RefreshQuery(string)` keeps
  taking "the query's id or alias", and the docs are corrected to say so.

## 4. Decisions

| # | Decision | Why |
|---|---|---|
| D1 | Move the sub-query refresh loop in `onCustomAction` **before** `await this.loadItem()` | It is the root cause of C1 and measured (C2). The grids do not depend on `item()`, so refreshing them first is equivalent. |
| D2 | The grid answers to **its own keys only**: `queryId` input ∪ `query().id` ∪ `query().alias`. It reloads only when one of those keys' counters **increases** past the value it last saw | Fixes F1. A "last seen per key" baseline is needed because the service is a root singleton whose counters survive navigation. A grid that mounts after earlier bumps, or that learns its query's id once `getQuery` resolves, must not reload for history. |
| D3 | Keys are compared **case-insensitively**: the service lower-cases on both `request` and `tokenFor` | Fixes F2, mirroring `ResolveQuery` (Guid parse + `OrdinalIgnoreCase` aliases). |
| D4 | Matching lives in the **grid**, not the service | Only the grid holds both forms of the key synchronously once its query has resolved. Canonicalising in the service would need an async alias→id lookup. Canonicalising on the server (rewriting `QueryId` to the Guid) would change what apps already send, and still leave a grid that holds an alias unmatched. |
| D5 | ~~Keep the `fetchFn` identity swap~~ **Superseded (owner, 2026-10-05: "matter of modernizing"):** `reload()` goes through `BsDatatableComponent.reload()`, with the swap kept only as the fallback while the datatable is not rendered. A filter change passes `{ resetPage: true }`, because the `settings` binding reaches the element only after the reload already fetched | The swap disarmed the datatable's reload dedupe (mintplayer-ng-bootstrap#407, closed; in 22.21.1). This is not what fixes #319: `reload()` coalesces only within one microtask, and C1's two bumps are split by a network round trip (S3). The fetch closure reads search, filters and deleted at call time, so reusing it is safe. |
| D6 | The 449-retry path is accepted as **two** fetches | Operations from the 449 response are dispatched before the prompt (`spark.service.ts:500-501`). The first refresh happens before the user answers, and the second after the action really ran. Those are two different states, so it is not a duplicate. |
| D7 | Tests: vitest for the client mechanics, **SparkTestDriver** for the server contract the client relies on, and an **E2E** browser test for the end-to-end count | The bug is client-side and is only visible end to end. The server half (operation passes the key verbatim; a query resolves by id and alias in any case and reports both) is what makes D2/D3 correct, so it is pinned too. |
| D8 | The E2E test injects the `refreshQuery` operation by **intercepting the real custom-action response** in Playwright, rather than changing QnA's `CloseQuestion` | No app ships the combination (F3). Adding a redundant `RefreshQuery` to a demo app only to create the bug would be app churn. The interception exercises the real dispatcher, service, grid and datatable. |

## 5. Spikes

| Spike | Question | Result |
|---|---|---|
| S1 | Does the double reproduce, and does D1 alone fix it? | Yes / yes (§2, C1/C2), QnA Question detail page, `CloseQuestion`/`ReopenQuestion` with a temporary `RefreshQuery`. Reverted. |
| S2 | When does Angular flush effects in these apps? | Zoneless; `notify` → `scheduleCallbackWithRafRace` (macrotask), `@angular/core/fesm2022/_pending_tasks-chunk.mjs:2412`. Bumps before the first real network await coalesce. |
| S3 | Would ng-bootstrap's new `reload()` (mintplayer-ng-bootstrap#407, closed 2026-10-03, in 22.21.1) have absorbed the double? | No. `scheduleFetchReload` coalesces within one microtask; C1's two bumps are split by a network round trip. |
| S4 | Can Playwright add an operation to the real `/spark/actions/execute` response, to drive the E2E test without app changes? | Yes: `route.FetchAsync` → patch JSON → `FulfillAsync` (M4). |

## 6. Plan (red → green)

- **M1 (red).** Vitest specs that fail on master:
  - po-detail: every `queryRefresh.request` happens **before** `service.get` (`invocationCallOrder`).
  - grid: a bump of an **unrelated** key keeps `fetchFn()` the same instance.
  - grid: a bump by the query's **id** while `queryId` holds the alias reloads, and so does the
    reverse and a different-case key.
  - grid: two bumps of its own key in one run → exactly one new `fetchFn`.
  - grid: a grid mounting after earlier bumps does not reload for them.
  - service: `request('X')` is seen by `tokenFor('x')`.
- **M2 (green).** D1 in `spark-po-detail.component.ts`; D2 in `spark-query-grid.component.ts`; D3 in
  `query-refresh.service.ts`; corrected docs on the service, `IClientAccessor.RefreshQuery` and
  `RefreshQueryOperation`.
- **M3.** SparkTestDriver class `RefreshQueryEnvelopeTests` (`tests/MintPlayer.Spark.Tests/Endpoints/Actions/`):
  - a custom action's `RefreshQuery(alias)` and `RefreshQuery(id)` arrive verbatim as a `refreshQuery`
    operation on the `/spark/actions/execute` envelope;
  - `/spark/queries/get` resolves the query by id, by alias and by a different-case alias, and the body
    carries both `id` and `alias`, which are the keys the grid matches on.

  These pin an existing contract, so they are green from the start. To prove they can fail, they are
  checked once by mutation (the probe action's `RefreshQuery` call removed → red).
- **M4 (red → green).** E2E `QnA/QnACustomActionRefreshTests`: the author opens their question's detail
  page, the `/spark/actions/execute` response is given a `refreshQuery` (one test by alias, one by id),
  `Close` is clicked, and the test asserts **exactly one** `/spark/queries/execute` after the action.
  Run against the reverted po-detail change for red, then green.
- **M5.** Run the affected suites once at the end (`npm run test:affected`).

## 7. Not done

- Nothing. D5 was first deferred, then done in this PR at the owner's request.

## 8. Status

- [x] M1 red (3 specs failing for the right reasons: call order 161 > 159, unrelated key reloaded, `tokenFor` case 0 ≠ 1)
- [x] M2 green (169/169 in the 5 touched spec files)
- [x] M3 SparkTestDriver `RefreshQueryEnvelopeTests` 8/8; mutation (no `RefreshQuery`, `Ordinal` alias index) → 5/8 red
- [x] M4 E2E `QnACustomActionRefreshTests`: pre-fix client **2, 2** `/execute` (alias, id) and 1 (no server refresh); fixed client **1, 1, 1**
- [x] M5 `npm run test:affected -- --skip-remote-cache`: all 12 affected projects green, unit + E2E, 5m02s (the first attempt only failed on the remote cache answering 499 after successful builds)
- [x] M6 D5: grid `reload()` via `BsDatatableComponent.reload()`, filters with `resetPage`; ng-spark 1057/1057
- [x] M7 versions: ng-spark `22.28.1` → `22.29.0` (minor: `reload()` gained an option, and grids ignore refreshes for other queries); Spark, Abstractions, SourceGenerators, AllFeatures, Testing `11.0.0-preview.98` → `preview.99` (majors unchanged: Angular 22, .NET 11)

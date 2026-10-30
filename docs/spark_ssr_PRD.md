# Spark server-side rendering — PRD and plan

Status: **Decisions locked 2026-10-30** (grilling session; D7 delegated to Claude). Next: the spikes (§5), then M1–M7.

## 1. Goal

Every page of `apps/MintPlayer` is server-side rendered with its data already in the HTML. The feature lives in the
framework, so **no per-entity code** is needed: the legacy site's hand-written `OnSupplyData` switch is replaced by one
generic supplier that understands Spark's own routes. A server-rendered page shows **exactly** what the same user would
see through the HTTP API: same row security, same attribute redaction, same denials.

Non-goals: SSR of the Lit front of ng-bootstrap (out of scope there too); static prerendering at build time (SSG).

## 2. What exists today (evidence)

### 2.1 Legacy MintPlayer (`C:\Repos\MintPlayer`, Angular 13.3)
- `MintPlayer.Web/Services/SpaPrerenderingService.cs`, class `SpaRouteService : ISpaPrerenderingService`, registered
  at `Startup.cs:173`. `BuildRoutes` (:54-141) **duplicates the Angular route table in C#**; `OnSupplyData` (:143-570)
  is one `switch (route?.Name)` that loads DTOs per entity into `data["person"]`, `data["songs"]`, …
- 404 through `Response.OnStarting` (:151-158), slug-mismatch redirects through `GenerateUrl` (:188, :217, :318),
  `last-modified` (:211). The current user always goes in `data["user"]` (:146-147).
- **Authorization was ad hoc**: only the playlist cases check the owner (403 at :520, :548). Nothing else.
- `main.server.ts:24-120` turns each key into a string injection token; `main.ts:20-39` provides them all as `null` in
  the browser; about 20 components branch on `@Inject(SERVER_SIDE)`. **The browser refetches everything on boot**
  (no TransferState). `NODE_TLS_REJECT_UNAUTHORIZED = "0"` at `main.server.ts:130`.
- Cost per new entity: a C# case, a token in `main.server.ts`, a null provider in `main.ts`, and a component branch.

### 2.2 `MintPlayer.AspNetCore.SpaServices` (`C:\Repos\MintPlayer.AspNetCore.SpaServices`)
- `UseSpaPrerendering` captures the downstream `index.html`, then runs the server bundle in Node (NodeServices,
  `Content/Node/prerenderer.js`) with the capture as `originalHtml`. Only `GET`, `text/html`, exactly `200` and a
  complete body are rendered; everything else passes through (README "How it works").
- `ISpaPrerenderingService.OnSupplyData(HttpContext, IDictionary<string, object>)` is **awaited**
  (`SpaPrerenderingExtensions.cs:377, :744`); a status set there (404/403/302) is honoured (`:380`,
  `ResponseHeaderPreservationTests`).
- `HttpContext.SkipPrerendering()` opts a request out (`PrerenderingHttpContextExtensions.cs:24-36`).
- The demo runs **Angular 21** `renderApplication` behind `createServerRenderer` (`Demo/Prerendering/Demo.Web/
  ClientApp/src/main.server.ts`), built with the old separate `ng run ClientApp:server` target.
  **Not yet proven on Angular 22's `@angular/build:application` (ESM server output).** → spike SP1.

### 2.3 ng-bootstrap (`C:\Repos\mintplayer-ng-bootstrap`, commit b28e1ac4, #412/#437)
- In `@mintplayer/ng-bootstrap` **v22.29.0** (tag `@mintplayer/ng-bootstrap/v22.29.0`). There are no custom elements
  and no shadow roots any more. Styles behave like emulated encapsulation, using `[data-mps]` in `@layer mp.components`
  with Bootstrap in `@layer mp.bootstrap`. Sheets are written into `DOCUMENT.head` during the server render.
- Browser-only work is guarded inside the library, so consumers need no guards. The one known limit is the theme
  toggle, which renders disabled on the server.
- Breaking changes for consumers are listed in `CHANGELOG.md` `[Unreleased] → Breaking`; there is no separate
  migration guide. They are: outputs renamed (`selectionChange`→`selectionChanged`, carousel `slideStart/End`,
  tree-select `searchStart`), `checkboxRef`/`radioRef`/`navStyle` removed, `*bsShellSidebar` now structural with a
  new `*bsShellTopbar`, `*bsNavbarDropdownLabelTemplate`, typeahead `[provider]`, and the `lit` peer dropped.
- Reference SSR setup: `apps/ng-bootstrap-demo/server.ts` (`AngularNodeAppEngine`), `app.routes.server.ts`
  (`RenderMode.Server` for `**`), `provideClientHydration(withEventReplay())`, zoneless. The e2e test is
  `apps/ng-bootstrap-demo-e2e/e2e/ssr.spec.ts`.
- Spark today: root `package.json:40` pins `@mintplayer/ng-bootstrap ^22.28.0` (pre-SSR); `ui-core ^1.1.0`,
  `web-components ^3.1.0`.

### 2.4 Spark
- **Hosting**: `apps/MintPlayer/MintPlayer/Program.cs:65-109` — `AddSpaStaticFilesImproved`, `UseSpark`,
  `MapSpark`, then `UseWhen(!/spark) → UseSpaImproved` with `UseAngularCliServer("start")` in Development. SparkId and
  DemoApp copy the block; Spark has no wrapper. Angular **22.2.2**, Nx `@nx/angular:application`, browser entry only,
  service worker in production. No app has `main.server.ts`/`@angular/ssr`.
- **Routes are generic** (`libs/node_packages/ng-spark/routes/src/spark-routes.ts:19-43`): `query/:queryId`,
  `po/:type/new`, `po/:type/:id/edit`, `po/:type/:id`, `po/:type/:id/:slug`, `po/:type`. MintPlayer adds only auth
  routes and `home` (a `SparkBoardComponent`). **The C# side can match these without per-entity code**, which is the core
  insight of this PRD.
- **Data path**: everything goes through `SparkService` (`ng-spark/services/src/spark.service.ts`): `POST /spark/po/load`
  (:242), `POST /spark/queries/execute` (:158), `POST /spark/queries/get` (:136), `GET /spark/types[/id]`,
  `/permissions/{id}`, `/program-units`, `/lookupref`.
- **Read paths**: `IPersistentObjectReadPath.LoadAsync` (`Services/PersistentObjectReadPath.cs:14-32`; row security,
  deleted scope, `OnDisableActions`, Read veto) and `IQueryReadPath.ExecuteAsync` (`Services/QueryReadPath.cs:22-49`;
  `IPermissionService`, `IRowSecurity`). Both are `internal`, Scoped, already shared by the HTTP endpoints and the live
  socket, and take the user from the scoped `HttpContext`.
- **Boundary net** (`Services/SparkPresentation.cs`): a `JsonTypeInfo.OnSerializing` hook installed into the HTTP JSON
  options. It checks every outbound `PersistentObject` was presented for *this* request; in Development it throws,
  elsewhere it prunes fail-closed. **SSR data must be serialized with the same options**, or it bypasses the net.
- **Browser globals** appear in ~25 ng-spark files (`spark-live.service.ts:426` WebSocket/`window.location`,
  `spark-language.service.ts:37,55,66` localStorage/`document.cookie`, `po-detail.component.ts:760`
  `window.history`, `spark-auth.service.ts:301-336`, PWA, notifications, WebAuthn, the timezone interceptor). Only 7
  files use `isPlatformBrowser`/`afterNextRender`. No `provideClientHydration`, no TransferState.
- **MintPlayer `player/*`** (YouTube, Vimeo, SoundCloud, Dailymotion, `PlaylistController`) is browser-only.
- Earlier notes: `docs/issue_460_PRD.md:1181-1195` (S-TZ2, timezone under SSR; `DatePipe` IANA limit at :150,
  :476-477); `docs/mintplayer_website_PRD.md` :161 (SSR goal), :179 (A4 bot pages "until SSR lands"), :196 (S6
  re-validate SSR on Angular 22). **This PRD supersedes S6 and retires A4 once SSR is live.**

## 3. Design (proposed)

```
GET /po/song/123/bohemian-rhapsody
  └─ UseSpaPrerendering (SpaServices) ── captures index.html
       └─ SparkPrerenderingService.OnSupplyData(ctx, data)          ← framework, generic
            ├─ match path against Spark's route table (po/query/board/home, app-registered extras)
            ├─ call the SAME read paths in-process with ctx.User
            │     IPersistentObjectReadPath / IQueryReadPath / types / permissions / program units / current user
            ├─ serialize each result with the HTTP JSON options (boundary net included)
            ├─ data["spark"] = { "<METHOD> <url> <body-hash>": <exact HTTP response body>, … }
            ├─ also: board tiles, lookups, sub-queries from the model (D3); app hooks for custom requests (D11)
            └─ status: the read path's own (anonymous 401 / signed-in 404, D4); slug mismatch → 301 if readable
  └─ Node: main.server.ts (renderApplication) → provideSparkServer(params.data.spark)
       └─ server-only HttpInterceptor answers SparkService requests from the map
            └─ Angular HttpTransferCache records the answers into <script id="ng-state">
  └─ Browser hydrates; the same requests hit the transfer cache → no second fetch
```

- **No per-entity code and no injection tokens.** The components don't change: they call `SparkService` as they do
  now. Only the transport answers differently on the server.
- **Authorization is inherited, not reimplemented**: the supplier runs the exact read paths and JSON options of the
  HTTP endpoints, under the request's user. The supplied map holds HTTP response bodies, so whatever Angular renders
  is byte-identical to what the user would fetch themselves.
- **Response headers**: `Cache-Control: private, no-store` and `Vary: Cookie` on every prerendered response (D7).
- **SEO tags** come from the model mapping plus an optional hook on the presented object (D12).

## 4. Decisions

| # | Decision | Status | Evidence |
|---|---|---|---|
| D1 | Rendering engine: SpaServices.Prerendering (NodeServices + `OnSupplyData`). `@angular/ssr`'s `AngularNodeAppEngine` is **not** a fallback: if SP1 finds the Angular 22 ESM bundle does not load, the fix goes into SpaServices (an issue there, released first). | ✅ owner, 2026-10-30 | In-process supply needs no loopback, CSRF or second process. |
| D2 | Data hand-off: Angular's **TransferState** mechanism. A request-keyed response map + server interceptor feed `HttpTransferCache` (`includePostRequests`), which writes TransferState (`ng-state`). The legacy injection tokens are dropped. | ✅ owner, 2026-10-30 | Legacy cost 3 edits per entity and refetched on boot (§2.1). |
| D3 | **Avoid misses as far as possible**: the supplier also works out board tiles, lookup refs, sub-queries and AsDetail data in-process from the model definitions. Anything still missed renders without data (the loading state) and the browser fetches it after hydration. Each miss is logged in Development, and an M5 test fails on any miss for the seeded MintPlayer pages. Rejected: a loopback HTTP call (an extra hop plus cookie/XSRF forwarding, which D1 avoids) and a two-pass render (double render time). | ✅ owner, 2026-10-30 (accepted without objection) | SP3/SP5 measure how many misses a real page has. |
| D11 | **App components that use `HttpClient` directly need no branches.** The app's C# hook supplies the *response* to the request the component will make, keyed by method, URL and body, through the same interceptor + TransferState path. No `DATA_FROM_SERVER` token: a token keeps holding the first page's data after client-side navigation unless it is cleared, while `HttpTransferCache` stops answering once the app is stable. A key mismatch degrades to a browser fetch and shows up in the D3 miss log. | ✅ owner, 2026-10-30 | Owner proposed an optional token. B was chosen as lower-code. |
| D4 | The SSR status **follows the API's rule exactly**: a denied or missing object/query gives anonymous callers **401** (login prompt) and signed-in callers **404** ("not found"). The supplier passes the read path's status through rather than computing its own. A slug mismatch gives a **301 to the canonical slug** only when the caller may read the object; otherwise it is the denial above, and the slug is never revealed. Every non-200 page carries `noindex` (D12). SP4 asserts that an anonymous request for a non-existent id and for a forbidden id return the same status **and** the same HTML. | ✅ owner, 2026-10-30 | `reference_sparkauthorize_default_policy_401`; existence must not leak (`project_coverage_private_repo_existence_oracle`). |
| D12 | **SEO tags = model mapping + an optional per-type hook.** The entity type declares its SEO in the model: the title is `Breadcrumb`, plus a description attribute, an image attribute, a schema.org `@type` and an indexable flag. ng-spark sets `Title`/`Meta`/canonical/robots/OG/JSON-LD generically on both server and client. The optional hook covers rich JSON-LD (references) and receives **only the presented, pruned object**: no session, no stored document. Robots: `noindex` on non-200, `/new`, `/edit` and non-indexable types; `index` only on a 200 page of an indexable type. The canonical link is the slugged URL. M5 asserts that a redacted value appears nowhere in the raw HTML, `ng-state` included. | ✅ owner, 2026-10-30 | `EntityTypeDefinition.cs:100-106` (Breadcrumb); ng-spark has no `Title`/`Meta` use today. |
| D5 | **In `MintPlayer.Spark` core.** Core references `MintPlayer.AspNetCore.SpaServices.Prerendering`, and the feature is opt-in through `AddSparkPrerendering()` / `UseSparkPrerendering()` (inert unless called). The read paths and `SparkPresentation` stay `internal`, so no public API can load data around authorization. On the npm side, a secondary entry point `@mintplayer/ng-spark/ssr`. Accepted cost: every app carries the NodeServices assemblies. | ✅ owner, 2026-10-30 | Core already references SpaServices.Xsrf (`MintPlayer.Spark.csproj:107`); read paths are internal (§2.4). |
| D6 | **SSR is switched on for MintPlayer only.** Every app gets the shared changes (D8 ng-bootstrap 22.29, M2 SSR-safe ng-spark) but keeps browser rendering. That is a decision that they don't need SSR, not something deferred: the demos are behind a login, CodeCoverage is too apart from public repositories, and SeoTester already has bot pages. The shared ng-spark changes are only *verified* under SSR on MintPlayer. | ✅ owner, 2026-10-30 | |
| D7 | **Rendered pages are never cached.** Every prerendered response gets `Cache-Control: private, no-store` and `Vary: Cookie`, and every request renders fresh. A shared cache for anonymous pages is rejected, because one stray `Set-Cookie` (anti-forgery) or a missing cookie dimension in the key would hand one visitor's page or token to another. An in-process output cache is rejected because Spark has no data-change invalidation for arbitrary queries. **Revisit trigger:** if SP1 measures a warm render above ~150 ms, an anonymous in-process output cache comes back into *this* PR. The service worker keeps serving the raw `index.html` for later navigations, so returning visitors skip SSR (crawlers have no service worker). | ✅ Claude, delegated by owner, 2026-10-30 | The old site's S1 measured ~30 ms TTFB (`mintplayer_website_PRD.md:48`, treat as history). SP1 re-measures. |
| D8 | ng-bootstrap upgrade to ^22.29.0 (no web components) lands in this same PR, with every app migrated. | proposed | One PR rule; SSR depends on it. |
| D9 | Live updates (WebSocket) connect only after hydration (`afterNextRender`), never on the server. | proposed | `spark-live.service.ts:426`. |
| D10 | **The server renders from cookies, with per-app defaults.** Language: the `spark-lang` cookie, then `Accept-Language`, then the app default. Theme: the dark-mode cookie (#462). Timezone: the `spark-timezone` cookie or `X-Spark-Timezone`, otherwise a **per-app default zone** in options (MintPlayer: `Europe/Brussels`). Every date renders as `<time datetime="ISO">text</time>`. After hydration, the browser re-formats it **through a signal** if its own zone differs; changing the text directly is a hydration mismatch and discards the server DOM. Offsets are computed per value, because `DatePipe` takes offsets, not IANA zones. Rejected: UTC (a visible 1–2 h jump on first visits) and no server dates (crawlers lose them). | ✅ owner, 2026-10-30 | `spark-language.service.ts:45-55`; `RequestTimeZoneResolver.cs` (cookie + `HeaderName`); `docs/issue_460_PRD.md:150`, S-TZ2. |

## 5. Spikes

Each spike is time-boxed and ends with a written result in §7 (numbers, file:line, red→green test).

| Spike | Question | Box |
|---|---|---|
| **SP1** | Does SpaServices.Prerendering's `prerenderer.js` load an Angular 22 `@angular/build:application` server bundle (ESM `.mjs`, `server` entry, no `ssr` entry)? Does the `AngularPrerendererBuilder` watch work under `UseAngularCliServer`? TTFB on a warm render of `/home` and `/po/song/{id}`. If ESM fails: a CJS wrapper, a SpaServices fix (filed **as an issue there**), or the D1 fallback. | 4 h |
| **SP2** | Render the MintPlayer shell + `po/:type/:id` + `query/:id` under `platform-server` with ng-bootstrap 22.29. List every crash or hydration mismatch (NG05xx) from browser globals in ng-spark, the auth package and the app. | 4 h |
| **SP3** | Supplied map + server interceptor + `withHttpTransferCacheOptions({ includePostRequests: true })`: are the keys stable for POST bodies (`/spark/po/load`, `/spark/queries/execute`), and does the browser make **zero** Spark data requests after hydration (Playwright network log)? | 3 h |
| **SP4** | Security equivalence. The in-process supplier, serialized with the HTTP options, must be byte-equal to the HTTP endpoint for anonymous, signed-in-without-right, signed-in-with-right, row-secured and redacted-attribute cases. The boundary net must fire, and must throw in Development for an unpresented object. This becomes the permanent test matrix (M5). | 4 h |
| **SP5** | Board/home page: how many requests do the tiles make, can the supplier predict them from the board definition, and what does a miss cost under D3 (a)/(b)/(c)? | 3 h |
| **SP6** | Caching and the service worker. ngsw must never cache a prerendered page (`/index.html` must stay the raw static file; check that dev and prod both do). Check `Cache-Control`/`Vary` on the response, and that SSR HTML is never served across users behind a reverse proxy. | 2 h |
| **SP7** | Hydration with zoneless + `withEventReplay`. The live socket, `spark-language`, timezone and the player must not mismatch, and there must be no layout flash for the theme toggle (the known ng-bootstrap limit). | 3 h |

## 6. Milestones (after the spikes)

1. **M1 ng-bootstrap 22.29.** Upgrade and migrate every app (CHANGELOG breaking list). Drop the `lit` peer and keep
   `web-components` only for `color-mode.css`.
2. **M2 SSR-safe ng-spark.** Guard browser globals: `afterNextRender` for live, PWA and notifications; cookies through
   `REQUEST`/`DOCUMENT` instead of `document.cookie`; `window.history` behind `isPlatformBrowser`. Add
   `provideClientHydration(withEventReplay(), withHttpTransferCacheOptions(...))` to the shared app config.
3. **M3 Server supplier (.NET).** Add `SparkPrerenderingService : ISpaPrerenderingService`, the route matcher over
   Spark's routes (plus an app hook for custom routes), the in-process read-path calls, serialization through the
   HTTP JSON options, status/redirect/`last-modified`, and an `AddSparkPrerendering()` / `UseSparkPrerendering()` pair
   that wraps the current `UseSpaImproved` block.
4. **M4 Server entry (npm).** Add `@mintplayer/ng-spark/ssr`: `provideSparkServer(data)`, the server interceptor, and
   a `createSparkServerRenderer(App, config)` so an app's `main.server.ts` is ~5 lines. Add Nx `server` targets and
   the dev builder script.
5. **M5 Security test matrix.** Make SP4's cases permanent tests. Include an E2E that renders a page as anonymous and
   asserts that redacted fields and denied rows are absent from the **raw HTML and the `ng-state` script**.
6. **M6 MintPlayer adoption.** Make every route SSR, including home/board, song/artist/person, queries and the auth
   pages. The player renders client-only. Retire A4 bot pages. Run a SeoTester check against the result.
7. **M7 SEO + docs.** Add the model SEO mapping and the presented-object hook (D12), plus MintPlayer's mappings
   (Song → `MusicRecording`, Artist → `MusicGroup`, Person → `Person`). Then the docs (`docs/guides/ssr.md`), and route snapshot/CSRF surface guards if new endpoints
   appear (none are expected).

## 7. Spike results

_Empty until the spikes run._

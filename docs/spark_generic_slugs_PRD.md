# Generic slug URLs for persistent objects — PRD and plan

Status: **investigated, decisions proposed, not yet grilled** (2026-11-08). Issue: #602. Blocks #601 (song page header).
One pull request (Spark core + ng-spark + the apps that change), per the one-PR rule.

## 1. Goal

Every persistent object has **one canonical URL**, `/po/{type}/{id}/{slug}`, where the slug is the slugified breadcrumb.
The framework owns it end to end:

- The **server** (the prerender pipeline, before Angular renders) answers `301` for a missing slug (`/po/song/23`) and for
  a stale one (`/po/song/23/old-title`), on every request. Denied and missing objects never get a redirect, so the slug
  never leaks.
- **Every link** the framework renders already points at the canonical URL, so there is no `replaceState` hop.
- **Custom pages** that show one object get the same behaviour without copying code (§4).
- There is **one implementation** of the slug algorithm and of the URL shape.

## 2. What exists today (evidence)

### Server (`libs/spark/MintPlayer.Spark`)

- `SparkPrerenderingService` (`Prerendering/SparkPrerenderingService.cs:19`) implements `ISpaPrerenderingService` from the
  external `MintPlayer.AspNetCore.SpaServices.Prerendering` (csproj:113). `UseSparkPrerendering()` wires it for **every SPA
  request**, not only for bots (`Extensions/SparkBuilderPrerenderingExtensions.cs:29-38, 49-68`).
- `OnSupplyData` (:52-90) runs all `ISparkPrerenderContributor`s. When one calls `context.Redirect(location, permanent)`,
  it sends `Response.Redirect` and returns (:70-74), **before Angular renders**. The first redirect wins
  (`SparkPrerenderContext.cs:61,86`). So the hook the issue needs already exists.
- `SparkRoutesPrerenderContributor` hard-codes the `po/...` patterns (:30-50). `SupplyObjectAsync` (:57-96) POSTs
  `/spark/po/load` in-process, with the page's user and cookies. It 301s **only when a slug is present** and differs from
  `SparkSlug(breadcrumb)` (:71-81). A bare `/po/:type/:id` is "left to the page" (:70).
- Authorization: the load goes through `PersistentObjectReadPath.LoadAsync` (`Services/PersistentObjectReadPath.cs:32-87`).
  A denied or missing object gives the same 404, or a 401 for anonymous callers (`Endpoints/SparkDenial.cs:22-27,75`).
  The redirect requires `item.IsSuccess`, so no slug leaks.
- `SparkEntityLinks` (`Services/SparkEntityLinks.cs:17-51`, `internal`): `TypeSegment` (alias, else id) and `DetailPath`
  build `/po/{type}/{id}/{slug}`. They are used by the sitemap (`SparkSitemapXml.cs:32,36`), search
  (`SparkSearchService.cs:148`, `SearchReadPath.cs:51`) and the contributor (:174). The C# slug function is
  `SparkRoutesPrerenderContributor.SparkSlug` (:255-269).
- The breadcrumb comes from `BreadcrumbResolver` (`Services/Breadcrumb/BreadcrumbResolver.cs:111`), set in
  `EntityMapper.cs:315-330`. Query rows use `row.Breadcrumb ?? row.Name` (`QueryResultProjector.cs:212`). There is no
  translation step (`FormatScalar => value?.ToString()`, :721), but a breadcrumb built from a translated field could
  still vary by language (S4).
- **No payload carries a URL or slug** today: not the PO, not query rows, not references.

### Client (`libs/node_packages/ng-spark`)

- `sparkRoutes({ slugs })` adds `po/:type/:id/:slug` with `data: { sparkSlug: true }` (`routes/src/spark-routes.ts:24,39-44`).
  It has no guard or resolver. `poDetail` swaps the component on both detail routes (:22).
- `sparkSlug` (`po-detail/src/spark-slug.ts:5-12`) is the TS twin of the C# function. `writeSlug`
  (`spark-po-detail.component.ts:393-400`) calls `location.replaceState` after the load. It is skipped when the component
  is embedded or the route has no `sparkSlug`. `writeSeo` (:468-477) builds the same array for the canonical link.
- **Links without the slug**, built inline as `['/po', type, id]`, with no central helper:

| Site | Breadcrumb there? |
|---|---|
| `grid/src/spark-query-grid.component.ts:366` `rowRouteFor` (row link, row click, edit) | yes (`QueryResultItem.breadcrumb`) |
| `pipes/src/reference-link-route.pipe.ts:17` (po-detail.html:178, :250, :262) | the callers have it |
| `pipes/src/router-link.pipe.ts:19` (program unit with `objectId`, board program-unit tile) | no |
| `boards/src/spark-board.component.ts:120` `tileLink`, `boards/src/tiles/spark-po-tile.component.ts:19` | no |
| `moderation/src/spark-review-queue.component.ts:194` | yes |
| `client-operations/src/provide.ts:114` navigate | no (type + id only) |
| `po-create/...:235`, `po-edit/...:468, :516` | on the result / loaded item |
| `organizations/accept/...:41` | no |
| CodeCoverage `spark/vanity-redirects.ts:24,39,54`, `account-dependency-graph-panel.component.ts:418`, `home-route.ts:18` | mostly yes |

Search results are already slugged by the server (`search/src/spark-search.models.ts:12`).

### Custom-mapped pages in the apps (today)

- **No app has a dedicated entity route outside `po/:type/:id`.** CodeCoverage (`app.routes.ts:87`) and SeoTester (`:38`)
  wrap `<spark-po-detail>` through `sparkRoutes({ poDetail })`, on the same URLs. Only MintPlayer turns slugs on (`:34`).
- CodeCoverage's **vanity URLs** (`:provider/a/:login`, `:provider/r/:owner/:repo[/c/:sha]`, `app.routes.ts:97-99`) are
  client-side `canActivate` guards that look the object up and navigate to `/po/...` (`spark/vanity-redirects.ts:17-58`).
  A server never sees them as redirects: SSR renders the guard's result.
- Singletons pinned by `objectId` (CodeCoverage home `/po/home/main`, `programUnits.json:49-62`) have no useful slug.
- **No app has its own `ISparkPrerenderContributor`**, and nothing declares "type X is shown at URL Y". The closest is
  boards: `SparkBoardDefinition.Paths` + `SparkBoardRegistry.FindByPath` + `SparkBoardsPrerenderContributor`, with
  literal paths only.
- #601 will add the first real custom page: a dedicated song component on `po/song/:id(/:slug)`.

## 3. Proposed decisions

All of these are proposals until the grill; the owner's own statements are marked.

| # | Decision | Evidence / reason |
|---|---|---|
| D1 | **The slug system becomes a framework-core feature**, in `MintPlayer.Spark` + `@mintplayer/ng-spark`, opt-in per app as today. | Owner, 2026-11-08: "probably into the framework core". |
| D2 | **The server redirects before Angular loads**, for a missing slug *and* a stale one, on every request (not only bots). `301`, query string kept. Never for denied, missing or deleted objects. | Owner, 2026-11-08: "SpaPrerenderingService … should be able to redirect before loading the angular page, both for /:id urls and outdated /:id/:old_slug urls". The contributor already loads the object (:57-96), so a missing-slug 301 costs no extra request. Prerendering already runs on every SPA request. |
| D3 | **One server service owns the URL**: `SparkEntityLinks` becomes a public `ISparkEntityLinks` in core, with the only slug implementation (moved out of the contributor). Sitemap, search, the contributor and the payloads (D4) use it. | Removes the C#/TS twin (§2). Apps need it for their own contributors (D7). |
| D4 | **The server sends the canonical path with each object** (`href`): on the PO payload, query result rows, reference attribute values, AsDetail rows, client-operation navigate, program units with an `objectId`, board tiles, create/update results. The client never computes a slug; `spark-slug.ts` is deleted. | One source of truth. Six of the ~12 link sites have no breadcrumb (§2 table), so a client helper cannot fix them anyway. The cost (payload size per row) is measured in S2. |
| D5 | **One client entry point for links**: a `sparkHref` pipe / `SparkLinks.detail(item)` that reads `href` (and falls back to `/po/{type}/{id}` only where no object is at hand, e.g. a bare id). Every `['/po', …]` in §2 switches to it. | Replaces the scattered arrays; `/po` appears in one place on each side. |
| D6 | **Client-side correction is a routing concern**: a framework resolver (`sparkObjectResolver`) on the detail routes loads the object and returns a `RedirectCommand` (replace URL, no history entry) when the URL is not `href`. The page gets the loaded object from the route; `writeSlug` goes away. It covers in-app navigations to an old URL (bookmarks inside the SPA, `navigate` client operations, app code). | Today only `spark-po-detail` corrects (`writeSlug`), so a custom page gets nothing. During SSR the server has already redirected (D2), so the resolver only acts in the browser. Feasibility in S1. |
| D7 | **Custom-mapped pages, three tiers** (§4): (a) custom component on the standard URL: route helper, nothing else; (b) custom URL with the id in it: an entity-type URL template in the model; (c) natural-key URLs (vanity): the app's own prerender contributor using `ISparkEntityLinks`. | The owner's open question; see §4. |
| D8 | **Types with no meaningful slug** (singletons, empty breadcrumb, breadcrumb equal to the id): no slug segment; the canonical URL is `/po/{type}/{id}`, and a URL *with* a slug 301s to it. | Today an empty slug or `'edit'` already drops the segment (`writeSlug` :393-400). |
| D9 | **Language**: *open, depends on #604* (`?lang=` in the URL, hreflang, sitemap alternates). Until then the slug comes from the breadcrumb in the **default language**, so one object has one URL. *Pending S4 and #604.* | A slug per language only makes sense once the language is part of the URL (#604); with the cookie alone, the 301 target would depend on a cookie. Owner, 2026-11-08: the URL must carry the language for SEO, and that is its own issue (#604). |
| D10 | **The `/po` prefix is centralised, not configurable**, except through the per-type URL template (D7b). | YAGNI: no app asked for another prefix; the template covers a real need per type. |

## 4. Custom-mapped Angular pages (the owner's open question)

The server cannot read the Angular route table, so the generic answer is that **the URL shape is declared once, on the
server, per entity type**, and the client only consumes `href`s. That splits custom pages into three tiers.

**(a) A custom component on the standard URL** (#601's song page, CodeCoverage/SeoTester's `poDetail` wrappers).
Nothing changes on the server: the URL is still `/po/{type}/{id}/{slug}`. On the client, the app declares the route with
a framework helper instead of a hand-written one:

```ts
sparkObjectRoute({ type: 'song', loadComponent: () => import('./pages/song/song-detail-page.component') })
// → po/song/:id and po/song/:id/:slug, with sparkObjectResolver; placed before sparkRoutes()
```

The page reads the resolved object (`input()` bound from route data) and gets slug correction, the 404 path and the
canonical link from the framework. `sparkRoutes({ poDetail })` becomes the same helper for "every type".

**(b) A custom URL that still carries the id**, e.g. `songs/{id}/{slug}` instead of `po/song/{id}/{slug}`. The entity type
gets an optional URL template in its model JSON (working name `"route": "songs/{id}/{slug}"`; S3). `ISparkEntityLinks`
builds `href`s from it, so links, the sitemap and search follow automatically. `SparkRoutesPrerenderContributor` matches
every declared template in addition to the default `po/{type}/{id}/{slug}`, and 301s the default URL to the template.
The client route is declared with `sparkObjectRoute({ type, path: 'songs/:id/:slug', … })`. A startup check (or test)
compares declared templates with the client route (S3).

**(c) Natural-key URLs** (CodeCoverage's `:provider/r/:owner/:repo`). The key isn't the id, so no template can express it
generically. The app writes its own `ISparkPrerenderContributor` that resolves the object and calls
`context.Redirect(links.DetailPath(...), permanent: true)`. The public `ISparkEntityLinks` (D3) is what makes that a few
lines. The existing client guard stays for in-app navigation. Whether CodeCoverage's vanity URLs move is a scope
question (Q4).

## 5. Spikes

Each spike is time-boxed (≤ 1 h), ends with a decision written here, and ships nothing on its own.

- **S1 — resolver + `RedirectCommand` under Angular 22, SSR and hydration.** Can a `ResolveFn` return a `RedirectCommand`
  with `replaceUrl`? Does the resolver's load replace po-detail's own load without a double request, including the
  `TransferState` hand-off after SSR? What happens to the embedded `objectType` mode, the deleted view and retry
  handling? Outcome: resolver vs a canActivate guard plus a service.
- **S2 — `href` in the payloads.** Exact places (PO, `QueryResultItem`, reference values, AsDetail rows, navigate operation,
  program units, boards, create/update results), the C# + TS model changes, and the size on a large grid page
  (MintPlayer songs, CodeCoverage commits). Does anything cache payloads that would then hold stale slugs?
- **S3 — the entity URL template (D7b).** Schema field and name, placeholders (`{type}`, `{id}`, `{slug}`), how the
  contributor matches templates (compile once at startup, ordered, conflict detection with boards and other templates),
  how a RavenDB id with `/` (`Songs/23`, URL-encoded as today) fits, and how to catch a mismatch with the Angular route.
  Possibly a source-generator/analyzer diagnostic. If no app needs a non-`po` URL now, D7b may be documented and deferred
  (Q2).
- **S4 — language.** Find entity types whose breadcrumb uses a translated field (`TranslatedString`, per-language
  attributes) in the apps; check what `BreadcrumbResolver` produces per `spark-lang`. Decide D9.
- **S5 — what a missing-slug 301 breaks.** Tests, E2E and the route snapshot that expect `200` on `/po/:type/:id`
  (MintPlayer, SeoTester checks, `SparkRoutesPrerenderContributor` tests); links in emails, webhooks or check runs
  (CodeCoverage check-run deep links) that use the bare URL and would now get a 301 each time; the `LegacyRedirects.cs`
  chain in MintPlayer (avoid a double 301).

## 6. Milestones (one PR)

1. **M1 — server core:** public `ISparkEntityLinks` with the single slug implementation; the contributor 301s for a missing
   and a stale slug; D8 rules; unit tests on the contributor (missing, stale, correct, denied, anonymous, deleted, no-slug type,
   query string kept).
2. **M2 — `href` in the payloads** per S2, C# and TS models; tests that the grid row, reference and PO carry the canonical path.
3. **M3 — client links:** `sparkHref` pipe / `SparkLinks`, all sites in §2 switch; delete `spark-slug.ts` and `writeSlug`;
   `writeSeo` uses `href`.
4. **M4 — routing:** `sparkObjectResolver` + `sparkObjectRoute()` per S1; `sparkRoutes()` built on it; po-detail takes the
   resolved object.
5. **M5 — custom pages:** the URL template (D7b) if S3 keeps it; the docs page for the three tiers; a guide example.
6. **M6 — apps:** MintPlayer (slugs already on), CodeCoverage links (vanity redirects, graph panel, home) and whatever Q1/Q4
   decide; release note (ng-spark minor, breaking for apps that used `sparkSlug` or `data.sparkSlug`).
7. **M7 — verify once at the end:** Spark + ng-spark tests, then on a running MintPlayer host: `curl -I` on `/po/song/{id}`
   (301), a stale slug (301), the canonical URL (200), a denied object (404, no `Location`); a grid click lands on the slugged
   URL with no `replaceState`.

## 7. Open questions for the owner

- **Q1** Should slugs stay opt-in per app (`sparkRoutes({ slugs: true })` + a server option), or become the default for every
  app? Recommendation: the default, since nothing breaks and SEO only improves; HR/Fleet are behind a login anyway.
- **Q2** Do we build the per-type URL template (D7b) now, or document tiers (a) and (c) and defer (b) until an app needs it?
- **Q3** Language (D9): one slug in the default language, or a slug per language? Settle together with #604.
- **Q4** Do CodeCoverage's vanity URLs move to a server contributor (real 301s) in this PR, or stay client guards?

# Spark search, OpenSearch description and search engines — plan

PRDs: [spark_search_PRD.md](spark_search_PRD.md) and [ng_spark_provide_spark_PRD.md](ng_spark_provide_spark_PRD.md).
Issues #238 and #560. **One PR**, branch `feat/issue238-search`.

## Status

- 2026-11-04: investigation (two teams), two grill rounds, PRD written. Upstream gaps filed as
  MintPlayer/MintPlayer.AspNetCore.Tools#48. Same day: `provideSpark(withX())` (#560) added to this PR.
  O5 (security plugin off) and O6 (RavenDB fallback) decided. **Open: Q1/Q2 (#560), answered by SP1.**
  Next: M0 spikes.

## M0 — Spikes (results into PRD §9, with numbers)

- [ ] S1 description library inside the module registry (R3 exception).
- [ ] S2 source generator reading App_Data model JSON.
- [ ] S3 RavenDB provider latency on MintPlayer data (Lucene, fuzzy, suggest, re-check).
- [ ] S4 sync: checkpointed poll vs messaging queue → decision D8.
- [ ] S5 disambiguation cost, stability, suffix parsing.
- [ ] S6 `OpenSearch.Client` on net11, version pin, 512 MB heap footprint, suggestion quality.
- [ ] S7 VPS `vm.max_map_count` + free RAM (owner runs).
- [ ] S8 `/search?q=` prerender + transfer cache.
- [ ] S9 OpenSearch `services:` container in an existing CI job.
- [ ] SP1 (#560) inventory of `provideSpark*` call sites, injector levels, order constraints.

## M-P — `provideSpark(withX(), …)` (#560), before M6 so search ships as features

- [ ] `SparkFeature` (`kind`, `order`, `providers`, `requires`), `provideSpark(...features)` with sorting,
      duplicate check (multi-registration kinds merge), `requires` resolution, `withConfig`.
- [ ] Convert every `provideSpark*` (except `provideSparkServerRendering`) to a `withX()` in its entry point; remove the old functions.
- [ ] Update all app configs (17 files), ng-spark tests, README and guides (43 docs).
- [ ] Unit tests: order independence, duplicate error, `requires`.

## M1 — Model + shared helpers

- [ ] `EntityTypeSearch` (`enabled`, `label`, `fields`, `fuzzy`) on `EntityTypeDefinition`; template validation
      reusing the breadcrumb validator; schema regenerated; `entity-type.ts`.
- [ ] Move `DetailUrl`, `ListQuery`, anonymous scope from `SparkSitemapXml` to core; sitemap uses them (D12).

## M2 — `MintPlayer.Spark.Search` (server)

- [ ] `ISparkSearchProvider` + candidate record in Abstractions.
- [ ] `SearchSecurityFilter` (D2), over-fetch.
- [ ] Labels + disambiguation + `ResolveExactAsync` (D3).
- [ ] `spark.AddSearch(...)`, options (`Fuzzy`, caps), in-endpoint rate limiter.
- [ ] Endpoints `/spark/search`, `/spark/search/suggest` (generator classes, `SparkPrerenderable`).
- [ ] Prerender contributor for `/search`.

## M3 — RavenDB provider + generator

- [ ] Generator emitting `Spark_Search` (Lucene, fields per D5) into the app.
- [ ] Default provider: search, fuzzy, suggest.

## M4 — `MintPlayer.Spark.Search.OpenSearch` (engine)

- [ ] `UseOpenSearch(config)`, client, index template + alias, query/suggest with fuzziness.
- [ ] Sync per S4 + full reindex command.
- [ ] Circuit breaker → RavenDB provider fallback (D6); `Spark_Search` deployed with `UseOpenSearch` too.

## M5 — `MintPlayer.Spark.OpenSearchDescription`

- [ ] Wrapper per D9 (SitemapXml shape, PackageReference `MintPlayer.AspNetCore.OpenSearch` 11.0.x).
- [ ] `SparkOpenSearchService` → labels / SingleHit (D4).
- [ ] R3 exception, route snapshot (`SPARK_UPDATE_ROUTE_SNAPSHOT`), `CsrfSurfaceTests`.

## M6 — ng-spark `search` entry point

- [ ] `withSearch()`, `sparkSearchRoutes()`, `SparkSearchPageComponent`, `<spark-search-box>`.
- [ ] `withOpenSearchDescription({ title })` (`<link rel="search">`, `requires` withSearch).

## M7 — mintplayer.com

- [ ] `search` blocks on Artist, Person, Song (labels, fields); delete `Indexes/Subjects_Search.cs`.
- [ ] `Program.cs`: `AddSearch(s => s.UseOpenSearch(...))`, `AddOpenSearchDescription(...)`; `app.config.ts`
      (`withOpenSearchDescription` in `provideSpark`) + routes.
- [ ] `apps/MintPlayer/opensearch/Dockerfile`, compose service + network + volume, deploy workflow builds/pushes
      the image; security plugin disabled, internal network only (D13).
- [ ] Point `mintplayer_website_PRD.md:99` and plan step 8 here.

## M8 — Tests (run once, at the end)

- [ ] Per PRD D14; CI: OpenSearch container joins an existing job.

## M9 — Docs

- [ ] READMEs for the three packages (security contract for providers), guide page, #238 description.

## M10 — PR + deploy

- [ ] Open the PR; CI is the sweep. Owner: `vm.max_map_count` + `.env` on the VPS before deploy.
- [ ] After deploy: production black-box check (PRD §1.1, §7) and a SeoTester run.

# Spark search, OpenSearch description and search engines — plan

PRDs: [spark_search_PRD.md](spark_search_PRD.md) and [ng_spark_provide_spark_PRD.md](ng_spark_provide_spark_PRD.md).
Issues #238 and #560. **One PR**, branch `feat/issue238-search`.

## Status

- 2026-11-04: investigation (two teams), two grill rounds, PRD written. Upstream gaps filed as
  MintPlayer/MintPlayer.AspNetCore.Tools#48. Same day: `provideSpark(withX())` (#560) added to this PR.
  O5 (security plugin off) and O6 (RavenDB fallback) decided. **Open: Q1/Q2 (#560), answered by SP1.**
  Next: M0 spikes.
- 2026-11-04: spikes S1–S6, SP1 done (PRD §6.1). M1 model + shared links and M2 server package written; the
  Search project builds (type-check only, no tests run yet). Deviations from the PRD, recorded in its §8:
  no exact-label index field (label lookup = AND search of the label's words, core compares rendered labels);
  a third endpoint `/spark/search/resolve` for D4; the index must **store** `Type` (provider projects it with
  `FromIndexOrThrow`). M1 complete. Next: M3 generator.

## M0 — Spikes (results into PRD §9, with numbers)

- [x] S1 description library inside the module registry — no R3 exception needed.
- [x] S2 generator reading model JSON — feasible as-is.
- [x] S3 corpus size (301 searchable docs) — D5 needs no measurement.
- [x] S4 sync — poll backbone + delete messages (D8).
- [x] S5 disambiguation — exact first, then known type suffix.
- [x] S6 OpenSearch.Client 2.3.0 + engine 3.9.0; footprint measured in CI/after deploy (no local Docker).
- [ ] S7 VPS `vm.max_map_count` + free RAM (owner runs).
- [ ] S8 `/search?q=` prerender + transfer cache.
- [ ] S9 OpenSearch `services:` container in an existing CI job.
- [x] SP1 (#560) inventory — Q1, Q2 answered.

## M-P — `provideSpark(withX(), …)` (#560), before M6 so search ships as features

- [ ] `SparkFeature` (`kind`, `order`, `providers`, `requires`), `provideSpark(...features)` with sorting,
      duplicate check (multi-registration kinds merge), `requires` resolution, `withConfig`.
- [ ] Convert every `provideSpark*` (except `provideSparkServerRendering`) to a `withX()` in its entry point; remove the old functions.
- [ ] Update all app configs (17 files), ng-spark tests, README and guides (43 docs).
- [ ] Unit tests: order independence, duplicate error, `requires`.

## M1 — Model + shared helpers

- [x] `EntityTypeSearch` (`enabled`, `label`, `fields`, `fuzzy`) on `EntityTypeDefinition`; template validation
      (`ModelSynchronizer.ValidateSearch`); schema regenerated (gitignored output); `entity-type.ts`.
- [x] Move `DetailUrl`, `ListQuery` from `SparkSitemapXml` to core `SparkEntityLinks`; sitemap uses them (D12).

## M2 — `MintPlayer.Spark.Search` (server)

- [x] `ISparkSearchProvider` + candidate record in Abstractions.
- [x] `SearchReadPath` (D2, the "SearchSecurityFilter"), over-fetch ×3.
- [x] Labels + disambiguation + `ResolveAsync` (D3), natural id order.
- [x] `spark.AddSearch(...)`, options (`Fuzzy`, caps), in-endpoint rate limiter, engine circuit breaker (D6).
- [x] Endpoints `/spark/search`, `/spark/search/suggest`, `/spark/search/resolve` (generator classes; results `SparkPrerenderable`).
- [ ] Prerender contributor for `/search`.

## M3 — RavenDB provider + generator

- [x] Generator emitting `Spark_Search` (Lucene, fields per D5) into the app (`SearchIndexGenerator`, only when the
      app references MintPlayer.Spark.Search; text = string / string-collection properties only). MintPlayer's
      generated output reviewed (3 maps), app compiles. Index compile on the server is checked in M8.
- [x] Default provider: search, fuzzy, suggest (`RavenSearchProvider`, in the Search package).

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

- [x] `search` blocks on Artist, Person, Song (Person searches `FirstName`/`LastName`: `FullName` is computed);
      `Indexes/Subjects_Search.cs` deleted (nothing used it); `spark.AddSearch()` in `Program.cs`.
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

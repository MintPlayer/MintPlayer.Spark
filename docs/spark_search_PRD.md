# Spark search, OpenSearch description and search engines — PRD

Status: **draft, 2026-11-04, grilled.** Covers #238 (in-app search), browser search integration (the
OpenSearch *description* protocol) and pluggable search engines (RavenDB, the OpenSearch *engine*), with
mintplayer.com as first consumer. Plan: [spark_search_plan.md](spark_search_plan.md).

Two things share the name "OpenSearch". This document says **OpenSearch description** for the browser
protocol (`/opensearch.xml`, address-bar suggestions) and **OpenSearch engine** for the Apache-2.0
Elasticsearch fork.

## 1. What the investigation found (2026-11-04)

### 1.1 mintplayer.com has no browser search at all

Black-box check of production: no `<link rel="search">` in the server-rendered `<head>`; `/opensearch.xml`,
`/opensearch.osdx`, `/opensearch`, `/osdx`, `/search?q=queen`, `/api/suggest?q=queen` all return **404
`text/html`** (the SPA shell). In `apps/MintPlayer`: no package, no registration in `Program.cs`, no search
service, no `/search` route. `Indexes/Subjects_Search.cs` (multi-map Artist/Person/Song, `Text` with
`FieldIndexing.Search` + `Suggestion`, server default engine = Corax) describes "the search endpoint" in its doc
comment, but nothing queries it.

It was in scope and never done: `docs/mintplayer_website_PRD.md:152` (in scope), `docs/mintplayer_website_plan.md:70`
(step 8), and PRD `:99` decided "app `MapGet`s". **This PRD replaces `:99` for OpenSearch and search.**

### 1.2 `MintPlayer.AspNetCore.OpenSearch` is correct and usable

Source `C:\Repos\MintPlayer.AspNetCore.Tools\OpenSearch\` (`E` = `MintPlayer.AspNetCore.OpenSearch\Extensions\OpenSearch.cs`).
- Serves `/opensearch.xml` (E:114) as `application/opensearchdescription+xml` (E:17, E:162), absolute URLs
  (E:137), `?q={searchTerms}` templates (E:153-154), `rel="self"` (E:155), 1.1 namespace
  (`Data\OpenSearchDescription.cs:15`). Suggest writes `[query, [completions]]` (E:171-177); search redirects
  (E:193-200). The app supplies `IOpenSearchService` (`ProvideSuggestions`, `PerformSearch`, no `CancellationToken`).
- `net10.0;net11.0`, shared framework only; latest `11.0.1-rc.1`.
- `AddOpenSearch<T>()` calls `AddControllersWithViews()`, adds an XML formatter and sets
  `RespectBrowserAcceptHeader = true` (E:42-53). **Harmless, and stays** (owner, 2026-11-04): the formatter only
  writes `OpenSearchDescription` (`Formatters\XmlSerializerOutputFormatter.cs:30-31`), so a browser `Accept`
  falls through to `*/*` → JSON for everything else. MVC services in a minimal-API host cost startup/memory only.
- Gaps filed upstream as **MintPlayer/MintPlayer.AspNetCore.Tools#48**: configurable `SearchForm` (now always
  `{origin}/`, E:158, outside the namespace), `ShortName` ≤ 16 not enforced (E:126), no `CancellationToken`,
  no descriptions/urls suggestion arrays.

### 1.3 Spark has search inside one query, not across types

- Per-query search is pushed down: `QueryReadPath.cs:174,189` → `QueryExecutor.ExecuteQueryAsync(…, search)`
  (:324), `ApplySearch` after the security predicate (:1086, :2236-2260), in-memory fallback (:424).
  `[Search]` marks generated-index fields (`libs/attributes/.../SearchAttribute.cs:50`), without `Suggestion()`.
- No global search, no model flag, no ng-spark search page or route (`sparkRoutes()` = `query/`, `po/`, `**`).
- **Security lives on the query path**: `IsAllowedAsync("Query", type)` (`QueryReadPath.cs:97-98`), row filter
  (`QueryExecutor.cs:1065`), `RowSecurityGate.ApplyAsync` (`RowSecurityGate.cs:96-104`, filter + breadcrumb + redact).
  Querying any index directly skips all three.
- **Re-check by ids exists**: `IQueryExecutor.ExecuteQueryAsync(…, restrictToIds)` (`QueryExecutor.cs:20-27, :1089`)
  and `IDatabaseAccess.GetPersistentObjectsByIdAsync` (`DatabaseAccess.cs:125-174`); `SparkSelectionResolver.cs:85-110`
  uses both. The type's `Query` right must be checked separately (`QueryReadPath.cs:97`).
- Breadcrumbs: model JSON `"breadcrumb"` template (`{Attribute}` placeholders, reference tokens), synthesized and
  validated by the synchronizer; `[Breadcrumb]` marks the property it defaults to
  (`libs/attributes/.../BreadcrumbAttribute.cs`). Resolved by `IBreadcrumbResolver` (`BreadcrumbResolver.cs:81-93`)
  from entities, so an index hit needs the re-check to get one.
- Model: `EntityTypeDefinition.Seo` (`EntityTypeDefinition.cs:197`, class :260-287) is plain JSON; the
  synchronizer keeps hand-written blocks (`ModelSynchronizer.cs:785-800`); the schema is generated
  (`tools/SchemaGenerator`) and every App_Data file is validated against it (`SparkSchemaGeneratorTests.cs:45-63`).
  The client model is `ng-spark/models/src/entity-type.ts`.

### 1.4 Building blocks to reuse

- **Module template**: `libs/sitemap/MintPlayer.Spark.SitemapXml` (`AddSitemap()`, `builder.Registry.AddEndpoints`,
  generator endpoints because hand `MapGet`s failed `EndpointConventionsTests` R2/R3, CI run 41859101083).
- **Anonymous-visible rows + detail URL**: `SparkSitemapReader.cs:59-97` (anonymous scope over `IQueryReadPath`),
  `SparkSitemapXml.DetailUrl` (`SparkSitemapXml.cs:41`, `/po/{alias|id}/{id}/{slug}`), `ListQuery` (:31-38). All
  `internal` to the sitemap assembly today.
- **Rate limiting**: `spark.AddRateLimiter()` is opt-in and MintPlayer doesn't call it; modules limit themselves
  in-endpoint (`libs/mcp/.../Http/McpRateLimiter.cs:9-14`). No output caching anywhere.
- **SSR**: `ISparkPrerenderContributor` (`Abstractions/Prerendering/ISparkPrerenderContributor.cs:18-67`) runs the
  real endpoint in-process (needs `SparkPrerenderableAttribute`, e.g. `Endpoints/Culture/Get.cs:14-15`); the browser
  replays it from Angular's transfer cache (`provide-spark-hydration.ts:40-43`). Registered with `TryAddEnumerable`.
- **ng-spark**: secondary entry points per feature; feature routes are a separate function the app puts before
  `sparkRoutes()` (`sparkModerationRoutes()`, `moderation/src/provide-spark-moderation.ts:42-48`). Head `<link>`s are
  added like `setCanonical` (`spark-seo.service.ts:176-188`), SSR-safe.
- **Feature detection**: none generic; only `GET /spark/auth/capabilities`.

### 1.5 Engine, deployment and licences

- **Subscriptions**: production RavenDB is a Community licence, **3 subscriptions per database**; extra ones fail
  (`LicenseLimitException`). Spark messaging uses one subscription per queue name.
- **Fuzzy**: Corax throws `Method 'Fuzzy' is not supported`; fuzzy needs a per-index `SearchEngineType.Lucene`.
- **RavenDB Elasticsearch ETL** exists, but appears licence-gated off Community (unverified) — not used (D8).
- **OpenSearch engine**: Apache-2.0; .NET client `OpenSearch.Client` (TFM support for net11 to verify, S6).
- **mintplayer.com deploy**: `apps/MintPlayer/docker-compose.yml` (one service, `mem_limit: 1g`, pull-only from
  ghcr), `.github/workflows/mintplayer-deploy.yml` (build → ghcr → SSH `pull`/`down`/`up -d` → `/health` loop),
  secrets from the server-managed `/var/www/mintplayer/.env`, RavenDB on the shared internal network
  `mintplayer-raven`. VPS: 7.7 GB RAM, ~2.7 GB free, no swap (`mintplayer_website_PRD.md:230`), shared vCPU.
- **CI**: no `services:` containers and no Testcontainers yet; the run is at the 20-concurrent-job cap.

## 2. Goals

1. **G1 — Low code.** An app turns each feature on with one line, and declares *what* is searchable in the model.
2. **G2 — Composable.** In-app search, OpenSearch description and the sitemap are separate opt-ins over one
   model and one search abstraction; the engine is pluggable (RavenDB by default, OpenSearch engine opt-in).
3. **G3 — Secure by construction.** Every hit, label and disambiguation is computed from rows the caller may read.
   Hidden rows never surface, not even as a count or a disambiguation suffix.
4. **G4 — Address-bar quality.** Out-of-the-box suggestions, typo tolerance (fuzzy) in both engines, and a
   picked suggestion lands on its item, not on a results page.
5. **G5 — Spark conventions.** Modules in the registry, generator endpoints, indexes generated into the app.

## 3. Non-goals

- Cross-type relevance tuning beyond the engine's score.
- Elasticsearch proper and the RavenDB Elasticsearch ETL (engine = OpenSearch fork, sync = Spark-side).
- Backward compatibility with `Subjects_Search` or anything else (owner: "No backward compat").
- Changing MintPlayer.AspNetCore.Tools in this PR (issue #48 there).

## 4. Design

### 4.1 Packages and the app's lines

```
model: entity type "search" block (+ existing "breadcrumb", "seo")
                     │
MintPlayer.Spark.Search ── spark.AddSearch(...)          ← #238
  ISparkSearchProvider (Abstractions), security re-check, labels + disambiguation,
  /spark/search + /spark/search/suggest, RavenDB provider (default, generated Spark_Search index)
     │                                   │
     │  MintPlayer.Spark.Search.OpenSearch ── s.UseOpenSearch(...)   (engine provider + sync)
     │
MintPlayer.Spark.OpenSearchDescription ── spark.AddOpenSearchDescription(...)
  wraps MintPlayer.AspNetCore.OpenSearch: /opensearch.xml, suggest, search redirect

@mintplayer/ng-spark/search ── withSearch(), withOpenSearchDescription({ title }) for provideSpark(...),
                               sparkSearchRoutes(), <spark-search-box>
MintPlayer.Spark.SitemapXml (exists) ── shares the detail-URL builder and anonymous scope, moved to core
```

mintplayer.com after this work:

```csharp
spark.AddSearch(s => s.UseOpenSearch(builder.Configuration.GetSection("OpenSearch")));
spark.AddOpenSearchDescription(o => o.ShortName = "MintPlayer");
```
```ts
provideSpark(…, withOpenSearchDescription({ title: 'MintPlayer' })),   // requires → adds withSearch()
...sparkSearchRoutes(), ...sparkRoutes(),
```

The client uses the new `provideSpark(withX(), …)` form (#560, [ng_spark_provide_spark_PRD.md](ng_spark_provide_spark_PRD.md)),
delivered in the same PR. An app on the default engine drops the `UseOpenSearch` argument;
`AddOpenSearchDescription` calls `AddSearch()` when it wasn't. In-app search without the description protocol is
`AddSearch()` + `withSearch()`.

### 4.2 Decisions

**D1 — Searchable types are declared in the model** (owner, O1). An entity type's App_Data JSON gets a `search`
block, sibling of `seo`:

```json
"search": {
  "enabled": true,
  "label": "{Title} — {Artist}",      // optional; default: the type's "breadcrumb" template
  "fields": ["Title", "Lyrics"],      // optional; default: the attributes the label uses
  "fuzzy": true                       // optional; default: AddSearch option (true)
}
```

`label` uses the breadcrumb template syntax and validator (placeholders, reference tokens). New
`EntityTypeSearch` class next to `EntityTypeSeo`, same `JsonIgnore(WhenWritingNull)` pattern, regenerated schema,
`entity-type.ts`.

**D2 — `ISparkSearchProvider` returns candidates, core decides what is shown** (G3). Providers return
`(entityType, id, score)` candidates only. `SearchSecurityFilter` (core of `MintPlayer.Spark.Search`) groups by
type, checks `IsAllowedAsync("Query", type)`, then runs `IQueryExecutor.ExecuteQueryAsync(…, restrictToIds)`
(pattern `SparkSelectionResolver.cs:85-110`), which applies the row filter and `RowSecurityGate` and returns
breadcrumb + values. Providers over-fetch (×3, bounded) to fill `take` after filtering. Labels are rendered from
these secured, redacted rows, never from index-stored values, so a redacted attribute can't leak through a label.

**D3 — Labels are generic and unambiguous** (owner, O3 note: "we should be able to distinguish the strings").
The browser returns only the picked string as `{searchTerms}`, so each suggestion string must map back to one item:
1. Base label = `search.label` template, default the type's breadcrumb.
2. Two visible hits of **different types** with the same label → append ` (<type label>)` (translated singular).
3. Still equal within one type → append ` (<type label> <n>)`, `n` = 1-based position by document id among the
   **visible** exact-label matches of that type.
4. Collisions are counted over **all visible** rows with that exact label (stored `LabelExact` field + D2
   re-check), not only the returned page, so the same item always gets the same string, and hidden rows never
   change a suffix (no existence oracle).

`ResolveExactAsync(text)` inverts it: strip a recognised suffix, look up `LabelExact`, re-check, pick by type
and ordinal. Both the OpenSearch-description search endpoint and the in-app box use it.

**D4 — Search redirect is configurable, default straight to the item** (owner, O3).
`AddOpenSearchDescription(o => o.SingleHit = SingleHitBehavior.Item | SearchPage)`. With `Item`: an exact label
resolve (D3) → the item's detail URL; otherwise exactly one visible hit → that item; otherwise `/search?q=`.

**D5 — Default engine: a generated `Spark_Search` multi-map index in the app** (owner, O2-shape; G5; memory rule:
indexes always live in the application). A source generator emits one `Spark_Search` index into the app (OutputKind
Console/Windows, like `[GenerateIndex]`) over every type whose model has `search.enabled`:
- fields `Type`, `Label` (Search + `Suggestion`), `LabelExact` (exact), `Text` (Search over `search.fields`),
  `LastModified` (`@last-modified`, used by D8's poll);
- `SearchEngineType.Lucene` per index (fuzzy needs it; the rest of the database stays Corax);
- fuzzy: Lucene fuzzy terms when `search.fuzzy`; suggestions: prefix search on `Label` + `SuggestUsing` for typos.
The generator's input is the model JSON (spike S2 — if App_Data isn't reachable as `AdditionalFiles`, the `search`
block moves to a C# attribute and the model mirrors it). **`Subjects_Search` is deleted** (owner, O2).

**D6 — OpenSearch engine provider** (owner: engine = OpenSearch fork; naming: `MintPlayer.Spark.Search.OpenSearch`).
`s.UseOpenSearch(config)`; client `OpenSearch.Client`. One index per app (`{app}-search`, alias for zero-downtime
reindex), documents `{ type, id, label, labelExact (keyword), text }`, `label` as `search_as_you_type` for
suggestions, `multi_match` with `fuzziness: AUTO` when `search.fuzzy`. Index template created at startup. Same
D2 re-check, same D3 labels. **When the engine is unreachable, search falls back to the RavenDB provider** (owner,
O6): `Spark_Search` (D5) is always generated and deployed, also with `UseOpenSearch`. A circuit breaker (open after
a failed call or timeout, half-open probe every few seconds) switches providers, logs once per outage, and the app
stays healthy. Results look the same because labels and security come from core (D2, D3), not the provider. The
same index also serves the checkpointed poll (D8a), so the fallback costs no extra index.

**D7 — Fuzzy in both providers** (owner, O-fuzzy: "Both providers"). Default on, per type `search.fuzzy`, global
`AddSearch(s => s.Fuzzy = false)`.

**D8 — Sync to the OpenSearch engine is Spark-side: (a) checkpointed poll + hard deletes via (b)** (decided by
S4, §6.1; owner: "Spike both — we can easily ship a source-generator if this helps"). `Spark_Search` maps soft-deleted
rows too (`IsDeleted` field; searches filter on it) so the poll sees deletions.
- **(a) Checkpointed poll**: a hosted worker queries `Spark_Search` (D5) for `LastModified > checkpoint`, pushes a
  bulk, stores the checkpoint document. No subscription, durable, catches writes outside Spark. Deletes: a
  generated per-collection tombstone check or a reconcile pass (S4 measures which).
- **(b) Messaging queue `spark-search`**: after-save/delete hooks enqueue; costs one of the 3 subscriptions per
  database and misses writes outside the save pipeline.
- Either way: a full reindex command (admin action + CLI flag), and the worker only runs when `UseOpenSearch` is set.

**D9 — OpenSearch description = thin wrapper over the library** (owner; package `MintPlayer.Spark.OpenSearchDescription`).
`AddOpenSearchDescription()` → `AddOpenSearch<SparkOpenSearchService>()` with options mapped (`ShortName` truncated
to 16, `ImageUrl` defaulting to the favicon), `builder.Registry.AddEndpoints(e => e.MapOpenSearch())` (needs an
`EndpointConventionsTests` R3 exception, like `MapDefaultSitemapXmlStylesheet()`). `SparkOpenSearchService`
delegates to the search service: suggest → D3 labels, search → D4. `RespectBrowserAcceptHeader` untouched.

**D10 — Endpoints.** Search module: `GET /spark/search?q=&take=` (JSON hits grouped by type,
`SparkPrerenderable`), `GET /spark/search/suggest?q=` (labels + detail URLs for the in-app box). Description:
`/opensearch.xml`, `/spark/opensearch/suggest`, `/spark/opensearch/search`. All anonymous GETs; minimum term
length 2; an in-endpoint per-IP limiter (McpRateLimiter pattern), because `AddRateLimiter` is opt-in. Route
snapshot and `CsrfSurfaceTests` updated.

**D11 — Client: `@mintplayer/ng-spark/search`.** Features for `provideSpark(...)` (#560): `withSearch()`, and
`withOpenSearchDescription({ title })` which adds the `<link rel="search">` (SSR-safe, `setCanonical` pattern) and
`requires` `withSearch()`. Plus `sparkSearchRoutes()` (`/search?q=` → `SparkSearchPageComponent`, results grouped
by type, `noindex`, server-rendered through a prerender contributor matching `/search`) and `<spark-search-box>`
(debounced suggest, keyboard navigation, picks go to the item).

**D12 — Sitemap shares, doesn't depend.** `DetailUrl`, `ListQuery` and the anonymous-scope helper move from
`SparkSitemapXml` (internal) to core so search and sitemap use one implementation. `/search` is not in the sitemap.

**D13 — mintplayer.com engine deployment** (owner: service in the app compose).
- `apps/MintPlayer/opensearch/Dockerfile`: `FROM opensearchproject/opensearch:<pinned>` + the `analysis-icu`
  plugin (accent/case folding: "Beyonce" finds "Beyoncé"). Built by `mintplayer-deploy.yml`, pushed to
  `ghcr.io/mintplayer/mintplayer-opensearch`, pulled like the app image.
- Service `mintplayer-search` in `apps/MintPlayer/docker-compose.yml`: single-node, `OPENSEARCH_JAVA_OPTS=-Xms512m
  -Xmx512m`, `mem_limit: 1g`, named volume, new internal network `mintplayer-search` joined only by the app, no
  published ports. Restarted by each deploy (`down`/`up`); the app tolerates it (D6) and the `/health` loop does
  not wait on it.
- **Security plugin disabled** (`DISABLE_SECURITY_PLUGIN=true`, owner, O5): protection is the internal-only
  network with no published ports; no certs or password, smaller footprint. Revisit if the network is ever shared.
  The host needs `vm.max_map_count=262144` once (owner runs it on the VPS, S7).

**D14 — Tests.** Search core (re-check, labels, disambiguation without oracle, exact resolve) against RavenDB in
`MintPlayer.Spark.Tests`; generator tests (`outputKind: ConsoleApplication`); OpenSearch-engine provider
integration tests against a `services:` container in an **existing** CI job (20-job cap); description wrapper
(XML, ShortName cap, suggest shape, redirect); ng-spark unit tests; one E2E for `/search?q=` SSR.

## 5. Open decisions

1. ~~O1 opt-in~~ — model JSON `search` block (owner).
2. ~~O2 `Subjects_Search`~~ — delete (owner).
3. ~~O3 single hit~~ — configurable, default to the item; strings distinct (owner).
4. ~~O4 link source~~ — a client feature (owner); now `withOpenSearchDescription()` under #560.
5. ~~O5 engine security plugin~~ — disabled, network isolation (owner, D13).
6. ~~O6 engine down~~ — fall back to the RavenDB provider (owner, D6).
7. ~~Scope~~ — one PR incl. the OpenSearch engine provider, MintPlayer setup, Dockerfile and fuzzy (owner).

## 6. Spikes

- **S1** — `AddOpenSearch` + `MapOpenSearch` in a Spark host's module registry: mapped by `MapSpark()`, no clash
  with the SSR fallback, exact R3 exception.
- **S2** — can a Spark source generator read App_Data model JSON (`AdditionalFiles` via the Spark targets) to emit
  `Spark_Search`? Else fall back to a C# attribute as the source of truth.
- **S3** — RavenDB provider on MintPlayer data: per-index Lucene + fuzzy + `SuggestUsing` + prefix; latency of one
  suggest round trip including D2 re-check and D3 collision counting.
- **S4** — sync: (a) checkpointed poll vs (b) messaging queue — latency, delete handling, MintPlayer's current
  subscription count, generator help. Pick one.
- **S5** — D3 disambiguation: collision query cost, ordinal stability when items are added/removed, suffix parsing
  with labels that themselves end in `(…)`.
- **S6** — `OpenSearch.Client` on net11, server version to pin, footprint at 512 MB heap with MintPlayer's corpus,
  `search_as_you_type` + fuzzy quality on real titles.
- **S7** — VPS: `vm.max_map_count`, free RAM with the engine up (owner runs the commands).
- **S8** — prerendering `/search?q=` through a contributor and the transfer cache (no double request).
- **S9** — CI: OpenSearch `services:` container inside an existing job; start-up time.

### 6.1 Spike results (2026-11-04)

- **S1 — description library in the registry: works, no R3 exception needed.** `EndpointConventionsTests` R3 flags
  only the method names in `MappingMethods` (`Map`, `MapGet`, …, `EndpointConventionsTests.cs:173-178`);
  `MapOpenSearch` is not one, exactly like SitemapXml's `MapDefaultSitemapXmlStylesheet()` today
  (`SparkSitemapExtensions.cs:33`). `AddOpenSearch<T>()` is an `IServiceCollection` extension, so it runs on
  `builder.Services`; `builder.Registry.AddEndpoints(e => e.MapOpenSearch())` is mapped by `MapSpark()`
  (`Program.cs:137`), before `UseSpaImproved` (:142), so the SSR fallback can't shadow it.
- **S4 — sync: (a) checkpointed poll as the backbone, hard deletes through a `spark-search` message.**
  - Messaging already runs on **one** subscription for all queues (`MessageFeeder.SubscriptionNameConstant =
    "SparkMessaging"`, `MessageFeeder.cs:36`; the local MintPlayer database has exactly that one subscription), so
    a `spark-search` queue costs no subscription. The Community cap is not the deciding factor any more.
  - Deciding factor: completeness. Only the poll sees writes outside Spark's save pipeline (migrations, imports,
    fixtures, `SparkMigrationRecords` show MintPlayer uses migrations). It reads `Spark_Search` by `LastModified`,
    which D5 deploys anyway.
  - The poll can't see hard deletes (tombstones aren't in an index). Soft deletes (MintPlayer uses `AddSoftDelete`)
    are modifications: `Spark_Search` therefore maps deleted rows too, with an `IsDeleted` field, instead of
    filtering them out, and the poll removes them from the engine. Hard deletes: Spark's delete pipeline sends a
    `spark-search` message; a full reindex reconciles anything missed.
- **S6 — engine and client.** `OpenSearch.Client` 2.3.0 (Apache-2.0) targets netstandard2.0/2.1, so it runs on
  net11; its compatibility table says 2.0.0+ supports OpenSearch 3.x (`opensearch-net/COMPATIBILITY.md:8-12`).
  Pin `opensearchproject/opensearch:3.9.0` (latest, 2026-11-04). Footprint and suggestion quality: **not measured
  locally** (Docker Desktop not running on this machine); measured in CI (S9) and after deploy.
- **S3 — corpus size.** Local copy of MintPlayer: 145 Artists, 141 Songs, 15 People (1194 documents in total). At this
  size latency is dominated by round trips, not by the index: one suggest = one `Spark_Search` query + one
  re-check query per type with hits (≤ 3). No measurement needed to choose D5; the circuit-breaker fallback (D6)
  rides on the same numbers.
- **S2 — generator reading the model: feasible as-is, no targets change.** `spark.targets:157-159` already adds
  `$(SparkAppDataDir)\Model\*.json` as `AdditionalFiles` (default on, `spark.props:29`; `CompilerVisibleProperty
  SparkAppDataDir`, `spark.targets:192`), and library model layers arrive with `SparkLayerPath` metadata
  (`SourceGenerators/build/…targets:31,47`). `PersistentObjectNamesGenerator.cs:143-175` already reads them and
  composes layers (`ComposedModel.Compose`). Model JSON names the CLR type (`"clrType": "MintPlayer.Entities.Song"`,
  `Song.json:6`) and attribute names equal property names, so the generator resolves `clrType` with
  `GetTypeByMetadataName` and emits typed `AddMap<Song>(...)`. Base class `SparkMultiMapIndexCreationTask`
  (`SparkIndexCreationTask.cs:96`, SPARK018-exempt). Deployment is automatic (`IndexCreation.CreateIndexes` per
  assembly, `SparkMiddleware.cs:943-961`). No precedent for a per-index engine: set
  `Configuration["Indexing.Static.SearchEngineType"] = "Lucene"` in the constructor. `ModelJsonReader.TryRead`
  (`ModelJsonReader.cs:39-78`) needs extending for `clrType` + `search` with the same identifier validation, since
  the JSON is untrusted input to generated code. Gate: OutputKind Console/Windows like `[GenerateIndex]`
  (`GenerateIndexGenerator.cs:164-167`); unresolved `clrType` → diagnostic.
- **S5 — disambiguation, decided by design.** Resolving a picked string first tries it as an exact `LabelExact`;
  only if that finds nothing does it strip a trailing ` (<type label>)` / ` (<type label> <n>)` whose type label is a
  searchable type's, so a title like "Help! (Remastered)" still resolves. Ordinal = position in natural id order
  among visible exact matches: adding an item can only append, never renumber, unless ids are reused.
- **S7 — VPS**: owner runs `sysctl vm.max_map_count` and `free -m` before the first deploy (plan M10).
- **S8, S9** — answered while implementing M2 (prerender contributor) and M8 (CI job); results logged in §10.

## 7. Acceptance

- mintplayer.com: `/opensearch.xml` 200 `application/opensearchdescription+xml`; typing in the address bar shows
  suggestions (with typos: "beatels" → The Beatles); picking one lands on the item; Enter on free text lands on
  `/search?q=` server-rendered; SeoTester's OpenSearch check passes.
- Two visible songs called "Yesterday" get distinct strings; a hidden third one changes nothing.
- A type anonymous users can't `Query`, and row-filtered rows, never appear in anonymous results (test).
- App code: the lines in §4.1 plus `search` blocks; `Subjects_Search` gone.
- `EndpointConventionsTests` green with only the planned R3 exception; route snapshot, `CsrfSurfaceTests` updated.
- Engine stopped → app healthy, suggestions and search served by the RavenDB provider with the same labels;
  engine back → circuit closes, sync catches up.

## 8. Decision log

| # | Decision | Evidence |
|---|---|---|
| — | Replaces `mintplayer_website_PRD.md:99` for OpenSearch/search | §1.1 |
| D1 | Model `search` block | owner, 2026-11-04 (O1) |
| D2 | Providers return candidates; core re-checks via `restrictToIds` | `QueryReadPath.cs:97`, `QueryExecutor.cs:1065,1089`, `RowSecurityGate.cs:96-104`, `SparkSelectionResolver.cs:85-110` |
| D3 | Generic labels from breadcrumb, auto-disambiguation over visible rows | owner (O3 note, "make it generic… BreadcrumbAttribute… auto-disambig"); oracle rule: memory "private-repo existence must not leak" |
| D4 | SingleHit configurable, default Item | owner (O3) |
| D5 | Generated `Spark_Search`, Lucene, `Subjects_Search` deleted | owner (O2, shape); Corax has no fuzzy; indexes live in the app (owner rule 2026-10-04) |
| D6 | Engine = OpenSearch fork, `MintPlayer.Spark.Search.OpenSearch` | owner (engine, naming) |
| D7 | Fuzzy in both providers | owner |
| D8 | Spark-side sync, (a) vs (b) by S4 | owner; Community 3-subscription cap |
| D9 | Description wraps the library (option B), `RespectBrowserAcceptHeader` stays, name `OpenSearchDescription` | owner, 2026-11-04; `XmlSerializerOutputFormatter.cs:30-31` |
| D13 | Engine as a service in the app compose, own image, security plugin disabled | owner (container placement, "its dockerfile"; O5) |
| D6 | Engine down → RavenDB provider fallback via circuit breaker; `Spark_Search` always deployed | owner (O6) |
| — | Upstream gaps | MintPlayer/MintPlayer.AspNetCore.Tools#48 |
| — | One PR incl. engine provider + MintPlayer setup | owner (scope) |
| D11 | Client features `withSearch()` / `withOpenSearchDescription()`; `provideSpark(withX())` (#560) in this PR | owner, 2026-11-04 |

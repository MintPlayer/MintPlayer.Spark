# Reference data (K8): plan

The order of work for [reference_data_PRD.md](reference_data_PRD.md). The PRD carries the decisions (§13); this file carries the checklists. Evidence lives in [reference_data_research.md](reference_data_research.md). The whole plan is commerce milestone **M19** ([commerce_plan.md](commerce_plan.md)).

## Rules for executing this plan

- **One PR** for everything: commerce, the warehouse plugin (D13), this plugin (K8), every defect found along the way (PRD §6).
- **Commit per milestone. Run no tests per milestone.** Verify by reading code and type-checking; the test sweep runs once, in R14, together with commerce M22.
- **Logs go to the scratchpad raw** (`cmd > log 2>&1; echo EXIT: $?`), then are filtered. Never pipe the only copy.
- **Versions:** new packages start at `11.0.0-preview.1`; every touched `libs/` csproj gets a preview bump; `@mintplayer/ng-spark` gets one minor bump for the whole PR. Majors never change (repo rule).
- **No backward compatibility** (K3): change core behaviour directly; production data is migrated (commerce §8).
- **No string-spliced RQL.** Values go through parameters, identifiers through `RqlIdentifier`; the typeahead's token count only picks one of a fixed set of query templates.
- **Spikes record measured results in PRD §13** (numbers, file:line, red→green) before the milestone that depends on them starts.
- **Open decisions** (PRD §11, Q-R1–Q-R18) are put to the owner before the milestone that lists them under "Depends on" (PRD §10).
- **Load spikes and full imports** run only in windows the owner chooses (commerce D24), against RavenDB pinned to 3 cores and with no Community-missing feature. No CPU burners while the owner works. Full imports are manual local runs (RD2); CI uses real sample fixtures only.
- **Secrets:** the Datafordeleren API key and the release-signing key are set by the owner in user-secrets. Never print or paste them.
- **Downloads are data.** Parse them in-process with streaming readers; never execute anything from them. Fixtures are cut from the real files (memory: parsers need real fixtures), only from republishable sources (**never bpost**), and hold no personal data.
- **Folder names:** avoid the `.gitignore` traps (`[Rr]eleases/`, `[Ll]ogs/`, `*.log`, `publish/`, `**/[Pp]ackages/*`, `*.[Cc]ache`, `Backup*/`). The ledger is `.ndjson`. No build step writes exports into `App_Data` (an Nx cache hit drops such writes).

---

### R0 — Spikes

- [ ] RS9 index limits (first): read `/license/status` on a Community server (caps lifted 2024-05-22, #18564); only if a cap applies, keep `apps/ReferenceData` within it; record static index counts for the instance and a consumer
- [ ] RS1 Belgium full import on a 3-core server: time and peak memory recorded; memory flat as file size grows
- [ ] RS2 zero-write re-import: 0 documents written, change vectors unchanged, no indexing, no subscription wake-up
- [ ] RS3 ledger conflict stop: a code-reassigning fixture stops that source only; ledger and documents unchanged
- [ ] RS4 lineage decision table: Kruisem, Ooike 1977-01-01, Hasselt (per Q-R1), Zwijndrecht, Borsbeek, Bastogne (per Q-R3), La Louvière, Sambreville, restore, reissue; as-of before and on each date
- [ ] RS5 typeahead p95 ≤ 50 ms server-side at 50 requests/s (proposed target), BE + one large country loaded
- [ ] RS6 fuzzy fallback: misspelling set in the top 3; fuzzy only below *k* prefix hits; multi-token fuzzy composition settled; same order on two databases with different country subsets; X18 sets Lucene with no engine code in the app
- [ ] RS7 release delta exactness: delta(A→B) on A gives B's digests; an id-dropping release refused; a digest mismatch re-imports that collection
- [ ] RS8 per-adapter fixtures: all 18 adapters + the global baseline parse real fixtures (cp1252, BOMs, Excel-serial dates, Lambert 72/2008, combined NIS6 parts, ZIP64 where relevant, dated file names found by pattern); AT, SI, EE and NO postcode linkage verified
- [ ] RS10 determinism: two builds give identical manifests; Windows and Linux give identical keys and bytes
- [ ] RS11 Wikidata extraction: per-country SPARQL vs filtered dump; time, size, reproducibility
- [ ] RS12 query shapes on 7.2.x and 7.1.10: `startsWith` on arrays, `order by Rank`, Lucene fuzzy per token
- [ ] RS13 consumer sync on Community: a monthly delta in under a minute; startup not blocked; re-run writes 0
- [ ] RS14 Belgian postal localities vs bpost's 2,720 geographic rows: manual and local, every gap explained, nothing committed
- [ ] RS15 BeST house-number validator: latency recorded; timeout accepts + flags "unverified"; a recorded real response as fixture
- [ ] RS16 bulk writes vs live updates: re-query storms measured; FR8 confirmed red or closed
- [ ] RS17 Ed25519 signing: BCL or vetted library (licence recorded); tampered manifest or file refused
- [ ] RS18 public API hardening: cross-origin 429 with `Retry-After`; one /64 = one bucket; 304 without a query
- [ ] RS19 large-country import (FR or US streets): time, memory, database and index size recorded
- [ ] RS20 time-zone assignment (Q-R10) on US, AU, ES and FR fixtures: right zone or null with a reason; tz database validation
- [ ] RS21 machine transliteration for keys (Cyrillic, Greek, Armenian, Georgian): ICU P/Invoke vs AnyAscii vs own tables; identical on Windows and Linux; pinned in `KeyVersion`
- [ ] RS22 1977 lineage without curation: share of the 1,832 ending codes that get an event from REFNIS + NIS6; every unresolved one listed

### R1 — Framework prerequisites and defects

- [ ] Commerce M1 items this plan needs have landed: X3 (natural-id race, F6), X10 (IPv6 /64, F5), X15 (single-`AsDetail` edit renderer + `noteChange`), X16 + D36 (`ISparkTimeZoneNames`), X17 (`TranslatedString` redesign incl. sanitized language index fields, RFC 4647 chain, any-language factory, `culture.json` tag warning; fixes FR2, FR3), X18 (automatic Lucene for fuzzy-marked indexes)
- [ ] RX1 CORS-readable 429s with `Retry-After` (`RateLimiterOptions.OnRejected`) (FR4, red test first)
- [ ] RX2 the route-table snapshot records skip-XSRF per route
- [ ] RX3 anonymous endpoints visible in review: posture listing or an exact-set anonymous-surface helper in `MintPlayer.Spark.Testing`, adopted by every app (FR5)
- [ ] RX4 library-declared index definitions emitted into the host app by a generator (only if Q-R6 is decided that way)
- [ ] RX5 `bs-typeahead`: debounce, `AbortSignal` cancellation, item template, ControlValueAccessor, in mintplayer-ng-bootstrap (published before ng-spark consumes it)
- [ ] FR1 SeoTester catalogue, badge, bot and legal endpoints: red test proving `Cache-Control: private`, then `.SkipXsrfToken()` (production: PRD §8)
- [ ] FR8 closed or fixed per RS16
- [ ] FR9 documentation drift fixed (the six items in PRD §6)
- [ ] K12: the source-generator study's recommendations folded into PRD §5 and this plan

### R2 — Abstractions

- [ ] Q-R7 (where `Address` lives) and Q-R14 (aliases, entry-point names) answered
- [ ] `libs/refdata/abstractions` → `MintPlayer.Spark.ReferenceData.Abstractions`: plain SDK (SPARKLIB001), references the generator (for `[ValueObject]` keys), in the slnx
- [ ] Document types: `RefCountry` (with the country profile), `RefPlace`, `RefPostalCode`, `RefStreet`, `RefLineageEvent`, `RefRelease`, `refdata/state`; names as X17 `TranslatedString`
- [ ] `Address` value (`[ValueObject] partial`) with ids, rendered `Lines`, `TimeZone`, `RefData { Release, Verified, HouseNumberVerified }`
- [ ] API DTOs (suggest, streets, place, resolve, by-code, postal code, profile, time zones, sources)
- [ ] Release-format contract: JSON schemas + fixtures, shared by the instance, the .NET client and the TS client
- [ ] Id grammar validator (every segment checked; no trailing `/` or `|`), shared by instance and client

### R3 — Core engine

- [ ] Q-R1–Q-R5 and Q-R11–Q-R13 answered
- [ ] `libs/refdata/core` → `MintPlayer.Spark.ReferenceData`: `spark.AddReferenceData()` binding `Spark:ReferenceData`, idempotent; library layer under its alias; reference types read-only and non-live
- [ ] Id ledger: append-only `ledger/{cc}.ndjson` (location per Q-R5); mint once; a conflict stops that source with a visible conflict record
- [ ] Lineage engine: event kinds (merge, absorb, demote, split, cede, rename, recode, reparent, restore), per-place operations, derived `Supersedes`/`SupersededBy` with `whole`/`part`; unresolved lineage per Q-R4
- [ ] Derived fields: `Lifetime`, `Segments` cut at every ancestor change, ancestor chains, integer date keys (0 / 99991231), `Rank`, `Hash` (canonical, without `Hash` and `Release`)
- [ ] As-of resolution (`Resolve`, `Ambiguous` on `part` edges)
- [ ] Search-key pipeline (startup normalization assert; NFKC; punctuation; case folding; script routing; Latin folding; tokens; compact; article/elision; abbreviation classes); `KeyVersion`
- [ ] Display rule on the server (local → ui → secondary → Latin → fallback), Q-R11 and Q-R13 applied
- [ ] Decision-table tests written, one per row (they run in R14)

### R4 — Sync pipeline (RD1)

- [ ] Source registry fed by the adapter packages; options `Spark:ReferenceData:Sources:{id}:Schedule` (6-field cron, UTC) plus per-source settings; keys only from user-secrets
- [ ] One cron job per configured source; the job only queues
- [ ] Change check: `ETag`/`If-None-Match`, `Last-Modified`/`If-Modified-Since`, a published checksum, else SHA-256 of the download; "no change" runs recorded
- [ ] `refdata-import` queue with a long `HandlerTimeout`; `BroadcastOnceAsync(ImportSource, "{source}/{version}")`; the admin "Import now" custom action queues the same message
- [ ] Downloads: a named client with a host allow-list, byte and time caps, to a temporary file
- [ ] Checkpointed recipient: partitions, cursor and counts in `ImportRuns/{source}/{version}`; resumes after a restart
- [ ] Diff by source id + source version + content hash; BulkInsert for a first load (explicit ids, `SkipOverwriteIfUnchanged`); chunked optimistic updates afterwards
- [ ] Retire, never delete: `absentSince`; refuse a sweep with an implausible retired share
- [ ] Derive pass for affected documents (lineage → segments → keys → rank → hash), descendants re-cut on ancestor changes
- [ ] Admin pages through the generic Spark UI: import runs, conflicts, sources
- [ ] Nothing imports inside a migration (a migration may at most queue a first run)

### R5 — Releases (RD6)

- [ ] Q-R8 (release storage) and Q-R17 (granularity) answered
- [ ] Canonical NDJSON writer (UTF-8 without BOM, LF, sorted by id ordinal, fixed property order, every property written, NFC, integers, 7-decimal coordinates, `yyyy-MM-dd`, sorted set-like arrays, fixed `@metadata`, no timestamps)
- [ ] Full files per country and collection; deltas from earlier releases; the manifest (sources with licence and attribution, files with SHA-256, count and digest, `KeyVersion`, builder version, next effective date)
- [ ] Optional Ed25519 signing with the RS17 choice; key from user-secrets
- [ ] `IReleaseStore` (Q-R8) and `/api/v1/releases/*` with immutable caching
- [ ] Verify command: recompute (id, Hash) digests and compare with the manifest
- [ ] Importers refuse a release that drops an id
- [ ] Reproducibility test: build twice, compare manifests

### R6 — Belgium adapter in depth

- [ ] Q-R16 (Walloon parts of municipality) answered
- [ ] `Sources.Belgium` package; config ids for each source (BeST, postal streets, REFNIS, NIS6, Flemish diff files, Flemish API)
- [ ] BeST change check: `ETag`, central directory by range request, inner-entry CRC; inner files matched by pattern (Lambert 2008 safe); address entries never fetched
- [ ] BeST parsing: municipalities; postal info (Flanders' slash-joined names, main municipality in capitals); streets (nl/fr/de, homonym qualifier, `streetname`/`hamlet`, statuses); Walloon parts (Q-R16); namespace normalization; region-prefixed street ids
- [ ] Postal streets: street ↔ postcode links; Walloon and Brussels postal localities; part of municipality → municipality
- [ ] REFNIS (cp1252, pipes, CRLF; edition found by pattern): every unit since 1970, periods, nl/fr/de names (level prefixes stripped, disambiguators kept as qualifiers)
- [ ] NIS6 editions: sections; a code change between editions keeps the id
- [ ] Flemish 2025 diff files: municipality, street and address mappings; street continuity per Q-R2
- [ ] Flemish API: retired municipalities and streets; `officieleTalen` / `faciliteitenTalen`
- [ ] Lineage without curation: 2019/2025 merges (same-day end/start + NIS6 prefixes + diff files), 2019 Hainaut recodes, 1977 demotions (REFNIS ends + NIS6 names within the arrondissement), unresolved per Q-R4, date precedence per Q-R3 (Statbel primary)
- [ ] Country profile: region, province (optional: none in Brussels), arrondissement, municipality, district (Antwerpen), section; labels nl/fr/de/en; address template; postcode regex; house-number rules (number, letter, box); typeahead steps; history known since 1970
- [ ] Credit lines: the BeST providers via FOD BOSA, "Source: Statbel" + licence link + changes, "Bron: Digitaal Vlaanderen"
- [ ] Real fixtures cut from BeST, postal streets, REFNIS, NIS6 and the diff files (nothing from bpost)
- [ ] RS14 cross-check against bpost run locally only

### R7 — Global baseline

- [ ] Q-R10 (time-zone assignment) answered
- [ ] Wikidata extractor (method per RS11), each extract pinned by SHA-256
- [ ] GeoNames dump, alternate names and postal codes (truncations respected; the GB Royal Mail notice); daily modification and deletion files
- [ ] NGA GNS romanized names; UN/LOCODE
- [ ] CLDR: territory, language, script and currency names; time-zone exemplar cities; the release version as change check; the licence notice
- [ ] IANA Language Subtag Registry (its `File-Date` as change check); ISO 3166-1 region codes
- [ ] Time-zone assignment per Q-R10, validated against the tz database
- [ ] Tier-2 country profiles: generic levels from GeoNames ADM1–ADM3, a generic address template, history known since from Wikidata's dated statements
- [ ] Machine transliteration for search keys (RS21 choice), true alphabets only, pinned in `KeyVersion`

### R8 — The 17 tier-1 adapters

Each package: change check, streaming parser, ids and history per PRD §4.4, country profile, credit line, real fixtures.

- [ ] NL: monthly free extract via the PDOK Atom feed (paid mutations unused); CBS herindelingen since 1991
- [ ] FR: BAN files (no api-adresse); COG history since 1943; INSEE code reuse → `@date` suffix; BAN toponym UUIDs, never `cle_interop`
- [ ] LU: dated download folder found through the dataset API; history known since the first import
- [ ] CH: BFS mutations and historicization numbers; ESID/EGAID; "©swisstopo" in every response and in the service metadata
- [ ] LI: swisstopo's LI directory; FOSNR; attribution as CH
- [ ] AT: the semi-annual BEV files (no paid API); Statistik Austria change file
- [ ] DK: Datafordeleren file downloads; API key from user-secrets (required from 2027-01-15); credit switches to "DMI, Kort og Grunddata" on 2026-12-01
- [ ] NO: (code, validFrom) keys; Sami and Kven names split; Bring postcodes (NLOD 2.0); files, not the capped API
- [ ] FI: kunta since 1970; SYKE/Ryhti postcodes unless Posti confirms in writing
- [ ] EE: ADS extract 9 and EHAK; the foreign-IP download limit documented; postcode licence verified first
- [ ] LV: VZD daily CSVs with `*_his` tables; `ATRIB` read per table
- [ ] ES: INE codes, Callejero and CartoCiudad; IGN formula in the service metadata and every ES response; CartoCiudad postcodes
- [ ] PL: TERYT and PRG; postcodes flagged as not validated by the post; retrieval time in the credit
- [ ] CZ: RÚIAN monthly files + daily changes; "ČÚZK, [rok]"
- [ ] SI: WFS or app export; postcode linkage verified first; EID ids
- [ ] US: TIGER/Line ADDRFEAT; ZIPs flagged non-authoritative; county changes since 1970
- [ ] AU: G-NAF quarterly + Geoscape boundaries + ASGS; the mail clause in our API terms; EULA text verified
- [ ] RS19 large-country import as a manual local run

### R9 — Search and the public API

- [ ] Q-R6 (index placement) and Q-R15 (API keys) answered
- [ ] Indexes per PRD §4.10: the two typeahead indexes on Lucene through X18, the others with Corax pinned; placed per Q-R6; within any cap RS9 finds
- [ ] Two-step typeahead: prefix tiers as lazy queries in one round trip → fuzzy fallback below *k* → C# re-ranking (tier, edit distance, `Rank`, id)
- [ ] Digits-only queries on postal codes; the as-of filter on integer keys; `within` and `kinds` validated; fixed templates only
- [ ] `/api/v1/*` endpoints per PRD §4.11 (generator endpoints in one group), including `/api/v1/timezones` and `/api/v1/sources`
- [ ] `group.AllowAnonymous()`; never 401; an empty 200 on no match; no anonymous Spark rights on reference types
- [ ] Named CORS policy on the group only; `.SkipXsrfToken()`
- [ ] ETag (release + normalized request incl. effective asOf) with 304 and no query; `Cache-Control: public`
- [ ] Rate-limit policy on the group (IPv6 /64 via X10; 429 per RX1); `/api` kept out of `PathPrefixes`
- [ ] Caps: `q` length and token count, `limit`, a short query timeout, `RequestAborted`
- [ ] Attribution array in every response; API terms with no extra restrictions and the G-NAF clause
- [ ] Aggregate-only request logging

### R10 — Client packages

- [ ] Q-R9 (release notifications) and Q-R18 (server-side CLDR names) answered
- [ ] `libs/refdata/client` → `MintPlayer.Spark.ReferenceData.Client`: `spark.AddReferenceDataClient()`; `BaseAddress` absolute http(s), no default (unset = free-text mode)
- [ ] Release sync service (RD6): cron + conditional GET on the manifest; deltas or full files; signature and hash checks; refuse dropped ids; `refdata/state` last; periodic per-collection checksum; startup never blocked
- [ ] Read-only local types (countries, places, postal codes) in the client's library layer
- [ ] Typed `IReferenceDataClient`
- [ ] `Address` behaviour: an untyped `IBeforeSave` interceptor throwing `SparkValidationException` (shape-only in free-text mode); lines rendered from the profile template
- [ ] `IHouseNumberValidator` + the BeST implementation (timeout; accept and flag "unverified")
- [ ] `ISparkTimeZoneNames` provider (D36 step 1), cached per (zone, language)
- [ ] Server-side CLDR names per Q-R18
- [ ] `@mintplayer/ng-spark/reference-data`: config token without default; `HttpClient` from `HttpBackend`; typeahead service (debounce, `switchMap`); `<spark-address-input>` ControlValueAccessor; the embedded ISO 3166-1 list with `Intl.DisplayNames` + `Intl.Collator`; profile-driven steps; free text always allowed
- [ ] `@mintplayer/ng-spark/reference-data/spark`: `sparkAddressRenderers` (needs X15)
- [ ] TypeScript display-rule twin, checked against the server with shared fixtures

### R11 — Demo app

- [ ] `apps/ReferenceData` (host + ClientApp, `ReferenceData.Library`, `ReferenceData.Tests`); https 5017 / http 5018; `/health`
- [ ] Admin sign-in with a local password; an Administrators group holding the import actions
- [ ] Consumer page: the address form against the instance itself, a free-text toggle, the sync status
- [ ] Database `RefData` on localhost:8080 in development; tests on the embedded server
- [ ] New-app checklist: `package.json` workspaces; slnx folder; `pull-request.yml` build list and the verify-model / verify-security loops; `regenerate.yml`; `tools/verify-ngsw-config.test.mjs` (`/api`, `/health` excluded both ways; app added to the list); SPA fallback excluding `/api`; `test.yml` matrix if the suite is long; `tools/test-local.mjs` `E2E_APPS` if E2E hosts the app
- [ ] `securityPosture.txt` and route-table snapshot baselines; anonymous-surface check (RX3)
- [ ] E2E flows of PRD §14
- [ ] `git status` confirms the fixtures and the ledger are tracked (no `.gitignore` trap)

### R12 — Commerce integration

- [ ] `Address` from the client package on venues, seller profiles, orders and shipping addresses; invoices keep `Lines` as the legal snapshot
- [ ] The commerce demo points at a host-configured endpoint (`null` = free text)
- [ ] Venue time zone taken from the address's place (D33 hint)
- [ ] Ended places show their successor with a "changed on" hint on venues and seller profiles

### R13 — Documentation

- [ ] `docs/guide-reference-data.md` (running an instance) and `docs/guide-reference-data-client.md` (consumers); a README per package
- [ ] Release-format specification: schemas, canonical rules, deltas, signing
- [ ] Sources and attribution page; API terms
- [ ] Per-adapter notes: schedules, keys, catches
- [ ] A line for `apps/ReferenceData` (local only, https 5017) in the repo instructions

### R14 — Sweep, verification, release

- [ ] `npm run test:affected` with the Developer licence (raw log), together with commerce M22; fix and re-run single failing classes only
- [ ] Decision-table, determinism, zero-write, delta and fixture tests green
- [ ] Browser verification through the Playwright MCP tools (address form, import runs, sources page)
- [ ] Version bumps: new packages `11.0.0-preview.1`; touched csproj files bumped; one ng-spark minor
- [ ] PRD §0 and §13 updated with spike results and the answers to Q-R1–Q-R18

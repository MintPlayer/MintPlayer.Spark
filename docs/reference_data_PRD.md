# Reference data (K8): PRD and plan

**Status:** research and grilling rounds 1–2 complete (2026-10-17); round 3 (owner, 2026-10-19): developed on this branch with a basic seed set (RD9 = commerce D39), search fuzzy by default (RD10 = commerce D38). Spikes not started.
**Issue:** part of [#521](https://github.com/MintPlayer/MintPlayer.Spark/issues/521) (owner scope K8), in the same PR as commerce and the warehouse plugin.
**Companions:**
- [reference_data_research.md](reference_data_research.md): the evidence. §1 Belgium, §2 other countries, §3 global sources and licences, §4 attribution, §5 romanization and tags, §6 model evidence, §7 Spark fit, §8 sources.
- [reference_data_plan.md](reference_data_plan.md): the order of work.
- [commerce_PRD.md](commerce_PRD.md): the first consumer (§4.17 time zones, §4.18 addresses) and the owner of the shared framework additions X3, X10, X15, X16, X17, X18 and D36. This PRD is commerce milestone **M19**.
- `warehouse_picking_PRD.md`: the warehouse plugin (D13). Its push + reconcile transport (WD1) is not what consumers use here (§4.8).

> The decision log (§13) is the record. Where the body and the log disagree, the log wins; fix the body.

---

## 0. Status

| Item | State | Date | Record |
|---|---|---|---|
| Research: Belgium, world sources and licences, model, Spark app architecture | ✅ | 2026-10-17 | research §1–§8 |
| Grilling round 1 (RQ1–RQ7, RD1, defaults) | ✅ | 2026-10-17 | §13 Round 1 |
| Round 2 (Q-R1–Q-R18 accepted) | ✅ | 2026-10-17 | §13 Round 2 |
| Round 3: develop on this branch with a basic seed set; fuzzy by default; optional VAT-rate, holiday and VAT-territory data for commerce | ✅ decided; ⏳ to build | 2026-10-19 | §13 Round 3 |
| Source-generator study (K12) | ⏳ | — | §5 |
| Open decisions Q-R1–Q-R18 | ⏳ | — | §11 |
| Spikes RS1–RS22 | ⏳ | — | §7 |
| Milestones R0–R14 | ⏳ | — | §10, plan |

---

## 1. Goal

Ship a **reusable Spark plugin for reference data**: countries, languages, administrative levels, places with their merger and split history, postal codes and streets. Names are localized, and romanized where an authority romanizes them. A public typeahead API serves address entry, and **deterministic releases** let consumer apps hold local copies. Commerce uses the client packages for every address (commerce §4.18). A demo app, `apps/ReferenceData`, runs an instance locally. **We deploy nothing** (K8 revised); any host can run its own instance.

Three properties drive every section:

- **History is data.** A place keeps its identity through renames, recodes and re-parenting. Mergers, splits and demotions are dated, so "which municipality was this address in on 2018-06-01?" has an answer. "Current" is computed from an as-of date and never stored (RD3).
- **Deterministic.** The same source versions give the same ids and the same bytes: in the instance, in every consumer, today and in five years (RD6, RD-DEF 12).
- **Sources refresh themselves.** Each source has a cron schedule. A cheap change check runs first; only a changed source is fetched and imported (RD1).

### Owner constraints

| # | Constraint |
|---|---|
| K3 | No backward compatibility: the libraries are in preview. This is why `TranslatedString` can be redesigned for place names (RD7, commerce X17). |
| K5 | One PR for everything: commerce, the warehouse plugin, this plugin, and every defect found along the way. |
| K6 | This repo is a **framework**, not a platform. The plugin is a set of configurable building blocks; `apps/ReferenceData` shows one instance. |
| K7 | The framework does not pick vendors. Every register gets its own adapter package, and the host chooses countries, sources and schedules in configuration. |
| K8 | **The owner's request** (as recorded on 2026-10-17: a paraphrase, with the owner's own words in quotation marks): a new Spark app in this repo for reference data. Countries, languages, cities, and regions at several levels per country (Belgium: Vlaanderen / Wallonie / Brussels gewest → provincies → cities; other countries have other layouts), plus street names. Localized in several languages and romanized. A publicly accessible, non-protected API without CORS restrictions. An address typeahead that narrows each box. Seeded deterministically into dev/prod **and** consumer databases. Belgium's open-data XML export. Merger history: Kruishoutem + Zingem → Kruisem, and Ooike split between two towns (owner recalled "the 1990's"; verified: 1 January 1977, §4.2). The owner's model idea: a `Place` for any municipality name, e.g. Bevere `isCity=false`, `parent=Oudenaarde`; on a merger, `isCity` goes from true to false and a parent is assigned. Owner: "This will almost certainly also be in-scope, as a new live-deployed application."<br>~~Live-deployed~~ **Revised:** "second thoughts - i don't think I'll be deploying this reference data app to my vps. If the code is written and can work properly, that would be great already". The app must work locally and under test; any host can deploy its own instance (§8).<br>**Round 3 (RD9, 2026-10-19):** "2 days ago I "decided" not to develop or not to seed-data the "reference data" application (countries, cities, streets, ...) - But I think I'll do it anyway, just a basic set, some countries, some regions, some streets, ... Just to be able to test --- All of that is in-scope for the current branch". → Developed on this branch, with a **basic seed set** (§3.2). Still not deployed by us. |
| K10 | **Time zones in one place.** All zone logic lives in core built-ins. Places only *carry* an IANA zone id; labels flow through the core `ISparkTimeZoneNames` (D36). This plugin never converts or formats zones. |
| K11 | **Standard web APIs first.** Country, language, script and currency names come from `Intl.DisplayNames` in the browser; sorting and filtering use `Intl.Collator` and `String.prototype.normalize`. The API serves only what a browser cannot know: subdivisions, places, postal codes and streets. |
| K12 | **Prefer source generators** over hand-written boilerplate. The study is running; its results are folded into §5. |

The time-zone decisions D33–D36 from the commerce grilling bind this plugin too. They are quoted in §13.

### Non-goals

See §9. In short: no house-number import, no OpenStreetMap, no countries beyond the RD2 adapters, no hand-curated history, and no public deployment by us.

---

## 2. What Spark already gives us

The full fit analysis, with verdicts, is in research §7. These are the parts the design leans on:

| Need | Spark feature | Evidence |
|---|---|---|
| Per-source schedules | Cron (6-field, UTC), one claim per occurrence across the cluster; the house pattern is a job that only queues a message | `libs/cron/MintPlayer.Spark.Cron/SparkCronScheduler.cs:101-102,228-275`; `apps/CodeCoverage/CodeCoverage/CommitGraph/CommitGraphRecipients.cs:38-50` |
| Restart-safe, resumable import | Messaging recipients with `ICheckpointRecipient` / `IMessageCheckpoint`, `BroadcastOnceAsync(msg, key)` de-duplication, a per-queue `HandlerTimeout`, renewed claims | `libs/messaging/MintPlayer.Spark.Messaging/SparkMessagingOptions.cs:114,144`; `SparkQueueOptions.cs:85`; precedent `DeleteRepositoryDataRecipient` (research §7) |
| An admin "Import now" | A custom action that queues the work | `libs/spark/MintPlayer.Spark/Services/DatabaseAccess.cs:971-980` |
| Small curated seed sets | A `StartedAsync` seeder with a `SeededHash` (refreshes only documents nobody edited) | `apps/SeoTester/SeoTester/Catalogue/CatalogueSeeder.cs:8-77` |
| Streaming XML | `XmlReader` with DTD prohibited, no resolver and a character cap | `apps/SeoTester/SeoTester.Library/Crawling/SitemapReader.cs:17-36` |
| Bounded downloads | SafeHttp caps wire bytes, decompressed bytes and time | `libs/spark/MintPlayer.Spark.SafeHttp/README.md` |
| Deterministic ids | `IHasNaturalId` and the id conventions; explicit ids for bulk writes | `libs/model/MintPlayer.Spark.Model/IHasNaturalId.cs:3-48`; `libs/spark/MintPlayer.Spark/SparkDocumentStoreConventions.cs:23-26,58-85` |
| Anonymous custom endpoints | Generator endpoints (`IGetEndpoint`, `IEndpointGroup`); `group.AllowAnonymous()` (share links); `.SkipXsrfToken()` | `apps/SeoTester/SeoTester/Catalogue/CatalogueEndpoint.cs:18-63`; `libs/spark/MintPlayer.Spark/Endpoints/Share/ShareEndpoints.cs:11-19`; `SparkMiddleware.cs:376-383` |
| CORS on one group only | `group.RequireCors(...)`, as the identity provider's `OidcConnectCorsGroup` does | `libs/identity_provider/MintPlayer.Spark.IdentityProvider/Endpoints/Oidc/Groups.cs:73-82`; `docs/guide-cors.md:77-88` |
| Rate limiting | `spark.AddRateLimiter()` plus named policies | `docs/guide-rate-limiting.md:7-90`; `apps/CodeCoverage/CodeCoverage/Program.cs:444-506` |
| Hand-written indexes with an engine choice | `SparkIndexCreationTask<T>` exposes the engine, analyzers and storage | `libs/spark/MintPlayer.Spark/Indexes/SparkIndexCreationTask.cs:20-44,72` |
| A library that ships model files, rights and renderers | Library layers under a permanent alias; `SparkModelSatellites.SeedRenderer` | `docs/guide-library-layers.md:3-12,198-233`; `libs/model/MintPlayer.Spark.Model/Model/SparkModelSatellites.cs:59` |
| Client renderer plug-in | Renderer arrays that the app spreads into its own list | `libs/node_packages/ng-spark/renderers/src/spark-attribute-renderer-registry.ts:51-73` |
| An accessible typeahead to build on | ng-bootstrap `bs-typeahead`: combobox role, keyboard, live announcer. It lacks debounce, cancellation, an item template and form-control support (RX5) | ng-bootstrap 22.25.0 `typeahead.d.ts:5-37` (research §7) |
| Test seeding and route review | `SeedFromJsonAsync`; the committed route-table snapshot records anonymous access, CORS and the rate-limit policy per route | `libs/testing/MintPlayer.Spark.Testing/SparkTestDriver.cs:277,296`; `RouteTableSnapshot.cs:141-185` |
| Time zones | `IRequestTimeZoneResolver`; `<spark-date-time>` (X16) and `ISparkTimeZoneNames` (D36) land in commerce M1 | `docs/guide-dates-and-sorting.md:291-331`; commerce §4.17 |

**What does not fit, and why the design avoids it:**

- **Spark's generic search** turns each word into `*word*` with no ranking and no folding, and pages static-index results in memory (`libs/spark/MintPlayer.Spark/Services/QueryExecutor.cs:2033-2045,1050-1100`; default take 50, `QueryReadPath.cs:38`). The typeahead is a dedicated endpoint (§4.10).
- **`[Replicated]` needs RavenDB ETL**, which Community does not have (`docs/endpoints_generator_adoption_plan.md:219`; ETL deploy has no licence check, `libs/replication/MintPlayer.Spark.Replication/Services/EtlTaskManager.cs:22-109`). Consumers get releases instead (§4.7).
- **Migrations** run synchronously inside `UseSpark()` under a 30-minute lock that is never renewed (`libs/migrations/MintPlayer.Spark.Migrations/SparkMigrationRunner.cs:127-216,229-234`). Imports run as a hosted, queued service (§4.5).
- **`[Reference]` resolves only in the local session** (`docs/guide-reference-attributes.md:7-11`). So consumers that reference places hold local copies (§4.7).
- **Revisions are capped on Community** (2 per document, 45 days; `libs/history/MintPlayer.Spark.History/README.md:13-60`). History is modelled as documents (§4.2).
- **The reference picker** loads at most 50 options and filters in memory (`docs/guide-reference-attributes.md:208-210`). Address entry uses the client package's typeahead (§4.15).

The gaps closed in this PR are in §5 (framework additions) and §6 (defects).

---

## 3. Product scope

### 3.1 Personas

- **Instance host:** the developer or operator who runs an instance. Picks countries, sources and schedules, and optionally a release-signing key.
- **Instance administrator:** signs in with a local password (RD-DEF 2), triggers imports, follows import runs, and sees ledger conflicts.
- **Consumer app:** a Spark app (commerce, or any host) or any other system. It syncs releases (RD6) and calls the typeahead API.
- **End user:** fills in an address in a consumer's form.

### 3.2 Coverage and depth (RD2, RD4)

| Tier | Countries | Admin units and history | Localities | Postal codes | Streets | House numbers |
|---|---|---|---|---|---|---|
| **Belgium, in depth** | BE | Since 1970 from Statbel REFNIS, sections from NIS6, mergers of 2019 and 2025 linked | Sections, Walloon parts of municipality (Q-R16), postal localities | BeST postal info (1,191 codes) | BeST (156,055 current) | Validator only (RD5) |
| **Tier 1: open registers** | NL, FR, LU, CH, LI, AT, DK, NO, FI, EE, LV, ES, PL, CZ, SI, US, AU | What each register publishes machine-readably (about 20 countries publish change events; FR since 1943, CH since 1960) | Per register | Per register; not open for the US, unverified for AT, SI and EE (§4.4) | Per register | Not imported |
| **Tier 2: global baseline** | Every other country; for the 18 above it adds names and cross-ids only | Wikidata (CC0): units, P571/P576, P1365/P1366, dated P131 | GeoNames populated places | GeoNames postal codes (with its truncations) | None | — |

- **History depth (RD4)** is what authorities publish in machine-readable form, and nothing more. Older units that still exist as sections appear as places without dated lineage (Bevere, absorbed by Oudenaarde in 1965, is NIS6 `45035Y`). There is **no hand curation**.
- **Each country profile states "history known since".** When an `asOf` date predates it, the API says so explicitly instead of guessing (§4.11).
- **The global baseline** also supplies exonyms (Wikidata labels), romanized names (NGA GNS, UN/LOCODE), localized country, language and script names (CLDR), codes (IANA Language Subtag Registry) and time-zone exemplar cities (CLDR, §4.13).

**Basic seed set (RD9, round 3).** For testing, the plugin ships a small committed seed set, built from the adapters' real sample fixtures (RS8) by the same import pipeline, so its ids come from the same ledgers and a later full import continues them instead of colliding:
- **countries:** a handful (BE, NL, FR, LU, DE) with their country profiles (levels, address-line template, postcode regex, VAT-territory exceptions for commerce GA4);
- **regions:** Belgium's three regions and its provinces; the top level of the others;
- **places:** a few dozen municipalities and sections, including every decision-table case (Kruisem with Kruishoutem and Zingem, Ooike, Oudenaarde with Bevere, Hasselt 2025, Bastogne), postal localities for them;
- **streets:** a few streets per seeded municipality;
- **time zones:** the zone and CLDR exemplar city of each seeded place.

It is seeded deterministically by `apps/ReferenceData` on first start (a `SeededHash` seeder, which leaves documents nobody edited refreshable) and by the tests. It is test data, not a coverage tier: the tiers below describe what full imports provide, and full imports stay manual local runs (RD2).

Volumes for Belgium (research §1): 565 municipalities, 2,720 municipality code rows since 1970, 2,616 NIS6 sections, 2,016 Walloon parts of municipality, 1,191 postal codes, 156,055 current streets, about 147k (postcode, street) pairs. That is about **310k small documents**. House numbers would add 6,656,349 current addresses and about 13 GB of XML a week (RD5 keeps them out).

### 3.3 What a host writes

Illustrative only. The final API is fixed in milestones R2–R10. Only `Spark:ReferenceData:Sources:{id}:Schedule` is decided (RD1).

**An instance:**

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>();        // admin sign-in with a local password (RD-DEF 2)
    spark.AddMessaging();                        // the import queue
    spark.AddRateLimiter();                      // meters /spark; the public API adds its own policy
    spark.AddReferenceData()                     // model, ledger, sync service, releases, /api/v1
         .AddBelgium()                           // one line per adapter package (K7)
         .AddNetherlands()
         .AddGlobalBaseline();
});
```

```jsonc
"Spark": {
  "ReferenceData": {
    "Sources": {
      "be-best":         { "Schedule": "0 0 4 * * *" },         // RD1: 6-field cron, UTC; the change check runs first
      "be-refnis":       { "Schedule": "0 30 4 * * 1" },
      "nl-bag":          { "Schedule": "0 0 5 2 * *" },         // the free extract is monthly
      "dk-dar":          { "Schedule": "0 0 5 * * *" },         // API key from user-secrets: required from 2027-01-15
      "global-geonames": { "Schedule": "0 0 6 * * 0" }
    },
    "Releases": { "Sign": true }                                // RD-DEF 5: optional Ed25519; the key comes from user-secrets
  }
}
```

**A consumer:**

```csharp
spark.AddReferenceDataClient();   // release sync, typed API client, Address behaviour, ISparkTimeZoneNames provider
```

```jsonc
"Spark": {
  "ReferenceData": {
    "Client": {
      "BaseAddress": "https://refdata.example.org",   // no default; null = free-text entry (RD-DEF 8)
      "PublicKey": "…",                               // verifies signed releases (RD-DEF 5)
      "Sync": {
        "Schedule": "0 15 4 * * *",                   // the same RD1 mechanism, pointed at the release manifest (RD6)
        "Countries": [ "be", "nl" ],
        "Collections": [ "countries", "places", "postalcodes" ]
      }
    }
  }
}
```

```ts
provideReferenceData({ baseUrl: 'https://refdata.example.org' }),   // null = free-text mode; no default URL
provideSparkAttributeRenderers([...sparkAddressRenderers]),
```

---

## 4. Architecture

### 4.1 Package map (RD-DEF 1)

The folder names under `libs/refdata/` stay short so paths stay under MAX_PATH (the commerce D31 rule). Package ids and aliases are **permanent** once published.

| Package | Alias | Purpose | Depends on | ng entry point |
|---|---|---|---|---|
| `MintPlayer.Spark.ReferenceData.Abstractions` | — | Plain SDK, no ASP.NET Core (SPARKLIB001): the document types, the country profile, the API DTOs, the release-format contract, and the `Address` value type (Q-R7). References the generator itself so `[ValueObject]` keys are generated | Abstractions | — |
| `MintPlayer.Spark.ReferenceData` | `reference-data` (Q-R14) | The instance: model layer, id ledger, lineage engine, derived fields, search keys, the sync service (RD1), releases (RD6), the public API (RD-DEF 3), import runs and admin actions | Abstractions, Messaging, Cron | — |
| `MintPlayer.Spark.ReferenceData.Sources.<Country>` (×18) | — | One adapter per country: change checks, streaming parsers, mapping, lineage rules, real sample fixtures (§4.4) | ReferenceData | — |
| `MintPlayer.Spark.ReferenceData.Sources.Global` | — | The global baseline: Wikidata, GeoNames, NGA GNS, UN/LOCODE, CLDR (names and exemplar cities), the IANA Language Subtag Registry, the time-zone assignment (§4.13) | ReferenceData | — |
| `MintPlayer.Spark.ReferenceData.Client` | `reference-data-client` (Q-R14) | The consumer: release sync (RD6), read-only local types (countries, places, postal codes), the typed API client, `Address` behaviour (validation, rendered lines), the `IHouseNumberValidator` hook with the BeST implementation (RD5), the free-text fallback (RD-DEF 8), and the `ISparkTimeZoneNames` provider (D36) | Abstractions | `reference-data`, `reference-data/spark` |

**Coupling rules.**
- Adapters never reference each other.
- The client never references the instance package: a consumer carries no importer, no ledger and no instance indexes.
- Nothing references commerce; commerce references the client (commerce §4.18).

### 4.2 Data model (RD3)

**The identity rule** (research §6, adopted by RD3):

> A **place** is one unit of territorial organisation as its authority recognises it. Its identity survives name, code, parent and boundary changes. It ends only when the unit ends: dissolved, merged into a newly created unit, or split with no continuing part. When the same unit carries on **one level down** (municipality → section, district or commune déléguée), its identity continues and only its kind and parent change.

That last sentence is the owner's mechanic (`isCity` true → false plus a parent), stored as a new **dated** period instead of an overwrite. Overwriting breaks as-of questions, splits, renames, transfers, recodes and code reuse (research §6). The owner's `isCity` is `Role == municipality` within a period; "stad" is an honorary title in Belgium, so a boolean cannot carry it.

**Collections.** `RefCountries`, `RefPlaces`, `RefPostalCodes`, `RefStreets`, `RefLineageEvents`, `RefReleases`, plus one `refdata/state` document per database. The `Ref` prefix keeps persistent-object names and collections clear of a consumer's own `Country` or `Place` (the D28 collision analyzer would flag it anyway).

**`RefPlace`** (one document per unit; the shape of `Names` follows X17):

```json
{
  "Id": "places/be/nis6/45017A",
  "Country": "be",
  "Periods": [
    { "From": "1977-01-01", "To": "2019-01-01", "Kind": "be:section", "Role": "submunicipality", "Parent": "places/be/nis/45017" },
    { "From": "2019-01-01", "To": null,         "Kind": "be:section", "Role": "submunicipality", "Parent": "places/be/nis/45068" }
  ],
  "Names": { "Entries": [
    { "Value": "Kruishoutem", "Lang": "nl", "Kind": "official", "From": "1977-01-01", "To": null, "Source": "statbel-nis6" }
  ] },
  "Codes": [
    { "Scheme": "nis6", "Value": "45017A", "From": null,         "To": "2025-01-01" },
    { "Scheme": "nis6", "Value": "45017Z", "From": "2025-01-01", "To": null }
  ],
  "OfficialLanguages": [ { "Lang": "nl", "From": null, "To": null } ],
  "TimeZone": "Europe/Brussels",

  "Lifetime": { "From": "1977-01-01", "To": null },
  "Segments": [
    { "FromKey": 19770101, "ToKey": 20190101, "Kind": "be:section", "Parent": "places/be/nis/45017",
      "Ancestors": [ "places/be/nis/45017", "places/be/nis/45000", "places/be/nis/40000", "places/be/nis/02000" ] },
    { "FromKey": 20190101, "ToKey": 99991231, "Kind": "be:section", "Parent": "places/be/nis/45068",
      "Ancestors": [ "places/be/nis/45068", "places/be/nis/45000", "places/be/nis/40000", "places/be/nis/02000" ] }
  ],
  "Supersedes": [], "SupersededBy": [],
  "Search": { "KeyVersion": 1, "Tokens": [ "kruishoutem" ], "Compact": [ "kruishoutem" ] },
  "Rank": 300000000,
  "Release": { "Introduced": "2026.11.0", "Changed": "2026.11.0" },
  "Source": [ "statbel-nis6" ],
  "Hash": "sha256:…"
}
```

- **Authored or imported:** `Periods` are half-open `[From, To)`; `null` means unbounded or unknown; periods never overlap, and a gap means the unit did not exist. `Kind` is a level key from the country profile; `Role` is generic (`country | region | subregion | municipality | submunicipality | locality`). `Names`, `Codes` and `OfficialLanguages` carry their own validity.
- **Derived by the build, never authored:** `Lifetime`; `Segments` (periods cut at every ancestor change, each with its full ancestor chain); `Supersedes`/`SupersededBy` (direct edges with a date, an event and `whole` or `part`); `Search`; `Rank`; `Hash`; `Release`.
- **Integer date sentinels.** `FromKey`/`ToKey` use `0` for an open start and `99991231` for an open end, because RavenDB range predicates never match null or absent values (memory notes on absent fields).
- **No time-dependent field is stored.** There is no `IsCurrent` and no `CurrentSuccessors`. A merger published in advance becomes effective at midnight without a rebuild, and a consumer that misses a release still answers correctly.
- **A municipality and its same-name section are two places** (RD3). From 1977 to 2018, municipality Kruishoutem 45017 contained the section Kruishoutem; one document would have had to be its own parent.

**`RefPostalCode`.** One document per code. A **locality** is a (code, name) pair, the unit people write on the city line. Each locality links to a section where one exists and always to its main municipality, over time. History comes from lineage: 9770 Kruishoutem's main municipality is 45017 until 2018 and 45068 from 2019. Postal codes are many-to-many with places (9750 = Huise, Ouwegem, Zingem; 1050 is used inside the City of Brussels). The same type serves CH Ortschaften and the FR (code postal, libellé d'acheminement, ligne 5) rows.

**`RefStreet`.** One document per street, with dated municipality and postal-code links (`Periods`), dated names, `Type` (`streetname | hamlet`, as in BeST) and the homonym `Qualifier`. Street identity across the Flemish re-identification on mergers is Q-R2. Street splits and merges (BeST's A=B+C, A+B=C) end streets and create new ones, with lineage. Periods use the **legal** date (2019-01-01), never the register's version timestamp.

**`RefLineageEvent`.** One event per legal change per receiving unit (the 1977 fusion is about 600 events, not one document). Event kinds (RD3): **merge, absorb, demote, split, cede, rename, recode, reparent, restore**. Each event lists per-place operations (`continue | end | demote | promote | cede | receive | rename | recode | reparent | create`) and its sources. `Supersedes`/`SupersededBy` on places are derived from the operations.

**The decision table** maps every source pattern onto the model mechanically. One test per row (RD3, RS4):

| Source pattern | Example (verified, research §1, §6) | Model action |
|---|---|---|
| n → 1 under a **new** code | **Kruisem** 45068 = Kruishoutem 45017 + Zingem 45057, 2019-01-01 | **Merge.** New place; predecessors end; their sections re-parent; the namesake section stays a place |
| 1 → n, one part continues | **Ooike** 45033, 1977-01-01: the west part with the village centre (≈4.95 km²) became a section of Wortegem-Petegem 45061 (NIS6 `45061Z`); the east part (≈1.70 km²) went to Oudenaarde 45035 and was added to Eine | The main part **continues**, demoted; the other part is **ceded**. bpost still lists two "Ooike" localities: 9790 and 9700 |
| Same name, new code | **Hasselt** 71022 → 71072, 2025-01-01, with Kortessem 73040 joining | Recode or merge: **Q-R1** |
| Merger across provinces | **Zwijndrecht** 11056 → Beveren-Kruibeke-Zwijndrecht 46030, moving from province Antwerpen to Oost-Vlaanderen | Its sections **re-parent**; their NIS6 prefixes still say province 1, so **hierarchy comes from periods, never from code digits** |
| A unit continues as a sub-unit | **Borsbeek** 11007 → Antwerpen's tenth district, 2025-01-01; Antwerpen keeps 11002 | **Absorb + demote:** same place, new period with kind `be:district` and parent 11002 |
| Sources disagree on the date | **Bastogne** 82003 + Bertogne 82005 → 82039: Statbel REFNIS says 2024-12-01, the decree's effective date and Wikidata say 2024-12-02 | Date precedence: **Q-R3** |
| 1 old code → 1 new code, nothing else changes | La Louvière 55022 → 58001 and ten other Hainaut municipalities, 2019 (research §1) | **Recode:** same place, new dated code, often a reparent |
| Name changes | Basse-Sambre → Sambreville, 92137, 1979 | **Rename:** new dated name; the old one becomes `historic` and stays searchable |
| A unit is restored | FR: Neussargues en Pinatelle back to five communes, 2025 | **Restore:** the old places get new periods under the same ids |
| An authority reissues a code to a different unit | INSEE 01015 for Arboys en Bugey, 2016 | **New place** with an `@yyyy-MM-dd` id suffix (§4.3) |

**`RefCountry` and the country profile (RD-DEF 6).** ISO codes, official languages and the profile:
- **levels**, each with a key, a generic role, an order, whether it is optional ("the Brussels-Capital Region has none" for `be:province`), and labels per language;
- which level is the **municipality**;
- a **hand-authored address-line template** (libaddressinput's data states no licence, so nothing is copied from it);
- the **postcode regex** and the **house-number rules** (Belgium: number, optional letter, optional box);
- the **typeahead steps** (Belgium: postal locality → street → house number);
- **history known since**.

Country names are not stored per consumer: browsers use `Intl.DisplayNames`, and servers use CLDR (RD-DEF 10, §4.9).

**As-of resolution.**

```
Resolve(p, d):
  if p.Lifetime contains d            -> [p]
  else union over p.SupersededBy where e.Date <= d of Resolve(e.Id, d)
  (sorted by id; Ambiguous = more than one result)
```

- "Which municipality was this address in on date X?" comes from the stored street id: the street's period covering X gives the municipality, and the municipality's segment gives the province and region at X.
- A pre-split reference resolves `Ambiguous` (`part` edges). The street disambiguates. An address-level split, where a new boundary cuts a street, needs house numbers and so stays `Ambiguous` (RD5).
- `asOf` defaults to the UTC date. The address form sends the viewer's local calendar date. Neither does zone arithmetic (K10).

### 4.3 Ids and the ledger (RD-DEF 12)

```
{collection}/{country}/{scheme}/{code}[@{yyyy-MM-dd}]
places/be/nis/45068        places/be/nis6/45017A       places/fr/insee/01015@2016-01-01
postalcodes/be/9770        streets/be/vlg/73243        lineage/be/2019-01-01/merge/nis-45068
countries/be               refreleases/2026.11.0
```

- **Mint once.** An id is minted the first time a unit is seen and is **never re-derived, reused or deleted**. A recode adds a dated code; the id keeps the old code.
- **The scheme names the code space that minted the id.** Belgian NIS codes are unique across levels, so one scheme. Streets use the authority's stable identifier: BeST namespace + objectIdentifier with the region (`vlg`, `wal`, `bru`), because Brussels and Flemish ids are both small integers and collide. Namespaces are normalized first (scheme, `www.`, trailing slash and case: BeST, the Flemish API and the 2025 diff files spell the same namespace three ways).
- **A date suffix only for a real reissue** of a code to a different unit. Statbel shows no reuse in 1970–2025, so Belgian ids carry none.
- **Never derive hierarchy from code digits** (Zwijndrecht's sections).
- **The ledger.** An append-only, committed `ledger/{country}.ndjson` maps every source identity (scheme, code, incarnation start, or a register URI) to its id. Every rule-derived id is checked against it. **A conflict stops the import of that source** (fail closed) and shows on the import run: a source rewriting history, a backfill of older states, a name match turning ambiguous. Where the ledger lives for a third-party instance is Q-R5.
- **Consumers never compute ids from codes.** They call `GET /api/v1/by-code/{cc}/{scheme}/{code}?asOf=` or use the local `ByCode` lookup. The plain id of INSEE 01015 is the commune déléguée Arbignieu, not today's commune nouvelle.
- **Id hygiene.** Ids are case-insensitive, every segment is validated, and no id ends in `/` or `|` (which asks the server to assign one).
- **Ids are public identifiers by design.** The API returns them, and every consumer's local copy uses the same ids, which is what makes `[Reference]` work. Change vectors are never exposed.

### 4.4 Sources and adapters (RD2)

An adapter implements two small interfaces. Sketch, not final:

```csharp
public interface IReferenceDataSource            // one per source; "be-best" is the config key
{
    string Id { get; }
    string Country { get; }                       // "be", or "*" for the global baseline
    SourceLicence Licence { get; }                // licence URL + credit line (RD-DEF 4)
    Task<SourceVersion?> CheckAsync(SourceState last, CancellationToken ct);    // null = unchanged
    IAsyncEnumerable<SourceRecord> ReadAsync(SourceVersion version, ImportCheckpoint from, CancellationToken ct);
}

public interface IReferenceDataMapper            // one per country: records → documents + lineage
{
    IAsyncEnumerable<RefChange> MapAsync(IAsyncEnumerable<SourceRecord> records, MappingContext context, CancellationToken ct);
}
```

Every adapter streams (no whole-file loads), resumes from a checkpoint, ships **real sample fixtures** cut from the real files (memory: parsers need real fixtures), and runs in CI on those fixtures only. **Full imports are manual local runs** (RD2).

**Belgium in depth** (research §1):

| Source | What it gives | Change check | Catches |
|---|---|---|---|
| **BeST Address** (FPS BOSA), [best-full-latest.zip](https://opendata.bosa.be/download/best/best-full-latest.zip), 312.5 MB, weekly, CC BY 4.0, EU High-Value Dataset | Municipalities, postal info, streets (nl/fr/de names, statuses, homonym additions, `streetname`/`hamlet`), Walloon parts of municipality | `ETag`/`Last-Modified` on the bundle; then the ZIP **central directory** (last 64 KB by range request) and each inner entry's CRC. Only the non-address entries (≈3.05 MB compressed) are fetched, because RD5 imports no addresses | **Lambert 2008 from 2026-10-19:** file names drop `L72`, so inner files are matched by pattern `^(Brussels\|Flanders\|Wallonia)(Municipality\|PostalInfo\|PartOfMunicipality\|StreetName\|Address)(\d{8})(L72)?\.zip$`, never by name. A zip of zips; Brussels files start with a BOM. **Current state only:** no retired municipalities, no successor links, and the streets of merged-away Flemish municipalities simply disappear (missing ≠ deleted, §4.5). Brussels and Wallonia postal infos carry no names |
| **BeST postal streets**, [postalstreets-latest.zip](https://opendata.bosa.be/download/best/postalstreets-latest.zip), 2.2 MB | ≈147k (postcode, street) pairs; for Wallonia also the part of municipality (`citypart_*`), the only cheap link from a part to its municipality | `ETag` | Street ↔ postcode links and Walloon/Brussels postal localities come from here (RD-DEF 9) |
| **Statbel REFNIS**, [TU_COM_REFNIS-20250101.zip](https://statbel.fgov.be/sites/default/files/files/opendata/REFNIS%20code/TU_COM_REFNIS-20250101.zip), CC BY 4.0 | Every region, province, arrondissement and municipality code since 1970 with validity dates and nl/fr/de names (2,782 rows; 2,359 → 596 municipalities on 1977-01-01; 565 since 2025-01-01) | The edition is discovered from the publisher's page by pattern (dated file names) | **cp1252**, pipe-separated, CRLF; disambiguators in names ("Tielt (Tielt)"); apostrophes changed `'` → `’` in the 2025 edition; **no successor links**; Brussels has no province |
| **Statbel NIS6**, [NIS6withnamefrom01012025.xlsx](https://statbel.fgov.be/sites/default/files/files/opendata/Statistische%20sectoren/NIS6withnamefrom01012025.xlsx) (and older editions) | 2,616 sections (deelgemeenten) with nl/fr names, including `45035Y BEVERE` and `45061Z OOIKE` | Dated file names | Section codes **change between editions** (45017A → 45017Z); sections of merged municipalities keep the old NIS5 prefix; combined parts ("MOREGEM + OOIKE") make the matcher fail closed |
| **Flemish merger diff files** ("verschilbestanden", 2025), [zip, 4.2 MB](https://assets.vlaanderen.be/raw/upload/v1734692529/repositories-prd/Verschilbestanden_Fusies_20-12-2024_wmyxyo.zip) | Old → new ids for 28 municipalities, 7,018 streets (359 renamed to resolve homonyms) and ≈289k addresses | Pinned by SHA-256 (a one-off file) | The licence wording is unverified (research §1). The 2019 mergers have no such file: their links come from REFNIS end/start dates plus NIS6 prefixes |
| **Flemish Basisregisters API** ([v2](https://api.basisregisters.vlaanderen.be/v2/gemeenten)) | Retired (`gehistoreerd`) municipalities and streets (327 municipalities = 285 in use + 42 retired); `officieleTalen` / `faciliteitenTalen` | Per resource | Change feeds need an API key (not used). Flanders re-creates streets and addresses with **new ids** on every merger; Wallonia keeps them |

- **Lineage without curation (RD4).** 2019 and 2025 merges come from same-day REFNIS end/start dates plus NIS6 old-prefix membership (and, for 2025, the diff files). A recode is "same names, one code ends and one starts the same day, no other participant". 1977 demotions come from REFNIS end dates matched to NIS6 section names within the arrondissement. A NIS6 prefix is evidence only for the period **before** a merger, never for current hierarchy. Whatever stays ambiguous is recorded as unresolved, never guessed (Q-R4).
- **bpost is excluded** (RD-DEF 9). Its disclaimer forbids reproduction and commercial use without written consent ([bpost.be/nl/disclaimer](https://www.bpost.be/nl/disclaimer)). BeST postal info is a strict superset of bpost's 1,146 geographic codes under CC BY 4.0. bpost's list may be read only for a manual, local cross-check; it never becomes a committed fixture.
- **English exonyms** come from Wikidata labels (CC0). Wikidata is weak for Belgian history (658 items with a NIS code against 2,720 REFNIS rows; Bastogne's item carries both 82003 and 82039), so it is never used for lineage.

**Tier-1 adapters** (research §2; every catch named there is listed). Research records no `ETag` or checksum file for these publishers (only one `Last-Modified`, outside this list), so change checks may fall back to a dated file name, a feed entry, a version or release field, or our SHA-256 of the download (RD1); RS8 records what each one actually sends. Where a register links streets to postal codes only through address points, the adapter streams the address file to derive the links and keeps no house numbers (RD5).

| Country | Sources (format, cadence) | Persistent ids | History since | Catches |
|---|---|---|---|---|
| **NL** | LVBAG extract (Kadaster) via the [PDOK Atom feed](https://service.pdok.nl/lv/bag/atom/bag.xml) → [lvbag-extract-nl.zip](https://service.pdok.nl/kadaster/bag/atom/downloads/lvbag-extract-nl.zip): XML in zip, 3.64 GB, free full extract **monthly** (made on the 8th): woonplaatsen, streets, postcodes, addresses. CBS municipalities ([86247NED](https://www.cbs.nl/nl-nl/cijfers/detail/86247NED), OData) | BAG 16-digit ids (survival across mergers unverified); CBS gemeentecode (a new code on a rename or a merger under a new name) | CBS herindelingen since 1991 | **Daily and monthly mutation files are paid** (€14.50/month + €219 setup; daily €219/month): only the monthly extract is free, so the schedule is monthly. Licence labels differ (BAG CC0 in the feed, Public Domain Mark at Kadaster); CBS CC BY 4.0. EPSG:28992. 342 gemeenten, 2,502 woonplaatsen |
| **FR** | INSEE COG ([page](https://www.insee.fr/fr/information/8740222), CSV ≈1 MB, yearly on 1 January, with `v_commune_depuis_1943` and `v_mvt_commune`); BAN ([latest CSV](https://adresse.data.gouv.fr/data/ban/adresses/latest/csv/), 938.58 MB national plus per-département files, continuous); La Poste's [base officielle des codes postaux](https://www.data.gouv.fr/fr/datasets/base-officielle-des-codes-postaux/) (≈1.5 MB, irregular) | INSEE code **plus validity** (or the `id.insee.fr` URI): a bare code is not a territory id, and codes are reused by design; BAN toponym UUID (`id_ban_toponyme`), never `cle_interop`, which embeds the commune code | 1943 | `api-adresse.data.gouv.fr` was announced as decommissioned at the end of January 2026; its successor (data.geopf.fr) allows 50 requests/s per IP. The adapter reads **BAN files**, never an API. BAN quality depends on the communes' BAL; Corsica's 2A/2B are not numeric; the overseas collectivities 975–989 have their own ISO codes; commune ↔ postcode is many-to-many. "Source : Insee" + the date of last update. 34,875 communes |
| **LU** | CACLR ([caclr.zip](https://download.data.public.lu/resources/registre-national-des-localites-et-des-rues/20261012-023002/caclr.zip), 1.96 MB, **weekly**: localities, streets, postcodes per street and building); BD-Adresses ([dataset API](https://data.public.lu/api/1/datasets/adresses-georeferencees-bd-adresses/), CSV 28 MB, weekly) | CACLR ids (stability unverified) | No machine-readable history: known since the first import (RD4) | The download path holds a **dated folder**, so the file is discovered through the dataset API, never hard-coded. CC0. Names in the official French/German forms only, no Luxembourgish names. 100 communes |
| **CH** | BFS commune register ([mutations API](https://www.agvchapp.bfs.admin.ch/api/communes/mutations?startPeriod=01-01-2025&endPeriod=01-01-2026), plus `snapshot?date=`; CSV with `ValidFrom`/`ValidTo`); swisstopo building addresses ([CSV zip](https://data.geo.admin.ch/ch.swisstopo.amtliches-gebaeudeadressverzeichnis/amtliches-gebaeudeadressverzeichnis_ch/amtliches-gebaeudeadressverzeichnis_ch_2056.csv.zip), **daily**) and streets ([STAC](https://data.geo.admin.ch/api/stac/v1/collections/ch.swisstopo.amtliches-strassenverzeichnis), daily); PLZ directory monthly | BFS number + historicization number per state (numbers reusable across states); ESID (streets), EGAID (addresses) | About 4,000 mutations since 1960 (start year unverified) | **Attribution mandatory "in every case"** ("©swisstopo"): in every API response carrying CH data and in the service metadata. The STAC metadata says `"license": "proprietary"` while swisstopo's OGD terms allow commercial use (the OGD terms govern; noted). BFS terms unverified. UTF-8 with BOM, `;`-separated; skip planned addresses (`ADR_STATUS`). de/fr/it/rm. 2,110 Gemeinden; about 4,100 Ortschaften (PLZ6), not congruent with Gemeinden |
| **LI** | swisstopo's directory for Liechtenstein ([CSV zip](https://data.geo.admin.ch/ch.swisstopo.amtliches-gebaeudeadressverzeichnis/amtliches-gebaeudeadressverzeichnis_li/amtliches-gebaeudeadressverzeichnis_li_2056.csv.zip), 386 KB, the CH schema, daily) | FOSNR 7001–7011 (Gemeinden); `STR_ESID` | None named: known since the first import | Attribution "expected" under swisstopo's OGD terms. The ZIP label reads "9493 Mauren FL"; `COM_CANTON` is empty. German. 11 Gemeinden, 944 streets, 13 postcodes |
| **AT** | BEV Adressregister ([downloads](https://www.bev.gv.at/Services/Downloads/Produktbezogene-Downloads/Unentgeltliche-Produkte/Adressregister.html), CSV in zip, **twice a year**: generated at the start of Q2 and Q4, offered 14 days later); Statistik Austria regional units and change file ([page](https://www.statistik.at/services/tools/services/regionales/regionale-gliederungen)) | Adresscode (7 digits) + Subcode, persistent; the GKZ embeds the district and may be re-keyed by district mergers (unverified) | Statistik Austria's change file (years unverified) | A semi-annual snapshot: most scheduled checks report "no change". The API costs €480/year per external app with certificate authentication, so the free files are used. Format changed in October 2022. "© Österreichisches Adressregister, JJJJ" and "Q: STATISTIK AUSTRIA". Postcode per address unverified. 2,092 Gemeinden |
| **DK** | DAR through Datafordeleren file downloads (`https://api.datafordeler.dk/FileDownloads/GetFile`; JSON in ZIP: a full file every Saturday night plus deltas); DAGI admin units (Sundays) | UUID `id_lokalId`; kommunekode, postnummer, vejkode | Bitemporal (DAR) | **An account is required, and after 15 January 2027 only API-key or OAuth access**: the key is a host secret in user-secrets. DAWA is closed (HTTP 410). Credit "(CC BY 4.0) KDS", changing to **"DMI, Kort og Grunddata" on 1 December 2026**; CC BY 4.0 covers downloads from 16 May 2024. Greenland and the Faroes are not included (own ISO codes). Danish. 98 kommuner |
| **NO** | Kartverket addresses ([Atom CSV feed](http://nedlasting.geonorge.no/geonorge/ATOM-feeds/MatrikkelenAdresse_AtomFeedCSV.xml); municipal files a day behind, national about a week); SSB KLASS ([API](https://data.ssb.no/api/klass/v1/), versions and correspondence tables); Bring postcodes ([data.norge.no](https://data.norge.no/en/datasets/5e6847ba-156d-4e14-85d3-8d7f8b727523), TXT/XLSX) | kommunenummer is **not stable** (counties renumbered in 2020 and 2024): keyed on (code, validFrom); address = kommunenummer + adressekode + number/letter | KLASS versions and correspondences | Bring postcodes are NLOD 2.0 according to data.norge.no only (bring.no states no licence). The API caps at 10,000 results, so files are used. Sami and Kven names arrive concatenated in one string and must be split (SSR stores a language per name). The CC BY-NC cadastre products are excluded. "© Kartverket" + link; "Statistisk sentralbyrå eller SSB". 357 kommuner |
| **FI** | Statistics Finland classifications ([kunta API](https://data.stat.fi/api/classifications/v2/classifications?searchText=kunta), JSON); SYKE's Ryhti building and address register ([OGC API](https://paikkatiedot.ymparisto.fi/geoserver/ryhti_building/ogc/features/v1)); Posti postcode files ([webpcode](https://www.posti.fi/webpcode/), `.dat` zips: daily, weekly and monthly files) | kunta codes (retirement rules unverified); Ryhti `address_key` | 1970 (yearly `kunta_1`) | **Posti's files may be passed on only together with its current service description and terms**, so postcodes come from SYKE/Ryhti unless Posti confirms API republication in writing. Ryhti's fields are known only second-hand; SYKE asks intensive users for an identifier; NLS needs a key. fi/sv names (NLS adds three Sami languages). "Source: Statistics Finland". 308 kunnat |
| **EE** | ADS address extracts ([valjavotted](https://aks.maaruum.ee/aks/valjavotted), CSV every 30 days, including "extract nr 9: addresses with components and history"); EHAK units ([files](https://s3.pilw.io/rp-kemit-kataster/EHAK/), monthly + a 1 January snapshot); postcodes ([page](https://geoportaal.maaamet.ee/eng/spatial-data/address-data/postal-codes-p661.html), ≈28 MB) | ADS ids (fields unverified); EHAK codes | ADS extract 9; EHAK yearly snapshots | **"The access is limited for foreign IP addresses"**: a host must run its instance where the download is allowed (documented). The postcode file states no licence (verify before republishing). ADS: "no license agreement", credit the Estonian Land and Spatial Development Board; AKS CC0. Domains are moving from maaamet.ee to maaruum.ee; L-EST97 coordinates. Estonian |
| **LV** | VZD State Address Register on data.gov.lv (package `6b06a7e8-dedf-4705-a47b-2a7c51177473`; daily CSVs `aw_novads`, `aw_iela`, `aw_eka`, `aw_dziv`… each with a `*_his` history twin) | `KODS` (+ `VKUR_CD`); deleted codes kept | `*_his` tables (`DAT_SAK`/`DAT_BEIG`) | Licence labels conflict (data.gov.lv "CC-BY-4.0", Geolatvija "CC0 1.0"): CC BY is followed; no credit text prescribed. `ATRIB` holds the postcode in `aw_eka` but the ATVK code in `aw_novads`. LKS-2020 coordinates. Varakļāni merged into Madona on 1 July 2025. Latvian |
| **ES** | INE municipality codes ([26codmun.xlsx](https://www.ine.es/daco/daco42/codmun/26codmun.xlsx), annual) and change tables; INE Callejero ([page](https://www.ine.es/dyngs/DAB/es/index.htm?cid=1390), ASCII ZIP, January and July releases since 2001, with variation files such as `Var_Callejero_0126_0726.zip`); CartoCiudad ([CNIG](https://centrodedescargas.cnig.es/CentroDescargas/buscadorCatalogo.do?codFamilia=02122), SHP per province, continuous) | Municipal codes kept since 1970; new ones start with 9; extinct codes are not reused unless the municipality is re-created | INE changes since 2000 (alterations since 1842) | **The IGN credit formula** (e.g. "CartoCiudad Continua CC-BY 4.0 scne.es"; "Obra derivada de …" for derived works) must appear next to the data **and in the service's own metadata** ("GetCapabilities o similar"): in `/api/v1/sources` and every response with ES data. INE: "Elaboración propia con datos extraídos del sitio web del INE: www.ine.es". Postcodes from CartoCiudad (Callejero's may be Correos-derived; the Correos list is closed). A footer says CC BY-SA (conflict noted). Layout changed in July 2025; Catastro excluded (licence conflict). Names like "Donostia/San Sebastián", "Coruña, A". 8,132 municipios |
| **PL** | TERYT ([full files](https://eteryt.stat.gov.pl/eTeryt/rejestr_teryt/udostepnianie_danych/baza_teryt/uzytkownicy_indywidualni/pobieranie/pliki_pelne.aspx), daily: TERC, SIMC, ULIC); PRG ([address points](https://opendata.geoportal.gov.pl/prg/adresy/PunktyAdresowe/POLSKA.zip), [admin units](https://opendata.geoportal.gov.pl/prg/granice/00_jednostki_administracyjne.zip)) | TERC / SIMC / ULIC (reuse unverified); street = SIMC + SYM_UL (unverified); PRG UUID-style ids; two street-id systems | TERYT change files for any period (from 2000) | **Postcodes come from PRG address points kept by the gminy and not validated by the post** (the PNA list is sold). The change service needs an account. "Źródło: GUS, TERYT, CC BY 4.0, stan na <data>" (state the retrieval time); PRG is "free for any use" without a licence name. 302,793 streets; minority-language names for 1,305 localities |
| **CZ** | RÚIAN via VDP ([address places](https://vdp.cuzk.cz/vdp/ruian/adresnimista); VFR XML/GML monthly + daily change files; a CSV per municipality monthly with PSČ, [fields](https://cuzk.gov.cz/Uvod/Produkty-a-sluzby/RUIAN/2-Poskytovani-udaju-RUIAN-ISUI-VDP/Dopady-zmeny-zakona-c-51-2020-Sb/Adresni-mista-CSV_atributy.aspx)) | RÚIAN codes (no reuse: unverified) | "Platí od/do" validity on every element + daily changes | Credit "ČÚZK, [rok]" (the year of the data), with the metadata and a link to the terms. VDP is informational (binding data only through ISZR). Windows-1250 encoding unverified. Česká pošta's CSVs are unlicensed (excluded). Czech |
| **SI** | GURS Register of Spatial Units and the address register (renewed March 2024; [WFS metadata](https://eprostor.gov.si/imps/srv/api/records/8f427833-d5bf-49fd-ae9e-1106eb63e901)); bulk only through the JGP/eGP apps, no file URL | EID since March 2024 (expect id changes around the renewal) | Validity dates unverified | **Postcode linkage unverified at field level.** No direct bulk URL: the adapter uses the free WFS or the app export (RS8). GURS may change structures and terms at any time. Credit "Geodetska uprava Republike Slovenije, Register naslovov, <year>". Italian and Hungarian names in the bilingual areas |
| **US** | Census TIGER/Line ([TIGER2026](https://www2.census.gov/geo/tiger/TIGER2026/), shapefiles yearly; 2026 released 2026-10-14; ADDRFEAT for street names and ZIP per side); [county changes](https://www.census.gov/programs-surveys/geography/technical-documentation/county-changes.html); NAD ([zip](https://data.transportation.gov/download/fc2s-wawr/application/x-zip-compressed), 2026-09-30, partial) | GEOID (recodable: Connecticut's planning regions, 2022), TLID, LINEARID | County changes since 1970 (decade pages, no validity model) | **Postal codes are not open:** ZIPs come from ADDRFEAT address ranges (Census-maintained, not authoritative, potential ranges) and the partial NAD; ZCTA ≠ ZIP; the HUD crosswalk is excluded. Credit the U.S. Census Bureau ("TIGER" is a trademark, never in product names). Several time zones (Q-R10) |
| **AU** | G-NAF ([data.gov.au](https://data.gov.au/data/dataset/19432f89-dc3a-4ef3-b943-5326ef1dbecc), PSV/SQL ≈2.0 GB, **quarterly**: Feb, May, Aug, Nov); Geoscape Administrative Boundaries ([page](https://geoscape.com.au/data/administrative-boundaries/), CC BY 4.0, quarterly localities); ABS ASGS ([Edition 3](https://www.abs.gov.au/statistics/standards/australian-statistical-geography-standard-asgs-edition-3/jul2021-jun2026), CC BY 4.0) | G-NAF persistent ids (`ADDRESS_DETAIL_PID` stable; locality and street-locality PIDs); ABS codes are edition-scoped | G-NAF release notices; ASGS editions (Edition 4 from July 2026) | **The G-NAF EULA:** the data "must not be used for the generation of an address or a compilation of addresses for the sending of mail" unless verified against another source; the clause passes to API users in our terms (§4.12). The EULA text could not be fetched (verify in R0); data.gov.au refuses automated fetches. Credit: "Incorporates or developed using G-NAF © Geoscape Australia licensed by the Commonwealth of Australia under the Open Geo-coded National Address File (G-NAF) End User Licence Agreement". Several time zones (Q-R10) |

Two refinements of RD2's "open postcodes" from research §2: **US** postal codes are not open (address-range ZIPs only), and **AU**'s licence is not plain CC BY (the mail clause). Postcode linkage is unverified or unlicensed for **AT**, **SI** and **EE**, and depends on data.norge.no's licence statement for **NO**. RS8 verifies each before its adapter ships.

**The global baseline** (`Sources.Global`, research §3):

| Source | Gives | Licence | Catches |
|---|---|---|---|
| [Wikidata](https://www.wikidata.org/wiki/Wikidata:Copyright) | Admin units, history (P571/P576, P1365/P1366, P131 with P580/P582), labels in ~300 languages, cross-walks (P297–P300, P1566) | CC0 | Crowd-edited: validated against tier-1 data where present. The full dump is ≈127 GB; extraction method and reproducibility are RS11. A SPARQL result has no published checksum, so the change check hashes the download (RD1) |
| [GeoNames](https://download.geonames.org/export/dump/readme.txt) | Places (feature classes A and P), alternate names with `from`/`to`, postal codes; daily modification and deletion files | CC BY 4.0 | Admin1 codes are mostly FIPS 10-4 (ISO only for US, CH, BE, ME), mapped through Wikidata P300 + P1566. Postal codes are truncated for CL, IE and MT, partial for CN, AR and BR, first part only for CA, NL and GB in `allCountries`; GB carries a Royal Mail notice. Import the dump, never the free web service |
| [NGA GNS](https://geonames.nga.mil/) | BGN/PCGN romanized names plus native script, world minus the US | No restrictions; citation requested | The strongest open source of systematic romanizations |
| [UN/LOCODE](https://unlocode.unece.org/) | Romanized names of trade locations, subdivision codes | CC BY 4.0 | Column details unverified (research §3) |
| [Unicode CLDR](https://www.unicode.org/license.txt) | Localized country, language and script names; time-zone exemplar cities (§4.13) | Unicode License v3 (notice on the sources page) | **Not** for subdivision names: since CLDR 42 they are uncurated Wikidata copies |
| [IANA Language Subtag Registry](https://www.iana.org/assignments/language-subtag-registry) | Every ISO 639 language, ISO 15924 script, ISO 3166-1 α-2 and M49 region code, with deprecations | CC0 | Used **instead of** SIL's ISO 639-3 tables, whose terms forbid offering "a means to redistribute the code set" |

**Optional sources for commerce (round 3, 2026-10-19; commerce GC10, GC11, GA4).** Off by default; each is its own adapter package under the same `IReferenceDataSource` contract, and each licence is verified in R0 before it ships (commerce D37):
- **EU VAT rates (GC10):** the Commission's TEDB (Taxes in Europe Database) rates per member state into a `VatRateSuggestion` collection (country, category, rate, valid from/to). Commerce shows them as **suggestions**; the host still sets its own rates (commerce §4.8). Fixtures from the public download; licence and machine-readable format to verify (RS23).
- **Holiday calendars (GC11):** public holidays per country (and per region where they differ) as dated `PublicHoliday` documents, consumed by commerce's carrier cut-offs and delivery promises through the core zone-aware schedule (warehouse WX14). Source per country chosen in R0 (an official open source where one exists; otherwise the global baseline marks the country "no holiday data"); no hand curation (RD4).
- **VAT territories (GA4):** the country profile gains a `VatTerritories` list (Canary Islands, Åland, Mount Athos, Northern Ireland as `XI`, the French overseas departments, …) with their postcode or subdivision patterns, so commerce's place-of-supply rule can tell a special territory from the mainland. Hand-written per profile (like the address-line template, RD-DEF 6), with the legal basis cited and checked on EUR-Lex.

The basic seed set (§3.2) includes the VAT territories of its countries, and a few holidays per seeded country for commerce's tests.

**Kept out of the core database** (research §3): OpenStreetMap and Overture (ODbL share-alike would reach our backing database), GISCO boundary geometries (non-commercial; the LAU code lists are fine), Chile INE (CC BY-SA), SIL ISO 639-3, Bulgaria's NSI EKATTE (no derivative works), Google's libaddressinput data (no licence), Who's On First (per-record licence mix; concordance only), and the proprietary postal products (PAF, AddressBase, Eircode, Canada Post, USPS, Correos, Poste Italiane, CTT, Deutsche Post, BKG, Australia Post, NZ Post).

### 4.5 The sync pipeline (RD1)

Owner: "for the per-country reference-data, wherever possible, the framework plugin should probably have a simple option/configuration value (eg cron-schedule) that first checks a checksum to see if a new version was made available, and fetches the online publicly available data again when changed, and somehow updates the data in the database".

1. **Schedule.** One cron job per configured source, from `Spark:ReferenceData:Sources:{id}:Schedule` (6-field, UTC). The job only queues work (the house pattern).
2. **Change check first.** In order of preference: HTTP `ETag`/`If-None-Match` or `Last-Modified`/`If-Modified-Since`; a checksum file the publisher posts; otherwise our SHA-256 of the download. Unchanged → the run ends, recorded as "no change".
3. **Queue the import.** `BroadcastOnceAsync(ImportSource{source, version, url, sha256}, "{source}/{version}")` onto a dedicated `refdata-import` queue with a long `HandlerTimeout`. The admin's "Import now" action queues the same message. **Never inside a migration** (§2).
4. **Download** through a named client with a host allow-list and SafeHttp-style caps, to a temporary file. Downloads are data: parsed in-process by streaming readers, never executed.
5. **Stream and checkpoint.** A checkpointed recipient reads one partition at a time (region, municipality group, file) and records status, counts and cursor in `ImportRuns/{source}/{version}`, which the admin pages show. A restart resumes from the cursor.
6. **Map and mint.** Source records map to documents; each source identity resolves through the ledger (§4.3). A ledger conflict stops this source's import.
7. **Diff.** By source persistent id + source version + content hash. Unchanged documents are skipped. A first load into an empty collection uses BulkInsert with explicit ids and `SkipOverwriteIfUnchanged`; later changes are written in chunks under optimistic concurrency.
8. **Retire, never delete.** An object missing from a source is closed (status or `To` date) with `absentSince`, and its fate is resolved from the lineage sources. The sweep is refused when the retired share is implausibly high (a broken download must not retire a country).
9. **Derive.** Lineage events, then segments, ancestors, successor edges, search keys, rank and hash for every affected document. An ancestor's change re-cuts its descendants' segments without editing them.
10. **Release.** A run that changed anything records a dataset release (§4.6; granularity is Q-R17).

Reference types are **not live**: no live-query subscription on these collections, so a bulk import cannot set off re-query storms (FR8).

### 4.6 Releases (RD6, RD-DEF 5)

A release is the instance's state written as **canonical files**:

```
{release}/{cc}/{Collection}.ndjson               full files
{release}/deltas/{from}/{cc}/{Collection}.ndjson every document whose hash changed since {from}
{release}/manifest.json  (+ manifest.sig)
```

- **Canonical bytes**, so the same inputs give the same bytes: UTF-8 without BOM, LF, one document per line sorted by id (ordinal); a fixed property order with every property written (`null` or `[]`, never absent); strings in NFC; integers only, coordinates to 7 decimals, dates `yyyy-MM-dd`; set-like arrays sorted by a defined key; `@metadata` holds only `@collection` and a fixed `Raven-Clr-Type`; no timestamps inside documents.
- **`Hash`** is the SHA-256 of the canonical document without `Hash` and `Release`. `Release.Introduced` is the first release holding the id; `Release.Changed` the release where the hash last changed.
- **The manifest** holds the schema version, the release and its predecessor, the builder version and `KeyVersion`, every source (URL, retrieval date, SHA-256, licence, attribution), every file (path, SHA-256, count, and a digest over (id, Hash)), per-country change counts, and the next effective date.
- **Signing (RD-DEF 5).** Optional Ed25519 over the manifest with the instance key; consumers configure the public key. The library choice is RS17.
- **Never delete.** No id ever leaves a release. **An importer refuses a release whose id set lacks any id of the previous one.** Ending is a closed period plus lineage, so deltas need no tombstones.
- **The format is a versioned public contract**, with JSON schemas and fixtures read by the instance, the .NET client and non-Spark consumers alike.
- **Storage** of release files on the instance is Q-R8. Releases are runtime output, never build output: anything a build writes into `App_Data` is lost on an Nx cache hit (`docs/spark_composition_PRD.md:37-38`).

### 4.7 Consumer sync (RD6)

The consumer runs **the same RD1 mechanism, pointed at the instance's release manifest**:

- a cron schedule and a conditional GET on the manifest; on startup too, without blocking it;
- apply deltas, or the full files when too far behind; BulkInsert with explicit ids and `SkipOverwriteIfUnchanged`, so **re-importing a release writes 0 documents** (no re-indexing, no subscription wake-up, no change-vector churn);
- verify the signature and the file hashes; refuse a release that drops ids;
- write `refdata/state` **last**, with the release and per-collection digests;
- **a periodic full per-collection checksum**: on a digest mismatch, that collection is re-imported in full.

**What a consumer holds locally:** the small collections (countries with their profiles and admin levels, places, postal codes; about 10k documents for Belgium), because Spark resolves `[Reference]` locally. **Streets** come from the live typeahead API. Invoices keep the rendered lines as the legal snapshot (§4.15).

Local copies are **read-only** in the consumer's model: the client's library layer declares them `isReadOnly`, and only the sync service writes them. Non-Spark consumers read the same files.

### 4.8 Why not `[Replicated]`, and not the warehouse transport

- RavenDB ETL is absent on Community, so `[Replicated]` cannot serve consumer copies there (§2).
- WD1's push + reconcile serves Spark-to-Spark data owned by one module. Releases also serve non-Spark consumers, need no module certificates, and are CDN-cacheable files. The RD1 record first named WD1's push + reconcile as the consumers' channel; RD6 then fixed it as the release sync above. Whether the instance additionally *notifies* consumers of a new release is Q-R9.

### 4.9 Names (RD7, X17)

Owner: "Probably A, but see if you can somehow just use one name-system - remember, no backward compat". So places use the **redesigned `TranslatedString`** (commerce X17), not a separate name type:

- **entries:** value, RFC 5646 tag, and optionally a kind (`official | common | exonym | historic`), validity (`From`/`To`), source, and romanization origin (`authoritative | generated`);
- a one-line API for app texts; any language; an RFC 4647 fallback chain on server and client; sanitized generated index field names (`zh-Hant` safe).

**How places use it.**
- Several names per language and kind ("'s-Gravenhage" official and "Den Haag" common, both `nl`); bilingual official names as one entry per language (Brussels streets).
- A rename closes the old entry and adds a new one; the old name becomes `historic` and stays searchable.
- **Tags follow RFC 5646:** omit Suppress-Script subtags (`nl`, `ru`, `ja`; never `ru-Cyrl` or `ko-Kore`); use a script subtag only where it distinguishes (`zh-Hans`/`zh-Hant`, `sr-Latn`, `ru-Latn`, `ja-Hrkt`). The script is derived from the tag through CLDR likely subtags.

**The display rule** (RD7; available everywhere, server and client) for a place at `asOf` and UI language `u`:
1. **local:** the official name(s) valid at `asOf` in the area's official language(s); several are ordered and joined as in Q-R13;
2. **ui:** if `u` is an official language there, that language's official name alone (Brussels: "Wetstraat" in nl, "Rue de la Loi" in fr);
3. **secondary:** otherwise the exonym in `u`, after the local name ("Antwerpen (Anvers)");
4. **Latin:** for a non-Latin place, the authoritative romanization;
5. fallback: `u` with RFC 4647 truncation, then the local official name, then any official name, then the first.

Stored addresses keep the local official name. The API returns `local`, `ui` and `latin`, and the formatter picks per use.

**Romanization.** Authoritative forms first: the register, GNS (BGN/PCGN), Wikidata. **Machine transliteration only for search keys**, and only for true alphabets (Cyrillic, Greek, Armenian, Georgian); never for kanji or abjads. Whether a source romanization without a declared system is shown, and how, is Q-R11. All 18 tier-1 registers publish Latin-script names, so romanization matters for the global baseline only.

**Country, language, script and currency names (RD-DEF 10, K11).** Browsers use `Intl.DisplayNames` (and `Intl.Collator` to sort and filter about 250 countries). Server-side output (mails, PDFs) uses CLDR names: the instance ingests CLDR and serves them (`GET /api/v1/countries?lang=`) for non-.NET consumers; how a .NET consumer gets them, including in free-text mode, is Q-R18. Neither is stored as documents in a consumer.

### 4.10 Search (RD8, RD8b, RD10, X18)

Owner: "If Lucene fuzzy can be used with any ravendb license, yes pick option C - otherwise we should let the framework provide a second option for the developer to choose from". Verified: the engine is chosen per index on every licence (Community, Developer and unlicensed servers default new indexes to Corax; an explicit `SearchEngineType.Lucene` overrides it; [docs](https://docs.ravendb.net/7.1/indexes/search-engine/corax); a Lucene index on a Corax-default server works, measured on 7.2.5). Then: "And perhaps lucene can be enabled on that index automatically" (X18).

**Round 3 (RD10 = commerce D38, 2026-10-19):** the owner chose **"Fuzzy by default"** for the whole framework: every index with searchable fields runs on Lucene and search is typo-tolerant by default, with a per-field opt-out; existing search indexes rebuild side by side. For this plugin that changes only the engine table below: the two typeahead indexes were already Lucene. The typeahead's own **two-step** query (prefix tiers first, fuzzy only when they return fewer than *k*) stays, because the deterministic re-ranking needs exact and prefix matches to outrank fuzzy ones; it is the "fuzzy by default" behaviour applied in this order.

**Search keys are data, computed at import** (one versioned C# pipeline, used again to fold the query):
1. assert at startup that `"é".Normalize(NormalizationForm.FormD).Length == 2` (invariant-globalization mode may return input unchanged);
2. NFKC; unify punctuation (all apostrophes → `'`, all dashes → `-`, drop the soft hyphen, `. , / ( ) &` → space);
3. `ToLowerInvariant()` plus explicit İ/ı → i, ß/ẞ → ss, ς → σ (.NET has no full case folding);
4. non-Latin names keep a native key (never strip combining marks there: が = か + U+3099) **plus** a Latin key (authoritative, or a machine key for true alphabets);
5. fold Latin: NFD, drop marks **only after a Latin letter or digit**, NFC; map ø, ł, đ/ð, æ, œ, þ; extra keys for de ä/ö/ü → ae/oe/ue and da/nb/sv å/ø/ä/ö;
6. tokenize on space, `'` and `-`;
7. emit spaced, compact (`sintmartenslatem`), article/elision-stripped (`'s`, `l'`, `le`, `de`, …) and abbreviation-class keys (sint/saint/sankt/st → `st`; onze-lieve-vrouw → `olv`).

`KeyVersion` lives in the manifest; any pipeline change re-keys the release, visibly.

**Two-step typeahead (RD8).**
1. **Prefix step**, three tiers as lazy queries in **one round trip**: exact compact key; compact-key prefix; every query token a prefix of some name token (`startsWith` on keyword fields). Digits-only input queries postal codes ("97" → 9700…9799).
2. **Fuzzy step**, only when the prefix step returns fewer than *k* results: `WhereEquals(Tokens, t).Fuzzy(s)` on whole tokens, on the document-query API. Fuzzy must follow `WhereEquals` directly, can't be combined with wildcards or `startsWith` in the same clause, and its similarity is `1 − d/len` (translated from an edit distance per token length). How several fuzzy tokens compose is RS6.
3. **Deterministic re-ranking in C#:** tier, then edit distance, then the precomputed `Rank`, then id. Never the engine's score: Lucene and Corax scores depend on what each database holds, so a consumer with only Belgium would rank differently from the instance.

- **`Rank`** is precomputed: the kind weight from the country profile (the city-line unit ranks highest), then population where known.
- **No `search()`.** RavenStandardAnalyzer drops English stop words, which makes the municipality **As** unfindable; it also splits on hyphens. Every field uses the default keyword analyzer; no custom analyzer (Community allows one per database).
- **The as-of filter** (`FromKey <= $asOf and ToKey > $asOf`) matches at most one segment per place, so fan-out never returns duplicates.
- **Injection safety.** Every value is a query parameter. The token count only chooses among fixed templates; `within` must match the id grammar; `kinds` must come from the country profile's allow-list (repo rule: never splice into RQL).
- **The free-text escape hatch stays** (RD8): no match is never a dead end.

**Indexes (RD-DEF 11, corrected: the Community cap of 12 static indexes was lifted on 2024-05-22, [#18564](https://github.com/ravendb/ravendb/discussions/18564), so verify with `/license/status`; multi-map stays preferred).** Proposed:

| Index | Engine | Purpose |
|---|---|---|
| `RefLocalities/Typeahead` | Lucene (~~automatic, X18~~ fuzzy by default, RD10) | Multi-map over places and postal localities, one entry per validity segment |
| `RefStreets/Typeahead` | Lucene (~~automatic, X18~~ fuzzy by default, RD10) | One entry per street period; a query always carries a municipality or postal-code filter, so it never scans millions |
| `RefPlaces/ByParent` | ~~Corax, pinned~~ ~~engine pinned by Spark (RD10)~~ server default (X18 as built, RD11): no searchable field, so not switched to Lucene | Children as of a date |
| `RefPlaces/ByCode` | ~~Corax, pinned~~ ~~engine pinned by Spark (RD10)~~ server default (RD11): no searchable field; code lookups are exact (an opt-out would apply if a code field were ever made searchable) | Who held code X on date D |
| `RefLineageEvents/ByParticipant` | ~~Corax, pinned~~ ~~engine pinned by Spark (RD10)~~ server default (RD11): no searchable field | Lineage per place |

~~Non-fuzzy indexes pin Corax explicitly, because Lucene is the default on Professional and Enterprise and an unpinned index would change engine on a licence upgrade.~~ *(Superseded by RD10 / commerce D38, round 3: the plugin no longer pins engines itself and has no "non-fuzzy index" class.)* ~~**Spark pins the engine explicitly on every index it registers** (commerce X18 as revised): Lucene where the index has a searchable field that has not opted out of fuzzy matching, otherwise the engine it pins at registration, so a licence upgrade (Lucene is the default on Professional and Enterprise) still never switches an index.~~ *(Superseded by RD11, 2026-10-20: settled against X18 as built.)* **Spark sets Lucene on an index with a searchable field (or an explicit `[Fuzzy]` opt-in) and leaves every other index on the server default** (`SparkSearchEngine.Apply`, `libs/spark/MintPlayer.Spark/Indexes/SparkSearchEngine.cs`); an engine an index pins in its own constructor is kept. The plugin pins nothing (no `Corax` in `libs/refdata`). The three lookup indexes have no searchable fields, so they run whatever the server defaults to: Corax on Community and Developer, Lucene on Professional and Enterprise. A licence upgrade can therefore switch them on a reset or a new definition; that is accepted, because they hold exact keyword lookups only, which both engines answer alike, and Spark's search adds fuzzy clauses only to an index the server confirms runs Lucene (`SparkSearchEngine.RunsLuceneAsync`). Spark's own library indexes count against the same 12 (RS9) (~~12~~: cap lifted 2024-05-22, see above). The owner rule "indexes always live in the application" meets a reusable plugin here: Q-R6.

### 4.11 Public API (RD-DEF 3)

An endpoint group `/api/v1`, outside `/spark`, built from generator endpoints:

```
GET /api/v1/suggest?q=kruish&country=be&kinds=&within=&asOf=&lang=nl&limit=10     places + postal localities
GET /api/v1/streets/suggest?q=kerk&country=be&municipality=places/be/nis/45068&postalCode=9770
GET /api/v1/places/{id}?asOf=&lang=           GET /api/v1/resolve?ids=a,b,c&asOf=&lang=
GET /api/v1/by-code/{cc}/{scheme}/{code}?asOf=      GET /api/v1/postalcodes/{cc}/{code}
GET /api/v1/countries?lang=                   GET /api/v1/countries/{cc}/profile
GET /api/v1/timezones?lang=                   CLDR exemplar cities (§4.13)
GET /api/v1/releases/latest                   GET /api/v1/releases/{release}/…   (manifest, files, deltas)
GET /api/v1/changes?since={release}           GET /api/v1/sources                (attribution, §4.12)
```

```json
{
  "release": "2026.11.0", "asOf": "2026-10-17",
  "attribution": [ "Statbel (CC BY 4.0)", "BOSA BeST Address (CC BY 4.0)" ],
  "items": [ {
    "type": "postal-locality",
    "label": "9770 Kruishoutem", "secondary": "Kruisem, Oost-Vlaanderen",
    "matched": { "value": "Kruishoutem", "lang": "nl", "kind": "official", "ranges": [ [ 5, 11 ] ] },
    "postalCode":   { "id": "postalcodes/be/9770", "code": "9770" },
    "locality":     { "key": "kruishoutem", "place": "places/be/nis6/45017A" },
    "municipality": { "id": "places/be/nis/45068", "name": "Kruisem" },
    "path": [ { "id": "places/be/nis/40000", "kind": "be:province", "name": "Oost-Vlaanderen" } ],
    "timeZone": "Europe/Brussels", "status": "current", "successors": []
  } ],
  "historyKnownSince": "1970-01-01", "next": null
}
```

"Kruishoutem" resolves three ways, each correct: the former municipality 45017 (history mode), the section, and the postal locality 9770 (what users type).

- **Authorization.** `group.AllowAnonymous()`, the share-link precedent. The API **never answers 401**: ng-spark's auth interceptor sends a consumer's user to the sign-in page on any 401, cross-origin URLs included (`libs/node_packages/ng-spark/auth/interceptors/src/spark-auth.interceptor.ts:12-28`). No match is an empty 200.
- **No anonymous Spark rights** on reference types. `/spark/*` answers `Access-Control-Allow-Origin: *` by default (`docs/guide-cors.md:29-40`), so every anonymous grant would be a second, unversioned public API. The contract is `/api/v1`.
- **CORS:** a named policy on the group only: any origin, GET and HEAD, no credentials, `ETag` and `Retry-After` exposed. Never a default policy, never `app.UseCors(...)`.
- **XSRF:** `.SkipXsrfToken()` on the group; otherwise the XSRF cookie forces `Cache-Control: private` (`SparkMiddleware.cs:380-382`). Not `.ShortCircuit()`, which skips the rate limiter too.
- **ETag** = `W/"{release}:{sha256(normalized request incl. the effective asOf + API version)}"`. A matching `If-None-Match` gets **304 without a query**, since the release id is in memory. RavenDB's `ResultEtag` is unusable: it ignores the query text.
- **Caching:** `Cache-Control: public, max-age=300` on suggestions; long-lived and immutable on versioned release files. `lang` is a query parameter, not `Vary: Accept-Language`.
- **Rate limiting:** a named token-bucket policy on the group, partitioned by IPv4 address or IPv6 **/64** (X10), with **CORS-readable 429s** and `Retry-After` (RX1). `spark.AddRateLimiter()` keeps metering `/spark`; `/api` stays out of its `PathPrefixes` (which replace the defaults).
- **Caps** (proposed defaults, configurable): `q` 1–64 characters after folding and at most 5 tokens, no control characters (else 400); the last token at least 2 characters (1 for CJK); `limit` default 10, maximum 20; `RequestAborted` plus a short timeout on every query; query parameters only.
- **Privacy:** typed fragments together with an IP can be personal data, so only aggregate counts are logged.
- **Bulk users sync releases** instead of crawling the API. That also keeps the API terms within CC BY's "no additional restrictions" (§4.12).

### 4.12 Attribution (RD-DEF 4)

- **Every record** carries its source ids (`Source`). **Every response** carries a compact `attribution` array. **`/api/v1/sources`** and a sources page list each source with its licence link, credit line, version and a "modified" notice.
- **Our terms add no restriction** beyond the upstream licences (CC BY 4.0 §2(a)(5)(B)): rate limits and fair use only, never "no redistribution". They require downstream users to keep the attributions and pass on **G-NAF's clause**: the data may not be used to generate addresses for mail unless each address is verified against a second source.
- **Sources are named as sources, not endorsers** (INE, StatCan, INSEE and others forbid implied endorsement).
- **swisstopo** requires attribution "in every case", and **IGN Spain** requires its formula in the service's own metadata: both appear in every response that carries their data and in the service self-description.

Credit lines (research §4; re-checked on every import):

| Source | Credit line |
|---|---|
| BeST Address | "Digitaal Vlaanderen (Vlaams Gewest), Paradigm (Brussels Hoofdstedelijk Gewest), Service public de Wallonie (Waals Gewest), via FOD BOSA BeST Address (https://opendata.bosa.be/)", a CC BY 4.0 link, and a statement that the data was modified |
| Statbel | "Source: Statbel", a link to the licence (https://statbel.fgov.be/en/cc-40), and a statement of changes |
| Flemish register | "Bron: Digitaal Vlaanderen" |
| Tier-1 registers | One line per register, from research §4 (e.g. "© swisstopo"; "Source : Insee" with the update date; "Elaboración propia con datos extraídos del sitio web del INE: www.ine.es" and "Obra derivada de CartoCiudad <fecha> CC-BY 4.0 scne.es"; the G-NAF EULA line) |
| GeoNames | "Contains data from GeoNames (https://www.geonames.org), licensed under CC BY 4.0, modified"; for GB postal codes the Royal Mail notice |
| CLDR | "Includes Unicode CLDR data, Copyright © 1991-2026 Unicode, Inc., under the Unicode License v3", with the licence text reproduced |
| Wikidata, IANA registry | Credited although CC0 does not require it |
| NGA GNS | NGA's citation, copied from https://geonames.nga.mil/ |

### 4.13 Time zones (RD-DEF 7, D33–D36, K10)

- **Places carry an IANA zone id** (`TimeZone`), so a venue gets its zone from its address and the commerce event page can add "20:00 in Brussels" (commerce §4.17, D33). How the zone is assigned in countries with several zones (US, AU, ES with the Canaries, FR overseas) is Q-R10.
- **CLDR exemplar cities** ("Brussel", "Bruxelles") are ingested by the global baseline and served at `GET /api/v1/timezones?lang=`. The client package registers an `ISparkTimeZoneNames` provider, which is **step 1** of D36's fallback chain: the reference-data provider → the platform-localized generic name → the IANA-derived English city. Results are cached per (zone, language) and fetched once per session; rendering never waits on them.
- **No zone arithmetic here** (K10): the plugin stores and serves zone ids and labels; conversion and formatting belong to the core built-ins (X16, D36).

### 4.14 House numbers (RD5)

- **Not imported.** House numbers stay free text, with Belgium's bus/box, checked by the country profile's format rules.
- **`IHouseNumberValidator`**, an optional hook in the client package, with **one** shipped implementation: Belgium's free, keyless [BeST API](https://best.pr.fedservices.be/api/opendata/best/v1/belgianAddress/v2/doc/openapi.yaml) (an example for hosts; "zonder garanties"; `/addresses` refuses queries without a narrowing parameter).
- **Never blocking.** A timeout or an error accepts the value and flags it "unverified".

### 4.15 Client packages

**.NET (`MintPlayer.Spark.ReferenceData.Client`).**
- `spark.AddReferenceDataClient()` binds `Spark:ReferenceData:Client`. `BaseAddress` must be an absolute http(s) URL and has **no default**: unset means free-text mode (RD-DEF 8).
- The release sync service (§4.7) and the read-only local types under the client's library layer.
- A typed `IReferenceDataClient` (suggest, resolve, by-code, profile, time-zone names).
- **`Address` behaviour:** an untyped `IBeforeSave` interceptor that throws `SparkValidationException` for invalid fields (shape-only checks in free-text mode); rendering of the address lines from the country profile's template; the house-number hook (§4.14).
- **The `Address` value** (placement: Q-R7):

```json
{
  "Country": "countries/be",
  "Street":       { "Id": "streets/be/vlg/…", "Name": "Kerkstraat" },
  "HouseNumber": "12", "Box": "3",
  "PostalCode":   { "Id": "postalcodes/be/9770", "Code": "9770" },
  "Locality":     { "Key": "kruishoutem", "Name": "Kruishoutem", "Place": "places/be/nis6/45017A" },
  "Municipality": { "Id": "places/be/nis/45068", "Name": "Kruisem" },
  "TimeZone": "Europe/Brussels",
  "Lines": [ "Kerkstraat 12 bus 3", "9770 Kruishoutem" ],
  "RefData": { "Release": "2026.11.0", "Verified": true, "HouseNumberVerified": false }
}
```

Free text is always possible (`Verified: false`): new streets appear before the registers have them, and many countries have no street data at all. Venues and seller profiles render live from the ids and show a successor with a "changed on" hint when a place has ended. Invoices keep `Lines` as the legal snapshot.

**Angular (`@mintplayer/ng-spark/reference-data`, an ng-spark entry point, so an ng-spark minor bump).**
- `REFERENCE_DATA_CONFIG` **without a default**, set by `provideReferenceData({ baseUrl })`. A missing provider fails loudly (NG0201); `null` is the explicit free-text mode.
- HTTP through `new HttpClient(inject(HttpBackend))`, which bypasses every interceptor: no bearer or cookie logic towards a public origin, no sign-in redirect. The language goes as an explicit `lang` parameter.
- `<spark-address-input>`, a ControlValueAccessor: the country picker uses `Intl.DisplayNames` + `Intl.Collator` over the ISO 3166-1 codes (the IANA registry's region subtags, CC0). The code list ships with the entry point, because no `Intl` API enumerates regions and free-text mode has no instance to ask. The locality and street boxes are typeaheads with debounce and `switchMap` cancellation; the steps follow the country profile; free text is always allowed.
- **`@mintplayer/ng-spark/reference-data/spark`**: `sparkAddressRenderers` to spread into an app's renderer list. It needs the po-form fix (X15): today a single `AsDetail` attribute can never get an edit renderer (`spark-po-form.component.html:381-398`), and renderer edits skip `noteChange`.
- **ng-bootstrap:** `bs-typeahead` gets debounce, cancellation, an item template and form-control support (RX5), in the mintplayer-ng-bootstrap repository, in the same unit of work (one-PR rule).

### 4.16 Demo app (RD-DEF 2)

- **`apps/ReferenceData`** with `ReferenceData` (host + ClientApp), `ReferenceData.Library` and `ReferenceData.Tests`. Ports **https 5017 / http 5018** (free: commerce's demo uses 5015/5016, the warehouse demo 5019/5020). **Local only.**
- **Admin sign-in** with a local password; an Administrators group holds the import actions and the import-run pages.
- **A consumer page** in the same app uses the client packages against the instance itself: the address form, free-text mode, and a sync status.
- **Basic seed set (RD9, round 3):** seeded on first start and in tests (§3.2), so the address form, the as-of queries and commerce's E2E work without a full import. The admin's "Import now" still runs full imports locally.
- **Database:** `RefData` on the local development server (localhost:8080, Developer licence). Spikes pin RavenDB to 3 cores and use no feature Community lacks (commerce D24, S18).
- **The new-app checklist** (commerce research §10; research §7): `package.json` workspaces; the slnx folder; `pull-request.yml` build list, `--spark-verify-model` and `--spark-verify-security` loops; `regenerate.yml`; `tools/verify-ngsw-config.test.mjs` (`/api` and `/health` excluded both ways, and the app added to the list of Spark apps); the SPA fallback excluding `/api`; `/health`; the `test.yml` matrix if the suite is long; `securityPosture.txt` and the route-table snapshot baselines.
- **`.gitignore` traps for folder names:** `[Rr]eleases/`, `[Ll]ogs/`, `*.log`, `publish/`, `**/[Pp]ackages/*`, `*.[Cc]ache` and `Backup*/` would silently untrack a fixture folder or the ledger (research §7). The ledger is `.ndjson`, never `.log`.

---

## 5. Framework additions (Spark core and existing libraries, same PR)

> **Source-generator programme (commerce X20, K12, accepted 2026-10-17)** applies here:
> - **TypeScript contracts** for the `reference-data` entry points (typeahead results, address form, release manifest types) come from the C#→TS tool;
> - the extended **`GenerateIndexGenerator`** brings sanitized per-language field names (X17) and the fuzzy marker (X18 → Lucene; since RD10 an opt-out marker, fuzzy being the default);
> - **library-declared indexes** are emitted into the host app (X19, Q-R6);
> - **enum-backed lookups** (G1) cover kinds such as place kinds and name kinds, through `translations.json` keys;
> - the **translation-key guard** covers this plugin's layers.
>
> Not generated, per the study: importer parsers and the lineage decision table.

**Already planned in the commerce PRD** (land in commerce M1; this plugin depends on them):

| # | Addition | What this plugin needs from it |
|---|---|---|
| X3 | Atomic natural-id create (fixes commerce F6, the probe-then-store race in `DatabaseAccess.cs:250-276`) | Embedded seed sets (country profiles, abbreviation tables) and admin creates with deterministic ids never overwrite each other silently |
| X10 | Identity-keyed rate-limit partitions + **IPv6 /64** grouping (fixes commerce F5, `SparkBuilderRateLimiterExtensions.cs:115`) | The public API's limiter: an IPv6 client controls at least a /64 |
| X15 | po-form edit renderer for a single `AsDetail` value object, plus `noteChange` for renderer edits | `<spark-address-input>` inside generic forms (§4.15) |
| X16 + D36 | `<spark-date-time>`, `ISparkDateTimeFormatter`, and `ISparkTimeZoneNames` with its fallback chain | The time-zone label provider (§4.13) |
| X17 | **One name system:** `TranslatedString` redesigned (RD7). Includes the Spark fixes from research §5 and §7: sanitized per-language index field names (`IndexNaming.LanguageField`, `IndexNaming.cs:91-92`), the RFC 4647 chain on server and client, a factory for any language, and a startup warning for `culture.json` tags that are not well-formed BCP 47 | Place, postal-locality and street names (§4.9); FR2, FR3 |
| X18 | ~~**Lucene set automatically** on an index with a fuzzy-marked field or search rule; deterministic side-by-side rebuild on an engine change; an analyzer for fuzzy queries against unmarked indexes (RD8b)~~ **Revised (RD10 = commerce D38): fuzzy by default.** Every index with searchable fields on Lucene, typo-tolerant search by default with a per-field opt-out, the engine pinned explicitly on every index, side-by-side rebuilds of existing search indexes; the analyzer flags fuzzy queries on opted-out fields | The two typeahead indexes (§4.10) |

**New in this PRD:**

| # | Addition | Why | Evidence of the gap |
|---|---|---|---|
| RX1 | **CORS-readable 429s:** `RateLimiterOptions.OnRejected` adds `Retry-After` and, on paths whose CORS policy allows it, `Access-Control-Allow-Origin` | A browser on another origin sees a network error instead of a 429 today (FR4) | The limiter runs before the CORS branches (`SparkMiddleware.cs:322` vs `:333-350`); no `OnRejected` or `Retry-After` anywhere; empty 429 body (`docs/Spark-API-Specification.md:480`) |
| RX2 | **The route-table snapshot records skip-XSRF** per route | A public endpoint that keeps minting XSRF cookies silently becomes uncacheable (FR1) | `RouteTableSnapshot.cs:141-185` records anonymous access, antiforgery, the rate-limit policy and CORS, but not skip-XSRF |
| RX3 | **Anonymous endpoints in review:** `[AllowAnonymous]` and unannotated endpoints listed in `securityPosture.txt`, or an exact-set anonymous-surface helper in `MintPlayer.Spark.Testing` that every app uses | The public API widens the anonymous surface on purpose; reviewers must see it (FR5) | Such endpoints never appear in the posture; CodeCoverage wrote its own controller-only test (`AnonymousSurfaceTests.cs:13-19,88-91`); no fallback policy (`SparkMiddleware.cs:60-62`) |
| RX4 | **Library-declared index definitions emitted into the application** by a generator (if Q-R6 is decided that way) | A reusable plugin needs indexes; the owner rule keeps index classes in the app (K12) | `[GenerateIndex]` always stores all fields and has no engine or analyzer setting (`GenerateIndexGenerator.Producer.cs:288-298`); a fan-out multi-map can't be expressed with it |
| RX5 | **`bs-typeahead`:** debounce, cancellation (`AbortSignal`), an item template, ControlValueAccessor (mintplayer-ng-bootstrap) | The address form | It fires on every keystroke, renders only `suggestion.text`, is not a form control, and is used nowhere in this repo (research §7) |

**K12 (source generators).** The study's results are folded in here when they arrive. Candidates visible in this design: RX4; one generated registration line per adapter assembly; the release-format DTOs and JSON schemas generated from one contract.

---

## 6. Defects found during the investigation (fixed in this PR, red test first)

| # | Defect | Evidence | Severity |
|---|---|---|---|
| FR1 | SeoTester's catalogue, badge, bot and legal endpoints set `Cache-Control: public` without `.SkipXsrfToken()`, so the XSRF cookie probably forces `private` and nothing caches them | `apps/SeoTester/SeoTester/Catalogue/CatalogueEndpoint.cs:54-55`; `SparkMiddleware.cs:380-382` | Performance (suspected; confirm red). **Production app** (§8) |
| FR2 | Generated per-language index field names are not sanitized: `Description_zh-Hant` is not a C# identifier, so a `[GenerateIndex]` entity with a script-tagged culture probably fails to build | `libs/source_generators/MintPlayer.Spark.SourceGenerators/Naming/IndexNaming.cs:91-92` | Correctness (latent, inferred; no test covers it). Fixed by X17 |
| FR3 | Server and client resolve `TranslatedString` differently: .NET falls back to the base culture, TypeScript jumps to `en`; for `sr-Latn` they disagree | `libs/model/MintPlayer.Spark.Model/TranslatedString.cs:22-34`; `libs/node_packages/ng-spark/models/src/translated-string.ts:3-13` | Correctness. Fixed by X17 |
| FR4 | 429 responses are CORS-opaque, carry no `Retry-After` and have an empty body | `SparkMiddleware.cs:322,333-350`; `docs/Spark-API-Specification.md:480` | Correctness. Fixed by RX1 |
| FR5 | `[AllowAnonymous]` and unannotated endpoints are invisible to `securityPosture.txt`, and the route snapshot omits skip-XSRF | `RouteTableSnapshot.cs:141-185`; `apps/CodeCoverage/CodeCoverage.Tests/_Infrastructure/AnonymousSurfaceTests.cs:13-19,88-91` | **Security review blind spot.** Fixed by RX2, RX3 |
| FR6 | Natural-id create race | = commerce F6 | Fixed by X3 |
| FR7 | Rate limiting keys on the exact IP; no IPv6 /64 grouping | = commerce F5 | Fixed by X10 |
| FR8 | Live updates subscribe per collection through the Changes API without coalescing, so bulk writes into a live-watched collection may set off re-query storms | `libs/spark/MintPlayer.Spark/Live/LiveHub.cs:219-221` | Performance (suspected; RS16 decides). Reference types are non-live regardless |
| FR9 | Documentation drift: `docs/seo_analyzer_PRD.md:159` cites old `pull-request.yml` lines (now 101/112/137); `docs/PRD-Migrations.md:85` ("the loser skips and serves") no longer matches the runner; `docs/guide-search.md:74-76` and `SearchAttribute.cs:7-8` describe a `{Name}Sort` companion where the generator emits `{Name}Search`; `docs/Spark-API-Specification.md:752` shows a GET execute with string `sortColumns`; the README tree (`README.md:153-154`) omits SeoTester; `apps/CodeCoverage/README.md:774-777` suggests a periodic backup task that production's Community licence reports as unavailable | research §7 | Documentation |

---

## 7. Spikes

Every spike ends with a measured result recorded in §13: numbers, file:line, or a red→green test. Environment (commerce D24): the owner's machine in windows the owner chooses, RavenDB pinned to **3 cores**, **no Community-missing feature**. Full imports are manual local runs (RD2).

| # | Spike | Question | Pass criterion |
|---|---|---|---|
| RS1 | Belgium full import | How long, and how much memory, on a 3-core Community-like server? | Time and peak memory of app and RavenDB recorded for all BE collections; memory stays flat as file size grows (streaming proven) |
| RS2 | Zero-write re-import | Is a re-import really a no-op? | Importing the same source versions again writes **0 documents**: change vectors unchanged, no indexing work, no subscription wake-up |
| RS3 | Ledger conflict stop | Does a history rewrite stop the import? | A fixture source that reassigns a code stops that source's import with a visible conflict; the ledger and the documents are unchanged; other sources continue |
| RS4 | Lineage decision table | Does every row produce the right periods, edges and as-of answers? | One test per row: **Kruisem** merge, **Ooike** split 1977-01-01 (continue + cede), **Hasselt** 71022 → 71072 (as Q-R1 decides), **Zwijndrecht** sections re-parented across provinces, **Borsbeek** demoted to district, **Bastogne** 1/2 December (as Q-R3 decides); plus recode (La Louvière), rename (Sambreville), restore and code reissue rows. Each asserts as-of answers the day before and the day of the change |
| RS5 | Typeahead latency | Server-side p95 under debounced load | p95 ≤ 50 ms server-side at 50 requests/s on the 3-core server with BE + one large tier-1 country loaded (a proposed target, confirmed or replaced by the measurement) |
| RS6 | Fuzzy fallback | Correctness and determinism of step 2 | A misspelling set (e.g. "Kruishoutm", "Oudenarde", "Bruxele") finds the intended place in the top 3; fuzzy runs only when the prefix step returns fewer than *k*; how multiple fuzzy tokens compose on the document-query API is settled; **the same order on two databases holding different country subsets**; X18 set Lucene without any engine code in the app |
| RS7 | Release delta exactness | Do deltas reproduce the target release? | Applying delta(A→B) to a database at A gives exactly B's per-collection digests; a release that drops an id is refused; a digest mismatch triggers a full re-import of that collection |
| RS8 | Per-adapter fixtures | Do the parsers survive real data? | Every adapter (18 + global) parses fixtures cut from the real files: cp1252, BOMs, Excel-serial dates, Lambert 72 and 2008 columns, combined NIS6 parts, ZIP64 where relevant, dated file names discovered by pattern |
| RS9 | Index limits | Does any index cap apply on Community? | `/license/status` read on a Community server and recorded (caps lifted on 2024-05-22, #18564). Only if a cap applies: `apps/ReferenceData` with every adapter and Spark library ≤ the cap, consolidating into multi-map indexes otherwise. Always: static index count recorded for the instance and for a consumer app |
| RS10 | Determinism | Do the same inputs give the same bytes everywhere? | Building a release twice from the same inputs gives identical manifests; **Windows and Linux instances produce identical search keys and release bytes** for BE plus the fixture corpus (normalization and transliteration do not depend on the platform ICU) |
| RS11 | Wikidata extraction | Per-country SPARQL extract or filtered dump? | Time, size and reproducibility recorded for all countries; the chosen method re-runs to the same SHA-256 when Wikidata has not changed |
| RS12 | Query shapes on the server versions | `startsWith` on array fields, `order by` on `Rank`, Lucene fuzzy per token | Work on the embedded test server (7.2.x) and on 7.1.10 (what this repo's production runs, so a likely host version); every search measurement in the repo so far was on 7.2.x |
| RS13 | Consumer sync on Community | How fast does a consumer catch up? | A Community-pinned consumer applies a monthly delta in under a minute; startup is not blocked; a re-run writes 0 documents |
| RS14 | Belgian postal localities | Do BeST postal info and postal streets cover what people write? | (code, name) pairs derived without bpost are compared, **manually and locally**, with bpost's 2,720 geographic rows: every gap explained; nothing from bpost committed |
| RS15 | BeST house-number validator | Latency and failure behaviour | Typical latency recorded; a timeout accepts the value flagged "unverified"; a recorded real response is the fixture |
| RS16 | Bulk writes and live updates | Does a bulk import into a live-watched collection cause re-query storms (FR8)? | Measured with a watching client; reference types confirmed non-live; FR8 confirmed red or closed |
| RS17 | Release signing | Ed25519 in .NET 11: built in, or a vetted library (licence checked)? | Sign and verify round trip; a tampered manifest or file refused; the dependency and its licence recorded |
| RS18 | Public API hardening | CORS-readable 429, IPv6 /64 partitions, ETag 304 | A cross-origin browser call sees 429 with `Retry-After`; two addresses in one /64 share a bucket; a repeated request gets 304 without a database query |
| RS19 | Large-country import | FR or US streets on the 3-core server | Time, peak memory, database and index size recorded; memory flat across file sizes |
| RS20 | Time-zone assignment | Q-R10's method on multi-zone countries | 100% of fixture places in US, AU, ES and FR get the right zone, or null with a stated reason; every id validated against the tz database |
| RS21 | Machine transliteration for keys | Which implementation for true alphabets? | ICU P/Invoke, AnyAscii (ISC) and own tables compared on Cyrillic, Greek, Armenian and Georgian samples; the choice gives identical keys on Windows and Linux (RS10) and is pinned in `KeyVersion` |
| RS22 | Lineage derivation for 1977 | How much of the 1977 fusion is derivable without curation (RD4)? | Share of 1,832 codes ending 1976-12-31 that get a demote or merge event from REFNIS + NIS6 name matching; every unresolved one listed |
| RS23 | Optional commerce sources (round 3) | TEDB rates and one holiday source per seeded country: format, change check, licence | Fixtures from the public downloads parse in CI; licences recorded; no rate is ever applied automatically (commerce GC10, GC11) |

---

## 8. Production impact (VPS) (K9)

**The reference-data app: none.** It is not deployed by us (K8 revised); it runs locally and under test, and any host may deploy its own instance. Round 3 (RD9) develops it on this branch with a basic seed set; that changes nothing on the VPS.

Round 3 (RD10 = commerce D38) does reach production apps: their search indexes switch to Lucene and rebuild side by side on the next deploy. That row is in commerce §8.

Changes of this PRD that do reach production apps:

| Change | App | When | Safeguard |
|---|---|---|---|
| FR1: `.SkipXsrfToken()` on SeoTester's public endpoints, if the red test confirms them uncacheable | SeoTester (prod) | Deploy of this PR | Red test first; the route-table snapshot (RX2) shows the change; post-deploy header check |
| RX1 CORS-readable 429s, X10 IPv6 /64 partitions | Every app | Deploy of this PR | Already listed for commerce (X10); 429 bodies and headers only change on rejection |
| X17 `TranslatedString` redesign | Apps with stored `TranslatedString` values | Deploy of this PR | Commerce S24 (inventory) and S22 (migration dry runs) |
| Regenerated `securityPosture.txt` if RX3 adds anonymous endpoints to it | Every app | This PR | `--spark-verify-security` in CI |

---

## 9. Out of scope (genuinely not being done)

- **House-number import.** About 6.7–7.2 M documents and 13 GB of XML a week for Belgium alone; a validator hook covers the need (RD5).
- **OpenStreetMap and Overture.** ODbL share-alike would extend to the backing database of a public API (research §3).
- **Countries beyond the RD2 adapters.** Streets without open postcodes (DE, IT, GB, CA, NZ, LT, PT, RS, IS, JP, TW), registers gated by an approval or an unclear licence (SE, SK, HR, UA, BR, MX, KR), and RU (a policy decision) get the global baseline only. Hosts can add adapters through the public interfaces (K7).
- **Hand-curated history,** including anything before 1970 in Belgium. Only what authorities publish machine-readably (RD4).
- **bpost's postal-code list,** the NGI GeoPackage/WMS and other non-republishable files (research §1).
- **Boundaries and geometry.** The typeahead needs none; GISCO geometries are non-commercial.
- **A public deployment by us** (K8 revised).

---

## 10. Milestones (one PR)

This whole PRD is commerce milestone **M19**. Tests run once at the end, batched with commerce M22 (repo rule). Each milestone is committed.

| # | Milestone | Depends on |
|---|---|---|
| R0 | Spikes RS1–RS23 (results logged in §13) | — |
| R1 | Framework: commerce X3, X10, X15, X16 + D36, X17, X18 (commerce M1); RX1–RX5; defects FR1–FR5, FR8, FR9 | commerce M1, RS16, RS18 |
| R2 | Abstractions: document types, country profile, `Address`, DTOs, release-format contract and JSON schemas | R1 (X17), Q-R7, Q-R14 |
| R3 | Core engine: id grammar and ledger, lineage decision table, derived fields, search-key pipeline, rank, hash, as-of resolution, the display rule | R2, RS3, RS4, RS21, Q-R1–Q-R5, Q-R11–Q-R13 |
| R4 | Sync pipeline (RD1): source registry and configuration, cron, change checks, downloads, checkpointed import queue, diff and retire, import runs and admin actions | R3, RS1, RS2 |
| R5 | Releases (RD6): canonical writer, manifest, deltas, signing, verify command, release store | R4, RS7, RS10, RS17, Q-R8, Q-R17 |
| R6 | Belgium adapter in depth | R4, RS8, RS14, RS22, Q-R16 |
| R7 | Global baseline adapter, including CLDR exemplar cities and time-zone assignment | R4, RS11, RS20, Q-R10 |
| R8 | The 17 tier-1 adapters | R4, RS8, RS19 |
| R9 | Search and the public API | R3, RS5, RS6, RS9, RS12, RS18, Q-R6, Q-R15 |
| R10 | Client packages (.NET and Angular) and the ng-bootstrap typeahead (RX5) | R5, R9, RS13, RS15, Q-R9, Q-R18 |
| R11 | Demo app `apps/ReferenceData`, new-app checklist, **basic seed set (RD9)**, E2E | R6–R10 |
| R11b | Optional commerce sources: TEDB VAT-rate suggestions, holiday calendars, VAT territories in country profiles (round 3; commerce GC10, GC11, GA4) | R4, RS23 |
| R12 | Commerce integration: `Address` on venues, seller profiles and orders; venue zone from the address (commerce M19, second line) | R10, commerce M4, M14 |
| R13 | Guides and READMEs; the release-format specification; the sources page | all |
| R14 | Test sweep and browser verification, with commerce M22 | all |

---

## 11. Open decisions

> **All accepted as recommended in Round 2 (owner, 2026-10-17); see §13.** The items below are now decisions.

- **Q-R1 Hasselt 2025: recode or merge?** RD3's log calls the row "Hasselt recode"; research §6 classifies it as a merge into a new unit, because Kortessem joined, Statbel issued a new code (71022 → 71072) and Flanders re-identified everything. *Recommendation:* apply the generic rule mechanically: one code → one code with no other participant is a recode; n → 1 under a new code is a merge (new place `places/be/nis/71072`, 71022 and 73040 end with `whole` edges). Users never see the difference: both display "Hasselt".
- **Q-R2 Street identity across the Flemish re-identification.** Flanders gives every street of a merging municipality a new id; the 2025 diff files map old to new. *Recommendation:* a 1 → 1 mapping **continues** the street (same document, new dated code), so consumers' street references survive a merger; splits and merges of streets create new documents with lineage.
- **Q-R3 Date precedence when sources disagree** (Bastogne: Statbel 2024-12-01, decree and Wikidata 2024-12-02). *Recommendation:* each country adapter names its primary source for admin units (Statbel for Belgium); its date drives the periods, and the other dates are kept on the lineage event's sources.
- **Q-R4 Ambiguous lineage without curation (RD4).** *Recommendation:* record the event as unresolved (the place ends, successors unknown, flagged), keep importing, and list unresolved items in the release notes; only ledger conflicts stop an import.
- **Q-R5 Where the ledger lives for a third-party instance.** *Recommendation:* the committed ledger per country ships inside its adapter package, so independent instances mint the same ids; units first seen after that package version go into an instance-local append-only ledger collection, and the next adapter release absorbs them.
- **Q-R6 Indexes of a reusable plugin versus the owner rule "indexes always live in the application".** *Recommendation:* RX4: the plugin declares its index definitions and a generator emits them into the host application, so the classes live in the app (rule kept) and every host gets the same indexes without copying code (K12).
- **Q-R7 Where the `Address` type lives.** RD-DEF 1 lists the `Address` value under `.Client`, but an entity library may not reference ASP.NET Core (SPARKLIB001), and consumers declare `Address` properties in their entity libraries. *Recommendation:* the type in `.Abstractions` (with the generator reference `[ValueObject]` needs), its behaviour in `.Client`.
- **Q-R8 Release-file storage on the instance.** *Recommendation:* an `IReleaseStore` with a file-system default; hosts can plug in blob storage. RavenDB attachments rejected for multi-GB street files.
- **Q-R9 Notify consumers of a new release?** (RD1 mentioned WD1 push + reconcile; RD6 chose polling.) *Recommendation:* no push; the conditional GET on the manifest costs one request per schedule tick; a consumer-side "Sync now" admin action covers urgency.
- **Q-R10 Time-zone assignment in multi-zone countries** (US, AU, ES, FR overseas). *Recommendation:* the country's zone where it has one; otherwise a per-place source joined through GeoNames or Wikidata ids (GeoNames' per-feature time zone, its field and licence verified in R0); null with a reason where ambiguous; every id validated against the tz database. RS20 measures it.
- **Q-R11 Showing source romanizations that declare no system** (e.g. Wikidata `en` labels), given RD7 keeps machine transliteration for search keys. *Recommendation:* never display our machine transliterations; display source romanizations with origin `generated` only as the Latin fallback, labelled as such in the API.
- **Q-R12 Name kinds beyond RD7's four** (research §5 proposed short, postal, colloquial, abbreviation, reading). *Recommendation:* keep the four; postal spellings are `official` names of postal-locality documents; abbreviations are search-key classes; readings use the `ja-Hrkt` tag.
- **Q-R13 Order and separator for several official names** when the viewer's language is not one of them (Brussels in an English UI). *Recommendation:* the authority's order, joined with " - " (the OSM Brussels convention): "Rue de la Loi - Wetstraat".
- **Q-R14 Library aliases and entry-point names** (permanent once published). *Recommendation:* aliases `reference-data` (instance) and `reference-data-client` (consumer layer); ng entry points `@mintplayer/ng-spark/reference-data` and `@mintplayer/ng-spark/reference-data/spark`.
- **Q-R15 API keys or higher limits for heavy users?** *Recommendation:* not in v1; heavy users sync releases (§4.11), and a host can put its own gateway in front.
- **Q-R16 Walloon parts of municipality** (2,016 villages and hamlets in BeST, no parent field; more than the 1,489 Walloon NIS6 sections). *Recommendation:* import them as `be:locality` places linked to their municipality through the postal-streets file, so Walloon users find the names they write.
- **Q-R17 Release granularity.** RD1 records a release whenever an import changed something; with ~25 sources on their own schedules that can mean several releases a day. *Recommendation:* keep one release per changing run (deltas make each cheap) and add an optional coalescing window in configuration; consumers' schedules decide how often they pull.
- **Q-R18 Server-side country and language names in a .NET consumer** (RD-DEF 10 says "CLDR server"): fetched from the instance and cached, or a pinned CLDR subset embedded in `.Client`? *Recommendation:* embed a pinned subset (territory, language, script and currency names for the languages in the app's `culture.json`) in `.Client`, so mails and PDFs work in free-text mode too; the instance serves the same data to non-.NET consumers.

---

## 12. Risks

| Risk | Mitigation |
|---|---|
| Sources change shape: BeST's Lambert 2008 switch renames files (2026-10-19), Statbel changes apostrophes and mixes date formats, NIS6 re-codes sections, DK changes its credit line (2026-12-01) and requires an API key (2027-01-15), FR retired api-adresse | Pattern-matched file names, per-adapter real fixtures (RS8), change checks that record what changed, configuration for keys |
| Machine-unreadable facts stay missing under RD4 (Ooike's east part to Eine; Bevere's 1965 date) | The API states "history known since"; unresolved lineage is flagged (Q-R4); the decision table still tests the mechanics with fixtures |
| Determinism breaks across platforms or library upgrades (ICU differs between Windows and Linux) | Keys computed once at import and shipped as data; `KeyVersion`; RS10 and RS21 compare Windows and Linux |
| Community limits: one custom analyzer, side-by-side rebuilds doubling disk (~~12 static indexes~~: lifted on 2024-05-22, verify via `/license/status`) | Multi-map indexes, keyword analyzers only, RS9; engine pinned per index |
| Large countries (FR, US streets) outgrow a 3-core / 6 GB host | Measured in RS19; hosts choose countries (K7); streets are per-country opt-in |
| Licence obligations are missed (CC BY credits, swisstopo "in every case", IGN metadata, G-NAF mail clause, FI Posti terms) | Attribution per record and per response, the sources page, terms that pass clauses on (§4.12); research §4 credit lines re-checked on every import |
| The `TranslatedString` redesign (X17) touches production data | Commerce S24 inventory and S22 dry runs |
| A consumer depends on a live API that is down | Local copies for references; the free-text fallback flagged unverified (RD-DEF 8) |
| Typeahead scraping | Releases make scraping pointless; rate limits protect the instance |
| Fixture or ledger folders silently untracked by `.gitignore` | Folder naming rules (§4.16); `git status` check in R11 |
| PR size and duration | Accepted (K5); milestones committed separately; tests batched at the end |
| The seed set drifts from what a full import would produce (round 3) | Built from the adapters' real fixtures by the same pipeline and ledgers (§3.2), regenerated by a tool with a CI diff check, so a later import continues its ids |

---

## 13. Decision log

Owner answers are quoted where the record quotes them.

### Round 1 (owner, 2026-10-17)

- **K8 (owner, new scope):** the reference-data app, recorded in §1 (K8 row). Owner: "This will almost certainly also be in-scope, as a new live-deployed application." On Ooike the owner recalled "the 1990's"; research verified 1 January 1977 (Statbel REFNIS: `45033 … 01/01/1970 … 31/12/1976`).
- **K8 revised (owner, during RQ1):** "second thoughts - i don't think I'll be deploying this reference data app to my vps. If the code is written and can work properly, that would be great already". → Not deployed by us; must work locally and under test; any host can deploy its own instance; VPS constraints no longer apply to it. Supersedes WD11's "only the reference-data app goes live" for this app.
- **RD1 (owner, unprompted):** "for the per-country reference-data, wherever possible, the framework plugin should probably have a simple option/configuration value (eg cron-schedule) that first checks a checksum to see if a new version was made available, and fetches the online publicly available data again when changed, and somehow updates the data in the database". → `Spark:ReferenceData:Sources:{id}:Schedule` (6-field cron); change check first (ETag/Last-Modified, a published checksum, else our SHA-256); on change only: a resumable queued import, never in a migration; diff by source persistent id + version + content hash; retire, never delete; record a dataset release. Any host can run its own instance (K6, K7).
- **RD2 (RQ1, revised):** **C** — all 17 open-register countries + Belgium in depth + the global baseline. BE: history since 1970, sections, postcodes, streets. NL (BAG monthly free extract), FR (BAN files + INSEE COG; api-adresse retired), LU, CH (swisstopo, attribution in every response), LI, AT (BEV twice yearly), DK (Datafordeleren API key from 2027-01-15 → host secret), NO (+ Bring postcodes, NLOD), FI, EE, LV, ES (IGN credit formula in service metadata), PL (TERYT + PRG), CZ (RÚIAN), SI, US (TIGER/Line), AU (G-NAF mail clause passed to API users). Global baseline: Wikidata, GeoNames, NGA GNS, UN/LOCODE, CLDR, IANA subtags. Streaming, resumable importers; CI on real sample fixtures; full imports are manual local runs. *Evidence:* research §2, §3.
- **RD3 (RQ2):** **A** — the owner's mechanic with dates: one Place document per unit with dated (kind, parent) periods, dated names and codes; a LineageEvent per legal change (merge, absorb, demote, split, cede, rename, recode, reparent, restore); identity survives recode, rename and reparent, continues one level down on demotion, and ends only on a merge into a new unit or a split without a continuing part; a municipality and its same-name section are two places; "current" is computed at query time from an as-of date; integer date sentinels; a decision table with one test per row (Kruisem, Ooike, Hasselt, Zwijndrecht, Borsbeek, Bastogne). *Evidence:* research §6. Open: Q-R1, Q-R3.
- **RD4 (RQ3):** **A** — history depth is what authorities publish machine-readably: BE from 1970 (REFNIS) plus sections from NIS6 (older absorptions such as Bevere 1965 present as places without dated lineage); each country imports its register's history (about 20 countries; FR since 1943, CH since 1960); "history known since" per country profile; the API says when an as-of date predates it; **no hand curation**.
- **RD5 (RQ4):** **A** — house numbers are not imported; free text (plus BE bus/box) checked by the country profile's rules; an optional `IHouseNumberValidator` hook with one shipped implementation, Belgium's free keyless BeST API, as an example for hosts; timeout and non-blocking fallback (accept, flag "unverified").
- **RD6 (RQ5):** **A** — deterministic releases plus the same RD1 sync on the consumer side: canonical NDJSON per country and collection, a manifest (checksums, deltas, optional signature); consumers' cron + checksum sync imports deltas with skip-if-unchanged bulk inserts (re-import = 0 writes) and refuses releases that drop ids; consumers copy the small collections locally (countries, admin levels, places, postal codes) because Spark resolves `[Reference]` locally; streets via the live API; works for non-Spark consumers; the release format is a versioned public contract; a periodic full per-collection checksum triggers a full re-import of that collection on mismatch.
- **RD7 (RQ6):** Owner: "Probably A, but see if you can somehow just use one name-system - remember, no backward compat". → Redesign `TranslatedString` as the single name system (commerce X17): entries with value, RFC 5646 tag, optional kind (official/common/exonym/historic), validity, source and romanization origin (authoritative/generated); a one-line API for app texts; an RFC 4647 fallback chain on server and client; any language; sanitized index field names; the display rule available everywhere; country, language, script and currency names from `Intl.DisplayNames` (client) and CLDR (server), not stored per consumer; machine transliteration only for search keys (true alphabets). Cost: every existing value is migrated (commerce S24, S22).
- **RD8 (RQ7):** Owner: "If Lucene fuzzy can be used with any ravendb license, yes pick option C - otherwise we should let the framework provide a second option for the developer to choose from". → Verified that the engine is not licence-gated, so **C**: a Lucene index with fuzzy. Two-step typeahead (word prefix on import-time keys first; fuzzy fallback when too few results); deterministic re-ranking in C#; the free-text escape hatch kept. ~~New fact: Community allows **12 static + 24 auto indexes per database** ([ravendb.net/buy](https://ravendb.net/buy)).~~ **Corrected the same day:** those caps were lifted for Community on 2024-05-22 ([discussion #18564](https://github.com/ravendb/ravendb/discussions/18564)); the buy page's footnotes are stale.
- **RD8b:** Owner: "And perhaps lucene can be enabled on that index automatically". → X18: a fuzzy-marked field or search rule makes Spark set `SearchEngineType.Lucene` on that index; others keep the server default; an engine change rebuilds side by side; an analyzer flags fuzzy queries on unmarked indexes. Reading `TranslatedString.cs` (flat dictionary + `Key`, `Create(en, fr, nl)`, fallback ending in the first value available, lines 7-51) confirmed X17's scope.
- **RD-DEF (defaults, all accepted):**
  1. packages `MintPlayer.Spark.ReferenceData.Abstractions`, `MintPlayer.Spark.ReferenceData` (model, releases, sync service, API), `MintPlayer.Spark.ReferenceData.Sources.<Country>` per adapter + `.Sources.Global`, `MintPlayer.Spark.ReferenceData.Client` (consumer release sync, typeahead client, Address value, validator hook), ng `@mintplayer/ng-spark/reference-data` (typeahead + address form);
  2. `apps/ReferenceData` https 5017 / http 5018, local only, an admin local password for import triggers;
  3. an `/api/v1` AllowAnonymous group, CORS any origin without credentials, `.SkipXsrfToken()`, an IP rate limit with IPv6 /64 (X10) + the CORS-readable 429 fix, ETag = release id + request hash (304 without a query), `Cache-Control: public`, query parameters only;
  4. attribution: source id per record + attribution array per response + a sources page (credit lines from research);
  5. releases optionally Ed25519-signed (instance key; consumers configure the public key);
  6. the country profile: levels and labels per language, the municipality level, a hand-curated address-line template (no libaddressinput, which has no licence), the postcode regex, house-number rules, history known since;
  7. places carry an IANA zone id; CLDR exemplar cities are ingested and feed the D36 provider;
  8. consumer fallback: free text flagged unverified;
  9. postal localities from BeST and the registers; bpost excluded;
  10. country and language names via `Intl.DisplayNames` (client) and CLDR (server);
  11. ~~at most 12 static indexes (Community)~~, multi-map preferred. Corrected: the cap was lifted on 2024-05-22, so check `/license/status` (RS9);
  12. a hosted, resumable, streaming sync service (never a migration), bulk insert for the first load, diffs after, and an append-only committed id ledger that stops conflicting imports.

**Carried over from the commerce grilling** (they bind this plugin; full entries in commerce §13):
- **D33:** every displayed date and time follows the viewer's locale and zone; event pages add the venue-local time as a secondary label. Owner: "make sure the date/time's presented to the viewer are always localized - timestamps are mostly stored using DateTimeOffset - I think we already do have the mechanism setup correctly". → The venue's zone comes from its address's place (§4.13).
- **D34, D35:** the `<spark-date-time [value] [alsoIn] [mode]>` component and its .NET twin (X16).
- **D36:** Owner: "Do you think we could/should also add an optional service somehow that resolves the timezone code to the translated city-name through the newly implemented reference-data application, if wired up from the spark-application?" → The core `ISparkTimeZoneNames` with its fallback chain; this plugin ingests CLDR exemplar cities and provides step 1 (§4.13).
- **K10:** Owner: "Now we only really need to bother with timezones in one place - all the complex timezone logic can be offloaded to core-framework shipped built-ins".
- **K11:** Owner: "Anything that can be done through the W3 browser-api's should probably be done so". → Localized country, language, script and currency names come from `Intl.DisplayNames`; the API may serve only subdivisions, places, postal codes and streets to browsers.
- **K12:** Owner: "we should also check if there are code features we could/should simplify using a source-generator in this repo. Sometimes source-generators can significantly reduce the amount of code we write". → §5, when the study reports.
- **K9:** Owner: "A redesign would be allowed too, but we'll probably need to change things on the vps then". → §8 lists the production changes this PRD causes.
- **WD1:** push + reconcile is Spark's standard replication transport; RD6's releases serve reference-data consumers instead (§4.8, Q-R9).

### Round 2 (owner, 2026-10-17)

- **Q-R1 … Q-R18:** all §11 recommendations **accepted as written** ("Accept all"). §11 now records decisions:
  - Hasselt 2025 is a merge into a new unit, by the mechanical n→1 rule;
  - 1→1 street re-identification continues the street;
  - the primary source per country decides dates;
  - unresolved lineage is flagged, not fatal;
  - adapter-shipped id ledgers plus a local ledger;
  - **library-declared indexes emitted into the host app by a generator (RX4), applied to all three PRDs** (commerce X19; it supersedes the warehouse's runtime WX16 mechanism with compile-time emission);
  - `Address` in `.Abstractions`;
  - `IReleaseStore` with a file-system default;
  - polling only;
  - per-place time zones where a country has several;
  - machine transliterations never displayed;
  - the four name kinds kept;
  - authority order joined with " - ";
  - aliases `reference-data` / `reference-data-client` with entry points `reference-data` / `reference-data/spark`;
  - no API keys in v1;
  - Walloon localities imported;
  - a release per changing run, with an optional coalescing window;
  - a pinned CLDR subset in `.Client`.

### Round 3 (owner, 2026-10-19; recorded in commerce §13 Round 5)

- **RD9 (= commerce D39):** Owner: "2 days ago I "decided" not to develop or not to seed-data the "reference data" application (countries, cities, streets, ...) - But I think I'll do it anyway, just a basic set, some countries, some regions, some streets, ... Just to be able to test --- All of that is in-scope for the current branch". → The app is **developed on this branch** (R0–R14) with a **basic seed set** (§3.2): some countries, regions, places (with every decision-table case) and streets, built from the adapters' real fixtures through the same ledgers. Still not deployed by us (K8 revised). *Note:* the record holds no earlier decision not to develop it; K8 revised only dropped the deployment.
- **RD10 (= commerce D38):** Owner chose **"Fuzzy by default"**: every index with searchable fields runs on Lucene and search is typo-tolerant by default with a per-field opt-out; existing search indexes rebuild side by side, production apps included. → §4.10's "non-fuzzy indexes pin Corax" is **superseded** (struck in §4.10); ~~Spark pins every index's engine itself (commerce X18 revised)~~ (superseded by RD11). The typeahead's two-step order stays.
- **RD11 (settling §4.10 against X18, 2026-10-20):** an index without a searchable field keeps the **server default** engine; neither Spark nor this plugin pins one. → §4.10 text and engine table corrected. *Evidence:* `SparkSearchEngine.Apply` sets Lucene only on a task with a `FieldIndexing.Search` field or a `[Fuzzy]` opt-in and skips every other task (`libs/spark/MintPlayer.Spark/Indexes/SparkSearchEngine.cs:126-157`); no `Corax` pin exists in `libs/refdata` or `apps/ReferenceData` (grep, 2026-10-20). The non-searchable indexes are exact keyword lookups, which both engines answer alike.
- **Commerce round 5 additions that land here (commerce D37, "Include everything"):** optional sources for EU VAT rates (TEDB, GC10) and holiday calendars (GC11), and VAT territories in the country profile (GA4), §4.4, RS23, R11b. Their licences and formats are checked in R0; legal content against EUR-Lex (commerce D37).

---

## 14. Verification and acceptance

- **Model:** the decision-table tests (RS4) stay in the regular suite, one per row, with as-of assertions before and on each change date.
- **Determinism:** two builds from the same inputs give identical manifests; Windows and Linux give identical keys and bytes (RS10); a re-import writes 0 documents (RS2); a release dropping an id is refused; delta application reproduces the target digests (RS7).
- **Adapters:** every adapter's real fixtures parse in CI (RS8); Belgium's full import is run once locally with time and memory recorded (RS1).
- **Search:** typeahead p95 within the RS5 target; the misspelling set passes (RS6); the same order on two databases with different country subsets.
- **API:** anonymous, never 401; CORS on `/api/v1` only; 304 on a matching ETag without a query; CORS-readable 429 with `Retry-After`; attribution in every response; caps enforced (400 on violations). The route-table snapshot and the anonymous-surface check show exactly the intended public routes.
- **Index limits:** `/license/status` read and recorded; the instance stays within any cap that applies (RS9).
- **E2E on `apps/ReferenceData`:**
  - the address form in Belgium: "kruish" → `9770 Kruishoutem (Kruisem)` → a street inside Kruisem → a free-text house number checked by the BeST validator, and accepted as "unverified" when the validator times out;
  - free-text mode with a `null` endpoint;
  - an as-of query before and after the Kruisem merger;
  - a consumer sync to the latest release, then a second sync writing nothing;
  - the time-zone label provider giving the translated exemplar city;
  - **round 3:** a fresh database is seeded with the basic set on first start, and a second start writes nothing (RD9); a misspelled place ("Oudenarde") is found through the default fuzzy step (RD10).
- **Browser checks** of the address form, the import-run pages and the sources page through the Playwright MCP tools.
- **Versions:** new packages start at `11.0.0-preview.1`; touched `libs/` csproj files get a preview bump; `@mintplayer/ng-spark` gets one minor bump for the whole PR. No major changes (repo rule).

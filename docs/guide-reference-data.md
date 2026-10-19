# Reference data: running an instance

Background: `docs/reference_data_PRD.md` (the decisions) and `docs/reference_data_plan.md` (the checklist). This guide
is what a host writes and what the instance does. The consumer side is `docs/guide-reference-data-client.md`.

`apps/ReferenceData` runs one instance locally (https 5017 / http 5018, database `RefData` on localhost:8080). It is
**not deployed by us** (K8); any host can run its own.

---

## What you write

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>(auth => auth.LocalCredentials = SparkLocalCredentials.Full); // admins (RD-DEF 2)
    spark.AddMessaging();     // the refdata-import queue
    spark.AddRateLimiter();   // runs the rate limiter; /api/v1 carries its own named policy
    spark.AddReferenceData()  // ledger, derive pass, imports, releases, /api/v1, the seed, the admin pages
        .AddGlobalBaseline()  // GeoNames, Wikidata, CLDR exemplar cities, tzdata, baseline profiles (NL, FR, LU, DE)
        .AddBelgium()         // Statbel REFNIS + NIS6, BeST postal streets
        .AddTedbVatRates()    // optional, commerce GC10
        .AddStatutoryHolidays(); // optional, commerce GC11
});
// after builder.Build():
if (app.RunReferenceDataSeedCommandIfRequested(args)) return;   // --refdata-build-seed / --refdata-verify-seed
```

`security.json` binds the library's one slot:

```json
"bindings": { "reference-data:administrators": [ "Administrators" ] }
```

It grants read-only queries on every reference type and the **Import now** action. There are no New/Edit/Delete
rights and no anonymous rights: the public contract is `/api/v1`, never `/spark`.

```jsonc
"Spark": {
  "ReferenceData": {
    "Sources": {
      "be-postalstreets": { "Schedule": "0 0 4 * * *", "Enabled": true },   // RD1: 6-field cron, UTC
      "be-refnis":        { "Schedule": "0 30 4 * * 1", "Enabled": true }
    },
    "Seed": { "Enabled": true, "Directory": "Seed" },
    "Releases": { "Directory": "RefDataReleaseStore", "Sign": false },
    "Api": { "TokenLimit": 60, "TokensPerPeriod": 20, "ReplenishmentPeriod": "00:00:01" }
  }
}
```

A source runs on a schedule only when `Enabled` (full imports are manual local runs, RD2). A configured source id that
no adapter registers refuses startup; so does `Sign: true` without an `IReleaseSigner` (spike RS17 picks Ed25519).

---

## The pipeline (RD1)

1. **Schedule.** One cron job per enabled source; the job only checks and queues.
2. **Change check first:** `ETag`/`Last-Modified`, a dated file name (Statbel), a release tag (CLDR, tzdata), else the
   SHA-256 of the download (Wikidata, TEDB). Unchanged: the run ends.
3. **Queue:** `RefDataImportMessage` on the `refdata-import` queue (handler timeout from `Import:HandlerTimeout`).
   "Import now" queues the same message.
4. **Download** through the named client, which refuses every host no registered source declares, with byte caps.
5. **Build:** every mapper maps its sources; every id goes through the **ledger**. A conflict refuses the whole run,
   nothing written (§4.3, RS3).
6. **Derive:** lineage edges, lifetimes, segments cut at every ancestor change, integer date keys, search keys, rank, hash.
7. **Write:** a document whose `Hash` matches is skipped (a rerun writes nothing, RS2); a changed one gets the new
   release stamp. A stored document the sources no longer list is kept and counted (retire, never delete); a run that
   would leave more than `Releases:MaxRetiredShare` of a collection absent is refused as a broken download.
8. **Release:** a run that changed anything writes a release (`{yyyy}.{MM}.{n}`) under `Releases:Directory`.

Not built yet: resuming from a partition cursor (a run is idempotent and reruns whole), BulkInsert for the first load,
deltas, and signing.

---

## Ids and the ledger

`{collection}/{country}/{scheme}/{code}[@{yyyy-MM-dd}]`: `places/be/nis/45068`, `places/be/nis6/45017Z`,
`postalcodes/be/9770`, `streets/be/vlg/73243`. An id is minted once from the code that first named the unit and
never changes: a recode adds a dated code (`places/be/nis/55022` also holds `nis 58001` from 2019). Consumers never
compute ids from codes; they call `/api/v1/by-code/{cc}/{scheme}/{code}`.

Each adapter ships `Ledger/{cc}.ndjson` (one `{"Identity","Id","Source"}` line per identity, sorted), so independent
instances mint the same ids. Ids first seen by an instance are kept in its `RefLedgerEntries` until an adapter release
absorbs them (Q-R5).

---

## The basic seed set (RD9)

The adapters' real fixtures (`libs/refdata/sources/*/Fixtures`, cut from the publishers' files) run through the same
pipeline into a committed release, `apps/ReferenceData/ReferenceData/Seed`: BE, NL, FR, LU and DE with their profiles
and VAT territories, Belgium's regions, provinces and arrondissements, every decision-table municipality (Kruisem,
Ooike, Oudenaarde with Bevere, Hasselt, Beveren-Kruibeke-Zwijndrecht, Borsbeek, Bastogne, La Louvière, Sambreville)
with their sections and postal localities, a few streets each, the first-level units of the others, CLDR exemplar
cities, TEDB VAT rates and statutory holidays.

- `dotnet run -- --refdata-build-seed` rewrites it. Ids it had to mint land in `Seed/ledger-added/`; move them into the
  adapters' `Ledger/` and run it again until it mints nothing.
- `dotnet run -- --refdata-verify-seed` (CI) exits 3 when the committed seed or a ledger is stale.
- On start the instance applies it: a fresh database gets it, a second start writes nothing, and a document a full
  import wrote since is never touched.

---

## The public API (`/api/v1`)

Anonymous by design and never a 401; CORS (any origin, GET/HEAD, no credentials) on this group only; no XSRF cookie
(it would force `Cache-Control: private`); a token bucket per IPv4 address or IPv6 /64. Every value is a query
parameter; `kinds` must be levels of the country's profile and `within` a valid id.

| Route | Answers |
|---|---|
| `GET /suggest?q=&country=&kinds=&within=&asOf=&lang=&limit=` | places and postal localities: exact compact key, compact prefix, word prefixes, then fuzzy below `Api:FuzzyBelow` results; ranked by tier, edit distance, rank, id |
| `GET /streets/suggest?q=&municipality=&postalCode=` | streets inside a municipality or a postal code (one is required) |
| `GET /places/{id-without-places/}?asOf=&lang=` | the place, its path, codes, successors and what it resolves to on `asOf` |
| `GET /resolve?ids=a,b&asOf=` · `GET /by-code/{cc}/{scheme}/{code}?asOf=` | as-of resolution · who held a code |
| `GET /postalcodes/{cc}/{code}` · `GET /countries?lang=` · `GET /countries/{cc}/profile` | localities · CLDR country names · the profile |
| `GET /timezones?lang=` | CLDR exemplar cities (only the names CLDR states) |
| `GET /sources` · `GET /releases/latest` · `GET /releases/{release}/{file}` | attribution and terms · the manifest · release files |

Responses carry `release`, `attribution` and an ETag `W/"{release}:{hash of the request}"`; a matching `If-None-Match`
is a 304 without a query. Caps: `q` 1–64 folded characters, at most 5 words, the last at least 2 characters, `limit` ≤ 20.

---

## Release format (schema version 1)

```
{release}/{cc}/{Collection}.ndjson     one canonical document per line, sorted by id (ordinal)
{release}/manifest.json                schema version, release, previous, builder, KeyVersion, sources, files
```

- Canonical bytes: UTF-8 without BOM, LF, properties sorted by ordinal name at every level, every property written,
  strings NFC, dates `yyyy-MM-dd`, set-like arrays sorted by the builder, `@metadata` with only `@collection` and
  `Raven-Clr-Type`, no timestamps.
- `Hash` = `sha256:` of the canonical document without `Hash`, `Release` and `@metadata`. Each file in the manifest
  carries its SHA-256, its count and a digest over its (id, Hash) pairs.
- A reader refuses a file whose bytes, count or digest disagree with the manifest, a document whose own hash is wrong,
  and a release that lacks an id the reader already holds (never delete).

The C# contract is `MintPlayer.Spark.ReferenceData.Releases` (writer, reader, applier) in the Abstractions package.

---

## Sources and attribution

| Source id | Data | Licence | Credit |
|---|---|---|---|
| `be-refnis`, `be-nis6` | Statbel REFNIS (units since 1970), NIS6 sections | CC BY 4.0 | "Source: Statbel", licence link, statement of changes |
| `be-postalstreets` | BeST postal streets | CC BY 4.0 | the BeST providers via FOD BOSA, licence link, statement of changes |
| `global-geonames` | first-level units, population, per-feature time zone | CC BY 4.0 | "Contains data from GeoNames …, modified" |
| `global-wikidata` | labels, ISO 3166-2 codes | CC0 | credited anyway |
| `global-cldr` | time-zone exemplar cities (and, in Abstractions, territory names) | Unicode License v3 | notice + licence text shipped |
| `global-tzdata` | zone list | public domain | credited |
| `eu-tedb` | VAT rates (suggestions) | Commission reuse notice (to verify, RS23) | "Source: European Commission, TEDB" |
| `statute-holidays` | statutory national holidays | legislation | the cited statutes |

`/api/v1/sources` lists them with the API terms (no restriction beyond the upstream licences; G-NAF's mail clause passed on).
bpost data is never used (RD-DEF 9).

---

## Admin pages

The library ships read-only queries for countries, places, postal codes, streets, lineage events and import runs, and
the **Import now** action on the import runs. In apps/ReferenceData the first administrator comes from user secrets in
Development: `ReferenceData:Admin:Email` and `ReferenceData:Admin:Password`.

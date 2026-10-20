# MintPlayer.Spark.ReferenceData.Sources.Finland

The Finnish reference-data adapter (docs/reference_data_PRD.md §4.4, plan R8). Register it with
`spark.AddReferenceData().AddFinland()`.

## Sources

| Id | Source | Licence and credit line | Default schedule | Change check |
|---|---|---|---|---|
| `fi-kunta` | Statistics Finland classification service (`https://data.stat.fi/api/classifications/v2/`): `kunta_1_{yyyyMMdd}` (municipalities, yearly since 1970) and `maakunta_1_…` (regions) in Finnish and Swedish, with the `kunta → maakunta` and `kunta → kielisuhde` (language status) correspondence tables | CC BY 4.0; "Source: Statistics Finland" | `0 0 5 2 1 *` (the lists change on 1 January) | the newest municipality list's date |
| `fi-ryhti` | SYKE Ryhti building and address register, OGC API Features collection `open_address` (paged GeoJSON) | CC BY 4.0; "Source: Finnish Environment Institute (Syke), Ryhti" ⚠️ exact wording unverified | `0 0 4 * * 0` | newest `modified_timestamp_utc` + feature count |

**Postcodes come from Ryhti, not from Posti.** Posti's postcode files may be passed on only together with its current
service description and terms (research §2), so they are excluded unless Posti confirms republication in writing.
SYKE asks intensive users for an identifier; NLS name data needs a key (neither is used).

## Mapping and catches

- **History from yearly lists.** A municipality's life is the run of yearly lists that hold its code; its fi/sv names
  and its language status (`kielisuhde`: 0 Finnish, 1 bilingual Finnish majority, 2 Swedish, 3 bilingual Swedish
  majority) are dated per list. A name in a language that is not official there is an exonym (Swedish
  "Tammerfors" for unilingual Tampere).
- **No successor links** in the classification service: a code that disappears ends its place with an
  **unresolved** lineage event (Q-R4), e.g. Honkajoki 099, absorbed by Kankaanpää 214 on 2021-01-01. The merger list
  is not imported (it is not part of this service).
- A code listed again after a gap is a new place with an `@yyyy-MM-dd` suffix (retirement rules unverified, PRD §4.4).
- **Parents** come from each year's `kunta → maakunta` table (regions since the 1990 tables); before that, the country.
- **Streets:** Ryhti has no street identifier, so a street is its folded name within the municipality
  (`streets/fi/k{kunta}/{key}`). Names in Finnish and Swedish, official per the municipality's language status.
  No house number is kept (RD5).
- **Åland** municipalities are in Statistics Finland's lists; Åland is a VAT territory in the profile (Art. 6(1)(d),
  unconfirmed on EUR-Lex) and also has its own ISO code AX.
- The endonyms "Suomi"/"Finland" are hand-authored (the embedded CLDR subset has de/en/fr/nl only).

## Fixtures (cut 2026-10-19)

| File | Cut from | Rule |
|---|---|---|
| `kunta_1_20200101.{fi,sv}.cut.json`, `kunta_1_20210101.{fi,sv}.cut.json` | `…/classifications/kunta_1_{date}/classificationItems?content=data&meta=max&lang={fi,sv}` | Espoo 049, Helsinki 091, Honkajoki 099 (2020 only), Kankaanpää 214, Maarianhamina 478, Rovaniemi 698; items kept with `localId`, `code`, `classificationItemNames` |
| `maakunta_1_{date}.{fi,sv}.cut.json` | `…/classifications/maakunta_1_{date}/classificationItems…` | Uusimaa 01, Satakunta 04, Lappi 19, Ahvenanmaa 21 |
| `kunta_1_{date}-maakunta_1_{date}.cut.json`, `kunta_1_{date}-kielisuhde_1_{date}.cut.json` | `…/correspondenceTables/kunta_1_{date}%23{maakunta,kielisuhde}_1_{date}/maps` | the rows of those municipalities, `sourceLocalId`/`targetLocalId` kept |
| `ryhti-open_address.cut.json` | `https://paikkatiedot.ymparisto.fi/geoserver/ryhti_building/ogc/features/v1/collections/open_address/items?f=json&filter=municipality_number='{214,091,478}'` | one address point per (street, postcode) of two streets in Kankaanpää and Helsinki and two in Maarianhamina; features unchanged |

Ledger: `Ledger/fi.ndjson`.

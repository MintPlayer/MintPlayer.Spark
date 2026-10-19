# MintPlayer.Spark.ReferenceData.Sources.Global

The global reference-data baseline (docs/reference_data_PRD.md §4.4, §4.13): GeoNames first-level units with their per-feature time zones, Wikidata labels and ISO 3166-2 codes, Unicode CLDR time-zone exemplar cities, the tz database's zone list, and baseline country profiles (NL, FR, LU, DE) with their VAT territories.

Fixtures (where present) are real samples cut from the publisher's files; their licences and credit lines are in the source classes and at `/api/v1/sources`.

## Fixtures (real samples, RS8; cut 2026-10-19)

| File | Cut from |
|---|---|
| `geonames-adm1.cut.txt` | the PCLI and ADM1 rows of GeoNames `DE.zip`, `FR.zip`, `LU.zip`, `NL.zip` (raw lines) |
| `wikidata-adm1-labels.csv` | a Wikidata SPARQL result: en/nl/fr/de labels and P300 codes of those units by P1566 |
| `cldr-exemplar-cities.cut.json` | cldr-json 48.2.3 `timeZoneNames.json` (de, en, fr, nl), the seeded countries' zones; `CLDR-LICENSE.txt` is its licence |
| `zone.tab` | tzdata 2026e, whole (public domain) |

VAT territories in `BaselineProfiles` cite Directive 2006/112/EC Art. 6; the article letters could not be checked on EUR-Lex on 2026-10-19 and are marked so in each entry.

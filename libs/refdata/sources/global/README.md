# MintPlayer.Spark.ReferenceData.Sources.Global

The global reference-data baseline (docs/reference_data_PRD.md §4.4, §4.13): GeoNames first-level units with their per-feature time zones, Wikidata labels and ISO 3166-2 codes, Unicode CLDR time-zone exemplar cities, the tz database's zone list, baseline country profiles with their VAT territories, and the name sources: NGA GNS conventional names, UN/LOCODE location codes and conventional names, and the IANA Language Subtag Registry.

**Baseline profiles.** The baseline writes a country's profile and first-level units only when no adapter of that country is registered (`IReferenceDataMapper.IsCountryAdapter`). The NL, FR and LU profiles moved to their tier-1 adapters (plan R8); DE stays here (no open register with postcodes, PRD §9).

Fixtures (where present) are real samples cut from the publisher's files; their licences and credit lines are in the source classes and at `/api/v1/sources`.

## Sources

| Id | Source | Licence | Change check | Mapped by |
|---|---|---|---|---|
| `global-geonames` | GeoNames per-country dumps (PCLI, ADM1) | CC BY 4.0 | conditional request | `GlobalMapper` (first) |
| `global-wikidata` | Wikidata SPARQL (labels, P300) | CC0 | SHA-256 of the result | `GlobalMapper` |
| `global-cldr` | cldr-json `timeZoneNames.json` | Unicode License v3 | the latest release tag | `GlobalMapper` |
| `global-tzdata` | tz database `zone.tab` | public domain | the latest tag | `GlobalMapper` |
| `global-gns` | NGA GEOnet Names Server per-country files | no restrictions, citation requested | conditional request on the first file | `GlobalNamesMapper` (last) |
| `global-unlocode` | UN/LOCODE release zip (CSV files only) | CC BY 4.0 | conditional request (`ETag`) | `GlobalNamesMapper` |
| `global-iana-subtags` | IANA Language Subtag Registry | CC0 | its `File-Date` (the first line, by range request) | `GlobalNamesMapper` |

`GlobalNamesMapper` runs after every country adapter (order 90) because it adds to their places; it mints nothing:
- **GNS:** a feature whose approved (`N`) name matches exactly one place of its country and level (ADM1/ADM2 → regions and subregions; ADM3/4 and PPL* → municipalities and below) gets its conventional (`C`) names as exonyms ("East Flanders"); a name already present is not repeated; variants (`V`) are not imported. Where the place's own names are not in Latin script the entry is marked an authoritative romanization (machine transliterations are never displayed, Q-R11).
- **UN/LOCODE:** a location whose name (both parts of "Brussel (Bruxelles)") matches exactly one municipality of its country, current ones first, gets an `unlocode` code (`BEHSS`); a reference row ("Antwerp = Antwerpen") adds an English exonym. Rows marked `X` are skipped; ambiguous matches are listed, never guessed.
- **IANA subtags:** every language tag the build uses (names, official languages, profiles) and every country code is checked; unregistered or deprecated subtags are listed with their preferred value. The registry is validation data only: serving it through the API would need a new release collection, which is not added.

**Credit lines.** GeoNames: "Contains data from GeoNames (https://www.geonames.org), licensed under CC BY 4.0, modified." CLDR: "Includes Unicode CLDR data, Copyright © 1991-2026 Unicode, Inc., under the Unicode License v3". UN/LOCODE: "UN/LOCODE, United Nations Economic Commission for Europe, CC BY 4.0", modified. GNS: NGA's citation (⚠️ recalled, not copied: the site renders through a script; verify it on https://geonames.nga.mil/). Wikidata and the IANA registry are credited although CC0 does not require it.

## Fixtures (RS8)

| File | Cut from | Kind |
|---|---|---|
| `geonames-adm1.cut.txt` | the PCLI and ADM1 rows of GeoNames `DE.zip`, `FR.zip`, `LU.zip`, `NL.zip` (raw lines) | real, 2026-10-19 |
| `wikidata-adm1-labels.csv` | a Wikidata SPARQL result: en/nl/fr/de labels and P300 codes of those units by P1566 | real, 2026-10-19 |
| `cldr-exemplar-cities.cut.json` | cldr-json 48.2.3 `timeZoneNames.json` (de, en, fr, nl), the seeded countries' zones; `CLDR-LICENSE.txt` is its licence | real |
| `zone.tab` | tzdata 2026e, whole (public domain) | real |
| `unlocode-codelist.cut.csv` | `unlocode-latest.zip` of 2026-10-16 (`release/csv/UNLOCODE CodeListPart1–3.csv`): the BE/DE/FR/LU/NL header rows, Belgium's seeded municipalities (incl. the `#`-marked "Brussel (Bruxelles)"), the reference rows ("Antwerp = Antwerpen", "Flushing = Vlissingen"), Köln, München, Paris, Luxembourg, Amsterdam; raw lines | real |
| `unlocode-subdivisions.cut.csv` | the same release, `SubdivisionCodes.csv`: the BE, DE, FR, LU and NL rows | real |
| `language-subtag-registry.cut.txt` | the registry of File-Date 2026-09-17: the header, every 2-letter language and region subtag, a few 3-letter ones (incl. deprecated `iw`, region `BU`, `DD`, `150`), the scripts Latn, Cyrl, Hans, Hant, Grek, Arab, Hebr; records kept whole | real |
| `gns-adm1.doc-derived.txt` | **doc-derived**: the real header of the GNS country files, with rows written to the published schema (https://geonames.nga.mil/geonames/GNSHome/reference.html) for Bayern/Bavaria, Niedersachsen/Lower Saxony, Antwerpen/Antwerp and Oost-Vlaanderen/East Flanders; feature ids are negative so they can never be mistaken for real UFIs. On 2026-10-19 every per-country file on the GNS data page held only its header (0 features for 269 of 270 entities) | doc-derived |

VAT territories in `BaselineProfiles` cite Directive 2006/112/EC Art. 6; the article letters could not be checked on EUR-Lex on 2026-10-19 and are marked so in each entry.

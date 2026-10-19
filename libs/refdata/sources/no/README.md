# MintPlayer.Spark.ReferenceData.Sources.Norway

The Norwegian reference-data adapter (docs/reference_data_PRD.md §4.4, plan R8). Register it with
`spark.AddReferenceData().AddNorway()`.

## Sources

| Id | Source | Licence and credit line | Default schedule | Change check |
|---|---|---|---|---|
| `no-klass` | SSB KLASS classifications 131 (kommuner) and 104 (fylker): versions as code lists with validity, and change tables (`https://data.ssb.no/api/klass/v1/`) | CC BY 4.0; "Kilde: Statistisk sentralbyrå (SSB)" | `0 0 5 * * 1` | the classifications' `lastModified` |
| `no-adresser` | Kartverket Matrikkelen address files, CSV in ZIP, from the [Atom feed](http://nedlasting.geonorge.no/geonorge/ATOM-feeds/MatrikkelenAdresse_AtomFeedCSV.xml) (national file) | CC BY 4.0; "© Kartverket" + link | `0 0 5 * * 0` | the feed entry id's timestamp |
| `no-postnummer` | Bring's postcode register (`postnummerregister-ansi.txt`) | NLOD 2.0 per [data.norge.no](https://data.norge.no/en/datasets/5e6847ba-156d-4e14-85d3-8d7f8b727523); bring.no states no licence | `0 30 5 * * 1` | `ETag` / `Last-Modified` |

The CC BY-NC cadastre products are excluded. The address API caps at 10,000 results, so files are used.

## Mapping and catches

- **kommunenummer is not stable.** Counties were renumbered in 2020 and 2024, so a place is keyed on (code, valid
  from): identity `knr:{code}` for a unit alive at the start of the requested history, `knr:{code}@{date}` otherwise.
  A change table row that is one-to-one on its date is a **recode** (the place keeps its id and gains a dated code:
  Kåfjord 1940 → 5426 → 5540 is one place); n → 1 under a new code is a **merge** into a new place (Larvik 0709 +
  Lardal 0728 → 0712, 2018); 1 → n is a **split** with part edges (Troms og Finnmark 54 → 55 + 56, 2024). A rename
  keeps its code (Oslo → "Oslo - Oslove", 2026) and only dates the names.
- **Hierarchy:** a municipality's county is the county code its own kommunenummer starts with (SSB defines the
  number that way), resolved to the county *place* through the county code's own periods, so a renumbered county
  never re-parents a municipality by digits alone.
- **Sami and Kven names** arrive joined with " - ". They are split per language by the rule in `NorwayNames`:
  the Norwegian part matches the name the address register (or Bring) writes for the municipality; a part with a
  Northern Sami letter is `se`; of the remaining parts the last of three is Kven (`fkv`), any other `smi`. ⚠️ A
  heuristic: SSR's per-name languages are not imported (RS8).
- **Streets:** `streets/no/k{first kommunenummer of the place}/{adressekode}` from the address points
  (`adressetype = vegadresse`); matrikkel addresses (no street) are skipped; no house number is kept (RD5). A
  street's language follows the same letter rule.
- **Postcodes:** Bring's register (Windows-1252, tab-separated, CRLF, no header); service codes (category `S`) are
  skipped. The address points add the street ↔ postcode links.
- **The country name** "Norge" is hand-authored (the embedded CLDR subset has de/en/fr/nl only).

## Fixtures (cut 2026-10-19)

| File | Cut from | Rule |
|---|---|---|
| `klass-131-codes-2017-01-01-2026-10-19.cut.json` | `https://data.ssb.no/api/klass/v1/classifications/131/codes.json?from=2017-01-01&to=2026-10-19` | the versions of Oslo, Kåfjord (1940/5426/5540), Kautokeino (2011/5430/5612), Tromsø (1902/5401/5501), Larvik/Lardal (0709/0728/0712/3805/3909); items unchanged, re-serialized compactly |
| `klass-131-changes-2017-01-01-2026-10-19.cut.json` | `…/131/changes.json?from=2017-01-01&to=2026-10-19` | the changes naming those codes |
| `klass-104-codes-2017-01-01-2026-10-19.cut.json`, `klass-104-changes-…` | the same for classification 104 | counties 03, 07, 08, 19, 20, 38, 39, 40, 54, 55, 56 |
| `Basisdata_5540_Kafjord_25833_MatrikkelenAdresse.cut.csv` | the feed's Kåfjord file (`Basisdata_5540_Kafjord_25833_MatrikkelenAdresse_CSV.zip`, 2026-09-29) | header, one matrikkel address, one address per (street, postcode) of six streets (Norwegian, Sami and Kven names); raw lines (UTF-8 BOM, `;`) |
| `Basisdata_5612_Kautokeino_25833_MatrikkelenAdresse.cut.csv` | the feed's Kautokeino file | header and two addresses |
| `postnummerregister-ansi.cut.txt` | `https://www.bring.no/postnummerregister-ansi.txt` | the rows of Kåfjord and a few of Kautokeino, Oslo, Tromsø and Larvik; raw bytes (Windows-1252, CRLF) |

The requested range of a KLASS file is in its name: a version starting on the range's first day has an unknown
start, and one ending on its last day is open. Ledger: `Ledger/no.ndjson`.

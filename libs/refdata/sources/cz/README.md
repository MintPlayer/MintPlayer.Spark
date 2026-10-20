# MintPlayer.Spark.ReferenceData.Sources.Czechia

The Czech reference-data adapter (docs/reference_data_PRD.md §4.4, plan R8): ČÚZK's RÚIAN through the VDP exchange
format. `spark.AddReferenceData().AddCzechia()`.

## Sources

| Id | File | Schedule (default) | Change check |
|---|---|---|---|
| `cz-ruian-units` | `https://vdp.cuzk.gov.cz/vymenny_format/soucasna/{yyyyMMdd}_ST_UZSZ.xml.zip` (≈4.7 MB; VFR 3.1 XML: state, kraje, okresy, obce, části obce) | `0 0 5 2 * *` | the dated file name: the newest month-end whose file exists (HEAD, at most three months back); `ETag`/`Last-Modified` recorded |
| `cz-ruian-addresses` | `https://vdp.cuzk.gov.cz/vymenny_format/csv/{yyyyMMdd}_OB_ADR_csv.zip` (≈63 MB; one CSV per obec) | `0 0 5 2 * *` | as above |

- **Licence and credit:** CC BY 4.0. Credit line "ČÚZK, [rok]" with the **year of the data** (the state date of the
  file read; the fixture is the 2026-09-30 state, so "ČÚZK, 2026"), plus the metadata and a link to the terms. The
  credit line states that the data was modified and that VDP is informational (binding data only through ISZR).
- **Ids:** RÚIAN codes, which RÚIAN does not reuse (unverified, research §2): `places/cz/vusc/78`,
  `places/cz/okres/3505`, `places/cz/obec/563889`, `places/cz/cobce/408638`, `streets/cz/ulice/252000`,
  `postalcodes/cz/46001`.
- **History:** periods from `DatumVzniku` and, where a file states it, `DatumZaniku`/`PlatiDo`. Profile "history known
  since" 2012-07-01 (RÚIAN went live). RÚIAN names no successors: an obec that ends gets an **unresolved** lineage
  event (Q-R4). The daily change files (`ST_ZZSZ`) are not read yet: the monthly full state is enough for the RD1 cadence.

## Catches handled

- **VDP moved host** (`vdp.cuzk.cz` → `vdp.cuzk.gov.cz`, 302): both hosts are allowed, the new one is used.
- **Windows-1250**, verified on the real file (`Kód` = `4B F3 64`), no BOM, `;`-separated, **LF** line ends, no quoting.
- A unit element and its container share a name in VFR (`<vf:Vusc>` around `<vf:Vusc gml:id=…>`): units are told apart by their `gml:id`.
- An obec without a street network: its address places name no street (`Kód ulice` empty); the part of the obec
  stands in for the street (profile address lines); the PSČ is still linked.
- **No house numbers are kept** (RD5): address rows are read only for street ↔ PSČ links.
- Česká pošta's PSČ CSVs are unlicensed and not used; PSČ come from RÚIAN address places.

## Fixtures (real samples, RS8; cut 2026-10-19)

| File | Cut from | Rule |
|---|---|---|
| `20260930_ST_UZSZ.cut.xml` | `20260930_ST_UZSZ.xml.zip` | the header, the state, kraj 78 (Liberecký), okres 3505 (Liberec), obce 563889 (Liberec) and 563943 (Cetenov) with all their části obce; elements kept verbatim except the `*:Geometrie` blocks, which are removed |
| `20260930_OB_563889_ADR.cut.csv` | `20260930_OB_563889_ADR.csv.zip` (Liberec) | the parts 408638, 408913 and 408824: the first three streets per part in file order, one address row per (part, street, PSČ); raw bytes kept (cp1250, LF) |
| `20260930_OB_563943_ADR.cut.csv` | `20260930_OB_563943_ADR.csv.zip` (Cetenov, no street network) | one address row per (part, PSČ); raw bytes kept |

Ledger: `Ledger/cz.ndjson`, filled by `--refdata-build-seed` (see docs/guide-reference-data.md).

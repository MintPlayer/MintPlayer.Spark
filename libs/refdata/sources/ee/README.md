# MintPlayer.Spark.ReferenceData.Sources.Estonia

The Estonian reference-data adapter (docs/reference_data_PRD.md §4.4, plan R8). Register it with
`spark.AddReferenceData().AddEstonia()`.

## Sources

| Id | Source | Licence and credit line | Default schedule | Change check |
|---|---|---|---|---|
| `ee-ehak` | EHAK settlement-unit layer, `https://s3.pilw.io/rp-kemit-kataster/EHAK/asustusyksus_shp.zip` (its DBF holds each settlement unit with its municipality and county: the whole current hierarchy); monthly, with a 1 January snapshot | No licence agreement; credit "Estonian Land and Spatial Development Board (Maa- ja Ruumiamet)" | `0 0 5 3 * *` | `ETag` (S3) |
| `ee-ads` | ADS address extract nr 9 "addresses with components and history" ([väljavõtted](https://aks.maaruum.ee/aks/valjavotted)), CSV, every 30 days | No licence agreement (ADS); AKS data CC0; credit the board | `0 0 5 5 * *` | `ETag`/`Last-Modified` of the configured file |

- **The ADS file URL is configuration:** `Spark:ReferenceData:Sources:ee-ads:Url`. The AKS web application serves the
  extracts without a published stable link; the import refuses to run until it is set.
- ⚠️ **"The access is limited for foreign IP addresses"** (ADS): a host must run its instance, or at least the
  import, where the download is allowed.
- **Postcodes are not shipped.** The extract carries `SIHTNUMBER`, and the postcode file (≈28 MB, geoportaal) states
  no licence; republishing them waits until that is verified (PRD §4.4). The country profile still validates the
  five-digit format.
- Domains are moving from maaamet.ee to maaruum.ee (both hosts are allowed). Coordinates (L-EST97) are not used.

## Mapping and catches

- **EHAK** is one classifier with codes unique across levels: `places/ee/ehak/{code}` for counties, municipalities,
  Tallinn's districts (type 6, `ee:district`) and settlement units (`ee:settlement`, the city line). The DBF is
  Windows-1257 (its language driver says only "Windows ANSI"; ä, õ, ö, ü are the same bytes in 1252 and 1257, š and ž
  are not).
- **History from ADS:** a municipality the extract names that EHAK no longer lists ended on the last day a component
  named it. If, on that day, the same address object names a settlement unit **of the same name** in its new
  municipality, the unit **continues one level down** (a demote event; the settlement's EHAK code is an alias of the
  same place: Kuressaare linn, municipality until 2017-10-24, then a city in Saaremaa vald). Otherwise it ends with an
  unresolved event (Q-R4).
- **Streets** are ADS traffic areas (`TASE5`), one per ADS object (`streets/ee/ads/{ADS_OID}`), with a dated period
  per municipality state; no house numbers (RD5), no postcodes (above).
- **History known since:** the first import (the depth of extract 9's history is unverified).
- The endonym "Eesti" is hand-authored (the embedded CLDR subset has de/en/fr/nl only).

## Fixtures

| File | Provenance | Rule |
|---|---|---|
| `asustusyksus.cut.dbf` | **real cut** of `asustusyksus.dbf` in `https://s3.pilw.io/rp-kemit-kataster/EHAK/asustusyksus_shp.zip` (2026-09-02 edition, fetched 2026-10-19) | the records of Kesklinna and Haabersti linnaosa (Tallinn), Kuressaare linn, Orissaare alevik and Arandi küla (Saaremaa vald); header and records byte for byte, the record count rewritten |
| `ads-valjavote-9.docderived.csv` | **doc-derived** from the ADS documentation (https://geoportaal.maaamet.ee/est/Ruumiandmed/Aadressiandmed/Aadressiandmete-valjavotted-p624.html, and the `TASE1`…`TASE8` component levels of ADS) | the extract could not be downloaded from here (the AKS application serves it behind its own UI). ⚠️ Column names, date format (`dd.MM.yyyy`), ADS object ids, the TASE5 codes, the postcodes and the former municipality code 0349 for Kuressaare linn are **illustrative**; the street and unit names are real. Replace with a real cut (RS8) |

Ledger: `Ledger/ee.ndjson`.

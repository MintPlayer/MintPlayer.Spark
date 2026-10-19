# MintPlayer.Spark.ReferenceData.Sources.Luxembourg

The Luxembourg reference-data adapter (docs/reference_data_PRD.md §4.4, plan R8): the national register of localities
and streets (CACLR) into cantons, communes, localities, streets and postal codes, the Luxembourg country profile, the
id ledger and real sample fixtures. It replaces the global baseline's hand-written LU profile.

```csharp
spark.AddReferenceData().AddLuxembourg();
```

## Source

| Id | Dataset | Licence | Default schedule (UTC) | Change check |
|---|---|---|---|---|
| `lu-caclr` | ACT, *Registre national des localités et des rues* ([dataset](https://data.public.lu/fr/datasets/registre-national-des-localites-et-des-rues/)), `caclr.zip`, ≈2 MB, weekly | CC0 1.0 | `0 0 5 * * 2` | the MD5 checksum the data.public.lu dataset API publishes for the resource |

Credit line (CC0 needs none; given anyway): "Registre national des localités et des rues (CACLR), Administration du
cadastre et de la topographie, via data.public.lu, CC0".

## Catches

- **Dated download folder** (`…/registre-national-des-localites-et-des-rues/20261019-023002/caclr.zip`): the file is
  discovered through the dataset API (`/api/1/datasets/registre-national-des-localites-et-des-rues/`), never hard-coded.
- **Fixed-width ISO-8859-1 files with CRLF** and no extension; layouts from ACT's
  [Spécifications des fichiers adresses v2.2](https://act.public.lu/dam-assets/fr/publications/documents-techniques/specs_fichiers_adresses.pdf).
  `NULLIF` columns hold `?` for "no value". `COMMUALL` carries one column more than the specification's `COMMUNE`:
  position 95, `O` for a current commune, `N` for a former one.
- **A commune code is unique only within its canton**: its identity is canton + code (`0814` Groussbus-Wal).
- **No machine-readable history** (RD4): `COMMUALL`'s date is a modification stamp, not a legal date (Grosbous, merged
  into Groussbus-Wal on 2023-09-01, carries 2009-01-27), so former communes are skipped with a warning. History is known
  since the first import. Localities and streets with an end date (`DATE_FIN_VALID`) get a closed period.
- **Street ↔ postcode links** exist only per building (`IMMEUBLE`): the file is streamed and no house number is kept
  (RD5). The postal city line is the postcode's own label, which can differ from the street's locality (8818 `GREVELS`
  for Redingshaff in Wahl); `ESCH/ALZETTE` becomes `Esch/Alzette`. PO-box codes (type `B`) are skipped.
- Names are the register's official forms (French or German as the commune chose, e.g. *Groussbus-Wal*); there are
  no separate Luxembourgish names. They are tagged `fr`, the register's administrative language.

## Fixtures (real samples, RS8; cut 2026-10-19)

| File | Cut from | Rule |
|---|---|---|
| `CANTON.cut` | `caclr.zip` of 2026-10-19 (`20261019-023002`) | cantons 03 Esch-sur-Alzette and 08 Redange |
| `COMMUALL.cut` | same | Esch-sur-Alzette (03/04), Groussbus-Wal (08/14) and the former Grosbous (08/07) and Wahl (08/13) |
| `LOCALITE.cut` | same | Esch-sur-Alzette, Grosbous, Wahl, Dellen |
| `RUE.cut` | same | the first 3–4 current streets of each locality |
| `IMMEUBLE.cut` | same | one current building per (street, postcode) |
| `CODEPT.cut` | same | the postcodes those buildings use |

Raw lines kept (ISO-8859-1, CRLF). Ledger: `Ledger/lu.ndjson`, filled by `--refdata-build-seed` (docs/guide-reference-data.md).

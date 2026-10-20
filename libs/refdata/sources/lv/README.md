# MintPlayer.Spark.ReferenceData.Sources.Latvia

The Latvian reference-data adapter (docs/reference_data_PRD.md §4.4, plan R8): the State Address Register (Valsts adrešu reģistrs) of the State Land Service (VZD), published daily on data.gov.lv.

```csharp
spark.AddReferenceData().AddLatvia();
```

| Source id | Dataset | Default schedule (UTC) | Change check |
|---|---|---|---|
| `lv-var` | [VZD open data](https://data.gov.lv/dati/lv/dataset/varis-atvertie-dati) (package `6b06a7e8-dedf-4705-a47b-2a7c51177473`) | `0 30 5 * * *` (daily) | CKAN `package_show`: the latest `last_modified` of the tables read (no checksum file is published) |

**Licence and credit.** data.gov.lv labels the data CC BY 4.0, Geolatvija CC0 1.0: CC BY is followed. VZD prescribes no credit text; the credit line names VZD, the register, the licence and the modification.

## Mapping

| Table (`TIPS_CD`) | Level | Notes |
|---|---|---|
| `aw_rajons` (102) | `lv:district` | ended 2009-07-01; history only |
| `aw_novads` (113) | `lv:municipality` | `ATRIB` = ATVK code (scheme `atvk`) |
| `aw_pilseta` (104) | `lv:municipality` under the state (state city, e.g. Rīga), else `lv:town` | |
| `aw_pagasts` (105) | `lv:parish` | |
| `aw_ciems` (106) | `lv:village` | not in the fixture |
| `aw_iela` (107) | streets | `streets/lv/var/{KODS}` |
| `aw_eka` (108) | — | streamed for `ATRIB` = postcode only: street ↔ postcode links and postal localities; no house numbers kept (RD5); `aw_dziv` is never fetched |

- **History.** Each `*_his` row is an earlier version (valid until `DAT_MOD`, inclusive, under its own `VKUR_CD`); the current row follows. `DEL` rows end on `DAT_BEIG` (inclusive); `ERR` rows are skipped. History is known since the register began (1999-03-16).
- **Lineage without curation.** An ended municipality's successors are the municipalities its towns and parishes lie under the next day (Varakļāni → Madona, 2025-07-01: absorb), or a municipality of the same name starting that day (the 2021 reform re-created units under new codes: merge). Several successors make a split with part edges; none leaves the event unresolved (Q-R4).
- **Catches.** The CSVs are UTF-8 with a BOM although their CSVW metadata declares ISO-8859-1 (the BOM decides). `ATRIB` means ATVK in the unit tables and the postcode in `aw_eka`. The district table gives 2009-07-01 as the districts' last day, their children's versions end 2009-06-30. The register re-pointed towns to the 2021 municipalities without a history row, so a version under a parent that did not exist yet is placed under the parent's same-name predecessor until the parent starts. Planning and statistical regions are not in the register and are not imported.

## Fixtures (real samples, RS8; cut 2026-10-19 from the files published that day)

| File | Rule |
|---|---|
| `aw_rajons.cut.csv` | Madonas rajons (100001153) |
| `aw_novads.cut.csv`, `aw_novads_his.cut.csv` | Madonas novads before (100016003, `DEL` 2021) and after the 2021 reform (100016671), Varakļānu novads (100016300, `DEL` 2025-06-30); their history rows (none) |
| `aw_pilseta.cut.csv`, `aw_pilseta_his.cut.csv` | Madona, Cesvaine, Varakļāni, Rīga and their history rows |
| `aw_pagasts.cut.csv`, `aw_pagasts_his.cut.csv` | Aronas, Murmastienes and Varakļānu pagasts and their history rows |
| `aw_iela.cut.csv`, `aw_iela_his.cut.csv` | three streets each in Madona, Varakļāni and Rīga, and their history rows |
| `aw_eka.cut.csv` | the first two existing addresses of each of those streets (for their postcodes) |

Raw lines kept (UTF-8 BOM, quoting, `yyyy.MM.dd` dates). Cut by `scratchpad/f4/scripts/cut_lv.py`. Ledger: `Ledger/lv.ndjson`, filled by `--refdata-build-seed`.

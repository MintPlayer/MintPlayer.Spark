# MintPlayer.Spark.ReferenceData.Sources.Liechtenstein

The Liechtenstein reference-data adapter (docs/reference_data_PRD.md §4.4, plan R8). `spark.AddReferenceData().AddLiechtenstein()`.

| Source id | What | Change check | Default schedule |
|---|---|---|---|
| `li-swisstopo-addresses` | swisstopo's official building-address directory for Liechtenstein (`amtliches-gebaeudeadressverzeichnis_li_2056.csv.zip`, 386 KB, daily; the CH schema) | `ETag` | `0 45 5 * * *` |

**Ids.** Gemeinden `places/li/fosnr/{7001…7011}`, streets `streets/li/esid/{ESID}`, postal codes `postalcodes/li/{9485…9498}`.
Everything is derived from the address rows; no house numbers are kept (RD5).

**History.** None is named for Liechtenstein: known since the first import (RD4); `HistoryKnownSince` is unset.

**Attribution.** "Expected" under swisstopo's OGD terms, as for Switzerland: the source is named "©swisstopo", so every
response with LI data carries it.

**Catches.** UTF-8 with BOM, `;`-separated. `COM_CANTON` is empty. Planned addresses (`ADR_STATUS` = `planned`) are
skipped. The postal label "9493 Mauren FL" carries "FL" to tell it from Mauren TG in the Swiss directory: the locality is
named "Mauren". Postal codes cross Gemeinde borders (9487 Gamprin-Bendern also inside Vaduz, 9493 Mauren inside Eschen):
the locality links to every Gemeinde it occurs in.

## Fixtures (real samples, RS8; cut 2026-10-19)

| File | Cut from | Rule |
|---|---|---|
| `amtliches-gebaeudeadressverzeichnis_li_2056.cut.csv` | `https://data.geo.admin.ch/ch.swisstopo.amtliches-gebaeudeadressverzeichnis/amtliches-gebaeudeadressverzeichnis_li/amtliches-gebaeudeadressverzeichnis_li_2056.csv.zip` (2026-10-19) | Vaduz, Planken, Mauren, Gamprin: the first address of the first 3 streets (by name), one planned address each, and the 9487 address inside Vaduz; raw lines, BOM kept |

# MintPlayer.Spark.ReferenceData.Sources.Switzerland

The Swiss reference-data adapter (docs/reference_data_PRD.md §4.4, plan R8). `spark.AddReferenceData().AddSwitzerland()`.

| Source id | What | Change check | Default schedule |
|---|---|---|---|
| `ch-bfs-communes` | BFS commune register (`agvchapp.bfs.admin.ch/api/communes/`): `snapshot?date=` (cantons, districts, communes with historicization numbers) and `mutations?startPeriod=01-01-1960&endPeriod=…` | SHA-256 of the mutation list (the service sends no validator) | `0 0 5 * * 1` |
| `ch-swisstopo-streets` | swisstopo official street directory (`amtliches-strassenverzeichnis_ch_2056.csv.zip`, daily): ESID, names, PLZ + Ortschaft, commune (BFS number), status | the STAC item's `file:checksum` of the CSV asset, else `ETag` | `0 30 5 * * *` |

**Ids.** Cantons `places/ch/canton/{ZH}`, districts `places/ch/district/{BFS district number}`, communes `places/ch/bfs/{BFS number}`
(a recode keeps the number that minted the id: Moutier is `places/ch/bfs/700` with the dated code 6831 since 2026-01-01),
streets `streets/ch/esid/{ESID}`, postal codes `postalcodes/ch/{PLZ}`.

**History** since 1960 from the mutations, derived per mutation number and terminal commune: merge (new number), absorb
(the terminal keeps its number), recode (one commune, one new number: Moutier BE → JU), rename and reparent. A commune
mapped onto itself with only a new historicization number (a boundary adjustment) gives no lineage: the register does not
say which way the territory went. Historicization numbers are kept as dated `bfs-hist` codes.

**Attribution (mandatory).** swisstopo requires "©swisstopo" *in every case*: the `/api/v1` responses carrying CH data
include it: the instance adds the compact credit of every source of a result's countries to the `attribution` array,
and this source's name is "©swisstopo", so the compact form reads "©swisstopo (OGD swisstopo …)". `/api/v1/sources`
lists the full credit line as the service metadata. The STAC metadata says `"license": "proprietary"`
while swisstopo's OGD terms allow commercial use; the OGD terms govern (noted). The BFS terms are unverified; the
opendata.swiss "open use, source required" terms are followed.

**Catches.** UTF-8 with BOM, `;`-separated (street directory); comma-separated CSV without BOM (BFS API). Historical
codes are unique per level only (10314 is both the commune Wollerau and the district Region Plessur). Ten cantons have
no districts: the register's "Kanton ohne Bezirksunterteilung" rows are not places. Bilingual names are split on
`" / "` ("Bern / Berne", "Bezirk See / District du Lac", "Aarbergstrasse / Rue d'Aarberg"); commune names are tagged
with the canton's first language, because the register states no language per commune (imprecise in BE, FR, VS, GR).
A street may lie in several postal localities ("7017 Flims Dorf, 7018 Flims Waldhaus"). Planned streets are skipped;
`Area` entries are hamlets; unofficial names are `common`. Postal labels keep their canton suffix ("1673 Ecublens FR").

## Fixtures (real samples, RS8; cut 2026-10-19)

| File | Cut from | Rule |
|---|---|---|
| `bfs-communes-snapshot-2026-01-01.cut.csv` | `https://www.agvchapp.bfs.admin.ch/api/communes/snapshot?date=01-01-2026` | every canton; the districts of the kept communes and Moutier's former district (Jura bernois); communes Biel/Bienne, Fribourg, Grolley-Ponthaux, Rue, Flims, Chur, Genève, Moutier; bytes kept |
| `bfs-communes-mutations-2025-2026.cut.csv` | `https://www.agvchapp.bfs.admin.ch/api/communes/mutations?startPeriod=01-01-2025&endPeriod=01-01-2026` (fetched to 2027) | mutations 3989 (Chur absorbs Tschiertschen-Praden), 3991 (Rue absorbs three), 3993 (Grolley-Ponthaux merge), 3998 (boundary adjustment), 3999 (Moutier recode, BE → JU) |
| `amtliches-strassenverzeichnis_ch_2056.cut.csv` | swisstopo `amtliches-strassenverzeichnis_ch_2056.csv.zip` (2026-10-19) | per kept commune: the first 4 real streets by name, one `Area`, one planned street, one street in several postal localities; raw lines (BOM kept) |

# MintPlayer.Spark.ReferenceData.Sources.Austria

The Austrian reference-data adapter (docs/reference_data_PRD.md §4.4, plan R8). `spark.AddReferenceData().AddAustria()`.

| Source id | What | Change check | Default schedule |
|---|---|---|---|
| `at-bev-adressregister` | BEV "Adresse Relationale Tabellen – Stichtagsdaten" (`data.bev.gv.at/download/Adressregister/Adresse_Relationale_Tabellen_Stichtagsdaten.zip`, ≈118 MB, generated at the start of Q2 and Q4): GEMEINDE, ORTSCHAFT, STRASSE, ADRESSE, `Aktualitaetsstand.txt` | `ETag` (most weekly checks report "no change"); the version is the record date (Stichtag) | `0 0 6 * * 1` |
| `at-statistik-regionen` | Statistik Austria `reglisten.zip`: `polbezirke.csv` (Bundesländer and political districts) | `ETag` | `0 0 6 1 * *` |
| `at-statistik-aenderungen` | Statistik Austria `aenderungenVerwaltungsgliederung.zip`: `aenderungenGemeinden_ab2002.ods` (code changes, mergers, splits, district mergers, renames, status changes since 2002) | `ETag` | `0 15 6 1 * *` |

The paid BEV API (€480 a year per external application, certificate authentication) is not used.

**Ids.** Bundesländer `places/at/bundesland/{1…9}`, districts `places/at/bezirk/{3 digits}`, Gemeinden `places/at/gkz/{GKZ}`
(a recode keeps the GKZ that minted the id: Ebergassing is `places/at/gkz/32401` with the dated code 30729 since 2017),
Ortschaften `places/at/okz/{OKZ}`, streets `streets/at/skz/{SKZ}`, postal codes `postalcodes/at/{PLZ}`.

**History** since 2002 (`HistoryKnownSince`), from the change file: district mergers re-key the GKZ (recode + reparent:
Ebergassing 32401 → 30729 when Wien-Umgebung was dissolved, 2017), mergers under a new GKZ (Fürstenfeld 62280, 2025),
splits (Murfeld 62347, 2020: `part` edges to Sankt Veit in der Südsteiermark and Straß in Steiermark) and renames
(Lenzing → Lenzing an der Ager, 2024-11-28). The 1945–2001 changes exist only as a PDF and are not imported. A
Gemeinde's district is the first three digits of the GKZ valid in a period (Austria's own code rule, every re-keying is
in the change file). Ortschaften hang off their current Gemeinde: BEV publishes no Ortschaft history.

**Postcodes (verified).** The PRD listed "postcode per address" as unverified. Verified on the 2026-10-01 tables: every
one of the ADRESSE rows carries a `PLZ` (2,232 distinct codes; none empty), so the street ↔ postcode links come from the
addresses. A street may have several postcodes (Stadtbergen in Fürstenfeld: 8280 and 8362).

**Attribution.** BEV's terms (`Nutzungsbedingungen.txt`, `Licensing_Conditions.txt` in the download) grant unlimited use
on condition that every release quotes "© Österreichisches Adressregister, Stichtagsdaten vom TT.MM.JJJJ"; the product
page names CC BY 4.0. The record date is this source's version in the release manifest (`Sources[at-bev-adressregister]
.Version`); the static credit line points there, because a `SourceLicence` cannot carry a per-import date. Statistik
Austria: "Q: STATISTIK AUSTRIA" (CC BY 4.0).

**Catches.** BEV CSVs are UTF-8 with BOM, `;`-separated and fully quoted; `Aktualitaetsstand.txt` is Windows-1252. The
format changed in October 2022 (the reader matches columns by header name). The regional lists are UTF-8 without BOM
with two title lines before the header. The change file is an ODS workbook (`content.xml` read with DTDs prohibited;
its rows repeat empty cells 16,378 times, capped). Names in the change file carry annotations ("Murfeld (ohne KG
Seibersdorf bei St. Veit)"); the plain name is preferred. Minority-language names (Croatian, Hungarian, Slovene) are not
in the BEV tables.

## Fixtures (real samples, RS8; cut 2026-10-19)

| File | Cut from | Rule |
|---|---|---|
| `GEMEINDE.cut.csv`, `ORTSCHAFT.cut.csv` | BEV ZIP (ETag `"710db51-65da09028afc0"`, Stichtag 2026-10-01), entries fetched by HTTP range request after reading the central directory | Gemeinden 10101 Eisenstadt, 10201 Rust, 30729 Ebergassing, 41713 Lenzing an der Ager, 61060 Sankt Veit in der Südsteiermark, 62280 Fürstenfeld and their Ortschaften |
| `STRASSE.cut.csv`, `ADRESSE.cut.csv` | the same ZIP | per Gemeinde the first 3 streets with addresses, one with several postcodes, one with a name addition; per street one address per distinct postcode; raw lines |
| `Aktualitaetsstand.txt` | the same ZIP | whole (Windows-1252) |
| `polbezirke.cut.csv` | Statistik Austria `reglisten.zip` (2026-10-15) | the title lines, the header and the districts of the kept Gemeinden |
| `aenderungenGemeinden_ab2002.cut.ods` | Statistik Austria `aenderungenVerwaltungsgliederung.zip` (2026-10-12) | the workbook rebuilt with the same parts; each sheet keeps its two title rows, the rows naming the kept codes (and 32401, 62252, 62267, 62347, 61061) and its "Q: STATISTIK AUSTRIA" footer; rows copied verbatim |

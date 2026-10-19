# MintPlayer.Spark.ReferenceData.Sources.Slovenia

The Slovenian reference-data adapter (docs/reference_data_PRD.md §4.4, plan R8): GURS's Register of Spatial Units
(RPE) and Register of Addresses (RN) through the free WFS. `spark.AddReferenceData().AddSlovenia()`.

## Sources

| Id | Read from | Schedule (default) | Change check |
|---|---|---|---|
| `si-rpe` | `https://ipi.eprostor.gov.si/wfs-si-gurs-rpe/wfs`: `STATISTICNE_REGIJE`, `OBCINE`, `NASELJA`, `ULICE`, `POSTNI_OKOLISI` | `0 30 5 * * 0` | the newest `DATUM_SYS` of every layer (one row each, `sortBy=DATUM_SYS DESC`) |
| `si-rn` | `https://ipi.eprostor.gov.si/wfs-si-gurs-rn/wfs`: `REGISTER_NASLOVOV` | `0 30 5 * * 0` | as above |

- **No bulk URL** exists (bulk only through the JGP/eGP apps), so layers are paged from the WFS (GeoServer, WFS 2.0.0,
  `outputFormat=csv`, 50,000 rows a page, sorted by EID) **without geometry**: only the listed attributes are requested.
- **Licence and credit:** CC BY 4.0. "Geodetska uprava Republike Slovenije, Register prostorskih enot, <year>" and
  "Geodetska uprava Republike Slovenije, Register naslovov, <year>", the year being the year of the newest `DATUM_SYS`
  (2026 for the fixtures), with a statement that the data was modified. GURS may change structures and terms at any time.
- **Ids:** municipalities and statistical regions by their code (`places/si/obcina/90`, `places/si/statreg/12`);
  settlements and streets by their **EID**, persistent since the register renewal of March 2024 (expect id changes
  around that date: a pre-2024 extract would mint other ids): `places/si/naselje/110300000101230039`,
  `streets/si/eid/110400000162285756`; postal codes `postalcodes/si/6330`. The composite codes (`SESTAVLJENA_SIFRA`) are kept as codes.
- **History:** current state only; "history known since" is the first import. The RPE also publishes `*_H` history
  layers (`OBCINE_H`, `NASELJA_H`, …): not read yet.

## Postcode linkage (verified first, as the PRD asks)

Verified at field level on the real WFS (2026-10-19): `REGISTER_NASLOVOV` carries `EID_ULICA` (empty where a
settlement has no street system), `EID_NASELJE` and `POSTNI_OKOLIS_SIFRA` on every address, so street ↔ postcode and
settlement ↔ postcode come from it. The address register also gives each municipality its statistical region
(`EID_STATISTICNA_REGIJA`), which the `OBCINE` layer lacks. A street may lie in another postal area than its
settlement's (Arze in Portorož → 6330 Piran). No house numbers are requested or kept (RD5).

## Catches handled

- Bilingual areas: `NAZIV_DJ` holds the Italian (Izola, Koper, Piran, Ankaran) or Hungarian (Lendava, Dobrovnik, Hodoš)
  official name; the language comes from the municipality (`SloveniaProfile.BilingualMunicipalities`). A second name
  elsewhere is reported as a warning, not guessed.
- Postal area names hold both languages in one string ("Piran - Pirano"): split on " - ".
- Slovenia has no regional self-government: the statistical region level is optional; a municipality without address
  rows (Ankaran in the fixture) stays under the country.

## Fixtures (real WFS responses, RS8; retrieved 2026-10-19)

| File | Request | Rule |
|---|---|---|
| `STATISTICNE_REGIJE.cut.csv` | the whole layer (12 rows) | the source's property list, no geometry |
| `OBCINE.cut.csv` | `OBCINE` | Piran (90) and Ankaran (213) |
| `NASELJA.cut.csv` | `NASELJA`, `CQL_FILTER=EID_OBCINA=…(Piran)` | Piran, Portorož and Seča (no street system) |
| `ULICE.cut.csv` | `ULICE`, Piran's | the first three streets by name in Piran and Portorož |
| `POSTNI_OKOLISI.cut.csv` | `POSTNI_OKOLISI`, `SIFRA IN (6320,6330)` | both rows |
| `REGISTER_NASLOVOV.cut.csv` | `REGISTER_NASLOVOV`, `OBCINA_SIFRA=90`, the source's property list (no house numbers) | one row per (settlement, street, postal code) of the cut |

Rows are kept byte for byte (UTF-8, no BOM, LF, GeoServer's CSV quoting). Ledger: `Ledger/si.ndjson`, filled by
`--refdata-build-seed` (see docs/guide-reference-data.md).

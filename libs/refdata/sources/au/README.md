# MintPlayer.Spark.ReferenceData.Sources.Australia

The Australian reference-data adapter (docs/reference_data_PRD.md §4.4, plan R8): Geoscape G-NAF.
`spark.AddReferenceData().AddAustralia()`.

## Source

| Id | Read from | Schedule (default) | Change check |
|---|---|---|---|
| `au-gnaf` | data.gov.au dataset `19432f89-…` → `g-naf_{mmmyy}_allstates_gda2020_psv_*.zip` (≈1.85 GB, quarterly: Feb, May, Aug, Nov) | `0 0 6 20 2,5,8,11 *` | the catalogue's newest GDA2020 PSV resource (CKAN `package_show`); its file name names the release |

Only `{STATE}_STATE`, `{STATE}_LOCALITY`, `{STATE}_STREET_LOCALITY`, `{STATE}_ADDRESS_DETAIL` and the
`STREET_TYPE` and `LOCALITY_CLASS` authority codes are extracted (pipe-separated, CRLF, no quoting, upper case).

## Licence, credit and the use restriction (pass on!)

G-NAF is licensed under the **Open G-NAF End User Licence Agreement**, not plain CC BY. Its text was read from the EULA
PDF on data.gov.au on 2026-10-19 (`20160226-eula-open-g-naf.pdf`; research could not fetch it earlier). Every response
with Australian data carries the credit line:

> Incorporates or developed using G-NAF © Geoscape Australia licensed by the Commonwealth of Australia under the Open
> Geo-coded National Address File (G-NAF) End User Licence Agreement.

and the EULA's **use restriction**, which a host's API terms must pass on to every API user (PRD §4.12):

> You must not use the Licensed Material for the generation of an address or a compilation of addresses for the
> sending of mail unless You have verified that each address to be used for the sending of mail is capable of
> receiving mail by reference to a secondary source of information other than the Licensed Material.

The EULA also asks to keep the copyright notice, a reference to the licence and its disclaimer, a link to the material,
and to indicate modifications: the credit line states that names were title-cased and that no house numbers are kept.
Sources are credited, never presented as endorsers (EULA "No endorsement").

## Model

- **Ids:** states by abbreviation (`places/au/state/ACT`, identity `gnaf-state:{STATE_PID}`); localities and districts
  by the persistent G-NAF locality PID (`places/au/gnaf/loc537c3f114213`); street localities by their PID
  (`streets/au/gnaf/ACT466`): one street within one locality, the street's own `GNAF_STREET_PID` kept as a code (the
  Barton Highway is one street PID in several localities); `postalcodes/au/2612`.
- **Levels:** country → state → (the ACT's districts, class `D`) → suburb/locality (the city-line unit, the municipality
  level of the profile). Alias-only localities (class `A`) are skipped. **Local government areas are not imported:**
  Geoscape Administrative Boundaries and ABS ASGS (Edition 3; Edition 4 from July 2026) need the boundary or mesh-block
  allocation files, which this adapter does not read yet.
- **Postcodes:** from the principal, current addresses (`ADDRESS_DETAIL.POSTCODE`); `9999` ("no postcode") is ignored.
- **Time zones (Q-R10):** per state (`AustraliaProfile.StateZones`), with the postcode exceptions Broken Hill (2880,
  `Australia/Broken_Hill`) and Lord Howe Island (2898, `Australia/Lord_Howe`); checked against the tz database's zone list.
- **History:** G-NAF is a current-state product (retired records leave the release; `DATE_CREATED` is a record date, not
  a legal one): "history known since" is the first import.

## Fixtures (real samples, RS8; cut 2026-10-19)

Read with HTTP range requests from the August 2026 GDA2020 release ZIP (`g-naf_aug26_allstates_gda2020_psv_110.zip`):
the central directory from the last 256 KB, then each entry's local header and deflated bytes.

| File | Rule |
|---|---|
| `ACT_STATE_psv.psv` | whole (one row) |
| `ACT_LOCALITY_psv.psv` | Braddon, Reid, Turner, Hall and Belconnen: gazetted, topographic and district rows |
| `ACT_STREET_LOCALITY_psv.psv` | the first three current street localities by name in each of those localities |
| `ACT_ADDRESS_DETAIL_psv.psv` | one principal, current address per (street locality, postcode) of those streets |
| `Authority_Code_STREET_TYPE_AUT_psv.psv`, `Authority_Code_LOCALITY_CLASS_AUT_psv.psv` | whole |

Rows are kept byte for byte (CRLF). Address rows are public register records (no personal data). Ledger:
`Ledger/au.ndjson`, filled by `--refdata-build-seed` (see docs/guide-reference-data.md).

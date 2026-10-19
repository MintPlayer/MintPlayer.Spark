# MintPlayer.Spark.ReferenceData.Sources.UnitedStates

The United States reference-data adapter (docs/reference_data_PRD.md §4.4, plan R8), from U.S. Census Bureau files.
`spark.AddReferenceData().AddUnitedStates()`.

## Sources

| Id | Read from | Schedule (default) | Change check |
|---|---|---|---|
| `us-codes` | `www2.census.gov/geo/docs/reference/codes2020/national_state2020.txt`, `national_county2020.txt` (pipe-separated, CRLF) | `0 0 6 1 * *` | `ETag`/`Last-Modified` of the county file |
| `us-county-changes` | `www.census.gov/…/county-changes.html` and its decade pages `county-changes.{1970…2010}.html` | `0 0 6 1 * *` | SHA-256 of the extracted text (no validator, no checksum file) |
| `us-addrfeat` | `www2.census.gov/geo/tiger/TIGER{year}/ADDRFEAT/tl_{year}_{GEOID}_addrfeat.zip`, one per county (only the `.dbf` is read) | `0 0 6 15 * *` | the newest yearly release directory that exists (2026 released 2026-10-14) |

- **Licence and credit:** U.S. Government works, public domain. Credited as "Source: U.S. Census Bureau, …", never as an
  endorser, and the trademark "TIGER" never appears in a product or package name (it appears only in the Census Bureau's
  own download paths). The address-range credit line says, in every response, that **ZIP Codes are derived from Census
  address ranges and are not authoritative USPS data** (potential ranges; ZCTA ≠ ZIP; the HUD crosswalk is excluded).
- **Ids:** GEOIDs (`places/us/geoid/48`, `places/us/geoid/48301`), `streets/us/linearid/{LINEARID}`, `postalcodes/us/{ZIP}`.
  A recode keeps the id of the old code (Wade Hampton 02-270 → Kusilvak 02-158, 2015, ledger alias); a GEOID reissued to
  a different unit gets a date suffix (Petersburg Census Area 02-195 → Petersburg Borough `places/us/geoid/02195@2013-01-03`).
- **History:** county changes since 1970 from the decade pages, which are prose with no validity model. The mapper reads
  entries "Name, State (SS-CCC): … effective Month D, YYYY" under their headings: *new* (created, a split or a merge,
  or a reissue), *deleted* (absorbed or split; no successor = unresolved, Q-R4), *name and/or code changes* (recode,
  rename; the "changed … to" mirror entries are skipped), *substantial boundary changes* (cede). Entries of states the
  code lists do not carry are skipped. "History known since" 1970.
- **Time zones (Q-R10):** a state wholly in one zone gives its zone to its counties (`UnitedStatesProfile.SingleZoneStates`,
  checked against the tz database's zone list when the global baseline supplies it); a county of a split state (AK, AZ,
  FL, ID, IN, KS, KY, MI, NE, ND, OR, SD, TN, TX) gets no zone, with the reason stated, until a per-county source is joined.
- **Units:** states and counties (county equivalents). Incorporated places are not imported; the city line of an
  address stays free text checked only by shape.

## Catches handled

- The `.dbf` is read by a small streaming dBASE III reader (`DbfReader`): field descriptors up to `0x0D`, deleted
  records skipped, UTF-8 as the `.cpg` states; no shapefile library, no geometry.
- One LINEARID is one street name feature; several edges (rows) share it, each with a ZIP per side.
- A county change page lists one change under several headings (Bedford: deleted, and a boundary change of Bedford
  County): one event per (date, kind, unit), the duplicate "Added the former …" entry skipped.
- Decade pages are HTML: reduced to text (scripts and tags removed, entities decoded, whitespace collapsed); downloads
  are data and never executed.

## Fixtures (real samples, RS8; cut 2026-10-19)

| File | Cut from | Rule |
|---|---|---|
| `national_state2020.cut.txt` | `national_state2020.txt` | the header and AK, TX, VA |
| `national_county2020.cut.txt` | `national_county2020.txt` | the header and TX 48-301 (Loving), VA 51-019 (Bedford), AK 02-063, 02-066, 02-105, 02-158, 02-195, 02-198 |
| `tl_2026_48301_addrfeat.dbf` | `tl_2026_48301_addrfeat.zip` (TIGER2026, Loving County TX) | the whole attribute table (19 records, 6 KB) |
| `county-changes.2010.cut.txt` | `county-changes.2010.html` | the text extraction the source performs, whole (the 2010s list) |
| `county-changes.2020.cut.txt` | `county-changes.html` | the same, the 2020s list (Connecticut's planning regions, read but not mapped: no county code in the entry) |

Ledger: `Ledger/us.ndjson`, filled by `--refdata-build-seed` (see docs/guide-reference-data.md).

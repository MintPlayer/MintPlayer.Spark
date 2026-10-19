# MintPlayer.Spark.ReferenceData.Sources.Belgium

The Belgian reference-data adapter (docs/reference_data_PRD.md §4.4): Statbel REFNIS (units since 1970), Statbel NIS6 (sections), BeST postal streets (postal localities and streets), the BeST full bundle (street statuses, homonym additions, hamlets, streets without a postal link), the Flemish 2025 merger diff files and the Flemish Basisregisters API, mapped with lineage derived without curation, the Belgian country profile, the id ledger and real sample fixtures.

Fixtures (where present) are real samples cut from the publisher's files; their licences and credit lines are in the source classes and at `/api/v1/sources`.

## Sources

| Id | Source | Default schedule (UTC) | Change check | Catches |
|---|---|---|---|---|
| `be-refnis` | Statbel REFNIS | Mondays 04:30 | the dated file name on the publisher's page | cp1252, pipes, CRLF; inclusive end dates |
| `be-nis6` | Statbel NIS6 | Mondays 04:30 | the dated file name | section codes change between editions |
| `be-postalstreets` | BeST postal streets | daily 04:00 | `ETag` | three namespace spellings; Brussels BOM |
| `be-best` | BeST full bundle, `best-full-latest.zip` (≈310 MB) | Sundays 04:00 | `ETag`/`Last-Modified`, then the ZIP central directory (the last 64 KB by range request, ZIP64 aware) and the CRCs of the non-address entries: a bundle whose address files alone changed is "no change" | only the non-address entries (≈3 MB) are fetched, each by range request and checked against its CRC; inner files matched by pattern (`L72` dropped from 2026-10-19, Lambert 2008); current state only (retired streets of merged-away Flemish municipalities disappear) |
| `be-flemish-diff` | Digitaal Vlaanderen, 2025 merger diff files (2024-12-20) | 1 January 05:00 (one-off) | pinned SHA-256 (`1e0f90bb…6bf8`); any other file is refused before it is opened | the address file (79 MB) and the Rijksregister variant are never extracted; municipality file has a BOM, street file not |
| `be-flemish-api` | Flemish Basisregisters API v2 (public, no key) | Mondays 05:00 | SHA-256 of the municipality list (no ETag) | one request per municipality and per retired street of a retired municipality; change feeds need a key (not used) |

**Credit lines.** Statbel: "Source: Statbel" + https://statbel.fgov.be/en/cc-40 + a statement of changes. BeST: the three regional providers "via FOD BOSA BeST Address" (CC BY 4.0, modified). Flemish register: "Bron: Digitaal Vlaanderen" (Modellicentie Gratis Hergebruik; ⚠️ the diff files' own licence wording is unverified, PRD §4.4).

## What each source adds

- **`be-best`:** homonym additions as `Qualifier` (Antverpiastraat (HO)/(EK)), `Hamlet` types, and current streets no postal code links (with their municipality, without postal code). Retired, proposed, reserved and rejected streets are never added from BeST. BeST municipalities, postal infos and Walloon parts are checked against the build; a part of municipality without a street cannot be linked (BeST states no parent) and is listed as a warning. A postal info code no street links to gets its document.
- **`be-flemish-diff` (Q-R2):** a 1 → 1 mapping **continues** the street: the old id becomes a ledger alias of the same document and a code dated until the merger; the street gets a period in the old municipality until the merger (postal codes assumed unchanged: the file states none); a renamed homonym keeps its old name as `historic`. A street of a newly created municipality starts on the merger date; one that joins a continuing municipality (Borsbeek into Antwerpen) keeps its start. Several old ids into one is a **merge**, one into several a **split**: the old streets become documents ended on the merger date, with a lineage event (`lineage/be/{date}/merge|split/best-vlg-{id}`). The date is REFNIS's (Q-R3).
- **`be-flemish-api`:** official languages (the API is the authority; a disagreement with REFNIS is listed), facility languages as `facility-language` codes (data for forms and mail, never for display), and retired streets of municipalities that no diff file covers (the 2019 mergers), ended with their municipality. A municipality retired in the API but open in REFNIS is listed.

## Fixtures (real samples, RS8)

| File | Cut from | Rule |
|---|---|---|
| `TU_COM_REFNIS-20250101.cut.txt` | Statbel `TU_COM_REFNIS-20250101.zip` (2026-10-19) | every region and province row; the arrondissements and municipalities of the seed (with the municipalities that ended in 1976 and match a seeded section by name); bytes kept (cp1252, CRLF) |
| `NIS6withnamefrom01012025.cut.xlsx` | Statbel `NIS6withnamefrom01012025.xlsx` (2026-10-19) | the sections of the seeded municipalities; the workbook rebuilt with the same parts, shared strings re-indexed |
| `{Region}_postal_street.cut.csv` | BeST `postalstreets-latest.zip` (2026-10-18) | for each (postcode, locality) of a seeded municipality with at least 3 streets: the first 3 by name plus one "kerk"/"église" street; raw lines kept (quoting quirks, BOM) |
| `{Region}{Kind}{yyyyMMdd}.cut.xml` | BeST `best-full-latest.zip` (ETag `"127af1ba-65e1300f96d80"`, fetched 2026-10-19 by range request; inner files of 2026-10-15/16/17, CRCs checked) | the objects of the seeded municipalities, postal codes, streets and Walloon parts (those of the postal-streets cut), the old street ids of the diff cut, and Antwerpen's homonyms 202/203; object blocks kept byte for byte (BOM, indentation, line ends) |
| `fusies-verschilbestand-gemeenten-20241213.cut.csv` | the diff zip (SHA-256 above), fetched 2026-10-19 | the rows of the seeded municipalities (11002, 11007, 11056, 46003, 46013, 71022, 73040); BOM and CRLF kept |
| `fusies-verschilbestand-straatnamen-20241218.cut.csv` | same | every row whose old or new street id is in the postal-streets cut; CRLF kept |
| `flemish-api-gemeente-{nis}.json`, `flemish-api-straatnaam-{id}.json` | `https://api.basisregisters.vlaanderen.be/v2/gemeenten/{nis}` and `/straatnamen/{id}`, recorded 2026-10-19 | the seeded merged municipalities, Antwerpen, Voeren (73109, facilities in French; not in the seed, read and skipped), and four streets (Hasselt's old Kerkstraat 114824, Borsbeek's 3681, Antwerpen's 244, Hasselt's 232304) |

No bpost data (RD-DEF 9). Ledger: `Ledger/be.ndjson`, regenerated by `--refdata-build-seed` (see docs/guide-reference-data.md).

Street lineage events name street ids in `Operations`; the derivation's succession edges are for places, so it lists them as "not in the build" warnings until it skips non-place ids.

# Reference data: research

Date: 2026-10-17. Evidence for the reference-data PRD, `docs/reference_data_PRD.md`.

The reference-data work gives Spark applications one shared source of countries, administrative places with their
history, postal codes and streets. Names are localized, ids are deterministic, and a public, anonymous typeahead API
serves the data. The commerce suite is the first consumer (K8 and §4.18 of `docs/commerce_PRD.md`). This file collects
the evidence the PRD cites:
- what Belgium publishes, and how its municipalities changed since 1970;
- which other countries publish an open address register, in what form and under what licence;
- which global sources fill the gaps, and which must stay out of a public API;
- the attribution those licences require;
- how romanization and language tags work, with .NET behaviour measured;
- how other registries record history, and what that implies for identity;
- how the result fits Spark today, with file:line.

**The PRD's decision log is authoritative for decisions; this file holds evidence only.** Where a passage below
recommends something, it records the researcher's reading of the evidence on this date; the PRD may have decided
otherwise.

**How the evidence was gathered.** Four read-only research passes, all on 2026-10-17:
1. **Belgium:** official pages, the published XSDs and the real files parsed directly. That means byte-range samples of
   the BeST bundle, the full small files (XSD, REFNIS, NIS6, bpost XLS, the Flemish merger difference files), and live
   probes of the public BeST API and the Flemish Basisregisters API.
2. **Other countries and global sources:** publishers' pages, licence texts and API documentation, one country at a time.
3. **The model:** how INSEE, BFS, Who's On First, GeoNames, Wikidata, OSM/OpenHistoricalMap and INSPIRE record history;
   language tags and romanization, with .NET 10.0.12 behaviour measured on Windows (ICU); RavenDB 7.x behaviour and the
   licence matrix from ravendb.net/buy.
4. **Spark fit:** the repository itself, for the new-app recipe, anonymous endpoints, search, seeding and client reuse.

Downloads were treated as data only: parsed with the researchers' own scripts, nothing executed. The Belgian pass ran
out of web-search budget near its end; what it could not verify is marked.

**Conventions**
- Repository paths are relative to the repository root, read on 2026-10-17 at master `e39b16de`. Abbreviations:
  `S/` = `libs/spark/MintPlayer.Spark/` · `NG/` = `libs/node_packages/ng-spark/`; §7.3 adds QE, GS and GQ.
- **✔** marks a line re-read against the checkout while compiling this file. Other line numbers come from the read-only
  research passes.
- **[unverified]** marks a claim the research flagged as not confirmed by a fetched page or file, or as needing a check
  before it is relied on.
- **(inference)** marks reasoning, not a documented or measured fact.
- Counts name their origin where it matters. *file* = parsed from the published file; *API* = summed from live API
  queries; **≈** = estimated from byte ratios or samples.
- Nothing here is legal advice. Licence readings are the researchers' reading on this date.

---

## 1. Belgium

### 1.1 Sources at a glance

| Source | Publisher | Content | Format | Cadence | Licence | History |
|---|---|---|---|---|---|---|
| **BeST Address** | FPS BOSA, consolidating the three regional registers | municipalities, postal infos, street names, addresses; parts of municipality for Wallonia only | ZIP of per-region ZIPs of XML (XSD v24.4); per-region CSVs; free API; self-hostable BeST-in-a-Box | weekly ("wekelijks bijgewerkt"; DCAT `WEEKLY`) | CC BY 4.0; EU High-Value Dataset | current snapshot only: no retired municipalities, no predecessor or successor links |
| **Flemish Gebouwen- en Adressenregister** | Digitaal Vlaanderen | municipalities, street names, addresses, postinfo | REST v2, JSON-LD; bulk download "in afbouw" | live | Modellicentie gratis hergebruik v1.0 (the same data is CC BY 4.0 inside BeST) | keeps `gehistoreerd` municipalities and streets; change feeds need an API key |
| **Flemish 2025 verschilbestanden** | Digitaal Vlaanderen | old→new ids of municipalities, streets and addresses for the 2025 mergers | CSV, XLSX | one-off (December 2024) | Flemish open data; Modellicentie assumed [unverified] | yes, 2025 only |
| **ICAR** | Service public de Wallonie (SPW) | address points | SHP, CSV, ESRI REST, ATOM | daily extract | CC BY 4.0 | street ids kept across a merger (new version) |
| **BeSt Brussels / UrbIS** | Paradigm (formerly CIRB/CIBG) | address register | XML, GPKG, JSON; geocoding API | weekly | CC0 on Geobru; another statement says "UrbIS license" | a few retired streets |
| **REFNIS** (`TU_COM_REFNIS`) | Statbel | region, province, arrondissement and municipality codes, names in nl/fr/de | pipe-separated text (cp1252) in a ZIP; XLSX | per edition (2019, 2025) | CC BY 4.0 | every code since 1970 with validity dates; no successor links |
| **NIS6 sections** | Statbel | 2,616 sections (deelgemeenten) | XLSX | per edition | CC BY 4.0 | implicit (sections keep the old NIS5 prefix); codes change between editions |
| **Merger tables** | Statbel (2019 note); Statistiek Vlaanderen (2025 page) | before/after tables | PDF; web page | per reform | CC BY 4.0 (Statbel) | the links for 2019 and 2025 |
| **bpost postcode lists** | bpost | postcodes with localities, main municipality, province | legacy BIFF8 `.xls` | editions named by year (`…_2025.xls`, modified 2026-03-16) | all rights reserved by the site disclaimer; the file itself states no licence | none |
| **Wikidata** | Wikimedia | English exonyms; partial history | SPARQL, dumps | live | CC0 | incomplete and inconsistent (§1.15) |

### 1.2 BeST Address (Belgian Streets and Addresses), FPS BOSA

**What it is.** BeST consolidates the three regional authentic address registers. The legal basis is the cooperation
agreement of 22 January 2016 between the Federal State and the three Regions (BS 15/02/2016), with a revised agreement
signed on 17 July 2019. The Regions are the managers ("beheerders") and the municipalities are the initiators. BOSA
publishes the consolidated file (BeST-in-a-Box terms §5–6,
<https://opendata.bosa.be/download/best/bestinabox/bestinabox_gebruiksvoorwaarden.pdf>, last revised 27/01/2025).

**Public page:** <https://opendata.bosa.be/index.nl.html> (FR: <https://opendata.bosa.be/index.fr.html>). It states that the
files hold *municipalities, street names, addresses, part of municipalities (**alleen Wallonië**), postalinfo*. Licence:
"Creative Commons 4.0 Attribution".

**Downloads** (HEAD on 2026-10-17; every file supports `Accept-Ranges: bytes` and carries an `ETag` and `Last-Modified`):

| File | URL | Size | Last-Modified | Notes |
|---|---|---|---|---|
| Full XML (3 regions) | https://opendata.bosa.be/download/best/best-full-latest.zip | 312,512,009 B | 2026-10-07 01:06 GMT | "wekelijks bijgewerkt"; a zip of zips |
| CSV Flanders (addresses) | https://opendata.bosa.be/download/best/openaddress-bevlg.zip | 152,822,852 B | 2026-10-07 | derived from the XML |
| CSV Brussels | https://opendata.bosa.be/download/best/openaddress-bebru.zip | 18,024,020 B | 2026-10-07 | |
| CSV Wallonia | https://opendata.bosa.be/download/best/openaddress-bewal.zip | 62,601,600 B | 2026-10-07 | |
| Streets with ≥ 1 address, per postcode | https://opendata.bosa.be/download/best/postalstreets-latest.zip | 2,187,899 B | 2026-10-07 | "enkel huidige straten" |
| Streets without addresses | https://opendata.bosa.be/download/best/postalstreets-empty-latest.zip | 221,412 B | 2026-10-07 | quays, bridges, field roads; postcode approximate |
| XSD v24.4 | https://opendata.bosa.be/download/best/best-xsd-v24_4.zip | 3,538 B | 2023-10-27 | BeStCommon / FullDownload / Mutations |
| XSD v24.3 changes | https://opendata.bosa.be/download/best/best-xsd-v24_3-changes.zip | 639,957 B | 2023-07-18 | |
| Mutation examples v1.2 | https://opendata.bosa.be/download/best/best-mutations-examples-v12.zip | 194,442 B | 2024-12-12 | examples and guide only |
| Test mutation files (Flanders) | https://opendata.bosa.be/download/best/best-mutations-test-fl.zip | 3,981,056 B | 2026-03-20 | test data only |
| DCAT-AP | https://opendata.bosa.be/download/best/dcat.rdf | 23,503 B | 2025-07-20 | `WEEKLY`, `CC_BY_4_0`, **HVD** (Reg. (EU) 2023/138, geospatial) and INSPIRE |
| Last Lambert 72 archive | https://opendata.bosa.be/download/best/archive/lambert72/openaddress-bevlg-20261007.zip (also `-bebru-`, `-bewal-`) | | 2026-10-07 | the page's "Full Download XML" archive link points to the Flemish CSV (a page bug) |

**Pending change: Lambert 2008 from 19 October 2026.** The page says the migration planned for the previous weekend was
postponed because not all regions had delivered. From 19 October, coordinates in the XML become **only** Lambert 2008
(EPSG:3812), and file names drop the `L72` suffix. The CSV columns `EPSG:31370_x/_y` become `EPSG:3812_x/_y`, and
positions may shift by up to 20 cm. Typeahead does not depend on coordinates, but **an importer must not hard-code file
names or column names.**

**Structure of the full bundle** (from the ZIP central directory, read from the last 64 KB with a range request):

| Inner entry (each one is itself a .zip) | Compressed | Inner XML (uncompressed) |
|---|---|---|
| BrusselsMunicipality20261003.zip | 931 B | 9,135 B |
| BrusselsPostalInfo20261003.zip | 636 B | 11,693 B |
| BrusselsStreetName20261003.zip | 155,716 B | 5,575,237 B |
| BrusselsAddress20261003.zip | 23,672,314 B | **1,427,681,594 B** |
| WalloniaAddress20261006L72.zip | 77,014,910 B | **> 4 GiB (ZIP64 marker)** |
| WalloniaStreetName20261006L72.zip | 1,418,355 B | 50,625,035 B |
| WalloniaPostalInfo20261006L72.zip | 2,784 B | 146,353 B |
| WalloniaPartOfMunicipality20261006L72.zip | 20,868 B | 735,887 B |
| WalloniaMunicipality20261006L72.zip | 5,970 B | 145,419 B |
| FlandersPostalInfo20261001L72.zip | 10,308 B | 215,600 B |
| FlandersMunicipality20261001L72.zip | 4,335 B | 122,719 B |
| FlandersStreetName20261001L72.zip | 1,431,528 B | ≈ 82 MB |
| FlandersAddress20261001L72.zip | 208,710,977 B | ≈ 7.6 GB |
| best-ccby-license.docx | 60,179 B | licence text (nl/fr/de/en) |

- The regions deliver on different days. The 7 October bundle holds Flemish data of 1 October, Brussels data of 3 October
  and Walloon data of 6 October.
- Naming is inconsistent. The Brussels inner *zip* has no `L72` but its address *XML* does; for Wallonia it is the reverse.
- **Every non-address entry together is only ≈ 3.05 MB compressed.** With HTTP range requests an importer can read the
  central directory and then fetch only those entries, without downloading 312 MB.

**Schema** (XSD v24.4, namespaces `http://fsb.belgium.be/data/common` and `.../mappingservices/FullDownload/v1_00`):
- Roots: `municipalityResponseBySource`, `postalInfoResponseBySource`, `partOfMunicipalityResponseBySource`,
  `streetNameResponseBySource`, `addressResponseBySource`. Each carries `source` (brussels | flanders | wallonia) and a
  `timestamp`.
- `IdentifierType` = `namespace` + `objectIdentifier` + `versionIdentifier`, all required. The Brussels specification says
  namespace + objectIdentifier "should form a URI unambiguously identifying the object… The URI should be resolvable".
- `GeographicalNameType` = `language` (nl | fr | de) + `spelling`.
- Fields per class:
  - **Municipality:** `code`, `name`+, `municipalityStatus`?
  - **PartOfMunicipality:** `code`, `name`+, status?. **It has no reference to its municipality.**
  - **PostalInfo:** `code`, `name`*, status?
  - **StreetName:** `code`, `name`+, `streetNameStatus`, `type` (**streetname | hamlet**), `homonymAddition`?,
    `isAssignedByMunicipality`, `isAssignedTo`* (road objects), and the lifecycle attributes `beginLifeSpanVersion` and
    `endLifeSpanVersion`?
  - **Address:** `code`, `position` (`pos` with `srsName`, `positionGeometryMethod`, `positionSpecification`),
    `sortField`?, `addressStatus`, `boxNumber`?, `houseNumber`, `officiallyAssigned`, `hasStreetName`, `hasMunicipality`,
    `hasPostalInfo`, `hasPartOfMunicipality`?, `isAssignedToAddressableObject`*. References include the referenced
    object's *versionIdentifier*.
- **Statuses.** Address and street: current, proposed, reserved, retired, rejected. Municipality, part of municipality and
  postal info: current, proposed, reserved, retired. Each status has `validFrom` and an optional `validTo`. The Brussels
  specification defines them as: must still be validated / validated / rejected / used to locate an addressable object /
  no longer used.
- **Mutations.xsd** (the delta format) adds `event` (date, type) and **`predecessor`/`successor`** identifiers on every
  add or update. The full download does **not** carry them, and the public page offers only examples and test files for
  mutations. The example guide covers street splits and merges (A=A+B, A=B+C, A+B=A, A+B=C).

**What is actually in the files** (parse of the real data; exact unless marked):

| | Brussels | Flanders | Wallonia |
|---|---|---|---|
| Namespace (municipality) | `https://databrussels.be/id/municipality` | `https://data.vlaanderen.be/id/gemeente/` (**trailing slash**) | `geodata.wallonie.be/id/Municipality` (**no scheme**) |
| `versionIdentifier` format | integer (e.g. `4`) | timestamp (`2002-08-13T16:33:18+02:00`) | integer |
| Municipalities | 19 (fr+nl, all) | 285 (nl only: 243; nl/fr/de: 30; nl/fr: 12) | 261 (fr+de+nl, all) |
| Municipality status element | absent | absent | absent: **current only** |
| `objectIdentifier` of a municipality | NIS code | NIS code | NIS code |
| PostalInfo | 47, **no names** ("Brussels does not provide postal names anymore", BeST release notes 1.10.5) | 528, one nl name that is a **slash-joined list** with the main municipality in capitals (9700 `Bevere/Edelare/Eine/…/Ooike/OUDENAARDE/Volkegem/Welden`) | 616, **no names** |
| PartOfMunicipality | – | – | 2,016 (fr: 1,922, de: 94); 67 names occur more than once (Flamierge ×3, ids 470–472); villages and hamlets, not only former municipalities |
| Street names | 5,978 in the file (5,972 current + 6 retired), all fr+nl | ≈ 89k including some retired and proposed; nl only; `homonymAddition` used (91 in an 18,757-record sample, e.g. `EK`, `HO`, `WI`, `BZL` = Antwerp districts) | ≈ 62k; sample: fr 97.4%, fr+nl 2.5% (Enghien, Mouscron, Comines-Warneton), de in the German-speaking municipalities; type `hamlet` occurs (41 in an 8,794 sample) |
| Retired objects | 6 streets, 3,361 addresses | retired streets and addresses **of current municipalities** (with `validTo` + `endLifeSpanVersion`) | **none** |
| Objects of merged-away municipalities | – | **dropped** (none of the Borsbeek 11007 streets are in the file) | n/a (Wallonia re-points them) |
| Address CRS (until 19 October) | EPSG:31370 | EPSG:31370 | EPSG:31370 |
| File quirks | UTF-8 **BOM**, tab-indented | `encoding="utf-8"`, 2 spaces | 1 space |

**National totals from the public BeST API** (sum of `nisCode=<digit>*` queries, 2026-10-17; exact):

| | Brussels | Flanders | Wallonia | Belgium |
|---|---|---|---|---|
| Municipalities | 19 | 285 | 261 | **565** |
| Postal infos | 47 | 528 | 616 | **1,191** |
| Parts of municipality | – | – | 2,016 | **2,016** |
| Streets, current | 5,972 | 88,132 | 61,951 | **156,055** |
| Streets, retired (in BeST) | 6 | 1,083 | 0 | 1,089 |
| Streets, proposed | | | | 255 |
| Addresses, current | 663,383 | 3,885,536 | 2,107,430 | **6,656,349** |
| Addresses, retired | 3,361 | 284,874 | 0 | 288,235 |
| Addresses, proposed | 180,675 | 48,207 | 0 | 228,882 |

**Derived CSVs** (the bundle page's "Tools"):
- `openaddress-be*.zip` columns: `EPSG:31370_x, EPSG:31370_y, EPSG:4326_lat, EPSG:4326_lon, address_id, box_number,
  house_number, municipality_id, municipality_name_de/fr/nl, postcode, postname_fr, postname_nl, street_id,
  streetname_de/fr/nl, region_code (BE-WAL…), status`. The ids are bare objectIdentifiers.
- `postalstreets-latest.zip` holds Brussels, Flanders and Wallonia `_postal_street.csv` files with the columns
  `postal_id, postal_nl/fr/de, street_nl/fr/de, city_nl/fr/de, citypart_nl/fr/de, street_prefix, street_no,
  street_version, city_prefix, city_no, city_version, citypart_prefix, citypart_no, citypart_version`.
  - Rows: Brussels 5,155 (exact); Flanders ≈ 83.8k and Wallonia ≈ 58k (from the byte ratio): **≈ 147k (postcode,
    street) pairs**.
  - For Wallonia the row also carries the **part of municipality** (`citypart_*`). That is the only cheap source of the
    part-of-municipality → municipality link.
- `postalstreets-empty-latest.zip`: 848 / 7,333 / 3,843 streets without addresses (Brussels / Flanders / Wallonia). Their
  postcode is approximate: "bij fusiegemeentes wordt de numeriek laagste postcode vermeld".

**APIs.**
- Free hosted API, "zonder garanties":
  <https://best.pr.fedservices.be/api/opendata/best/v1/belgianAddress/v2/doc/openapi.yaml>. Resources `/municipalities`,
  `/postalInfos`, `/streets`, `/addresses`, `/partOfMunicipalities`. Name search is accent- and case-insensitive (`*`
  substring, `~` fuzzy), with a `status` filter. `/streets` and `/addresses` refuse a query without a narrowing parameter
  ("Minimum Parameter condition not respected").
  - Retired municipalities are absent: `nisCode=45017` returns `total: 0`, even with `status=retired`.
  - Version 1.12.9 (release notes <https://opendata.bosa.be/download/best/bestinabox/RELEASE-NOTES.txt>).
- **BeST-in-a-Box**, a self-hostable Docker image with the same API and a database:
  <https://hub.docker.com/r/bosa/opendata-best-webapi>.
- For government users: <https://digital.belgium.be/federated-api-marketplace/rest-api/s332-general-bestservices-info/details>.
- Rules for assigning addresses:
  <https://www.ibz.rrn.fgov.be/sites/default/files/documents/nl/rijksregister/omzendbrieven/BeSt_20201104.pdf>.
- Brussels product specification (ISO 19131):
  <https://urbisdownload.datastore.brussels/BeSt/TechSpec/BeStAddress_TechSpec_EN20240601.pdf>. It says "The product is
  updated weekly", UTF-8, nl/fr/de, and "reflect the situation at the time of export".

### 1.3 Flanders: Gebouwen- en Adressenregister (Digitaal Vlaanderen)

- **REST API v2 (JSON-LD):** `https://api.basisregisters.vlaanderen.be/v2/gemeenten`, `/v2/gemeenten/{nis}`,
  `/v2/straatnamen?niscode=…`, `/v2/straatnamen/{id}`, `/v2/postinfo/{pc}`, `/v2/adressen?…`. Anonymous GETs worked. The
  **change feeds** (`/v2/feeds/straatnamen`, …) return 401 "API key verplicht". Documentation:
  <https://docs.basisregisters.vlaanderen.be/docs/api-documentation.html>; portal <https://basisregisters.vlaanderen.be/>.
- **Persistent URIs:** `https://data.vlaanderen.be/id/gemeente/{nis}`, `/id/straatnaam/{id}`, `/id/adres/{id}`,
  `/id/postinfo/{pc}`. The API's `naamruimte` has **no trailing slash**, BeST's namespace **has one**, and the 2025 merger
  CSV uses **`http://`**. All three forms name the same namespace.
- **History is kept here, unlike BeST.**
  - The API lists **327 municipalities: 285 `inGebruik` + 42 `gehistoreerd`**. The 42 are the 15 merged in 2019 plus the 27
    merged in 2025; Antwerpen kept 11002.
  - Example: `/v2/gemeenten/45017` → Kruishoutem, `gemeenteStatus: gehistoreerd`.
  - The street detail has **no successor link**.
  - The municipality detail exposes **`officieleTalen` and `faciliteitenTalen`**: Voeren `["nl"]`/`["fr"]` with the names
    Voeren/Fourons; Ronse/Renaix; Drogenbos. Leuven lists Leuven/Louvain/Löwen with official language nl only.
  - The postinfo detail has `postnamen[]` (a proper list), one `gemeente`, and `nuts3`.
- **The register has no deelgemeente object.** Postal names act as the label (postinfo 9750: Huise, Ouwegem, Zingem).
- **2025 merger difference files ("verschilbestanden")**, from
  <https://www.vlaanderen.be/digitaal-vlaanderen/onze-diensten-en-platformen/gebouwen-en-adressenregister/heradressering-bij-vrijwillige-gemeentefusies>.
  The page says "De identificatoren van gemeente, straatnaam en huisnummer/busnummer veranderen allemaal in de fuserende
  gemeenten", "Er mogen immers geen 2 dezelfde straatnamen in 1 gemeente zijn" and "De postcodes blijven wel overal
  behouden".
  - <https://assets.vlaanderen.be/raw/upload/v1734692529/repositories-prd/Verschilbestanden_Fusies_20-12-2024_wmyxyo.zip>
    (4.2 MB) contains:
    - `fusies-verschilbestand-gemeenten-20241213.csv` (OUD/NIEUW NIS code, gemeenteid URI, gemeentenaam; 28 rows);
    - `…-straatnamen-20241218.csv` and `…-straatnamen-20241218-Rijksregister.csv`;
    - `…-adressen-20241220.csv` (78.8 MB, ≈ 289k rows).
  - Streets enriched with National Register street codes:
    <https://assets.vlaanderen.be/raw/upload/v1736931847/repositories-prd/fusies-verschilbestand-straatnamen-20241218_RNverrijking_v01_milvsg.zip>
  - A simple XLSX: <https://assets.vlaanderen.be/raw/upload/v1739368450/repositories-prd/fusies2025_adressen_eenvoudig_lifd9r.xlsx>
  - Measured in the street file: **7,018 rows; 6,950 old ids → 6,932 new ids; 78 new ids absorb more than one old
    street.** Borsbeek "August Van Putlei" 3681 merges into the existing Antwerp street 244, and its National Register
    postcode changes 2150→2100. **359 streets were renamed** to resolve homonyms (Galmaarden "Processiestraat" →
    Pajottegem "Augusta Marcouxstraat"), and there are 166 National Register postcode changes.
- **The FAQ** (<https://www.vlaanderen.be/digitaal-vlaanderen/onze-diensten-en-platformen/gebouwen-en-adressenregister/faq>):
  homonyms must be resolved by the merger date; "Nieuwe homoniemtoevoegingen zijn niet toegestaan"; districts are in no
  address model.
- **Bulk download:** <https://download.vlaanderen.be/product/10145> (shapefile or GeoPackage; metadata
  <https://metadata.vlaanderen.be/srv/api/records/9c8b57a1-3c9a-4813-926e-38bb0bd387a7>). Its title says "adressen **in
  afbouw**" (being phased out); the API or BeST are the durable channels.
- **Licence on the Flemish channels:** "Modellicentie voor gratis hergebruik v1.0"
  (<https://data.vlaanderen.be/id/licentie/modellicentie-gratis-hergebruik/v1.0>), attribution **"Bron: Digitaal
  Vlaanderen"**. Inside BeST the same data is CC BY 4.0.
- The Adressenregister replaced CRAB on 2023-11-01. That date appears as a mass `versionIdentifier`.

### 1.4 Wallonia: ICAR (Inventaire Centralisé des Adresses et des Rues), SPW

- Metadata: <https://geoportail.wallonie.be/catalogue/2998bccd-dae4-49fb-b6a5-867e6c37680f.html> ("ICAR – Points
  d'adresses"). Extracted **daily**, licence **CC BY 4.0**, citation "Service public de Wallonie (SPW) – ICAR – Points
  d'adresses" with the date and <https://geodata.wallonie.be/id/2998bccd-dae4-49fb-b6a5-867e6c37680f>.
- Downloads: SHP (98 MB)
  <https://geoservices.wallonie.be/geotraitement/spwdatadownload/results/2998bccd-dae4-49fb-b6a5-867e6c37680f/icar_adpt_shp.zip>;
  CSV (50.5 MB) `…/icar_adpt_csv.zip`; ESRI REST
  <https://geoservices.wallonie.be/arcgis/rest/services/DONNEES_BASE/ICAR_ADR_PT/MapServer>; ATOM `…/atom_service.xml`.
  The layer has its own Walloon structure, not BeST or INSPIRE.
- **A merger keeps street ids.** After Bastogne + Bertogne, the 385 streets of 82039 kept their ICAR ids and got a new
  *version* pointing to 82039 (e.g. `Streetname/7742323/2`, `beginLifeSpanVersion 2024-12-02T21:02:32`). This is the
  **opposite of Flanders** (§1.14).
- Legacy streets carry the placeholder `validFrom 1969-01-01`.
- What exactly an ICAR "partie de commune" is remains open: observed at village or hamlet granularity, 2,016 objects, more
  than the 1,489 Walloon NIS6 sections [unverified].

### 1.5 Brussels: Paradigm (formerly CIRB/CIBG), UrbIS / "BeSt Brussels"

- Metadata (Geobru): <https://geobru-geonetwork.irisnet.be/geonetwork/srv/api/records/a8c9ccde-5c2b-11ed-913a-900f0cda5d5c>.
  Updated weekly; **licence stated as CC0** on this channel. Another statement names an "UrbIS license", so the Brussels
  licence statements conflict.
- Downloads: <https://urbisdownload.datastore.brussels/LastVersion/BeStBrussels_31370_XML_04000_lastversion.zip> (also
  `_GPKG_` and `_JSON_`); dataset page <https://datastore.brussels/web/data/dataset/a8c9ccde-5c2b-11ed-913a-900f0cda5d5c>;
  geocoding API <https://geoservices.irisnet.be/geocoding/swagger-ui/index.html>.
- Names are bilingual fr+nl throughout. Postal infos have no names.
- **Status-date semantics differ from Flanders.** A retired Brussels street has `validFrom` = the retirement date and no
  `validTo` ("Rue de Norvège", retired 1992-01-01). A retired Flemish street has `validFrom` = its creation and `validTo` =
  its retirement.

### 1.6 How the regional sources relate to BeST

Each Region manages its own authentic register: Flanders the Adressenregister, Wallonia ICAR, Brussels UrbIS / BeSt
Brussels. Each delivers BeST-XSD XML to BOSA. BOSA republishes the three as one bundle every week, plus CSVs and an API,
under **CC BY 4.0**, naming Digitaal Vlaanderen, Paradigm and the Service public de Wallonie (`best-ccby-license.docx`
inside the bundle). BOSA does not harmonise history: the bundle is each region's current snapshot. Richer history
(retired objects, events) lives only in the regional channels, and in practice only Flanders offers it.

### 1.7 Statbel REFNIS: every code since 1970

- Page: <https://statbel.fgov.be/en/open-data/code-refnis>. Current file
  <https://statbel.fgov.be/sites/default/files/files/opendata/REFNIS%20code/TU_COM_REFNIS-20250101.zip> (33 KB; also
  `.xlsx`). The 2019 edition is `…/TU_COM_REFNIS.zip`. Statbel's geography page also publishes REFNIS until 2018 (589
  municipalities), REFNIS_2019 (581) and REFNIS_2025 (565), and old-to-new tables for 2019 and 2025.
- **Format:** pipe-separated, **cp1252** (not UTF-8), CRLF. Columns
  `LVL_REFNIS|CD_REFNIS|CD_SUP_REFNIS|TX_REFNIS_DE|TX_REFNIS_FR|TX_REFNIS_NL|DT_VLDT_START|DT_VLDT_END`.
  `CD_SUP_REFNIS` is the parent. Some dates are Excel serial numbers (43465 = 2018-12-31).
- **A full history since 1970:** 2,782 rows. Region: 3. Province: 11 (10 current plus Brabant 20000, valid until
  31/12/1994). Arrondissement: 48 (43 current). **Municipality: 2,720 rows (565 current)**; the model pass counted 2,715
  distinct municipality codes.
- **Code structure.** Five digits: province, administrative arrondissement, then a sequence number. Codes are unique
  across levels.
  - 01000 = the kingdom. 02000, 03000 and 04000 = the Flemish, Walloon and Brussels regions.
  - Provinces `X0000`, with Vlaams-Brabant 20001 and Waals-Brabant 20002 from 1995-01-01; 20000 Brabant was valid
    1970–1994.
  - Arrondissements `XY000`. Municipalities: province digit + arrondissement digit + 3 digits.
  - Brussels has no province: arrondissement 21000's parent is the region 04000.
- **The file reproduces the official counts exactly** (computed from the validity dates): 2,607 municipalities
  (1/1/1970); 2,387 (1/1/1971); **2,359 (31/12/1976) → 596 (1/1/1977)**; 589 (1983); 581 (2019); 580 (1/12/2024);
  **565 (1/1/2025)**.
- It carries **no predecessor or successor links**, only intervals. The links come from the Statbel note for 2019
  (<https://statbel.fgov.be/sites/default/files/files/opendata/Statistische%20sectoren/20201005-Nota%20Refnis%20en%20Nuts-NL.pdf>,
  before/after tables), the Flemish page for 2025
  (<https://www.vlaanderen.be/statistiek-vlaanderen/nieuwe-nis-codes-voor-gefuseerde-gemeenten-vanaf-1-januari-2025>)
  and the verschilbestanden.
- **Reuse.** Statbel states no rule either way [unverified], but the data shows none:
  - in the 1970–2025 data, no code was reused for a *different* municipality;
  - new codes skip extinct numbers: 44082 (Zwijnaarde) was skipped in 2019, 46026–46028 in 2025;
  - the codes with two rows are the same unit renamed or re-parented: 92137 Basse-Sambre → Sambreville (1979);
    arrondissement 57000 Doornik → Doornik-Moeskroen (2019); 23000, 24000 and 25000 under new parents in 1995.
  - Statbel's rule: a municipality that changes arrondissement gets a new code.
- **Codes, names and territories move independently.** Antwerpen kept 11002 after absorbing Borsbeek (2025); Hasselt
  71022 → 71072, Deinze 44011 → 44083 and Mouscron 54007 → 57096 kept their names but changed code; arrondissement 57000
  kept its code after absorbing Mouscron.
- **Name gotchas:**
  - disambiguators: `Tielt (Tielt)`, `Beveren (Sint-Niklaas)`, `Moerbeke (Gent)`, `Longchamps (Bastenaken)`;
  - prefixes: `Provincie …`, `Arrondissement …`, `Bezirk …`;
  - the 2025 edition switched apostrophes `'` → `’` (`’s Gravenbrakel`), so string matching across editions fails;
  - the German exonym changed: `Bastnach` (82003) → `Bastogne` (82039);
  - renames keep the code: Montignies-le-Tilleul → Montigny-le-Tilleul 52048; Basse-Sambre → Sambreville 92137.
- **Languages:** REFNIS_2025 gives each municipality a language code: N for 285, F for 252, D for 9, FN/NF for the 19 in
  Brussels. Facility municipalities are not flagged there. Statbel's German names differ from both the nl and fr names for
  only six municipalities: Brüssel, Löwen, Brügge, Ypern, Lüttich, Bastnach. These are exonyms, not official names.
- **Licence:** CC BY 4.0 (<https://statbel.fgov.be/en/cc-40>). Attribution "Source: Statbel" (or "Source Statbel:
  (Directorate-General Statistics – Statistics Belgium)"), a link to the licence, and a statement of changes.

### 1.8 Sections: NIS6, statistical sectors and Walloon parts of municipality

- **NIS6** ("deelgemeenten", Statbel level 5):
  <https://statbel.fgov.be/sites/default/files/files/opendata/Statistische%20sectoren/NIS6withnamefrom01012025.xlsx>
  (127 KB).
  - 2,616 rows in 2025 (Brussels 27, Flanders 1,100, Wallonia 1,489); 2,690 in the 2019–2024 edition. Columns
    `CNIS5_2025, T_MUN_NL/FR/DE, C_NIS6, T_NIS6_NL/FR`. NIS6 names are UPPERCASE, NL/FR only.
  - It contains the owner's example **`45035Y BEVERE` under 45035 Oudenaarde**, and `45061Z OOIKE` under
    Wortegem-Petegem.
  - **Sub-codes keep the old municipality's prefix.** Kruisem: 45017Z KRUISHOUTEM, 45017C WANNEGEM-LEDE, 45017D NOKERE,
    45057A ZINGEM, 45057B OUWEGEM, 45057C HUISE. Beveren-Kruibeke-Zwijndrecht (46030): 11056A ZWIJNDRECHT and 11056B
    BURCHT, now in Oost-Vlaanderen.
  - **NIS6 codes are not stable between editions.** Between the 2019 and 2025 files, 150 codes were dropped and 76 added;
    45017A became 45017Z; "45061C MOREGEM + OOIKE*" was split into 45061Y and 45061Z. The 2025 REDEGEO reform re-coded
    sections, and Wikipedia still shows Ooike as `45061C8` (an older 7-position code). NIS6 is therefore "as of an
    edition".
- **NIS9 statistical sectors:** 20,781, of which 1,751 carry a prefix that differs from their current municipality code.
  Sectors with geometry: <https://statbel.fgov.be/en/open-data/statistical-sectors-2025> (columns `cd_sub_munty`,
  `tx_sub_munty_nl/fr`, `cd_munty_refnis`, …, NUTS 2024).
- **Walloon parts of municipality** (BeST `PartOfMunicipality`, optional in the schema and filled for Wallonia only): 2,016
  objects at village and hamlet level, with no link to their municipality except through `postalstreets` (§1.2).
- **Flanders:** no deelgemeente object in the register (§1.3).

### 1.9 Postal codes

- **bpost** (<https://www.bpost.be/nl/postcodevalidatie-tool>; FR `/fr/outil-de-validation-de-codes-postaux`). Files
  <https://www.bpost.be/sites/default/files/zipcodes/zipcodes_num_nl_2025.xls> and `zipcodes_alpha_nl_2025.xls`, and an FR
  variant `zipcodes_num_fr_2025.xls` (each 328 KB, last modified 2026-03-16, legacy BIFF8 `.xls`).
  - Columns: `Postcode | Plaatsnaam | Deelgemeente (Ja/Neen) | Hoofdgemeente | Provincie`.
  - 2,764 rows: **2,720 geographic place rows** (505 main "Neen", 2,215 sub "Ja") plus 44 special codes (612 Sinterklaas,
    1099 Brussel X, 1110 NATO).
  - 1,190 distinct codes, 1,146 of them geographic; 565 distinct main municipalities (the 2025 mergers already applied).
  - This is the owner's "Place" list: `9700 Bevere Ja OUDENAARDE`.
- **Relationships:**
  - The Belgian pass found every postcode under exactly one main municipality in bpost's list, with 513 postcodes covering
    more than one place (up to 19). The model pass read the same file as "1040 and 1050 shared with Brussels; 1804
    Cargovil with Vilvoorde/Zemst". The two readings disagree [unverified]; territory and addresses do cross (next items).
  - One municipality, many codes: Tournai has 20 codes, and 336 municipalities have one. One place, several codes:
    Antwerpen (7), Liège 4000/4020, Kallo 9120/9130, Haren and Kolmont 3700/3840.
  - **Ooike appears twice:** `9700 Ooike` (Oudenaarde) and `9790 Ooike` (Wortegem-Petegem).
  - **At address level the relation is many-to-many:** BeST has 2,661 addresses on 11 streets of the *City of Brussels*
    (21004) with postcode **1050** (Ixelles' code).
  - Place names repeat across municipalities: Beveren (Waregem 8791), Beveren (Roeselare 8800), Beveren-aan-de-IJzer
    (Alveringem 8691), Beveren-Waas (9120).
  - **Former municipality names stay valid:** 9770 Kruishoutem, 9771 Nokere and 9750 Zingem now have main municipality
    KRUISEM.
- **Mergers do not change postcodes:** "Bij de gemeentefusies veranderen geen postcodes" (Digitaal Vlaanderen), and "De
  postcodes blijven wel overal behouden" (the 2025 merger page). 2070 Zwijndrecht is now listed under OOST-VLAANDEREN.
  The 166 changes in the 2025 street file are **National Register** street postcodes (§1.3), not bpost codes.
- **BeST postalInfo (1,191) is a strict superset of bpost's geographic codes** (all 1,146, plus special codes such as
  1005–1012 and 1931/1934), under CC BY 4.0.
- **Terms:** <https://www.bpost.be/nl/disclaimer>. The content is protected by "intellectuele eigendomsrechten van bpost …
  waaronder … databankrecht", and reproduction, distribution or commercial use "is verboden, tenzij met
  voorafgaandelijke en schriftelijke toestemming van bpost". The file itself states no licence. Republishing the bpost
  list needs written permission; reading it as a cross-check does not republish it.

### 1.10 Languages, names and facility municipalities

- **Official trilingual names for every level:** Statbel REFNIS (nl/fr/de) and BeST municipality names (Wallonia: all three;
  Brussels: fr+nl; Flanders: nl, plus fr/de exonyms for 42, e.g. Antwerpen/Anvers/Antwerpen).
- **Street names:** Brussels fr+nl; Flanders nl only (official street names are Dutch even in facility municipalities);
  Wallonia fr, with fr+nl in the Walloon facility municipalities (Enghien: 203 in the sample; Mouscron;
  Comines-Warneton) and de in the German-speaking area.
- **English exonyms:** no official Belgian source. Wikidata labels (CC0) have them: Antwerp, Ghent, Bruges, Brussels.
- **27 facility municipalities** (<https://nl.wikipedia.org/wiki/Faciliteitengemeente>):
  - **12 Flemish** (nl, French facilities): Bever, Drogenbos, Herstappe, Kraainem, Linkebeek, Mesen, Ronse,
    Sint-Genesius-Rode, Spiere-Helkijn, Voeren, Wemmel, Wezembeek-Oppem;
  - **4 Walloon** (fr, Dutch facilities): Edingen/Enghien, Komen-Waasten/Comines-Warneton, Moeskroen/Mouscron,
    Vloesberg/Flobecq;
  - **2** (fr, German facilities): Malmedy, Waimes;
  - **9 German-speaking** (de, French facilities): Amel, Büllingen, Burg-Reuland, Bütgenbach, Eupen, Kelmis, Lontzen,
    Raeren, Sankt Vith.

  The Flemish API exposes this as `officieleTalen`/`faciliteitenTalen`. Brussels' 19 municipalities are officially
  bilingual.

### 1.11 Municipal history since 1961

**Timeline** (counts verified against REFNIS validity dates):

| Date | Event | Municipalities |
|---|---|---|
| 1961→1976 | "Eenheidswet" waves (1961 law). REFNIS shows 223 codes ending 31/12/1970 and 29 ending 31/12/1971 | 2,663 (1961, Wikipedia) → 2,607 (1/1/1970) → 2,387 (1/1/1971) → 2,359 (31/12/1976), all from REFNIS. Wikipedia's intermediate figures differ slightly (2,379 "by 1970"); REFNIS starts on 01/01/1970, so its 1970 figure is a floor-date snapshot |
| **1 Jan 1977** | National fusion: royal decree of 17 September 1975, ratified by the law of 30 December 1975; 1,832 codes end 31/12/1976 | **2,359 → 596** |
| 1 Jan 1983 | Antwerpen absorbs seven municipalities and keeps 11002; Comines 54002 → Comines-Warneton 54010 | **589** (308 VL, 262 WAL, 19 BXL) |
| 1 Jan 1995 | The province of Brabant (20000) splits into Vlaams-Brabant 20001 and Waals-Brabant 20002 (Brussels has no province) | – |
| **1 Jan 2019** | 7 Flemish mergers (15 → 7); the Hainaut arrondissement reform recodes 11 municipalities | **581** |
| **1/2 Dec 2024** | Bastogne 82003 + Bertogne 82005 → Bastogne **82039** | 580 |
| **1 Jan 2025** | 13 Flemish mergers (28 → 13) | **565** (300→285 VL, 262→261 WAL, 19 BXL) |

Belgium had **no municipal mergers between 1983 and 2019**. Sources: the REFNIS file;
<https://en.wikipedia.org/wiki/Fusion_of_the_Belgian_municipalities>.

**1977.** REFNIS ends 1,832 codes on 31/12/1976, but it carries no links, so the 1977 predecessor → successor list is not
in any machine-readable source the research found. The model pass expects it to be derived from REFNIS end dates, the
NIS6 parts and curated sources, failing closed on ambiguity; it would come to about 600 receiving-unit events. Examples
established in this research: Kruishoutem 45017 absorbed Nokere and Wannegem-Lede and kept its code; Ooike 45033 and
Huise 45015 were split (§1.13).

**1983** (full list):

| Old (NIS) | New (NIS) |
|---|---|
| Antwerpen 11002 + Berchem 11003, Borgerhout 11006, Deurne 11012, Ekeren 11014, Hoboken 11019, Merksem 11028, Wilrijk 11051 | **Antwerpen 11002** (code kept) |
| Comines 54002 | **Comines-Warneton 54010** |

**1 January 2019** (full list: 15 → 7 municipalities; none kept an old code; Statbel note + REFNIS):

| Old (NIS) | New (NIS) | Remarks |
|---|---|---|
| Kruishoutem 45017 + Zingem 45057 | **Kruisem 45068** | |
| Lovendegem 44036 + Waarschoot 44072 + Zomergem 44080 | **Lievegem 44085** | |
| Meeuwen-Gruitrode 72040 + Opglabbeek 71047 | **Oudsbergen 72042** | Opglabbeek moved from arrondissement Hasselt to Maaseik |
| Puurs 12030 + Sint-Amands 12034 | **Puurs-Sint-Amands 12041** | |
| Deinze 44011 + Nevele 44049 | **Deinze 44083** | new code, same name |
| Aalter 44001 + Knesselare 44029 | **Aalter 44084** | new code, same name |
| Neerpelt 72025 + Overpelt 72029 | **Pelt 72043** | |

The same day, eleven Hainaut municipalities were recoded without merging (§1.12).

**2024–2025** (full list: 581 → 565; the Statbel/Flanders page, the Flemish difference file and REFNIS):

| Old (NIS) | New (NIS) | Remarks |
|---|---|---|
| Antwerpen 11002 + Borsbeek 11007 | **Antwerpen 11002** | code kept (≈ +2% population); Borsbeek became Antwerp's tenth district |
| Galmaarden 23023 + Gooik 23024 + Herne 23032 | **Pajottegem 23106** | |
| Ruiselede 37012 + Wingene 37018 | **Wingene 37021** | new code, same name |
| Meulebeke 37007 + Tielt 37015 | **Tielt 37022** | new code, same name |
| De Pinte 44012 + Nazareth 44048 | **Nazareth-De Pinte 44086** | |
| Lochristi 44034 + Wachtebeke 44073 | **Lochristi 44087** | new code, same name |
| Lokeren 46014 + Moerbeke 44045 | **Lokeren 46029** | Moerbeke moved arrondissement Gent → Sint-Niklaas |
| Melle 44040 + Merelbeke 44043 | **Merelbeke-Melle 44088** | |
| Beveren 46003 + Kruibeke 46013 + Zwijndrecht 11056 | **Beveren-Kruibeke-Zwijndrecht 46030** | Zwijndrecht moved **province** Antwerpen → Oost-Vlaanderen |
| Bilzen 73006 + Hoeselt 73032 | **Bilzen-Hoeselt 73110** | |
| Borgloon 73009 + Tongeren 73083 | **Tongeren-Borgloon 73111** (fr Tongres-Looz) | |
| Ham 71069 + Tessenderlo 71057 | **Tessenderlo-Ham 71071** | |
| Hasselt 71022 + Kortessem 73040 | **Hasselt 71072** | new code, same name; Kortessem moved arrondissement Tongeren → Hasselt |
| Bastogne 82003 + Bertogne 82005 | **Bastogne 82039** | Walloon decree of 19 May 2023 (MB 23/06/2023), effective **2 December 2024** (installation of the council). **Statbel REFNIS uses 01/12/2024**; Wikidata uses 2024-12-02 for Bertogne |

Notes on 2025:
- **Zwijndrecht's province change is in the decree itself.** Its title changes "de grens tussen de provincies Antwerpen
  en Oost-Vlaanderen", and Art. 4 places the new municipality in Oost-Vlaanderen. It is a return: Zwijndrecht and Burcht
  had moved the other way in 1923.
- **Arrondissements:** no codes changed in 2025, but boundaries moved for Zwijndrecht, Moerbeke and Kortessem.
- Postal codes did not change ("De postcodes blijven wel overal behouden"). OSM's tracking page is a useful cross-check:
  <https://wiki.openstreetmap.org/wiki/WikiProject_Belgium/Municipality_Fusions/2025>.
- The Bastogne decree is also reported in
  <https://www.lavenir.net/regions/luxembourg/2023/05/17/officiel-les-communes-de-bastogne-et-bertogne-fusionneront-en-2024-EI5LD3TQBVF7ZBHAF4W3XXGPFA/>.

### 1.12 Recodes, renames and moves without a merger

- **2019, Hainaut arrondissement reform** (eleven municipalities, no territorial change):
  - new arrondissement 58000 La Louvière: La Louvière 55022 → 58001, Binche 56011 → 58002, Estinnes 56085 → 58003,
    Morlanwelz 56087 → 58004;
  - to Soignies: Seneffe 52063 → 55085, Manage 52043 → 55086;
  - to Ath: Enghien 55010 → 51067, Silly 55039 → 51068, Lessines 55023 → 51069;
  - Mouscron 54007 → 57096 and Comines-Warneton 54010 → 57097: arrondissement Mouscron 54000 was abolished and merged
    into Tournai-Mouscron 57000, which kept its code.
- **Renames that keep the code:** Basse-Sambre → Sambreville 92137 (1979); Montignies-le-Tilleul → Montigny-le-Tilleul
  52048; arrondissement 57000 Doornik → Doornik-Moeskroen (2019).
- **Re-parenting that keeps the code:** arrondissements 23000, 24000 and 25000 under the new provinces in 1995.
- **A province move inside a merger:** Zwijndrecht 2025 (§1.11). Its sections 11056A/B kept the Antwerp-province prefix
  while lying in Oost-Vlaanderen, so the hierarchy cannot be derived from code digits.

### 1.13 Ooike, Bevere and Huise: verified facts

**Ooike** (the owner remembered "the 1990s"):
- **Statbel REFNIS:** `4|45033|45000|Ooike|Ooike|Ooike|01/01/1970|31/12/1976`. Ooike had one code, 45033. It was a
  municipality until 31 December 1976 and ceased on **1 January 1977**, in the national fusion. Belgium had no mergers in
  the 1990s.
- **How it was split** (nl.wikipedia <https://nl.wikipedia.org/wiki/Ooike>: "Op 1 januari 1977 werd de gemeente Ooike in
  twee delen gesplitst"; fr.wikipedia <https://fr.wikipedia.org/wiki/Ooike>):
  - the **western part with the village centre** became the section Ooike of **Wortegem-Petegem** (NIS 45061; NIS6
    `45061Z OOIKE`);
  - the **eastern rural part** was added to the section **Eine** of **Oudenaarde** (NIS 45035; NIS6 `45035S EINE`). The
    dividing line follows the N459.
  - Of 6.65 km², **4.95 km² went to Wortegem-Petegem and 1.70 km² to Oudenaarde** (495 ha / 170 ha). These figures come
    from an unsourced Wikipedia footnote [unverified].
- **Traces today:**
  - bpost: `9790 Ooike → WORTEGEM-PETEGEM` and `9700 Ooike → OUDENAARDE`;
  - the Flemish postinfo names for 9700 and 9790 both contain "Ooike";
  - BeST shows 0 addresses with postcode 9700 in 45061, and 0 with 9790 in 45035: each part uses its municipality's
    postcode;
  - Statbel's sectors put 45061Z OOIKE under Wortegem-Petegem, and sector 45035A892 "OOIKE" under 45035S EINE.
- **A conflicting secondary source:** FOTW (<https://crwflags.com/fotw/flags/be-vovwp.html>) says "since 1970 … Ooike (a
  part of which had been incorporated to Oudenaarde in 1970)". REFNIS contradicts that for Ooike. REFNIS does show Elsegem
  45008, Moregem 45025, Petegem-aan-de-Schelde 45039 and Wortegem 45055 ending 31/12/1970, so FOTW probably conflates the
  earlier Wortegem-Petegem merger (inference). REFNIS lists 45061 from 01/01/1970, the file's floor date.
- Wikidata Q2643529 (Ooike) has only "section of Wortegem-Petegem" and coordinates: no NIS code, no dissolution date, no
  replaced-by.

**Bevere** (the owner's example) merged into Oudenaarde before REFNIS coverage. nl.wikipedia
(<https://nl.wikipedia.org/wiki/Bevere>) gives both 1 January 1965 and "fusie van 1964", so the exact date is
[unverified]. Bevere has no REFNIS row, but it exists as NIS6 `45035Y BEVERE` and as bpost `9700 Bevere Ja OUDENAARDE`.

**Huise** (45015) was also split in 1977: "DEEL HUISE" went under Kruishoutem and "HUISE" under Zingem. In 2019 both parts
ended up inside Kruisem, and the 2025 parts file lists only 45057C HUISE.

**Chains occur:** Nokere 45031 (ended 1976) → Kruishoutem 45017 (1977–2018) → Kruisem 45068 (2019–). From 1977 to 2018 the
municipality Kruishoutem (45017) contained the section Kruishoutem (NIS6 45017A, now 45017Z).

### 1.14 Identifiers: what is stable and what is not

**On a merger, Flanders re-identifies and Wallonia does not.**

| Region | Streets and addresses of the merged municipalities | Evidence |
|---|---|---|
| **Flanders** | historicised and **re-created with new ids**, in 2019 and in 2025; the street detail has no successor link | 45017 Kruishoutem has 212 `gehistoreerd` streets and 45068 Kruisem has 329 `inGebruik`; 37018 old Wingene has 275 `gehistoreerd` and 37021 new Wingene 371. The 2025 page: "De identificatoren van gemeente, straatnaam en huisnummer/busnummer veranderen allemaal". The 2025 difference files map old → new (§1.3) |
| **Wallonia** | **keep their ids**; a new version points to the new municipality | Bastogne 82039: 385 streets kept their ICAR ids (e.g. `Streetname/7742323/2`, `beginLifeSpanVersion 2024-12-02T21:02:32`) |
| **Brussels** | no merger since BeST exists (inference: REFNIS shows the same 19 municipalities from 1983 to 2025) | – |

- **Record time ≠ legal date.** Nevele's Kerkstraat (73243) was historised with versieId 2023-11-01, nearly five years
  after the 2019 merger.
- **Mass re-versioning happens:** Flanders 2002-11-21 (the CRAB import) and 2023-11-01 (the Adressenregister go-live).
  Addresses embed the *versions* of the objects they reference, so a version change is not a content change.
- **A NIS code is not a stable entity id:** recodes without a merger (2019), a code kept across a merger (Antwerpen 1983
  and 2025), a new code with the same name (Wingene, Tielt, Deinze, Aalter, Hasselt, Lokeren, Lochristi).
- **A NIS6 code is stable only within an edition** (§1.8).
- **Namespaces need normalising** before deriving a region: strip the scheme, `www.`, a trailing slash and the case.
  `https://data.vlaanderen.be/id/gemeente/`, `https://data.vlaanderen.be/id/gemeente` and
  `http://data.vlaanderen.be/id/gemeente` are one namespace, and Wallonia's `geodata.wallonie.be/id/…` has no scheme. The
  namespaces per region: `https://data.vlaanderen.be/id/{gemeente,straatnaam,adres,postinfo}/`,
  `https://databrussels.be/id/{municipality,streetname,address,postalinfo}`,
  `https://geodata.wallonie.be/id/{Municipality,Streetname,Address,PostalInfo,PartOfMunicipality}`.
- **Street ids collide across regions:** Brussels and Flemish street ids are both small integers, so any key needs the
  region.
- **`versionIdentifier` is opaque:** a timestamp in Flanders, an integer in Brussels and Wallonia. It is only comparable
  for equality, and it changes on mass re-versioning, so it does not belong in an id.
- **ISO 3166-2 codes exist for regions and provinces:** BE-VLG, BE-WAL, BE-BRU; BE-VAN, BE-VBR, BE-VLI, BE-VOV, BE-VWV,
  BE-WBR, BE-WHT, BE-WLG, BE-WLX, BE-WNA.
- **Streets cannot be keyed on (municipality, name).** Brussels: "Kloosterstraat" is both *Rue du Cloître* (4625) and *Rue
  du Couvent* (5216) in the City of Brussels, and "Maarschalkdreefje" names two different French streets in Uccle; neither
  has a `homonymAddition`. Flanders uses `homonymAddition` codes (Antwerp districts). Wallonia has duplicate
  part-of-municipality names.

### 1.15 Which sources carry history

| Source | Retired municipalities | Predecessor/successor | Validity dates | Sections |
|---|---|---|---|---|
| BeST full XML | ✗ | ✗ (only in the mutation XSD, not public) | status `validFrom/validTo`; lifespan attributes | Wallonia parts of municipality only (no parent link) |
| BeST API | ✗ | ✗ | yes | Wallonia parts of municipality |
| Flemish API | ✓ (42 `gehistoreerd`) | ✗ (the feeds need an API key; whether merger events carry new ids is [unverified]) | version ids | postnamen per postinfo |
| Flemish verschilbestanden | ✓ (2025) | **✓ old→new: municipality, street, address** (2025 only) | – | – |
| Statbel REFNIS | ✓ since 1970 | ✗ (intervals only) | **✓** | ✗ |
| Statbel 2019 note | – | ✓ (2019 table) | – | – |
| Statbel NIS6 | – | implicit for 2019 and 2025 (old NIS5 prefix) | as of the edition | **✓ 2,616** |
| bpost | – | ✗ | ✗ | ✓ (place names, Ja/Neen) |
| Wikidata | partial | partial: P1366/P1365 | P571/P576, not always exact | partial |

**Wikidata check** (SPARQL, 2026-10-17, <https://query.wikidata.org/>):
- Only **658** items have a 5-digit NIS code (P1567), against 2,720 REFNIS municipality rows. Of those, 34 have P576
  (dissolved), **26** have P1366 (replaced by) and 80 have P571 (inception).
- Good: Kruishoutem and Zingem have "dissolved 2018-12-31" and "replaced by Kruisem"; Kruisem has "inception 2019-01-01"
  and "replaces" both.
- Bad: **Bastogne Q34021 carries both 82003 and 82039** (the item was reused); Wingene Q735797 has only the new 37021;
  Mouscron has only 57096; Bertogne "dissolved 2024-12-02" has no replaced-by; Ooike has no NIS code.

### 1.16 Volumes

| Object | Records | Source / size |
|---|---|---|
| Regions / provinces / arrondissements | 3 / 10 (+1 historical) / 43 (+5 historical) | REFNIS (33 KB) |
| Municipalities | 565 current; 2,720 code rows since 1970 | BeST ≈ 0.3 MB XML; REFNIS |
| Sections (NIS6) | 2,616 | Statbel XLSX, 127 KB |
| Walloon parts of municipality | 2,016 | BeST, 0.7 MB XML |
| Postal codes | 1,191 | BeST, 0.4 MB XML |
| Street names | 156,055 current (+1,089 retired, 255 proposed in BeST) | BeST ≈ 140 MB XML across 3 regions (3 MB zipped) |
| Streets with ≥ 1 address | 144,062 (Flanders 80,826, Wallonia 58,112, Brussels 5,124) | model pass |
| (postcode, street[, part of municipality]) | ≈ 147k | `postalstreets-latest.zip`, 2.2 MB |
| 2025 Flemish id mappings | 28 municipalities / 7,018 streets / ≈ 289k addresses | difference ZIP, 4.2 MB |
| Flemish register, all statuses | 99,742 straatnaam objects; 4,623,695 address objects | model pass |
| **Addresses (if ever)** | **6,656,349 current; 7.17 M including retired and proposed** | ≈ 13 GB of XML a week (Brussels 1.43 GB, Wallonia > 4 GiB, Flanders ≈ 7.6 GB) or CSVs, 233 MB zipped |

- **What a typeahead needs** (country → region → province → municipality → [section/postcode] → street): the REFNIS
  hierarchy; BeST municipalities, postal infos and streets; NIS6 and/or the Walloon parts of municipality for places; and
  `postalstreets` for street ↔ postcode (and part of municipality for Wallonia). That is **≈ 310k small documents**,
  reloadable weekly in seconds (inference from the sizes).
- **The cost of house numbers:** about 20× more documents (≈ 6.7–7.2 M). Each weekly run streams ≈ 13 GB of XML, storage
  and index would run to tens of GB in RavenDB, and every week brings tens of thousands of changes (inference from the
  sizes). Brussels alone has 180k *proposed* addresses. The cheaper alternative is to validate a house number on demand
  against the BeST API or a self-hosted BeST-in-a-Box.

### 1.17 Change detection and parsing

- **Bundle:** a conditional GET (`If-None-Match` with the ETag, or `If-Modified-Since`) on `best-full-latest.zip` and
  `postalstreets-latest.zip`. With byte ranges the central directory (last 64 KB) can be read and only the small entries
  fetched. Inner files are recognisable by a pattern such as
  `^(Brussels|Flanders|Wallonia)(Municipality|PostalInfo|PartOfMunicipality|StreetName|Address)(\d{8})(L72)?\.zip$`,
  never by a fixed name.
- **Per object:** the key is the normalised (namespace, objectIdentifier); `versionIdentifier` is compared for equality
  only. Because of mass re-versioning and embedded reference versions (§1.14), a content hash (names by language, status
  and dates, parent ids without their versions) is the reliable change signal.
- **Disappearance ≠ deletion.** When a Flemish municipality merges, all its streets vanish from BeST. Their status and
  successors have to come from the Flemish API and the difference files.
- **.NET parsing facts:**
  - the bundle is a **zip of zips**, and the Walloon address XML exceeds 4 GiB, so ZIP64 is required.
    `System.IO.Compression` reads ZIP64. `ZipArchive` wants a seekable stream, so each inner zip goes to a temp file
    first, and the XML is streamed with `XmlReader`, never loaded as a DOM;
  - Brussels files start with a UTF-8 BOM; REFNIS is **cp1252**; bpost is legacy `.xls` (BIFF8); some REFNIS dates are
    Excel serial numbers;
  - street `type=hamlet` (Wallonia) is a hamlet name used in place of a street name.

### 1.18 Pitfalls

1. BeST has no retired municipalities and drops the objects of merged municipalities.
2. Flanders re-identifies streets and addresses on a merger; Wallonia does not.
3. A NIS code is **not** a stable entity id (recodes, a kept code, a new code with the same name).
4. Merged municipalities moved arrondissement or province: Zwijndrecht to Oost-Vlaanderen; Moerbeke, Kortessem and
   Opglabbeek to other arrondissements.
5. Brussels has no province level, so a hierarchy must allow region → municipality.
6. Never key a street on (municipality, name) (§1.14).
7. Postcodes cross municipal boundaries at address level (1050 in the City of Brussels). bpost's
   one-main-municipality-per-postcode view is a simplification.
8. Postal names: none for Brussels and Wallonia; Flanders packs them into one slash-joined string with odd
   capitalisation ("Petegem-Aan-De-Schelde").
9. Status-date semantics differ between Brussels and Flanders; Wallonia uses the placeholder date 1969-01-01; the
   Bastogne merger date is 1 December (Statbel) or 2 December (decree). Intervals need their source recorded.
10. REFNIS names carry disambiguators and prefixes, apostrophes changed between editions, and a German exonym changed.
11. Typeahead must filter on status (current, perhaps proposed): Brussels has 180k proposed addresses.
12. The Lambert 2008 switch on 19 October 2026 renames files and changes coordinates and CSV column names.
13. NIS6 codes change between editions; the record date of a Flemish historisation can be years after the legal date.
14. The bpost XLS is for cross-checking only, not for republication.

### 1.19 Licences

| Source | Licence | Republish via a public API? | Attribution |
|---|---|---|---|
| **BeST Address** (all objects, 3 regions) | **CC BY 4.0** (BOSA page, DCAT, `best-ccby-license.docx`, terms §6); EU **High-Value Dataset** | **Yes**, including derived data and commercial use | name the providers, link CC BY 4.0, state that the data was modified or derived, imply no endorsement (§4) |
| Flanders (API, downloads) | Modellicentie Gratis Hergebruik v1.0 | Yes | "Bron: Digitaal Vlaanderen", or the short form "bevat overheidsinformatie, verkregen onder de modellicentie voor gratis hergebruik Vlaanderen v1.0" |
| Wallonia ICAR | CC BY 4.0 | Yes | "Service public de Wallonie (SPW) – ICAR – …" + date + dataset URL |
| Brussels BeSt Brussels / UrbIS | CC0 (Geobru); an "UrbIS license" is also mentioned | Yes (via BeST, CC BY 4.0, in any case) | not required on the CC0 channel; recommended |
| Statbel REFNIS / NIS6 / sectors | CC BY 4.0 | Yes | "Source: Statbel", a licence link, and a statement of changes |
| Flemish 2025 verschilbestanden | Flemish government open data (Modellicentie assumed; [unverified]) | Probably yes | "Bron: Digitaal Vlaanderen" |
| Wikidata (exonyms) | CC0 | Yes | none required |
| bpost postcode XLS | all rights reserved (disclaimer: reproduction or commercial use only with prior written consent; database rights) | **No**, not without written consent | – |
| BeST NGI GeoPackage / WMS (geo.be) | "enkel niet-commercieel gebruik" | Avoid | – |
| NGI AdminVector (sections, boundaries) | unclear (CC BY 4.0 on one page, non-commercial on another) | Verify first | – |

CC BY 4.0 forbids *additional terms or technological measures that restrict recipients' reuse*. A bulk export published
next to a rate-limited API keeps a publisher clearly on the safe side (the Belgian pass's reading; not legal advice).

**A conflicting secondary listing.** transportdata.be lists BeST with per-region licences: Brussels under an UrbIS licence,
Flanders under the free-reuse licence, Wallonia under CC BY 4.0
(<https://www.transportdata.be/dataset/address-points-belgium-best-address>). BOSA's own page, the DCAT record and the
`best-ccby-license.docx` in the bundle state CC BY 4.0 for the consolidated bundle (§1.2). The primary statements are BOSA's,
but the Brussels wording has now conflicted in three places (CC0, "UrbIS license", CC BY 4.0) and is worth confirming.

### 1.20 Open points

- Whether the Flemish change feeds (API key required) carry successor ids for merger retirements. The 2025 difference files
  cover that need; for 2019, successors must be matched by name within the new municipality, or a 2019 difference list
  must be found.
- The exact licence wording of the Flemish difference files.
- What exactly an ICAR "partie de commune" is (§1.4).
- Whether BOSA's weekly cadence resumes normally after the 19 October Lambert 2008 migration; the current bundle is
  from 7 October.
- The exact date of Bevere's merger (1964 or 1965).
- Whether bpost's list assigns 1040, 1050 and 1804 to one main municipality or two (§1.9).
- Record counts marked "≈" for the Flemish and Walloon street XML and the postal-street CSVs are estimates from byte
  ratios; the API totals are exact.
- A VRT article (2025-10-15) reportedly says online maps still showed outdated names in the merged municipalities
  (<https://www.vrt.be/vrtnws/nl/2025/10/15/crevits-vraagt-aan-google-maps-apple-maps-om-hun-kaarten-aan-te/>; seen in a
  search result only, not fetched) [unverified].

---

## 2. Other countries

### 2.1 The overall picture

**Does a Belgian-style open register exist elsewhere? Yes, in most of the EU/EEA and in several countries beyond it.**
- The EU High-Value Datasets Implementing Regulation (EU) 2023/138 (OJ L 19, 20.1.2023, p. 43;
  <https://eur-lex.europa.eu/eli/reg_impl/2023/138/oj>) obliges member states to publish administrative units and
  addresses free of charge. Verified from the recitals (<https://www.umwelt-online.de/recht/eu/23a/23a_0138.htm>; EUR-Lex
  returned empty pages to the fetcher): free of charge, "minimal legal and technical restrictions", machine-readable, via
  APIs; bodies may be exempted for up to two years. From memory and [unverified]: it applies from **9 June 2024**;
  Art. 4 requires **CC BY 4.0 or an equivalent or less restrictive licence, CC0 allowed**; Annex §1 (geospatial) covers
  administrative units down to municipalities (boundaries, names, codes), geographical names and **addresses**
  (thoroughfare, locator, postcode, admin-unit names, position), as **bulk download and API**.
- Many registers became open because of it, or already were: the Dutch BAG, French BAN/COG, Luxembourg CACLR, Danish DAR,
  Czech RÚIAN, Polish TERYT/PRG, the Slovenian address register, Latvian VZD, Estonian ADS, Spanish INE
  Callejero/CartoCiudad, Italian ANNCSU, Austrian BEV, Finnish SYKE/Statistics Finland and 15 of 16 German Länder. Swiss
  swisstopo/BFS and Norwegian Kartverket/SSB are open outside the EU. Beyond Europe: US TIGER/Line, Australian G-NAF, NZ
  LINZ, Canada (Road Network File, SGC), Mexico INEGI, Brazil CNEFE 2022, Japan Post's copyright-free postcode file (with
  romaji) and Taiwan's postal street file with English names.

**The usual gap is postcodes.** Postal operators still own them in DE, IT, PT, IE, GR, BG, MT, CA, NZ and the US. In the
UK the postcode units are open, but their link to streets (PAF) is not.

**Identifiers are less stable than they look.** Norway renumbered whole counties in 2020 and again in 2024. ISTAT codes
change when a comune changes province (Sardinia, 1 January 2026). Portugal reissued freguesia codes in 2025. Greece
re-coded 28 municipalities without a recorded change (secondary source). Canada may reuse SGC codes after two editions.
German AGS and Austrian GKZ embed the district. History is published in machine-readable form in about 20 countries
(§2.5).

**Operational changes to plan for** (as of 2026-10-17):
- Denmark's DAWA is closed (HTTP 410), and Datafordeleren requires an API key or OAuth from 15 January 2027.
- France's `api-adresse` was decommissioned at the end of January 2026; its successor allows 50 requests/s.
- The LINZ address layer id changed (105689 → 123113) in September 2025.
- The BAG's daily mutation files are paid; only the monthly full extract is free.
- BeST moves to Lambert 2008 on 19 October 2026 (§1.2).

**Hosts that refused automated fetching during the research** (facts behind them stay [unverified]): IBGE
(www.ibge.gov.br), data.gov.au, dvv.fi, avoindata.fi, hms.is (429), fias.nalog.ru, data.gov.ro, data.gov.lt, datos.gob.mx,
transportation.gov, opendata.swiss, the ISO and UNECE pages; usgs.gov answered 504, Correos de México refused the
connection, geodata.gov.gr and download.geosrbija.rs timed out, and statcan.gc.ca was intermittent. Plan for manual
downloads or mirrors there.

### 2.2 Country matrix

Waves: **1** = street level with open postcodes · **1b** = streets, but no open postcodes or one blocker · **G** = gated
by an approval or a licence answer · **D** = a policy decision is needed first · **T2** = global fallback only (§3).

| Country | Admin + history | Postcodes open? | Streets open? | Main licence(s) | Wave |
|---|---|---|---|---|---|
| NL | CBS herindelingen | yes (BAG) | yes (BAG) | CC0; CBS CC BY 4.0 | 1 |
| FR | COG, events since 1943 | yes (La Poste) | yes (BAN, TOPO) | Licence Ouverte 2.0 | 1 |
| LU | manual (no change list) | yes (CACLR) | yes | CC0 | 1 |
| DE | Destatis since 1950 | **no** | 15 of 16 Länder | dl-de/by-2-0, dl-de/zero-2-0, CC BY 4.0 | 1b |
| CH / LI | BFS mutations API | yes (swisstopo) | yes (daily) | swisstopo OGD terms (attribution) | 1 |
| AT | Statistik Austria changes | yes (BEV) | yes (twice a year) | CC BY 4.0 | 1 |
| DK | DAR bitemporal | yes | yes | CC BY 4.0 (+ account/key) | 1 |
| NO | SSB KLASS | yes (Bring, NLOD 2.0) | yes | CC BY 4.0 | 1 |
| SE | SCB since 1974 | in Lantmäteriet addresses | gated | CC0 / CC BY 4.0 + GDPR terms | G |
| FI | kunta_1 since 1970 | yes (SYKE; Posti terms) | yes (fi/sv) | CC BY 4.0 | 1 |
| IS | dates per address | yes (HMS CSV) | yes | [unverified] | 1b |
| EE | EHAK snapshots, ADS history | yes | yes | CC0 / open (foreign-IP limit) | 1 |
| LV | `*_his` tables | yes | yes | CC BY 4.0 | 1 |
| LT | historical streets/places | **no** | yes | CC BY 4.0 | 1b |
| PL | TERYT change files | yes (PRG points) | yes | CC BY 4.0; PRG "free for any use" | 1 |
| CZ | RÚIAN validity + daily changes | yes | yes | CC BY 4.0 ("ČÚZK, rok") | 1 |
| SK | change batches | [unverified] | yes | CC0 vs "no unprocessed sharing" | G |
| SI | RPE (2024 renewal) | yes (field level [unverified]) | yes (it/hu names) | CC BY 4.0 | 1 |
| HU | annual gazetteers | free file, **no licence** | 6 cities only | none stated | T2 |
| HR | [unverified] | in INSPIRE AD [unverified] | yes (GML) | Open Licence vs INSPIRE restriction | G |
| RO | SIRUTA editions | 2016 file (OGL-ROU) | stale, big cities | OGL-ROU 1.0 | T2 |
| BG | numbered change log | **no** | **no** | NSI: no derivative works | T2 |
| RS | weekly snapshots | **no** | yes | Serbian Open Data Licence | 1b |
| UA | KATOTTH versions | Ukrposhta, no licence | Ukrposhta, no licence | CC BY 4.0 / none | G |
| RU | GAR validity dates | in GAR [unverified] | yes (GAR) | FNS Open Licence | D |
| GR | SMD02 | **no** | **no** | ELSTAT reuse (CC BY-like) | T2 |
| ES | INE since 2000 + 1842 database | yes (CartoCiudad/Callejero) | yes | CC BY 4.0 | 1 |
| IT | ISTAT/SITUAS since 1861 | **no** (CAP proprietary) | yes (ANNCSU) | CC BY 4.0 | 1b |
| PT | CAOP + INE tables | **no** (CTT) | stale (BNM 2019) | CC BY 4.0 | 1b |
| IE | boundary snapshots | **no** (Eircode) | **no** | CC BY 4.0 (Tailte Éireann) | T2 |
| GB | ONS CHD since 2009 | units yes (NI restricted) | yes (with postcode district) | OGL v3 | 1b |
| MT | — | **no** | **no** | CC BY 4.0 (councils only) | T2 |
| US | Census since 1970 | ZCTA + ZIP per street side | yes (TIGER) | public domain (credit requested) | 1 |
| CA | Interim List, SGC | FSA only | yes (RNF) | StatCan Open Licence / OGL-Canada | 1b |
| MX | AGEEML update register | SEPOMEX (no commercialisation) | yes (INEGI) | INEGI Libre Uso | G |
| BR | DTB yearly since 1940 | CEP in CNEFE 2022 | yes (CNEFE) | IBGE terms [unverified] | G |
| AU | G-NAF releases, ASGS | yes (G-NAF) | yes | G-NAF EULA (mail clause); ABS CC BY 4.0 | 1 |
| NZ | Stats NZ yearly | **no** | yes | CC BY 4.0 | 1b |
| AR / CL | — | no | AR: Georef API / CL: no | AR [unverified]; CL CC BY-SA 4.0 | G / T2 |
| JP | Japan Post change reasons, MIC | yes (copyright-free, romaji) | n/a (town areas) | none claimed; ABR PDL 1.0 | 1b |
| KR | [unverified] | in Juso | in Juso (English) | [unverified] | G |
| TW | [unverified] | [unverified] | yes (Chunghwa Post, EN) | OGDL-TW 1.0 | 1b |
| CN, HK, IN, IL, SG, TH | partly | partly (IN PIN, GeoNames) | no | various | T2 |

### 2.3 The 17 open-register countries

These are the countries where streets and an open postcode link were found: NL, FR, LU, CH, LI, AT, DK, NO, FI, EE, LV,
ES, PL, CZ, SI, US, AU. Belgium (§1) is the eighteenth.

#### Netherlands (NL)

| Aspect | Evidence |
|---|---|
| What exists | Kadaster **LVBAG Extract (BAG 2.0)** via the PDOK Atom feed <https://service.pdok.nl/lv/bag/atom/bag.xml> → <https://service.pdok.nl/kadaster/bag/atom/downloads/lvbag-extract-nl.zip> (product page <https://www.kadaster.nl/zakelijk/producten/adressen-en-gebouwen/bag-2.0-extract>): woonplaats↔gemeente table, places, postcodes, streets, addresses, buildings. GeoPackage "bag-light" <https://service.pdok.nl/kadaster/bag/atom/downloads/bag-light.gpkg> (7.82 GB, 1 Oct 2026). OGC API <https://api.pdok.nl/kadaster/bag/ogc/v2>. **PDOK Locatieserver v3.1** <https://api.pdok.nl/bzk/locatieserver/search/v3_1/> (suggest/lookup/free/reverse; rows ≤ 50, start ≤ 10,000; free, no key). PDOK Location API <https://api.pdok.nl/kadaster/location-api/v1/>. CBS "Gemeentelijke indeling op 1 januari 2026" (<https://www.cbs.nl/nl-nl/onze-diensten/methoden/classificaties/overig/gemeentelijke-indelingen-per-jaar/indeling-per-jaar/gemeentelijke-indeling-op-1-januari-2026>) and StatLine "Gebieden in Nederland 2026" (86247NED, OData, <https://www.cbs.nl/nl-nl/cijfers/detail/86247NED>). Hierarchy: 12 provinces (PDOK PV20–PV31) → **342 gemeenten** on 1 Jan 2026 (4-digit CBS code, `GM0080` in StatLine) → woonplaatsen (4-digit `woonplaatscode`, e.g. 3602 Grou) → openbare ruimten → nummeraanduidingen |
| Format, cadence | XML in a zip, 3.64 GB (8 Oct 2026). The **full extract is free monthly** (made on the 8th); daily and monthly mutation files only through paid Mijn Kadaster subscriptions (€14.50/month + €219 setup; daily €219/month) |
| Licence | PDOK Atom: **CC0 1.0**; Kadaster's product page: Public Domain Mark 1.0 plus its delivery terms; the Location API page: CC BY 4.0. Three labels for the same data; none requires attribution. CBS: CC BY 4.0 (<https://www.cbs.nl/nl-nl/over-ons/website/copyright>; "Gebieden in Nederland 2026" explicitly, <https://data.overheid.nl/dataset/72061-gebieden-in-nederland-2026>). Locatieserver: "open en gratis", no licence or rate-limit statement found [unverified fair use] |
| History | CBS herindeling pages 2016–2026 (none in 2024 or 2026); changes since 1949 in StatLine region notes; "Gebieden; overzicht vanaf 1830"; divisions and name/code changes since 1991 (<https://www.cbs.nl/nl-nl/dossier/nederland-regionaal/gemeente/herindelingen-en-grenswijzigingen>). BAG keeps "voorkomens" with validity dates (bitemporal); whether the extract includes history is [unverified] |
| Ids | CBS: a merger under a new name, or a rename, gets a **new code**; a merged municipality that keeps one old name keeps that code; codes are "nooit in zijn geheel hergebruikt". BAG 16-digit ids (prefix = the gemeentecode of origin) persist through a herindeling [unverified]; BAG itself warns against deriving the hierarchy from its ids |
| Postcodes | **Open (CC0):** the postcode is an attribute of every BAG nummeraanduiding, so postcode ↔ street ↔ woonplaats ↔ gemeente is open. PostNL's own products are commercial [unverified] |
| Notes | Latin only. BAG holds **one** official woonplaatsnaam chosen by the municipality, often Frisian in Fryslân (Grou); "Ljouwert" returns nothing (Leeuwarden is Dutch). EPSG:28992. The WFS has no standalone nummeraanduidingen |

#### France (FR)

| Aspect | Evidence |
|---|---|
| What exists | **INSEE COG 2026** <https://www.insee.fr/fr/information/8740222> (all years <https://www.insee.fr/fr/information/2560452>): `v_commune_2026`, `v_mvt_commune_2026` (events), **`v_commune_depuis_1943`** (every code/label pair since 1943 with its validity), overseas files. INSEE API Métadonnées <https://api.insee.fr/metadonnees/geo/commune/75056> answered **without an API key** (persistent URI `http://id.insee.fr/geo/commune/…`). **BAN** <https://www.data.gouv.fr/fr/datasets/base-adresse-nationale/>, files <https://adresse.data.gouv.fr/data/ban/adresses/latest/csv/> (`adresses-france.csv.gz` 938.58 MB on 16 Oct 2026; per-département files; lieux-dits beta 8 MB). Géoplateforme geocoder <https://data.geopf.fr/geocodage/search> (successor of API Adresse). geo.api.gouv.fr (communes with `codesPostaux`: Annecy 74010 → 5 postcodes). La Poste **Base officielle des codes postaux** <https://www.data.gouv.fr/fr/datasets/base-officielle-des-codes-postaux/>. DGFiP **TOPO** (replaces FANTOIR since July 2023) <https://www.data.gouv.fr/fr/datasets/662c47ee3d303760163d4fc8>, including cancelled entities. Hierarchy: régions → départements (2A/2B) → arrondissements → cantons → **34,875 communes** on 1 Jan 2026 (DGCL BIS 206; unchanged because no commune nouvelle may be created in the year before the March 2026 elections) + communes déléguées/associées + municipal arrondissements |
| Format, cadence | COG: CSV + DBF, UTF-8, ≈ 1 MB zipped, yearly (files from 1999). BAN: CSV, continuous, geocoder refreshed twice a week, ≈ 26 M addresses [unverified]. La Poste CSV ≈ 1.5 MB, irregular (updated 8 Sep 2026) |
| Licence | **Licence Ouverte / Open Licence 2.0 (Etalab)** for all of the above. INSEE credit **"Source : Insee"** + date of last update (<https://insee.fr/fr/information/2008466>). No share-alike or non-commercial clause. BAN moved from an ODbL / "licence gratuite de repartage" regime to LO on 1 Jan 2020 (<https://acteurspublics.fr/articles/les-donnees-de-la-base-dadresses-nationale-enfin-sous-licence-libre-et-gratuite/>). Géoplateforme API: 50 requests/s per IP, then HTTP 429 and a 5 s block; no key |
| History | `v_mvt_commune` (creations, mergers, renames, … with MOD codes) and `v_commune_depuis_1943` (published since COG 2021); yearly COG back to 1999. 2026: 19 renames, 40 communes déléguées/associées suppressed, Mayotte → 976R |
| Ids | INSEE code + validity period, or the id.insee.fr URI. **A commune nouvelle takes the chef-lieu's code**, which then coexists with a commune déléguée of the same code, so a bare code is not a territory id. BAN: `cle_interop` (INSEE + street code + number) and UUIDs `id_ban_commune` / `id_ban_toponyme` / `id_ban_adresse` (BAL 1.4; secondary source) |
| Postcodes | **Open** (LO 2.0), many-to-many commune ↔ postcode; BAN carries a postcode per address (field name `code_postal` [unverified]) |
| Notes | `api-adresse.data.gouv.fr` was decommissioned at the end of January 2026 (<https://adresse.data.gouv.fr/outils/api-doc/adresse>). BAN quality depends on communes publishing BAL; lieux-dits are beta; Corsica 2A/2B are not numeric; overseas collectivities (PM, BL, MF, WF, PF, NC, TF) have their own ISO 3166-1 codes but sit in COG as 975–989. BAL 1.4/1.5 has regional-language street-name columns `voie_nom_<iso639>` [unverified] (<https://aitf-sig-topo.github.io/voies-adresses/>) |

#### Luxembourg (LU)

| Aspect | Evidence |
|---|---|
| What exists | **CACLR** "Registre national des localités et des rues" (Administration du cadastre et de la topographie) <https://data.public.lu/fr/datasets/registre-national-des-localites-et-des-rues/>: `caclr.zip` (1.96 MB, <https://download.data.public.lu/resources/registre-national-des-localites-et-des-rues/20261012-023002/caclr.zip>) and `caclr.xlsx` (43 MB): official names of cantons, communes, localités, streets, building numbers, postcodes per street and building. **BD-Adresses** <https://data.public.lu/api/1/datasets/adresses-georeferencees-bd-adresses/> (CSV 28 MB, GeoJSON 81 MB, SHP 20 MB) with geocode APIs <https://apiv3.geoportail.lu/geocode/search>. Hierarchy: 12 cantons → **100 communes** (since 1 Sep 2023) → localités → streets → numbers |
| Format, cadence | ZIP/XLSX/CSV; **weekly** |
| Licence | **CC0** (portal licence field `cc-zero`); no attribution required. Terms of apiv3.geoportail.lu not found [unverified] |
| History | Mergers (<https://elections.public.lu/en/fusion-communes.html>): 2011 laws (16 → 6), Wiltz (December 2014), 1 Jan 2018 (105 → 102), 1 Sep 2023 (→ 100). **No machine-readable change list**; CACLR is a current snapshot, so history would be hand-curated (small) |
| Ids | CACLR internal ids [unverified]; commune codes change on a merger [unverified]; Eurostat LAU codes as cross-reference |
| Postcodes | **Open (CC0)** at street and building level |
| Notes | Official (French/German-form) names only; CACLR says it does **not** contain names in other languages, so there are no Luxembourgish forms. The geocoded subset is smaller than CACLR |

#### Switzerland (CH)

| Aspect | Evidence |
|---|---|
| What exists | **BFS Amtliches Gemeindeverzeichnis** <https://www.agvchapp.bfs.admin.ch/de>, REST: `snapshot` <https://www.agvchapp.bfs.admin.ch/api/communes/snapshot?date=01-01-2026> (CSV 135 KB: HistoricalCode, BfsCode, ValidFrom, ValidTo, Level, Parent, Name, ShortName…) and `mutations` <https://www.agvchapp.bfs.admin.ch/api/communes/mutations?startPeriod=01-01-2025&endPeriod=01-01-2026> (MutationNumber, MutationDate, Initial*/Terminal* codes, names, parents). swisstopo **Amtliches Ortschaftenverzeichnis mit PLZ und Perimeter** <https://www.swisstopo.admin.ch/de/amtliches-ortschaftenverzeichnis> (CH + LI). **Official directory of streets** <https://data.geo.admin.ch/api/stac/v1/collections/ch.swisstopo.amtliches-strassenverzeichnis>. **Official directory of building addresses** <https://data.geo.admin.ch/api/stac/v1/collections/ch.swisstopo.amtliches-gebaeudeadressverzeichnis>, CSV <https://data.geo.admin.ch/ch.swisstopo.amtliches-gebaeudeadressverzeichnis/amtliches-gebaeudeadressverzeichnis_ch/amtliches-gebaeudeadressverzeichnis_ch_2056.csv.zip> (columns ADR_EGAID, STR_ESID, BDG_EGID, ADR_EDID, STN_LABEL, ADR_NUMBER, BDG_CATEGORY, BDG_NAME, ZIP_LABEL, COM_FOSNR, COM_NAME, COM_CANTON, ADR_STATUS, ADR_OFFICIAL, ADR_MODIFIED, ADR_EASTING, ADR_NORTHING). Hierarchy: 26 cantons → 144 districts → **2,110 communes** on 1 Jan 2026 (counted in the snapshot API) → Ortschaften (PLZ6, ≈ 4,100) → streets → addresses |
| Format, cadence | Gemeindeverzeichnis XLSX/XML/TXT + REST; Ortschaftenverzeichnis CSV/SHP/FileGDB/INTERLIS, monthly; streets and building addresses **daily** (CSV `;`-separated, UTF-8 with BOM) |
| Licence | swisstopo **"Nutzungsbedingungen kostenlose Geodaten und Geodienste" (OGD, version 1.3.2021)** <https://www.swisstopo.admin.ch/de/nutzungsbedingungen-kostenlose-geodaten-und-geodienste>: use, redistribution, enrichment and **commercial use allowed**; **attribution mandatory in every case** ("Bundesamt für Landestopografie swisstopo", "Office fédéral de topographie swisstopo" or "©swisstopo"); excessive use of geoservices may be restricted (a contract is offered). The STAC metadata labels these collections `"license": "proprietary"`, linking to <https://www.swisstopo.admin.ch/ogd-conditions>. BFS terms could not be read (opendata.swiss 403); "Open use. Must provide the source." is usual for BFS [unverified] |
| History | About 4,000 numbered mutations (1 Jan 2025: Tschiertschen-Praden → Chur, Honau → Root, Chapelle (Glâne) → Rue); historicised since 1960 (start year [unverified]; ValidFrom dates go back to 12.09.1848). eCH-0071 |
| Ids | A continuing commune keeps its BFS number, absorbed numbers retire, new communes get new numbers; a canton change gives a new number, and reuse across states is documented as possible. Every version has a unique HistoricalCode (`GDE_GHSTNR`). Federal EGAID (address), ESID (street), EGID (building), EDID (entrance) are persistent |
| Postcodes | **Open** via swisstopo (PLZ6 perimeters fed by cantons and Swiss Post; ZIP_LABEL per address). Swiss Post's open-data portal `swisspost.opendatasoft.com` returned HTTP 404 on 2026-10-17 |
| Notes | de/fr/it/rm; names in the commune's language; bilingual names as one string ("Biel/Bienne") [unverified]. LV95 (EPSG:2056). Planned addresses (`ADR_STATUS` = planned) need filtering. **The attribution must survive into API output** |

#### Liechtenstein (LI)

| Aspect | Evidence |
|---|---|
| What exists | swisstopo building-address directory, LI item <https://data.geo.admin.ch/ch.swisstopo.amtliches-gebaeudeadressverzeichnis/amtliches-gebaeudeadressverzeichnis_li/amtliches-gebaeudeadressverzeichnis_li_2056.csv.zip> (386 KB zip, 1.75 MB CSV; 12,966 addresses including 233 planned; 944 street ids; 13 PLZ 9485–9498); the Ortschaftenverzeichnis covers LI too. **11 Gemeinden**, numbered 7001 Vaduz … 7011 Schellenberg (FOSNR) |
| Format, cadence | CSV, daily (same pipeline and schema as CH) |
| Licence | Published under swisstopo's OGD terms link; Overture credits "Liechtensteinische Landesverwaltung — OGD v2" (secondary). Attribution expected. EEA member; HVD applicability [unverified] |
| History | Not researched separately |
| Ids | FOSNR; STR_ESID; the federal address ids |
| Postcodes | Swiss PLZ system, open |
| Notes | German. Labels like "9493 Mauren FL"; `COM_CANTON` is empty |

#### Austria (AT)

| Aspect | Evidence |
|---|---|
| What exists | Statistik Austria regional classifications <https://www.statistik.at/services/tools/services/regionales/regionale-gliederungen> (Gemeindeverzeichnis 2022–2026 as ODS, "Änderungen in der Verwaltungsgliederung" ZIP, "Regionale Listen"). **BEV "Adressregister Relationale Tabelle Stichtagsdaten"** <https://www.bev.gv.at/Services/Downloads/Produktbezogene-Downloads/Unentgeltliche-Produkte/Adressregister.html>: addresses, streets, Ortschaften, Gemeinden, 1 m geocoding; Post AG postal areas adopted quarterly. Paid BEV Adresssuch-Service API (<https://www.bev.gv.at/Services/Geoinformationsdienste/Services/BEV-Adresssuch-Service.html>). Hierarchy: 9 Bundesländer → **94 Bezirke** → **2,092 Gemeinden** (page updated 13.10.2026) → Ortschaften → streets → addresses |
| Format, cadence | CSV in a zip, **twice a year** (generated at the start of Q2 and Q4, offered 14 days later) |
| Licence | Statistik Austria **CC BY 4.0** (<https://data.statistik.gv.at/>), credit "Q: STATISTIK AUSTRIA". BEV Stichtagsdaten **CC BY 4.0**; credit for derived products **"© Österreichisches Adressregister, JJJJ"** (<https://www.bev.gv.at/Services/Produkte/Adressregister/Oesterreichisches-Adressregister.html>). The API is paid: €480/year per external web application, certificate auth |
| History | The Statistik Austria change file (years covered [unverified]); the Styrian reform of 1 Jan 2015 (539 → 287 Gemeinden) [unverified] |
| Ids | BEV Adresscode (7 digits, "nicht sprechend") + 3-digit Subcode per building, persistent. GKZ (5 digits: Land + Bezirk + Gemeinde) embeds the Bezirk, so district mergers re-key municipalities [unverified] |
| Postcodes | Open through the BEV Stichtagsdaten (PLZ adopted from Post AG; PLZ per address in the free table [unverified]) |
| Notes | Only a semi-annual free snapshot; the catalogues are JS-only; format change from October 2022. Official bilingual place names (Slovenian, Croatian, Hungarian) exist; their presence in the files is [unverified] |

#### Denmark (DK)

| Aspect | Evidence |
|---|---|
| What exists | **DAR (Danmarks Adresseregister)**, Klimadatastyrelsen (KDS), served by **Datafordeleren** <https://datafordeler.dk/>: a GraphQL API (API key or OAuth) plus file downloads (`https://api.datafordeler.dk/FileDownloads/GetFile`). Entities Adresse, Husnummer, NavngivenVej, Postnummer, SupplerendeBynavn. **DAGI** (admin units). Danske Stednavne (place names). **DAWA is closed:** `GET https://api.dataforsyningen.dk/postnumre/8000` returned **410 Gone** on 2026-10-17, and its docs redirect to <https://dataforsyningen.dk/data/4924>. 5 regions (1081–1085) and 98 kommuner (3-digit), unchanged since 2007 [unverified] |
| Format, cadence | DAR: JSON in a ZIP, bitemporal, temporal or current-state totals every Saturday night, plus deltas (guide <https://datafordeler.dk/media/251atk5z/brugervejledning-transitionsguide-for-fildownloads.pdf>). DAGI: total download every Sunday (JSON, GPKG or GML) |
| Licence | "Vilkår for brug af Danmarks Adresseregister (DAR) - (16.05.2024)": "CC BY 4.0 licens gælder for frie grunddata"; "Du skal kreditere Klimadatastyrelsen på et passende sted" (<https://datafordeler.dk/vejledning/brugervilkaar/danmarks-adresseregister-dar/>); example credits "Klimadatastyrelsen", "KDS", "(CC BY 4.0) KDS". **Registration required**; "Efter 15. januar 2027 kan anvenderne udelukkende hente data med autentifikation som API-key eller OAuth". **The agency becomes "DMI, Kort og Grunddata" on 1 Dec 2026** (<https://via.ritzau.dk/pressemeddelelse/15130330/dmi-og-klimadatastyrelsen-sammenlaegges-til-ny-styrelse?lang=da>), so the credit string will change |
| History | DAR is **bitemporal** (validity and registration time); full bitemporal totals and deltas are downloadable |
| Ids | DAR UUIDs (`id_lokalId`); kommunekode, 4-digit postnummer, legacy 4-digit vejkode per municipality |
| Postcodes | **Open** (DAR Postnummer, CC BY 4.0); every address carries its postnummer |
| Notes | Danish only. By 15 Jan 2027 Datafordeleren drops REST, WMS/WMTS/WCS, the old "Filudtræk" and username/password users (earlier announced as 30 June 2026). Three agency names in about two years. Greenland and the Faroe Islands are not in DAR [unverified details] |

#### Norway (NO)

| Aspect | Evidence |
|---|---|
| What exists | **Matrikkelen – Adresse (ADR0003)**, Kartverket (metadata <https://geonorge.no/geonetworktest/srv/api/records/f7df7a18-b30f-4745-bd64-d0863812350c>; Atom <http://nedlasting.geonorge.no/geonorge/ATOM-feeds/MatrikkelenAdresse_AtomFeedCSV.xml>; download API <https://nedlasting.geonorge.no/api/capabilities/>). **Adresse REST API** <https://ws.geonorge.no/adresser/v1/>: "Det er ikke nødvendig med innlogging/autorisasjon"; page size ≤ 1000; "API-et returnerer kun de første 10 000 resultatene". **SSR** (Sentralt stedsnavnregister; each name with its language). **KLASS** (SSB) 131 (kommuner) and 104 (fylker) <https://data.ssb.no/api/klass/v1/>. **Postnummer i Norge** (Posten Bring) <https://data.norge.no/en/datasets/5e6847ba-156d-4e14-85d3-8d7f8b727523>. 15 fylker since 1 Jan 2024; **357 kommuner** (KLASS 131 `codesAt?date=2026-01-01` returns 357 + 9999 "Uoppgitt") |
| Format, cadence | Addresses: CSV, FGDB, GML, PostGIS, SOSI; national, county and municipal files (municipal ones lag a day, national ones about a week); yearly historical snapshots. Postcodes: TXT/XLSX, updated 15.9 and 1.10 each year |
| Licence | Kartverket **CC BY 4.0**, "© Kartverket" with a link; for SSR "Alle stadnamn er henta frå SSR ©Kartverket" (<https://www.kartverket.no/api-og-data/vilkar-for-bruk>). Not every Matrikkel product is open this way ("Cadastre – property" is CC BY-NC 4.0 per a search result). SSB **CC BY 4.0**, credit "Statistisk sentralbyrå eller SSB, og helst med lenke til ssb.no" (<https://www.ssb.no/diverse/lisens>); data.norge.no still lists NLOD for KLASS 131. Bring postcodes: **NLOD 2.0** per data.norge.no (bring.no states none). No API keys |
| History | KLASS versions per reform, an "Endringer" tab, correspondence tables and a `codes?from=…&includeFuture=true` query (<https://www.ssb.no/metadata/om-klass>; overview <https://www.ssb.no/metadata/alle-endringer-i-de-regionale-inndelingene>). The 2020 reform (428 → 356 kommuner) was partly reversed in 2024: Viken (30) split into 31, 32, 33; Vestfold og Telemark into 39 and 40; Troms og Finnmark into 55 and 56; "1507 Ålesund deles i 1508 Ålesund og 1580 Haram" |
| Ids | **kommunenummer are not stable**: every municipality in a changed county was renumbered in 2020 and again in 2024, so a key needs (code, validFrom). Addresses: kommunenummer + adressekode + nummer/bokstav |
| Postcodes | **Open** (Bring, NLOD 2.0), and every address carries postnummer and poststed |
| Notes | KLASS packs official Sami and Kven names into one string ("Guovdageaidnu - Kautokeino", "Lyngen - Ivgu - Yykeä", "Trondheim - Tråante"), which must be split; SSR stores each name with its language. KLASS also serves Nynorsk and English |

#### Finland (FI)

| Aspect | Evidence |
|---|---|
| What exists | **Statistics Finland classification API** <https://data.stat.fi/api/classifications/v2/classifications?searchText=kunta>: `kunta_1` every year from 1970-01-01 to 2026-01-01, `maakunta_1` from 1992, `hyvinvointialue_1` from 2021-10-13 (one language per call). **SYKE Ryhti addresses** OGC API <https://paikkatiedot.ymparisto.fi/geoserver/ryhti_building/ogc/features/v1> (`open_address`, `open_address_deleted`, `open_building`) and INSPIRE <https://paikkatiedot.ymparisto.fi/geoserver/ryhti_inspire_ad/ogc/features/v1/>; fields per the OpenAddresses mapping: address_name_fin/swe, postal_code, postal_office_fin/swe, address_key. NLS Geographic Names (≈ 800,000 names in Finnish, Swedish and three Sami languages; the OGC API needs a key and returned 401). **Posti** files <https://www.posti.fi/webpcode/>: PCF (postal codes, almost daily), **BAF (street addresses, weekly)**, POM (postcode changes, monthly). **308 kunnat** in 2026 (`kunta_1_20260101`) |
| Format, cadence | JSON (classifications); OGC API Features; zipped `.dat` (Posti) |
| Licence | Statistics Finland **CC BY 4.0**, "Source: Statistics Finland" (<https://www.stat.fi/org/lainsaadanto/copyright_en.html>). NLS CC BY 4.0, naming the licensor, dataset and time (<https://www.maanmittauslaitos.fi/en/opendata-licence-cc40>). SYKE CC BY 4.0; long-term or intensive OGC API use needs a requested identifier (<https://www.syke.fi/en/environmental-data/open-web-services/web-map-services>). **Posti: bespoke** — "Postinumeropalvelut ovat maksuttomia tiedostoja"; "Tietoja voi luovuttaa edelleen, mutta samalla on aina annettava myös voimassa oleva palvelukuvaus ja käyttöehdot" (<https://www.posti.fi/fi/asiakastuki/postinumerotiedostot>); the full terms PDF redirects, so redistribution through an API is flagged |
| History | Yearly `kunta_1` versions since 1970; POM monthly; Ryhti `open_address_deleted` |
| Ids | Municipality codes (a dissolved code is retired [unverified]); Ryhti `address_key`; permanent building id (PRT) [unverified] |
| Postcodes | Posti (bespoke terms), also carried on Ryhti addresses |
| Notes | Finnish and Swedish names for bilingual municipalities and streets. dvv.fi and avoindata.fi block automated fetches. Åland: 16 Swedish-only municipalities [unverified] |

#### Estonia (EE)

| Aspect | Evidence |
|---|---|
| What exists | **ADS** (Aadressiandmete süsteem) extracts, Maa- ja Ruumiamet <https://geoportaal.maaamet.ee/eng/spatial-data/address-data-p313.html>: CSV at <https://aks.maaruum.ee/aks/valjavotted> and on SFTP `aksfailid.maaruum.ee`; the recommended file is "extract nr 9 Addresses with components and history"; search API <https://aks.geoportaal.ee/aks/search/address>. **EHAK** <https://geoportaal.maaamet.ee/eng/spatial-data/administrative-and-settlement-division-p312.html> (SHP/TAB/DXF/DGN from <https://s3.pilw.io/rp-kemit-kataster/EHAK/>). **Postal codes** (≈ 28 MB, updated 27.08.2025) <https://geoportaal.maaamet.ee/eng/spatial-data/address-data/postal-codes-p661.html>. 15 maakonnad and 79 omavalitsused since 2017 [unverified counts] |
| Format, cadence | ADS: "CSV extracts are produced once in every 30 days", L-EST97 (EPSG:3301). EHAK: monthly, with a snapshot every 1 January |
| Licence | "ADS data is open data, so there is no license agreement"; "please refer to the Estonian Land and Spatial Development Board as the provider". AKS metadata: CC0 1.0. EHAK: unrestricted, but "the reference to the data source … and validity date must be made!" (e.g. "Administrative and settlement units, Estonian Land and Spatial Development Board 01.01.2026"). The postcode download states no licence |
| History | ADS extract 9 includes history; EHAK yearly snapshots; a page on the 2017 reform |
| Ids | ADS object and address ids (field names [unverified]); EHAK codes |
| Postcodes | **Open** through ADS ("we added postal codes to the Address Data System in 2019") |
| Notes | **"The access is limited for foreign IP addresses."** The agency was renamed and its domains are moving from maaamet.ee to maaruum.ee |

#### Latvia (LV)

| Aspect | Evidence |
|---|---|
| What exists | **VZD "Valsts adrešu reģistra atvērtie dati"** on data.gov.lv (package 6b06a7e8-dedf-4705-a47b-2a7c51177473): CSVs per level `aw_pilseta`, `aw_novads`, `aw_pagasts`, `aw_ciems`, `aw_iela`, `aw_eka`, `aw_dziv`, a historical `*_his` twin for each, Riga suburb links, centroids and SHP. Deleted-address coordinates since 22.09.2010 (yearly). Geolatvija INSPIRE (Atom). Varakļāni novads merged into Madona on 1 July 2025 |
| Format, cadence | CSV, **daily**; coordinates recently moved to LKS-2020 TM |
| Licence | data.gov.lv metadata: **CC BY 4.0**; the Geolatvija INSPIRE copies say CC0 1.0, so CC BY 4.0 is the safe reading. No prescribed text; "Valsts zemes dienests" is a reasonable credit [unverified]. No key |
| History | The `*_his` tables; every record has DAT_SAK (created), DAT_BEIG (liquidated), DAT_MOD (modified) and STATUSS (EKS, DEL or ERR) |
| Ids | KODS with its parent VKUR_CD; liquidated objects keep their codes with status DEL; ATRIB holds the ATVK code in `aw_novads` |
| Postcodes | **Open**: in `aw_eka`, ATRIB is the "Pasta indekss" |
| Notes | ATRIB means different things per table |

#### Spain (ES)

| Aspect | Evidence |
|---|---|
| What exists | INE **Relación de municipios y sus códigos** <https://www.ine.es/daco/daco42/codmun/26codmun.xlsx> (plus `diccionario26.xlsx`, `26codislas.xlsx`). INE **Modificaciones de municipios anotadas en el REL**, yearly since 2000 (e.g. <https://www.ine.es/daco/daco42/codmun/codmun26/26codmunmod.htm>). INE **Alteraciones de los municipios en los Censos de Población desde 1842** <https://www.ine.es/uc/8ugc9oOn> (methodology <https://ine.es/alteraciones/alteraciones_municipios_metodologia.pdf>). INE **Callejero del Censo Electoral** <https://www.ine.es/dyngs/DAB/es/index.htm?cid=1390> (`UP`, `VIAS`, `PSEU`, `TRAM`, `SECC`, `TiposVia.csv`; ≈ 1.5 M segments). **CartoCiudad** (IGN/CNIG) <https://datos.gob.es/es/catalogo/e00125901-spaign-cartociudad-addresses>, SHP per province <https://centrodedescargas.cnig.es/CentroDescargas/buscadorCatalogo.do?codFamilia=02122>. NGMEP, NGBE (≈ 1.2 M toponyms), BDLJE (IGN). Catastro INSPIRE AD <https://www.catastro.hacienda.gob.es/webinspire/index.html>. 17 comunidades + Ceuta and Melilla, 50 provinces, **8,132 municipios** (2026) |
| Format, cadence | Municipality list: XLSX, annual at 1 Jan (2026 edition published 4 Feb 2026). Callejero: ASCII in ZIP, **twice a year** (31 Dec and 30 Jun states) since July 2001, each with a variations file. CartoCiudad: continuous |
| Licence | **INE: CC BY 4.0** (<https://ine.es/dyngs/AYU/es/index.htm?cid=125>), verbatim "Fuente: Sitio web del INE: www.ine.es" or, processed, "Elaboración propia con datos extraídos del sitio web del INE: www.ine.es"; INE licenses only data INE itself originated. **IGN/CNIG: CC BY 4.0** (Orden FOM/2807/2015, <https://www.ign.es/resources/licencia/Condiciones_licenciaUso_IGN.pdf>) with the prescribed form «<identificador del producto> <fecha> CC-BY 4.0 <atribución de productores>» (product table <http://www.scne.es/tablaProductos.php>: "CartoCiudad Continua CC-BY 4.0 scne.es", "NGMEP Continua CC-BY 4.0 ign.es", "NGBE Continua CC-BY 4.0 ign.es", "BDLJE Continua CC-BY 4.0 ign.es", "IGR-RT 2026 CC-BY 4.0 scne.es"); **a new web service built from modified data must carry the attribution in its self-description and service metadata.** **Catastro: conflicting** — the Licencia de Acceso y Uso v1.0 (<https://www.catastro.hacienda.gob.es/webinspire/documentos/Licencia.pdf>) forbids distributing the original information, while the CODSI metadata says "CC BY 4.0 DG Catastro" (<https://www.idee.es/csw-codsi-idee/srv/api/records/ES_SDGC_AD>) |
| History | INE yearly change tables since 2000: creations (with parent municipality, REL resolution date and effective date), disappearances (with absorbing municipality), name changes and corrections; the 1842 database census by census; Callejero variations per release |
| Ids | Codes were assigned alphabetically to the municipalities of 31-12-1970 and kept since; new municipalities get codes starting with 9; extinct codes are not reused, **except** that a re-created municipality gets its old code back (INE metadata, oper=185). Code = province + 3 digits + control digit |
| Postcodes | Correos sells the official list (a datos.gob.es thread cites €831.80 [unverified], <https://datos.gob.es/eu/solicitud-de-datos/codigos-postales-de-espana>). **Open routes:** the CP on each CartoCiudad portal (CC BY 4.0, Correos named among producers) and the postal district on each Callejero segment; the CartoCiudad CP is the cleaner one |
| Notes | The REL name is the only official name: co-official only ("Girona", "Lleida", "Ourense", "València"), bilingual with a slash ("Donostia/San Sebastián"; municipality "Alacant/Alicante" against province "Alicante/Alacant"), or hyphenated ("Vitoria-Gasteiz"); 134 strings contain "/". INE puts articles after the name ("Coruña, A"). The INE footer says "CC BY-SA 4.0" but links CC BY 4.0. The Callejero layout changed in July 2025 |

#### Poland (PL)

| Aspect | Evidence |
|---|---|
| What exists | **TERYT** (GUS) full files, "urzędowy" and "adresowy" variants <https://eteryt.stat.gov.pl/eTeryt/rejestr_teryt/udostepnianie_danych/baza_teryt/uzytkownicy_indywidualni/pobieranie/pliki_pelne.aspx>; TERYT ws1 SOAP service <https://api.stat.gov.pl/Home/TerytApi> (account by email; full files, change files for a period, address verification). **PRG** (GUGiK) <https://www.geoportal.gov.pl/pl/dane/panstwowy-rejestr-granic-prg/>: address points <https://opendata.geoportal.gov.pl/prg/adresy/PunktyAdresowe/POLSKA.zip> (SHP) and <https://opendata.geoportal.gov.pl/prg/adresy/PRG-punkty_adresowe.zip> (GML), units <https://opendata.geoportal.gov.pl/prg/granice/00_jednostki_administracyjne.zip>; **8,560,617 address points and 302,793 streets/squares** at 31.03.2026 (<https://www.geoportal.gov.pl/aktualnosci/podsumowanie-prg-prng-i-kwartal-2026-r/>). 16 województwa, 380 powiat-level units, ≈ 2,477 gminy [unverified counts] |
| Format, cadence | TERYT XML/CSV [unverified], daily; PRG boundaries yearly (1 January), addresses continuously; WFS and OGC API Features |
| Licence | **TERYT: CC BY 4.0** on dane.gov.pl (dataset 747, <https://api.dane.gov.pl/1.4/datasets/747>), with the condition to state when the information was created and obtained; the eTERYT page itself shows only "Copyright © 1995-2016 GUS". **PRG:** "Dane PRG są dostępne bezpłatnie i do dowolnego wykorzystania" — permissive, but no named licence and no prescribed attribution |
| History | TERYT change files for any period, and full files as of a past date; NTS history since 27.07.2000; PRG yearly boundary archives 2005–2025 |
| Ids | TERC (7-digit WWPPGGR), SIMC (7-digit locality), ULIC SYM_UL (5-digit street *name* from the national catalogue, so a street is SIMC + SYM_UL [unverified]); reuse after mergers [unverified]; PRG UUID-style local ids |
| Postcodes | Poczta Polska sells the official PNA list (own use only per a secondary source, <https://datahub.io/logistics/postal-codes-pl/.licensing-notes>). **PRG address points carry the postcode** (kept by the gminy), giving an open postcode ↔ locality ↔ street link |
| Notes | Minority-language names (German, Kashubian, Lithuanian, Belarusian, Lemko): 1,305 localities in 67 gminy as of 22.09.2025 (<https://www.gov.pl/web/ksng/nazwy-w-jezykach-mniejszosci-w-polsce>). Two street-id systems side by side (ULIC and PRG) |

#### Czechia (CZ)

| Aspect | Evidence |
|---|---|
| What exists | **RÚIAN** in VFR (XML/GML 3.2.1), ČÚZK, from VDP <https://vdp.cuzk.cz/vdp/ruian/adresnimista>: monthly state files plus **daily change files**. RÚIAN address places as CSV (one per municipality; columns include Kód ADM, obec, MOMC, část obce, ulice, numbers, **PSČ**, coordinates, "Platí od"; <https://cuzk.gov.cz/Uvod/Produkty-a-sluzby/RUIAN/2-Poskytovani-udaju-RUIAN-ISUI-VDP/Dopady-zmeny-zakona-c-51-2020-Sb/Adresni-mista-CSV_atributy.aspx>); a hierarchy CSV; ISÚI code lists (UI_OBEC, UI_ZRUSENA_ADRM). ≈ 2.9–3 M address places [unverified, older figure]. 14 kraje, 77 okresy, 206 ORP, ≈ 6,250 obce [unverified] |
| Format, cadence | VFR monthly + daily changes; CSV monthly (built on the 1st, valid to the end of the previous month) |
| Licence | **CC BY 4.0**, free, no registration, commercial use allowed since 1 Jan 2020 (ČÚZK FAQ <https://cuzk.gov.cz/ruian/Poskytovani-udaju-ISUI-RUIAN-VDP/Vymenny-format-RUIAN-(VFR)/FAQ-casto-kladene-otazky-k-VFR/4-Je-mozne-ziskat-data-RUIAN-i-pro-komercni-ucely.aspx>; general terms <https://cuzk.gov.cz/Predpisy/Podminky-poskytovani-prostor-dat-a-sitovych-sluzeb/Podminky-poskytovani-prostorovych-dat-CUZK.aspx>). Attribution **"ČÚZK, [rok]"** (the year of the data); data travel with their metadata, and the terms must be made available |
| History | Every element has "platí od/do"; daily changes; cancelled address places listed with their cancellation date |
| Ids | RÚIAN codes are unique and system-assigned, not reused [unverified] |
| Postcodes | **Open:** PSČ per address place (CC BY 4.0). Česká pošta's free monthly CSVs (<https://www.ceskaposta.cz/ke-stazeni/zakaznicke-vystupy>) carry no licence statement |
| Notes | Czech only. The CSVs use Windows-1250 and semicolons [unverified] |

#### Slovenia (SI)

| Aspect | Evidence |
|---|---|
| What exists | **Register prostorskih enot (RPE)** and the **Register naslovov** (launched 1–3 March 2024 in IS Kataster), GURS (<https://www.gov.si/novice/2024-02-28-informacijska-prenova-registra-prostorskih-enot-in-vkljucitev-registra-naslovov/>); bulk download through JGP/eGP on Portal Prostor; WFS/WMS without registration (metadata <https://eprostor.gov.si/imps/srv/api/records/8f427833-d5bf-49fd-ae9e-1106eb63e901>). 212 občine, 58 upravne enote, ≈ 6,000 naselja [unverified] |
| Format, cadence | SHP/CSV, EPSG:3794 [unverified] |
| Licence | **CC BY 4.0** (GURS terms <https://www.e-prostor.gov.si/fileadmin/struktura/EGP/preberi_me.pdf>): "Geodetska uprava Republike Slovenije", the data type and the reference date (e.g. "Geodetska uprava Republike Slovenije, Register naslovov, 2024"). GURS may change data structures and terms at any time |
| History | Historical states with validity dates [unverified]. The 2024 renewal abolished the "prostorski okoliš" and aligned municipal borders with parcels, so identifiers changed around March 2024 |
| Ids | **EID** since March 2024; older RPE codes in legacy extracts [unverified] |
| Postcodes | Postal districts are GURS spatial units linked to house numbers [unverified at field level] |
| Notes | Bilingual names are required in **Italian** (Koper, Izola, Piran, Ankaran) and **Hungarian** (Lendava and others) areas (Pravilnik <https://www.uradni-list.si/glasilo-uradni-list-rs/vsebina/2003-01-3437/pravilnik-o-vsebini-in-nacinu-vodenja-registra-prostorskih-enot>); field names [unverified] |

#### United States (US)

| Aspect | Evidence |
|---|---|
| What exists | **TIGER/Line 2026** (Census Bureau) <https://www.census.gov/geographies/mapping-files/time-series/geo/tiger-line-file.html>, FTP <https://www2.census.gov/geo/tiger/TIGER2026/>: admin (STATE, COUNTY, COUSUB, …), places (PLACE, CONCITY), streets (ROADS, EDGES, FEATNAMES), address ranges with street name and ZIP per side (**ADDRFEAT**, ADDR, ADDRFN), ZIP approximation (**ZCTA520**). **GNIS** (USGS/BGN) staged files <https://prd-tnm.s3.amazonaws.com/?prefix=StagedProducts/GeographicNames/&delimiter=/> (the usgs.gov page returned 504). **National Address Database** (US DOT) <https://data.transportation.gov/download/fc2s-wawr/application/x-zip-compressed>, compiled 2026-09-30, partial coverage. Codes: FIPS state (2) + county (3) + subdivision or place (5) = the Census GEOID |
| Format, cadence | Shapefiles, **annual** (the 2026 release came out 2026-10-14, boundaries as of 2026-01-01); GNIS pipe-delimited UTF-8 (FederalCodes republished 2026-09-02, ≈ 7 MB) |
| Licence | TIGER/Line metadata (<https://meta.geo.census.gov/data/existing/decennial/GEO/GPMB/TIGERline/Archived_19115/tl_2023_66010_addrfeat.shp.iso.xml>): "Access constraints: None"; "not copyrighted however TIGER/Line and Census TIGER are registered trademarks … acknowledgement must be given to the U.S. Census Bureau as the source"; data.gov labels it CC0 1.0. GNIS: "Use constraints: None. Acknowledgment of the U.S. Geological Survey would be appreciated". NAD: public domain (`us-pd`). The HUD-USPS ZIP crosswalk is not cleared for republication; USPS AIS products are sold |
| History | County changes 1970–present per decade (<https://www.census.gov/programs-surveys/geography/technical-documentation/county-changes.html>; 2020s <https://census.gov/programs-surveys/geography/technical-documentation/county-changes.2020.html>) listing created, deleted, renamed and **recoded** entities; Connecticut adopted nine planning regions as county equivalents (Federal Register notice 2022-12063). BAS new incorporations, mergers, consolidations, disincorporations (<https://www.census.gov/geographies/reference-files/time-series/geo/bas/new-annex.html>). TIGER vintages yearly back to 2007: snapshots, no validity intervals |
| Ids | GEOID/FIPS are stable but **can be recoded** (Connecticut 2022). GNIS feature ids are permanent. TLID (permanent edge id) and LINEARID for streets |
| Postcodes | USPS ZIP lists are licensed. **ZCTA ≠ ZIP.** The street ↔ ZIP link is open through ADDRFEAT (Census-maintained, not authoritative; potential ranges, single-address ranges suppressed) and NAD |
| Notes | English; Spanish names in Puerto Rico [unverified detail]. Connecticut's pre-2022 counties are the predecessors of its planning regions |

#### Australia (AU)

| Aspect | Evidence |
|---|---|
| What exists | **G-NAF** (Geoscape Australia, published by the Commonwealth) <https://data.gov.au/data/dataset/19432f89-dc3a-4ef3-b943-5326ef1dbecc> (403 to fetch), product page <https://geoscape.com.au/data/g-naf/>, docs <https://docs.geoscape.com.au/en/stable/gnaf.html>: "more than 15.2 million G-NAF addresses", streets, localities, postcodes. **G-NAF Core** behind a request form <https://geoscape.com.au/request-g-naf-core/>. Geoscape **Administrative Boundaries** <https://geoscape.com.au/data/administrative-boundaries/>. **ASGS Edition 3** (ABS) <https://www.abs.gov.au/statistics/standards/australian-statistical-geography-standard-asgs-edition-3/jul2021-jun2026>; Edition 4 "will be released progressively from July 2026" |
| Format, cadence | PSV/SQL (the gnaf-loader dump is ≈ 2.0 GB, <https://github.com/minus34/gnaf-loader>), **quarterly** (Feb/May/Aug/Nov; latest Aug 2026). Localities quarterly; ASGS LGAs annually, SAL/POA every five years |
| Licence | **Open G-NAF EULA** (resource <https://data.gov.au/data/dataset/19432f89-dc3a-4ef3-b943-5326ef1dbecc/resource/09f74802-08b1-4214-a6ea-3591b2753d30/>; text could not be fetched): "must not be distributed except in accordance with the terms of the licence". The G-NAF Core EULA "is based on CC BY 4.0" and says the data **"must not be used for the generation of an address or a compilation of addresses for the sending of mail"** unless each address is verified against a secondary source; the Open EULA carries the same clause [unverified wording]. Administrative Boundaries: CC BY 4.0 (secondary). ABS: CC BY 4.0 (<https://www.abs.gov.au/website-privacy-copyright-and-disclaimer>) |
| History | Quarterly releases with change notes; retired records flagged rather than deleted [unverified]; LGAs change yearly in ASGS |
| Ids | `ADDRESS_DETAIL_PID` persistent across releases (also LOCALITY_PID, STREET_LOCALITY_PID [unverified]); ABS codes are edition-scoped; the postal-area code equals the postcode |
| Postcodes | The Australia Post list is closed [unverified], but **G-NAF carries `POSTCODE` per address**, so postcode ↔ locality ↔ street is open under the EULA. gnaf-loader warns its postcode boundaries are custom-built: "Do not treat this data as authoritative" |
| Notes | **The mail clause must pass to downstream users** of a public API, along with the attribution. Dual Aboriginal/English official names exist in several states, but G-NAF localities are English [unverified] |

### 2.4 The rest, grouped by blocker

#### Streets open, but no open postcodes or one other blocker (wave 1b)

| Country | What exists | Licence | History and ids | Postcodes | Blocker |
|---|---|---|---|---|---|
| DE | Destatis Gemeindeverzeichnis GV-ISys (monthly GV100AD fixed-width, quarterly Excel, archive to 1950) <https://www.destatis.de/DE/Themen/Laender-Regionen/Regionales/Gemeindeverzeichnis/_inhalt.html>; "Namens-, Grenz- und Schlüsseländerungen" 1950–2026; BKG **VG250** (yearly, with minority/regional-language names) and **GN250** (≈ 170,000 names); Länder open address data (ALKIS house coordinates), e.g. NRW <https://www.opengeodata.nrw.de/produkte/geobasis/lk/akt/>; ≈ 10,750 Gemeinden | Destatis: reproduction "mit Quellennachweis gestattet", credit "© Statistisches Bundesamt (Destatis), 2026". BKG: **dl-de/by-2-0** (<https://www.govdata.de/dl-de/by-2-0>), verbatim "© BKG (Jahr des letzten Datenbezugs) dl-de/by-2-0" + data-source list + change notice. Länder (per Overture's attribution page, secondary): dl-de/zero-2-0 Berlin, Hessen, NRW; dl-de/by-2-0 BW, BB, HH, RP, SL, SN, ST, TH; CC BY 4.0 HB, MV, NI, SH; **Bayern absent**. HK-DE house coordinates are **not open** (fees for third parties; BKG geocoder results may not be stored) | Change pages since 1950. AGS (8) / ARS (12) embed the Kreis, so district reforms re-key municipalities [unverified examples]. No national street or address id | **Not open nationally** (Deutsche Post) | postcodes; 16 providers in three licence families; Bavaria missing |
| IT | ISTAT **Elenco comuni italiani** <https://www.istat.it/storage/codici-unita-amministrative/Elenco-comuni-italiani.xlsx> (Italian and other-language names in separate columns); variazioni 1991–2025 and **SITUAS** <https://situas.istat.it/> (reference-date queries, suppressed and renamed comuni since 1861; REST API documented only by the unofficial "opensituas"); **ANNCSU** stradari and indirizzari <https://www.anncsu.gov.it/it/consultazione-dellarchivio/open-data/> (monthly CSV; schema <https://www.anncsu.gov.it/.allegati/metadata_indirizzario.json>). **7,894 comuni** from 21 Feb 2026; the Sardinia reform took effect 1 Jan 2026 | ISTAT **CC BY 4.0** (<https://www.istat.it/note-legali/>). ANNCSU **CC BY 4.0** per the joint technical specification implementing Reg. (EU) 2023/138 (§3.4, <https://www.agenziaentrate.gov.it/portale/documents/20143/8411843/ALLEGATO+-+SPECIFICHE+TECNICHE.pdf/2b3a983e-7c3e-e868-c9b7-c2092f9d9a12?t=1734598098978>); the download page itself states none | ISTAT code (province + sequence) **changes when a comune changes province** (Sardinia 2005, 2016, 2026; the file keeps the previous codes); the Belfiore cadastral code survives province moves; ANNCSU persistent national ids (10 and 15 digits) | **CAP proprietary** (Poste Italiane, <https://business.poste.it/business/files/1473808238165/banchedati-faq-cap.pdf>); ANNCSU has no CAP field | no open CAP; ANNCSU coordinates partial |
| GB | OS **Open Names** (≈ 44k settlements, > 870k roads with `postcode_district` and `populated_place`, > 1.6 M postcodes; `name1_lang` ∈ cym/eng/gla) <https://www.ordnancesurvey.co.uk/products/os-open-names>; OS Open UPRN (41,676,575, no address text); OS Open USRN (1,770,676, no names); **Code-Point Open** (GB only); **ONSPD/NSPL** (UK incl. NI) <https://geoportal.statistics.gov.uk>; **Code History Database** from 1 Jan 2009 <https://www.ons.gov.uk/methodology/geography/geographicalproducts/namescodesandlookups/codehistorydatabasechd> | **OGL v3.0** (OS; ONS <https://www.ons.gov.uk/methodology/geography/licences>): "Contains OS data © Crown copyright and database right [year]"; "Source: Office for National Statistics licensed under the Open Government Licence v.3.0"; postcode products add Royal Mail, UPRN products add Royal Mail and GeoPlace statements. **Northern Ireland restricted:** re-use "(not including logos or Northern Ireland data)"; BT postcodes need an LPS licence for commercial use | CHD with statutory-instrument references; ONSPD keeps terminated postcodes. GSS codes (9 characters) are new on a significant change and not reused [policy unverified]; UPRN and USRN are lifetime ids | Units open for GB; **postcode-unit → street is closed** (PAF, AddressBase); NI needs an LPS licence | no open postcode–street link; NI |
| CA | **SGC 2021** (293 census divisions, 5,161 subdivisions; 7-digit code) <https://www.statcan.gc.ca/en/subjects/standard/sgc/2021/introduction>; annual **Interim List of Changes** <https://www150.statcan.gc.ca/n1/pub/92f0009x/92f0009x2025001-eng.htm>; **Road Network File 2025** <https://open.canada.ca/data/en/dataset/9260360b-bf21-436d-bd59-ac050cdd74f6>; CGNDB (weekly, incl. Indigenous names) <https://natural-resources.canada.ca/maps-tools-publications/maps/geographical-names-canada/download-geographical-names-data>; ODA v1.0 (2021, frozen) <https://www.statcan.gc.ca/en/lode/databases/oda> | **Statistics Canada Open Licence** (<https://www.statcan.gc.ca/en/reference/licence>): "Source: Statistics Canada, name of product, reference date. Reproduced and distributed on an "as is" basis with the permission of Statistics Canada." (adapted: "Adapted from Statistics Canada, … This does not constitute an endorsement …"). **OGL – Canada 2.0** (<https://open.canada.ca/en/open-government-licence-canada>): "Contains information licensed under the Open Government Licence – Canada." | SGC every 5 years since 1976; CSD changes "several hundred times per five-year period". **An SGC code may be reused once two editions have passed** since it was last used | **Not open** (Canada Post; the Geolytica case settled in 2016 without a ruling, <https://www.cippic.ca/news/canada-post-v-geolytica-lawsuit-settled>); FSA level only | postcodes |
| NZ | LINZ **NZ Addresses** (layer 123113, weekly; old 105689 returns 404) <https://data.linz.govt.nz/services/api/v1.x/layers/123113/>; **Roads** (123110, 82,375 centrelines); **Suburbs and Localities** (113764, 6,563); **Place Names** (51681, 54,731, with `maori_name`); Stats NZ TA 2026 (123497) and other areas; 67 territorial authorities | **CC BY 4.0** (<https://www.linz.govt.nz/products-services/data/licensing-and-using-data>); the standard LINZ attribution statement is referred to but not quoted ("Sourced from the LINZ Data Service and licensed for reuse under the CC BY 4.0 licence" from memory [unverified]); accounts and keys needed for downloads/APIs [unverified] | Stats NZ yearly changes with versioned codes (TA2026_V1_00); LINZ `address_id`, `road_id` | **Not open** (NZ Post); NZ Addresses has no postcode field | postcodes |
| LT | **Adresų registras** (Registrų centras) on data.gov.lt: CSV such as <https://www.registrucentras.lt/aduomenys/?byla=adr_gatves.csv>; API <https://get.data.gov.lt/datasets/gov/rc/ar/> (including `istorinegatve`, `istorinegyvenamojivietove`); 60 savivaldybės | **CC BY 4.0** (as harvested into data.europa.eu); no attribution text found | Abolished streets and places with validity dates; `aob_kodas`, `gat_kodas` (API `_id` stability [unverified]) | **Not found** in the open data (Lietuvos paštas) | postcodes; address fields need checking |
| PT | DGT **CAOP2025** <https://www.dgterritorio.gov.pt/cartografia/cartografia-tematica/caop> (GPKG per region, WMS, OGC API); INE **BNM** <https://dados.gov.pt/datasets/base-nacional-de-moradas> (INSPIRE AD); INE correspondence tables (2013 <https://smi.ine.pt/Versao/Download/9160>; 2025 <https://smi.ine.pt/Versao/Download/10698>); 308 municípios, **3,259 freguesias** after the 2025 restoration | CAOP **CC BY 4.0, HVD**, "Informação geográfica cedida pela Direção-Geral do Território" (<https://dados.gov.pt/datasets/carta-administrativa-oficial-de-portugal-caop2025-continente>); BNM CC BY 4.0, HVD | Every CAOP version lists the laws it reflects. **Freguesia codes are not stable**: new codes in 2013 and 2025 ("apresentam novos códigos DTMNFR, criados pelo INE") | **Not open** (CTT, registration) | postcodes; BNM stale (feed dated 2019-11-11) |
| RS | RGZ **Adresni registar** <https://data.gov.rs/sr/datasets/adresni-registar/> (weekly CSV/GPKG; export host timed out) | **Serbian Open Data Licence** (`sodl`, <https://data.gov.rs/api/1/datasets/licenses/>), added to the EU licence vocabulary in June 2026 (<https://hub.data.gov.rs/en/2026/06/>); full text not fetched, described as CC BY-like [unverified] | Weekly snapshots; change history not exposed [unverified] | No open bulk file found [unverified] | postcodes; licence text |
| IS | HMS **Staðfangaskrá** <https://hmsstgsftpprodweu001.blob.core.windows.net/fasteignaskra/Stadfangaskra.csv> (38 MB, UTF-8, Last-Modified 2026-10-17; HNITNUM, SVFNR, HEINUM, POSTNR, HEITI_NF, HEITI_TGF, DAGS_INN, DAGS_LEIDR, …); Örnefni place names (Náttúrufræðistofnun, 200,000) | Örnefni **CC BY 4.0** (<https://www.natt.is/is/midlun/opin-gogn>). Staðfangaskrá: OpenAddresses records a reuse guideline with attribution to HMS; [unverified] (hms.is 429; opingogn.is redirects; fasteignaskra.is 404) | DAGS_INN/DAGS_LEIDR per address; frequent mergers (2018, 2020, 2022, 2024 twice, 2026); 61 municipalities after 16 May 2026 [unverified] | POSTNR on every address | written confirmation of HMS's reuse terms; street names in nominative **and** dative |
| JP | Japan Post **KEN_ALL** <https://www.post.japanpost.jp/service/search/zipcode/download/readme.html> (Shift_JIS CSV, ≈ 124k rows, monthly `ADD_YYMM`/`DEL_YYMM` difference files; katakana readings; a **change reason** per record: 1 merger/reorganisation, 2 residential-address display, 3 land readjustment, 4 postal district adjustment, 5 correction, 6 abolished) and **KEN_ALL_ROME** <https://www.post.japanpost.jp/zipcode/dl/roman-zip.html> (readme <https://www.post.japanpost.jp/service/search/zipcode/download/readme_ro.html>; ≈ 120k records, 1.76 MB zip updated 2025-06-18; Hepburn romaji, half-width upper case, "about once a year", truncated beyond 35 characters); Digital Agency **Address Base Registry** <https://www.digital.go.jp/policies/base_registry_address> → <https://dataset.address-br.digital.go.jp/> (the 町字 dataset became official on 1 June 2025); MIC code list <https://www.soumu.go.jp/denshijiti/code.html>; e-Stat merger dates <https://www.e-stat.go.jp/municipalities/cities/areacode> (search results only). 47 prefectures, ≈ 1,700 municipalities | Japan Post: 「郵便番号データに限っては日本郵便株式会社は著作権を主張しません。自由に配布していただいて結構です。」 — no copyright claimed, free distribution, no conditions. Address Base Registry: **PDL 1.0** (<https://www.digital.go.jp/policies/base_registry_address_tos>), credit 「出典：「アドレス・ベース・レジストリ」（デジタル庁）（URL）」, processed data labelled 「…を加工して作成」; designed CC BY 4.0-compatible [unverified] | Change reasons + monthly diffs. JIS X 0401/0402 municipality code (5 digits + check digit): a merged municipality's code retires and the new unit gets a new one; 町字ID for town areas; postcodes change with postal-district adjustments (reason 4) | **Fully open** (postcode ↔ municipality ↔ town area in one file) | no street names exist (block addressing): town-area level is complete. Long area names span rows; placeholders such as 「以下に掲載がない場合」; romaji lags the monthly file |
| TW | Chunghwa Post **路街中英對照文字檔** (street names, Chinese ↔ English) <https://data.gov.tw/dataset/152276>; 路街名替代字對應檔 (variant characters) <https://data.gov.tw/dataset/158970>; 臺灣地名資料 街道類 (yearly) <https://data.gov.tw/en/datasets/53685>; 22 county-level units [unverified] | **Open Government Data License v1.0** (<https://data.gov.tw/license>, SPDX `OGDL-Taiwan-1.0`): attribution in the provider's form is mandatory and failing it voids the grant *ab initio*; **one-way compatible with CC BY 4.0** | [unverified] | 3+3 codes by Chunghwa Post; dataset not located [unverified] | postcode file and admin-code source to confirm |

#### Gated by an approval or a licence answer

| Country | What exists | Licence | History and ids | Postcodes | What gates it |
|---|---|---|---|---|---|
| SE | SCB codes and "Kommunändringar 1974-idag" <https://www.scb.se/hitta-statistik/regional-statistik-och-kartor/regionala-indelningar/lan-och-kommuner/>; Lantmäteriet Administrativ indelning (GeoPackage, weekly); **Belägenhetsadress** (street, number, postnummer, postort, UUID, version validity; spec <https://www.lantmateriet.se/globalassets/om-lantmateriet/var-samverkan-med-andra/byggnad-adress-lagenhet/informationsspecifikation-utbyte-belagenhetsadress.pdf>); 21 län, 290 kommuner | SCB **CC0** (<https://www.scb.se/vara-tjanster/oppna-data/>). Lantmäteriet HVD terms v1.0: CC BY 4.0 with "datakälla i form av produktnamn, ©Lantmäteriet, att informationen har bearbetats … och att CC BY 4.0 gäller" (<https://www.lantmateriet.se/globalassets/geodata/geodataprodukter/anvandningsvillkor_for_vardefulla_datamangder.pdf>). **HVD with personal data (addresses, buildings), v1.1:** CC BY 4.0 **plus** GDPR controllership, processing "endast … för de ändamål som angetts i ansökan och som Lantmäteriet har godkänt", EU/EEA processing (<https://www.lantmateriet.se/globalassets/geodata/geodataprodukter/anvandningsvillkor_for_vardefulla_datamangder_pu.pdf>) | SCB since 1974; address UUIDs never change (since 2015) | Inside Belägenhetsadress, only under the personal-data terms | an approved application (a private application was refused, an organisation's approved: <https://community.openstreetmap.org/t/lantmateriet-belagenhetsadress/127427?page=4>); EU/EEA hosting |
| SK | Ministry of Interior **Register adries** (Act 125/2015, <https://static.slov-lex.sk/static/SK/ZZ/2015/125/20240401.html>): XML batch + change batches; resource ids <https://wiki.openstreetmap.org/wiki/Sk:MinvSKAddress>; "Adresy" points (<https://rpi.gov.sk/en/metadata/e94d090f-464c-4b98-a000-6cb9d130d2c0>) | CC0 per the OSM community (secondary) vs "prohibition of sharing in unprocessed form" (<https://launify.com/en/data>) — conflicting | Change batches; RA ids used by OSM as `ref:minvskaddress` (persistence [unverified]) | PSČ source [unverified] (<https://www.posta.sk/psc> loads via JS) | licence wording |
| HR | DGU **Adrese INSPIRE** GML (84.4 MB, updated 2026-10-11) <https://geoportal.dgu.hr/services/atom/INSPIRE_Addresses_(AD).zip>; Upravne jedinice INSPIRE <https://geoportal.dgu.hr/services/atom/au/xml> | data.gov.hr: **Otvorena dozvola** (<https://data.gov.hr/id/licence/otvorena-dozvola-rh>; source + date of last update, mark changes; "Contains public sector information licensed under the Open Data Licence"); the ATOM feeds carry an INSPIRE Art. 13(1)(e) access restriction | RPJ matični brojevi [unverified] | PostalDescriptor in INSPIRE AD [unverified] | licence conflict; GML only |
| UA | **KATOTTH**: 30 versions 24.03.2023–07.07.2026, latest <https://mininfra.gov.ua/storage/app/sites/1/uploaded-files/kodifikator-07-07.xlsx> (31,749 rows); Ukrposhta <https://www.ukrposhta.ua/files/shares/out/postindex.zip> (29,673 rows **with Latin names**) and <https://www.ukrposhta.ua/files/shares/out/houses.zip> (streets and house numbers, Cyrillic) | KATOTTH **CC BY 4.0** (ministry site footer); Ukrposhta files state no licence | No machine-readable change list (diff the versions); 2020 reform, 2016 decommunization and 2022–2024 derussification renames; the data.gov.ua copy is stale (2021) | Ukrposhta files, terms unclear | Ukrposhta licence; occupied territories; renaming churn |
| BR | IBGE **DTB** yearly since 1940 <https://geoftp.ibge.gov.br/organizacao_do_territorio/estrutura_territorial/divisao_territorial/> (5,571 municipal units in 2025, with new/extinct district lists); Localidades API <https://servicodados.ibge.gov.br/api/v1/localidades/>; **CNEFE 2022** <https://ftp.ibge.gov.br/Cadastro_Nacional_de_Enderecos_para_Fins_Estatisticos/Censo_Demografico_2022/> (≈ 3.7 GB, street + CEP) | **Not verified** (www.ibge.gov.br 403 to every fetch); "Fonte: IBGE" from memory [unverified] | Yearly DTB change lists; 7-digit municipal codes stable [unverified non-reuse] | CEP in CNEFE 2022 (frozen snapshot); Correios DNE paid [unverified] | IBGE terms |
| MX | INEGI **AGEEML** <https://www.inegi.org.mx/app/ageeml/> (catalogue, update register, equivalence tables, "bajas"); web service <https://gaia.inegi.org.mx/wscatgeo/v2/> (incl. `/asentamientos/`, `/vialidades/`); SEPOMEX postcode catalogue (download page refused; datos.gob.mx 403) | INEGI **Libre Uso** (<https://www.inegi.org.mx/inegi/terminos.html>): commercial use allowed, "Fuente: INEGI, nombre del producto…", keep metadata, disclose transformations. **SEPOMEX:** "se proporciona en forma gratuita, no estando permitida su comercialización, total o parcial" (quoted by <https://postalcodes-mexico.readthedocs.io/en/stable/readme.html>); a claimed "Libre Uso MX" republication is [unverified] | Update register per cut; removed localities stay as "bajas" | SEPOMEX, self-contradictory licence | SEPOMEX non-commercial clause |
| AR | **Georef AR API** <https://apis.datos.gob.ar/georef/api/> (docs <https://datosgobar.github.io/georef-ar-api/>; provincias … **calles**, direcciones; bulk download) | Data licence **not verified**; datos.gob.ar default CC BY 4.0 [unverified] | INDEC codes [unverified] | Correos CPA not open [unverified] | licence |
| KR | Ministry of the Interior and Safety road-name address service (juso.go.kr, business.juso.go.kr): full and change address databases including an **English address database**, and a keyed search API [unverified: the download pages could not be fetched]; municipal datasets on data.go.kr carry English road-name fields citing juso.go.kr; the basic-district map only on application (data.go.kr 15050416; search results) | [unverified]: data.go.kr uses the KOGL (공공누리) types; the Juso DB terms were not read | Road-name change-history fields; legal-dong codes (10 digits) with abolition dates [unverified] | Inside the Juso DB | licence and access terms |

#### A policy decision first

| Country | What exists | Licence | Issue |
|---|---|---|---|
| RU | FNS **GAR** (successor of FIAS/KLADR) <https://www.nalog.gov.ru/opendata/7707329152-fias/>: weekly XML ZIP (e.g. <https://fias.nalog.ru/opendata/7707329152-fias/data-16102026-structure-20191024.zip>), structure <https://data.nalog.ru/opendata/7707329152-fias/structure-12032021.zip>; two hierarchies (administrative and municipal) | FNS **"Открытая лицензия"** (<https://data.nalog.ru/files/tipovye_usloviya_od.pdf>): perpetual, free, non-exclusive, commercial use; do not distort the content, the source or the time of last update | **fias.nalog.ru refused connections** from the research environment; GAR presents **Crimea, Sevastopol and occupied Ukrainian oblasts as Russian** (89 subjects by Russian law, 83 internationally recognised); sanctions review [unverified] |

#### Global fallback only (tier 2)

| Country | What exists | Licence | Why only tier 2 |
|---|---|---|---|
| HU | KSH **Helységnévtár** (annual, back to 1873) <https://www.ksh.hu/apps/hntr.main?p_lang=HU>; Magyar Posta workbook <https://www.posta.hu/static/internet/download/Iranyitoszam-Internet_uj.xlsx> (11,160 rows; streets for six cities only); KSH minority-language names <https://www.ksh.hu/apps/hntr.nemzetisegi?p_lang=HU> | No formal open licence ("© Központi Statisztikai Hivatal"; the postal file states none) | KCR address register closed; no open streets |
| RO | INS **SIRUTA**; Poșta Română 2016 postcode file <https://data.gov.ro/dataset/coduri-postale-romania>; RENNS view-only | Postcodes **OGL-ROU-1.0** (<https://data.gov.ro/pages/licence>); SIRUTA [unverified] | Streets only from a stale 2016 file; data.gov.ro unreachable |
| BG | NSI **EKATTE** (Excel archive <https://www.nsi.bg/nrnm/ekatte/zip/download?files_type=excel>; 5,260 settlements; `Ekat_ver.txt` with 1,635 numbered changes up to 21.07.2020; official Latin transliteration column) | NSI licence v2.0: commercial use with attribution, but **"Нямате право да разпространявате производни и сборни произведения"** (no derivative or compiled works) | Licence forbids a compiled API; no open streets or postcodes; the JSON export held one record |
| GR | ELSTAT **SKA01** register and **SMD02** changes <https://www.statistics.gr/en/statistics/-/publication/SKA01/->; 333 municipalities | ELSTAT reuse policy (<https://www.statistics.gr/documents/20181/1412250/Copyright_Reuse_Policy_EN.pdf>): free, source indicated, modifications stated — CC BY-like | Codes **not stable** (28 municipalities re-coded 2020→2021 with no SMD02 entry, secondary); no open addresses or postcodes |
| IE | Tailte Éireann boundaries 2024 and townlands (ArcGIS Hub); **Logainm** bilingual placenames API (key) <https://docs.gaois.ie/en/data/logainm/v1.0/api> | Tailte Éireann **CC BY 4.0**, "© Tailte Éireann"; Logainm ambiguous (CC BY 4.0 on data.gov.ie, no caching terms); **Eircode internal use only** (<https://www.eircode.ie/legal>) | No open street list; no open postcodes |
| MT | Address Registrar local councils API <https://address.gov.mt/wp-json/gov/v1/local-councils> (68 councils); Planning Authority boundaries (INSPIRE WFS) | **CC BY 4.0** (Malta Data Portal records) | Streets and postcodes not downloadable yet |
| CL | INE Chile Códigos Únicos Territoriales | INE **CC BY-SA 4.0** (<https://www.ine.gob.cl/terminos-de-uso-y-licencia-de-datos-abiertos>) — share-alike | Share-alike; no open streets or postcodes |
| CN | GB/T 2260 codes (6 digits, Ministry of Civil Affairs) and NBS 12-digit codes [unverified] | No open licence known; geodata publication is regulated [unverified] | No open postcode or street data; GeoNames carries only "the first digits of codes ending in 00" for China; ICU pinyin is wrong for polyphones (§5.3) |
| HK | **Address Lookup Service** <https://data.gov.hk/en-data/dataset/hk-dpo-als_01-als> (XML/JSON lookup, Chinese and English; dictionary <https://www.als.ogcio.gov.hk/docs/Data_Dictionary_for_ALS_EN.pdf>); 18 districts; **no postal codes** | DATA.GOV.HK terms (<https://data.gov.hk/en/terms-and-conditions>) allow commercial and non-commercial reuse (search results; full page not read) | A lookup service, not a bulk file; a bulk route (Lands Department/CSDI) is [unverified]. English names are official, not romanizations |
| IN | India Post **All India Pincode Directory** <https://www.data.gov.in/resource/all-india-pincode-directory-till-last-month> (monthly CSV of post offices) and web service <https://data.gov.in/catalog/all-india-pincode-directory-through-webservice>; Local Government Directory codes [unverified] | **GODL – India** (secondary sources; text not read) | PIN/post-office level only; no national street register |
| IL | data.gov.il **israel-streets-synom** (Hebrew; city and street codes, official vs synonym; last updated early 2024); 4-digit locality codes; the Academy of the Hebrew Language's settlement-name list in four spellings (<https://old.hebrew-academy.org.il/wp-content/uploads/רשימת-שמות-היישובים-יולי-2022.pdf>) | data.gov.il terms [unverified] | Streets are Hebrew-only, and machine transliteration of unvocalized Hebrew is useless (§5.3) |
| SG | data.gov.sg under the **Singapore Open Data Licence v1.0** (<https://data.gov.sg/open-data-licence>) | Commercial use and sub-licensing allowed; notice "Contains information from {name of dataset} accessed on {date} … made available under the terms of the Singapore Open Data Licence version 1.0". **OneMap** terms (<https://www.onemap.gov.sg/legal/termsofuse.html>, clause 3(b)(i)) forbid storing or redistributing SLA data | Whether a postcode or street list exists on data.gov.sg is [unverified] |
| TH | All [unverified]: 76 provinces + Bangkok, ≈ 878 amphoe, ≈ 7,255 tambon; 5-digit postcodes per district (Thailand Post); DOPA admin lists with RTGS English names on data.go.th | Licence to verify | Admin units and postcodes at best; no streets |

The research verdicts for HK, IN, IL, SG and TH read "Partial" (admin units, postcodes or names exist), but none gives an
open street register under a confirmed licence, so the matrix places them in tier 2.

### 2.5 History published machine-readably, and codes that are not ids

**Change events are published in machine-readable form for:** FR (`v_mvt_commune`, `v_commune_depuis_1943`), CH (BFS
mutations API), NL (CBS herindelingen), DE (Destatis yearly changes since 1950), AT (Statistik Austria change file), IT
(ISTAT variazioni since 1991, SITUAS since 1861), ES (INE REL changes since 2000, census alterations since 1842), NO
(KLASS versions and correspondences), FI (yearly `kunta_1` since 1970), SE (SCB changes since 1974), US (county changes
since 1970, BAS lists), CA (the Interim List), GB (ONS Code History Database since 2009), BR (DTB yearly since 1940), MX
(AGEEML update register), DK (DAR bitemporal data), LV (`*_his` tables), LT (historical streets and places), EE (ADS
extract 9), PL (TERYT change files for any period), CZ (RÚIAN "platí od/do" plus daily changes), UA (dated KATOTTH
versions, to be diffed), GR (ELSTAT SMD02) and JP (Japan Post change reasons). Belgium publishes intervals (REFNIS) and
per-reform tables (§1.15). Luxembourg publishes no change list (§2.3).

**A bare official code is not a permanent id.** NO renumbered whole counties in 2020 and 2024; ISTAT codes change on a
province move (Sardinia 2026); PT reissued freguesia codes in 2025; CA may reuse SGC codes after two editions; DE AGS and
AT GKZ embed the district; US Connecticut moved to planning regions in 2022; GR re-coded 28 municipalities between the
2020 and 2021 registers (secondary report); SI identifiers changed with the March 2024 register renewal; BE recoded eleven
municipalities in 2019 (§1.12).

**Persistent ids that registers do provide:** BFS HistoricalCode, INSEE `id.insee.fr` URIs, BAG ids, DAR UUIDs, Swiss
EGAID/ESID, G-NAF PIDs, UPRN/USRN, ANNCSU progressivi, LINZ `address_id`/`road_id`, Lantmäteriet UUIDs, VZD KODS,
Registrų centras `aob_kodas`/`gat_kodas`, RÚIAN codes, the KATOTTH object part.

The world-sources pass read this as: key on `{country}:{scheme}:{code}@{validFrom}` or on the source's persistent id, model
units as versions linked by merge/split/rename/recode events, and pin every import with a manifest (source URL, release
date, checksum, licence version, attribution string), so that the same manifest always yields the same ids and
documents. §6 holds the evidence the model pass weighed against that.

---

## 3. Global sources and licences

### 3.1 Summary

| Source | Covers | Licence | Share-alike? | History of units | Romanized names | Role |
|---|---|---|---|---|---|---|
| GeoNames dump | places, admin1–4, alternate names, postcodes | CC BY 4.0 | no | names only (`isHistoric`, `from`/`to`); daily modification/delete files | unlabelled variants + `asciiname` | tier-2 places and postcodes |
| Wikidata | admin units, places, codes, labels in ≈ 300 languages | CC0 | no | yes (P571/P576, P1365/P1366, qualified P131) | `en` labels + transliteration properties | tier-2 backbone, cross-walk, multilingual labels and exonyms |
| NGA GNS | foreign geographic names (world minus the US) | no restrictions (citation requested) | no | variant and historic names | **BGN/PCGN standard romanizations** + native script | best open source of romanized names |
| UN/LOCODE | ≈ 100k+ trade locations | CC BY 4.0 (portal footer) | no | status/date columns | Roman-alphabet names with and without diacritics [unverified] | romanized town names, subdivision codes |
| Unicode CLDR | names of countries, languages, scripts, M49 regions in hundreds of locales; transforms | Unicode License v3 | no | — | transliteration rules (BGN, UNGEGN …) | authoritative localized country/language/script names |
| IANA Language Subtag Registry | every ISO 639, ISO 15924, ISO 3166-1 α-2 and M49 code as a subtag | **CC0** | no | deprecations + preferred values | — | language, script and region codes |
| ISO 3166 / Debian iso-codes | country and subdivision codes | ISO: codes free to use; iso-codes LGPL-2.1+ | (LGPL) | iso-codes keeps ISO 3166-3 withdrawn codes | — | codes as facts; names from CLDR/Wikidata |
| ISO 639-3 (SIL) | language codes | SIL terms (**no means to redistribute**) | — | retirements file | — | do not redistribute; use IANA |
| Who's On First | admin units, localities, postcodes | CC0 structure + **per-source licences** | depends on source | yes (`wof:supersedes` / `wof:superseded_by`) | `name:*` per language | cross-walk, second opinion |
| OpenStreetMap | everything, including streets | ODbL 1.0 | **yes** (derivative database) | current state, plus a full-history planet | `name:xx-Latn`, `int_name` | keep out of the core (§3.9) |
| Overture Maps divisions | admin units | **ODbL** (OSM-derived) | **yes** | — | from OSM | keep out of the core |
| OpenAddresses | addresses | per source; many share-alike | some | — | — | per-source cherry-picking only |
| Natural Earth | admin-0/1 boundaries, populated places | public domain | no | — | many languages [unverified] | coarse boundaries/centroids |
| geoBoundaries | ADM0–ADM5 boundaries | CC BY 4.0 | no | yearly releases | — | boundaries if ever needed |
| Google address metadata | address formats, postcode regexes, sub-region names incl. Latin | **no licence stated** | ? | — | `sub_lnames` | reference only |

### 3.2 Wikidata

- **Licence:** "All structured data from the main, Property, Lexeme, and EntitySchema namespaces is available under the
  Creative Commons CC0 License" (<https://www.wikidata.org/wiki/Wikidata:Copyright>). No attribution is required.
- **History properties** (labels confirmed through the Wikidata API): P571 *inception*, P576 *dissolved, abolished or
  demolished*, P1365 *replaces*, P1366 *replaced by*, P131 *located in the administrative territorial entity* with the
  qualifiers P580 *start time* / P582 *end time*, P150 *contains the administrative territorial entity*.
- **Codes and cross-walks:** P297/P298/P299 (ISO 3166-1 α-2/α-3/numeric), P300 (ISO 3166-2), P605 (NUTS), P782 (LAU), P281
  (postal code), P1566 (GeoNames ID), P6766 (Who's On First ID), P402 (OSM relation ID), plus one property per national code
  system (INSEE, CBS, AGS, ISTAT, TERYT, NIS P1567 …), which join it to the national registers.
- **Names:** labels in ≈ 300 languages; P1448 *official name*, P1705 *native label*, P2561 *name*. Romanization properties:
  **P2440** *transliteration or transcription* (generic qualifier), **P2125** revised Hepburn, **P1814** name in kana,
  **P2001** Revised Romanization (Korean), **P1942** McCune–Reischauer, **P1721** Hanyu Pinyin, **P5412** Belarusian 2007
  system, **P5825** ISO 15919, **P9311** Jyutping. Coverage on place items is sparse; in practice the `en` (and other
  Latin-script) labels are the romanization, with no system declared.
- **Size and access:** the full JSON dump is ≈ 127 GB gzipped (a mid-2024 mirror listing). Per-country SPARQL extracts or
  filtered dumps with a pinned dump date keep imports reproducible.
- **Quality:** crowd-edited, so validation against a national register matters where one exists. The Belgian check (§1.15)
  found 658 of 2,720 municipality rows with a NIS code, one item reused across a recode (Bastogne Q34021 carries 82003 and
  82039), and Ooike with no NIS code. Good for exonyms (Antwerp, Ghent, Bruges, Brussels), not for history.

### 3.3 GeoNames

- **What:** the dump at <https://download.geonames.org/export/dump/>: `allCountries.zip` (403 MB on 2026-10-17),
  `alternateNamesV2.zip` (195 MB), `cities500/1000/5000/15000.zip`, `admin1CodesASCII.txt`, `admin2Codes.txt`,
  `adminCode5.zip`, `hierarchy.zip` (parent-child, type `ADM`), `countryInfo.txt`, `featureCodes.txt`, plus **daily**
  `modifications-<date>.txt`, `deletes-<date>.txt` and the alternate-name equivalents (readme
  <https://download.geonames.org/export/dump/readme.txt>).
- **Licence:** "This work is licensed under a Creative Commons Attribution 4.0 License"; data "as is"; no prescribed credit
  line.
- **Structure:** `name`, `asciiname`, `alternatenames`, feature class/code, `admin1`–`admin4` codes. Admin1 codes are
  **mostly FIPS 10-4, with ISO codes only for US, CH, BE and ME**, so a mapping to ISO 3166-2 is needed (Wikidata P300 +
  P1566 provides it).
- **Alternate names (V2):** `alternateNameId, geonameid, isolanguage, alternate name, isPreferredName, isShortName,
  isColloquial, isHistoric, from, to`. `isolanguage` is "iso 639 language code 2- or 3-characters, optionally followed by a
  hyphen and a countrycode", plus pseudo-codes `post`, `iata`, `icao`, `faac`, `link`, `wkdt` (Wikidata id), `abbr`,
  `fr_1793`; the web UI also defines `piny` and `phon`. **Romanizations are not tagged by system:** on the Ulaanbaatar record
  (2028462) the romanized variants (Ulaanbaator, Ulan Bator, Urga …) carry no language code, and historic names carry
  periods (库伦, historic 1778–1924).
- **History:** names only (`isHistoric`, `from`/`to`) and deletions; no merger/split model. geonameIds are stable ("will
  never change"), but features get deleted or merged, and reuse of deleted ids is undocumented.
- **Postal codes** (<https://download.geonames.org/export/zip/readme.txt>): CC BY 4.0, but GB carries "Contains Royal Mail
  data Royal Mail copyright and database right 2022". Partial by design: CA, NL and UK have only the first part in
  `allCountries.zip` (full codes in `CA_full.csv.zip`, `GB_full.csv.zip`); **Chile, Ireland and Malta are truncated "for
  copyright reasons"**; China only codes ending in 00; Argentina the first 5 positions; Brazil only major (-000) codes.
- **The free web services** (api.geonames.org) are not a production backend; the dump is.

### 3.4 NGA GEOnet Names Server (GNS)

<https://geonames.nga.mil/>: "no licensing requirements or restrictions" on GNS data. NGA asks for a citation that the
toponymic information is based on the Geographic Names Database, containing names approved by the U.S. Board on Geographic
Names and maintained by NGA. It holds BGN-approved **standard spellings of foreign names** plus variant, historical and
**non-Roman-script** spellings, as monthly country files and a ≈ 371 MB world file [unverified, secondary]. It excludes the
US (covered by USGS GNIS). **It is the strongest open source of systematic romanized names (BGN/PCGN) for non-Latin-script
countries.**

### 3.5 UN/LOCODE

<https://unlocode.unece.org/>, footer: "All UN/CEFACT standards are free to use under CC By 4.0 license." Official releases
twice a year (2025-1 listed as current in search results) with continuous pre-releases. Location names in the Roman
alphabet with a "without diacritics" variant and ISO 3166-2 subdivision codes [column details unverified]. Useful for
romanized names of trade-relevant towns and as a cross-walk; it is not a gazetteer.

### 3.6 Unicode CLDR

- **Licence:** UNICODE LICENSE V3 (<https://www.unicode.org/license.txt>): use, copy, modify, publish, distribute and sell
  "without restriction", provided the copyright and permission notice appears with all copies **or** in associated
  documentation (an attribution page satisfies this).
- **What to take:** localized display names of territories (ISO 3166-1 + M49 regions), languages and scripts in hundreds of
  locales; territory containment (M49 groupings); transliteration transforms; the BCP 47 transform keywords (§5.4).
- **What not to take:** subdivision names. Since CLDR 32 they are imported from Wikidata; CLDR 41 slated them for
  deprecation, and CLDR 42 marks most as "provisional … derived from Wikidata, and are not further curated", except English
  (<https://cldr.unicode.org/downloads/cldr-42>, …/cldr-46, …/cldr-47). Wikidata is the direct source.

### 3.7 Code lists: IANA, ISO 3166, ISO 639, M49

- **IANA Language Subtag Registry** (<https://www.iana.org/assignments/language-subtag-registry>): every ISO 639 language
  code (as language/extlang subtags), ISO 15924 scripts, ISO 3166-1 α-2 and UN M49 region subtags, with deprecations and
  preferred values. IANA protocol registries "may be freely used by any party for any purpose", with rights under **CC0
  1.0** (joint statement of 10 November 2021, <https://www.iana.org/help/licensing-terms>). The clean source for language,
  script and region codes, with CLDR for their localized names.
- **ISO 3166:** the ISO site refused the fetch (HTTP 403). ISO's long-standing public position (a 2003 statement and a 2019
  Central Secretariat comment, quoted in search results) is that the codes may be used free of charge in commercial
  applications, while the standard and data products are sold. Open mirrors: Debian **iso-codes** (ISO 3166-1/-2/-3, 639,
  4217, 15924 with gettext translations; **LGPL-2.1-or-later**; <https://salsa.debian.org/iso-codes-team/iso-codes>),
  Wikidata (P297–P300, CC0), CLDR (names), the IANA registry (α-2 region subtags). Treating codes as facts, taking country
  names from CLDR and subdivisions from Wikidata (cross-checked with iso-codes) means no LGPL file is redistributed.
- **ISO 3166-1 reuse:** alpha-2 codes are reserved for at least 50 years after deletion, and AI, GE and SK were reassigned in
  the past; ISO 3166-3 holds the four-letter codes of withdrawn names.
- **ISO 639-3 (SIL)** (<https://iso639-3.sil.org/code_tables/download_tables>): commercial use allowed with attribution to
  iso639-3.sil.org and unmodified identifiers, provided **"the product, system, or device does not provide a means to
  redistribute the code set"**. A public API that lists every code is exactly such a means: a red flag. The IANA registry
  holds the same codes under CC0.
- **UN M49:** no licence statement found on unstats.un.org; CLDR (containment and localized region names) and the IANA
  registry (region subtags) cover it under clean licences.

### 3.8 Who's On First, Natural Earth, geoBoundaries, Google address metadata

- **Who's On First:** the structure is "made available under a Creative Commons Zero designation"; the data is "both an
  original work and a modification of existing open data", and "some sources require attribution, some do not"; "Crediting
  Who's On First is recommended and linking back to the License is required" (suggested credit "Data from Who's On First.
  [License](https://whosonfirst.org/docs/licenses/)"). Sources include CC BY 2.5/3.0/4.0, CC0, OGL variants and MPL 2.0, and
  three show no licence link (<https://whosonfirst.org/docs/licenses/>,
  <https://github.com/whosonfirst-data/whosonfirst-data/blob/master/LICENSE.md>). Per-record source tracking must be
  honoured, so it serves as a concordance and sanity check, not a primary source. History properties in §6.1.
- **Natural Earth** (<https://www.naturalearthdata.com/about/terms-of-use/>): "All versions of Natural Earth raster + vector
  map data … are in the public domain"; "No permission is needed … Crediting the authors is unnecessary" (suggested "Made
  with Natural Earth."). Admin-0/admin-1 and populated places only.
- **geoBoundaries** (<https://www.geoboundaries.org/>): "provided under the CC BY 4.0 license"; "Our license requires an
  acknowledgement in any products you produce".
- **Google address metadata** (libaddressinput; <https://github.com/google/libaddressinput/wiki/AddressValidationMetadata>):
  per-country address format (`fmt`), required fields (`require`), postcode regex (`zip`), examples (`zipex`), sub-region
  keys and **Latin names (`sub_lnames`)**. The data is "publicly available" but **no licence is stated** (a 2016 W3C thread
  noted the same); the library code is Apache-2.0. Reference only until a licence is confirmed.

### 3.9 EU-level sources

- **Eurostat LAU lists** (<https://ec.europa.eu/eurostat/web/nuts/local-administrative-units>): yearly LAU → NUTS 3
  correspondence tables 2010–2026 (2021–2026 cover EU-27 + EFTA + available candidates; 2020 includes the UK). **No LAU
  change or succession tables** were found, so year-to-year changes must be diffed. Licence: Eurostat's default **CC BY 4.0**
  (<https://ec.europa.eu/eurostat/help/copyright-notice>), credit "Source: Eurostat, [title], [link], [access date]"; the
  1961–2011 historical population tables on the same page are non-commercial only.
- **GISCO boundaries** (LAU 2011–2024, NUTS, countries;
  <https://ec.europa.eu/eurostat/web/gisco/geodata/statistical-units/local-administrative-units>, API
  <https://gisco-services.ec.europa.eu/distribution/v2>). Terms
  (<https://ec.europa.eu/eurostat/web/gisco/geodata/statistical-units>): non-exclusive, non-transferable, **not for
  commercial purposes** (commercial use needs a EuroGeographics licence); credit "© EuroGeographics for the administrative
  boundaries". The code lists are usable; the **geometries cannot be redistributed** through a public API that commercial
  users consume.
- **INSPIRE AD/AU:** <https://inspire-geoportal.ec.europa.eu/> now 301-redirects to data.europa.eu, which has an HVD filter
  (<https://data.europa.eu/data/datasets?is_hvd=true&locale=en>) and an HVD dashboard
  (<https://data.europa.eu/en/hvd-dashboard>; loads dynamically, no figures readable). INSPIRE downloads are mostly GML via
  Atom/WFS [unverified]; a fallback only where no national CSV exists. INSPIRE ids (`inspireId` = namespace + localId) "will
  not change"; §6.1 covers its time model.
- **Overture's attribution page** (<https://docs.overturemaps.org/attribution>) lists the official address source and
  licence per country and German Land: a useful secondary inventory, not a licence source.

### 3.10 What to keep out of a public API, and why

| Source | Problem | Evidence |
|---|---|---|
| **OpenStreetMap** | ODbL 1.0 share-alike. Importing OSM street names into a database and serving them through a public API is Publicly Using a **Derivative Database**: each response is an insubstantial extract, but the backing database is not, so the OSM-derived part (or a way to recreate it) must be offered under ODbL, and OSM credited | Attribution guidelines (adopted 2021-06-25) <https://osmfoundation.org/wiki/Licence/Attribution_Guidelines>: "© OpenStreetMap contributors", link <https://www.openstreetmap.org/copyright>; "Geocoders that use OpenStreetMap data must credit OpenStreetMap". Substantial guideline (2014-06-06): < 100 features one-off is insubstantial; repeated or systematic extraction aggregates (<https://osmfoundation.org/wiki/Licence/Community_Guidelines/Substantial_-_Guideline>). Geocoding guideline (2017-08-24): results are not declared Produced Works; "systematically reverse engineering the whole or a substantial part of the OSM database through Geocoding would result in creation of a Derivative Database"; "Publicly Using the Geocoder will trigger share-alike obligations on the Derivative Database" (<https://osmfoundation.org/wiki/Licence/Community_Guidelines/Geocoding_-_Guideline>). Collective Database guideline (2016-06-17): OSM and non-OSM data stay separate only when "the data used for a particular data type is either all OSM or all non-OSM within the same regional cut"; merging or de-duplicating is not covered (<https://osmfoundation.org/wiki/Licence/Community_Guidelines/Collective_Database_Guideline_Guideline>) |
| **Overture divisions, transportation, base** | ODbL, inherited from OSM | <https://docs.overturemaps.org/attribution/>: divisions "© OpenStreetMap contributors. Available under the Open Database License." |
| **Share-alike OpenAddresses sources; INE Chile** | share-alike | "many sources require attribution, and many others have a share-alike clause" (<https://github.com/openaddresses/openaddresses>); INE Chile CC BY-SA 4.0 (§2.4) |
| **SIL ISO 639-3 tables** | "does not provide a means to redistribute the code set" | §3.7; the IANA registry carries the same codes under CC0 |
| **Bulgaria NSI EKATTE** | "no derivative and compiled works" | §2.4; Bulgarian units can come from Wikidata unless NSI grants written permission |
| **GISCO boundary geometries** | not for commercial purposes | §3.9; Kartverket's CC BY-NC cadastre products likewise |
| **Proprietary postal and address products** | sold or internal-use licences | Royal Mail PAF, AddressBase, Eircode ECAF/ECAD, Canada Post/PCCF, USPS AIS, the HUD crosswalk, Correos (ES), Poste Italiane CAP, CTT (PT), Deutsche Post, BKG HK-DE (geocoder results may not be stored), Australia Post, NZ Post, **bpost** (§1.9), and the SEPOMEX catalogue (its page forbids commercialisation) |
| **Unlicensed or contradictory files** | usable only after written clarification | Magyar Posta and KSH (HU), Ukrposhta files (UA), the Slovak register's "no sharing in unprocessed form" note, the Croatian INSPIRE access restriction, Catastro (ES), HMS Staðfangaskrá (IS), the Flemish difference files' exact wording (BE) |
| **Google address metadata** | no licence stated | §3.8 |
| **OneMap (SG)** | terms forbid storage and redistribution | §2.4 |

If OSM is ever used, the guidelines above allow it as a **separate collection of whole countries per data type**, never
merged with or de-duplicated against official data of the same country, with a published ODbL dump of that collection and
the credit "© OpenStreetMap contributors, ODbL". A join by key from OSM streets to non-OSM municipality ids counts as a
reference that must follow the same rules. OSM's romanized names are tagged `name:ja-Latn`, `name:ko-Latn`,
`name:zh-Latn-pinyin` (also `-tongyong`, `-wadegile`) and `int_name` (<https://wiki.openstreetmap.org/wiki/Multilingual_names>).

### 3.11 A licence-safe combination (the research passes' reading)

**Allowed in a core database** (no share-alike, commercial reuse allowed):
- **Public domain / CC0:** Wikidata, the IANA registry, Natural Earth, US federal data (TIGER/Line, GNIS, NAD), NGA GNS, BAG
  (NL), CACLR (LU), SCB (SE), Estonian ADS/AKS, Japan Post postcode data (copyright not claimed), Brussels UrbIS (CC0
  channel).
- **CC BY 4.0, CC BY-compatible government licences and permissive notices:** GeoNames, UN/LOCODE, CLDR (Unicode License
  v3); Licence Ouverte 2.0 (FR), dl-de/by-2-0 and dl-de/zero-2-0 (DE), the swisstopo OGD terms (CH/LI), NLOD 2.0 (NO); OGL
  v3 (UK), OGL – Canada 2.0, the Statistics Canada Open Licence, INEGI Libre Uso (MX); OGDL-Taiwan 1.0, PDL 1.0 (JP),
  Singapore ODL 1.0, GODL-India; Modellicentie gratis hergebruik (Flanders); and the plain CC BY 4.0 registers: BeST,
  Statbel, ICAR, CBS, Statistik Austria/BEV, DAR, Kartverket/SSB, NLS/SYKE/Statistics Finland, VZD, Registrų centras,
  ISTAT/ANNCSU, INE/IGN, CAOP/BNM, Tailte Éireann, ABS, LINZ/Stats NZ, TERYT, RÚIAN, GURS.

**Allowed, with obligations to pass on or approvals to obtain:** G-NAF (AU) mail clause; Lantmäteriet addresses (SE)
approval, GDPR and EU/EEA processing; Datafordeleren (DK) account and key; Posti (FI) terms travel with the data; swisstopo
(CH/LI) attribution in every case; IGN/CNIG (ES) attribution in service metadata; Estonia's foreign-IP limit; HMS (IS)
terms in writing; Northern Ireland postcodes in ONSPD/NSPL need an LPS licence (or stripping BT postcodes). §4 lists the
attribution side of each.

**Tiering the world-sources pass proposed:** tier 0 = global reference lists for every country (IANA codes, CLDR names,
Wikidata P297–P300); tier 1 = a per-country adapter over the national register, importing admin units with change events,
then localities, postcodes, streets, and optionally address points where a street → postcode → locality link has to be
derived (FR, NL, CH, DK, NO, LV, EE, AU …); tier 2 = Wikidata for the admin hierarchy and history, GeoNames for places and
postcodes (respecting its truncations and the GB Royal Mail notice), NGA GNS, UN/LOCODE and Wikidata for romanized names,
and **no streets**; an optional tier 2b of OSM streets for selected whole countries only by explicit decision.

---

## 4. Attribution requirements

### 4.1 Obligations that travel with the data

Most of the licences above need a credit, a licence link and a "modified" notice wherever the data is shown. Some go
further and reach into every API response, the service's own metadata, or the terms passed to API users:

| Source | Obligation | Evidence |
|---|---|---|
| **swisstopo (CH, LI)** | attribution "**in every case**"; a web page alone does not carry it to API consumers | §2.3 CH |
| **IGN/CNIG (ES)** | a new web service built from modified data must carry the attribution formula in its **self-description (GetCapabilities "o similar") and service metadata**, and visibly next to the data | §2.3 ES |
| **CC BY 4.0** (BeST, Statbel, ICAR, GeoNames, UN/LOCODE, most registers) | credit, licence link, indication of changes; no implied endorsement; **no additional terms or technological measures that restrict recipients' reuse** (§2(a)(5)(B)) | §1.19, §3.11 |
| **Licence Ouverte 2.0 (FR)** | source (at least the licensor's name) **and the date of last update**, satisfiable by a URL | §2.3 FR |
| **dl-de/by-2-0 (DE)** | "© BKG (Jahr des letzten Datenbezugs) dl-de/by-2-0" + the data-source list + a change notice; on web pages, links on "BKG" and "dl-de/by-2-0" | §2.4 DE |
| **ČÚZK (CZ)** | "ČÚZK, [rok]"; data distributed **together with their metadata**; the terms made available, e.g. by a link | §2.3 CZ |
| **INEGI (MX)** | keep the metadata unaltered; tell end users about transformations; credit "Fuente: INEGI, nombre del producto…" "cuando técnicamente sea posible" | §2.4 MX |
| **OGDL-Taiwan 1.0** | attribution in the provider's form; **failing it voids the grant ab initio** | §2.4 TW |
| **G-NAF (AU)** | the mail clause **must pass to downstream users** (API terms), with the prescribed attribution | §2.3 AU |
| **Lantmäteriet (SE), if approved** | product name, ©Lantmäteriet, "bearbetad" if processed, CC BY 4.0 (may sit in documentation or metadata when it cannot sit next to the data); purpose and GDPR conditions pass on | §2.4 SE |
| **Posti (FI)** | the current service description and terms must accompany any redistribution | §2.3 FI |
| **ELSTAT (GR)** | state clearly that the data were modified and that ELSTAT bears no responsibility for the result | §2.4 GR |
| **Statbel (BE)** | "Source: Statbel", a licence link, **and a statement of changes** | §1.7 |
| **Unicode License v3 (CLDR)** | the copyright and permission notice with all copies or in associated documentation; the licence text reproduced on the attribution page | §3.6 |
| **NGA GNS** | NGA's citation (exact wording from <https://geonames.nga.mil/>) | §3.4 |
| **geoBoundaries** | "an acknowledgement in any products you produce" | §3.8 |
| **Statistics Canada** | the prescribed "Source:" or "Adapted from" line, with the non-endorsement sentence | §2.4 CA |
| **Singapore ODL 1.0** | a conspicuous notice naming the dataset and access date | §2.4 SG |
| **OSM (only if ever used)** | "© OpenStreetMap contributors", ODbL; a geocoder and the applications that include it must credit OSM | §3.10 |

Most of these licences also forbid implying endorsement (INE, StatCan, INSEE, SODL, PDL, BeST). The world-sources pass and
the Belgian pass read the obligations into three mechanics: a `sourceId` (with licence link and source version) on every
stored record; a compact `attribution` array in every API response (e.g. `["© swisstopo", "Source: Insee"]`); and a
`/sources` (or `/attribution`) page listing every source with its licence, credit line, version and a "modified" notice,
with the attribution block also in the API metadata and documentation. API terms may set rate limits and fair use, but no
"no redistribution" clause, and must ask downstream users to keep the attributions and pass on the G-NAF clause.

### 4.2 Draft credit lines

Quoted strings are the publishers' own; *italic* lines are the researchers' formulation where no wording is prescribed.
The year and version need re-checking on every import.

**Belgium**
- BeST Address: *"Digitaal Vlaanderen (Vlaams Gewest), Paradigm (Brussels Hoofdstedelijk Gewest), Service public de Wallonie
  (Waals Gewest), via FOD BOSA BeST Address (https://opendata.bosa.be/)"*, with a link to CC BY 4.0, a statement that the
  data was modified or derived, and no implied endorsement. The providers are those named in `best-ccby-license.docx`.
- Flanders (API, difference files): "Bron: Digitaal Vlaanderen", or "bevat overheidsinformatie, verkregen onder de
  modellicentie voor gratis hergebruik Vlaanderen v1.0".
- Wallonia: "Service public de Wallonie (SPW) – ICAR – Points d'adresses", the date, and
  <https://geodata.wallonie.be/id/2998bccd-dae4-49fb-b6a5-867e6c37680f>.
- Statbel: "Source: Statbel" (or "Source Statbel: (Directorate-General Statistics – Statistics Belgium)"), a link to
  <https://statbel.fgov.be/en/cc-40>, and a statement of changes.
- Brussels (CC0 channel) and Wikidata: not required; a courtesy line is recommended.

**Global**
- *Contains data from GeoNames (https://www.geonames.org), licensed under CC BY 4.0, modified.* For GB postcodes: "Contains
  Royal Mail data Royal Mail copyright and database right 2022."
- *Contains data from Wikidata (https://www.wikidata.org), CC0 1.0.*
- *Includes Unicode CLDR data, Copyright © 1991-2026 Unicode, Inc., under the Unicode License v3*, with the licence text
  (<https://www.unicode.org/license.txt>) reproduced on the page.
- *Language, script and region codes: IANA Language Subtag Registry (CC0 1.0).*
- NGA GNS: NGA's citation, crediting the Geographic Names Database with names approved by the U.S. Board on Geographic Names
  and maintained by the National Geospatial-Intelligence Agency.
- *UN/LOCODE © United Nations Economic Commission for Europe, CC BY 4.0.*
- Eurostat (if LAU tables are used): "Source: Eurostat, [title], [link], [access date]".
- Optional: "Made with Natural Earth."

**Per country**

| Country | Credit lines |
|---|---|
| NL | *Kadaster, Basisregistratie Adressen en Gebouwen (BAG), CC0* (courtesy) · *Bron: CBS, Gebieden in Nederland 2026, CC BY 4.0* |
| FR | "Source : Insee" + date of last update (COG) · *Base Adresse Nationale, La Poste (Base officielle des codes postaux), DGFiP (TOPO) — Licence Ouverte 2.0, mise à jour <date>* |
| LU | *Administration du cadastre et de la topographie, CACLR (CC0)* (courtesy) |
| DE | "© Statistisches Bundesamt (Destatis), 2026" · "© BKG (2026) dl-de/by-2-0" + the BKG source list URL (<https://sgx.geodatenzentrum.de/web_public/gdz/datenquellen/datenquellen_vg_nuts.pdf>) + a change notice · one line per Land dataset per its dl-de or CC BY licence |
| CH / LI | "© swisstopo" (or "Bundesamt für Landestopografie swisstopo") · *Source: BFS, Amtliches Gemeindeverzeichnis* |
| AT | "© Österreichisches Adressregister, 2026" · "Q: STATISTIK AUSTRIA" |
| DK | "(CC BY 4.0) KDS" → "DMI, Kort og Grunddata" from 1 Dec 2026 |
| NO | "© Kartverket" · *Statistisk sentralbyrå (SSB), CC BY 4.0* · *Postnummer: Posten Bring AS, NLOD 2.0* |
| SE | *Belägenhetsadress, ©Lantmäteriet, bearbetad, CC BY 4.0* (if approved) · *SCB (CC0)* |
| FI | "Source: Statistics Finland" · "contains data from the National Land Survey of Finland <dataset> <MM/YYYY>" · *SYKE, CC BY 4.0* · Posti per its terms |
| EE | *Estonian Land and Spatial Development Board (address data)* · "Administrative and settlement units, Estonian Land and Spatial Development Board 01.01.2026" |
| LV / LT | *Valsts zemes dienests, Valsts adrešu reģistrs (CC BY 4.0)* · *VĮ Registrų centras, Adresų registras (CC BY 4.0)* |
| ES | "Elaboración propia con datos extraídos del sitio web del INE: www.ine.es" · "Obra derivada de CartoCiudad <fecha> CC-BY 4.0 scne.es", **also in the API's service metadata** |
| IT | *Elaborazione su dati ISTAT e ANNCSU (Agenzia delle Entrate–ISTAT), CC BY 4.0* |
| PT | "Informação geográfica cedida pela Direção-Geral do Território" (CAOP, CC BY 4.0) |
| GB | "Contains OS data © Crown copyright and database right 2026" · "Contains Royal Mail data © Royal Mail copyright and database right 2026" · "Source: Office for National Statistics licensed under the Open Government Licence v.3.0" |
| IE | "© Tailte Éireann" (CC BY 4.0) |
| US | *Source: U.S. Census Bureau, TIGER/Line Shapefiles 2026* (TIGER is a registered trademark, so not in product names) · *U.S. Geological Survey, GNIS* |
| CA | "Contains information licensed under the Open Government Licence – Canada." · "Adapted from Statistics Canada, <product>, <reference date>. This does not constitute an endorsement by Statistics Canada of this product." |
| MX | "Fuente: INEGI, <nombre del producto>." + a transformation notice |
| AU | "Incorporates or developed using G-NAF © Geoscape Australia licensed by the Commonwealth of Australia under the Open Geo-coded National Address File (G-NAF) End User Licence Agreement" · *Source: Australian Bureau of Statistics, CC BY 4.0* |
| NZ | *Sourced from the LINZ Data Service and Stats NZ, licensed for reuse under CC BY 4.0* (LINZ's standard statement to be checked) |
| JP | *郵便番号データ: 日本郵便株式会社* (courtesy) · 「出典：「アドレス・ベース・レジストリ」（デジタル庁）を加工して作成」 |
| TW | the provider's exhibit wording under OGDL 1.0 (Chunghwa Post street file) |
| PL | *Źródło: GUS, TERYT, CC BY 4.0, stan na <data>* (the dane.gov.pl condition asks for the creation/retrieval time) · *Źródło: GUGiK, Państwowy Rejestr Granic* (courtesy) |
| CZ | "ČÚZK, 2026" (year = data currency), plus a link to the metadata and the ČÚZK terms |
| SI | "Geodetska uprava Republike Slovenije, Register naslovov, 2026" (authority, data type, reference date) |
| RS | *Републички геодетски завод, Адресни регистар — Српска лиценца за отворене податке* (the licence text's own wording to be checked) |
| UA | *КАТОТТГ — Міністерство з відновлення, інфраструктури та транспорту України, CC BY 4.0* |
| GR | *Source: ELSTAT* + ELSTAT's required statement that the data were modified and that ELSTAT bears no responsibility for the result |
| HR / RO (if used) | "Contains public sector information licensed under the Open Data Licence" + links (HR) · "Conține informații publice în baza Licenței pentru Guvernare Deschisă v1.0" (RO) |

---

## 5. Romanization and language tags

### 5.1 Standards in play

| Script / language | International / UN system | National official system (place names, signage) | Notes |
|---|---|---|---|
| Cyrillic (generic) | ISO 9:1995 (one-to-one, diacritics, reversible) | — | Russia's GOST 7.79-2000 is ISO 9-based |
| Russian | UN V/18 (1987, the "GOST 1983" system); BGN/PCGN 1947 | none used on signs consistently; passports follow ICAO Doc 9303 | BGN/PCGN is what NGA GNS and CLDR `Russian-Latin/BGN` produce; no single legally mandated system for geographic names [unverified] |
| Ukrainian | UN X/9 (2012) = national system | Cabinet of Ministers Resolution No. 55 (27 Jan 2010) | BGN gives "Kyyiv", the national system "Kyiv" |
| Belarusian | UN X/6 (2012) = national 2007 system | 2007 national system | Wikidata P5412 |
| Bulgarian | UN X/7 (2012) = national system | Transliteration Act 2009 ("streamlined system") | national "Sofia", BGN "Sofiya"; EKATTE carries it |
| Serbian / Macedonian Cyrillic | UN VIII/14 (2002, Serbian); III/11 (1977) | official Serbian Latin alphabet (digraphia) | Latin is a co-official orthography mapping 1:1, not a transliteration |
| Greek | UN V/19 (1987) = ELOT 743 ≈ ISO 843 | ELOT 743 (type 2 transcription on signs) | OSM `int_name` follows ELOT 743 |
| Chinese | UN III/8 (1977): Hanyu Pinyin; ISO 7098 | Hanyu Pinyin (PRC; Taiwan since 2009 with legacy exceptions [unverified]) | polyphonic characters need dictionaries |
| Japanese | no UN resolution in the WGRS list | **Cabinet Notice No. 4 of 22 Dec 2025 (令和7年12月22日 内閣告示第4号): Hepburn-based**, replacing the 1954 Kunrei-shiki notice; ISO 3602 is Kunrei-based | kanji readings are not derivable; kana or source romaji needed |
| Korean | UN V/20 (1987), a pre-2000 system that South Korea later replaced; the WGRS archive now lists Korean under "other languages" | Revised Romanization (South Korea, 2000); McCune–Reischauer (North Korea variant) | Wikidata P2001 / P1942 |
| Hebrew | UN III/13 (1977), IX/9 (2007) | Academy of the Hebrew Language rules | abjad: no vowels in normal writing |
| Arabic | UN XI/3 (2017, superseding the 1972 Beirut system) | varies by country | abjad |
| Persian | UN X/8 (2012) | national system | abjad |
| Thai | UN I/14 (1967), VIII/13 (2002) | Royal Thai General System (RTGS) | |
| Indian scripts | UN II/11, III/12, IV/17 | Hunterian for place names; ISO 15919 academic | Wikidata P5825 (ISO 15919) |
| Library cataloguing | ALA-LC (all scripts) | — | diacritic-heavy; ICU has some `ALALOC` transforms |

Sources: the UNGEGN Working Group on Romanization Systems archive (<https://arhiiv.eki.ee/wgrs/resolutions.htm>; frozen since
May 2023, current material on the UNGEGN WGRS site) and its status page (<https://arhiiv.eki.ee/wgrs/>), which lists 30
languages with UN-recommended systems and puts Japanese, Korean, Georgian, Armenian, Kazakh and others under "other
languages".

**Japan, a conflict between the passes.** The model pass recorded "Kunrei is official, modified Hepburn the most used
internationally". The world-sources pass found that the Council for Cultural Affairs recommended Hepburn on 20 August 2025
and that Cabinet Notice No. 4 of 22 December 2025 replaced the 1954 Kunrei-shiki notice (tsu/ji/sha, syllabic n, doubled
consonants, long vowels with a macron or doubled vowel letters). That finding comes from search results quoting
bunka.go.jp / mext.go.jp; the full notice was not read [unverified]. Japan Post's romaji are Hepburn either way.

### 5.2 Where authoritative romanized names exist

| Source | Coverage | System | Licence | Quality |
|---|---|---|---|---|
| NGA GNS | all countries except the US | BGN/PCGN (+ native script, variants) | no restrictions (cite) | **systematic; the best generic source** |
| Japan Post KEN_ALL_ROME | Japan to town-area level | Hepburn, upper case, no macrons, truncated at 35 characters | copyright not claimed | official-ish; yearly |
| Chunghwa Post street file | Taiwan streets | postal English (pinyin with legacy exceptions) | OGDL-TW 1.0 (CC BY-compatible) | official English address forms |
| Juso English DB | South Korea addresses | Revised Romanization | [unverified] | official; licence to check |
| CBS / Academy lists | Israel localities | Academy rules | [unverified] | official spellings |
| NSI EKATTE (BG) | Bulgarian settlements | 2009 Transliteration Act (column "Транслитерация", `name_en`: Скомля → Skomlya, Абланица → Ablanitsa) | NSI licence v2.0 **forbids distributing derivative/compiled works** | official, but needs NSI permission |
| Ukrposhta `postindex.zip` (UA) | oblast/raion/settlement + postcode | national 2010 system ("Vinnytska / VINNYTSKYI / Vinnytsia") | no licence stated | official-ish; KATOTTH itself has no Latin column |
| Serbian registers (RS) | streets, house numbers | Serbian Latin = 1:1 from Cyrillic | Serbian Open Data Licence | deterministic, so it can be generated if the files lack Latin columns |
| GR, RU | — | ELOT 743 (GR); no single mandated system (RU) | — | official lists are Greek- or Cyrillic-only: GNS or generated |
| UN/LOCODE | trade locations | "Roman alphabet" per national romanization | CC BY 4.0 | limited coverage, good spellings |
| Wikidata | everything | `en` labels (no system declared) plus P2125/P2001/P1721/P1942/P5412/P2440 | CC0 | uneven; system-tagged values sparse |
| GeoNames | everything | unlabelled variants, `asciiname` | CC BY 4.0 | useful as search variants, not for display |
| OSM | everything | `name:xx-Latn`, `name:zh-Latn-pinyin`, `int_name` | ODbL | good, but share-alike |
| Google address metadata | admin areas of CN/JP/KR/TW etc. | `sub_lnames` | no licence stated | reference only |
| NZ LINZ | NZ names | macron and `*_ascii` variants side by side | CC BY 4.0 | both forms supplied by the source |

### 5.3 Measured .NET and ICU behaviour

**.NET has no transliteration API.** `System.Globalization` exposes none (both passes; .NET 10.0.12 on Windows).

**ICU is reachable by P/Invoke.** Windows 10 1903+ and Windows 11 ship a combined `C:\Windows\System32\icu.dll` with
**unsuffixed** exports (`utrans_openU`, `utrans_transUChars`, `utrans_openIDs`). A PowerShell 7 probe (C# via `Add-Type`)
reported **ICU 72.1 with 742 transliterator ids**, including CLDR's BGN/PCGN and UNGEGN transforms (`Russian-Latin/BGN`,
`Any-uk_Latn/BGN`, `Bulgarian-Latin/BGN`, `Greek-Latin/UNGEGN`, `Any-sr_Latn/BGN`, `Armenian-Latin/BGN`,
`Georgian-Latin/BGN`, `Any-ka_Latn/BGN_1981`, `Any-mn_Latn/MNS`, `Ethi-Latn/ALALOC`, …). Measured outputs:

| Transform | Input | Output | Verdict |
|---|---|---|---|
| `Russian-Latin/BGN` | Москва, улица Тверская | Moskva, ulitsa Tverskaya | correct BGN/PCGN |
| `Any-Latin` | Москва, улица Тверская | Moskva, ulica Tverskaâ | ISO 9-style, diacritics |
| `Ukrainian-Latin/BGN` | Київ, Хрещатик | Kyyiv, Khreshchatyk | correct BGN; **not** the national "Kyiv" |
| `Bulgarian-Latin/BGN` | София, Пловдив | Sofiya, Plovdiv | BGN; the national system gives "Sofia" |
| `Greek-Latin/UNGEGN` | Αθήνα, Θεσσαλονίκη | Athí̱na, Thessaloníki̱ | diacritics; needs `Latin-ASCII` to reach the sign form "Athina" |
| `Han-Latin` | 北京市 重庆市 六安市 | běi jīng shì chóng qìng shì liù ān shì | syllable by syllable; 六安 is Lù'ān, so **wrong** |
| `Any-Latin` | 東京都渋谷区 | dōng jīng dōu sè gǔ qū | Chinese readings of Japanese kanji: **useless** |
| `Katakana-Latin` | トウキョウト シブヤク | toukyouto shibuyaku | mechanical; Hepburn is "Tōkyō-to Shibuya-ku" |
| `Hangul-Latin` | 서울특별시 종로구 | seoulteugbyeolsi jonglogu | jamo transliteration; RR is "Seoul-teukbyeolsi Jongno-gu" |
| `Korean-Latin/BGN` | 서울특별시 종로구 | (unchanged) | not available |
| `Hebrew-Latin/BGN` | תל אביב | tl ’vyv | **useless** (abjad) |
| `Arabic-Latin/BGN` | القاهرة | lqاhrh | **useless** |
| `Any-Latin; Latin-ASCII` | Αθήνα Москва 서울 תל אביב | Athena Moskva seoul tl 'byb | fine as a *search key* for alphabets only |

- **Linux:** distribution `libicuuc`/`libicui18n` export **version-suffixed** symbols (e.g. `utrans_openU_74`); they load with
  `NativeLibrary.TryLoad` + `TryGetExport`, probing the suffix the way .NET's own `System.Globalization.Native` does. Alpine
  images need `icu-libs`. `InvariantGlobalization` does not stop an application loading ICU itself (approach not verified in
  code) (<https://learn.microsoft.com/en-us/dotnet/core/extensions/globalization-icu>,
  <https://github.com/dotnet/runtime/blob/main/docs/design/features/globalization-invariant-mode.md>).
- **Managed ports are immature.** `ICU4N.Transliterator` 60.1.0-alpha.356 (last updated 2022-01-15; a port of ICU4J 60.1 that
  its README puts at ≈ 40% complete, "APIs in flux"; <https://www.nuget.org/packages/ICU4N.Transliterator>). The model pass saw
  ICU4N at alpha.440 (2026-06-16), still a prerelease with "APIs in flux" (<https://github.com/NightOwl888/ICU4N>). AnyAscii
  (ISC; <https://github.com/anyascii/anyascii>) works character by character, giving "QiYuXian" for 埼玉県.
- **CLDR's own Jpan-Latn transform** is provisional, "only for testing"
  (<https://cldr.unicode.org/index/cldr-spec/transliteration-guidelines>).
- **A build-time alternative:** run the transforms during the import (ICU4C `uconv -x`, ICU4J or PyICU) and store the output
  with its ICU/CLDR version: no runtime native dependency, and deterministic across databases (tooling not verified).

**Normalization and casing in .NET** (measured by the model pass, .NET 10.0.12, Windows, ICU):
- `ToLowerInvariant()` leaves İ, ı, ß/ẞ and ς alone; .NET has no full case folding
  (<https://www.unicode.org/Public/UCD/latest/ucd/CaseFolding.txt>), so these need an explicit map.
- `Normalize(NormalizationForm.FormKC)` folds ﬁ, fullwidth forms and U+2011. NFKD leaves ø, ł, đ/ð, æ, œ and þ alone, which ICU
  `Latin-ASCII` and Lucene's `ASCIIFoldingFilter` map.
- A culture-aware `IsPrefix` refuses "malmo" as a prefix of "Malmö" in `sv`, so search keys have to be ordinal data, not
  culture comparisons.
- Stripping combining marks is only safe after a Latin letter or digit: Japanese が is か + U+3099, and stripping it changes
  the word.
- Invariant-globalization mode may return `Normalize` input unchanged; whether that holds on .NET 11 is [unverified], so a
  startup check such as `"é".Normalize(NormalizationForm.FormD).Length == 2` detects it.
- Official data is mostly ASCII ("'s-Gravenhage" in BAG uses U+0027); user input is not (curly apostrophes, typographic
  dashes, U+00AD).

### 5.4 Language tags: RFC 5646, RFC 4647, RFC 6497

- **RFC 5646 §3.1.9 / §2.2.3** (<https://www.rfc-editor.org/rfc/rfc5646.html>): omit Suppress-Script subtags. That gives
  `nl`, `fr`, `de`, `ru`, `uk`, `el`, `ja`, `ko`, never `ru-Cyrl`, `ja-Jpan` or `ko-Kore`. A script subtag belongs only where
  it distinguishes: `zh-Hans`/`zh-Hant` (zh has no Suppress-Script), `sr-Cyrl`/`sr-Latn`, `ru-Latn`, `uk-Latn`, `el-Latn`,
  `ja-Latn`, `ja-Hrkt`, `ko-Latn`.
- **The script (ISO 15924)** can be filled from CLDR likely subtags when a tag omits it
  (<https://github.com/unicode-org/cldr-json/blob/main/cldr-json/cldr-core/supplemental/likelySubtags.json>), so filters
  never parse tags. Spark's Contributions already store Language and Script as two slots (`ko` / `Kore`, §7.4).
- **RFC 6497** (<https://www.rfc-editor.org/rfc/rfc6497.html>) advises against the `-t-` extension when a format has separate
  source/target fields. Registered IANA variants exist only for some systems: `pinyin`, `wadegile`, `tongyong`, `hepburn`,
  `alalc97`; there is none for UNGEGN, BGN/PCGN, ISO 9, GOST, Kunrei or Korean RR. CLDR's `common/bcp47/transform.xml`
  defines `m0` mechanism keywords, among them `alaloc`, `bgn`, `din`, `gost`, `iso`, `mcst` (Korean Ministry of Culture =
  RR), `mns`, `ungegn`, `iast`, `ewts`, `buckwalt`, `satts` and `prprname`
  (<https://raw.githubusercontent.com/unicode-org/cldr/main/common/bcp47/transform.xml>). National systems without a keyword
  (Ukrainian 2010, Bulgarian 2009, ELOT 743, Japan's 2025 notice) need a field of their own. The world-sources pass sketched
  tags such as `ru-Latn-t-ru-m0-bgn`; the model pass preferred a plain tag plus a separate system field, per RFC 6497.
- **Canonical case** (language lowercase, Script titlecase, region uppercase) matters in Spark: `RequestCultureResolver`
  matches `Accept-Language` entries against the configured language keys, exact first and then the base culture
  (`S/Services/RequestCultureResolver.cs:16-43` ✔), and the model pass found that match case-sensitive.
- **RFC 4647 lookup** truncates from the right: `zh-Hant-TW` → `zh-Hant` → `zh`. Spark's `TranslatedString.GetValue` today
  goes exact → `Split('-')[0]` → first value, so `zh-Hant-TW` never reaches `zh-Hant`; the client's `resolveTranslation` goes
  exact → `en` → first, with no base-language fallback (§7.4).
- **Endonyms and exonyms (UNGEGN glossary,** <https://unstats.un.org/unsd/geoinfo/ungegn/docs/pubs/Glossary_add_en.pdf>**):** an
  official romanization is an endonym, not an exonym ("Moskva… is not an exonym"). UNGEGN recommends minimizing exonyms, and
  Mapbox falls back to the local name (<https://docs.mapbox.com/data/tilesets/reference/mapbox-streets-v8/>). Name kinds used
  across UNGEGN, GeoNames, OSM and Wikidata: official, standardized, common, short, exonym, historic, colloquial, plus postal
  spellings and readings (`ja-Hrkt`).
- **Display evidence:** the area's official language comes from the municipality (REFNIS N/F/D/FN/NF, or the Flemish
  `officieleTalen`), and the authority orders bilingual names (FN vs NF). The OSM Brussels convention joins the two official
  names with " - " on one line. Country names: CLDR per UI locale, with `alt="short"` / `alt="variant"` ("UK", "Czech
  Republic"); CLDR was at 48.2 (2026-03-17), with 49 in beta (<https://cldr.unicode.org/downloads/cldr-49>).

### 5.5 What both passes read from this

- **Store source-provided romanizations as names**, each with its tag, system, source and a generated/authoritative flag.
  Authoritative sources per language, as the model pass listed them: Russian GOST 1983 (UN V/18; BGN/PCGN is a different
  system), Ukrainian national 2010 (UN X/9), Greek ELOT 743, Chinese Pinyin, Korean RR 2000 in the ROK, Japanese per §5.1;
  Arabic (2017), Hebrew (2007) and Thai (2002) from sources only.
- **Generate only for true alphabets** (Cyrillic, Greek, Armenian, Georgian, Mongolian Cyrillic), pinned to an ICU/CLDR
  version and flagged as generated, and only where no source value exists. For Ukrainian, Bulgarian and Greek the national
  system appears on signs, so it beats BGN.
- **Never generate for Han, kana/kanji, Hangul, Hebrew, Arabic or Thai** (measured above); use NGA GNS, the national registers
  (Japan Post, Chunghwa Post, Juso, CBS) and Wikidata labels.
- **Search keys are separate from display names:** every name variant (native, official romanizations, GNS, exonyms) plus a
  folded key for alphabetic scripts, so that "Kyiv", "Kiev", "Kyyiv" and "Київ" all find the same unit without displaying a
  machine transliteration. The model pass places transliteration in the build, so its output ships as data and a library
  upgrade shows up as a key-version change.

---

## 6. Model evidence

### 6.1 How registries record history

| Register | Identity | Codes reused? | How history is published |
|---|---|---|---|
| **INSEE COG (FR)** | (TYPECOM, COM) per vintage | **Yes.** A commune nouvelle takes the chef-lieu's code, which coexists with a commune déléguée (COMD) of the same code; codes change on a département change (MOD 41) and when the chef-lieu moves (MOD 50) | Yearly files; `v_commune_depuis_1943` (DATE_DEBUT/DATE_FIN); `v_mvt_commune` with MOD codes 10, 20, 21, 30–35, 41, 50, 70–72; an API by code + date |
| **BFS (CH)** | a historicization number `GDE_GHSTNR` per state; the BFS number per unit | **Documented as possible across states**; a canton change gives a new number | eCH-0071; numbered mutations; REST `/snapshot`, `/correspondances`, `/mutations`, `/levels` |
| **CBS / BAG (NL)** | CBS gemeentecode; BAG 16-digit ids | CBS: "nooit in zijn geheel hergebruikt"; BAG ids survive a herindeling | CBS indeling per 1 January since 1991; BAG bitemporal versions |
| **DESTATIS (DE)** | AGS (8 digits) / ARS (12 digits) | keys **change with Kreis reforms**; reuse policy [unverified] | Gebietsänderungen 1950–2026; GV-ISys |
| **ONS (GB)** | GSS code (9 characters) | new codes on change; "never reused" wording [unverified] | Code History Database since 2009 |
| **INE (ES)** | province (2) + municipality (3) + check digit | not reused, "with one exception" (a re-created municipality gets its old code back) | yearly change tables since 2000; census alterations since 1842 |
| **ISTAT (IT)** | province (3) + sequence (3) | renumbered in the Sardinia reform of 2026-01-01 | variazioni since 1991; SITUAS since 1861 |
| **Statbel (BE)** | NIS5; NIS6 per edition | none seen 1970–2025; new codes skip extinct numbers | validity intervals since 1970, no links; per-reform tables (§1.7) |
| **ISO 3166-1** | alpha-2 | reserved ≥ 50 years after deletion; AI, GE and SK were reassigned in the past | ISO 3166-3 four-letter codes |
| **GeoNames** | geonameid, "will never change" | reuse of deleted ids undocumented | ADMnH / PPLH feature codes for historical units; alternate names with from/to; daily diffs; no merger/split model |
| **Who's On First** | `wof:id`, kept forever | no | `wof:supersedes` / `wof:superseded_by`, `mz:is_current`, EDTF inception/cessation; a *list* of hierarchies (`wof:hierarchy`); `wof:parent_id` with special values −1…−4; `wof:concordances`; a new record supersedes on a significant change, including a new parent |
| **Wikidata** | Q-id; merged items redirect (<https://www.wikidata.org/wiki/Help:Merge>) | — | P1365/P1366, P571/P576, P131 with P580/P582 qualifiers (parallel P131 statements for several hierarchies) |
| **INSPIRE** | `inspireId` (namespace + localId), "will not change" | — | `versionId`; AU lifespan (`beginLifespanVersion`) = dataset time; AD `validFrom`/`validTo` = real-world time, i.e. bitemporal (<https://inspire.ec.europa.eu/schemas/au/4.0/AdministrativeUnits.xsd>, <https://inspire.ec.europa.eu/schemas/ad/4.0/Addresses.xsd>, <https://inspire.ec.europa.eu/codelist/StatusValue/StatusValue.en.json>) |
| **OpenStreetMap** | object ids | — | current state only, plus a full-history planet of edits; the model pass consulted the admin_level template (<https://wiki.openstreetmap.org/wiki/Template:Admin_level>) and OpenHistoricalMap's tags (<https://wiki.openstreetmap.org/wiki/OpenHistoricalMap/Tags>) without recording a history mechanism to reuse |

**INSEE's own words on the commune déléguée** (the mouvement rows for Arboys-en-Bugey, 2016-01-01: "Arbignieu COM 01015 →
Arbignieu COMD 01015" and "Saint-Bois COM 01340 → Saint-Bois COMD 01340"): TYPECOM "ne représente pas différents états
d'une même commune mais différentes communes. Ainsi peuvent coexister une commune et une commune déléguée avec le même
code." MOD 32 is a *création*. Sources: <https://www.insee.fr/fr/information/8377162>,
<https://www.insee.fr/fr/information/8740218>, <https://www.insee.fr/fr/information/6800675>,
<https://www.insee.fr/fr/information/5230987>, <https://www.insee.fr/fr/information/7671867>,
<https://www.insee.fr/fr/information/2560628>.

**Sources rewrite history.** INSEE changed Trois Lacs' code by mistake in 2021 and restored it in 2023. The Flemish register
records a merger years after its legal date (§1.14). Bastogne's merger date differs between Statbel and the decree (§1.11).
NIS6 codes change between editions (§1.8).

**How the history models compare on the cases that matter** (the model pass's comparison; "this model" is its proposal):

| Concern | Owner's proposal | Model pass's proposal | INSEE COG | BFS | WOF | Wikidata |
|---|---|---|---|---|---|---|
| Identity | a name | the unit, followed through recodes | (TYPECOM, code) | historicization number per state + BFS number | `wof:id` | Q-id |
| Merger | isCity → false + parent (overwrite) | an event; predecessors end or demote (dated) | MOD 31/32/33 rows | 29 × n → 21 / 26 + 29 → 26 | a new record supersedes | P1366 + P576 |
| Split | not expressible | an event with several `part` successors | MOD 20/21 | 29 → 21 × n | new records | several P1366 |
| Rename | overwrite | dated name; the old one stays searchable | MOD 10 | 23/23 (new state) | in place if the old name is kept | P1448 + P580/P582 |
| Transfer | overwrite | dated parent; derived segments | MOD 41 (new code) | 24/24 | supersede | parallel P131 |
| Code reuse | n/a | an `@date` id suffix; dated codes | yes, by design | possible | n/a | codes are statements |
| As-of query | no | yes | yearly files + API | `/snapshot?date=` | via EDTF filters | SPARQL |
| Postal codes | not modelled | own documents | separate (La Poste) | separate (swisstopo) | `postalcode` placetype | P281 |

(The BFS cells use the mutation notation as the model pass recorded it.) Sources for BFS:
<https://www.agvchapp.bfs.admin.ch/documents/rest_api_it.pdf>, <https://dam-api.bfs.admin.ch/hub/api/dam/assets/18484929/master>,
eCH-0071 <https://ech.ch/sites/default/files/dosvers/hauptdokument/STAN_d_REP_2014-03-07_eCH-0071_V1.1_Datenstandard%20Historisiertes%20Gemeindeverzeichnis%20der%20Schweiz.pdf>.
For WOF: <https://whosonfirst.org/docs/properties/wof/>, <https://whosonfirst.org/docs/processes/wof-life-cycle/>,
<https://whosonfirst.org/docs/processes/significant-event/>, <https://whosonfirst.org/docs/hierarchies/>,
<https://whosonfirst.org/docs/properties/name/>. For GeoNames: <https://www.geonames.org/export/codes.html>,
<https://forum.geonames.org/gforum/posts/list/41065.page>. For Wikidata: <https://www.wikidata.org/wiki/Property:P131>,
<https://www.wikidata.org/wiki/Q817375>, <https://www.wikidata.org/wiki/Q2785216>.

### 6.2 The owner's mechanic, tested against the evidence

The owner's proposal: `Place { Name, IsCity, Parent }`; Bevere is `IsCity = false, Parent = Oudenaarde`; when towns fuse,
the old towns go from `IsCity = true` to `false` and get the new town as parent.

**Where it matches what authorities do:**
- a merger whose predecessors survive as named parts: after 2019, "9770 Kruishoutem" is still what goes on the envelope, with
  main municipality Kruisem; Bevere under Oudenaarde likewise;
- an absorption into a unit that continues: Antwerpen kept 11002 and Borsbeek became its tenth district, literally
  `IsCity: true → false, Parent: Antwerpen` (<https://www.vlaanderen.be/organisaties/stad-antwerpen>,
  <https://www.vrt.be/vrtnws/nl/2023/12/19/reacties-inwoners-borsbeek-op-fusie-met-antwerpen/>);
- the French commune nouvelle: the old commune continues as a commune déléguée, the same place on the ground with a lower
  role and a parent (§6.1). The owner's mechanic is the COG mechanic.

**Where overwriting breaks it:**

| # | Case | Example | What goes wrong with `IsCity` + `Parent` | What it needs |
|---|---|---|---|---|
| 1 | History is overwritten | Kruishoutem in 2018 vs 2019 | "Which municipality was this address in on 2018-06-01?" has no answer; consumers cannot see what changed or since when; a merger adopted months ahead cannot be published early | dated periods |
| 2 | A municipality and its namesake section coexist | 1977–2018: municipality Kruishoutem (45017) **contained** the section Kruishoutem (NIS6 45017A, now 45017Z); likewise Oudenaarde | one document would have to be its own parent | separate places for the municipality and the section |
| 3 | Split | Ooike 45033 (1977); Huise 45015 (1977), reunited in Kruisem in 2019 | one parent cannot point to two successors; one name now denotes two places (9790 Ooike, 9700 Ooike) | lineage events with several successors |
| 4 | Rename | Basse-Sambre → Sambreville 92137 (1979); arrondissement Doornik → Doornik-Moeskroen (2019); FR MOD 10 | the old name, which people still type, is lost | dated names |
| 5 | Transfer between parents | Zwijndrecht (province, 2025); Opglabbeek (arrondissement, 2019); Kortessem and Moerbeke (2025); Brabant split (1995), which changed the province of every Leuven street without touching Leuven | previous parents are lost; an ancestor's change rewrites every descendant's chain | dated parent edges; derived ancestor timelines |
| 6 | Recode without change | La Louvière 55022 → 58001 and ten others (2019); FR MOD 41/50; every AGS in a reformed Kreis; NIS6 45017A → 45017Z | if ids are codes, a non-event creates a "new" place and orphans every reference | ids minted once; codes dated |
| 7 | Code reuse | INSEE 01015 is both the commune nouvelle Arboys en Bugey and the commune déléguée Arbignieu; BFS numbers reusable across states; ISO reassigned AI, GE and SK | `places/fr/insee/01015` would be ambiguous | an incarnation suffix |
| 8 | Postal codes span places | 9750 = Huise, Ouwegem, Zingem; CH 3174 Thörishaus = Neuenegg + Köniz; FR has 39,192 rows code postal × commune × ligne 5 | a postal code is not a node in the tree | postal documents with many-to-many links |
| 9 | Units that are not municipalities | NL woonplaats (2,502; "Hengelo (Gld)"); CH Ortschaft (≈ 4,100, not congruent with Gemeinden); GB post town; FR lieu-dit and libellé d'acheminement; Antwerp districts; DE Ortsteil; BeST hamlets | a boolean cannot say what a thing is; and "city" (*stad*) is an honorary title in Belgium: Oudenaarde is a city and Kruisem is not | a kind per country level plus a generic role |
| 10 | As-of for an address | an address in Nevele in 2018 | the answer comes from the street's municipality over time, and in Flanders even street and address ids change in a merger | streets with dated municipality links, continued across re-identification |
| 11 | Several hierarchies | admin vs postal vs statistical; WOF stores a list of hierarchies, Wikidata parallel P131 statements | one parent per place means one hierarchy | the admin tree in periods; postal links on postal documents |

### 6.3 Source patterns mapped to lineage events

The model pass's identity rule: a place is one unit of territorial organisation as its authority recognises it; its identity
survives name, code, parent and boundary changes; it ends only when the unit ends (dissolved, merged into a newly created
unit, or split with no continuing part); when the same unit carries on **one level down** (municipality → section, district
or commune déléguée), its identity continues and only its kind and parent change. Every pattern below appears in a real
source:

| Source pattern | Real example | Model action |
|---|---|---|
| 1 old code → 1 new code, same unit | La Louvière 55022 → 58001 (2019); FR MOD 41/50; DE Schlüsseländerung; CH 27/27; NIS6 45017A → 45017Z | **Recode**: same place, a new dated code, often a reparent too |
| n → 1 with a **new** code | Kruisem 45068; Hasselt 71072 (same name, new code); CH Fusion (29 × n → 21); NL merger under a new name | **Merge**: a new place; each predecessor **ends**, or is **demoted** if it continues as an official sub-unit |
| n → 1 where one predecessor **keeps** its code | Antwerpen 11002 + Borsbeek (2025); Kruishoutem 45017 absorbing Nokere and Wannegem-Lede (1977); CH Eingemeindung (26 + 29 → 26); NL merger keeping a name; FR fusion-association | **Absorb**: the keeper continues; the others end or are demoted |
| A unit continues as a sub-unit | Borsbeek → district; Bevere → section of Oudenaarde; Arbignieu COM → COMD; FR MOD 33/70 | **Demote**: same place, a new period with the lower kind and the new parent (the owner's case) |
| 1 → n, nothing continues | CH Gemeindetrennung (29 → 21 × n); Brabant province (1995) | **Split**: the place ends; all successors listed (ambiguous) |
| 1 → n, one part continues | Ooike 45033 (1977); Huise 45015 (1977) | the main part **continues** (demoted); the other parts are **ceded** |
| Part of a unit moves | Ooike east → Eine; CH Gebietsabtausch / Ausgemeindung | **Cede**: no identity change; recorded on both sides |
| Name changes | Sambreville 1979; Doornik-Moeskroen 2019; FR MOD 10 | **Rename**: a new dated name; the old one becomes historic and stays searchable |
| Parent changes, code kept | arrondissements 23000/24000/25000 under 20001/20002 (1995) | **Reparent** |
| A unit is restored | FR MOD 21: Neussargues en Pinatelle back to five communes (2025) | **Restore**: the old places get new periods (same ids); the dissolved unit ends |
| An authority reissues a code to a different unit | INSEE 01015 for Arboys en Bugey (2016); BFS reuse across states; ISO alpha-2 reassignment | **New place** with a date suffix on its id |

**The two judgement calls, and the evidence behind them:**
- **A recode continues the place.** Who's On First would supersede on a new parent, which means a new record for La Louvière in
  2019, and so a reference migration for every consumer over a non-event. BFS links the states instead.
- **A namesake section is a separate place.** NIS6 lists it separately (45017Z KRUISHOUTEM under 45068), bpost lists it as a
  locality, and it coexisted with its municipality from 1977 to 2018. It comes into being the day the municipality first
  absorbs others (1977-01-01 for Kruishoutem).

**Never derive the hierarchy from code digits.** Zwijndrecht's sections 11056A/B lie in Oost-Vlaanderen while their prefix
says province Antwerpen (§1.12), and the BAG catalogue says the same of its own ids
(<https://imbag.github.io/catalogus/hoofdstukken/algemene-principes>).

### 6.4 Worked cases the evidence supplies

These are the cases a history model has to get right, each a row of §6.3 applied to verified facts:

1. **Kruisem, 2019 (merge).** Create 45068; end 45017 and 45057; reparent each section (Kruishoutem, Wannegem-Lede, Nokere,
   Zingem, Ouwegem, Huise); postal localities 9750 and 977x switch main municipality on 2019-01-01. "Kruishoutem" then resolves
   three ways: the former municipality 45017 (history only), the section 45017A/Z (current), and the postal locality 9770
   (current, what users type).
2. **Bevere, 1964/65 (demote).** A curated event, because Statbel's 1970-based file does not know Bevere; the date stays
   secondary until verified (§1.13).
3. **Ooike, 1977 (continue + cede).** 45033 continues as a section of Wortegem-Petegem 45061; Oudenaarde receives the eastern
   part into Eine; postal 9790 Ooike → municipality 45061, 9700 Ooike → within Eine, municipality 45035. The 2019 NIS6
   edition's combined "MOREGEM + OOIKE" part is the kind of case where a name matcher must fail closed to curation.
4. **Huise, 1977 and 2019.** 45015 continues as a section of Zingem; "Deel Huise" becomes a section under Kruishoutem (a cede);
   both end up in Kruisem in 2019; the 2025 NIS6 edition drops "Deel Huise".
5. **Antwerpen + Borsbeek, 2025 (absorb + demote).** 11002 continues; Borsbeek 11007's municipality period ends and a
   district period starts under 11002.
6. **Hasselt, 2025 (merge under the same name).** Statbel gave a new code and the Flemish register re-identified everything,
   so the authorities treat it as a new unit: 71022 and Kortessem 73040 end, and Kortessem's sections re-parent. Both units
   display "Hasselt".
7. **Zwijndrecht, 2025 (merge across provinces).** 11056 ends; sections 11056A/B re-parent to 46030 in Oost-Vlaanderen while
   their prefixes still say province 1.
8. **La Louvière, 2019 (recode + reparent).** Same place: NIS 55022 until 2019-01-01, then 58001; parent arrondissement 55000
   → 58000 (created that day).
9. **Brabant, 1995 (split + reparent).** Province 20000 ends; 20001 and 20002 are created; arrondissements 23000/24000 move to
   20001 and 25000 to 20002; 21000 sits directly under the Brussels region. Leuven's own record does not change, but its
   ancestor chain does on 1995-01-01.
10. **Bastogne, 1/2 December 2024 (merge).** Statbel says 01/12/2024; the decree's effective date is 2 December (installation
    of the council); Wikidata uses 2024-12-02 for Bertogne (§1.11). The interval needs its source recorded.
11. **Arboys-en-Bugey, 2016 (FR).** Arbignieu 01015 and Saint-Bois 01340 continue, demoted COM → COMD; the commune nouvelle is
    a new unit holding the already-used code 01015.
12. **Neussargues en Pinatelle (FR, restore, 2025).** The commune nouvelle ends with `part` edges to the five communes, which
    come back.
13. **A Flemish street, 2025.** The register issues a new straatnaam URI (§1.14); a 1 → 1 mapping in the difference files means
    the same street to a consumer; a duplicate-name rename (359 in 2025) keeps the old name findable; the legal date
    (2025-01-01), not the register's version timestamp, is the change date.

### 6.5 As-of and successor resolution

- **"Which municipality was this address in on date X?"** is answered from the street: the street's municipality on X, then that
  municipality's ancestors on X. With only a section or municipality, its own state on X answers it.
- **A split makes the successor ambiguous.** A consumer holding a pre-split reference has to re-validate the address; its street
  disambiguates. An address-level split, where a new boundary cuts a street, needs house-number data, so without house numbers
  the honest answer is "ambiguous".
- **"Current" depends on the date asked.** A merger published in advance by decree takes effect at midnight; anything stored as
  "current" would go stale without a rebuild (inference from the advance publication of 1 January decrees, §1.11).
- **Range queries never match null in RavenDB** (§7.3), which is why the model pass used integer date keys (0 for an open
  start, 99991231 for an open end).

### 6.6 Evidence behind the id rules

- **No code is reused in Statbel's data, and one code can span a merger:** 45017 has a single row from 1970 to 2018 (the 1977
  absorption kept the code), so a plain `places/be/nis/45017` is unambiguous, while a date in every id would make every lookup
  need a date and would move an id whenever a source corrects a date (Bastogne).
- **Real reissues exist elsewhere:** INSEE 01015 (2016), BFS reuse across states, ISO alpha-2 reassignment, SGC reuse after two
  editions (§2.4), INE's re-created municipalities (§2.3).
- **Units without codes** exist (some sections, postal localities, German streets) and need a minted slug that only a ledger
  keeps stable across renames.
- **Stable street ids per source:** BeST namespace + objectIdentifier (BE), the BAG openbare-ruimte id (NL), the BAN toponym
  UUID (FR; not `cle_interop`, which embeds the commune code and changes with it), ESID (CH), USRN (GB).
- **Spark accepts such ids.** It assigns ids through conventions and supports `IHasNaturalId`
  (`S/SparkDocumentStoreConventions.cs:58-85`), and the Contributions generator already emits slash-separated natural ids of the
  form `{targetId}/{Property}/{slots…}`, e.g. `Songs/1234/Lyrics/ko/Kore`
  (`libs/contributions/MintPlayer.Spark.Contributions.SourceGenerators/ContributionsEmitter.cs:127-134` ✔).

### 6.7 Hierarchies and city lines per country

| Country | Levels, top-down (local names) | Optional / irregular | Municipality level | City line (what is printed) | Ids and history |
|---|---|---|---|---|---|
| **BE** | Gewest/Région → Provincie/Province → Arrondissement → Gemeente/Commune → Deelgemeente/Section (district in Antwerpen) → Straat | no province in Brussels; sections are not in the Flemish register | Gemeente | `NNNN Plaatsnaam` (the bpost locality, often a section name) | NIS (no reuse seen), NIS6 (unstable), BeST URIs |
| **NL** | Provincie → Gemeente → Woonplaats → Openbare ruimte → Nummeraanduiding | woonplaatsen cover every gemeente; the BES islands are outside the 342 | Gemeente | `1234 AB  WOONPLAATS` (PostNL: two spaces, uppercase) | CBS code never reused in full; BAG ids survive herindeling; bitemporal |
| **FR** | Région → Département → Arrondissement → (Canton) → Commune (+ COMD/COMA; ARM in Paris/Lyon/Marseille) → Voie / lieu-dit | irregular top: Corse, Métropole de Lyon, Paris, Alsace, the overseas units | Commune | line 6: `code postal + LIBELLÉ D'ACHEMINEMENT`; line 5: lieu-dit or commune déléguée | INSEE codes reused by design; history since 1943; BAN UUIDs |
| **DE** | Land → Regierungsbezirk → Kreis / kreisfreie Stadt → Gemeindeverband → Gemeinde → Ortsteil | Regierungsbezirk only in BW, BY, HE, NW; Gemeindeverband named per Land; Ortsteil has no code | Gemeinde | `PLZ Ort` (Deutsche Post locality) | AGS/ARS change on Kreis reform; changes since 1950 |
| **CH** | Kanton → Bezirk → Gemeinde → (Ortschaft = PLZ6) → Strasse | some cantons have no Bezirk (BFS pseudo-districts) | Gemeinde | `PLZ Ortschaft` (Ortschaft ≠ Gemeinde) | BFS number + historicization id; reuse possible; ESID |
| **GB** | Nation → Region (England only) → County / UA / met. district / London borough → District → Parish / community | two-tier vs unitary; Scottish community councils are not a tier; NI counties are not administrative | District / UA | `POST TOWN` (postal geography) + postcode on its own line | GSS + CHD since 2009; UPRN/USRN stable; no free address text |
| **ES** | Comunidad autónoma → Provincia → Municipio | — | Municipio | `%Z %C %S` | INE codes; new ones start with 9 |
| **IT** | Regione → Provincia → Comune | province codes renumbered (Sardinia 2026) | Comune | `CAP Comune (PR)` | ISTAT codes |
| **US** | State → County → (county subdivision) → Place | places can cross county lines | Place (USPS city) | `CITY, ST ZIP` (USPS last line, <https://pe.usps.com/text/pub28/28c2_006.htm>) | — |
| **JP** | 都道府県 → 市区町村 → 町字 → 番地/住居表示 | — | 市区町村 | `〒Z`, prefecture first; `lfmt` for Latin | local government code (1968); 町字ID (official 2025-06-01) |
| **RU** | Субъект → район / городской округ → населённый пункт | administrative vs municipal hierarchies [unverified detail] | — | libaddressinput, Latin `sub_lnames` | FIAS/GAR persistent codes |

- **Typeahead narrowing differs per country:** BE city line = postcode digits or a locality/municipality name → postal locality
  (code, name, main municipality), then the street within that municipality ("kruish" → `9770 Kruishoutem (Kruisem)`; "97" →
  9700…9799); NL postcode + huisnummer is the national norm but needs BAG addresses, otherwise woonplaats → street; FR commune
  or code postal → (code postal, libellé, ligne 5) rows, then voie; DE PLZ/Ort many-to-many, streets without an open national
  source; CH PLZ/Ortschaft → Strasse → Gebäudeadresse; GB post town / postcode district, streets free text; elsewhere GeoNames
  admin + populated places, then free text.
- **Free text must stay possible:** new streets appear before the registers have them, and many countries have no data at all.
- **Address formats:** libaddressinput's notation (`%O%n%N%n%A%n%Z %C` for BE, `Require: ACZ`, postcode `^\d{4}$`;
  <https://www.gstatic.com/chrome/autofill/libaddressinput/chromium-i18n/ssl-address/data/BE>) is a convenient notation, but
  the dataset states no data licence (§3.8), so formats would be authored rather than copied. For fallback countries, its
  `state_name_type` vocabulary (area, county, department, district, do_si, emirate, island, oblast, parish, prefecture,
  province, state) names the generic level labels. The UPU S42 addressing standard is the international reference
  (<https://www.upu.int/en/postal-solutions/programmes-services/addressing-solutions>).
- **Level labels are data:** the German label for a Belgian section is not settled [unverified]. Other sources consulted for
  these rows: <https://www.cbs.nl/nl-nl/dossier/nederland-regionaal/gemeente/gemeenten-en-regionale-indelingen/codering-gebieden>,
  <https://www.postnl.nl/versturen/brief-of-kaart-versturen/hoe-verstuur-ik-een-brief-of-kaart/brief-adresseren>,
  <https://data.laposte.fr/datasets/laposte-hexasmal>,
  <https://github.com/BaseAdresseNationale/adresse.data.gouv.fr/blob/master/public/schemas/adresses-csv.md>,
  <https://www.destatis.de/DE/Themen/Laender-Regionen/Regionales/Gemeindeverzeichnis/Glossar/amtlicher-gemeindeschluessel.html>,
  <https://www.ons.gov.uk/methodology/geography/ukgeographies/administrativegeography/england>,
  <https://www.gov.uk/government/publications/open-standards-for-government/identifying-property-and-street-information>,
  <https://www.ine.es/daco/daco42/codmun/codmun00i.htm>.

---

## 7. Spark fit

This section records what the repository offers and constrains, read at `e39b16de`. Whether and where the app is deployed
is the PRD's decision; the deployment facts below describe the owner's production host as measured, and the Community
licence limits apply to any host that runs Community.

### 7.1 Adding an app: the recipe and the hand-maintained lists

- **There is no template; the recipe is SeoTester's M7 + M12:** `docs/seo_analyzer_PRD.md` §4.1 (`:156-188`) plus its log
  entries "M7" (`:1302-1407`, the skeleton) and "M12" (`:38`, `:1926-2022`: compose, workflows, runbook). The commerce
  research reached the same conclusion (`docs/commerce_research.md:1820-1826`).
- **Shape** (`docs/seo_analyzer_PRD.md:158`, `:1307-1310`): `apps/<App>/<App>` (host + `ClientApp`, nx project
  `@spark-apps/<kebab-name>`), `<App>.Library` (plain SDK; an entity library refuses ASP.NET, SPARKLIB001, `:1353-1354`),
  `<App>.Tests`. The host's `project.json` declares only `implicitDependencies` on its ClientApp
  (`apps/SeoTester/SeoTester/project.json:1-5`); the test project's adds the host's `App_Data/**` and `appsettings*.json` to
  the inputs and a `local` configuration without coverage (`apps/SeoTester/SeoTester.Tests/project.json:4-25`).
- **Composition traps** (`docs/seo_analyzer_PRD.md:1334-1337`): the generated `AddActions()`, `AddCustomActions()`,
  `AddRecipients()`, `AddCronJobs()` exist only once the assembly declares such a class (CS1061 otherwise). The `--spark-*`
  commands are build steps that return before `Build()` (`apps/SeoTester/SeoTester/Program.cs:189-196`), and CI depends on
  them.
- **Liveness:** `GET /health`, anonymous, always 200, depending on nothing that can be misconfigured, is the contract for
  the compose healthcheck and the deploy's readiness loop (`apps/SeoTester/SeoTester/Health/HealthEndpoint.cs:5-17`); a
  generator endpoint mapped by `Map<App>Endpoints()` (`apps/SeoTester/SeoTester/Program.cs:208-213`).
- **Server-only paths stay out of the SPA twice:** the host's `UseWhen(... UseSpaImproved ...)` must exclude the public API
  prefix, so an unknown `/api/...` is a 404 and never `index.html` (CodeCoverage excludes `/api` and `/badge`,
  `apps/CodeCoverage/CodeCoverage/Program.cs:596-601`; SeoTester `/spark`, `/connect`, `/.well-known`, `/mcp`,
  `apps/SeoTester/SeoTester/Program.cs:217-233`), and `ngsw-config.json` must exclude it from the service worker.

**Hand-maintained lists a new app joins.** The PRD's own list (`docs/seo_analyzer_PRD.md:159`) cites `pull-request.yml`
lines that have since moved; these are the current anchors. Items 10–15 exist only for an app deployed beside CodeCoverage.

| # | File:line (current) | What to add |
|---|---|---|
| 1 | `package.json:13-24` (workspaces) | the app's ClientApp |
| 2 | `MintPlayer.Spark.slnx:42-47` (the `/apps/SeoTester/` folder) | a folder with every csproj |
| 3 | `.github/workflows/pull-request.yml:101` | the `nx run-many --target=build --projects=` list (restores binaries the verifications need, `:83-99`) |
| 4 | `.github/workflows/pull-request.yml:112` | the `--spark-verify-model` loop |
| 5 | `.github/workflows/pull-request.yml:137` | the `--spark-verify-security` loop (anonymous-surface baseline, `:126-134`) |
| 6 | `.github/workflows/regenerate.yml:53-61` | the merge queue's regenerate list ("keep the two equal", `:15`) |
| 7 | `tools/verify-ngsw-config.test.mjs:38-42` and `:66` | `APP_NAVIGATION_URLS.<App> = ['!/api', '!/api/**', '!/health', '!/health/**', …]` (each prefix both ways, `:34-37`) and the name in the "finds the seven Spark apps" list |
| 8 | `tools/test-local.mjs:31-33` (`E2E_APPS`) | only if the E2E suite hosts the app |
| 9 | `.github/workflows/test.yml:42-46` | nothing for a short suite ("Other suites"); a long suite gets its own matrix row **and** an entry in Other suites' `exclude` |
| 10 | `tools/verify-backend-networks.test.mjs:26-30`, `:62-84`, `:184` | the stack file, the cross-stack network, exact members (`web` holds exactly two apps, `:82`), the RavenDB clients list |
| 11 | a new `tools/verify-<app>-deploy.test.mjs` | modelled on `tools/verify-seotester-deploy.test.mjs` (`:132`, `:137`, `:150`, `:162`) |
| 12 | new `<app>-deploy.yml` + `<app>-image-check.yml` | modelled on `seotester-deploy.yml` / `seotester-image-check.yml` |
| 13 | `.github/workflows/code-coverage-deploy.yml:365-372`, `:396-398` | the cross-stack network block and a `register_app_cert` line |
| 14 | `apps/CodeCoverage/ops/raven-cert-renew.sh` | SeoTester is hard-coded 15 times (`:7,31,60,86,159,177,204,214,247-252`), including the stale-registration sweep (`:154-177`); a third app means turning the certificates into a list (a production change) |
| 15 | `apps/CodeCoverage/docker-compose.yml` (`coverage-raven` networks) | the new external network |
| 16 | docs | the app's README and deploy doc; a production paragraph in `CLAUDE.md`; the tables of `docs/code-coverage/raven-tls-migration.md:16-32,42-54`; the README tree (`README.md:153-154`, already stale: no SeoTester) |
| 17 | `apps/SparkId/SparkId/Migrations/` | a Development-only relying-party migration like `M_202610101200_SeoTesterRelyingParty.cs:7-35`, only if the admin UI signs in through SparkId |
| 18 | the app's `App_Data/securityPosture.txt` and route-table snapshot test | baselines; a public API widens the anonymous surface on purpose, and the posture diff is the reviewer's signal (`pull-request.yml:126-134`) |

- **`.gitignore` naming traps** (the class of `**/coverage/`): `[Rr]eleases/` (`:29`), `[Ll]ogs/` (`:40`), `*.log` (`:100`),
  `publish/` (`:202`), `**/[Pp]ackages/*` (`:222`), `*.[Cc]ache` (`:253`), `Backup*/` (`:283`). A seed or release folder
  named `Releases/` or `Packages/` would be silently untracked.
- **Ports** (https / http): Fleet 5003/5004, HR 5005/5006, DemoApp 5007/5008, QnA 5009/5010, SparkId 5011/5012, SeoTester
  5013/5014 (each `Properties/launchSettings.json:21`), CodeCoverage 5200/5201 (`:22`); the commerce demo reserves 5015/5016
  (`docs/commerce_research.md:1838`). `git grep` found no use of 5015–5029 in any config, so **5017/5018** were free. The
  localhost OAuth callbacks are registered per port (`docs/seotester/deploy.md:20-22`), so a port is permanent once providers
  are registered.
- **Database and certificate** (for a deployment beside CodeCoverage): production databases are named after the app
  (`apps/SeoTester/SeoTester/appsettings.json:12`); created by the operator with `EnsureDatabaseCreated=false`
  (`docs/code-coverage/raven-tls-migration.md:56-59`, `docs/seotester/deploy.md:79-83`); one client certificate per app with
  Admin on its own database only (`raven-tls-migration.md:44-54`); one pairwise internal network per app (owner decision
  D55; `raven-tls-migration.md:16-32`).
- **Image and deploy:** copy SeoTester's Dockerfile (`aspnet:11.0`; the Data Protection folder created and chowned before
  `USER app`, `docs/guide-docker-deployment.md:106-110`), compose service (`apps/SeoTester/docker-compose.yml:30-119`:
  certificates read-only, hard-coded Traefik `Host(...)`, healthcheck `start_period` sized for startup work, `mem_limit`) and
  deploy workflow (`.github/workflows/seotester-deploy.yml:9-52,286-331`). A precedent for a public, CORS-open, cacheable
  service on the same host: `apps/SparkSchemas` serves `Access-Control-Allow-Origin "*"` and `Cache-Control "public,
  max-age=31536000, immutable"` on versioned paths (`apps/SparkSchemas/nginx.conf:14-33`), useful for versioned release
  files.
- **Configuration:** user secrets `<app>-app-<8 hex>` (`apps/SeoTester/SeoTester/SeoTester.csproj:8`); outside Development
  Spark refuses to start until a Data Protection key location is set, and `ApplicationName` should be explicit
  (`docs/guide-data-protection.md:7-10,20-25,40-49,71-76`); forwarded headers default to loopback + private ranges with
  `ProxyHops=1`, and a CDN in front needs its ranges and `ProxyHops=2` or every visitor shares one rate-limit bucket
  (`docs/guide-docker-deployment.md:113-140,168-187`).
- **Admin sign-in:** with `LocalCredentials = Disabled` and no external provider Spark refuses to start
  (`docs/seo_analyzer_PRD.md:1321-1326`); CodeCoverage's pattern is one org-owned GitHub OAuth app and an `Administrators`
  group listed by stable provider user id (`docs/code-coverage/hosting.md:232-236`).
- **CI gates:** `--spark-verify-model` (`pull-request.yml:103-124`), `--spark-verify-security` against the committed posture
  (`:126-149`), the route-table snapshot test (`docs/seo_analyzer_PRD.md:1377-1380`), `npm run test:tools`
  (`pull-request.yml:77-81`), the image-check workflow, and the version-bump check for packages under `libs/`
  (`:151-160`).

### 7.2 A public, anonymous, CORS-open API

**Anonymous access.**
- "`anonymous` is not "everyone". A right that both an anonymous visitor and a signed-in user should have is **two grants**"
  (`docs/guide-authorization.md:328-331`); `@anonymous`/`@authenticated` resolve through `wellKnown`, and no claim can assert
  them (`:53-54`, `:333-335`). SeoTester's catalogue does exactly that (`apps/SeoTester/SeoTester/App_Data/security.json:38,41`).
- A denied anonymous caller gets 401 when the app has a sign-in, otherwise a 404 identical to a real not-found
  (`docs/guide-authorization.md:384-393`; `S/Endpoints/SparkDenial.cs:74-77`).
- **No fallback policy:** `AddSpark` registers only the `[SparkAuthorize]` policy (`S/SparkMiddleware.cs:60-62`), so an endpoint
  without authorization metadata is public by default, "indistinguishable from an oversight"
  (`apps/CodeCoverage/CodeCoverage/Controllers/BrowseController.cs:28-31`).
- Precedents: CodeCoverage's `BadgeController` is `[AllowAnonymous] [EnableRateLimiting("badges")] [SkipXsrfToken]`
  (`apps/CodeCoverage/CodeCoverage/Controllers/BadgeController.cs:29-33`); Spark's share endpoints use `group.AllowAnonymous()`
  (`S/Endpoints/Share/ShareEndpoints.cs:11-19`).
- **A review blind spot:** `[AllowAnonymous]` endpoints never appear in `securityPosture.txt`; CodeCoverage added an exact-set
  test over controllers (`apps/CodeCoverage/CodeCoverage.Tests/_Infrastructure/AnonymousSurfaceTests.cs:13-19,88-91`). The route-table
  snapshot records `allow-anonymous`, antiforgery, the rate-limit policy and CORS per route
  (`libs/testing/MintPlayer.Spark.Testing/RouteTableSnapshot.cs:141-185`), but not skip-XSRF.
- **A hazard for consumers:** ng-spark's auth interceptor navigates to sign-in on **any** 401 whose URL does not start with
  `apiBasePath`, cross-origin URLs included (`NG/auth/interceptors/src/spark-auth.interceptor.ts:12-28`). A 401 from a reference
  API would throw a user out of a half-filled form, so a public API must never answer 401.

**The generic query endpoint vs custom endpoints.**
- `POST /spark/queries/execute` returns `{columns, items[{id, breadcrumb, etag, values, typeHints}], totalItems, …}`; `take`
  defaults to 50 and is clamped to 1..1000 (`docs/Spark-API-Specification.md:223-240`; `S/Services/QueryReadPath.cs:38,176` ✔).
  For a public typeahead (inference): POST with JSON is preflighted and not HTTP-cacheable, it exposes RavenDB ids and change
  vectors, its shape is the model JSON, and its filter/sort surface has been oracle-prone (18 defects in one audit,
  `docs/subquery_column_filters_plan.md:408`).
- **`/spark/*` is already CORS-open:** every Spark endpoint answers `Access-Control-Allow-Origin: *` by default
  (`docs/guide-cors.md:29-40`; `S/SparkMiddleware.cs:333-340`). Every anonymous entity grant is therefore also a cross-origin
  API through `/spark/queries/execute` (inference).
- **Custom endpoints** (MintPlayer.AspNetCore.Endpoints generator): `internal sealed partial class X : IGetEndpoint` with
  `static string Path`, `[QueryParam]` properties, `[Inject]` fields and `HandleAsync(HttpContext)`
  (`apps/SeoTester/SeoTester/Catalogue/CatalogueEndpoint.cs:18-29`); a group is `IEndpointGroup` with `Prefix` and
  `Configure(RouteGroupBuilder, IServiceProvider)`, members `[MemberOf<G>]` (`apps/QnA/QnA/Testing/QnATestSeamEndpoints.cs:13-35`);
  wired by `[assembly: EndpointsMethodName("Map<App>Endpoints")]` and `endpoints.Map<App>Endpoints()` after `MapSpark()`
  (`apps/SeoTester/SeoTester/AssemblyInfo.cs:4`). One `Configure` attaches CORS and rate limiting to a whole group, as the
  identity provider's `OidcConnectCorsGroup` does (`libs/identity_provider/MintPlayer.Spark.IdentityProvider/Endpoints/Oidc/Groups.cs:73-82`).
  A stale `IMemberOf<G>` silently maps at the root (memory note, PR #492).
- A custom endpoint queries RavenDB directly, so Spark's presentation boundary (redaction, row rules) does not apply
  (`S/SparkMiddleware.cs:102-105`); it projects to explicit public DTOs. For public data with no row rules that is safe.

**CORS.**
- Outside `/spark` the pipeline calls `UseCors()` with the app's default policy if one exists; otherwise an endpoint opts in
  with `RequireCors` (`S/SparkMiddleware.cs:342-350`; `docs/guide-cors.md:77-88`). A default policy would reach every endpoint
  without metadata (`/connect`, controllers, `/health`) but not `/spark/*`
  (`tests/MintPlayer.Spark.Tests/IdentityProvider/OidcCorsScopeTests.cs:209-226`). `app.UseCors("X")` once opened the whole
  pipeline in the identity provider
  (`libs/identity_provider/MintPlayer.Spark.IdentityProvider/Extensions/SparkIdentityProviderExtensions.cs:190-198`).
- CORS runs **before** authentication, so preflights are not refused (`S/SparkMiddleware.cs:324-332`). Any-origin cannot carry
  credentials; cross-origin callers see the anonymous view (`docs/guide-cors.md:92-105`); anonymous GETs are outside the CSRF
  gate (`docs/guide-controllers.md:60-79`). `SetPreflightMaxAge` is used nowhere; a GET with only CORS-safelisted headers needs
  no preflight (Fetch spec).

**Rate limiting.**
- `spark.AddRateLimiter()`: fixed window, 150 requests / 10 s per client IP on `/spark` and `/connect`, 429
  (`docs/guide-rate-limiting.md:7-27`); setting `PathPrefixes` **replaces** the defaults, and every prefix shares one bucket
  (`:48-49,70-74`). Named policies with `RequireRateLimiting`/`[EnableRateLimiting]` compose with it (`:76-90`); CodeCoverage
  defines `browse`, `uploads`, `fork-uploads` and `badges` (600/min) (`apps/CodeCoverage/CodeCoverage/Program.cs:444-506`).
- **Not idempotent:** only `spark.AddRateLimiter()` installs the middleware; an extra `app.UseRateLimiter()` takes two leases
  per request and halves every budget (`docs/guide-rate-limiting.md:155-175`; `apps/SeoTester/SeoTester/Program.cs:150-156`).
- **The partition key is the exact address** (`S/Extensions/SparkBuilderRateLimiterExtensions.cs:115`), and so is every other
  limiter (`apps/CodeCoverage/CodeCoverage/Program.cs:453,500`, `SparkIdentityProviderExtensions.cs:116`,
  `S/Endpoints/Share/ShareEndpoints.cs:95`, `libs/mcp/MintPlayer.Spark.Mcp/Http/McpRateLimiter.cs:55`,
  `libs/settings/MintPlayer.Spark.Settings/Quotas/SparkQuotas.cs:31`). **The IPv6 /64 gap:** nothing aggregates by prefix (the only
  truncation code is Moderation's /24 and /48 HMAC, `libs/moderation/MintPlayer.Spark.Moderation/Services/ModerationIpHasher.cs:43-50`),
  the VPS publishes AAAA (`docs/seotester/deploy.md:160-165`), and an IPv6 client usually controls at least a /64. Whether
  Docker/Traefik on the VPS sees real IPv6 client addresses is [unverified].
- **429s are CORS-opaque:** the limiter runs before the CORS branches (`S/SparkMiddleware.cs:322` vs `:333-350`); there is no
  `OnRejected`/`Retry-After`, and the 429 body is empty (`docs/Spark-API-Specification.md:480`). A browser caller sees a
  network error, not a 429.
- Forwarded headers behind Traefik are right by default (rightmost `X-Forwarded-For`, `ProxyHops=1`; an empty trust list is
  refused outside Development: `S/Services/SparkForwardedHeaders.cs:58-80,111-118`). Typeahead costs about 3–5 debounced
  requests per second per active user (inference).
- **Existing caps:** generic `take` ≤ 1000; share links ≤ 100 (`S/Sharing/SparkShareOptions.cs:23`); **no search-length cap in
  Spark** (only CodeCoverage caps at 200 characters, `apps/CodeCoverage/CodeCoverage/CommitGraph/CommitGraphReader.cs:35`); no
  request timeouts, response compression or query-complexity limits anywhere.

**Caching.**
- No `AddOutputCache`, `UseResponseCaching` or `AddResponseCompression` anywhere; caching is hand-written per endpoint:
  `BadgeController` (`public, max-age=300`, weak ETag over SHA-256, 304 on `If-None-Match`,
  `apps/CodeCoverage/CodeCoverage/Controllers/BadgeController.cs:91-107`); SeoTester's `CatalogueEndpoint` (`public, max-age=300`,
  `Vary: Accept-Language`, `CatalogueEndpoint.cs:54-55`); SparkSchemas' immutable versioned files.
- **The XSRF mint trap:** `UseAntiforgeryGenerator()` runs on every response, and "An endpoint that declares itself
  shared-cacheable must opt out with [SkipXsrfToken] / .SkipXsrfToken(). A response carrying a per-user Set-Cookie has its
  Cache-Control forced to private" (`S/SparkMiddleware.cs:380-383` ✔). `.ShortCircuit()` is no substitute: it skips the rate
  limiter too. SeoTester's catalogue, badge, bot and legal endpoints set `public` without opting out, so they are probably
  uncacheable today [unverified].
- **A query's `ResultEtag` does not include the query text**, so it cannot serve as an HTTP ETag for different queries
  (<https://docs.ravendb.net/6.0/client-api/rest-api/queries/query-the-database/>). Content that depends only on (dataset
  release, normalized request) can carry an ETag derived from those two and answer 304 without a query (the model pass's
  reading). Corax counts every match unless statistics are skipped (`SkipStatistics`).
- **No CDN or proxy cache:** DNS points straight at the VPS and Traefik carries no cache middleware
  (`docs/code-coverage/hosting.md:118-127`).
- **Privacy:** typed fragments together with an IP address can be personal data (the model pass's reading), which argues for
  aggregate logging only.

### 7.3 Search on RavenDB

Abbreviations: **QE** = `S/Services/QueryExecutor.cs`, **GS** = `docs/guide-search.md`, **GQ** =
`docs/guide-queries-and-sorting.md`.

**Spark's generic search does not fit a typeahead.**
- `BuildSearchTerm` splits on whitespace, strips `*` and `?`, and wraps every word as `*word*` (QE:2033-2045 ✔), then emits one
  `Search()` per string field with `SearchOptions.Guess` and `SearchOperator.And`, values as parameters (QE:2234-2244; GS:10-15).
  Semantics: infix substring per word, every word in the **same** field, case-insensitive, no fuzzy, **no ranking**
  (GS:22-44,164-174). A leading wildcard is a term-dictionary scan (RavenDB knowledge).
- `/spark/queries/execute` pages static-index queries **in memory** over the whole result (QE:1050-1100), and a static index
  without a `[FromIndex]` projection gives an empty search-field set, so the search filters the **whole materialized result in
  memory** (QE:2098-2119,2213-2218; GS:151-160). Nothing folds or transliterates in any search path; the index generator copies
  expressions verbatim, "no accent stripping" by design (`docs/issue_210_PRD.md:156-159`). The reference picker fetches 50
  options and filters in memory (`docs/guide-reference-attributes.md:208-210`).
- **`.Search()` and row security:** an explicit `SearchOptions.Or` leaks onto the neighbouring row-security clause
  (`docs/issue_210_PRD.md:543-578`; QE:2173-2181; GS:144-149).

**Engines.**
- **Corax is the default on Community and Developer, Lucene on Professional/Enterprise** (model pass, ravendb.net/buy; the
  2026-10-17 re-fetch of the page lists "Static & Auto with Corax support" for Community and carries the footnote "Corax is set
  as a default indexing engine"). The engine
  is a per-index setting (`SearchEngineType`) and is not licence-gated
  (<https://docs.ravendb.net/7.1/indexes/search-engine/corax>, <https://docs.ravendb.net/7.2/indexes/search-engine/corax>); a
  Lucene index with fuzzy search works per index on a Corax-default server (memory note `reference_raven_fuzzy_unsupported_corax`,
  measured on 7.2.5).
- **Corax has no fuzzy search, no proximity search and no field-level boost**; query-time `boost()` works; fuzzy cannot be
  combined with wildcards (GS:164-170). As recorded during the decision round, fuzzy must directly follow `WhereEquals`, with no
  wildcard or `startsWith` in the same clause, and is available in document queries only [not re-checked here].
- **No production code pins an engine:** Spark creates databases with server defaults (`S/SparkMiddleware.cs:214-222`), the
  compose file sets no indexing option (`apps/CodeCoverage/docker-compose.yml:8-25`), measurements ran on Corax ("the default
  here", `docs/issue_210_plan.md:139-140`), and the localhost dev server defaults to **Lucene**
  (`docs/issue_272_273_275_276_plan.md:53-57`). A licence upgrade would silently change the default engine of unpinned indexes.
- **Version skew:** client RavenDB.Client 7.2.6 (`S/MintPlayer.Spark.csproj:64`), production server 7.1.10
  (`apps/CodeCoverage/docker-compose.yml:7`); every search measurement in the repository was on 7.2.x, so `startsWith` on array
  fields and `order by` on the production version are [unverified].

**Analyzers, stop words and folding.**
- With no setting, a field uses **LowerCaseKeywordAnalyzer**: one lowercase term per value. `Search` fields use
  **RavenStandardAnalyzer**, which removes English stop words and splits on hyphens, so the municipality **As** cannot be found
  through `search()`. No EdgeNGram analyzer ships; NGramAnalyzer is `[NotForQuerying]`; a wildcard query against a custom
  analyzer keeps only the first token (<https://docs.ravendb.net/7.2/indexes/using-analyzers>).
- **In-index folding is closed:** an `AdditionalSources` helper works (`docs/raven_datetimeoffset_and_sort_companions_PRD.md:274-276`),
  but the owner ruled it out because the helper text is embedded in every index and any edit forces a fleet-wide re-index
  ("recorded here so it is not re-proposed", `:290-308`); Community allows **one** custom analyzer per database (5 per
  cluster; ravendb.net/buy, re-fetched; `docs/seo_analyzer_PRD.md:761`); RavenDB has no egress
  (`docs/code-coverage/raven-tls-migration.md:34-40`). Folding therefore has to happen in C# before the write, with the same code
  folding the query.
- **Ranking:** Corax scores with BM25, which uses corpus statistics, so a database holding only one country would rank
  differently from a database holding all of them (the model pass's reading). Precomputed ranks and tiers order the same
  everywhere.
- Text-search references: <https://docs.ravendb.net/7.2/client-api/session/querying/text-search/starts-with-query>,
  <https://docs.ravendb.net/7.2/client-api/session/querying/text-search/full-text-search>,
  <https://docs.ravendb.net/7.2/indexes/boosting>, <https://docs.ravendb.net/7.2/client-api/session/querying/sort-query-results>.

**Absent fields and nulls.**
- An absent field does not match `== null`, and range predicates never match null or absent values (memory notes
  `reference_raven_absent_field_and_hasvalue.md`, `reference_raven_missing_field_default_match.md`); an absent field matches
  neither `== false` nor `== null` (`docs/seo_analyzer_PRD.md:900`). Index maps therefore emit sentinels: integer date keys with
  0 for an open start and 99991231 for an open end, and every boolean written explicitly.
- Inside `search()` a bare `*` matches everything and an empty term returns nothing (memory `reference_raven_search_semantics.md`);
  `startsWith` with an empty prefix matches everything (RavenDB knowledge). Parameters stop RQL injection but not search syntax,
  so the query text needs an allow-list after folding.
- Values go into RQL as parameters only; identifiers through `RqlIdentifier`
  (`libs/spark/MintPlayer.Spark.Abstractions/RqlIdentifier.cs:7-16,39-45`; `CLAUDE.md` "RavenDB queries").

**Index helpers and deployment.**
- `SparkIndexCreationTask<T>` exposes the whole `AbstractIndexCreationTask<T>` API (engine, analyzers, storage) and
  deliberately no `StoreAllFields` (`S/Indexes/SparkIndexCreationTask.cs:20-44,72`); SPARK018 warns when generated field options
  are not applied (`libs/source_generators/MintPlayer.Spark.SourceGenerators/Diagnostics/SortCompanionAnalyzer.Rules.cs:57-64`).
- `[GenerateIndex]` always emits `StoreAllFields(Yes)`
  (`libs/source_generators/MintPlayer.Spark.SourceGenerators/Generators/GenerateIndexGenerator.Producer.cs:288-298`) and has no engine
  or analyzer setting, so a typeahead index is hand-written. Indexes live in the application project, never in a library
  (owner rule, memory `feedback_indexes_only_in_application.md`).
- Indexes deploy at startup through `IndexCreation.CreateIndexes`, best-effort, with errors only to the console
  (`S/SparkMiddleware.cs:898-964`, `CreateSparkIndexes` at `:898` ✔). Any Map or Fields change is a side-by-side rebuild: the old
  index keeps serving, but **two copies exist on disk and in memory** meanwhile (`docs/spark_index_agreement_PRD.md:95-118`).
- **Corax's compression dictionary is trained when the index is created**, from up to 100,000 documents. Because Spark deploys
  indexes before anything is imported, an index created on an empty collection trains on nothing; resetting it after the first
  representative import is the remedy (model pass; <https://docs.ravendb.net/7.2/indexes/search-engine/corax>).
- Query → index binding: query `indexName` → `[DefaultIndex]` → raw collection (GQ:683-716). Only mapped fields are queryable,
  and `FieldIndexing.No` silently returns no rows (memory `project_subquery_filters_not_applied.md`). Fan-out stays far under
  RavenDB's 1024-entries-per-document warning at 1–3 entries per document.

**Sizing facts and estimates.** Indexing ran at ≈ 25k docs/s for plain fields and ≈ 9k docs/s with one analyzed field on the dev
laptop; the largest dataset measured in the repository is 50k documents (`docs/spark_index_agreement_PRD.md:103-110`). The
app-architecture pass estimated, for 10 M (street, place) documents: 5–10 GB of documents on disk (no document compression on
Community, RavenDB knowledge), a 1–2 GB typeahead index (StoreAllFields would add 3–5 GB; write-time edge n-grams would
multiply terms 3–5×), a 0.5–1.5 GB hot working set, and a 30–60 min first index build with a 1–3 h first load on three shared
cores. Belgium is about two orders of magnitude smaller (§1.16).

### 7.4 Names in Spark today

| Fact | Where |
|---|---|
| No Country, Language, PostalCode or Place entity exists; Address in DemoApp and HR is free text | `apps/DemoApp/DemoApp.Library/Entities/Address.cs:3-13`, `apps/HR/HR.Library/Entities/Address.cs:5-21` |
| `TranslatedString` = a flat `Dictionary<string,string> Translations` + `Key`. `GetValue`: exact → `Split('-')[0]` → first value. `Create(en, fr?, nl?)` only | `libs/model/MintPlayer.Spark.Model/TranslatedString.cs:6-52` (`GetValue` `:22-34` ✔, `Create` `:44-51` ✔) |
| Stored nested as `{"Translations":{…}}`; keys are free strings, never validated | `docs/guide-translated-strings.md:117-133`; `S/Services/EntityMapper.cs:971-993` |
| Generated per-language index fields are `$"{prop}_{language}"`, unsanitized: `Description_zh-Hant` is not a C# identifier, so a `[GenerateIndex]` entity with such a key probably fails to build (inferred; no test covers it) | `libs/source_generators/MintPlayer.Spark.SourceGenerators/Naming/IndexNaming.cs:91-92` ✔ |
| The language list behind those fields is fixed at compile time from `culture.json` | GS:64-70 |
| The client's `resolveTranslation` = `ts[lang] ?? ts['en'] ?? first`, with no base-language fallback; .NET falls back to the base culture, TypeScript jumps to `en`, so for script tags such as `sr-Latn` the two disagree | `NG/models/src/translated-string.ts:9-13` ✔; `docs/guide-translated-strings.md:26-38,56-70` |
| `RequestCultureResolver` picks a UI language from `Accept-Language`: exact → base culture → the default language; the model pass found the key match case-sensitive | `S/Services/RequestCultureResolver.cs:16-43` ✔ (flow; case sensitivity not re-checked) |
| Contributions use (Language, Script) as two slots ("ko/Kore"), not one BCP 47 tag; script detection is designed but not built | `docs/guide-contributions.md:23,109-110`; `libs/contributions/MintPlayer.Spark.Contributions.Abstractions/ContributionSlotFormat.cs:28-45`; `docs/contributions_PRD.md:85-95` |
| Contributions keep history as ordinary documents because revisions are capped on Community, and startup refuses more | `libs/history/MintPlayer.Spark.History/README.md:13-60`; `docs/guide-contributions.md:29-34` |

What a place name needs beyond that (the model pass's comparison): several names per language ("'s-Gravenhage" and "Den Haag",
both nl); a kind, validity, romanization system and source per name; script-tagged keys that survive index-field generation; an
RFC 4647 fallback chain (`zh-Hant-TW` never reaches `zh-Hant` today); and a factory beyond en/fr/nl (no `de`, which Belgium
needs).

### 7.5 The Community licence and the production host

**Community licence** (ravendb.net/buy, re-fetched 2026-10-17, quoting the page):
- "The limit is 3 subscriptions per database & 15 subscriptions per cluster."
- "Max number of revisions to keep is 2, and max age is 45 days." "No default policy can be set up."
- "1 custom analyzer per database & 5 custom analyzers per cluster"; "1 custom sorter per database & 5 custom sorters per
  cluster".
- "Max cores in cluster 3"; "Max cluster memory usage 6 GB RAM"; "Limited to 60 cores per customer entity, including its
  subsidiaries".
- Backups: only "Local" is listed.
- **No RavenDB/SQL ETL, no external replication, no Replication Hub or Sink** (the model pass's reading of the same page; the
  re-fetched text lists those rows without Community values). The repository agrees: "Community has no ETL, Developer does"
  (`docs/endpoints_generator_adoption_plan.md:219`; `docs/coverage-handoff-plan.md:136-138`).
- **12 static and 24 auto indexes per database** was recorded from ravendb.net/buy during the decision round. The 2026-10-17
  re-fetch found no index-count limit in the page text ("Static & Auto with Corax support"), so the figure is [unverified]; a
  13th static index on a Community server would settle it. **Resolved (2026-10-17):** RavenDB lifted the Community static
  and auto index limits for newly issued licences on 2024-05-22; existing licences update when the server reaches
  api.ravendb.net, and `/license/status` shows the effective limits
  ([discussion #18564](https://github.com/ravendb/ravendb/discussions/18564); the 2023 caps are in
  [discussion #17477](https://github.com/ravendb/ravendb/discussions/17477)).
- Expiration and refresh run at least every 36 h on Community (a smaller value kills the server); production's licence status
  reports `HasPeriodicBackup: false` (`docs/seo_analyzer_PRD.md:69-73,757-762`; `apps/CodeCoverage/README.md:383-399`; memory
  `reference_raven_licence_tiers.md`).

**`[Replicated]` does not work on Community.** It creates a RavenDB ETL task on the source, and the ETL deploy has no licence
check, so Community answers 500 (`libs/replication/MintPlayer.Spark.Replication/Services/EtlTaskManager.cs:22-109`;
`tests/MintPlayer.Spark.Tests/Replication/EtlTaskManagerDeploymentTests.cs:19`; `docs/commerce_PRD.md:520`, F18). It also needs
module mTLS and a shared `SparkModules` database (`docs/guide-cross-module-sync.md:101-114`; `docs/guide-replication-mtls.md:8-20`),
and its write-back sync actions cost a subscription against the cap of 3
(`libs/replication/MintPlayer.Spark.Replication/Workers/SyncActionSubscriptionWorker.cs:16`). Documentation:
<https://docs.ravendb.net/7.2/server/ongoing-tasks/etl/raven>, <https://docs.ravendb.net/7.2/server/ongoing-tasks/external-replication>.

**The owner's production host** (measured facts):

| Constraint | Evidence |
|---|---|
| Community licence, measured 2026-09-07; server 7.1.10, client 7.2.6; the licence expires 2027-03-31 and is renewed by hand (no egress) | `docs/commerce_research.md:308`; `docs/commerce_PRD.md:550,568`; `raven-tls-migration.md:34-40` |
| One Hetzner CPX32 (shared vCPU), twelve compose stacks; measured 2026-10-11: 4 cores, 7.6 GB RAM with about **3 GB available**, 93 GB free disk, a spare 10 GB volume | `apps/CodeCoverage/README.md:835-836`, `:196`; `docs/coverage_commit_graph_PRD.md:34` |
| One RavenDB server for every app (`coverage-raven`), capped at 3 cores / 6 GB; no second node on Community; the Coverage database alone grows ≈ 20 GB a year | `apps/CodeCoverage/README.md:319-329,850`; `docs/code-coverage/raven-tls-migration.md:92`; `docs/coverage_branch_merge_PRD.md:490-495` |
| Memory limits exist on some containers (SeoTester 1 GB, …) but **none** on `coverage-app` and `coverage-raven` | `docs/seotester/deploy.md:75-77`; `docs/code-coverage/hosting.md:45-47`; `apps/CodeCoverage/docker-compose.yml:201,228` |
| Re-indexing 228k documents "burns CPU on the production host" | `apps/CodeCoverage/README.md:760-763` |
| **No scheduled backups**; the verified path is a manual smuggler export (zstd dump) | `docs/seotester/deploy.md:256-257`; `apps/CodeCoverage/README.md:693-767,851` |
| No Studio in production; no external monitoring stack | `raven-tls-migration.md:276-282,299-302`; `docs/code-coverage/hosting.md:241-249` |
| CI and local runs use a **Developer** licence that caps nothing, so licence limits must be checked against production | memory `reference_raven_licence_tiers.md`; `CLAUDE.md` "Running the test suites" |

### 7.6 Seeding: mechanisms and constraints

| Mechanism | How it runs | Evidence | Fits |
|---|---|---|---|
| `ISparkMigration` | runs **synchronously inside `UseSpark()`** after index creation, before Kestrel listens, with `CancellationToken.None`; the `SparkMigrationRecords/{version}` marker is written only after `UpAsync` returns, so a failing migration is a restart loop; compare-exchange lock with a 30-min TTL that is **not renewed** (✔ `:229-232`); a waiting instance polls up to 35 min, then refuses to start | `libs/migrations/MintPlayer.Spark.Migrations/ISparkMigration.cs:17-31`; `SparkMigrationRunner.cs:15-20,96-100,227-238`; `S/SparkMiddleware.cs:481-521`; `libs/migrations/MintPlayer.Spark.Migrations/SparkMigrationsExtensions.cs:42-45` | schema reshapes and **queuing** a first import (CodeCoverage's `M_202610091400` only queues messages); **never the import itself**: a 228k-document re-key took ≈ 100 s and forced the healthcheck `start_period` to 600 s (`.github/workflows/code-coverage-deploy.yml:401-412`; `apps/CodeCoverage/CodeCoverage/Migrations/M_202609210900_ForgeQualifiedDocumentIds.cs:50-58,82-87`) |
| Hosted seeder (`IHostedLifecycleService.StartedAsync`) | after the host started, non-blocking; embedded JSON; insert-only writes with an empty change vector; refreshes only documents nobody edited (`SeededHash`); optimistic writes retried on `ConcurrencyException`; 128-id chunks | `apps/SeoTester/SeoTester/Catalogue/CatalogueSeeder.cs:8-77` (and the settings package's `SettingsSeeder`) | small curated sets |
| Cron (`ISparkCronJob`) | NCrontab in UTC; one compare-exchange claim per occurrence cluster-wide; no lease, retry or catch-up; the house pattern is a job that only queues a message | `libs/cron/MintPlayer.Spark.Cron/SparkCronScheduler.cs:101-102,228-275`; `apps/CodeCoverage/CodeCoverage/CommitGraph/CommitGraphRecipients.cs:38-50` | the "is there a new export?" check |
| Messaging (recipients) | one RavenDB subscription for all queues; per-queue `HandlerTimeout` (default 10 min), `MaxAttempts` 5, claims renewed; resumable `ICheckpointRecipient`/`IMessageCheckpoint`; `BroadcastOnceAsync(msg, key)` de-duplicates | `libs/messaging/MintPlayer.Spark.Messaging/SparkMessagingOptions.cs:114,144`; `SparkQueueOptions.cs:85`; precedent `DeleteDataAction` → `DeleteRepositoryDataRecipient` (512 per transaction, 2,000 per message, re-queue) | the import itself, restart-safe and resumable |
| Custom action | an explicit, authorized trigger; HTTP-request writes wait for indexes after each commit, background writes do not | `S/Services/DatabaseAccess.cs:971-980` | an admin "Import now" that queues the work |
| `BulkInsert` | used **only in tests** today; raises no store event, so no interceptor, `@expires` stamping or audit runs | `git grep`; `S/Expiration/ExpirationStamper.cs:15-20` ("Patches and bulk inserts raise no event"); raw and bulk writes are not audit-stamped (`docs/guide-auditing.md:123-131`) | first load, with explicit ids (bulk insert bypasses id conventions) |
| `PatchByQuery` / `DeleteByQuery` | refused on a stale index unless a `StaleTimeout` is given; RavenDB 7.2 can return a torn null result, hence a wrapper | `M_202609210900_ForgeQualifiedDocumentIds.cs:73-100`; `apps/CodeCoverage/CodeCoverage/Migrations/BulkOperation.cs:13-31,46-71` | retirement sweeps, parameters only |

- **Idempotent bulk writes:** BulkInsert's `SkipOverwriteIfUnchanged` (since 5.2) skips documents equal to the stored ones, so a
  re-import of the same data writes nothing, re-indexes nothing and wakes no subscription. Session writes without the flag, and
  Smuggler imports, always create a new change vector; Smuggler's defaults do not export tombstones
  (<https://docs.ravendb.net/7.2/client-api/bulk-insert/how-to-work-with-bulk-insert-operation>,
  <https://docs.ravendb.net/7.2/client-api/smuggler/what-is-smuggler>).
- **Session limits:** the default `MaxNumberOfRequestsPerSession` (30) is kept and lifted per session with `IgnoreMaxRequests()`
  (`S/SparkMiddleware.cs:228-233`, `S/Extensions/SessionExtensions.cs:12-31`); streaming reads (`Advanced.StreamAsync`) are
  common (`apps/CodeCoverage/CodeCoverage/Ingestion/BuildFinalizer.cs:60`).
- **The natural-id create race:** store conventions are `UseNaturalIds().UseGeneratedIds()`
  (`S/SparkDocumentStoreConventions.cs:26,58-63,79-83`), and "the id must not change" once a document exists
  (`libs/model/MintPlayer.Spark.Model/IHasNaturalId.cs:15-38`). `DatabaseAccess` derives the natural id and probes `ExistsAsync`
  in a side session (`S/Services/DatabaseAccess.cs:266-276` ✔), then stores with **no** expected change vector ("Null for a
  create — including a create under a caller-chosen id", `:774-779` ✔). Two concurrent creates with the same key both pass the
  probe and the second silently overwrites the first; the overwrite is pinned by
  `tests/MintPlayer.Spark.Tests/Services/NaturalIdConventionTests.cs:99-118`, and no test covers the race. Storing with
  `string.Empty` as the change vector raises a `ConcurrencyException`, which already maps to 409 (`:418-419`).
- **Write semantics for importers:** `StoreAsync(e, id)` is an upsert (last write wins); `StoreAsync(e, string.Empty, id)` is
  insert-only (`CatalogueSeeder.cs:67-68`); loaded documents under `OptimisticConcurrencyMode.Writes` update only if unchanged
  (`CatalogueSeeder.cs:47`).
- **Id hygiene:** ids are case-insensitive; a trailing `/` or `|` asks the server to assign one
  (`libs/contributions/MintPlayer.Spark.Contributions/ContributionsInterceptor.cs:194-196`), so an empty segment silently breaks
  determinism. An id that embeds a mutable parent code (a municipality's NIS code changes with a merger) forces a re-key like
  the 228k-document migration.
- **Large files:** CodeCoverage's XML ingest loads a DOM with a 256M-character cap
  (`apps/CodeCoverage/CodeCoverage/Ingestion/Parsing/SafeXml.cs:32-64`), right for ≤ 50 MB reports and wrong for a multi-GB export;
  the streaming precedent is `apps/SeoTester/SeoTester.Library/Crawling/SitemapReader.cs:17-36` (`XmlReader`, DTD prohibited, no
  resolver, a character cap). App containers run at `mem_limit: 1g` (`apps/SeoTester/docker-compose.yml:112`). The SSRF-safe
  client caps wire bytes, decompressed bytes and time (`libs/spark/MintPlayer.Spark.SafeHttp/README.md`).
- **Live collections:** live updates subscribe per collection through the Changes API (`S/Live/LiveHub.cs:219-221`) with no
  coalescing found, so bulk writes into a live-watched collection may cause re-query storms [unverified].
- **Production safety:** no database is backed up on a schedule, so a smuggler export precedes the first import and any large
  re-import (`apps/CodeCoverage/README.md:693-767`); deploys are one container `down`/`up`, so an import must survive restarts
  (messaging claims + checkpoint); Community keeps at most 2 revisions for 45 days (`docs/seo_analyzer_PRD.md:758`), so
  merge/split history has to be data, not revisions.
- **Nx cache hazards:** only `bin`/`obj` are cached outputs, so anything a build writes into `App_Data` is missing after a
  cache-hit build (`docs/spark_composition_PRD.md:37-38`; `nx.json:37-41,53-56`); a replay deletes outputs before extracting
  (memory `reference_nx_cache_replay_destructive.md`); large outputs poison the remote cache (memory
  `reference_ravendb_server_poisons_nx_cache.md`, `reference_nx_remote_cache_504.md`). An export must never be committed, embedded
  or build-generated; only small curated JSON can be embedded.
- **Development and tests:** tests seed with `SeedFromJsonAsync` (`libs/testing/MintPlayer.Spark.Testing/SparkTestDriver.cs:277,296`);
  parsers need real fixtures (memory `reference_coverage_parsers_need_real_fixtures.md`; for this data: Excel-serial dates,
  Lambert 72 and 2008 columns, the combined "MOREGEM + OOIKE" part). A Development-only migration is recorded as applied in
  production too (`apps/SparkId/SparkId/Migrations/M_202610101200_SeoTesterRelyingParty.cs:29-31`), so a later production seed
  needs a new version.

### 7.7 Distribution to consumers, and client reuse

**Distribution options as the model pass weighed them:**

| Option | For | Against | Reading |
|---|---|---|---|
| Live API only (consumers store ids + snapshot text) | no infrastructure in consumers; always current | checkout depends at runtime on another service; no local joins; **no `[Reference]`**, which resolves only in the local session (`docs/guide-reference-attributes.md:7-11`; `S/Services/Breadcrumb/BreadcrumbResolver.cs:686`); rate limits; tests need a stub | for typeahead and streets, not as the only channel |
| `[Replicated]` RavenDB ETL | push, near real time | needs the ETL licence on the source (§7.5); mTLS + a shared `SparkModules` database; one task per consumer; no cross-organisation trust | not on Community |
| Versioned snapshot files (NDJSON per country/collection + manifest + checksums, applied by a consumer-side service) | works across organisations and licences; deterministic; testable with real files; cacheable | latency = release cadence; consumers run a service | the primary channel |
| Change feed (per-release deltas) | small downloads; consumers can react | needs release history | part of the snapshot channel |

- **Local copies are not read-only by default:** Spark does not enforce read-only for imported or replicated types; HR's replicated
  `Car.json` has `isReadOnly:false`.
- **Snapshots on any licence:** the SparkSchemas `v{n}` pattern (immutable, versioned, CORS-open) fits release files; a migration
  can load a snapshot only once, so refreshes belong to a scheduled job.

**The Angular side** (`NG/` = `libs/node_packages/ng-spark/`):
- One public npm library, `@mintplayer/ng-spark` (`NG/package.json:4`, 22.41.1; peers include `@mintplayer/ng-bootstrap
  ^22.25.0`), with 52 secondary entry points. Adding one is a new folder: path mapping (`tsconfig.base.json:5-6`), the single nx
  build target, CI publishing (`.github/workflows/dotnet-build-master.yml:164,271-284`) and the PR version gate
  (`pull-request.yml:197`) pick it up by wildcard; a separate npm package would have to be added to each.
- **Every existing config token has a default** (`SPARK_CONFIG.baseUrl` = `/spark`, `NG/src/lib/spark-config.ts:3-22`;
  `/spark/auth`, `NG/auth/models/src/auth-config.ts:16-23`). `SparkLanguageService` fetches `/culture` and `/translations`, so
  anything using the `t` pipe needs a Spark host; `/models`, `/renderers` and `currentLanguage` are backend-free
  (`NG/models/src/translated-string.ts:6-7`).
- **Interceptors:** `sparkLanguageInterceptor` sets `Accept-Language` on every request, cross-origin included
  (`NG/services/src/spark-language.interceptor.ts:27-30`); the auth interceptor redirects on any 401 (§7.2). An `HttpClient` built
  from `HttpBackend` bypasses every interceptor (Angular knowledge).
- **Typeahead building blocks:** `@mintplayer/ng-bootstrap` 22.25.0 ships `typeahead`, `select` and `tree-select`. `bs-typeahead`
  has good accessibility (combobox role, `aria-*`, keyboard, a live announcer) but fires on **every keystroke with no debounce or
  cancellation**, renders only `suggestion.text` and is not a ControlValueAccessor (the package's `typeahead.d.ts:5-37`,
  `fesm2022/...typeahead.mjs:71-83,111`); it is used nowhere in the repository. `bs-tree-select` is a ControlValueAccessor over an
  async provider with `searchDebounceMs`, item templates and an `AbortSignal`; it needs a `<bs-form>` ancestor and is used once
  (`NG/po-form/src/spark-po-form.component.html:87-97`). ng-spark's `spark-reference-picker` and `spark-search-box` (300 ms
  debounce) are bound to a Spark host.
- **Plugging an address attribute into a consumer app:** edit renderers get `value, attribute, options, valueChange` and no form
  data (`NG/renderers/src/spark-attribute-renderer.ts:78-90`); the renderer list is one value and the last provider wins, so
  add-ons export arrays that the app spreads (`NG/renderers/src/spark-attribute-renderer-registry.ts:51-73`;
  `apps/QnA/QnA/ClientApp/src/app/app.config.ts:40-43`). **A single `AsDetail` attribute can never get an edit renderer:** the
  po-form template's `AsDetail` branch comes before the edit-renderer branch (`NG/po-form/src/spark-po-form.component.html:381-398`),
  per-field renderers inside the modal cannot fill sibling fields, and renderer edits skip `noteChange`, so `triggersRefresh`
  never fires for them (`NG/po-form/src/spark-po-form.component.ts:586,600-604`); with no edit renderer registered,
  `getEditRendererComponent` returns null (`:589-593`). Collection elements must be `[ValueObject] partial`
  (`docs/guide-asdetail-attributes.md:20-25`). A library can seed a renderer onto its own types' attributes with
  `SparkModelSatellites.SeedRenderer` (`libs/model/MintPlayer.Spark.Model/Model/SparkModelSatellites.cs:59`), which model sync
  applies to embedded types and never lets overwrite an existing renderer (`S/Services/ModelSynchronizer.cs:340,575-590`); library
  layers can ship model files, translations and rights (`docs/guide-library-layers.md:3-12,198-233`).
- **Versioning:** a new entry point is an ng-spark **minor**; NuGet packages stay `11.0.0-preview.N` (`CLAUDE.md` "Versioning").

**The .NET side:**
- Package layout `libs/<snake>/<Full.Name>/` with its own `PackageId`; master packs and pushes with `--skip-duplicate`
  (`dotnet-build-master.yml:161,264`); the PR gate demands a version bump in every changed csproj (`pull-request.yml:180-193`). A
  package id is published, permanently, by its first merge.
- **SPARKLIB001:** an entity library must not depend on ASP.NET Core (`Directory.Build.targets:182-202`), so a shared value type
  lives in a plain-SDK `*.Abstractions` package (precedents: History.Abstractions, SoftDelete.Abstractions). `[ValueObject]`'s key
  generator runs only in the declaring assembly, so that package must reference the generator itself (memory
  `reference_valueobject_needs_generator_reference.md`).
- **Typed-client precedents:** CodeCoverage takes a base address from options and switches the feature off when unset
  (`apps/CodeCoverage/CodeCoverage/CommitGraph/CommitGraphServiceCollectionExtensions.cs:14-24`); SeoTester demands an absolute
  http(s) URL and throws at registration (`apps/SeoTester/SeoTester.Fetching/SeoFetchingServices.cs:89-103`); HR's demo hard-codes a
  fallback URL (`apps/HR/HR/Api/FleetCarsProxyEndpoint.cs:36`). The add-on template binds `Spark:<Name>` and then applies a
  delegate, idempotent via a marker (`libs/notifications/MintPlayer.Spark.Notifications/SparkNotificationsExtensions.cs:43-59`).
- **Validation:** declarative rules are a closed set (maxLength, minLength, range, regex, email, url; unknown rules check nothing:
  `libs/spark/MintPlayer.Spark.Abstractions/ValidationRule.cs:5-11`); business rules throw
  `SparkValidationException(message, attributeName)` from an `IBeforeSave` interceptor
  (`libs/spark/MintPlayer.Spark.Abstractions/SparkValidationException.cs:3-31`; `docs/guide-interceptors.md:80-86,108`).
- **One contract for several clients** has a precedent: `apps/CodeCoverage/contracts/dependency-update/*.schema.json`, read by
  `apps/CodeCoverage/CodeCoverage.Git/Dependencies/DependencyContracts.cs`.
- The commerce PRD expects "an `Address` value plus a typeahead component from the reference-data client package", with the
  endpoint configurable by the host, including free-text entry (`docs/commerce_PRD.md:422-426`).

### 7.8 Gaps, stale documentation and open checks found along the way

**What Spark lacks today:**
1. IPv6 /64 rate-limit partitioning: every limiter keys on the exact address (§7.2).
2. CORS-opaque 429s without `Retry-After`: the limiter runs before CORS and has no `OnRejected` (§7.2).
3. No output-cache, compression or request-timeout plumbing; skip-XSRF is easy to miss and the route snapshot does not record it.
4. No typeahead-grade search: no prefix-only search, folding, transliteration or minimum length (§7.3).
5. No production `BulkInsert` and no resumable importer precedent (§7.6).
6. The natural-id create race in `DatabaseAccess`, untested (§7.6).
7. po-form: no edit renderer for a single `AsDetail` attribute; renderer edits skip `noteChange` (§7.7).
8. `bs-typeahead` has no debounce, cancellation, item template or form-control support (§7.7).
9. Ops: `raven-cert-renew.sh` and `verify-backend-networks.test.mjs` assume exactly two apps; no scheduled backups (§7.1, §7.5).
10. `[AllowAnonymous]` and unannotated endpoints are invisible to `securityPosture.txt` (§7.2).
11. Language keys are not sanitized in generated index field names; `TranslatedString` has no RFC 4647 chain on either side, and
    its factory takes only en/fr/nl (§7.4); `culture.json` tags are not validated as BCP 47.

**Stale documentation and memory:**
- `docs/seo_analyzer_PRD.md:159` cites `pull-request.yml` lines 137/148/173; they are now 101/112/137.
- `docs/PRD-Migrations.md:85` ("the loser skips and serves") no longer matches the lock-wait-then-refuse runner.
- `docs/guide-search.md:74-76` and `SearchAttribute.cs:7-8` describe a `{Name}Sort` companion; the generator emits `{Name}Search`
  (`libs/source_generators/MintPlayer.Spark.SourceGenerators/Naming/IndexNaming.cs:68-82`).
- `docs/Spark-API-Specification.md:752` shows a GET execute with string `sortColumns`.
- `README.md:153-154`'s tree omits SeoTester; `apps/CodeCoverage/README.md:774-777` suggests a periodic backup task that production's
  Community licence reports as unavailable.
- Memory notes `reference_json_fixture_importer_drops_offsets.md` (fixed in `JsonFixtureImporter.cs:41-49`),
  `reference_spark_addon_assembly_limits.md` (library layers now ship model and security files) and
  `ng-spark-library-structure.md` (describes the removed `ng-spark-auth` package) are stale.

**Unverified, worth a spike before building:**
- `startsWith` on string-array fields and `order by` a rank on the production server version (7.1.10, Corax).
- Whether IPv6 visitors reach the app with their own addresses through Docker and Traefik.
- Whether SeoTester's `public` endpoints actually ship `private` because they mint XSRF cookies.
- Whether bulk writes into a live-watched collection cause re-query storms.
- The Community index-count limit (§7.5).
- The model pass's proposed spikes: import Belgium (all collections) and then FR communes + postal codes, recording time and peak
  memory and checking that a re-import writes 0 documents; `suggest` p95 under debounced load (50 rps; a proposed target of
  ≤ 50 ms server-side); index disk and RAM for BE vs FR streets; the Corax dictionary with the index created before vs after the
  import; a Community consumer applying a monthly delta in under a minute.
- Real BeST counts and identifier stability across the 2025 mergers, read from a first import (the API totals in §1.2 are exact;
  the street XML counts are estimates).

---

## 8. Sources

All pages were fetched or probed on 2026-10-17 unless a line says otherwise.

**Repository documents cited most.** `docs/seo_analyzer_PRD.md` · `docs/commerce_PRD.md` · `docs/commerce_research.md` ·
`docs/guide-search.md` · `docs/guide-queries-and-sorting.md` · `docs/guide-translated-strings.md` ·
`docs/guide-reference-attributes.md` · `docs/guide-authorization.md` · `docs/guide-cors.md` · `docs/guide-rate-limiting.md` ·
`docs/guide-controllers.md` · `docs/guide-data-protection.md` · `docs/guide-docker-deployment.md` ·
`docs/guide-cross-module-sync.md` · `docs/guide-replication-mtls.md` · `docs/guide-contributions.md` ·
`docs/guide-asdetail-attributes.md` · `docs/guide-library-layers.md` · `docs/guide-interceptors.md` · `docs/guide-auditing.md` ·
`docs/issue_210_PRD.md` · `docs/issue_210_plan.md` · `docs/issue_272_273_275_276_plan.md` · `docs/spark_index_agreement_PRD.md` ·
`docs/raven_datetimeoffset_and_sort_companions_PRD.md` · `docs/spark_composition_PRD.md` · `docs/contributions_PRD.md` ·
`docs/subquery_column_filters_plan.md` · `docs/Spark-API-Specification.md` · `docs/PRD-Migrations.md` ·
`docs/endpoints_generator_adoption_plan.md` · `docs/coverage-handoff-plan.md` · `docs/coverage_commit_graph_PRD.md` ·
`docs/coverage_branch_merge_PRD.md` · `docs/code-coverage/raven-tls-migration.md` · `docs/code-coverage/hosting.md` ·
`docs/seotester/deploy.md` · `apps/CodeCoverage/README.md` · `CLAUDE.md`.

**Belgium: BOSA / BeST.** https://opendata.bosa.be/index.nl.html · https://opendata.bosa.be/index.fr.html ·
https://opendata.bosa.be/download/best/best-full-latest.zip · https://opendata.bosa.be/download/best/openaddress-bevlg.zip ·
https://opendata.bosa.be/download/best/openaddress-bebru.zip · https://opendata.bosa.be/download/best/openaddress-bewal.zip ·
https://opendata.bosa.be/download/best/postalstreets-latest.zip · https://opendata.bosa.be/download/best/postalstreets-empty-latest.zip ·
https://opendata.bosa.be/download/best/best-xsd-v24_4.zip · https://opendata.bosa.be/download/best/best-xsd-v24_3-changes.zip ·
https://opendata.bosa.be/download/best/best-mutations-examples-v12.zip · https://opendata.bosa.be/download/best/best-mutations-test-fl.zip ·
https://opendata.bosa.be/download/best/dcat.rdf ·
https://opendata.bosa.be/download/best/archive/lambert72/openaddress-bevlg-20261007.zip ·
https://opendata.bosa.be/download/best/bestinabox/bestinabox_gebruiksvoorwaarden.pdf ·
https://opendata.bosa.be/download/best/bestinabox/RELEASE-NOTES.txt ·
https://best.pr.fedservices.be/api/opendata/best/v1/belgianAddress/v2/doc/openapi.yaml ·
https://hub.docker.com/r/bosa/opendata-best-webapi ·
https://digital.belgium.be/federated-api-marketplace/rest-api/s332-general-bestservices-info/details ·
https://www.ibz.rrn.fgov.be/sites/default/files/documents/nl/rijksregister/omzendbrieven/BeSt_20201104.pdf ·
https://urbisdownload.datastore.brussels/BeSt/TechSpec/BeStAddress_TechSpec_EN20240601.pdf ·
https://www.transportdata.be/dataset/address-points-belgium-best-address ·
https://www.rijksregister.fgov.be/sites/default/files/documents/fr/registre-national/circulaires/COM_BESTADR_20220502_F.pdf.

**Belgium: regions.** Flanders: https://api.basisregisters.vlaanderen.be/v2/gemeenten ·
https://docs.basisregisters.vlaanderen.be/docs/api-documentation.html · https://basisregisters.vlaanderen.be/ ·
https://www.vlaanderen.be/digitaal-vlaanderen/onze-diensten-en-platformen/gebouwen-en-adressenregister/heradressering-bij-vrijwillige-gemeentefusies ·
https://www.vlaanderen.be/digitaal-vlaanderen/onze-diensten-en-platformen/gebouwen-en-adressenregister/faq ·
https://assets.vlaanderen.be/raw/upload/v1734692529/repositories-prd/Verschilbestanden_Fusies_20-12-2024_wmyxyo.zip ·
https://assets.vlaanderen.be/raw/upload/v1736931847/repositories-prd/fusies-verschilbestand-straatnamen-20241218_RNverrijking_v01_milvsg.zip ·
https://assets.vlaanderen.be/raw/upload/v1739368450/repositories-prd/fusies2025_adressen_eenvoudig_lifd9r.xlsx ·
https://download.vlaanderen.be/product/10145 · https://metadata.vlaanderen.be/srv/api/records/9c8b57a1-3c9a-4813-926e-38bb0bd387a7 ·
https://data.vlaanderen.be/id/licentie/modellicentie-gratis-hergebruik/v1.0 ·
https://www.vlaanderen.be/statistiek-vlaanderen/nieuwe-nis-codes-voor-gefuseerde-gemeenten-vanaf-1-januari-2025 ·
https://assets.vlaanderen.be/image/upload/v1709627167/VR_2024_2302_DOC.0214-3_Tweede_golf_samenvoegingsdecreten_-_decreetBIS_z9gmin.pdf.
Wallonia: https://geoportail.wallonie.be/catalogue/2998bccd-dae4-49fb-b6a5-867e6c37680f.html ·
https://geodata.wallonie.be/id/2998bccd-dae4-49fb-b6a5-867e6c37680f ·
https://geoservices.wallonie.be/geotraitement/spwdatadownload/results/2998bccd-dae4-49fb-b6a5-867e6c37680f/icar_adpt_shp.zip ·
https://geoservices.wallonie.be/arcgis/rest/services/DONNEES_BASE/ICAR_ADR_PT/MapServer. Brussels:
https://geobru-geonetwork.irisnet.be/geonetwork/srv/api/records/a8c9ccde-5c2b-11ed-913a-900f0cda5d5c ·
https://urbisdownload.datastore.brussels/LastVersion/BeStBrussels_31370_XML_04000_lastversion.zip ·
https://datastore.brussels/web/data/dataset/a8c9ccde-5c2b-11ed-913a-900f0cda5d5c ·
https://geoservices.irisnet.be/geocoding/swagger-ui/index.html.

**Belgium: Statbel and bpost.** https://statbel.fgov.be/en/open-data/code-refnis ·
https://statbel.fgov.be/sites/default/files/files/opendata/REFNIS%20code/TU_COM_REFNIS-20250101.zip ·
https://statbel.fgov.be/sites/default/files/files/opendata/REFNIS%20code/TU_COM_REFNIS-20250101.xlsx ·
https://statbel.fgov.be/sites/default/files/Over_Statbel_FR/Nomenclaturen/REFNIS_2025.csv ·
https://statbel.fgov.be/fr/propos-de-statbel/methodologie/classifications/geographie ·
https://statbel.fgov.be/sites/default/files/files/opendata/Statistische%20sectoren/20201005-Nota%20Refnis%20en%20Nuts-NL.pdf ·
https://statbel.fgov.be/en/news/modification-nsi-codes-municipalities-and-administrative-districts-1st-january-2019 ·
https://statbel.fgov.be/sites/default/files/files/opendata/Statistische%20sectoren/NIS6withnamefrom01012025.xlsx ·
https://statbel.fgov.be/sites/default/files/Over_Statbel_FR/Nomenclaturen/NIS6nwithnamefrom01012019.xlsx ·
https://statbel.fgov.be/sites/default/files/files/opendata/Statistische%20sectoren/Nis9_Nis6_refnis_names_01012026.xlsx ·
https://statbel.fgov.be/sites/default/files/Over_Statbel_FR/Nomenclaturen/Conversion%20Postal%20code_Refnis%20code_va01012025.xlsx ·
https://doc.statbel.fgov.be/publications/A000.02/A000.02N_Lappendeken_Belgie.pdf ·
https://statbel.fgov.be/en/open-data/statistical-sectors-2025 · https://statbel.fgov.be/en/cc-40 ·
https://www.bpost.be/nl/postcodevalidatie-tool · https://www.bpost.be/sites/default/files/zipcodes/zipcodes_num_nl_2025.xls ·
https://www.bpost.be/nl/disclaimer.

**Belgium: history and names.** https://nl.wikipedia.org/wiki/Ooike · https://fr.wikipedia.org/wiki/Ooike ·
https://nl.wikipedia.org/wiki/Bevere · https://nl.wikipedia.org/wiki/Oudenaarde · https://nl.wikipedia.org/wiki/Zwijndrecht_(België) ·
https://en.wikipedia.org/wiki/Fusion_of_the_Belgian_municipalities · https://nl.wikipedia.org/wiki/Faciliteitengemeente ·
https://crwflags.com/fotw/flags/be-vovwp.html · https://www.wikidata.org/wiki/Q2643529 · https://query.wikidata.org/ ·
https://wiki.openstreetmap.org/wiki/WikiProject_Belgium/Municipality_Fusions/2025 ·
https://www.lavenir.net/regions/luxembourg/2023/05/17/officiel-les-communes-de-bastogne-et-bertogne-fusionneront-en-2024-EI5LD3TQBVF7ZBHAF4W3XXGPFA/ ·
https://www.vlaanderen.be/organisaties/stad-antwerpen ·
https://www.vrt.be/vrtnws/nl/2023/12/19/reacties-inwoners-borsbeek-op-fusie-met-antwerpen/ ·
https://www.vrt.be/vrtnws/nl/2025/10/15/crevits-vraagt-aan-google-maps-apple-maps-om-hun-kaarten-aan-te/ (search result only, not
fetched).

**EU level.** https://eur-lex.europa.eu/eli/reg_impl/2023/138/oj (empty to the fetcher) ·
https://www.umwelt-online.de/recht/eu/23a/23a_0138.htm · https://ec.europa.eu/eurostat/web/nuts/local-administrative-units ·
https://ec.europa.eu/eurostat/help/copyright-notice ·
https://ec.europa.eu/eurostat/web/gisco/geodata/statistical-units/local-administrative-units ·
https://ec.europa.eu/eurostat/web/gisco/geodata/statistical-units · https://gisco-services.ec.europa.eu/distribution/v2 ·
https://gisco-services.ec.europa.eu/distribution/v2/pcode/csv/PCODE_PT_2025_4326.csv · https://inspire-geoportal.ec.europa.eu/ ·
https://data.europa.eu/data/datasets?is_hvd=true&locale=en · https://data.europa.eu/en/hvd-dashboard ·
https://docs.overturemaps.org/attribution.

**Netherlands.** https://service.pdok.nl/lv/bag/atom/bag.xml · https://service.pdok.nl/kadaster/bag/atom/downloads/lvbag-extract-nl.zip ·
https://service.pdok.nl/kadaster/bag/atom/downloads/bag-light.gpkg ·
https://www.kadaster.nl/zakelijk/producten/adressen-en-gebouwen/bag-2.0-extract · https://api.pdok.nl/kadaster/bag/ogc/v2 ·
https://www.pdok.nl/introductie/-/article/basisregistratie-adressen-en-gebouwen-ba-1 ·
https://api.pdok.nl/bzk/locatieserver/search/v3_1/ · https://www.pdok.nl/pdok-locatieserver ·
https://github.com/PDOK/locatieserver/wiki/API-Locatieserver · https://api.pdok.nl/kadaster/location-api/v1/ ·
https://www.cbs.nl/nl-nl/onze-diensten/methoden/classificaties/overig/gemeentelijke-indelingen-per-jaar/indeling-per-jaar/gemeentelijke-indeling-op-1-januari-2026 ·
https://www.cbs.nl/nl-nl/cijfers/detail/86247NED · https://www.cbs.nl/nl-nl/over-ons/website/copyright ·
https://data.overheid.nl/dataset/72061-gebieden-in-nederland-2026 ·
https://www.cbs.nl/nl-nl/dossier/nederland-regionaal/gemeente/herindelingen-en-grenswijzigingen ·
https://www.cbs.nl/nl-nl/dossier/nederland-regionaal/gemeente/gemeenten-en-regionale-indelingen/codering-gebieden ·
https://imbag.github.io/catalogus/hoofdstukken/algemene-principes ·
https://www.postnl.nl/versturen/brief-of-kaart-versturen/hoe-verstuur-ik-een-brief-of-kaart/brief-adresseren.

**France.** https://www.insee.fr/fr/information/8740222 · https://www.insee.fr/fr/information/2560452 ·
https://api.insee.fr/metadonnees/geo/commune/75056 · https://insee.fr/fr/information/2008466 ·
https://www.insee.fr/fr/information/8377162 · https://www.insee.fr/fr/information/8740218 · https://www.insee.fr/fr/information/6800675 ·
https://www.insee.fr/fr/information/5230987 · https://www.insee.fr/fr/information/7671867 · https://www.insee.fr/fr/information/2560628 ·
https://www.data.gouv.fr/fr/datasets/base-adresse-nationale/ · https://adresse.data.gouv.fr/data/ban/adresses/latest/csv/ ·
https://adresse.data.gouv.fr/donnees-nationales · https://adresse.data.gouv.fr/outils/api-doc/adresse ·
https://data.geopf.fr/geocodage/search ·
https://cartes.gouv.fr/aide/fr/guides-utilisateur/utiliser-les-services-de-la-geoplateforme/geocodage/ ·
https://www.data.gouv.fr/fr/datasets/base-officielle-des-codes-postaux/ · https://data.laposte.fr/datasets/laposte-hexasmal ·
https://www.data.gouv.fr/fr/datasets/662c47ee3d303760163d4fc8 · https://aitf-sig-topo.github.io/voies-adresses/ ·
https://github.com/BaseAdresseNationale/adresse.data.gouv.fr/blob/master/public/schemas/adresses-csv.md ·
https://acteurspublics.fr/articles/les-donnees-de-la-base-dadresses-nationale-enfin-sous-licence-libre-et-gratuite/.

**Luxembourg, Germany, Switzerland, Liechtenstein, Austria.** LU:
https://data.public.lu/fr/datasets/registre-national-des-localites-et-des-rues/ ·
https://download.data.public.lu/resources/registre-national-des-localites-et-des-rues/20261012-023002/caclr.zip ·
https://data.public.lu/api/1/datasets/adresses-georeferencees-bd-adresses/ · https://apiv3.geoportail.lu/geocode/search ·
https://data.public.lu/en/datasets/inspire-annex-i-theme-geographical-names-named-places/ ·
https://elections.public.lu/en/fusion-communes.html. DE:
https://www.destatis.de/DE/Themen/Laender-Regionen/Regionales/Gemeindeverzeichnis/_inhalt.html ·
https://www.destatis.de/DE/Themen/Laender-Regionen/Regionales/Gemeindeverzeichnis/Namens-Grenz-Aenderung/namens-grenz-aenderung.html ·
https://www.destatis.de/DE/Themen/Laender-Regionen/Regionales/Gemeindeverzeichnis/Glossar/amtlicher-gemeindeschluessel.html ·
https://www.destatis.de/DE/Service/Impressum/_inhalt.html · https://github.com/digineo/gemeindeverzeichnis ·
https://gdz.bkg.bund.de/index.php/default/verwaltungsgebiete-1-250-000-stand-01-01-vg250-01-01.html ·
https://gdz.bkg.bund.de/index.php/default/geographische-namen-1-250-000-gn250.html ·
https://sg.geodatenzentrum.de/public/gdz/dokumentation/deu/hk-de.pdf · https://www.govdata.de/dl-de/by-2-0 ·
https://sgx.geodatenzentrum.de/web_public/gdz/datenquellen/datenquellen_vg_nuts.pdf ·
https://gdk.gdi-de.org/geonetwork/srv/api/records/4ff293bb-8d08-4674-bd2a-d75f681c5f92 ·
https://www.opengeodata.nrw.de/produkte/geobasis/lk/akt/ · https://www.opengeodata.nrw.de/produkte/geobasis/lk/akt/gebref_txt/. CH/LI:
https://www.agvchapp.bfs.admin.ch/de · https://www.agvchapp.bfs.admin.ch/api/communes/snapshot?date=01-01-2026 ·
https://www.agvchapp.bfs.admin.ch/api/communes/mutations?startPeriod=01-01-2025&endPeriod=01-01-2026 ·
https://www.agvchapp.bfs.admin.ch/documents/rest_api_it.pdf · https://dam-api.bfs.admin.ch/hub/api/dam/assets/18484929/master ·
https://ech.ch/sites/default/files/dosvers/hauptdokument/STAN_d_REP_2014-03-07_eCH-0071_V1.1_Datenstandard%20Historisiertes%20Gemeindeverzeichnis%20der%20Schweiz.pdf ·
https://www.swisstopo.admin.ch/de/amtliches-ortschaftenverzeichnis · https://www.swisstopo.admin.ch/de/amtliches-verzeichnis-der-strassen ·
https://data.geo.admin.ch/api/stac/v1/collections/ch.swisstopo.amtliches-strassenverzeichnis ·
https://data.geo.admin.ch/api/stac/v1/collections/ch.swisstopo.amtliches-gebaeudeadressverzeichnis ·
https://data.geo.admin.ch/ch.swisstopo.amtliches-gebaeudeadressverzeichnis/amtliches-gebaeudeadressverzeichnis_ch/amtliches-gebaeudeadressverzeichnis_ch_2056.csv.zip ·
https://data.geo.admin.ch/ch.swisstopo.amtliches-gebaeudeadressverzeichnis/amtliches-gebaeudeadressverzeichnis_li/amtliches-gebaeudeadressverzeichnis_li_2056.csv.zip ·
https://www.swisstopo.admin.ch/de/nutzungsbedingungen-kostenlose-geodaten-und-geodienste · https://www.swisstopo.admin.ch/ogd-conditions.
AT: https://www.statistik.at/services/tools/services/regionales/regionale-gliederungen ·
https://www.bev.gv.at/Services/Downloads/Produktbezogene-Downloads/Unentgeltliche-Produkte/Adressregister.html ·
https://www.bev.gv.at/Services/Geoinformationsdienste/Services/BEV-Adresssuch-Service.html ·
https://www.bev.gv.at/Services/Produkte/Adressregister/Oesterreichisches-Adressregister.html · https://data.statistik.gv.at/.

**Nordic and Baltic.** DK: https://datafordeler.dk/ · https://datafordeler.dk/media/251atk5z/brugervejledning-transitionsguide-for-fildownloads.pdf ·
https://datafordeler.dk/vejledning/brugervilkaar/danmarks-adresseregister-dar/ ·
https://www.klimadatastyrelsen.dk/om-klimadatastyrelsen/vilkaar-og-priser · https://api.dataforsyningen.dk/postnumre/8000 ·
https://dataforsyningen.dk/data/4924 ·
https://via.ritzau.dk/pressemeddelelse/15130330/dmi-og-klimadatastyrelsen-sammenlaegges-til-ny-styrelse?lang=da. NO:
https://geonorge.no/geonetworktest/srv/api/records/f7df7a18-b30f-4745-bd64-d0863812350c ·
http://nedlasting.geonorge.no/geonorge/ATOM-feeds/MatrikkelenAdresse_AtomFeedCSV.xml · https://nedlasting.geonorge.no/api/capabilities/ ·
https://ws.geonorge.no/adresser/v1/ · https://data.ssb.no/api/klass/v1/ ·
https://data.norge.no/en/datasets/5e6847ba-156d-4e14-85d3-8d7f8b727523 · https://www.kartverket.no/api-og-data/vilkar-for-bruk ·
https://www.ssb.no/diverse/lisens · https://www.ssb.no/metadata/om-klass · https://www.ssb.no/metadata/alle-endringer-i-de-regionale-inndelingene.
SE: https://www.scb.se/hitta-statistik/regional-statistik-och-kartor/regionala-indelningar/lan-och-kommuner/ ·
https://www.scb.se/vara-tjanster/oppna-data/ ·
https://www2.lantmateriet.se/globalassets/geodata/geodataprodukter/pb-administrativ-indelning-nedladdning-vektor.pdf ·
https://www.lantmateriet.se/globalassets/om-lantmateriet/var-samverkan-med-andra/byggnad-adress-lagenhet/informationsspecifikation-utbyte-belagenhetsadress.pdf ·
https://www.lantmateriet.se/en/geodata/our-products/open-data/ ·
https://www.lantmateriet.se/globalassets/geodata/geodataprodukter/anvandningsvillkor_for_vardefulla_datamangder.pdf ·
https://www.lantmateriet.se/globalassets/geodata/geodataprodukter/anvandningsvillkor_for_vardefulla_datamangder_pu.pdf ·
https://community.openstreetmap.org/t/lantmateriet-belagenhetsadress/127427?page=4. FI:
https://data.stat.fi/api/classifications/v2/classifications?searchText=kunta ·
https://paikkatiedot.ymparisto.fi/geoserver/ryhti_building/ogc/features/v1 ·
https://paikkatiedot.ymparisto.fi/geoserver/ryhti_inspire_ad/ogc/features/v1/ ·
https://www.maanmittauslaitos.fi/en/maps-and-spatial-data/datasets-and-interfaces/product-descriptions/geographic-names ·
https://avoin-paikkatieto.maanmittauslaitos.fi/maastotiedot/features/v1/ · https://www.posti.fi/webpcode/ ·
https://www.stat.fi/org/lainsaadanto/copyright_en.html · https://www.maanmittauslaitos.fi/en/opendata-licence-cc40 ·
https://www.syke.fi/en/environmental-data/open-web-services/web-map-services · https://www.posti.fi/fi/asiakastuki/postinumerotiedostot.
IS: https://hmsstgsftpprodweu001.blob.core.windows.net/fasteignaskra/Stadfangaskra.csv ·
https://www.natt.is/is/midlun/opin-gogn/nidurhal-gagna · https://www.natt.is/is/midlun/opin-gogn. EE:
https://geoportaal.maaamet.ee/eng/spatial-data/address-data-p313.html · https://aks.maaruum.ee/aks/valjavotted ·
https://aks.geoportaal.ee/aks/search/address · https://geoportaal.maaamet.ee/eng/spatial-data/administrative-and-settlement-division-p312.html ·
https://s3.pilw.io/rp-kemit-kataster/EHAK/ · https://geoportaal.maaamet.ee/eng/spatial-data/address-data/postal-codes-p661.html. LV:
data.gov.lv package 6b06a7e8-dedf-4705-a47b-2a7c51177473. LT: https://www.registrucentras.lt/aduomenys/?byla=adr_gatves.csv ·
https://get.data.gov.lt/datasets/gov/rc/ar/ · https://creativecommons.org/licenses/by/4.0/deed.lt.

**Central, Eastern and South-Eastern Europe.** PL:
https://eteryt.stat.gov.pl/eTeryt/rejestr_teryt/udostepnianie_danych/baza_teryt/uzytkownicy_indywidualni/pobieranie/pliki_pelne.aspx ·
https://api.stat.gov.pl/Home/TerytApi · https://www.geoportal.gov.pl/pl/dane/panstwowy-rejestr-granic-prg/ ·
https://opendata.geoportal.gov.pl/prg/adresy/PunktyAdresowe/POLSKA.zip · https://opendata.geoportal.gov.pl/prg/adresy/PRG-punkty_adresowe.zip ·
https://opendata.geoportal.gov.pl/prg/granice/00_jednostki_administracyjne.zip ·
https://www.geoportal.gov.pl/aktualnosci/podsumowanie-prg-prng-i-kwartal-2026-r/ · https://api.dane.gov.pl/1.4/datasets/747 ·
https://www.gov.pl/web/ksng/nazwy-w-jezykach-mniejszosci-w-polsce ·
https://www.poczta-polska.pl/en/dla-biznesu/oferta/uslugi-pozostale/pocztowe-numery-adresowe/ ·
https://datahub.io/logistics/postal-codes-pl/.licensing-notes · https://github.com/GML22/PostCodesMaps. CZ:
https://vdp.cuzk.cz/vdp/ruian/adresnimista ·
https://cuzk.gov.cz/Uvod/Produkty-a-sluzby/RUIAN/2-Poskytovani-udaju-RUIAN-ISUI-VDP/Dopady-zmeny-zakona-c-51-2020-Sb/Adresni-mista-CSV_atributy.aspx ·
https://geoportal.gov.cz/php/micka/record/basic/CZ-00025712-CUZK_SERIES-MD_RUIAN-CSV-HIE-ST?dlang=eng ·
https://cuzk.gov.cz/ruian/Poskytovani-udaju-ISUI-RUIAN-VDP/Vymenny-format-RUIAN-(VFR)/FAQ-casto-kladene-otazky-k-VFR/4-Je-mozne-ziskat-data-RUIAN-i-pro-komercni-ucely.aspx ·
https://cuzk.gov.cz/Predpisy/Podminky-poskytovani-prostor-dat-a-sitovych-sluzeb/Podminky-poskytovani-prostorovych-dat-CUZK.aspx ·
https://www.ceskaposta.cz/ke-stazeni/zakaznicke-vystupy. SK: https://static.slov-lex.sk/static/SK/ZZ/2015/125/20240401.html ·
https://wiki.openstreetmap.org/wiki/Sk:MinvSKAddress · https://rpi.gov.sk/en/metadata/e94d090f-464c-4b98-a000-6cb9d130d2c0 ·
https://launify.com/en/data · https://www.zakonypreludi.sk/zz/1994-191 · https://www.posta.sk/psc. SI:
https://www.gov.si/novice/2024-02-28-informacijska-prenova-registra-prostorskih-enot-in-vkljucitev-registra-naslovov/ ·
https://eprostor.gov.si/imps/srv/api/records/8f427833-d5bf-49fd-ae9e-1106eb63e901 ·
https://www.e-prostor.gov.si/fileadmin/struktura/EGP/preberi_me.pdf ·
https://www.uradni-list.si/glasilo-uradni-list-rs/vsebina/2003-01-3437/pravilnik-o-vsebini-in-nacinu-vodenja-registra-prostorskih-enot.
HU: https://www.ksh.hu/apps/hntr.main?p_lang=HU · https://www.ksh.hu/apps/hntr.egyeb?p_lang=HU&p_sablon=LETOLTES ·
https://www.ksh.hu/apps/hntr.nemzetisegi?p_lang=HU · https://www.posta.hu/static/internet/download/Iranyitoszam-Internet_uj.xlsx ·
https://bmprojektek.kormany.hu/akadalymentes/download/e/61/11000/KCR_projekt_zarorendezveny_tajekoztato.pdf. HR:
https://geoportal.dgu.hr/services/atom/INSPIRE_Addresses_(AD).zip · https://geoportal.dgu.hr/services/atom/ad/xml ·
https://geoportal.dgu.hr/services/atom/au/xml · https://data.gov.hr/ckan/api/3/action/package_search?q=prostornih%20jedinica ·
https://data.gov.hr/id/licence/otvorena-dozvola-rh · https://web.dzs.hr/Hrv/about_us/Legals/Open%20Licence.pdf. RO:
https://data.gov.ro/dataset/activity/siruta-2024 · https://data.gov.ro/dataset/coduri-postale-romania ·
https://datahub.io/logistics/postal-codes-ro/.licensing-notes · https://www.posta-romana.ro/cauta-cod-postal.html ·
https://inspire-geoportal.ec.europa.eu/srv/api/records/%7B5DCAFDDB-7729-4655-8C5C-845AE8538BFC%7D · https://data.gov.ro/pages/licence.
BG: https://www.nsi.bg/nrnm/ · https://www.nsi.bg/nrnm/ekatte/index · https://www.nsi.bg/nrnm/ekatte/zip/download?files_type=excel ·
https://www.nsi.bg/nrnm/pages/za-razrabotchitsi · https://www.nsi.bg/nrnm/pages/posledni-promeni ·
https://www.nsi.bg/bg/content/14843/basic-page/лиценз-за-използването-на-статистическата-информация-произвеждана-и-разпространявана-от-националния-статистически-институт ·
https://www.bgpost.bg/postal-services/po-codes. RS: https://data.gov.rs/sr/datasets/adresni-registar/ ·
https://download.geosrbija.rs/download-api/opendata-proxy/export?category=ar&layer=ulica_ar&geometry=true&fileName=ulica_csv&format=csv ·
https://data.gov.rs/api/1/datasets/licenses/ · https://hub.data.gov.rs/en/2026/06/. UA:
https://mininfra.gov.ua/diialnist/rozvytok-mistsevoho-samovriaduvannia/kodyfikator-administratyvno-terytorialnykh-odynyts-ta-terytorii-terytorialnykh-hromad ·
https://mininfra.gov.ua/storage/app/sites/1/uploaded-files/kodifikator-07-07.xlsx ·
https://www.ukrposhta.ua/files/shares/out/postindex.zip · https://www.ukrposhta.ua/files/shares/out/houses.zip. RU:
https://www.nalog.gov.ru/opendata/7707329152-fias/ · https://fias.nalog.ru/opendata/7707329152-fias/data-16102026-structure-20191024.zip ·
https://data.nalog.ru/opendata/7707329152-fias/structure-12032021.zip · https://www.nalog.gov.ru/opendata/ ·
https://data.nalog.ru/files/tipovye_usloviya_od.pdf · https://datahub.io/logistics/postal-codes-ru/.licensing-notes. GR:
https://packagist.org/packages/achilleskal/greek-ota · https://www.statistics.gr/en/statistics/-/publication/SKA01/- ·
https://www.statistics.gr/statistics/-/publication/SMD02/- ·
https://www.gov.gr/org/ktimanet/ktematographese/geopule-geokhorikon-dedomenon-inspire-ellenikou-ktematologiou ·
https://www.statistics.gr/en/pneumatika-diakaiomata-kai-epanachresimopoiese-stoicheion ·
https://www.statistics.gr/documents/20181/1412250/Copyright_Reuse_Policy_EN.pdf · https://datahub.io/logistics/postal-codes-gr/.licensing-notes.

**Southern Europe and the British Isles.** ES:
https://www.ine.es/dyngs/INEbase/es/operacion.htm?c=Estadistica_C&cid=1254736177031&menu=ultiDatos&idp=1254734710990 ·
https://www.ine.es/daco/daco42/codmun/26codmun.xlsx · https://www.ine.es/daco/daco42/codmun/codmun26/26codmunmod.htm ·
https://www.ine.es/daco/daco42/codmun/codmun00i.htm · https://www.ine.es/uc/8ugc9oOn ·
https://ine.es/alteraciones/alteraciones_municipios_metodologia.pdf · https://www.ine.es/dyngs/DAB/es/index.htm?cid=1390 ·
https://www.uv.es/goerlich/Ivie/CodPost · https://www.ine.es/dynt3/metadatos/es/RespuestaDatos.html?oper=189 ·
https://datos.gob.es/es/catalogo/e00125901-spaign-cartociudad-addresses ·
https://centrodedescargas.cnig.es/CentroDescargas/buscadorCatalogo.do?codFamilia=02122 ·
https://www.catastro.hacienda.gob.es/webinspire/index.html · https://ine.es/dyngs/AYU/es/index.htm?cid=125 ·
https://www.ign.es/resources/licencia/Condiciones_licenciaUso_IGN.pdf · http://www.scne.es/tablaProductos.php ·
https://www.catastro.hacienda.gob.es/webinspire/documentos/Licencia.pdf · https://www.idee.es/csw-codsi-idee/srv/api/records/ES_SDGC_AD ·
https://datos.gob.es/eu/solicitud-de-datos/codigos-postales-de-espana. IT:
https://www.istat.it/classificazione/codici-dei-comuni-delle-province-e-delle-regioni/ ·
https://www.istat.it/storage/codici-unita-amministrative/Elenco-comuni-italiani.xlsx ·
https://www.istat.it/wp-content/uploads/2024/09/Codici-statistici-e-denominazioni-delle-unita-amministrative-della-Sardegna.zip ·
https://situas.istat.it/ · https://www.anncsu.gov.it/it/consultazione-dellarchivio/open-data/ ·
https://www.anncsu.gov.it/.allegati/metadata_indirizzario.json · https://www.istat.it/note-legali/ ·
https://www.agenziaentrate.gov.it/portale/documents/20143/8411843/ALLEGATO+-+SPECIFICHE+TECNICHE.pdf/2b3a983e-7c3e-e868-c9b7-c2092f9d9a12?t=1734598098978 ·
https://business.poste.it/business/files/1473808238165/banchedati-faq-cap.pdf. PT:
https://www.dgterritorio.gov.pt/cartografia/cartografia-tematica/caop · https://geo2.dgterritorio.gov.pt/caop/CAOP_Continente_2025-gpkg.zip ·
https://ogcapi.dgterritorio.gov.pt/ · https://dados.gov.pt/datasets/base-nacional-de-moradas · http://inspire.ine.pt/AD/atom/downloadservice.xml ·
https://smi.ine.pt/Versao/Download/9160 · https://smi.ine.pt/Versao/Download/10698 · https://dados.gov.pt/datasets/lugares-censitarios-n-o-3 ·
https://www.ctt.pt/feapl_2/app/open/postalCodeSearch/postalCodeSearch.jspx ·
https://dados.gov.pt/datasets/carta-administrativa-oficial-de-portugal-caop2025-continente. IE:
https://data-osi.opendata.arcgis.com/datasets/osi::townlands-national-statutory-boundaries-ungeneralised-2024 ·
https://data-osi.opendata.arcgis.com/datasets/osi::townlands-national-placenames-gazetteer · https://docs.gaois.ie/en/data/logainm/v1.0/api ·
https://docs.gaois.ie/en/data/getting-started · https://data.gov.ie/api/3/action/package_search?q=logainm&rows=3 ·
https://www.eircode.ie/business/products-and-services · https://www.eircode.ie/files/eircode-pricing-information.pdf ·
https://www.eircode.ie/legal · https://www.arcgis.com/sharing/rest/content/items/795b12f697634a759633336d28ebdcd5?f=json. GB:
https://www.ordnancesurvey.co.uk/products/os-open-names · https://docs.os.uk/os-downloads/products/addresses-and-names-portfolio/os-open-names ·
https://geoportal.statistics.gov.uk · https://www.ons.gov.uk/methodology/geography/geographicalproducts/namescodesandlookups/codehistorydatabasechd ·
https://www.ons.gov.uk/methodology/geography/licences ·
https://www.ons.gov.uk/methodology/geography/ukgeographies/administrativegeography/england ·
https://www.gov.uk/government/publications/open-standards-for-government/identifying-property-and-street-information. MT:
https://address.gov.mt/ · https://address.gov.mt/wp-json/gov/v1/local-councils ·
https://haleconnect.com/ows/services/org.1261.27899a9f-4361-4e9c-a73e-148512500919_wfs?SERVICE=WFS&Request=GetCapabilities ·
https://www.maltapost.com/postcode/.

**Americas and Oceania.** US: https://www.census.gov/geographies/mapping-files/time-series/geo/tiger-line-file.html ·
https://www2.census.gov/geo/tiger/TIGER2026/ · https://www.census.gov/cgi-bin/geo/shapefiles/index.php ·
https://catalog.data.gov/dataset/tiger-line-shapefile-2023-county-guam-gu-address-range-feature ·
https://meta.geo.census.gov/data/existing/decennial/GEO/GPMB/TIGERline/Archived_19115/tl_2023_66010_addrfeat.shp.iso.xml ·
https://www.usgs.gov/us-board-on-geographic-names/download-gnis-data ·
https://prd-tnm.s3.amazonaws.com/?prefix=StagedProducts/GeographicNames/&delimiter=/ · https://www.transportation.gov/NAD ·
https://data.transportation.gov/download/fc2s-wawr/application/x-zip-compressed · https://data.transportation.gov/api/views/fc2s-wawr.json ·
https://www.census.gov/programs-surveys/geography/technical-documentation/county-changes.html ·
https://census.gov/programs-surveys/geography/technical-documentation/county-changes.2020.html ·
https://www.census.gov/geographies/reference-files/time-series/geo/bas/new-annex.html ·
https://www.census.gov/programs-surveys/bas/geographies/reference-files.html · https://huduser.gov/apps/public/uspscrosswalk/home ·
https://pypi.org/project/hudpy/ · https://webharvest.gov/peth04/20041031034806/http://www.usps.gov/ncsc/products/products.htm ·
https://postalpro.usps.com/resources-list/fact_sheets · https://pe.usps.com/text/pub28/28c2_006.htm. CA:
https://www.statcan.gc.ca/en/subjects/standard/sgc/2021/introduction · https://www150.statcan.gc.ca/n1/pub/92f0009x/92f0009x2025001-eng.htm ·
https://open.canada.ca/data/en/dataset/9260360b-bf21-436d-bd59-ac050cdd74f6 ·
https://www12.statcan.gc.ca/census-recensement/2011/geo/RNF-FRR/index-s-eng.cfm?year=25 ·
https://natural-resources.canada.ca/maps-tools-publications/maps/geographical-names-canada/download-geographical-names-data ·
https://www.statcan.gc.ca/en/lode/databases/oda · https://open.canada.ca/data/en/dataset/3d282116-e556-400c-9306-ca1a3cada77f ·
https://www150.statcan.gc.ca/n1/en/catalogue/92-179-X2021001 · https://www.statcan.gc.ca/en/reference/licence ·
https://open.canada.ca/en/open-government-licence-canada · https://www.cippic.ca/news/canada-post-v-geolytica-lawsuit-settled. MX:
https://www.inegi.org.mx/temas/mg/ · https://www.inegi.org.mx/app/ageeml/ · https://www.inegi.org.mx/servicios/catalogoUnico.html ·
https://gaia.inegi.org.mx/wscatgeo/v2/ · http://www.correosdemexico.gob.mx/lservicios/servicios/CodigoPostal_Exportar.aspx ·
https://datos.gob.mx/busca/dataset/catalogo-nacional-de-codigos-postales ·
https://www.snieg.mx/Documentos/Normatividad/Vigente/Norma_Tecnica_sobre_Domicilios_Geograficos.pdf · https://www.inegi.org.mx/inegi/terminos.html ·
https://postalcodes-mexico.readthedocs.io/en/stable/readme.html · https://packagist.org/packages/eclipxe/sepomexphp. BR:
https://geoftp.ibge.gov.br/organizacao_do_territorio/estrutura_territorial/divisao_territorial/ ·
https://servicodados.ibge.gov.br/api/docs/localidades · https://servicodados.ibge.gov.br/api/v1/localidades/ ·
https://ftp.ibge.gov.br/Cadastro_Nacional_de_Enderecos_para_Fins_Estatisticos/Censo_Demografico_2022/. AU:
https://data.gov.au/data/dataset/19432f89-dc3a-4ef3-b943-5326ef1dbecc · https://geoscape.com.au/data/g-naf/ ·
https://docs.geoscape.com.au/en/stable/gnaf.html · https://github.com/minus34/gnaf-loader ·
https://docs.geoscape.com.au/projects/gnafcore_info/en/stable/ · https://geoscape.com.au/request-g-naf-core/ ·
https://geoscape.com.au/data/administrative-boundaries/ ·
https://www.abs.gov.au/statistics/standards/australian-statistical-geography-standard-asgs-edition-3/jul2021-jun2026 ·
https://placenames.fsdf.org.au/ ·
https://data.gov.au/data/dataset/19432f89-dc3a-4ef3-b943-5326ef1dbecc/resource/09f74802-08b1-4214-a6ea-3591b2753d30/ ·
https://www.abs.gov.au/website-privacy-copyright-and-disclaimer. NZ: https://data.linz.govt.nz/services/api/v1.x/layers/123113/ ·
https://data.linz.govt.nz/services/api/v1.x/layers/123110/ · https://data.linz.govt.nz/services/api/v1.x/layers/113764/ ·
https://data.linz.govt.nz/services/api/v1.x/layers/51681/ · https://datafinder.stats.govt.nz/services/api/v1.x/layers/123497/ ·
https://www.linz.govt.nz/products-services/data/licensing-and-using-data. AR/CL: https://datosgobar.github.io/georef-ar-api/ ·
https://apis.datos.gob.ar/georef/api/ · https://github.com/datosgobar/georef-ar-api ·
https://www.ine.gob.cl/terminos-de-uso-y-licencia-de-datos-abiertos.

**Asia and the Middle East.** JP: https://www.post.japanpost.jp/service/search/zipcode/download/readme.html ·
https://www.post.japanpost.jp/zipcode/dl/roman-zip.html · https://www.post.japanpost.jp/service/search/zipcode/download/readme_ro.html ·
https://www.digital.go.jp/policies/base_registry_address · https://dataset.address-br.digital.go.jp/ ·
https://www.digital.go.jp/policies/base_registry_address_tos · https://www.soumu.go.jp/denshijiti/code.html ·
https://www.e-stat.go.jp/municipalities/cities/areacode. TW: https://data.gov.tw/dataset/152276 · https://data.gov.tw/dataset/158970 ·
https://data.gov.tw/en/datasets/53685 · https://data.gov.tw/license. HK: https://data.gov.hk/en-data/dataset/hk-dpo-als_01-als ·
https://www.als.ogcio.gov.hk/docs/Data_Dictionary_for_ALS_EN.pdf · https://data.gov.hk/en/terms-and-conditions. IN:
https://www.data.gov.in/resource/all-india-pincode-directory-till-last-month ·
https://data.gov.in/catalog/all-india-pincode-directory-through-webservice. IL:
https://old.hebrew-academy.org.il/wp-content/uploads/רשימת-שמות-היישובים-יולי-2022.pdf. SG: https://data.gov.sg/open-data-licence ·
https://www.onemap.gov.sg/legal/termsofuse.html. KR, TH and CN: no page could be read (search results only).

**Global sources.** GeoNames: https://download.geonames.org/export/dump/ · https://download.geonames.org/export/dump/readme.txt ·
https://download.geonames.org/export/zip/readme.txt · https://www.geonames.org/export/codes.html ·
https://forum.geonames.org/gforum/posts/list/41065.page. Wikidata: https://www.wikidata.org/wiki/Wikidata:Copyright ·
https://www.wikidata.org/wiki/Property:P131 · https://www.wikidata.org/wiki/Q817375 · https://www.wikidata.org/wiki/Q2785216 ·
https://www.wikidata.org/wiki/Help:Merge. OSM: https://www.openstreetmap.org/copyright ·
https://osmfoundation.org/wiki/Licence/Attribution_Guidelines ·
https://osmfoundation.org/wiki/Licence/Community_Guidelines/Substantial_-_Guideline ·
https://osmfoundation.org/wiki/Licence/Community_Guidelines/Geocoding_-_Guideline ·
https://osmfoundation.org/wiki/Licence/Community_Guidelines/Collective_Database_Guideline_Guideline ·
https://wiki.openstreetmap.org/wiki/Multilingual_names · https://wiki.openstreetmap.org/wiki/Template:Admin_level ·
https://wiki.openstreetmap.org/wiki/Key:name · https://wiki.openstreetmap.org/wiki/OpenHistoricalMap/Tags. Who's On First:
https://whosonfirst.org/docs/licenses/ · https://github.com/whosonfirst-data/whosonfirst-data/blob/master/LICENSE.md ·
https://whosonfirst.org/docs/properties/wof/ · https://whosonfirst.org/docs/processes/wof-life-cycle/ ·
https://whosonfirst.org/docs/processes/significant-event/ · https://whosonfirst.org/docs/hierarchies/ ·
https://whosonfirst.org/docs/properties/name/. Overture and OpenAddresses: https://docs.overturemaps.org/attribution/ ·
https://github.com/openaddresses/openaddresses · https://geocode.earth/docs/reference/data_sources/. CLDR:
https://www.unicode.org/license.txt · https://cldr.unicode.org/downloads/cldr-42 · https://cldr.unicode.org/downloads/cldr-46 ·
https://cldr.unicode.org/downloads/cldr-47 · https://cldr.unicode.org/downloads/cldr-49. Codes:
https://www.iana.org/assignments/language-subtag-registry · https://www.iana.org/help/licensing-terms ·
https://salsa.debian.org/iso-codes-team/iso-codes · https://iso639-3.sil.org/code_tables/download_tables. Others:
https://unlocode.unece.org/ · https://geonames.nga.mil/ · https://www.naturalearthdata.com/about/terms-of-use/ ·
https://www.geoboundaries.org/ · https://github.com/google/libaddressinput/wiki/AddressValidationMetadata ·
https://www.gstatic.com/chrome/autofill/libaddressinput/chromium-i18n/ssl-address/data/BE ·
https://www.upu.int/en/postal-solutions/programmes-services/addressing-solutions.

**History models.** INSEE, BFS, WOF, GeoNames and Wikidata pages as listed above; INSPIRE:
https://inspire.ec.europa.eu/schemas/au/4.0/AdministrativeUnits.xsd · https://inspire.ec.europa.eu/schemas/ad/4.0/Addresses.xsd ·
https://inspire.ec.europa.eu/codelist/StatusValue/StatusValue.en.json.

**Romanization, language tags, Unicode, .NET.** https://arhiiv.eki.ee/wgrs/resolutions.htm · https://arhiiv.eki.ee/wgrs/ ·
https://unstats.un.org/unsd/geoinfo/ungegn/docs/pubs/Glossary_add_en.pdf · https://www.rfc-editor.org/rfc/rfc5646.html ·
https://www.rfc-editor.org/rfc/rfc6497.html · https://raw.githubusercontent.com/unicode-org/cldr/main/common/bcp47/transform.xml ·
https://github.com/unicode-org/cldr/blob/main/common/bcp47/transform.xml ·
https://github.com/unicode-org/cldr-json/blob/main/cldr-json/cldr-core/supplemental/likelySubtags.json ·
https://cldr.unicode.org/index/cldr-spec/transliteration-guidelines · https://www.unicode.org/reports/tr38/ ·
https://www.unicode.org/Public/UCD/latest/ucd/CaseFolding.txt · https://www.unicode.org/versions/Unicode18.0.0/core-spec/chapter-6/ ·
https://docs.mapbox.com/data/tilesets/reference/mapbox-streets-v8/ · https://learn.microsoft.com/en-us/dotnet/core/extensions/globalization-icu ·
https://github.com/dotnet/runtime/blob/main/docs/design/features/globalization-invariant-mode.md ·
https://www.nuget.org/packages/ICU4N.Transliterator · https://github.com/NightOwl888/ICU4N · https://github.com/anyascii/anyascii.

**RavenDB.** https://ravendb.net/buy (re-fetched 2026-10-17) · https://docs.ravendb.net/7.1/indexes/search-engine/corax ·
https://docs.ravendb.net/7.2/indexes/search-engine/corax · https://docs.ravendb.net/7.2/indexes/using-analyzers ·
https://docs.ravendb.net/7.2/client-api/session/querying/text-search/starts-with-query ·
https://docs.ravendb.net/7.2/client-api/session/querying/text-search/full-text-search · https://docs.ravendb.net/7.2/indexes/boosting ·
https://docs.ravendb.net/7.2/client-api/session/querying/sort-query-results ·
https://docs.ravendb.net/7.2/client-api/bulk-insert/how-to-work-with-bulk-insert-operation ·
https://docs.ravendb.net/7.2/client-api/smuggler/what-is-smuggler · https://docs.ravendb.net/7.2/server/ongoing-tasks/etl/raven ·
https://docs.ravendb.net/7.2/server/ongoing-tasks/external-replication ·
https://docs.ravendb.net/7.2/client-api/how-to/setup-aggressive-caching ·
https://docs.ravendb.net/6.0/client-api/rest-api/queries/query-the-database/ ·
https://docs.ravendb.net/7.2/document-extensions/revisions/overview.

**Caveats on sources.** Several official hosts refused automated fetching (§2.1), and the shared web-search budget ran out part
of the way through each external pass; claims behind those hosts, or taken from search snippets, Wikipedia or memory, are
marked [unverified]. Wikipedia supplied the Ooike split geometry, the Bevere date and the facility-municipality list; REFNIS
supplied every Belgian date and count it could. Secondary inventories (Overture's attribution page, OpenAddresses records,
datahub.io licensing notes, launify.com) were used as leads, not as licence sources. Licence readings are the researchers' and
need a human read before go-live, as the world-sources pass recommended for every red flag.

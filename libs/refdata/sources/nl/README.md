# MintPlayer.Spark.ReferenceData.Sources.Netherlands

The Dutch reference-data adapter (docs/reference_data_PRD.md §4.4, plan R8): CBS provinces and gemeenten with their
herindelingen since 1991, and the BAG's woonplaatsen, streets and postcodes, with the Dutch country profile, the id
ledger and real sample fixtures. It replaces the global baseline's hand-written NL profile.

```csharp
spark.AddReferenceData().AddNetherlands();
```

## Sources

| Id | Dataset | Licence | Default schedule (UTC) | Change check |
|---|---|---|---|---|
| `nl-bag` | Kadaster LVBAG extract via the [PDOK Atom feed](https://service.pdok.nl/lv/bag/atom/bag.xml) → `lvbag-extract-nl.zip` (3.64 GB, made monthly on the 8th) | CC0 1.0 (feed) / Public Domain Mark (Kadaster) | `0 0 5 10 * *` | the feed entry's `updated` date |
| `nl-cbs-gemeenten` | CBS OData table [86247NED](https://opendata.cbs.nl/ODataApi/odata/86247NED) "Gebieden in Nederland 2026" | CC BY 4.0 | `0 30 5 1 * *` | the table's `Modified` (TableInfos) |
| `nl-cbs-herindelingen` | CBS [gemeentelijke indelingen per jaar](https://www.cbs.nl/nl-nl/onze-diensten/methoden/classificaties/overig/gemeentelijke-indelingen-per-jaar), the table of each year page since 1991 | CC BY 4.0 | `0 45 5 1 * *` | our SHA-256 of the tables (CBS sends no validator) |

Credit lines: "Bron: Kadaster, Basisregistratie Adressen en Gebouwen" (CC0/PDM, credited anyway); "Bron: CBS", CC BY 4.0,
with a statement that the data was modified.

## Catches

- **Only the monthly extract is free.** The daily and monthly mutation files are paid (€14.50/month + €219 setup; daily
  €219/month), so they are unused and the schedule is monthly. The extract is a ZIP of ZIPs; only `9999WPL*`,
  `9999OPR*`, `9999NUM*` and `GEM-WPL-RELATIE-*` are unpacked (dated names, matched by pattern). Their XML is streamed
  (one `bagObject` at a time, DTDs prohibited, no resolver).
- **Licence labels differ**: CC0 in the PDOK feed, the Public Domain Mark at Kadaster; CBS is CC BY 4.0.
- **The BAG registers no gemeenten.** Kadaster's gemeente–woonplaats relation file (not an official BAG product) puts a
  woonplaats under its gemeente over time; adjacent periods with one gemeente (the 2010 registration slivers) are joined.
- **CBS values are space-padded** (`"GM1979    "`) and the OData table id changes every year (`86247NED` = 2026).
- **Herindeling tables** come in two HTML layouts (figure table; Word table with spacer rows). A gemeente that absorbs
  others is listed as both ended and new (Groningen 2019): that is an *absorb*; a new code is a *merge* (Eemsdelta 2021).
  The ended gemeente's province comes from the table's abbreviation (`Gr.`), never from code digits. Herindelingen are
  dated 1 January of the page's year. ⚠️ The markup of the pages before 2019 is unverified (RS8).
- **BAG ids embed the issuing gemeente's code** (`0003…` for Appingedam) and survive mergers: never read as hierarchy.
- **Postcodes are PC6** (`9901EA`), taken from current nummeraanduidingen; no house number is kept (RD5).
- EPSG:28992 geometries are not read; woonplaatsen and streets are known since the BAG's start (2009–2011), gemeenten
  since 1991 (CBS).

## Fixtures (real samples, RS8; cut 2026-10-19)

| File | Cut from | Rule |
|---|---|---|
| `9999WPL08102026-000001.cut.xml` | `lvbag-extract-nl.zip` of 2026-10-08, inner `9999WPL08102026.zip` (range request) | woonplaatsen 3386 Appingedam, 3234 Delfzijl, 3449 Loppersum, 3241 Spijk (named "Spijk Gn" from 2011 to 2017), 3458 't Zandt, every voorkomen; `geometrie` elements removed (not read) |
| `GEM-WPL-RELATIE-08102026-000001.cut.xml` | same, inner `GEM-WPL-RELATIE-08102026.zip` | the relations of those woonplaatsen (0003/0010/0024 until 2021, 1979 after) |
| `9999OPR08102026-000001.cut.xml` | same, inner `9999OPR08102026.zip` | 4 current roads (`Weg`) of Appingedam with nummeraanduidingen, 2 of each other woonplaats |
| `9999NUM08102026-000001.cut.xml` | same, the first part of inner `9999NUM08102026.zip` (partially inflated) | up to 2 current nummeraanduidingen per distinct postcode of those Appingedam roads |
| `cbs-86247NED-gemeenten.cut.json` | CBS OData `86247NED/TypedDataSet` (2026-10-19) | GM1979 Eemsdelta and GM0014 Groningen, with province PV20 |
| `cbs-herindelingen-2019.cut.html`, `cbs-herindelingen-2021.cut.html` | the CBS pages "Gemeentelijke indeling op 1 januari 2019 / 2021" | the table, with the header rows and the rows of Groningen/Haren/Ten Boer (2019) and Eemsdelta (2021) |

Ledger: `Ledger/nl.ndjson`, filled by `--refdata-build-seed` (docs/guide-reference-data.md).

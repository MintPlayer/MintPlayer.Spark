# MintPlayer.Spark.ReferenceData.Abstractions

The shared contract of the reference-data packages (`docs/reference_data_PRD.md`): the document types (`RefCountry` with its
profile, `RefPlace` with dated periods, names and codes, `RefPostalCode`, `RefStreet`, `RefLineageEvent`, `RefTimeZone`,
`PublicHoliday`, `VatRateSuggestion`), the id grammar (`RefIds`), integer date keys (`DateKeys`), as-of resolution (`RefAsOf`),
the display rule (`PlaceNames`), the search-key pipeline (`RefSearchKeys`), the canonical release format (`Releases/`), the
`/api/v1` response contracts and the `Address` value object. No ASP.NET Core: an entity library can reference it.

Includes a pinned Unicode CLDR territory-name subset (`Cldr/`, Unicode License v3). See `docs/guide-reference-data.md`.

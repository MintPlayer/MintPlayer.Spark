# MintPlayer.Spark.ReferenceData.Client

The consumer side (`docs/guide-reference-data-client.md`): `spark.AddReferenceDataClient()` binds `Spark:ReferenceData:Client`
(`BaseAddress` has no default: unset is free-text mode), registers the typed `/api/v1` client, the `Address` before-save
behaviour (validation and rendered lines from the country profile, time zone from the municipality), the house-number
hook with the BeST validator, the release sync, and the `ISparkTimeZoneNameProvider` that names zones with CLDR exemplar
cities (commerce D36 step 1). It never references the instance package.

# MintPlayer.Spark.ReferenceData

A reference-data instance for MintPlayer.Spark (`docs/reference_data_PRD.md`, `docs/guide-reference-data.md`): `spark.AddReferenceData()`
adds the id ledger, the derive pass (segments, lineage edges, search keys, rank, hash), the scheduled change-checked import
(`Spark:ReferenceData:Sources:{id}`), deterministic releases, the basic seed set, the read-only admin pages and the anonymous
`/api/v1` API (CORS on that group only, no XSRF cookie, a per-address token bucket, ETag 304s). Adapters plug in as
`MintPlayer.Spark.ReferenceData.Sources.*` packages (`.AddBelgium()`, `.AddGlobalBaseline()`, …).

# MintPlayer.Spark.Model

The model vocabulary an entity library uses besides attributes: `TranslatedString`,
`TransientLookupReference` / `DynamicLookupReference` / `ELookupDisplayType`, `EShowedOn`,
`IHasNaturalId`, `ValidationError`, the reserved action names (`SparkCoreActions`,
`SparkCombinedActions`, `[SparkReservedActions]`), and the small registries that generated code in a
library writes into (`SparkValueObjects`, `SparkModelSatellites`).

It is a plain `Microsoft.NET.Sdk` package with no dependencies, so a project that holds only entities
can reference it **without** pulling in the ASP.NET Core shared framework (#388). Pair it with
`MintPlayer.Spark.Attributes`. The application references `MintPlayer.Spark`, which brings both in.

## The namespace is deliberately not the package name

Every type keeps the namespace `MintPlayer.Spark.Abstractions` (or `.Abstractions.Authorization` /
`.Abstractions.Model`), where it lived before the split. Do not "fix" that:

- About 40 fully qualified metadata names in the Spark source generators and analyzers identify these
  types by namespace (`GetTypeByMetadataName("MintPlayer.Spark.Abstractions.…")`).
- Generated code emits `using MintPlayer.Spark.Abstractions;` and `global::MintPlayer.Spark.Abstractions.…`
  names into consuming projects.

Moving a type to a new *assembly* is invisible to both. Moving it to a new *namespace* breaks them,
silently in the generators' case: a type that does not resolve just switches a generator off.

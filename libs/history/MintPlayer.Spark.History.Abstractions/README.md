# MintPlayer.Spark.History.Abstractions

`IAuditable`: the interface an entity implements to have `MintPlayer.Spark.History` stamp
`CreatedBy` / `CreatedAt` / `ModifiedBy` / `ModifiedAt` on every write.

It is a plain `Microsoft.NET.Sdk` package with no dependencies, so an entity library can opt in
**without** referencing the framework or the ASP.NET Core shared framework (#388). The application
references `MintPlayer.Spark.History`, which references this package and does the stamping.

The namespace stays `MintPlayer.Spark.History`, where the interface lived before the split, so no
consuming code changes.

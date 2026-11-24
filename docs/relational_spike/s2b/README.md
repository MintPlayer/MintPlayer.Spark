# #536 spike S2b: the real `QueryExecutor` over an EF Core prototype of the storage seam

Results and decisions: `docs/relational_providers_PRD.md` §8.10.

`EfSpark.cs` is a throwaway EF Core implementation of `ISparkStore`, `ISparkSession` and `ISparkQueryProvider`: a
`DbContext` built from a hand-written `EfSparkModel` (D1 tables and TPH, D25 child tables, D26 FKs and join tables, O13
stored get-only members). `S2bTests.cs` starts the real Spark host through `SparkEndpointFactory`, swaps those three
services in, and runs `IQueryExecutor` against SQL Server and PostgreSQL.

There is no `.csproj` here on purpose, because `@nx/dotnet` would infer a project from it. To rerun:

1. Copy both `.cs` files to a scratch folder.
2. Add a test csproj next to them: `net11.0`, `ImplicitUsings` and `Nullable` enabled,
   `<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>`, `<EnableSpaBuilder>false</EnableSpaBuilder>`;
   packages `Microsoft.EntityFrameworkCore.SqlServer` 11.0.0, `Npgsql.EntityFrameworkCore.PostgreSQL` 11.0.0,
   `Microsoft.NET.Test.Sdk`, `xunit` 2.9.3, `xunit.runner.visualstudio`, `MintPlayer.Assertions`; global usings `Xunit` and
   `MintPlayer.Assertions`; a `ProjectReference` to `libs/testing/MintPlayer.Spark.Testing`.
3. Set `RAVENDB_LICENSE` (the test host still starts RavenDB's embedded server) and `SPARK536_PG_PASSWORD`, then
   `dotnet test`. SQL Server: Windows authentication on `localhost\MSSQLSERVER01`; PostgreSQL: `127.0.0.1:55432`.

The tests drop and recreate their own `sparkspike536s2b` database on each server. Set `S2B_LOG=1` to write the SQL
each test ran to `sql-<provider>.log` next to the binaries.

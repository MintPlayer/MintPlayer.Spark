# #536 spikes S2b and S4: Spark's own query, save and delete paths over an EF Core prototype of the storage seam

Results and decisions: `docs/relational_providers_PRD.md` §8.10 (S2b) and §8.11 (S4).

`EfSpark.cs` is a throwaway EF Core implementation of `ISparkStore`, `ISparkSession` and `ISparkQueryProvider`. It
builds a `DbContext` from a hand-written `EfSparkModel`:
- D1 tables and TPH
- D25 child tables
- D26 FKs and join tables
- O13 stored get-only members
- D3 versions
- D7 binary key collations
- D30 constraint-error translation

`S2bTests.cs` (S2b) and `S4Tests.cs` (S4) start the real Spark host through `SparkEndpointFactory` and swap those three
services in. They then run `IQueryExecutor` and `IDatabaseAccess` against SQL Server and PostgreSQL.

There is no `.csproj` here on purpose, because `@nx/dotnet` would infer a project from it. To rerun:

1. Copy the three `.cs` files to a scratch folder.
2. Add a test csproj next to them:
   - `net11.0`, with `ImplicitUsings` and `Nullable` enabled
   - `<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>` and `<EnableSpaBuilder>false</EnableSpaBuilder>`
   - packages `Microsoft.EntityFrameworkCore.SqlServer` 11.0.0, `Npgsql.EntityFrameworkCore.PostgreSQL` 11.0.0,
     `Microsoft.NET.Test.Sdk`, `xunit` 2.9.3, `xunit.runner.visualstudio` and `MintPlayer.Assertions`
   - global usings `Xunit` and `MintPlayer.Assertions`
   - a `ProjectReference` to `libs/testing/MintPlayer.Spark.Testing`
3. Set `RAVENDB_LICENSE` (the test host still starts RavenDB's embedded server) and `SPARK536_PG_PASSWORD`, then run
   `dotnet test`. SQL Server uses Windows authentication on `localhost\MSSQLSERVER01`; PostgreSQL is `127.0.0.1:55432`.

The tests drop and recreate their own `sparkspike536s2b` database on each server. Set `S2B_LOG=1` to write the SQL
each test ran to `sql-<provider>.log` next to the binaries. The German-language SQL Server test also writes the raw
error messages it parsed to `sql-sqlserver-german.log`.

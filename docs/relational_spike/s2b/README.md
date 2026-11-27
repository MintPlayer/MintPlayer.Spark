# #536 spikes S2b, S4 and S5: Spark's own query, save and delete paths, and neutral views, over an EF Core prototype of the storage seam

Results and decisions: `docs/relational_providers_PRD.md` §8.10 (S2b), §8.11 (S4) and §8.12 (S5).

`EfSpark.cs` is a throwaway EF Core implementation of `ISparkStore`, `ISparkSession` and `ISparkQueryProvider`. It
builds a `DbContext` from a hand-written `EfSparkModel`:
- D1 tables and TPH
- D25 child tables
- D26 FKs and join tables
- O13 stored get-only members
- D3 versions
- D7 binary key collations
- D30 constraint-error translation

`S5Views.cs` adds D32's neutral view declaration (`SparkView<TRow>`, with `Set<T>()` and `Load<T>(id)`). It has
renderers for EF, LINQ to objects and RavenDB, and `SparkViewDiffer`, which puts the views into migrations.

`S2bTests.cs` (S2b), `S4Tests.cs` (S4) and `S5Tests.cs` (S5) start the real Spark host through `SparkEndpointFactory`
and swap those three services in, plus an index catalog that knows the view. They then run `IQueryExecutor` and
`IDatabaseAccess` against SQL Server and PostgreSQL. The S5 RavenDB case deploys the rendered index to the test
driver's embedded server. Each test creates its schema from the migration operations, not `EnsureCreated`.

There is no `.csproj` here on purpose, because `@nx/dotnet` would infer a project from it. To rerun:

1. Copy the five `.cs` files to a scratch folder.
2. Add a test csproj next to them:
   - `net11.0`, with `ImplicitUsings` and `Nullable` enabled
   - `<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>` and `<EnableSpaBuilder>false</EnableSpaBuilder>`
   - packages `Microsoft.EntityFrameworkCore.SqlServer` 11.0.0, `Npgsql.EntityFrameworkCore.PostgreSQL` 11.0.0,
     `Microsoft.EntityFrameworkCore.Design` 11.0.0 (S5's scaffolding case),
     `Microsoft.NET.Test.Sdk`, `xunit` 2.9.3, `xunit.runner.visualstudio` and `MintPlayer.Assertions`
   - global usings `Xunit` and `MintPlayer.Assertions`
   - a `ProjectReference` to `libs/testing/MintPlayer.Spark.Testing`
3. Set `RAVENDB_LICENSE` (the test host still starts RavenDB's embedded server) and `SPARK536_PG_PASSWORD`, then run
   `dotnet test`. SQL Server uses Windows authentication on `localhost\MSSQLSERVER01`; PostgreSQL is `127.0.0.1:55432`.

The tests drop and recreate their own `sparkspike536s2b` database on each server. Set `S2B_LOG=1` to write the SQL
each test ran to `sql-<provider>.log` next to the binaries. The German-language SQL Server test also writes the raw
error messages it parsed to `sql-sqlserver-german.log`. S5 writes:
- the view SQL: `view-<provider>.sql` and `view-ravendb.txt`
- the scaffolded migration and snapshot: `migration-<provider>.cs.txt` and `snapshot-<provider>.cs.txt`
- what the `ALTER` under the view did: `alter-under-view-<provider>.txt`

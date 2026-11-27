# #536 spike S19: applying migrations with several instances, failures and scripts

Results and decisions: `docs/relational_providers_PRD.md` §8.7 (and D33).

Throwaway code. There is no `.csproj` here on purpose, because `@nx/dotnet` would infer a project from it. To rerun:

1. Copy `Program.cs` to a scratch folder.
2. Add a console csproj next to it: `net11.0`, `ImplicitUsings` and `Nullable` enabled,
   `<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>`, and
   `Microsoft.EntityFrameworkCore.SqlServer` 11.0.0 plus `Npgsql.EntityFrameworkCore.PostgreSQL` 11.0.0.
3. Run `dotnet run -- sqlserver` (Windows authentication on `localhost\MSSQLSERVER01`) or `dotnet run -- postgres`
   (`127.0.0.1:55432`, password from the `SPARK536_PG_PASSWORD` environment variable).

The spike drops and recreates its own `sparkspike536mig*` databases. Set `S19_LOG=1` to write EF's SQL to `sql.log`.
Scenarios 1 and 1b use EF's own migration lock and are expected to fail (they are the findings); 1d adds the
Npgsql workaround (`AdvisoryLockHistoryRepository`, npgsql/efcore.pg#4162) and passes; 1c is Spark's guard.

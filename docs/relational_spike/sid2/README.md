# #536 spike S-ID2: `Id<T>` on EF Core 11

Results and decisions: `docs/relational_providers_PRD.md` §8.4.

Throwaway code. There is no `.csproj` here on purpose, because `@nx/dotnet` would infer a project from it. To rerun:

1. Copy `Program.cs` to a scratch folder.
2. Add a console csproj next to it: `net11.0`, `ImplicitUsings` and `Nullable` enabled, with these packages:
   - `Microsoft.EntityFrameworkCore.SqlServer` 11.0.0
   - `Microsoft.EntityFrameworkCore.InMemory` 11.0.0
   - `Npgsql.EntityFrameworkCore.PostgreSQL` 11.0.0
3. Run the shape checks: `dotnet run -- sqlserver` (SQL Server 2025 at `localhost\MSSQLSERVER01`),
   `sqlserver2022` (`localhost`), `postgres` (the `spark536-pg` container on 127.0.0.1:55432) or `inmemory`.
4. Run the Guid insert cost: `dotnet run -- perf-sqlserver 100000` or `perf-postgres 100000`.

SQL Server uses Windows authentication. For PostgreSQL, set `SPARK536_PG_PASSWORD` to the container's password.
The spike drops and recreates the databases `sparkspike536id` and `sparkspike536perf`.

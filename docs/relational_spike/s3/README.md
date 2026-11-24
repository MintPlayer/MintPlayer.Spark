# #536 spike S3: synchronize generates EF migrations from a model built from metadata

Results and decisions: `docs/relational_providers_PRD.md` §8.8 (and D24, O17).

Throwaway code. There is no `.csproj` here on purpose, because `@nx/dotnet` would infer a project from it. To rerun:

1. Copy `Program.cs` and `run.sh` to a scratch folder.
2. Add a console csproj next to them: `net11.0`, `ImplicitUsings` and `Nullable` enabled, `RootNamespace` `S3`,
   `<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>`, and
   `Microsoft.EntityFrameworkCore.SqlServer`, `Npgsql.EntityFrameworkCore.PostgreSQL` and
   `Microsoft.EntityFrameworkCore.Design` 11.0.0 (Design without `PrivateAssets`: the scaffolding runs in the app).
3. Run `bash run.sh` (SQL Server on `localhost\MSSQLSERVER01` with Windows authentication; PostgreSQL on
   `127.0.0.1:55432`, password from `SPARK536_PG_PASSWORD`; `run.sh` reads it from the S1 spike's `Program.cs`).

`run.sh` resets both `sparkspike536s3` databases and the `Migrations/` folder, then for each model version runs
`gen n` (scaffold into `Migrations/<Provider>/`) and `verify n` in separate processes, so each verify runs on a build
that compiled the migration, as a developer's next build does. The generated migrations are not committed.

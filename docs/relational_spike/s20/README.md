# #536 spike S20: Spark commands as keyed DI registrations

Results and decisions: `docs/relational_providers_PRD.md` §8.6 (and D34).

Throwaway code. There is no `.csproj` here on purpose, because `@nx/dotnet` would infer a project from it. To rerun:

1. Copy `Program.cs` to a scratch folder.
2. Add a csproj next to it with `Sdk="Microsoft.NET.Sdk.Web"`: `net11.0`, `ImplicitUsings` and `Nullable` enabled,
   `<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>`, and
   `Microsoft.EntityFrameworkCore.SqlServer` plus `Microsoft.EntityFrameworkCore.Design` 11.0.0.
3. Run `dotnet run`. No database is needed: the `DbContext` points at an unreachable address on purpose, and the
   spike counts every connection attempt.

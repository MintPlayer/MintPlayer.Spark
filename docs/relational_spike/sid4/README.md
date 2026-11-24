# #536 spike S-ID4: mixed-case legacy ids on a copy of a Coverage database

Results and decisions: `docs/relational_providers_PRD.md` §8.5.

Throwaway code. There is no `.csproj` here on purpose, because `@nx/dotnet` would infer a project from it. To rerun:

1. Copy `Program.cs` to a scratch folder.
2. Add a console csproj next to it: `net11.0`, `ImplicitUsings` and `Nullable` enabled, and
   `<PackageReference Include="RavenDB.Client" Version="7.2.7" />`.
3. Run `dotnet run -- [sourceDatabase]` against the unsecured dev server on `http://localhost:8080`. The default
   source is `Coverage`.

The source database is only **exported**. The spike imports it into `spike_sid4` (dropped and recreated first), and
every check, including the saves, runs on that copy. To check a production export, import it into the dev server
under its own name and pass that name. Never point the spike at a production server.

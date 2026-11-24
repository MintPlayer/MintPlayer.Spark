# #536 spike S-ID1 (+ S-ID5's RavenDB half): `Id<T>` on RavenDB 7.2

Results and decisions: `docs/relational_providers_PRD.md` §8.3.

Throwaway code. There is no `.csproj` here on purpose, because `@nx/dotnet` would infer a project from it. To rerun:

1. Copy `Program.cs` to a scratch folder.
2. Add a console csproj next to it: `net11.0`, `ImplicitUsings` and `Nullable` enabled, and
   `<PackageReference Include="RavenDB.Client" Version="7.2.7" />`.
3. Start a RavenDB 7.2 server on `http://localhost:8080` (unsecured dev server).
4. Run `dotnet run`.

The spike drops and recreates the database `spike_sid1`. It prints one line per check and a summary. 46 of 51
checks pass. The 5 failures are expected negatives, kept as evidence: four raw `Contains` queries and the
option-(a) body-field query, C6.

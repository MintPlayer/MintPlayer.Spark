# #536 spikes S1/S2: the SQL Server mapping and query translation

The evidence behind `docs/relational_providers_PRD.md` §8 (S1, S2). Throwaway code. There is **no `.csproj` here on
purpose**: `@nx/dotnet` infers a project from every `.csproj`, and CI would build it.

To rerun it, copy the three `.cs` files to a scratch folder next to this project file:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="11.0.0" />
    <PackageReference Include="Microsoft.Data.SqlClient" Version="7.1.2" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.4" />
  </ItemGroup>
</Project>
```

Each run drops and recreates the database `SparkSpike536` on the server you name. Pass the server and the
compatibility level:

| Command | What it runs |
|---|---|
| `dotnet run -- "localhost\MSSQLSERVER01" 170` | C1 and C2, the EF-serialized mappings, on SQL Server 2025 |
| `dotnet run -- localhost 160` | the same on SQL Server 2022 |
| `dotnet run -- <server> <compat> r2` | round 2: what EF serialization cannot do |
| `dotnet run -- <server> <compat> r3` | round 3: the hybrid that D1 adopts |

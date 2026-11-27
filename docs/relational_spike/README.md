# #536 spikes S12/S13: the D1 hybrid on PostgreSQL and MariaDB

The evidence behind `docs/relational_providers_PRD.md` §8.2. Throwaway code: round 3 of `docs/sql_server_spike/`
(Newtonsoft owns the document, EF Core 11 only translates queries over a complex `ToJson` model of `Data`), with
the same model, seed and 18 query shapes, ported to two more databases. There is **no `.csproj` here on purpose**
(`@nx/dotnet` would infer and build it).

To rerun it, copy `Program.cs` to a scratch folder next to this project file:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="11.0.0" />
    <PackageReference Include="Microting.EntityFrameworkCore.MySql" Version="11.0.0" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.4" />
  </ItemGroup>
</Project>
```

Start the two servers as local-only, throwaway containers (the password is a spike constant, not a secret):

```
docker run -d --name spark536-pg    -p 127.0.0.1:55432:5432 -e POSTGRES_PASSWORD=spike536 postgres:18
docker run -d --name spark536-maria -p 127.0.0.1:53306:3306 -e MARIADB_ROOT_PASSWORD=spike536 mariadb:11.8
```

| Command | What it runs |
|---|---|
| `dotnet run -- pg` | the hybrid on PostgreSQL 18 (Npgsql 11.0.0, `SetPostgresVersion(18, 0)` for the virtual `Raw` column) |
| `dotnet run -- maria` | the hybrid on MariaDB 11.8 (Microting 11.0.0, `EnablePrimitiveCollectionsSupport()`) |

Each run drops and recreates the database `sparkspike536`.

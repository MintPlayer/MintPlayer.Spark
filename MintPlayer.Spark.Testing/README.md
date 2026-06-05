# MintPlayer.Spark.Testing

Test-utilities library for writing automated tests against Spark apps. It provides an embedded RavenDB driver, an in-memory Spark host factory, antiforgery-aware HTTP helpers, JSON fixture seeding, index helpers, and Verify snapshot defaults.

> This is a **test-utilities** library, not a test project — it references xUnit for the `IAsyncLifetime` type but contains no `[Fact]`s (`IsTestProject=false`). Add it to your own xUnit test project as a `<PackageReference>`. It is xUnit-based and pulls in `RavenDB.TestDriver` (which bundles an embedded RavenDB server) and `Verify.Xunit`, so treat it as a batteries-included integration-test harness.

## What's in the box

| Type | Purpose |
|------|---------|
| `SparkTestDriver` | xUnit base class that spins up an in-memory RavenDB instance per test class and exposes a ready `IDocumentStore Store`. |
| `SparkEndpointFactory<TContext>` | Boots a minimal in-memory Spark HTTP host (ASP.NET Core `TestServer`) wired to a supplied store, for endpoint/integration tests. |
| `SparkTestClient` | `HttpClient` wrapper that attaches the antiforgery cookie + `X-XSRF-TOKEN` header to every mutating request. |
| `JsonFixtureImporter` | Seeds a store from RavenDB query-result-format JSON fixture files. |
| `RavenIndexHelper` | Deploys indexes and waits for them to become non-stale (usable from any store-holding fixture). |
| `VerifyDefaults` | Centralizes [Verify](https://github.com/VerifyTests/Verify) snapshot path configuration (auto-initialized via a module initializer). |

## Setup

### 1. Reference the project

```xml
<ItemGroup>
  <ProjectReference Include="..\MintPlayer.Spark.Testing\MintPlayer.Spark.Testing.csproj" />
</ItemGroup>
```

### 2. Provide a RavenDB license

RavenDB 7.x requires a license even for the embedded TestDriver. `SparkTestDriver` loads one from, in order:

1. The `RAVENDB_LICENSE` environment variable (JSON content — CI-friendly).
2. A `raven-license.log` file at the repository root (local development).

If neither is present, tests fail at initialization with a clear message. See [ravendb.net/buy](https://ravendb.net/buy) for community/developer licenses.

## Usage

### Data-layer tests — `SparkTestDriver`

Derive from `SparkTestDriver` to get a per-class embedded store. Override `IndexAssemblies` to auto-deploy and wait on indexes before the first test runs.

```csharp
public class PersonQueryTests : SparkTestDriver
{
    // Indexes in this assembly are deployed and awaited during InitializeAsync.
    protected override IEnumerable<Assembly> IndexAssemblies => [typeof(People_ByName).Assembly];

    [Fact]
    public async Task Finds_people_by_name()
    {
        await SeedFromJsonAsync("Data/Seed/people.json"); // resolves against the test output dir

        using var session = Store.OpenAsyncSession();
        var matches = await session.Query<Person, People_ByName>()
            .Where(p => p.Name == "Ada")
            .ToListAsync();

        matches.Should().ContainSingle();
    }
}
```

Copy fixtures to the output directory so the relative path resolves:

```xml
<ItemGroup>
  <Content Include="Data\**\*"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>
</ItemGroup>
```

### Endpoint/integration tests — `SparkEndpointFactory<TContext>`

Boots a real Spark middleware pipeline over `TestServer`, against a store you supply (typically `Store` from a `SparkTestDriver`). It writes the supplied model definitions into a per-test temp content root, so `ModelLoader` sees exactly the entity types your fixture declares.

```csharp
public class CarEndpointTests : SparkTestDriver
{
    [Fact]
    public async Task Create_then_get_round_trips()
    {
        await using var factory = new SparkEndpointFactory<FleetContext>(
            testStore: Store,
            models: FleetModels.All,
            configureServices: services =>
            {
                // Optional: register custom Actions, swap IAccessControl for authz tests, etc.
            });

        // Antiforgery-aware client: warms up to mint the XSRF token, then attaches it to writes.
        using var client = await factory.CreateAuthorizedClientAsync();

        var create = await client.PostJsonAsync("/spark/po/Car", new { Brand = "Tesla" });
        create.EnsureSuccessStatusCode();

        var list = await client.GetAsync("/spark/po/Car");
        list.EnsureSuccessStatusCode();
    }
}
```

> By default the factory opts into `AllowAnonymousAccess()` so endpoint logic can be tested under an "everyone-can" baseline (the framework default is deny-all). Tests that exercise authorization should register their own `IAccessControl` via `configureServices`.

`TestServer`'s `HttpClient` does not manage cookies automatically, which is why mutating requests need the antiforgery cookie + token threaded through explicitly. `SparkTestClient` (via `CreateAuthorizedClientAsync`) does this for you; if you need the raw values, call `factory.MintAntiforgeryAsync()`.

### Snapshot tests — `VerifyDefaults`

The module initializer configures Verify automatically, so snapshots land under `VerifyResults/{TestClass}/{TestMethod}.verified.*`. No per-test setup needed; just `await Verify(result)`.

## Related

- [CronosCore RavenDB test helper](https://github.com/MintPlayer) — complementary JSON-seeding + Verify infrastructure standardized across MintPlayer repos.
- [HTTP API Specification](../docs/Spark-API-Specification.md) — the endpoints the `SparkEndpointFactory` host exposes.

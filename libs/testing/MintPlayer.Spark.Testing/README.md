# MintPlayer.Spark.Testing

Test-utilities library for writing automated tests against Spark apps. It provides an embedded RavenDB driver, an in-memory Spark host factory, antiforgery-aware HTTP helpers, JSON fixture seeding, index helpers, and Verify snapshot defaults.

> This is a **test-utilities** library, not a test project — it references xUnit for the `IAsyncLifetime` type but contains no `[Fact]`s (`IsTestProject=false`). Add it to your own xUnit test project as a `<PackageReference>`. It is xUnit-based and pulls in `RavenDB.TestDriver` (which bundles an embedded RavenDB server) and `Verify.Xunit`, so treat it as a batteries-included integration-test harness.

## What's in the box

| Type | Purpose |
|------|---------|
| `SparkTestDriver` | xUnit base class that creates a fresh in-memory RavenDB database per test case and exposes a ready `IDocumentStore Store`. |
| `SparkEndpointFactory<TContext>` | Boots a minimal in-memory Spark HTTP host (ASP.NET Core `TestServer`) wired to a supplied store, for endpoint/integration tests. |
| `SparkTestClient` | `HttpClient` wrapper that attaches the antiforgery cookie + `X-XSRF-TOKEN` header to every mutating request. |
| `JsonFixtureImporter` | Seeds a store from RavenDB query-result-format JSON fixture files. |
| `RavenIndexHelper` | Deploys indexes and waits for them to be registered and non-stale (usable from any store-holding fixture). |
| `AsyncWait` | Bounded polling for work with no completion signal; always throws on expiry. |
| `RavenIndexDeploymentException` | An index faulted or was never deployed — distinct from a timeout. |
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

Derive from `SparkTestDriver` to get an embedded store. Override `IndexAssemblies` to auto-deploy and wait on indexes before the test body runs.

> **Each test case gets its own database.** xUnit constructs a new instance of the test class for every `[Fact]` and every `[Theory]` row, so `InitializeAsync` — and the `CreateDatabaseOperation` behind it — runs per test, not per class. All of those databases live on one shared embedded server, so a large suite should cap test parallelism in `xunit.runner.json`; running unconstrained can make the server unresponsive under CI load.

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

### Seeding that is queryable — `SeedAsync`

**This is the default way to write documents a test will then query.** It saves with
`WaitForIndexesAfterSaveChanges(throwOnTimeout: true)`, so the *server* holds the write until the
indexes covering it are current — no explicit index wait needed afterwards:

```csharp
var car = new Car { Plate = "ABC-123" };
await SeedAsync(session => session.StoreAsync(car));

// query immediately; no WaitForIndexing
using var session = Store.OpenAsyncSession();
var hits = await session.Query<Car, Cars_ByPlate>().Where(c => c.Plate == "ABC-123").ToListAsync();
```

Why this beats saving and then polling every index:

- **Targeted** — only the indexes this write touched, not the whole database.
- **No sampling window** — a global poll can catch a momentarily-clean snapshot and return while
  another writer's document is still unindexed. The write here does not complete until its indexes
  are current, so there is no gap to lose.
- **Impossible to forget** — the guarantee is attached to the write, not to a call someone has to
  remember to add next to each new query.

Declare the entity outside the lambda if you need its generated id afterwards. Reach for
`WaitForIndexesAsync` instead when no single session owns the write — Smuggler/JSON imports, or
documents written by a background worker or by the code under test.

### Waiting for anything else — `AsyncWait`

For asynchronous work with no completion signal (a worker attaching, a file-watcher invalidating a
cache, a cron job firing). Everything here **throws** on expiry, naming what was awaited and for how
long — a wait that quietly gives up turns into a confusing assertion failure somewhere downstream.

```csharp
await AsyncWait.UntilAsync(
    () => recorder.Count(nameof(EverySecondJob)) > 0,
    "the every-second job to fire at least once",
    TimeSpan.FromSeconds(8));

var message = await AsyncWait.ForAsync(
    () => session.LoadAsync<SparkMessage>(id),
    m => m.Status == EMessageStatus.Processed,
    $"message '{id}' to be processed",
    describeLast: m => $"Status={m?.Status}");
```

Prefer a real signal where one exists: `SeedAsync` for writes, `WaitForIndexesAsync` for indexing.
Never substitute a fixed `Task.Delay` — it makes a test that passes prove only "not yet".

### Waiting for indexes — `WaitForIndexesAsync`

"Settled" means **deployed and up to date**, not just up to date:

```csharp
await WaitForIndexesAsync();   // on SparkTestDriver — carries this fixture's declared index names
```

The second half alone is a trap. *"Every index is non-stale"* is universally quantified, so on a
database with no indexes — where every fixture starts, since each test gets its own — it is
vacuously true and returns instantly, having guaranteed nothing. `SparkTestDriver` remembers the
indexes it deployed (from `IndexAssemblies` or `DeployIndexesAsync`) and passes them along, so a
wait cannot pass because the index it was waiting for was never registered.

On a plain store, name them yourself:

```csharp
await store.WaitForIndexingAsync(expectedIndexes: RavenIndexHelper.DeclaredIndexNames(myAssembly));
```

**Auto-indexes are the exception, and it is inherent.** They are held to the same *staleness* bar as
declared indexes — a stale auto-index is exactly what returns the wrong rows — but they cannot take
part in the *deployment* check, because they do not exist until a query creates them. RavenDB blocks
on that first creation itself, which is what makes plain seed-then-query safe.

Failures are typed, because the causes are unrelated:

| Exception | Meaning | What to do |
|---|---|---|
| `RavenIndexDeploymentException` | An index faulted, or was never registered. Carries `FaultedIndexes` / `MissingIndexes` and the index errors. | Fix the index — waiting will never help. |
| `TimeoutException` | Indexes are healthy but did not catch up in time. | Retry, raise the limit, or look at load. |

### Endpoint/integration tests — `SparkEndpointFactory<TContext>`

Boots a real Spark middleware pipeline over `TestServer`, against a store you supply (typically `Store` from a `SparkTestDriver`). It writes the supplied model definitions into a per-test temp content root, so `ModelLoader` sees exactly the entity types your fixture declares.

```csharp
public class CarEndpointTests : SparkTestDriver
{
    // The route takes the entity type's ID, not its name — so the fixture declares the id and
    // builds its model from it.
    private static readonly Guid CarTypeId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task Create_then_get_round_trips()
    {
        await using var factory = new SparkEndpointFactory<FleetContext>(
            testStore: Store,
            models: [FleetModels.Car(CarTypeId)],
            configureServices: services =>
            {
                // Optional: register custom Actions, swap IAccessControl for authz tests, etc.
            });

        // Antiforgery-aware client: warms up to mint the XSRF token, then attaches it to writes.
        using var client = await factory.CreateAuthorizedClientAsync();

        // Note the envelope. The endpoint reads a PersistentObjectRequest, so the entity goes
        // under `persistentObject` with its attributes as name/value pairs — posting a bare
        // `new { Brand = "Tesla" }` deserializes to a request with no persistent object and fails.
        var create = await client.PostJsonAsync($"/spark/po/{CarTypeId}", new
        {
            persistentObject = new
            {
                name = "Car",
                objectTypeId = CarTypeId,
                attributes = new[]
                {
                    new { name = "Brand", value = (object)"Tesla" },
                },
            },
        });
        create.EnsureSuccessStatusCode();

        var list = await client.GetAsync($"/spark/po/{CarTypeId}");
        list.EnsureSuccessStatusCode();
    }
}
```

> By default the factory writes a permissive `App_Data/security.json` — a `*/*` grant to both
> well-known roles — so endpoint logic can be tested under an "everyone-can" baseline. It is a real
> file rather than a switch, so the default exercises the same evaluation path production does, and
> after `Start()` the factory asserts the host loaded it.
>
> A test that IS about authorization passes `security:` — `SparkTestSecurity.Empty`,
> `.Permissive.Without("Secret")`, `.Granting(…)`, `.FromFile(…)`. For the two things a grant list
> cannot express — recording what was asked, and deciding by predicate — swap the service instead
> with `services.UseSparkTestAccessControl(SparkTestAccessControl.DenyAll())`.

`TestServer`'s `HttpClient` does not manage cookies automatically, which is why mutating requests need the antiforgery cookie + token threaded through explicitly. `SparkTestClient` (via `CreateAuthorizedClientAsync`) does this for you; if you need the raw values, call `factory.MintAntiforgeryAsync()`.

### Snapshot tests — `VerifyDefaults`

The module initializer configures Verify automatically, so snapshots land under `VerifyResults/{TestClass}/{TestMethod}.verified.*`. No per-test setup needed; just `await Verify(result)`.

## Related

- [CronosCore RavenDB test helper](https://github.com/MintPlayer) — complementary JSON-seeding + Verify infrastructure standardized across MintPlayer repos.
- [HTTP API Specification](../../../docs/Spark-API-Specification.md) — the endpoints the `SparkEndpointFactory` host exposes.

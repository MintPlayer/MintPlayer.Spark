---
remarks: "Distributed by the MintPlayer.Spark.Testing NuGet package and copied into each consuming test project on build. In a consuming project this file is kept in sync with the package on every build — do NOT edit it there; edit the source in MintPlayer.Spark.Testing. Commit it to source control."
generated-by: "MintPlayer.Spark.Testing build target (CopySparkTestingAgentsGuide)"
---

# MintPlayer.Spark.Testing — agent guide

Self-contained reference for writing tests against the MintPlayer.Spark framework. Read the
**Hard rules** first — most of them are about tests that pass for the wrong reason, which is the
failure mode this document exists to prevent.

Spark is **not** Vidyano. If you have written tests for CronosCore, see
[What is different from Vidyano](#what-is-different-from-vidyano) before assuming anything.

---

## Hard rules (read first)

- **Pick the right driver.** `SparkTestDriver` gives a **fresh database per test case**;
  `SparkSharedDatabase` + `SparkSharedTestDriver` give **one per test class**. The shared one is
  faster; the per-case one is what you need the moment a test asserts on an unscoped count, uses
  a fixed id, or does anything database-wide. [Details](#choosing-a-driver).
- **A 404 does not mean "not found".** Spark answers 404 for *denied* as well as *absent*, on
  purpose, so it is not an existence oracle. Asserting `NotFound` proves refusal-**or**-absence —
  never absence. [Details](#the-404-rule-audit-m-3).
- **The default test security grants everything.** `SparkEndpointFactory` writes a permissive
  `security.json` unless told otherwise, so an endpoint test proves *endpoint logic*, never
  *authorization*. Authorization needs `security: SparkTestSecurity.Empty` / `.Without(...)` /
  `.Granting(...)`.
- **Mutating requests need a minted antiforgery token**, and antiforgery runs **before**
  authorization — so an unminted `POST` answers **400** and proves nothing about the permission
  check you were testing. Call `MintAntiforgeryAsync()` (or use `CreateAuthorizedClientAsync()`).
- **Seed with `SeedAsync`, not a raw session**, when the test then queries. It asks the *server* to
  hold the write until the covering indexes have caught up.
- **Never assert on elapsed time to prove something was fast.** Under a suite running hundreds of
  databases against one server, that measures the machine. Assert the property.
- **A polling timeout is a failure bound, not a success bound.** `AsyncWait.UntilAsync` returns the
  instant the condition holds, so a generous timeout costs a passing run nothing. Set it to many
  times the expected duration.
- **Give an Actions class a globally unique name.** Discovery matches on *simple name across every
  loaded assembly* and caches the answer process-wide. [Details](#actions-class-discovery).
- **Test naming: sentence style.** `A_refusal_is_byte_identical_to_a_genuine_not_found`. No
  "Should"/"Assert" words. (This is *not* CronosCore's `Subject_Action_Detail`.)

---

## Choosing a driver

Both ship. Neither replaces the other.

| | `SparkTestDriver` | `SparkSharedDatabase` + `SparkSharedTestDriver` |
|---|---|---|
| Database | per test **case** | per test **class** |
| Isolation | total | class-local — tests see each other's documents |
| Cost | one create/delete per case; a host cannot be reused | one per class |
| Use when | the test needs an empty or exclusive database | everything else |

Per-case (the conservative default):

```csharp
public class CarTests : SparkTestDriver
{
    [Fact]
    public async Task Reads_back_what_it_wrote()
    {
        await SeedAsync(s => s.StoreAsync(new Car { Plate = "ABC" }, "cars/1"));

        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<Car>("cars/1")).Plate.Should().Be("ABC");
    }
}
```

Per-class — note the fixture owns the database *and* anything expensive built on it:

```csharp
public sealed class CarHost : SparkSharedDatabase
{
    public SparkEndpointFactory<FleetContext> Factory { get; private set; } = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Factory = new SparkEndpointFactory<FleetContext>(Store, [FleetModels.Car(CarTypeId)]);
    }

    public override async Task DisposeAsync()
    {
        if (Factory is not null) await Factory.DisposeAsync();
        await base.DisposeAsync();
    }
}

public class CarQueryTests(CarHost host)
    : SparkSharedTestDriver(host), IClassFixture<CarHost>
{
    [Fact]
    public async Task Lists_the_car_it_seeded()
    {
        await SeedAsync(s => s.StoreAsync(new Car { Plate = "ABC" }, Id("cars/1")));
        // Id("cars/1") -> "t3f9a1c02/cars/1", unique to this test case
    }
}
```

### What a shared database forbids

These are the ways a class silently breaks when moved. All of them are reasons to **stay on
`SparkTestDriver`** — that is what it is for, not a workaround:

- **Unscoped counts.** `Query<T>().CountAsync()`, `SingleAsync()` over a collection,
  `Should().BeEmpty()`, `TotalRecords.Should().Be(3)`. Assert on the ids you seeded.
- **Reused fixed ids.** Two tests both writing `people/1` now collide. Use `Id(...)`.
  ⚠️ It does not help types that derive their own id (`IHasNaturalId`) — there the *business key*
  must differ per test.
- **Anything database-wide.** `StopIndexingOperation`, subscription enumeration, compare-exchange
  on a fixed key, index-error or entry-count assertions, `Store.Maintenance.Server`.
- **A test whose subject is an empty database.**

⚠️ **`Store.OnBeforeQuery` handlers are never removed** if you subscribe inline. Harmless while
the store dies with the case; on a shared store they accumulate and capture *other* tests' RQL.
Use `RqlRecorder.Attach(Store)` — disposable, concurrent-safe, and attaches before the session is
constructed (Raven copies handlers into a session at construction, so a later subscription
silently never fires).

---

## Booting a Spark host

`SparkEndpointFactory<TContext>` runs the real middleware pipeline over `TestServer`. Everything
happens in the constructor — there is no separate `StartAsync`.

```csharp
new SparkEndpointFactory<MyContext>(
    testStore: Store,
    models: [MyModels.Person(PersonTypeId)],
    configureServices: services => { … },   // runs LAST, after AddSpark
    configureSpark:    spark    => { … },   // runs INSIDE AddSpark — the only place modules reach
    environment: "Testing",
    configureIndexCatalog: catalog => { … },
    security: SparkTestSecurity.Permissive);
```

It writes the model files, `modelHashes.json` and `security.json` into a private temp content root,
then asserts the host actually loaded the security file — without that check a silently ignored
file would make every authorization test vacuously green.

**Routes take the entity type's ID, not its name**, and writes take an envelope:

```csharp
await client.PostJsonAsync($"/spark/po/{PersonTypeId}", new
{
    persistentObject = new
    {
        name = "Person",
        objectTypeId = PersonTypeId,
        attributes = new[] { new { name = "FirstName", value = (object)"Alice" } },
    },
});
```

Posting a bare `new { FirstName = "Alice" }` deserializes to a request with no persistent object.

### Indexes are a two-step obligation

Deploy to RavenDB **and** register in the catalog. Nothing enforces both:

```csharp
await new Cars_Overview().ExecuteAsync(Store);          // 1. deploy
_factory = new SparkEndpointFactory<MyContext>(Store, [model],
    configureIndexCatalog: catalog =>                    // 2. register
    {
        catalog.RegisterIndex(typeof(Cars_Overview));
        catalog.RegisterProjection(typeof(VCar), typeof(Cars_Overview));
    });
```

Arming is explicit and per fixture because fixture indexes are usually nested test classes, and an
assembly scan that picked them up wholesale would fail every host.

---

## Authorization in tests

`SparkTestSecurity` writes a real `security.json`, so the default exercises the same evaluation
path production does.

| | |
|---|---|
| `Permissive` | `*/*` to both well-known groups — **the default** |
| `Empty` | nothing granted to anyone |
| `.Granting("Read/Car", …)` | resources are `{action}/{target}` |
| `.Denying(…)` | denials beat grants |
| `.Without("Secret")` | permissive except those targets |
| `.FromFile(path)` / `.FromJson(json)` | verbatim |

Well-known groups are `anonymous` and `authenticated`. **`Everyone` was removed and the validator
refuses a file that declares it.** `anonymous` is *not* a floor under `authenticated` — a right
both should have is two grants.

Prefer this over swapping the service. `SparkTestAccessControl` (`AllowAll`/`DenyAll`/`Granting`/
`Matching`, plus an `Asked` list that keeps repeats) exists for the two questions a grant list
cannot answer — *what did the code ask for*, and *decide by predicate*. Install with
`services.UseSparkTestAccessControl(...)`, which removes the existing registration rather than
appending. There is deliberately **no `IPermissionService` double**: it is four lines of string
concatenation, and faking it removes the one piece of logic that keeps a resource-string assertion
honest.

### The 404 rule (audit M-3)

An authenticated caller must not be able to tell "does not exist" from "exists but you may not read
it", or Spark becomes an existence oracle. Three consequences for assertions:

1. **404 proves refusal-or-absence, never absence.**
2. **Assert equality between the two shapes**, not against a literal — send the same request twice,
   once against a real id and once against an absent one, and compare.
3. **Bodies too**, with caller-supplied identifiers normalized out. Some endpoints echo the
   requested id, which is byte-identical *for one request* (what M-3 needs) yet differs between two.

**Access endpoints** (`/spark/po/*`, `/spark/actions/*/…`, `/spark/lookupref/*`) answer 404 —
or **401** when the application has a way to sign in, because then authenticating would help.
**Catalogue endpoints** (`/spark/types`, `/spark/queries`, `/spark/aliases`,
`/spark/program-units`, `/spark/actions/{type}`, `/spark/permissions/{type}`) answer **200 with
everything filtered out**, because the client shell loads them on boot for every visitor. So
asserting 200 alone would also be true of a leak — name what must *not* appear in the body.

⚠️ The permissive default means most factory-booted tests would still pass if a permission check
were deleted outright. A deny-all mirror suite — every endpoint against `SparkTestSecurity.Empty` —
is the only thing that turns that into a red build. Add a row when you add an endpoint.

---

## Seeding and indexing

`SeedAsync` writes and returns only once RavenDB has indexed, via server-side
`WaitForIndexesAfterSaveChanges` with `throwOnTimeout: true`. It beats saving then polling because
it is targeted to the indexes that write touched, has no sampling window, and cannot be forgotten
by the next query someone adds.

Use `WaitForIndexingAsync` instead when **no single session owns the write**: JSON/Smuggler
imports, a background worker, or the code under test.

⚠️ **On a database with no indexes, "every index is non-stale" is vacuously true** and the wait
returns instantly. That is why `expectedIndexes` exists, and why `WaitForIndexesAsync` on a driver
carries the fixture's own deployed index names. Auto-indexes are the inherent exception — they do
not exist until a query creates them, and RavenDB blocks on that first creation itself.

`SeedFromJsonAsync` imports RavenDB query-result-shaped JSON
(`{ "Results": [ { "@metadata": { "@id": … }, … } ] }`) and copies **all** metadata through — which
is how a fixture controls or omits `@Raven-Clr-Type` deliberately.

---

## Traps

### Actions-class discovery

`{EntityName}Actions` is found by **simple name across every loaded assembly**, and the result —
including "not found" — is cached **process-wide**. Two fixtures each nesting a `NoteActions` race:
first found wins, the cast fails for the loser, and it silently falls back to
`DefaultPersistentObjectActions<T>` with the row rule gone, for the rest of the process.

So: **give it a globally unique name** (`RowRuleLedgerActions`, `GuardedDocActions`), *or* stub
`IActionsResolver` and bypass discovery entirely.

### `LoadAsync<object>` and `@Raven-Clr-Type`

Usually looks fine — Raven reads the CLR-type metadata and returns a real entity. It degrades to a
`JObject` only when that metadata is absent or unresolvable: raw put, bulk insert, Smuggler import,
ETL, or a type since renamed or moved. **A session-seeded fixture is immune and cannot reproduce
it** — the client re-derives the metadata from the entity it stores. To test it, patch server-side
or use a JSON fixture that omits the key.

### Absent field vs `== false`

Adding a field and querying its default silently excludes every pre-existing document — a missing
property does not match `== false`. Use `!= true`. **Model-built test data always writes the field,
so it cannot reproduce this**; write the document without the property.

### Subscriptions cannot evaluate `now()`

A subscription is change-vector-driven: a document is tested against the query when it is
*written*, and time passing is not a write. Gate on a boolean a sweeper sets. RavenDB ≤7.2.1
answered a silent false; ≥7.2.2 rejects the query. ⚠️ A subscription that matches nothing looks
exactly like a queue with nothing in it.

### Query aliases are unique

An alias identifies exactly one query — a collision throws at load, not at use. An omitted alias is
*derived* from the name (`GetStocks` → `stocks`), so it can collide with one somebody declared.

### Static state in fixtures

The idiom for "the signed-in principal" is a `public static` on the fixture's nested Actions class.
**Every fixture must own its statics** — test classes run concurrently, so a shared one leaks.

---

## Licensing

RavenDB 7.x needs a licence even embedded. Loaded from `RAVENDB_LICENSE` (JSON content), else
`raven-license.log` at the repository root. Missing both, a fixture fails at initialisation naming
both sources.

Override `RequireLicense => false` for a suite that must survive without one — the motivating case
is fork pull requests, which get no organization secrets. That gates **this fixture's** failure, not
the server's tolerance: an *invalid* licence still fails at startup regardless, because a supplied
licence is always validated.

⚠️ **Never print a licence value**, and never commit one. Both sources hold secrets.

---

## What is different from Vidyano

For readers coming from CronosCore:

| | Spark | CronosCore / Vidyano |
|---|---|---|
| Runner | xUnit | NUnit |
| Parallelism | **classes run in parallel** (capped) | sequential; `[NonParallelizable]` |
| Database | per case, or per class | one, shared by the whole run |
| Shared seed data | none | `Data/Seed/**`, never reset |
| Clock | no global clock to reset | `SetDateTime` in every `[SetUp]` |
| Host | `TestServer` per fixture | one process-wide Kestrel host |
| DI | per fixture, via constructor hooks | fixed for the whole run |
| Project naming | free | load-bearing (`{project}.UnitTests` → `{project}.Startup`) |
| E2E | Playwright | `.visc` scripts |
| Test naming | sentence style | `Subject_Action_Detail` |
| Mocks | NSubstitute, plus hand-rolled doubles | none |

The consequence that matters most: **because xUnit runs classes concurrently, a single
process-wide database would be entered by several at once.** That is why Spark's shared driver
shares a *server* and keeps the database as the isolation boundary.

---

## Conventions

- **Naming:** sentence style — `POST_without_antiforgery_token_is_rejected_with_400`.
- **Assertions:** `MintPlayer.Assertions`. Imported globally via a csproj `<Using>`; no `using`
  needed. It replaced FluentAssertions, whose v8 went to a paid licence. Mostly source-compatible,
  but four differences bite: absence on a nullable *value* type is `NotHaveValue()`/`HaveValue()`,
  not `BeNull()`/`NotBeNull()`; `BeEquivalentTo` takes an `IEnumerable`, has no `params` overload,
  and its 3rd positional is the options lambda, so a reason must be passed as `because:`;
  `AndWhichConstraint` exposes `.Which`, not `.Subject`; and `Throw<T>()` returns
  `ExceptionAssertions<T>` directly while `WithMessage()` returns an `AndConstraint`, so chaining
  off the former uses `.Which` and off the latter `.And`. `WithMessage` globs are
  case-**sensitive** here.
- **Snapshots:** `Verify` is wired automatically by a module initializer, writing to
  `VerifyResults/{Class}/{Method}.verified.*`. It is a minority technique here; explicit
  assertions are the norm.
- **Fixture models:** a static factory per entity, parameterized by the type id the test declares —
  `GuardedDocModel.For(DocTypeId)`.
- **Fixture data:** `<Content Include="Data\**\*.json" CopyToOutputDirectory="PreserveNewest" />`.
  ⚠️ `xunit.runner.json` must be a `Content` item too — xUnit reads it from the output directory,
  not the project root.

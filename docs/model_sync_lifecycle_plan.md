# Plan — Model synchronization lifecycle

**PRD:** [model_sync_lifecycle_PRD.md](model_sync_lifecycle_PRD.md) ·
**Branch:** `feat/issue-253-preserve-model-attributes`

Eight milestones, one commit each. Ordered so every commit leaves the tree green and each risky
change lands on top of a verified foundation. M1–M3 are the lifecycle refactor and are independently
shippable; M4–M6 build the hash gate; M7–M8 close CI and docs.

Per the repo convention, test suites run **once at the end** (M8), not per milestone. Intermediate
milestones are verified by reading and type-checking.

**All milestones complete.**

| | Milestone | Commit |
|---|---|---|
| M1 | Extract `PopulateIndexRegistry` | `757f42b` |
| M2 | Builder-phase synchronization, offline, no `Environment.Exit` | `d415954` |
| M3 | Development-only `IModelSynchronizer` registration | `19bdf9e` |
| M4 | `SparkModelShape` hasher | `b47ec5a` |
| M5 | Write `model-hashes.json` | `f15b5e3` |
| M5b | Structural per-file hashing (not raw bytes) | `f27d1ee` |
| M6 | Startup check + override | `2a44844` |
| M7 | `--spark-verify-model` + CI gate | `62ecbd3` |
| M8 | Docs, version bump, full suite | `5fefc83` |
| M9 | Newline normalisation + cross-OS verification on WSL | `703758b` |
| M10 | Fix unbounded query duplication; idempotency guard | this commit |

---

## M1 — Extract `PopulateIndexRegistry` (PRD R1, R8)

**File:** `libs/spark/MintPlayer.Spark/SparkMiddleware.cs`

Split `CreateSparkIndexes` (`:381-430`) into its two existing halves. `:398-421` is pure reflection
over `Assembly.GetEntryAssembly()` with no `documentStore` reference; `:423`
(`IndexCreation.CreateIndexes`) is the only DB call.

Extract `PopulateIndexRegistry(IIndexRegistry registry, Assembly assembly)`. `CreateSparkIndexes`
calls it then does `:423`. The offline write path (M2) and the check (M6) call it alone.

**Not optional:** `ModelSynchronizer` consults the registry at `:55`, `:70`, `:79`, `:175-177`. An
unpopulated registry does not fail — it silently emits projection types as their own model files and
skips the `QueryType`/`IndexName` merge. Wrong output, no error.

**One behaviour change to make deliberately:** `:426-429` swallows every exception into a
`Console.WriteLine`. The extracted function must **not** swallow — a registry that failed to populate
has to fail the run. Leave the existing swallow around the `IndexCreation` call only.

Pure refactor otherwise. No public API change.

## M2 — Builder-phase synchronization (PRD R1, R2, R3, R11 · fixes P1, P3)

**Files:** `SparkMiddleware.cs`, `Configuration/UseSparkOptions.cs`, new
`Extensions/SparkDevelopmentExtensions.cs`, `SparkFullGenerator.Producer.cs`, 4 demo `Program.cs`

Add:

```csharp
public static bool SynchronizeSparkModelsIfRequested(this WebApplicationBuilder builder, string[] args)
public static bool SynchronizeSparkModelsIfRequested<TContext>(this WebApplicationBuilder builder, string[] args)
    where TContext : SparkContext, new()
```

Non-generic overload recovers the context type from the descriptor registered by `UseContext<T>`
(`SparkMiddleware.cs:127-132`): `services.LastOrDefault(d => d.ServiceType == typeof(SparkContext))?.ImplementationType`,
then `Activator.CreateInstance`. `Session` stays null — verified safe, the synchronizer reads
`PropertyType` only. Ordering is guaranteed: `UseContext<T>` runs inside `configure(builder)` at
`:116`, and this call sits after `AddSpark` returns.

Constructs `new ModelSynchronizer(builder.Environment, registry)` directly — the pattern
`ModelSynchronizerTests.cs:40` already uses — with the registry from M1. **No DI provider, no
`IDocumentStore`, no session.**

Returns `bool`, sets `Environment.ExitCode`. **No `Environment.Exit`.** Guard three cases with clear
messages rather than an NRE, all exit 2: no `SparkContext` descriptor ("`UseContext<T>()` was never
called"), null `ImplementationType`, no public parameterless ctor.

Read the flag from `args` directly (PRD R11) — never `IConfiguration`.

Then remove:
- `SparkExtensions.SynchronizeSparkModels<TContext>()` (`:295`) → make internal
- `SparkExtensions.SynchronizeSparkModelsIfRequested<TContext>(args)` (`:325`) → delete
- `UseSparkOptions.SynchronizeModelsIfRequested<TContext>(args)` (`UseSparkOptions.cs:14`) → delete

**Keep** `UseSparkOptions` and the `UseSpark(Action<UseSparkOptions>)` overload (`:281-289`) — a
legitimate extension point, and `UseSparkOptionsTests.cs` keeps passing untouched. Fix the stale
`<example>` at `:276-280` and the stale class doc at `UseSparkOptionsTests.cs:16`.

Generator (`SparkFullGenerator.Producer.cs:134-148`): stop emitting `:143`; `UseSparkFull` drops both
its `args` parameter and `WriteUseSparkFull`'s now-dead `contextType`. Emit the builder-phase call
from `AddSparkFull` instead, where the context type is already known at compile time.
`SparkFullGeneratorTests.cs` asserts only `Contain("TestApp.AppContext")`, which the surviving
`UseContext<T>` emission still satisfies — no test change expected, but verify.

Demos: move the call from the `app.` block up beside `AddSpark` — `HR:67`, `DemoApp:49`,
`WebhooksDemo:85`; `Fleet:124` becomes `UseSparkFull()`.

**Verify explicitly, do not assume (PRD R8):** that no in-process path can reach `UseSpark()` with
`--spark-synchronize-model` still in `args`. If one can, M6's check must bypass itself on that flag.

## M3 — Development-only registration (PRD R4 · fixes P2)

**Files:** `Services/ModelSynchronizer.cs`, `SparkMiddleware.cs`, 2 test files

Delete `[Register(...)]` at `ModelSynchronizer.cs:18` (verified: drops cleanly out of the generated
`AddSparkServices()`). Register conditionally in `AddSparkCore`, reading the environment from the
service descriptor at registration time:

```csharp
var env = (services.LastOrDefault(d => d.ServiceType == typeof(IHostEnvironment))?.ImplementationInstance
        ?? services.LastOrDefault(d => d.ServiceType == typeof(IWebHostEnvironment))?.ImplementationInstance)
        as IHostEnvironment;
if (env?.IsDevelopment() == true)
    services.AddSingleton<IModelSynchronizer, ModelSynchronizer>();
```

Must be null-safe — a bare `ServiceCollection` has no such descriptor (measured).

⚠️ **Must go in `AddSparkCore`, not a `CreateBuilder` factory.** Measured: a conditional registration
before `AddSparkServices()` yields two descriptors and the later unconditional one wins
`GetRequiredService` — the gate would silently do nothing.

**Test fallout — the complete list, verified:**
- `OidcAdminRouteTests.cs:87` — the `scratch` factory omits `environment:`, so it defaults to
  `"Testing"` (`SparkEndpointFactory.cs:54`) and `InitializeAsync` awaits it at `:54`, failing *every*
  test in the class. Switch both IdP call sites (`:88` and `OidcAdminRegistrationTests.cs:31`) to
  `new ModelSynchronizer(env, registry)` — they are fixtures asserting what the synchronizer *writes*,
  not consumers of a production service.
- `SparkExtensionsTests.cs:100-179` — 4 tests + the `SubstituteForApplicationBuilder` helper. Delete
  the two flag-detection tests; **rewrite, don't drop**, `:127` (Production must not reach the
  synchronizer — it pins the security property being made structural) and `:143`.
- `SynchronizeModelsIfRequestedTests.cs` — all 4 tests assert only `args.Contains(...)` on a local
  array and never touch Spark code. Delete the file.
- `ModelSynchronizerTests.cs` — unaffected, constructs the class directly.

Do **not** flip `SparkEndpointFactory`'s `"Testing"` default: `:75-78` documents that modules
deliberately behave differently outside Development, and a separate test covers the IdP's Production
refusal.

## M4 — `SparkModelShape` + discovery (PRD R5, R6)

**Files:** new `libs/spark/MintPlayer.Spark.Abstractions/Model/SparkModelShape.cs`, new
`libs/spark/MintPlayer.Spark/Services/ModelShapeDiscovery.cs`

Two pieces, because Abstractions **cannot** hold it all: `MintPlayer.Spark.Abstractions.csproj` has
zero `PackageReference` items (no `Raven.Client`), so it cannot name `IRavenQueryable<>`; and
`SparkContext` and `IIndexRegistry` live in core.

**Piece A — `SparkModelShape` (Abstractions).** Pure functions over `Type`. No Raven, no DI, no IO, no
new package references. Extract from `ModelSynchronizer.cs`: `GetDataType` (`:646-675`),
`GetCollectionElementType`/`ResolveCollectionElementType` (`:687-730`), `IsComplexType` (`:732-740`),
`IsNullable` (`:742-745`). Reuse `GetSparkModelProperties`/`IsIgnoredForSparkModel` as-is.

**Piece B — `ModelShapeDiscovery` (core).** Walks `IRavenQueryable<T>` properties on the context
*type*, drops projection types, asks `IIndexRegistry` for `QueryType`/`IndexName`, builds the
transitive closure of embedded types. Extract `IsRavenQueryable`/`GetQueryableEntityType`
(`:633-643`) and `CollectEmbeddedTypes` (`:189-214`).

`ModelSynchronizer` is refactored to call A and B rather than keeping duplicates — one definition of
"what shape is this model", used by writer and checker alike.

**Tests — this is where the safety lives:**
- A `partial`-class fixture split across two files, pinning that the hash survives member-order
  changes. Without it someone "simplifies" the `OrderBy` away in a year and the failure lands in
  production, not CI.
- Each R6 invariant asserted individually (ordinal sort, `FullName` not `AssemblyQualifiedName`,
  UTF-8 without BOM, `\n` not `Environment.NewLine`).
- The R5 sensitivity table: property added/removed, nested property removed, `[Reference]` changed and
  removed, `[Sortable]` removed, `[IgnoreProperty]` added, `int`→`string`, setter removed → **all
  change**; `int`→`long` and `List<string>`→`string[]` → **unchanged**.
- Same hash from two separate processes.

## M5 — Write `model-hashes.json` (PRD R7)

**File:** `Services/ModelSynchronizer.cs`

After the write loop, compute per-entity hashes + `contextRoots` + the roll-up and write
`{ContentRootPath}/App_Data/model-hashes.json` — **one level above** `App_Data/Model/`, or
`ModelLoader.cs:49` will try to deserialize it as an `EntityTypeFile` on every startup.

No new dependencies: SHA-256 is in-box and the shape data is already computed by M4.

Also introduce a write-sink seam here (`ModelSynchronizer` currently calls `File.WriteAllText` inline
at `:116`/`:165` and `File.Delete` at `:181`) so M7's verify mode can compare instead of write.

**Measure before M7 depends on it:** synchronize must be byte-stable across repeat runs on an
unchanged model. `ModelSynchronizerTests.cs:208-209` syncs twice but only asserts `IsSortable`
survived — it never compares bytes. GUIDs are minted at `:97`, `:261`, `:456` and existing ids are
preserved by reference (`:449`, `:509`), so it *should* be stable — but that is exactly the word that
has already cost us once on this task. Three-line test: sync, capture bytes, sync again, compare.

## M6 — Startup check (PRD R8, R9 · fixes P4, delivers G1)

**Files:** new `Exceptions/SparkModelOutOfSyncException.cs`, `SparkMiddleware.cs`

Recompute the shape hash via M4, compare to `App_Data/model-hashes.json`. Development → warn and
continue; otherwise → throw. Fail **closed** on a missing hash file or missing model directory.

Placement: after `PopulateIndexRegistry` has run (`QueryType`/`IndexName` feed the hash) and before
the app serves anything. M1 makes this possible without a live `IDocumentStore`.

Error message per PRD R8 — names the drifted entities, both hashes, the fix command, the
"if this appeared after a deployment" line, and the override.

`SPARK_MODEL_HASH_OVERRIDE` per R9: value-based, must equal the computed `modelHash`, warns on **every**
startup. No boolean off-switch.

Tests: mismatch throws outside Development; mismatch warns in Development; missing file fails closed;
override with the right value starts and warns; override with a wrong/stale value still throws.

## M7 — `--spark-verify-model` + CI (PRD R10)

Verify mode reuses M5's write-sink to compare instead of writing. Exit codes per PRD R10.

CI step in `.github/workflows/pull-request.yml`, between the `affected --target=build` (`:90`) and
`affected --target=test` (`:93`) steps — reuse the built output, fail before the slow suite. No
RavenDB service container is needed, and none exists in any workflow today.

## M8 — Docs, version bump, full suite

- `README.md:32, 67, 82, 250`; `libs/spark/MintPlayer.Spark/README.md:47, 57, 77, 90, 320, 327, 329-330`;
  `libs/all_features/MintPlayer.Spark.AllFeatures/README.md:25, 34`
- New `docs/model-hash.md` — what the hash covers and deliberately does not (labels, renderers, rules,
  virtual attributes), the override, and the orphan chain: recovery is "re-run synchronize **and read
  what it printed**".
- Note in `docs/prd/unified-builder-prd.md` §4 that its "cannot be fully automated" premise is
  superseded — the context type comes from the descriptor, args from `string[]`.
- Launch profiles keep working (`Fleet/HR/DemoApp` `launchSettings.json`, `.vscode/launch.json:27`) —
  verify, since the flag now returns rather than `Environment.Exit`s.
- Lockstep `<Version>` bump across all 21 packable libs.
- **Full test suite, once.** Plus the release gate the PRD calls out: print the model hash on Linux CI
  and compare against a Windows dev machine (R6 cross-OS/cross-machine is still unmeasured).

## M10 — Fixed-point guarantee (PRD R12)

**Files:** `Services/ModelSynchronizer.cs`, new `tests/.../Model/SynchronizeIdempotencyTests.cs`

Two fixes and a guard.

`CollectQueriesFor` replaces the two inline `Where(q => q.EntityType == …)` filters and de-duplicates
by query id. This kills the unbounded growth an orphaned model file caused (+1 query per run,
measured). De-duplication by id is always safe — same id, same query. Name collisions are left alone
deliberately: an ambiguous model should surface, not be silently resolved.

A duplicate attribute name is now reported with the entity and file named, instead of escaping as
`ArgumentException: An item with the same key has already been added` out of `ToDictionary`, which
killed the command with nothing actionable in the message.

`SynchronizeIdempotencyTests` pins run 2 == run 3 across an empty directory, a **minimal** seed
(every optional field absent — the shape that exposes omitted-on-write/derived-on-read bugs), a
hand-authored seed carrying #253 preserved fields, and both orphan-file shapes. Verified to fail
without the fix.

**Deliberately out of scope, filed as follow-ups.** Both are byte-stable, so neither is an
idempotency defect, and both predate this branch:

- Stale `queryType`/`indexName`/`referenceType`/`asDetailType`/`lookupReferenceType` are never
  cleared when the corresponding attribute or projection is removed. These *are* structurally hashed,
  so the verifier will confirm a dead reference.
- An entity whose simple type name collides with a registered projection type's name has its model
  file written and then deleted on every run (the stale-projection cleanup matches on
  `ProjectionType.Name`). Byte-stable precisely because the file is absent every time, so a verify
  gate cannot see it.
- A second `IRavenQueryable<T>` of the same entity type on one context silently loses its query, and
  same-simple-name types in different namespaces collide on one `{Name}.json`.

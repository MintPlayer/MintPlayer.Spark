# PRD — Model synchronization lifecycle: offline sync, dev-only synchronizer, model-hash startup gate

**Branch:** `feat/issue-253-preserve-model-attributes` (rides on PR [#263](https://github.com/MintPlayer/MintPlayer.Spark/pull/263))
**Plan:** [model_sync_lifecycle_plan.md](model_sync_lifecycle_plan.md)
**Supersedes:** `docs/prd/unified-builder-prd.md` §4 (see [R9](#r9))

---

## Problem

Three defects and one missing capability, all in the same code path.

**P1 — `--spark-synchronize-model` exits 0 in Production without doing anything.**
`SparkMiddleware.cs:331` calls `Environment.Exit(0)` *outside* the Development guard at
`:300-304`. Passing the flag in Production prints "Model synchronization is only available in
Development mode" and then terminates the process with **exit code 0**, before `app.Run()`. Under
Kubernetes or systemd that is a restart loop reporting success. The AllFeatures generator injects
this call into every consuming app (`SparkFullGenerator.Producer.cs:143`), so apps have it whether
their authors wrote it or not — Fleet has a `--spark-synchronize-model` launch profile and no
hand-written call.

**P2 — the synchronizer is resolvable in production.**
`[Register(typeof(IModelSynchronizer), ServiceLifetime.Singleton)]` (`ModelSynchronizer.cs:18`) is
harvested unconditionally into the generated `AddSparkServices()`, called at `SparkMiddleware.cs:55`.
`IModelSynchronizer` is public. So `sp.GetRequiredService<IModelSynchronizer>().SynchronizeModels(ctx)`
runs in Production and never sees the guard, which lives one layer up in the extension method.

**P3 — synchronization requires a live RavenDB, for no reason.**
`SynchronizeSparkModels` resolves `IDocumentStore` at `:307`, which triggers the store factory and
`WaitForRavenDbConnection` (`:89`), then opens a session at `:310` and assigns it to the context. The
session is never used: `ModelSynchronizer.cs` contains no `GetValue`, no `.Invoke(`, and no reference
to `Session` — it reflects over `PropertyType` only. The DB dependency is pure placement accident.

**P4 (missing) — nothing detects a model that no longer matches its entity classes.**
Change an entity and forget to re-run synchronize, and the app starts happily against a stale model.
It surfaces later as missing columns and silently dropped values on save — `EntityMapper.cs:712`
already tells users to re-run synchronize when it hits the downstream symptom, which is too late.

---

## Goals

- **G1** A production app **refuses to start** when its model no longer matches its CLR entity
  classes. *(Stated as an absolute requirement by the product owner.)*
- **G2** `--spark-synchronize-model` runs **headlessly with no RavenDB**, so a merge queue can invoke it.
- **G3** The synchronizer is **absent from the production DI container**.
- **G4** No code path can exit the process with status 0 without doing what it was asked.

## Non-goals

- Removing `ModelSynchronizer` from the shipped assembly. Investigated and rejected — see [D1](#d1).
- Detecting hand-edits of model JSON. That is a supported workflow (#253); see [D3](#d3).
- Building the merge queue itself. Out of scope; we provide the command and the exit codes it needs.

---

## Requirements

### R1 — Synchronization moves to the builder phase
The write path runs after `AddSpark(...)` and **before `builder.Build()`**:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSpark(builder.Configuration, spark => { … });

if (builder.SynchronizeSparkModelsIfRequested(args))
    return;                       // ordinary return from Main
```

`AddSpark` is verified lazy: `IDocumentStore` is a factory lambda (`SparkMiddleware.cs:67`), and every
module registration (`AddAuthorization`, `AddMessaging`, `AddReplication`, `AddIdentityProvider`,
`AddCron`, `AddMigrations`) either registers descriptors or defers work into `Registry.AddMiddleware`,
which only executes inside `UseSpark()`. So a builder-phase run that never resolves `IDocumentStore`
touches no network, no DB and no disk beyond the model files it writes.

Running before `Build()` also means Kestrel never binds and `UseAngularCliServer` never spawns
`ng serve` — a hosted-service or `IStartupFilter` design would start the Angular dev server just to
write JSON files.

### R2 — No generic parameter; no `Environment.Exit`
The context type is recovered from the service descriptor registered by `UseContext<T>`
(`SparkMiddleware.cs:127-132`) and instantiated with `Activator.CreateInstance`, with `Session` left
null. Measured in a spike: `ImplementationType` is populated, instantiation succeeds, and the
`IRavenQueryable` getters are never invoked.

The method returns `bool` and sets `Environment.ExitCode`; the host returns from `Main`. This fixes
**P1** structurally — there is no `Environment.Exit` left to escape a guard. A `<TContext>` overload
is kept for explicitness.

### R3 — The flag works in every environment; the *service* does not exist in production
The two concerns are separated:

| Concern | Gate |
|---|---|
| **Write** (`--spark-synchronize-model`) | none — explicit operator intent, writes files, exits before `Build()` |
| **Check** (startup verification) | throws outside Development, warns in Development |
| **Service** (`IModelSynchronizer` in DI) | never registered outside Development |

Removing the environment gate from the *write* path is what makes **G2** possible: CI has no
`ASPNETCORE_ENVIRONMENT`, so it defaults to Production and the command would otherwise silently do
nothing and exit 0 — the worst possible failure for a merge queue. Setting
`ASPNETCORE_ENVIRONMENT=Development` in CI is the alternative and it is worse: it also flips the DB
auto-create branch (`:92`), the OIDC issuer requirement (`HR/Program.cs:33-39`) and the
`UseAngularCliServer` branch.

The command opens no DB, serves no request and exposes no endpoint. An explicit CLI flag is a
stronger "a human meant this" signal than an environment name.

### R4 — `[Register]` is removed from `ModelSynchronizer`
Satisfies **G3** and **P2**. Once the builder-phase path uses `new`, nothing needs it from DI.
Verified: removing the attribute drops the type cleanly out of the generated `AddSparkServices()`.
The gate for the Development-only registration lives in `AddSparkCore`, which can read the
environment from the `IHostEnvironment` service descriptor's `ImplementationInstance` at
*registration* time — not from a factory lambda.

⚠️ Registering it in `SparkApplication.CreateBuilder` instead would be a **silent no-op**: measured,
`AddSparkServices()` runs later and its unconditional registration wins `GetRequiredService`.

### R5 — Model hash: hash the CLR shape, not the JSON
`SparkModelShape` (new, in `MintPlayer.Spark.Abstractions`) produces a canonical text rendering of the
model derived **from the CLR types**, and SHA-256s it. Inputs:

- Per type: `Type.FullName`, projection/query type `FullName`, index name, `[Breadcrumb].Template`
- Per property (from `GetSparkModelProperties()`): `Name`, derived `dataType`, array-ness, `CanWrite`,
  nullability, `AsDetail` element type, `[Reference]` target + query, `[LookupReference]` type,
  `[Sortable]`

Deliberately **not** hashed, because they do not exist in the CLR: labels and translations, renderer,
rendererOptions, group, editMode, columnSpan, isVisible, order, rules, attribute `Id` GUIDs,
hand-authored virtual attributes, inline queries.

`Type.FullName`, never `AssemblyQualifiedName` — version bumps would churn the hash for free.
The **derived `dataType`** feeds the hash, not the raw CLR type name: measured, `int`→`long` and
`List<string>`→`string[]` leave the hash unchanged because they produce byte-identical JSON. Under a
refuse-to-start policy, a false positive that halts production for a no-op change is the fastest way
to teach operators to reach for the override.

### R6 — Determinism is safety-critical
A non-deterministic hash does not cause a merge conflict; it causes production to refuse to start at
random. Required invariants, all exercised by spike:

- `OrderBy(p => p.Name, StringComparer.Ordinal)` — never reflection order, never the default comparer
- Type blocks `Distinct` then ordinal-sorted by `FullName`
- `new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)`
- `'\n'` hardcoded — never `Environment.NewLine` (Windows writes the file, Linux verifies it)
- Nothing culture-sensitive stringified
- SHA-256, full 64-char lowercase hex, never truncated

**Measured: reflection property order IS unstable.** Building identical source twice, changing only
the compile order of two files of a `partial` class:

| Build | unsorted hash | sorted hash |
|---|---|---|
| A | `6eabdd8f…` | `dede5380…` ✅ |
| B | `9a8e2f19…` ❌ | `dede5380…` ✅ |

Stable across separate processes, `tr-TR`, invariant globalization, and Debug vs Release. The ordinal
sort is the only thing preventing random production failures on rebuild, in a codebase where
`partial` classes and source generators are everywhere.

**Cross-OS: MEASURED, and it matches.** A self-contained spike referencing the real
`MintPlayer.Spark.Abstractions` was published for `win-x64` and `linux-x64` and run on Windows and on
Linux under WSL, over the same HR model directory. Every per-file hash, the roll-up, and the CLR
shape hash were identical:

```
Windows  os=Win32NT newline=CRLF   combined=987acb6429717acaccb885089dfa477a12ab3bf3b3423aaab521e50ccffed88c
Linux    os=Unix    newline=LF     combined=987acb6429717acaccb885089dfa477a12ab3bf3b3423aaab521e50ccffed88c
```

The git-autocrlf case was measured too: LF-converted copies of the same files, hashed on Linux,
produced that same roll-up. So a hash written on a developer's Windows machine verifies inside a
Linux container.

Newlines are additionally collapsed (`\r\n` and lone `\r` → `\n`) in every string that reaches either
hash. Parsing the JSON already removes the file's own line endings, so this only covers newlines
carried *inside* a value — but the cost of missing one is a deployment that will not start, and
normalising is free. Removing it fails a test.

The pinned golden hash in `SparkModelShapeTests` remains the standing guard: CI runs on Linux, so it
re-checks platform stability on every run.

### R7 — `App_Data/modelHashes.json`, per-entity plus roll-up

```json
{
  "version": 1,
  "modelHash": "dede5380…",
  "contextRoots": "a4628cde…",
  "entities": { "Address": "3f1c1a18…", "Company": "12a83f74…", "Person": "c494bbbc…" }
}
```

**`App_Data/`, not `App_Data/Model/`.** `ModelLoader.cs:49` and `ModelSynchronizer.cs:229` both glob
`Model/*.json` and deserialize every hit as `EntityTypeFile`; a hash file there would log
`Error loading model file …` on every startup, next to a check that halts on model problems.
`HR.csproj:35` also feeds that glob to the Roslyn analyzers.

Per-entity hashes earn their place twice: the error message names the offending entity instead of
starting a hunt, and *one* entity drifting reads as a code change while *every* entity drifting reads
as a stale `App_Data` — a diagnostic unavailable from a single hash. `contextRoots` catches a
**removed** root, which per-entity hashes cannot see (the stale JSON file and its CLR class both still
exist and still agree).

The expected hash **must ship as a file**, never a source-generated compiled constant — a constant
would always agree with the assembly, making deployment skew undetectable.

### R8 — The check
Runs at startup, after the index registry is populated (`QueryType`/`IndexName` feed the hash).
Development → log a warning and continue. Everything else → throw `SparkModelOutOfSyncException`.

Development warns rather than skips: drift mid-edit is normal, so throwing would make the framework
hostile during the activity it exists to support — but silence means the developer's own machine never
tells them, and the first signal is CI. A warning is also the cheapest early detector if the hasher
ever becomes non-deterministic (R6).

**The check must not fire when `--spark-synchronize-model` is present.** Otherwise drift is
unrecoverable: the app will not start, and the only command that fixes it is the one that cannot
start. R1's builder-phase placement means sync returns before the check is reached; this must be
verified explicitly rather than assumed.

Fail **closed** on a missing hash file or missing model directory — `ModelLoader.cs:41-42` currently
returns an empty model silently, which would make the control bypassable by deleting one file.

### R9 — Escape hatch: `SPARK_MODEL_HASH_OVERRIDE`, value-based
Set it to the **actual** hash from the error message. Not a boolean.

Self-expiring by construction: the value is this build's hash, so the next model change makes it wrong
and the app throws again. It therefore cannot be baked into a Helm chart or base image and forgotten —
which is exactly how a `CHECK=Off` flag rots into a permanent default. A wrong or stale value fails
closed. Logged at Warning on **every** startup, never log-once.

**No `Spark:ModelHashCheck=Off` config key.** Offering both means the durable, greppable,
copy-pasteable one becomes the one everybody uses.

### R10 — `--spark-verify-model` for CI
Writes nothing; exits non-zero on drift. Preferred over `synchronize && git diff --exit-code`:
it leaves the workspace pristine, needs no git index (a queue may build from a tarball or container
layer), and `git diff --exit-code` returns 1 for *any* dirt — a restore-touched lockfile would report
"model out of sync".

| exit | meaning |
|---|---|
| 0 | in sync / synchronize succeeded |
| 1 | synchronization threw (type mismatch `:329`, breadcrumb validation `:567`) |
| 2 | misconfiguration — no `SparkContext` descriptor, no parameterless ctor, registry population failed |
| 3 | drift detected (verify mode only) |

```
dotnet build Demo/HR/HR/HR.csproj -c Release
dotnet run --project Demo/HR/HR --no-build -c Release -- --spark-verify-model
```

`--no-build` matters: without it `dotnet run` rebuilds, and the build consumes `App_Data/Model/*.json`
as `AdditionalFiles` (`HR.csproj:34`).

### R11 — Args are read from `string[]`, never `IConfiguration`
**Measured:** a bare `--spark-synchronize-model` is *silently dropped* by
`CommandLineConfigurationProvider` — zero keys, no exception. The space-separated form works but is
**greedy**: in one run `--urls=http://localhost:5099` was swallowed as the flag's value and never
applied. `WebApplication` does not expose args at all; they are not even reflectable.

### R12 — Synchronization must be a fixed point
Synchronization is a read-modify-write over its own output, so the invariant is
`Serialize(Load(Serialize(x))) == Serialize(x)`. Run 1 legitimately differs from its input — it mints
ids, synthesizes a breadcrumb and materialises defaults — but run 2 and run 3 must be byte-identical.

Two mechanisms break it, and **either alone is sufficient**:

1. **Write-side omission.** `DefaultIgnoreCondition = WhenWritingNull` makes "null" and "absent" the
   same bytes.
2. **Read-side derivation.** The load path invents a value the write path never produced.

The design rule that follows, and the one worth remembering: *deriving a value on load is correct for
a pure reader and poison for a read-modify-write tool.* `ModelLoader` and `QueryLoader` derive
`Alias` and `EntityType` on load and never write, so they are fine permanently. The synchronizer's own
load path must not.

**Two defects were found and fixed.**

*`SparkQuery.EntityType` was omitted on create and derived on load.* A newly created query left the
field null, so it was omitted; the next run read the file, filled it in via `??=`, and wrote it back.
Measured `run1 == run2: False`, `run2 == run3: True`. Fixed by setting it at creation.

*Inline queries duplicated without bound.* `existingQueries` is the flat concatenation of every model
file's inline queries, re-partitioned by `EntityType` with no de-duplication. Normally
self-correcting, because the file a query was wrongly in is rewritten without it. It stops being
self-correcting when a file is **never rewritten** — an orphan whose type is no longer a context root
nor a reachable embedded type, which is what removing or renaming an entity without deleting its JSON
produces. Measured:

```
run1: 2 queries, 1449 bytes    run3: 4 queries, 2207 bytes
run2: 3 queries, 1828 bytes    run4: 5 queries, 2586 bytes     +1 query, +379 bytes per run
```

Also reachable via a case-only difference in `entityType` (the match is `OrdinalIgnoreCase`) and by
renaming a model file. Fixed by de-duplicating on query id — two entries with the same id are the
same query, so keeping the first is always correct. Entries sharing only a `Name` are deliberately
left alone: that is an ambiguous model worth surfacing, not silently resolving.

**Everything else converges.** Measured across every scalar type, nullable variants, get-only
properties, `[IgnoreProperty]`, references with and without explicit queries, lookups, sortable
arrays, embedded and nested-embedded types, projections through a real `IndexRegistry`, breadcrumbs
present and synthesized, and hand-authored renderer/rules/tabs/groups: `run1 == run2 == run3`
throughout. `order` normalisation and minimal seeds are one-shot canonicalisations. Running the real
command against all four demos gives `committed → run1: no diff` and `run1 → run2: no diff`, so the
repository itself sits at the fixed point.

**Guard.** `SynchronizeIdempotencyTests` asserts the fixed point over three fixture shapes, including
a **minimal** seed with every optional field absent — a fully-populated fixture cannot catch this
class, because the mechanism is a field missing on write and derived on read. Verified to fail
(`found 4` instead of `2`) when the de-duplication is removed.

Chosen over the alternatives: an in-synchronize round-trip assertion would run against real consumer
models but only fires *after* a regression has shipped, whereas the test gates Spark's own CI; and an
analyzer for "a field derived on the read path must also be set on the write path" is interprocedural
and cross-assembly — a whole analyzer project, high false-positive rate, for one rule.

Not adopted: making every field always-persist (dropping `WhenWritingNull`). It does not even fix the
bug — `??=` fires on an explicit `null` just as it does on an absent field — and it would add roughly
37% to every model file while burying hand-authored lines under nulls, against the stated intent that
model JSON is an editing surface.

### R13 — The model must not misdescribe the code

Three defects let synchronization produce a model that described something other than the code. All
were byte-stable, so neither the idempotency guard nor the verify gate could see them — a gate that
compares a value against itself confirms whatever is there.

**Stale projection references.** `QueryType`/`IndexName` were only ever set, never cleared, so
deleting a `[FromIndex]` projection left a reference to a type that no longer existed — and since
both feed the structural hash, verification confirmed it. Now assigned unconditionally, including
back to null, with the clear logged.

Safe because **nothing reads them**: a full-tree sweep found the sole reader is `ModelFileShape`, the
hash itself. Every runtime consumer resolves projections through `IIndexRegistry`, and the TypeScript
model does not declare the fields at all. Clearing is consistent rather than destructive, because
synchronization and the running app populate that registry from the same entry assembly — so a
projection absent at sync time is absent at runtime too.

**A name collision deleted a live model file.** The stale-projection cleanup deletes
`{ProjectionType.Name}.json`; model files are keyed by *simple* type name, so an entity sharing that
name resolved to the same path. Running after all writes, it deleted a file the same run had produced
and reported success. It now skips anything written during the run.

**A duplicate queryable root silently lost a query.** Two context properties of one entity type map
to one file; queries came from a snapshot taken before any write, so the second write dropped the
first's. Measured: the file was stable from run 1 with an unchanging hash, permanently carrying only
the last property's query — a wrong answer that never changes, and therefore invisible to every gate
that works by comparing runs. Properties are now grouped by entity type, one write per file, one
query per property.

**Two residuals, deliberately not changed.**

*A projection-only attribute keeps its flags when the projection is deleted.* It becomes an orphan
with no CLR property, so #253 carries it over verbatim, retaining `inCollectionType: false` and
`showedOn: Query`. Left alone on purpose: `inCollectionType: false` is *accurate* — the attribute
genuinely has no CLR property — and an index-computed virtual attribute is legitimately query-only,
so clearing these would destroy valid configuration for the exact scenario #253 exists to support.
Synchronize already warns that the attribute was kept.

*A `[FromIndex]` projection declared in a referenced library is invisible to the registry*, because
`PopulateIndexRegistry` scans only `Assembly.GetEntryAssembly()`. Its `queryType` is therefore cleared
on every sync. Not a functional regression — the running app resolves projections from that same
registry and equally cannot see it, so the field had no reader — but it is silent JSON churn for such
an app, which is why the clear is logged. Relevant because libraries *can* contribute persistent
objects.

### R14 — Index and projection discovery must reach beyond the entry assembly

Spark discovered RavenDB indexes and `[FromIndex]` projections by scanning
`Assembly.GetEntryAssembly()` only. A module shipped as a class library therefore got **none** of it:

- its index was never created — `IndexCreation.CreateIndexes` is entry-assembly scoped too, inside a
  catch-all that logs and continues;
- its projection was never registered, so all three `ProjectInto` sites were skipped
  (`DatabaseAccess.cs:153`/`:421`, `QueryExecutor.cs:132`, `QueryExecutor.cs:266`);
- so index-**computed** fields came back null, with the right row count and no error — the symptom
  RavenDB gives when you query an index without projecting, which reads as a broken index.

Index-side filtering was lost too (more rows than intended), sorting on a projection-only column
silently no-opped, and **no consistency check could catch any of it**: synchronization and the runtime
read the same empty registry, so both agreed and the hashes matched. The framework's own guide shows
the entity in a library but the index and projection in the entry assembly — teaching the workaround
without ever saying it is one.

This is an inconsistency rather than a deliberate limit: seven other runtime resolvers
(`ActionsResolver`, `CustomActionResolver`, `LookupReferenceDiscoveryService`, `FindClrType`,
`ResolveType`) already scan `AppDomain.CurrentDomain` and find library types fine. Only the index and
projection registry was narrower.

**Design: declare → materialize → apply.**

| Phase | Does | Store? | Runs where |
|---|---|---|---|
| **Declare** | records that an assembly contributes indexes/projections | no | modules, inside their `AddXxx` |
| **Materialize** | resolves index and projection types into the registry | no | **both** paths |
| **Apply** | deploys indexes to RavenDB | yes | runtime only |

The split is the load-bearing part. A design that resolves assemblies into instantiated index objects
*at declaration time* needs a document store there, which would lock the build-time sync command out
entirely — it has no store and no container.

**Ordering comes free from the existing lifecycle.** Modules accumulate during `configure(builder)`
(`SparkMiddleware.cs:126`); the registry is snapshotted into DI at `:129`; both consumers — the
build-time command and `UseSpark` — run strictly afterwards. ASP.NET's phase boundary enforces this,
rather than the author's line ordering.

Three ways a declaration can still arrive too late, all avoided by construction:
1. declared inside an `AddMiddleware` callback — those run at `ApplyMiddleware` (`:297`), after
   `CreateSparkIndexes` (`:288`), so **both** consumers miss it;
2. declared through `UseSpark(Action<UseSparkOptions>)`, which runs `UseSpark()` before the callback;
3. `SparkEndpointFactory` wrote the model hash in its **constructor**, before `AddSpark` — so a test
   host could never see its own declaration. Fixed in the same change.

**Defaults are unchanged.** With nothing declared, the resolved list is exactly `[entryAssembly]` —
what Spark scanned before. Declarations **append** to the entry assembly rather than replacing it;
substituting (as the replication `AssembliesToScan` precedent does) would silently drop an app's own
indexes the moment it added a module.

**Materialize runs in two passes** — every assembly's indexes, then every assembly's projections — so
a projection in one assembly over an index in another registers regardless of declaration order.
Otherwise it lands on a `Console` warning nobody reads.

**Failure policy per phase.** Declare cannot fail. Materialize does not swallow (registry population
is a correctness precondition) with one narrowing: `ReflectionTypeLoadException` keeps the loadable
types and warns, because a partially-loadable assembly is a deployment fact, not a Spark defect — and
the catch must sit *inside* the `ReflectionCache` factory, or the failure is cached and re-thrown for
the process lifetime. Apply stays best-effort but per assembly, so one unreachable module no longer
costs every other module its indexes.

---

## Decisions

### D1 — A dev-only NuGet package was investigated and rejected
`<DevelopmentDependency>true</DevelopmentDependency>` does **not** keep a `lib/` assembly out of a
consumer's publish output. Measured — packed, consumed from a local feed, published:

```
ConsumerA.dll  ConsumerA.exe  ConsumerA.runtimeconfig.json
DevLib.dll          <-- SHIPPED
```

The flag governs the *outbound* package graph (non-transitivity), not the *inbound* asset graph. The
repo's two existing `DevelopmentDependency=true` projects only appear to work because they also set
`IncludeBuildOutput=false` and pack to `analyzers/` — a packed listing shows **no `lib/` folder at
all**. That precedent does not transfer: model synchronization must be callable API, not a generator.

And forcing the assembly out (`ExcludeAssets="runtime"`) breaks startup. Assembly loading is lazy per
*method JIT*, not per call site — a false `if` does not save you:

```
Unhandled exception. System.IO.FileNotFoundException: Could not load file or assembly 'DevLib…'
   at Program.<Main>$(String[] args)
```

thrown before the first line of `Main`. Since the AllFeatures generator injects the call into every
app's startup path, that is a crash on every production boot regardless of args.

`PrivateAssets="all"` is a trap worth recording: it removes the DLL from `publish/` but leaves it in
`bin/`, so F5 works and only the deployed app breaks.

### D2 — `SparkApplication.CreateBuilder` was considered and dropped
`WebApplicationBuilder` is sealed (`CS0509`), so a wrapper would be a five-member pass-through that
breaks `builder.Configuration.AddJsonFile(...)` (it is a `ConfigurationManager`, not an
`IConfiguration`) and every third-party `builder.AddServiceDefaults()`-style extension.

More decisively, it delivers **conventional** unreachability, not structural: one `IsDevelopment()`
that `ASPNETCORE_ENVIRONMENT=Development` on a prod box re-arms. And it introduces a silent-loss
footgun — a consumer who keeps plain `WebApplication.CreateBuilder` still compiles, still runs, and
silently has no synchronizer, with no compile-time signal. R4's `AddSparkCore` gate achieves the same
guarantee for *all* apps without a new entry point.

### D3 — Hash the CLR shape, not the model JSON
A JSON-content hash is not merely weaker here — it is **incompatible with #253**, which just shipped
on this branch. `ModelSynchronizer.cs:401-450` never reassigns `Label`, `Renderer`, `RendererOptions`,
`Group`, `EditMode`, `Rules`, `ColumnSpan` or `Id`, and `:501-528` carries over whole attributes with
no CLR property. Hand-authoring model JSON is a supported, first-class workflow. A content hash would
refuse to start production after a French label translation.

Deployment skew — the strongest argument for a JSON hash — is covered anyway: stale `App_Data` carries
a stale `modelHashes.json`, the new binaries hash differently, mismatch caught.

**No second JSON hash either.** Two hashes with different meanings and different remedies is an
incident-response liability at 3am, and doubles the false-positive surface of a mechanism that halts
the app.

### D4 — Vidyano does not actually refuse to start
Recorded because the feature was requested by analogy. The decompiled source has exactly one
comparison — `PersistentObjectActions.cs:351-356` — which adds a **Notice-level banner** in the admin
UI when a developer opens the Schema page: *"It appears that your Entity model has changed, use
Synchronize to fix this."* Requests keep serving. There is no startup validation and no CI
verification in ~40 consuming repos. The hard failures people remember are a *missing* `model.json`
(`VidyanoModelContext.cs:111`) and `SeederHash`/repository-version mismatches
(`InitializeArgs.cs:37-42`) — different concerns.

So **G1 is stricter than the precedent**, deliberately. Two things Vidyano does confirm:
its hash is over the CLR reflection shape (`EntityModel.cs:270-273`), never the generated JSON — the
property that makes generator-free verification possible; and its regeneration command is documented
*"No DB required"*, matching G2.

Vidyano also hit the merge-conflict problem and solved it by **sharding**, not tooling: per-persistent-object
hashes landed Feb 2026 under the commit message *"Enable PerPO ModelHash for reduced merge conflicts"*.
No merge driver, no gitignore, no CI. That validates R7's per-entity design.

### D5 — Merge conflicts are accepted, mitigated by sharding
Per-entity hashes mean two PRs touching different entities land on non-adjacent lines of a sorted map
and merge cleanly. Two PRs touching the *same* entity conflict — but they already conflict in that
entity's JSON, so nothing new. Residual: `modelHash`/`contextRoots` conflict on essentially every pair,
resolved by re-running synchronize.

`.gitattributes` merge drivers were rejected: `merge=ours` needs each developer to define the driver in
*local* git config, so it fails open per-machine. Gitignoring and generating at build was rejected
outright — generated from the same CLR types the check reads, the comparison becomes a tautology that
passes forever and looks like it works.

---

## Risks

| Risk | Mitigation |
|---|---|
| Non-deterministic hash halts production at random | R6 invariants, pinned by a `partial`-class fixture test. **Cross-OS/cross-machine still unmeasured — release gate.** |
| Check fires but the fix command cannot run | R8: sync returns before the check is reached; verified explicitly, not assumed |
| Override becomes a permanent deployment default | R9: value-based and self-expiring; no boolean off-switch |
| `App_Data` not deployed → check throws on a healthy app | Verified: `App_Data/Model/` is copied to build output; the Web SDK includes it as Content with `CopyToPublishDirectory=PreserveNewest` (documented at `MintPlayer.Spark.csproj:50-56`) |
| Orphan chain: removing a property never cleans the model | Documented, not fixed. Synchronize preserves the orphan and warns (`:511-513`, `:521-527`); recovery is "re-run synchronize **and read what it printed**" |
| Synchronize not byte-stable across runs | **Measured and fixed** — see R12 |

---

## Breaking changes (pre-1.0 preview, `10.0.0-preview.50`)

| Removed / changed | Replacement |
|---|---|
| `SynchronizeSparkModels<TContext>()` public | internal |
| `SynchronizeSparkModelsIfRequested<TContext>(args)` public | `builder.SynchronizeSparkModelsIfRequested(args)` |
| `UseSparkOptions.SynchronizeModelsIfRequested<TContext>(args)` | none needed |
| `UseSparkFull(string[] args)` → `UseSparkFull()` | Fleet `Program.cs:124` only |
| `IModelSynchronizer` resolvable in any environment | Development only |

`UseSparkOptions` and the `UseSpark(Action<UseSparkOptions>)` overload are **kept** — a legitimate
extension point; only the one method is removed.

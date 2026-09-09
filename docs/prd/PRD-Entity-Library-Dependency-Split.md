# PRD — An entity library should not depend on ASP.NET Core

**Status: NOT STARTED.** Depends on `MintPlayer.Spark.Attributes` existing, which
`PRD-AsDetail-Row-Identity.md` A1 creates.
**Origin:** sidestepped from PR #381; the attribute split in `a47c3118` is step one of this, and on
its own it benefits exactly one project.

---

## 1. Problem

`MintPlayer.Spark.Abstractions` is `Sdk="Microsoft.NET.Sdk.Web"`, and legitimately so — five of its
88 source files genuinely use ASP.NET Core:

- `Authentication/SparkCompositeAuthenticationHandler.cs:2`
- `Authentication/SparkCredentialSchemeExtensions.cs:1-2`
- `Authentication/SparkSystemContext.cs:1,40` (`IHttpContextAccessor`)
- `Builder/SparkModuleRegistry.cs:7,79,138` (`IApplicationBuilder`)
- `Builder/ISparkBuilder.cs:4-5` (`IServiceCollection`, `IConfiguration`)

The Web SDK puts a hard framework reference in the assets file — `frameworkReferences:
['Microsoft.AspNetCore.App', 'Microsoft.NETCore.App']` — and that flows transitively. So a library
that declares nothing but entities acquires `Microsoft.AspNetCore.App`, **cannot target
`netstandard2.0`**, and cannot be loaded by a host without the ASP.NET Core shared framework. The
side effects are visible in the build: a `staticwebassets.endpoints.json` in the output, and a
`Directory.Build.targets:2-5` that exists purely to suppress `launchSettings.json` for Web-SDK class
libraries.

**Moving the attributes out is necessary but not sufficient.** Measured, of the four entity
libraries:

| Library | attribute uses | other Abstractions types used |
|---|---|---|
| `HR.Library` | 10 | **none** — can drop the reference today |
| `CodeCoverage.Library` | 17 | `TransientLookupReference`, `ELookupDisplayType`, `DynamicLookupReference` |
| `DemoApp.Library` | 4 | `ELookupDisplayType`, `TransientLookupReference`, `DynamicLookupReference` |
| `Fleet.Library` | 11 | those three plus `TranslatedString` |

So after the attribute split, **three of four libraries still drag in the ASP.NET Core shared
framework for an enum**.

⚠️ And even `HR.Library` does not actually become clean, because it references
`MintPlayer.Spark.Replication.Abstractions`, whose csproj `ProjectReference`s Abstractions and
inherits the framework reference. Fleet does the same. The attribute split does not notice this.

## 2. Design

### D1 — Move the four dependency-free model types

`TranslatedString` (only `System.Text.Json`), `TransientLookupReference` (a POCO over
`TranslatedString`), `DynamicLookupReference` (POCOs, no usings), `ELookupDisplayType` (a bare enum).

Destination is a naming decision: widen `MintPlayer.Spark.Attributes` beyond what its name says, or
add `MintPlayer.Spark.Model` and have `Attributes` depend on it. Prefer the second — an assembly
called Attributes containing `TranslatedString` is the kind of thing that reads as an accident later.

⚠️ **Keep the namespace `MintPlayer.Spark.Abstractions`,** as the attribute split did. 50 occurrences
of 24 distinct fully-qualified `"MintPlayer.Spark.*"` metadata names live in the generators and
analyzers, plus ~15 `global::` prefixes written into *generated code*. Every one is immune to an
assembly move and fatal to a namespace move. That property is load-bearing and belongs in the new
package's README, not only in a PRD.

### D2 — Break `Replication.Abstractions`'s dependency

`ReplicatedAttribute` is a pure POCO attribute; the package pulls in Abstractions for other reasons.
Either drop that reference or move `[Replicated]` into the attributes package. Until this is done,
Fleet and HR keep the framework reference no matter what else moves.

### D3 — ✅ Resolve the marker assembly, never name it — ALREADY DONE

`GenerateIndexGenerator` filtered candidate assemblies by literal name, and that is the bug that made
HR's indexes vanish when the attributes moved. **The row-identity work already replaced it with the
derived form** (`e75b1647`), so this PRD's extra package cannot repeat the failure:

```csharp
compilation.GetTypeByMetadataName(GenerateIndexAttributeFullName)?.ContainingAssembly?.Name
```

The legacy name is *not* also accepted — there is no backward-compatibility requirement, and keeping
it would have been the same asserted-literal habit in a smaller form.

**The general rule, worth writing down once:** every other name-based check in this repo keys on
*namespace*, which `GetTypeByMetadataName` and `ToDisplayString` make cheap and safe. The single
*assembly*-name literal is the one that broke. Never filter by assembly-name literal — resolve the
marker type and ask which assembly it came from.

### D4 — Let the libraries target `netstandard2.0`

The point of the exercise. Only meaningful once D1 and D2 are both done.

### D5 — Non-goals

- Moving `SparkAuthorizeAttribute`. It derives from ASP.NET Core's `AuthorizeAttribute`, implements
  `IAuthorizationRequirementData`, is not model vocabulary, and getting its derivation wrong fails
  open.
- Splitting Abstractions' genuinely web-dependent five files into their own package. Possible, not
  obviously worth it.

## 3. Risks

⚠️ **The publishing hazard is the sharp edge, and it applies to every package move.**
`.github/workflows/dotnet-build-master.yml:82-83` runs a solution-wide `dotnet pack` with no project
argument and `:148-149` pushes `nupkgs/*.nupkg` with `--skip-duplicate`. `pull-request.yml` has **no
pack step at all**, so package shape is never exercised before master.

Consequence: if the version is not bumped, the new packages publish (new ids, they succeed) while the
*changed* Abstractions hits `--skip-duplicate` and **silently keeps the old package — the one that
still physically contains the moved types**. A consumer referencing both then gets CS0433, the type
exists in two assemblies. Every package moves in lockstep and the preview number must be bumped in
the same PR.

⚠️ **A duplicate declaration is worse than a compile error for the generators.**
`GetTypeByMetadataName` returns `null` on ambiguity, so with an old Abstractions and a new package on
one graph, `GenerateIndexGenerator.cs:108`/`:459`, `HostTranslationsAggregatorGenerator.cs:30`,
`ProjectionPropertyAnalyzer.cs:30,54` and `AttributeDescriptionsGenerator.cs:82` all **silently
switch off**. Indexes and translations vanish with no diagnostic. `[TypeForwardedTo]` on every moved
type removes this entirely and costs one line each.

⚠️ **The attribute split shipped without forwarders** (`19c4c4b2`), since there is no
backward-compatibility requirement. Everything below applies only if that changes.

⚠️ These attributes and model types are read by **runtime reflection** —
`ModelSynchronizer.cs:684-686`, `ReferenceResolver.cs:15` — so a pre-built consumer assembly does not
fail loudly against a new Abstractions; it produces a **wrong model**. This is the strongest argument
for type forwarders, and it is independent of the preview-grade "breaking changes are fine" stance,
because the break is not one a consumer can see.

## 4. Acceptance criteria

1. All four entity libraries build with **no** reference to `MintPlayer.Spark.Abstractions`.
2. At least one of them targets `netstandard2.0` and builds.
3. `tests/.../Generators/ReferencedAssemblyEntityTests.cs:18-28` passes — it already models the
   exact HR shape (an entity using `[GenerateIndex]`/`[Search]` and nothing else Spark) and would
   have caught the original breakage. Use it as the gate, not a full solution build.
4. Every package version bumped in lockstep in the same PR.
5. `[TypeForwardedTo]` present for every moved type, verified by loading a pre-split consumer
   assembly against the new packages and confirming the model is still correct.
6. The generator's assembly filter is derived from the resolved attribute symbol, not a literal —
   verified by adding a hypothetical third attribute host in a test and seeing nothing break.

## 5. Also worth doing while here

- **No docs mention which package an entity library should reference.** `guide-reference-attributes`,
  `guide-asdetail-attributes`, `guide-attribute-descriptions`, `guide-attribute-grouping`,
  `guide-translated-strings` all still say Abstractions. No `AGENTS.md` under the new directory.
- **Add a pack step to `pull-request.yml`** so package-shape regressions on a new project surface
  before nuget.org rather than after.

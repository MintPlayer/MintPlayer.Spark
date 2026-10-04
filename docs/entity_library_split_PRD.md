# PRD: an entity library should not depend on ASP.NET Core (#388)

Issue: [#388](https://github.com/MintPlayer/MintPlayer.Spark/issues/388) · PR: [#484](https://github.com/MintPlayer/MintPlayer.Spark/pull/484)
(branch `fix/bs-select-full-width`; #388 is the main issue, the ng-bootstrap 22.21.1 bump rides along).
Plan: [entity_library_split_plan.md](entity_library_split_plan.md).
**Supersedes** `docs/prd/PRD-Entity-Library-Dependency-Split.md` and `docs/prd/plan-entity-library-dependency-split.md`
(written at #382, before #460/#465/#466/#483). Their stale statements are listed in §8.

Status 2026-10-04: **implemented (M1–M8), local sweep green** (§9). Implementation findings are marked
"Found during implementation" / "Built". Every planning claim below was re-verified against `master` @ `9e2f32bd`
by three read-only investigations on that date.

## 1. Problem

A project that declares nothing but entities still gets the ASP.NET Core shared framework, so a
host without that framework (a console tool, a worker, a test fixture) cannot load it. The reason is that
`MintPlayer.Spark.Abstractions` is `Sdk="Microsoft.NET.Sdk.Web"`
(`libs/spark/MintPlayer.Spark.Abstractions/MintPlayer.Spark.Abstractions.csproj:1`), and the framework
reference flows transitively to everything that references it.

The Web SDK is legitimate in Abstractions. **Six** of its files use ASP.NET Core or Microsoft.Extensions;
the issue said five, and the sixth is new since:
`Authentication/SparkSystemContext.cs:1,40-41`, `Builder/SparkModuleRegistry.cs:7,9,98,118,157,170`,
`Builder/ISparkBuilder.cs:5-6`, `Authentication/SparkCompositeAuthenticationHandler.cs:2-3,43-44`,
`Authentication/SparkCredentialSchemeExtensions.cs:1-2`, and `Interceptors/SparkBuilderInterceptorExtensions.cs:1,3,31,36`.
The entity libraries are the ones paying for it.

There are **five** entity libraries now (the issue said four: `QnA.Library` is new). This is
everything each one pulls in that drags ASP.NET Core in:

| Library | Abstractions types used | Other dependencies that drag ASP.NET Core in |
|---|---|---|
| `HR.Library` | none (attributes only) | `Replication.Abstractions` → Abstractions |
| `DemoApp.Library` | `DynamicLookupReference`, `TransientLookupReference`, `ELookupDisplayType` | none (`Messaging.Abstractions` is clean) |
| `Fleet.Library` | the above, plus `TranslatedString` (`Entities/Car.cs:99`) | `Replication.Abstractions` → Abstractions |
| `CodeCoverage.Library` | `TransientLookupReference` (5 lookups), `DynamicLookupReference`, `ELookupDisplayType` | **`MintPlayer.Spark.Authorization`** (Web SDK) for `[Reference(typeof(SparkUser))]` (`Entities/ApiToken.cs:130`, csproj :21-29); **`MemoryCache`** (`Services/SourceContentCache.cs:1,42`) with no PackageReference, so it resolves only through the shared framework |
| `QnA.Library` | none directly | **`MintPlayer.Spark.History`** (references the whole `MintPlayer.Spark`, csproj :38) for `IAuditable`; `Moderation.Abstractions` → Abstractions; `Contributions.Abstractions` → Abstractions; `[Newtonsoft.Json.JsonIgnore]` (`Question.cs:78`) with no PackageReference |

Apart from the four model types, no entity library uses anything from Abstractions. Every public
Abstractions type name was grepped against every library source; the only other hit is a comment,
`ApiToken.cs:17`. No consumer gets Abstractions *only* through an entity library: every host app
also references `MintPlayer.Spark` (§4 of the consumer investigation).

## 2. Goal

All five `apps/*/*.Library` projects build without the `Microsoft.AspNetCore.App` framework
reference, directly or transitively. A build guard enforces this, so it cannot regress silently.
The guard is also the proof that the dependency chain underneath each library is really free.
Everything stays on `net11.0` (§3.4).

Owner decision 2026-10-04: **all five** libraries, in this PR. Freeing only DemoApp, Fleet and HR
was the other option.

## 3. Design

Two new plain-`Microsoft.NET.Sdk` packages. Types move **by assembly only. Every namespace stays
as it is.** That is the same technique #382 used for `MintPlayer.Spark.Attributes`, whose files declare
`namespace MintPlayer.Spark.Abstractions;` (e.g. `ValueKeyAttribute.cs:1`).

### 3.1 `MintPlayer.Spark.Model` (new, `libs/model/MintPlayer.Spark.Model`)

This holds the vocabulary types an entity library, or a feature `*.Abstractions` package, actually uses:

| Type(s) | From (Abstractions) | Why it has to move |
|---|---|---|
| `TranslatedString` + `TranslatedStringJsonConverter` | `TranslatedString.cs:6-7,59-113` | Fleet uses it. The converter is in the same file and is referenced by `[JsonConverter]` |
| `TransientLookupReference`, `TransientLookupReference<TKey>` | `TransientLookupReference.cs:3,20` | used by DemoApp, Fleet and CodeCoverage |
| `DynamicLookupReference`, plus `EmptyValue` and `LookupReferenceValue<TValue>` | `DynamicLookupReference.cs:3,5-11,13,17` | `EmptyValue` and `LookupReferenceValue<TValue>` are in its dependency closure, declared in the same file |
| `ELookupDisplayType` | `ELookupDisplayType.cs:3` | plain enum |
| `ValidationError` | `ValidationError.cs:3` (uses `TranslatedString`) | `Contributions.Abstractions` `IContributions.cs:37` |
| `SparkCoreActions`, `SparkCombinedActions`, `SparkReservedActionsAttribute`, `SparkNotAnActionAttribute` (namespace `…Abstractions.Authorization`) | `Authorization/SparkCoreActions.cs`, `SparkCombinedActions.cs`, `SparkReservedActionsAttribute.cs:23,40` | `Moderation.Abstractions` `ModerationRights.cs:3,15,46,59`, `Contributions.Abstractions` `ContributionRights.cs:3` |

The `[assembly: SparkReservedActions(typeof(SparkCoreActions))]` and `(typeof(SparkCombinedActions))`
declarations (`SparkCoreActions.cs:3-4`) move with the constants, into the Model assembly. See R1.

`Attributes` keeps holding attributes only, as the issue asked. The owner confirmed on 2026-10-04 that
the vocabulary goes into a new `MintPlayer.Spark.Model` package rather than into a wider Attributes package.

**Found during implementation (2026-10-04): five more types had to move.** Each one was proven by a
compile error, not predicted; the read-only investigations missed all of them.

| Type(s) | Why | Evidence |
|---|---|---|
| `EShowedOn`; `SparkModelSatellites` + `SparkSatelliteModelType`, `SparkSatelliteQuery`, `SparkRendererSeed`, `SparkNewAttributeSeed` (namespace `…Abstractions.Model`) | Contributions.Abstractions registers its generated types as model satellites at run time (`ContributionDescriptor.cs:264-298`) | CS0234 building Contributions.Abstractions on Model |
| `SparkQueryAliases.Derive` | `ContributionDescriptor.cs:42` derives the contributions query's alias. **Split:** `Derive` moves to Model under the same class name; `Index`, which needs `SparkQuery`, stays in Abstractions as `SparkQueryAliasIndex.Index` (2 callers: `QueryLoader.cs:37`, `SparkDevelopmentExtensions.cs:926`). The 10 `Derive` callers are unchanged | same |
| `SparkValueObjects` (namespace `…Abstractions.Model`) | The library-side `[ValueObject]` key generator emits `SparkValueObjects.Register` into the entity library | CS0234 in `SparkValueObjectKeys.g.cs` of CodeCoverage/Fleet/HR.Library |
| `IHasNaturalId` | The contributions generator emits an explicit `IHasNaturalId` implementation into QnA.Library | CS0538 in `QnA.Entities.QuestionTranslations.Contribution.g.cs` |

Those are all the fully qualified Abstractions names the library-side generators
(LibraryGenerators, Contributions.SourceGenerators) emit: `IHasNaturalId`, `SparkValueObjects.Register`
and `ValueKey`, which is the attribute already in Attributes.

### 3.2 `MintPlayer.Spark.Authorization.Abstractions` (new, `libs/authorization/MintPlayer.Spark.Authorization.Abstractions`)

This holds the Raven user/role **document model**, so that `[Reference(typeof(SparkUser))]` works
without the Web SDK. The namespace stays `MintPlayer.Spark.Authorization.Identity`.

| Type | Verified web-free |
|---|---|
| `SparkUser` | its only ASP.NET mention is a doc comment (`SparkUser.cs:36`) |
| `SparkUserClaim`, `SparkUserLogin`, `SparkUserToken` | no ASP.NET references |
| `SparkUserPasskey` | mirrors `UserPasskeyInfo` in doc comments only (`:6-8`); deliberately not `IdentityUserPasskey<TKey>` |
| `SparkRole`, `SparkRoleClaim` | no ASP.NET references; moved so the whole user/role document model lives in one place |

`MintPlayer.Spark.Authorization` references the new package and keeps everything that needs the
Web SDK. That is 50 of its 63 files: `Endpoints/` (18), `Extensions/` (9), the Identity services
(`UserStore`×3, `RoleStore`, `SparkUserManager`, `SparkSignInManager`, `SparkUserNameValidator`,
`SparkExternalLoginLinker`, `SparkCredentialInventory`, `SparkUserBackfill`, `SparkAuthLinkBuilder`,
`SparkAuthMail`, `SparkAccountMail`), and `Configuration/` options binding. Owner decision 2026-10-04:
"an authorization abstractions package".

Out of scope for this package: the other web-free contracts (`SparkAccountContracts`,
`SparkRegistrationMethods`, `SparkPendingExternalLogin`, …). No entity library needs them, and moving
them would widen the package past what its name says.

### 3.3 Other package changes

| Package | Change | Evidence |
|---|---|---|
| `Replication.Abstractions` | Drop the Abstractions `ProjectReference` (csproj :22). Its only use is `SyncAction.cs:2,99,103`: `GetCachedProperties()`, `AccessorCache.GetGetter` and (missed by the investigation) `IsIgnoredForSparkModel()`, which is just "has `[IgnoreProperty]`". **Built:** references Attributes instead; `SyncAction<T>` keeps a static per-closed-type `PropertyInfo[]` (public, readable, non-indexer, no `[IgnoreProperty]`) and reads through `GetValue` | consumer investigation §2; build |
| `Contributions.Abstractions`, `Moderation.Abstractions` | Reference `Model` instead of Abstractions (csproj :29 / :30) | §3.1 table |
| **`MintPlayer.Spark.History.Abstractions`** (new, `libs/history/`) | Holds `IAuditable` (`libs/history/MintPlayer.Spark.History/IAuditable.cs:21`, namespace `MintPlayer.Spark.History` kept). `History` references it | QnA blocker |
| `CodeCoverage.Library` | Swap Authorization for Authorization.Abstractions. Add an explicit `Microsoft.Extensions.Caching.Memory` PackageReference (a standalone NuGet, not the shared framework). `Newtonsoft.Json` stays: it is used (`[JsonIgnore]`). **Found during implementation:** add an explicit `RavenDB.Client`. It used to arrive through Authorization, and without it `[GenerateIndex]` stops emitting the library's own copy of the indexes (gate at `GenerateIndexGenerator.cs:107-109`), which `CodeCoverage.GithubIntegration` aliases as `Indexes` (its csproj :24-34). CS0234 proved it | consumer investigation §1; build |
| `QnA.Library` | Swap History for History.Abstractions. Add an explicit `Newtonsoft.Json` PackageReference for `Question.cs:78` | consumer investigation §1 |
| All five libraries | Drop the Abstractions reference. Add explicit `Attributes`, plus `Model` where it is used (today they get Attributes transitively via `Abstractions.csproj:24`). **Built:** HR.Library needs Model too, despite using no Model type in source: its generated `[ValueObject]` keys register into `SparkValueObjects` | #388 S4; build |
| `Abstractions` | References `Model` (and keeps `Attributes`), so every existing consumer still sees every moved type | |
| `Directory.Build.targets:2-5` | Unchanged: Abstractions and Authorization are still Web-SDK class libraries | |

### 3.4 No `netstandard2.0` (#388 S5 dropped)

**Decision 2026-10-04 (owner): .NET Core only.** "We probably shouldn't support .NET Framework. Just
.NET Core purely." Every project stays on `net11.0`. The #388 S5 proof, "target netstandard2.0 on at
least one", is replaced by the build guard (§3.5), which proves the same thing: no ASP.NET Core
anywhere in the closure.

Spike S2 (2026-10-04), a throwaway multi-target build of Attributes, Model, Messaging.Abstractions
and DemoApp.Library on netstandard2.0 with PolySharp, found:
- Attributes and the Model types compile unchanged (PolySharp + System.Text.Json 10.0.12).
- `Messaging.Abstractions` `IMessageBus.cs:12,15,36` uses default interface members. That is CS8701 on
  netstandard2.0, a runtime feature with no polyfill. Supporting it would have meant changing the
  public API.
- Entities use `DateOnly` (`DemoApp.Library/Entities/Person.cs:17`), which does not exist on
  netstandard2.0. So the target would constrain the entities themselves, not just Spark.
- The spike was rerun to completion with `IMessageBus.cs` excluded and `DateOnly` stubbed. The build
  succeeded with **0 errors**, including the generated `SparkAttributeDescriptions.g.cs`. So those two
  are the *only* blockers: the generators and the Model/Attributes code are not.

The issue's other netstandard argument, "cannot be loaded by a host without that framework", is
fully met by dropping the ASP.NET Core framework reference on `net11.0`.

### 3.5 Build guard

An MSBuild target that runs for projects marked `<SparkEntityLibrary>true</SparkEntityLibrary>`
(the five libraries) fails the build when `Microsoft.AspNetCore.App` appears in
`@(FrameworkReference)` or `@(TransitiveFrameworkReference)`.

**Spike S3 (2026-10-04) confirmed the mechanism.**
- Where the reference shows up:
  - A transitive web reference reaches a plain-SDK project as a **`TransitiveFrameworkReference`**
    item, not a `FrameworkReference`. It is populated by `ResolvePackageAssets` from
    `project.assets.json`, where it sits on the referenced project's entry
    (`"MintPlayer.Spark.Abstractions/11.0.0-preview.94": frameworkReferences ["Microsoft.AspNetCore.App"]`).
  - Measured on today's master: HR.Library and DemoApp.Library have
    `FrameworkReference=Microsoft.NETCore.App | TransitiveFrameworkReference=Microsoft.AspNetCore.App`.
    Attributes has an empty `TransitiveFrameworkReference`.
- The guard:
  ```xml
  <Target Name="SparkEntityLibraryGuard" AfterTargets="ResolvePackageAssets" Condition="'$(SparkEntityLibrary)' == 'true'">
    <ItemGroup>
      <_SparkAspNet Include="@(FrameworkReference);@(TransitiveFrameworkReference)" Condition="'%(Identity)' == 'Microsoft.AspNetCore.App'" />
    </ItemGroup>
    <Error Condition="'@(_SparkAspNet)' != ''" Code="SPARKLIB001" Text="…" />
  </Target>
  ```
- **Red/green:**
  - Red on today's HR.Library: `error SPARKLIB001`, exit code 1 from both `dotnet msbuild` and `dotnet build`.
  - Green on Attributes and Messaging.Abstractions.
- After the move it must still be proven falsifiable: temporarily re-add the Abstractions reference
  to one library and watch the build fail. Neither a clean grep nor a green build proves anything on its own.
- **Proven after the move (2026-10-04, `aa0f777e`):** with the Abstractions reference re-added to
  HR.Library, `dotnet build` exits 1 with `error SPARKLIB001: HR.Library is an entity library
  (SparkEntityLibrary=true) but depends on the ASP.NET Core shared framework …`. With the csproj
  restored it exits 0. The whole solution builds with all five libraries marked.

### 3.6 Packages can ship migrations (needed for R4)

**Today they cannot.** Verified 2026-10-04:
- **Generators:** `MigrationRegistrationGenerator` (`SourceGenerators/Generators/MigrationRegistrationGenerator.cs:16-33`)
  and `SparkFullGenerator` (`AllFeatures.SourceGenerators/…/SparkFullGenerator.cs:98`) scan
  `context.SyntaxProvider`, so they only find classes in the app's own source.
- **Runtime:** there is no assembly scan. `SparkMigrationRegistry` holds only what the generated
  `AddMigration<T>()` calls register.
- **Apps without migrations:** an app with none of its own gets no generated `AddMigrations()` at all
  (`MigrationRegistrationGenerator.Producer.cs:27`).

The runner itself is ready for this:
- Every version gets its own applied-marker (`SparkMigrationRunner.cs:50-55`, `SparkMigrationRecords/{version}`),
  and every unapplied version runs, ordered by version. A package migration with an older timestamp
  therefore still runs on a database that is further along.
- A version clash throws at startup (`SparkMigrationRegistry.cs:22-25`), so it is loud.

**Design** (owner proposal 2026-10-04, "a source-generator … that looks through all referenced
assemblies"):
1. Both generators also walk `compilation.SourceModule.ReferencedAssemblySymbols`, the way
   `ReservedActionsReader.cs:40` already does. They pick up public, non-abstract types that implement
   `MintPlayer.Spark.Migrations.ISparkMigration`, and emit `migrations.AddMigration<global::…>()` for
   each one, after the app's own.
   - Only assemblies that themselves reference `MintPlayer.Spark.Migrations` are walked. That keeps
     the scan off the BCL and third-party packages.
   - The result is reduced to an equatable list of type names, so the incremental pipeline does not
     re-emit on every keystroke.
2. `AddMigrations()` is emitted when *either* the app or a referenced package has migrations, so QnA
   gets one.
3. Every app that stores users (CodeCoverage, HR, QnA) calls `spark.AddMigrations()`. QnA does not
   today (`Program.cs`), and it is added in this PR.
4. A package migration must be `public`, because the generated code in the app names it. A new
   analyzer warns on a non-public `ISparkMigration` in a project that is not an app (`OutputType`
   Library). Otherwise such a migration would be skipped silently.
5. A package migration uses the same `yyyyMMddHHmm` version scheme. A clash with an app's version
   fails startup with the existing message.
6. Authorization references `MintPlayer.Spark.Migrations`, which is plain SDK, so this adds no
   web dependency anywhere it matters.
7. **Tests:**
   - Generator tests: a referenced in-memory assembly (`GeneratorHarness.CompileToMetadataReference`)
     with a public migration is registered; an internal one triggers the warning; an app with no
     migrations of its own still gets `AddMigrations()`.
   - A runtime test: a fresh database records the package migration's marker.

## 4. Risks

- **R1. Silent loss of reserved actions and action layers. Found 2026-10-04; not in the issue.**
  - `SparkAssemblies.SparkAware()` (`SparkAssemblies.cs:17-37`) only scans assemblies that reference
    the assembly named `MintPlayer.Spark.Abstractions` (`:19`, `:36-37`). It feeds
    `SparkReservedActionRegistry` (`MintPlayer.Spark/Services/SparkReservedActionRegistry.cs:19`) and
    `SparkActionLayers` (`Abstractions/Actions/SparkActionLayers.cs:67`).
  - Once Moderation.Abstractions and Contributions.Abstractions reference only `Model`, their
    `[assembly: SparkReservedActions]` (`ModerationRights.cs:3`, `ContributionRights.cs:3`) would no
    longer be discovered. The core verbs would also vanish, since their declaration moves into the
    Model assembly.
  - Nothing would error: verbs would just stop being reserved.
  - **Fix:** `SparkAware` accepts a reference to any of `Abstractions`, `Model` or `Attributes`.
    Every other assembly-name-keyed scan must be found and fixed the same way (plan M3 audit).
  - Proven by a red/green runtime test: the registry contains `ModerationRights` and the core verbs.
  - The compile-time reader (`ReservedActionsReader.cs:29,40`) walks every referenced assembly and
    matches by metadata name, so it survives the move.
- **R2. Publish hazard (#388).** Master packs the whole solution (`dotnet-build-master.yml:88-89`) and
  pushes with `--skip-duplicate` (`:154-158`). If Abstractions were not bumped, it would keep the
  old package, which still physically contains the moved types. A consumer then gets CS0433, and
  `GetTypeByMetadataName` returns null on the duplicate (`GenerateIndexGenerator.cs:108`,
  `HostTranslationsAggregatorGenerator.cs:29`, `ProjectionPropertyAnalyzer.cs:31,55`,
  `AttributeDescriptionsGenerator.cs:79`), so generators switch off with no diagnostic. **Mitigations:**
  - a lockstep bump of every package to `11.0.0-preview.95`;
  - the existing PR gate `pull-request.yml:189-237`, which requires a `<Version>` bump in every
    changed `libs/` project;
  - ~~a new `dotnet pack` step on PRs, so package shape is exercised before master.~~ **Not done,
    owner decision 2026-10-04 ("leave as-is"), for CI cost:** the PR workflow builds only *affected* projects in *Debug* through Nx
    (`pull-request.yml:102-103`), so a solution-wide `dotnet pack --no-build` would fail there. A
    pack step therefore means a full Release build on every PR run. Not added. The bump gate already
    covers the `--skip-duplicate` hazard, and the lockstep bump (all 33 + 3 new at `.95`) is in this PR.
- **R3. Generators.** They are safe: no code compares an assembly name to `"MintPlayer.Spark.Abstractions"`.
  `TranslatedString` is matched by name plus namespace (`SparkModelSymbols.cs:26,28,171-173`), and
  generated code emits `using MintPlayer.Spark.Abstractions;` plus unqualified names
  (`HostTranslationsAggregatorGenerator.Producer.cs:28,35,45,47,62`). The one assembly-keyed check,
  `GenerateIndexGenerator.cs:512-526`, derives the assembly from the resolved symbol.
- **R4. Persistence: stored `SparkUser`/`SparkRole` documents need a migration** (owner decision
  2026-10-04: "existing SparkUser documents will need to be migrated with a new RavenMigration").
  - Raven stores `@metadata.Raven-Clr-Type` as `Namespace.Type, Assembly`. The model types are
    values and DTOs, not document roots, so nothing changes for them.
  - `SparkUser` and `SparkRole` **are** document roots. Their stored metadata names the
    `MintPlayer.Spark.Authorization` assembly, which after the move no longer holds them.
  - Typed loads (`LoadAsync<SparkUser>`) ignore the stored type, but Spark's untyped loads do not:
    `RowSecurity.cs:64,600` and `BreadcrumbResolver.cs:202,636` fall back to a `JObject` when
    `@Raven-Clr-Type` does not resolve.
  - Affected apps store plain `SparkUser` (`AddAuthentication<SparkUser>`): CodeCoverage (production,
    `Program.cs:118`), HR (`:34`) and QnA (`:38`). An app's own subclass (`AppUser : SparkUser`)
    records the app's assembly and is unaffected.
  - **Fix:** a `RavenMigration` (`ISparkMigration`) that rewrites `Raven-Clr-Type` on `SparkUsers`
    and `SparkRoles` documents whose value names the old assembly.
    - Shipped by the framework (Authorization), not by each app. See §3.6 for how a package's
      migrations get discovered.
    - Tests: red/green on a document whose metadata names the old assembly. After the migration it
      loads untyped as `SparkUser`, not `JObject`. Run it twice to prove it is idempotent.
- **R5. `SparkUser`'s `cref`s.** Moved doc comments referencing web types (`SparkUser.cs:36` mentions
  `UserManager` in a `<c>`) must not become `<see cref>` to types the new package cannot see. The same
  applies to `TranslatedString.cs:14`, whose `<see cref="SparkText"/>` becomes `<c>`.
- **R6. AllFeatures reads "`SparkUser` resolves" as "Authorization is referenced". Found 2026-10-04.**
  - `SparkFullGenerator.cs:130` sets `HasSparkUser = GetTypeByMetadataName("…Identity.SparkUser") != null`.
  - When that flag is set, `Producer.cs:36-37,108` emits
    `SparkBuilderAuthorizationExtensions.AddAuthentication<SparkUser>(…)`.
  - After the move, `SparkUser` also resolves through Authorization.Abstractions, for example in an
    app that references `CodeCoverage.Library` but not Authorization. The generated call then targets
    a package the app does not reference, which is a compile error.
  - The same happens to an app that subclasses `SparkUser` (`Kind == "User"`, `:45`) without
    referencing Authorization.
  - **Fix:** rename the flag to `HasAuthorization`, key it on
    `MintPlayer.Spark.Authorization.Extensions.SparkBuilderAuthorizationExtensions` (which stays in
    Authorization, like the other flags at `:131-132`), and emit `AddAuthentication` only when it is set.
  - Generator tests: no Authorization reference means no call, even with `SparkUser` visible; with
    Authorization referenced, the call is emitted as today.

### 4.1 Pre-implementation audit (2026-10-04)

**Code that checks an assembly by name.** Every `GetReferencedAssemblies`, `GetName().Name`,
`ContainingAssembly`, and `AppDomain…GetAssemblies` site in `libs/` was checked:

| Site | Keys on | Verdict |
|---|---|---|
| `SparkAssemblies.cs:19,36-37` | the `MintPlayer.Spark.Abstractions` name | **Breaks** → R1 |
| `ContributionCatalog.cs:32,120` | the `MintPlayer.Spark.Contributions.Abstractions` name | Safe: that package is not split, and QnA.Library keeps referencing it |
| `LookupReferenceDiscoveryService.cs:34`, `CustomActionResolver.cs:75`, `ActionsResolver.cs:131`, `SparkTypeResolver.cs:50`, `SparkHistory.cs:234`, `ModerationTargets.cs:60`, `SparkSoftDelete.cs:79`, `ContributionShapeCheck.cs:90`, `SparkDevelopmentExtensions.cs:852,1305` | every loaded assembly, matched by type or name, with no assembly filter | Safe: a moved type is still one type |
| `SparkActionLayers.cs:82`, `SparkReservedActionRegistry.cs:73`, `SparkMiddleware.cs:597,849-867`, `AttributeDescriptionCatalog.cs:59`, `SparkMailTemplateResolver.cs:76` | the name in a log or error message | Safe |
| Generators | `GenerateIndexGenerator.cs:512-526` derives the assembly from the symbol; `ReservedActionsReader.cs:40` walks every reference; `SparkFullGenerator.cs:130` | Safe, except **R6** |

**Other checks:**
- **`internal` members:** none on any moved type (all 15 files grepped), so no `InternalsVisibleTo` is needed.
- **QnA's `spark-contributions.targets`:** it adds only the Contributions generator as an analyzer
  (`:29-30`), with no Abstractions dependency.
- **NuGet ids:** `MintPlayer.Spark.Model`, `MintPlayer.Spark.Authorization.Abstractions` and
  `MintPlayer.Spark.History.Abstractions` are unclaimed on nuget.org (flat-container 404);
  `MintPlayer.Spark.Attributes` is ours (200).

## 5. Out of scope (genuinely not being done)

- Moving `SparkAuthorizeAttribute`. It derives from ASP.NET Core's `AuthorizeAttribute`, and getting
  its derivation wrong fails open (#388).
- Splitting the six web-using Abstractions files into their own package (#388).
- `[TypeForwardedTo]` shims (#388 S6). There is no backward-compatibility requirement (owner
  preference), and #382 shipped the attribute split without them.

## 6. Verification

- **Build guard (§3.5):** green on all five libraries, red when an Abstractions reference is re-added.
- **`ReferencedAssemblyEntityTests`** (`tests/MintPlayer.Spark.SourceGenerators.Tests/Generators/ReferencedAssemblyEntityTests.cs:18-37`):
  extended with a library that uses `TransientLookupReference` and `TranslatedString`, compiled against
  Attributes and Model only. This exercises exactly the shape that broke at #382.
- **R1 runtime test:** the registry discovers Moderation, Contributions, History, SoftDelete and the core verbs.
- **R4 migration test:** a `SparkUser`/`SparkRole` document written with the old assembly name loads
  untyped as its CLR type after the migration (and as a `JObject` before it), and a second run is a no-op.
- **`dotnet pack` of the solution:** `MintPlayer.Spark.Abstractions.nupkg` does not contain
  `TranslatedString`, and `Model.nupkg` does.
- **Local sweep:** `npm run test:affected`.

## 7. Docs

- `docs/guide-translated-strings.md:17,390`: the class now lives in the `MintPlayer.Spark.Model` package.
- `docs/guide-asdetail-attributes.md:215` and `docs/guide-attribute-descriptions.md:144`: a library
  references `Attributes` + `Model`, not Abstractions.
- `using` lines in `guide-reference-attributes.md` and `guide-asdetail-attributes.md` stay valid,
  because the namespace is kept.
- New `README.md` + `AGENTS.md` for Attributes, Model and Authorization.Abstractions, each stating
  **"the namespace is deliberately not the package name"**, and why: about 40 metadata literals in the
  generators, and `global::`/`using` emissions into generated code.

## 8. Stale statements in the superseded docs

| Old claim | Now |
|---|---|
| 5 web-using Abstractions files | 6 (`SparkBuilderInterceptorExtensions.cs`) |
| 4 entity libraries | 5 (`QnA.Library`) |
| 4 types move | 5 types plus a converter: `EmptyValue`, `LookupReferenceValue<T>`, `TranslatedStringJsonConverter`; also `ValidationError` and the action constants |
| `GenerateIndexGenerator.cs:459/:497` resolver | `:512-526` |
| `HostTranslationsAggregatorGenerator.cs:30`, `ProjectionPropertyAnalyzer.cs:30,54`, `AttributeDescriptionsGenerator.cs:82` | `:29`, `:31,55`, `:79` |
| `dotnet-build-master.yml:82-83`, `:148-149` | `:88-89`, `:154-158` |
| `ModelSynchronizer.cs:684-686` lookup reflection | around `:754` |
| "88 source files" | 102 |
| (not mentioned) | CodeCoverage needs Authorization; QnA needs History; `SparkAware` R1; the PR version-bump gate |

## 9. Verification results (2026-10-04)

`RAVENDB_LICENSE=<Developer> npm run test:affected`, everything affected:

1. **Run 1:** `MintPlayer.Spark.Tests` failed 5 of 3,617; Nx then bailed. Two real defects from the
   move, both invisible to the build:
   - **Auth mail templates.** `SparkAuthenticationExtensions.cs` registered Authorization's embedded
     `SparkAuth/*.mjml` via `typeof(SparkUser).Assembly`, which is now Authorization.Abstractions.
     Every account mail (confirm email, password reset, link confirmation) would have thrown
     "No mail template 'SparkAuth/ConfirmEmail'". Anchored on `SparkAuthMailTemplates`.
     (`MailManagerTests` ×2, `MailSpikeM2Tests`.)
   - **`HistoryRights`** shared `IAuditable.cs` and moved with it, away from History's
     `[assembly: SparkReservedActions(typeof(HistoryRights))]`, so `History`/`Revert` stopped being
     read from that assembly. Moved back, with `IHistoryUserNameResolver`.
     (`SparkReservedActionRegistryTests` ×2.)
   - Fixed in `a091e7a6`; those four classes plus the new migration tests re-run green (46 tests).
2. **Run 2:** every test project green (`Spark.Tests`, `SourceGenerators.Tests`, `CodeCoverage.Tests`,
   `Client.Tests`, the Angular suites). `MintPlayer.Spark.E2E.Tests:build` failed only on
   `Misconfigured remote cache endpoint: Unexpected response status: 499`: the remote Nx cache
   rejected the upload. That is infrastructure, not code.
3. **Run 3** (`-- --skip-remote-cache`): E2E built from the local cache and its tests passed (2m 3s).
   **All 13 test projects green.**

Afterwards, every top-level type in every moved file was listed and every `typeof(<moved type>).Assembly`
was grepped, including fully qualified forms. No other anchor exists.

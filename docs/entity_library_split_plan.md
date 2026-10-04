# Plan: an entity library should not depend on ASP.NET Core (#388)

PRD: [entity_library_split_PRD.md](entity_library_split_PRD.md) · PR: [#484](https://github.com/MintPlayer/MintPlayer.Spark/pull/484)
(branch `fix/bs-select-full-width`).
Status 2026-10-04: **M0–M8 implemented** on `fix/bs-select-full-width` (M6 dropped); the local sweep is the last step. Nothing pushed.

Rules for this work:
- **Commit per milestone; run tests only at the end (M8).** Intermediate milestones are checked by
  building and reading the code.
- **Every type moves by assembly only. The namespace never changes.**
- **One PR.** Every package and app change below lands in #484.

## Milestones

### M0: ng-bootstrap 22.21.1 ✅
`<bs-select class="w-100">` renders full width (MintPlayer/mintplayer-ng-bootstrap#424). Root
`package.json` + lockfile only.

### Spikes (before M1; throwaway code, findings go into the PRD)
- **S1. A migration shipped by a framework package. ✅ Resolved 2026-10-04 → PRD §3.6.**
  Packages cannot ship migrations today: both generators scan only the app's syntax. Fix:
  scan referenced assemblies (milestone M2a).
- **S2. netstandard2.0. ✅ Resolved 2026-10-04 → PRD §3.4: dropped** (owner: ".NET Core purely").
  The spike found that `IMessageBus` default interface members (CS8701) and `DateOnly` in entities
  block it. M6 is removed.
- **S3. Build-guard mechanism. ✅ Resolved 2026-10-04 → PRD §3.5.** Hook `AfterTargets="ResolvePackageAssets"`
  and check `@(TransitiveFrameworkReference)`. It is red on today's HR.Library (exit code 1) and green
  on Attributes.

### M1: `MintPlayer.Spark.Model` ✅ `16de378b`
1. Create `libs/model/MintPlayer.Spark.Model/MintPlayer.Spark.Model.csproj`: `Microsoft.NET.Sdk`,
   `net11.0`, `Version 11.0.0-preview.95`, package metadata copied from Attributes.
2. `git mv` the following, keeping their namespaces:
   - `TranslatedString.cs` (with its converter)
   - `TransientLookupReference.cs`
   - `DynamicLookupReference.cs` (with `EmptyValue` and `LookupReferenceValue<T>`)
   - `ELookupDisplayType.cs`
   - `ValidationError.cs`
   - `Authorization/SparkCoreActions.cs` (with its two `[assembly:]` lines)
   - `Authorization/SparkCombinedActions.cs`
   - `Authorization/SparkReservedActionsAttribute.cs` (both attributes)
3. Change `TranslatedString.cs:14` `<see cref="SparkText"/>` → `<c>SparkText</c>`. Check every moved
   file for crefs or usings into types that stayed behind.
4. Abstractions gains a `ProjectReference` to Model. Add Model to `MintPlayer.Spark.slnx` next to Attributes.
5. Check: `dotnet build MintPlayer.Spark.slnx`. Grep that no moved file is still in Abstractions.

### M2: `MintPlayer.Spark.Authorization.Abstractions` ✅ `7388b20d`
1. Create `libs/authorization/MintPlayer.Spark.Authorization.Abstractions`: `Microsoft.NET.Sdk`,
   `net11.0`, `.95`, no references.
2. `git mv` `Identity/SparkUser.cs`, `SparkUserClaim.cs`, `SparkUserLogin.cs`, `SparkUserPasskey.cs`,
   `SparkUserToken.cs`, `SparkRole.cs` and `SparkRoleClaim.cs`. The namespace stays
   `MintPlayer.Spark.Authorization.Identity`. Doc-comment mentions of `UserManager`/`UserPasskeyInfo`
   stay as `<c>`, never `<see cref>`.
3. Authorization references it. Add it to the slnx.
4. Check `InternalsVisibleTo`: if Authorization or the tests use `internal` members of the moved
   types, the new assembly needs the same `InternalsVisibleTo` entries as `Authorization.csproj`.
5. Check: solution build.

### M2a: packages can ship migrations (PRD §3.6) ✅ `948d4aaa`
1. `MigrationRegistrationGenerator`: add a `CompilationProvider` branch that walks
   `SourceModule.ReferencedAssemblySymbols`. It only enters assemblies that reference
   `MintPlayer.Spark.Migrations`, collects public non-abstract `ISparkMigration` implementations into
   an equatable list of fully qualified names, and merges them with the syntax results.
2. The producer emits `AddMigrations()` when the app or a referenced package has migrations
   (`Producer.cs:27`).
3. Apply the same change to `SparkFullGenerator` (`:98`), sharing the reader rather than duplicating it.
4. New analyzer: a non-public `ISparkMigration` in a library project gives a warning. Add a new SPARK
   diagnostic id and document it in `docs/diagnostics.md`.
5. Add `spark.AddMigrations()` to QnA's `Program.cs`.
6. Write the generator tests and the runtime marker test (run in M8).

### M2b: migrate stored `SparkUser`/`SparkRole` documents (PRD R4, owner decision) ✅ `948d4aaa`
1. In Authorization: an `ISparkMigration` that patches `@metadata.Raven-Clr-Type` on `SparkUsers` and
   `SparkRoles`. It only touches values that name the old `MintPlayer.Spark.Authorization` assembly
   and the type `MintPlayer.Spark.Authorization.Identity.SparkUser`/`SparkRole`, and leaves app
   subclasses alone. Use a set-based patch by query (`from SparkUsers where …`) rather than loading
   every user; wait for the operation to finish inside `UpAsync`.
2. Make it `public`, with a `yyyyMMddHHmm` version. Authorization references `MintPlayer.Spark.Migrations`;
   M2a's generator then registers the migration in every app that calls `AddMigrations()`.
3. Write the red/green test (run in M8): before the migration the document loads untyped as a
   `JObject`; afterwards it loads as `SparkUser`. A second run changes nothing.

### M3: the `SparkAware` trap (PRD R1) and an audit of assembly-name scans ✅ `aa0f777e` (R6 in `948d4aaa`)
1. `SparkAssemblies.SparkAware()` (`SparkAssemblies.cs:17-37`) treats a reference to any of
   `MintPlayer.Spark.Abstractions`, `.Model` or `.Attributes` as "Spark-aware". Name the three
   assemblies from types (`typeof(TranslatedString).Assembly`, …), never as string literals.
2. ✅ Audit done before implementation (PRD §4.1): `SparkAssemblies` is the only runtime site that
   breaks. Generators: fix R6 here. `SparkFullGenerator.cs:130` `HasSparkUser` becomes
   `HasAuthorization`, keyed on `SparkBuilderAuthorizationExtensions`, and `AddAuthentication` is only
   emitted when it is set.
3. Write the red/green tests now and run them in M8:
   - `SparkReservedActionRegistry.All` contains `ModerationRights`, `ContributionRights`,
     `HistoryRights`, `SoftDeleteRights` and the `SparkCoreActions` verbs.
   - The R6 generator test: `SparkUser` visible without Authorization gives no `AddAuthentication`
     call; with Authorization it does.

### M4: feature packages ✅ `aa0f777e` (five more types moved, PRD §3.1)
1. `Replication.Abstractions`: drop the Abstractions reference (csproj :22). In `SyncAction.cs:99,103`,
   replace `GetCachedProperties()`/`AccessorCache.GetGetter` with a private static
   `ConcurrentDictionary<Type, PropertyInfo[]>` plus `PropertyInfo.GetValue`. This is a sync path,
   not a hot loop; check how often it is called before settling.
2. `Contributions.Abstractions` (:29) and `Moderation.Abstractions` (:30) reference Model instead of
   Abstractions. Rebuild to confirm they used nothing else.
3. New `libs/history/MintPlayer.Spark.History.Abstractions`: `git mv IAuditable.cs`, keeping namespace
   `MintPlayer.Spark.History`. History references it. Add it to the slnx.
4. Check: solution build.

### M5: the five entity libraries + the build guard ✅ `aa0f777e` (guard proven red/green)
1. Each `*.Library.csproj`: remove the Abstractions reference, add explicit `Attributes` (+ `Model`
   where used), and set `<SparkEntityLibrary>true</SparkEntityLibrary>`.
   - **CodeCoverage:** Authorization → Authorization.Abstractions (remove the #388 comment at :21-28);
     add a `Microsoft.Extensions.Caching.Memory` PackageReference at the version the solution already
     resolves; drop `Newtonsoft.Json` (:13) only if a build without it is green.
   - **QnA:** History → History.Abstractions; add an explicit `Newtonsoft.Json` PackageReference.
     Check the contributions `.targets` import (csproj :25) for anything Abstractions-dependent.
2. Add the build guard to the root `Directory.Build.targets`, conditioned on `SparkEntityLibrary`.
   Use the S3 target from PRD §3.5 (`AfterTargets="ResolvePackageAssets"`, checking
   `@(FrameworkReference);@(TransitiveFrameworkReference)`, error `SPARKLIB001`). Document it in
   `docs/diagnostics.md`.
3. **Prove the guard is falsifiable:** temporarily re-add Abstractions to HR.Library and expect the
   build to fail; revert. Record the error text in the PRD.
4. Check: solution build. All five host apps still build; each references `MintPlayer.Spark`, which
   brings Abstractions in.

### ~~M6: netstandard2.0~~ (dropped, PRD §3.4)

### M7: versions, CI, docs ✅ `15d13bb1` (PR pack step: open owner decision, PRD R2)
1. Bump **every** `MintPlayer.Spark*` package from `11.0.0-preview.94` to `.95` in lockstep (33 csproj).
   `MintPlayer.Dotnet.SocketExtensions` stays unchanged, as in preview.94. The new packages start at `.95`.
2. Add a `dotnet pack --no-build -c Release -o nupkgs` step to `pull-request.yml` after its release
   build, so package shape is exercised on every PR. Keep the cache-replay ordering note from
   `dotnet-build-master.yml:75-87` in mind.
3. Docs: update `guide-translated-strings.md:17,390`, `guide-asdetail-attributes.md:215` and
   `guide-attribute-descriptions.md:144`.
4. Write `README.md` + `AGENTS.md` for Attributes, Model, Authorization.Abstractions and
   History.Abstractions, each stating that the namespace is deliberately not the package name, and why.
5. ✅ The old `docs/prd/` PRD and plan carry a superseded banner (done with the PRD).

### M8: verification (the only test run) — tests `2b262aac`; sweep running
1. Extend `ReferencedAssemblyEntityTests` with a library using `TransientLookupReference` and
   `TranslatedString`, compiled against Attributes and Model only (no Abstractions in its references).
2. Run the M2b migration test (written in M2b).
3. Run `dotnet pack` locally and inspect the nupkgs: Abstractions no longer contains the moved types;
   Model, Authorization.Abstractions and History.Abstractions do.
4. Run `RAVENDB_LICENSE='C:\Repos\MintPlayer.Spark\.secrets\raven-license.log' npm run test:affected > <scratchpad>/sweep.log 2>&1`.
   Fix failures by re-running only the failing class.
5. Update the PRD status and PR #484's description (#388 is the main issue: "Closes #388").

## Commit and PR

- One commit per milestone on `fix/bs-select-full-width`.
- Retitle PR #484 for #388 once M8 is green; keep the ng-bootstrap bump as a section of the description.
- Push only when everything is done and green locally: CI runs cost money.

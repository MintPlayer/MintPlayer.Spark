# PRD — One composition system: library defaults, application overrides

Status: **draft, grill pending.** Evidence pinned to `master` @ `680794fd`, gathered read-only by
three investigations (inventory, MSBuild mechanics, merge semantics). Nothing is built yet; §6
lists the spikes. **No backward compatibility** — the libraries are in preview (owner,
2026-10-06); file formats, names, ids and existing mechanisms may be replaced outright.

Triggered by [generic_passkeys_page_PRD.md](generic_passkeys_page_PRD.md): to use the generic
Spark UI, a library must ship PersistentObjects (and, ideally, their default rights), and today a
library cannot.

## 1. What the investigation found

### 1.1 Five composition models for one kind of problem

| File | How library defaults travel | Where the app file is read | Composition | Reload | Gate |
|---|---|---|---|---|---|
| `actions.json` | `AdditionalFiles SparkActionsLayer="library"` → `LibraryActionsGenerator` → `[assembly: SparkActions(json)]`, discovered at runtime via `SparkAssemblies.SparkAware()` (`SparkActionLayers.cs:67-90`) | `ContentRoot/App_Data` (`ActionsCatalogueLoader.cs:82`) | per property at **runtime**; `null` removes; app wins; lib-vs-lib = SPARK036 (`SparkActionLayers.cs:94-146`) | watcher, all events (`:229-239`) | composed hash (`ConfigFileShape.cs:40-75`) |
| `translations.json` | `[assembly: SparkTranslations]` (`LibraryTranslationsGenerator.cs`) | **never from disk** — compiled by `HostTranslationsAggregatorGenerator` into a registry | per key+language at **compile time**; `""` = untranslated; libs alphabetical | none (rebuild) | none |
| `Model/*.json` | **no library layer**; the synchronizer writes library entities into the app's own files (`ModelSynchronizer.cs:46-60`) | `ContentRoot/App_Data/Model/*.json` (`ModelLoader.cs:49`) | none (one file per type; #253 never deletes) | none (`Lazy`) | full verify + hash (`SparkDevelopmentExtensions.cs:239-311`) |
| `security.json` | **none, by design** ("a second place to put the file is a second place to fail to find it", `SecurityConfigurationLoader.cs:16-18`); Moderation prints rights to paste (`ModerationInitCommand.cs:2-4, :87`) | `ContentRoot` const path (`:30, :75`) | app only | watcher, Changed only (`:131-137`) | anonymous-surface baseline only (`securityPosture.txt`) |
| `programUnits.json`, `culture.json` | none | `ContentRoot` | app only | none | structural hash / none |
| `moderation.json` | Moderation package | `IConfiguration` source, lowest precedence | `IConfiguration` | none | none |

Plus side channels: reserved verbs via `[assembly: SparkReservedActions]`, renderer seeds via
`SparkModelSatellites.SeedRenderer`, and build-time copies (`CopySparkAgentsGuide`,
`SparkAuthGenerateSetupFile`) that do not compose.

### 1.2 Facts that constrain any design

- **Every runtime reader uses `ContentRootPath`**: ModelLoader, SecurityConfigurationLoader,
  ActionsCatalogueLoader, CultureLoader, ProgramUnitsLoader and others. None uses
  `AppContext.BaseDirectory`. Under `dotnet run` and in the E2E host
  (`SparkAppTestHost.cs:770-779`, `dotnet run --no-build`, WorkingDirectory=appDir) that is the
  **project folder**, not `bin`. `ScratchContentRoot` (Spark.Tests) builds a temporary content
  root holding `App_Data`.
- **Nx restores only `bin`/`obj` on a cache hit** (`nx.json:35-53`). Anything a build writes into
  `App_Data` is missing after a cache-hit build.
- **Assembly attributes already travel identically over PackageReference and ProjectReference.**
  At runtime they are read through `SparkAware`. At compile time they are read through
  `compilation.SourceModule.ReferencedAssemblySymbols` (`LibraryActionsReader.cs:30`). Generators
  can read a referenced assembly's attributes, but **not** its embedded resources.
- **A ProjectReference does not import buildTransitive**, so every app has hand-written
  `<Import>`s for `spark.props`/`.targets` and `spark-authorization.props`/`.targets`
  (CodeCoverage.csproj:2,4,56,63; Fleet, HR, QnA, DemoApp the same; tests import
  `spark-testing.targets`). NuGet auto-imports only **one** `buildTransitive/$(PackageId).targets`
  per package.
- **The ASP.NET Static Web Assets SDK is the MSBuild precedent for files shipped by libraries.**
  - ProjectReference side: a getter target is invoked across `@(ProjectReference)`.
  - Package side: a generated buildTransitive manifest.
  - Both produce one item type, so the consumer cannot tell them apart.
  - At dev time a runtime manifest maps paths back to their source files instead of copying them.

  The catch: the getter target must exist in every library project.
- **`*.json` globs are everywhere.** There are about 15 runtime sites (`ModelLoader.cs:49`,
  `ModelSynchronizer.cs:373,455,620,1275`, `SparkDevelopmentExtensions.cs` ×10,
  `ModelFileShape.cs:60`, `ModerationInitCommand.cs:105`) and the AdditionalFiles glob
  (`spark.targets:159`). A file such as `Model/X.authorization.json` would be read as a second
  entity, or throw on a duplicate alias (`ModelLoader.cs:68-74`).
- **`App_Data` is hard-coded about 24 times** at runtime and in generators. `SparkAppDataDir` is
  honoured only by MSBuild.
- **Ids drift on hand copies.** The library type `SparkUser` has id `4e13c0de-…0001` in QnA and
  `3f7c1d92-…` in CodeCoverage, and an E2E test pins QnA's id. Attribute `group`/`tab` references
  are Guids (`EntityTypeDefinition.cs:338,386`).
- **Rights are grant facts, not overridable fields.**
  - A deny is absolute across the caller's groups, and `isImportant` beats denials
    (`Right.cs:38-70`).
  - Denying a library grant on `authenticated` would also lock out administrators.
  - Group ids are app-chosen GUIDs, and `wellKnown` is optional, so **a library cannot name any
    group**.
- **`IManager.GetPersistentObject` applies no `security.json` rights** (`Manager.cs:20-27` →
  `EntityMapper.cs:154-169`, which only scaffolds). Attribute rights are applied only when
  endpoints serialize (`IAttributeRightsEnforcement.PresentAsync`; `New.cs:97-104` applies them
  *after* the hook). A PO built in code carries every attribute until an endpoint presents it.
  This conflicts with the owner's requirement (G6).

## 2. Goals (owner requirements, 2026-10-06)

- **G1** Any plugin library can ship sensible default files: actions, PersistentObjects (model),
  default rights, translations, program units, and later other file kinds.
- **G2** The application ships its own files, whose fields **override** the defaults
  (composition). It does not copy them. Example: overriding only the icon of a library-shipped
  action. (Correction from S2: `apps/QnA/QnA/App_Data/actions.json:4` is the app's own action, not
  an override.)
- **G3** Library updates **reach** apps. A one-time copy into the app is not allowed.
- **G4** The behaviour is identical for **PackageReference and ProjectReference**, with as little
  duplication as possible (DRY).
- **G5** It works for JSON now and is extensible to other file kinds and features.
- **G6** `IManager` applies the composed `security.json` rights, including when constructing
  **new** PersistentObject instances.
- **G7** No backward compatibility.

## 3. Non-goals

- Changing the *semantics* of an individual feature, such as what an action or a right means.
  Only how its definition is assembled changes.
- Runtime plugin loading (assemblies not referenced at build time).

## 4. Design

### D1 — Transport: how library layers reach the app (⚠️ grill Q1)

| | **A. Owner's proposal:** library files copied as `App_Data/**/<name>.<alias>.json` (gitignored), composed at runtime | **B. Owner's alternative:** a Spark target collects every library's files (Static-Web-Assets style) and composes them into the **output** folder | **C. Embedded layers:** each library's `App_Data/**` is compiled into assembly attributes, composed at runtime (today's actions/translations transport, generalised) |
|---|---|---|---|
| Same for package and project references (G4) | Only with a per-app import, or with B's getter | Yes, via the getter target plus a manifest | **Yes, already proven** |
| DRY | N libraries × N apps unless centralised | One target | One generator, no MSBuild collection |
| `dotnet run` / E2E / `ScratchContentRoot` (reads ContentRoot) | Works | **Breaks** unless every reader moves to `bin` | Works |
| Nx cache hit | **Files not restored** | Restored (bin) | Restored (inside the dll) |
| App generators see library data at compile time | Only as AdditionalFiles after the copy (one build late) | If composed into obj before CoreCompile | **Yes** (referenced-assembly attributes) |
| `dotnet watch` / churn in the source tree | Writes into the source tree | Clean | Clean |
| Developer can *see* the library defaults | **Yes, as files** | In bin/obj | Through a CLI (D9) |

**✅ Decided: C** (owner, 2026-10-06, after a walkthrough of today's actions and translations
transport). The rest of this section is kept for the record.

**Amended by S1:** runtime discovery does not rely on a `SparkAware` walk alone. The compiler
drops a referenced assembly whose types the app never uses, so a library that ships only layers
was visible at compile time but **missing at runtime**. The app-side generator records the layered
assemblies (`[assembly: SparkLayerAssemblies("…")]`), and the runtime `Assembly.Load`s exactly
those, so both views agree. Layer paths are embedded relative, with `/`. Web SDK libraries keep
`Content Remove` on their layer files (NETSDK1152).

**As built in M3 (2026-10-06).**
- **Contract** (`MintPlayer.Spark.Attributes`, namespace `MintPlayer.Spark.Abstractions`):
  - `[assembly: SparkLayer(alias, kind, path, json)]`, one per file; the text unparsed and unchunked
    (S1: attribute blobs have no practical ceiling, so the translations' 60 KB chunks are gone).
  - `[assembly: SparkLayerDependencies("A", …)]` beside them: the layered assemblies the library was
    compiled against, transitively. **Deviation:** the order is not read from
    `GetReferencedAssemblies` (Q3 said so) because the compiler drops a reference whose types a
    library never uses, the same trimming S1 found for applications; a library may override another's
    layer without touching its code. Recorded, both views read the same list.
  - `[assembly: SparkLayerAssemblies("A", …)]` in the application, in layer order.
  - `SparkActions` and `SparkTranslations` are removed, with `LibraryActionsGenerator` and
    `LibraryTranslationsGenerator`.
- **The path decides the kind** (`SparkLayerKinds.FromPath`, shared source): `actions.json`,
  `translations.json`, `Model/*.json`, `security.json`, `programUnits.json`, `moderation.json`.
  One metadata item, `SparkLayerPath`, so build and generator cannot disagree on a kind.
- **Items.** The generator package's `build/MintPlayer.Spark.SourceGenerators.targets` adds them,
  with the `Content Remove`, for a class library that is not a test project
  (`<SparkLibraryLayers>false</SparkLibraryLayers>` opts out); `Directory.Build.targets` imports it
  in this repository. `build/`, not `buildTransitive/`: the package is referenced with
  `PrivateAssets="all"`, so it reaches exactly the library. The libraries' hand-written items are gone.
- **Generators.** `LibraryLayersGenerator` embeds, only in a class library (an executable embeds
  nothing, S3), and still checks every `translations.json` (SPARK_TRANS_001–004).
  `ApplicationLayersGenerator` writes `SparkLayerAssemblies` in an executable. Readers:
  `LibraryLayersReader` at compile time (the actions analyzer, the security analyzer, the host
  translations aggregator, which keeps its registry until M5 and now flattens the raw layers itself),
  `SparkLayerCatalog` at run time (`Libraries`, `Of(kind)`, `Discover`; `SparkActionLayers` reads it).
- **Run time.** `SparkLayerCatalog` loads the entry assembly's recorded names (`Assembly.Load`, a
  name that does not load fails) and falls back to `SparkAssemblies.SparkAware()` when the entry
  assembly recorded nothing (a `WebApplicationFactory` host, whose entry assembly is the test
  runner). Cached; two libraries with one alias throw at first use, which is startup.
- **Package vs project** (acceptance item 3) is `npm run test:layer-transport`
  (`tools/layer-transport-check.mjs`): S1's `run.sh` against the real libraries, packed at a
  throw-away version to a local feed with `--artifacts-path`, so neither `bin`/`obj` nor the Nx cache
  is touched. A script and not an xunit test, because packing the closure and restoring from
  nuget.org takes minutes and the local sweep must not wait for it; M11 runs it.

**Recommendation was: C**, with D9 for visibility. If physical files are a hard requirement, B
(Static Web Assets pattern, composed into `obj`/`bin`) rather than A, and every reader moves to
the output folder.

### D2 — One layering engine
Generalise `SparkActionLayers.Compose` into `SparkLayers.Compose(layers, KindSpec)`. It works on
raw JSON trees and records which layer each leaf came from.
- **Layer order:** dependency order (Q3). `MintPlayer.Spark` core comes first, then each library
  above the libraries it references, alphabetical between unrelated ones, then the app.
- **Objects** merge per property.
- **Arrays of identified elements** merge per element key. **Arrays of primitives** are replaced
  whole.
- **`null`** resets a property to the layer below. A keyed-array **element** is removed with
  `{"<key>": "...", "$remove": true}`, because a bare `null` carries no key (S2). A property that is
  reset and then set again by a later layer is **appended**. Today's engine keeps the old position,
  which only `Sources` and the printed actions show; the hash sorts (S2).
- **Atomic paths:** `KindSpec.IsAtomic(path)` marks values that are replaced whole rather than
  merged. For example, an action's object-valued properties at depth 2 (S2).
- **Annotations** (`$schema`, `_`-prefixed keys) are ignored.
- **Conflicts:**
  - Library against library: a diagnostic at compile time (the generator) and at startup.
  - The app always wins, silently.
  - **As built in M3:** a library overriding a library it depends on is silent too (Q3). The engine
    still lists every library-vs-library conflict; the actions analyzer, `SparkActionLayers.Compose`
    and the translations aggregator drop those whose winner recorded the loser in
    `SparkLayerDependencies`.
- **Per-kind flags** are explicit rather than silently unified:
  - case sensitivity of keys
  - "`""` means untranslated" (translations)
  - whether `null` removal is allowed

### D3 — Per-kind rules (`KindSpec`)

| Kind | Element key | Notes |
|---|---|---|
| actions | action name (case-insensitive) | as today |
| translations | key (ordinal), then language | each layer is **flattened first**, so `"a.b"` and `{"a":{"b"}}` meet; `"ns": null` removes a whole prefix; a new language is appended (`GetValue` falls back to the first); `""` from the app ignored; moves from compile time to the same runtime engine (D10) |
| model | `persistentObject.name` | `attributes[]`, `tabs[]`, `groups[]`, `queries[]` by name (the file's root `queries`; the sub-query list `persistentObject.queries` is replaced whole, amended in M4, see D6). **`id` can never be overridden.** An app file for a library type is a delta |
| programUnits | unit id | |
| culture | (app only; libraries may not ship it) | |
| moderation | privilege name; reputation keys | `group` is a token or slot bound in `security.json`. The composed result is exposed as an `IConfiguration` source below appsettings and environment variables (Q6) |
| security | see D4 | |

### D4 — Library default rights (⚠️ grill Q2: reverses a deliberate design position)
- **Rights are a keyed set.** ~~`rights` becomes an object keyed by a stable key~~ (amended in M6:
  it stays an array whose elements carry a `key`, keyed by the engine like every other keyed array):
  `"<alias>:<name>"` for library rights, the id for the app's own. The app removes a library grant
  with ~~`"<key>": null`~~ `{"key": "<alias>:<name>", "$remove": true}`, the engine's removal for a
  keyed element (D2). It never edits a library right; it removes it and adds its own.
- **Guard rails:**
  - **Library layers may only grant.** No `isDenied` and no `isImportant`.
  - **They may grant only on resources the library owns:** its own types, its reserved verbs and
    its pseudo-types. A grant on an app type is a startup error.
  - **Groups are referenced by token.** `@anonymous` and `@authenticated` resolve through the
    app's `wellKnown`. Library slots, such as `moderation:moderators`, are bound by the app in
    `"bindings": { "moderation:moderators": "<groupId>" }`. An unresolved or unbound token is a
    **startup error**, never a silent no-op.
  - ~~The app opts in per library.~~ **No opt-in** (grill Q2 = B, 2026-10-06). A library's rights
    are active as soon as it is referenced.
  - **Per-library opt-out** (owner, 2026-10-06). `security.json` `"libraries": { "<alias>": false }`
    switches off every right that library ships. Its rights still appear in the posture table,
    marked inert, so the opt-out is visible too. To drop a single grant, ~~`"<key>": null`~~
    `{"key": "<key>", "$remove": true}` still works.
  - **The gate is the review point.** `securityPosture.txt` becomes a readable, committed **text
    table of every effective right**, with the layer each right came from. It is not a bare hash.
    `--spark-verify-security` fails on drift, so adding a library, or a library update that changes
    rights, appears as a diff in that file in the same PR.
  - Grants to `@anonymous` get their own section in the posture file, and their own CI warning,
    so they cannot hide in a long diff.
- **As built in M6 (2026-10-06).**
  - **Shape.** `rights` is an array; every element needs a string `key` (strict JSON, the engine refuses
    one without, and a key stated twice in one layer). The app's own keys are its old ids (`"id"` renamed
    to `"key"` in all five apps and every fixture); any text without `:` is allowed. A library writes its
    keys bare (`"passkeys-read"`) and the composer namespaces them (`authorization:passkeys-read`).
    `groupId` holds a group id, `@anonymous`, `@authenticated` or a slot `alias:slot` (both lower-kebab);
    a slot bound to several groups composes into one right per group. New root members (app only):
    `"bindings": { "<alias>:<slot>": ["<group id or name>", …] }` (names match ignoring case, D24) and
    `"libraries": { "<alias>": false }`. A library layer may state only `rights` and `reservedTargets`.
  - **Code.** `SparkSecurityLayers` (shared source, `libs/spark/Shared/Layering/`, kind
    `SparkKinds.Security`, keys ignoring case as the loader always read them) composes, checks the guard
    rails and resolves tokens; `SparkSecurityFiles.Compose` (Abstractions) turns it into the
    `SecurityConfiguration` the evaluator already reads (`Right.Key`, `Right.Layer` = alias or null,
    `Right.Group` = the token as written, `InertRights`, `Bindings`, `Libraries`). `Right.Id` is gone; the
    validator's duplicate-id rule became the engine's duplicate-key refusal. `SecurityFile` describes one
    layer for `security.schema.json` (adds `key`, `$remove`, `bindings`, `libraries`, `reservedTargets`;
    `groupId` is a string, no longer `uuid`).
  - **Problems** (every one listed at startup by `SecurityConfigurationLoader`, same sentences at build
    time): **SPARK047** guard rail (a deny, `isImportant`, a target the library does not ship, a group by id
    or another library's slot, an app-only member, a key with `:`, a `$remove`); **SPARK048** a token
    without its `wellKnown` entry, an unknown `@token`, an unbound slot, a binding to an undeclared group;
    **SPARK049** the app stating a library key other than to remove it, removing a key no library ships, a
    key prefix or binding or opt-out naming no referenced library. SPARK047 is reported in the library's
    own build by `LibraryLayersGenerator` and, for referenced libraries, by `SecurityConfigurationAnalyzer`
    in the app's, which reports 048/049 too; SPARK013 no longer judges tokens or slots.
  - **Deviation: what a library owns.** Ownership is judged on the resource's target (an attribute right
    by its type): the persistent-object and query names and aliases of the library's own model layers,
    plus the pseudo-types it declares in `"reservedTargets"` (`Moderation`). A reserved target that names
    a composed model type, or one another library already declares, is refused, so it cannot be used to
    claim an app type. "Its reserved verbs" (D4) are not a separate rule: a library may grant any verb on
    a target it owns, and nothing on a target it does not. Moderation's printed per-type grants
    (`Vote/Question`) therefore cannot become library rights in M9; its pseudo-type grants
    (`Review/Moderation` to `moderation:moderators`) can.
  - **Posture.** `securityPosture.txt` is the full table: `## Reachable without signing in (expanded)`
    (the old content), `## Granted to @anonymous`, `## Rights`, `## Inert: libraries switched off`, one
    row `group | effect | resource | key | layer` each (` | `-separated so a longer name does not rewrite
    every line; sorted by group, resource, effect, key). A row named by token shows it
    (`Signed-in users (@authenticated)`); an inert row shows the token unresolved. Verify fails on any
    drift (exit 3), prints a `::warning::` annotation when the anonymous sections moved, and exits 2 when
    the configuration does not compose. **The hash with layer identity is M7 (D7)**; M6 only renders the
    table. All five apps' posture files were regenerated with `--spark-synchronize-security`.
  - **Proof for CodeCoverage (production).** The effective rights were dumped from the old `security.json`
    (group name | effect | resource, sorted) before any change and from the new posture table after:
    identical for all five apps (CodeCoverage 37 rows, DemoApp 16, Fleet 26, HR 28, QnA 48), and each
    app's anonymous-reachable list is byte-identical to its old `securityPosture.txt`. Only the format and
    the `id` → `key` rename changed.
  - **Deviation: no real library grant yet.** No app grants anything on `SparkUser`, so an Authorization
    default (for example `Read/SparkUser` to `@authenticated`) would widen every app's effective rights,
    CodeCoverage's included, and decide a disclosure question D6 deliberately left closed. The path is
    proven with fixtures (`SparkSecurityLayersTests`, `SecurityLayersAnalyzerTests`, generator tests); the
    real grants are M9's. Moderation's `moderation.json` is the real library layer M6 ships (below).
  - **Moderation (grill Q6).** `MintPlayer.Spark.Moderation` has the alias `moderation` and ships
    `App_Data/moderation.json`: the reputation table and the four privileges (QnA's former values), each
    with `"Group": "moderation:voters|flaggers|downvoters|reviewers"`. `SparkModerationFiles.Compose`
    (Abstractions, `SparkKinds.Moderation`: every object per key ignoring case, arrays whole, `null`
    removes) composes it with the app's file; `AddSparkModerationFile` inserts the result as one
    configuration source at index 0, so appsettings and environment variables still override it. A
    `PostConfigure` resolves each privilege's `Group` slot through the security bindings into `GroupId`
    (exactly one group, slot only; a token or id is refused); the startup check refuses an unresolved one.
    Code-set `GroupId` (no `Group`) still works, which the test host uses (its `AddSpark(Action)` has no
    configuration builder, so it gets no library defaults). QnA's `moderation.json` shrank to its delta
    (`DownvoteCastTypes`) and its `security.json` binds the four slots by group name.
    `--spark-init-moderation` prints a `key` per right and the privilege's slot as `groupId`, the form an
    app or M9's library layer pastes.

### D5 — Ids for library-shipped model elements
Ids are deterministic UUIDv5 values over (library alias, type, member kind, member name). They
are **written into the shipped file** and **verified by the library's generator**, so a hand edit
or a rename is caught as a diagnostic. They are never computed silently at runtime, because a
rename would then change ids on the wire. The app layer cannot override `id`. Types the app owns
keep their minted ids.
- **As built in M4 (2026-10-06).**
  - **Seeds and namespace.** `SparkModelIds` (shared source, `libs/spark/Shared/Layering/SparkModelLayers.cs`)
    is RFC 4122 v5 in the fixed namespace `036a66ee-1790-46de-9f0e-4e1d856750c9`. The type is
    `{alias}:{Type}`; a member is `{alias}:{Type}.{attributes|tabs|groups}.{Name}`, and the file's
    queries `{alias}:{Type}.queries.{Name}`. The member kind is in the seed, so an attribute and a tab
    with one name cannot collide. The RFC's own example is a test.
  - **Verification.** `LibraryLayersGenerator` checks every shipped `Model/*.json`: **SPARK045**
    (error) names the path, the expected id and its seed when an id is missing or minted; **SPARK046**
    (error) when the file is not strict JSON or names no type, which applications would refuse at
    startup.
  - **Writer.** `npm run stamp:library-model-ids -- <library project folder>`
    (`tools/stamp-library-model-ids.mjs`) reads the alias from the csproj and writes the ids, keeping key
    order and rewriting `group`/`tab` references. Chosen over a `--spark-…` verb because a library has
    no host to run one in. It is a second implementation of the derivation; the generator is the
    check that the two agree.
  - **Production data.** Model ids are not stored in documents: RavenDB documents carry `@collection`
    and the CLR type, and the type id travels only on the wire (`/spark/po/{typeId}`, client caches).
    CodeCoverage's `SparkUser` id changed from `3f7c1d92-…` to `0d3faefa-…` (QnA's from
    `4e13c0de-…0001`) with no migration; a URL or bookmark naming the old type id stops resolving,
    and the alias `sparkuser` still does. No `security.json`, `programUnits.json` or right referenced
    either id (searched); the E2E test that pinned QnA's id now pins the derived one.

### D6 — One composed-model provider; the synchronizer writes only the app's delta
- One `IModelSource` (composed layers + app files) replaces every `*.json` glob (§1.2) and the
  generator input.
- The synchronizer reads the composed model and writes back only the **app layer's delta**. It
  never writes library-owned types or fields.
- `TranslationsSeeder` and `SparkSchemaReference`, which also write into app files, follow the
  same rule.
- **As built in M4 (2026-10-06).**
  - **One composer, both builds.** `SparkModelLayers.Compose` (shared source) groups library layers by
    `persistentObject.name`, puts the application's files naming a library type on top as deltas, and
    composes each with `SparkKinds.Model`. An application file naming no library type is the
    application's own type, as before: two application files are never merged. An application file
    the engine cannot read (not strict JSON, no name) is passed through as written, so each reader
    reports it exactly as it did. Problems the run time refuses: an unreadable library layer, an `id`
    changed by a later layer, two unrelated libraries stating a field differently (a library
    overriding one it depends on is silent, as for actions).
  - **Run time.** `SparkModelFiles.Compose(contentRoot)` (Abstractions; `SparkComposedType` with
    `Name`, `Json`, `FileName`, `AppFile`, `Library`, `Layers`, `Provenance`, `Source`) throws on those
    problems. `IModelSource` (MintPlayer.Spark, singleton, read once) wraps it for DI; `ModelLoader`
    reads it. Replaced: `ModelLoader`, the ten `SparkDevelopmentExtensions` verify gates, three
    `ModelSynchronizer` readers (existing types, description drift, missing translations),
    `ModelFileShape` (via `ModelHashFile`) and `ModerationInitCommand`: 16 sites.
    `QueryExecutor` and `SecurityConfigurationValidator` only named the directory in messages.
    `SparkEndpointFactory` writes test fixtures and is not a reader.
  - **Hash keys (interim until D7; the layers are recorded since M7, see D9 "As built in M7").** `ModelFileShape` hashes each composed type under its file name,
    a library type under the library's file name. A type moved from an application into a library
    therefore keeps its key and, ids not being structural, its hash: CodeCoverage's and QnA's
    `modelHashes.json` verified unchanged after `SparkUser` moved. HR references the library and gained
    `SparkUser.json` (re-synchronized; only `modelHashes.json` changed). The drift message still says
    "present on disk" for a library type; the provenance-aware table is D7.
  - **Generators.** `PersistentObjectNamesGenerator` composes the referenced libraries' model layers
    (`LibraryLayersReader`) with the application's AdditionalFiles through the same composer, so
    `PersistentObjectIds` and `PersistentObjectNames` carry library types (CodeCoverage:
    `SparkUser = "0d3faefa-…"`, verified in the build output). Only an executable gets library
    constants; a library compilation does not, and a library's own layer files (`SparkLayerPath`) are
    never its application model. `SecurityConfigurationAnalyzer` reads the composed model too; its "no
    model files, report nothing" rule still keys on the application's files.
  - **Writers.** The synchronizer starts from the composed model and writes a library type through
    `SparkModelLayers.Delta` (`SparkLayers.Delta`, the inverse of `Compose`): only what differs from the
    library layer (both serialized the same way first, so an omitted default is no difference), never an
    id the library states, never a removal (#253), and no file at all when nothing differs. It seeds no
    description of a library type into the application's `translations.json`. `TranslationsSeeder`
    already skipped keys any layer defines; `SparkSchemaReference` only edits the `$schema` line of
    files that exist. Neither needed more.
  - **Schema.** `model.schema.json` no longer requires `id` on the type, attributes, tabs, groups or
    queries, so a delta validates. The server still requires every composed element to have one.
  - **Deviation from the plan: `SparkUser` moved in M4, not M9** (owner's M4 brief). It ships from
    `libs/authorization/MintPlayer.Spark.Authorization/App_Data/Model/SparkUser.json` with derived ids,
    along with its `model.SparkUser` translations. CodeCoverage's copy and translations are deleted.
    QnA keeps a delta: `UserName` `showedOn: "PersistentObject"` (the library ships
    `"Query, PersistentObject"`, CodeCoverage's value; that was the only structural-or-visible drift).
    Fleet, HR and DemoApp had no copy.
  - **Deviation: the model `KindSpec`.** M2 keyed every array named `queries` by `name`, but
    `persistentObject.queries` holds sub-query aliases or `{ "query": … }`, without a name, so every
    file with a sub-query would have been refused. Only the file's root `queries` is keyed now;
    the sub-query list is replaced whole.

### D7 — Gates hash the composed result, with provenance
- `modelHashes.json` hashes the composed model. That follows the actions precedent: hash what is
  composed, not the files that went into it.
- It records each layer's identity (alias + version/hash), so a drift message names the layer
  that moved.
- Rights get the same treatment (D4).
- A library update that changes a structure therefore turns CI red and forces a reviewed re-sync.
  That is the review point visible files would have given.

### D8 — Uniform reload
- App layers of every kind reload through one watcher policy (all events, debounced).
- Library layers are compiled in, so changing them needs a rebuild.

### D9 — Visibility without copies
- `--spark-describe <kind> [name]` prints the composed result, annotated with the layer each value
  came from.
- The generated `AGENTS.md` documents the command.
- ~~Optional composed view in `obj/spark/`~~: rejected (Q4 = A). `--layers` shows the raw layers on
  demand. Startup logs the active library layers.
- **As built in M7 (2026-10-06): D7, D8 and D9.**
  - **Hashes with provenance (D7).** `modelHashes.json` is version 2. Beside the composed hashes
    (`files`, `configFiles`) it records `libraries` (alias → assembly) and `layers`: for every entry a
    library states a layer of (`Model/<file>`, `actions.json`, `programUnits.json`), each layer (the
    alias, `app` for the application's file) and the **structural** hash of what that layer alone
    states (`ModelFileShape.DescribeJson` per model layer, `ConfigFileShape.DescribeLayerActions` per
    actions layer, the units rendering per menu layer). An entry only the application states has no
    `layers` entry. The layers are part of the `modelHash` roll-up (`layers:` line), so the file cannot
    go stale against them: a library that starts stating what the application's delta already states
    turns the gate red too, and the re-synchronization is the review.
    - **Deviation: no assembly version.** D7 said "alias + version/hash". A version changes on every
      release, so every bump would turn every application's gate red with nothing to review; the
      identity is the per-layer structural hash, and `libraries` names the assembly.
    - **Drift messages** name the layer: `file SparkUser.json: expected … actual … — layer library
      'authorization' (MintPlayer.Spark.Authorization) changed`; `the application's file changed` for
      an edit of the delta; `layers of Model/X.json: the composed structure is unchanged, but the layers
      stating it moved: …`; `library <alias> (<assembly>): newly states a layer …`. A version 1 file
      says it predates the layers instead of printing two bare hashes.
    - **M4's leftover fixed:** a type a library newly ships reads `shipped by library 'authorization'
      (…)` (with `, with the application's delta` when the app has one), never "present on disk".
    - **Program units compose (D3).** `SparkKinds.ProgramUnits` (groups and their units keyed by `id`,
      names ignoring case) and `SparkProgramUnitsFiles` (Abstractions) compose the libraries'
      `programUnits.json` layers with the application's file; `ProgramUnitsLoader`, the
      `--spark-verify-model` target check and the hash read the composed menu. No library ships one
      yet, so every application's menu and its hash are unchanged.
    - **Rights (D4/D7).** `securityPosture.txt` stays the gate and is compared as text, which is all a
      hash of it would compare. New section `## Layers: libraries that ship rights (alias | assembly |
      hash)`, one line per library with a `security.json` layer (`SparkSecurityFiles.Layers`: the first
      12 hex digits of the SHA-256 of the layer, whitespace aside; `| switched off` when opted out).
      `--spark-verify-security` now prints the changed lines (`-`/`+`) instead of both files whole, and
      `Changed by layer: <alias> (<assembly>), app` from the changed rows' layer column and the changed
      `## Layers` lines. No library ships rights yet (M6 deviation), so every posture file shows
      `(nothing)` there.
  - **One watcher policy (D8).** `AppLayerSnapshot<T>` (MintPlayer.Spark, internal) is the only
    `FileSystemWatcher` left in the loaders: the snapshot is composed on first read (a failure throws, so
    startup fails), recomposed on every watcher event for the named files (Changed, Created, Deleted,
    Renamed; 100 ms debounce that restarts on each event, so one save composes once) and swapped
    atomically; `Reloaded` is raised after a swap.
    - **Reloads:** `actions.json`, `translations.json`, `security.json`, `programUnits.json`,
      `culture.json` (newly: program units and culture were read once). The actions and security 5-minute
      `MemoryCache` expiry is gone; `IActionsCatalogueLoader.InvalidateCache` and
      `ISecurityConfigurationLoader.InvalidateCache` became `Reload()` (recompose now).
    - **Failure policy per kind (deviation from "one policy").** Every kind keeps its previous snapshot
      when a reload does not compose, except rights: `security.json` keeps its existing, tested
      fail-closed behaviour (`AppLayerFailure.Refuse`: every read throws until the file composes again),
      because the previous rights are no longer what the file says.
    - **Labels follow a translations reload (M5's leftover).** `ITranslationsLoader.Reloaded`; a snapshot
      given `labels:` recomposes at once (not debounced again) when it fires, so when
      `TranslationsLoader.Reload()` returns, the action catalogue, the program units, the culture and
      the model's labels have followed.
    - **Not reloaded: the model's structure** (deviation from D8's "every kind"). `ModelLoader` rebuilds
      its definitions from the same composed model when the translations reload, but `IModelSource` is
      still read once: the model is verified against the compiled entity classes at startup
      (`modelHashes.json`), and a structure read later would never have passed that gate.
      **Moderation** does not reload either: its consumers read `IOptions<SparkModerationOptions>`, once.
      Library layers never reload (D15).
  - **`--spark-describe <kind> [name] [--layers]` (D9)**, handled by `SynchronizeSparkModelsIfRequested`
    like the model verbs (no new call in any `Program.cs`), exits before the host is built. Kinds:
    `actions`, `model`, `translations`, `security` (alias `rights`), `programUnits`, `moderation`.
    Without `--layers` it prints the composed JSON (the rights as a `key | resource | group | effect`
    table, resolved as the evaluator reads them, with inert rights and problems); with it, one line per
    leaf, `path = value @layer` (the engine's `SparkComposition.Describe`, which gained a layer-name map
    and a path filter), a library by alias, the application as `app`. A name narrows to an action, a
    model type, a translation key prefix, a right's key or resource, a program unit or group id, or a
    moderation section. Header: `actions, composed from: spark (MintPlayer.Spark) → app (App_Data/actions.json)`.
    `--spark-print-effective-actions` is removed (no backward compatibility); `AGENTS.md` and the
    custom-actions guide name the new verb. The schemas' descriptions are M10's.
    - **Deviation:** D9 said the default output is annotated and `--layers` shows raw layers; as built,
      the annotation is what `--layers` adds (the M7 brief), and the raw layers are the libraries'
      files themselves.
  - **Startup log.** `UseSpark` logs each library layer once at Information
    (`Spark layers: authorization (MintPlayer.Spark.Authorization) ships model, translations.`), or that
    none ships any.
  - **Regenerated (evidence).** All five apps' `modelHashes.json` (`--spark-synchronize-model`) and
    `securityPosture.txt` (`--spark-synchronize-security`); `git status` showed no other file touched, so
    no model file changed. Then `--spark-verify-model` and `--spark-verify-security` exit 0 for QnA, Fleet,
    HR, DemoApp and CodeCoverage. Before regenerating, every app's verify-model exited 3 (the `layers:`
    roll-up line). What moved besides `version`, `modelHash`, `libraries` and `layers`:
    - **CodeCoverage (production):** `configFiles.programUnits.json`. The old rendering read the root
      `_comment` array of that file as menu entries (`_comment[0]`, …); the composed menu drops
      annotations (D2), so the hash moved with no change to the menu. Its posture gained only the empty
      `## Layers` section; every row is byte-identical.
    - **Fleet:** `files.SparkUser.json` was missing since M4 (Fleet references Authorization; its gate was
      already red, now reading `file SparkUser.json: shipped by library 'authorization'
      (MintPlayer.Spark.Authorization) but not in modelHashes.json`).
    - The other apps: only the new fields, and the empty `## Layers` posture section.

### D10 — Translations join the runtime engine
- Translations stop being composed at compile time. The app layer is read from disk (and reloads)
  like every other kind.
- The `HostTranslationsAggregatorGenerator` registry is removed.
- The generators that need translations at compile time read the library attributes plus the
  app's AdditionalFiles, as the analyzers do for actions today. S3 found that only SPARK_TRANS_005
  needs this; it moves to an analyzer like `LibraryActionsConflictAnalyzer`.
- `LibraryTranslationsGenerator` is gated off for the host. Today it also embeds the host's own
  file, which would double the app layer at runtime (S3). **Done in M3:** it is replaced by
  `LibraryLayersGenerator`, which embeds nothing in an executable.
- The static `SparkTranslations.All` (read at `SparkText.cs:32`, `TranslationsLoader.cs:16`,
  `TranslationsSeeder.cs:77` and `ModelSynchronizer.cs:1300`, and filled today by a ModuleInitializer)
  becomes a startup-registered snapshot that is swapped atomically on reload.
- Cost (S3, measured): 3–20 ms warm and 66–172 ms cold for 3 layers and up to 586 keys. That is
  acceptable, provided the engine uses indexed lookups; a naive version took 120 ms warm.
- **As built in M5 (2026-10-06).**
  - **Engine.** `SparkKinds.Translations` (ordinal keys and languages, a language must be a string,
    the app's `""` ignored) plus `SparkTranslationLayers` (shared source) flatten each layer first, then
    compose with `SparkLayers.Compose`. A namespace's `null` is expanded into one `null` per key the
    layers below stated under that prefix (a sorted set, one range per removal), as a separate layer
    applied before the layer's own keys, so a layer can remove a namespace and restate part of it. A key
    left with no language is dropped. Library order is the catalog's (core first, then by dependency),
    which settles S2 drift item 2.
  - **Flattening rules.** An object of strings is a translation; an object of objects and `null`s is a
    namespace (`null` removes); an all-`null` object is a namespace. A language cannot be removed:
    `{"en": "x", "nl": null}` is SPARK_TRANS_002 (mixed). Refused at run time and reported at build
    time: mixed (002), an array (004), one key stated twice, dotted and nested (**SPARK_TRANS_006**, new,
    error), and invalid JSON (001, now the strict `SparkJson` reader, so a duplicate property is 001).
    An empty object (003) is skipped. `LibraryLayersGenerator` checks every `translations.json` through
    the same flattener; MiniJson and `TranslationsTreeFlattener` are deleted.
  - **Run time.** `SparkTranslations` (Abstractions) is no longer a static registry: `Compose(layers)` and
    `Compose(contentRoot, libraries?)` return `SparkTranslationsComposition(All, Conflicts)`;
    `Libraries` reads `SparkLayerCatalog`. `ITranslationsLoader` (singleton) holds the snapshot: composed
    on first use (a broken file throws, so startup fails), reloaded by a `FileSystemWatcher` on
    `translations.json` (the actions loader's policy, 100 ms debounce), swapped atomically. A reload that
    does not compose is logged and keeps the previous snapshot. Library conflicts are logged at warning
    level with SPARK_TRANS_005's wording. `GET /spark/translations` serves the snapshot.
  - **Readers.** `SparkText.Resolve`/`Lookup` take the translations as an argument; `ModelLoader`,
    `ActionsCatalogueLoader`, `CultureLoader` and `ProgramUnitsLoader` inject `ITranslationsLoader`.
    `ActionsCatalogueLoader.Build` takes an optional dictionary (none: humanized names), and
    `--spark-print-effective-actions` composes from disk. `TranslationsSeeder` and
    `ModelSynchronizer.DescribeMissingTranslations` compose from the content root, since they run without a
    service provider.
  - **Removed.** `HostTranslationsAggregatorGenerator` (+ producer), the generated
    `SparkTranslationsRegistry` and its ModuleInitializer, `SparkTranslations.Register/All`, the aggregate
    models, its snapshot test. `LibraryLayersGenerator` stays gated to libraries (M3), so an application's
    file is only read from disk, never also embedded.
  - **Analyzer.** `LibraryTranslationsConflictAnalyzer` reports SPARK_TRANS_005 for an application (as the
    aggregator did), composing the libraries' layers and the app's `translations.json` AdditionalFile
    through `SparkTranslationLayers`; a library overriding one it depends on is silent. An app file that
    does not read is left out (001–004 report it), so library conflicts are still found. The app stating
    a key does not silence a library conflict, as before.
  - **Golden.** `tests/MintPlayer.Spark.Tests/Layering/Golden/{qna,codecoverage}-translations.golden.txt`
    were dumped by reflection from the compiled `SparkTranslationsRegistry.All` in each application's bin,
    before the aggregator was removed (398 and 586 keys, as in S3); `SparkTranslationLayersTests` composes
    the core and Authorization files plus the app's and compares, language order included.
  - **Deviation: baked texts do not follow a reload yet** (fixed in M7, see D9 "As built in M7"). The model's, the action catalogue's, the
    program units' and the culture's labels are resolved when those loaders load (the model and culture
    once, the catalogue per its 5-minute cache or its own file change). `GET /spark/translations` and every
    `Resolve` follow a reload at once. Re-resolving the baked texts belongs to M7's uniform reload (D8).
  - **Deviation: the JSON schema** still describes a namespace as objects only; it learns `null` with M10.

### D11 — DRY imports
- `spark.props`/`.targets` (and each library's targets) are imported **once** from
  `Directory.Build.props`/`.targets` for in-repo projects. The hand-written `<Import>`s in every
  app and test project are removed.
- Every target is guarded, because the files now reach every project, including netstandard2.0
  generators and tests.
- Application detection uses `'$(OutputType)'=='Exe' And '$(IsTestProject)'!='true'`, with an
  explicit opt-out property.
- **Amended by S4:**
  - **Everything is imported from `Directory.Build.targets`, the props included.** `OutputType`
    and `IsTestProject` are still empty when `Directory.Build.props` is evaluated, for all 56
    projects. That only holds while no csproj body reads `SparkAppDataDir`/`SpaRoot` at
    evaluation time, and none does.
  - **The opt-out is `<SparkApplication>false</SparkApplication>`.** Tools need it:
    `tools/SchemaGenerator` is an Exe and otherwise gets SPARK001 and an `AGENTS.md`.
  - **Library targets** (for example `spark-authorization.targets`) gate at execution time on
    `@(ReferencePath)`, never on the application type. Otherwise DemoApp, which has no
    Authorization reference, got SPARK030 and a generated `spark-auth.setup.ts`.
  - **Two kinds of target stay per project.**
    - The targets that add ProjectReferences (`spark-allfeatures.targets`,
      `spark-contributions.targets`), because restore does not see `IsTestProject`.
    - `spark-testing.targets`, because only 3 of the 5 test projects get its AGENTS.md.
  - **Proven:** 18 hand `<Import>`s removed, the solution builds with 0 errors and the same 309
    warnings by code, no new files appear in any project, and the 5 apps' AdditionalFiles,
    ProjectReferences and Spark properties are identical to before.

### D12 — One `App_Data` location
- Every runtime and generator site resolves `App_Data` through one helper, honouring
  `SparkAppDataDir` end to end.
- The 24 hard-coded `"App_Data"` sites and the literal `"App_Data/Model/"` matches in the
  generators are replaced.

### D13 — `IManager` applies rights at construction (G6)
- `IManager.GetPersistentObject` (and every construction path, including New and the hooks) runs
  the same attribute-rights presentation the endpoints run today. It prunes per caller.
- Endpoints stop presenting a second time.
- Code that needs the unpruned object asks for it explicitly, with an elevated call that is named
  so it can be reviewed.

### D14 — One engine source, compiled into both the runtime and the generators
Generators and analyzers (`netstandard2.0`) need composed data at compile time, for
`PersistentObjectIds` and the security analyzer. They cannot reference
`MintPlayer.Spark.Abstractions`. So the `Compose` and `KindSpec` code lives in **one shared source
file set**, linked into both. Two engines are exactly how actions (runtime) and translations
(compile time) drifted apart. There is a test that runs the same golden layers through both
builds.
- **The tree type (S2).** The generators have no System.Text.Json, and their MiniJson rejects
  arrays, which the model kind needs. So the shared source works on a small abstract JSON tree
  (or MiniJson with arrays added), not on `System.Text.Json.Nodes`.
- **Three engines today, not two.** `LibraryActionsConflictAnalyzer` re-composes actions at
  compile time. It folds into the shared engine.
- **As built in M2 (2026-10-06).** The source set is `libs/spark/Shared/Layering/` (`SparkJson`,
  `SparkLayers`, `SparkKinds`), linked into `MintPlayer.Spark.Abstractions` and
  `MintPlayer.Spark.SourceGenerators`, internal in both. The tree is our own (`SparkJson*`), not
  MiniJson: MiniJson stays the translations reader until M5 replaces it. Decided while building:
  - **Strict JSON in every layer.** No comments, no trailing commas, no duplicate keys. The actions
    run time used to skip comments and trailing commas (`JsonCommentHandling.Skip`); no file in the
    repository used either, and `_`-prefixed keys are the comment mechanism the schemas publish.
    Strictness is what the generators already had, so it is the choice that removes drift item 5.
  - **What refuses a layer, and what only reports.** A layer that cannot be read (invalid JSON, a
    key twice under the kind's comparer, a value of the wrong shape via `KindSpec.Shape`, a keyed
    element without its key, `$remove` next to anything but the key) throws `SparkLayerException`.
    A statement the kind forbids (changing an immutable `id`, a `null` where `NullRemoves` is off)
    is ignored and listed in `Errors`, so the caller can report every one at once.
  - **The analyzer** reports nothing when a library layer does not compose, rather than skipping
    only that layer. The run time refuses the same layer at startup with its message.
  - **Conflicts name the first spelling** of an action and property, not the conflicting layer's.
  - The golden files (S2) are committed in `tests/MintPlayer.Spark.Tests/Layering/Golden/`. They
    were captured from the hand-written engine before the port, and both builds are tested
    against them.

### D15 — What is uniform and what stays per kind
- **Uniform across every kind:**
  - shipping (one generator)
  - discovery and layer order
  - the app layer read from disk and reloaded
  - the engine semantics: per-property merge, `null` removes or resets, the app wins silently,
    and a library-vs-library conflict is a diagnostic
  - gates hash the composed result, with provenance
  - `--spark-describe`
  - writers touch only the app delta
- **Per kind, by necessity:**
  - element keys (`KindSpec`)
  - model ids are immutable
  - rights are a keyed grant set with guard rails, not overridable fields (D4)
  - translations keep "`""` = untranslated" **and gain `null` removal**, which they lack today
  - key case sensitivity: actions are case-insensitive and translations are ordinal. This is
    deliberate (S2 drift list).
  - **Uniform, newly for translations:** the library order rule (core first, then by
    dependency). Translations are only ordinal-alphabetical today (S2).
- **Not layered:**
  - `culture.json` (app-only)
  - `modelHashes.json` and `securityPosture.txt` (gate outputs)
  - `oidc-signing-key.json` (runtime state)
  - ~~`moderation.json`~~: it is layered (Q6 = B); see D3
- **Library layers never hot-reload**, because they are compiled in.

### D13a — Decided shape of D13 (grill Q5, delegated by the owner with two constraints)
The owner's constraints (2026-10-06):
- **(1)** Breadcrumbs keep rendering properly.
- **(2)** No API response exposes hidden values, nor even that a hidden attribute exists.

The decision: **construction-time pruning plus one boundary safety net.**
- **Construction.** `IEntityMapper` (`GetPersistentObject` and `ToPersistentObject`; `IManager` only delegates to it, S5) builds persistent objects pruned for the current caller and verb. Pruning
  **removes** refused attributes; it does not blank them, so their existence does not leak.
  System code uses a named, reviewable `manager.AsSystem()`. A request with no HTTP caller (a
  background job) is explicitly the system caller, never an anonymous one.
- **Boundary net.** One serialization step checks every persistent object in an outbound response:
  the envelope, query rows, retry prompts and client operations.
  - In **Development** an unpresented object **throws**. That covers an object built with
    `new PersistentObject` or `AsSystem()` and then returned.
  - In **Production** it prunes.
  - The ten scattered `PresentAsync` calls (Get, New ×2, Refresh, Create/Update via
    `SaveResponsePresenter`, `ExecuteCustomAction`, `RowSecurityGate`, `RetryPresentation`,
    `PersistentObjectPresenter`) collapse into the net.
- **Breadcrumbs (constraint 1).** These are resolved from the **entity** by `BreadcrumbResolver`,
  not from the pruned object's attributes, so pruning does not break them. Tokens a static right
  refuses already render empty, the same as an empty value (`BreadcrumbResolver.cs:440-443`; the placeholder is only for a whole denied document, :237, :413; corrected by S7;
  contributions M2c-2a).
  - The breadcrumb **template** sent with type metadata must not name a hidden attribute either.
    S7 found that it does today (EntityTypeDefinition.cs:96), so `ForFormAsync`/`ForQueryAsync` rewrite it, and query `SortColumns`/`Columns` are filtered as well (§9 S7).
- **Leak test (constraint 2).** A test seeds canary values and canary attribute names on hidden
  attributes. It asserts that **no** serialized response contains either: PO get/new/refresh/save,
  query rows, breadcrumbs, type metadata, retry prompts and error messages.
- **Trap.** Hooks that read a hidden attribute now see it missing, and silently compute something
  wrong. S5 lists every `GetPersistentObject` caller and every attribute-reading hook before
  pruning is switched on. Each one that needs hidden data moves to `AsSystem()`. Writing to a pruned attribute is a silent no-op, and hooks get `TryGet`, because ForgeAccountsActions.cs:51 writes a Read-denied attribute (§9 S5).
- **As built in M8 (2026-10-06).**
  - **The construction API is async.** The rights decision is async (the caller's groups come from a
    pluggable provider), so `IEntityMapper`/`IManager` expose `GetPersistentObjectAsync(name|id|<T>, verb = "Read")`
    and `ToPersistentObjectAsync(entity, …, verb)`. The sync names are gone (no backward compatibility).
    `AsSystem()` returns `ISystemManager` / `ISystemEntityMapper`, whose methods stay sync because they
    decide nothing.
  - **Verbs** (`AttributeRightsEnforcement.Verbs`): `Read`/`Edit` remove Read-denied and mark Edit-denied
    read-only; `New` removes Read-denied and marks New-denied read-only; `Query` removes Query-denied.
    These are the pairs the old `PresentAsync` call sites passed. The row gate passes its own verb
    (`Query`). History's presenter now also marks Edit-denied read-only, which it did not before.
  - **No HTTP caller is the system for presentation only.** `PresentAsync` returns untouched when there
    is no `HttpContext`. `IAttributeRights` (and with it the write shield) and row security are
    unchanged: `SparkSystemContext` stays positive-claim-only for them. Evidence: the deny-in-a-scope
    tests (`MaterializeInterceptorTests`, `HistoryTests`) rely on that.
  - **Pruning is recorded on the object** (`PersistentObject.PruneAttributes`). The indexer answers a
    detached attribute for a pruned name (write = no-op, read = null) and still throws for a name the
    type never had. `TryGetAttribute` is the honest read. `IClientAccessor.RefreshAttribute(po, name)` is
    a no-op for a pruned name, because the patch would name it.
  - **The net is a `JsonTypeInfo.OnSerializing` hook on `PersistentObject`** (`SparkPresentation`). It is
    installed into the HTTP `JsonOptions` (PostConfigure) and into the streaming endpoint's own options.
    A result filter was rejected: it sees opaque `IResult`s, would need a reflection walk, and misses the
    middleware's 449 and WebSocket frames. The serializer already visits every object written, including
    an `object`-typed result and AsDetail rows. Each presentation stamps a per-request token kept in
    `HttpContext.Items`. Outside Development an unstamped object is presented, failing closed (Query- and
    Read-denied removed, Edit-denied read-only), and a warning is logged. That fallback blocks on the
    rights decision, which the request's type check has normally already made. The test host runs as
    `Testing`, so it takes the pruning path.
  - **Deviation: three explicit presentations remain.** The net cannot collapse a site whose object was
    not built for the caller, because Development would throw there by design:
    - Refresh ×2: the effective object is a system construction.
    - `SaveResponsePresenter`, the posted-object branch: the object is read off the wire.

    Removed: Get, New ×2, ExecuteCustomAction ×2, `RowSecurityGate`, `RetryPresentation` (static half),
    `PersistentObjectPresenter`, and `SaveResponsePresenter`'s reload. Per-row `RedactAsync` stays
    everywhere.
  - **System constructions:**
    - `EffectiveObjectFactory`
    - `SaveValidation`'s stored object
    - `SyncActionHandler`
    - History's revert input. This is a save input, judged by the write shield as a posted object is.
  - **S7:**
    - The type breadcrumb template drops refused tokens and keeps and re-escapes its literals.
      `ForFormAsync` drops Read- and New-denied tokens; `ForQueryAsync` drops Query- and Read-denied ones.
    - `RendererOptions` entries whose string value names a refused attribute are dropped, on definitions
      and on objects; an emptied map becomes null.
    - A `SparkSubQuery.ParentReference` naming a Query-denied attribute of the query's row type is
      dropped.
    - `Queries/Get|List` go through `ForQueryMetadataAsync`, which filters `SortColumns`, `Columns` and
      `ParentReference`.
    - Query rows are `QueryResultItem`s projected from rows the gate built for the caller, so they are not
      persistent objects on the wire.
  - **Production change in CodeCoverage (deviation).** `MyAccountRowActions.ProviderOf` read `Provider`
    off the rebuilt parent. That attribute is Read-denied to the page's only readers, so pruning would
    have scoped nothing and shown every forge's accounts on each forge page. `ForgeAccountsActions` now
    sets the page's id to the forge (the route's id), and the grid scopes by `parent.Id`. No other app
    hook reads an attribute any real caller is denied: only CodeCoverage's `security.json` has attribute
    denies, and library layers may only grant.
  - **Known gaps.**
    - `IClientAccessor.RefreshAttribute(Guid, id, name, value)` names an attribute without an object, and
      nothing checks it.
    - The leak test does not cover a sub-query `parentReference`, because startup validation needs a
      real reference.
    - A create whose required attribute is hidden would still report it by name in its validation error.
  - **Tests.** `ConstructionRightsTests` covers:
    - the canary leak test: the name and value of a hidden attribute, over PO get/new/refresh/save, query
      rows, type metadata, query metadata, an action result, a retry prompt, the net's fallback and a 404,
      every response body recorded raw;
    - §7 acceptance;
    - the New verb;
    - the write no-op;
    - system outside a request;
    - `AsSystem`, sync and save validation keeping hidden data;
    - Development throws, other environments prune.

### D16 — Library alias (grill Q7 = B)
- Every library that ships layers declares `<SparkLibraryAlias>authorization</SparkLibraryAlias>` in
  its csproj. It is **required**, with no default derived from the assembly name.
- The generator reads it through `CompilerVisibleProperty` and stamps it into the layer attribute.
  This was proven over both reference kinds (S1).
- A library author references the generator package with `PrivateAssets="all"`. Without it the
  generator flowed into the app despite the nuspec's `exclude`, ran twice, and broke the build with
  CS0101 (S1). An analyzer checks this, the way SPARK002 does.
- The alias is used in:
  - right keys (`authorization:passkeys-read`)
  - slot tokens (`moderation:moderators`)
  - the opt-out (`"libraries": { alias: false }`)
  - UUIDv5 ids (D5)
  - layer names in `--spark-describe` and in the posture table
- Two layered libraries with the same alias are a generator error in the app and a startup error.
- The generator warns when an alias is too generic. The docs say to choose it like a NuGet package
  id: it is effectively permanent, because changing it rewrites keys and ids in every consuming
  app.
- **As built in M3 (2026-10-06):**
  - **SPARK041** (error): layer files and no alias, or one that is not lower-kebab
    (`^[a-z][a-z0-9]*(-[a-z0-9]+)*$`, because it becomes part of right keys and ids); nothing is
    embedded. **SPARK042** (warning): a generic alias, from a short deny-list (`app`, `application`,
    `base`, `common`, `core`, `default`, `extensions`, `lib`, `library`, `main`, `module`, `plugin`,
    `shared`, `test`, `tests`, `util`, `utils`). **SPARK043** (error): two referenced libraries with one
    alias, from `ApplicationLayersGenerator`; `SparkLayerCatalog` throws the same at startup.
  - **SPARK044** (MSBuild warning) is the `PrivateAssets` check. It is detectable: it lives in the
    generator package's `build/` targets, which NuGet imports into the referencing library, and reads
    that `PackageReference`'s `PrivateAssets` exactly as SPARK002 does. A Roslyn analyzer could not
    see package metadata.
  - **Aliases:** `MintPlayer.Spark` = `spark`, `MintPlayer.Spark.Authorization` = `authorization`,
    the two libraries that ship layers today. Moderation, Messaging, IdentityProvider and
    Contributions ship none yet; each gets its alias in the milestone that gives it a layer (M6/M9
    for Moderation's `moderation`), since an alias without layers is never read.

## 5. Open decisions (grill)

- ~~**Q1** Transport~~ → **C** (decided).
- ~~**Q2** Library default rights~~ → **B**: adopted. The guard rails are grant-only, own
  resources only, group tokens and removable by key (`$remove`, amended in M6). There is no opt-in; the readable posture
  table is the gate.
- ~~**Q3** Library ordering~~ → **B, dependency order**.
  - A library stacks above every library it references, and the app is on top.
  - Alphabetical order only breaks ties between unrelated libraries.
  - Overriding a dependency is intentional and silent. Overlap between **unrelated** libraries is
    the only conflict diagnostic.
  - One shared ordering function (D14) runs at runtime (`GetReferencedAssemblies`) and at compile
    time (referenced assembly symbols). A test checks that both give the same order.
- ~~**Q4** Composed view as files~~ → **A, CLI only**.
  - Use `--spark-describe <kind> [name] [--layers]`. No files are written to `obj/spark/`, because a
    build-written view goes stale as soon as the hot-reloaded app layer changes.
  - Discoverability: every kind's JSON schema description mentions the command, and startup logs
    the active library layers.
- ~~**Q5** Where attribute rights apply~~ → **construction plus a boundary net** (D13a). Delegated
  by the owner under two constraints: breadcrumbs keep rendering, and nothing hidden leaks, neither
  value nor existence.
- ~~**Q6** `moderation.json`~~ → **B, it joins the engine as a kind**:
  - The Moderation library ships the default reputation table and privileges as a library layer.
  - The app overrides per key; privileges are keyed by name, and `null` removes one.
  - Raw `GroupId`s become group tokens and slot bindings, as for rights (D4). An unbound slot fails
    startup.
  - The composed result feeds the existing option classes through a configuration source that sits
    **below** appsettings and environment variables, so per-deployment overrides still work.

## 6. Spikes

- **S1** A generalised generator embeds a library's `App_Data/Model/*.json` plus `security.json`
  as attributes, and an app in this repo (ProjectReference) **and** a packed nupkg
  (PackageReference, local feed) both see identical layers at runtime and in a generator.
- **S2** `SparkLayers.Compose` reproduces today's actions composition byte for byte (golden test
  over QnA's `actions.json`). It also composes a model delta (an app overriding `showedOn` of a
  library type).
- **S3** Moving translations to runtime (D10) keeps the translations analyzers and generators
  working, and measures the startup cost.
- **S4** `Directory.Build.targets` imports `spark.targets` repo-wide. All projects build, the
  guards hold, and nothing is written into the generator or test projects.
- **S5** D13: list every caller of `GetPersistentObject` and measure how many depend on unpruned
  attributes (actions hooks that read hidden fields).
- **S7** Find every place type metadata or a response carries a breadcrumb **template**, a
  `showedOn` list or an attribute list. Confirm whether a hidden attribute's *name* reaches the
  client today, as an existence leak (constraint 2 of D13a).
- **S6** ~~Only if Q1 = B~~ (Q1 = C, so not run): a Static-Web-Assets-style getter target plus a buildTransitive manifest,
  composed into `obj/` before CoreCompile, with readers switched to `AppContext.BaseDirectory`.
  Prove `dotnet run`, the E2E host and `ScratchContentRoot` still work.

## 7. Acceptance

- `MintPlayer.Spark.Authorization` ships `SparkUser`, `Passkeys` and `PasskeyRow` (model) plus
  their default rights. CodeCoverage, Fleet and HR delete their hand copies, and the passkeys
  page works on each. QnA overrides one `SparkUser` attribute with a delta.
- An app override of one property of a **library** action composes. No golden file in the repo
  exercises an override today: QnA's `actions.json:4` is the app's own `CloseQuestion` (S2). Core New/Edit/Delete are shipped by the
  core layer.
- The same app, consuming the libraries as **packages** from a local feed, composes identically
  (S1, kept as a test).
- A library update that changes a model structure or an effective right fails
  `--spark-verify-model` / `--spark-verify-security`, and the message names the layer.
- `IManager.GetPersistentObject` returns a pruned object for a caller lacking an attribute right
  (test).
- No app or test csproj contains a hand-written Spark `<Import>`.

## 8. Decision log

| # | Decision | Reason / evidence |
|---|---|---|
| 1 | No backward compatibility | Owner, 2026-10-06 |
| 2 | Library defaults must reach apps on update, so a one-time copy is ruled out | Owner, 2026-10-06 (G3) |
| 3 | Works identically for package and project references, DRY | Owner, 2026-10-06 (G4) |
| 4 | Transport C: embedded attribute layers, composed at runtime | Owner, 2026-10-06 (grill Q1). Proven for both reference kinds by actions and translations. A (copy into App_Data) loses files on an Nx cache hit and is misread by `*.json` globs. B (compose into bin) is invisible to readers based on the content root (`dotnet run`, E2E, `ScratchContentRoot`) |
| 6 | Libraries ship default rights. They are active when the library is referenced, the guard rails apply, and a readable posture table is the review gate | Owner, 2026-10-06 (grill Q2 = B). This deliberately reverses the "a package cannot ship its own rights" position (`SecurityConfigurationLoader.cs:16-18`). An opt-in was rejected as a second switch beside referencing the package |
| 7 | Per-library opt-out of shipped rights (`"libraries": { alias: false }`), still listed as inert in the posture table | Owner, 2026-10-06 (grill Q2 follow-up) |
| 8 | Layer order follows dependencies: a library sits above the libraries it references, alphabetical only between unrelated ones | Owner, 2026-10-06 (grill Q3 = B). Alphabetical order made extension depend on names, and flagged intended overrides as conflicts |
| 9 | Attribute rights apply at construction (`IManager`, with `AsSystem()` for system code), plus one boundary net that throws in Development and prunes in Production; pruning removes attributes; breadcrumbs resolve from the entity | Grill Q5, delegated by the owner (2026-10-06) under the constraints "breadcrumb keeps rendering" and "no hidden value or existence in any API response". Today about ten scattered `PresentAsync` call sites each have to remember to prune (see D13a) |
| 10 | CLI-only visibility (`--spark-describe`), with no composed files | Grill Q4 = A, 2026-10-06. A view written by the build goes stale against the hot-reloaded app layer |
| 11 | `moderation.json` joins the engine; group ids become tokens or bindings; it stays reachable as `IConfiguration` below appsettings and environment variables | Grill Q6 = B, 2026-10-06. It is the same group-reference problem as D4, and its defaults belong to the library (G1) |
| 12 | Explicit, required short alias per library (D16) | Grill Q7 = B: the owner's own proposal, the final call delegated to Claude (2026-10-06). An alias implied by the assembly name would change keys and ids silently on a rename, and make collisions likelier |
| 5 | One engine source, linked into both the runtime and the generators (D14) | The actions and translations engines drifted apart (case sensitivity, `null`/`""`, ordering), per the inventory investigation |

## 9. Spike results

### S1 — embedded layers over ProjectReference and PackageReference (2026-10-06, standalone prototype)

**Verdict: PROVEN, with two pitfalls (D1, D16 amended).**

**Setup.** A standalone prototype was built outside the repo, so the repo's Directory.Build files
could not affect it:
- **Contract.** `[assembly: SparkLayer(alias, path, json)]`.
- **Library generator** (netstandard2.0, packed into `analyzers/dotnet/cs`). It reads AdditionalFiles
  that carry `SparkLayerPath` metadata, plus `build_property.SparkLibraryAlias`.
- **Items.** `buildTransitive` props/targets add `Model/*.json` and `security.json` as AdditionalFiles.
- **App-side dump generator.** It reads `SourceModule.ReferencedAssemblySymbols`.
- **Libraries.**
  - An in-repo-style library (ProjectReference plus Import) holding QnA's real `SparkUser.json`,
    `DeleteReason.json` and `security.json`.
  - A third-party-style library built **only from packages**.
- **Two apps.** One consumes the libraries by ProjectReference, the other by PackageReference from a
  local folder feed.

**Result.** The runtime dump and the compile-time dump (`alias|assembly|path|length|sha256`, for
example `authorization|Proto.Auth|security.json|10088|D2A7B61251C49CA3`) are **byte-identical
between the two apps**.
- **Consumers** need no buildTransitive for transport, because the layers sit inside the dll.
- **Library authors** need it for the items, the metadata and `CompilerVisibleProperty`.

**Pitfalls**
1. **Reference trimming.** A library whose types the app never uses is dropped from the app's
   metadata. Its layer was seen at compile time but not by the runtime walk. The fix, proven: the
   app-side generator records `[assembly: SparkLayerAssemblies(...)]` and the runtime loads exactly
   those names (D1).
2. **The generator flows to the app.** Without `PrivateAssets="all"` on the library author's
   generator reference, the generator reached the app and ran twice, which fails with CS0101 (D16).
3. **Size is not a constraint.**
   - 1 MB and 20 MB JSON round-trip with identical hashes; a 20 MB dll builds in 16 s and is read
     in 300 ms.
   - The largest real Model directory is 116 KB (CodeCoverage).
   - There is no CS8103, because attribute blobs live in #Blob, not #US.
   - Reflection reads are cached.
4. **Paths** are embedded relative, with `/`, through item metadata. No machine paths.
5. **Web SDK libraries** keep `Content Remove` (NETSDK1152).

The prototype is in the session scratchpad (`s1/`, driven by `run.sh`) and is not committed.

### S4 — repo-wide import of the Spark targets (2026-10-06, worktree, not committed)

**Verdict: PROVEN with amendments (D11 amended).**

**Change.** `Directory.Build.targets` imports `spark.props`, `spark-authorization.props`,
`spark-authorization.targets` and `spark.targets` under
`SparkApplication = OutputType=='Exe' And IsTestProject!='true'`, with the opt-out
`<SparkApplication>false</SparkApplication>`. A gating target, `_SparkGateAuthorizationTargets`
(AfterTargets=ResolveAssemblyReferences), sets `EnableSparkAuthSpa=false` unless
`@(ReferencePath)` contains `MintPlayer.Spark.Authorization`. The 18 hand `<Import>`s were removed
from CodeCoverage, DemoApp, Fleet, HR and QnA.

**Evidence** (`dotnet build MintPlayer.Spark.slnx`)

| Build | Errors | Notes |
|---|---|---|
| Baseline | 0 | 309 warnings |
| Naive guard | 1 | SchemaGenerator got SPARK001 and an `AGENTS.md`; DemoApp got SPARK030 and `spark-auth.setup.ts` |
| Fixed | 0 | 309 warnings, identical histogram by code; no new files in any project |

**Probing all 56 csproj files** (`-getProperty`/`-getItem`):
- `OutputType`/`IsTestProject` are empty at props time.
- In the targets file, all 5 test projects are `Exe` + `IsTestProject=true` (xunit 2.9.3 with the
  current Test.Sdk), so the guard holds.
- The apps' AdditionalFiles, ProjectReferences and Spark properties are identical to the
  hand-import baseline. DemoApp differs only in auth defaults, which are cancelled at execution.

**Stays per project:**
- the ProjectReference-adding targets, because restore does not see `IsTestProject`;
- `spark-testing.targets`.

**Unrelated finding:** a baseline build already modifies `package-lock.json` and generates the
gitignored `spark-auth.setup.ts` in four apps.

### S2 — `SparkLayers.Compose` against today's actions engine (2026-10-06, throw-away prototype)

**Verdict: PROVEN, with one ordering caveat.**
- **Golden test.** The core layer came from the `[assembly: SparkActions]` in QnA's bin, the only
  library actions layer (314 chars, `MintPlayer.Spark`), with `apps/QnA/QnA/App_Data/actions.json`
  on top. A dump of action, declaredBy, property=json @layer and the conflicts from both engines was
  **identical (1008 bytes)**.
- **Edge cases.** Five synthetic edge cases were run through both engines; four were identical:
  - case-insensitive names, keeping the first spelling;
  - a `null` remove followed by a re-add, which appends;
  - lib-vs-lib conflicts, with the app silent and `""` kept;
  - annotations, and an object-valued property.

  The fifth differed: a property reset with `null` and then set again keeps its old position in
  today's engine, because .NET `Dictionary` reuses the slot (`SparkActionLayers.cs:127,140`). The
  new engine appends it. The hash is unaffected, because `ConfigFileShape.DescribeActions` sorts.
  Decided: append (D2).
- **Model delta.** The library layer was QnA's `SparkUser.json`. The app delta changed `UserName`'s
  `showedOn` (the real QnA-vs-CodeCoverage drift) and added an attribute.
  - Every library field was kept. Provenance per leaf was correct: `attributes[UserName].showedOn`
    came from the app and `.isReadOnly` from the library.
  - An app changing `persistentObject.id` or an attribute `id` was refused with two errors.
  - A keyed-array element cannot be removed with `null`, so the prototype used `$remove: true`
    (D2).
- **Engine needs.** `KindSpec.IsAtomic(path)` was required, because an action's object-valued
  properties are replaced whole.
- **The drift list** (actions vs translations, plus the actions analyzer):
  1. Key case: actions are insensitive; translations are ordinal for both key and language.
  2. Library order: actions put core first, then case-insensitive alphabetical; translations are
     ordinal alphabetical.
  3. `null`: actions remove or reset; in translations it marks the leaf "mixed" and drops the
     subtree (SPARK_TRANS_002, silently for the host).
  4. `""`: a value in actions; in translations the app's is ignored and a library's is kept.
  5. Invalid JSON: the actions runtime throws; the generators and the analyzer skip silently, and
     the analyzer reads a scalar action as a removal.
  6. Duplicate names: the actions runtime throws; the analyzer and MiniJson keep the last one.
  7. Conflict equality: `JsonNode.DeepEquals`, sorted-key strings, or ordinal strings.
  8. Translations are a nested tree that is flattened per layer.
  9. The order of languages matters.
  10. A third engine exists: `LibraryActionsConflictAnalyzer`.

  All of these are settled in D2, D3, D14 and D15.
- Spike code and logs are in the session scratchpad (`spike/SparkLayers.cs`, `spike/Program.cs`).
  They are not committed.

### S3 — translations at runtime (2026-10-06, throw-away prototype)

**Verdict: PROVEN.**
- **Golden test.** Runtime composition read the `[SparkTranslations]` chunks by reflection, added
  the app's `translations.json` from disk, then flattened and merged. It matched the generated
  `SparkTranslationsRegistry.All` exactly, including language order: QnA 398 of 398 keys,
  CodeCoverage 586 of 586.
- **The host embeds its own chunk.** `LibraryTranslationsGenerator` is not gated to libraries, so
  this would duplicate the app layer. It has to be gated off (D10).
- **Compile-time consumers.**
  - `LibraryTranslationsGenerator` (SPARK_TRANS_001–004) is unaffected.
  - `HostTranslationsAggregatorGenerator` is removed. Its SPARK_TRANS_005 already reads
    referenced-assembly attributes plus AdditionalFiles, and moves to an analyzer.
  - No other generator or analyzer reads translations (`GenerateIndex`, `SparkModelSymbols`,
    `ModelNamesReader`, `ModelJsonReader` checked).
- **Startup cost** (Stopwatch; noisy machine; assemblies preloaded; discovery, parse, flatten and
  merge counted):

| App | Layers | Cold, 5 processes | Warm median | Warm min |
|---|---|---|---|---|
| QnA | 3 | 66–172 ms | 10.6–17.7 ms | 3.1 ms |
| CodeCoverage | 3 | 90–168 ms | 12–20 ms | 4.3 ms |

  The first prototype took 120 ms warm, because of O(n²) lookups and provenance scans. The shared
  engine therefore uses indexed lookups.

### S5 — callers of `GetPersistentObject` (2026-10-06, code reading)

**Verdict: D13 is feasible but sits at the wrong layer.** The three `IManager.GetPersistentObject`
overloads (`IManager.cs:23,30,38`) only delegate to `IEntityMapper` (`Manager.cs:21-27`). The real
construction paths are `IEntityMapper.GetPersistentObject` and `IEntityMapper.ToPersistentObject`.

App callers through `IManager`, none of which needs hidden data:

| file:line | kind | reads/writes attributes |
|---|---|---|
| apps/QnA/QnA/Interceptors/DeleteReasonInterceptor.cs:41 | retry-prompt virtual PO | own prompt only |
| apps/Fleet/Fleet/Actions/CarActions.cs:156 | `OnBeforeDelete` popup | writes `popup["LicensePlate"]` |
| apps/DemoApp/DemoApp/Actions/StartPageActions.cs:35 | virtual `OnLoad` | writes 4 attributes |
| apps/CodeCoverage/CodeCoverage/Actions/HomeActions.cs:38 | virtual `OnLoad` | writes `AccountCount`/`RepoCount`, which are denied to anonymous users; the anonymous branch returns first (:50) |
| apps/CodeCoverage/CodeCoverage/Actions/ForgeAccountsActions.cs:48 | virtual `OnLoad` | **:51 writes `obj["Provider"]`, which is Read-denied to authenticated users (security.json:218)** |

There is no pruning at construction today, so ForgeAccounts works now. Under D13 as written, the
indexer would throw `KeyNotFoundException` (`PersistentObject.cs:133-135`) for every signed-in user
on production. `AsSystem()` does not help, because the Development net throws on a system object
that reaches the response.

Framework paths through `IEntityMapper` that must see hidden data, and so need system construction:
- `SaveValidation.cs:92`: stored values for unwritable attributes.
- `SyncActionHandler.cs:130`: replication. Pruning there would drop synced fields (data loss).
- `EffectiveObjectFactory.cs:41`: the effective object for refresh and save.

Load hooks that read attributes, which fail closed or throw once those attributes are denied:
`QuestionTranslatorFormInterceptor.cs:26` (`AuthorId`), `RepositoryActions.cs:47` (`Gate`), and
`CarActions.cs:97-110` (`ShapeForStatus`, 5 attributes). Today the load hooks run on unpruned
objects, because `Get.cs:90` presents after the hooks.

These `PresentAsync` sites become redundant behind the boundary net, for static attribute rights:
Get.cs:90, New.cs:104/:198, Refresh.cs:162/:172, SaveResponsePresenter.cs:57/:62, ExecuteCustomAction.cs:343/:347,
RowSecurityGate.cs:226, RetryPresentation.cs:24, PersistentObjectPresenter.cs:58. The per-row
`RowSecurity.RedactAsync` calls (RowSecurityGate:218, RetryPresentation:40, Presenter:52/54,
DefaultPersistentObjectActions:172) stay.

Tests: 3 files call `IManager` and about 41 calls in 6 files call `IEntityMapper`.

### S7 — hidden attribute names reaching the client (2026-10-06, code reading)

**Verdict: today's data exposes no hidden name, but there are two structural leaks.**
- **The type breadcrumb template is not filtered.** `ForFormAsync` (AttributeRightsEnforcement.cs:142-172)
  removes Read-denied attributes, but `ShallowCopy` copies `Breadcrumb` verbatim
  (EntityTypeDefinition.cs:96). The client applies it itself (`as-detail-display-value.pipe.ts:27`).
  `RendererOptions` and `SparkSubQuery.ParentReference` aren't filtered either (minor).
- **Query `SortColumns` and `Columns` are not filtered.** `SparkQuery` (SparkQuery.cs:34, :80) is serialized raw by
  Queries Get.cs:53 and List.cs:30. Current CodeCoverage sorts are only on `Login`/`Name`.
- Filtered already: query rows (`ForQueryAsync`: QueryExecutor.cs:88, StreamingQueryExecutor.cs:70,
  SparkSelectionResolver.cs:97), PO JSON (`RetainAttributes`; `showedOn` sits on each attribute and goes
  with it), and the permissions endpoint (type-level booleans only).
- `BreadcrumbResolver`: a refused token renders **empty**, the same as an empty value (:440-443). The
  placeholder `"—"` (SparkOptions.cs:24) is only for a whole denied document (:237, :413).

### Amendments taken from S5/S7
1. D13 prunes inside `IEntityMapper` (`GetPersistentObject` and `ToPersistentObject`), and `IManager`
   inherits it.
2. On a constructed object, writing to a pruned attribute is a **silent no-op**, and hooks get
   `TryGet`. This keeps ForgeAccountsActions.cs:51 working when `Provider` is pruned.
3. Pruning takes the verb (Read+Edit, Read+New, read-only marking). Both construction and the net
   receive the verb context.
4. `AsSystem()` is required for SyncActionHandler, SaveValidation's stored object and the effective
   object. Hooks receive the **pruned** object, and the leak test covers that.
5. `ForFormAsync`/`ForQueryAsync` rewrite the `Breadcrumb` template, dropping refused tokens.
   `Queries/Get` and `Queries/List` filter `SortColumns`/`Columns` by attribute rights. Both go into
   the canary leak test.
6. Correction to D13a: refused tokens render empty, not as the placeholder.
7. The net replaces only static attribute presentation. Per-row redaction stays.

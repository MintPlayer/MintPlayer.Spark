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
- **Per-kind flags** are explicit rather than silently unified:
  - case sensitivity of keys
  - "`""` means untranslated" (translations)
  - whether `null` removal is allowed

### D3 — Per-kind rules (`KindSpec`)

| Kind | Element key | Notes |
|---|---|---|
| actions | action name (case-insensitive) | as today |
| translations | key (ordinal), then language | each layer is **flattened first**, so `"a.b"` and `{"a":{"b"}}` meet; `"ns": null` removes a whole prefix; a new language is appended (`GetValue` falls back to the first); `""` from the app ignored; moves from compile time to the same runtime engine (D10) |
| model | `persistentObject.name` | `attributes[]`, `tabs[]`, `groups[]`, `queries[]` by name. **`id` can never be overridden.** An app file for a library type is a delta |
| programUnits | unit id | |
| culture | (app only; libraries may not ship it) | |
| moderation | privilege name; reputation keys | `group` is a token or slot bound in `security.json`. The composed result is exposed as an `IConfiguration` source below appsettings and environment variables (Q6) |
| security | see D4 | |

### D4 — Library default rights (⚠️ grill Q2: reverses a deliberate design position)
- **Rights are a keyed set.** `rights` becomes an object keyed by a stable key: `"<alias>:<name>"`
  for library rights, the id for the app's own. The app removes a library grant with
  `"<key>": null`. It never edits a library right; it removes it and adds its own.
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
    marked inert, so the opt-out is visible too. To drop a single grant, `"<key>": null` still
    works.
  - **The gate is the review point.** `securityPosture.txt` becomes a readable, committed **text
    table of every effective right**, with the layer each right came from. It is not a bare hash.
    `--spark-verify-security` fails on drift, so adding a library, or a library update that changes
    rights, appears as a diff in that file in the same PR.
  - Grants to `@anonymous` get their own section in the posture file, and their own CI warning,
    so they cannot hide in a long diff.

### D5 — Ids for library-shipped model elements
Ids are deterministic UUIDv5 values over (library alias, type, member kind, member name). They
are **written into the shipped file** and **verified by the library's generator**, so a hand edit
or a rename is caught as a diagnostic. They are never computed silently at runtime, because a
rename would then change ids on the wire. The app layer cannot override `id`. Types the app owns
keep their minted ids.

### D6 — One composed-model provider; the synchronizer writes only the app's delta
- One `IModelSource` (composed layers + app files) replaces every `*.json` glob (§1.2) and the
  generator input.
- The synchronizer reads the composed model and writes back only the **app layer's delta**. It
  never writes library-owned types or fields.
- `TranslationsSeeder` and `SparkSchemaReference`, which also write into app files, follow the
  same rule.

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

### D10 — Translations join the runtime engine
- Translations stop being composed at compile time. The app layer is read from disk (and reloads)
  like every other kind.
- The `HostTranslationsAggregatorGenerator` registry is removed.
- The generators that need translations at compile time read the library attributes plus the
  app's AdditionalFiles, as the analyzers do for actions today. S3 found that only SPARK_TRANS_005
  needs this; it moves to an analyzer like `LibraryActionsConflictAnalyzer`.
- `LibraryTranslationsGenerator` is gated off for the host. Today it also embeds the host's own
  file, which would double the app layer at runtime (S3).
- The static `SparkTranslations.All` (read at `SparkText.cs:32`, `TranslationsLoader.cs:16`,
  `TranslationsSeeder.cs:77` and `ModelSynchronizer.cs:1300`, and filled today by a ModuleInitializer)
  becomes a startup-registered snapshot that is swapped atomically on reload.
- Cost (S3, measured): 3–20 ms warm and 66–172 ms cold for 3 layers and up to 586 keys. That is
  acceptable, provided the engine uses indexed lookups; a naive version took 120 ms warm.

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

## 5. Open decisions (grill)

- ~~**Q1** Transport~~ → **C** (decided).
- ~~**Q2** Library default rights~~ → **B**: adopted. The guard rails are grant-only, own
  resources only, group tokens and removable with `null`. There is no opt-in; the readable posture
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

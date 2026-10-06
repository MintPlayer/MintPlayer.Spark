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
  (composition). It does not copy them. Example: overriding only an action's icon
  (`apps/QnA/QnA/App_Data/actions.json:4`).
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
- **`null`** removes an element or resets a property to the layer below.
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
| translations | key, then language | `""` from the app ignored; moves from compile time to the same runtime engine (D10) |
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
  app's AdditionalFiles, as the analyzers do for actions today.

### D11 — DRY imports
- `spark.props`/`.targets` (and each library's targets) are imported **once** from
  `Directory.Build.props`/`.targets` for in-repo projects. The hand-written `<Import>`s in every
  app and test project are removed.
- Every target is guarded, because the files now reach every project, including netstandard2.0
  generators and tests.
- Application detection uses `'$(OutputType)'=='Exe' And '$(IsTestProject)'!='true'`, with an
  explicit opt-out property.

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
- **Construction.** `IManager` builds persistent objects pruned for the current caller. Pruning
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
  refuses already render as the redaction placeholder (`BreadcrumbResolver.cs:224-237, :413`,
  contributions M2c-2a).
  - The breadcrumb **template** sent with type metadata must not name a hidden attribute either.
    S7 checks this.
- **Leak test (constraint 2).** A test seeds canary values and canary attribute names on hidden
  attributes. It asserts that **no** serialized response contains either: PO get/new/refresh/save,
  query rows, breadcrumbs, type metadata, retry prompts and error messages.
- **Trap.** Hooks that read a hidden attribute now see it missing, and silently compute something
  wrong. S5 lists every `GetPersistentObject` caller and every attribute-reading hook before
  pruning is switched on. Each one that needs hidden data moves to `AsSystem()`.

### D16 — Library alias (grill Q7 = B)
- Every library that ships layers declares `<SparkLibraryAlias>authorization</SparkLibraryAlias>` in
  its csproj. It is **required**, with no default derived from the assembly name.
- The generator reads it through `CompilerVisibleProperty` and stamps it into the layer attribute.
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
- QnA's icon override (`actions.json:4`) still composes. Core New/Edit/Delete are shipped by the
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

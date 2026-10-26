# Spark Editor and storage-agnostic core — PRD and plan

Issue: [#62](https://github.com/MintPlayer/MintPlayer.Spark/issues/62) (VS + VS Code extension). Related:
[#536](https://github.com/MintPlayer/MintPlayer.Spark/issues/536) (SQL Server support, a later consumer of
the storage seam). Supersedes PR #63 (closed 2026-10-26; it predates the `apps/` + `libs/` layout).

Status: **planned**. Decisions grilled with the owner on 2026-10-26 (D1–D14 below). Nothing implemented.

## 1. Goal

A developer configures a Spark application's model — persistent objects, attributes, queries, custom actions,
program units, security groups and rights, languages and translations — from inside the IDE, through a UI
modelled on Vidyano's Management section, instead of hand-editing `App_Data/**/*.json`.

- The IDE extension starts **one** editor web server per IDE instance (`dotnet MintPlayer.Spark.Editor.dll`),
  on a random free port bound to `127.0.0.1`, and reads the port back from its stdout.
- Each Spark application in the solution/workspace gets an "Open in Spark Editor" context-menu item; it opens a
  tab whose webview points at `http://127.0.0.1:{port}/#app={id}&token={token}`.
- The editor **is a Spark application**: its actions classes read and write the target application's JSON files,
  not a RavenDB database. That requires Spark's core to become storage-agnostic first.
- It works in this repository too, using the shipped extension's embedded DLL (no local build needed).

## 2. Two pull requests, one plan

| PR | Content | Depends on |
|---|---|---|
| **PR A — storage-agnostic core** | provider seam under `DatabaseAccess`/`QueryExecutor`, `MintPlayer.Spark.RavenDB` (today's Raven code, extracted), `MintPlayer.Spark.InMemory`, capability checks, provider conformance suite, every app on `UseRavenDb()` except DemoApp on `UseInMemory()` | — |
| **PR B — Spark Editor** | `MintPlayer.Spark.Editor` app + meta-model storage provider, Synchronize "never remove" + `[IgnoreProperty]` analyzer, ng-spark request-credential hook, VS Code extension, Visual Studio extension, versioning engine `vsix` kind + publish jobs | PR A merged |

PR A closes [#551](https://github.com/MintPlayer/MintPlayer.Spark/issues/551) (storage-agnostic core); PR B closes #62. No further splitting (D4).

## 3. Decisions

Each decision states its evidence: a file:line, a measurement, or "owner preference".

### Hosts and process model

- **D1 — VS Code and Visual Studio, both in PR B.** Owner preference (Q1 = B). The editor stays runnable
  standalone (`dotnet …Editor.dll`, opens nothing itself) as a debugging aid and for Playwright tests, not as a
  product. Rider is out of scope.
- **D10 — One server per IDE instance, apps selected by URL fragment.** Owner preference. Feasible because the
  editor's own Spark model is the *meta-model* (EntityType, Attribute, Query, Group, Right, ProgramUnit, …), which
  is the same for every target. `IModelSource` stays the singleton it is. The target application is only data.
  - The extension starts the server lazily on the first "Open in Spark Editor" call.
  - It passes over **stdin** (never argv) one JSON message: the token, its own PID, and the target registry
    `[{ id, name, appDataPath, binPath }]`. Registry updates (project added or removed) follow as further stdin
    lines.
  - The browser can only name an **app id**. The server refuses ids that aren't in the registry, so no path ever
    comes from the browser.
  - A scoped `IEditorTarget` is resolved per request from `X-Spark-Editor-App`. Every cross-request cache
    (composed model, library layers, schema revision, file watcher) is **keyed by target**. A target's cache is
    created on first use and released when idle.
  - Lifetime: the server stops on stdin EOF **and** when the parent PID exits. The VS extension also puts the
    process in a Job Object (pattern from PR #63's `SparkEditor.Vsix`).
- **Readiness line.** After `app.StartAsync()`, the server reads the real port from
  `IServerAddressesFeature`. It then prints `SPARK_EDITOR_READY {"port":N}` and flushes. The token is never
  printed or logged.

### Security of the localhost server

- **D9 — Per-session token, no cookies, `127.0.0.1` only.** Owner preference (Q9 = A).
  - Spark's cookie antiforgery (PR #451) is expected to break in a VS Code webview. The app runs as a cross-site
    iframe under `vscode-webview://`, so `SameSite=Lax` cookies aren't sent. That expectation is unverified, so
    Spike S5 tests it.
  - The extension generates a 128-bit token. ng-spark reads `#token=…&app=…` once, keeps both in memory, and
    sends them as headers (`X-Spark-Editor-Token`, `X-Spark-Editor-App`) on every call and in the WebSocket's
    first message. The comparison is constant-time.
  - The server checks `Host` (`127.0.0.1:{port}` or `localhost:{port}`, against DNS rebinding) and `Origin` on
    state-changing requests and on the WS upgrade. There is no CORS.
  - The server binds to `127.0.0.1` explicitly, not `localhost`. Kestrel refuses dynamic port 0 on `localhost`
    because that name means both IPv4 and IPv6 loopback, and `localhost` may resolve to `::1` first. PR #63 used
    `localhost:0`. S1 confirms the exact Kestrel behaviour.
  - The editor runs **without** `MintPlayer.Spark.Authorization`. Its own `security.json` grants the single local
    session everything.
  - Writes go only to canonicalised paths under the target's `appDataPath`.
  - ng-spark gets a pluggable request-credential hook so it doesn't assume the antiforgery cookie flow.

### What the editor shows and writes

- **D5 — Composed view, origin markers, library items frozen.** Owner preference.
  - The editor shows every PO, attribute, query, group, right, program unit and translation, composed across all
    library layers (`SparkModelFiles.Compose`, `libs/spark/MintPlayer.Spark.Abstractions/SparkModelFiles.cs`).
  - Each item carries an origin marker: a library assembly, or the application. The `Provenance` map already
    exists for `--spark-describe`.
  - Library items are **read-only**. The developer can still add **their own** attributes, queries and so on,
    including on a library PO such as `SparkUser`. Those additions are written to the app's delta file, and only
    there.
- **D6 — "Library" keeps its existing definition.** Owner preference: no new MSBuild flag.
  `MintPlayer.Spark.SourceGenerators.targets:19-35` embeds a project's model as a library layer when it isn't an
  `Exe`/`WinExe` and isn't a test project, unless `<SparkLibraryLayers>false</SparkLibraryLayers>` is set. The
  editor reads the target's library layers from its built `binPath` with `MetadataLoadContext`, reading the
  embedded resources without executing target code.
  - If the target isn't built, the editor shows the app layer only, with the banner "build the project to see
    library types".
  - A library project's own `App_Data` opened directly is read-only.
- **D7 — Lossless editing and schema-revision gating.** Owner preference (Q7 = A).
  - All writes go through `JsonNode` and never round-trip through a C# model, so unknown properties survive.
    Existing key and item order is kept and new items are appended (PR #63's approach), which keeps git diffs
    minimal.
  - The editor ships every `schemas/v*` revision and compares it with the target's `$schema`. A newer target gets
    a banner ("some properties aren't editable; update the extension"). An older target is protected: a field its
    revision lacks is never written.
  - Writes are atomic: write a temp file in the same directory, then rename. PR #63 used plain `WriteAllText`.
- **D8 — Virtual POs and attributes are first-class; Synchronize never removes.** Owner preference.
  - A PO or attribute needs no C# class or property behind it. `IManager.Retry.Action(...)` types are an example.
    The editor marks POs and attributes that have no CLR counterpart as **Virtual** and styles them distinctly.
  - `--spark-synchronize-model` only adds and modifies. It already carries over attributes without a CLR property
    (#253, `ModelSynchronizer.cs:1143-1153`). Two changes:
    - The stale-projection deletion is removed (`ModelSynchronizer.cs:378-383`).
    - An attribute whose property is now `[IgnoreProperty]` stays in place instead of being dropped (today:
      `ModelSynchronizer.cs:1152`).
  - A new analyzer warns when an `[IgnoreProperty]` property still has an attribute in the model JSON. It reads
    the model through `AdditionalFiles`.
  - Management shows a **Synchronize** button, and both extensions offer a Synchronize command. Either one runs
    `dotnet run --project <target> -- --spark-synchronize-model` in an IDE terminal, and the file watcher
    refreshes the open tabs.
- **External edits.** One recursive `FileSystemWatcher` per target, with a 300 ms debounce and suppression of the
  editor's own writes, pushes changes over a WebSocket (PR #63's design). A form with unsaved changes whose file
  changed underneath shows a "changed on disk — reload / overwrite" banner. Otherwise the view refreshes silently.
- **Scope of files.** Every hand-edited file that has a published schema: `Model/*.json`, `security.json`,
  `programUnits.json`, `translations.json`, `culture.json`, `actions.json` (`schemas/*.schema.json`). Generated
  files (`modelHashes.json`, `securityPosture.txt`, `oidc-signing-key.json`) are never written. `boards.json`
  follows if its schema exists by the time PR B starts. Users aren't editable: they live in the target's database,
  which the editor never opens.
- **UX reference: Vidyano Management.** It has Persistent Objects, Attributes, Queries, Custom Actions, Program
  Units, Security (Groups, Rights), Culture (Languages, Messages) and Settings (recorded 2026-03-28). S7 walks it
  in detail.

### Storage-agnostic core (PR A)

- **D2/D3 — A provider seam *below* the policy pipeline.** Owner preference (Q2 = A, Q3 = B).
  - `DatabaseAccess` (1186 lines) is the PersistentObject policy pipeline, not a data layer. Its dependencies
    (`DatabaseAccess.cs:21-38`) are permissions, row security, collection guard, interceptors, disabled actions,
    attribute write shield, save validation, breadcrumbs and the reference resolver. It also holds natural-id
    collision handling and index waits.
  - Raven appears only in `session`/`documentStore`, `LoadEntityAsync` (:1056), `CommitAsync` (:976),
    `WriteSaveAsync`/`WriteDeleteAsync` (:771/:841), `EvictTrackedAsync` (:936) and `WaitForIndexesAsync`
    (:1042).
  - `DatabaseAccess` stays the orchestrator and keeps every check. A provider supplies only these operations:
    - load an entity
    - stage a write or delete
    - commit
    - run a query (D12)
    - optionally wait for consistency
  - So a new provider can't bypass authorization.
  - Today's blockers that disappear: `SparkMiddleware.cs:192` always registers `IDocumentStore`; saving a type
    with no `clrType` throws (`DatabaseAccess.cs:228-229`); deleting one is refused (`DatabaseAccess.cs:585-587`).
- **D12 — The query contract is a provider-neutral spec, and typed actions keep their native queryable.** Owner
  preference (Q12 = C).
  - Core builds a spec (S3, §4b: LINQ expression trees plus provider hooks, superseding "typed filter tree"): source, filters, search text, sort, page, projection columns, row-security
    predicate. Each provider translates it: RQL for Raven, LINQ-to-objects for InMemory, SQL later.
  - *Refined by D15 (§4a): these become `IQueryable<T>` extensions in the provider package (EF Core pattern).* Raven-specific overloads (`.Search()`, `.Customize()`, `.Include()`, named indexes, `.ProjectInto<>()`) stay
    reachable through `RavenPersistentObjectActions<T>` with `IAsyncDocumentSession`/`IRavenQueryable`, so app
    code isn't rewritten.
  - PR #63's `ISparkSession` with a plain `IQueryable` made these unreachable or forced casts back to Raven types,
    which leaks the abstraction. Not repeated.
  - **Row security and filters are applied by core, never left to the provider.** A provider conformance suite
    must pass for every provider. It covers a row-security leak test, absent-field `== false`/`== null` cases,
    DateTimeOffset, paging and sorting.
- **D13 — Provider packages, provider-specific registration, capabilities checked at startup.** Owner proposal,
  refined.
  - Packages: `MintPlayer.Spark.RavenDB` (PR A) and `MintPlayer.Spark.SqlServer` (#536). They have the same
    neutral surface: registration options, the query spec, `IDatabaseAccess` and the PO-level actions hooks. Each
    keeps its own typed escape hatch.
  - Method names are provider-specific (`spark.UseRavenDb(…)`, `spark.UseInMemory(…)`, later
    `spark.UseSqlServer(…)`), as in EF Core. A shared name would be an ambiguous call in any project that imports
    both, and the call site would no longer say which store is in use.
  - Each provider declares **capabilities** (`Subscriptions`, `Revisions`, `FullTextSearch`, `Attachments`, …),
    and each feature library declares what it requires. `AddSpark` fails fast at startup with a clear message on a
    mismatch, for example "Spark.Messaging needs Subscriptions; the SqlServer provider doesn't offer it".
    `NotSupportedException` remains a last line of defence inside the provider.
  - **Core-pipeline pieces** become provider-neutral in PR A. Candidates are model hash verification,
    translations, antiforgery storage, settings and reference resolution; S2 confirms the list.
  - **Feature libraries** stay Raven-only and declare it: messaging, subscription workers, history,
    contributions, webhooks, identity provider, cron. Authorization's **user store** is Raven-bound, but
    `security.json` enforcement (`IPermissionService`) is core and must stay provider-neutral.
- **D14 — `MintPlayer.Spark.InMemory` proves the seam, and DemoApp runs on it.** Owner preference.
  - A real published provider: dictionary storage, LINQ-to-objects on the spec, and basic capabilities only. It
    runs the conformance suite next to Raven.
  - DemoApp (core only, `AddSpark`, `apps/DemoApp/DemoApp/Program.cs:12`) switches to `UseInMemory()` and ships
    seed data. It logs and shows "in-memory: data resets on restart".
  - The guides keep a Raven section that points at HR or Fleet for indexes.
  - The editor's meta-model provider is **not** this package. It maps POs onto specific files and lives in PR B.

### Distribution and versioning

- **Embedded DLL.**
  - The VS Code `.vsix` bundles a framework-dependent publish of the editor under `server/`. PR #63's CI never
    ran `dotnet publish` into `server/`, so its packaged extension couldn't find the editor.
  - The VSIX bundles the same output. PR #63's VSIX looked for hard-coded `C:\Repos\…` paths and forced
    `ASPNETCORE_ENVIRONMENT=Development`.
  - Production mode serves the prebuilt Angular `wwwroot`. It never uses `UseAngularCliServer`.
  - A `sparkEditor.serverPath` setting overrides the DLL, for developing the editor itself. Using the editor in
    this repository needs no override: the shipped DLL plus the D7 "target is newer" banner is enough.
- **Runtime.**
  - VS Code depends on `ms-dotnettools.vscode-dotnet-runtime`. It prefers an installed runtime
    (`dotnet.findPath`) and otherwise acquires the ASP.NET Core 11 runtime.
  - Visual Studio checks `dotnet --list-runtimes` and links to the installer.
  - S4 verifies that a .NET 11 **preview** can be acquired.
- **D11 — The versioning engine gains a `vsix` package kind.** Owner preference (Q11 = A).
  - The major is locked to the .NET major of the embedded editor (`11.x`). Tags are `<id>/v<version>`, like every
    other package.
  - "Changed" covers the extension's sources plus the editor's dependency graph.
  - The engine stamps `package.json` and `source.extension.vsixmanifest`, with fixtures in the engine's tests.
  - This is a change to `apps/CodeCoverage/versioning-action`, which is production.
  - Publishing to the VS Code Marketplace reuses the approach already used for
    `mintplayer-ng-bootstrap/libs/mintplayer-ng-bootstrap-snippets`. Publishing also covers Open VSX, and the VS
    Marketplace through VsixPublisher.
  - The VS Marketplace rejects re-publishing a version, so the job skips duplicates the way NuGet's
    `--skip-duplicate` does.

### Carried over from PR #63 vs dropped

- **Kept as ideas, rewritten on master:**
  - order-preserving `JsonNode` writes
  - the watcher, debounce, self-write suppression and WebSocket push (`SparkDataRefreshService` in ng-spark)
  - the spawn and webview patterns
  - the VS Job Object
- **Dropped:**
  - PR #63's storage abstraction: master's actions API differs completely (`IPersistentObjectActions.cs`)
  - its `Schemas/` folder and `publish-schemas.yml`: `apps/SparkSchemas`, `tools/SchemaGenerator` and the
    `schemas/v{n}` releases cover them
  - picking a port by binding and releasing it (racy)
  - a webview with no CSP
  - parse errors swallowed with `Console.WriteLine`
  - the unrelated demo and renderer changes

## 4. Spikes (before or at the start of the PR they gate)

| # | Question | Gates | Done when |
|---|---|---|---|
| S1 | Kestrel: is `127.0.0.1:0` bound reliably, and is the real port read after start (`IServerAddressesFeature`)? Confirm that `localhost:0` is refused. Does the stdin-EOF plus parent-PID shutdown work on Windows and Linux? | PR B | a 30-line console host that prints the ready line and exits within 1 s when its parent is killed |
| S2 | Inventory every `IDocumentStore` / `IAsyncDocumentSession` / `IRavenQueryable` consumer outside the future Raven package. Classify each as core-pipeline or feature, with file:line. | PR A | a table in this PRD with an owner-agreed core/feature column |
| S3 | `QueryExecutor` (2811 lines): which parts build the spec (core) and which generate RQL (provider)? Draft the spec type, and check it against one #431 sub-query filter, one `.Search()` and one row-security filter. | PR A | a spec draft, plus the three cases expressed in it |
| S4 | VS Code: a `WebviewPanel` iframe to `127.0.0.1`, with a CSP `frame-src`, `asExternalUri` and `portMapping`. Does a WebSocket work inside it, in local and Remote-WSL? Can `vscode-dotnet-runtime` acquire the ASP.NET Core 11 preview? | PR B | a throwaway extension showing a live WS echo page in both modes |
| S5 | Are cookies (antiforgery) really dropped in the webview iframe? Does the fragment token survive a reload of the webview? | PR B (D9) | observed behaviour recorded here |
| S6 | Delta writing: given a composed library PO plus the app's additions, produce the minimal app delta file (attributes keyed by name, reorder, removing an app-added attribute). Is the delta byte-stable through a compose → write round trip? | PR B (D5) | a round-trip test on HR's `SparkUser` delta |
| S7 | Walk Vidyano Management (the owner's local Fleet app) and record its screens, forms and navigation as the UX reference. Credentials stay out of the repo. | PR B UI | screen inventory in this PRD |
| S8 | VS extension: a Community.VisualStudio.Toolkit tool window or document window with WebView2 pointed at the server, and a Solution Explorer context-menu item per Spark application project. | PR B | a throwaway VSIX opening a tab per project |

### 4a. S2 result — RavenDB consumers (2026-10-26, owner signed off)

Method: `grep -rE "IDocumentStore|IAsyncDocumentSession|IRavenQueryable|IDocumentSession|Raven\.Client"` over
`libs/**/*.cs`. 56 files in `libs/spark` (53 in `MintPlayer.Spark`, 3 in Abstractions). Line numbers are at master
`479c4feb`.

**Findings that change the plan:**

- **F1 — The app's data model is declared Raven-typed.** Every `SparkContext` declares `IRavenQueryable<T>`
  properties (`apps/DemoApp/DemoApp/DemoSparkContext.cs:9-11`), and `SparkContext.Session` is an
  `IAsyncDocumentSession` (`SparkContext.cs:7`). `ModelSynchronizer.cs:75,490,1593`, `ModelShapeDiscovery.cs:95`,
  `ValueObjectKeyVerifier.cs:168` and `QueryExecutor.cs:904-911` find collections by `IRavenQueryable<>`.
  - **D15 — Collections are plain `IQueryable<T>`. Provider packages add EF-Core-style operators.** The owner's
    first suggestion was a Spark-owned type that provider packages extend. They then agreed to this counter-proposal
    ("yes", 2026-10-26).
    - **Why not a wrapper type (`ISparkQueryable<T>`).** Standard LINQ operators return `IQueryable<T>`, so
      `Cars.Where(…).Search(…)` loses the wrapper type. Keeping it would mean re-declaring every LINQ operator, as
      RavenDB does for `IRavenQueryable<T>`, and any LINQ helper from outside Spark would still drop it. Its
      compile-time check is also weak: an operator exists wherever its package is *referenced*, not where that
      provider is *registered*.
    - Contexts declare `public IQueryable<Car> Cars => Set<Car>();`, and `Custom.*` query methods return
      `IQueryable<T>`. The 32 files returning `IRavenQueryable<T>` (F2) keep compiling, since it is an
      `IQueryable<T>`. Core's collection discovery keys on `IQueryable<>`: `ModelSynchronizer.cs:75,490,1593`,
      `ModelShapeDiscovery.cs:95`, `ValueObjectKeyVerifier.cs:168`, `QueryExecutor.cs:904-911`.
    - **Why standard LINQ needs nothing.** `Queryable.Where` only builds an expression tree and calls
      `source.Provider.CreateQuery`. The *runtime* object of a Raven-backed `IQueryable<T>` is RavenDB's query
      inspector, so RavenDB's provider translates the tree to RQL and returns another RavenDB query. That is why a
      later `q is IRavenQueryable<T>` check still holds after any `Where`, `OrderBy` or `Select`.
    - **Rule 1 — never re-declare a standard operator.** A `Where<T>(this IQueryable<T>, Expression<…>)` in a
      provider package has `System.Linq.Queryable.Where`'s exact signature, which is CS0121 (ambiguous call) in
      every file importing both. Provider packages ship only operators that LINQ lacks: `.Search(…)`,
      `.Include(…)`, `.Customize(…)`, `.UseIndex<TIndex>()`, and SQL equivalents later.
    - **Rule 2 — delegate to the store's own method; never put our own method into the tree.** Each operator
      checks `q is IRavenQueryable<T>` (or the EF equivalent) and calls RavenDB's own method, so RavenDB's own
      method info is what ends up in the tree. `Expression.Call(ourMethod, …)` would reach RavenDB's translator as
      an unknown method, a `NotSupportedException` at execution. On a foreign provider the operator throws
      `NotSupportedException` naming the operator, the package and the actual provider, as EF's `Include` does.
    - **Rule 3 — core executes only through the provider hooks** (§4b `CountAsync` / `ToListAsync`), never through
      a neutral `ToListAsync` on `IQueryable<T>`. EF Core already extends `IQueryable<T>` with that name, so a
      second one would be ambiguous. Whether the async session refuses synchronous `.ToArray()` is unverified;
      check it in milestone 2.
    - Trap accepted: using a Raven operator on an in-memory query fails when the query runs, not at build time. The
      clear exception and the conformance suite cover it.
    - `SparkContext.Session` moves to a `RavenSparkContext` base in the Raven package. Raw session work (patches,
      operations, attachments) stays in `RavenPersistentObjectActions<T>`.
- **F2 — 32 files outside core return `IRavenQueryable<T>`**, including DemoApp's custom queries over index
  projections (`apps/DemoApp/DemoApp/Actions/CarActions.cs:39`, `PersonActions.cs:62`). Moving DemoApp to InMemory
  (D14) means rewriting those two queries as LINQ over `Set<T>()`, with no index.
- **F3 — "Open a fresh session" is a real operation, not an accident.** Concurrency re-checks and out-of-unit reads
  open their own session, for example:
  - `DatabaseAccess.cs:345,507,623`
  - `SyncActionHandler.cs:49`
  - `LookupReferenceService.cs:132-227`
  - `ProgramUnits/Get.cs:82`
  - `SparkShareLinks.cs:86,178,261`

  The contract needs `OpenUnit()` beside the request-scoped unit.
- **F4 — The change vector is the ETag.** `GetChangeVectorFor` sets `PersistentObject.Etag` and is the
  optimistic-concurrency token (`DatabaseAccess.cs:435,523,785,826,869,876`; `RowChangeVectors.cs`), and
  `ConcurrencyException` is caught at `:418,540,755` and `WriteUnit.cs:94`.
  - The contract gets an opaque version token plus a neutral `SparkConcurrencyException`.
  - `SparkCommittedInterceptors.PreviousChangeVector` (Abstractions `:76`) keeps its name, documented as opaque.
- **F5 — Interceptors are already neutral at the boundary.** `SparkInterceptorContext.Session` is `object`, cast
  back to Raven in `SparkInterceptorContextExtensions.cs:9-12`. `GetSession()` moves to the Raven package.
- **F6 — Sixteen libraries have no Raven usage at all:** approvals, challenges, client, commerce, controllers,
  fulfilment, gs1, printing, refdata, sms, socket_extensions, waiting_room, warehouse, webpush, all_features (plus
  Abstractions' own policy types). They already go through `IDatabaseAccess`, so they become provider-neutral for
  free.

**Core files.** Each row says where its Raven use ends up: **P** moves to `MintPlayer.Spark.RavenDB`, **C** stays in
core and takes a neutral contract type instead of the session, **Cap** moves to the provider behind a capability.

| File:line | Raven use | Class |
|---|---|---|
| `SparkMiddleware.cs:192-235` | registers `IDocumentStore`, `IAsyncDocumentSession`, `IDocumentSession` | P (`UseRavenDb`) |
| `SparkMiddleware.cs:564, 614, 681, 903-997` | connection wait, index scan and deploy | P |
| `SparkDocumentStoreConventions.cs`, `Configuration/SparkRavenDbSecurity.cs` | store conventions, certificates | P |
| `Indexes/SparkIndexCreationTask.cs`, `RavenIndexHierarchy.cs`, `IndexCatalog.cs:22` | index types | P (core keeps the index *name* on the model) |
| `SparkIndexWait.cs:64-95` | waits for non-stale indexes | P (the contract's optional consistency wait) |
| `Extensions/SessionExtensions.cs`, `SessionWriteSnapshot.cs`, `RowChangeVectors.cs` | session internals, change vectors | P |
| `Extensions/SparkDevelopmentExtensions.cs:656` | index-definition dump | P |
| `ValueObjectKeyVerifier.cs:81-168` | RQL check of value-object keys | P |
| `ModelSynchronizer.cs:1712` | reads index fields for projections | P (an index-shape hook); `:75,490,1593` become C per F1 |
| `RavenDataProtectionKeyRepository.cs`, `SparkDataProtection.cs:103` | data-protection key ring in Raven | P; core falls back to ASP.NET's default file ring |
| `Expiration/ExpirationStamper.cs`, `ExpirationStartup.cs:68` | `@expires` metadata | Cap `Expiration` |
| `Live/LiveHub.cs:32,48,253`, `LiveActionsSource.cs:38-98`, `LiveConnection.cs:44,62`, `LiveStartup.cs:30` | Changes API feed | Cap `ChangeNotifications` (InMemory can offer it: it sees every write) |
| `Live/LiveAppStreamSubscription.cs:40,92,156` | streaming session | Cap `ChangeNotifications` |
| `Services/DatabaseAccess.cs:21-22, 1056-1063, 771-879, 976` | load, store, delete, commit | C — the provider seam (D2/D3) |
| `Actions/DefaultPersistentObjectActions.cs:109,199,246,269` | session for load and etag | C — `RavenPersistentObjectActions<T>` keeps the session |
| `EntityMapper.cs:114,591,698,775,1071-1081` | reference loads | C (batch load by ids) |
| `ReferenceResolver.cs:103-109` | `.Include()` | C — include paths become a spec hint the provider may ignore |
| `Breadcrumb/BreadcrumbResolver.cs:91,112,319,676-694` | batch loads | C |
| `RowSecurity.cs:260,345,504,619-631`, `RowSecurityGate.cs:24`, `SparkRowRule.cs:28,72` | batch loads, async materialize | C |
| `CollectionGuard.cs:29-36` | collection from metadata | C — the provider answers "collection of this entity" |
| `SparkContext.cs:7`, `SparkContextResolver.cs:8-16` | context session | C per F1 |
| `SparkInterceptorPipeline.cs:35,38,126,153`, `SparkInterceptorContextExtensions.cs:9` | session parameter | C per F5 |
| `DisabledActionsEvaluator.cs:77`, `PersistentObjectPresenter.cs:35`, `CustomActionExecution.cs:101`, `RefreshInvoker.cs:75`, `SparkSelectionResolver.cs:51`, `RetryPresentation.cs:36` | injected session | C |
| `SyncActionHandler.cs:24,49,53,277`, `LookupReferenceService.cs:25,132-227`, `ProgramUnits/Get.cs:21,82`, `Sharing/SparkShareLinks.cs:63-272` | fresh sessions, collection names | C per F3 |
| `Endpoints/PersistentObject/DeleteMany.cs:52`, `Refresh.cs:54` | injected session | C |
| `QueryExecutor.cs` (29 hits), `QueryResultProjector.cs:173` | queries, etags | C + provider, see S3 |
| `Queries/LiveQueryArgs.cs:45` | **public** `Session` property | C — moves to a Raven subtype; public API break (minor) |
| Abstractions `SparkQuery.cs:21,59`, `SparkIndexValue.cs` | doc comments only | C |

**Libraries.**

| Library | Files | Class |
|---|---|---|
| identity_provider | 70 | Feature, Raven-only |
| messaging, subscription_worker | 18, 2 | Feature, needs `Subscriptions` |
| moderation | 15 | Feature, Raven-only |
| authorization | 9: `UserStore*`, `RoleStore`, `AgentGrantStore`, `SparkUserBackfill`, `SparkExternalLoginLinker`, two migrations | Feature (user store). `IPermissionService` and `security.json` enforcement have no Raven use and stay core |
| source_generators | 13 | Generated code names Raven types. The emitted `IRavenQueryable` and session code follows F1 |
| testing | 9 | `SparkTestDriver` stays Raven. The conformance suite adds a neutral base |
| organizations, replication | 8, 8 | Feature, Raven-only |
| contributions, history | 6, 4 | Feature, needs `Revisions` |
| dashboards | 5 (`RavenStatisticsProvider`, revisions config) | Feature, Raven-only |
| settings | 4 | **Provider-neutral in PR A** (owner, sign-off 3). Needs from the contract: load by id, load by id prefix (`SparkSettings.cs:71` `LoadStartingWithAsync`), create-only store (`SparkQuotas.cs:67`, empty change vector), optimistic concurrency on a fresh unit (`SettingsSeeder.cs:62`, `SparkQuotas.cs:51`). `SettingActions.cs:19` returns `IQueryable<Setting>` (D15). SQL Server stores these as a JSON document column (#536) |
| notifications, soft_delete | 5, 4 | Feature, Raven-only; soft_delete needs `Revisions` |
| cron, migrations | 2, 2 | Feature, Raven-only |
| attributes, boards, mail, mcp | 1 each | Feature, Raven-only (each one injects the store directly) |

**Owner sign-off (2026-10-26):**

1. F1: first a Spark-owned type, then the EF Core pattern on plain `IQueryable<T>` (counter-proposal accepted). Recorded as D15 above.
2. Data protection on InMemory uses ASP.NET's default file key ring: "y".
3. Settings may not be Raven-only ("rather not — in SQL Server probably store as JSON string"). It becomes
   provider-neutral in PR A; see the settings row above.

The rest of the classification stands as proposed.

### 4b. S3 result — the query spec (2026-10-26, draft)

**How `QueryExecutor` is built today.** Both sources (`ExecuteDatabaseQueryAsync` `:819`, `ExecuteCustomQueryAsync`
`:1156`) run the same pipeline over an untyped `object queryable`:

1. Acquire the source.
   - Database: a context property (`:898`), re-rooted onto the bound index (`:983-990`, Raven).
   - Custom: the actions method's return value.
2. Project into the index's view (`:993`, `ApplyProjection` `:1864`, Raven `ProjectInto`).
3. Includes (`:999-1003`, Raven).
4. Row-security pushdown (`:1008`, `ComposeRowFilterAsync`). This is an `Expression<Func<T,bool>>` from the actions
   class: neutral LINQ.
5. Column filters (#431) (`:1019`, `ApplyColumnFilters` `:2313`, built by `AnyEquals` / `AnyContains` `:2418/:2458`).
   These are `Expression` trees: neutral LINQ.
6. Search (`:1029`, `ApplySearch` `:2193`). This is Raven `.Search()`, restricted to the index's fields. Where it
   isn't applied, the in-memory fallback `NarrowBySearch` (`:2011`, `:307`, `:414`) runs instead: neutral.
7. Restrict to ids (`:1040`, `RestrictToIds` `:618`, LINQ), then sort (`:1047`, `ApplySorting` `:1911`, LINQ).
8. Paging pushdown, only when nothing can remove rows afterwards (`:1074-1100`), then count and execute
   (`CountQueryableAsync` `:2611`, `ExecuteQueryableAsync` `:2719`, Raven async).
9. The row-security gate on materialized rows (`:1112`, `RowSecurityGate`), with dedupe by id for fan-out indexes.
   This is neutral and is **the enforcement point**. Pushdown is only an optimization.

**Conclusion:** the spec should be **LINQ expression trees plus a few provider hooks**, not a hand-made filter tree.
This replaces D12's "typed filter tree": the expressions already exist and are portable to Raven's LINQ provider,
LINQ-to-objects and EF. Core keeps steps 4, 5, 7, the paging decision and 9. The provider owns steps 1–3, 6 and 8.

```csharp
public interface ISparkQueryProvider
{
    // Step 1 — Database.* source: the root queryable for a collection, optionally bound to a named index
    // (null when the provider has no indexes). Custom.* sources skip it: actions return their own queryable.
    IQueryable OpenSource(Type entityType, string? indexName, out Type resultType);

    // Steps 2-3, 6: optional; each reports whether it applied, and core falls back where it did not.
    IQueryable ApplyIncludes(IQueryable source, IReadOnlyList<string> paths);          // no-op allowed
    (IQueryable Query, bool Applied) ApplySearch(IQueryable source, SparkSearchSpec search);
    IReadOnlySet<string>? IndexedFields(IQueryable source);                            // null = unrestricted
    bool MayFanOut(IQueryable source);                                                 // today: "indexType is not null"

    // Step 8: async execution.
    Task<int> CountAsync(IQueryable source, CancellationToken ct);
    Task<IReadOnlyList<object>> ToListAsync(IQueryable source, CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>> VersionTokensAsync(IEnumerable<string> ids, CancellationToken ct); // F4
}

public sealed record SparkSearchSpec(string Term, IReadOnlyList<PropertyInfo> Properties);
```

The core side is a record the executor builds once and then applies. Each piece is
`LambdaExpression`/`Expression<Func<T,bool>>`:

```csharp
internal sealed record SparkQuerySpec(
    Type EntityType, Type ResultType, Type SortType, string? IndexName,
    LambdaExpression? RowFilter, bool RowFilterIsComplete,       // RowFilterComposition today
    IReadOnlyList<LambdaExpression> ColumnFilters,               // #431
    SparkSearchSpec? Search,
    IReadOnlyCollection<string>? RestrictToIds,
    IReadOnlyList<SortColumn> Sort,
    int Skip, int Take);
```

**The three check cases, expressed in it:**

1. **A #431 sub-query filter** (a `Custom.*` sub-query, the column filter `Status ∈ {Open, Closed}`).
   - The actions method returns `IQueryable<T>` (native on Raven) scoped to the parent.
   - `ColumnFilters = [x => x.Status == "Open" || x.Status == "Closed"]`, the expression `AnyEquals` already builds.
   - Core applies it with `Queryable.Where`. Raven translates it into RQL `in`/`or`; InMemory evaluates it. No
     provider hook is involved.
2. **A `.Search()`.**
   - `Search = new("foo bar", [Name, Code])`, intersected with `IndexedFields`.
   - Raven's `ApplySearch` returns `Applied: true` and emits consecutive `.Search()` calls after the row filter, so
     `(predicate) and (search or search)` holds (the `:1022-1025` constraint stays inside the Raven provider).
   - InMemory returns `Applied: false`, so core runs `NarrowBySearch` and the paging decision drops to in-memory
     paging. This is today's fallback path, now chosen by the provider rather than by the bound index.
3. **A row-security filter** (`GetFilterExpressionAsync` → `x => x.OwnerId == currentUser`, plus an
   `IsAllowedAsync` override).
   - `RowFilter = that lambda` and `RowFilterIsComplete = false` (a per-row refinement exists).
   - Core applies `Where(RowFilter)` for pushdown, and the gate (step 9) runs `IsAllowedAsync` on every
     materialized row regardless of provider.
   - `RowFilterIsComplete = false` forbids database paging.
   - The conformance leak test asserts that a provider which ignores the pushed `Where` still returns no
     foreign rows.

**With D15:**
- `OpenSource` returns an `IQueryable` whose runtime type is the provider's own (no wrapper).
- Raven-only operators become `IQueryable<T>` extensions in `MintPlayer.Spark.RavenDB`, rather than requiring
  raw session code: `.Customize()`, `ProjectInto<>` with an explicit view, named index queries in custom sources,
  and `.Include()` beyond the model's reference paths. Only raw session work (patches, operations, attachments)
  still needs `RavenPersistentObjectActions<T>`.

**Risk:**
- `RestrictToIds` and `ApplySorting` call `Queryable.*` through reflection over `object`. They already work on
  LINQ-to-objects (custom sources can return in-memory sequences, `AsQueryable` at `:2637`), so InMemory inherits
  a tested path.
- The untested part is EF (#536): client-evaluated expressions such as `AnyContains` over a collection property
  may not translate. That is deferred to #536's own spike.

## 5. Milestones

Tests run once at the end of each PR, not per milestone.

### PR A — storage-agnostic core

1. S2 + S3 complete, with tables written into this PRD.
2. The provider contract in core (load, stage, commit, query spec, consistency wait, capabilities), with
   `DatabaseAccess` and `QueryExecutor` rewired onto it. Behaviour must stay unchanged for Raven.
3. `MintPlayer.Spark.RavenDB` extracted, with `UseRavenDb()` and `RavenPersistentObjectActions<T>`. Every app
   and library is moved onto it, and feature libraries declare their capabilities.
4. Startup capability check plus an analyzer for a feature package referenced without a capable provider.
5. `MintPlayer.Spark.InMemory`, the provider conformance suite (run by both providers), and DemoApp on
   `UseInMemory()` with seed data. Guides updated.
6. Save and delete for types with no `clrType` go through `{Name}Actions` on every provider (removes
   `DatabaseAccess.cs:228`/`:585`).

### PR B — Spark Editor

1. Synchronize never removes (D8), plus the `[IgnoreProperty]` analyzer.
2. The `MintPlayer.Spark.Editor` host: ready line, stdin registry, token, Host/Origin checks, lifetime (S1).
3. The meta-model provider: composed read with `MetadataLoadContext` layers, origin markers, Virtual marking,
   lossless atomic `JsonNode` writes, delta writes (S6), schema-revision gating.
4. File watcher plus WebSocket push, and the conflict banner.
5. The Management UI in ng-spark (S7), plus the request-credential hook and the Synchronize button.
6. The VS Code extension (S4/S5): discovery, context menu, lazy server, webview with CSP, runtime acquisition,
   `serverPath` setting.
7. The Visual Studio extension (S8): the same, with WebView2 and a Job Object.
8. The versioning engine `vsix` kind plus the publish jobs, with the CI job count kept at 20 or fewer.

## 6. Acceptance

- DemoApp starts and serves CRUD with **no** RavenDB server.
- Every existing suite stays green on Raven, and the conformance suite passes for both Raven and InMemory.
- From VS Code and from Visual Studio, on HR and Fleet at the same time, one server and two tabs show no
  cross-talk (D10).
- In the editor:
  - Adding an attribute to `SparkUser` writes only HR's delta, and the library attributes are disabled.
  - A virtual PO can be created, edited and deleted.
  - An external text edit appears in the open tab.
  - `git diff` after an edit touches only the changed lines.
- A request without the token, or with a foreign `Host`/`Origin`, is refused.
- The extensions are versioned and published from tags by the master workflow.

## 7. Out of scope

- The SQL Server provider itself: #536, built on PR A's seam.
- Porting feature libraries (messaging, identity provider, …) to non-Raven providers: these depend on #536 and
  its capabilities.
- Rider and other IDEs.
- Editing users, or any other database-resident data, from the editor.
- Generating C# from the editor. The model JSON is the editor's whole world, and C# stays authoritative only
  through Synchronize.

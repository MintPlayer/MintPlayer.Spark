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
  - Core builds a spec: source, typed filter tree, search text, sort, page, projection columns, row-security
    predicate. Each provider translates it: RQL for Raven, LINQ-to-objects for InMemory, SQL later.
  - Raven-specific overloads (`.Search()`, `.Customize()`, `.Include()`, named indexes, `.ProjectInto<>()`) stay
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

# Plan: Spark templates, `dnx create-spark-app`, terminal UI framework

**PRD:** [templates_dnx_terminal_PRD.md](templates_dnx_terminal_PRD.md). Decisions D1–D19 were locked with the
owner on 2026-11-16.

**Status:** S1, S2 and S6 (shape) done; M1 done, with all 16 libraries asked for: 7 joined once #689 made them packable (2026-11-17). The `spark-app` first cut builds in four variants and
creates cleanly from the CLI and from VS 2026 (flat layout under either "same directory" choice; VS's own solution
format). M2 done (2026-11-17): a generated app writes its model on the first Development run and starts. This was
checked on InMemory without sign-in and on RavenDB with local accounts, and three startup refusals in the template
were fixed along the way. M3 done (2026-11-17): the five item templates build, synchronize and verify in a generated RavenDB app. M4 done (2026-11-17): `node tools/template-matrix.mjs`, local only (owner), 32 variants green in 629 s. S3 and M5 done (2026-11-17): `spark` and `create-spark-app` run through `dnx` from a local feed, `new` and every `add` item verified end to end. M6 done (2026-11-18): the template references every package it pins, so it re-releases with them, and it releases in lockstep with both tools. The first `/release` proves it. Next: the final test run, then the PR is ready. The eight #689 libraries ship on the next `/release`. Each spike has a time box, questions, an exit criterion and an answer slot. Record each
answer with evidence: numbers, file:line, commands run.

**Order in this repo:** S1 → S2 → S6 → M1–M4 (templates and the core option), then S3 → M5–M6 (tools and release).
The framework track (S4, S5) runs in MintPlayer.Dotnet.Tools under Tools #18 (D6). This repo only files and
updates issues there.

All of this repo's work goes in **one PR** (owner rule), including the `MintPlayer.Spark` change for D13.

---

## Spikes (this repo)

### S1: Template skeleton and IDE behaviour (1 day)
- Run `dotnet new templatepack`. Build a `spark-app` with two features (one with a prerequisite, one RavenDB-only),
  the store and auth choices, and a plain CLI ClientApp.
- **VS 2026:**
  - Does `ide.host.json` hide and label symbols?
  - Does `isEnabled` gray out a choice? The owner saw it ignored around 2023.
  - Does a run-script post-action run?
- **CLI:** can a template **refuse** a symbol combination with a message, not just ignore it? Try constraints, a
  computed symbol, and an error post-action.
- **Rider and C# Dev Kit:** does the template show, and which symbols?
- **Exit:**
  - a behaviour table per host
  - D16's final form: `isEnabled`, or refusal at Create
  - D13's post-action viability
- **Answer (CLI half, 2026-11-17):** recorded with the test template in `C:\Repos\TemplateTest`, on SDK 11.0.100,
  using a throwaway hive.
  - **`isEnabled` works on the CLI, but it drops a disabled value instead of refusing it.** `--Notifications` with
    Store None generated Notifications off, with exit 0 and no message. D16's "refuse on the CLI" therefore needs
    something other than `isEnabled`. Options: the `dnx` tool validates first, or a computed `InvalidCombo` emits an
    `#error`.
  - **`isRequired` with a condition works.** `--Auth oidc` without `--OidcAuthority` returned "Missing mandatory
    option(s)", exit 102.
  - **Computed symbols auto-include prerequisites.** `MessagingEffective = (Messaging || Notifications)` generated
    `Messaging.cs`.
  - ⚠️ **A disabled parameter is null, not false.** An exclude condition of `(!Notifications)` did NOT exclude the
    file. The S6 generator must route every condition through a computed symbol (`X && true`), which treats null as
    false.
  - **Visual Studio half (owner, latest VS 2026, 2026-11-17):**
    - ✅ **`isEnabled` works.** Switching Store to RavenDB makes the Notifications checkbox appear, and switching away
      makes it disappear. VS **hides** a disabled option rather than graying it out. This supersedes the owner's
      ~2023 observation that `isEnabled` did not work.
    - ✅ **`ide.host.json` `isVisible: false` hides an option** (test 5).
    - ❌ **VS does not enforce a conditional `isRequired`.** The OIDC authority field appears for oidc, but a blank
      value is accepted (test 4). Required values must therefore have a usable default or placeholder, and the app
      must report a missing value at startup.
    - ⚠️ **A symbol named `Auth` appeared twice.** VS showed its own "Authentication type" dropdown (with only
      "None") next to ours. This looks like a clash with VS's built-in auth option, inferred and not verified. The
      symbol was renamed to `AuthMode`; ~~retest pending~~ retested, see below. **The manifest generator must avoid VS's reserved symbol
      names** (`auth`, `Framework`, `langVersion`, …), each to be confirmed.
    - ✅ after the fix: **blank solution** (test 3). The template had no `primaryOutputs`, and VS adds only the listed
      projects to the solution. `spark-app` must list every project (host, Library, Tests) in `primaryOutputs`.
      Retest (Project21): the project was in the solution, and `Messaging.cs` was generated because Notifications
      pulled it in. The authority was blank (`''`), which confirms VS doesn't enforce `isRequired`.
    - The second Authentication dropdown was not reported after the rename to `AuthMode`. The clash is taken as
      fixed, so the generator must avoid symbol ids that VS treats as its own.
    - **The solution file belongs to the host (owner, 2026-11-17, `C:\Repos\Project23`).** VS 2026 writes its own
      solution around `primaryOutputs`, in the format its "Default solution file format" option names (`.sln` on the
      owner's machine). The template's `SparkApp.slnx` did not appear in the output. **Decision:** the template ships
      `SparkApp.slnx` only when `HostIdentifier` (a `bind` symbol) is `dotnetcli` or `dotnetcli-preview`. The
      `dotnet new` CLI creates no solution, so it needs ours (`.slnx` is also what `dotnet new sln` makes from .NET
      10). VS, Rider and C# Dev Kit each write their own solution in the format the user chose. Verified on the CLI:
      `CliCheck.slnx` was emitted with both projects. VS retest with the owner's `.sln` setting: `C:\Repos\Project24`
      got a `.sln` and no `.slnx`.
    - **Flat layout under either VS checkbox (owner, 2026-11-17).** VS always generates into a fresh folder (maintainer,
      dotnet/templating#5022). With "Place solution and project in the same directory" unchecked, VS puts its `.sln`
      in `<Location>\<Solution>\` and runs the template in `<Location>\<Solution>\<Name>\`, so everything sat one level
      below the `.sln` (`C:\Repos\Project24`). `preferNameDirectory` is not the cause: it applies only when no output
      folder is given, and VS always gives one. **Decision:** bind `context:CreateSolutionDirectory`. When VS reports
      it as `True`, a second `sources` entry writes with `"target": "../"` and the `primaryOutputs` move to `../`.
      Measured with a diagnostic file (removed again): VS 2026 binds `HostIdentifier = vs`, `IsExclusive = True`,
      `SolutionName` = the name, and `CreateSolutionDirectory` = `False` checked (`C:\Repos\Project25`) / `True`
      unchecked (`C:\Repos\Project26`). Both came out flat: `.sln`, `.gitignore`, `Directory.Packages.props` and both
      project folders side by side, and the `.sln` points at both projects. The CLI layout is unchanged.
  - **S1 DONE.**

### S2: Versions in template content and a same-commit feed (½ day)
- How do the template's `PackageReference`s and the ng-spark range get the versions released in the same run?
  Options:
  - a pack-time token substitution from `MintPlayer.Versioning.MSBuild`
  - a generated `Directory.Packages.props`
  - a floating `11.*` range
- How does the CI matrix pack every package from the current commit into a local feed, and point the generated
  apps at it?
- **Exit:** a local pack whose `content/` shows the real versions, plus a recipe for the local feed.
- **Answer (2026-11-17): a generated `Directory.Packages.props`, written at pack time from the repo's own versioning.**
  - **No version is written in content/.** The template's projects use central package management, and
    `content/SparkApp/Directory.Packages.props` is not in source. The `SparkTemplatePackageVersions` target in
    `libs/templates/MintPlayer.Spark.Templates/MintPlayer.Spark.Templates.csproj` calls
    `MintPlayerVersioning;_GetProjectVersion` on each `SparkTemplatePackage` project. That is the same pair NuGet
    uses to stamp ProjectReference dependency versions (`_GetProjectVersion`,
    `sdk/11.0.100/NuGet.Build.Tasks.Pack.targets:355`). The target writes one `PackageVersion` per package into
    `obj/<cfg>/template-generated/` and packs that file as `content/SparkApp/Directory.Packages.props`.
  - **ng-spark** gets `^<version>` from `libs/node_packages/ng-spark/package.json` (the last released version, stale
    by design). It replaces `NG_SPARK_VERSION` in a packed copy of `ClientApp/package.json`. Measured: `^22.44.0`.
  - **Measured:** `dotnet pack … -c Release -p:Version=11.0.0` pinned all 17 Spark packages at 11.0.0. The
    template then generated and built four variants (InMemory without a client; the RavenDB defaults; everything
    on with oidc; InMemory with RavenDB-only flags, which were dropped). The builds ran against nuget.org with exit
    0, warnings NU1507 (this machine's several sources) and ASP0014 only.
  - **Same-commit feed recipe (M4):** `dotnet pack MintPlayer.Spark.slnx -c Release -p:Version=<v> -o feed`. The
    explicit version flows into every project (#544 D1), so the template pins `<v>` for every Spark package. Then
    `dotnet new install feed/MintPlayer.Spark.Templates.<v>.nupkg` and a `nuget.config` with `feed` ahead of
    nuget.org. The ng-spark half still resolves from npm; a client build in the matrix needs `npm pack` of the
    local ng-spark (M4).
  - **Guards:**
    - **SPARKTPL001** errors when a pinned package resolves to `0.0.0-PLACEHOLDER` (never tagged, so never
      published). A Debug pack, which reads no tags, only gets a warning.
    - The step runs only when the template itself is packable. On master no package is tagged, so the template is
      not packed, and its RavenDB, InMemory and Logging pins (tagged only on the release line) cannot fail the
      build. Simulated as a solution pack on master: exit 0, no package.
  - ⚠️ **Found:** `MintPlayer.Spark.Minification` and `MintPlayer.Spark.SitemapXml` have **no tag at all**
    (`git tag -l`), so they were never released. They are left out of the template until they ship. Recorded on
    #689.
  - **Two content fixes found by building:**
    - `spark.AddActions()` exists only when the app has Actions classes (it is generated), so the template doesn't
      call it.
    - With `--no-client`, NodeServices (through MintPlayer.Spark) ran `npm install` in a missing `ClientApp\`. The
      host now sets `EnableSpaBuilder=false`.

### S6: Manifest generator and matrix cost (1 day)
- Draft the `features.json` schema and a generator for symbols, computed prerequisites, `ide.host.json` and `#if`
  regions, covering three real features.
- Prototype the graph test (`requires` equals ProjectReferences) and the coverage test (every packable `libs/*` is
  listed or excluded).
- Time about 32 instantiations (minimal, all-on, each feature alone) with a shared restore and parallel builds.
- **Exit:** the schema, timings, and whether the matrix fits one PR job or needs to be a subset on PRs and the full
  matrix on master.
- **Answer (2026-11-17, partly):**
  - **Shape (owner):** generate the data, guard the code; pack time only, no generated file in git.
    - `libs/templates/MintPlayer.Spark.Templates/features.json`: `core` (always pinned), `features` and `excluded`
      (with a reason: `dependency`, `not-an-app-feature`, `unreleased`; `pending` was retired with the M1 remainder). Per feature: `id`, `flag`,
      `project` (relative to `libs/`; its file name is the package id), `displayName`, `description`, `default`,
      `store`, `needsClient`, `requires`, `registration`, `client`, `appsettings`. A feature with `symbol` is chosen
      by a hand-written symbol (Authorization: `HasAuth`, from the AuthMode choice), so no parameter is generated.
    - `libs/templates/MintPlayer.Spark.Templates.Build`: the `GenerateSparkTemplate` task (run through
      `TaskHostFactory`, so nothing locks its dll) writes `template.json`, `ide.host.json`, `dotnetcli.host.json`
      and the host csproj into `obj/template-generated/` and returns the projects to pin. The csproj carries one
      marker line, `<!-- spark-features: PackageReferences -->`. The generator refuses a manifest with a cycle, a
      prerequisite bound to a store or the client its dependent isn't, or a symbol the hand-written template.json
      already defines (error SPARKTPL002).
    - `Use{X}` = X's constraints && (X chosen || any transitive dependent chosen under its own constraints), e.g.
      `UseMessaging = (IsRaven && ((Messaging && true) || (IsRaven && (Mail && true))))`.
    - `tests/MintPlayer.Spark.Templates.Tests` (17 tests): coverage (every packable csproj under `libs/` is listed),
      graph (`requires` equals the features among the project's direct ProjectReferences), the generator, and the
      content guard (registration call, ng-spark providers and appsettings section inside their condition's block;
      every `#if` names a defined symbol).
  - **Found by the graph test:** Authorization references Migrations. Its package ships two migrations
    (`M_202610041800`, `M_202610051200`) that only the app's `AddMigrations()` runs, so `--auth` with
    `--migrations false` silently skipped them. Authorization now requires Migrations, and `UseMigrations` includes
    `HasAuth`.
  - **Not yet covered:** `needsClient` is generated but no feature uses it, so it is unverified in VS and the CLI.
    The 16 `pending` libraries become features in the M1 remainder.
  - **Matrix timing (measured 2026-11-17, this machine, packages from nuget.org's 11.0.0, restore cache warm):**
    `dotnet new` then `dotnet build` per variant. Minimal (`--no-client`): 11 s. With the client: 135–177 s each
    (RavenDefault 177, InMemoryIgnored 153, Everything 136, AuthNoMigrations 135). The build runs `npm install` in
    each ClientApp (286 MB `node_modules`), and that is nearly all of it.
    - **About 32 variants that way would take over an hour, so not in a PR job.**
    - **Proposal for M4 (an estimate, not measured):**
      - Run `npm ci` once into a shared folder and link it into each variant. Build every variant's .NET side
        (~11–15 s each, about 8 min serial, ~3 min at `--parallel=4`).
      - Run `ng build` once per distinct client. Only `HasAuth`, `UseSoftDelete` and `UseHistory` change the client
        today, which gives 8 combinations at most.
      - Measure this in M4 before choosing between a PR subset and the full matrix on master.
  - **Checked end to end:** packed, installed and built five variants, all exit 0. The CLI help lists the generated
    flags. `--auth authorization --migrations false` now references Migrations and ships the sample migration.

### S3: Tool packages and `dnx` (½ day)
- Pack `MintPlayer.Spark.Cli` and `create-spark-app` from one shared commands library. Run both with
  `dnx --add-source <local>` on a cold cache. Record the time and the confirmation prompt, with and without `-y`.
- Compare the in-process template engine (`Microsoft.TemplateEngine.Edge`) with running `dotnet new install` and
  then `dotnet new spark-app`.
- Check that the versioning engine, pack and publish handle tool packages; these are the repo's first.
- **Exit:** the instantiation approach, timings, and the pipeline changes needed.
- **Answer (2026-11-17):**
  - **Instantiation: shell out to `dotnet new`, not the in-process engine.** The template already works through the
    CLI (M1–M4), and the CLI path is what users and the matrix run, so the tool adds no second code path. The tool reads
    the installed version from `dotnet new uninstall` (lists packages with a `Version:` line; ~0.7 s) and, when it
    differs from its own, runs `dotnet new install MintPlayer.Spark.Templates::<own version> --force` (`--force`
    because reinstalling the same package otherwise exits 106). A development build (version `0.0.0-…`) uses whatever
    is installed, or a nupkg passed as the hidden `--template`. `Microsoft.TemplateEngine.Edge` was not adopted: it
    would add a second instantiation path and pin the tool to an engine version the SDK does not.
  - **dnx works on a local feed:** `dotnet tool exec <id>@<v> --configfile <nuget.config> -- <args>` (the smoke used
    a config whose packageSourceMapping sends `MintPlayer.Spark*` and `create-spark-app` to the feed). No
    confirmation prompt appeared with redirected stdin. Measured on this machine (scratchpad smoke, cold tool cache,
    template hive isolated through `DOTNET_CLI_HOME`):

    | Step | Time |
    |---|---|
    | Pack template + every pinned package into the feed | 98.5 s |
    | Pack `MintPlayer.Spark.Cli` / `create-spark-app` | 10.4 s / 7.9 s |
    | `dnx create-spark-app Mini --store InMemory --features none --no-client --yes` (tool fetch, template install, generate, restore, synchronize) | 24.3 s |
    | `dnx MintPlayer.Spark.Cli -- new Shop --yes --skip-npm` (RavenDB, defaults, synchronize) | 21.4 s |
    | `spark add entity` / `actions` / `custom-action` (no sync) | 1.9 s / 2.0 s / 1.9 s |
    | `spark add index` (with synchronize) | 12.6 s |
    | `--spark-verify-model` on the result | 7.8 s, in sync |
  - **Pipeline:** no change. Tool packages are ordinary nupkgs; the versioning engine takes their major from
    `net11.0` and their label from their dependencies (all stable), so both start at `11.0.0` on the release line,
    and the publish pushes them like any other. Both csprojs carry `0.0.0-PLACEHOLDER` and `SparkApplication=false`
    (Directory.Build.targets treats every Exe as a Spark host and demanded the SourceGenerators reference, SPARK001).
    What M6 must still do is pack the template on the release line (M3 finding), with the tools beside it. (Done in
    M6: the release job already packs only on a release line; M6 added the lockstep group.)

## Spikes (MintPlayer.Dotnet.Tools, via Tools #18)
- **S4: RazorConsole fit (2 days).** Find out whether overlays, a modal, absolute positioning for a draggable
  window, scroll containers and mouse drag can be done without forking the renderer. Also test conhost, Windows
  Terminal and Linux, and a headless test with `RazorConsole.Testing`.
- **S5: Command bridge (2 days).** Pair an `ICliCommand` with a Razor form. It runs headless with flags, opens the
  form on a TTY for missing values, and fails on `--no-interactive`, `CI=true` or a redirected stdin. Also design
  localized command and option text in CliGenerator.

---

## Milestones (this repo)

### M1: `MintPlayer.Spark.Templates` and the manifest
- Create `libs/templates/MintPlayer.Spark.Templates/`. Model the csproj on
  `libs/sitemap/MintPlayer.Spark.SitemapXml/MintPlayer.Spark.SitemapXml.csproj` (`0.0.0-PLACEHOLDER`, README, MIT,
  net11.0) and add `PackageType=Template`.
- Add `features.json` covering every feature lib, with the generator from S6. Add the graph and coverage tests.
- Add the `spark-app` choices from PRD §4.1 and the prerequisite and constraint behaviour from S1 (D16).
- Avoid folder names that `.gitignore` swallows (`coverage`, `Releases`), and check `git status` lists every new
  file.
- **Progress (2026-11-17):** the package is created and in the slnx. `.nxignore` excludes
  `libs/templates/*/content/`, because `@nx/dotnet` would otherwise infer the template's csproj files as workspace
  projects.
  - **`spark-app` (hand-written for now; the S6 generator replaces the symbol block):**
    - Choices: store (RavenDB default, InMemory); `--auth none|authorization|oidc` with `--oidc-authority`;
      `--no-client`.
    - Any store: `--controllers`, `--logging`.
    - RavenDB only: `--migrations` (default on, with a sample seed migration), `--messaging`, `--mail` (pulls in
      messaging), `--cron` (with a sample job), `--history`, `--soft-delete`, `--mcp`.
    - Every condition goes through a computed `Use*`/`Has*` symbol (S1). RavenDB-only features use
      `isEnabled: Store == "RavenDB"`, and their computed symbol also ANDs `IsRaven`, so the CLI drops them as well.
    - `dotnetcli.host.json` gives kebab-case flags.
  - **For S6:** every RavenDB-only feature already declares it with `[SparkRequiresStore(..., Provider = RavenDb)]`
    on its registration method, 21 sites including Authorization. The manifest generator can read the store
    requirement from there instead of restating it.
  - **M1 remainder (2026-11-17).** The owner asked for all 16 `pending` libraries: the clear-cut ones plus
    Replication, GitHub webhooks, OpenSearch and GS1.
    - **10 became features:** WebPush, Contributions, Dashboards, IdentityProvider, Moderation, Replication,
      Settings, GitHubWebhooks, GitHubDevTunnel and Gs1.
    - **6 are `unreleased`:** none of them is on nuget.org, so a generated app could not restore them.
      - Search, Search.OpenSearch and OpenSearchDescription were never tagged.
      - Boards, Notifications and Organizations have only preview tags, never pushed. The nuget.org flat
        container returns no index for them (#689).
      - Their wiring was written, then taken out again, because every `#if` must name a defined symbol. They
        join once published.
    - **Authorization can now be switched on by a dependent.** Dashboards, Settings, IdentityProvider and
      Moderation reference it.
      - A feature may have `selectedWhen` (Authorization:
        `AuthMode == "authorization" || AuthMode == "oidc"`). The generator then emits no parameter, but still
        generates its condition (`HasAuth`) as selected || dependents.
      - The hand-written `HasAuth` is gone. Proven by the `SettingsAlone` variant: `--settings` with the default
        `--auth none` builds with Authorization referenced.
    - **`alsoRequires`:** some features need another feature at runtime without referencing its package.
      Replication resolves `IMessageBus`; GitHub webhooks deliver to recipients through the bus. Both reference
      only Messaging.Abstractions. They `alsoRequire` Messaging. The graph test keeps `requires` exact.
    - **Pins:** the template now also pins the `dependency` packages. The Library csproj references
      Moderation.Abstractions for the moderatable sample Person.
    - **`App_Data/security.json`** now ships when `HasAuth` is on. Before this, the auth variants had none, and a
      missing file refuses startup.
      - It declares Administrators and binds every chosen library's slots (an unbound slot refuses startup).
      - With Moderation it adds its five groups, plus Vote/Flag/Downvote grants on Person, because the startup
        check refuses a privilege group with no grant (`ModerationStartupCheck.cs:118`).
      - The loader is strict JSON (`SparkJson.cs:228`). Each conditional binding therefore ends with a comma,
        and a `_comment` annotation (`IsAnnotation`, `SparkLayers.cs:368`) closes the object.
    - **Samples:** `AppSettings.cs` (Settings refuses a type with no `SparkSetting`) and
      `Webhooks/GitHubEventsRecipient.cs`. `appsettings.json` gets `GitHub` and `Spark:Replication` placeholders.
      The client gets `withDashboards`/`withModeration`/`withContributions` and their renderers, plus the
      identity-provider routes.
    - **Fixed: the template's own version leaked into its pins.** The `MSBuild` task passes global properties
      through, so `dotnet pack -p:Version=11.0.0` pinned every package at 11.0.0. The S6 runs passed only because
      every package there had an 11.0.0. Here, preview-only Webhooks.GitHub failed restore with NU1102. Fixed with
      `RemoveProperties`; it now pins `11.0.0-preview.112`.
    - **Verified:**
      - 19 template tests pass.
      - Packed and built four variants, all exit 0:
        - AllServer (every new feature, `--auth oidc`, no client): 19 s.
        - SettingsAlone: 6 s.
        - WebhooksAlone: 6 s.
        - AllClient (every new feature, `--auth authorization`, with the client): 130 s.
      - The generated `appsettings.json` and `security.json` parse.
      - The 9 ng-spark entry points used exist in the pinned 22.44.0 (`npm view … exports`).
    - **Not verified:** that these variants *start*. Starting needs the model JSON (M2 synchronizes it on the
      first run), so the security grants on Person are checked at startup only after M2.
  - **#689 fixed, and the 6 joined (2026-11-17, owner: "make the necessary changes to let the missing packages be
    published too").**
    - **Cause of #689:** eight libraries are on `Microsoft.NET.Sdk.Web` with a `PackageId` but no `IsPackable`:
      Boards, Notifications, Organizations, Search, Search.OpenSearch, OpenSearchDescription, Minification,
      SitemapXml. The Web SDK defaults `IsPackable` to false, and the engine follows it
      (`DotnetProjectReader.IsPackable`), so they never entered the graph. That is why the 11.0.0 release named
      none of them. Every released Web-SDK library sets `IsPackable` explicitly (Authorization, Messaging, …).
      - **Fix:** `<IsPackable>true</IsPackable>` in the eight. Packed locally in Release, all eight hold only `lib/`
        and the README (no appsettings or launchSettings).
      - **The silence:** the reader now warns when a Web-SDK project declares a `PackageId` and leaves
        `IsPackable` to the default. The warning goes to the graph's warnings, which the run's check shows. Red→green:
        `ProjectGraphTests.A_web_sdk_project_that_names_a_package_but_never_packs_is_reported`. Over the repository it
        flags exactly the eight.
    - **7 features added:** Boards, Notifications, Organizations, Search (`--site-search`), OpenSearch,
      OpenSearchDescription and Sitemap.
      - Minification stays out, with a new reason, `needs-prerendering`: it refuses startup without
        `UseSparkPrerendering()`, and the template is not server-rendered.
      - `unreleased` is now unused; `pending` is gone from the test's reason list.
    - **`--search` is `dotnet new`'s own legacy option.** `dotnet new spark-app --search` ran `dotnet new search`,
      a NuGet template search. The generator now refuses a flag that `dotnet new` owns. Red→green:
      `A_flag_dotnet_new_already_owns_is_refused`.
    - **The ng-spark floor comes from tags.** `package.json` said 22.44.0, which has no `./search` entry point
      (checked with `npm view … exports`; 22.49.0 and 22.53.0 have it). The template now pins
      ^<highest stable `@mintplayer/ng-spark/v*` tag reachable from HEAD> (^22.49.0 on this branch). It falls back to
      `package.json` with SPARKTPL002 only in a clone without tags.
    - **Wiring:**
      - Notifications and Organizations bind their `:users` slot to "Signed-in users", as SeoTester does.
      - `App_Data/boards.json` ships a `home` board with two tiles, the `people` query and a count of it. The client
        shows it at `/board`.
      - The shell's top bar gets the notification bell and the organization switcher.
      - `/search` is the results page; `withOrganizationRoutes()` serves the invitation page.
      - With OpenSearch, `AddSearch` uses the engine when `Search:OpenSearch:Uri` is set.
    - **Verified:**
      - 20 template tests pass.
      - Three variants generated and built, all exit 0, restored from nuget.org plus a local feed of the eight
        (Release packs, so the versions the template pins):
        - NewServer (all seven, no client): 11 s.
        - SearchAlone: 5 s.
        - NewClient (all seven, `--auth authorization`, with the client): 124 s.
      - Their appsettings.json, security.json and boards.json parse.
    - **Pins on this branch are not the release's.** This branch predates 11.0.0, so HEAD reaches no
      `MintPlayer.Spark.RavenDB` tag and the pin is 0.0.0-untagged (NU1603 locally). Boards and its siblings pin their
      preview tags. On the release line the engine tags the eight first (each csproj changed here), then the pack
      reads them.
  - **Open:**
    - a Tests project in `primaryOutputs`
    - ~~an `App_Data` first run (M2)~~ done in M2

### M2: `SynchronizeOnStartupIfMissing` (core, D13)
- Add a new option in `MintPlayer.Spark`. In Development it runs the model and security synchronizers when
  `App_Data` files are missing, and never overwrites. Write the test red first.
- If the option adds an endpoint or route, update `CsrfSurfaceTests` and the route snapshot. It shouldn't, but
  check.
- **Done (2026-11-17).** `spark.SynchronizeModelIfMissing()` sets `SparkOptions.SynchronizeModelIfMissing`.
  `SynchronizeSparkModelsIfRequested` (both overloads) runs the regular synchronize when no flag was passed, the
  environment is Development, and `App_Data/Model` holds no `*.json`. It returns false, so the host goes on to
  start. The design and what "missing" means are in PRD §4.4.
  - Red → green: `SynchronizeModelIfMissingTests`. The first-run test was red (1 failed, 3 passed) and is
    green after the change (4/4).
  - No endpoint and no route: `CsrfSurfaceTests` and the route snapshot are unaffected.
  - The template's `Program.cs` calls `spark.SynchronizeModelIfMissing()` after `UseContext`.
  - Release order: the template needs a `MintPlayer.Spark` that has the option. Both ship in the same
    `/release`, and the template pins Spark from its tags.
  - **Found by the first start: no variant without sign-in could ever start.** `security.json` shipped only
    with `HasAuth`, and core Spark refuses startup without it (`SecurityConfigurationException`, "Spark
    requires a security configuration file"). This hit every InMemory variant and RavenDB with `--auth none`.
    The file now always ships. Without sign-in, Person's right goes to `@anonymous`, as in DemoApp, because
    nobody is ever authenticated there.
  - **The second start found the next refusal, in every variant.** A right granted to a well-known group
    (`@anonymous` or `@authenticated`) needs the type to declare its row policy
    (`RowPolicyDeclarationValidator`, run by `VerifyRowPolicyDeclarations` in `SparkMiddleware.cs`). The template
    had no `PersonActions`. It now ships `SparkApp/Actions/PersonActions.cs`, which implements
    `ISparkOwnsRowSecurity` with a rationale per sign-in mode and says to scope the rows before storing anything
    personal. This is DemoApp's pattern (`AddressActions`).
  - **The third refusal, in the sign-in variants only:** the template maps registration
    (`LocalCredentials = Full`), and startup refuses that without a mail transport
    (`LocalCredentialEndpointFilter.GuardAgainstRegistrationWithoutMail`). In `features.json`, Authorization now
    `alsoRequires` Mail, which brings Messaging. In Development, Mail writes confirmation links to `.eml` files.
    The alternatives were rejected. `SignInOnly` leaves a new app with no way to create its first account.
    `AllowUnconfirmedRegistration` would switch off a guard that exists for production.

### M3: Item templates
- Add `spark-entity`, `spark-actions`, `spark-custom-action`, `spark-lookup` and `spark-index`.
- **Spike (2026-11-17), measured with a throwaway item template on SDK 11.0.100:**
  - The "add JSON property" post-action (`695A3659-…`) does not fit. Source:
    `dotnet/sdk` `src/Cli/Microsoft.TemplateEngine.Cli/PostActionProcessors/AddJsonPropertyPostActionProcessor.cs`.
    - It searches from the output directory, recursively, and climbs one parent level by default. From an
      app's subfolder, a recursive search also matches `bin/` copies.
    - It rewrites the whole file through `JsonNode` with `WriteIndented`, which drops comments and reflows
      formatting.
    - It can only add a property, never append to an array, so it cannot add a `security.json` right.
    - The template app ships no `actions.json`, so it would have to create one (`allowFileCreation`) in the
      output directory.
    - In the spike it reported "Post action failed." with no reason, even at `-v diag`.
  - Manual-instruction text is printed **without** symbol substitution (`Sample/TheEntity` came out
    verbatim).
  - `msbuild:RootNamespace` binds work from a subfolder: the evaluator walks up to the project. They need a
    restored project, though. Unrestored, the bind fails with a warning, and a `defaultValue` on the bind
    symbol is used instead (measured: `FallbackNs`).
- **Decisions:**
  - Items create code files only. A JSON edit an item needs (an `actions.json` entry, a `security.json`
    right, a context property) is spelled out in the generated file's own doc comment. That text is
    substituted, so it names the real action, type and folder. `spark add` (M5) makes those edits itself.
  - Run an item **from the project directory**. Each item writes into its conventional folder (`Entities/`,
    `Actions/`, `CustomActions/`, `LookupReferences/`, `Indexes/`), as DemoApp lays them out. Its namespace is
    `<RootNamespace>.<Folder>`. `--namespace` overrides the root; an unrestored project gets `UnrestoredProject`,
    which is visible in the file.
  - The template app gets `<Using Include="SparkApp.Library.Entities" />`, so app-side items need no entity
    namespace parameter.
- **Built (2026-11-17):** the five items are in `content/Spark{Entity,Actions,CustomAction,Lookup,Index}`.
  - `sourceName` is `SparkItem`. The namespace is the token `SparkNs`, which is a `coalesce` of `--namespace` over
    the `msbuild:RootNamespace` bind. `spark-entity` and `spark-index` take `--plural`, which defaults to a
    `join` of the name and `s` (token `SparkPluralName`). `spark-custom-action` takes a required `--entity`
    (token `SparkEntity`).
  - `spark-actions` is a `partial` class with an `[Inject] ILogger<T>` field and `IBeforeSave<T>`; the generator
    writes the constructor, including the base's `IEntityMapper`. `spark-custom-action` injects
    `IDatabaseAccess` and works on the selected rows or the parent.
  - Verified with a scratch run: generate a RavenDB app, run each item from its project directory, wire the
    entity and lookup as the doc comments say, then build, `--spark-synchronize-model` and verify. **Result:
    all five generate with the right folder and namespace (`Items.Library.Entities`, `Items.Actions`, …); the
    build has no warning; synchronize writes `Invoice.json` with the lookup attribute; `--spark-verify-model`
    exits 0 ("in sync").** Template tests 20/20.
  - Two fixes came from that run:
    - The first index sample had an `Id`-only view. The synchronizer merges the sole viewed index's view into
      the entity as its grid shape (the `[DefaultIndex]` election). Name and Status then went detail-only, and
      verify refused (exit 3): "none of that type's 2 attribute(s) is shown on a query", and "sorts by 'Name',
      which is showedOn 'PersistentObject'". The sample now claims `[DefaultIndex]` and projects `Name`.
    - `[Search]` on the view's `Name` raised SPARK006 until the Map filled `NameSearch`.
  - Still open for M3: nothing. The `spark add` JSON edits are M5.
  - **Found, for M6:** the pack stamps the highest tag *reachable from HEAD* into `Directory.Packages.props`. On
    a master-based branch that is the previews (`MintPlayer.Spark.SourceGenerators 11.0.0-preview.110`, which
    predates #551 and emits `IRavenQueryable` index roots that the current `SparkContext` rejects with CS0266),
    and `0.0.0-untagged` for the #689 libraries (NU1603). The stable `11.0.0` tags exist only on
    `release/dotnet-11.x`. The template must therefore be packed on the release line, never from master. The
    M4 matrix pins a same-commit feed (S2), so it does not hit this.

### M4: Template tests and CI
- ~~Add TemplateVerifier snapshots in a `*.Tests` project.~~ Dropped: the matrix builds, synchronizes and verifies
  what a snapshot would only diff, and the owner keeps template testing out of CI (below).
- ~~Add the matrix from S6 against a same-commit local feed (S2), inside an existing job (20-job cap).~~
  **Decision (owner, 2026-11-17): "Only test the templates locally. Clearly add the command to the readme."** The
  matrix is `node tools/template-matrix.mjs`, documented in `libs/templates/MintPlayer.Spark.Templates/README.md`
  ("Testing the template"); no workflow runs it. The owner was shown: full ~12–14 min vs a ~5 min PR run, smoke
  subset ~5–6 min.
- Add the PR checklist line from PRD §4.6.
- **Progress (2026-11-17):**
  - **Same-commit feed knob.** S2's recipe (`-p:Version=<v>` pins every package at `<v>`) stopped working in M1,
    when `RemoveProperties="Version;…"` was added so a stray `-p:Version` could not pin unreleased versions. Now
    `-p:SparkTemplateFeedVersion=<v> -p:SparkTemplateFeedDirectory=<dir>` packs every package the template pins at
    `<v>` into `<dir>` and pins `<v>`. Measured: 47 packages in 74–109 s.
  - **SPARKTPL001 also refuses `0.0.0-untagged`** in a non-Debug pack. Red: a plain Release pack on this branch
    now fails ("pins MintPlayer.Spark.InMemory at 0.0.0-untagged"), where in M3 it silently pinned that version and
    restore fell back to a pre-#551 preview.
  - **`tools/template-matrix.mjs`:**
    - Variants come from `features.json`: MinimalInMemory, MinimalRaven, Defaults, Oidc, AllOn and `Only<Feature>`
      for each feature, 32 in all.
    - Per variant: `dotnet new`, restore from the feed (package source mapping sends `MintPlayer.Spark*` to the feed
      only), build without the client, `--spark-synchronize-model`, `--spark-verify-model`. Neither flag touches the
      database, so CI needs no RavenDB.
    - `Defaults` also gets the five item templates, wired as their doc comments say.
    - Clients: one shared `node_modules` (the ClientApp's package.json is the same in every variant), and one
      `ng build` per distinct set of `#if`-bearing sources.
    - The version is unique per run, because NuGet's global packages folder caches by id and version.
  - **Slice measured on this machine (4 variants, parallel 4):**
    - Feed pack: 74 s.
    - Per variant: new + restore + build + synchronize + verify, 30–55 s.
    - Items step: 10 s.
    - Shared `npm install`: 165 s.
    - `ng build`: 13–21 s per distinct client (3 of 4 variants).
    - Extrapolated full matrix: ~12–14 min.
  - **Found by the slice:** AllOn's client failed the template's 1.5 MB initial budget (1.61 MB raw, 352 kB
    transferred). The routes are already lazy; the weight is the feature providers and renderers, which register at
    bootstrap. The budget is now 2 MB, the value the feature-rich apps use (QnA, Fleet, CodeCoverage, SeoTester,
    MintPlayer); DemoApp, HR and SparkId keep 1.5 MB.
  - **Full run (2026-11-17, this machine, parallel 4): all 32 variants pass, 179 steps, 629 s.** Feed pack 55 s,
    shared `npm install` 131 s, 15 distinct clients at 11–21 s each. The extrapolated 12–14 min was high.
  - The PR checklist line is in a new `.github/pull_request_template.md` (the repository had none). It names the
    matrix command, because CI no longer catches a broken template.
  - **M4 done.**

### M5: `MintPlayer.Spark.Cli` and `create-spark-app`
- Create the shared `MintPlayer.Spark.Cli.Commands` library with CliGenerator commands `new` and `add <item>`, and
  two thin `PackAsTool` hosts.
- Prompts use Spectre.Console (D19), with a flag for every prompt (D4).
- Add the RavenDB probe with an InMemory fallback, the OIDC authority check, `npm install`, and the eager
  synchronize.
- **Built (2026-11-17):**
  - `libs/cli/MintPlayer.Spark.Cli.Commands` (not packable): `SparkCli.RunAsync` is the whole entry; the hosts
    `libs/cli/MintPlayer.Spark.Cli` (command `spark`) and `libs/cli/create-spark-app` (prepends `new`, D18) are one
    line each. The library compiles `FeatureManifest.cs` and embeds `features.json`, so the picker and the
    validation read the manifest the template is generated from (D15); `FeatureManifest.Parse` was added for that.
  - `spark new [name]`: `-o`, `--store`, `--auth`, `--oidc-authority`, `--features a,b|none`, `--no-client`,
    `-y/--yes`, `--no-interactive`, `--skip-npm`, `--skip-sync`, hidden `--template`. It prompts (Spectre.Console,
    D19) only for what is missing, and never with `--yes`, piped stdin or `CI` set (D4). `NewRequest.Validate` refuses
    an unknown flag, a RavenDB feature (or a prerequisite of one) on InMemory, sign-in on InMemory, a client feature
    with `--no-client`, OIDC without an authority; it names the prerequisites it adds. The template call switches
    every enabled feature on or off explicitly, so the app never depends on the template's defaults matching the
    tool's. Then: RavenDB probe (offers InMemory minus the RavenDB features, on a terminal, when sign-in is off), OIDC
    discovery check (warns), template install (S3), `dotnet new spark-app`, `npm install`, synchronize (skipped when
    RavenDB is down: the app synchronizes on its first start, M2).
  - `spark add entity|actions|custom-action|lookup|index <name>`: one subcommand per item. It finds the app from the
    app, its library or the solution directory (`SparkProject.Locate`), runs the item in the project it belongs in,
    then makes the doc comment's edits as text (`JsonText`, `ContextEditor`: comments and layout survive, the file's
    line ending is kept): the context root, the security right (`Administrators` unless `--group`; a well-known group
    prints the row-filter advice), the actions.json entry. Every edit is computed before anything is written. The
    model-changing items end with `--spark-synchronize-model`.
  - Tests: `tests/MintPlayer.Spark.Cli.Tests`, 38 (red first): JSON text edits, the context edit, the locator, the
    group resolution and right, the installed-version parse, the request validation and template arguments.
- **Two fixes the smoke found (S3):**
  - `spark add entity Invoice` failed with "Required argument missing": CliGenerator 13.1.1 names an option after
    its alias without the dashes, and System.CommandLine 2.0 matches an option's name as a token, so `entity` was read
    as `--entity Invoice` (`add foo bar` parsed). Fixed by one subcommand per item (each shows only its options);
    filed as MintPlayer.Dotnet.Tools #220.
  - The RavenDB probe said "not answering" while curl got 200: `localhost` resolves to ::1 first, something
    swallows ::1:8080 on this machine, and HttpClient waits out its timeout instead of falling back (measured:
    2039 ms timeout via localhost, 200 in 13 ms via 127.0.0.1). The probe now asks both at once.
  - **M5 done.** Not covered by a test: the prompts themselves (Spectre needs a terminal) and the InMemory offer;
    the headless paths ran in the S3 smoke.

### M6: Release wiring and docs
- Make the pipeline handle the template and tool packages (S3).
- Publish `create-spark-app` in the first release, to claim the unprefixed name.
- Document `dotnet new install MintPlayer.Spark.Templates`, `dnx create-spark-app` and `spark add` in the guides
  and README. State the Angular-major note for template minors (PRD §4.7).
- **Built (2026-11-18):**
  - **Packing needed no change.** The `release` job packs the whole solution (`dotnet-build-master.yml:233`), so the
    template and both tools are packed and pushed with everything else. It runs only on a release (`:213-224`), which
    on Spark means a release line. That is also where the M3 finding requires the template to be packed, because
    there the pins resolve to the tags of the same run. Every released id must be in `nupkgs/` (`:238-256`), which
    holds `create-spark-app.<v>.nupkg` (S3).
  - **The template releases whenever a package it pins releases.** The engine reads `ProjectReference`s from the
    csproj body (`DotnetProjectReader.cs:81`), not `features.json`, and re-releases a .NET dependent whenever a
    dependency releases (versioning PRD D4). The template now references all 46 pinned projects, each with
    `ReferenceOutputAssembly=false`, `Private=false`, `PrivateAssets=all` and `SkipGetTargetFrameworkProperties`.
    Without them a fix to a pinned library shipped, and the published template still pinned the old version.
    - Guard: `The_template_project_references_exactly_the_packages_it_pins` checks the list in both directions. It was red
      with all 46 missing, then green (template tests 21/21).
    - A Release pack (same-commit feed `11.0.0-m6`) succeeded. Its nuspec has an empty `net11.0` dependency group and
      the package holds no DLL.
  - **One version for the three (coverage.yml `groups: spark-templates`).** A tool installs
    `MintPlayer.Spark.Templates::<its own version>` (M5). A tool released without the template would install a
    version that does not exist. A lockstep group releases every member at the highest version
    (`ReleasePlanner.cs:143-163`). All three target `net11.0`, so their majors agree.
  - **The template is pinned `prerelease: stable`.** The upstream label is the least mature in-repo dependency
    (`ReleasePlanner.cs:437-445`), and the template now references `Webhooks.GitHub(.DevTunnel)`, which stay on
    `preview`. Without the pin the template would release as `11.x-preview`. A `stable` pin overrides upstream
    (`:419`).
  - **Not verified locally:** an engine `run --plan-only` needs a Release build of the whole solution for its
    manifest, which takes too long for this check. The first `/release` after the merge is the proof. It should
    release `MintPlayer.Spark.Templates`, `MintPlayer.Spark.Cli` and `create-spark-app` at one stable `11.x`
    version, which also claims the unprefixed `create-spark-app` id (D18).
  - **Docs:**
    - The root README has "Create an app", and both READMEs are in its guide table.
    - The templates README covers the tool and gained "Versions": the release coupling and the Angular-major note
      (PRD §4.7). Its option table now points to `-h` for the feature switches.
  - **M6 done.**
- **The `.Tests` project (owner, 2026-11-18).** The post-M6 doc review found that PRD §4.1 promised `.Tests`, and that
  the template never shipped one. The owner chose to add it, referencing the package that ships `SparkTestDriver`.
  - **What it is:** `SparkApp.Tests` references the host and `MintPlayer.Spark.Testing`, and holds one sample
    `PersonTests` (store and load).
    - It ships with RavenDB only (`!IsRaven` excludes it, in both layouts), because the driver runs an embedded
      RavenDB.
    - It is in the slnx through an XML conditional comment. The matrix proved the engine processes those in
      `.slnx`: MinimalInMemory's slnx has no Tests line, and Defaults' has one.
    - It is in `primaryOutputs` for both layouts.
    - `MintPlayer.Spark.Testing` moved from `excluded: not-an-app-feature` to `core`, so the template pins it and
      references it (the M6 guard).
    - The template pins `Microsoft.NET.Test.Sdk 18.12.0`, `xunit 2.9.3` (v2, which Testing is built on) and
      `xunit.runner.visualstudio 4.0.1`, as DemoApp.Tests does.
    - `raven-license.log` is in the template's `.gitignore`.
  - **Guards (red → green):**
    - `Every_template_project_is_in_the_solution_and_the_primary_outputs`
    - `The_tests_project_references_the_testing_package_and_the_host`
    - Template tests 23/23.
  - **Found: SPARK001 refused every test project on the Testing *package*.**
    - MintPlayer.Spark's `buildTransitive` check demands the source generators in every project that reaches it.
    - A test project defines no Spark app, so it needs none. The matrix's Defaults build failed with SPARK001 in
      `Defaults.Tests`.
    - This repository never saw it, because its tests reference Testing by ProjectReference, and buildTransitive
      comes only with a package.
    - Fixed in `spark-testing.targets`, which sets `MintPlayerSparkSourceGeneratorsReferenceValidated`, as
      AllFeatures' package targets already do. That file's own comment already claimed the two packages shared
      the flag.
    - After the fix: matrix `Defaults` all green (build 9 s, synchronize 19 s, verify 2 s). `MinimalInMemory`
      green in the run before.
  - **The generated test runs:** `dotnet test Defaults.Tests` with `RAVENDB_LICENSE` set to the Developer licence's
    path. It passed twice, and the passing runs provisioned the embedded server in about 2 s.
  - **⚠️ Not located: one failure.** The first run, right after the matrix, failed in 4 s with `Value cannot be
    null. (Parameter 'ServerDirectory')`.
    - `RavenServerLocator` returned null, and no copy was attempted: afterwards there was no `7.2.7` folder,
      staging folder or lock.
    - All of its inputs were valid when checked: `RavenDB.Embedded/7.2.7` in the deps file, `Raven.Server.dll` in
      the package (extracted 2026-10-27), and `NUGET_PACKAGES` unset.
    - The exact command, rerun after deleting the provisioned copy, passed.
    - The locator has four silent `return null` paths, so the error cannot say which one fired.
    - `SparkEmbeddedServer.cs:58-59` says null "leaves RavenDB's default in place". It does not: RavenDB throws.
    - Making the locator report its reason is the next step if it recurs (not done: it changes that documented
      design).

## Outside this repo (issues only)
- Tools #18: framework scope, D5–D7, S4–S5 (updated 2026-11-16).
- Templates #6: README pointer, NuGet deprecation, bot off, archive, transfer to MintPlayer-Archive.
- Spark #689: three packages silently skipped by the versioning engine. ~~This is a separate defect and not part of
  this PR.~~ Fixed in this PR (2ac440c22): eight Web-SDK libraries lacked `IsPackable` (M1 remainder).
- MintPlayer.Dotnet.Tools #220: CliGenerator option names lose their dashes (found in M5, worked around here).

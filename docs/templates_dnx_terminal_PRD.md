# Spark templates, `dnx create-spark-app`, and a terminal UI framework

**Status:** PROPOSED. Investigation is done and no code exists yet. Three parallel surveys ran on 2026-11-16:
template packs and `dnx`, the .NET terminal-UI landscape, and the earlier MintPlayer CLI work.
Every decision is still open for the owner (§7). Spikes S1–S6 in the plan answer the technical unknowns.
**Plan:** [templates_dnx_terminal_plan.md](templates_dnx_terminal_plan.md)
**Issues:** Spark #433 (`dnx create-spark-app`), Tools
[#18](https://github.com/MintPlayer/MintPlayer.Dotnet.Tools/issues/18) (tool for advanced console
applications).

Evidence legend: **[V]** verified this session from a cited source or file. **[K]** comes from an
investigator's prior knowledge and was not re-fetched; a spike must confirm it before anything depends on it.

---

## 1. Executive summary

| Belief | Finding |
|---|---|
| *"MintPlayer.Dotnet.Templates is the place for Spark templates."* | **Refuted.** That repo holds one EF Core, Identity and ElasticSearch "ASP.NET Core with Angular" template (`mintplayer-web`), hard-coded at `10.0.0`, and nothing Spark-specific [V, `gh`]. Spark templates must track Spark's released versions, so they belong here, versioned by the tag engine like every other package. |
| *"`dnx` replaces `dotnet new`."* | **Refuted.** `dnx` is `dotnet tool exec <PackageId>[@version]`: it restores a tool package into the NuGet cache and runs it without installing it [V, [docs](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-exec)]. It runs code; it has no template engine of its own. The two work together: templates hold the content, and the `dnx` tool asks the questions. |
| *"An interactive UI makes commands and subcommands obsolete."* | **Refuted.** CI, scripts, `spark-regenerate` and coding agents all drive CLIs without a terminal. An interactive screen is a way to *fill in* a command's parameters, not a replacement for the command (D4). |
| *"The CLI generator in Tools is frozen and unusable."* | **Refuted.** `MintPlayer.CliGenerator` 13.1.1 is on nuget.org. It builds on System.CommandLine 2.0, registers commands in DI as scoped services, nests commands through `[CliParentCommand]` and has diagnostics MINTCLI001–009 [V, `gh`/nuget]. It is usable as the non-interactive layer as it stands. |
| *"We should write a full terminal framework ourselves."* | **Half-refuted.** Writing the **renderer and input stack** is years of edge cases already absorbed elsewhere (§5.3). The **developer-facing layer** is missing everywhere, and it is the part worth building: a host builder, DI, localization, command and interactive duality, and a component model. |

**Recommendation in one line:** ship a `MintPlayer.Spark.Templates` pack from this repo first. Then add a thin
`dnx` tool that prompts and drives the same templates. Build the terminal framework as a layer over a chosen
renderer, choose that renderer by spike, and keep the framework out of Spark (D6).

---

## 2. Goals and non-goals

**Goals**
- G1. `dotnet new install MintPlayer.Spark.Templates` provides a working Spark solution template and a set of
  item templates. A freshly generated app builds and runs.
- G2. `dnx <tool-package>` asks a few questions (name, store, auth, Angular yes or no) and produces the same
  output as G1. With every answer passed as a flag, it runs without prompting, so CI can use it.
- G3. A decision, backed by the spikes, on how to build the "advanced console" framework of Tools #18.
- G4. Template versions come from the tag engine. Nothing is hand-edited (repo CLAUDE.md, "Versions come from tags").

**Non-goals (for now)**
- A Spark-wide `spark` CLI that replaces the `--spark-*` host switches. It is listed as a later possibility in §6.4.
- Updating or archiving MintPlayer.Dotnet.Templates. That is another repo, so file an issue there.
- Visual Studio item-template integration ("Add > New Item"). The reason is in §4.1.

---

## 3. Findings: what exists today

### 3.1 Spark's moving parts a template must reproduce [V]
Taken from `apps/DemoApp/DemoApp/{DemoApp.csproj,Program.cs}`:
- **Projects:** a host (`Microsoft.NET.Sdk.Web`, net11.0, `SpaRoot=ClientApp\`), a `.Library` project
  (entities, lookups) and a `.Tests` project.
- **Packages:** `MintPlayer.Spark`, `.InMemory` or `.RavenDB`, `.Controllers`, the Spark source generators,
  `MintPlayer.AspNetCore.SpaServices` and `MintPlayer.SourceGenerators`. In the repo these are
  `ProjectReference`s, so the template must turn them into `PackageReference`s at the released versions.
- **Program.cs:**
  - `AddSpark(cfg, spark => { UseInMemory|RavenDB; UseContext<T>; AddControllers; AddActions })`.
  - The `SynchronizeSparkModelsIfRequested` / `…Security…IfRequested` early returns.
  - After `Build()`: `UseSpark`, `MapSpark`, and `UseSpaImproved` with `UseAngularCliServer("start")`.
- **`App_Data`:** `Model/*.json`, `actions.json`, `security.json`, `modelHashes.json`, `translations.json`
  and the other files are **all generated** by `--spark-synchronize-model` / `--spark-synchronize-security`,
  never written by hand (memory: model JSON needs the synchronizer).
- **ClientApp:** in the repo it runs through Nx targets (`nx run @spark-demo/demo-app:serve`). A standalone
  template needs a plain Angular CLI workspace on `@mintplayer/ng-spark` ^22.

### 3.2 Template and tool mechanics
- **Template pack:** `PackageType=Template`, `IncludeContentInPack`, `IncludeBuildOutput=false`,
  `.template.config/template.json`. Scaffold it with `dotnet new templatepack`, from
  `Microsoft.TemplateEngine.Authoring.Templates`, which adds validation and localization tasks
  [V, [MS Learn](https://learn.microsoft.com/en-us/dotnet/core/tools/templates)].
- **Template features:** symbols of type parameter, choice and bool. `sourceName` renames the app.
  `#if` and `sources.modifiers` handle conditional content [V]. Post-actions for restore, run script
  and add-to-solution exist [K].
- **Testing:** `Microsoft.TemplateEngine.Authoring.TemplateVerifier` gives snapshot tests (xUnit)
  [V, [Authoring Tools](https://github.com/dotnet/templating/blob/main/docs/authoring-tools/Authoring-Tools.md)].
- **`dnx`:**
  - It takes the **package ID**, not the command name, and supports `@version` and ranges.
  - It confirms the download by default. `-y|--yes` is documented for .NET 11 [V].
  - A RID-specific or AOT tool package needs one package per RID plus a pointer package [K].
- **Visual Studio:** project and solution templates appear in "Create a new project". Item templates
  are CLI-only [V, MS Learn]. Rider and C# Dev Kit behaviour is unknown, and S1 checks it.

### 3.3 Earlier MintPlayer CLI work [V]
- `MintPlayer.Dotnet.Tools/SourceGenerators/Cli` provides these attributes: `[CliRootCommand]`,
  `[CliCommand("name")]`, `[CliParentCommand(typeof(P))]`, `[CliOption("--x","-x")]`, `[CliArgument(i)]` and
  `ICliCommand.Execute(CancellationToken) → Task<int>`.
- It generates `services.Add{Root}()` and `host.Invoke{Root}Async(args)`. Verz runs on it.
- **Missing:** localization of command, option and description text, and all of Tools #18's interactive
  widgets.

### 3.4 Existing Spark command-line surface [V]
- `--spark-synchronize-model`, `--spark-verify-model` and `--spark-describe` are in
  `libs/spark/MintPlayer.Spark/Extensions/SparkDevelopmentExtensions.cs`.
- `--spark-synchronize-security` and `--spark-verify-security` are in
  `SparkSecurityVerificationExtensions.cs:31`.
- Parsing is hand-written against `args` in each app's Program.cs. Exit codes are 1, 2 and 3.
  CI wraps the switches in `.github/actions/spark-regenerate/action.yml`.
- No project in the repo has `PackAsTool` yet, so a tool package is new ground for the release pipeline.

---

## 4. Part A: templates (`MintPlayer.Spark.Templates`)

### 4.1 Contents
| Short name | Type | What it creates |
|---|---|---|
| `spark-app` | solution | Host, `.Library` and `.Tests`, with an optional ClientApp. Choices: `--store InMemory\|RavenDB` and `--auth none\|Authorization\|Authorization+IdP`. Plus `--no-client`. |
| `spark-entity` | item | An entity class and the `IQueryable<T>` root on the context. The post-action reminds the user, or runs, `--spark-synchronize-model`. |
| `spark-actions` | item | `XxxActions : DefaultPersistentObjectActions<T>` with `[Inject]` readonly fields (memory: never primary constructors). |
| `spark-custom-action` | item | A custom action class in `CustomActions/`. |
| `spark-lookup` | item | A lookup reference. |
| `spark-index` | item | A `SparkIndexCreationTask` (RavenDB only; indexes live in the application, per memory). |

Item templates show up only in the CLI (§3.2). The `dnx` tool can expose them as subcommands (`… add entity
Person`), and that is where they become discoverable.

### 4.2 Hard problems
1. **Versions inside template content.** The template's `PackageReference`s and the `@mintplayer/ng-spark`
   range must name the versions **released alongside the template pack**. Tags are the only source of
   truth (#153). A pack-time MSBuild step has to substitute tokens from the versions the engine resolved for
   the same release. Spike **S2**.
2. **Generated `App_Data`.** There are two options: ship a pre-generated snapshot, which can go stale against
   the packages, or run `--spark-synchronize-model` / `--spark-synchronize-security` as a post-action, which
   is slow and runs a script so the user is asked to confirm. Spike **S1** measures both.
3. **Angular workspace.** The repo's Nx wiring cannot be copied, so the template needs its own minimal CLI
   workspace and has to be kept in sync with `apps/DemoApp/ClientApp`.
4. **`.gitignore` traps.** `**/coverage/` and `Releases/` silently untrack folders with those names (memory).
   Avoid such names in template content and check `git status` after adding.
5. **Drift from DemoApp.** The template is a second copy of the minimal app. S1 decides whether DemoApp
   is *generated from* the template, which leaves one source, or whether a CI job instantiates and builds
   the template.

---

## 5. Part B: a terminal UI framework

### 5.1 What the owner wants
These are taken from the request and Tools #18:
- DI through a `TerminalHostBuilder.Create(...)` in the Generic Host style
- Colouring and localization
- Inputs: buttons (with mouse clicks), text, password, checkbox or multi-select, radio or single-select,
  with scrolling
- Spinners and progress
- Tabs, pagination, tables with selectable rows
- A calendar
- Draggable panels and popups
- A declarative or component model like RazorConsole's
- A non-interactive command mode

### 5.2 Landscape (survey of 2026-11-16)
| Library | Model | Mouse | DI/host | Widgets vs list | AOT | Headless test | Licence / state |
|---|---|---|---|---|---|---|---|
| Spectre.Console 0.57.2 | immediate (live displays) | no | partial | prompts, spinner, progress, table, calendar (static) | yes [V] | `TestConsole` | MIT, active [V] |
| Spectre.Console.Cli | command parser | – | partial | – | **no** (reflection) [V] | – | 1.0.0-alpha [V] |
| RazorConsole 0.5 / 1.0-alpha | Razor VDOM → Spectre renderables | yes [V] | Generic Host `UseRazorConsole<T>` [V] | button, select, text; partial | samples [V] | `RazorConsole.Testing` [V] | MIT, pre-1.0, ~1.7k★ [V] |
| Terminal.Gui v2 (2.4.x) | retained view tree | yes [V] | none | most of the list. TabView, DatePicker and movable windows [K] | partial [K] | partial | MIT, ~11k★, stable v2 [V] |
| XenoAtom.Terminal.UI (3.x) | retained, reactive, binding | yes [V] | unknown | DataGrid, overlays, dialogs, popups, toasts [V]; tabs and calendar unconfirmed | unknown | unknown | one maintainer (xoofx), net10-only [V]; licence unconfirmed |
| Hex1b (0.175) | declarative, React-style diffing | unknown | unknown | basic | unknown | screenshot tool | MIT, early 0.x [V] |
| Consolonia (11.3) | Avalonia XAML on a terminal | yes | Avalonia | XAML controls | no [K] | unknown | heavy |
| Sharprompt / PrettyPrompt | prompts only | no | no | prompts | yes | no | low activity [K] |
| System.CommandLine 2.0.x | parser | – | Hosting package dropped [V] | – | yes | yes | GA [V] |
| ConsoleAppFramework (Cysharp) | source-generated parser | – | DI | – | AOT-first [K] | – | active [K] |

**None of these covers the whole list.** Localization is left to the developer everywhere, and no library
combines a component model, mouse-driven retained widgets and a command mode.

### 5.3 Why we should not write the renderer
The renderer and input stack would have to solve all of the following:
- VT input parsing: SGR and X10 mouse, bracketed paste, and the kitty keyboard protocol with its modifier
  ambiguities.
- Windows console modes: conhost against Windows Terminal against ConPTY.
- Resize handling.
- Grapheme and wide-character width, which differs between terminals.
- Diff rendering without flicker.
- Testing against a virtual terminal.
- Accessibility: screen readers handle full-screen TUIs badly, which is another reason for a plain-text mode.

Terminal.Gui has absorbed these for years and XenoAtom more recently. Owning this layer is a maintenance cost
that does nothing for Spark.

### 5.4 What is worth building ("MintPlayer.Terminal", name TBD)
1. **Host:** `TerminalHostBuilder.Create(args)` over `Microsoft.Extensions.Hosting`, with DI, configuration,
   logging and `IStringLocalizer` throughout.
2. **Command/interactive duality (D4):** every screen binds to a command object (the CliGenerator's
   `ICliCommand` with `[CliOption]` properties).
   - **With flags given:** the command runs headless.
   - **On a TTY with values missing:** only the missing values are prompted for.
   - **With `--no-interactive` / no TTY / `CI=true`:** the command fails on missing values with a clear error.
3. **Localization of commands:** the gap in the CliGenerator (§3.3). This extends that generator; it does
   not replace it.
4. **Component model:** components built over the chosen backend's widgets. Whether they are Razor
   components (RazorConsole's approach) or C# reactive builders is spike **S5**.
5. **Missing widgets** (calendar, pagination and so on) are filled in only where the chosen backend lacks them.

---

## 6. Part C: `dnx create-spark-app`

### 6.1 Shape
- A `PackAsTool` package, the first in this repo (§3.4).
- The tool asks its questions, then instantiates the templates from Part A. This keeps one source of truth
  (D3). It either drives the template engine in process (`Microsoft.TemplateEngine.Edge` +
  `…Orchestrator.RunnableProjects`) or shells out to `dotnet new spark-app --…` after a
  `dotnet new install`. Spike **S3**.
- Afterwards it can run `npm install` and the synchronize steps, and optionally check RavenDB
  connectivity. This is its value over a bare `dotnet new`.

### 6.2 Package ID
Users type `dnx <PackageId>`, so the ID is the user experience. Candidates:
- `MintPlayer.Spark.Create`: prefix-consistent. The owner already holds the `MintPlayer` prefix [K].
- `create-spark-app`: short and npm-like, but outside the prefix.

Either way, the ID is **permanent on first publish** (memory: a wrong ID or major cannot be undone). Q4.

### 6.3 Staging
The `dnx` tool must not wait for the terminal framework. v1 uses simple prompts: plain `Console`, or
Spectre.Console prompts, which are AOT-safe and MIT [V]. It moves onto the framework once S4 and S5 have
settled it. The command definitions (CliGenerator) stay the same, so only the prompt layer changes.

### 6.4 Later: a `spark` CLI
`spark model sync|verify`, `spark security sync|verify`, `spark describe` and `spark add <item>` would wrap
the existing `--spark-*` switches. The switches stay as the stable protocol: the CLI runs
`dotnet run -- --spark-…` against `--project`, for one app or several, the way `spark-regenerate` does.
This is out of scope here and is recorded so the tool's command tree leaves room for it.

---

## 7. Decisions

| # | Decision | Status | Evidence |
|---|---|---|---|
| D1 | Spark templates live in this repo as `MintPlayer.Spark.Templates`, versioned by tags, released from `release/dotnet-11.x` | proposed | §1 row 1; repo CLAUDE.md versioning |
| D2 | Templates come before the `dnx` tool; the tool comes before the framework | proposed | §6.3: the framework is the longest pole and must not block #433 |
| D3 | The `dnx` tool instantiates the template pack and never generates files itself | proposed | Avoids two copies of the app; §6.1 |
| D4 | Every interactive flow has a flag-complete non-interactive equivalent | proposed | §1 row 3; `spark-regenerate` and CI drive CLIs headless |
| D5 | Do not write a renderer or input stack; build host, DI, localization, duality and components over an existing backend chosen by S4 | proposed | §5.2–5.3 |
| D6 | The terminal framework is not Spark code. It lives in MintPlayer.Dotnet.Tools (next to CliGenerator, Tools #18) or in its own repo | proposed | Memory: don't couple unrelated packages, and other repos get an issue rather than edits from here |
| D7 | Reuse `MintPlayer.CliGenerator` for commands; add localization there | proposed | §3.3 |

## 8. Open questions for the owner
- **Q1. Store default.** Should `spark-app` default to InMemory (like DemoApp) or RavenDB, and should RavenDB
  be a choice?
- **Q2. Auth.** Should the template offer Authorization and the SparkId identity provider, or start
  core-only?
- **Q3. Angular.** Is a ClientApp included by default, and is it plain Angular CLI or Nx?
- **Q4. Tool ID.** `MintPlayer.Spark.Create` or `create-spark-app` (§6.2)?
- **Q5. Framework home.** MintPlayer.Dotnet.Tools or a new repo (D6)? Does #433 stay a Spark issue, with the
  framework tracked as Tools #18?
- **Q6. Old templates repo.** Archive MintPlayer.Dotnet.Templates, or keep `mintplayer-web` there?
- **Q7. Scope of "fully-fledged".** Is the calendar or the draggable panels a must-have for v1 of the
  framework, or is the first customer just `create-spark-app`'s prompts?
- **Q8. Release.** Is it acceptable that the template pack and the tool release only from the release
  line (via a backport), like every other Spark package?

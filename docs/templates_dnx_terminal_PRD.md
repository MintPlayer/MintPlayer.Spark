# Spark templates, `dnx create-spark-app`, and a terminal UI framework

**Status:** DECIDED, not started.
- **Investigation:** three parallel surveys on 2026-11-16 covered template packs and `dnx`, the .NET terminal-UI
  landscape, and the earlier MintPlayer CLI work.
- **Decisions:** taken in a grill session with the owner the same day (§7). Each one records its alternatives and
  the risk accepted.
- **Next:** spikes S1–S6 in the plan answer the remaining technical unknowns.

**Plan:** [templates_dnx_terminal_plan.md](templates_dnx_terminal_plan.md)

**Issues:**
- Spark #433 (`dnx create-spark-app`).
- Tools [#18](https://github.com/MintPlayer/MintPlayer.Dotnet.Tools/issues/18): the terminal framework, which
  lives there (D6).
- Templates [#6](https://github.com/MintPlayer/MintPlayer.Dotnet.Templates/issues/6): archive the old repo.
- Spark #689: a side finding (§9).

Evidence legend: **[V]** verified this session from a cited source or file. **[K]** comes from prior knowledge and
was not re-fetched; a spike must confirm it before anything depends on it.

---

## 1. Executive summary

| Belief | Finding |
|---|---|
| *"MintPlayer.Dotnet.Templates is the place for Spark templates."* | **Refuted.** It holds one EF Core, Identity and ElasticSearch template (`mintplayer-web`), hard-coded at `10.0.0`, and nothing Spark-specific [V, `gh`]. Spark templates must track Spark's releases, so they ship from here. The old repo is archived (D11). |
| *"`dnx` replaces `dotnet new`."* | **Refuted.** `dnx` is `dotnet tool exec <PackageId>[@version]`. It runs a tool package without installing it and has no template engine [V, [docs](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-exec)]. The templates hold the content; the tool asks the questions. |
| *"An interactive UI makes commands and subcommands obsolete."* | **Refuted.** CI, `spark-regenerate` and coding agents drive CLIs without a terminal. An interactive screen fills in a command's parameters (D4). |
| *"The CLI generator in Tools is frozen."* | **Refuted.** `MintPlayer.CliGenerator` 13.1.1 is on nuget.org. It is built on System.CommandLine 2.0, with DI-scoped commands and nesting through `[CliParentCommand]` [V]. |
| *"We should write a full terminal framework."* | **Refuted for the renderer, accepted for the developer layer.** We commit to RazorConsole so that downstream developers write Razor components. We build what it lacks: a component library and a command↔component bridge (D5). |
| *"A backend abstraction keeps the renderer swappable."* | **Refuted.** Razor components are tied to RazorConsole's renderer. Once developers write `.razor`, nothing is swappable, and an abstraction would only cost effort (grill Q2–Q3). |

---

## 2. Goals and non-goals

**Goals**
- G1. `dotnet new install MintPlayer.Spark.Templates` provides **one** `spark-app` template with a choice per Spark
  feature. The choices visible in Visual Studio are controlled by `ide.host.json`. It also provides item templates.
- G2. `dnx create-spark-app` (or `dnx MintPlayer.Spark.Cli`) asks for the same choices and produces the same
  output. With every answer passed as a flag, it runs without prompting.
- G3. A generated app runs on its first F5 or `dotnet run` in every host: Visual Studio, CLI and the tool (D13).
- G4. Versions come from the tag engine. Nothing is hand-edited.
- G5. In MintPlayer.Dotnet.Tools, a component library and a command bridge over RazorConsole (Tools #18).

**Non-goals**
- Our own terminal renderer or input stack (D5).
- A backend abstraction over terminal libraries (§1).
- Changing DemoApp (D12).
- A Visual Studio "Add > New Item" integration. Item templates are CLI-only in Visual Studio [V, MS Learn]; the
  tool's `spark add <item>` is where they become discoverable.

---

## 3. Findings: what exists today

### 3.1 How a Spark app is assembled [V]
- **Projects:** a host (`Microsoft.NET.Sdk.Web`, net11.0, `SpaRoot=ClientApp\`), a `.Library` project and a
  `.Tests` project (`apps/DemoApp`).
- **Features** are calls on the `AddSpark` builder, for example in `apps/HR/HR/Program.cs:27-88`:
  - `spark.AddMessaging()`
  - `AddMailManager()`
  - `AddReplication(...)`
  - `AddMcp(...)`
  - `AddMigrations()`
  - `AddExternalProviders(...)`

  Some features also need `Use`/`Map` calls, appsettings sections or ng-spark entry points.
- **Feature packages depend on each other.** For example:
  - `MintPlayer.Spark.Notifications` references Spark.RavenDB, Authorization, Messaging and WebPush.
  - `Organizations` references RavenDB and Authorization.

  `libs/` holds about 30 feature folders.
- **`MintPlayer.Spark.AllFeatures`** already references most features and generates
  `AddSparkFull`/`UseSparkFull`/`MapSparkFull` from feature flags (`SparkFullFeatureFlags`). That is prior art for
  per-feature wiring knowledge.
- **Auth:**
  - `spark.AddAuthentication<SparkUser>(auth => auth.LocalCredentials = SparkLocalCredentials.Full)` gives local
    accounts with no identity provider (`apps/QnA/QnA/Program.cs:44-46`).
  - External OIDC providers come from **configuration** (`Spark:Auth:Providers`, `:49-50`).
- **Stores:** `MintPlayer.Spark.RavenDB` and `MintPlayer.Spark.InMemory` are both stable at `v11.0.0` (tags,
  2026-11-16).
- **`App_Data`** is entirely generated by `--spark-synchronize-model` / `--spark-synchronize-security`
  (`SparkDevelopmentExtensions.cs`, `SparkSecurityVerificationExtensions.cs:31`). It is never hand-written.

### 3.2 Template and tool mechanics
- **Template pack:** `PackageType=Template`, `.template.config/template.json`, scaffolded with
  `dotnet new templatepack` [V, [MS Learn](https://learn.microsoft.com/en-us/dotnet/core/tools/templates)].
  Snapshot tests use `Microsoft.TemplateEngine.Authoring.TemplateVerifier` [V].
- **`ide.host.json`** (`.template.config/`) controls which symbols Visual Studio shows (`symbolInfo`, `isVisible`)
  [V, [schema](https://www.schemastore.org/ide.host.json)].
- **`isEnabled` conditions between symbols did not work in Visual Studio when the owner used them, around 2023**
  (owner's evidence). Whether VS 2026 honours them is unknown (S1).
- **Post-actions** for run script, restore and add-to-solution exist [K]. Whether Visual Studio runs a run-script
  post-action is [K] (S1).
- **`dnx`:**
  - It takes the package ID and `@version`.
  - It confirms the download by default. `-y` is available on .NET 11 [V].
  - A RID-specific or AOT tool package needs one package per RID plus a pointer package [K].

### 3.3 Terminal-UI landscape (2026-11-16)
| Library | Model | Mouse | DI/host | Fit | Licence / state |
|---|---|---|---|---|---|
| **RazorConsole** 0.5 / 1.0-alpha | Razor VDOM → Spectre renderables | yes [V] | `UseRazorConsole<T>` on Generic Host [V] | Built-ins: TextInput (masking), TextButton, Select, Spinner, Table, Panel, layout, charts, Markdown [V, [list](https://razorconsole.com/blog/built-in-components/)]. **Missing:** tabs, calendar, checkbox/radio lists, progress, pagination, modal, draggable panels, scroll containers, row selection | MIT, pre-1.0, ~1.7k★; `RazorConsole.Testing` [V] |
| Spectre.Console 0.57.2 | immediate | no | partial | prompts, including a grouped, scrolling `MultiSelectionPrompt`; progress; table | MIT, AOT [V] |
| Terminal.Gui v2 2.4.x | retained | yes | none | broad (TabView, DatePicker, movable windows [K]) | MIT, stable [V] |
| XenoAtom.Terminal.UI 3.x | retained, reactive | yes | ? | DataGrid, overlays [V] | one maintainer; licence unconfirmed |
| Hex1b 0.175 | declarative | ? | ? | basic | MIT, early [V] |
| System.CommandLine 2.0.x | parser | – | Hosting package dropped | – | GA [V] |

### 3.4 Earlier MintPlayer CLI work [V]
- `MintPlayer.CliGenerator` provides these attributes: `[CliRootCommand]`, `[CliCommand]`, `[CliParentCommand]`,
  `[CliOption]`, `[CliArgument]`.
- Commands implement `ICliCommand.Execute(CancellationToken) → Task<int>`. The generator produces
  `services.Add{Root}()` and `host.Invoke{Root}Async(args)`.
- **Missing:** localization, and prompting for missing values.

---

## 4. Part A: `MintPlayer.Spark.Templates`

### 4.1 One template, a choice per feature (D8)
`spark-app` creates a host, a `.Library` and a `.Tests` project, plus a ClientApp. Its choices:

| Choice | Values | Default | Decision |
|---|---|---|---|
| `--store` | `RavenDB` \| `InMemory` | `RavenDB` | D9 |
| `--auth` | `none` \| `authorization` \| `oidc` | `authorization` | D10. `authorization` gives local accounts. `oidc` adds a `Spark:Auth:Providers` entry with a placeholder authority; it is configuration only, with no code difference. |
| `--no-client` | bool | off (client included) | D14. The client is a plain Angular CLI workspace on `@mintplayer/ng-spark` ^22, zoneless, with ng-bootstrap components, not Nx. |
| one bool per feature | Messaging, MailManager, Notifications, Organizations, Boards, Dashboards, History, SoftDelete, Moderation, Contributions, Replication, Cron, Settings, WebPush, Mcp, IdentityProvider, … | off | D8 |

### 4.2 The feature manifest (D15)
`features.json` in the template project lists, per feature:
- package id
- `requires` (other features)
- store constraint
- needs-client flag
- `AddX`/`UseX`/`MapX` fragments
- appsettings section
- ng-spark entry points
- localizable label and description

A pack-time step generates from it the `template.json` symbols, the computed prerequisite symbols, `ide.host.json`
and the `#if` regions in `Program.cs`, `appsettings.json` and the ClientApp.

Two tests guard it:
- **Graph test:** each feature's `requires` equals its csproj ProjectReference graph.
- **Coverage test:** every packable lib under `libs/` is in the manifest or explicitly excluded. A new feature
  can't be forgotten silently.

### 4.3 Prerequisites and conflicts (D16)
- **Soft prerequisites (other features)** are switched on automatically through computed symbols. The generated
  README lists them, for example "added because Notifications needs it".
- **Hard constraints** (store, and client-only features with `--no-client`) behave as follows:
  - **In Visual Studio:** `isEnabled` grays the choice out. If S1 shows VS still ignores `isEnabled`, Create fails
    with a clear message instead.
  - **On the CLI:** generation is refused with a clear error. S1 must prove a template can *refuse* a combination,
    not just ignore it [K].
- **The `dnx` tool** resolves all of this itself and never offers an invalid combination.

### 4.4 `App_Data` on first run (D13)
A snapshot is impossible across about 30 feature combinations. So `App_Data` is produced in three places:
1. **Baseline: a new Spark option**, for example `spark.SynchronizeOnStartupIfMissing()`. The generated app calls
   it in Development only. It runs the regular synchronizers **only when the files are missing** and never
   overwrites reviewed model files. This is a change to `MintPlayer.Spark`, and it lands in the same PR.
2. **The template's run-script post-action** runs the synchronize eagerly where the host allows it.
3. **The `dnx` tool** runs it eagerly too, along with `npm install`.

### 4.5 Item templates
`spark-entity`, `spark-actions` (`[Inject]` readonly fields), `spark-custom-action`, `spark-lookup` and
`spark-index` (RavenDB only).

### 4.6 Drift and CI (D12)
- **DemoApp stays as it is:** the guides' example, on ProjectReferences and an Nx ClientApp.
- **The template is guarded by a CI matrix** in an existing job, because of the 20-job cap. The matrix covers
  minimal, all-on, and each feature alone with its prerequisites (about 32 instantiations). Each one builds .NET
  and the ClientApp and runs `--spark-verify-model`.
- **It runs against packages packed from the same commit**, on a local feed, never nuget.org.
- **Whether it fits a PR job** is S6's question.
- **PR checklist line:** "Touched DemoApp's or a feature's wiring? Check the manifest."

### 4.7 Versions inside the template (S2)
The template's `PackageReference`s and the ng-spark range must name the versions released in the same run, taken
from the tag engine.

The pack is a .NET-major NuGet package (11.x) carrying Angular-major content, so an Angular 23 move changes the
template as a minor version. The release notes must say so.

---

## 5. Part B: terminal framework (in MintPlayer.Dotnet.Tools, Tools #18)

- **D5: RazorConsole is the renderer.** We write no renderer and no backend abstraction, and downstream developers
  write Razor components.
- **What we ship:**
  1. **A component library:** Tabs, Calendar/DatePicker, CheckboxList, RadioList (scrolling), Progress,
     Pagination, DataTable with row selection, Modal, and a draggable Window. Generic fixes go upstream to
     RazorConsole as PRs.
  2. **A command bridge:** a CliGenerator `ICliCommand` paired with a Razor component as its interactive form,
     where `[CliOption]` properties become component state.
     - **With flags given:** the command runs headless.
     - **On a TTY with values missing:** the form opens.
     - **With `--no-interactive`, no TTY or `CI=true`:** it fails with a clear error (D4).
     - **Localized command and option text** is added to the CliGenerator.
- **Gate (S4):** RazorConsole renders through Spectre's immediate-mode output. Before committing to Modal and the
  draggable Window, prove that overlays, absolute positioning, scroll regions and mouse drag can be done without
  forking its renderer.
- **Accepted risk:** RazorConsole is pre-1.0. A fork means owning a renderer after all. Its MIT licence keeps that
  possible.

---

## 6. Part C: the `spark` CLI and `create-spark-app`

- **D17: `MintPlayer.Spark.Cli`,** command `spark`.
  - `dnx MintPlayer.Spark.Cli` with no arguments, outside a Spark project, starts the interactive `new`.
  - Subcommands: `new` and `add <item>`, with `model`/`security`/`describe` later. Those later ones wrap the
    existing `--spark-*` switches through `dotnet run -- --spark-…` against `--project`; the switches remain the
    stable protocol.
- **D18: `create-spark-app` alias.** The commands live in a shared `MintPlayer.Spark.Cli.Commands` library, with
  two thin tool hosts: `spark` and `create-spark-app`. The alias's `Main` runs `new` in process. Both are packed in
  the same release run.
  - Only the dashed ID ships, which matches #433. It sits outside the `MintPlayer` prefix reservation, so it must
    be in the first release to claim the name.
- **D19: v1 prompts use Spectre.Console.** The feature picker needs a grouped, scrolling multi-select. It moves onto
  the Tools component library once a CheckboxList exists. The command definitions (CliGenerator) don't change; only
  the prompt layer does.
- **Tool-only extras:** a RavenDB connectivity probe that offers InMemory when no server is found, an OIDC authority
  check, `npm install`, and the eager synchronize.
- All IDs were free on nuget.org on 2026-11-16 [V].

---

## 7. Decisions

| # | Decision | Alternatives rejected | Risk accepted | Evidence |
|---|---|---|---|---|
| D1 | Templates ship from this repo as `MintPlayer.Spark.Templates`, tag-versioned, released from `release/dotnet-11.x` | the old Templates repo | – | §1; repo CLAUDE.md |
| D2 | Order: templates → tool → framework | framework first | – | owner, grill Q1 |
| D3 | The tool instantiates the template pack and never generates files itself | hand-generation | – | one source of truth |
| D4 | Every interactive flow has a flag-complete headless equivalent | interactive-only | – | §1 row 3 |
| D5 | RazorConsole is the renderer. We ship a component library and a command bridge; generic fixes go upstream | own renderer; backend abstraction; upstream-only; nothing | RazorConsole may not support overlays or drag (S4); pre-1.0 | owner, grill Q2–Q3; §3.3 |
| D6 | The framework lives in MintPlayer.Dotnet.Tools | new repo; this repo | Tools becomes a grab-bag; a later split loses history | owner, grill Q4. Generator and bridge change in one PR. |
| D7 | Reuse CliGenerator and add localization and missing-value prompting there | – | – | §3.4 |
| D8 | **One** `spark-app` template with a choice per feature; `ide.host.json` controls what VS shows | separate templates per scenario | large symbol surface | owner, grill Q10 |
| D9 | Store: RavenDB default, InMemory choice | InMemory default; RavenDB only | the first run fails without a server, so the README and the tool's probe must say so | owner, grill Q5 |
| D10 | Auth: `none \| authorization \| oidc` | without oidc; core only | the oidc placeholder must be filled in before external login works | owner, grill Q6; §3.1 |
| D11 | Archive MintPlayer.Dotnet.Templates and move it to MintPlayer-Archive | keep alive; leave alone | `mintplayer-web` users stay on net10; NuGet deprecation message | owner, grill Q14; Templates #6 |
| D12 | DemoApp unchanged; CI matrix guards the template against same-commit packages | DemoApp generated from template; template generated from DemoApp | catches broken, not different | CLAUDE.md DemoApp role |
| D13 | `App_Data`: the app synchronizes on its first Development run if missing; the post-action and the tool also run it eagerly | post-action only; tool only | a core change; it must never overwrite | owner, grill Q13 |
| D14 | Client: plain Angular CLI, included by default, `--no-client` | Nx; opt-in | a second ClientApp copy can drift, so CI builds it | owner, grill Q7 |
| D15 | Feature choices generated from a `features.json` manifest, guarded by graph and coverage tests | hand-written JSON; self-describing packages | generator complexity; matrix size (S6) | owner, grill Q11 |
| D16 | Prerequisites auto-included; hard constraints use `isEnabled` in VS and refusal on the CLI | auto-only; disable-only | VS may still ignore `isEnabled` (owner, around 2023), which falls back to refusing at Create | owner, grill Q12 |
| D17 | Tool `MintPlayer.Spark.Cli`, command `spark` | `.Create`; `create-spark-app` only | the ID promises a general CLI | owner, grill Q8 |
| D18 | `create-spark-app` alias via a shared commands assembly, run in process | runtime `dnx` forward; name reservation only | two tool IDs in the repo's first tool release | owner, grill Q9 |
| D19 | v1 prompts use Spectre.Console | RazorConsole now | a later swap of the prompt layer | RazorConsole lacks a multi-select [V] |

---

## 8. Remaining unknowns (owned by spikes)

| Unknown | Spike |
|---|---|
| Does VS 2026 honour `isEnabled`? Does it run run-script post-actions? Can a template refuse a combination? Do Rider and C# Dev Kit show the template? | S1 |
| Released versions in template content; a same-commit local feed for CI | S2 |
| First tool packages in the pipeline (two IDs); `dnx` cold start; in-process template engine vs `dotnet new` | S3 |
| RazorConsole overlays, absolute positioning, scroll and drag | S4 (Tools) |
| Command bridge and localized commands | S5 (Tools) |
| Manifest generator shape; whether about 32 instantiations fit one PR job | S6 |

---

## 9. Side findings (2026-11-16)

- **A correction from this session.** An early listing suggested `MintPlayer.Spark.InMemory` was still on preview.
  It is not: the listing truncated the tags, and `v11.0.0` exists.
- **Spark #689.**
  - Boards, Notifications and Organizations were **silently skipped** by the 11.0.0 release. The engine gave them
    no version and they are absent from `MintPlayerVersioningMajors`.
  - Pack then disabled them: `This project cannot be packaged…`, run 43854085870.
  - The run stayed green.
  - `/release` reports nothing after "its release run starts now".
- **#687's ng-spark changes are not released yet.** Since Segment P, a master merge releases nothing. They ship
  with the next `/release angular-22.x`.

# Plan: Spark templates, `dnx create-spark-app`, terminal UI framework

**PRD:** [templates_dnx_terminal_PRD.md](templates_dnx_terminal_PRD.md)
**Status:** NOT STARTED. Spikes come first. Each spike has a time box, a question, an exit criterion,
and a place to record its answer (with evidence: numbers, file:line, commands run).

Order: **S1 → S2 → M1–M3 (templates)**, then **S3 → M4–M5 (`dnx` tool)**. The framework track
(**S4 → S5 → S6**) is independent and runs in parallel or after; it lives outside this repo (PRD D6).

---

## Spikes

### S1: Template skeleton from DemoApp (1 day)
**Question:** can `dotnet new spark-app` produce a host, Library and Tests solution that builds and runs
against **published** Spark 11.x packages? Is `App_Data` better as a snapshot or as a post-action?
- Scaffold with `dotnet new templatepack`. Copy DemoApp and replace its ProjectReferences with
  PackageReferences. Add a plain Angular CLI ClientApp on `@mintplayer/ng-spark` ^22.
- Measure: time to instantiate, time to the first `dotnet run`, and time for the synchronize post-action.
  Check whether a pre-generated `App_Data` passes `--spark-verify-model` against the released packages.
- Check visibility in VS 2026 "Create a new project", Rider and C# Dev Kit.
- **Exit:** a generated app serves its Angular shell, plus a decision between snapshot and post-action
  backed by the timings.
- **Answer:** _(pending)_

### S2: Released versions inside template content (½ day)
**Question:** how do `PackageReference Version="…"` and the `ng-spark` range inside the template get the
versions released alongside the template pack, without hand edits (#153)?
- Option A: a pack-time MSBuild target substitutes tokens from `MintPlayer.Versioning.MSBuild`'s resolved
  versions of the sibling packages.
- Option B: the template references a floating `11.*` range.
- Option C: CPM `Directory.Packages.props` generated at pack time.
- Check how the release line packs packages in one run, and whether a sibling's version is known at the
  template's pack time.
- **Exit:** the chosen option, proven by a local pack showing real versions in `content/`.
- **Answer:** _(pending)_

### S3: `dnx` tool mechanics (½ day)
**Question:** should the tool drive the template engine in process or shell out to `dotnet new`? How does
`dnx` feel on the first run?
- Build a throwaway `PackAsTool` package and run it with `dnx --add-source <local>` on a cold cache. Time it
  and note the confirmation prompt (and `-y`).
- Try `Microsoft.TemplateEngine.Edge` to instantiate the S1 template from the tool's own embedded pack.
  Compare with `dotnet new install` followed by `dotnet new spark-app`.
- Check whether the release pipeline (versioning, pack, publish `--skip-duplicate`) handles a tool package.
  This is the repo's first.
- **Exit:** a decision between in-process and shelling out, the first-run timing, and the pipeline changes
  needed.
- **Answer:** _(pending)_

### S4: Terminal backend bake-off (2–3 days, outside this repo)
**Question:** which renderer should the framework sit on?
- Build the same screen in **Terminal.Gui v2**, **XenoAtom.Terminal.UI** and **RazorConsole**. The screen
  has:
  - a text box, password, checkbox list (scrolling) and radio list
  - a button clicked with the mouse
  - tabs, a table with selectable rows, a date picker and a draggable dialog
  - a spinner and progress bar
- Score each on: Windows Terminal, conhost and a Linux terminal; mouse; resize; wide characters; NativeAOT
  publish; a headless test of the screen; how well it fits DI and Generic Host; licence (confirm XenoAtom's);
  maintainer count.
- Confirm the PRD's **[K]** claims (Terminal.Gui TabView, DatePicker and movable windows; AOT).
- **Exit:** a scored matrix and a recommendation.
- **Answer:** _(pending)_

### S5: Component model (2 days, after S4)
**Question:** Razor components or C# reactive builders over the chosen backend?
- Prototype `TerminalHostBuilder.Create(args)` with DI, `IStringLocalizer` and one screen in each style.
  Judge how readable the screen is, how state and events work, and how hot reload behaves.
- **Exit:** the chosen style and a sketch of the public API.
- **Answer:** _(pending)_

### S6: Command/interactive duality (1 day, after S4)
**Question:** can one `ICliCommand` (CliGenerator) run headless with flags and prompt for missing options on
a TTY?
- Prompt only for the options that are not set. Honour `--no-interactive`, `CI=true` and a redirected stdin.
  Check the exit codes.
- Design localized command, option and description text as an extension to the CliGenerator.
- **Exit:** a working sample, plus an issue filed in MintPlayer.Dotnet.Tools for the generator changes.
- **Answer:** _(pending)_

---

## Milestones (this repo)

### M1: `MintPlayer.Spark.Templates` package
- Create `libs/templates/MintPlayer.Spark.Templates/`. Model the csproj on
  `libs/sitemap/MintPlayer.Spark.SitemapXml/MintPlayer.Spark.SitemapXml.csproj`:
  `0.0.0-PLACEHOLDER`, README, MIT, net11.0, `PackageType=Template`.
- Add `spark-app` with choices `store`, `auth` and `client` (pending Q1–Q3), using S2's version mechanism.
- Avoid folder names caught by `.gitignore` (`coverage`, `Releases`) and check `git status`.

### M2: Item templates
- Add `spark-entity`, `spark-actions`, `spark-custom-action`, `spark-lookup` and `spark-index` (PRD §4.1).

### M3: Template tests and CI
- Add TemplateVerifier snapshot tests in a `*.Tests` project, which gets `NoSystemProxy` automatically.
- Add one CI step that instantiates `spark-app`, builds it and runs `--spark-verify-model`. Fit it into an
  existing job, because the run is capped at 20 concurrent jobs (repo CLAUDE.md).
- Decide whether DemoApp is generated from the template or whether CI guards the drift (PRD §4.2.5).

### M4: The `dnx` tool package
- Use the package ID from Q4, with `PackAsTool` and `ToolCommandName`.
- Commands defined with `MintPlayer.CliGenerator`: `new` (the default) and `add <item>`.
- Every prompt has a flag equivalent (PRD D4). Prompts are v1-simple (PRD §6.3).
- After generation it runs `npm install` and the synchronize steps, and optionally probes RavenDB.

### M5: Release wiring
- Make the versioning, pack and publish pipeline handle the template and tool packages, using S3's findings.
- Document `dnx …` and `dotnet new install …` in the guides (`docs/`) and the README.

## Outside this repo (file issues, do not edit from here)
- MintPlayer.Dotnet.Tools: framework PRD seeded from S4–S6 (Tools #18). CliGenerator: localization and
  missing-value prompting hooks.
- MintPlayer.Dotnet.Templates: archive or point to `MintPlayer.Spark.Templates` (Q6).

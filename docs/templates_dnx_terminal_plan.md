# Plan: Spark templates, `dnx create-spark-app`, terminal UI framework

**PRD:** [templates_dnx_terminal_PRD.md](templates_dnx_terminal_PRD.md). Decisions D1–D19 were locked with the
owner on 2026-11-16.

**Status:** NOT STARTED. Each spike has a time box, questions, an exit criterion and an answer slot. Record each
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
      symbol was renamed to `AuthMode`; retest pending. **The manifest generator must avoid VS's reserved symbol
      names** (`auth`, `Framework`, `langVersion`, …), each to be confirmed.
    - ❌→fix: **blank solution** (test 3). The template had no `primaryOutputs`, and VS adds only the listed projects
      to the solution. `spark-app` must list every project (host, Library, Tests) in `primaryOutputs`. Retest
      pending, along with auto-include and labels.

### S2: Versions in template content and a same-commit feed (½ day)
- How do the template's `PackageReference`s and the ng-spark range get the versions released in the same run?
  Options:
  - a pack-time token substitution from `MintPlayer.Versioning.MSBuild`
  - a generated `Directory.Packages.props`
  - a floating `11.*` range
- How does the CI matrix pack every package from the current commit into a local feed, and point the generated
  apps at it?
- **Exit:** a local pack whose `content/` shows the real versions, plus a recipe for the local feed.
- **Answer:** _(pending)_

### S6: Manifest generator and matrix cost (1 day)
- Draft the `features.json` schema and a generator for symbols, computed prerequisites, `ide.host.json` and `#if`
  regions, covering three real features.
- Prototype the graph test (`requires` equals ProjectReferences) and the coverage test (every packable `libs/*` is
  listed or excluded).
- Time about 32 instantiations (minimal, all-on, each feature alone) with a shared restore and parallel builds.
- **Exit:** the schema, timings, and whether the matrix fits one PR job or needs to be a subset on PRs and the full
  matrix on master.
- **Answer:** _(pending)_

### S3: Tool packages and `dnx` (½ day)
- Pack `MintPlayer.Spark.Cli` and `create-spark-app` from one shared commands library. Run both with
  `dnx --add-source <local>` on a cold cache. Record the time and the confirmation prompt, with and without `-y`.
- Compare the in-process template engine (`Microsoft.TemplateEngine.Edge`) with running `dotnet new install` and
  then `dotnet new spark-app`.
- Check that the versioning engine, pack and publish handle tool packages; these are the repo's first.
- **Exit:** the instantiation approach, timings, and the pipeline changes needed.
- **Answer:** _(pending)_

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

### M2: `SynchronizeOnStartupIfMissing` (core, D13)
- Add a new option in `MintPlayer.Spark`. In Development it runs the model and security synchronizers when
  `App_Data` files are missing, and never overwrites. Write the test red first.
- If the option adds an endpoint or route, update `CsrfSurfaceTests` and the route snapshot. It shouldn't, but
  check.

### M3: Item templates
- Add `spark-entity`, `spark-actions`, `spark-custom-action`, `spark-lookup` and `spark-index`.

### M4: Template tests and CI
- Add TemplateVerifier snapshots in a `*.Tests` project.
- Add the matrix from S6 against a same-commit local feed (S2), inside an existing job (20-job cap).
- Add the PR checklist line from PRD §4.6.

### M5: `MintPlayer.Spark.Cli` and `create-spark-app`
- Create the shared `MintPlayer.Spark.Cli.Commands` library with CliGenerator commands `new` and `add <item>`, and
  two thin `PackAsTool` hosts.
- Prompts use Spectre.Console (D19), with a flag for every prompt (D4).
- Add the RavenDB probe with an InMemory fallback, the OIDC authority check, `npm install`, and the eager
  synchronize.

### M6: Release wiring and docs
- Make the pipeline handle the template and tool packages (S3).
- Publish `create-spark-app` in the first release, to claim the unprefixed name.
- Document `dotnet new install MintPlayer.Spark.Templates`, `dnx create-spark-app` and `spark add` in the guides
  and README. State the Angular-major note for template minors (PRD §4.7).

## Outside this repo (issues only)
- Tools #18: framework scope, D5–D7, S4–S5 (updated 2026-11-16).
- Templates #6: README pointer, NuGet deprecation, bot off, archive, transfer to MintPlayer-Archive.
- Spark #689: three packages silently skipped by the versioning engine. This is a separate defect and not part of
  this PR.

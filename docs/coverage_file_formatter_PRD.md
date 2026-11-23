# File formatter — a custom action that opens a pull request formatting a file by the repository's own settings

**Status:** Draft 2026-11-23. Research done, nothing implemented. The open questions Q1–Q7 await the owner, and spikes
S1–S6 are in [coverage_file_formatter_plan.md](coverage_file_formatter_plan.md).
**Issue:** #657 · **App:** `apps/CodeCoverage` (production, coverage.mintplayer.com) · one pull request

## The problem, stated plainly

#657 asks for: *"Add CustomAction that creates a pull-request which formats a file according to the workspace settings
(eg `.editorconfig` for .net solutions)"*.

So a user looks at a file, sees it doesn't follow the repository's style, and lets the bot open a pull request that
fixes it. This feature has three parts:

1. **Where the action lives.** The user is looking at a file, but files are not persistent objects in CodeCoverage (see
   below).
2. **Who runs the formatter, and on what.** The formatter has to honour the repository's settings, and the tool version
   the repository pins. Most formatters execute repository code once they go beyond whitespace.
3. **How the change becomes a pull request.** This part already exists: the onboarding bot pull requests and the
   dependency-update patches.

## What exists to build on

All verified against `master` @ `ff66fd0a6`. Paths are under `apps/CodeCoverage/`.

### Pull requests opened by a custom action

- **`ConvertCentralPackagesAction`** (`CodeCoverage/CustomActions/ConvertCentralPackagesAction.cs:26`) is the closest
  precedent.
  - It is a query action on Repository (`App_Data/actions.json:62-67`, `showedOn: "query"`, `selectionRule ">0"`),
    limited to two queries (`Actions/RepositoryActions.cs:121-127`).
  - Its grant is in `App_Data/security.json:333-337`, mirrored in `securityPosture.txt:68`, with its label in
    `translations.json:18`.
  - Besides the Spark grant, it keeps only repositories the caller manages, through
    `IDependencyOnboardingService.CheckAsync` (`ConvertCentralPackagesAction.cs:52-58`).
  - It asks a question with `manager.Retry.Action` (`:41-45`).
  - It reports synchronously: one `manager.Client.Notify` per repository with the PR URL (`:64-69`).
- **`OnboardingService.BotPullRequestAsync`** (`CodeCoverage/Onboarding/OnboardingService.cs:572-624`) turns a set of
  `GitWriterFile(Path, Content)` edits into a pull request. It takes four steps:
  1. gets writer access (`IForgeMergeQueue.GetWriterAccessAsync`, `:576`; workflow files need `workflows: write`)
  2. commits through the git-writer sidecar (`writer.OnboardingAsync`, `:586`; `MergeQueue/GitWriterSidecarClient.cs`)
  3. finds the pull request, or creates it (`IForgeDependencyUpdates.Find/CreatePullRequestAsync`, `:611-622`)
  4. refuses other forges before planning (`RefusalBeforePlanning`, `:98-107`)
- **GitHub only.** `IForgeDependencyUpdates` and `IForgeMergeQueue` have only GitHub implementations
  (`CodeCoverage.GithubIntegration`).

### Reading the repository

- **`IRepositoryTree`** (`CodeCoverage.Versioning/Graph/RepositoryTree.cs:18`, `Entries` + `ReadAsync(path, maxBytes)`)
  is the shared "tree + contents at a commit" abstraction. It has two implementations:
  - `GitTree` (`:35`) reads the bare mirror
  - `ForgeRepositoryTree` (`CodeCoverage/PackageVersioning/ForgeRepositoryTree.cs:12`) reads through the forge API,
    and refuses truncated listings
- **Nothing parses style configuration today.** There is no reader for `.editorconfig`, `.prettierrc*` or
  `.globalconfig`, and no Roslyn Workspaces or EditorConfig package in the CodeCoverage projects.

### Running tools: never on the server

- **The server runs git, and nothing else.**
  - Git runs in the GitWriter/GitMirror sidecars, on bare blob-less clones with no checkout and no hooks
    (`CodeCoverage.Git/GitRunner.cs:167`, `CodeCoverage.GitWriter/Jobs/WriterService.cs`).
  - The only other process is `dotnet --version` (`CodeCoverage.Versioning/Build/DotnetSdk.cs:17`).
  - There is no Node and no sandbox.
- **Package managers run in the repository's own runner.** That is the `dependency-action`
  (`dependency-action/action.yml`, modes `lookup | apply | migrate`, `src/main.ts:67`).
  - The server dispatches it with `workflow_dispatch` (`Dependencies/Lifecycle/DependencyUpdateEngine.cs:343,406,678`;
    `WorkflowHook.cs:269`).
  - The action never pushes. It uploads a patch artifact.
  - The server downloads the artifact (`DependencyUpdateEngine.cs:917`, `migrate.patch` + `migrate-result.json`), and
    `PatchValidator` / `DependencyLayoutCheck` validate it (`CodeCoverage.GitWriter/Jobs/`).
  - GitWriter pushes under a lease (`WriterService.Dependencies.cs:69`).
- **The action advertises what it can do** in a `# capabilities:` header that the bot reads at the SHA a repository
  pins (`dependency-action/action.yml:1-4`). A new mode is announced there.
- **Constraint:** `dependency-action/dist` is committed, and CI fails on any byte difference. Rebuild it only from the
  main checkout (repo `CLAUDE.md`, "The committed ncc bundles").

### Files in the UI

- **A file is not a persistent object.** `FileCoverage` (`CodeCoverage.Library/Entities/FileCoverage.cs`) is plain data.
- **The file viewer is a custom Angular page** at `:provider/r/:owner/:repo/c/:sha/f?path=`
  (`ClientApp/src/app/app.routes.ts:114`, `pages/file/file.component.ts:162`). A file is identified by provider, owner,
  repo, commit sha and repo-relative `path`.
- **Files are listed** by `components/commit-files-panel` via `BrowseController.cs:444` (`/tree`) and `:525`
  (`/hierarchy`). `:583` serves one file.
- **No custom action acts on a single file today.**

## Formatter research

Sources: [dotnet format docs](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-format),
[Prettier configuration](https://prettier.io/docs/configuration), [Prettier plugins](https://prettier.io/docs/plugins).
Items marked † come from our own knowledge and are not yet measured. S1 and S2 measure them.

| | C#: `.editorconfig` | TS/JS/JSON/SCSS/HTML: Prettier |
|---|---|---|
| Single file without a build | `dotnet format whitespace --folder --include <file>`: yes, loads no project†. `style`/`analyzers` need a loaded project, restore included | `prettier --write <file>`: yes |
| Runs repository code? | `whitespace --folder`: no. `style`/`analyzers`: **yes**. Microsoft: *"may restore, compile, and run analyzers … Only invoke the tool against trusted code"* | `.prettierrc.json`/`.yaml`: no. `prettier.config.(m\|c)js` and `plugins`: **yes** (loaded with `import()`) |
| What the safe subset misses | var/braces preferences, using order (`dotnet_sort_system_directives_first`, IDE0055), unused usings (IDE0005), all analyzer fixes† | Repositories with a JS config or plugins (common in Angular/Nx workspaces) |
| Version pinning | The SDK from `global.json` picks dotnet format. Whitespace rules are stable, style defaults move† | The `devDependencies` version matters: 2→3 changed the `trailingComma` default |
| Config precedence | `.editorconfig` hierarchy up to `root = true` | `.prettierrc*` wins over `.editorconfig`. Prettier maps only `end_of_line`, `indent_style`, `indent_size`, `max_line_length` from `.editorconfig` |
| Licence | MIT (Roslyn, dotnet format)† | MIT |

**Prior art:** none of these runs on a shared server; each runs in the repository's own workflow.
- [autofix.ci](https://autofix.ci): the repository's workflow runs the formatter, and the hosted service pushes the
  result.
- [git-auto-commit-action](https://github.com/stefanzweifel/git-auto-commit-action): commits with `GITHUB_TOKEN`
  (`contents: write`). Those commits trigger no new runs.

**Conclusion.** A formatter that honours the repository's settings (style rules, plugins, the pinned version) has to
run repository code. The server never runs repository code, and that boundary should stay. The safe in-process subset,
Roslyn whitespace or Prettier with JSON config and no plugins, is a different, weaker feature. It would also need Node
on the server.

## Proposed design (to confirm in the grill)

### D1 — The formatter runs in the repository's runner, as a new `format` mode of `dependency-action` (proposed)

This uses the same chain as `migrate`:
1. **Dispatch:** the server dispatches the repository's dependency workflow with `mode: format`, a `base-sha` and the
   requested path(s).
2. **Check out and format:** the runner checks out `base-sha` and runs the repository's own formatter at the
   repository's pinned version:
   - .NET: `dotnet format` with the `global.json` SDK
   - Prettier: from the lockfile, with scripts off
3. **Upload:** the runner uploads `format.patch` + `format-result.json`.
4. **Push:** the server validates the patch (Q4: it may touch only the requested files), GitWriter commits it on a bot
   branch, and `IForgeDependencyUpdates` opens the pull request, or finds it.

**Why.** Every building block exists, the runner already runs repository code by design, and the server keeps its
"git only" boundary. The `# capabilities: format` header lets the bot refuse repositories that pin an older action
(S5).

**Rejected, pending the owner (Q2):**
- an in-process Roslyn/Prettier formatter on the server (weaker, needs Node, and blurs the boundary)
- a separate `format-action` (a second committed ncc bundle and a second workflow template to onboard)

### D2 — The action is asynchronous

The action itself only validates and dispatches. It notifies *"Formatting `<path>` — the pull request follows when the
run finishes"*. The result arrives through the existing `workflow_run` webhook or the poll (`WorkflowHook.cs:353`).

The user learns the outcome from the pull request itself, plus a notification if Spark can deliver one later. S4
checks whether `manager.Client.Notify` can reach a user after the request ends. If it can't, Q6 decides.

A run that produces **no diff** ends with no pull request, plus a recorded result ("already formatted").

### D3 — Authorization is the same as `ConvertCentralPackages`

- A Spark grant on the action (`security.json` + `securityPosture.txt`, then `--spark-synchronize-security` and
  `--spark-verify-security`).
- Plus "caller manages the repository", checked through the existing onboarding check.
- A private repository's existence must not leak: refusals for an unmanaged repository read the same as for an absent
  one.

### D4 — Pull request shape (proposed)

- **Base branch:** the branch the viewed commit belongs to. Q3 asks whether to always use the default branch instead.
- **Head:** `coverage/format/<hash of base branch + path>`. Running the action twice for the same file updates the open
  pull request instead of opening a second one (find-or-create, as `BotPullRequestAsync` does).
- **Base commit:** the base is the branch's current tip, not the viewed sha, so the patch applies cleanly.
- **Body:** which formatter and version ran, and which config files it honoured.

## Open questions for the owner (grill)

- **Q1 — Scope of "a file".** One file from the file page only? Or several (a folder, the commit's changed files, the
  whole repository)? Proposal: one or more paths, and the UI offers one file in v1.
- **Q2 — Where it runs.** The runner (D1), or also an in-process whitespace-only fallback for repositories without the
  dependency workflow? Proposal: runner only.
- **Q3 — Which branch.** The viewed commit's branch (D4), or always the default branch?
- **Q4 — Patch guard.** Should the server refuse a patch that touches anything besides the requested files? A formatter
  can rewrite a shared file such as a `using` order fix, though that's unlikely. Proposal: refuse.
- **Q5 — Which formatters in v1.** dotnet format (whitespace + style, or analyzers too?) and Prettier only? Others
  (gofmt, black, rustfmt) later? Proposal: dotnet format `whitespace` + `style`, and Prettier.
- **Q6 — Feedback.** Is "the pull request appears" enough, or should the file page show a pending/done state?
- **Q7 — Where the action lives.** A button on the file page (S3: a custom page calling a Spark action with parameters),
  a Repository query action that asks for a path, or both?

## Out of scope

- Other forges. The pull request building blocks are GitHub only, and onboarding already refuses the rest.
- Formatting on every push or as a check: this is an on-demand action.
- Running any formatter on the server.

## Decision log

| # | Decision | Evidence |
|---|---|---|
| — | none yet; filled in after the grill and the spikes | |

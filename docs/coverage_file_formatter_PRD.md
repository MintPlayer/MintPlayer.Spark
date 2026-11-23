# Formatting — repository actions and pipeline steps that format code by the repository's own settings

**Status:** Grilled 2026-11-23 (Q1–Q14 answered, see "Decisions"). Resumed 2026-11-23 after #710 merged (#711): the
bell, its pop-out and visitor notifications this feature's run feedback builds on are on master. Spikes are in
[coverage_file_formatter_plan.md](coverage_file_formatter_plan.md).
**Issue:** #657 · **App:** `apps/CodeCoverage` (production, coverage.mintplayer.com) · one pull request

## The problem, stated plainly

#657 asks for: *"Add CustomAction that creates a pull-request which formats a file according to the workspace settings
(eg `.editorconfig` for .net solutions)"*.

The grill widened it into one formatting feature with three entry points:

1. **On demand:** the Repository actions **FormatRepository** and **LintRepository** open a pull request that formats
   (or lint-fixes) the repository by its own settings.
2. **In the merge queue:** a conflict that differs **only in whitespace** is resolved, and the files are formatted
   before the squashed commit is published.
3. **In backports:** the same, after the cherry-pick, with the release line's own formatter.

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
    `IDependencyOnboardingService.CheckAsync` (`:52-58`).
  - It asks a question with `manager.Retry.Action` (`:41-45`), and reports with `manager.Client.Notify` (`:64-69`).
- **`OnboardingService.BotPullRequestAsync`** (`CodeCoverage/Onboarding/OnboardingService.cs:572-624`) turns a set of
  `GitWriterFile` edits into a pull request.
  - It gets writer access (`IForgeMergeQueue.GetWriterAccessAsync`, `:576`). For a workflow file it asks
    `pushesWorkflowChanges: true`, and `GitHubForgeMergeQueue.cs:1358` mints a token with `workflows: write`.
  - It commits through the git-writer sidecar (`:586`).
  - It finds the pull request, or creates it (`IForgeDependencyUpdates`, `:611-622`).
  - It is GitHub only: `RefusalBeforePlanning`, `:98-107`.
- **Onboarding checkboxes** are `EOnboardingFeature` values (`CodeCoverage.Library/Onboarding/EOnboardingFeature.cs`:
  `SizeLabels`, `SdkFromGlobalJson`, …). Each feature adds its files to the one onboarding pull request through
  `OnboardingPlanner.PlanAsync` (`CodeCoverage/Onboarding/OnboardingPlanner.cs:118`).
- **Handing a bot pull request to the merge queue.** Send `MergeQueueSignalMessage { Command = Enqueue, Actor, Source }`
  through the outbox (`Dependencies/Lifecycle/DependencyUpdateEngine.cs:1531-1535`). The queue re-checks rules,
  approvals and checks itself (R5).

### The queue's and the backports' regenerate step

- **Merge queue** (`docs/code-coverage/merge-queue.md:23-66`).
  - After PREPARE resolves conflicts in `autoResolve` files, REGENERATE dispatches the repository's workflow on
    `mq/<base>/pr-<n>`, and the run uploads `merge-queue-patch`.
  - PUBLISH validates the patch against `regeneratedPaths` and squashes prepared tree + patch into **one** commit.
  - Case 1/2 (no conflict to resolve) dispatch nothing.
  - `autoResolve[].action` names a rule's own workflow, up to 8 distinct ones (`:265`).
  - A `.github/**` change evicts `touches-workflows` (`:39`).
- **Backports** (`docs/code-coverage/backports.md:69-75`): after the cherry-pick, the line's own regenerate workflow
  runs on the backport branch. If it fails, the pull request opens with a "Not regenerated" note.
- **The runner pieces are composite actions plus templates, not ncc bundles:** `.github/actions/lockfile-regenerate`
  and `.github/actions/spark-regenerate`, with the templates `docs/code-coverage/templates/lockfiles-regenerate.yml`
  and `regenerate.yml`.
  - Their SECURITY header: `contents: read`, no secrets, a GitHub-hosted runner only, never pushes.
  - The queue checks that the `mq/` copy of the workflow equals the default branch's (D12).
- ⚠️ *"The merge queue's runner pieces (`lockfile-regenerate`, `spark-regenerate`) have no automated tests; they were
  proven live only. The new action must not repeat that"* (`docs/code-coverage/dependency-updates-PRD.md:868`).

### Where code runs

- **The server runs git only** (`CodeCoverage.Git/GitRunner.cs:167`, in bare blob-less clones with no checkout and no
  hooks), plus `dotnet --version`. It never runs repository code.
- Repository code runs only in the repository's own runner.

### Notifications

- **`MintPlayer.Spark.Notifications`** (`libs/notifications/MintPlayer.Spark.Notifications/README.md`).
  - `ISparkNotifier.NotifyAsync` stores a `Notification` document. Its queries are live, so an open UI gets it over
    `/spark/live`. Mail and Web Push are optional channels.
  - Today only SeoTester calls `AddNotifications()`.
  - A notification is addressed by `UserIds` only.
- **`/spark/live` accepts anonymous connections** (`libs/spark/MintPlayer.Spark/Endpoints/Live/LiveSocket.cs:43-44`).
- **ng-spark:**
  - `notifications` has `SparkNotificationsService` and `<spark-notification-bell>`, which follow the live queries.
  - `client-operations` has a hand-rolled `<spark-toast-container>` (fixed top-right, plain `--bs-*` colours) that shows
    the in-request `manager.Client.Notify` messages.
  - Nothing toasts a live notification.
- **Since #710 (#711, master `aff3e24bc`):** the toast container is gone. `<spark-notification-bell>` is in every app's
  topbar, CodeCoverage's included, and pops out both the in-request `Notify` messages and arriving stored
  notifications. CodeCoverage still has no `AddNotifications()`/`withNotifications()`, so its bell shows session
  messages only until M8. `SparkNotification` also takes `VisitorIds`; FormatRun's requester is always signed in, so
  `UserIds` is enough here.

## Formatter research

Sources: [dotnet format docs](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-format),
[Prettier configuration](https://prettier.io/docs/configuration), [Prettier plugins](https://prettier.io/docs/plugins).
Items marked † come from our own knowledge and are not yet measured. S1/S2 measure them.

| | C#: `.editorconfig` | TS/JS/JSON/SCSS/HTML: Prettier |
|---|---|---|
| Single file without a build | `dotnet format whitespace --folder --include <file>`: yes†. `style`/`analyzers` need a loaded project, restore included | `prettier --write <file>`: yes |
| Runs repository code? | `whitespace --folder`: no. `style`/`analyzers`: **yes**, *"Only invoke the tool against trusted code"* | JSON/YAML config: no. JS config and `plugins`: **yes** |
| Version pinning | the `global.json` SDK picks dotnet format; style defaults move† | the `devDependencies` version matters (2→3 changed `trailingComma`) |
| Licence | MIT† | MIT |

**Prior art:** none of these runs on a shared server; each runs in the repository's own workflow.
- [autofix.ci](https://autofix.ci) runs the formatter in the repository's workflow, and a hosted service pushes.
- [git-auto-commit-action](https://github.com/stefanzweifel/git-auto-commit-action) commits with `GITHUB_TOKEN`.

**Conclusion:** a formatter that honours the repository's settings must run repository code, so it runs in the
repository's runner (D1).

## Decisions (grill 2026-11-23)

| # | Decision | Evidence |
|---|---|---|
| D1 | **The formatter runs in the repository's runner, never on the server.** No in-process fallback. A repository without the formatting workflow is refused with "set up Formatting first". | Q2 = A (owner). Research above, "Where code runs" |
| D2 | **Onboarding installs it.** A new `EOnboardingFeature.Formatting` checkbox adds the formatting workflow to the onboarding pull request. | Owner: "a new checkbox in the onboarding flow … already established patterns". `EOnboardingFeature.cs` |
| D3 | **Two Repository custom actions, FormatRepository and LintRepository**, each granted in `security.json`. Query actions like `ConvertCentralPackages`, so several repositories can be selected; the caller must manage each repository. Both dispatch the **same** workflow with `mode: format \| lint`. | Q1 = B (owner) |
| D4 | **No file-page button.** The workflow's `paths` input exists for the pipelines. The docs recommend one FormatRepository run right after onboarding, so later pull requests stay small. | Q7 = A (owner) |
| D5 | **Auto-queue prompt.** Only when the repository has the merge queue feature, `manager.Retry.Action` offers "Open the pull request" or "Open it and add it to the merge queue". The enqueue happens right after the pull request opens, with `Actor` = the user who clicked and a new `EMergeQueueCommandSource.Formatting`. The queue still enforces approvals and checks; the pull request body notes "queued; waiting for a review" when it holds. | Q8 = A (owner). `DependencyUpdateEngine.cs:1531` |
| D6 | **The on-demand pull request targets the default branch**, like every other bot pull request. Release lines are formatted by their own backports (D8). | Q3, superseded by the owner's pipeline proposal |
| D7 | **The on-demand pull request may format `.github/**`.** It goes through `BotPullRequestAsync`, which asks for `workflows: write`. | Owner: workflow writes are solved (popup user token for onboarding; installation token with `workflows: write`). `GitHubForgeMergeQueue.cs:1358` |
| D8 | **The pipeline step is a resolver for whitespace-only conflicts.** It covers the merge queue (after the rebase, before the squash) and backports (after the cherry-pick, with the line's own copy of the workflow). It runs **only** when such a conflict was resolved, so clean merges still dispatch nothing. | Owner: "mainly for when the merge-conflicts are only about whitespaces"; Q9 = A |
| D9 | **Guards on the pipeline step:** (1) the patch may touch only files the pull request or backported commit changed; (2) never `.github/**`, because an unreviewed workflow change after approval is an escalation (`touches-workflows`, D12); (3) if the pull request changes formatter inputs (config, plugins, the formatter's lockfile entry, `global.json`), the step is **skipped with a note**, not evicted. | Q9 = A (owner) |
| D10 | **A new `autoResolve` strategy, `whitespace`, with a preset `autoResolveDefaults: [formatting]`** for the safe types. A hunk qualifies when both sides are equal without whitespace (string literals are S7). The head's content is taken, and the format workflow rewrites the file with the **target** branch's formatter. YAML, Python and other whitespace-significant types are not in the preset and keep today's behaviour. | Q10 = C (owner). `merge-queue.md:259-314` |
| D11 | **A per-repository deny-list** as an `exclude` input (globs) on the composite action, set in the repository's own workflow. All three entry points honour it. | Owner, on Q10 |
| D12 | **The runner piece is a composite action plus a template:** `.github/actions/format` (inputs `mode`, `paths`, `exclude`, `expected_sha`, `pr_number`) and `docs/code-coverage/templates/formatting.yml`. One artifact contract, `merge-queue-patch`. The logic lives in a small script **with tests**, plus a live proof. | Q11 = A (owner). `dependency-updates-PRD.md:868` |
| D13 | **The first version ships these tools.** Format: `dotnet format whitespace`+`style`, Prettier, `gofmt`, `rustfmt`, `ruff format`/`black` (whichever `pyproject.toml` configures), `terraform fmt`. Lint: `dotnet format analyzers`, `eslint --fix`, `ruff check --fix`, `cargo clippy --fix`; Go and Terraform report "format only". Each uses the repository's pinned version. | Q5 = B, ESLint included (owner) |
| D14 | **Run feedback is a stored run plus a live notification.** A `FormatRun` document holds the repository, mode, requester, run id, state, head branch, the auto-queue choice and an expiry. Completion calls `ISparkNotifier.NotifyAsync`, which carries the pull request URL or the outcome ("already formatted", "run failed", "timed out"). CodeCoverage adopts `AddNotifications()`. **The bell-only UI and anonymous async notifications are a separate issue and pull request, released before this one** (Q12–Q14). | Q6 = A, Q12 = A, Q13 = A, Q14 = A in a separate PR (owner) |
| D15 | **The patch guard (former Q4) is `PatchValidator.Parse` with a per-entry-point path policy**, as `MigratePatchValidator` and `DependencyPatchValidator` already do. Both profiles keep `Parse`'s shape rules: only mode 100644, no binary patch, no copy. A formatter only rewrites files, so both also refuse **new files, deletions and renames**. On-demand: `IsWritablePath` (`.github/**` allowed, D7) minus the `exclude` globs. Pipeline: `IsSafePath` (never `.github/**`, D9.2) **and** the changed-files allow-list (D9.1) minus `exclude`. | S6 below. The proposed default, made stricter (no adds, deletes or renames) |
| D16 | **The pipeline step never formats a file that was not already formatted.** Before rewriting, the action checks the **target branch's** copy of the conflicted files with `--verify-no-changes` (or the tool's equivalent). If any is not clean, the step is **skipped with a note** ("format the repository first"), like D9.3, and does not evict. The cost, accepted: a repository that never runs FormatRepository never gets whitespace conflicts resolved. | Q15 = A (owner, 2026-11-23). S1: Spark's defaults would change 258 files |
| D17 | **The runner proves a hunk is whitespace-only (Q16 = A).** The writer's `whitespace` resolver pre-filters with "equal without whitespace" and resolves with the head's side (`merge-file --theirs`). The format run then rebuilds both whole-file resolutions (`merge-file --ours` and `--theirs` on `merge_base`, `ours_sha`, `theirs_sha`), formats both with the target's formatter, and requires them to be **byte-identical**. Otherwise the run fails, and the pull request is evicted as an unresolved conflict. The patch is the formatted `--theirs` file. C# gets no Roslyn check in the writer: the runner's proof covers it. | Owner, 2026-11-23 ("probably A"), confirmed by S10 (resolutions identical on git 2.43–2.54) and S11 (no false accept across 7 formatters) |
| D18 | **The pipeline run derives its own files; the server guards the patch by the resolutions (M1, follows from D17).** The runner gets `ours_sha`, `theirs_sha` and `merge_base` instead of a `paths` list. Its candidates are the files changed on both sides whose three-way merge conflicts, whose `--ours` and `--theirs` resolutions differ, and whose prepared copy equals `--theirs`. That is stricter than D9.1's "changed by the pull request", so `paths` and the `ListPullRequestFilesAsync` call (S9) are dropped. The server keeps the patch to the paths resolved under the `whitespace` strategy (D15's pipeline profile, with that list as the allow-list). The queue sends `ours` = the base it rebased onto and `theirs` = the head it merged, with `merge_base` empty (the runner computes it); backports send the picked commit's parent as `merge_base`. | D17; S9 (the resolutions are recorded per path) |
| D19 | **Under D17, the D9.3 and D16 cases change.** (1) A pull request that changes formatter inputs **fails** the pipeline run, and the queue evicts: the proof would run the pull request's own formatter config, so it proves nothing. D9.3's "skip with a note" stays for nothing: without the proof the `--theirs` resolution cannot stand. (2) A candidate under `.github/**` or `exclude`, or one no formatter of the repository covers, also fails the run. (3) D16 runs **after** the proof: a proven resolution whose target copy is not formatted is kept as the writer made it (the head's side, unformatted), with a note, instead of being formatted. | Follows from D17: the writer's `--theirs` resolution is only safe once proven. Tests: `format.test.mjs`, "pipeline: …" |
| D20 | **Artifacts and modes.** One workflow, `formatting.yml`, with the dispatch inputs `mode` (default `pipeline`), `expected_sha`, `pr_number`, `queue_run_id`, `merge_base`, `ours_sha`, `theirs_sha`, all optional so one file serves all three entry points. The template lives at `apps/CodeCoverage/CodeCoverage/Formatting/formatting.yml` (embedded; `.dockerignore` leaves `docs/` out of the image, so not under `docs/code-coverage/templates/` as D12 said). `exclude` is not a dispatch input: per D11 it is written in the workflow's `with:`, and the server reads it from the default branch's copy for the D15 guard. The template calls the action at the moving tag `format-v1`, which `format-action-publish.yml` moves on every master push that changes the action (the owner's 2026-11-13 rule for the bot's actions, `ActionTagReference`). Every run uploads `merge-queue-patch` (one file, possibly empty); format and lint also upload `format-report` (JSON: tools, versions, notes, files, remaining lint problems), which the pull request body quotes. A tool gets explicit file lists of at most 400 (S8), and any changed file outside them is restored before the patch is taken. | S4, S5, S8 |

## Spike results (2026-11-23)

### S1 — dotnet format in a runner

The runs were on a `git archive` copy of this branch (3393 `.cs` files), never on the working tree: SDK 11.0.100,
`dotnet format` 11.0.100-rtm, Windows.

| Run | Result |
|---|---|
| `whitespace --folder --verify-no-changes`, whole tree | 24 s, no restore, exit 2: **6141 findings in 258 files** |
| the same, `--include` one clean file | 2 s, exit 0 |
| the same, `--include` one dirty file (`DotnetApiListing.cs`) | exit 2, 287 findings, **all in that file** |
| `--include` that file, writing | the file only, 616 changed lines |
| a nested `.editorconfig` beside it with `[*.cs] indent_style = tab` | exit 0 |
| a nested one with `root = true` and only `end_of_line` | exit 2, 287 findings: the parent is ignored, defaults apply |
| `style` on `CodeCoverage.Library.csproj`, no restore first | 13 s, exit 0; it restores on its own |
| the same after an explicit restore, `--no-restore` | 9 s (restore 3 s) |
| SDK **10.0.115** (`global.json` pinned to it), whitespace, whole tree | 24 s, **the same 6141 findings, line for line** |
| SDK 10.0.115, `style` on a `net11.0` project | exit 1, crash: the project cannot load |

The answers:

1. **`whitespace --folder` needs no restore and no SDK match.**
   - It is fast and gives the same results on SDK 10 and 11.
   - `style` and `analyzers` load the projects. They need a restore (private feeds through the repository's own
     `nuget.config` and the runner's credentials) and an SDK that can build the target framework.
   - **→ The action runs `actions/setup-dotnet` with `global-json-file`**, then `whitespace` and `style` per solution.
     Lint mode adds `analyzers`.
2. **Nested `.editorconfig` and `root = true` are honoured, and `--include` is exact.** D9.1's allow-list therefore
   maps directly onto `--include <changed files>`.
3. **SDK noise: none for whitespace.** The real noise is elsewhere: **a repository without explicit settings is
   formatted to the defaults.**
   - Spark's root `.editorconfig` only sets `end_of_line` for generated files.
   - So 42 files indented with tabs are converted to spaces.
   - Object-initializer members are put on lines of their own (716 + 338 + 209 of the findings).
   - This is what FormatRepository is for (D4: one run after onboarding). For the pipeline step, D16 guards against it.

### S2 — Prettier and ESLint at the pinned version, scripts off

**Setup:** throwaway `node:24-bookworm` containers (Node 24.22.3), a shallow clone in a tmpfs, no repository mount.
**Spark itself has no Prettier or ESLint config**, so the measurements are on three outside repositories.

| | `vuejs/core` @44234e8 | `nrwl/nx-examples` @ff64e63 | `prettier/eslint-config-prettier` @bd6e617 |
|---|---|---|---|
| Package manager (`packageManager`) | pnpm 12.21 | yarn 4.15 | yarn 4.18 |
| Install with scripts off (`pnpm install --frozen-lockfile --ignore-scripts` / `yarn install --immutable --mode=skip-build`) | 84 s, ok | 140 s, ok | 85 s, ok |
| Prettier (resolved) / `--check .` | 3.10.1 / 32 s, 0 files | 2.6.2 / 4 s, 0 files | 3.9.6 / 6 s, 0 files |
| `.prettierignore` holds (no `node_modules`/`dist` paths listed) | yes | yes | yes |
| `npx prettier@<resolved>` **without** an install | 17 s, 0 files | 5 s, 0 files | **fails: "Cannot find package 'prettier-plugin-pkg'"** |
| ESLint (resolved) / `eslint .` | 10.15.0 / 14 s, clean | 9.39.5 / 2 s, 2 warnings | 9.26.0 / 9 s, 12 warnings |

The answers:

1. **Install, then the local binary.** `npx prettier@<version>` without an install is faster (17 s against 84 + 32 s),
   but it breaks as soon as `.prettierrc` names a plugin.
   - **→ The action always installs with scripts off**, using the lockfile's package manager through corepack
     (`npm ci --ignore-scripts`, `pnpm install --frozen-lockfile --ignore-scripts`,
     `yarn install --immutable --mode=skip-build`), then runs `npx --no-install`.
   - `.prettierignore` holds.
2. **JS configs and plugins work with scripts off** in all three. That includes `eslint.config.js`/`.mjs`,
   `@nx/eslint-plugin` and a Prettier plugin. No postinstall step was needed.
3. **ESLint in an Nx workspace needs no build:** 2 s on nx-examples.
   - **The project graph did fail:** `nx show projects` exits 1, because nx-examples' `@nx/dotnet` plugin needs
     `dotnet`. ESLint ran anyway.
   - A rule that reads the graph (`@nx/enforce-module-boundaries`) would need the plugins' toolchains on the runner.
     This was not measured: the root config does not use that rule.
   - **Nested configs:** nx-examples has 16 `eslint.config.mjs` files. From ESLint's documentation, not measured here:
     ESLint 9 takes the config from the working directory, so a root `eslint .` skips the project configs, and ESLint 10
     looks the config up per file.
   - **→ The action groups files by their nearest `eslint.config.*` and runs ESLint from that directory.** That is
     right on both 9 and 10.
4. Install time (85–140 s) is most of a JS run, and the runner's npm cache is the lever for it (`actions/setup-node`
   `cache:` keyed on the lockfile).

### S7 — detecting whitespace-only conflict hunks

**Measured:** 11 pairs of hunks were compared with Roslyn 4.14 `SyntaxFactory.ParseTokens` (a file-based app in the
scratchpad), comparing kind and text per token.

| Pair (ours / theirs) | Should be | "Equal without whitespace" | Roslyn tokens |
|---|---|---|---|
| re-indented; `Foo(a,b)` / `Foo( a, b )` | same | same | same |
| an initializer put on several lines | same | same | same |
| comment text `// a b` / `// a  b` | same | same | same (comments are trivia) |
| `"a b"` / `"a  b"`; `"a b"` / `"ab"` | **different** | **same: wrong** | different |
| verbatim string, interpolation hole `{a}` / `{ a }`, raw string re-indented, `' '` / `''` | **different** | **same: wrong** | different |
| `a + +b` / `a++b` | **different** | **same: wrong** | different |
| a hunk that **starts inside** a multi-line raw string | **different** | same: wrong | **same: wrong** |
| the previous case, lexing the **whole file** | different | — | different |

- **"Equal without whitespace" is wrong in 8 of 11 cases.** It may only act as a cheap pre-filter, never as the
  decision.
- **Roslyn tokens are exact once the whole file is lexed.** Lexing only the hunk fails when the hunk starts inside a
  literal.
- The resolver already has all three blobs: PREPARE is a single `git merge-tree --write-tree` (`MergeQueueResolver.cs:125`),
  and hunks come from `git merge-file --diff3` (`ConflictRepository.cs:92`). So it can build both whole-file
  resolutions (every hunk ours, every hunk theirs), lex both, and compare.
- **TypeScript and JavaScript:** the writer is C# with no Node, and the TS scanner needs Node. The writer could carry a
  hand-written JS lexer, but regex literals (`/a b/`) make one ambiguous. See Q16.
- **`git merge-tree -X ignore-space-change` (git 2.47.1): accepted, and dangerous.**
  - Setup: the target re-indents a line; the head changes spacing on that line **and** changes `"x y"` to `"x  y"` in
    the next line.
  - Result: exit 0, no conflict, and **the string change is lost silently**. The target's `"x y"` wins.
  - **→ Never use it.** D10 decides per hunk, as planned.

### S8 — the other formatters on real repositories

**Setup:** each run was in a throwaway container (`--tmpfs /work`, no repository mount) on a shallow clone at the
commit shown. Only the output folder was mounted.

| Repository | Pin found | Config found | Format result | Lint result |
|---|---|---|---|---|
| `spf13/cobra` @adbc881 (golang:1.25) | `go 1.15` in `go.mod`, no `toolchain` line | none; gofmt has no config | `gofmt -l .` 1 s, **0 files** | `go vet` 62 s, clean |
| `BurntSushi/ripgrep` @4a51093 (rust:1, rustc 1.100) | **none**: no `rust-toolchain*`; `rust-version` is the MSRV, not a pin | `rustfmt.toml` | `cargo fmt --all --check` 2 s, **0 files** | `cargo clippy --fix` 35 s: **48 files changed** (40× "doc list item without indentation", `matches!`, …) |
| `pydantic/pydantic` @5176478 | ruff **0.17.3** from `uv.lock` | `[tool.ruff]`, `[tool.ruff.format]` | `ruff format --check .` 1 s: **38 files**, nearly all Python code blocks inside Markdown (36 under `docs/`) | `ruff check .` 1 s, 2 fixable |
| `psf/black` @a12e959 | none (the project is black itself) | `[tool.black]` | `black --check .` 17 s, **0 files** (68 unchanged) | — |
| `terraform-aws-modules/terraform-aws-vpc` @577ad62 (terraform 1.13.5) | none at the root; `.pre-commit-config.yaml` pins the hook repository (v1.108.1), not terraform | none; `terraform fmt` has no config | `terraform fmt -check -recursive` 1 s, **0 files** | — |

The answers:

1. **The format tools are stable across versions:** gofmt, rustfmt, black and terraform fmt gave clean runs on
   well-kept repositories, even with a newer tool than the repository pins (Go 1.25 against `go 1.15`).
2. **Lint is not stable without a pinned toolchain.** clippy on the newest rustc found 48 files in a well-kept
   repository.
   - **→ Lint mode uses `rust-toolchain(.toml)` when present, otherwise stable.** The pull request body names the
     toolchain used.
   - The pipeline never lints (D10 formats only).
3. **The tool's scope is not the repository's scope.** `ruff format .` reaches Markdown code blocks that pydantic's own
   tooling never formats.
   - **→ The action never runs a tool on `.`.** It passes an explicit file list per tool, filtered by the tool's own
     extensions (`*.py`/`*.pyi` for ruff and black, `*.go`, `*.rs` via `cargo fmt -- <files>`, `*.tf`/`*.tfvars`),
     minus `exclude`.
   - In the pipeline, that list is the changed files (D9.1).
4. **Pin detection has these sources:**
   - Python: `uv.lock`, `poetry.lock`, `requirements*.txt`, then the `.pre-commit-config.yaml` `rev`;
   - Rust: `rust-toolchain(.toml)`;
   - Go: `go.mod` `toolchain`, then `go`;
   - Terraform: `.terraform-version`, then `required_version`, then the latest release.
   - The pull request body names every version used.
5. These five clones at these commits become the script's test fixtures (M1). They are trimmed copies, so the tests
   need no network.

### S10 — can the runner rebuild the writer's conflict hunks?

**Setup:**
- **The inputs:** 325 real (base, ours, theirs) triples of `.cs`/`.ts` files from this repository's history. For each
  file, the three newest versions v1 < v2 < v3 became base = v2, ours = v1, theirs = v3, so both sides change the same
  regions.
- **The git versions:**
  - the writer's own base image: `mcr.microsoft.com/dotnet/aspnet:11.0` + Debian git, **2.53.0**;
  - `ubuntu:24.04`, **2.43.0**;
  - `alpine:latest`, **2.54.1**;
  - local Windows, **2.47.1**.

| Call | Result |
|---|---|
| The writer's own `merge-file -p --diff3 --diff-algorithm=myers` (`ConflictRepository.cs:92`) | **byte-identical on 2.47.1, 2.53.0 and 2.54.1**: 325/325, of which 90 conflict |
| The same on 2.43.0 | **exit 129, a usage error**: `merge-file --diff-algorithm` arrived in git 2.44 |
| 2.43.0 **without** the flag, even with `-c diff.algorithm=histogram` | byte-identical to the writer on all 325: before 2.44, merge-file always uses myers |
| `merge-file -p --ours` and `--theirs` (whole-file resolutions) on 2.43.0, 2.47.1 and 2.53.0 | **byte-identical**: 650/650. `--ours` equals `--theirs` on exactly the 235 triples without a conflict |

The answers:

1. **The runner can rebuild the writer's result exactly.**
   - On git ≥ 2.44 it passes `--diff-algorithm=myers`; on older git it omits the flag (myers is fixed there).
   - The action fails with a clear message only below 2.43, the oldest version measured.
2. **What the runner needs:**
   - **The sides:** stage 2 is the base branch, stage 3 the pull request (`MergeTreeOutput.cs:7`), and
     `ConflictRepository` labels them ours and theirs. **So the head's side is `--theirs`**, and D10's resolution is
     `merge-file --theirs`.
   - **The inputs:**
     - `merge_base`: explicit for a backport, which takes the commit's own parent;
     - `ours_sha`: the target tip;
     - `theirs_sha`: the head, or the cherry-picked commit;
     - `paths`: the files the `whitespace` rule resolved.
   - Each is fetched with `git fetch --depth=1 origin <sha>`. GitHub serves reachable commits by id; the M6 live proof
     confirms this.

### S11 — "format both resolutions, compare" per formatter

**Setup:** each D13 formatter ran on pairs of whole files that differ in one way only, in throwaway containers.
"same" means the two formatted files are byte-identical, so the hunk counts as whitespace-only.

| Variant | dotnet format whitespace (SDK 11) | Prettier 3.10.1 | gofmt (Go 1.25) | rustfmt (stable) | ruff format | black | terraform fmt 1.13 |
|---|---|---|---|---|---|---|---|
| (a) spacing and indentation | same | same | same | same | same | same | same |
| (b) line wrapping | differ | same | differ | same | same | same | differ |
| (c) blank lines | differ | differ | differ | differ | differ | differ | differ |
| (d) comment spacing | differ | differ | differ | differ | differ | differ | differ |
| (e) **whitespace inside a string** | **differ** | **differ** | **differ** | **differ** | **differ** | **differ** | **differ** |

The answers:

1. **Correct where it matters:** (a) is accepted and (e) refused by all seven. There is no false accept.
2. **The false refusals are safe and expected:**
   - formatters keep blank lines and comment text, so (c) and (d) are refused everywhere;
   - wrapping (b) is accepted only by the reflowing formatters (Prettier, rustfmt, ruff, black);
   - every false refusal falls back to today's behaviour, evicting the pull request as an unresolved conflict.
3. **This makes Q16 A the decision (D17).** The proof uses the target's own formatter, needs no lexer in the writer, and
   works for every D13 language.

### S4 — reusing dispatch → artifact → GitWriter

- **No generic "apply a patch on base X, push branch Y" job exists.**
  - `PublishJob` (`CodeCoverage.GitWriter/Jobs/WriterService.cs:25`, `PublishAsync` :131-198) belongs to the merge queue.
    It rebuilds the squashed commit, requires it to equal `PreparedSha`, and pushes with a lease on the PR head.
  - The layout check is the dependency updater's alone (`DependencyPatchValidator.cs:251`).
  - The closest job is `BackportRegeneratedJob` (`BackportService.cs:19`, `RegeneratedAsync` :226-). It validates the
    patch, fetches the head, checks the tip against `ExpectedSha`, then applies with `WriterService.ApplyPatchAsync`
    (:251). But it folds the patch into the backport commit, and its endpoint accepts only backport heads
    (`VersioningEndpoints.cs:549`).
- **→ M5 adds a new writer job and endpoint, modelled on `RegeneratedAsync`.** It fetches the base, checks the tip,
  applies the patch, makes a child commit and pushes `coverage/format/<mode>`.
- **Dispatch, correlation and expiry already exist in `WorkflowHook`** (`WorkflowHooks/WorkflowHook.cs:212-290`):
  - stray runs on the ref are cancelled first;
  - a dispatch must return a run id, otherwise the outcome is `Uncorrelated`;
  - `PollRun` runs every 3 min, plus one `Timeout` per run;
  - the `workflow_run` webhook (`CiRunCompleted`) matches the run by run id (`BackportRegeneration.cs:62-77, 304-328`);
  - timeouts are gated by a generation number (`BackportRegeneration.cs:409`).
- **→ `FormatRun` stores the run id and a generation**, the same way.
- **The notifier call belongs in one finish method.** `BackportRegenerationEngine.FinishAsync` (:416) is the template:
  it is the single exit for success and failure. Nothing in CodeCoverage calls `ISparkNotifier` yet (M8).

### S5 — the template and its onboarding

- **Onboarding never copies a composite action.** It edits files in place on an `OverlayTree`
  (`OnboardingPlanner.cs:160-167, 348-366`), and templates call actions remotely, pinned to a commit:
  `uses: MintPlayer/MintPlayer.Spark/.github/actions/spark-regenerate@<sha>` (`regenerate.yml:65`).
- **Embedded templates:** the only one shipped today is `dependency-action/templates/*.yml`, with LogicalName
  `DependencyTemplates.*` (`CodeCoverage.csproj:33`), read by `DependencyOnboarding.ReadTemplate`. Those under
  `docs/code-coverage/templates/` are documentation only.
- **→ M2 embeds `formatting.yml` the same way**, with its own LogicalName.
- **Permissions:** the regenerate templates declare only `contents: read`. Uploading the `merge-queue-patch` artifact
  happens inside the composite action and needs nothing more (`spark-regenerate/action.yml:120-126`;
  `GitWriterSidecarClient.cs:52` expects that name). **→ The formatting template declares `contents: read`.**
- **Missing or changed workflow:** `MergeQueueEngine.BeforeFirstDispatchAsync` (:1364-1408) calls
  `VerifyProvenanceAsync`. `Missing` evicts with "workflow-missing"; `Differs` evicts with "workflow-differs". Backports
  use the same check (`BackportRegeneration.cs:311-319`). It checks that the file exists and matches byte for byte, not
  its version.
- **→ The on-demand actions refuse "set up Formatting first" with the same `Missing` result** (D1).

### S6 — the patch guard

- `PatchValidator.Validate(patch, regeneratedPaths, maxBytes)` (`PatchValidator.cs:76`) takes globs that already act as
  an allow-list.
- For an exact list of files, the internal `Parse(patch, pathPolicy, maxBytes, files)` (:87) takes a path rule. This is
  how `MigratePatchValidator` and `DependencyPatchValidator.cs:44` work.
- What `Parse` refuses (doc :49-57):
  - every mode except 100644, which rules out mode changes, symlinks, gitlinks and executables;
  - binary patches, via a NUL check;
  - copies.
- A rename is a delete plus an add, and **both** paths are checked (:53, 204-217).
- `PatchFile` reports `IsNew`, `IsDeleted` and `IsRename` (:290), so D15's "rewrite only" rule is a check over `files`.
- `IsSafePath` (:312) refuses `.git`, `.github`, `.gitmodules` and `.gitattributes`.
- **No validator takes an exclude list, and `PathGlob` has no negation.** → `exclude` is a second glob list in the path
  policy (D15).

### S9 — pipeline integration

- **PREPARE already records which rule resolved each file.** `entry.Resolutions` holds `Path`, `Strategy`, `Regenerate`
  and `Rule`, from `MergeQueueResolver` (`CodeCoverage.Git/MergeQueue/MergeQueueResolver.cs:205`; `Regenerate =
  rule.Regenerate ?? resolver.RegeneratesByDefault`).
- `MergeQueueWorkflowGuard.RegenerateWorkflows` (:91-101) maps them to `AutoResolve[Rule].Action`, and `DispatchAsync`
  runs those workflows one after another (`MergeQueueEngine.cs:1118, 1304-1357`).
- **→ A `whitespace` resolver with `RegeneratesByDefault = true`, plus the preset's `action: formatting.yml`, makes
  REGENERATE dispatch the format workflow with no new engine path.**
- Strategies are strings. A new one needs three things:
  - a constant in `MergeQueueWireRules.Strategies` (:36-49);
  - an `IConflictResolver`;
  - an entry in `ConflictResolverRegistry.BuiltInResolvers()`, which `ConflictResolverRegistryTests` keeps in step.
- **Backports:** `BackportRecipients.cs:292-303` calls `BackportRegenerationEngine.Plan` and then `BeginAsync`.
  `DispatchAsync` (`BackportRegeneration.cs:295`) checks provenance against the line, then dispatches. The same
  resolution record drives it (M7).
- **The changed-files allow-list (D9.1) and the inputs-changed skip (D9.3)** cannot come from PREPARE: its result holds
  only the `.github/**` paths and the resolved paths (`GitWriterSidecarClient.cs:367-371`). Both come from
  `IForgeLabels.ListPullRequestFilesAsync` (`IForgeLabels.cs:57`), which PR size labels and backport attention already
  use. The list is passed to the workflow as `paths`.
| D21 | **As built (M4–M7), five choices the decisions left open.** (1) The on-demand result goes through the git writer's existing `/jobs/onboarding` with a `format` field: the writer applies and checks the patch and force-pushes `mintplayer/format` or `mintplayer/lint`, so no new writer endpoint. (2) `exclude` globs are normalized once (`FormatPatchValidator.Glob`): a leading `./` is dropped, a glob without `/` gets `**/`, a trailing `/` gets `**`. (3) `ResolverVersion` is **not** bumped: no existing configuration's prepared tree changes, and a writer without the strategy refuses it as unknown. (4) In the pipeline the server does not apply `exclude` itself: the runner fails on an excluded candidate (D19), and the publish allow-list is only the concrete resolved paths, so an explicit `whitespace` rule never widens `regeneratedPaths` by its glob. (5) A backport proves a resolution only when nothing was normalized: the writer reports the line's tip, the picked commit and its parent only then, because a lockfile reset or a platform marker makes a synthetic commit that exists only in the writer. Otherwise the plan is refused and the pull request opens "Not regenerated". | M4–M7. Tests: `FormatWriterJobTests`, `FormatRunEngineTests`, `WhitespaceResolverTests`, `MergeQueueWhitespaceStrategyTests`, `BackportRegenerationEngineTests` (M7_*), `BackportTests` (proof assertions) |

## As built (2026-11-23)

| Milestone | Where |
|---|---|
| M1 runner | `.github/actions/format` (`action.yml`, `format.mjs`, `fetch-commits.sh`, `format.test.mjs`); template `apps/CodeCoverage/CodeCoverage/Formatting/formatting.yml` (D20) |
| M2 onboarding | `EOnboardingFeature.Formatting`, `OnboardingPlanner` |
| M3 actions | `FormatRepository`, `LintRepository` in `actions.json`, `security.json`, `translations.json` |
| M4/M5 run and pull request | `FormatRunEngine`, `FormatRuns` entity, `FormatRepositoryAction`, `FormattingWorkflow`, `FormatPatchValidator`, writer `FormatAsync` (D21.1) |
| M6 strategy | `WhitespaceResolver`, `AutoResolvePresets` (`formatting`), `MergeQueueYml`, `MergeQueueEngine.DispatchAsync` (D18 inputs) |
| M7 backports | `BackportService` (`Proof*`), `BackportRegeneration.Plan` (D21.5) |
| M8 notifications | `AddNotifications()`, the `notifications:users` binding |
| M9 docs | [`code-coverage/formatting.md`](code-coverage/formatting.md), `merge-queue.md`, `backports.md`, `onboarding.md`, `README.md` |

## Not decided yet

- Nothing. Q16 became D17 after S10 and S11. The options were:
  - **A:** the writer uses "equal without whitespace" only as a pre-filter. The **runner proves** it for every language:
    1. rebuild both whole-file resolutions with the same `git merge-file --diff3 --diff-algorithm=myers` on the three
       commits, passed as inputs;
    2. format both with the target's formatter;
    3. require the two results to be identical, or fail the run, which evicts the PR as an unresolved conflict does.
    - Formatters never change string contents, so the formatter's own rules decide what counts as whitespace.
  - **B:** Roslyn tokens in the writer for C# (exact), and a hand-written conservative lexer in the writer for TS/JS.
    It refuses any hunk whose differing whitespace sits inside a string, a template or a possible regex literal.
  - **C:** C# only in the first version; other types keep today's behaviour.

## Out of scope

- Other forges. The pull request building blocks are GitHub only, and onboarding already refuses the rest.
- Running any formatter on the server.
- Formatting on every push, or as a failing check.
- The notification UI and anonymous async notifications: #710 (D14).

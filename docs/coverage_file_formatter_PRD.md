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

## Not decided yet

- Nothing. Q15 is D16.

## Out of scope

- Other forges. The pull request building blocks are GitHub only, and onboarding already refuses the rest.
- Running any formatter on the server.
- Formatting on every push, or as a failing check.
- The notification UI and anonymous async notifications: #710 (D14).

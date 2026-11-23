# Formatting — repository actions and pipeline steps that format code by the repository's own settings

**Status:** Grilled 2026-11-23 (Q1–Q14 answered, see "Decisions"). Nothing implemented. **Paused** until #710 (one bell
UI + anonymous async notifications, D14) is released; this feature's run feedback builds on it. Spikes are in
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

## Not decided yet

- **On-demand patch guard (former Q4):** the proposed default is to refuse binary, rename, mode and symlink changes, and
  anything the `exclude` list covers. S6 confirms what `PatchValidator` already refuses.

## Out of scope

- Other forges. The pull request building blocks are GitHub only, and onboarding already refuses the rest.
- Running any formatter on the server.
- Formatting on every push, or as a failing check.
- The notification UI and anonymous async notifications: #710 (D14).

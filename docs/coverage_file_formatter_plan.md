# Formatting (#657) — implementation plan

**PRD:** [coverage_file_formatter_PRD.md](coverage_file_formatter_PRD.md) · **Branch:** `feat/issue657-file-formatter`
· **one pull request**

## Status

- **2026-11-23: research and grill done** (PRD D1–D14).
- **Paused:** #710 (one bell UI + anonymous async notifications, PRD D14) is built and released first. #657 then resumes with
  spikes S1, S2, S4–S9, then M1–M10.
- Tests run **once, at the end** (M10). No interim gate runs.

## Spikes

Each spike is time-boxed and answers one question. A spike that cannot answer its question within the box stops and
reports what it found. Each result goes into the PRD with its evidence.

### S1 — dotnet format in a runner (≤ 1.5 h)

1. What does `dotnet format <sln|csproj>` (whitespace + style) need: a restore, private feeds, time? What does
   `analyzers` need?
2. Does the run honour nested `.editorconfig` files and `root = true`? Is `--include <files>` exact (needed for D9)?
3. What diff noise comes from using a different SDK than `global.json` pins (10.0.x vs 11.0.x)?

### S2 — Prettier and ESLint at the pinned version, scripts off (≤ 2 h)

1. Compare `npm ci --ignore-scripts` + `npx prettier --write` with `npx prettier@<lockfile version>`: time and
   correctness. Does `.prettierignore` hold?
2. Do a JS config and plugins work with `--ignore-scripts`?
3. Run **`eslint --fix` on an Nx workspace (Spark itself)**: does `@nx/eslint-plugin` need a build or the project
   graph? How long does it take?

### S4 — Reusing dispatch → artifact → GitWriter for the on-demand run (≤ 1.5 h)

1. Is there a generic "apply this patch on base X, push branch Y" job, or is `PublishJob.Patch` tied to the
   dependency lease and layout check?
2. How does a completed run find its `FormatRun`, and how does a stuck run expire?
3. Where should `FormatRun` completion call `ISparkNotifier`?

### S5 — The workflow template and its onboarding (≤ 45 min)

1. Read how `EOnboardingFeature.SizeLabels`/`SdkFromGlobalJson` plan their files, then shape `Formatting` the same way.
2. Which permissions does the format run need? `contents: read`, `actions: read`?
3. How does the server detect that a repository's workflow is too old, or missing, so it can refuse clearly?

### S6 — The patch guard (≤ 30 min)

1. Can `PatchValidator` take a path allow-list (the changed files, D9)?
2. Which existing check refuses binary, rename, mode and symlink changes, and the `exclude` paths?

### S7 — Detecting whitespace-only conflict hunks, string literals included (≤ 2 h)

1. Measure "equal without whitespace" against a token-aware comparison: Roslyn's `SyntaxFactory.ParseTokens` for C#,
   the TypeScript scanner for TS/JS.
2. Can the comparison run in the git writer (C#, no Node), or does TS need a fallback that refuses any hunk whose
   differing whitespace is inside a string token?
3. Does `git merge-tree` accept `-X ignore-space-change`, and is that ever useful here? (Expected: not for D10, which
   decides per hunk.)

### S8 — The other formatters on real repositories (≤ 2 h)

Pick one real public repository each for Go, Rust, Python (ruff, and black), and Terraform. Check:
- that the pinned version is detected (`go.mod` toolchain, `rust-toolchain.toml`, `pyproject.toml`,
  `.terraform-version`)
- that the config is detected (`rustfmt.toml`, `[tool.ruff]`/`[tool.black]`)
- that a run is clean, and what diff it leaves

These are the script's test fixtures, which the "parsers need real fixtures" rule requires.

### S9 — Pipeline integration (≤ 1.5 h)

1. Where does PREPARE report "resolved under a `whitespace` rule", so that REGENERATE dispatches the format workflow?
2. How does the backport path run a line's workflow after the cherry-pick, so it can do the same?
3. How is the formatter-inputs-changed skip (D9.3) detected from the pull request's diff?

## Milestones

One commit per milestone. Paths are under `apps/CodeCoverage/`.

- **M1 — Runner piece.**
  - Add `.github/actions/format` (composite) and its script, covering:
    - picking tools per ecosystem (D13)
    - pinned versions
    - `paths` and `exclude`
    - `.github/**` excluded in the pipeline modes
    - output in `merge-queue-patch`
  - The script's tests use the S8 fixtures.
  - Add the `docs/code-coverage/templates/formatting.yml` template with the SECURITY header.
- **M2 — Onboarding.** Add `EOnboardingFeature.Formatting`, the planner files and the chooser checkbox.
- **M3 — Actions.**
  - Add `FormatRepository` and `LintRepository` to `actions.json`, `security.json` (+ `securityPosture.txt`) and
    `translations.json`.
  - Run `--spark-synchronize-model`, `--spark-synchronize-security` and `--spark-verify-security`, and regenerate the
    translation golden in the same commit.
- **M4 — `FormatRun` and dispatch.** Add the document and the action classes. They cover authorization, the
  workflow-present check, the D5 auto-queue prompt, the dispatch and the expiry sweep.
- **M5 — Result ingestion.**
  1. Download the artifact.
  2. Run the patch guard (S6).
  3. Commit through `BotPullRequestAsync` (workflow flag when `.github/**` changed, D7).
  4. Find or create the pull request on `coverage/format/<mode>`.
  5. Enqueue if chosen (D5).
  6. Notify the requester (D14).
- **M6 — `whitespace` strategy.** Add the `autoResolve` strategy, the `formatting` preset, detection per S7, and the
  REGENERATE dispatch of the format workflow with the changed-files allow-list (D9, D10).
- **M7 — Backports.** Run the line's format workflow after a whitespace-only cherry-pick resolution.
- **M8 — Notifications adoption.** Call `AddNotifications()` in CodeCoverage, with the `notifications:users` binding,
  the bell from the separate issue's work, and the `FormatRun` notifications.
- **M9 — Docs.** Add `docs/code-coverage/formatting.md`, update `merge-queue.md` (`whitespace` strategy), `backports.md`,
  `onboarding.md` and the `README.md` link, and fill in the PRD's "As built" section.
- **M10 — Tests, once.** Run the touched CodeCoverage.Tests classes (actions, ingestion, patch guard, the `whitespace`
  resolver, onboarding planner), the GitWriter tests and the script tests. Add CsrfSurfaceTests and the route snapshot
  only if an endpoint is added. The full suite runs on the PR.

## Risks

- **Lint in Nx workspaces** may need a build (S2).
- **String-literal whitespace** may be falsely treated as formatting (S7).
- **Repositories onboarded before the Formatting checkbox** need a feature pull request first. The actions refuse
  until it's merged.

# File formatter (#657) — implementation plan

**PRD:** [coverage_file_formatter_PRD.md](coverage_file_formatter_PRD.md) · **Branch:** `feat/issue657-file-formatter`
· **one pull request**

## Status

**2026-11-23: research done.** Three read-only investigations covered the PR-opening action precedent, repository
reading and execution, and formatter tools. Their findings are in the PRD.

**Next:**
1. the grill (Q1–Q7)
2. spikes S1–S6
3. M1–M8

Tests run **once, at the end** (M8). No interim gate runs.

## Spikes

Each spike is time-boxed and answers one question. A spike that cannot answer its question within the box stops and
reports what it found. Each result goes into the PRD's decision log with its evidence.

### S1 — dotnet format on one file in a runner (≤ 1.5 h)

Use a scratch repository, and the Spark repository read-only. Measure:
1. Does `dotnet format whitespace --folder --include <file>` run without a restore, and does it honour a nested
   `.editorconfig` and `root = true`?
2. What does `dotnet format <sln|csproj> --include <file>` (whitespace + style) need: a restore, private feeds, time?
   And `--severity`/`analyzers`?
3. Does `--include` keep the diff to that one file, including the IDE0005/using-order fixes?
4. What diff noise comes from using a different SDK than `global.json` pins? Compare 10.0.x and 11.0.x on the same
   file.

Decides Q5 and the D1 command line.

### S2 — Prettier at the repository's pinned version, scripts off (≤ 1 h)

1. Resolve the version from the lockfile, then compare:
   - `npm ci --ignore-scripts` + `npx prettier --write <file>`
   - `npx prettier@<version>` without installing
   Measure time and correctness.
2. A JS config (`prettier.config.mjs`) and `plugins` need the workspace's `node_modules`. Does `--ignore-scripts` break
   any common plugin?
3. Monorepo: does the nearest `.prettierrc`/`.prettierignore` from the file win? Does the bot honour
   `.prettierignore` (refuse an ignored file)?

### S3 — Invoking the action from the custom file page (≤ 1 h)

The file page (`ClientApp/src/app/pages/file/file.component.ts`) is not a persistent-object page. Options to settle:
- Can ng-spark execute a custom action on a Repository persistent object with parameters (path, sha) from custom code?
  How do `ConvertCentralPackages`' `manager.Retry.Action` and the action's parameters reach the server?
- Or does it need a transient persistent object (`FormatRequest`)?
- A plain controller endpoint is the last resort, because it bypasses the Spark action grants and needs CsrfSurfaceTests
  and the route snapshot.

Decides Q7's mechanics.

### S4 — Reusing the dispatch → artifact → GitWriter chain (≤ 1.5 h)

Read `DependencyUpdateEngine` (`:343,406,678,917`), `WorkflowHook.cs:269,353`, `PatchValidator`,
`WriterService.Dependencies.cs:69` and `GitWriterSidecarClient`, then answer:
1. Is there a generic "apply this patch on base X, push branch Y" GitWriter job, or is `PublishJob.Patch` tied to the
   dependency lease and layout check?
2. How does a run's result find its pending request? Today that's the `pending.RunId`/artifact. What does a format
   request store (a new document such as `FormatRun`?), and how does a stuck run expire?
3. Can a notification reach the requesting user after the HTTP request ended (D2/Q6)?

### S5 — The workflow template and capabilities gating (≤ 45 min)

1. Do repositories onboarded with `dependency-action/templates` pass a free `mode` input through to the action? Or does
   each repository's workflow file need an edit (a new `workflow_dispatch` input and an onboarding upgrade PR)?
2. How does the bot read `# capabilities:` at the pinned SHA (`DependencySdkPreset`)? Add `format` the same way, and
   refuse with a clear message below it.
3. Which token permissions does the format run need? It only reads the repository and uploads an artifact.

### S6 — Patch guard (≤ 30 min)

Can `PatchValidator` be given an allow-list of paths (Q4)? Which existing check refuses binary, rename, mode and
symlink changes, so a formatter patch can't carry them?

## Milestones

One commit per milestone. Paths are under `apps/CodeCoverage/`.

- **M1 — Action declaration.**
  - Add `FormatFile` to `App_Data/actions.json`, `security.json` (+ `securityPosture.txt`) and `translations.json`.
  - Run `--spark-synchronize-model`, `--spark-synchronize-security` and `--spark-verify-security`.
  - Regenerate the translation golden: `SPARK_UPDATE_GOLDEN=1 … SparkTranslationLayersTests`, in the same commit.
- **M2 — Request domain.** Add the `FormatRun` document (repository, base branch, base sha, paths, requester, run id,
  state, expiry), and the action class `CustomActions/FormatFileAction.cs`. The action covers:
  - authorization (D3)
  - the capability check (S5)
  - validating the paths against the tree (`IRepositoryTree`), refusing ignored, binary and oversized files
  - the dispatch
- **M3 — `format` mode in `dependency-action`.**
  - Add `src/format.ts`, built on the S1/S2 command lines.
  - Write `format.patch` + `format-result.json` (formatter, version, config files, "no change").
  - Add `# capabilities: format`, and unit tests next to the existing mode tests.
  - Rebuild `dist` **in the main checkout**.
- **M4 — Workflow template.** Pass the new inputs through (S5). If existing repositories need a workflow edit, add the
  onboarding upgrade the same way earlier template changes were rolled out.
- **M5 — Result ingestion.**
  1. The `workflow_run`/poll path downloads the artifact.
  2. The patch guard (S6) checks it.
  3. GitWriter commits it on `coverage/format/<hash>`.
  4. The pull request is found or created (D4).
  5. `FormatRun` records the outcome, and an expiry sweep closes stuck runs.
- **M6 — UI.** Add a "Format file" button on the file page for a repository the user manages, wired the way S3 decides.
  Show pending/done state if Q6 asks for it.
- **M7 — Docs.** Add `docs/code-coverage/file-formatter.md`, a link from `docs/code-coverage/README.md`, and the
  `dependency-action/README.md` `format` mode section. Fill in the PRD decision log and "As built".
- **M8 — Tests, once.**
  - `FormatFileActionTests` (on the `ConvertCentralPackagesActionTests`/`DependencyHarness` pattern)
  - ingestion and patch-guard tests
  - the action's vitest specs
  - the ClientApp spec for the button
  - plus CsrfSurfaceTests and the route snapshot if S3 ends in an endpoint

  Run only the touched classes locally. The full suite runs on the PR.

## Risks

- **Older templates.** Existing onboarded repositories may need a workflow file edit before `format` works (S5).
- **Large analyzer-heavy solutions.** `dotnet format style` on these is slow in a runner (S1). Proposed bound: the run's
  own timeout, and the format run reports "timed out" through `FormatRun`.
- **Repositories without the dependency workflow** can't use the action at all, unless Q2 adds a fallback.

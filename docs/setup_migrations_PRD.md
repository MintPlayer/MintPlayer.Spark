# #658: setup migrations (PRD + plan)

Issue: [#658](https://github.com/MintPlayer/MintPlayer.Spark/issues/658). Branch: `feat/issue658-setup-migrations`.
Area: CodeCoverage (production): onboarding, dependency updater, dependency action.

## 1. Problem

**P1. The apply job installs the SDK before the plan moves it.** SpaServices #111 (2026-11-13, after the #656 deploy):
`platform:dotnet 11.0` was not applied, "A compatible .NET SDK was not found. Requested SDK version: 11.0.100 …
Installed SDKs: … 10.0.114". The template runs `actions/setup-dotnet` with `global-json-file: global.json`
(`dependency-action/templates/dependency-apply.yml:55-58`) **before** the action. At that point `global.json` still
names the 11.0 preview. The platform group then moves it to 11.0.100 (#617, squash `dce3c6725`, the action SHA
SpaServices pins), and the restore asks for an SDK nobody installed. `platform:node` has the same latent defect: the
template installs no Node at all.

**P2. A generated file cannot be changed safely in an onboarded repository.**
- The `workflow-outdated` path opens "Update the MintPlayer dependency workflows" and replaces the whole file: "Changes
  you made to the file are replaced too" (`Dependencies/Lifecycle/DependencyOnboarding.cs:54`;
  `DependencyUpdateEngine.cs:363-372`).
- Re-running **Set up features** overwrites the dependency workflows with no check for an existing file
  (`DependencyOnboarding.cs:154`).
- `coverage.yml` records no version of the generated setup.

**Owner (2026-11-13):**
- "do you think we should work out a migration-like system (like "nx migrate") where the version is stored in the
  coverage.yml file, and when changes have to be applied to the repo setup for the coverage support, the bot applies
  them, subsequent changes by the user won't be reverted then".
- "Migration system with B as first migration".
- Also: "determine whether we can let the coverage-setup-repository-migrations integrate properly with for example the
  NX migrations (I have several nx monorepos)".
- Standing constraint: "forcing everyone to use the global.json is not preferred" (earlier, #649).

## 2. Findings

Four read-only investigations (2026-11-13). Paths are under `apps/CodeCoverage/`. ✔ = re-checked by hand.

| # | Finding | Evidence |
|---|---|---|
| F1 | The apply edits and resolves **one group at a time**: per group it snapshots, edits (platform first, then each adapter's pin, edit, relock, verify), parses blockers and resets on failure. Each group builds on the previous one's tree. A failed consistency check bisects by re-running edit and resolve on prefixes of the plan. "Edit everything, then resolve" (the literal B) breaks the snapshots, the per-group blockers and `files`, and bisection. | `dependency-action/src/apply/orchestrator.ts:86-156, 166-170, 173-231` |
| F2 | ✔ Platform groups are ordered **last**, so package groups resolve on the old SDK. Only the platform group, and the companion packages that ride in it, need the new one. A failed or bisected-out platform group is reset to the old `global.json`, so the job needs **both** SDKs. | `CodeCoverage/Dependencies/Policy/DependencyPolicyEngine.cs:667`; also `DependencyFixPins.cs:112` |
| F3 | The action has one `mode` input (lookup, apply, migrate) and one output (`output-dir`). It installs no toolchain itself; it only checks the tools exist. The plan file is readable from the checked-out ref (`readPlan`). | `dependency-action/action.yml:9`, `src/main.ts:66-111`, `src/apply/apply.ts:24`, `orchestrator.ts:101-106` |
| F4 | ✔ Re-running Set up features overwrites `dependency-lookup.yml`, `dependency-apply.yml`, `angular-migrate.yml` and `nx-migrate.yml` with the pinned template. The templates are embedded resources from `dependency-action/templates/*.yml`. | `DependencyOnboarding.cs:119-154`, `CodeCoverage.csproj:33` |
| F5 | Targeted edits that keep the rest of a user's file already exist: `GlobalJsonSdk` and `LineNarrowing` (regex line edits), and `ReleaseLineTriggers` (finds the spot with YamlDotNet, then edits the text). "Skipped with a note" goes into a `List<string> notes`, which the PR body renders as "To do by hand" checkboxes. | `Onboarding/GlobalJsonSdk.cs:30-35`, `LineNarrowing.cs:25-42`, `ReleaseLineTriggers.cs:26-80`, `OnboardingPlanner.cs:369-375` |
| F6 | `coverage.yml` has no single model. Each section has its own reader (`CoverageYml`, `VersioningYml`, `MergeQueueYml`, `BackportYml`, `DependenciesYml`), and none has a version. Sections are inserted as text, in alphabetical order (`CoverageYmlSections.Add`). `AddKey` returns null on a flow-style section. | `Feedback/CoverageYmlSections.cs:34-112` |
| F7 | Onboarding PR machinery that a migration PR can reuse: the planner over an `OverlayTree` (unchanged files are dropped), the push through the git writer with a fixed author date (a retried force-push is a no-op), find/create/update of the PR, and a rebuild on every new default-branch head. | `Onboarding/OnboardingPlanner.cs:118-167`, `OverlayTree.cs:51`, `OnboardingService.cs:121-132, 179-236`, `OnboardingRecipients.cs:20-52` |
| F8 | The git writer accepts only a fixed list of onboarding paths. | `CodeCoverage.GitWriter/Endpoints/DependencyEndpoints.cs:303` (`IsOnboardingPath`) |
| F9 | Pinned action SHAs (`# dependency-update-v1`, `versioning-v1`) are already moved by the dependency updater. That updates the action's **code**, never the workflow text. `IsBotRuntime` skips the cooldown. | `DependencyPolicyEngine.cs:762-770`, `PackageVersioning/VersioningOnboarding.cs:35`, `docs/code-coverage/dependency-updates.md:66-68, 242` |
| F10 | Hooks that already run per repository: push to the default branch (`OnboardingRebuildEventsRecipient`, `VersioningDispatchEventsRecipient`), and the dependency cron every 10 minutes with per-repository `Scheduled` triggers. The dependency run already loads and checks the workflow files (`workflow-outdated`). | `OnboardingRecipients.cs:21-36`, `DependencyUpdatesCronJob.cs:30,83`, `DependencyUpdateEngine.cs:363-372` |
| F11 | Nx today: `nx migrate` runs only for a `migrate:` rule in `coverage.yml` on an approved major. The workflow is `nx-migrate.yml` or `angular-migrate.yml`, dispatched per group. It runs install, `nx migrate --from … --include=all`, `--run-migrations`, then relock, deletes `migrations.json`, and the patch joins the dependency PR. The validator **refuses `.github/**` and `coverage.yml`** in a migrate patch, and allows `package.json`, `nx.json` and `project.json`. A repository is treated as an Nx workspace when it has any `nx.json`; no Nx version is tracked. | `DependencyPolicyEngine.cs:454-461`, `DependencyUpdateEngine.cs:810-919`, `dependency-action/src/migrate.ts:75-173`, `MigratePatchValidator.cs:7-16`, `DependencyOnboarding.cs:128` |
| F12 | Earlier choice: #613 item 4 chose replace-and-warn for outdated workflows because SpaServices and DependencyUpdaterTest were stuck. `coverage_backport_PRD.md:1082` notes that each feature "detects already onboarded differently". | `DependencyUpdateEngine.cs:368-369`, `docs/coverage_backport_PRD.md:1082` |

## 3. Design (proposal)

**Version.** `coverage.yml` gets a top-level `setup: <n>` (Q1). Onboarding writes the current number. A repository
without the key is at 0 (everything onboarded before this change).

**Migration.** A C# class per step:
```csharp
interface ISetupMigration
{
    int Version { get; }          // 1, 2, …; the registry is ordered and gap-free (a test)
    string Title { get; }         // one line in the PR body
    Task ApplyAsync(OverlayTree tree, SetupContext context, List<string> notes);
}
```
- Each migration is **idempotent**: it detects its own result and does nothing on a file that already has it.
- It **edits the text** at an anchor and leaves the rest of the file alone, the `GlobalJsonSdk`/`ReleaseLineTriggers`
  way (F5). It never re-serialises YAML and never rewrites from the template.
- An anchor it cannot find gives a note ("To do by hand", F5) and no edit. The version still advances, so the note is
  the record.
- A migration that concerns a feature the repository does not use (no `dependency-apply.yml`) does nothing.

**The pull request.**
- Branch `mintplayer/setup`, title "Update the CodeCoverage setup to <n>". One commit holds the migrations and the
  bumped `setup:`, so merging it is what records the version.
- Rebuilt from scratch on every new default-branch head, like onboarding (F7). It never conflicts and always applies
  on top of whatever merged, Nx migrations included.
- Never auto-merges. The body lists each migration with its title, the files it changed, and its notes.

**Trigger.** The dependency run (F10) compares `setup:` with the registry's highest version and opens or rebuilds the
PR. The push-to-default-branch rebuild covers an open PR. The `workflow-outdated` full replacement (F12) is retired
in favour of migrations (Q2).

**Re-running Set up features.** It writes a generated workflow only when the file is missing (F4 → fixed), and stamps
`setup:` at the current version. It writes no stamp when the PR leaves an existing older file alone; the migrations
cover that file.

**Migration 1: install the planned toolchain before the apply** (B, refined by F1-F3).
- A new action mode, `prepare`, reads the plan and outputs the SDKs the platform group moves to (`dotnet-sdks`) and the
  Node version (`node-version`). It makes no edits.
- In `dependency-apply.yml`, the existing `setup-dotnet` step keeps `global-json-file: global.json` and gains
  `dotnet-version: ${{ steps.prepare.outputs.dotnet-sdks }}`. Both SDKs are then installed, and `global.json` picks the
  right one before and after each edit, reset and bisect (F2). A `setup-node` step follows when `node-version` is set
  (SP5).
- The `apply` step is unchanged, and `ApplyResult` and the patch format stay the same.
- The template gets the same steps, so a new onboarding is already at 1.

**Nx integration.**
- Setup migrations stay **bot-driven and in their own PR**. They are not shipped as Nx `migrations.json` entries
  (rejected: such entries need a published npm package every repository depends on, run only on an approved Nx major,
  cannot touch `.github/**` (F11), and do nothing for non-Nx and .NET-only repositories).
- Collisions are structural, not timing:
  - Nx migrate patches cannot touch `.github/**` or `coverage.yml` (F11), and migrations 1..n touch only those, so the
    two edit disjoint files.
  - A later setup migration that edits `nx.json` (for example `sharedGlobals`) does so through an idempotent JSON-path
    edit. Because the setup PR is rebuilt on each default-branch head, it applies on top of a merged Nx migration.
- A migration that depends on the Nx version (an `nx affected` flag that changed between majors) reads it from the
  lockfile through `SetupContext` (SP8).

## 4. Open questions (owner)

| # | Question | Recommendation |
|---|---|---|
| Q1 | One `setup:` number for the whole repository, or one per feature (`dependencies.setup`, `versioning.setup`, …)? | **One number.** Migrations skip features the repository does not use, and features onboarded later come in at the current template. Per-feature numbers multiply the bookkeeping, and every migration would still need its own detection. Trap: a migration for feature X that ships while X is not onboarded advances the number without touching X. That is harmless only because X is then onboarded from the current template. |
| Q2 | Retire `workflow-outdated`'s full replacement? | **Yes:** a missing input becomes a migration. Keep the replacement only for a file the bot cannot parse at all, behind a note. |
| Q3 | Until migration 1 is merged, should a platform group that moves the SDK be held ("needs setup 1") instead of failing in the apply? | **Yes:** a hold whose reason names the open setup PR, lifted when `setup:` ≥ 1. Without it, every run spends an apply that is bound to fail, as on #111. |
| Q4 | Should the first setup PR on a repository be a draft, like the first dependency PR? | **No:** it changes only the bot's own files and never auto-merges. |

## 5. Spikes

| # | Question | How | Result |
|---|---|---|---|
| SP1 | Does `actions/setup-dotnet@v6` install **both** `global-json-file` and `dotnet-version` when both are given, and is an empty `dotnet-version` (no platform move) accepted? | Read the action's source (`installer`/`setup` entry) at the tag; if unclear, one run in a scratch repository (needs the owner's go-ahead: a CI run). | Open |
| SP2 | With both a preview and the GA SDK installed, does `global.json` pick the right one before the edit (preview, `rollForward` as written) and after it (GA)? Covers the reset back to the preview. | `dotnet-install` of 11.0.100-preview.N and 11.0.100 into a temp dir in a Linux container; `dotnet --version` under each `global.json`. | Open |
| SP3 | What do onboarded repositories' `dependency-apply.yml` files look like today: still the template, edited, or older template generations? This sizes the anchor rules for migration 1. | Read the owner's onboarded repositories' workflow files through the GitHub API (read-only). The repository list comes from the owner or from production (needs go-ahead). | Open |
| SP4 | Where `setup:` goes and how it is read: a new reader beside `CoverageYml`, flow-style files, and a missing `coverage.yml`. | Read `CoverageYmlSections`, `CoverageYml`; red tests on the fixtures. | Open |
| SP5 | Does a `platform:node` move need the new Node during the apply (npm/pnpm lockfile resolution under `engines`, `.nvmrc`), and does the action's own runtime (node24) conflict with a `setup-node` step? | Read `applyPlatform.ts` (node) and the js adapter; a container run of `npm install --package-lock-only` under two Node versions. | Open |
| SP6 | Can migration 1 recognise its own result and every earlier template generation? Its anchor is the `actions/setup-dotnet` step before the dependency action; a missing step means a repository without .NET. | Unit tests over the SP3 shapes and the template history (`git log -- dependency-action/templates/dependency-apply.yml`). | Open |
| SP7 | How does the git writer's path allow-list (F8) take migration paths without opening it to arbitrary files? | Read `IsOnboardingPath` and its tests. | Open |
| SP8 | Is the Nx version needed by any migration we can foresee, and can `SetupContext` read it from `package-lock.json`/`pnpm-lock.yaml` cheaply? | Read the lockfile parsers the lookup already uses. | Open |

## 6. Milestones (one PR)

- **M0** ✅ Issue #658, branch, this PRD (four investigations, F1-F12).
- **M1** Spikes SP1, SP2, SP4, SP5, SP7 (SP3 with the owner's go-ahead); the owner answers Q1-Q4.
- **M2** Action `mode: prepare` (TypeScript): outputs `dotnet-sdks` and `node-version` from the plan. Red tests first
  (`src/prepare.test.ts`: no platform group, a dotnet move, a node move). Template `dependency-apply.yml` with the new
  steps. Rebuild `dist` in the main checkout (CLAUDE.md, ncc).
- **M3** Framework (C#): `ISetupMigration`, the registry (gap-free test), the `setup:` reader and writer, onboarding
  stamping, and Set up features writing a workflow only when it is missing. Red tests first.
- **M4** Runner: the dependency run opens or rebuilds `mintplayer/setup`, the default-branch rebuild covers it, the PR
  body lists titles, files and notes, and the git writer allow-list is widened. Q3's hold if the owner agrees.
- **M5** Migration 1, with tests on the template's generations and on user-edited shapes (SP3, SP6): extra steps, a
  renamed step, comments, a matrix, and no .NET.
- **M6** Retire `workflow-outdated`'s full replacement (Q2).
- **M7** Docs: `docs/code-coverage/setup-migrations.md` (how a repository is migrated, how to write a migration),
  `onboarding.md` (re-run behaviour), `dependency-updates.md` (the prepare step, the hold).
- **Gate:** the affected test classes once at the end, then the PR. After the deploy, SpaServices gets "Update the
  CodeCoverage setup to 1". Once it is merged, `platform:dotnet 11.0` applies on #111.

## 7. Log

- 2026-11-13: P1 diagnosed on SpaServices #111 after the #656 deploy (the platform group was refused: SDK 11.0.100 not
  installed). Owner chose the migration system with B as its first migration and asked for Nx integration. Four
  read-only investigations: F1-F12. B refined from "split edit/resolve" (breaks per-group apply, F1) to
  "prepare + both SDKs" (F2). F2 and F4 re-checked by hand. Issue #658, branch, this PRD.

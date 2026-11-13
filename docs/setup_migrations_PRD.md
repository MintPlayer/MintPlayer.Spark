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

## 3. Design (decided)

### 3a. P1: the plan commit names the planned SDK

**Decision D1 (owner, 2026-11-13: "I'll follow your suggestion").** The bot's plan commit also moves the root
`global.json`'s `sdk.version` to the SDK the plan's .NET platform group moves to. No workflow changes, so no onboarded
repository has to merge anything first.
- Why it works: `dependency-apply.yml` checks out the dispatched commit (`ref: ${{ github.sha }}`, the plan commit) and
  `setup-dotnet` reads `global-json-file: global.json` from it (`dependency-action/templates/dependency-apply.yml:47-58`).
  The job then installs the SDK the platform group needs.
- It supersedes the earlier proposal (a `mode: prepare` that outputs both SDKs, and a workflow migration to call it). That
  needed a setup migration merged in every repository before the bug was fixed anywhere; this fixes every onboarded
  repository at the next deploy.

**D2. Which plans pre-set, and which pins.** Only when the plan has **exactly one** .NET platform group with an
`sdkVersion`, **unscoped** (no `locations`/`exclude`), and the root `global.json` has an `sdk.version` **on the group's
from-channel** that the target is **newer** than (semver precedence). Those are exactly the pins the platform group moves
itself (`applyPlatform.ts:136-140`, `:199`, `:210`). Every other case writes the plan file only, as before. A
`global.json` with a BOM, or one whose text does not round-trip byte for byte, is left alone.
Code: `CodeCoverage.GitWriter/Jobs/Dependencies/GlobalJsonPreset.cs`, called from `WriterService.Dependencies.cs`
(`DependencyWorkRefAsync`).

**D3. The provenance rule accepts exactly that change.** `PlanCommitRule` (bot side) and `readDispatch` (runner side,
`dependency-action/src/apply/apply.ts`) accept the plan file plus the root `global.json`, modified (never added),
regular, and equal to "the old text with `sdk.version` set to the plan's SDK". Any other change to it, any other path, a
nested `global.json` or a plan without the group is refused as before. Tests: `GlobalJsonPresetTests` (7),
`apply.test.ts` "#658" (4).

**D4. The action keeps the patch and the per-group results as they were.**
- Every group except the platform group, every reset and every bisect round sees the pre-set (`resetAll` goes back to
  `HEAD`). So package groups and the consistency check run on the new SDK, which is the only one installed.
- The platform group first writes the `expected_sha` text back, then edits as it always did. Its `platform.edited` and
  `files` therefore name `global.json` as before (`orchestrator.ts`, `applyGroup`).
- A platform group that does not end applied (refused, failed, bisected out) leaves `global.json` out of the patch:
  `runApply` writes the `expected_sha` text back before the diff.
- Test (the "little test" the owner asked for): `apply.test.ts` runs the full `runApply` on a real git repository shaped
  like SpaServices #111 (`11.0.100-rc.2…` → `11.0.100`, CRLF): applied, `files: ['global.json']`, the patch moves the
  version. A group that fails leaves `global.json` byte for byte at the preview, and out of the patch.

**D5. No hold when `setup-dotnet` cannot install the planned SDK.** The SDK version comes from the release index
(`DependencyPolicyEngine.cs:644`, `SdkVersion = target.LatestSdk`), so it exists to install. A failed apply run already
ends the run with the run's link (`DependencyUpdateEngine.cs:431-434`), and the next run plans again. A hold would add
state for a case with no known occurrence.

**D8. Only for a repository whose pinned action reads the pre-set.** Repositories run the action at the SHA their
`dependency-apply.yml` pins (`…/dependency-action@<sha> # dependency-update-v1`). An action from before #658 refuses a
plan commit with a second path (`apply.ts` `readPlan`, "must change … only"), so a pre-set sent to it would fail the
whole apply, not just the platform group. The action's `action.yml` therefore declares `# capabilities: sdk-preset`
(a YAML comment: no effect on the runner). Before the plan commit the bot reads the apply workflow's pinned ref and the
`action.yml` at that ref (the same `GetRepositoryFileAsync` it uses for R15), and sets `PresetSdk` on the work-ref job
only when it is declared (`DependencySdkPreset.cs`, `DependencyUpdateEngine.SdkPresetSupportedAsync`).
- Rollout: the merge moves `dependency-update-v1` (`dependency-action-publish.yml`). The updater then moves each
  repository's pin like any action digest. Until that pin is merged, the platform group fails as before; from the next
  run on it applies. On SpaServices #111 that is one extra run.
- Tests: `DependencySdkPresetTests` (the pin, the declaration, and that this repository's `action.yml` declares it);
  `DependencyPreviewToReleaseEndToEndTests` (declared → `PresetSdk`, not declared → plan file only);
  `DependencyWriterJobTests.Issue658_…` (without the flag the commit holds the plan file only).

**Open, not in this PR: `platform:node`.** The template installs no Node at all, so a Node move resolves under the
runner's default Node. No repository has hit it. It needs its own analysis (`engines`, `.nvmrc`, the action's own node24
runtime).

### 3b. P2: generated files and user edits

**D6. Re-running Set up features never overwrites an existing dependency workflow** (F4). `DependencyOnboarding.Plan`
writes a template only where the repository has no such file. That keeps a user's edits. An outdated workflow is the
`workflow-outdated` pull request's job ("Update the MintPlayer dependency workflows"), which reviews as its own pull
request and says that it replaces the file. Test: `Issue658_an_existing_workflow_is_never_overwritten`.

**D7. The setup-migration framework is deferred until there is a migration to ship.** The first migration was the reason
to build it now. D1 fixes P1 with no workflow change, so the framework would ship with an empty registry. Its runner
(the `mintplayer/setup` pull request, `setup:` in `coverage.yml`, the git writer's allow-list) would go to production
untested against a real change. The design below stays the plan for the first change to a generated workflow that a
user's edits must survive. Q1-Q4 move with it.

### 3c. The framework, for the first real migration (not built)

**Version.** `coverage.yml` gets a top-level `setup: <n>` (Q1). Onboarding writes the current number. A repository
without the key is at 0.

**Migration.** A C# class per step:
```csharp
interface ISetupMigration
{
    int Version { get; }          // 1, 2, …; the registry is ordered and gap-free (a test)
    string Title { get; }         // one line in the PR body
    Task ApplyAsync(OverlayTree tree, SetupContext context, List<string> notes);
}
```
- Each migration is **idempotent**: it recognises its own result and does nothing on a file that already has it.
- It **edits the text** at an anchor and leaves the rest of the file alone, the `GlobalJsonSdk`/`ReleaseLineTriggers`
  way (F5). It never re-serialises YAML and never rewrites from the template.
- An anchor it cannot find gives a note ("To do by hand", F5) and no edit. The version still advances.
- A migration for a feature the repository does not use does nothing.

**The pull request.** Branch `mintplayer/setup`, "Update the CodeCoverage setup to <n>": one commit with the migrations
and the bumped `setup:`. It is rebuilt on every default-branch head like onboarding (F7) and never auto-merges.

**Trigger.** The dependency run (F10) compares `setup:` with the registry. The `workflow-outdated` full replacement is
retired in favour of migrations (Q2).

**Nx integration.**
- Setup migrations stay **bot-driven, in their own PR**, not Nx `migrations.json` entries. Such entries would need a
  published npm package every repository depends on. They run only on an approved Nx major, cannot touch `.github/**`
  (F11), and do nothing for non-Nx and .NET-only repositories.
- Nx migrate patches cannot touch `.github/**` or `coverage.yml` (F11). Setup migrations touch only those, so the two
  never edit the same file. A later migration that edits `nx.json` does so as an idempotent JSON-path edit, on a branch
  rebuilt on top of whatever merged.

## 4. Open questions (owner): for the framework (D7)

| # | Question | Recommendation |
|---|---|---|
| Q1 | One `setup:` number for the whole repository, or one per feature? | **One number.** Migrations skip features the repository does not use. |
| Q2 | Retire `workflow-outdated`'s full replacement? | **Yes,** once the framework exists: a missing input becomes a migration. |
| Q3 | ~~Hold the platform group until migration 1 is merged?~~ | Obsolete: D1 needs no migration. |
| Q4 | Should the first setup PR be a draft? | **No:** it changes only the bot's own files and never auto-merges. |

## 5. Spikes

| # | Question | Result |
|---|---|---|
| SP1 | Does `setup-dotnet` install both `global-json-file` and `dotnet-version`? | **Obsolete** (D1 needs one SDK). |
| SP2 | Does `global.json` pick the right one of two installed SDKs? | **Obsolete** (D1). |
| SP3 | What do onboarded repositories' `dependency-apply.yml` look like? | Deferred with the framework (D7). |
| SP4 | Where `setup:` goes and how it is read. | Deferred with the framework (D7). |
| SP5 | Does a `platform:node` move need the new Node during the apply? | Open, outside this PR (§3a). |
| SP6 | Can a workflow migration recognise every template generation? | Deferred with the framework (D7). |
| SP7 | How does the git writer's allow-list take migration paths? | Deferred with the framework (D7). |
| SP8 | Does a migration need the Nx version? | Deferred with the framework (D7). |
| SP9 | Does the pre-set survive the real apply: per-group snapshots, a failed group's reset, the patch against `expected_sha`? | **Yes.** `runApply` on real git (`apply.test.ts` "#658"): applied → `files: ['global.json']`, patch `11.0.100-rc.2…` → `11.0.100`. Failed → `global.json` back at the preview and out of the patch. |
| SP10 | Does the git writer's commit keep the file's bytes (CRLF) and pass its own rule? | **Yes.** `Issue658_the_plan_commit_moves_the_root_global_json_to_the_planned_sdk` (writer job on a served repository, CRLF file). |

## 6. Milestones (one PR)

- **M0** ✅ Issue #658, branch, PRD (four investigations, F1-F12).
- **M1** ✅ Design settled with the owner: D1 replaces the `prepare` + migration design (§3a).
- **M2** ✅ Bot: `GlobalJsonPreset` and the plan commit (`WriterService.Dependencies.cs`); `PlanCommitRule` accepts exactly
  the pre-set. Tests: `GlobalJsonPresetTests`, `DependencyWriterJobTests.Issue658_…`, `PlanCommitRuleTests` unchanged and green.
- **M3** ✅ Action: `readDispatch` accepts the pre-set; the platform group edits from the `expected_sha` text; a platform
  group that is not applied leaves `global.json` out of the patch. Tests in `apply.test.ts`. `dist` rebuilt in the main
  checkout.
- **M4** ✅ Set up features keeps an existing workflow (D6).
- **M4b** ✅ Capability gate (D8): `# capabilities: sdk-preset` in `action.yml`, `DependencySdkPreset`, `PresetSdk` on
  the work-ref job.
- **M5** ✅ Docs: `docs/code-coverage/dependency-updates.md` (what the plan commit holds).
- **Gate** ✅ The affected test classes once at the end (log below), then the PR. After the merge and deploy, the
  `dependency-update-v1` tag moves and SpaServices gets a pin update for the action. Once that pin is merged, the next
  run installs 11.0.100 and applies `platform:dotnet 11.0` on #111 (D8).
- **Later (D7):** the setup-migration framework (§3c) with its first real migration.

## 7. Log

- 2026-11-13: P1 diagnosed on SpaServices #111 after the #656 deploy (the platform group was refused: SDK 11.0.100 not
  installed). Owner chose the migration system with B as its first migration and asked for Nx integration. Four
  read-only investigations: F1-F12. F2 and F4 re-checked by hand. Issue #658, branch, this PRD.
- 2026-11-13: Owner asked why the bot cannot set the new SDK first; then followed the recommendation to put the SDK move
  in the plan commit (D1). The `prepare` + both-SDKs design (old §3 "Migration 1") is superseded. With no first
  migration left, the framework is deferred (D7). D6 fixes the Set-up-features overwrite on its own.
- 2026-11-13: M2-M4 implemented. GitWriter tests 57/57 (`GlobalJsonPresetTests`, `PlanCommitRuleTests`,
  `DependencyWriterJobTests`); action tests 23/23 (`apply.test.ts`, `orchestrator.test.ts`); `tsc --noEmit` clean.
- 2026-11-13: Found before committing: repositories pin the action by SHA, and the pinned (pre-#658) action refuses a
  plan commit with a second path, which would have failed every apply with a .NET platform group. Added D8, the
  capability gate.
- 2026-11-13: Gate: GitWriter.Tests 57/57 (with the `PresetSdk` flag), CodeCoverage.Tests 122/122
  (`DependencyLifecycleRulesTests`, `LineNarrowingTests`, `OnboardingServiceTests`, `OnboardingPlannerTests`,
  `DependencySagaTests`, `DependencyOnboardingTests`, `DependencySdkPresetTests`, `DependencyPreviewToReleaseEndToEndTests`),
  action 23/23. Committed as `6b5a78066`; PR opened.

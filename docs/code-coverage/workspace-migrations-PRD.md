# Dependency updates: workspace-tool migrations (`nx migrate` and friends)

Branch: `docs/workspace-migrations-prd`. Status: **proposal**. The decisions in §5 are the assistant's proposals and wait
for the owner (§8). The spikes in §6 have not been run.

## 1. Why

The owner (2026-11-16): "for NX workspaces we could/should use `npx nx migrate latest` because just updating the package
versions isn't always just the answer. … I would certainly try to first make sure that the nx workspace is up-to-date." And
later: "would we provide similar functionality for turborepo etc?"

An Nx version is more than a number. Each Nx release carries generators that rewrite `nx.json`, `project.json`,
`tsconfig*.json` and source, plus `packageJsonUpdates` that move third-party packages (Angular, TypeScript, ESLint, …) to
the versions that Nx release supports. A bump that only edits `package.json` skips all of that. Plain `nx migrate` run
afterwards also skips it, silently ([nrwl/nx#23505](https://github.com/nrwl/nx/issues/23505)).

Spark itself shows the gap. It is on Nx 23.3.2 (`package.json:70-75`), Nx 24.0.6 is out, and PR #685 listed
`/approve nx` (23.3.0 → 24.0.6). That approval would have bumped the versions only, with no migrations, because the
`migrate:` rule in Spark's `coverage.yml:13-17` is commented out. Spark already has `.github/workflows/nx-migrate.yml`.

## 2. What exists today (audit 2026-11-16, master `4bc444c3b`)

Most of the migrate path was built for R9 (`dependency-updates-PRD.md` §13.10, P9, spike S17). Paths are under
`apps/CodeCoverage/` unless stated.

| Piece | Where | What it does |
|---|---|---|
| Config | `CodeCoverage/Dependencies/DependenciesYml.cs:51-53`, `:180-189`, record `DependencyMigrateRule` `:627` | `migrate: [{ match, onMajor: <workflow> }]`, opt-in per rule |
| Rule lookup | `Policy/DependencyPolicyConfig.cs:246` `MigrateWorkflow(depName)` | The first matching glob wins |
| **When it runs** | `Policy/DependencyPolicyEngine.cs:949-951` | **Only `approved && level == Major`**. A group with a migration gets `AutoMerge = false` (`:956`) |
| Dispatch | `Lifecycle/DependencyUpdateEngine.cs:863-909` | After apply: stages the work-ref commit, dispatches the migrate workflow with `base_sha`, `packages` (`[{name,from,to}]`), `run_id`. One at a time (`:946-951`), 45 min timeout (`:75`) |
| Failure | `DependencyUpdateEngine.cs:959-977` `DropMigrationAsync` | A partial or failed migration drops the group and holds `migrate:{group}` |
| Templates | `dependency-action/templates/nx-migrate.yml`, `angular-migrate.yml` | `mode: migrate`, `migrate-tool: nx \| angular`; `contents: read`, no secrets |
| Commands | `dependency-action/src/migrate/migrate.ts:74-95` | Nx: install, `nx migrate <to> --from=<each pkg>@<from> --include=all --agentic=false`, install, `nx migrate --run-migrations`, relock. Angular: one `ng update <pkg> --migrate-only --from --to` per package |
| Package manager | `migrate.ts:59-65` | pnpm-lock, then yarn.lock, else npm |
| Patch check | `CodeCoverage.GitWriter/Jobs/Dependencies/MigratePatchValidator.cs:20-30` | A deny-list: the plan path, unsafe paths (`.github/**`, `.git*`), `coverage.yml`. Everything else may change |
| Extra bumps (R19) | `migrate.ts:115` `extraBumps`; `Policy/DependencyHolds.cs:257-285` `DependencyMigrationNotes.Check` | Lists dependencies the migration moved outside the group; a bump onto a **held** version blocks |
| Auto-merge | `Lifecycle/DependencyLifecycleRules.cs:251-252` | "It contains code migrations, which never auto-merge." PR body marker: `DependencyPullRequestBody.cs:42` |
| Onboarding | `Lifecycle/DependencyOnboarding.cs:131-134`, snippet `:92-96` | Adds `nx-migrate.yml` when the tree has `nx.json` and `angular-migrate.yml` when it has `angular.json`; the `migrate:` rules are written **commented out** |
| Grouping | `Policy/DependencyPolicyEngine.cs:98` `GroupOf`; `dependency-action` `renovate.ts:30` | Configured group, else Renovate's `groupName` (`group:monorepos` + `group:recommended` put `nx` and `@nx/*` together), else the name. `Policy/DependencyCompanions.cs:18-40` has built-in companions for dotnet and node only |
| Tests | `MigratePatchProfileTests`, `DependencyWriterJobTests`, `DependencySagaTests`, `DependencyAutoMergeGateTests`, `DependencyWorkflowGuardTests`, `DependenciesYmlTests`, `migrate.test.ts`, `templates.test.ts`; fixture `CodeCoverage.GitWriter.Tests/Fixtures/Migrate/nx-22-to-23.patch` | |

## 3. Facts gathered (research 2026-11-16)

From `nrwl/nx` `packages/nx/src/command-line/migrate/migrate.ts` (master) and the published tarballs `nx@24.0.6` and
`@nx/angular@24.0.6`:

- **F1. Target resolution.** `nx migrate <x>`: a bare version, range or dist-tag targets `nx`. `latest` is the
  registry's dist-tag (on 2026-11-16: `latest` 24.0.6, `previous` 23.3.2, `next` 24.1.0-beta.1). Nx resolves versions
  with its own minimum release age (`resolvePackageVersionRespectingMinReleaseAge`).
- **F2. Third-party bumps live in the plugins.** `nx`'s own `packageJsonUpdates` is empty. `@nx/angular@24.0.6`'s
  `packageJsonUpdates` moves `@angular/*`, angular-eslint, ngrx, ng-packagr and others. Each entry is gated by `requires`
  and `incompatibleWith` ranges, evaluated against the version a package lands on in this run, else the installed one.
  For example, the 23.2.0 entry requires `@angular/core >=22.0.0 <22.1.0`. **So `nx migrate` moves Angular minors and
  patches itself.**
- **F3. Minors and patches carry migrations.** `nx@24.0.6`'s `migrations.json` has generators at 22.1.0, 22.6.0, 22.7.0
  and 23.2.0, plus patch-level entries (20.0.1, 19.2.x). **Migrating only on majors misses real migrations.**
- **F4. Flags.** `--run-migrations` is a separate second phase. `--from` is required after a manifest-only bump
  (#23505, our S17). `--exclude-applied-migrations` is the documented catch-up path. `--create-commits` prompts on the
  default branch unless it is passed explicitly. There is also `--agentic=false` and the `NX_MIGRATE_SKIP_INSTALL`
  environment variable. nx.dev recommends **one major at a time**
  (<https://nx.dev/docs/features/automate-updating-dependencies>).
- **F5. Angular compatibility (npm peerDependencies).**
  - `@nx/angular` 22.7.x: `@angular/build >=19 <22`.
  - 23.3.2, 24.0.6 and 24.1.0-beta.1: `>=20 <23`.
  - So **Nx 24 supports Angular 22**, and no `@nx/angular` release supports Angular 23 yet.
- **F6. Security.** Migrations run package code by design. `nx migrate` also uses the registry (`npm view`, `npm pack`)
  and runs an install. The source imports Nx Cloud connection code; S17's job environment already sets `NX_NO_CLOUD=true`.
- **F7. Renovate** has no Nx manager. Self-hosted setups run `nx migrate {{newVersion}} --from=nx@{{currentVersion}}`
  plus `--run-migrations` as `postUpgradeTasks`, behind an admin `allowedCommands` list. Dependabot has nothing.

Other tools (the codemod CLIs' sources and docs; items marked † were not confirmed against a source):

| Tool | Command | Exact target | `--from` | Bumps versions itself | Detect | Fit |
|---|---|---|---|---|---|---|
| Nx | `nx migrate <to> --from=…` then `--run-migrations` | yes | yes | yes (F2) | `nx.json` | **built** |
| Angular CLI | `ng update <pkg> --migrate-only --from --to` | yes | yes | no | `angular.json` | **built** |
| Turborepo | `npx @turbo/codemod migrate --from <v> --to <v> --force`, install off† | yes | yes | by default (`--install`) | `turbo` in `package.json` / `turbo.json` | good |
| Storybook | `npx storybook@<to> automigrate --yes --skip-install` | via the command's version | no (reads installed) | `upgrade` does; `automigrate` does not | `.storybook/main.*` | fair |
| Next.js | `npx @next/codemod upgrade <rev> -y` | yes | **no** | yes (also React) | `next` in `package.json` | poor: it wants to do the bump itself |
| Expo | `npx expo install --fix` | — | — | it aligns versions | `app.json` + `expo` | not a migration |
| Lerna | `lerna repair`† | — | — | — | `lerna.json` | rare |
| .NET | `upgrade-assistant` is deprecated in favour of an interactive, agent-driven upgrade tool | — | — | — | — | none headless |
| pnpm/yarn workspaces, Rush, moonrepo | none known | | | | | none |

Sources: `vercel/turborepo` `packages/turbo-codemod/src/cli.ts`; <https://angular.dev/cli/update>;
<https://storybook.js.org/docs/api/cli-options>; `vercel/next.js` `packages/next-codemod/bin/next-codemod.ts`;
<https://docs.renovatebot.com/self-hosted-configuration/#allowedcommands>.

## 4. Gaps

- **G1. Migrations only on approved majors** (`DependencyPolicyEngine.cs:949`). This contradicts F3.
- **G2. Opt-in, and off in practice.** Onboarding writes the rule commented out, so a repository with `nx-migrate.yml`
  still gets version-only Nx bumps (Spark today).
- **G3. Any migration blocks auto-merge of the whole rolling PR** (`DependencyLifecycleRules.cs:251`). If G1 is fixed
  naively, every Nx patch keeps every rolling PR waiting for a person.
- **G4. The Nx group is borrowed, not built in.** It comes from Renovate's `group:monorepos` preset. A configured
  `rules[].group`, a hold on one member, or a preset change can split `nx` from `@nx/*`, and a mixed Nx install fails.
- **G5. Nx and Angular each have their own migration.** An Nx Angular workspace has both `nx.json` and an Angular
  project, gets both templates, and `@angular/*` (threshold `never`, `DependenciesYml.cs:713`) moves through
  `ng update` while `nx migrate` (F2) wants to move it too. Nothing says which one owns Angular.
- **G6. Extra bumps vs thresholds.** R19 only blocks an extra bump onto a **held** version. Under `--include=all`, Nx can
  move `@angular/*` patches (F2) in a repository whose threshold for them is `never`.
- **G7. Major hops.** The plan picks the newest candidate, so 22 → 24 is one migrate run. Nx recommends one major at a
  time (F4).
- **G8. Only two tools.** `migrate-tool` is `angular | nx` (`migrate.ts:73`); no Turborepo, no Storybook.

## 5. Proposed decisions (assistant; for the owner in §8)

- **D1. The target is the planned version, never `latest`.** The plan already applies the cooldown, holds, fleet red,
  approvals and the npm-major-equals-Angular-major rule. `nx migrate latest` would skip all of them. The migrate step
  runs `nx migrate <planned> --from=<old>`, which is what the owner meant by `latest` once the plan has picked it.
- **D2. A built-in workspace-tool group.** When `nx.json` is present, `nx`, `@nx/*`, `@nrwl/*` and
  `create-nx-workspace` form one group named `nx`, whatever Renovate's `groupName` says. It is the same mechanism as
  `DependencyCompanions` (dotnet, node). A hold on any member holds the group. Turborepo gets `turbo`, `@turbo/*`,
  `eslint-config-turbo` the same way once it has an adapter (D8).
- **D3. A tool group migrates on every move, not only on majors** (fixes G1, from F3). The migrate rule stops being
  `onMajor`-only: `on: any | minor | major` (default `any` for Nx). If S3 shows the lookup can tell cheaply whether the
  range has generators, it is dispatched only then. Otherwise it is always dispatched (S17 measured about 2.5 min plus
  install).
- **D4. A migration that changes nothing beyond the bump stays auto-mergeable** (fixes G3). The migrate result
  classifies its patch:
  - **manifest-only:** `package.json`, lockfiles and the Nx `migrations.json` that is deleted again.
  - **config:** `nx.json`, `project.json`, `tsconfig*.json`, `.gitignore`.
  - **source:** anything else.

  Only manifest-only patches keep the group auto-mergeable. Config and source changes keep R9's "never auto-merges".
  Whether config may auto-merge is owner question Q3.
- **D5. In an Nx workspace, Nx owns Angular** (fixes G5). With `nx.json` present, `@angular/*` and the other packages
  that `@nx/angular`'s `packageJsonUpdates` own are moved by `nx migrate`, not by `ng update`. Nx's own guidance and F2
  both point this way. The Angular group still needs its approval for a major, because the threshold is unchanged; an
  approved Angular major inside an Nx workspace becomes an Nx migration to the Nx version that supports it (F5). S4
  verifies that `nx migrate` runs Angular's own `ng update` schematics.
- **D6. Extra bumps obey thresholds, not just holds** (fixes G6). An extra bump over its own package's threshold marks
  the group "needs approval" with the bump listed, instead of riding along. An extra bump the approval already covers
  rides along, as now.
- **D7. One major per run for tool groups** (fixes G7, from F4). A tool group two majors behind plans the newest release
  of the next major first. The following run continues from there.
- **D8. Adapters, not a free-form command.** Each tool is an adapter in `dependency-action`:
  `{ tool, detect, members, needsFrom, steps(pm, from, to), extraBumps }`. A repository never supplies a shell command,
  which keeps us out of Renovate's `allowedCommands` problem (F7). Order:
  1. nx and angular (built)
  2. turbo (S6)
  3. storybook (S7)

  Next.js stays out: it does the bump itself and has no `--from`. .NET has no headless tool.
- **D9. On by default where detected.** When the tool's marker file and its template are present, its built-in rule
  applies without a `migrate:` entry. `migrate: { nx: off }` opts out. This reverses R9's per-rule opt-in (Q1).
- **D10. Catch up first.** As the owner asked, Spark is brought to Nx 24 before the new behaviour ships. This is also
  live spike S1: enable the existing rule in Spark's `coverage.yml`, then `/approve nx` on the rolling PR. It runs
  today's code path (approved major → `nx-migrate.yml`), with no bot change needed.

## 6. Spikes

| # | Question | Setup | Pass |
|---|---|---|---|
| S1 | Does today's path take Spark 23.3.2 → 24.0.6 with migrations? (D10) | Spark: uncomment the `nx` migrate rule, `/approve nx` | Rolling PR marked "contains code migrations", migrate patch accepted, CI green; list the extra bumps |
| S2 | What does a move with no generators in range produce? (D4) | Scratch Nx repo: 23.3.0 → 23.3.2 (no generators) vs 23.1.x → 23.3.2 (23.2.0 generators) | The first patch is manifest-only; the second is classified config or source |
| S3 | Can the lookup tell whether a range has generators without running them? (D3) | Read `migrations.json` from the target tarballs (`npm pack --dry-run --json` or the registry tarball) for `nx` and each `@nx/*` | A yes/no and its cost per run |
| S4 | Does `nx migrate` run Angular's own migrations? (D5) | Scratch Nx + Angular workspace, `@nx/angular` move whose `packageJsonUpdates` moves `@angular/core` | Angular `ng update` schematics appear in the run's log and diff |
| S5 | Which extra bumps does `--include=all` add for 23.3 → 24.0.6, and against which thresholds? (D6) | The S1 run, or scratch | The table of extra bumps and the threshold each one hits |
| S6 | Turborepo non-interactive after a manifest-only bump (D8) | Scratch pnpm + turbo repo, `@turbo/codemod migrate --from --to --force` with install off (confirm the flag form) | Exit 0, a codemod diff, no prompt, no install |
| S7 | Storybook `automigrate` after a manifest-only bump (D8) | Scratch Storybook repo | It reads the installed version, runs, and writes no `package.json` change of its own |
| S8 | A two-major hop vs two runs (D7) | Scratch: 22.x → 24 in one `--from` run vs 22 → 23 → 24 | Same tree, or the hop misses or breaks generators |

## 7. Plan (one PR after the owner's answers; the S1 catch-up is a PR of its own, D10)

- **M0. Catch-up (S1).** Spark `coverage.yml` gets the `nx` rule; `/approve nx`; record S1 and S5.
- **M1. Built-in tool groups (D2).** `DependencyCompanions`-style grouping keyed on the workspace marker.
  Tests: the group survives a configured `rules[].group` and a member hold.
- **M2. Migrate on any move (D3).** The `on:` key, the policy change at `DependencyPolicyEngine.cs:949`, and the S3
  dispatch filter if it pays.
- **M3. Patch classes and auto-merge (D4).** `migrate-result.json` gets `patchClass`; the auto-merge rule at
  `DependencyLifecycleRules.cs:251` reads it.
- **M4. Nx owns Angular (D5).** The Angular group routes to the nx adapter when `nx.json` is present.
- **M5. Extra bumps vs thresholds (D6) and one major per run (D7).**
- **M6. Default on (D9).** Onboarding writes no commented rules; existing repositories pick it up from their templates.
- **M7. Turborepo and Storybook adapters (D8)**, after S6 and S7.
- **M8. Docs:** `dependency-updates.md` (migrations section), the setup page, the R9 row in
  `dependency-updates-PRD.md` marked superseded where it is.

## 8. Questions for the owner

1. **Q1 (D9).** Should migrations be on by default where the tool is detected, reversing R9's opt-in?
2. **Q2 (D5).** In an Nx workspace, should Nx own Angular, so that `@angular/*` moves only through `nx migrate`?
3. **Q3 (D4).** May a migration that only edits config (`nx.json`, `project.json`, tsconfig) auto-merge, or only a
   manifest-only one?
4. **Q4 (D8).** Which other tools should get adapters: Turborepo, Storybook, both, or neither for now?
5. **Q5 (D10).** May I run the S1 catch-up on Spark now (uncomment the rule, `/approve nx`)?

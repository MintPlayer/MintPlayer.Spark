# PRD — Absorb the Coverage app into the MintPlayer.Spark workspace

**Status:** implemented — [PR #339](https://github.com/MintPlayer/MintPlayer.Spark/pull/339), branch `feat/absorb-coverage-app` · **Date:** 2026-09-01 · **Plan:** [coverage_monorepo_plan.md](coverage_monorepo_plan.md)

> Written before the work and kept as the record of what was intended. Where the build disagreed with it, the plan's milestone notes say so rather than this file being quietly rewritten — §6 records which acceptance criteria actually hold.

> **Naming note.** This repo will soon hold six "coverage" things meaning four subjects. To be
> precise:
> - **this pair** — moving the Coverage *application* into this workspace;
> - `docs/PRD-CoverageHandoff.md` + `docs/coverage-handoff-plan.md` — Spark hardening requested *by*
>   the Coverage app in preview.42 (`Origin:` names `C:\Repos\Coverage\docs\spark-handoff.md`); the
>   plan grew into the whole IdentityProvider security pass. **Both still say `Status: Draft for
>   review` although everything in them shipped** — they get status-stamped as part of this work;
> - `docs/codecov/` — diagnosing a coverage-*report* upload failure on Spark PR #123;
> - `docs/code-coverage/` — the Coverage app's own docs, arriving here (§5.8).

---

## 1. Goal

Move the Coverage application's **source** — not a submodule, not a subtree — out of
`C:\Repos\Coverage` and into this workspace, so that Coverage becomes a first-class member of the
Nx + npm-workspaces monorepo alongside the demo apps, consuming Spark from **project source**
instead of from published `10.0.0-preview.*` NuGet packages and `@mintplayer/ng-spark@^22.8.0`.

Coverage is Spark's only real production consumer. Today every Spark API change costs a publish
cycle before Coverage can validate it, and every Coverage adoption finding is written up as a
cross-repo handoff document — eleven such files exist, and four of them duplicate a Spark PRD
outright (§5.8). Bringing it in-tree collapses that loop: a breaking Spark change and its Coverage
fix land in the same commit, and Coverage's ~144 tests become a regression gate on Spark itself.

### Why now

1. The pending **Coverage authorization migration** (`Everyone` → `authenticated` grant plus
   `IsAllowedAsync` overrides) has been blocked since 2026-08-21 purely because cross-repo writes are
   not possible from a session rooted in this repo. Merging removes the blocker rather than working
   around it.
2. Coverage is already on `preview.68`, the current version in this repo. There is no version gap.
3. Coverage is small: **239 tracked files, 3.6 MB**. The payload is not the risk; the wiring is.
4. `Coverage.Tests` is on **FluentAssertions 8.8.0** — the paid-licence major. It cannot enter this
   repo as-is, which forces the assertion-library decision (D4) now rather than later.

## 2. Scope

### In scope

- Renaming `Demo/` → `apps/` so all four demos and Coverage share one application root.
- Copying the Coverage app, its SPA, `Coverage.Library`, its test project, its `action/`, its
  `tools/` lcov helper, its Dockerfile, its `docker-compose.yml` and its docs into this workspace,
  using **filesystem copy commands only** (`cp`/`copy`) — never by reading a file and re-typing it.
- Rewiring the three `.csproj` files from `PackageReference` onto Spark to `ProjectReference`.
- Registering the SPA as an npm workspace and an Nx project.
- Moving `action/` here and updating every consumer that pins it.
- **Replacing FluentAssertions with `MintPlayer.Assertions` solution-wide** (D4).
- **Reworking the `AGENTS.md` build targets** so in-repo consumers get a tracked pointer and NuGet
  consumers keep the full copy, and fixing the first-writer-wins collision they contain (D5).
- **Raising coverage from 83.98% to ≥90%**, starting by uploading Angular coverage at all (D6).
- Fixing `spark-allfeatures.targets`, which breaks external NuGet consumers (§5, R11).
- Porting Coverage's CI, and its deploy job with the published image name unchanged.
- Regenerating `App_Data/Model/*.json`, `modelHashes.json`, `security.json`, `securityPosture.txt`.
- Triaging Coverage's twenty docs into `docs/code-coverage/` (§5.8), stubbing the four that duplicate
  a Spark PRD and dropping the two that are dead.
- Completing the deferred authorization migration.

### Out of scope

- Any change to Coverage's product behaviour, data model or UI beyond what the move forces.
- Rewriting Coverage's tests onto `MintPlayer.Spark.Testing` (it rolls its own `RavenTestDriver`
  base). Worth doing; not part of this move.
- Deleting the `MintPlayer/CodeCoverage` repository — it is emptied and archived as a separate,
  later, manual act, after the monorepo build is green and has deployed at least once.
- Changing the deployed image name or touching the VPS `docker-compose.yml`.
- Adopting `MintPlayer.Assertions` in any repo other than this one.

## 3. Decisions taken

| # | Question | Decision |
|---|---|---|
| D1 | Where does Coverage live? | **`git mv Demo apps`**, then `apps/CodeCoverage/CodeCoverage` + `apps/CodeCoverage/CodeCoverage.Library`. All applications share one root; Coverage is not filed as a demo. |
| D2 | What happens to `action/`? | **Move it and update consumers** — `MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@master`. Every pinned consumer is updated in the same unit of work. |
| D3 | How does deployment move? | **Port the workflow, keep the image name.** A new workflow here publishes `ghcr.io/mintplayer/codecoverage:master` exactly as before and drives the same VPS ssh deploy. |
| D4 | FluentAssertions? | **Drop it entirely; adopt `MintPlayer.Assertions`** solution-wide — all five existing test projects and every new one. One assertion library per workspace. |
| D5 | Generated `AGENTS.md` in-repo? | **In-repo consumers get a pointer to the single source of truth in `libs/`; external NuGet consumers keep the full copy.** Pointers become tracked files, replacing the current ignore-the-copies rule. |
| D6 | Coverage | **Raise it as part of this work.** Measured .NET union baseline is **83.98%**; target **≥90%**, with no shipping project left at zero measurement. Angular coverage starts being uploaded at all. |

D1 has a property that makes it cheap: `Demo/HR/HR` and `apps/HR/HR` are the **same depth**, so every
`..\..\..\libs\...` `ProjectReference`, every `..\..\..\..\tsconfig.base.json` extends, and every
`Targets\*.props` import survives the rename. Only paths naming the `Demo` segment literally change,
and those are enumerable — thirteen files, listed in the plan's M1.

D2 is the one decision that breaks something outside this repo, taken deliberately: a single action
surface is worth more than uninterrupted pins, and per the one-PR rule the consumer updates land
together rather than as a follow-up.

D4's scope is solution-wide by choice. Coverage's 8.8.0 reference is the urgent part, but leaving this
repo's four projects on 7.2.2 would mean two assertion libraries in one workspace and a permanent
licence ceiling. §5.9 sizes it; it is far cheaper than the raw call-site count suggests, because
`MintPlayer.Assertions` is deliberately shaped like FluentAssertions. Any test project added under D6
uses it from the start.

D5 and D6 push this work into the Spark framework itself rather than only the app being absorbed.
That is deliberate: the AGENTS.md mechanism has a latent bug that Coverage would import (R10), and
"absorb the app" is the natural moment to fix the measurement gap that let two security-critical
extensions sit at 0% coverage (R12).

## 4. Current state

### 4.1 What Coverage is made of

| Part | Path today | Tracked files |
|---|---|---|
| Web app (ASP.NET Core + SPA) | `Coverage/` | 146 |
| Entity POCOs | `Coverage.Library/Entities/` | 14 (590 lines) |
| Tests (xUnit + RavenDB.TestDriver) | `Coverage.Tests/` | 31 (~144 facts) |
| GitHub upload action (node20 + committed `dist/`) | `action/` | 17 |
| PRDs / plans / findings | `docs/` | 20 |
| lcov path-rebase helper | `tools/` | 2 |
| Root infra | `Coverage.slnx`, `docker-compose.yml`, `coverlet.runsettings`, `.dockerignore`, `.env.example`, `README.md` | 6 |

A normal Spark host: `Program.cs` calls `AddSpark(...)` with Controllers, Cron, Messaging, Migrations
and the GitHub webhooks packages; `App_Data/` carries the mandatory `security.json`, a
`securityPosture.txt` baseline, `modelHashes.json` and eleven generated `Model/*.json`; the SPA lives
at `Coverage/ClientApp` under `SpaRoot`, served through `UseAngularCliServer` in development. Ports
**5200/5201** collide with nothing here (demos use 5003–5008 and 60493/60494).

Ten `MintPlayer.Spark.*` packages at `10.0.0-preview.68` become `ProjectReference`s. Staying on
NuGet because they come from other repositories: `MintPlayer.AspNetCore.SpaServices` 10.5.0,
`MintPlayer.SourceGenerators` + `.Attributes` 10.20.0, and — newly — `MintPlayer.Assertions` 1.0.0.
`YamlDotNet` and `Newtonsoft.Json` also stay.

### 4.2 What this workspace expects of a member app

- **.NET:** `Microsoft.NET.Sdk.Web`, `net10.0`, `IsPackable=false`, `SpaRoot=ClientApp\`; relative
  `ProjectReference`s into `libs/`; the source generator referenced as
  `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`; `spark.targets` and
  `spark-authorization.props`/`.targets` imported **explicitly**, because NuGet's automatic
  `buildTransitive` import does not apply to `ProjectReference` consumers; an `AdditionalFiles` block
  feeding `App_Data/translations.json`, `Model/*.json` and `culture.json` to the generators.
- **Nx:** .NET projects are inferred from `*.csproj` by `@nx/dotnet` — no `project.json` needed. An
  app host gets a three-line `project.json` whose only job is `implicitDependencies`. The SPA gets a
  **full** `project.json` using `@nx/angular:application` / `:dev-server` / `:unit-test`; **there is
  no `angular.json` anywhere in this repo.**
- **npm:** one hoisted `node_modules`; the SPA is listed in the root `package.json` `workspaces` by
  explicit path; its own `package.json` declares almost nothing and delegates `start`/`build`/`test`
  to `nx run <project>:<target>`. `UseAngularCliServer(npmScript: "start")` invokes that.
- **TypeScript:** the SPA's `tsconfig.json` extends `tsconfig.base.json`, whose `paths` map
  `@mintplayer/ng-spark(-auth)` to **library source**, not `dist`.
- **CI:** `nx affected` picks a new app up automatically. Two places do **not** auto-discover: the
  hardcoded `--spark-verify-model` and `--spark-verify-security` loops in `pull-request.yml`.
- **Assertions (new, per D4):** `MintPlayer.Assertions` via a csproj-level
  `<Using Include="MintPlayer.Assertions" />`, matching how FluentAssertions is imported today.

## 5. Risks and constraints

### R1 — This repo's `.gitignore` swallows any folder named `Coverage` (verified)

`core.ignorecase` is `true` on Windows, so git matches ignore patterns case-insensitively. Lines
152–156 of `.gitignore` are the stock Coverlet block: `coverage*.json`, `coverage*.xml`,
`coverage*.info`, `coverage/`, `**/coverage/`. Measured directly:

```
$ git check-ignore -v Coverage/App_Data/security.json
.gitignore:156:**/coverage/   Coverage/App_Data/security.json
```

`cp -r` followed by `git add` would copy every file to disk and stage **nothing**, with no error.
This applies to `docs/coverage/` too, which is why the docs folder is named **`docs/code-coverage/`**
— it sidesteps the trap by name instead of relying on the fix. `.gitignore:70` `artifacts/`
(unanchored) is a second, milder instance. **Fixing `.gitignore` is milestone zero and gates
everything else.**

Two related rules: `**/spark-auth.setup.ts` is ignored here but *tracked* in the Coverage repo; and
`Demo/*/*/AGENTS.md` / `tests/*/AGENTS.md` are ignored here while Coverage tracks committed copies.
Those copies are build output (`CopySparkAgentsGuide` rewrites them every build) so the ignore rule
is right and the copies must not come along — but note neither existing pattern matches
`apps/CodeCoverage/CodeCoverage/AGENTS.md`, which is three segments deep. New rules are needed.

### R2 — `action/`'s repo-root path is a public API

`action/action.yml` is consumed as `MintPlayer/CodeCoverage/action@master` by this repository's own
`pull-request.yml` and `dotnet-build-master.yml`, and by `C:\Repos\mintplayer-ng-bootstrap`. Per D2
all move to the new path in this same unit of work. Coverage's own CI already uses local `./action`,
which simply becomes `./apps/CodeCoverage/action`.

`action/dist/index.js` is a **committed 2.31 MB bundle** and CI enforces it is current
(`git status --porcelain dist` clean after `npm run build`). That gate comes along, and no ignore
rule may start ignoring `dist/`.

### R3 — npm hoisting will change resolved dependency versions

| Package | Coverage wants | This repo pins |
|---|---|---|
| `@mintplayer/ng-bootstrap` | `22.16.0` (exact) | `^22.17.0` |
| `@angular/*` | `^22.0.0` | `22.1.3` (with an `overrides` block) |
| `@mintplayer/ng-animations` | `^22.0.0` | `22.1.0` |
| `@mintplayer/ng-spark`, `-auth` | `^22.8.0` **published** | mapped to **source** by `tsconfig.base.json` |

The last row is the substantive one, and the point of the move — but a *partial* application (some
files resolving to source, others to the hoisted package) gives the app two copies of ng-spark and
two sets of Angular DI tokens. It is all-or-nothing. The ng-bootstrap span also crosses the accordion
Lit shadow-DOM change, so expect visual fallout in Coverage's panels.

New deps to lift to the root: `@angular/cdk`, `@mintplayer/ng-click-outside`,
`@mintplayer/ng-focus-on-load`, `@mintplayer/web-components` 2.13.0, `highlight.js`, `lit`.

### R4 — Model and security artefacts must be regenerated, not copied

`modelHashes.json` gates startup outside Development and `security.json` is mandatory. Both were
generated against `preview.68` **packages**; after switching to Spark-from-source the generated model
may drift and the app refuses to start until `--spark-synchronize-model` is re-run.
`securityPosture.txt` — the committed list of anonymously reachable rights — must be regenerated with
it and reviewed line by line, since copying it blindly would hide a widened anonymous surface.
`--spark-verify-model` / `--spark-verify-security` are CI gates, so a stale artefact fails the build
rather than failing quietly. Synchronize must be a fixed point: a second run must produce no diff.

### R5 — lcov path ambiguity gets worse, not better

`tools/rebase-lcov-paths.mjs` exists because Vitest emits `src/main.ts` from both `action/` and
`Coverage/ClientApp/`, and the ingestion server drops ambiguous longest-suffix matches. This
workspace has **six** JS projects today and eight after the move, most with a `src/main.ts`. The
helper becomes more load-bearing, not less, and its `base-dir` arguments are repo-root-relative so
every call site changes.

### R6 — Two test-runner constraints

`Coverage.Tests` calls `RavenTestDriver.ConfigureServer` from a static constructor, once per
assembly, process-wide. It must stay its own assembly. It also sets `EnableSpaBuilder=false` to stop
transitive NodeServices targets building a ClientApp — keep that. It will be the first app-scoped
test project here; to stay inside CI's coverage glob it needs a `project.json` whose `test` command
writes to `<project>/coverage`.

### R7 — Root-level files that exist in both repos

`.gitignore`, `.dockerignore`, `docker-compose.yml`, `README.md`, `coverlet.runsettings`,
`.env.example`, `.claude/settings.local.json` and `docs/` all exist on both sides. None may be copied
over this repo's version; each is merged or relocated into `apps/CodeCoverage/`. `.dockerignore` is the
sharp one: only the build-context root's file is honoured, so Coverage's rules (`docs/`, `action/`,
`*.md`, `.env`, `*.pem`) must be merged additively and re-expressed relative to the new root, or the
image build breaks or leaks.

### R8 — Coverage's CI workflows are path-blind

`ci.yml` and `publish.yml` hardcode `Coverage.slnx`, `Coverage/ClientApp`, `action`,
`tools/*.test.mjs`, and would fire on every change anywhere in this workspace. They are not copied;
their *jobs* fold into this repo's existing workflows, and the deploy job becomes a new
`paths`-filtered workflow modelled on `webhooks-demo-deploy.yml`.

### R9 — The assertion-library swap (D4) is cheap but has three sharp edges

**Measured footprint: 4167 `.Should()` call sites across 297 files** — 3200 in
`MintPlayer.Spark.Tests`, 367 in `Coverage.Tests`, 356 in `SourceGenerators.Tests`, 144 in E2E, 100
in `Client.Tests`. All four Spark projects import FA through a csproj-level
`<Using Include="FluentAssertions" />`, so there are only **five** package references and **four**
global usings to change, not 297 file headers (Coverage uses 25 per-file usings).

The three things that usually make an FA migration hard are all **absent**: zero `AssertionScope`,
zero `BeEquivalentTo` options lambdas (all 117 calls are plain), and zero custom assertion
extensions. `MintPlayer.Assertions` keeps `.Should()`, `.And`, `.Which`, the `because` parameter
(~738 sites use one), `BeEquivalentTo` with a full options object, and
`Throw<T>().WithMessage(...)`/`.Where(...)`. So roughly 4050 of 4167 sites compile unchanged.

The sharp edges, in order of danger:

1. ~~**`WithMessage` is case-sensitive here**, so all 69 sites need individual review.~~
   **Corrected during M6.** The direction of the change makes this safe: case-sensitive matching is
   strictly *stricter* than FluentAssertions' case-insensitive default, so a pattern that stops
   matching turns a passing test into a failing one, loudly. It cannot produce a false pass, because
   the assertion has no negative form. Running the suite covers all 69 sites. The real silent-failure
   class turned out to be single-element `BeEquivalentTo` (below).
2. **`.Subject` does not exist on `AndWhichConstraint` — only `.Which`.** 18 sites, mechanical.
3. **Genuine gaps**, each with few or no call sites: `BeApproximately` (1 site → `BeCloseTo`),
   `AllBeEquivalentTo` (1 site, no equivalent, rewrite), `NotContainAny` (2 sites, verify),
   `BeOneOf` on strings/objects (11 sites to check — it exists only for numeric/enum/DateTime), and
   `BeEquivalentTo` on a statically dictionary-typed subject (not defined; needs an `object` cast).
   Also absent but **unused here**: `HaveElementAt`, `BeInRange`, `ContainItemsAssignableTo`,
   member-info assertions, type-selector APIs.

Behavioural differences to internalize: negative assertions treat a **null subject as passing**;
`Be` uses `Equals`, `BeSameAs` compares references, `BeEquivalentTo` compares structure; collections
in `BeEquivalentTo` are matched **unordered** by default; a failure throws
`MintPlayer.Assertions.AssertionFailedException`, which xUnit renders as an error rather than an
assert failure. The library's own README documents a stale namespace for `AssertionScope`
(`.Execution`); the type is actually in the root namespace — follow the source.

**Maturity is the honest risk.** `MintPlayer.Assertions` is version **1.0.0**, three commits old, and
its own README says so plainly: *"a bug you hit may be one nobody has hit yet."* Its benchmark is
strong (13.08 µs / 20.34 KB vs FA 7.2.2's 201.08 µs / 409.14 KB on a 4-level graph) and it ships
analyzers plus code fixes for the renames, but 4167 call sites landing on it in the same PR as a repo
merge means two unproven things in flight at once. Mitigation: migrate `Coverage.Tests` first (367
sites, and the licence-urgent one), get it green, then do the 3800 Spark sites. Any library bug found
needs a version bump in `MintPlayer.Dotnet.Tools`, since 1.0.0 is already published and unchanged
since.

### R10 — The `AGENTS.md` mechanism has a latent bug that Coverage would import (D5)

Two `AGENTS.md` sources are distributed by build target: `libs/spark/MintPlayer.Spark/AGENTS.md` (via
`CopySparkAgentsGuide` in `spark.targets`, packed to `buildTransitive/`) and
`libs/testing/MintPlayer.Spark.Testing/AGENTS.md` (via `CopySparkTestingAgentsGuide` in
`spark-testing.targets`, packed to `build/`). **Both default their output to
`$(MSBuildProjectDirectory)\AGENTS.md`** — the same path. `spark.targets` documents the collision and
does not defend against it.

The failure mode is worse than last-writer-wins. Both targets declare `Inputs`/`Outputs` pointing at
source and destination, so whichever runs first writes the file, the file is then newer than the other
target's `Inputs`, and MSBuild marks that target **up to date and skips it forever** — the `Copy` never
executes and nothing warns. First writer wins permanently.

In this repo the bug is latent, and structurally so: no project imports both targets files, because
under `ProjectReference` the imports are hand-written. Verified by checksum — all four demo copies are
the Spark guide, all three test copies are the Testing guide, all nine correct. In Coverage it is
**live**: `Coverage.Tests` `PackageReference`s the Testing package and transitively auto-imports
`MintPlayer.Spark`'s `buildTransitive` targets, so both race, and its `AGENTS.md` is byte-identical to
the app's — the Spark guide, not the Testing guide.

D5's pointer mode fixes this incidentally by not using `Inputs`/`Outputs` on the pointer path, but the
copy path needs the collision guarded regardless, since external consumers hit it. Note also that the
existing ignore rules (`Demo/*/*/AGENTS.md`, `tests/*/AGENTS.md`) match neither of Coverage's paths,
which are one segment deeper — and under D5 those rules are deleted rather than extended.

### R11 — `spark-allfeatures.targets` is broken for NuGet consumers (verified)

Unrelated to the move, found while checking whether the shipped targets survive `ProjectReference`.
They do — every path is layout-symmetric by design, and the demos have been proving it. The one
genuine break runs the **other** way.

`libs/all_features/MintPlayer.Spark.AllFeatures/Targets/spark-allfeatures.targets` contains an
unconditional `ItemGroup` adding two `ProjectReference`s via `$(MSBuildThisFileDirectory)..\..\`. Its
own header comment says *"When consumed via NuGet, the analyzers/ folder in the package handles this
automatically and this file is not needed"* — but `MintPlayer.Spark.AllFeatures.csproj:60` packs it to
`buildTransitive/$(PackageId).targets`, so NuGet auto-imports it anyway, and in a package layout those
`..\..\` paths resolve to directories that do not exist.

**Measured, not assumed** — packed to a folder feed and consumed by a scratch app: restore *succeeds*,
and the symptom is an `MSB9008` warning ("the referenced project … does not exist") on every build. It
is noise plus broken intent, not the restore failure first supposed.

The fix is **not** just `Condition="Exists(...)"` on the `ItemGroup`. `SPARK001`'s validation accepts a
`ProjectReference` carrying `OutputItemType="Analyzer"` without checking the project file is real, so
the dangling reference had been silently *satisfying* that validation for package consumers; guarding
it alone converts a warning into a hard `SPARK001` error. The package branch must also assert
`MintPlayerSparkSourceGeneratorsReferenceValidated` — accurate rather than a workaround, since the
package genuinely ships both generator DLLs in `analyzers/dotnet/cs`.

**Status: done** (commit `4d488261`), verified in both directions — external consumer builds clean, and
Fleet still evaluates both analyzer `ProjectReference`s with the validated flag left unset so `SPARK001`
still runs for real.

### R12 — Coverage measurement is missing a whole language (D6)

Measured .NET union baseline, from the four cobertura reports on disk (2026-08-24), unioned per
file+line: **83.98%** (16,715 / 19,903 lines). Per-assembly worst offenders: `Webhooks.GitHub.DevTunnel`
29.6%, `SubscriptionWorker.Abstractions` 53.2%, `Webhooks.GitHub` 62.4%, `Replication` 73.6%.
`MintPlayer.Spark` itself is 87.3% but holds the largest absolute gap at 979 uncovered lines.

Two findings make this more than a number:

1. **Angular coverage is never uploaded.** Both workflows glob only
   `tests/*/coverage/**/coverage.cobertura.xml`, and `disable-search: true` means nothing else is
   picked up. Vitest writes `libs/node_packages/*/coverage/cobertura-coverage.xml` (ng-spark 83.59%,
   ng-spark-auth 94.34%) and it is discarded. The badge is .NET-only today. **This must be fixed
   before setting a target**, or ~2,000 newly-counted lines will move the number under the milestone.
2. **`MintPlayer.Spark.SubscriptionWorker` — a shipping package — has zero measurement.** No test
   project references it; it appears in no cobertura report. Worker loops fail by quietly stopping, so
   this is the highest unknown-unknowns ratio in the repo.

And the two single worst classes are both security-critical and both at **0%**:
`SparkSecurityInitExtensions` (0/71) and `SparkSecurityVerificationExtensions` (0/47) — the
implementations behind `--spark-synchronize-security` and `--spark-verify-security`. CI's
"anonymous surface has not widened" gate is the only thing between a one-line `security.json` diff and
a public endpoint, and the gate itself is untested. A false negative there is invisible by
construction. That this survived unnoticed is the argument for D6.

There are no `[ExcludeFromCodeCoverage]` attributes anywhere and no runsettings, so nothing is being
hidden — the gaps are real.

## 6. Acceptance criteria

1. `git status --porcelain` after the copy shows every intended Coverage file **staged**, and
   `git check-ignore` reports no match for anything under `apps/CodeCoverage/` or `docs/code-coverage/`
   that should be tracked.
2. `dotnet build` succeeds with Coverage's three projects included, resolving
   Spark by `ProjectReference` — no `MintPlayer.Spark.*` `PackageReference` remains.
3. `npx nx show projects` lists the Coverage host, library, test project and SPA; a change under
   `libs/spark/` marks Coverage affected.
4. `npm ci` at the root installs one hoisted tree; Coverage's SPA has no private lockfile or
   `node_modules`.
5. `npx nx run <coverage-spa>:build` compiles against ng-spark **source** — verifiable by editing a
   library file and seeing the SPA rebuild.
6. `--spark-verify-model` and `--spark-verify-security` pass for Coverage, and re-running the
   `--spark-synchronize-*` counterparts produces an empty diff.
7. `dotnet run --project apps/CodeCoverage/CodeCoverage` starts, prints the dev server's
   `➜ Local: http://localhost:NNNNN/`, and the app is serviceable at `https://localhost:5200`.
8. **No `FluentAssertions` reference or using remains in any project in this repo**, and the full
   test sweep is green on `MintPlayer.Assertions`: five .NET projects, both SPA suites, the action's
   Vitest suite.
9. All 69 `WithMessage` sites have been individually reviewed for case sensitivity (R9.1) — a
   green suite alone does not discharge this.
10. The action builds to a byte-identical `dist/`, and no workflow here or in
    `mintplayer-ng-bootstrap` still references `MintPlayer/CodeCoverage/action@master`.
11. The deploy workflow publishes `ghcr.io/mintplayer/codecoverage:master` and the VPS
    `/health/ready` loop passes — the existing server picks up the new image with no manual change.
12. The four existing demos still build, still verify model and security, and the E2E suite still
    boots Fleet from `apps/Fleet/Fleet`.
13. `docs/code-coverage/` holds the surviving Coverage docs with status stamps; the four
    Spark-duplicating docs are one-line stubs pointing at the Spark PRD; the two dead docs are gone;
    and `PRD-CoverageHandoff.md` / `coverage-handoff-plan.md` no longer read `Draft for review`.
14. Every in-repo project (four demos, four test projects, Coverage's two) has a **tracked**
    `AGENTS.md` pointer resolving to the right source guide — the Spark guide for apps, the Testing
    guide for test projects — and a second build leaves the worktree clean. External NuGet consumers
    still receive a full copy, and a project importing both targets no longer silently skips one.
15. `MintPlayer.Spark.AllFeatures` restores cleanly for a consumer referencing it as a
    `PackageReference` from outside this repo.
16. Angular cobertura from `libs/node_packages/*` is uploaded by both workflows, and the badge
    reflects .NET **and** Angular.
17. .NET union line coverage is **≥90%** (from 83.98%), no shipping project is at zero measurement,
    and `SparkSecurityInitExtensions` / `SparkSecurityVerificationExtensions` are no longer at 0%.
18. Coverage's authorization migration is complete: nothing granted to a legacy `Everyone` group,
    type-level grants **moved** to `authenticated`, and `IsAllowedAsync` overrides deciding
    per-organization/repository/commit visibility.

### Outcome

**16 of 18 hold.** 2351/2351 .NET tests pass plus 22 SPA and 35 action; the app runs against
RavenDB; the image builds (375 MB, verified twice, once `--no-cache`); ng-spark resolves to source,
proven with `tsc --showConfig`; both `--spark-verify-*` gates exit 0 and synchronize is a fixed
point; the action's `dist` rebuilds byte-identically behind a CI gate; and no `FluentAssertions`
reference remains anywhere.

Two do not:

- **#17 (≥90% coverage) is not met.** Measured, excluding generated code: Spark .NET libs 87.71%,
  the CodeCoverage app 50.77%, Angular 81.30%, overall **82.35%**. M12 closed the two 0% security
  extensions and the unmeasured worker package; items 3–8 of the gap analysis are untouched.
- **#9 was withdrawn, not met.** It required reviewing all 69 `WithMessage` sites individually for
  case sensitivity. That criterion rested on a wrong premise — the change is strictly *stricter*, so
  it can only turn a passing test into a failing one, never produce a false pass. Running the suite
  covers it. See R9.

**#18 needed no work**: the authorization migration had already landed upstream before the merge.
Verified rather than assumed — `wellKnown {anonymous, authenticated}`, no legacy `Everyone`, row
gating on four action classes, and `LocalCredentials` correctly relying on the preview.58 default.

### Beyond the plan

Work the eighteen milestones did not anticipate, all of it forced by something measured:

- **`10.0.0-preview.69`** across all 22 .NET libraries. Three *shipped* targets files changed, and
  `--skip-duplicate` would otherwise have published nothing while the source moved on. ng-spark and
  ng-spark-auth deliberately unbumped — zero changed files.
- **Docker build context 742 MB → 6.6 MB.** 714 MB of it was
  `tests/MintPlayer.Spark.Tests/bin.stale/`, which `**/bin` does not match because the component is
  `bin.stale`. `tests/` and `**/*.Tests/` are now excluded; the image never needs them.
- **`*.pem` did not exclude the GitHub App private key.** Docker's `*` does not cross `/`, so it
  matched only a root-level file. Now `**/*.pem`.
- **68 `ClientApp/dist` artefacts were being committed.** M4 runs the Nx build, there was no ignore
  rule yet, and `git add -A` swept them in. Untracked with `git rm --cached`, and
  `apps/**/ClientApp/dist/` now prevents a recurrence.

The last three share one shape, and it is the same shape as R1: **a pattern that looks like it
covers a case and does not.** `**/coverage/` swallowing `Coverage/`, `**/bin` missing `bin.stale`,
`*.pem` missing a nested key. Worth checking any new ignore rule against a real path rather than
reading it.

## 7. Open questions

~~**Nx project naming.**~~ **Decided:** `@spark-apps/code-coverage` for the SPA, inferred
`CodeCoverage` for the host — `@spark-apps/`, not `@spark-demo/`, because this is a product.

~~**Test-project placement.**~~ **Decided:** `apps/CodeCoverage/CodeCoverage.Tests/`, keeping the
app self-contained, with a `project.json` overriding the `test` target. That override turned out to
be load-bearing rather than cosmetic: `@nx/dotnet` infers a `test` target as bare `dotnet test` with
no `--collect`, so without it the project would have run and measured nothing.

- **`coverlet.runsettings` at this repo's root** — still open, but now with a number attached.
  There is no runsettings here, so generated code is instrumented: **79.90% including `*.g.cs` and
  `obj/` versus 82.35% excluding**. Adopting the Coverage repo's `ExcludeByFile` is worth ~2.5
  points on the reported figure for no change in tested behaviour.
- **`self-coverage-PRD.md` vs `docs/codecov/`.** After the merge, one repo has two documents
  answering "how does this repo measure its own coverage". They should be reconciled; this PRD does
  not decide how.
- **Does the old repo keep its issues?** Transferring twenty-one merged PRs and the issue history is
  a manual GitHub operation, not part of this plan.

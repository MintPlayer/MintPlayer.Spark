# Plan — Absorb the Coverage app into the MintPlayer.Spark workspace

**Companion to:** [coverage_monorepo_PRD.md](coverage_monorepo_PRD.md) · **Date:** 2026-09-01

Eighteen milestones on **one branch, one pull request** — including the `mintplayer-ng-bootstrap`
change, framework fixes to Spark's own build targets, a coverage push, and the emptying of the old
repo. Test suites run **once**, in M15; intermediate milestones are verified by building and by reading
the code. The single exception is M5, which runs one small suite deliberately.

Ordering is not negotiable in four places: M0 unblocks git from seeing the files at all; M1 must precede
the copy so nothing moves twice; M4 cannot be split; M5 must precede the solution-wide assertion swap so
the library is proven on a small project first; and M11 must precede M12 so coverage is measured over
both languages before anyone targets a number.

Three milestones are framework work rather than app work — M8 (build targets), M12 (coverage) and the
Spark half of M6. They are here because absorbing Coverage is what surfaces them.

---

## M0 — Make `.gitignore` stop swallowing `Coverage/`

**Gates everything.** Verified: `git check-ignore -v Coverage/App_Data/security.json` matches
`.gitignore:156:**/coverage/`, because `core.ignorecase=true`.

1. Replace the unanchored Coverlet block at `.gitignore:152-156` with paths anchored to where coverage
   output is actually written, mirroring what the Coverage repo already does:
   - `tests/*/coverage/`
   - `apps/*/*/ClientApp/coverage/`
   - `apps/*/*/coverage/` (for `Coverage.Tests`)
   - `libs/node_packages/*/coverage/`
   - `apps/Coverage/action/coverage/`
   - keep `coverage*.json` / `coverage*.xml` / `coverage*.info`; drop bare `coverage/` and
     `**/coverage/`.
2. Anchor `.gitignore:70` `artifacts/` to `/artifacts/`.
3. **Delete** the `Demo/*/*/AGENTS.md` and `tests/*/AGENTS.md` rules (`.gitignore:390-397`) along with
   their rationale comment. Under D5 the generated files become tracked pointers, not copies — the
   comment's objection ("committing the copies would duplicate it and put a diff in every demo on
   every edit of the guide") no longer applies to a handful of stable lines that change only when a
   path changes. Do not add an `apps/` replacement rule. Sequencing note: the pointers do not exist
   until M8, so this step only removes the rules; the files get committed there.
4. Ensure nothing ignores `apps/Coverage/action/dist/`; add an explicit `!` negation if a `dist/` rule
   reaches it. Add `apps/Coverage/action/node_modules/` if not already covered.

The docs folder is named **`docs/coverage-app/`** rather than `docs/coverage/` so it sidesteps this
trap by name instead of depending on step 1.

**Verify:** `git check-ignore -v` returns non-zero for probe files at
`apps/Coverage/Coverage/App_Data/security.json`, `apps/Coverage/action/dist/index.js`,
`apps/Coverage/Coverage/ClientApp/src/spark-auth.setup.ts`, `docs/coverage-app/PRD.md` and
`Demo/HR/HR/AGENTS.md`; and still *matches*
`tests/MintPlayer.Spark.Tests/coverage/x/coverage.cobertura.xml`. Prove nothing regresses:
`git ls-files | git check-ignore --stdin` must print nothing.

## M1 — `git mv Demo apps`

Rename first, so Coverage is copied into its final home exactly once. `Demo/HR/HR` → `apps/HR/HR` is
the same depth, so all `..\..\..\libs\...` references, `tsconfig.base.json` extends chains and
`Targets\*` imports survive untouched. Only literal `Demo` path segments change — the complete list,
enumerated from `git grep`:

| File | What changes |
|---|---|
| `MintPlayer.Spark.sln` | project paths + the `Demos` solution folder (rename it `apps`) |
| `MintPlayer.Spark.slnLaunch` | lines 6, 11 |
| `package.json` | 4 `workspaces` entries |
| `.gitignore` | lines 385, 386, 396 |
| `.github/workflows/pull-request.yml` | the two hardcoded verify loops |
| `.github/workflows/webhooks-demo-deploy.yml` | `paths:` allowlist, `file:` Dockerfile path, the raw.githubusercontent compose URL in the ssh script |
| `.github/dependabot.yml` | line 27 |
| `.vscode/launch.json` | lines 9, 11, 26, 28 |
| `.vscode/tasks.json` | line 10 |
| `tests/MintPlayer.Spark.E2E.Tests/MintPlayer.Spark.E2E.Tests.csproj` | line 50 `AdditionalFiles` |
| `tests/MintPlayer.Spark.E2E.Tests/_Infrastructure/FleetTestHost.cs` | lines 485, 502, 506, 603 |
| `tests/MintPlayer.Spark.E2E.Tests/_Infrastructure/CarFixture.cs` | doc comment, line 10 |
| `Demo/WebhooksDemo/WebhooksDemo/Dockerfile` | the selective csproj `COPY` list |

The `Demo.Car` / `Demo.Person` hits in `tests/MintPlayer.Spark.Tests/**` are CLR type-name fixtures,
**not** paths — leave them alone.

Then `npm install` at the root to rewrite the `workspaces` paths in `package-lock.json`.

**Verify:** `dotnet build MintPlayer.Spark.sln`; `npx nx show projects` still lists 40; `npm ci` clean;
a grep for a remaining `Demo/` or `Demo\` outside `docs/` and the type-name fixtures returns nothing.

## M2 — Copy the Coverage tree in (filesystem commands only)

`cp -r` / `copy` only — no file is read and retyped. Source `C:\Repos\Coverage`.

| From | To |
|---|---|
| `Coverage/` | `apps/Coverage/Coverage/` |
| `Coverage.Library/` | `apps/Coverage/Coverage.Library/` |
| `Coverage.Tests/` | `apps/Coverage/Coverage.Tests/` |
| `action/` | `apps/Coverage/action/` |
| `tools/` | `apps/Coverage/tools/` |
| `docs/` | `docs/coverage-app/` (triaged in M13) |
| `docker-compose.yml` | `apps/Coverage/docker-compose.yml` |
| `README.md` | `apps/Coverage/README.md` |
| `.env.example` | `apps/Coverage/.env.example` |
| `coverlet.runsettings` | `apps/Coverage/coverlet.runsettings` |

**Do not copy:** `.git/`, `.vs/`, `.playwright-mcp/`, `tmp/`, `artifacts/`, `node_modules/` (×3),
`bin/`, `obj/`, `dist/` except `action/dist/`, `Coverage.Tests/TestResults/`,
`Coverage/ClientApp/coverage/`, `Coverage/Coverage.csproj.user`, `Coverage.slnx`, `.gitignore`,
`.dockerignore`, `.claude/settings.local.json`.

Two of those deserve a word. **`Coverage.slnx`** is dropped because its three projects join
`MintPlayer.Spark.sln` in M3. **`.claude/settings.local.json`** is dropped rather than merged: its four
entries are all stale (a dead scratchpad UUID and an `ilspycmd` invocation against `preview.46` in the
NuGet cache) — nothing to salvage.

**Delete after copying:** `apps/Coverage/Coverage/AGENTS.md` and
`apps/Coverage/Coverage.Tests/AGENTS.md`. Both are build-generated, and M8 replaces them with
pointers. Their byte-identity is not a curiosity — it is the bug M8 fixes, diagnosed in PRD R10.

Keep `ClientApp/src/spark-auth.setup.ts` only if M0's negation covers it; otherwise let the
Authorization targets regenerate it.

**Merge additively, never overwrite:** `.dockerignore` — fold Coverage's rules (`docs/`, `action/`,
`tmp/`, `*.md`, `.env`, `*.pem`, `.playwright-mcp/`) into this repo's root file, re-expressed relative
to the new root. Only the build-context root's `.dockerignore` is honoured, so getting this wrong
either breaks the demo image builds or ships a `.pem` into a layer.

**Verify:** `git add -A --dry-run` stages the expected count (≈205 after exclusions); `git status`
shows no unexpected deletions.

## M3 — .NET wiring

1. `apps/Coverage/Coverage/Coverage.csproj`: drop all ten `MintPlayer.Spark.*` `PackageReference`s; add
   `ProjectReference`s to `..\..\..\libs\{spark\MintPlayer.Spark, authorization\..., controllers\...,
   cron\..., messaging\..., migrations\..., webhooks\MintPlayer.Spark.Webhooks.GitHub,
   webhooks\...DevTunnel}`, plus `source_generators\MintPlayer.Spark.SourceGenerators` as
   `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`. Add the explicit imports
   (`spark-authorization.props` at the top; `spark-authorization.targets` and `spark.targets` at the
   bottom) — automatic `buildTransitive` import does **not** apply to `ProjectReference` consumers.
   Keep `MintPlayer.AspNetCore.SpaServices`, `MintPlayer.SourceGenerators*`, `YamlDotNet` and the
   `AdditionalFiles` block; add the `culture.json` conditional line to match the demos.
2. `Coverage.Library.csproj`: `MintPlayer.Spark.Abstractions` package → `ProjectReference`.
3. `Coverage.Tests.csproj`: `MintPlayer.Spark.SourceGenerators` → analyzer-style `ProjectReference`;
   keep `EnableSpaBuilder=false`. FluentAssertions is handled in M5 — leave it alone for now so this
   milestone stays a pure reference swap.
4. Pin the floating `Microsoft.AspNetCore.Authentication.JwtBearer` `10.0.*` to the exact version the
   demos resolve.
5. Add all three projects to `MintPlayer.Spark.sln` under a new `apps/Coverage` solution folder.
6. Add a `Coverage` profile to `MintPlayer.Spark.slnLaunch`.
7. Confirm ports 5200/5201 in `Properties/launchSettings.json` are unchanged — the GitHub App callback
   URL is registered against 5200, so do not renumber — and that the four `--spark-*` CLI profiles
   survived the copy.

**Verify:** `dotnet build MintPlayer.Spark.sln`; `grep -rn "MintPlayer.Spark.*Version" apps/Coverage`
finds no Spark package reference.

## M4 — npm / Nx wiring (single atomic step)

1. Add `apps/Coverage/Coverage/ClientApp` to the root `package.json` `workspaces`.
2. Delete `apps/Coverage/Coverage/ClientApp/package-lock.json`; strip its dependency lists to the demo
   shape (Spark Angular packages only) and point `start`/`build`/`test` at
   `nx run @spark-apps/coverage:<target>`.
3. Lift Coverage-only deps to the root `package.json`: `@angular/cdk`,
   `@mintplayer/ng-click-outside`, `@mintplayer/ng-focus-on-load`, `@mintplayer/web-components`
   2.13.0, `highlight.js`, `lit`. Resolve `@mintplayer/ng-bootstrap` 22.16.0-exact against this repo's
   `^22.17.0` in favour of `^22.17.0`, and fix the fallout — that span crosses the accordion Lit
   shadow-DOM change, so budget UI time here.
4. Convert `ClientApp/angular.json` into `ClientApp/project.json` named `@spark-apps/coverage`, using
   `@nx/angular:application` / `:dev-server` / `:unit-test` with **workspace-relative** paths, modelled
   on `apps/HR/HR/ClientApp/project.json`. Carry Coverage's own settings verbatim:
   `loader: {".svg": "text"}`, its style list, its budgets, and its `unit-test` coverage excludes
   (`src/**/*.spec.ts`, `src/main.ts`, `src/spark-auth.setup.ts`, `src/app/app.config.ts`). Delete
   `angular.json` — this repo has none.
5. Add `apps/Coverage/Coverage/project.json`:
   `{ "name": "Coverage", "implicitDependencies": ["@spark-apps/coverage"] }`.
6. Add `apps/Coverage/Coverage.Tests/project.json` with a `test` target running
   `dotnet test --no-build --no-restore --collect:"XPlat Code Coverage" --results-directory coverage`,
   and widen CI's `tests/*/coverage/**` glob to cover `apps/*/*/coverage/**` (M10).
7. Make `ClientApp/tsconfig.json` extend `../../../../tsconfig.base.json` so `@mintplayer/ng-spark`
   and `-auth` resolve to library **source**. All-or-nothing: two resolutions in one app tree means
   duplicate Angular DI tokens.
8. `npm install` at the root only.

**Verify:** `npx nx show projects` lists `Coverage`, `Coverage.Library`, `Coverage.Tests`,
`@spark-apps/coverage`; `npx nx run @spark-apps/coverage:build` succeeds; touching
`libs/node_packages/ng-spark/src/public-api.ts` marks the SPA affected.

## M5 — Drop FluentAssertions from `Coverage.Tests` (the pilot)

Coverage is on **8.8.0** — the paid-licence major — so it cannot stay, and at 367 call sites over 25
files it is the right place to prove `MintPlayer.Assertions` before touching 3800 Spark sites.

1. `Coverage.Tests.csproj`: remove `FluentAssertions 8.8.0`, add `MintPlayer.Assertions 1.0.0`
   (published to nuget.org and GPR from `MintPlayer.Dotnet.Tools`; it stays a `PackageReference`,
   like `MintPlayer.SourceGenerators`, because it lives in another repo). One reference brings the
   library, the equivalency source generator and the analyzers.
2. Swap the 25 per-file `using FluentAssertions;` for `using MintPlayer.Assertions;` — or, better,
   delete them and add a csproj `<Using Include="MintPlayer.Assertions" />` to match how the Spark
   test projects import their assertion library.
3. Fix the known non-compiling shapes. In Coverage specifically: 2 `.Subject` sites
   (`Controllers/UploadsControllerStatusTests.cs:340,361`, both
   `BeOfType<NotFoundResult>().Subject`) → `.Which`. Coverage has no `WithMessage`, no `Throw`, no
   `AssertionScope` and no `BeEquivalentTo` options lambda, so nothing else should need hand work.
4. Let the analyzers' code fixes handle any rename (`BeGreaterOrEqualTo` →
   `BeGreaterThanOrEqualTo`, `WithInnerExceptionExactly` → `WithInnerExactly`, and two more).
5. Meta-tests that assert on a failure exception must expect
   `MintPlayer.Assertions.AssertionFailedException`, not `XunitException`.

**Verify:** `dotnet test apps/Coverage/Coverage.Tests` green. This is the one place in the plan where a
single project's suite runs before the M13 sweep — it is the gate on D4, and running it here is far
cheaper than discovering a library problem after 4167 sites have moved.

## M6 — Drop FluentAssertions from the four Spark test projects (solution-wide)

3800 remaining call sites, and cheaper than that number suggests: all four projects import FA through a
csproj-level `<Using Include="FluentAssertions" />`, so this is **four** package references and **four**
global usings, not 272 file headers.

1. In each of `tests/MintPlayer.Spark.Tests`, `.SourceGenerators.Tests`, `.E2E.Tests`,
   `.Client.Tests`: `FluentAssertions 7.2.2` → `MintPlayer.Assertions 1.0.0`, and
   `<Using Include="FluentAssertions" />` → `<Using Include="MintPlayer.Assertions" />`. Also remove
   the four stray per-file `using FluentAssertions;` in `MintPlayer.Spark.Tests`.
2. **Review all 69 `WithMessage` sites individually.** `MintPlayer.Assertions` matches the glob
   **case-sensitively**; FluentAssertions is case-insensitive by default. These compile either way and
   can pass or fail for the wrong reason — this is the only silent-breakage class in the swap. They
   cluster in `Authorization/SecurityConfigurationValidatorTests.cs`,
   `Authorization/SecurityConfigurationLoaderTests.cs` and `Services/QueryLoaderTests.cs`. Where the
   original intent was case-insensitive, use the explicit `StringComparison` overload rather than
   editing the pattern.
3. Rewrite the 16 remaining `.Subject` sites to `.Which` — 7 of them in
   `Streaming/StreamingDiffEngineTests.cs`, plus `Webhooks/GitHub/SparkBuilderExtensionsTests.cs:112`
   and `Services/ModelSynchronizerTests.cs:551`.
4. Handle the five genuine gaps:
   - `SubscriptionWorker/RetryNumeratorTests.cs:94` `BeApproximately` → `BeCloseTo(expected, delta)`.
   - `Reflection/ReflectionCacheTests.cs:136` `AllBeEquivalentTo` — no equivalent; rewrite as
     `AllSatisfy` with a `BeEquivalentTo` inside.
   - The 2 `NotContainAny` sites — confirm an equivalent exists; otherwise express as
     `NotContain` per item or `OnlyContain`.
   - The 11 `BeOneOf` sites — it exists for numeric/enum/DateTime but **not** strings or objects.
     Check each subject's static type.
   - Any `BeEquivalentTo` whose subject is statically a dictionary — not defined there; cast to
     `object`.
5. `Throw<T>().Where(...)` (44 sites) and `.And.Message.Should()` chains work unchanged. The
   worst single line is `Services/QueryLoaderTests.cs:126`
   (`Throw` + `WithMessage` + `.And.Message.Should().Contain(...)`) — verify it by hand.
6. Update the three FluentAssertions examples in `MintPlayer.Spark.Testing`'s XML doc comments
   (`RqlRecorder.cs:39,44`, `SparkSharedDatabase.cs:49`, `SparkSharedTestDriver.cs:23`). They are
   comment text only and do not compile, but they document idioms to package consumers.

**Verify:** `dotnet build` clean; `grep -rn "FluentAssertions" --include=*.csproj --include=*.cs .`
returns nothing. Suites run in M13.

## M7 — Move `action/` and update every consumer (D2)

New reference: `MintPlayer/MintPlayer.Spark/apps/Coverage/action@master`.

1. `.github/workflows/pull-request.yml` and `dotnet-build-master.yml`: replace
   `MintPlayer/CodeCoverage/action@master` with local `./apps/Coverage/action` — same repo now, no need
   to round-trip through GitHub.
2. `C:\Repos\mintplayer-ng-bootstrap`: update its workflow to the new external path. Lands in this same
   unit of work per the one-PR rule; if cross-repo writes are blocked from this session, prepare the
   exact diff at `docs/coverage-app/ng-bootstrap-action-path.md` and apply it from a session rooted in
   that repo **before** merging.
3. Add the stale-`dist` gate to this repo's CI: `npm ci && npm run test:coverage`, then
   `npm run build`, failing if `git status --porcelain dist` is dirty.
4. Do **not** add `action/` to the root npm workspaces. It is a CommonJS node20 bundle with its own
   `tsconfig`, `typescript ^5.9.0` against this repo's `6.0.3`, and `@vercel/ncc`; hoisting it invites a
   bundle change and the committed `dist/` is contract. Keep its private lockfile.

**Verify:** `grep -rn "MintPlayer/CodeCoverage" .github` returns nothing;
`cd apps/Coverage/action && npm ci && npm run build && git status --porcelain dist` prints nothing.

## M8 — Fix the shipped build targets (D5 + PRD R10/R11)

Framework work, not app work. It lands here because Coverage's arrival is what makes the AGENTS.md
collision live, and because M9 onward assumes a clean build.

### M8a — `AGENTS.md` pointer mode

Introduce `$(SparkAgentsGuideMode)`, defaulting to `Copy` in `spark.targets`
(`Condition=" '$(SparkAgentsGuideMode)' == '' "`), and set it to `Pointer` **once** in this repo's root
`Directory.Build.props`. Because that file is imported by every project under the repo root, one line
covers the four demos, the four test projects and Coverage's two — no per-csproj edit, and no external
consumer ever sees it. Prefer this over inferring from `$(NuGetPackageRoot)`: a greppable switch beats
a heuristic, and it keeps the option of having one project exercise the real copy path.

1. Split `CopySparkAgentsGuide` into the existing copy target (now also gated on
   `'$(SparkAgentsGuideMode)' == 'Copy'`) and a new `WriteSparkAgentsGuidePointer` gated on `Pointer`.
2. The pointer target must **not** declare `Inputs`/`Outputs` — use
   `<WriteLinesToFile … Overwrite="true" WriteOnlyWhenDifferent="true" />`. Same incremental benefit,
   and it is precisely the `Inputs`/`Outputs` pair that causes the first-writer-wins wedge.
3. Compute the link with
   `$([MSBuild]::MakeRelative('$(MSBuildProjectDirectory)', '$([System.IO.Path]::GetFullPath('$(SparkAgentsGuideSource)'))'))`
   and `.Replace('\','/')` so it renders in every Markdown viewer. Give the pointer front-matter saying
   it is generated and naming the source, so nobody hand-edits it back into a copy.
4. **Mirror all of it in `spark-testing.targets`.** Read the same `$(SparkAgentsGuideMode)` from both
   files rather than introducing a second property — the two packages already share
   `$(MintPlayerSparkSourceGeneratorsReferenceValidated)`, so cross-package property coupling is
   established here. Otherwise test projects keep getting full copies while demos get pointers.
5. **Guard the collision on the copy path too**, since external consumers still hit it: a project that
   references both packages auto-imports both targets, and today the loser is skipped silently
   forever. Either gate `CopySparkAgentsGuide` on `'$(IsTestProject)' != 'true'`, or give the Testing
   target a distinct default filename. Do not leave it documented-but-undefended.
6. Commit the ten pointer files (M0 removed the ignore rules).

**Verify:** build twice; `git status` clean the second time. Each pointer resolves to the correct
guide — Spark's for apps, Testing's for test projects — which is the thing that is wrong in Coverage
today. Confirm an external-style consumer still gets a full copy by building with
`-p:SparkAgentsGuideMode=Copy` in a scratch project.

### M8b — `spark-allfeatures.targets` NuGet break

`libs/all_features/MintPlayer.Spark.AllFeatures/Targets/spark-allfeatures.targets` adds two
`ProjectReference`s through `$(MSBuildThisFileDirectory)..\..\` with no condition, and
`MintPlayer.Spark.AllFeatures.csproj:60` packs it to `buildTransitive/` — so NuGet auto-imports it and
those paths do not exist in a package layout. The file's own comment already says it *"is not needed"*
when consumed via NuGet. Either wrap the `ItemGroup` in
`Condition="Exists('$(MSBuildThisFileDirectory)..\..\MintPlayer.Spark.AllFeatures.SourceGenerators\MintPlayer.Spark.AllFeatures.SourceGenerators.csproj')"`
or stop packing the file. Prefer the condition — it keeps the in-repo convenience and makes the intent
explicit.

**Verify:** `dotnet pack` the AllFeatures project, then restore a scratch console app that
`PackageReference`s the produced `.nupkg` from a folder feed. It must restore.

## M9 — Regenerate model and security artefacts

Copied `App_Data` was generated against `preview.68` **packages**; Spark-from-source may differ.

1. `dotnet run --project apps/Coverage/Coverage -- --spark-synchronize-model`
2. `dotnet run --project apps/Coverage/Coverage -- --spark-synchronize-security`
3. Re-run both. **A second run must produce an empty diff** — synchronize is required to be a fixed
   point; a non-empty second diff is a Spark bug to fix here, not a file to commit.
4. Commit the resulting `Model/*.json`, `modelHashes.json`, `security.json`, `securityPosture.txt`.
   Review the `securityPosture.txt` diff line by line: it is the anonymous-surface baseline (11 lines
   today), and any new line is a newly anonymously reachable right.

**Verify:** `--spark-verify-model` and `--spark-verify-security` both exit 0.

## M10 — First real run

`dotnet run --project apps/Coverage/Coverage` — and nothing else. The host spawns the dev server
itself; do not start `ng serve`/`npm start` alongside it, and do not run `ng build`/`ng test` against
this workspace while it is running.

Wait for `➜ Local: http://localhost:NNNNN/` — that, not `Now listening on:`, is the signal the app is
serviceable. Then exercise `https://localhost:5200`: sign-in, an organization page, a repository page,
a build page with the coverage column, and a badge URL. Requires a local RavenDB at
`http://localhost:8080` with `PublicServerUrl` set to localhost, not `host.docker.internal`.

## M11 — CI

Coverage's `ci.yml`/`publish.yml` are **not** copied; their jobs fold in.

1. `pull-request.yml`: append `apps/Coverage/Coverage` to **both** hardcoded verify loops — the one
   place a new app is otherwise silently skipped.
2. Fold in the Angular and action coverage uploads: Coverage's SPA suite and the action's Vitest suite
   become additional `flags:` on the existing upload step (`dotnet`, `angular`, `action`).
3. Wire `apps/Coverage/tools/rebase-lcov-paths.mjs` into the upload path and extend its invocations to
   every JS project emitting a `src/main.ts` — eight now. Keep its `node --test` self-test in CI.
4. Widen the coverage glob to include `apps/*/*/coverage/**` (paired with M4.6).
5. Confirm `nx affected` genuinely covers Coverage before relying on it — the host's
   `implicitDependencies` is what links the .NET project to the SPA.
6. **Start uploading Angular coverage at all** — this repo's own, not just Coverage's. Both workflows
   currently glob only `tests/*/coverage/**/coverage.cobertura.xml`, and `disable-search: true` means
   nothing else is discovered, so `libs/node_packages/{ng-spark,ng-spark-auth}/coverage/cobertura-coverage.xml`
   (ng-spark 83.59%, ng-spark-auth 94.34%, ~2,000 lines) has been produced and silently discarded. The
   badge has been .NET-only. Add the explicit glob to `pull-request.yml` **and**
   `dotnet-build-master.yml` — the latter is the baseline the server diffs PRs against, so a
   PR-only fix would read as a coverage cliff on every PR.

   Do this **before** M12. It moves the reported number on its own, and a target set against the
   .NET-only baseline would be measuring something that no longer exists.
7. The four Demo ClientApps have one scaffolded spec each and no Nx `test` target — they are
   build-verified only. Leave that as is; noting it so nobody reads their absence from the report as a
   regression.

## M12 — Raise coverage (D6)

Baseline, measured by unioning the four cobertura reports per file+line: **83.98%** (16,715 / 19,903
.NET lines). Target **≥90%**, and no shipping project at zero measurement. M11 must land first — until
Angular cobertura is uploaded, the reported number is .NET-only and will shift under this milestone.

Ordered by value ÷ effort. The first two are the reason this milestone exists at all.

1. **`SparkSecurityInitExtensions` (0/71) and `SparkSecurityVerificationExtensions` (0/47)** —
   `libs/spark/MintPlayer.Spark/Extensions/`. The implementations behind `--spark-synchronize-security`
   and `--spark-verify-security`, both at **zero**. CI's "the anonymous surface has not widened" gate is
   the only thing between a one-line `security.json` diff and a public endpoint, and the gate itself is
   untested — a false negative there is invisible by construction. ~8–12 cases: no drift → exit 0,
   widened anonymous surface → non-zero, narrowed surface, missing baseline, malformed `security.json`.
2. **`MintPlayer.Spark.SubscriptionWorker`** — the only shipping project with no measurement at all; no
   test project even references it. Add a `ProjectReference` from `MintPlayer.Spark.Tests`, then cover
   `SparkSubscriptionWorker<T>.RunSubscriptionLoopAsync` (20/77): create-if-not-exists, batch
   processing, transient-failure retry, cancellation, permanent-failure escalation. ~8–10 cases.
   Worker loops fail by quietly stopping — nothing throws, data just stops moving.
3. **Replication mTLS** — `SparkModuleCertificateExtensions.OnCertificateValidatedAsync` (0/42) and
   `SparkModuleCertificateExtensions` (16/45). Valid chain, wrong thumbprint, expired, unknown module,
   revoked, missing cert when required. ~8–10 cases. A rejection bug fails loudly; an *acceptance* bug
   is silent. `docs/findings-replication-mtls.md` is spec input.
4. **`OidcTokenGenerator` (65%) + `OidcSigningKeyService` (61%)** — key rotation, `kid` selection,
   expired-key pruning, per-grant-type claim shaping, `aud`/`iss`/`nonce`. ~12–15 cases. Wrong claims
   or a stale signing key mint tokens that *validate* but grant the wrong thing.
5. **Core CRUD error branches** — `ExecuteQuery.HandleAsync` (54.5%), `DeletePersistentObject.HandleAsync`
   (55.6%), `RefreshPersistentObject` (24.3%). ~12–18 cases. Best raw-percentage return per hour:
   `MintPlayer.Spark` holds the largest absolute gap at 979 uncovered lines and the `Endpoints/` and
   `Services/` fixtures already exist.
6. **Webhooks** — `Webhooks.GitHub.SparkBuilderExtensions` (37.5%) and DevTunnel
   `WebSocketDevClientService.ConnectAndReceive` (0/40). Signature validation, malformed payload,
   unknown event, reconnect/backoff. ~8–10 cases. DevTunnel at 29.6% is the worst assembly in the repo.
7. **`SparkCronScheduler.TryRunOnceAsync` (48.1%)** — overlapping-run suppression, throwing job, missed
   window, cancellation. ~6–8 cases. Two test files cover the whole scheduler today, and a cron job
   silently not firing is the classic invisible outage.
8. **SourceGenerators `ValueComparerExtensions` (33/315)** and the three `*ValueComparer` classes (0%) —
   the largest single uncovered block. Equality round-trips over the generator's model records. ~6–8
   cases. Ranked last because a wrong comparer costs stale generated code or a rebuild storm, not a
   production incident.

Every new test uses `MintPlayer.Assertions` (D4) — that is what "solution-wide" means here.

Items 1–8 close roughly 1,400–1,600 uncovered lines, which is the 83.98% → ~91% move. If the number
lands short, prefer adding cases to 1–4 over chasing percentage in easy code: this milestone exists to
retire silent-failure risk, not to move a badge.

## M13 — Docs triage

The twenty copied files land in `docs/coverage-app/`. Triage, not bulk retention:

**LIVE (3) — move and keep authoritative.** `upload-api.md` (the standing external upload contract:
"fields are added, never removed"); `roadmap-2026-08.md` (T0.1 backups and T1.1–T1.4 are still
unbuilt); `PRD.md` → rename to **`product-overview.md`**, since a bare `PRD.md` is meaningless in this
repo and would collide case-insensitively with `docs/prd/PRD.md` if ever routed there.

**HISTORICAL (11) — move, status-stamp, keep as the why-record.** `PLAN.md` → rename to
**`build-log-m0-m10.md`** (same generic-name problem); `upload-result-contract.md`,
`coverage-analyzer-suite.md`, `adoption-findings.md`, `adopt-generated-indexes.md`,
`adopt-spark-generic-ui.md`, `adopt-spark-preview-57.md`, `program-units-PRD.md`,
`program-units-plan.md`, `self-coverage-PRD.md`, `reauth-on-401.md`. Several are the *app* side of a
framework change whose Spark side also exists — keep both; they are complementary, not duplicates
(`adopt-generated-indexes.md` ↔ `issue_210_*`, `adoption-findings.md` ↔ `issue_274_*`,
`adopt-spark-preview-57.md` ↔ `issue_281_*`, `program-units-*` ↔ `issue_324_*`/`issue_327_*`).

**SUPERSEDED (4) — replace with a one-line stub pointing at the Spark doc.** Each already admits it in
its own header:

| Coverage doc | Points at |
|---|---|
| `composed-queries-PRD.md` | `issue_327_PRD.md`, `issue_327_plan.md`, `release-notes-preview-67.md` — and it is knowingly wrong in three places, so it must not be read as design |
| `spark-issue-279-PRD.md` | `issue_279_PRD.md`, `issue_279_plan.md`, `release-notes-preview-56.md` |
| `spark-handoff.md` | `PRD-CoverageHandoff.md`, `coverage-handoff-plan.md`, `release-notes-preview-42.md` — Spark's PRD ingested it item-by-item and its `Origin:` line names this file |
| `spark-async-row-filter.md` | `issue_239_PRD.md`, `issue_239_plan.md` |

**OBSOLETE (2) — do not carry over.** `parse-session-stuck-pending.md` (single bug write-up, root
cause found and fixed; its durable lesson is already in `spark-handoff.md`) and
`ng-bootstrap-handoff.md` (a one-session change list for another repo, fully applied).

Also in this milestone:

1. **Status-stamp `docs/PRD-CoverageHandoff.md` and `docs/coverage-handoff-plan.md`.** Both still read
   `Status: Draft for review` (2026-08-07) although everything in them shipped in preview.42 and the
   IdP pass. Sitting next to the incoming Coverage set, they would read as pending work.
2. Add `docs/coverage-app/README.md` indexing the survivors with their status.
3. Re-point `adopt-spark-generic-ui.md`'s upstream scoreboard from GitHub issue URLs to the in-repo
   `docs/issue_*.md` files — that is a concrete gain from the merge.
4. `apps/Coverage/README.md` keeps the GitHub App setup walkthrough; fix its six `docs/…` links and add
   a pointer from this repo's root `README.md`.
5. Add a short `apps/` orientation note to `CLAUDE.md`: the `Demo/` → `apps/` rename, that Coverage is
   a production app, and that its `dotnet run` rule is the same as the demos'.

## M14 — Docker and deployment (D3)

1. Port `Coverage/Dockerfile` to `apps/Coverage/Coverage/Dockerfile` modelled on
   `apps/WebhooksDemo/WebhooksDemo/Dockerfile` — the current template, **not**
   `DemoApp/Dockerfile`, which targets .NET 8 and is already broken. Selective csproj `COPY` list for
   Coverage's full transitive lib closure, then `npm ci`, then
   `npx nx run @spark-apps/coverage:build --configuration=production --skip-nx-cache`, then
   `dotnet publish /p:UseAppHost=false /p:EnableSpaBuilder=false`. Keep `EXPOSE 8080` only — Traefik's
   port detection depends on it.
2. New workflow `.github/workflows/coverage-deploy.yml`, cloned from `webhooks-demo-deploy.yml`, with a
   hand-maintained `paths:` allowlist for Coverage's lib closure. **Keep
   `IMAGE_NAME: mintplayer/codecoverage` and the `:master` tag** so the VPS compose file needs no
   change. Carry over Coverage's ghcr visibility PATCH step and its 18×10s `/health/ready` readiness
   loop (a 503 means a bad GitHub App key — hard fail, do not retry past the loop).
3. `apps/Coverage/docker-compose.yml` keeps its hardcoded `Host(coverage.mintplayer.com)` Traefik
   labels and pinned RavenDB `7.1.10`. The VPS re-curls this file from raw.githubusercontent — update
   that URL to the new path, and remember the server's copy only changes when the workflow runs.

Do not deploy to production as part of verification. First deploy happens after merge.

## M15 — Test sweep

Batched to the end. **One sweep:**

- `dotnet build MintPlayer.Spark.sln`
- `npx nx run-many --target=test` — this repo's four projects plus `Coverage.Tests` and everything
  M12 added, all on `MintPlayer.Assertions`
- both SPA suites, and `apps/Coverage/action` Vitest
- `--spark-verify-model` + `--spark-verify-security` across all five apps
- a second full build, confirming the M8a pointers leave `git status` clean
- union the cobertura reports and confirm the D6 target: **≥90%** .NET line coverage, up from 83.98%

Known flake protocol: the E2E teardown flake and the RavenTestDriver "Server failed to start in 60 s"
cascade are documented failure modes. If they appear, re-run the **named** tests in isolation before
treating them as regressions, and never clean `RavenDBServer`.

Because M5/M6 moved 4167 assertion call sites, a failure here needs a triage step the sweep does not
normally have: decide whether it is a Coverage-migration regression, a `MintPlayer.Assertions` semantic
difference (null-subject-passes, unordered `BeEquivalentTo`, case-sensitive `WithMessage`), or a
library bug. A library bug needs a version bump in `MintPlayer.Dotnet.Tools` — 1.0.0 is published and
unchanged since, so there is no floating fix to pick up.

## M16 — Empty the old repository

After M13 is green and one deploy has succeeded from this repo:

1. Strip `MintPlayer/CodeCoverage` to a README pointing at
   `MintPlayer/MintPlayer.Spark/apps/Coverage`, with `action/` removed (D2 moved it).
2. Archive it. Issue/PR history stays where it is.

## M17 — Finish the deferred authorization migration

Unblocked by the merge — stalled since 2026-08-21 only because it needed cross-repo writes.

1. Move Coverage's type-level grants from any legacy `Everyone` group to `authenticated`, declared by
   id in the `wellKnown` map. **The type-level grant must move, not be deleted**:
   `EnsureAuthorizedAsync` runs before the row gate, so with no type-level right `IsAllowedAsync` and
   `GetRowFilterAsync` never run and every caller is denied, signed-in included.
2. Override `IsAllowedAsync` on the Account/Repository/Commit/Build actions classes to decide whether
   the caller may see that specific organization / repository / commit / build, keyed on the associated
   GitHub user.
3. Set `LocalCredentials = Disabled` and pass `localCredentials: 'disabled'` to `sparkAuthRoutes`.
4. Re-run `--spark-synchronize-security` and review the `securityPosture.txt` diff — the anonymous
   surface should **shrink**. Badge and public-repository endpoints are the intended exceptions;
   confirm each still resolves anonymously.

---

## Risks carried into implementation

| Risk | Mitigation |
|---|---|
| `.gitignore` silently ignoring the copy (PRD §R1) | M0 first, verified with `git check-ignore`; docs folder named `coverage-app` to sidestep it by name |
| ng-spark published→source flip is all-or-nothing (§R3) | M4 is one atomic step; verify by editing a lib file and watching the SPA rebuild |
| `ng-bootstrap` 22.16.0 → ^22.17.0 crosses the accordion shadow-DOM change | Expect visual fallout in Coverage's panels; budget UI time in M4 |
| `synchronize` not a fixed point (§R4) | M9.3 runs it twice and treats a second diff as a Spark bug |
| **`WithMessage` case sensitivity — the only silent breakage in D4** | M6.2 reviews all 69 sites by hand; a green suite does not discharge it |
| `MintPlayer.Assertions` is 1.0.0 and three commits old (§R9) | M5 pilots it on 367 sites before M6 touches 3800; library bugs need a bump in `MintPlayer.Dotnet.Tools` |
| Action consumers break between merge and consumer update (§R2) | ng-bootstrap diff prepared in M7.2 and applied before merge |
| E2E / RavenTestDriver flake masking a real regression | Re-run named tests in isolation; never clean `RavenDBServer` |
| lcov suffix ambiguity across eight JS projects (§R5) | M11.3 extends the rebase helper; its `node --test` stays in CI |
| AGENTS.md first-writer-wins wedge reappearing in the new pointer target (§R10) | M8a.2 forbids `Inputs`/`Outputs` on the pointer target; M8a.5 guards the copy path for external consumers |
| Tracked pointers dirtying the worktree on every build (D5) | `WriteOnlyWhenDifferent="true"`; M8a verifies a second build leaves `git status` clean |
| Coverage target chased in easy code instead of risky code (§R12) | M12 is ordered by silent-failure risk, not by line count; items 1–4 take priority if the number lands short |
| Coverage baseline shifting mid-milestone as Angular lines are folded in | M11 must land before M12; the 83.98% figure is .NET-only and will move once Angular uploads |

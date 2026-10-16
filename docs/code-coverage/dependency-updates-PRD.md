# Rolling dependency updates — PRD + plan (#419)

Issue: [#419 "package update + sdk updater"](https://github.com/MintPlayer/MintPlayer.Spark/issues/419). It has an
empty body, so this document is its design. App: `apps/CodeCoverage` (**production**, coverage.mintplayer.com).

## 0. Status

| Item | State |
|---|---|
| Investigation: codebase, prior art, ecosystem mechanics | ✅ 2026-10-09 |
| Grilling: decisions Q1–Q20 (§12) | ✅ 2026-10-09, owner |
| **Interim: one weekly Dependabot PR** (`multi-ecosystem-groups`, all ecosystems) | ✅ in PR #501 |
| **M3 dependency graph (§6) + an Account-page graph card** | ✅ in PR #501 (owner: "just that, nothing more"); the owner judged the card "perfect" in the browser, 2026-10-09 |
| Spikes S1–S11 (§10) | ⏸ **postponed**, possibly never; owner, 2026-10-09: "we'll see" |
| Every other milestone (§11: M1, M2, M4–M11) | ⏸ **postponed**, possibly never. Nothing beyond M3 is built |
| **Restart (§13): investigation + grilling** | ✅ 2026-10-15/16, owner. Decisions R1–R19 (§13.10, §13.11.4, §13.12) |
| Spikes S12, S13, S14, S17 (§13.12); S15 (= N1) and S19 (the net11 golden, in N3) | ✅ 2026-10-16 |
| N1 hook extraction · validators (gaps A/B/C, R14/R18; D-V1–D-V8) · N3 runner action + contracts + templates · N2/N4 config + policy engine (D-P1–D-P18) · N5–N7 saga, commands, bisect, propagation, enqueue, onboarding (D-L1–D-L15) · N8 migrate · N9 fleet view, readiness, P12, App permissions, user guide (D-N1–D-N5) | ✅ built 2026-10-16 |
| Owner privacy: settings visible only to managers and administrators; Spark `OnDisableActionsAsync` can withhold Read/Query, refused server-side (D-A1–D-A8, MintPlayer.Spark 11.0.0-preview.111) | ✅ 2026-10-16, red→green `OwnerPrivacyEndToEndTests` |
| Guides sidebar group (Merge queue setup, Dependency updater setup page, Connect an AI agent) | ✅ 2026-10-16 |
| **Local sweep** (before the privacy and guides work): Git 142, GitWriter 252, CodeCoverage 2719, action unit 276 + bundle 2, ClientApp 350, all green. Integration 6 passed and **6 skipped** (mvn, uv, go and composer missing locally; Renovate Docker test skipped on Windows), so their **first real run is CI** | ✅ 2026-10-16 |
| After the privacy work: CodeCoverage 2752/2752, the affected Spark classes 533/533. Guides: the 34 affected Angular specs | ✅ 2026-10-16 |
| S18 live (`PieterjanDeClippel/DependencyUpdaterTest`) | ✅ 2026-10-16, §13.13.1: onboarding, draft rolling PR, tree-equal no-op, approve + `uses:` carve-out, human-commit replay, scheduled PR merged by the queue; defects S18-D1–D4 |
| Event-driven onboarding, rate-limit retries, two library fixes (P13–P18, §13.13.2) | 🔍 investigated 2026-10-16; to grill |
| Deploy; GitHub App permissions (Dependabot alerts read, `release` + `dependabot_alert`) | ✅ 2026-10-16 (#522 deployed, both Apps changed, all 4 installations accepted) |

§1–§12 are the plan as grilled on 2026-10-09. **§13 replaces §7.1, most of §5, §7.8, §7.9 and §10–§11**, and §13.10–§13.12 are authoritative. Where they disagree with §1–§12, §13 wins. §12 still records the reasons for the choices §13 keeps. The user guide is [dependency-updates.md](dependency-updates.md).

**Interim (in #501):**
- **Config:** `.github/dependabot.yml` puts every ecosystem into one multi-ecosystem group, `all-dependencies`,
  weekly on Monday:
  - GitHub Actions, NuGet, and Docker (the CodeCoverage, DemoApp and SparkSchemas images);
  - npm at `/` plus `/apps/CodeCoverage/action`.
- **Angular majors** are ignored, because of the platform-major lock.
- **Security settings** (owner decision): Dependabot security updates stay off, and alerts stay on. Fixes arrive
  in the weekly PR.

**M3 as built in #501** (owner decisions 2026-10-09):
- **Producers:** declared names only, no registry confirmation. Edges only between repositories of the same
  account.
- **Endpoint:** `GET api/browse/accounts/{provider}/{login}/dependency-graph`, filtered exactly like
  `GetAccountRepos` (#453). A hidden repository's manifest is never loaded.
- **Scans:** triggered by a default-branch push that touches a manifest, the nightly reconcile (skipped when the
  tree sha is unchanged), and a repo being added.
  - Migration `M_202610091400_BackfillRepositoryManifestScans` queues one first scan per connected repository at
    deploy. It streams by id prefix, so it doesn't depend on an index, and uses the nightly per-day dedup key.
  - Without it every node had `scannedAt: null` until the nightly run, and the card stayed hidden. Seen locally
    2026-10-09.
- **Card:** on the Account page, via the app's existing `extraContentTemplate` branch in
  `po-detail-page.component.ts`. No Spark change was needed.
  - **Library:** Cytoscape.js 3.34.3 + cytoscape-dagre 4.0.1 (dagre `rankDir: LR`). They are lazy-loaded into
    their own chunks (442 kB + 46 kB). The initial bundle stays at the 1.37 MB master already had (CodeCoverage
    image build, 2026-10-09). Owner: "interactive, not a static SVG".
  - **States:** hidden (no access, or no repositories), "not scanned yet", "no dependencies", or the graph.
  - **Interactions:** pan, zoom and drag; hover highlighting; click a repository to open it; click an edge to list
    its packages; fit; a toggle for isolated repositories.
  - **Edge labels** use the node label size. Each is capped at **90% of its edge's length** (owner), measured
    endpoint to endpoint in graph units and refitted while a node is dragged. Longer labels end in "…", and edges
    too short show no label.
  - **Tooltips:** hovering an edge or node gives the full text as a native tooltip (the container's `title`
    follows the pointer): "from → to" plus the packages, or the repository's full name.
- **Not built from §6:**
  - the map-reduce "dependents of X" index (the endpoint doesn't need it);
  - locked versions (only the declared constraint is stored);
  - the `coverage.yml` `exclude` list;
  - re-reading only the changed paths (a changed tree is rescanned whole).
- **Judgement calls in the parsers, to confirm:**
  - Web SDK projects count as not packable unless `IsPackable=true`.
  - `PrivateAssets=all` references and test projects count as dev.
  - pip `requirements*dev*|*test*` files and the dev/test/lint/docs groups count as dev.

## 1. Problem

Dependabot opens one PR per package **per project directory**, and each PR runs the full CI.

Evidence (MintPlayer.Spark, measured 2026-10-09 with `gh pr list` / `gh run list`):
- **Volume:** the last 100 Dependabot PRs span 2026-07-04 → 2026-10-09, about **7 PRs/week**. 19 open, 81 closed,
  **0 merged**. The owner closes them and bumps by hand: `docs/prd/combined-dependency-bumps-plan.md` superseded
  27 of them.
- **The per-directory split** is a multiplier: `coverlet.collector 10.0.1→10.1.0` is **5 open PRs** (#493,
  #495–#498), and `@mintplayer/*-player 20.0→20.1` is 6 PRs (#469–#477).
- **CI cost:** each PR costs about **17–21 runner-minutes**. `pull-request.yml`'s build takes 15.7 min median, plus the
  coverage-upload, image-check and demo-image jobs. Dependabot also rebases every open PR when master moves.
- **What the cost really is:** all 14 MintPlayer org repos are public, and GitHub-hosted minutes are free there. So
  the cost is runner concurrency and queue time, load on the Nx cache server, coverage ingest, and triage time. For
  private repos it is billed minutes.
- **No coverage on Dependabot PRs:** they get no secrets or OIDC, so they can't upload coverage (hazard H12,
  `docs/coverage_branch_pr_badges_plan.md:156-161`).

## 2. Why a custom bot (and not configuring Dependabot or Renovate)

Dependabot and Renovate are better at the update work itself: they cover 20+ ecosystems and are free and
maintained. Dependabot already has one-PR-across-ecosystems (`multi-ecosystem-groups`, GA 2025-07-01).
Neither has **context**. Dependabot runs stateless, per repo, per directory, on a schedule. The CodeCoverage bot already
holds the accounts, every repo, CI outcomes and coverage in RavenDB. Its edge is everything built on that
(Q1, Q3):

1. A **dependency graph between repos** (§6), including manifests in subfolders.
2. **Release propagation** of the owner's own packages in graph order, with no cooldown (§7.6).
3. **Fleet feedback:** a red result in one repo holds that version for the others (§7.7).
4. **Readiness** for platform upgrades: "Angular 23: 8/10 dependencies ready" (§8).
5. **Platform locks** without config: npm major = Angular major, Microsoft.* major = TFM major (§5).
6. **Coverage on the update PR.** The App commits to a same-repo branch, so CI gets secrets. Dependabot can't (H12).
7. **Red-CI attribution and bisect** (§7.4). Neither incumbent reads CI results.
8. **Fleet view, licence-change holds, deprecation → replacement, release notes** (§8).

Decision Q1: **we build the update engine ourselves** (not Renovate- or Dependabot-as-engine). Decision Q2: the
**bot decides, and the user's own GitHub Actions execute.** The owner does not run other users' workloads on the VPS,
so no repo-controlled code runs on our infrastructure, and feed credentials stay in the user's GitHub secrets.

## 3. Goals / non-goals

Goals:
- G1. Per repository, **at most two bot PRs**: the rolling dependency PR and the security PR (Q18). Each is
  updated in place, never multiplied.
- G2. Opt-in at **account level** in the `Defaults` group ("Standaardinstellingen van dit account"), with a
  Repository override. Every policy knob is a **lookup-reference attribute** on Account, with an `Inherit` override
  on Repository (Q7, §4.1).
- G3. Ecosystems (Q19, all in):
  - NuGet (csproj, CPM, `packages.lock.json`), the `global.json` SDK and `dotnet-tools.json`
  - npm / pnpm / yarn (classic + Berry)
  - pip / Poetry / uv / pip-tools
  - Composer
  - GitHub Actions `uses:`
  - Docker `FROM` / compose `image:`

  Several ecosystems per repo, and manifests anywhere in the tree.
- G4. Compatibility is checked in stages (§5.2). A red PR is caused by test-level breakage, never by a version the
  bot could have known to be incompatible.
- G5. Release propagation, fleet feedback, readiness, fleet view, licence hold, deprecation replacement and release
  notes (Q3: everything except coverage-aware risk).

Non-goals (genuinely not done):
- **Coverage-aware risk per bump** (mapping imports/`using`s to file coverage). Excluded in Q3.
- **Source-code migrations** a bump needs (`nx migrate`, ESM conversions, runtime moves). The bot holds the group,
  reports it, and replays the human's fix (§7.3).
- **Account-level feed credentials stored by the bot.** They are replaced by GitHub secrets in the user's CI (Q2).
- **Automating coverage-action onboarding.** The coverage step lives inside the user's test job, so it can't be a
  stub workflow (discussed with Q4).

## 4. Settings and configuration

### 4.1 Account lookups, overridable per repository
Every row follows the existing `DeleteBranchOnPrClose` cascade (`Account.cs:92`, `Repository.cs:143-144`,
resolver pattern `Repository.ResolveDeleteBranchOnPrClose` at `:200-206`). Repository values are a three-or-more-state
lookup that includes `Inherit`, never a `bool?`: the generic form can't edit nullable bools (`Repository.cs:125-130`).

| Attribute | Values (default **bold**) | Decision |
|---|---|---|
| `DependencyUpdates` | Account bool **off**; Repository `Inherit`/`Enabled`/`Disabled` | G2 |
| `DependencyUpdatePolicy` | `PatchOnly` / **`MinorAndPatch`** / `LatestCompatible` | Q7 |
| `DependencyScheduleFrequency` | `Daily` / **`Weekly`** / `Monthly` | Q8 |
| `DependencyScheduleWeekday` | Mon–Sun (**Mon**), ignored for Daily | Q8 |
| `DependencyScheduleHour` | 0–23 (**6**); runs are spread across that hour | Q8 |
| `TimeZone` (Account only) | IANA id, **UTC** | Q8 (default chosen by the assistant) |
| `DependencyCooldown` | `0` / `1d` / **`3d`** / `7d` | Q16 |
| `DependencyAutoMerge` | **`Never`** / `PatchOnly` / `MinorAndPatch` / `Always` | Q13 |
| `DependencyBisectBudget` | `Off` / `1` / **`2`** / `4` / `8` CI runs | Q12 |

Edit rights already exist: `security.json:170,176`, with row filters in `AccountActions.cs:39-48` and
`RepositoryActions.cs:16-29`.

### 4.2 Package rules (Q7, Q9)
- **Account rules:** an AsDetail collection `DependencyRules` on Account. Each rule has a pattern (glob over package
  id), an ecosystem, an action (`pin <range>` / `ignore` / `allowMajor`), an optional `group`, and
  **`Enforced` (default true)**.
- **Repo rules:** the same shape, in `coverage.yml`.
- **Combination:**
  - An *enforced* account rule is a ceiling: a repo rule may narrow it (a tighter range, an extra ignore), never
    loosen it.
  - A *non-enforced* account rule is a default that a repo rule replaces.
  - Comment commands (§7.5) never override enforced rules.

### 4.3 `coverage.yml` `dependencies:` section (Q5, Q6)
This extends the existing `Feedback/CoverageYml.cs` (YamlDotNet, camelCase, unknown keys ignored). Like `gate:`, it is
read **from the base ref**, never from a PR head.

```yaml
dependencies:
  enabled: false                # may only opt OUT (Q6); opting in is the account/repo setting
  ignore: ["Microsoft.CodeAnalysis.*"]
  rules:
    - match: ["vitest", "@vitest/*"]
      allowMajor: true
      group: vitest
  exclude: ["samples/**", "**/fixtures/**"]   # paths skipped by discovery and the graph scan
  verify: auto                  # auto (default) | off | "<command>"   (Q12)
```

A malformed section falls back to the stored settings. The parse error is reported in the PR body and the fleet
view, the same as `gate:`'s `parseError`.

## 5. Version selection

### 5.1 Filters, in order
For each dependency the bot takes the **highest candidate that passes every filter**:
1. **Declared intent:**
   - A range in the manifest (`^22.1.0`, `~=1.4`, `^1.2`) is kept. The bot updates within it and raises the floor
     (`rangeStrategy: bump`).
   - Exact pins (NuGet `Version="x"`, `==`, `global.json`, `uses: …@v4`) follow `DependencyUpdatePolicy`.
2. **Rules:** account and repo rules (§4.2), and holds (§7.3–7.5).
3. **Prerelease policy:**
   - Offer a prerelease only if the current version already is one.
   - **Never downgrade.** The combined plan rejected MintPlayer.Assertions 1.1.0, Endpoints 11.0.0 and ASP.NET
     10.0.12 as downgrades against 11.x previews.
4. **Cooldown:** the candidate's age must be at least `DependencyCooldown`. **Own packages are exempt** (§7.6).
5. **Licence:** if the candidate's licence differs from the current version's (npm `license`, nuspec
   `license`/`licenseUrl`, PyPI `license`/classifiers, Composer `license`), it is held with a note. FluentAssertions 8
   is the precedent.
6. **Compatibility metadata of the candidate**, checked against the *resulting* set:
   - **npm:** `peerDependencies` and `engines.node`. Angular moves as a set, and `@mintplayer/ng-bootstrap@23`
     (peers `@angular/core ^23`) is rejected while the app is on 22. Exact peers chain: `@vitest/coverage-v8@5` ↔
     `vitest@5`.
   - **NuGet:** a dependency group compatible with the project's TFM (`NuGet.Frameworks`; this is NU1202).
     **Platform families** (`Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`, `Microsoft.EntityFrameworkCore.*`,
     `System.*`) cap their major at the TFM major.
   - **pip:** `requires_python`.
   - **Composer:** `require.php` / extensions, checked against `config.platform`.
   - **`global.json`:** stays on its major unless `allowMajor`.
7. **Groups move atomically.** Inferred groups come from shared peers and monorepo families (`@nx/*`+`nx`,
   `RavenDB.*`); configured groups add to them. If one member fails, the whole group is held.
8. **Alignment:** the same package gets the same version in every manifest of the repo. `coverlet.collector` was
   6.0.4 in one project and 10.0.1 in the others.

### 5.2 Compatibility stages (Q12)
| Stage | Where | Catches | CI spent |
|---|---|---|---|
| 1. Metadata | bot, instantly | peers, TFM, Python/PHP version, platform caps | none |
| 2. Resolver | Action, same run | ERESOLVE, NU1107/NU1202/NU1605, Composer conflicts. The plan is ordered by group; the Action drops a failing group, bisects locally, re-resolves | none |
| 3. `verify` build, **default on** | Action, same run | removed or renamed APIs. Compile errors are **attributed from the log**: TS `Module '"x"' has no exported member`, C# CS0246/CS0117 mapped type → assembly → package via `project.assets.json`. The named group is held and verify re-run | the user's minutes |
| 4. PR CI | the user's CI on the PR | test failures → §7.4 | 1 run + bisect budget |

`verify: auto` auto-detects the build per ecosystem: `dotnet build` of the discovered solution, or `npm run build
--if-present` per workspace. It costs a build of the user's minutes per update run, so the fleet view shows that
cost per repo.

### 5.3 Acceptance test
**S3:** replay `docs/prd/combined-dependency-bumps-plan.md` against master `bd217fcb`. Every taken and not-taken
row must be reproduced, or explained as a new rule.

## 6. Dependency graph between repositories

### 6.1 Producers: which repo publishes which package id
- **Declared:**
  - npm: a `package.json` with `name` and no `"private": true`.
  - NuGet: a packable csproj (`PackageId`, falling back to `AssemblyName`, including `Directory.Build.props`).
  - Python and Composer: `pyproject.toml` `[project].name`, `composer.json` `name`.
  - One repo can produce many: MintPlayer.Spark produces `@mintplayer/ng-spark` plus the `MintPlayer.Spark*` NuGets.
- **Confirmed** by the registry pointing back: npm `repository`, nuspec `repository url` / SourceLink.

### 6.2 Consumers: who references it, anywhere in the tree
- **Discovery:** `GET /repos/{o}/{r}/git/trees/{sha}?recursive=1` (one call), then filter by manifest file name and
  fetch only those blobs. This finds e.g. a `package.json` in a subfolder of the MintPlayer repo that depends on
  `@mintplayer/ng-video-player`.
- **Skipped paths:** `node_modules/`, `vendor/`, `bin/`, `obj/`, `dist/`, plus `coverage.yml` `exclude`.
- **Edge data:** consumer repo, manifest path, ecosystem, package id, constraint, locked version, dev or runtime.
- **Non-package edges:** `uses: MintPlayer/…@…` (the coverage-upload action), `FROM ghcr.io/mintplayer/…`, git
  submodules.
- **Cross-check:** `GET /repos/{o}/{r}/dependency-graph/sbom` (GitHub-only), in spike S5.

### 6.3 Freshness and storage
- **Updates:**
  - On a default-branch push, only the changed manifest paths are re-parsed. When the webhook's file list is
    truncated, the compare API fills it in.
  - A nightly full reconcile, the same pattern as `ReconcileForgeStateCronJob`.
- **Storage:** a `RepositoryManifest` document per repo (produced and consumed), plus a map-reduce index for
  "dependents of package X" and "dependencies of repo Y".
- **Cycles:** order only on runtime edges. A cycle of dev-only edges is treated as one unit.

### 6.4 Visibility (#453)
- Every graph, fleet-view and readiness row is filtered by the viewer's repo visibility. Missing ≡ no access: a
  private consumer of a public package must never appear for someone who can't see it.
- The graph is built per installation. Cross-account edges appear only to viewers who can see both sides.

## 7. Execution and the PR lifecycle

### 7.1 The split (Q2)

> ⚠️ **Superseded by §13 if P1–P4 are accepted.** The merge queue already ships a one-way `workflow_dispatch`
> → patch artifact → git-writer push path. §13.3 reuses it instead of `repository_dispatch` + an OIDC round trip.
1. **Trigger:** the bot sends a `repository_dispatch` to the repo. Contents write is enough, and the App already
   has it.
2. **The `dependency-update` Action, in the user's CI:**
   - discovers manifests;
   - queries feeds, private ones with the repo's own secrets;
   - sends candidates plus compatibility metadata to the bot, authenticated with **OIDC** like the coverage
     upload.
3. **The bot** applies §5 policy, holds, rules and fleet feedback, and returns a plan ordered by group.
4. **The Action** edits the manifests, regenerates the lockfiles (stage 2) and runs `verify` (stage 3).
   Untrusted code runs only here. It uses scripts-off flags where they exist: `npm --ignore-scripts`,
   `composer --no-scripts --no-plugins`.
5. **The Action uploads** the changed files and the applied/held report. The bot diff-validates them: only expected
   manifest and lockfile paths, and `.github/workflows` changes that are `uses:` ref edits only.
6. **The bot commits** with the App token (Git Data API: blobs → tree → commit, one commit per ecosystem). This
   triggers CI, which `GITHUB_TOKEN` pushes would not. The bot then upserts the PR.

**Shipping:** the Action is packaged like the coverage action (`apps/CodeCoverage/action`, ncc bundle,
`coverage-action-publish.yml`, prefixed tags `coverage-upload-v*`). Location `apps/CodeCoverage/dependency-action`,
tags `dependency-update-v*`, with the publish workflow generalised to handle both. It is probably a **composite**
action: conditional `setup-dotnet` (from `global.json`) / `setup-node` / `setup-python` / Composer steps, then
the TS core (S1).

**Silence detection:** a dispatch with no response within N hours (workflow missing, Actions disabled, minutes
exhausted, secret broken) is reported on the PR and in the fleet view.

### 7.2 Onboarding and permissions (Q4, Q20)
- **New permission:** the App requests **Workflows: write**, which Actions `uses:` bumps and the onboarding PR need.
  Existing installations get GitHub's accept-new-permissions prompt. Until they accept, the feature shows as
  unavailable there. Update `docs/code-coverage/hosting.md:254-259` and `apps/CodeCoverage/README.md:400-454`.
- **The onboarding PR**, opened when the feature becomes enabled for a repo:
  - adds the stub `.github/workflows/dependency-updates.yml` (`on: repository_dispatch, workflow_dispatch`, one step
    `uses: MintPlayer/…/dependency-action@dependency-update-v1`). The stub is kept current by the bot's own
    Actions bumps;
  - **edits `dependabot.yml`**: it removes the `updates:` entries for ecosystems the bot covers, and deletes the
    file if nothing remains;
  - **translates** the removed `ignore:` and `registries:` knowledge into the PR body, as suggested `coverage.yml`
    rules and the GitHub-secret names the Action will read;
  - recommends turning off Dependabot *security updates* (a repo setting), because §7.8 replaces them.
- **Workflow files:** the bot only changes them on its own branches, never on default branches.

### 7.3 Rolling PR, scheduled refresh and human commits (Q8, Q10, Q11)
- **Branch and PR:**
  - Branch `mintplayer/dependencies`; the PR number is stored on the `Repository` document.
  - Body tables per ecosystem: applied / held (with reason) / available-not-applied / ignored, plus release notes
    (§8).
- **At the scheduled slot** (frequency/weekday/hour in the account's time zone, spread within the hour), the bot
  recomputes from the default branch. It **pushes only if the resulting tree differs**. No new versions means no
  push and no CI.
- **Rebase:** only on conflict with the base, never just because master moved.
- **Human commits are replayed** by the Action (it has the checkout) on top of the recomputed bot commits:

  | Conflict in | Resolution |
  |---|---|
  | lockfile | never merged textually; regenerated from both sides' manifests |
  | a manifest version line the human changed | **the human's value wins**, and becomes a repo hold "X held at v (set by @user in <commit>)" that lifts automatically when a version **newer than the one the bot had proposed** appears (Q11) |
  | anything else | stop, keep the branch, comment |

  After every replay, stages 2–3 re-run before anything is pushed.

### 7.4 Red CI (Q12)
The bot already receives `check_run` events, and reads the conclusion on its own head commit.
- **Bisect, test failures only:** within `DependencyBisectBudget`.
  - **Suspicion order:** package names in failing stack traces, then majors, then versions held red elsewhere in
    the fleet.
  - **One re-run** of the deciding step before declaring a culprit, against flaky tests; it counts toward the
    budget.
  - The culprit group is held, with the failing run linked.
- **While bisecting:** a "bisecting (step i/n), don't merge" banner, and the bot's own check run on the head stays
  `in_progress`.
- **When the budget runs out:** the bot comments the failing checks and the groups ranked by suspicion.

### 7.5 Comment commands (Q14)
- **Commands:** `hold <group|package>`, `unhold <…>`, `bisect`, `refresh`.
- **Who:** commenters with repo `write`/`maintain`/`admin`, checked through the API per command. Others get 👎 and
  nothing happens.
- **Limit:** commands never override `enforced` account rules.

### 7.6 Release propagation and cooldown (Q13, Q16)
- **What starts it:** a release or push event from a producer repo (§6.1) starts propagation to its consumers in
  graph order. It is **debounced**: one PR update after 30 minutes with no further releases from that producer.
- **"Own" package** (exempt from the cooldown) requires **all three** of:
  - the producer repo is on the same account;
  - that repo declares the package id;
  - the registry publish coincides with a release or push event the bot received from that repo.

  A hijacked registry account publishing a look-alike therefore doesn't count as own.
- **Third-party packages** wait out `DependencyCooldown`.

### 7.7 Fleet feedback (Q15)
- **No waiting:** every repo updates on its own schedule.
- **Red holds the version:** if a version goes red at stage 4 in any repo, it is held for repos that haven't merged
  it yet.
- **Shared slot:** repos at the same slot are staggered within the hour by a ranking: line coverage, then flaky
  rate, then CI time. A green result from a low-coverage repo doesn't count as confidence anywhere.

### 7.8 Auto-merge (Q13)
- **What may merge:** `DependencyAutoMerge` decides that. Only **at the scheduled slot**, so there is at most one bot
  merge per period.
- **Mechanism:** it uses GitHub's native auto-merge, so branch protection stays authoritative. The bot **refuses**
  on repos whose default branch has no required status checks, and says why in the PR body. This guard was kept by
  the assistant and stands unless the owner objects.

### 7.9 Security PR (Q17, Q18)
- **Branch** `mintplayer/dependencies-security`. It holds **all** open advisory fixes, updated in place, and skips
  schedule and cooldown. It may auto-merge outside the slot, but only for security-only content.
- **Data:** the GitHub Advisory Database (GraphQL `securityVulnerabilities`), plus Dependabot alerts for private
  repos. That needs the *Dependabot alerts: read* permission, which is another accept prompt.
- **Overlap with the rolling PR:**
  - The rolling PR drops anything the security PR covers.
  - After a security merge, the rolling PR is rebased and its lockfiles regenerated.
- **Blocked fixes** (by an enforced rule or a platform cap) surface loudly: "vulnerable, fix blocked by rule X".
- **Duplicates:** an open Dependabot security PR for the same package is detected and mentioned, never duplicated.

## 8. Fleet view, readiness and PR enrichment
- **Fleet view:** an account page listing, per package, which repo is on which version, the holds and why, red and
  frozen PRs, silent dispatches, and the `verify` cost per repo. Every row is visibility-filtered (§6.4).
- **Readiness:** for each platform major (Angular, .NET TFM, Node, Python):
  - "N of M dependencies already support the next major", with the blockers listed;
  - shown per repo and per account.

  The move itself only happens via a rule change (`allowMajor` / pin).
- **Deprecation → replacement:** NuGet deprecation metadata `alternatePackage` becomes a suggestion in the PR body.
  npm only has a `deprecated` text, which is shown as is.
- **Release notes:** taken from GitHub releases or `CHANGELOG.md` of the package's repository, filtered to the
  version span, with breaking-change headings highlighted.

## 9. Security summary
- No repo-controlled code runs on our infrastructure (Q2). The bot reads repo files from the **default branch**
  only.
- The bot stores no feed credentials. The Action uses the repo's own GitHub secrets.
- The Action authenticates to the bot with OIDC, scoped to the repo the plan is for.
- Uploads are diff-validated (§7.1 step 5), and workflow changes are limited to `uses:` refs on the bot's own
  branches.
- "Own package" requires the three-way check (§7.6).
- Never splice package or registry names into RQL (repo rule). Package ids are values, so they go in query
  parameters.

## 10. Spikes

> ⚠️ §13.7 says which of S1–S11 survive the restart and adds S12–S18.

| # | Question | Done when |
|---|---|---|
| S1 | The composite Action: conditional `setup-*` per detected ecosystem, OIDC round trip (candidates → plan → upload), timing on this repo | End-to-end dry run on a scratch repo; time per stage recorded |
| S2 | Where each parser lives. Bot (.NET): `NuGet.Versioning`/`Frameworks`, `Semver` `ParseNpm`, PEP 440 and Composer constraint parsers. Action (TS): manifest edits that keep formatting | Byte-identical round trip except the changed versions, on real manifests from ≥10 public repos per ecosystem |
| S3 | The §5 selector against the combined plan (master `bd217fcb`) | Every taken and not-taken row reproduced or explained |
| S4 | Rolling-PR mechanics: App commit of uploaded files, tree-equal no-op, human-commit replay with the §7.3 conflict table | 3 scheduled runs with no new versions trigger 0 CI runs; a replayed fix survives a recompute |
| S5 | Dependency graph: tree scan + producer confirmation on the MintPlayer org (including the subfolder `package.json` in MintPlayer), compared with the SBOM API | Edge list checked by hand for the org; differences explained |
| S6 | Log attribution: TS and C# compile errors → package; package names in test stack traces | ≥80% correct attribution on a corpus of real failing builds (the combined plan's ESM and sass failures included) |
| S7 | `verify: auto` detection and its cost on this repo, CodeCoverage and a Python/Composer sample | Command chosen per repo; minutes measured |
| S8 | "Own package" correlation: registry publish time vs producer release/push events, for npmjs and nuget.org | Matching rule with its false-positive and false-negative cases documented |
| S9 | Advisories: GraphQL `securityVulnerabilities` coverage per ecosystem; the Dependabot-alerts permission; a fix that crosses a cap | A fix PR for a known advisory on a sample repo; the blocked case surfaces |
| S10 | Publishing a second action from the monorepo (generalise `coverage-action-publish.yml`, immutable-tag guard) | Both actions publish independently with their own prefixed tags |
| S11 | Metadata sources: licence per ecosystem, NuGet `alternatePackage`, release notes from GitHub releases / CHANGELOG | Extractor per ecosystem tested on real packages |

## 11. Milestones (one PR)

> ⚠️ Superseded by §13.8 if §13 is accepted (kept as the grilled baseline).
1. **M1 Settings:**
   - the §4.1 lookups on Account and Repository, with resolvers like `ResolveDeleteBranchOnPrClose`;
   - a migration;
   - translations (en/nl/fr) in the `Defaults` group.
2. **M2 Rules and config:**
   - `DependencyRules` AsDetail with `Enforced`;
   - the `coverage.yml` `dependencies:` parser, with tests shaped like `CoverageYmlTests`;
   - the combination rules from §4.2.
3. **M3 Dependency graph:** the tree scan, producers and consumers, `RepositoryManifest`, indexes, freshness on push
   and nightly, and visibility filtering.
4. **M4 The Action:** discovery, feed queries, manifest edits, lockfile regeneration with local group bisect,
   `verify` with log attribution, upload. Plus its packaging and publish workflow.
5. **M5 Selection engine:** §5.1 filters, registry metadata clients with caching (Packagist etiquette), the
   S3 replay as a regression test using recorded **real** registry fixtures.
6. **M6 Forge write surface:** `EForgeCapability.DependencyUpdates`, Git Data API commits, PR upsert on
   `IForgeIntegration` (no credentials in signatures, `IForgeIntegration.cs:24-27`), conformance entries.
7. **M7 Lifecycle:**
   - cron + queue `coverage-dependency-updates`, deduplicated by repo, within `ClaimTtl` or checkpointed;
   - dispatch and silence detection, tree-equal no-op, conflict-only rebase, human-commit replay.
8. **M8 Red CI and commands:** bisect within budget, banner + `in_progress` check, comment commands.
9. **M9 Propagation, fleet feedback, cooldown, auto-merge:** the debounce, the "own" check, staggering, the
   slot-only auto-merge with its required-checks guard.
10. **M10 Security PR:** advisory sourcing, the second branch, overlap handling, blocked-fix surfacing, the
    permission.
11. **M11 Fleet view, readiness, enrichment and onboarding:**
    - the account page, readiness, deprecation suggestions, release notes, the licence hold;
    - the onboarding PR (stub + `dependabot.yml` translation), the Workflows permission;
    - docs in `docs/code-coverage/`.

## 12. Decision log (grilled 2026-10-09; "owner" = decided by the repo owner)

| # | Decision | Rejected | Evidence / reason |
|---|---|---|---|
| Q1 | Build the update engine ourselves | Renovate as engine; Dependabot as engine | owner preference |
| Q2 | The bot decides, the user's Actions execute; same path for everyone | VPS sandbox; separate sandbox VM; Action-only | owner: no other users' workloads on the paid VPS. Consequence: no bot-stored feed credentials |
| Q3 | Everything except coverage-aware risk | core-only subsets | owner |
| Q4 | Request Workflows write; the bot opens an onboarding PR with a stub | manual onboarding; no Actions bumps | owner. Coverage-action onboarding is not automatable (it's a step inside the test job) |
| Q5 | Config in a `coverage.yml` `dependencies:` section | `.github/mintplayer.yml`; a separate file | owner |
| Q6 | The file can only opt out or tune; opting in is the setting | file opts in; file is the only switch | owner |
| Q7 | The exact-pin policy is an Account lookup + Repository override; account package rules exist | a fixed default | owner: "as configurable as possible" |
| Q8 | Schedule = frequency + weekday + hour lookups, spread within the hour | frequency only; cron string | owner. The time zone default (UTC) was chosen by the assistant |
| Q9 | Account rules carry `Enforced` (default true); enforced = a ceiling | account always wins; most specific wins | owner |
| Q10 | Replay human commits; conflicts resolved per the §7.3 table | freeze; freeze + refresh | owner ("with correct conflict resolution") |
| Q11 | The human's manifest value becomes a hold, lifted when a newer version than the bot's proposal appears | permanent pin; one-shot | owner |
| Q12 | Bisect test failures within a budget (default 2); `verify` on by default with log attribution | hold-only; unbudgeted | owner. Neither Dependabot nor Renovate reads CI results |
| Q13 | Auto-merge lookup (default Never), acting only at the slot; propagation debounced | a separate merge cadence; debounce only | owner: avoid a stream of patch merges on master per release |
| Q14 | Comment commands for repo writers; never over enforced rules | managers only for CI-spending commands; no commands | owner |
| Q15 | No canary waiting; a red result holds the version fleet-wide; stagger within the slot | automatic canary with wait; designated canary | owner |
| Q16 | Own packages bypass the cooldown (three-way check); cooldown lookup, default 3d | a short own cooldown; one cooldown for all | owner |
| Q17 | The bot handles advisories immediately | leave to Dependabot; report only | owner |
| Q18 | One dedicated security PR per repo with all advisory fixes | fixes in the rolling PR | owner |
| Q19 | All ecosystems | only the ones the owner uses | owner; this repo has npm, NuGet, global.json, dotnet-tools.json and Actions; pip and Composer were named in the request |
| Q20 | The onboarding PR edits `dependabot.yml` and translates removed rules into the PR body | warn only; ignore | owner |
| — | Branches `mintplayer/dependencies` / `-security`; private repos in scope (their minutes, visibility-filtered) | — | assistant defaults; not grilled |

## 13. Restart (2026-10-15): a generic engine on the workflow-hook pattern

**Status: grilled 2026-10-15. §13.10 is authoritative**; where §13.1–§13.9 disagree with it, §13.10 wins. Four parallel investigations on 2026-10-15:
- the merge-queue code, as reuse candidates;
- this PRD against the new requirement;
- external engines and ecosystem commands;
- where Dependabot and Renovate fall short.

Their findings are below, with sources. Proposals P1–P12 are the assistant's. Each needs an owner decision (§13.9).

### 13.1 What changed since §1–§12

1. **Owner requirement (2026-10-15):** "generic across several/all/any ecosystems". Restore and update commands
   must **never** run on the VPS: MSBuild targets, npm lifecycle scripts, `setup.py`, and Maven and Gradle plugins
   all run arbitrary code. Instead, the bot triggers a GitHub Actions workflow in the repo, *exactly like the merge
   queue's regenerate hook*.
2. **The merge queue (#515, #517) built the pipeline §7.1 had planned.** All references are under `apps/CodeCoverage/`.
   - **Dispatch.** `workflow_dispatch` with `return_run_details: true` (GitHub API since 2026-02-19), so the run id
     comes back in the 200 response. A 204 evicts with `dispatch-uncorrelated` (`GitHubForgeMergeQueue.cs:442-474`,
     `MergeQueueEngine.cs:939-943`).
   - **Waiting.** The `workflow_run` webhook, with a `PollRun` every 3 min as fallback, and a `Timeout` step per
     workflow (`MergeQueueEngine.cs:66,949,951,1376-1381`). The outcome check requires `head_sha == PreparedSha` and
     `conclusion == success` (`:1132-1175`).
   - **Results.** The workflow never pushes. It uploads a `git diff --binary` artifact, read with a capped zip read
     at 8 MB (`GitWriterSidecarClient.cs:39-42`, `GitHubForgeMergeQueue.cs:547-620`).
   - **Validation.** `PatchValidator` (`CodeCoverage.GitWriter/Jobs/PatchValidator.cs:8-34`) checks the path
     allow-list. It refuses `.github/`, `.gitmodules`, `.gitattributes`, symlinks, mode changes, binary patches and
     submodules. It runs the rule validators after apply, re-diffs to catch an `UnexpectedChange`, and refuses
     overlaps between workflows.
   - **Workflow provenance.** The dispatched workflow's blob must equal the default branch's (`MergeQueueEngine.cs:999-1015`).
     There is a fork guard (`:960-991`). The template has `contents: read`, no secrets and `persist-credentials: false`.
   - **The git writer** (`codecoverage-git-writer` image) is git-only: no Node or SDK, read-only rootfs, an empty
     hooks dir, `attr.tree` = the empty tree, so in-repo filter and merge drivers never run (`GitRunner.cs:97-199`).
     It pushes with a token minted per job, scoped to one repository and revoked afterwards
     (`GitHubForgeMergeQueue.cs:1127-1185`, `MergeQueueEngine.cs:1838`). **`git` is the only executable that runs
     on the server.**
   - **Per-rule workflows.** O36 lets each rule name its own `action:`, and the workflows are dispatched in turn
     (`MergeQueueYml.cs:169-197,285-303`).
   - **The saga.** Step and signal messages, Generation gating, an outbox schedule and a transient retry cap of 6
     (`MergeQueueEngine.cs:63,77-240,1958-1989`), on a serial queue lane (`CoverageQueues.cs:57-62`).
3. **External facts that undo Q1's premise** ("building it ourselves" is no longer the only generic option):
   - **Renovate** runs inside a job as a lookup engine:
     - `--platform=local` defaults to `dryRun=lookup`. With `reportType=file` and `reportPath` it writes JSON:
       `repositories[repo].packageFiles[manager][].deps[]` with `depName`, `currentValue`, `datasource`, and
       `updates[]` (`newVersion`, `newValue`, `updateType`).
     - That covers about 90 managers in **one format**. It is marked *experimental*.
     - Local **apply** is not supported: "Branch creation is not supported".
     - Sources: [local platform](https://docs.renovatebot.com/modules/platform/local/),
       [self-hosted config](https://docs.renovatebot.com/self-hosted-configuration/),
       [report types](https://raw.githubusercontent.com/renovatebot/renovate/main/lib/instrumentation/types.ts).
       The report field names were read from source and are **unverified** (S12).
   - **dependabot-core** has been **MIT since 2024-05-13**
     ([changelog](https://github.blog/changelog/2024-05-13-dependabot-core-is-now-open-source-with-an-mit-license/)),
     and the **Dependabot CLI** is MIT. `dependabot update <eco> <repo> -o out.yml` "outputs data that could be
     used to create PRs" without creating them. It needs Docker, which `ubuntu-latest` has. Its proxy injects
     credentials, so the updater never sees a secret. The job-file format is undocumented
     ([cli](https://github.com/dependabot/cli)).
   - **Renovate is AGPL-3.0.** Invoking it unmodified in the *user's* workflow places no obligation on us. This is
     the assistant's reading, not legal advice.
   - **Native commands** exist almost everywhere:
     - `dotnet package update`: new in the .NET 10 SDK; it restores implicitly; CPM is **unverified**
       ([docs](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-update)).
     - `npm install x@v --ignore-scripts`, `uv lock --upgrade-package`, `mvn versions:use-dep-version`,
       `cargo update -p x --precise v`, `go get x@v`, `composer update --no-scripts`.
     - Docker images and Actions `uses:` have no native updater. Renovate's lookup covers them.
4. **The supply-chain threat is current.** Shai-Hulud (Sept and Nov 2025) ran from npm **preinstall** scripts and
   harvested CI tokens ([Unit 42](https://unit42.paloaltonetworks.com/?p=157750)). So the runner side needs the same
   discipline as the merge queue's regenerate template: a read-only token, no secrets, no push.
5. **Dependabot closed some of §2's gaps.** Grouping one dependency across directories (2026-02), and CPM plus
   transitive pins in the new NuGet updater. Our edge has to come from state Dependabot doesn't have (§13.6), not
   from grouping.

### 13.2 The contract: the bot is ecosystem-agnostic, the workflow is not (P1)

**P1.** The bot never parses a version, a manifest or a lockfile to *update* it. M3's graph scanners stay, for
reading only. The bot only handles records:

```
{ ecosystem, manager, packageFile, depName, currentValue, candidates[{ version, updateType, releaseTimestamp? }] }
```

The workflow produces them, and the bot answers with a plan of `{ depName, packageFile, newVersion, group }`.
Everything ecosystem-specific lives in the workflow: lookup, compatibility, lockfile regeneration and migrations.
Any ecosystem the workflow can handle is supported **without a bot change**. That is the merge queue's model:
`coverage.yml` names a workflow, and the bot defines only the contract.

The bot's policy needs no version parsing:
- **Ordering and major/minor/patch** come from the report's `updateType` and candidate order.
- **Rules** (§4.2) match on `ecosystem` / `depName` globs.
- **Holds, fleet feedback, propagation and cooldown** compare `releaseTimestamp` against the policy.

§5.1's per-ecosystem compatibility filters (npm peers, TFM, `requires_python`, …) move to stage 2 in the workflow,
where the resolver reports them as resolve failures. The platform locks (Angular major, TFM major) become default
account rules with `Enforced`, and stop being code.

### 13.3 Two dispatches, one lookup and one apply (P2, P3)

**P2.** The round trip of §7.1 becomes two dispatches through the merge queue's pipeline. Each is one way: inputs
in, artifact out.

| Phase | Workflow job | Token / secrets | Returns |
|---|---|---|---|
| **lookup** | Renovate `--platform=local --dry-run=lookup --report-type=file` (default implementation; S12). Private feeds via `hostRules` from repo secrets | `contents: read`; feed secrets **allowed** (no repo code executes in lookup; S12 must prove it per manager) | `lookup.json` artifact (the §13.2 records) |
| *(bot)* | applies policy (rules, holds, cooldown, fleet, propagation) and stores the plan | — | plan id |
| **apply** | for each plan group: apply to the pinned version with scripts off, regenerate the lockfile, optionally `verify` (S13) | `contents: read`; feed secrets only if S14 shows restore can't execute package code for that ecosystem; never `id-token`, never write | `git diff --binary` artifact plus `result.json` (applied, failed per group with the resolver message) |
| *(git writer)* | `PatchValidator` with a `dependencies` path profile (P5), applies, pushes `mintplayer/dependencies` with the App token, so CI runs | per-job scoped token | — |

**P3. Plan transport.** The apply dispatch passes `plan_id`, `expected_sha` and a compact plan as inputs. This is
open: `workflow_dispatch` caps inputs at 25 and the payload size is unverified (S16). If the plan doesn't fit, the
workflow fetches it read-only from `GET api/dependencies/plans/{id}`, authenticated by its OIDC token (it would
reuse `ApiTokens/GitHubOidc.cs`). That token would be the only `id-token: write` in the template, and the GET
returns nothing but the plan. Lookup and apply run against the same `expected_sha`; the bot discards the plan if
the base moved in between.

**Why two dispatches and not one.** The policy is fleet state: holds, red-elsewhere, propagation and enforced rules.
Serialising all of it into one dispatch would push §5 into the workflow, in a different language per installation.
Two runs of a small job cost about 2–4 runner-minutes (to measure in S12/S13). That is negligible next to one CI run
of the resulting PR, which is 17–21 min (§1).

**Pluggable like the merge queue.** `coverage.yml` names the workflows, and we ship defaults:

```yaml
dependencies:
  lookupAction: dependency-lookup.yml   # default template ships in the onboarding PR
  applyAction: dependency-apply.yml
  migrate:                              # P9, see §13.6
    - match: "@angular/core"
      onMajor: angular-migrate.yml
```

A repo with an unusual ecosystem (Bazel, a private Maven layout) writes its own workflow that honours the contract.
The bot doesn't change.

### 13.4 Which engine does the apply (P4, decided by S13)

| Option | Generic? | Concerns |
|---|---|---|
| A. Native commands per ecosystem, in our action (TS) | yes, with one small adapter per ecosystem (~10) | we maintain the adapters; Docker and `uses:` edits are text edits we write |
| B. Dependabot CLI producing the updated file contents | about 20 ecosystems | Docker-in-runner; undocumented job and output format (YAML); it does its own version selection, so it has to be forced to our pins |
| C. Renovate full mode | about 90 managers | can't apply locally; it pushes and opens PRs itself with a write token, which is the §13.1 threat. **Rejected** unless S13 finds a no-push mode |

**P4.** Renovate (lookup) plus option A (apply), with option B as a fallback per ecosystem if an adapter turns out
hard (Gradle, Maven). In S13 the plan is fixed and the engine only applies, so B's own version selection must be
neutralised (`allowed-updates` / `ignore` down to exactly the pinned version).

### 13.5 Paths, `.github`, rebase and replay (P5–P7)

- **P5. A `dependencies` path profile for `PatchValidator`.** The allow-list comes from the lookup report, not a
  static glob list: the `packageFile`s plus their manager's lockfiles. The existing preset already lists the
  lockfiles (`AutoResolvePresets.cs:20-34`). Everything else is refused, as are `Directory.Build.*`, `*.targets`,
  `nuget.config`, `.npmrc` and install scripts.
  - **`.github/workflows/*` carve-out** for Actions `uses:` bumps. Only hunks whose `-`/`+` lines differ solely in
    the ref after `uses: owner/repo@` are allowed. The writer token then gets `workflows: write`, which it already
    can when needed (`GitHubForgeMergeQueue.cs:1127-1185`).
  - The template's own `uses:` lines are bumped by the same path, so the onboarding stub stays current (Q4).
- **P6. The rolling branch is maintained by the merge-queue machinery, not by the workflow.**
  - Conflict-only rebase via the git writer's `merge-tree`.
  - Lockfiles: take-base plus regenerate (presets O35/O37) through the apply workflow.
  - Human commits are replayed by the writer, with §7.3's table expressed as resolver rules.
  - This replaces §7.3's "the Action replays".
- **P7. One commit per apply run.** §7.1's "one commit per ecosystem" is dropped. If per-ecosystem commits are
  wanted, the git writer can split the validated patch by `manager`, since the report says which file belongs to
  which manager. That's cheap, but adds no safety.
- **Provenance.** The merge queue's `provenance.mjs` accepts only packages already present in the base or PR
  lockfile (`lockfiles.sh:243-258`), which by design refuses an update. The apply phase needs its own check:
  every changed lockfile entry is a planned package, or a transitive dependency the resolver brought in. That check
  stays in the workflow (it is per ecosystem). The bot then checks `result.json` against the plan.

### 13.6 Where this bot stands out (research 2026-10-15)

These are ranked by defensibility. Each needs state that a stateless per-repo updater lacks.

1. **Migrations through the repo's own workflow (P9; reopens a §3 non-goal).**
   - Dependabot's grouped Angular PRs only edit `package.json`
     ([angular/components#30464](https://github.com/angular/components/issues/30464)).
   - `nx migrate` silently skips migrations after a manifest-only bump
     ([nrwl/nx#23505](https://github.com/nrwl/nx/issues/23505)).
   - Renovate's `postUpgradeTasks` needs an admin allow-list and is off by default.
   - We'd dispatch a per-rule `migrate` workflow (`ng update --migrate-only --from --to`,
     `nx migrate --run-migrations`) after apply, exactly like O36's per-rule `action:`.
   - **Security cost:** migrations rewrite source, so the patch allow-list widens to the whole tree minus
     `.github/` and build files. The PR then never auto-merges, and its body says "contains code migrations". Spike S17.
2. **Release propagation in graph order** (§7.6, unchanged). The producer merges through the queue, then its
   consumers are updated in topological order, with a "propagation wave" view: lib vX is in 7/10 consumers, 1 is red.
3. **Red-CI diagnosis and fleet holds** (§7.4, §7.7). Renovate's Merge Confidence is anonymous crowd data; ours is
   the owner's own fleet, weighted by coverage.
4. **Coverage on the update PR.** It already works because the App pushes to a same-repo branch. **P10:** put the
   coverage delta in the PR body, and add a "no coverage drop" auto-merge condition. This is display only, not Q3's
   excluded per-bump risk model.
5. **Auto-merge = enqueue in the merge queue (P8; replaces §7.8's native auto-merge).**
   - Dependabot rebases every open PR and stops after 30 days
     ([changelog](https://github.blog/changelog/2023-04-13-automatic-rebases-on-dependabot-pull-requests-stop-after-30-days-of-inactivity/)).
   - We have one PR, rebased only on conflict, merged through the queue, with lockfiles regenerated and never merged
     textually.
6. **Platform readiness and lockstep** (§8). Combined with (1), "move to Angular 23" becomes one rule change that
   opens a PR with the migrations already run.
7. **Holds that expire** (§7.3/Q11), against `@dependabot ignore`, which lasts forever.

**Not worth competing on:**
- grouping (Renovate presets; Dependabot's 2026 grouping across directories);
- transitive security fixes;
- private-feed plumbing (Dependabot has OIDC for Azure Artifacts now);
- NuGet CPM.

**P11.** Reconsider Q17/Q18 (our own security PR). Deferring advisories to Dependabot security updates, and
mentioning them in the fleet view, saves M10 with no loss of edge.

### 13.7 Spikes

Kept from §10:
- **S3**: the selector replay, now against lookup records.
- **S4**: rolling-PR mechanics, now on the writer (P6).
- **S6**: log attribution, needed only if `verify` stays in apply.
- **S8**: own-package correlation, now using `releaseTimestamp` from the report.
- **S10**: a second published action.

Dropped or absorbed:
- **S1**: absorbed by S13 and S15.
- **S2**: the bot no longer parses versions to update them.
- **S5**: done in M3.
- **S7**: folded into S13.
- **S9**: pending P11.
- **S11**: the metadata comes from the report or the workflow. Revisit after S12.

| # | Question | Done when |
|---|---|---|
| S12 | **Renovate lookup as the generic report.** `--platform=local --dry-run=lookup --report-type=file` on this repo plus samples for Maven, Gradle, Cargo, Go, uv/Poetry, Composer and Docker. Check: the real report fields; whether `releaseTimestamp` is present; private feeds via `hostRules` from secrets; runtime; and **whether lookup or extract executes any repository code** for each manager (Gradle and Maven especially) | Field map checked against a real `report.json`; a "lookup executes no repo code" yes/no per manager, with evidence; minutes per repo |
| S13 | **The apply engine.** For one fixed plan per sample repo, produce the patch with option A (native, pinned, scripts off) and option B (Dependabot CLI forced to the pins). Include `dotnet package update` on a **CPM** solution and on `global.json` / `dotnet-tools.json` | Byte-equal or explained patches; per-ecosystem pick recorded; adapter LOC estimated |
| S14 | **Is restore safe with scripts off?** Per ecosystem: does lockfile regeneration run package-supplied code? Cases: NuGet `build/*.targets` during `restore` or `package update`; uv/pip building sdists (`--no-build`, `--only-binary`); Maven and Gradle plugins; Cargo `build.rs` (`cargo update` shouldn't); Go (none); Composer plugins | Table: executes / doesn't / flag to stop it. It decides per ecosystem whether feed secrets may enter the apply job (P2) |
| S15 | **Extract the workflow hook.** Lift dispatch, correlation, wait, timeout, artifact read, `PatchValidator` and writer publish out of `MergeQueueEngine` into a shared service that both the merge queue and the updater use. Add the `dependencies` profile and the `uses:`-only `.github/workflows` rule (P5) | The merge-queue tests are still green; a `uses:` bump passes and any other workflow edit is refused, in unit tests |
| S16 | **Plan transport.** The real `workflow_dispatch` input size limit; inputs vs an OIDC-authenticated plan GET (P3) | Limit measured; the largest real plan (this repo, all ecosystems) fits or doesn't |
| S17 | **Migrations hook (P9).** `ng update --migrate-only --from 21 --to 22` and `nx migrate --run-migrations` on a scratch Angular/Nx repo through a dispatched workflow; the widened allow-list; the no-auto-merge marker | A green migrated PR on the scratch repo; the patch is refused if it touches `.github/` or build files |
| S18 | **End-to-end on SparkTest** (`PieterjanDeClippel/SparkTest`, the merge queue's live repo): onboarding stub → lookup → plan → apply → push → CI → enqueue | One rolling PR updated twice; a tree-equal schedule triggers 0 CI runs; a human commit survives a recompute |

### 13.8 Milestones (one PR, if §13 is accepted)

**Status (2026-10-16):** N1–N9 are built on `docs/issue419-dependency-updates-restart` with their tests (not run yet;
the owner batches the sweep). N1 ✅ (`WorkflowHook`), N2 ✅ (D-P13/D-P16), N3 ✅ (`dependency-action`, templates,
contracts), N4 ✅ (D-P1–D-P18), N5 ✅ (D-L1–D-L6, D-N1, D-N2), N6 ✅ (D-L7, D-L10, D-N3), N7 ✅ (D-L9, D-L11), N8 ✅ bot and
runner side (R7–R9, R15, R19; the dispatched half is S18), N9 ✅ (onboarding D-L14, permissions D-N4, fleet view and
readiness D-N5, coverage delta R11, docs). P12 ✅ (D-N5). Security PR (old M10) not built: superseded by R10. **Left:** S18
live on SparkTest after deploy, and the S12/S13 re-captures that replace the REST-doc bodies in
`GitHubForgeDependencyUpdatesTests`.

1. **N1. Workflow hook extraction (S15).** No behaviour change for the merge queue.
2. **N2. Settings and rules.** = M1 + M2, plus the `dependencies.lookupAction` / `applyAction` / `migrate` keys.
3. **N3. The workflow templates and the action.**
   - Lookup (Renovate, pinned by digest).
   - Apply adapters (S13 picks), scripts off, `result.json`.
   - Packaging per S10.
4. **N4. Policy engine on records** (P1).
   - Rules, holds, cooldown from `releaseTimestamp`, enforced platform locks as default rules.
   - The S3 replay as a regression test, on a **real** recorded `report.json`.
5. **N5. Lifecycle.**
   - Cron + queue `coverage-dependency-updates`.
   - Lookup → plan → apply saga on the extracted hook.
   - Tree-equal no-op, conflict-only rebase and replay through the writer (P6).
6. **N6. Red CI and commands** = M8.
7. **N7. Propagation, fleet feedback and enqueue-on-green** = M9 with P8.
8. **N8. Migrations hook** (P9, if accepted).
9. **N9. Fleet view, readiness, coverage delta (P10), onboarding PR, Workflows permission, docs** = M11.
10. Security PR (M10): only if P11 is rejected.

M3 (the graph) is done. Optionally (**P12**), it also consumes the lookup report's `packageFiles`. That would give
the graph Maven, Gradle, Cargo and Go for free, with the M3 scanners kept as the fallback for repos without the
workflow.

### 13.9 Open questions for the owner (as asked; answered in §13.10)

| # | Proposal | Reverses | Recommendation |
|---|---|---|---|
| P1 | The bot is ecosystem-agnostic and handles records only; per-ecosystem logic lives in the workflow | Q1 (own engine), §5.1 filter 6, S2 | accept: it is the only way to be "any ecosystem" without code per ecosystem in the bot |
| P2 | Two dispatches: lookup (read-only, may hold feed secrets) and apply (no write, no `id-token`; secrets only where S14 allows) | §7.1 (OIDC round trip) | accept; reuses #515/#517 |
| P3 | Plan via inputs, falling back to an OIDC-authenticated GET | §7.1 step 2's OIDC upload | decide after S16 |
| P4 | Renovate for lookup, native adapters for apply, Dependabot CLI as fallback per ecosystem | Q1 | decide after S12/S13 |
| P5 | Allow-list taken from the report, plus a `uses:`-only carve-out under `.github/workflows` | — | accept |
| P6 | The writer maintains the rolling branch (rebase and replay); the workflow only regenerates | §7.3 "the Action replays" | accept |
| P7 | One commit per apply run | §7.1 step 6 | accept |
| P8 | Auto-merge = enqueue in the merge queue at the slot | §7.8 native auto-merge | accept |
| P9 | A migrations hook (`migrate:` per rule); migrated PRs never auto-merge | §3 non-goal "source-code migrations" | owner's call: biggest differentiator, biggest allow-list widening |
| P10 | Coverage delta in the PR body, plus a "no coverage drop" merge condition | — (Q3 excluded per-bump *risk*, not display) | accept |
| P11 | Drop our own security PR and defer to Dependabot security updates | Q17, Q18 | owner's call; saves M10 |
| P12 | The graph also ingests the lookup report | — | accept, cheap |

Also open:
- Does Q19 ("all ecosystems") now mean "anything Renovate's lookup supports, plus an apply adapter"? It could also
  mean the §3 G3 list first, then the rest.
- Does the restart still go in one PR with the interim Dependabot config removed by the onboarding PR (Q20)?

### 13.10 Decision log (grilled 2026-10-15; "owner" = decided by the repo owner)

| # | Decision | Rejected | Evidence / reason |
|---|---|---|---|
| R1 | **P1 accepted.** The bot handles ecosystem-neutral records only (`ecosystem, depName, current, candidates[updateType, releaseTimestamp]`). All version, manifest and compatibility logic lives in the workflow. **Supersedes Q1** and §5.1 filter 6. Platform locks become default `Enforced` rules. A record missing `updateType`/`releaseTimestamp` is treated as a hold | hybrid with bot-side locks/compat; keep Q1 | owner. "Generic across any ecosystem" isn't reachable with per-ecosystem bot code |
| R2 | **A group the resolver rejects** (e.g. Angular 23 before `@nx/angular` supports it): the apply run applies group by group and resets a failing group so the others still ship. `result.json` names the blockers (parsed per adapter: npm `ERESOLVE`, NuGet `NU1107`/`NU1202`, …). The bot holds the group until a lookup shows a newer version of a blocker. Fallbacks: no blockers parsed → retry when any package changes; always retry after 4 slots | fail the whole run; retry every slot without blockers | owner. Example raised by the owner: an Angular major before Nx is compatible |
| R3 | **Two one-way dispatches**, lookup then apply, on the merge queue's pipeline. The plan is discarded only if the base moved **and** touched a manifest or lockfile | one run with a mid-run OIDC callback; one dispatch with policy upfront | owner. Reuses #515/#517 unchanged; a callback would need `id-token` in an install job |
| R4 | **The rolling branch is rebuilt from scratch each slot.** Lookup + apply on the default branch, then the git writer replays human commits (`merge-tree` + resolver rules; a human manifest value wins and becomes a hold; packages a human fix depends on stay pinned). Force-push only if the tree differs | rebase the existing branch; drop human commits | owner. Keeps Q10/Q11 |
| R5 | **Auto-merge = enqueue in the merge queue, only.** Repos without `mergeQueue: { enabled: true }` (`MergeQueueEngine.cs:383`) get no auto-merge. **Supersedes §7.8** | enqueue + native fallback; native only | owner. One safe path; native merges onto a moved base |
| R6 | **Auto-merge thresholds live in `coverage.yml`** (`dependencies.autoMerge`, globs → `never/patch/minor/major`), with built-in defaults (e.g. `"@angular/*": never`, `"actions/*": minor`). **Over-threshold updates stay out of the PR** until a writer comments `approve <group>`. An approval expires when a newer major appears. Enforced account rules remain a ceiling (Q9). **Partly supersedes Q7/Q13** (thresholds move from lookups to the file) | all-or-nothing PR; a second review-lane PR | owner chose the file. The approval mechanism and the enforced ceiling are assistant assumptions, stated in the grill and not objected to |
| R7 | **Platform/SDK updates.** The lookup emits `platform` records (endoflife.date + official feeds such as dotnet `releases-index.json`). The action has **one adapter per SDK** (`detect / current / apply(newMajor)`) that edits every location atomically together with the locked packages (TFM, `global.json`, Dockerfile `FROM`, `setup-*`, and `<Version>` major when opted in). Platform majors **never auto-merge**, whatever the file says. Previews are followed only if the repo is already on one. Adapters: dotnet, node, python, java, go | report-only; allow auto-merge | owner. Owner's request: "latest SDK → update csproj/props". A package major burns forever (repo CLAUDE.md) |
| R8 | **Platform config:** auto-detect locations, with an optional override, under `dependencies.platforms.<sdk>`: `strategy: replace\|add` (default `replace`), `keepPrevious` (default 0), `lockPackageMajor` (explicit opt-in), `locations`, `exclude`. A computed TFM (`$(Prop)`, conditions) is **reported, never guessed** | explicit-only; auto-detect without override | owner: "specify if the sdk-version is configured globally (Directory.Build.props) or per project" |
| R9 | **The migrate hook is accepted (P9; reopens the §3 non-goal).** `dependencies.migrate` maps a rule to a workflow (`ng update --migrate-only`, `nx migrate --run-migrations` templates shipped). It is dispatched after apply, **only for approved over-threshold groups**. Its patch may touch the tree except `.github/**`, `coverage.yml`, build files and scripts. It **never auto-merges** and the PR is marked "contains code migrations". A partial migration drops the whole group | on request only; keep the non-goal | owner. The top differentiator in §13.6 |
| R10 | **Security fixes go in the rolling PR** (P11 variant). A vulnerable package skips schedule and cooldown and triggers an out-of-slot apply. If the PR is blocked (pending approval, bisecting), that run **rebuilds it with only the auto-mergeable groups plus the fix**, and the rest return at the next slot. The fix may auto-merge (never a major). **Supersedes Q17/Q18, §7.9 and M10** | our own security PR; defer to Dependabot | owner |
| R11 | **The coverage delta goes in the PR body, and auto-merge requires no drop** beyond `dependencies.autoMerge.maxCoverageDrop`. An **incomplete** report (missing uploads) is its own hold, never read as a drop. No coverage data → the gate is skipped | display only; nothing | owner |
| R12 | **Ecosystems: every ecosystem Renovate's lookup reports gets an apply adapter**, plus the platform adapters (R7). The T5 template rule (no secrets, no `id-token`, no write) also applies to user-supplied custom workflows. **Supersedes Q19 / G3** | the old Q19 list plus a pluggable fallback; only the owner's ecosystems | owner: "as many as possible" |
| R13 | **Apply engine:** chosen per ecosystem after S12–S14, against four criteria: (1) patch byte-equal to the plan; (2) no package code executed during resolve, or contained; (3) machine-readable blockers; (4) under 5 runner-min on the sample. **Native adapters are the tie-breaker and the long-term direction**; the Dependabot CLI only where native fails the criteria. Renovate and the Dependabot CLI are pinned by digest, and the spike fixtures become regression tests | all native upfront; Dependabot CLI first | owner: "C, but I would eventually opt for A" |
| R14 | **Plan transport:** a plan commit (`.mintplayer/dependency-plan.json`) on the bot-owned dispatch ref `mintplayer/dependencies-work` = `expected_sha` + that commit. `PatchValidator` refuses the plan path. The commit must never touch `.github/` (provenance check). The ref is cleaned up after each run. **Supersedes P3/S16** | inputs only; OIDC GET | assistant (owner delegated; owner has no objection to OIDC in general). Keeps `id-token` out of the job that installs fresh packages |
| R15 | **Actions updates:** a narrow carve-out under `.github/workflows` and `action.yml`. Allowed: `uses: owner/repo@<ref>` changes (+ `# vX` comment), and `with:` version keys on a **closed, code-defined** list of setup actions (`setup-dotnet`, `setup-node`, …), never configurable in `coverage.yml`. The writer token gets `workflows: write` only for such patches. Action **majors** are over-threshold by default. The lookup reports an **interface diff** (removed or renamed inputs the repo passes, new required inputs, missing outputs it reads, node runtime) as blockers. Workflows not triggered by `pull_request` never auto-merge `uses:`/`with:` edits. **New `IContentValidator`s run bot-side** (next to `JsonValidator`, `CodeCoverage.Git/MergeQueue/Resolution/ContentValidators.cs`): `YamlValidator` (also used by the merge queue) and `GitHubWorkflowValidator` (schema, `uses:` well-formed, `with:` keys exist in the target `action.yml` read statically, referenced outputs exist). The runner's own actionlint result is advisory only | no carve-out; broad carve-out | assistant (owner delegated). Owner: "when an action does a major tag-bump, the inputs/outputs will probably have changed"; "validate if a github action/workflow doesn't yield errors before committing". The runner ran untrusted code, so its verdict can't be trusted |
| R16 | **Onboarding PR switches the repo over** (Q20 kept). It adds thin template callers of the versioned action (`@dependency-update-v1`), a commented `dependencies:` section, and removes `dependabot.yml` version-update entries (translated into rules; alerts stay on). **The first rolling PR is a draft** and never auto-merges. This repo goes first; #501's `dependabot.yml` in git history is the rollback | shadow mode; manual switch | owner |
| — | P7 (one commit per apply run) and P12 (the graph also ingests the lookup report, M3 scanners as fallback) | — | assistant defaults, stated in the grill and not objected to |

**What the merge queue removes** (owner asked, 2026-10-15): dispatch, correlation and waiting, the patch artifact, `PatchValidator`, the git-writer push, provenance and fork guards, conflict-only rebase with lockfile regeneration, merging (R5), and the saga pattern. Roughly 35–40% of the old plan (the old M4/M6/M7 plumbing and §7.8).

**Still to build:** the apply adapters (the biggest piece), the lookup contract, the policy engine, settings and onboarding, red-CI bisect, an apply-specific provenance check (the merge queue's `provenance.mjs` rejects new versions by design), the R15 carve-out and validators, and the platform adapters.

**Preconditions:**
- Merge-queue v2 (#517) deployed and re-tested on SparkTest.
- S15 (extracting the hook out of `MergeQueueEngine`) keeps every merge-queue test green.

**Spike changes after the grill:**
- **S16 is dropped** (R14).
- **S13 now covers every ecosystem Renovate reports** (R12) and the R13 criteria.
- **S12 adds the `action.yml` interface diff** (R15) and the platform records (R7).
- **New S19:** the platform adapters for dotnet and node. Covers `Directory.Build.props` vs per-csproj, multi-targeting, a computed TFM, and `lockPackageMajor` on this repo (`net10 → net11`, and 10.x → 11.x packages). Done when the dry-run patch on this repo matches the hand-made net11 migration commit, or each difference is explained.
- **N8 also covers R7/R8/R15**, and **N10 is dropped** (R10).

### 13.11 Test plan (investigated 2026-10-15)

Three parallel investigations: bot-side C#, runner-side action, and an adversarial catalogue.

**Rules for every test:**
- Each parser fixture is a **real capture**: the Renovate `report.json`, `result.json`, resolver errors, `action.yml`, `dependabot.yml`, `releases-index.json`. Each capture directory has a `SOURCE.md` (tool, version, command, date), and `tools/capture-fixtures.mjs` re-captures them.
- A red run before every fix; no mutation guards.
- MintPlayer.Assertions.
- No timing waits: delayed saga steps are released explicitly.

#### 13.11.1 Bot side (C#), about 300–380 tests

**Infrastructure reused:**
- `MergeQueueHarness` (fake outbox; `DrainAsync`/`ReleaseAsync`), on `CoverageRavenTest`.
- `FakeForgeMergeQueue` and `FakeGitWriter` (`MergeQueueFakes.cs`).
- `StubGitHub`: real Octokit over captured bodies. There is no WireMock in CodeCoverage.
- `TestRepo` (`CodeCoverage.Git.Tests`), and `WriterTestBase` + `GitHttpServer` (`CodeCoverage.GitWriter.Tests`).
- **New:** an injectable clock in the harness (it hard-codes `TimeProvider.System` at `MergeQueueHarness.cs:171`). Reuse `MutableTime` (`CommitGraph/CommitGraphTestKit.cs:124`). Cooldown, expiry and the 4-slot retry need it.

| Decision | Test classes (level) | Key cases |
|---|---|---|
| N1 hook extraction | the existing merge-queue suites, **unchanged** (941 in about 30 s); `WorkflowHookTests` (Raven) | Assertion files must not change in the diff. 204 → `dispatch-uncorrelated`; `head_sha` mismatch; `PollRun` fallback; `Timeout`; artifact cap, expired or missing; blob provenance; stale Generation |
| R1 records | `LookupReportParserTests`, `PolicyEngineTests`, `SelectorReplayTests` (unit) | Real S12 reports; a missing `updateType`/`releaseTimestamp` becomes a hold; unknown managers pass through; a Docker digest or Go pseudo-version is never parsed as a version; golden plan replay |
| R2 blockers | `ApplyResultParserTests` (unit), `GroupHoldTests` (Raven, clock) | Real S13 `result.json`; the hold lifts only on a newer blocker version; with no blockers, retry on any change; after 4 slots, always retry; other groups still ship |
| R3 two dispatches | `DependencySagaTests` (Raven) | lookup → plan → apply → publish; the base moved in docs only → plan kept; the base moved in a manifest → re-lookup; red, timeout or uncorrelated lookup → no apply |
| R4 rebuild + replay | `RollingBranchReplayTests` (`CodeCoverage.Git.Tests`, no host), `RollingBranchPushTests` (GitWriter, one host per class) | Human commits replayed; the human's manifest value becomes a hold; a pinned dependency for a human fix; equal tree → no push |
| R5 enqueue only | saga cases | Queue on → `MergeQueueSignalMessage`; queue off → `MergeRequests` empty |
| R6 thresholds | `DependenciesYmlTests` (unit, this repo's real file), `ApprovalCommandTests` (Raven, webhook path) | Defaults; invalid values are errors, not exceptions; the enforced ceiling wins; a non-writer's `approve` is refused; expiry on a newer major |
| R7/R8 platforms | `PlatformRecordPolicyTests` (unit), `DependenciesYmlTests` `platforms.*` | A platform major never auto-merges; previews only when the repo is already on one; a computed TFM is reported, never planned |
| R9 migrations | `MigrationPatchProfileTests` (real repo), saga | Source edits allowed; `.github/**`, `coverage.yml`, build files and scripts refused; dispatched only for approved groups; a partial migration drops the group; never enqueued |
| R10 security | saga (clock) | Out-of-slot apply; a blocked PR is rebuilt with only the auto-mergeable groups plus the fix; a major fix is never auto-merged |
| R11 coverage | `CoverageDeltaGateTests` (Raven, the existing cobertura fixtures) | A drop within the limit passes; beyond it holds; an incomplete report is its own hold; no data → gate skipped |
| R12 custom workflows | `DependencyWorkflowGuardTests` (from `MergeQueueForkGuardTests`) | secrets, `id-token`, write permissions, a missing top-level `permissions:`, or `pull_request_target` → refused; the shipped templates are read from disk |
| R13 pinning | unit + `PatchValidator` on S13 patches | Templates pin Renovate and the Dependabot CLI by `@sha256:`; every real S13 patch passes the `dependencies` profile |
| R14 plan transport | `PlanCommitTests` (GitWriter), saga | ref = `expected_sha` + one commit touching only the plan file; a `.github/` touch is refused; the plan path in the patch is refused; the ref is deleted on success, failure and timeout |
| R15 Actions | `PatchValidatorTests` (+ `uses:` cases), `ContentValidatorsTests` (`YamlValidator`), `GitHubWorkflowValidatorTests` | See §13.11.3; real `action.yml` pairs, **shared with the runner's interface-diff tests** |
| R16 onboarding | `OnboardingPrTests` (Raven + fake forge) | This repo's real #501 `dependabot.yml` is translated; alerts are left alone; the first PR is a draft and never enqueued; the templates call `@dependency-update-v1`; new `IForgeMergeQueue` calls get `StubGitHub` cases with bodies captured live in S18 |

**Speed:** the Raven and unit classes run at merge-queue speed. Only the GitWriter host classes are slow: share one host per class, and keep the replay matrix host-free. `tools/test-shard-weights.json` covers Spark.Tests only, so nothing changes there. Watch that `CodeCoverage.Tests` stays under the 4.7 min CI wall; if it doesn't, it gets its own matrix entry.

#### 13.11.2 Runner side (`apps/CodeCoverage/dependency-action`)

The merge queue's runner pieces (`lockfile-regenerate`, `spark-regenerate`) have **no automated tests**; they were proven live only. The new action must not repeat that.

Its layout copies `apps/CodeCoverage/action` (vitest colocated, ncc `dist`, `test:bundle`). Three nx targets:
- **`test`**: unit tests, in "Other suites", taking seconds.
- **`test:bundle`**: the committed `dist` run against a stub.
- **`test-integration`**: real package managers against **local registries**. It gets its own `test.yml` matrix entry, split into `node+dotnet`, `jvm`, `python+go+rust+php` and `docker+renovate+dependabot-cli`, each with a 15 min timeout. The Nx cache makes an unchanged action about 1 min.

The local registries:
- npm: verdaccio.
- NuGet: a folder feed with `<clear/>`.
- PyPI: `--find-links`.
- Maven: `file://` + `-o`.
- Go: `GOPROXY=file://…,off`.
- Cargo: local-registry + `--offline`.
- Composer: an artifact repo.

The public registries are unset, so an accidental fetch fails instead of passing. Docker tests are skipped loudly on Windows.

| Component | Level | Asserts |
|---|---|---|
| Record mapping | unit | Real S12 reports → golden records; a record missing a field becomes a hold marker and is never dropped |
| Renovate lookup step | integration (Docker) | The report maps; **a sentinel planted by an `init.gradle` or Maven extension never appears** (lookup runs no repo code) |
| Ecosystem adapters (all of R12) | integration | **The patch is byte-equal to a golden diff** (R13 criterion 1) |
| Scripts off (S14 as regression tests) | integration | Synthetic packages with an npm `preinstall`, NuGet `build/*.targets`, a Composer plugin and a `setup.py` sdist write a sentinel; **it must be absent** |
| Blocker parsers | unit | Real public captures (Angular N+1 against `@nx/angular`), plus the same failure re-produced against the local registry, so a tool upgrade that changes the message fails loudly; unrecognised text → "no blockers" |
| Group-by-group apply/reset | unit (fake adapter, real git) + integration (npm, 3 groups, the middle one conflicting) | The tree has groups 1 and 3 only; no leftover lockfile or `node_modules` state; the blocker is named |
| `result.json` + patch artifact | unit (ajv) + bundle | Validates against the schema; the artifact is a `git diff --binary` that `PatchValidator` accepts |
| Platform adapters | unit on file trees + a `dotnet restore` smoke test | props vs csproj; `net10.0;net48`; `add`/`keepPrevious`; a **computed TFM is reported, not edited**; `global.json`, tools, Dockerfile, `setup-*`; `lockPackageMajor`; **S19 golden: this repo before the net11 commit equals the hand-made migration commit** |
| `action.yml` interface diff | unit | Real tag pairs (`upload-artifact` v3→v4, `checkout` v4→v5, `setup-node`); fixtures shared with the C# validator, which must reach the same verdict |
| Migrate templates | workflow level only (S17 scratch repo, S18 SparkTest) | Kept out of PR CI because they need the public registry and run for minutes; the allow-list logic is unit-tested |
| Templates | static unit + pinned actionlint | `contents: read`, no `id-token`, `persist-credentials: false`, `@dependency-update-v1` pinned by SHA |

**Contract test.** Schemas live in `apps/CodeCoverage/contracts/dependency-update/{record,plan,result,platform-record}.schema.json`, each with a `contractVersion`, plus `fixtures/valid` (real outputs) and `fixtures/invalid`.
- The runner validates with ajv, and a test fails if the generated TS types are stale.
- The bot round-trips every valid fixture through its DTOs, validates its plans with JsonSchema.Net, and refuses the invalid fixtures.
- **Dogfood** extends the `UploadActionDogfoodTests` pattern:
  1. C# writes `.mintplayer/dependency-plan.json`.
  2. It runs the committed `dist` in apply mode against verdaccio.
  3. It feeds the result into the real `PatchValidator` and the plan-vs-patch check.

  Only this test catches drift between the two sides.

#### 13.11.3 Adversarial catalogue

**None of the R15 carve-out exists yet.** `PatchValidator.IsSafePath` refuses all of `.github/` (`PatchValidator.cs:295`) and never inspects hunk content, and `ContentValidators.cs` has no YAML validator.

**Already covered:**
- symlinks, modes, binaries, copies: GW `PatchValidatorTests.cs:111`
- `..`, `.git/`, `.GITHUB/`, `.gitmodules`: `:134-145`
- smuggled headers: `:148`
- the patch cap: `:171-179`, `GitHubForgeMergeQueueTests.cs:593`
- overlaps and invalid content: `ResolutionV2Tests.cs:86,119`
- workflow blob provenance: `MergeQueueEngineTests.cs:610,1734`

**NEW tests** (an attacker controls the apply or lookup runner unless stated):
1. A new `Directory.Build.targets/.props` at any depth, any case → refused.
2. Edits to `.npmrc`, `NuGet.Config`, `.yarnrc.yml` `yarnPath`, `.pnpmfile.cjs`, or `global.json` `msbuild-sdks` → refused.
3. An allowed `*.csproj` gains `<Exec>`, or `package.json` gains `scripts.postinstall` → refused (gap A).
4. `result.json` claims group X while the patch also bumps Y → refused. The applied groups are derived from the patch.
5. A lockfile `resolved` URL on an attacker host, or `git+`, `github:`, `file:`, `link:`, `http:` → refused (gap B).
6. A report `packageFile` of `../x`, `.github/workflows/ci.yml`, `src/Program.cs` or `coverage.yml` → the allow-list is not widened (gap A).
7. A `uses:` bump plus an added `run:` in the same hunk; a Cyrillic homoglyph in owner/repo; `./local` → remote; `docker://` → refused (gap C).
8. A `with:` key outside the closed list, a `${{ }}` value, or a multi-line value → refused.
9. Flow-style steps, `uses:` text inside `run: |`, anchor or alias edits → refused, by comparing the parsed YAML before and after with aliases expanded.
10. A moved tag → the bot pins its own resolved 40-hex SHA.
11. A plan commit touching `.github/` or a second path → refused before dispatch. A writer pushing to the dispatch ref → `head_sha` mismatch.
12. *Custom workflow:* `contents: write`, `id-token`, `secrets.*`, no top-level `permissions:`, or `pull_request_target` → refused.
13. *Collaborator:* `coverage.yml` above an enforced rule → clamped. `dependencies:` edited on the rolling branch → ignored, because config is read from the default branch.
14. *Non-writer commenter:* `approve`, an edited comment, a bot author → ignored. The role is checked again at apply time.
15. *Approval replay:* after a newer major, a group-membership change, or loss of write access → expired.
16. *Look-alike or dependency confusion* claiming "own package" → no cooldown bypass (gap E).
17. A forged `updateType` or `releaseTimestamp`; a `replacement` without a rule; a platform major labelled minor (gap D).
18. A migrate patch touching `.github/`, `coverage.yml`, build files or scripts → refused.

#### 13.11.4 Design gaps found by the adversarial pass

| Gap | Problem | Proposed fix | Status |
|---|---|---|---|
| A | P5 derives the allow-list from the **untrusted** lookup report. Even a correct list admits `*.csproj`/`package.json`, which can carry `<Exec>` targets or lifecycle scripts that then run in CI **with secrets** | Each `packageFile` must match a code-defined filename pattern for its manager and exist at `expected_sha`; lockfiles are code-defined siblings; the deny-list wins; **no added or deleted files**; a **substitution check**: each `-`/`+` pair in a non-lockfile differs only by a planned `current → new` token on a line naming the planned `depName`; applied groups derived from the patch | **accepted 2026-10-15** (owner did not object) |
| B | Lockfile provenance (`provenance.mjs`-style) would run on the compromised runner | A bot-side, ecosystem-neutral scan of added lockfile lines: URL hosts on the account's registry allow-list or already in the base lockfile; no `git+`/`github:`/`file:`/`link:`/`http:`; an integrity value on every changed entry; more than N new transitive packages → a human | **accepted 2026-10-15** (owner did not object) |
| C | The R15 carve-out exists only on paper | A `WorkflowRefPatchRule` in the writer + `GitHubWorkflowValidator`: strict 1:1 hunk pairs, owner/repo byte-identical ASCII, only the ref token + `# vX` change, the ref equals the SHA the bot resolved, a before/after YAML comparison with aliases expanded, flow-style and anchored changed nodes refused | **accepted 2026-10-15** (owner did not object) |
| D | Auto-merge and cooldown trust the report's `updateType`/`releaseTimestamp`; R1 only catches *missing* values | see **R17** below | decided |
| E | "Own package" and approvals are underspecified | Own package: three-way check bot-side against the registry (producer graph + publishing repo owned by the account + provenance attestation). Approvals bound to (group, depName set, target major, approver role at apply, base `coverage.yml` blob); `created` comments only | **accepted 2026-10-15** (owner did not object) |

Also: protect `mintplayer/*` refs with a ruleset (App only); pin `@dependency-update-v1` by SHA in the templates; require an explicit top-level `permissions:` in custom workflows.

**R17 (owner, 2026-10-15): lookup records are trusted, with two generic guards.**

Why trust them at all: lookup is a different trust level from apply. It runs only Renovate, pinned by digest, on the default-branch checkout, and installs no fresh packages.

The guards:
1. **A version floor that can only raise the update type.** If the leading numeric segment differs (or the second, for `0.x`), the update counts as major, whatever the label says. This is string comparison, not version parsing, so R1 holds.
2. **Managers where S12 shows lookup executes repo code** get no auto-merge and no cooldown bypass. Their records always go to a human.

The VPS makes no registry calls. Rejected:
- the bot re-fetching from registries: per-ecosystem clients would undo R1, and it defends against an attacker who already owns Renovate or the default branch;
- trusting the report as is.

Known costs:
- CalVer and date tags classify as major, so they need a rule before they can auto-merge.
- A Renovate image digest bump needs human review, like an action major.

Gaps A, B, C and E are accepted as design amendments: they tighten R1, R12 and R15 without reversing them.

**Residual risk, stated plainly:** once a malicious version is planned, the PR's CI installs it **with secrets**. The cooldown and fleet holds reduce that risk, and no validator removes it.

### 13.12 Spike results (2026-10-15/16) and the decisions they forced

S12, S13, S14 and S17 ran locally (Docker for the missing toolchains). S15 and S19 are implementation work (N1, N3). S18 needs the deployed bot. Captured fixtures are copied into the test projects by N3/N4 with their `SOURCE.md`.

**Owner decisions from the spikes:**

| # | Decision | Rejected | Evidence / reason |
|---|---|---|---|
| R18 | **The migrate patch uses a deny-list only:** `.github/**`, `coverage.yml`, symlinks, mode changes, executable files. Everything else may change, including `tsconfig*.json`, `angular.json`, `nx.json`, `project.json`, `package.json` and the lockfile. **Refines R9.** There is no extra "review these" block | a defined allow-list; keep "build files refused" | owner: "it's kinda hard to let dependency-updates that require migration steps work across all ecosystems", and "any third-party nuget package can suddenly decide to ship an auto-loaded targets file, so if you'd be worried about malicious code, we shouldn't even update the package version at all". S17: real Angular 21→22 and Nx 22→23 migrations touched tsconfig, angular.json, nx.json, .gitignore, package.json + lock and tools/*.ts, and none touched `.github`/`coverage.yml` |
| R19 | **`nx migrate --include=all`.** Its extra dependency bumps (S17: typescript 5.9.2 → 6.0.3) ride along in the migrated group. They are listed in the PR body, and the lockfile is regenerated with scripts off. If an extra bump hits a bot hold (fleet-red, enforced rule), the PR gets a blocking note "held version introduced by migration" | `--include=required` (loses the TS6 tsconfig migrations, 9 → 7); re-planning extra bumps as their own groups | owner |

**Residual risk, restated (owner, 2026-10-16):** the validators stop the *runner* from changing anything beyond the plan. They cannot make a *planned* package safe: any updated package can bring code that CI runs (NuGet `build/*.targets`, npm scripts in CI's own install). Only the cooldown, fleet holds and human review of majors address that.

#### S12: Renovate lookup ✅ (2026-10-15)

**Engine:** `renovate/renovate:44.125.2` @ `sha256:a7dec3432d62b39872058424f0490829413b3f597f5d72fb41b9b7bd57ccac77`, run as `--platform=local --dry-run=lookup --binary-source=install --report-type=file`. Fixtures: 14 samples plus this repo (scratchpad `spikes/s12/fixtures/renovate`, to be copied into the test projects).

**Shape:** `repositories.local.packageFiles[manager][] → {packageFile, lockFiles?, deps[]}`.
- Each dep: `depName`, `packageName`, `datasource`, `versioning`, `currentValue`, `currentVersion`, `lockedVersion?`, `currentDigest?`, `skipReason?`, `updates[]`.
- Each update: `updateType` (major/minor/patch/digest), `newValue`, `newVersion`, `newDigest?`, `releaseTimestamp?`, `isBreaking`, `isLockfileUpdate?`.

**`releaseTimestamp`:** present for npm, nuget, maven, gradle, cargo, go, pypi, composer, rubygems, Actions tags, dotnet-version, node-version and rust. **Missing** for most Docker tags (1 of 9), setup-java, `.python-version` and `runs-on`.

**Lookup executes no repo code** for any tested manager: gradle(+wrapper), maven(+wrapper), npm with `.npmrc`, gomod, cargo, pep621, poetry, pip_setup, pip_requirements, composer, bundler, nuget, dockerfile, github-actions, asdf/nvm/nodenv/pyenv, and this repo.
- ~35 planted hooks produced 0 sentinels.
- The positive control fired inside the same image.
- `util/exec` is imported only by the artifact and post-update paths.
- So the R17 guard-2 list is **empty**. Re-run the sentinel test on every digest bump; this becomes a test.

**Parser facts:**
- Only one candidate per bucket unless the `separate*` options are set.
- 0.x breaks are labelled `minor` (confirms the R17 floor).
- npm `newDigest` is an integrity hash, not a version.
- Go pseudo-versions come back as `digest` updates.
- ubuntu has `newVersion: "resolute"`.
- An SDK rc.1→rc.2 is labelled `patch`.
- The manager name ≠ the tool (pyproject without `uv.lock` → `poetry`).
- **The TFM is not represented.** `.mvn/extensions.xml` and `[tool.uv] dev-dependencies` are silently dropped.

**Actions:** majors are reported, with SHA pins (`currentDigest`/`newDigest`). `action.yml` is fetched via `gh api … contents/action.yml?ref=`, falling back to `action.yaml`. In the real pairs (checkout v4/v5, upload-artifact v3/v4, setup-node v4/v5) **no input or output was removed**; only the node runtime changed and inputs were added. upload-artifact v4's real break is invisible in `action.yml`, so the interface diff is necessary but not sufficient. That is why majors stay over-threshold (R15).

**Runtime:** 17–50 s per sample; this repo 187 s (504 deps), plus about 30–60 s to pull the image. **One lookup ≈ 3.5–4 runner-min**, so the §13.3 estimate was low.

**Decisions taken from S12** (assistant; technical, within R1–R17):
- **D-S12a.** A missing `releaseTimestamp` → the cooldown counts from the **bot's first-seen time**, which is never earlier than the real release, so it is conservative. This refines R1's "missing → hold", which would hold Docker updates forever.
- **D-S12b.** The lookup template sets `separateMultipleMajor`, `separateMinorPatch` and `separateMultipleMinor`, so holds and cooldown have fallback candidates.
- **D-S12c.** The TFM stays **adapter-only** (R7). The action's platform step emits the platform records from `releases-index.json`/endoflife.date. Docker tags of platform images (mcr dotnet/node/python) are grouped under their platform record, which owns the preview rule.
- **D-S12d.** Apply adapters are keyed on `datasource` + `lockFiles`, never on `manager`.
- **D-S12e.** Lockfile-only refresh (transitive updates without a manifest change) is an **opt-in** `dependencies.lockFileMaintenance: true` step, default off. It widens the transitive change surface, which gap B's new-package cap then bounds.

#### S13: apply engines ✅ (2026-10-15)

16 cases across 15 ecosystems. Fixtures in scratchpad `spikes/s13/fixtures/apply/<eco>/*.diff` with `SOURCE.md`.

**Result:** **text-edit + an ecosystem pin/lock step, for every ecosystem.** With the pin step, all 16 cases were *pure* (only the version token on the dep's own line changed) and *exact* (the lockfile holds exactly the planned version).
- **Native commands break gap A in JS and .NET.** npm and pnpm re-sort dependencies, `yarn up` drops the `^`, and `dotnet add package` / `tool update` reformat the XML/JSON; under CPM it rewrote the whole props file. Python, Composer, the Maven versions plugin and `go get` stayed pure.
- **Text-edit plus plain relock is not exact for ranges.** npm, pnpm and yarn locked `^4.19.2` → 4.22.3, and Cargo re-resolved to 1.0.229. Every adapter therefore has a **pin** step:
  - npm: `i name@ver --package-lock-only --ignore-scripts`, then restore the manifest and relock.
  - pnpm: `update name@ver --lockfile-only`.
  - yarn: `set resolution`.
  - cargo: `update -p --precise` **before** the edit.
  - uv: `lock --upgrade-package n==v`.
  - composer: `update n --with n:v --no-install --no-scripts --no-plugins`.
- **A failed restore can still write the lockfile** (NuGet rewrote packages.lock.json on NU1605). So R2's reset must restore the group's files explicitly.
- **Structured blockers** exist only for NuGet (`project.assets.json` `logs[]` with code + libraryId), pnpm (`--reporter=ndjson`) and yarn (`--json` YN codes). npm is semi-structured (`--json` code `ERESOLVE`, prose detail). The rest are regex over prose, built on the fixtures, plus R2's "nothing parsed" fallback.
- **Gradle exits 0 while printing `FAILED`**, so it needs output scanning, not just the exit code.
- **Composer ≥2.9** blocks versions that have advisories (`policy.advisories.block`), so an old repo can fail to lock.
- **Criterion 2 can't be met everywhere:**
  - Gradle evaluates `*.gradle.kts` on every run.
  - Maven loads plugins and extensions.
  - NuGet restore runs MSBuild evaluation (repo code; S14).
  - npm, pnpm and yarn run with scripts off; Cargo resolve doesn't run `build.rs`.
- **Timings:** 1–15 s per ecosystem, except Gradle at 40–126 s.
- **Size:** about 1,100–1,300 LOC of TypeScript. That is a ~200 LOC shared core (unique-match line edit, EOL and BOM byte-preserving, refuse on 0 or more than 1 match) plus 20–110 LOC per adapter.

**Dependabot CLI v1.95.0 (fallback B).**
- `--local <dir>` works without a token: 154 s including a 2.17 GB image.
- An ignore-condition forced the exact version, and the result was pure and exact.
- **`dependencies: [x]` did not restrict the job**: it also emitted unrelated majors.
- Its output is YAML events with whole file contents.
- **It is not needed.** It internally runs the same text-edit + pin strategy as our npm adapter.

**Decisions taken from S13** (assistant; within R13, where native is the tie-breaker):
- **D-S13a.** Every adapter is `edit() → pin() → relock() → verify() → parseBlockers()`, and **resets the group's files on any failure**.
- **D-S13b.** There is no Dependabot CLI fallback. R13's per-ecosystem choice resolves to native text-edit + pin everywhere.
- **D-S13c.** Golden-diff tests use **range manifests with old lockfiles** (exact-pin samples hide the latest-satisfying trap).
- **D-S13d.** Gradle and Maven apply jobs are treated as executing repo code: no secrets, under T5. This holds regardless of S14.

#### S14: does lockfile regeneration run package code? ✅ (2026-10-15)

Method: a local registry or feed per ecosystem holding a malicious-by-design package whose hook writes a sentinel. Networked tools ran in Docker with `--network none`. Resolver-error fixtures are in scratchpad `spikes/s14/fixtures/resolver-errors/<eco>/`. Full table: `spikes/s14/FINDINGS.md`.

| Ecosystem | Safest command | Package code runs? | What stops it |
|---|---|---|---|
| npm | `--ignore-scripts --package-lock-only` | no (the default `npm install` fired 3 hooks) | either flag |
| pnpm 9/10 | `--lockfile-only --ignore-scripts` | no (v10 also blocks by default) | either flag |
| yarn 4 | `--mode=update-lockfile` | no | the mode |
| NuGet | restore only | **no on restore**; its `build/*.targets` fire on the **next build** (`buildTransitive` too) | no switch exists: **never build** in the apply job |
| uv | `uv lock --upgrade-package` | **yes, for sdist-only packages** (`setup.py` runs) | `--no-build` (fails when a package has no wheel) |
| Composer | `--no-scripts --no-plugins` | no; `--no-scripts` **alone still ran the plugin** | `--no-plugins` |
| Cargo | `update -p --precise` | no (`build.rs` only runs on build) | don't build |
| Go | `go get` / `go mod tidy` | no (even `go build` runs no module code) | — |
| Maven | `versions:use-dep-version` | no | — |
| Gradle | `--write-locks` | the dependency doesn't run, but **the repo's `build.gradle` does** (configuration phase), and so would a dependency that is itself a build plugin | — |

`dotnet package update` picked the latest version and not `--version`. That confirms D-S13a: edit the csproj, then restore.

**Decisions taken from S14** (assistant; within R12/R13 and gap A):
- **D-S14a. The apply job never builds.** There is no `verify` stage in apply; the PR's own CI is the verify. This makes NuGet restore-only safe, and it means §13.3's "optionally `verify`" is removed.
- **D-S14b. Feed secrets enter the apply job only for ecosystems on a code-defined safe list:** npm, pnpm, yarn, NuGet (restore only), Composer, Cargo, Go and Maven. **uv** gets `--no-build` whenever feed secrets are present. Without secrets it may build sdists, because that job holds nothing worth stealing and its output is still bounded by the gap A/B validators. **Gradle** never gets feed secrets: the repo's build script and any plugin dependency run.
- **D-S14c.** Composer always runs with **both** `--no-scripts --no-plugins`.

#### S17: migrations hook ✅ locally (2026-10-15; the dispatched half is in S18)

**Verdict:** works for Angular 21→22 and Nx 22.7.12→23.3.0. Fixtures are in scratchpad `spikes/s17/fixtures/migrate`.

**Template conditions (all verified):**
1. **Install first:** `npm ci --ignore-scripts` (181 s Angular, 77 s Nx). Without it, `ng update` exits 1 with ELSPROBLEMS.
2. **Nx needs `--from`.** After our apply, plain `nx migrate <new>` is a **silent exit-0 no-op**, which reproduces nx#23505. Use `nx migrate <new> --from=nx@<old>,@nx/js@<old> --agentic=false`, then `npm install --ignore-scripts`, then `nx migrate --run-migrations --agentic=false`.
3. **Angular:**
   - Set `NG_DISABLE_VERSION_CHECK=1` and use the local binary (`npx --no-install ng`); otherwise it fetches a temporary `latest` CLI.
   - Run one package per `--migrate-only` run, with `--allow-dirty` from the second package on.
   - Re-running is idempotent.
4. **Job env:** `npm_config_ignore_scripts=true NG_CLI_ANALYTICS=false NX_DAEMON=false NX_NO_CLOUD=true`.
5. **Migrations execute package code by design.** The job therefore has `contents: read` and no secrets.

**Exit 0 ≠ the result builds.** The Angular 22 core migrations on an app without `strictTemplates` produced NG4003 on `ng build`. The migrated Nx tree built green. That confirms never auto-merge, with the PR's CI as the real gate.

**Partial migration:** both tools exit 1 and leave the earlier edits on disk, so the partial patch looks valid. Nx says "Run halted at migration 3 of 7". Detection:
1. `set -euo pipefail`.
2. Upload only `if: success()`.
3. A completion marker, `migrate-result.json`, written as the last step.
4. The bot requires `conclusion == success` **and** the marker, else it drops the group.

**Paths real migrations touch:**
- Angular core: `src/**/*.ts`, `tsconfig.app.json`.
- The Angular cli schematic ran its own install, giving `package.json` + lockfile changes. Its `update-workspace-config` migration edits `angular.json`.
- Nx 23: `nx.json` (reformatted whole), `.gitignore`, `tsconfig.base.json`, `libs/*/tsconfig.lib.json`, `tools/*.ts`, `package.json` + lockfile.
- Earlier Nx migrations have also touched `project.json` and `.nx/nxw.js` (a script).
- None touched `.github/**` or `coverage.yml`.

**This conflicts with R9's "build files refused"** if that includes tsconfig, angular.json, nx.json, project.json or package.json (owner question S17-Q1).

**Nx `--include=all`** (the default) adds its `packageJsonUpdates`: typescript `~5.9.2` → `6.0.3`, a bump outside the group. `--include=required` avoids that, but drops the TS6 tsconfig migrations (9 → 7), which would then never run (owner question S17-Q2).

**Timings:** `ng update` core 38 s, cli 7 s; `nx migrate --from` 98 s; `--run-migrations` 49 s; lock regeneration 19–35 s.

**Mutual compatibility** (owner asked 2026-10-16: "how we'd be able to make `npm i` keep working — the dependencies must be compatible among one another"). Assistant decisions, within R2 and D-S13a:
- **D-C1.** The lookup template extends Renovate's `group:monorepos` + `group:recommended`. Records carry `groupName`, and the bot plans per group, so related packages (`@angular/*`, `nx` + `@nx/*`) always move together. A record without a `groupName` is a group of its own.
- **D-C2.** Pin/relock runs with **strict peer checking** wherever the tool has it (npm `--strict-peer-deps`, pnpm `strict-peer-dependencies=true`). A peer mismatch is then a blocker, never a warning: S14 captured npm's "overriding peer dependency" *warning* form, which otherwise writes a lockfile that later fails. Groups are applied in order, each on top of the previous ones. A conflicting group is reset and held (R2), so the remaining set is always one the resolver accepted together.
- **D-C3.** After the last group, a **whole-tree consistency check** runs per ecosystem: `npm ci --ignore-scripts --dry-run`, `pnpm install --frozen-lockfile --ignore-scripts --lockfile-only`, `yarn install --immutable --mode=skip-build`, `dotnet restore --locked-mode`, `uv lock --check`, `composer validate` + `install --dry-run --no-scripts --no-plugins`, `cargo metadata --locked`, `go mod verify`. If it fails, the apply bisects over the groups instead of shipping. The PR's CI is the final gate (§7.4).

#### Bot-side validators (2026-10-16, gaps A–C, R14, R15, R18)

Built in `CodeCoverage.GitWriter/Jobs/Dependencies` (`DependencyPatchValidator`, `LockfileScanner`, `WorkflowRefPatchRule`,
`MigratePatchValidator`, `PlanCommitRule`) and `CodeCoverage.Git/MergeQueue/Resolution` (`YamlValidator`,
`GitHubWorkflowValidator`, both also nameable by merge-queue rules). Tests run the real S13 apply captures, the S17 migrate
patches and the S12 `action.yml` pairs (copied into the test projects with their `SOURCE.md`). Decisions taken while
building them (assistant, within the accepted gaps):

- **D-V1. A tag-pinned `uses:` line may take the planned tag or the bot's SHA; a SHA-pinned line only the bot's SHA.**
  Evidence: the S13 actions capture bumps `setup-node@v4.0.0 → @v4.0.2` (tag to tag) beside a SHA bump. Forcing every
  line to a SHA would rewrite the repository's pinning style; a moved tag is still caught on SHA-pinned lines (#10).
- **D-V2. The substitution check's "line naming the dependency"** is the line itself or up to 3 old-side lines above it,
  naming the depName, packageName, their last `/`/`:` segment or `sharedVariableName`. Evidence: S13 Maven inline
  (`<version>` under `<artifactId>guava`), Maven property (`<jackson.version>`), dotnet-tools (`"version"` under
  `"dotnet-ef"`). `global.json`'s `msbuild-sdks` lines are refused outright, because the `sdk.version` line names
  `"version"` too.
- **D-V3. A lockfile belongs to a package file in its directory or an ancestor** (workspace roots), and
  `Directory.Packages.props` also owns the lockfiles below it (CPM). Each changed lockfile needs an applied owner.
- **D-V4. The deny-list also holds** `setup.py`, `build.rs`, Gradle settings/init scripts/properties, the Gradle and Maven
  wrapper properties and the `.mvn`/`.yarn`/`.husky` directories, so wrapper updates are never applied by this profile.
- **D-V5. Groups are derived from the patch and must be whole** (`partial-group`), and must equal `result.json`'s claim
  in both directions (`group-mismatch`). A lockfile-only record (`currentValue == newValue`) counts as applied when one of
  its lockfiles changed.
- **D-V6. The `migrate` profile also refuses** what the writer never writes (`.git` components, `.gitmodules`,
  `.gitattributes`, paths git would quote) and all of `.mintplayer/`.
- **D-V7. Over the new-package cap (default 25) is `NeedsHuman`, a third outcome**, not a refusal.
- **D-V8. The plan entry is a minimal record** (`DependencyPlanEntry`) until the contract DTO lands (**resolved by D-P2**:
  it now maps from the contract `PlanItem`, `DependencyPlanEntry.From(plan)`); the S13 NuGet CPM
  capture is not a test fixture because its spike work tree no longer holds the captured base (blob ids differ).

#### N2 + N4: settings, `dependencies:` and the policy engine (2026-10-16)

Built in `CodeCoverage/Dependencies` (`DependenciesYml`, `DependencyContractReader`, `Policy/*`), the contract DTOs in
`CodeCoverage.Git/Dependencies/DependencyContracts.cs`, the settings and state documents in `CodeCoverage.Library`. Tests in
`CodeCoverage.Tests/Dependencies/Policy` (the S12 reports copied to `Fixtures/Dependencies/renovate` with their
`SOURCE.md`). Decisions taken while building them (assistant, within R1–R19, D-S12*, D-C*, gap E):

- **D-P1. The Renovate-report mapping exists in C# only as a test helper** (`RenovateReportMapper`, a port of the runner's
  `mapReport()`). The bot reads only contract records (R1); the port lets the policy tests run on the real S12 reports and is
  pinned to the runner by a test: for the five reports the runner turned into `lookup.*.json` fixtures, both give identical
  records.
- **D-P2. The DTOs live in `CodeCoverage.Git`, the schema validation in the app.** The writer maps its
  `DependencyPlanEntry` from the contract `PlanItem` (D-V8 resolved), so both sides read one plan type; JsonSchema.Net stays
  out of the sidecars. Every runner document is validated against the embedded `contracts/dependency-update` schemas before
  it is deserialized (errors are data); every plan the bot writes is validated too, and an invalid one throws (a bot bug,
  never committed).
- **D-P3. Thresholds** (R6): built-in `default: minor`, so majors wait for an approval. The most specific matching
  `autoMerge` glob wins (literal characters; an exact name beats a glob); the file's globs beat the built-ins; a built-in
  (`@angular/*: never`, `actions/*: minor`) and the ecosystem defaults (`github-actions` and `platform:*` stop at minor,
  R15/R7) only ever *lower* the file's default. `allowMajor: true` raises inclusion only: the major is planned without an
  approval but never auto-merges. Because R6 makes the threshold also the inclusion gate, `@angular/*: never` means every
  Angular update, patches included, waits for `approve` (it moves with migrations, R9).
- **D-P4. The account rule's `pin <range>` became `MaxUpdate`** (never/patch/minor/major): a range cannot be read without
  parsing versions (R1). An enforced rule caps the threshold, `allowMajor` and any approval (an update above it is "above the
  account rule", never "awaiting approval"); an enforced `ignore` cannot be undone. A non-enforced rule applies only when no
  file glob or rule names the dependency. Its `Group` applies when the file sets none.
- **D-P5. A digest update without a `newVersion`** (S12: `python:3.11-slim` re-pushed) is planned at the record's current
  version, which a digest update keeps by definition; any other missing version stays an R1 hold. The identity token of a
  digest update is its digest (first-seen, hold snapshots), so each re-push gets its own cooldown.
- **D-P6. Selection per group, by level.** The engine tries the levels present from major down. At each, every member takes
  its best problem-free candidate within the level; the group passes only if no member is stopped (R1 marker, rollback or
  replacement, no current value, fleet-red, file/human/comment hold), it is under the enforced ceiling, within its inclusion
  threshold or approved, and old enough. The cooldown falls back, member by member, to an older candidate of the same level.
  Reasons are reported in that precedence (held > above ceiling > awaiting approval > cooldown), so an over-threshold update
  shows as awaiting approval even while it is still cooling down (with its `Until`). Every level above the planned one is
  reported, so the PR body lists the waiting major next to the shipped minor.
- **D-P7. The group key:** a configured group (file rule, then account rule) beats the lookup's candidate `groupName`, then
  the record's, then the dependency name; one dependency in several package files is one group (§5.1 #8).
- **D-P8. Holds.** R2 "a newer version of a blocker" = a version token for that blocker the lookup did not show when the
  hold was taken (tokens are compared, never ordered). A `failed` group, or an `unresolvable` one with no attributed blocker,
  is an *unattributed* hold that lifts on any change of the lookup fingerprint. "Always after 4 slots" counts scheduled slots
  only (`IsScheduledSlot`); an out-of-slot security run does not advance it. A human hold (Q11) lifts on any version token not
  seen when it was taken (the bot's proposal included); a comment hold lifts only on `unhold`, which never removes a bot hold.
- **D-P9. Approvals** (R6, gap E) are taken from the PR body's awaiting-approval entries for the group: its sorted dependency
  names, `depName@major` per dependency (the R17 leading segment, `0.x` for zero majors) and the base `coverage.yml` blob. An
  approval stays valid while the group still offers exactly that (a hold on the group does not expire it) and expires on a
  newer major, a membership change or another blob. The role check (write+, `created` comments, again at apply) is N6's.
- **D-P10. Plan order** (D-C2): security fixes first, then by level (patch first, so the safest groups land before a riskier
  one can be reset), then by group name (ordinal); platform groups last, because they move the TFM under everything else.
- **D-P11. A digest is planned only where one is pinned today** (`currentDigest`): npm's `newDigest` is an integrity hash,
  and planning it would make the validators' lockfile-only test (`CurrentDigest == NewDigest`) fail.
- **D-P12. Lockfile-only updates** (Renovate `isLockfileUpdate`, value unchanged) are planned when the record has a lock
  file, and skipped silently when it has none (nothing to apply). `lockFileMaintenance` (D-S12e) is parsed but produces no
  record: it is an input of the lookup template (N5), not of the policy.
- **D-P13. The file's `cooldown` may only lengthen the owner's** (a cooldown is a supply-chain control); its `schedule` parts
  override the settings' slot. A section with any error falls back to the defaults and keeps only a valid `enabled: false`.
- **D-P14. State documents are loaded by id, never queried:** `DependencyUpdateStates/{provider}/{id}` per repository
  (first-seen times capped at 5000, holds, approvals, slot count) and `DependencyFleetStates/{provider}/{id}` per account
  (red versions, confirmed own releases; capped at 2000 each). A version red in the repository itself is not a fleet hold
  there: R2 and the bisect own that case. Own-release confirmation (gap E's three-way check) is N7's; the policy trusts only
  what is recorded.
- **D-P15. A security fix still respects the thresholds** (R10 skips schedule and cooldown, not R6): a major fix waits for an
  approval, flagged as security. A security run plans only the fixes' groups and groups that may auto-merge anyway; the rest
  are listed as left for the next slot.
- **D-P16. Settings.** The account also carries `DependencyTimeZone` (IANA, UTC; an unknown id reads as UTC), because the
  slot hour needs one. A repository overrides the schedule as one unit (`DependencySchedulePolicy` Inherit/Override, like
  `MergeQueueCheckExclusionsPolicy`). Every zero value is the default, so reading needs no migration;
  `M_202610161200_BackfillDependencyUpdateSettings` writes the defaults onto old documents only so the N5 scheduler can query
  them (an absent field matches nothing in RQL).
- **D-P17. The S3 replay's golden plans were derived by hand** from this repository's real report (2026-10-15T19:17Z, three
  days' cooldown, built-in thresholds), not by running the engine: at capture time only the five groups released before
  2026-10-12T19:17Z ship (`@types/node` lockfile, `JsonSchema.Net` 9.4.1, `Microsoft.CodeAnalysis` 5.9.0 ×3,
  `boky/postfix` 4.4.0, RavenDB 7.2.3); a week later 27 groups ship and 15 wait for approval (12 `@angular/*`, `typescript`,
  and the `@types/node` and `postfix` majors). Re-derive them when the fixture is re-captured.
- **D-P18. Platform records:** the target is the first candidate channel (the runner lists them highest first) that is not a
  preview, or any when the repository already is on one; its level is the R17 floor on the channel strings (`10.0 → 11.0`
  major, `3.11 → 3.15` minor). A platform major never auto-merges, even when the file admits it; `platform:*` defaults to
  minor, so a major waits for `approve platform:<sdk> <channel>`. Computed locations are reported; a platform whose every
  location is computed is not planned.

#### N5–N7 and the bot side of N8/N9: the saga, red CI, propagation, onboarding (2026-10-16)

Built in `CodeCoverage/Dependencies/Lifecycle` (`DependencyUpdateEngine` on the lane `coverage-dependency-updates`, its
messages, recipients, `DependencyUpdatesCronJob`, and the pure rules: `DependencyLifecycleRules`, `DependencyPullRequestBody`,
`DependencyOnboarding`), the state in `CodeCoverage.Library/Entities/DependencyUpdateRun.cs`, the replay in
`CodeCoverage.Git/Dependencies/RollingBranchReplay.cs`, the writer jobs in `CodeCoverage.GitWriter` (`WriterService.Dependencies.cs`,
`Endpoints/DependencyEndpoints.cs`), and GitHub's side in `GitHubForgeDependencyUpdates` and `GitHubEventsRecipient`. Decisions
taken while building them (assistant, within R1–R19, D-S*, D-C*, D-V*, D-P*):

- **D-L1. A seam of its own, `IForgeDependencyUpdates`**, beside `IForgeMergeQueue` (the PR create/update, reactions, the bot's
  check run, a multi-file artifact, tag resolution, a static file read of another repository). The hook keeps speaking
  `IForgeMergeQueue`, so the merge queue's conformance list is untouched. The new `IGitWriterSidecar` members default to
  `NotSupportedException`, so every other writer implementation compiles unchanged.
- **D-L2. The lookup is dispatched on the default branch itself**, not on a bot ref: D14's `head_sha` check then fails a run
  whose branch moved between the read and the dispatch, and the saga starts over on the new head (`Restarts`, at most 3). A
  bot ref would need a writer job per run for no stronger guarantee.
- **D-L3. Artifacts are read by name** (`lookup.json`; `result.json` + `dependency.patch`; `migrate-result.json` +
  `migrate.patch`): the action uploads a directory, so the hook's single-file read would refuse it. Other files in the archive
  are ignored; the cap holds for the archive and for the named files together.
- **D-L4. The writer judges every patch again before it builds from it** (`validate`, the staged work ref, `rolling`): the
  saga's verdict is not trusted across a request boundary. The rolling bot commit is built on `expected_sha`, never on the
  plan commit, so the plan file never reaches the rolling branch.
- **D-L5. R3's "manifest or lockfile"** is every `packageFile` and lock file of the lookup report and of the plan; a compare
  that is truncated or unreadable counts as touched (start over).
- **D-L6. The replay (R4).** Human commits are the non-merge commits above the bot's last commit (`RollingBotSha`); a branch
  that no longer holds it was rewritten by hand and is kept (`rolling-rewritten`, comment, no push). Each conflict region of a
  planned manifest is resolved line by line where the three sides line up (the human's line where the human changed it, the
  bot's otherwise), else the human's lines whole. Any other conflict, a lockfile included, stops (`replay-conflict`): lockfiles
  are never merged textually, and the writer cannot regenerate them. The human's manifest values are read from the human
  commits' own diffs (the changed token of a line naming a planned dependency, D-V2's three lines above), conflict or not,
  and become Q11 holds, so the next slot does not fight the person. Replayed commits keep the person's author, committer,
  dates and message. ~~"Packages a human fix depends on stay pinned" (R4) is not built~~ **superseded by D-N1** (built), and
  ~~"a lockfile included, stops"~~ **superseded by D-N2** for the plan's own lockfiles.
- **D-L7. Bisect (old §7.4).** One re-run of the failing check runs first (`RerunCheckAsync`), counted in the budget; a red
  verdict within 2 minutes of it is the old one. Then suspects are left out **cumulatively** (majors first, then a version red
  elsewhere in the fleet, then the later plan groups, D-P10's order reversed), one re-plan + apply + CI run per step; the first
  green build names the last group left out, and a red build with one suspect left names that one. The culprit gets an
  unattributed hold (`red:<group>`, lifts when the lookup changes or after 4 slots), its versions go on the fleet's red list
  (Q15), and a refresh rebuilds everything else. Failing stack traces are not read (no log access); ~~build failures are
  not told from test failures~~ **superseded by D-N3** (told apart by the failing checks' names). A budget of 0 reports the ranked suspects without bisecting; a writer's `bisect` comment then
  runs one with a budget of 4.
- **D-L8. The bot's own check run** (`Dependency updates`) is completed (neutral, success, failure) everywhere except while it
  bisects, when it stays in progress. The saga leaves it and its check suite out of its own gate (else the gate waits for
  itself), and "nothing reported" is never green: at least one check must have passed.
- **D-L9. Enqueue-on-green (R5)** sends the merge queue's own command signal with a new source,
  `EMergeQueueCommandSource.DependencyUpdates`: the queue counts the App as `write` (never a bypass role) and keeps every other
  condition (the approval floor, rules, checks). Only scheduled, security and own-release runs enqueue; a run a comment started
  (`approve`, `hold`, `refresh`) does not, and the next slot's tree-equal rebuild picks it up.
- **D-L10. Commands** come only from the first word of a **created** comment by a non-bot sender, never from the App; the role
  is read from the forge per command (write or above), and approvals are re-read at plan time (gap E's "again at apply"): an
  approver who lost write access loses the approval. `bisect` and `refresh` take no argument; anything after them is an
  ordinary comment.
- **D-L11. Own packages (§7.6, gap E).** A producer event is a published release, or a push to a producer's default branch
  (a repository whose manifest declares packages). The own-package window is ±2 hours between that event and the lookup's
  `releaseTimestamp`; the bot reads no registry (R17), so "provenance attestation" is not checked. Propagation is debounced
  30 minutes per producer; each consumer on the account (manifest `Consumes` ∩ the producer's `Produces`) gets an own-release
  run.
- **D-L12. Security alerts (R10)** arrive as GitHub `dependabot_alert` events (created, reopened, reintroduced), mapped onto
  the lookup's ecosystem words. The App must subscribe to the event and request *Dependabot alerts: read*; ~~that is not in the
  `DependencyUpdates` need yet~~ **superseded by D-N4** (it is now).
- **D-L13. The schedule (Q8)** is checked by a cron every 10 minutes; a slot is due from the repository's spread minute (a
  hash of its id) to the end of the slot hour, deduplicated per repository and local day. The file's `schedule` parts and
  opt-out are read from the run document, written at each run's start, so a changed file takes effect from the next run.
- **D-L14. Onboarding (R16).** The migrate templates are added only where the tree has `angular.json` / `nx.json`. The action is
  pinned to the commit the bot resolved for `dependency-update-v1`, the tag kept as a comment; an unresolvable tag keeps the
  tag. `dependabot.yml` loses `updates:` (every ecosystem: R12) and what only served them (`multi-ecosystem-groups`,
  `registries`); with nothing else left it is deleted. Dependabot `ignore:` translates to `rules: ignore`, to an `autoMerge`
  threshold for update-type ignores (majors → `minor`, minors and majors → `patch`, so they wait for `approve`), and to a
  comment for version ranges (R1: rules take no ranges). Before pushing, the App's declaration and the installation's
  acceptance of the `DependencyUpdates` need are read; a gap blocks onboarding with the reason, retried daily until the
  pull request exists.
- **D-L15. Platform majors, migrations, gap B's new-package cap, R19's held versions and workflows not run on `pull_request`
  (R15)** each refuse the enqueue with their own line in the body. A security run follows D-P15 (the policy's `Security` mode
  always), whether or not the pull request was blocked.

~~**Not built here:** the fleet view (§8 readiness, the propagation wave), the App manifest change itself (Workflows: write,
the `release` and `dependabot_alert` events), docs (`hosting.md`, the README)~~ **built in the next wave (D-N1–D-N5)**,
except the propagation wave view (the fleet view's per-package rows show which consumer is on which version, which is what
the wave view was for) and any live capture: every GitHub body in `GitHubForgeDependencyUpdatesTests` follows the REST
docs and S18 must replace them with captures. The App's settings on GitHub are not in the repository (no App manifest JSON
exists here); the owner changes them by hand, as [hosting.md §6](hosting.md#6-github-app) lists.

#### Saga gaps, permissions, the fleet view and P12 (2026-10-16)

Built in `CodeCoverage/Dependencies/Lifecycle` (`DependencyFixPins`, `DependencyRelockPlan`, `DependencyCheckFailure`,
`DependencyFleetView`, `DependencySlots.Next`), `CodeCoverage/Dependencies/DependencyLookupIngest.cs`, the replay
(`RollingBranchReplay`: `relockable`, `RelockTrailer`) and the writer's rolling job (`relock-needed`), the read endpoints in
`BrowseController.DependencyUpdates.cs`, the App need in `GitHubInstallationPermissions` and `AccountPermissionsController`,
and the Angular panels `account-dependency-updates-panel` and `repo-dependency-updates-panel`. User guide:
[dependency-updates.md](dependency-updates.md). Decisions (assistant, within R1–R19 and D-L*):

- **D-N1. A human fix keeps the versions it was made against (R4).** *Detection by the fix's base:* the commits above the
  bot's own (`RollingBotSha`) are compared against it (the forge's compare); if they change any path that is not a package
  file or lockfile of the plan that commit was built from (`DependencyUpdateRunData.RollingPlanJson`, kept across runs,
  cleared when the pull request is merged or closed), they are a fix. A truncated or unreadable compare counts as a fix.
  *The pin:* each group of that previous plan whose versions the policy moved (or no longer plans) is planned again from
  the previous plan's items, token for token, over the current lookup records (nothing parsed, R1). A person's own hold
  (comment or human value) wins, as does a record whose current value moved on the default branch, and platform groups are
  not pinned. A pinned group never auto-merges (a person already changed code there). The pins are listed in the body
  ("Pinned for a fix on this branch") and on the repository panel. Rejected: pinning every dependency of a branch with any
  human commit (would freeze manifest-only edits, which Q11 already covers); reading the fix's imports (per-ecosystem code,
  R1).
- **D-N2. Lockfile conflicts in the replay are regenerated, not stopped.** Mirrors the merge queue's take-one-side +
  regenerate (O35/O37) without its per-rule workflow: the writer takes the **bot's side** of a conflicting lockfile that
  belongs to the plan (never a textual merge), finishes the replay, and pushes the replayed head to the work ref instead of
  the rolling branch (`relock-needed`). The saga commits a **relock plan** on it (`DependencyRelockPlan`: per lockfile, the
  plan's items that own it, as lockfile-only updates at the versions already in the manifests; owners a person changed by
  hand are left out) and dispatches the repository's own **apply workflow** in purpose `relock` (no action change: lockfile-
  only items run pin + relock + verify, scripts off, D-C3 consistency check). Its patch is judged by the same `dependencies`
  profile against the replayed head, then committed on it with a `Dependency-Relock:` trailer and pushed under the lease of
  the head the replay read; a later replay skips relock commits (they are derived, and made again when needed). Any failure
  (relock run red, a group not regenerated, a lockfile whose every owner a person changed) falls back to the old stop. One
  more fallback: a replay conflict whose human values have just become Q11 holds is retried **once, at once** (a refresh,
  which no longer plans those dependencies, so the same commits replay cleanly); a second conflict stops for a person.
  Rejected: reusing the merge queue's `lockfile-regenerate` action (it needs a merge-queue rule and its provenance refuses
  new versions by design, §13.5); a new action mode (a rebuild of the committed `dist` for what lockfile-only items already
  do).
- **D-N3. Build vs test failure, from check names only.** The failing check runs' names are classified (`build`, `compile`,
  `restore`, `lint`, `typecheck`, `pack` → build; `test`, `e2e`, `spec`, `integration`, … → test; both → both) and the bisect
  and red comments say "a build check failed" or "a test check failed". Logs and annotations are not read: a log is runner
  output, and the Actions log API per job would buy only the comment's wording; a check named `ci` stays unknown. The
  bisect itself is the same either way. This is the shipped behaviour, not a deferral.
- **D-N4. The `DependencyUpdates` App need is complete.** Contents, Pull requests, Checks, Issues, Actions and Workflows
  write, Metadata read, **Dependabot alerts read** (`vulnerability_alerts`, shown as "Dependabot alerts"), and the events
  `workflow_run`, `issue_comment`, `check_run`, `check_suite`, **`pull_request`** (the saga's merged/closed signals),
  `push`, `release`, **`dependabot_alert`**. Onboarding checks it (D-L14); the Account page's permission notice adds its
  App-side gaps **only for an account with dependency updates on** (an account without the feature is never asked). The
  repository has no App manifest JSON; `hosting.md` §6 and the README list the permissions and events.
- **D-N5. The fleet view, readiness and the repository panel are built from the saga's own state**, never from a forge or a
  registry: run documents, policy state, the last report (projected server-side, the lookup JSON is never loaded) and the
  manifests. *Visibility:* the repositories are exactly `GetAccountRepos`' list for the viewer (#453); every document is
  loaded by an id derived from one of them; the account's red versions are used only for the repository they belong to.
  *Health* per repository: off, new, onboarding, blocked, running, waiting, bisecting, red (`red`, `bisected`), frozen
  (`replay-conflict`, `rolling-rewritten`), **silent** (`dispatch-uncorrelated`, `dispatch-refused`, `*-timeout`,
  `*-missing`: a dispatch whose run never answered), failed, ok. *Packages:* the ones two repositories or more use, or with
  something going on (planned, awaiting approval, held, red), at most 300, with each repository's version (P12's lookup
  version, else the scanners' constraint). *Readiness* per platform (Angular via `@angular/core`, and the SDK platform
  records): the next major is the highest any lookup offered or any repository is on (R17's leading segment only); per
  repository on-target, in-pull-request, ready (offered, and only an approval, the cooldown or an account rule in the way),
  blocked (an R2 blocker hold, with its blockers; or fleet-red), held, or not-offered; listed only when someone is behind.
  *Next slot:* `DependencySlots.Next`, the same day/hour rule as the cron, skipping a window whose run already started.
  *P12:* the lookup's records are stored on the repository's manifest (`LookupConsumes`, with versions; platform and runner
  records left out; names normalized like M3's parsers); the graph and the fleet view read them for every ecosystem the
  lookup reported and keep the scanners for the rest. Produces stays the scanners' (a lookup says what a repository uses,
  not what it publishes). The settings UI is Spark's own PO pages (Account, Repository, `DependencyRule`), already exposed
  by the N2 model.

#### Who reads an owner's settings (2026-10-16): D-A1–D-A8

**Found:** N2 put the dependency settings on `Account` (`DependencyUpdates`, `DependencySchedule*`, `DependencyTimeZone`,
`DependencyCooldown`, `DependencyBisectBudget`, the `DependencyRules` AsDetail) and on `Repository`, next to the merge-queue
and branch-deletion settings. `security.json` grants `QueryRead/Account` and `QueryRead/Repository` to the anonymous role,
`AccountActions.GetRowFilterAsync` returns no read filter, and `GetProtectedAttributesAsync` hid only `BadgeToken`. So
anyone, signed in or not, read every account's settings and every public repository's settings, and listed (sorted,
filtered, distinct-valued) the account grid on them. `DependencyRules` can name packages of private repositories.
*Red run* (`OwnerPrivacyEndToEndTests` before the fix, 9 of 30 failing): a visitor's and a stranger's account load carried
`DeleteBranchOnPrClose`, every `Dependency*` attribute and `DependencyRules`; their public-repository load carried
`DeleteBranchOnPrClose`, `DependencySchedulePolicy`, `DependencyCooldown` …; `GetAccounts` answered 200 to a visitor, a
stranger and the owner, sorted and filtered by `DeleteBranchOnPrClose`. The `/api/browse` endpoints (account ref, repos,
dependency graph, both dependency-updates views, sparklines, repo) leaked none of the seeded markers.

| # | Decision | Rejected | Evidence / reason |
|---|---|---|---|
| D-A1 | **Layering: `security.json` is the allow-list, the actions class narrows it per request and never widens it.** The grants stay (`QueryRead/Account` to everyone, `Edit/Account` to signed-in users); `AccountActions`/`RepositoryActions` take away per record and per caller | per-user rules in `security.json` (it has no per-record form) | owner, 2026-10-16. Spark's `OnDisableActionsAsync` runs only after the type right and the row gate, so it cannot grant (Spark test `Withholding_nothing_never_grants_what_security_json_denies`) |
| D-A2 | **The account grid is the Administrators' alone, withheld per request** (`AccountActions.OnDisableActionsAsync` disables `Query` on the query target for anyone without `ReadSettings/Account`). **"Isn't Read enough?" — yes for every page, but `security.json` cannot express "Read without Query":** Spark expands `Read` ⇒ `Query` (`SparkRightImplications`, #324), and the type catalogue a detail page renders from is Query-scoped, so `Read/Account` is the same grant as `QueryRead/Account` | narrowing the grant to `Read/Account` (measured: the posture report still listed `Query/Account` as reachable anonymously); denying `Query/Account` (a deny beats the Administrators' grant, and blanks the catalogue) | no page runs `GetAccounts`: the account page is `POST /spark/po/load` (`vanity-redirects.ts` → `/po/account/{id}`), the three `Account` references (Repository, ApiToken, GitHubProject) are read-only breadcrumbs resolved under `Read` (`BreadcrumbResolver`), `programUnits.json` has no accounts unit, and `/api/browse/accounts/*` queries the index itself. A grid must go because sort and filter run on stored values before any row exists, so redaction cannot hide them (`QueryExecutor`, #295) |
| D-A3 | **An account's settings are protected attributes for anyone who neither manages the account nor holds `ReadSettings/Account`.** Deny by default: `OwnerSettings.ProtectedAccountAttributes` is every property not on the public list (`Login`, `Type`, `AvatarUrl`, `Provider`, `Connection`, `DisconnectedReason`, `DisconnectedAtUtc`), so a setting added later is owner-only without anyone remembering | `DisableActions("Read")` on another owner's account (removes the public account page); static attribute denies (`Read/Account/DependencyRules`: managers are signed-in users too, and a deny beats every grant) | Spark nulls protected attributes on every read path (detail load, query rows, breadcrumbs, refresh, retry, live socket) and shields them from write-back (`IPersistentObjectActions.GetProtectedAttributesAsync`) |
| D-A4 | **Edit is withheld on an account the caller does not manage** (`OnDisableActionsAsync`, object target), the administrator included. The write row filter stays | only the row filter (the page offered Edit and the submit 404'd); letting administrators edit | the page and the submit answer from one rule; the filter keeps an edit of somebody else's account a 404 |
| D-A5 | **Repository: the same, deny by default.** Public: `Account`, `Archived`, `Connection`, `DefaultBranch`, `Disconnected*`, `FullName`, `IsPrivate`, `LatestCoverage*`, `Name`, `OwnerLogin`, `Provider`; everything else (settings, gate, fork budget, keys) is protected for outsiders. **`BadgeToken` stays the managers' alone**, administrators included | administrators read the badge token | it is a credential (badge access on private repositories), not a setting. The repository grids show only public attributes, so no sort/filter oracle reaches a protected one |
| D-A6 | **Administrators = `ReadSettings/Account` and `ReadSettings/Repository` in `security.json`**, granted to the Administrators group (forge id in `Coverage:Administrators`). The verb is declared with `[assembly: SparkReservedActions(typeof(OwnerSettingsRights))]` | hard-coding `CoverageAdministrators.GroupId` in the actions (bypasses the allow-list); a free-standing target such as `ReadAll/OwnerSettings` (SPARK011 + SPARK012: Spark could not see that the app asks for it) | owner: "except for administrators"; the posture report lists both rights |
| D-A7 | **The dependency-updates views stay as built (D-N5).** They show, per repository the caller can see, its own status, the next slot and a schedule line; never the account's rules, check exclusions or badge token | redacting the schedule line for outsiders | the panel is the public status of a repository the caller can already see; the regression test asserts the rule, check-exclusion and badge-token markers never travel on any `/api/browse` endpoint |
| D-A8 | **Spark: `Read` and `Query` can be disabled too, enforced server-side.** `Read` withheld on an object refuses `POST /spark/po/load` (and the live socket's load); `Query` withheld on a query refuses `/spark/queries/execute` and `/distinct-values`. Both answer as a missing object or query (404, or the uniform 401 to a visitor), never 403. ng-spark needs no change: it renders no Read/Query buttons and already handles the 404 | a 403 (confirms existence, #453) | owner requirement; `DisableActionsTests` red without the change (3 of 20 failing: `found HttpStatusCode.OK`) and green with it (20/20) |

**Regression test** (`apps/CodeCoverage/CodeCoverage.Tests/Controllers/OwnerPrivacyEndToEndTests.cs`): the real composition
root, the app's own `security.json`, model and actions; the forge scripted per caller; the Administrators group the real
`CoverageAdministrators` provider. Callers: a visitor, a signed-in stranger, the account's manager, an administrator who
manages nothing. It fails when a model attribute of `Account` or `Repository` is on neither of its public/private lists,
asserts that an outsider's account and repository loads carry nothing outside the public list, that only an administrator
runs, sorts, filters or lists distinct values of `GetAccounts`, that the repository grids and the account page's sub-query
carry public attributes only, that no `/api/browse` endpoint carries a seeded marker, and that managers and administrators
do see the settings (administrators without the badge token).

**SparkTestDriver or the app host?** `SparkEndpointFactory` can boot with the app's `security.json`
(`SparkTestSecurity.FromFile`) and its model files, but it is its own minimal host: none of `Program.cs`'s registrations
(the visibility service, the forges, `CoverageAdministrators`, the browse controllers, the app's indexes) would be there
unless the test re-registered them by hand, which is the composition under test. `CoverageWebAppFactory` boots the real
`Program`, so the test uses it, with the header sign-in `HostMetricsDashboardTests` already uses.

### 13.13 After S18: event-driven onboarding, rate-limit retries, and the defects found live (2026-10-16)

#### 13.13.1 S18 live results (`PieterjanDeClippel/DependencyUpdaterTest`)

The test repository is public and scratch. It pins old NuGet packages (Newtonsoft.Json, Serilog, Humanizer.Core, with `packages.lock.json`), old npm packages (lodash, chalk, date-fns, typescript, with `package-lock.json`), and `actions/*@v4`. Its `coverage.yml` sets `mergeQueue: { enabled: true }`. Dependency updates are enabled on the Repository only; the account stays on "use account setting" (off).

**Passed:**
- **Onboarding PR #1.** Opened by `app/coverageproduction`. One commit with the two templates, pinned to `bfd709a9` (`dependency-update-v1`), each with `contents: read`, no `id-token` and `persist-credentials: false`, and a commented `dependencies:` section appended to the existing `coverage.yml`. Its CI was green.
- **Merging #1 started the first run.** `PullRequestEndedAsync` triggers a refresh.
  - The lookup was dispatched on `master` (17:11:53, success) and the apply on `mintplayer/dependencies-work` (17:12:55, success).
  - **Rolling PR #2** was opened as a **draft** at 17:13:39. It holds one commit touching only `package.json`, `package-lock.json`, `Lib.csproj` and `packages.lock.json`, and applies 6 groups within the default `minor` threshold.
  - The PR body lists 12 groups "awaiting approval" (all majors, the Actions majors, the platforms), each with its `approve <group>` line. .NET 11 is "available, not applied" as a preview (R7).
  - Its CI (`build`) is green, and so is the bot's own **Dependency updates** check run.

**Defects found:**
- **S18-D1: platforms appear twice.** The `setup-node`/`setup-dotnet` `with:` records (`node` 20 → 24.22.0, `dotnet-sdk` 8.0.x → 10.0.402) are separate groups from `platform:node 26` / `platform:dotnet 10.0`, with different targets. Per D-S12c, records of a platform's `setup-*` `with:` values (and its images) must fall under that platform's record. One approval then moves every location to one target.
- **S18-D2: the scheduled onboarding is not event-driven.** The repository was enabled at about 17:03; the onboarding PR came from the 10-minute cron. See §13.13.2.

**Also passed (17:14–17:38):**
- **Tree-equal `refresh`.** The comment got 👍. Lookup and apply ran (17:15:05, 17:16:02). The head stayed `e28f07d8`, with **no push and no CI run**.
- **`approve actions/checkout` and the human-commit replay.**
  - A human commit `da7a4fa6` (`NOTES.md`) was pushed to the rolling branch, then `approve actions/checkout` was commented, which got 👍.
  - The rebuild gave "Update dependencies (7 groups)" plus the replayed human commit `04b3e974` on top, and `NOTES.md` was kept.
  - `ci.yml` went from `actions/checkout@v4` to `@v7` (tag style kept, D-V1). The unapproved `setup-*@v4` stayed.
  - The push changing `.github/workflows/ci.yml` went through, so the `workflows: write` token and the R15 carve-out work live. CI was green.
- **Merging the first (draft) PR #2** by hand ended the draft phase.
- **A scheduled, non-draft run.**
  - `ms` 2.1.1 was added to `master`, with a repository schedule override of daily at 17:00 UTC (spread minute :30).
  - The cron fired at 17:30:04 (lookup) and 17:31:04 (apply). **PR #3**, not a draft, applied 2 groups (`ms` patch, `typescript` minor). CI and the bot check went green, and the bot handed it to the merge queue (R5).
- **The merge queue merged #3 at 17:37:55** ("Merged by the queue"), once the S18-D3 workaround below was applied.

**More defects:**
- **S18-D3: the merge queue refuses the bot's own rolling PR.**
  - The refusal read: "Refused: approval — @coverageproduction[bot] does not have write access, so an approval of commit `a69d6514` by someone with write access is needed." The approval floor for authors without write access applies to the App, which is never a collaborator, so auto-merge can never happen without a human approval.
  - **Fix:** a rolling PR whose head the bot pushed and validated itself (`DependencyUpdateRun.RollingSha` = the PR head) counts as trusted for the approval floor. Branch protection's required reviews still apply, because GitHub enforces them.
  - Workaround used in S18: the owner's approval plus `/queue`.
- **S18-D4: testing waits for the schedule.**
  - There is no way to start a *scheduled* run (the only kind that may auto-merge, D-L) on demand. The owner asked whether to edit the `SparkMessages` documents on production; that was rejected as unsafe for the messaging state machine.
  - **Proposal:** an admin-only "Run the scheduled slot now" custom action on Repository, which broadcasts `Trigger(Scheduled)` with a fresh dedup key.

**Not yet done:** re-capturing the GitHub response bodies for `GitHubForgeDependencyUpdatesTests` from this run.

#### 13.13.2 Event-driven onboarding (owner, 2026-10-16)

Owner: "Each time someone installs the app on one of his repos, accounts or organizations, the bot should broadcast a message on the message queue … create the onboarding pull-request — if it fails because a rate-limit is hit, the message-queue will retry automatically with an incremental backoff. The bulk of all that complex code already exists as generic library." Also: "the recipient should be idempotent. So if the code fails halfway, the retry cannot fail at the first line again because the first file already exists."

**What exists, investigated 2026-10-16 with file:line evidence in the session:**

*Webhooks:*
- One catch-all recipient, `GitHubEventsRecipient : IRecipient<GitHubWebhookMessage>` (GI `Recipients/GitHubEventsRecipient.cs:29`). The typed `GitHubWebhookMessage<TEvent>` is deliberately unused (see that file's doc comment).
- `installation` and `installation_repositories` upsert the repositories, then send `ReconcileAccountMessage`.
- `ReconcileAccountRecipient` (CC `Ingestion/ReconcileAccountRecipient.cs:21-49`) is the first point where repositories have a `DefaultBranch`, and it already fans out the manifest scans.
- `OnboardAsync` returns early without a `DefaultBranch`, so hooking the raw webhook would silently do nothing.

*Messaging (`libs/messaging`):*
- Per-handler retries: `MaxAttempts` 5, with backoff from a table (5 s, 30 s, 2 min, 10 min, 1 h), configurable per queue. After the last attempt the message is dead-lettered.
- `NonRetryableException` dead-letters at once.
- `BroadcastOnceAsync` deduplicates enqueueing by key, per message type.
- `IMessageContext.MessageId` stays the same across retries, and `IMessageCheckpoint` / `IMessageProgress` let a recipient resume.
- A parked message does not block its lane.
- A recipient **cannot** ask for a specific delay.

*Spark save hooks:*
- `IAfterSaveCommitted<T>` is a durable outbox: it is retried, and the recipient throws to retry. `SaveContext.Before` gives the stored entity, so an off→on change can be detected.
- `RepositoryActions` has `IBeforeSave<Repository>`; `AccountActions` has no save hook.

*Onboarding today:*
- About 10–14 GitHub calls, plus one git-writer push of a single commit.
- It is already idempotent on three points: an early return on state, "workflows already on the default branch → Done", and find-before-create of the PR. The branch is pushed as a whole, never file by file.
- **Gaps:**
  - It does **not** short-circuit `Blocked`, so an owner-closed PR would be reopened by an event-driven trigger. Only the cron filters that case today.
  - The Onboard signal is not wrapped in `GuardedAsync`, so failures go straight to the message bus's retry, which is fine.
  - `GetTreeAsync` and `FetchFileContentAsync` **swallow every error and return null**. A rate-limited onboarding would build its PR from an empty tree and a missing `coverage.yml` or dependabot file, and open a **wrong** PR.
  - A secondary-rate-limit 403 on the push is read as a permission problem (`Blocked`).
  - Rate limits become `ForgeRequestException(…, 429)`, and the reset time is lost.

**Proposed design (P13–P18, to grill):**

| # | Proposal |
|---|---|
| P13 | **Triggers.** (a) `ReconcileAccountRecipient`, after its manifest-scan fan-out, broadcasts an `Onboard` signal for every repository whose setting **resolves on** and has not been onboarded. That covers installs, repositories added to an installation, and permissions accepted. (b) `IAfterSaveCommitted<Repository>` / `<Account>` broadcast `Onboard` when `DependencyUpdates` changes from off to on. An account fans out over its repositories that resolve on. Both use `BroadcastOnceAsync` with the key `dependency-onboard:{repoId}:{settingsChangeId or installation event}`. The 10-minute cron stays only as the fallback for a `Blocked` onboarding, once a day |
| P14 | **The setting stays the gate.** Installing the App does not onboard repositories that resolve off; the account default stays off ("the updater pushes branches into somebody else's repository", `Account.cs:139-143`). Enabling the setting is the trigger. Recommended; owner decision |
| P15 | **Idempotent recipient.** Each step is "look up, then create or reuse": the branch is rebuilt as one commit and force-pushed with a lease (never file by file); the open onboarding PR is found by its head branch and updated, never duplicated; a 422 "already exists" counts as success; progress (branch head, PR number) is saved to `DependencyUpdateRun` after each step. `OnboardAsync` itself short-circuits `Done`, `PullRequestOpened` and **owner-closed `Blocked`** (moved from the cron). Test: fail after each step, deliver the retry, and expect exactly one branch and one PR |
| P16 | **Rate limits are retried and never mistaken for data.** Classify GitHub 429, 403 with `x-ratelimit-remaining: 0`, and the secondary-limit 403 (its documented message, or `Retry-After`) as `ForgeRateLimitedException(retryAfter)`, carrying the reset or `Retry-After`. `GetTreeAsync` and `FetchFileContentAsync` must rethrow it (and every non-404 error) instead of returning null. A missing file is a 404, and only a 404 |
| P17 | **Library: `RetryAfterException(TimeSpan)`** in `MintPlayer.Spark.Messaging.Abstractions`, next to `NonRetryableException`. The processor parks the handler for `max(table delay, hint)`, keeps the expiry check, and counts the attempt toward `MaxAttempts`. That bounds an endlessly throttled message; a larger `MaxAttempts` can be set for the dependency queue. This is generic: every external API has 429s. `ForgeRateLimitedException` derives from it, or is mapped to it at the recipient boundary |
| P18 | **Library defect found by the investigation:** a reclaim (host crash, lapsed claim) increments `SparkMessage.AttemptCount`, but dead-lettering only looks at the handler's `AttemptCount`. A message that keeps killing its host is never dead-lettered, despite the comment at `MessageRetrySweeper.cs:218`. Fix it in the same PR, with a red→green test |

Also in the same PR: **S18-D1** (platforms appearing twice), **S18-D3** (the bot's own rolling PR trusted for the approval floor), **S18-D4** (a "run the scheduled slot now" action), and re-capturing the GitHub bodies.

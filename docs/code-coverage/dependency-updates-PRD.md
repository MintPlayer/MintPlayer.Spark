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

The rest of this document (§1–§12) is the plan as grilled. It stays valid as a design, but nothing beyond M3 is
committed to. If the work resumes, start from §12's decision log, which records why each choice was made.

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

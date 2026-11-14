# Backporting to release lines (#568) — PRD

Status: **design grilled (2026-11-05), decisions D1–D19 in §8; #569 spikes S3, S6, S9, S10 done (§8.1), milestones M1, M2, M3, M4, M11 implemented (§8.2), merged as PR #572 (`e1908e0e5`, 2026-11-05). #570 next, on
`feat/issue570-backports`.** §5 is the pre-grilling proposal; §8 supersedes it. Plan: [coverage_backport_plan.md](coverage_backport_plan.md).
Parent decisions: [coverage_package_versioning_PRD.md](coverage_package_versioning_PRD.md) D10 (backports), D11 (dist-tags),
D12 (auto-cut). Conflict machinery: [coverage_merge_queue_PRD.md](coverage_merge_queue_PRD.md) §10, O33–O37.

## 1. Problem

The default branch always targets the newest platform (.NET 11, Angular 22). Users on an older platform still need
fixes and, sometimes, features. .NET can multi-target; Angular, React and Vue can't, so an older platform means a
**release line**: a branch whose packages target that platform and carry its major (CLAUDE.md "major version is
locked to the targeted platform").

Issue #568 asks for a **resilient** way to move a merged change from the default branch onto one or more release
lines, offered by the CodeCoverage app so every repository on it can use it. Requirements stated in the issue:

- R1. The head of the default branch stays buildable, runnable and on the latest platform. Merging the default
  branch into a release branch is never an option (it would drag the new platform markers along).
- R2. Version numbers never conflict (they live in tags, #153), but platform markers do: the SDK pin,
  `<TargetFramework>`, `package.json` framework ranges, lockfiles.
- R3. Trivial conflicts are resolved automatically, reusing the existing `IConflictResolver` classes.
- R4. Decide how a pull request states its target lines: labels per PR, or labels added by default and removed by
  the maintainer.
- R5 (owner, 2026-11-05). **Library dependencies follow the line.** `MintPlayer.Spark.Dashboards` targets .NET 10 on
  the .NET 10 line and .NET 11 on the .NET 11 line, so every dependency it ships with must resolve to a version
  that targets the same platform. Same for Angular, React and Vue packages.

### 1.1 Two premises in the issue, corrected with evidence

- **"Cherry-picking removes the commit from the original branch": false.** A cherry-pick writes a new commit on the
  checked-out branch and never touches the source. Verified: `main` was byte-identical before and after
  `git cherry-pick -x` (research note 2, §2.1, `exp1.log` lines 4–9).
- **"Apply the patch instead of cherry-picking": the same operation.** `git cherry-pick`, `git am -3`,
  `git apply --3way` and `git merge-file` all perform the same three-way merge against the change's parent and
  give the same conflicts; `am`/`apply` without `-3` only lose the ability to produce markers (research note 2,
  §2.2 table). The app already does this three-way merge without a work tree, with
  `git merge-tree --write-tree --merge-base=<parent> <target tip> <change>`
  (`apps/CodeCoverage/CodeCoverage.GitWriter/Jobs/BackportService.cs`).

## 2. What already exists (evidence)

**Backports are built, merged and deployed.** #153 M10 (PRD D10) shipped in `36eee549b` (#537, 2026-10-24).
`code-coverage-deploy.yml` deployed master `17d6b6681` on 2026-11-05. `docs/code-coverage/README.md` still says
versioning is "not yet deployed": stale, fix in M1.

| Piece | Where | What it does today |
|---|---|---|
| Triggers | `CodeCoverage.GithubIntegration/Recipients/GitHubEventsRecipient.cs:506-585` | `backport: 8.x` label at merge or after merge; `/backport 8.x 9.x` comment |
| Syntax | `CodeCoverage.Library/Forge/BackportCommand.cs` | line = major, `N.x` or a branch name; ≤ 10 lines |
| Orchestration | `CodeCoverage/PackageVersioning/BackportRecipients.cs:50-246` | Write role for label/comment; reads coverage.yml from the default branch; resolves lines against `versioning.releaseBranches`; one PR per line titled `[8.x] <title>` from `backport/<pr>-<line>`; conflict comment with manual `git cherry-pick -x` commands |
| Writer job | `CodeCoverage.GitWriter/Jobs/BackportService.cs`, `POST /jobs/versioning/backport` | bare repo; detects merge / squash / rebase; `merge-tree` per target; **any conflict refuses every line**; commit keeps the author, adds `(cherry picked from commit …)` trailers; create-only push |
| Auto-cut | `CodeCoverage/PackageVersioning/ReleaseBranchCutRecipient.cs` (D12) | creates `release/<M>.x` at the first parent when a merge moves a platform major |
| Versioning on a line | `CodeCoverage.Versioning/Releases/ReleasePlanner.cs:178-231` | `lastOnLine` = highest tag with the branch's major, so master's 11.x tags don't disturb `release/10.x` |
| npm dist-tags (D11) | `versioning-action/src/semver.ts:35-50` | older stable major → `v<major>-lts`; **any prerelease → `next`** |
| Conflict resolvers | `CodeCoverage.Git/MergeQueue/Resolution/*` | take-base, keep-both (JSON), line-set, evict + 7 validators; pure functions of the stage blobs |

**Gaps against #568**

- G1. **No conflict resolution on backports.** `BackportService.cs:9-10`: "no conflict resolver runs on old
  lines". This **contradicts the decision record**: PRD D10 step 1 says the dry run uses "`RollingBranchReplay` and the
  `IConflictResolver` registry". The code comment cites spike S8 (the queue's replay rebases a branch onto a moving
  base and doesn't fit). D10 has to be corrected either way.
- G2. **All-or-nothing across lines.** One conflicting line blocks the lines that apply cleanly.
- G3. **Platform markers aren't understood.** A change next to `<TargetFramework>` conflicts. Experiment B:
  `<Nullable>` edited on the line below a differing `<TargetFramework>` conflicts with every three-way method
  (research note 2, §2.2–2.3).
- G4. **No regeneration** of lockfiles or generated files on a backport branch. The merge queue dispatches
  `regenerate.yml`, but only on its queue branch.
- G5. **Release branches don't build.** `pull-request.yml:5` and `dotnet-build-master.yml:5-6` trigger on master
  only, so a backport PR gets no CI, which D10 relies on ("the CI run on that pull request is the compile check").
- G6. **One major per branch for every ecosystem.** `BranchMajor` applies to NuGet and npm alike
  (`VersionCalculator.cs:48`), and any refusal aborts the whole run (`ReleaseRun.cs:96`). Spark ships both, and their
  majors move independently (.NET yearly, Angular every six months), so a `release/10.x` that also holds
  ng-spark 22 refuses the run.
- G7. **Lines can only be called `release/<N>.x`.** `release/net10` or `dotnet-10` gives `branch-not-versioned`, or
  the major refuses to go down (`ReleaseRun.cs:187-195`).
- G8. **A prerelease on an older line takes npm `next` from the default branch** (`semver.ts`).
- G9. **Cross-branch plan staleness.** The plan fingerprint covers every tag (`TagHistory.cs:27-31`), so a tag pushed
  from master between a release branch's plan and release jobs refuses the release (`ReleaseRun.cs:143-148`).
- G10. **`schema-revision`** (`dotnet-build-master.yml:36-120`) would publish schema releases from an old line.
- G11. **Dependencies aren't line-aware** (R5, §5.6).
- G12. **Backport needs a `versioning:` section** (`BackportRecipients.cs:101-103`), so a repository can't backport
  without onboarding versioning.

## 3. How others do it (research summary)

Full notes with file:line evidence: [research note 1](research/backport-research-1-angular-dotnet.md),
[2](research/backport-research-2-ecosystems-git.md), [3](research/backport-research-3-inrepo-mergequeue.md),
[4](research/backport-research-4-versioning-branches.md).

| | Angular `ng-dev` | dotnet `arcade` | CPython miss-islington | korthout/backport-action | Kibana `backport` | k8s prow |
|---|---|---|---|---|---|---|
| When | at merge | after merge | before or after merge | after merge | after merge | after merge (armed on open PR) |
| Trigger | exactly one `target:` label → branch set from release trains | `/backport to X` comment, write/admin only | `needs backport to X.Y` label | `backport X` label / comment | version labels → `branchLabelMapping` | `/cherrypick X` or label, org members |
| Apply | dry-run `cherry-pick -x` on all, atomic push | cherry-pick if patch-id equals PR diff, else `am --3way` | `cherry_picker` (`-x`) | cherry-pick (squash/rebase/merge aware) | cherry-pick | `git am` |
| Conflict | abort all; author opens a PR on the branch | comment with git output, no PR | comment, assign merger, no PR | comment, or **draft PR with markers** (opt-in) | comment + CLI; now an agentic resolver workflow | comment (+ issue) |
| Dedupe | atomic push | deterministic branch, force-push | none (re-label) | branch, empty pick = skipped, 422 = skipped | PR-body marker | branch exists → link PR |
| Policy | merge rights | servicing labels + admin-only merge on `release/*` | core devs | — | auto-approve clean ones | `cherry-pick-approved` label |

Lessons:

1. **Mechanism is cheap; policy is separate.** dotnet: anyone with write access can create a backport PR, and
   Shiproom plus branch protection decide what ships.
2. **No project auto-merges a conflict.** The variants are "comment and stop" and "draft PR with markers".
3. **Angular doesn't cherry-pick into LTS at all** ("LTS branches diverge too fast"), and Kibana now runs an AI
   resolver on failures. Old lines drift, and the resolver budget has to grow with their age.
4. **Branch discovery is data.** Angular uses the branch regex `^\d+\.\d+\.x$` plus npm `vN-lts` dist-tags.
   Kibana maps version labels to branches.
5. **Idempotence:** deterministic head branch, `-x` trailer as the authoritative record, patch-id as a fast pre-check
   (it stops matching after a conflict resolution), and an empty pick reported as "already present".
6. **Vue and React have no per-platform release lines.** Nothing to copy there.

## 4. Goals and non-goals

Goals:

- One merged PR → one backport PR per requested line, **resolving platform-marker and generated-file conflicts
  automatically and safely**. Safe means no real change is ever dropped silently (research note 2, §2.5: `-X ours` and
  a keep-target merge driver on a csproj both silently dropped a real `<Nullable>` change).
- Release lines that build, test, version and publish with their own platform and dependency versions (R5).
- Works for .NET, Angular, React and Vue repositories, and for repositories that mix them (Spark).
- Every outcome reported on the source PR: applied, resolved (and how), already present, conflicted (and what to do).

Non-goals:

- Merging a backport PR automatically. CI on the backport PR is the compile check against the old platform (D10), and
  a human or the merge queue merges it.
- Forward-porting from a release line to the default branch.
- Choosing what may ship (servicing approval). Branch rulesets express that.

## 5. Design (proposed; every ⚠️ is an open question in §7)

### 5.1 Release lines

A release line is a branch plus the **ecosystems** it versions. Proposal (Q1):

- Branch name `release/<ecosystem>-<major>.x`, for example `release/dotnet-10.x` and `release/angular-21.x`, for every
  repository (D1). Existing lines in MintPlayer repos use other names (see §5.1.1).
- On an ecosystem-scoped line, **only that ecosystem's packages release**. The others are left out of the plan,
  not refused (fixes G6). Their majors would collide with the default branch's (ng-spark 22.x on a .NET 10 line).
- `BranchMajor` is taken from the name, and it applies only to the line's ecosystem (engine change, spike S3).
- Lines are listed explicitly in coverage.yml with a `supported` flag. Auto-cut (D12) adds a line and an
  end-of-life entry retires it, which is Angular's "branches are data" lesson. The supported set drives default
  labels (§5.2) and dependency updates (§5.6).

#### 5.1.1 Existing lines in MintPlayer repositories (2026-11-05, `gh api repos/MintPlayer/<repo>/branches`)

| Repository | Line-like branches |
|---|---|
| mintplayer-ng-bootstrap | `angular-17` (+ `angular-21-def`, `angular-21-standalone`, `angular-21b`, probably not lines) |
| mintplayer-ng-video-player | `angular-19` |
| mintplayer-ng-seo | `angular-21b` |
| MintPlayer.Dotnet.Tools | `release/dotnet-6`, `release/dotnet-7` |
| MintPlayer.AspNetCore.Tools | `release/dotnet-6`, `release/dotnet-7` |
| MintPlayer.AspNetCore.SpaServices | `release/6.0`, `release/7.0`, `release/8.0` |
| MintPlayer.DotnetDesktop.Tools | `release/3.1`, `release/6.0` … `release/10.0` |

Four naming schemes are already in use; none is `release/<ecosystem>-<N>.x`.

### 5.2 Choosing targets (R4)

Proposal (Q2): the app **adds labels by default** for the supported lines that apply, and the maintainer removes
the ones that shouldn't get the change. The issue's suggestion, made safe by three rules:

- Labels are added when the PR is opened, and only for lines whose ecosystem the PR touches. A PR that only touches
  `libs/node_packages/**` gets no `dotnet-*` label. The test is the same changed-inputs test the versioning
  engine already uses (D4).
- A "Backport targets" check run lists the lines and which apply. It is informational and never blocks the merge.
- `backport.defaultLabels: false` turns this off. Labels and `/backport` keep working as today.

### 5.3 Applying the change

Keep `merge-tree` plumbing (S8 stands). Change `BackportService` so it:

1. **Normalizes platform markers before the merge** (new, G3, spike S2). In the *base* and *theirs* blobs of a
   marker file, rewrite every known marker value to the **target branch's** value, then do the three-way merge. A
   hunk that only bumped a marker disappears, and a real edit next to a marker merges cleanly, because both sides
   then agree on the marker line. Unlike "keep target", the PR's real edits are kept by construction. Markers:
   - .NET: `global.json` `sdk.version`; `<TargetFramework(s)>`; `PackageReference` versions of platform-locked
     packages (`Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`, `Microsoft.EntityFrameworkCore.*`, …);
     `Directory.Packages.props` likewise.
   - npm: platform package ranges in `package.json` (`@angular/*`, `react`, `react-dom`, `vue`, …) and
     platform-locked first-party ranges (§5.6).
   - CI: `dotnet-version` / `node-version` in `.github/**`.
   - Extensible in coverage.yml (Q5).
2. **Runs the conflict resolvers** (G1) with the line's `autoResolve` rules: the merge queue's rule shape and
   registry, and the same keep-both / line-set / take-base semantics. Stage 2 is the release branch, so take-base
   means "keep the line's side", which is safe only for wholly generated files.
3. **Regenerates** lockfiles and generated files (G4) by dispatching the line's own regenerate workflow on the
   `backport/…` branch before the PR opens. That workflow is the branch's copy, so it runs with that line's SDK and
   Node, and `package-lock.json` is never merged: take the line's side, then regenerate.
4. **Reports per line** (Q3): applied clean / applied with resolutions (listing each path and strategy) / already
   present (empty result) / conflicted.

### 5.4 When it still conflicts (Q4)

Proposal: the default stays the CPython/arcade behaviour. Comment the conflicted files and hunks, give the exact
local commands, and push nothing. Opt-in `backport.onConflict: draft` pushes the branch with the conflict markers
committed and opens a **draft** PR (korthout's mode), so the fix can happen in the browser. The
`NoConflictMarkers` validator then keeps that PR red until the markers are gone.

### 5.5 Idempotence and provenance

- Deterministic head `backport/<pr>-<line>`. If it exists, find its PR and link it instead of creating a second one.
- `(cherry picked from commit …)` trailers (exists) plus a hidden `<!-- backport-of: owner/repo#123 -->` marker in
  the PR body.
- After the backport PR merges, add a `backported: <line>` label on the source PR (Node's pattern), so "which lines
  have #123?" needs no git query.

### 5.6 Dependencies follow the line (R5)

Spark's dependencies come in four kinds, each handled differently.

| Kind | Example (`MintPlayer.Spark.Dashboards`, `ng-spark`) | On a release line |
|---|---|---|
| A. In-repo project | `ProjectReference` → `MintPlayer.Spark`, `.Authorization` | Built from the same branch, so same TFM. **Pack** must write the referenced project's version on *this* line (10.x), not master's. Today `MintPlayer.Versioning.MSBuild` uses the highest *reachable* tag, which should give 10.x on the branch: verify (S4). Optional upper bound `[10.2.0, 11.0.0)` (Q7). |
| B. First-party, platform-locked, other repo | `@mintplayer/ng-bootstrap ^22.34.0` (peer), `MintPlayer.Dotnet.Tools` | The other repository needs the same line (`ng-bootstrap` 21.x for `release/angular-21.x`). The app knows every onboarded repo's lines, so a line can **declare its upstream lines** and the backport check flags a change that needs a feature missing upstream (Q8). |
| C. Third-party, platform-locked | `Microsoft.AspNetCore.*`, `@angular/*` | Pinned per branch. Markers in §5.3 step 1. Dependency updates on a line are **capped at the line's major** (dependency-updates rolling PR per supported line, Q9). |
| D. Platform-free | `RavenDB.Client 7.2.6`, `MintPlayer.SourceGenerators 13.0.0` (netstandard), `@mintplayer/ui-core ^1.7.0` | Same across lines. A backported bump is an ordinary change. The dependency-updates bot may target lines too (Q9). |

Classifying a package as platform-locked reuses the versioning engine's `MajorAnchor` (D7). Our own packages anchor
by construction, and third-party ones are recognized by a built-in list (Microsoft.* aspnetcore/extensions/EF,
`@angular/*`, `react*`, `vue`) plus coverage.yml.

### 5.7 CI and publishing on lines

- `pull-request.yml` and `dotnet-build-master.yml` triggered for `release/**` (G5). Each line runs its own copy of
  the workflows (its own SDK pins), so the workflow files are markers too (§5.3).
- `schema-revision`, coverage baseline upload, deploys: default branch only (G10). Deploy workflows already are.
- npm prerelease on an older major → `v<major>-next`, never `next` (G8, small engine change).
- Plan staleness: fingerprint only the tags of the packages in the plan, not every tag (G9, or Q10).
- A `release/**` ruleset (deletions, force pushes, required checks); the Queue App exempt if the merge queue serves
  lines (`reference_merge_queue_ruleset_exempt`).

### 5.8 Decoupling from versioning (G12)

A `backport:` section in coverage.yml (defaults when absent) holding `lines`, `defaultLabels`, `onConflict`,
`autoResolve`, `markers` and `regenerate`. `versioning.releaseBranches` keeps working as the source of lines when
`backport.lines` is absent.

## 6. Spikes (details in the plan)

| Spike | Question it answers |
|---|---|
| S1 | **Conflict census.** How many of master's last ~100 squash merges apply to a reconstructed .NET 10 line, plain vs with normalization vs plus resolvers? Which files conflict? Sets the value of S2 and the resolver budget. |
| S2 | Marker normalization: does pre-merge rewriting keep every real change and drop marker-only hunks? (csproj XML, global.json, package.json, workflow YAML; research note 2's scenario B as the first test) |
| S3 | Engine: ecosystem-scoped lines (`release/dotnet-10.x`), other ecosystems out of the plan, per-ecosystem `BranchMajor`. |
| S4 | Pack on a line: does `MintPlayer.Versioning.MSBuild` emit `MintPlayer.Spark >= 10.y` for Dashboards on the line? Cost of upper bounds? |
| S5 | Live, on the `SparkTest` scratch repo: does an App-token-opened backport PR trigger `pull_request` CI on a release branch, and can the regenerate workflow be dispatched on the `backport/…` branch and its patch applied? |
| S6 | npm publish on a line: does `publish-npm-packages` honour `publishConfig.tag`; behaviour of `v<major>-next`. |

## 7. Open questions (for the grilling)

**Status 2026-11-05: all answered in §8**, except Q10, which is deferred to spikes S3/S9 with fixed criteria (D17).
Map: Q1 → D1/D2, Q2 → D5, Q3 → D6, Q4 → D7, Q5 → D8, Q6 → D9, Q7 → D10b, Q8 → D11, Q9 → D13, Q11 → D14/D15/D16,
Q12 → D14 (per-line prerelease override), Q13/Q14 → D12. Where §5 differs from §8, §8 wins.

- Q1. Line naming and scope: `release/<ecosystem>-<major>.x` with ecosystem-scoped release, or your `dotnet-10` naming,
  or combined lines?
- Q2. Default labels added by the app (and removed by the maintainer), or opt-in per PR?
- Q3. Per-line independence, or keep D10's all-or-nothing across lines?
- Q4. On conflict: comment only, or opt-in draft PR with markers? A per-repo or per-PR choice?
- Q5. Marker list: built-in per ecosystem plus a coverage.yml extension, or config only?
- Q6. Lockfiles: always "take the line's side and regenerate", never merged?
- Q7. NuGet dependency upper bounds `[10.y, 11.0.0)` on lines (and master)?
- Q8. Cross-repo upstream lines (ng-bootstrap, Dotnet.Tools): gate, warn, or nothing?
- Q9. Dependency-updates rolling PR per supported line, capped at the line's major?
- Q10. Plan staleness across branches: fingerprint scoped to the plan's packages, or serialize tag writing across branches?
- Q11. Which lines does Spark open now? There are no `release/*` branches and no 10.x/21.x tags. A first run releases
  every package as `10.0.0-preview.1` (preview pin). Cut a .NET 10 line from the commit before the net11 move, or wait
  for the next platform move?
- Q12. While `prerelease: preview` is pinned, every line release is a minor preview. Is that acceptable on a line?
- Q13. Approval: write access is enough to request (today); should merging into `release/**` need anything extra?
- Q14. Should the merge queue serve release lines (`mergeQueue.branches`)?

## 8. Decision log

| # | Decision | Evidence |
|---|---|---|
| C1 | **Not opinionated.** CodeCoverage is used for MintPlayer's repos but may be deployed by others. Every preference here is a coverage.yml default, never a hard rule. | Owner, 2026-11-05 |
| C2 | **Rolled out to every MintPlayer repository that ships libraries** (Angular and .NET), not only Spark. | Owner, 2026-11-05 |
| D1 (Q1) | A line is **ecosystem-scoped**, and the ecosystem is **always** in its name (`release/dotnet-10.x`, `release/angular-21.x`). Only that ecosystem's packages release on it. **No plain `release/<N>.x`**, not even for single-ecosystem repos: it would lock a repo out of adding a second ecosystem later. Supersedes the `release/*.x` default of D10/D12 (auto-cut must create the ecosystem form). | Owner, 2026-11-05; G6 (`VersionCalculator.cs:48`, `ReleaseRun.cs:96`) |
| D2 (Q1b) | The line **pattern is configurable** in coverage.yml, default `release/{ecosystem}-{major}.x`. The engine takes ecosystem and major from the placeholders; a pattern without `{ecosystem}` or `{major}` is refused (keeps D1). Existing branches are **renamed by onboarding**: the bot opens a PR with the coverage.yml change (and a table old → new), and **when that PR merges, the bot renames the branches** via the GitHub rename API (GitHub retargets open PRs and redirects old URLs). Rename permission for an App to be verified (spike S7). | Owner, 2026-11-05; §5.1.1 |
| D4 (Q2 support) | **Supported lines are derived from data**, with a coverage.yml override per line (`keep` past end of life / `retire` now). A **scheduled job in the app** collects which platform versions exist and which are maintained (.NET, Angular, React, Vue, Node, PHP, …; sources: dotnet `releases-index`, `endoflife.date`, the same ones the dependency updater already records in `PlatformCandidate.Eol`/`Lts`, `CodeCoverage.Git/Dependencies/DependencyContracts.cs:202-260`). The observations are **public, read-only data**: a Spark persistent object + query + program unit (page). When a line's platform reaches end of life the app **suggests** retiring it, by PR, never on its own. Ecosystems without a support policy fall back to the explicit list. | Owner, 2026-11-05 |
| D5 (Q2 labels) | **Default labels**: on PR open the app adds `backport: <line>` for every **supported** line (D4) whose ecosystem the PR changes (D4-style changed-inputs test). The maintainer removes what shouldn't go. `backport.defaultLabels: all \| fixes \| off` (default `all`); `fixes` labels only PRs that look like fixes (`fix:`/`fix(…):` title or a configurable label such as `bug`). An informational **"Backport targets" check run** lists the applicable lines in every mode, never blocks. Labels and `/backport` keep working. Angular-style single `target:` label (C) rejected as the default (too opinionated, C1); may come later as opt-in. | Owner, 2026-11-05 |
| D6 (Q3) | **Lines are independent.** Every line that applies (clean or auto-resolved) gets its PR; each conflicting line gets its own report. Supersedes D10's all-or-nothing (`BackportService.cs:9-10`). Reason: the source PR is already merged, so blocking a clean line buys nothing (Angular is atomic only because it backports *while* merging). Drift between lines is made visible by the "Backport targets" check run and `backported: <line>` labels, not by blocking. | Owner, 2026-11-05 |
| D7 (Q4) | **Unresolvable conflict**: default `backport.onConflict: comment`: comment on the source PR with conflicted files/hunks and copy-paste commands (`git switch -c backport/<pr>-<line> origin/<line branch>`, `git cherry-pick -x <sha>`, push); the bot opens the PR when that branch is pushed. Opt-in `draft` (repo setting, or `/backport <line> --draft` per request): push with markers committed, open a **draft** PR; `NoConflictMarkers` keeps it red; squash merge keeps markers out of history. Always: a PR into a line is **recognised as the backport** by its `-x` trailer or a "Backport of #N" body and linked/labelled; `/backport <line>` retries. AI resolver (Kibana) out of scope. | Owner, 2026-11-05; research note 1 §3 (arcade, korthout, CPython) |
| D8 (Q5) | **Built-in marker catalog per ecosystem**, extendable/disableable in coverage.yml (`backport.markers`). Reuses the dependency updater's platform location kinds (`contracts/dependency-update/platform-record.schema.json:32`: `tfm`, `global.json`, `dotnet-tools`, `docker`, `setup-action`, `engines`, `types-node`, `version-file`, …) plus framework packages (`Microsoft.AspNetCore.*`/`.Extensions.*`/`.EntityFrameworkCore.*` versions; `@angular/*`, `react`, `react-dom`, `vue` ranges) and first-party platform-anchored packages (versioning D7 anchors, e.g. `@mintplayer/ng-bootstrap`). **Safety rule:** a marker is rewritten to the line's value **only where the PR left it unchanged** (base value == theirs value); a PR that changes a marker itself (e.g. `net10.0` → `net10.0;net11.0`) conflicts normally and the report says it changed a platform value. Proven or refuted by S2. | Owner, 2026-11-05; research note 2 §2.5 (keep-target loses real changes) |
| D9 (Q6) | **Lockfiles and generated files: take the line's side, then regenerate** by dispatching the **line's own** regenerate workflow on `backport/<pr>-<line>` (GitHub runs the workflow file from the dispatched ref → line's SDK/Node), apply its patch (limited to `regeneratedPaths` + resolved files, merge-queue O35–O37 trust model), then open the PR. Onboarding (D3) adds the regenerate workflow to every supported line by PR. Missing workflow → PR opens with a "not regenerated" note. Lockfiles are never merged. | Owner, 2026-11-05; `coverage.yml` `mergeQueue.postRebaseActionName: regenerate.yml` |
| D10b (Q7) | **No NuGet upper bounds by default.** In-repo dependencies (`ProjectReference`) pack as `>= <the dependency's highest tag reachable on the line>` (to verify in S4); the TFM already stops a net10 consumer from restoring 11.x (NU1202). Opt-in `versioning.dependencyRanges: major-bounded` writes `[x, nextMajor)`. npm needs nothing: caret peers cap the major. | Owner, 2026-11-05; NuGet guidance against upper bounds |
| D11 (Q8) | **Cross-repo first-party dependencies: diagnose and point the way.** When a backport raises a first-party platform-anchored dependency (e.g. `@mintplayer/ng-bootstrap ^22.34.0` → `^22.36.0`), the report names the upstream release the new minimum points to, lists that release's PRs (GitHub Release notes, versioning D14) and shows per PR whether the upstream repo has `backported: <matching line>`. Worded as "probably needs". Upstream not on this instance → name the dependency only. Chained automatic upstream backports (B) rejected for now: the app must not start feature backports in another repo on its own. **The backport only reports; it never changes a dependency version.** Updating a line's dependencies, including to an upstream's line release, is the package updater's job (M9, D13): "updating versions in downstream repos belongs to the package-updater feature. So the release-line deploys shouldn't bother about it". The diagnosis stays (owner chose to keep it). | Owner, 2026-11-05 |
| D12 (Q14) | **Backport PRs go through the merge queue, automatically.** The bot never pushes into a line: it opens `backport/<pr>-<line>` PRs (exists, `BackportRecipients.cs:116,177`) and **enqueues them** when the repo's merge queue is enabled (`backport.enqueue`, default true). `mergeQueue.branches` gets **patterns / `supported-lines`** (today exact names: `MergeQueueEngine.cs:398,677` `Contains(..., Ordinal)`); one serial queue per line (existing per-base design). A line's queue rebases/regenerates with the **line's own** workflow (D9). Approval floor (O11) and required checks still apply, so a backport that can't build (e.g. Dashboards on `angular-20.x` without `bs-tile-manager`) is evicted and never reaches the line. Not a separate issue: a milestone of this PR. Manual enqueue rejected ("merging cherry-pick branches manually is the exact opposite" of the goal); keeping lines out of the queue rejected ("a cherry-picked commit that breaks an old line won't be easily reverted"). | Owner, 2026-11-05 |
| D13 (Q9) | **Dependency updates on lines: one rolling PR per supported line (D4), default patch + security only.** Platform-anchored packages (D8 catalog) are always capped at the line's major. Setting `dependencies.lines: all \| patch \| security \| off` (default `patch`). The rolling PR goes through the line's merge queue (D12). Security advisories whose fix lies outside the allowed range are flagged in the report. | Owner, 2026-11-05; servicing practice (research note 1 §2: dotnet servicing, Angular LTS) |
| D14 (Q11) | **Spark opens both past lines now, and auto-cuts future ones.** The default branch keeps the latest major; when a merge moves master to the next platform major (preview), auto-cut (versioning D12, now in the D1 form) creates the old line at that merge's first parent. This is the standard model (Angular cuts `N.M.x` when `main` moves on; aspnetcore `main` is on .NET 11 while `release/10.0` serves 10). Past lines: **`release/dotnet-10.x` at `61c5c4276^`** (#442, 2026-09-24, net10 → net11; NuGet has `10.0.0-preview.1…86`, so the line continues at `.87`; needs a seed tag at the cut for MSBuild reachability) and **`release/angular-21.x` at `1b5d7450e^`** (#178, 2026-06-07, Angular 21 → 22; ng-spark was `0.x` before, so the line starts at `21.0.0`). Each line releases only its own ecosystem (D1). While `prerelease: preview` is pinned, the .NET 10 line keeps continuing `10.0.0-preview.N`; a per-line prerelease override is needed if a line should go stable on its own. **Revised (owner, 2026-11-06: "Current lines only"): the two past lines are not cut now.** Both cut points predate tag versioning (#537), so a line there runs no CI and cannot release without a bootstrap; each is cut, with its bootstrap, when a fix must ship on it (plan M11). | Owner, 2026-11-05; `npm view`/nuget flat-container 2026-11-05; `git log -S` |
| D15 (Q13) | **Packages release only from release lines, never from the default branch**, when the release-lines feature is enabled in coverage.yml. All version tags are created on lines. Consequences: (1) a line exists for master's **current** majors too (`release/dotnet-11.x`, `release/angular-22.x` today); (2) auto-cut changes meaning: the merge that moves master to a new major **creates the new line** (`release/dotnet-12.x`) and the previous one stops receiving master's features by default; (3) master never packs (no reachable tag) and runs no versioning; (4) production app deploys (CodeCoverage, SeoTester, MintPlayer) still deploy from master: this is about packages only. **Supersedes** D14's "only branch when the next preview comes out" and versioning D10/D12's default-branch releases. | Owner, 2026-11-05 |
| D16 (Q13b) | **The current line also receives changes by backport PR**, exactly like older lines; no alternative strategy, no setting. Rebasing the line onto master rejected (rewrites published history, orphans every tag: `TagWriter.cs:111-123` requires ancestry, MSBuild needs reachability; needs force-push to a protected branch). Fast-forward rejected by the owner in favour of one uniform mechanism. Cost accepted: every shipped PR runs CI twice (master + the line). Because a change only ships once it reaches a line, **the current line is always default-labelled**, even in `defaultLabels: fixes` mode (D5); `fixes` narrows only the older lines. | Owner, 2026-11-05 |
| D17 (Q10) | **Concurrent releases from several lines: deferred to spike S3/S9, with resilience criteria fixed now.** Must hold: (a) no version is ever tagged or published twice; (b) a release is never refused unless its own plan is really stale; (c) no routine manual re-runs; (d) nothing serial across lines that can overlap (#552 constraint). Provisional favourite: **scoped fingerprint** (the planned packages' tags on this line's major + in-repo dependencies' tags). Argument to prove in S9: with one line per ecosystem major (D1), two lines can never compute the same version, and same-line runs are already serialised by the per-ref concurrency group (`dotnet-build-master.yml:14-16`); the create-only tag push is the backstop. Older-major npm prereleases publish under `v<major>-next`, never `next` (G8). | Owner, 2026-11-05: "make sure it's resilient; decide after the spikes" |
| D18 | **Opt-in per repository.** Repositories without the release-lines feature keep today's behaviour (versioning releases from the default branch, D10 backports to `versioning.releaseBranches`). D15/D16 apply only where coverage.yml enables release lines. `schema-revision`, coverage baselines and app deploys stay default-branch-only in every mode. | C1; follows from D15 |
| D3 | **Onboarding becomes a feature chooser**: one onboarding flow lets the repo owner pick which features to set up fully: code coverage, merge queue, dependency updates, size labels, release lines/backports (and versioning, which lines need). Today each feature has its own onboarding (versioning: `VersioningOnboarding.cs`; dependency updates: onboarding page + PR). Part of this PR (one-PR rule). | Owner, 2026-11-05 |
| D19 (S3) | **Unanchored packages release from the newest line of their ecosystem**, configurable. Found by S3: D15 left no branch for packages without a platform anchor (Spark's `SourceGenerators`, `LibraryGenerators` on netstandard2.0, `Versioning.MSBuild`); they released only from master, and older lines skip them so two lines never tag one version. Default: a NuGet package belongs to `dotnet` (netstandard is .NET too), an npm package to its framework's ecosystem, a plain node library to the one framework all the repository's npm packages share. The newest line is the highest major any **anchored** package of that ecosystem has a tag at. Override per package: `versioning.packages.<id>.line: <ecosystem>`. A repository mixing netX, netstandard and Angular libraries needs no setting. | Owner, 2026-11-05: "configure through coverage.yml, sensible defaults; mixed .NET Core / .NET Standard / Angular repos must work out of the box" |

### 8.1 Spike evidence

**S3, ecosystem-scoped lines in the engine (2026-11-05): done.**
- `versioning.lines` (present, even empty, turns lines on; `pattern` defaults to `release/{ecosystem}-{major}.x`).
  `ReleaseLinePattern` (`CodeCoverage.Versioning/Releases/ReleaseLines.cs`) requires both placeholders exactly once
  and no `*`. `{ecosystem}` matches `[a-z][a-z0-9]*`, so it never swallows the separator before the major.
- The planner (`ReleasePlanner.OutOfLine`):
  - On a line, a package of another ecosystem, or anchored at another major, is **skipped with a note** naming the
    branch it releases from. Before, the whole run was refused.
  - A package that already has tags on the line and now computes another major is still **refused**. That is a
    backport that moved its platform, and it must not be hidden.
  - The default branch releases nothing (D15), with a note per package.
- Hosts: the CLI, the controller and the dispatch recipient share `VersioningConfig.IsVersionedBranch`. The git
  writer re-reads the line from the branch name (`GitWriterVersioningRunEndpoint.LineOf`) instead of trusting the app.
- An empty `lines:` is YAML null. Both readers turn it into `{}` (`VersioningYml`, the action's `versioningSection`).
- Evidence:
  - `ReleaseLinesTests`: 24 tests, a mixed repository with net10/net11, netstandard, Angular and plain node packages.
  - `VersioningSettingsTests`: the wire round-trip.
  - `VersioningYmlTests`.
  - `main.test.ts`.
- Also fixed: on a line, the upstream prerelease label read a dependency's last tag of **any** major. An 11.x
  preview would have made a 10.x line release a prerelease. It now reads the tag at the line's major
  (`ReleasePlanner.UpstreamLabel`). Red→green:
  `ReleaseLinesTests.A_dependency_in_preview_on_the_newer_line_does_not_make_the_older_line_a_prerelease`.
- Left for M4:
  - Auto-cut in the D15 sense: master's anchors create missing lines.
  - The manifest's registry lookup still uses the line's major for every package (`BuildManifest.cs:48`). This is
    harmless for packages skipped on that line.

**S9, concurrent releases from several lines (2026-11-05): decided, scoped fingerprint.**

Argument, against D17's criteria:
- **(a) No version is tagged twice.**
  - An anchored package releases only on the line of its own (ecosystem, major) (S3). Lines are unique per
    (ecosystem, major), so two lines never compute the same anchored version.
  - An unanchored package releases only on the newest line of its ecosystem (D19). The one window is between the
    cut of a new major's line and that line's first anchored tag, when both lines see themselves as newest.
  - The scoped fingerprint covers the planned package at the planned major. The second release of the same free
    version is therefore refused as stale, and the create-only tag push stays the backstop.
- **(b) Refused only when really stale.**
  - Tags of other majors and other ecosystems are outside the scope.
  - Same-line runs are serialised by the per-ref concurrency group (`dotnet-build-master.yml:14-16`).
- **(c) No routine re-runs.** A re-run is needed only in the cut window above.
- **(d) Nothing serial across lines.** No lock spans lines.

Implementation:
- `TagHistory.Fingerprint(excluded, scope)`: with a scope, it counts only the scope's packages at the scope's
  majors.
- `TagPlan.Scoped` is true when the plan ran on a line. The release recomputes the scope from the plan's own tags,
  locally (`ReleaseRun.ReleaseAsync`) and on the server (`GitWriterTagsRequest.FingerprintScoped` →
  `TagsJob` → `ReleaseRun.RequireFreshAsync`).
- Repositories without lines keep #552's whole-history fingerprint (D18).

Accepted trade-off: on a line, a tag hand-pushed for a package **outside** the plan, at the same major, between plan
and release, no longer refuses the release. The next run picks it up.

Evidence (`VersioningPlanReleaseTests`, real origins):
- `Another_line_releasing_in_between_leaves_a_line_plan_fresh`:
  - `Core/v11.0.0` and `@acme/util/v1.0.0` pushed between the plan and the release.
  - Red with the whole-history fingerprint: 2 failed, verified by switching the scope off. Green with it.
- `A_line_plan_made_stale_on_its_own_line_is_still_refused`: a `Core/v10.0.1` pushed in between refuses the
  release.

**S6, npm on an older major (2026-11-05): one fix, in this repository.**
- Where the tag is set:
  - `versioning-action/src/semver.ts` `distTag` decides the dist-tag.
  - `npm.ts:103` stamps it into the dist `package.json` as `publishConfig.tag`.
  - `MintPlayer/github-actions/publish-npm-packages` (`@jsdevtools/npm-publish`) reads `publishConfig.tag ?? "latest"`
    and passes `--tag`.
- Already right: an older major's stable release went to `v<major>-lts`.
- The fix: an older major's **prerelease** went to `next`, which would have moved `next` off the newest line.
  - It is now `v<major>-next` whenever any newer major exists, stable or prerelease.
  - Red→green: 2 failed, then 10 passed in `semver.test.ts`.
- Not yet proven live: the plain npm CLI ignores `publishConfig.tag`. A dry run reported "with tag latest", and only
  `--tag` changed it. So the action must pass `--tag` itself.
  - The bundled library's source says it does.
  - It is confirmed live by the first `v<major>-next` or `-lts` publish, the "done when" of #569.
  - If it doesn't, the fix belongs in MintPlayer/github-actions, by issue.

**S10, platform lifecycle sources (2026-11-05, live fetches).**

| Source | Major | Released | End of support | LTS | Preview |
|---|---|---|---|---|---|
| dotnet `release-metadata/releases-index.json` (`max-age=900`, ETag) | `channel-version` | — (only `latest-release-date`) | `eol-date`, absent while not announced (11.0 today) | `release-type` lts/sts | `support-phase` preview/go-live (11.0 is `go-live`, rc.3) |
| endoflife.date v1 `/api/v1/products/<p>` (`{schema_version, generated_at, result}`) | `releases[].name` | `releaseDate` | `eolFrom`, `isEol`, `isMaintained` | `isLts`, `ltsFrom` | none |

- endoflife.date covers angular, react, vue, nodejs, php, python, svelte, nextjs, nestjs and others. It has no
  `typescript`.
- endoflife.date has **no preview majors** (Angular 23 and Node 27 are absent). Preview majors come from the npm
  registry's dist-tags (`next`) for npm platforms, and from `support-phase` for .NET.
- Use v1's booleans, not the legacy endpoint, whose `eol`, `support` and `lts` mix booleans and dates.
- No rate-limit headers or statement. A daily poll is enough; the .NET index allows 15-minute caching.
- Reuse: the `PlatformCandidate` shape (`CodeCoverage.Git/Dependencies/DependencyContracts.cs:245-260`). It needs a
  released date and `maintained`. No C# fetcher exists yet, so the collector (M3) brings its own HTTP client.

**S1, conflict census (#570, 2026-11-05, local replay).** A simulated `release/dotnet-10.x` cut at `c6513fc62`
(`61c5c4276^`), and every first-parent master commit up to `e1908e0e5` (72) replayed onto it with
`git merge-tree --write-tree --merge-base=<c^1> <line> <c>`, the way `BackportService` applies a change. After each
applied commit the line is pinned back to its platform (TFM `net10.0`, `Microsoft.AspNetCore|Extensions|EntityFrameworkCore`
`10.0.11`, `dotnet-version`/`global.json` SDK `10.0.100`), as a maintained line would be. A commit that conflicts is put
on the line as master resolved it, pinned, so each commit is measured on its own (the first attempt let one conflict
cascade: 66 of 72 conflicted only because #461 was missing). Script: a throwaway Node prototype, not committed.

| Pass | Applied | Conflicted | Conflicted paths |
|---|---|---|---|
| Plain (today) | 61 (85 %) | 11 (15 %) | **all** platform-value files: `*.csproj` (9 commits), workflows and `.github/actions/setup/action.yml` (2), `global.json` (1) |
| Normalized (S2 prototype) | **72 (100 %)** | 0 | — |
| Normalized + line-side for generated `App_Data` and lockfiles | 72 | 0 | — (nothing left for the resolvers) |

- **No conflict in source, in generated `App_Data`, or in a lockfile** in 72 commits. Platform values cause every
  conflict, so S2 normalization is the main lever, and the resolver budget for .NET lines is small.
- **D8's safety rule, sharpened.** The prototype's first rule ("the PR changed a marker in this file → don't
  normalize the file") left 3 conflicts: #461 **added** framework packages at 11.x next to an existing one, `6e48f3326`
  moved the SDK from 11 rc.1 to rc.2, `92c298bec` moved `setup-dotnet` into a composite action. None touches the
  line's own platform. Rule now: compare **per occurrence** (a package by its `Include`, other values by position); an
  occurrence present on both sides whose value changed **and** that involves the line's platform (`net10.0` →
  `net10.0;net11.0`) is a platform change and conflicts; a change wholly above the line's major, and an added or
  removed occurrence, is normalized. With it, the only commit flagged is #442, the net10 → net11 move itself.
- **Not measured:** insertions at one place on both sides (keep-both and dedupe). The simulated line has no fixes of
  its own, which is the only way a line diverges beyond platform values, so the census cannot see them. The Angular
  line (`release/angular-21.x`) is not replayed; its markers are `package.json` ranges (S2).
- **Keyed-merge census (same day).** To measure same-key conflicts (two versions of one package), the last 150 merged
  PRs were fetched (`refs/pull/N/head`) and replayed. Against master at merge time all 150 are up to date (the queue or
  the author updated them first), so the conflicts really met are in the PR branches' own merges of master: 5 PRs
  have any, 16 merges, 12 clean, 4 conflicted, **all in docs or a script** (`dependency-updates-PRD.md` ×2,
  `coverage_merge_queue_PRD.md`, `tools/verify-coverage-paths.mjs`), none in a csproj, props or `package.json`.
  Rebased PRs leave no trace, so this is a floor. Spark's history gives no evidence either way: the queue rebases, and
  dependencies move in one rolling PR (#419).
- **Keyed-merge census of the other MintPlayer repositories (same day).** Read-only replays of `mintplayer-ng-bootstrap`,
  `MintPlayer.Dotnet.Tools`, `MintPlayer.AspNetCore.Tools` and `mintplayer-ng-video-player`. Two measures:
  - **Merges inside PR branches:** almost none (553 merged PRs; 1 PR with inner merges, its 2 conflicted regions in
    `AnalyzerReleases.Unshipped.md`). The author squash- or rebase-merges.
  - **Head against head** of consecutive PRs that touch a csproj, props, targets or `package.json`, at their merge base
    (an upper bound: it includes drift no author met): 290 pairs, 240 conflicted, **528 regions where both sides changed
    one key's version** (ng-bootstrap 99, Dotnet.Tools 159, AspNetCore.Tools 53, ng-video-player 217), against a handful
    of delete-vs-change (9) and other both-changed values (7).
  - **Which version:** the spot-checked regions are the **package's own version** (`"version"` 15.0.3 → 15.0.4 / 15.0.5 in
    ng-bootstrap; `<Version>` 1.1.2 → 1.1.3 / 1.2.0 in `SourceGenerators.csproj`, the most frequent file, 44 regions),
    not a dependency. The split was not measured further. These repositories have no Dependabot or Renovate PRs (one
    in ng-bootstrap's last 400), so bot dependency conflicts are unmeasured.
  - **Consequence:** keyed-merge refuses the own version. `<Version>` is a property (a scalar, no winner), and a
    package.json value takes the highest only inside `dependencies`, `devDependencies`, `peerDependencies` or
    `optionalDependencies` (test `The_package_own_version_both_sides_bumped_is_refused`). With versions computed from
    tags (#153, csproj `0.0.0-PLACEHOLDER`), an onboarded repository no longer edits its own version, so that conflict
    stops arising rather than needing a winner. Rows: the session scratchpad's `s1/others/pairs-*.tsv`, not kept.

### 8.2 Implementation decisions (#569, 2026-11-05)

- **Auto-cut criterion with lines on (M4, D15).** The default branch tags nothing, so the old D12 trigger (a placed
  release moved a major) never fires. `ReleaseBranchCuts.ForLinesAsync` cuts, at HEAD, every (ecosystem, major) its
  **anchored** packages need (`ReleasePlanner.LinesOf`) where **no package of that line has a tag at that major**.
  - Trade-off accepted: a line whose major has released is never recut, so a deleted line is recreated by hand.
    Without the rule every master push would retry the create of an existing line.
  - Free packages never cut a line (D19: they follow the newest one).
  - A line cut carries `ReleaseBranchCut.Ecosystem`, survives the "placed packages" filters (`Wire.Released`,
    `VersioningController.Released`) and gets its own PR comment, identical in C# and TS.
  - Evidence: `VersioningPlanReleaseTests.The_default_branch_with_lines_tags_nothing_and_cuts_the_line_of_its_major`
    and `A_line_that_has_released_is_not_cut_again` (real origins); `ReleaseBranchCutRecipientTests.A_release_line_cut_says_the_line_was_cut_not_left`
    and `releases.test.ts` "writes the same release-line comment as the server".
- **Per-line prerelease (M4, D14).** `versioning.lines.prerelease: { dotnet-10.x: upstream }`, keyed by the line as
  `ReleaseLine.ToString()` writes it (independent of the branch pattern). Order: package pin, then the line's label,
  then `versioning.prerelease`, then upstream (`ReleasePlanner.ReleaseLabel`). The values are the existing labels;
  `upstream` is how a line goes stable, so no new `stable` keyword. ~~no new `stable` keyword~~ **Superseded
  2026-11-14** for a package whose upstream never goes stable: `stable` is now a value at every level
  ([versioning-beta-upstream-PRD](code-coverage/versioning-beta-upstream-PRD.md) G2). `upstream` still makes a line
  stable when its upstreams are. The writer re-validates the keys and labels.
  Evidence: `ReleaseLinesTests.A_line_goes_stable_on_its_own_while_the_global_label_stays`,
  `A_package_pin_wins_over_its_lines_label`, `A_line_label_must_name_a_line_and_a_label`; the wire round-trip in
  `VersioningSettingsTests.Release_lines_survive_the_wire_from_the_app`.
- **Schema revision on a line (M11, D18).** `schema-revision` in `dotnet-build-master.yml` publishes only from the
  default branch. On a line it walks the `schemas/v*` tags downwards and reuses the newest one whose files equal the
  line's, and fails the run when none does: a line never publishes a schema revision.
- **Workflows on lines (M11).** `dotnet-build-master.yml` pushes and `pull-request.yml` run on `release/**`. The
  deploy workflows stay `master`-only by their own triggers, so a line push cannot deploy. The coverage upload also
  runs on lines. ~~Unverified how the server treats a non-default branch's whole-workspace upload.~~ **Checked in code
  (M11, 2026-11-06):** `CommitAssembler.Promote` (`CommitAssembler.cs:393-396`) moves the repository's headline
  coverage only for an upload on the default branch, so a line's upload is stored as that branch's commit and never
  replaces master's badge.
- **Master neither packs nor publishes with lines on (M11, D15 (3)).** The `release` job's versioning step still runs
  on the default branch, because it auto-cuts a new major's line. When that branch released nothing, a
  "Decide whether this run publishes" step skips the pack, the artifact and the `publish` job. A line always packs and
  publishes, so a push that failed earlier is retried by the next line run (`--skip-duplicate` skips the rest).
- **Lifecycle page placement (M3, D4).** The `PlatformRelease` query is under *Guides* (alias `platform-lifecycle`),
  readable anonymously and signed in (`security.json` `…aa`/`…ab`): it is public data, like the guides. Collector:
  `PlatformLifecycleCronJob`, daily 04:17 UTC; a failing source keeps yesterday's rows (`PlatformLifecycleTests`,
  over fixtures captured live on 2026-11-05).
- **Not in #569, moved to #570: per-line `keep`/`retire` and the retire-suggestion PRs (D4).** Nothing in #569 reads
  the supported set: it feeds default backport labels (D5) and the backport targets check (M8). Building the
  override and the PR before their consumer would ship unused config. The lifecycle data they need is collected now.
- **Rulesets come from onboarding, in #571 (owner, 2026-11-05).** "the onboarding process should be able to create
  the correct rulesets". #554's setup action is built into the onboarding chooser (M10) and extended to release lines:
  - the default branch gets #554's two merge-queue rulesets; today they exist only by hand (Spark 26211184,
    ng-bootstrap 26211467, "merge-queue: restrict updates", `~DEFAULT_BRANCH`, update + deletion + non-fast-forward,
    the CodeCoverage App `exempt`; created through the API on 2026-10-30, read back 2026-11-05);
  - the lines get the same restriction on the pattern translated to a ref glob (`refs/heads/release/*-*.x`), because
    backport PRs reach the lines through the queue (D12).
  - **Token: the signed-in user's**, as a repository admin; the App does not get Administration: write (owner:
    "User's token"). When the stored token is missing or expired, the page opens a GitHub sign-in popup, as the
    GitHub login does today, and uses the token from that OAuth response.
  - Until #571 ships, #570 adds Spark's line ruleset by hand when it cuts the lines.
- **Coverage in PR #572 (owner: "You can also add coverage for files outside this pull-request").** Tests were
  added for the classes the coverage MCP ranked worst, excluding the onboarding and dependency classes, which #571
  rewrites. The new test classes were: CommitGraphMirrors, the git writer's client and answers at each merge-queue
  job, the merge queue's webhook forward, the backport recipients, `VersioningController`'s statuses,
  `SnapshotCache`, and the GitHub normaliser's backports, commands and producer events
  (`GitHubEventsCommandTests`). Two production changes were found along the way:
  - an organization rename whose account id was not a number threw (`JsonElement.TryGetInt64` throws on a
    non-number); it is now skipped like a missing id (red then green: the `"id":"x"` row of
    `GitHubEventsCommandTests.An_organization_rename_needs_the_accounts_id_and_login`);
  - the dead `GitHubEventsRecipient.GetOrCreateCommit` was removed.
  - Still uncovered, for later: `MergeQueueEngine` (125 lines at `19344c85`), `GitHubForgeMergeQueue` (51).

### 8.3 Implementation decisions (#570, 2026-11-05)

- **Keep-both is only for sets, and an MSBuild keep-both is checked by key (owner: "y", 2026-11-05).** Raised by the
  owner: when both sides inserted lines at one place, keeping both is not always right. Four cases:
  - both added the same text: git merges it cleanly, so no resolver sees it;
  - independent entries of a set (two packages, two ignore lines, two JSON members): keep both;
  - one entry with two values (`QRCoder 1.8.0` and `1.9.0`, one JSON key twice): a real conflict;
  - ordered content (middleware, statements, YAML steps): never resolved automatically.

  The keep-both resolver already refuses any region whose merge-base section is not empty
  (`KeepBothResolver.cs:75`), and JSON's default rule refuses duplicate members. The gap: the `xmlProjects` defaults
  (`*.csproj`, `*.props`, `*.targets`, `Directory.Packages.props`) validated only well-formedness, so the keep-both of
  one package added twice with another version passed and gave a broken project (NU1504). Fixed for the merge queue
  and the backports alike: new validator `msbuild-no-duplicate-keys` (`ContentValidators.cs`), now the `xmlProjects`
  validator. An item's key is its type, its `Include`/`Update`/`Remove` value and its `Condition`; a property's is
  its name and its `Condition`; case-insensitive, scoped to one group element. Red then green: well-formed `xml`
  accepts every row of `ContentValidatorsTests.A_key_one_MSBuild_group_holds_twice_is_refused_where_it_repeats`, the
  new validator refuses each at the repeated line. The inputs are validated first, so a project that already repeats a
  key would refuse every keep-both: a scan of this repository's 156 MSBuild files found no repeated key.
- **`keyed-merge`: a three-way merge per key (owner: "Build it now. Support the scenarios we can", 2026-11-05).** The
  owner's refinement of the case list:
  - a different key on each side → both, de-duplicated;
  - the same key and value → git merges it, out of scope;
  - the same key with different values → common for package versions.

  The owner also noted that MSBuild has scalar fields (`<Version>`) and collection fields (`<PackageReference>`). Built
  as strategy `keyed-merge` (`KeyedMergeResolver.cs`), opt-in, tests first (`KeyedMergeResolverTests`, 17 tests, one a 4-row theory):
  - **Items are collections.** The key is the item's type, its `Include`/`Update`/`Remove` value and its `Condition`,
    as the validator above has it. Each metadata name merges three-way, so a `Version` from one side and a
    `PrivateAssets` from the other both apply.
  - **Properties are scalars.** When both sides change one, the merge refuses, `<Version>` included: one value has no
    winner.
  - **A package version both sides changed takes the highest.** That is a `PackageReference`/`PackageVersion`
    `Version`, or a range inside a `package.json` dependency object when both sides use the same operator.
  - **Everything else refuses:**
    - a value both sides changed that is not a package version;
    - delete against change;
    - an unorderable version (`1.*`, a range, `$(X)`);
    - a line that is not an entry.

  Two cases the tests caught:
  - A child `<Version>` line conflicts alone. The region is now widened to its whole item (`WidenToItems`).
  - A `<Message Text=… />` inside a target read as an empty property. Now a self-closing tag without an item spec
    counts as an entry only when it has no attributes besides `Condition`.

  **K1 (owner: "small wins toward the end goal", 2026-11-05):** the highest only when both sides raised the version.
  Before, a base-branch pin from 1.7.0 down to 1.6.0 against a PR bump to 1.8.0 silently took 1.8.0. Now it refuses
  (`A_version_lowered_on_either_side_is_refused_not_overridden_by_the_highest`, 3 rows, plus a package.json case).
  **K2 (owner: "per-sdk a class/service … Let's do that first", 2026-11-05):** each format is one `[Register]`ed
  `IManifestFormat` service (`MergeQueue/Resolution/Manifests/`), injected into the resolver as a list. It parses its
  files into one structure (`ManifestEntry`: key, kind, scalar or collection, values by name, the entry's own text),
  widens a region to whole entries (`Enclose`), orders versions (`CompareVersions`, null = no order), writes values back
  in place (`Write`) and joins entries (`Join`, a JSON object's commas). The three-way merge stays the resolver's and
  is the same for every format. Built test first: `ManifestFormatTests` holds each format's contract (one format per
  file name, lockfiles none; NuGet order; npm order only inside dependency objects and of one operator), the
  registered set equal to `ManifestFormats.BuiltIn`, and the end-to-end `KeyedMergeResolverTests` unchanged across
  the refactor (94 green with the registry tests). `composer.json` is the second JSON one, tested on laravel/laravel's own layout
  (4-space indent, two-part constraints `^13.17`, `php` first). `php`/`ext-*`/`lib-*` refuse: they are the platform.
  **Lockfiles coexist (owner, 2026-11-05):** they stay take-base + regenerate. Keyed-merge picks the manifest's
  versions, and the regenerate run rebuilds the lockfile from them. A lockfile never matches a keyed-merge format.
  The census above shaped one rule: a package's own `"version"` is not a dependency, so it refuses. **Defaults
  unchanged:** `xmlProjects` stays `keep-both`. `keyed-merge` refuses a region that is not entries, such as an inserted
  `<Target>`, which `keep-both` resolves. Whether the defaults move is the owner's call.
- **S2, marker normalization (2026-11-05, test first).** `PlatformMarkers.Normalize(path, ours, base, theirs)`
  (`CodeCoverage.Git/Backports/`) rewrites the base's and the change's platform values to the line's, **per
  occurrence**. An occurrence is keyed by marker name plus its ordinal; a package's name is its id. This implements S1's
  sharpened D8 rule:
  - Unchanged, or moved wholly above the line's highest major: takes the line's value.
  - Changed, and the line's major (or a value with no major) is involved: left to git and returned as a
    `PlatformChange`.
  - Added: takes the line's value of the same marker or of a family sibling (another framework package, another
    `@angular/*`). With no sibling, it is reported.
  - Removed: the base takes the line's value, so the removal applies.

  The markers:
  - `TargetFramework(s)`;
  - `PackageReference`/`PackageVersion` of `Microsoft.AspNetCore|Extensions|EntityFrameworkCore`;
  - the `global.json` SDK;
  - `dotnet-version`/`node-version` in `.github/**/*.yml` (a matrix list is skipped);
  - the `package.json` `@angular/*`, `react`, `react-dom` and `vue` ranges.

  Only the value's characters are replaced (a CRLF test pins it). A lockfile is never a marker file.

  **Proven by `PlatformMarkersTests`, with the real `git merge-file`:**
  - Scenario B conflicts without normalization and merges clean with it, keeping `disable` and `net10.0`.
  - Scenario C (`global.json` 11.0.100 → 11.0.200) conflicts without it and is empty with it.
  - `net11.0` → `net10.0;net11.0` still conflicts and is reported.
  - A framework package added at 11.0.0 takes its sibling's 10.0.11.
  - An Angular `^22` bump vanishes while a dependency added beside it applies.
  - A workflow step added below `dotnet-version` merges.

  **Signature deviation from the plan** (`Normalize(path, blob, targetMarkers)`): the safety rule needs base and
  theirs together, and the line's blob gives the target values.

  **Not yet:**
  - Markers in `action.yml` outside `.github/`, `dotnet-tools.json`, Dockerfiles, `engines`, and coverage.yml
    `backport.markers`. These come with M5, where D8's catalog is wired in.
  - What a project new on the line gets. Today it is reported, not guessed.
- **M5, first part (2026-11-05): normalization in the writer, and independent lines.**

  How `BackportService` applies the change now:
  - It lists the paths the change touches (`diff --name-only --no-renames <base> <commit>`) and keeps those
    `PlatformMarkers.Handles`.
  - Per target, it normalizes their blobs against the line tip's copy and writes the rewritten blobs into throwaway
    commits, built in a throwaway index and never pushed.
  - It runs `merge-tree --merge-base=<base'> <tip> <theirs'>` on those commits.

  The safeguards:
  - A blob that is not UTF-8 is left to the plain merge: the writer decodes git's output as UTF-8, so the blob size
    is compared with the decoded byte count.
  - A byte order mark is put back, because the reader drops it.
  - The file mode is kept.

  Each target now has its own `push` result, adding `conflicted`, and carries its `platformChanges`. The job's
  outcome is `backported` whenever it ran. The writer never returns `conflicted` for the whole job any more, and the
  app no longer reads it.

  In the app's comment:
  - every line gets its own bullet;
  - a conflicting line gets its own section with its files, the platform values the change edits there, and the
    manual commands;
  - a created pull request's body lists those platform values.

  **Tests:**
  - Writer `BackportTests`:
    - `A_conflicting_line_does_not_hold_back_a_clean_one`, which replaces `One_conflicting_line_pushes_nothing_anywhere`;
    - scenario B lands on the line with `net10.0` and `disable`;
    - `net10.0;net11.0` conflicts and reports;
    - a move to `net12.0` is `empty`.
  - App: `A_conflicting_line_still_lets_the_clean_line_open_its_pull_request`, and the platform note.

  **Still M5:** `onConflict: draft` (D7) and `backport.markers`. Both are done in the third part, below.
- **M5, second part (2026-11-05): the merge queue's resolver on lines (G1, §5.3 step 2).**

  `BackportService` now merges through `MergeQueueResolver.MergeAsync`. It passes the merge base explicitly (new
  `mergeBase` parameter, a cherry-pick), with stage 2 as the line.

  **The rules come from the default branch's `coverage.yml` `mergeQueue.autoResolve`, never the line's own copy.**
  Merge-queue O16 (owner, grill 9) holds for lines too: a widened allow-list pushed to a branch must not govern merges
  into that branch. §5.3's "the line's `autoResolve` rules" is read that way, as the rules that apply to the line. A
  section with errors gives no rules. The app sends the rules per target (`BackportTargetWire.AutoResolve`), so a future
  per-line override needs no wire change.

  A conflict outside the rules makes the line `conflicted`. The resolver's other refusals (for example, an unsupported
  conflict type) are reported with their detail. A resolved line lists its `resolutions`, and the PR body names each
  path and strategy. A `regenerate` resolution says "needs regenerating" until M6 dispatches the line's workflow.

  **Tests:**
  - Writer: `A_conflict_the_line_own_rules_allow_is_resolved_with_the_line_side`. A `modelHashes.json` conflict is
    `conflicted` without rules, and resolved to the line's side with a take-base `anyLine` rule, while the PR's
    `fix.txt` applies.
  - Writer: a malformed rule is refused (400).
  - App: the rules are read from the default branch, a broken section gives none, and resolutions are listed.

  `BackportService` takes its dependencies through `[Inject]` fields (owner preference, 2026-11-05).
- **M5, third part (2026-11-05): the `backport:` section, with `onConflict` (D7) and `markers` (D8).**

  `BackportYml` parses the top-level `backport:` key of the **default branch's** `coverage.yml`, for the same O16 reason
  as the rules. Only the two M5 keys are read; `lines`, `defaultLabels` and `regenerate` come with M6 and M8. Absent
  means the defaults. Errors never throw: the defaults apply, and the backport comment lists the errors.

  **`onConflict: comment | draft`.** `comment` is the default (D7). With `draft`, each target carries `draft: true`. A
  target that conflicts outside the rules then runs a plain `merge-tree --write-tree --merge-base=<normalized base>` of
  the normalized sides. Its tree, with git's conflict markers in it, is committed onto the line with the backport's
  message and pushed. The result is `drafted`, with the conflicting paths. The app opens it with `draft: true`
  (`IForgeVersioning.OpenPullRequestAsync` gained a `draft` parameter), and `NoConflictMarkers` keeps it red.
  - The draft tree applies **no `autoResolve` rule**. It is git's merge as it is, so a file the rules would have
    resolved may hold markers too. Accepted for v1: the human resolves the draft anyway, and mixing the resolver's
    partial output with git's would make the tree's provenance unclear.
  - A resolver refusal other than a conflict outside the rules (for example, an unsupported conflict type) stays
    `conflicted`, never a draft: it has no marker tree to show.
  - The per-request `/backport <line> --draft` is command parsing, so it waits for M8 (built in M8d, below).

  **`markers: { disable: [...], packages: [...] }`.**
  - `disable` names built-in kinds: `tfm`, `framework-packages`, `global.json`, `setup-action`, `npm-frameworks`.
  - `packages` are package names or `*` patterns (letters, digits, `@ / . _ -`; a lone `*` is refused). They are matched
    case-insensitively in MSBuild `PackageReference`/`PackageVersion` and in `package.json` alike.
  - Each pattern is its own family, so an added package takes the value of a sibling matching the same pattern.
  - A package a built-in kind already reads is one occurrence, never two: the first kind wins by value position.
  - The writer re-validates both lists (`markers` rule, at most 50 packages).

  **Tests:**
  - `PlatformMarkersTests`: a disabled kind; `@mintplayer/ng-bootstrap` and `MintPlayer.Spark*` normalized; the kind
    names.
  - `BackportYmlTests`: defaults, a read section, five broken sections.
  - Writer: `A_draft_line_gets_its_conflict_pushed_with_the_markers` (the clean line is still `created`);
    `A_configured_package_is_normalized_like_a_framework` (`conflicted` without the option, `created` with it); and
    malformed `disable`/`packages` refused.
  - App: the section sends `draft` and the markers and opens a draft; a broken section is reported and the defaults
    apply; GitHub's body carries `"draft":true` only for a draft.
- **M6 decisions (owner, 2026-11-05, asked before building it).**
  - **Provenance: the line tip's copy of the workflow.** A backport's regenerate workflow must be byte-identical at
    the backport commit to the copy at the release line's tip. This **narrows dependency-updates D12 for backports**:
    D12 requires the default branch's copy (`WorkflowHook.cs:46`). A line's `regenerate.yml` drifts from master as soon
    as master bumps its `setup-dotnet`/`node-version`, so D12 as written would refuse every line after its first
    platform move, which defeats D9's "the line's own SDK and Node".
    - D12's threat still holds: a workflow the cherry-picked change edited never runs.
    - Accepted risk: someone who can push to the line can change what runs. They can push to the line anyway, and
      the patch stays limited to the regenerated paths.
    - Rejected: the default branch's copy (refuses every line after its first bump); the default branch's copy with
      markers normalized (brittle: any other edit on master breaks every line).
  - **Trigger: every regenerated path, not only conflicts.** D9 says lockfiles are never merged. Every path the change
    touches that matches `mergeQueue.regeneratedPaths` therefore takes the line's side, and then the line's workflow
    regenerates it, as does every `regenerate` resolution.
    - Accepted cost: a backport that touches a lockfile waits for a workflow run, minutes, before its PR opens.
    - Rejected: regenerating only resolved conflicts, which lets a cleanly text-merged lockfile reach the line
      unverified.
    - **Superseded in part (owner, 2026-11-07): only derived files take the line's side.** "Every path that matches
      `regeneratedPaths`" was too wide. That list names what a regenerate patch may touch, and Spark's holds files with
      hand-written content: `Model/*.json`, `translations.json`, `security.json`. #580's backports (#581, #582) reset
      HR's `Person.json` and Spark's `translations.json` to the line's version and lost the change's edits (two
      add-new flags, two translation keys), which no regeneration can bring back. They were restored by hand.
      - Now: the paths of the whole-file `take-base` rules that regenerate (lockfiles, `modelHashes.json`,
        `securityPosture.txt`) take the line's side (`BackportRecipient.LineSidePaths`). Every other file merges as
        text under its own rule. The patch's allowed globs are still the whole `regeneratedPaths` list.
      - Accepted cost: a hand-written file in `regeneratedPaths` that merges cleanly is not regenerated on the line.
      - The same backports showed `regenerate.yml` had never restored: `nuget.config` maps `MintPlayer.*` to the
        GitHub Packages feed, which needs credentials, and the workflow had none. **Superseded the same day:** a
        workflow-wide `packages: read` + `GITHUB_TOKEN` failed the merge queue's own fork guard (PR #583 CI,
        `MergeQueueForkGuardTests.This_repositorys_own_workflows…`: "`packages: read` grants more than `contents:
        read`"), so a fork's pull request could never have been regenerated. Owner's choice (2026-11-07): split by
        fork. The workflow declares a boolean `fork` input and has two jobs. `fork` (`if: inputs.fork`) has no
        credential and restores from nuget.org only (`-p:RestoreConfigFile`, a config in `$RUNNER_TEMP`). `trusted`
        (`if: !inputs.fork`) gets `packages: read` and the token. The queue sends `fork: true` for a fork's entry
        when the workflow declares the input (`MergeQueueEntry.ForkInputWorkflows`, read with the fork precondition).
        The guard skips exactly such a gated job (merge-queue.md §2.3, item 2). Backports are never forks.
- **M6, built (2026-11-05): regeneration on backport branches (D9).**

  **Writer.**
  - Each target carries the derived paths (`BackportTargetWire.RegeneratedPaths`): since 2026-11-07 those of the
    whole-file `take-base` rules that regenerate, no longer the whole `mergeQueue.regeneratedPaths` (see the superseded
    decision above).
  - Before the merge, every changed path that matches one is reset in "theirs" to the base's entry (removed when the
    base lacks it). The merge therefore keeps the line's side and cannot conflict there.
  - These paths are kept out of the platform-marker paths.
  - The target's result lists `regenerate`: those paths, plus every `regenerate` resolution.
  - The new job `POST /jobs/versioning/backport/regenerated` folds a regenerate patch into the backport commit, so it
    stays one commit with the same parent, message and author. It works in four steps:
    1. It validates the patch like the merge queue's publish (`PatchValidator`, T4: regenerated paths only, regular
       files).
    2. It checks that the head is still `expectedSha` (`branch-moved` otherwise).
    3. It applies the patch through `WriterService.ApplyPatchAsync` and checks the tree.
    4. It pushes with a lease on the tip.

    Its outcomes are `regenerated`, `patch-rejected`, `branch-moved` and `push-rejected`. Only `backport/*` heads are
    accepted.

  **App.**
  - `BackportRecipient` sends the paths, except under `postRebaseWhen: never`. With `never` they merge as plain text,
    as the queue would.
  - A `created` target with a `regenerate` list does not open its pull request. It goes to
    `BackportRegenerationEngine`, a saga over `IWorkflowHook` that is serial on the versioning lane and gated by
    generation. Its document is `BackportRegenerations/<repo>`, which the webhook filter point-loads.
  - `BackportRegenerationEngine.Plan` picks the workflows as the merge queue does (O36): a `regenerate` resolution's
    rule `action`, else `postRebaseActionName`. They run one after the other, each on the commit the previous fold
    left.
  - The allowed globs are the list plus each concrete path outside it (O37). This is the same rule as
    `PublishRegeneratedPaths`.
  - Before the first dispatch, `VerifyProvenanceAsync` runs against **the line branch** (the owner decision above).
  - The dispatch inputs are `expected_sha`, `pr_number` and `queue_run_id`. The run is observed, `merge-queue-patch`
    is downloaded, and the writer folds it in with its own token (T9).
  - The pull request then opens with "Regenerated on this line by …", and the backported pull request gets a comment.
  - **Every stop opens the pull request anyway** with "**Not regenerated**: <why>" and the stale files (D9's "PR opens
    with a note"). The stops are: a missing or edited workflow, no workflow configured, `never`, a refused or
    uncorrelated dispatch, a failed or wrong-commit run, an unusable artifact, a refused patch, no token, the timeout
    (`postRebaseTimeoutMinutes`, with the run abandoned, D53), and more than 6 transient failures in a row.
  - A `drafted` target lists its stale files the same way, since nothing regenerates a draft.
  - `GitHubEventsRecipient` lets `workflow_run` completions through while a backport is pending, and
    `BackportRegenerationEventsRecipient` forwards the run's completion onto the lane.
  - Opening the PR is idempotent (`OpenPullRequestAsync` finds an open one on a 422). A step retried after the open
    can repeat the comment. This is accepted.

  **Tests:**
  - Writer `BackportTests`: a regenerated path keeps the line's side and is listed; the patch is folded; it is refused
    outside its paths and on a moved branch; an empty patch leaves the commit as it is.
  - App `BackportRegenerationEngineTests`:
    - fold, then open;
    - two workflows in sequence on the folded commit;
    - provenance that differs from the line tip;
    - a refused patch;
    - a timeout that abandons the run, and a stale generation that is dropped;
    - the plan.
  - App `BackportRecipientTests`: the paths are sent and the target waits; a line that cannot regenerate opens with a
    note; nothing is sent under `never`.
  - The webhook filter, the client route and the registrations.
- **M7, built (2026-11-05): the merge queue for lines (D12).**

  **`mergeQueue.branches`** takes three kinds of entry (`MergeQueueBranches`):
  - a branch name, as before;
  - a pattern, such as `hotfix/*`. Its `*` stands for part of one path segment and never spans a `/`;
  - `supported-lines`: every branch that the same file's `versioning.lines` pattern matches.

  The parser refuses the following, each with a readable error:
  - a pattern without a fixed first segment (a bare `*` would queue every top-level branch);
  - `**` and `?`;
  - `supported-lines` without a valid `versioning.lines` section.

  Exact entries keep their writer-rule check (D57). The section and the run's snapshot carry `LinePattern`. Every
  branch check goes through `MergeQueueSection.Queues`: the enqueue refusal, the check-button offer and the comment
  link. A refusal that names `supported-lines` also prints its pattern.

  - **"Supported" is the pattern until M8.** D4 narrows it with lifecycle data and the per-line `keep`/`retire`,
    which M8 builds. Until then every branch that matches the line pattern counts, retired ones included. A retired
    line takes no backports anyway: nobody labels one.
  - **The queue panel's candidates.** With a pattern or the keyword, `mergeQueue.branches` names no branch to ask
    the forge for. The panel therefore lists every open pull request once (`ListOpenPullRequestsAsync(repo, null)`)
    and filters it through `Queues`. Exact names still list per branch.

  **A line's queue runs the line's own workflow** (D9, extending the owner's M6 decision):
  - When the base matches `LinePattern`, the queue checks D12 provenance and the fork precondition against the line
    tip, not the default branch.
  - The refusal reads "`path` on the queue commit is not `release/…`'s".
  - Every other queue branch is unchanged: it runs the default branch's copy (O16 and D12 as built).
  - The workflow **names** still come from the default branch's `coverage.yml` (O16), so each line needs a file of
    the same name. M10's onboarding adds it.

  **Backport pull requests are enqueued automatically**, under the new `backport.enqueue` setting (default `true`):
  - `BackportRecipient.Enqueues` holds when all three are true: `enqueue` is set, the default branch's queue is
    enabled, and its `branches` queue the line.
  - When it holds, the opened pull request gets a `MergeQueueSignalMessage` Enqueue with source
    `EMergeQueueCommandSource.Backports` and actor `mintplayer-backports`.
  - The report line then ends ", handed to the merge queue".
  - A line that regenerates first carries the decision in `BackportRegeneration.Enqueue`. The saga enqueues once the
    pull request opens, and only when it was regenerated.

  Three things are never enqueued:
  - **A draft**, which the queue refuses.
  - **A pull request with "Not regenerated" files.** It would merge stale lockfiles that only a person can fix; that
    person queues it afterwards.
  - **A line the queue does not name.** It gets no signal, so the original pull request receives no refusal comment.

  Like `DependencyUpdates`, the `Backports` source counts as the write role, read from no forge, and never reaches a
  bypass role. The approval floor, rules and required checks apply as for a person. A backport the App opened is
  therefore still held by an `all` approval floor until someone approves it.

  **The approval floor and the App's own backports (owner, 2026-11-07).**
  - **Problem.** The paragraph above meant only an `all` floor to hold a backport. But the floor reads the pull request's
    *author*, and the App (`coverageproduction[bot]`) is never a collaborator. So under `ExternalAuthors` too, every
    backport was refused with "does not have write access". Measured: #581, #582, #584 and #585 on Spark were all refused
    "approval" when queued with the check's button.
  - **Record.** Every backport pull request the App opens is stored as a `BackportPullRequest`
    (`BackportPullRequests/{provider}/{repositoryId}/{number}`): the report's created and drafted pull requests, the
    regeneration saga's, and the one `BackportLinkRecipient` opens for a writer's hand-pushed `backport/<N>-<line>`. The
    merge deletes the record. A close without a merge keeps it, because the pull request may reopen.
  - **Exempt by default** (owner: "exempt our own bot id from the requirement"). Under `ExternalAuthors`, a recorded
    backport whose head is not in a fork counts as a writer's pull request. A login or branch name alone would trust any
    App, so the record is the proof. This is safe without tracking the head commit: the cherry-picked change passed the
    floor on its own pull request, and only a writer or an App with write access can push to a branch in the base
    repository. A writer's request for changes still vetoes, as for every pull request.
  - **`all` floor: a writer's approval queues it** (owner: "react to approvals … and automatically add that PR to the
    merge queue"). A review of a recorded backport that is not in the queue sends the review signal. The App then
    queues it as the `Backports` source when some writer's latest review approves it and no writer's latest review
    requests changes. Approvers are writers, the floor's own rule (D47; owner chose write access over admins). It applies
    only where `backport.enqueue` and `mergeQueue.branches` let the App queue a fresh backport, so an approval elsewhere
    stays silent. The floor itself is checked again at enqueue, on the pinned head.
  - Backports opened before this change have no record. They still need an approval and a person to queue them.
  - Tests: `MergeQueueEngineTests` (the exemption; an unrecorded bot pull request; `all` still holds; an approval
    queues it; a non-writer's approval and a writer's request for changes do not; an unrecorded pull request is not
    queued), `BackportRecipientTests` and `BackportLinkRecipientTests` (recorded; deleted on merge).

  **Tests:**
  - `MergeQueueYmlTests`: patterns and `supported-lines`; the keyword without `lines`; a glob-named line is still a
    line; unbounded patterns.
  - `MergeQueueEngineTests`: a line pull request is queued and dispatched with the line's copy; the default branch's
    copy on a line is refused; non-line bases are refused; the `Backports` source is a writer.
  - `MergeQueueControllerTests`: the pattern listing.
  - `BackportRecipientTests`: enqueued; off, disabled, not a queue branch, and draft; stale files; the decision
    carried to the saga.
  - `BackportRegenerationEngineTests`: enqueued after regeneration; never enqueued unregenerated.
- **M8a, built (2026-11-05): supported lines (D4, D16).** `SupportedLines` decides which lines take backports. M7's
  "every match of the pattern" now holds only for the button, the comment link and the panel (see below).

  **Which lines exist** is read from the forge: the new `IForgeMergeQueue.ListBranchesAsync(repo, prefix)` lists the
  branches under the pattern's fixed start (`release/` for the default pattern, every branch for a pattern that starts
  with a placeholder). On GitHub it pages `git/matching-refs/heads/{prefix}`. The prefix's segments before the last
  are validated, so `release/../` throws instead of reaching the URL. Branches the pattern does not match (such as
  `release/notes`) are not lines.

  **Which lines are supported**, per line, in this order:
  1. `backport.lines.<line>: retire` drops it, even the current line (the owner's explicit word beats D16).
  2. `backport.lines.<line>: keep` keeps it, even past end of life.
  3. The current line (the highest major of its ecosystem among the existing lines) is always supported (D16).
  4. Otherwise the platform's `PlatformRelease` phase decides: anything but `eol` is supported.
  5. An ecosystem with no lifecycle data at all has no support policy and falls back to the explicit list (D4): only
     its `keep` lines and its current line. A major missing from data the ecosystem does have is treated the same,
     because nothing says it is maintained.

  Every result carries a reason ("dotnet 9 reached end of life", "`keep` in coverage.yml", "the current dotnet line"),
  which M8b's check run and the queue's refusal print. The lifecycle documents are read by id prefix
  (`LoadStartingWithAsync("PlatformReleases/")`, a few dozen documents), so no index is involved.

  **`backport.lines`** (§5.8) holds the overrides. Each key must be a line name (`dotnet-10.x`) and each value `keep`
  or `retire`; anything else is a parse error naming the entry. It does not list the lines: those come from
  `versioning.lines` and the branches that exist, which #569 settled. `versioning.releaseBranches` is not consulted.

  **The queue refuses an unsupported line** at enqueue, with the reason: "`release/dotnet-10.x` is not a supported line
  (…), so `supported-lines` does not queue it." It applies only when the base matches solely through the
  `supported-lines` keyword (`MergeQueueSection.QueuesOnlyAsSupportedLine`); a line named exactly or by a pattern
  stays queued, since the owner named it. The check-button offer, the comment link and the panel stay pattern-based:
  they run on every pull request event, and listing branches plus lifecycle data there would cost a forge call each
  time for a refusal the enqueue gives anyway.

  **Tests:**
  - `SupportedLinesTests`: supported while the platform is, the current line always; `keep`/`retire`; the reasons;
    the order; the branch prefix.
  - `BackportYmlTests`: `lines` parsed; a non-line key and an unknown value refused.
  - `GitHubForgeMergeQueueTests`: the matching-refs listing; a traversal prefix throws. The conformance row.
  - `MergeQueueEngineTests`: an older line without lifecycle data is refused under `supported-lines`, and queued once
    `keep` names it.
- **M8b, built (2026-11-05): default labels and the "Backport targets" check (D5, D16, D18).**

  **Which ecosystems a pull request changes** is the versioning engine's own changed-inputs test (§5.2):
  `ChangedEcosystems.OfAsync` runs `ReleasePlanner.ChangedInputsAsync` (now shared, with the same ignore globs and
  test-project exclusion) for each package, and collects `Ecosystems.Of` of those with changes. It runs in the git
  writer as `POST /jobs/versioning/ecosystems`: the job fetches the base branch and `refs/pull/N/head`, diffs from their
  **merge base**, and reads the graph at the head. The merge base matters: the default branch's own changes since the
  pull request branched off are not the pull request's (the writer test moves `main`'s Angular library after the
  branch-off and expects only `dotnet`). A head that moved on answers `head-mismatch`; the newer push has its own
  message. Cost accepted: one full fetch per push to a pull request into the default branch, in repositories with lines
  on only, the same fetch the plan job already makes.

  **Who gets processed** (`BackportTargetsEventsRecipient`, a filter on the forge-event lane): pull requests into the
  default branch, not from a `backport/` head and not by a bot (a dependency update on the default branch is no
  backport: each line gets its own, D13). The lane (`BackportTargetsRecipient`) then reads the default branch's
  `coverage.yml` and stops unless `versioning.lines` is on (D18).

  **Labels** (`BackportTargets.Labels`): `backport: <line>` for every supported line (M8a) whose ecosystem changed.
  - `defaultLabels: all` (default) labels them all; `fixes` labels older lines only when the title is a conventional
    fix (`fix:`, `fix(scope):`, `fix!:`) or the pull request carries one of `backport.fixLabels` (default `[bug]`), and
    the current line always (D16); `off` labels nothing.
  - A misspelt `defaultLabels` is an error and labels **nothing** (unlike `onConflict`, where the default is safe):
    `all` would label everything because of a typo.
  - Labels are added only on open or reopen, and only when the pull request carries no backport label yet, so a
    maintainer's choice survives a reopen. They are never removed: after the open they are the maintainer's.
  - The label names a line (`dotnet-10.x`), so the backport recipient now resolves line names through the
    `versioning.lines` pattern when lines are on (`BackportRecipient.ResolveLine`); before M8b it only knew
    `versioning.releaseBranches`, so `backport: dotnet-10.x` named no branch. A bare major (`10.x`) names no ecosystem
    and resolves to nothing there. The backport head is `backport/<pr>-<line>`.

  **The check** ("Backport targets", always neutral, on every head): a table of every existing line with supported
  (and why not), changed and labelled, a title naming the lines that apply, and the `backport:` section's errors.

  **Tests:**
  - `ChangedEcosystemsTests` (engine): touched ecosystems; docs, tests and ignored files touch none; a line pin.
  - `VersioningEcosystemsTests` (writer): the merge base, not the default branch's tip; a moved head.
  - `BackportTargetsTests`: `all`, `fixes`, `off`; the fix title and labels; the check's rows and its empty case.
  - `BackportTargetsRecipientTests`: labelled and checked on open; `fixes`; no relabel on a push or over an existing
    label; a repository without lines untouched; a moved head.
  - `BackportYmlTests`: `defaultLabels`, `fixLabels`, a misspelt mode. `BackportTests`: line names resolve.
  - `GitWriterSidecarClientTests`: the route.
- **M8c, built (2026-11-05): provenance, `backported:` labels and hand-made backports (§5.5, D7).**

  **The marker.** Every body the app writes for a backport (the clean pull request and the `onConflict: draft` one)
  ends in `<!-- backport-of: owner/repo#N -->` (`BackportProvenance.Marker`). The regeneration saga (M6) appends its
  notes after the stored body, so the marker survives it. A marker naming another repository is ignored: a source is
  only ever a pull request of the same repository.

  **A backport merges** (`BackportLinkEventsRecipient` forwards every `PullRequestMerged` to the versioning lane;
  `BackportLinkRecipient` does the reads). It acts only when the base is a line (the `versioning.lines` pattern when
  lines are on, else `versioning.releaseBranches`) and not the default branch. The source is, in this order:
  1. the marker (the app's own backport);
  2. a body saying "Backport of #N", then a `backport/<N>-<line>` head (`BackportProvenance.FromText`);
  3. a `cherry-pick -x` trailer in the pull request's commits (at most five), resolved through the merged pull request
     into the default branch holding that commit.

  The source must be a **merged pull request into the default branch**, so no text in a body can label an arbitrary
  issue or an open pull request. It gets `backported: <line>` (the line name when the pattern matches, else the
  branch's last segment), added once. A hand-made backport also gets a comment on the source linking it; the app's own
  backport does not, since the backport report already links it.

  **A person creates `backport/<pr>-<line>`** (the commands the conflict comment gives, M5): the app opens its pull
  request into the line, titled `[<line>] <source title>`, with the marker. Only a push that **created** the branch and
  was **not by a bot** counts: the push event now carries `Created` and `PushedByBot` (`BranchCommitPushed`, filled by
  the GitHub normaliser). That keeps the app's own pushes out (its branches open their pull requests after regenerating,
  M6, and racing that would open one before the regenerated commit), and a later push to a branch whose pull request a
  maintainer closed does not reopen it. The line resolves as for labels (`ResolveLine`); a bare major with lines on
  names nothing.

  Cost accepted: one versioning-lane message and one `coverage.yml` read per merged pull request in a connected
  repository; the forge reads follow only for merges into a line.

  **Tests:**
  - `BackportProvenanceTests`: the marker round trip (case, other repository), body and head parsing, trailers, the
    label name.
  - `BackportLinkRecipientTests`: the app's backport labels without a comment; a hand-made one (body, head, trailer)
    labels and comments; merges into the default branch or a non-line, and unmerged or missing sources, do nothing; a
    hand-pushed branch opens its pull request with the marker; a missing source or a bare major opens nothing; the
    forwarder skips bots, later pushes and other branches.
- **M8d, built (2026-11-05): `/backport <line> --draft` (D7).**
  - `BackportCommand.Parse` returns the lines and the draft flag. `--draft` may stand anywhere on the command line and
    applies to every line the comment names. Any other token starting with `-` makes the comment **no command**: it
    would otherwise pass as a line token (letters and `-`), and an unknown option silently ignored would do something
    the person did not ask for. `--draft` with no line is no command either.
  - The flag travels on `BackportRequested.Draft` → `BackportMessage.Draft`; the recipient drafts when the request asks
    or `onConflict: draft` says so. Labels carry no option, so a labelled backport follows `onConflict`.
  - The conflict comment now ends each conflicting line's section with the next steps: push the hand-made branch (M8c
    opens its pull request) or comment `/backport <line> --draft`, with the line name taken from the backport head.
  - Tests: `BackportTests` (parsing; the conflict hint), `GitHubEventsCommandTests` (the flag on the event),
    `BackportRecipientTests` (a draft request drafts under the `comment` default; without it nothing drafts).
- **M8e, built (2026-11-05): the cross-repo dependency diagnosis (D11).** It is built in #570, not #571 as the split
  first said, because it reads only the backport's own result and the versioning runs' records.
  - **Writer:** `PlatformMarkers.Normalize` reports a `DependencyRaise` (path, npm/nuget, package, before, after, the
    line's value) for every **configured first-party** package (`markers.packages`) that the change raised wholly above
    the line's major and that normalization therefore kept at the line's value. A package the change did not move, an
    unconfigured package and a framework marker report nothing. The writer returns them per target
    (`BackportTargetResult.Raises`).
  - **Which repository releases what:** `VersioningController.FinishAsync` records each release whose tag is in place
    (`created`/`already-present`) in `PackageReleases/{kind}/{id}` (`PackageReleaseRecord`): the repository, and per
    version its tag and the pull requests its notes name (`(#N)`). The record is stored and loaded by id, with no query
    and so no RQL to splice into. **The first repository to release a package keeps it**, so another repository cannot
    take over a name. A package released before this record existed is unknown until its next release. Caps: 200
    releases per package, 50 pull requests per release.
  - **The report:** whatever each line's outcome, the backport comment adds a paragraph per raise. It says that the line
    keeps the package at its value and probably needs what the raised version brought, released on the line's own
    major. When the release is known, it adds the upstream tag and, for each of its first 10 pull requests, whether the
    upstream already backported it to a line of that major. The major is read from a `backported: <line>` label's
    trailing number (M8c), so `angular-21.x` and `21.x` both match. The wording is "probably needs": the app never starts
    a backport in another repository.
  - **No private-repo leak:** the upstream is named only when it is this repository or a public, connected one on the
    same forge. Otherwise the paragraph names the dependency alone. Another repository's pull requests are written as
    code (`` `owner/repo#N` ``), not as references, so the comment adds nothing to their timelines. Their titles are
    untrusted text and go in a code span, so a mention or markdown in them stays inert.
  - Tests: `PlatformMarkersTests.A_raised_first_party_package_that_normalization_drops_is_reported`; the writer's
    `BackportTests` (the raise on the target); `UpstreamReleasesTests` (range minimums, label majors, notes parsing, first
    claimant, public / private / unknown / own upstream); `BackportRecipientTests.A_raised_first_party_package_is_diagnosed_whatever_the_lines_outcome`.
- **Who may command the app (owner, 2026-11-05: "Make sure that special pull-request comments (like "/backport") are
  only handled for users that should be able to do so").** An audit of every way a person can ask the app to act:

  | Trigger | Actor | Check | Below write | Test |
  |---|---|---|---|---|
  | `/backport <lines> [--draft]` comment | the signed sender (T1) | role ≥ write, read per request, before anything is read or run | refusal comment, nothing runs | `BackportRecipientTests.A_backport_comment_without_write_access_runs_nothing` (none, read, triage) |
  | `backport:` label on a merged pull request | the sender | the same | the same | `…A_label_or_comment_needs_write_access_but_a_merge_does_not` |
  | `backport:` labels as the pull request merges | the merger | none: the merge needed write, and the merger saw the labels | n/a | the same test |
  | `/queue`, `/dequeue`, the queue label, the check-run button | the sender | role ≥ write (dequeue: also the author), re-checked before the merge | refusal comment / ignored | `MergeQueueEngineTests` (outsider), `MergeQueueCheckButtonTests` (reader) |
  | the dependency updater's commands | the sender | role ≥ write | 👎 reaction | `DependencySagaTests` (visitor) |
  | a hand-pushed `backport/<pr>-<line>` branch (M8c) | the push's sender | **new:** role ≥ write; before, only GitHub's rule that creating a branch needs write | ignored, logged | `BackportLinkRecipientTests.A_backport_branch_pushed_without_write_access_opens_nothing` |

  Every normaliser drops bots and the App itself, and handles only **new** comments (an edit is never a command). A
  backport merging into a line (M8c) and the default labels (M8b) are no commands: the first needs a merge (write), the
  second only proposes labels a maintainer removes, and its backport needs the merge. The other forges raise none of
  these commands.
- **Every automatic resolution is listed in the per-line report** (D6): path, strategy, and what each side added.

### 8.4 Spikes and implementation decisions (#571, 2026-11-05)

- **S4, packing on a line: confirmed, nothing to build for the default.** I built a scratch repository with projects
  `A → B` (`ProjectReference`) that import `MintPlayer.Versioning.MSBuild.targets` from source. It is tagged `A/v10.0.0`
  and `B/v10.0.0` at the base, `B/v10.1.0` and `A/v10.0.1` on `release/dotnet-10.x`, and `B/v11.0.0` and `A/v11.0.0` on
  `main`. `dotnet pack A` (SDK 11.0.100-rc.3) on the line gives `A.10.0.1.nupkg` with
  `<dependency id="B" version="10.1.0" />`. On `main` it gives `A.11.0.0` with `B 11.0.0`. Master's `B/v11.0.0` is
  unreachable from the line, so D10b's default (`>=` the line's tag) holds as built. The reason is that
  `MintPlayerVersioning` runs before `_GetOutputItemsFromPack`, and NuGet's `_GetProjectVersion` reads the referenced
  project's `PackageVersion` from it.
- **S4, the cost of `major-bounded`: one target, no NuGet task change.** The target runs `AfterTargets="_GetProjectReferenceVersions"`
  and rewrites `_ProjectReferencesWithVersions`' `ProjectVersion` metadata to `[v, (major+1).0.0)`. NuGet's pack accepts
  a range there: the line gives `version="[10.1.0, 11.0.0)"` and `main` gives `"[11.0.0, 12.0.0)"`. It only adds a
  target to `MintPlayer.Versioning.MSBuild.targets`, behind the property `MintPlayerVersioningDependencyRanges=major-bounded`.
- **S8, onboarding inventory.** Two onboardings open a pull request today:
  - **Versioning:** the `SetUpVersioning` custom action → `VersioningOnboardingService` → GitWriter
    `/jobs/versioning/onboarding`, branch `mintplayer/versioning-onboarding`.
  - **Dependency updates:** the `dependency-updates-onboard` page and the `EnableDependencyUpdates` action → the engine →
    GitWriter `/jobs/dependencies/files`, branch `mintplayer/dependencies-onboarding`.

  Five more are static copy-paste guides with no state: `coverage/setup`, `merge-queue/setup` (regenerate workflow,
  App permissions, ruleset steps), `pr-size-labels/setup`, `backports/setup` and `dependency-updates/setup`. What the two
  PR flows share: the permission gate (`IDependencyOnboardingService.CheckAsync`), `IRepositoryTree`, the forge
  pull-request find → create/update calls (`IForgeDependencyUpdates`), and the writer access calls. What they don't
  share:
  - each writes coverage.yml text its own way (no shared YAML editing);
  - each GitWriter endpoint has its own path allow-list;
  - each detects "already onboarded" differently;
  - there is no single list of features and the App permissions each one needs.
- **S7 (renaming a branch as the App): not run yet.** It needs the dev App on the scratch repository, and it is a live
  check like S5.
- **M9, which lines get dependency runs: every supported line, the current one included.** Owner's choice (2026-11-05,
  asked as "Should the current line also get its own patch-only dependency run?"). The consequence: master's own rolling
  dependency pull request gets no default `backport:` labels, since each line's runs keep that line up to date themselves.
  That already holds: `BackportTargetsEventsRecipient` (#570 M8b) skips every pull request a bot opened.
- **M9, one run document per line, beside the default branch's.** The default run keeps the id
  `DependencyUpdateRuns/{provider}/{id}`. A line's run is `…/lines/{line}`, and its bulky half is
  `DependencyUpdateRunData/…/lines/{line}`. Recipients load the line runs by that prefix (`LoadStartingWithAsync`, no
  index). Why: the saga's state is per branch, and every per-run field (pending workflow run, rolling sha, pull request,
  bisect, pins) would otherwise need a per-line map. Messages carry `Line`, so a step or signal reaches its own run.
  Tests: `DependencyLineRunTests`.
- **M9, the bot's branches per line: `mintplayer/dependencies-<line>` and `mintplayer/dependencies-work-<line>`.** A
  '-' rather than a '/' because git cannot hold `mintplayer/dependencies` and `mintplayer/dependencies/x` at once
  (D/F conflict in the ref namespace). The writer accepts only a line `ReleaseLine` itself writes
  (`ReleaseLine.TryParse(line)?.ToString() == line`), so a branch is never built from other text. Tests:
  `DependencyWriterJobTests.D13_*`. The merge queue recognises a line's rolling branch through
  `DependencyUpdateRun.IsRollingBranch`, which never matches a work ref.
- **M9, configuration and workflows stay the default branch's.** A line's run reads coverage.yml from the default
  branch (O16's rule). It dispatches the lookup on the line's branch (D14: the run builds the line's head), and its
  workflows must have the default branch's blob on the line's commit (D12). A line whose workflows drifted ends with
  `workflow-differs` until the line has them again (D9's regenerate setup keeps them in step). The pull request's title
  starts with `[<line>]`. A line's pull request is handed to the merge queue only when `mergeQueue.branches` queues the
  line. The run token gets the line (`{githubId}-{line}-{run}-{n}`), so the artifact names never collide with the
  default run's.
- **M9, scheduling without reading files: `FileLines`.** The default branch's run reads the supported lines
  (`SupportedLines`, with `backport.lines` overrides) at its start, into `DependencyUpdateRun.FileLines`. The ten-minute
  scheduler then triggers each line's run in the same slot (dedup key `{slot}:{line}`). As with `FileDisabled`, no
  tick reads a file. The cost: a line starts its runs one slot after the default run first reads it. A forge failure
  keeps the lines read before. A security alert (R10) also triggers each line that already has a run.
- **M9, `dependencies.lines`: `patch` (default), `all`, `security`, `off`.** It sets which updates a line's run may
  take. `off` records no lines and ends a line's run as `disabled`. Tests: `DependenciesYmlTests`.
- **M9c, the line ceiling is a level, not a version compare.** A line never takes a major. Under D13 the line's
  platform major is the line's own, so "no major" keeps every platform-anchored package under it, with no per-package
  major table to keep. `patch` takes patches. `all` takes up to minors. `security` takes only the run's security fixes.
  A security fix may go up to a minor on any line. One that needs a major is held (`AboveLineCeiling`, "needs a major …
  a human backports it"), so the pull request body lists it rather than dropping it. The ceiling is a candidate
  problem like a hold, so a lower candidate of the same dependency still ships. As with a hold, the candidates passed
  over are listed only when nothing lower ships (the engine's tier rule), so a line's pull request does not list "a
  minor exists" for every dependency. Tests: `LineCeilingTests`,
  `DependencyLineRunTests.D13_a_line_run_plans_under_its_ceiling…`.
- **M10, one combined onboarding pull request** (owner, 2026-11-06, asked as "one combined PR or one PR per feature";
  the owner chose combined). The chooser writes every picked feature into one `mintplayer/onboarding` pull request:
  coverage.yml sections, workflows, the old → new line table and the versioning seeds. Why: per-feature pull requests
  each edit coverage.yml and conflict with each other after the first merge. One merge also triggers the renames.
- **M10, coverage.yml sections go in alphabetical order, and a bot pull request is rebuilt rather than merged**
  (owner, 2026-11-06: "let the pull-requests the bot creates insert the sections in alphabetical order … several PRs
  inserting a section in the same spot will probably again cause merge conflicts").
  - Both onboardings appended their section to the end (`VersioningOnboarding.CoverageSectionAsync`,
    `DependencyOnboarding.Plan`). Now one editor puts a section before the first top-level key that sorts after it,
    or replaces the section when it is already there.
  - The conflict is handled by rebuilding. The bot's coverage.yml change is an edit ("this section, with this
    text"), not a stored file. Its onboarding pull request is replanned on the default branch's new head whenever
    that head moves while the pull request is open. A bot pull request therefore never stays in conflict, however
    many touched the same spot. The dependency onboarding already rebuilt on a new head
    (`DependencyUpdateEngine.cs:1505`). The versioning onboarding did not.
- **M10, dependency updates switch on when the onboarding pull request merges.** The engine skips its own onboarding
  pull request once `dependency-lookup.yml` is on the default branch (`DependencyUpdateEngine.cs:1480-1481`). The
  combined pull request carries the workflows, and the repository's `DependencyUpdates` is set to Enabled only after
  the merge, so the engine then marks onboarding done and starts the first run. Enabling it earlier would open the
  engine's own pull request beside the combined one. The bulk "Enable dependency updates" page stays as it is.
- **M10, coverage is switched on, not written into CI.** Uploading coverage needs a step in the repository's own
  build, whose shape varies (`/coverage/setup` has per-stack examples). The chooser turns the repository's coverage
  on and puts the matching example in the pull request body as a checklist item. It does not edit a workflow it
  cannot know.
- **M10, rulesets are a step of the chooser** (owner, 2026-11-06), not a separate action. With the merge queue or
  release lines picked, the ruleset plan is shown beside the pull request plan and applied with the user's token
  (§8.2).
- **M10e builds all of #554 and closes it** (owner, 2026-11-06, asked "how much of #554", answered "All of #554"
  over "what the queue needs" and "both rulesets, no extras"). The chooser's ruleset step:
  - creates ruleset 1 on the default branch and the lines, repairing `always` to `exempt`;
  - creates ruleset 2, with the checks the default head reports confirmed in the plan and the approval count asked;
  - migrates classic protection, ruleset 1 first, so the branch is never open.

  #554's other parts are part of it too: organization rulesets on the Account page, the enqueue refusal for a locked
  or review-blocked branch, and the eviction hint.
  **In its own pull request, not #571's** (owner, 2026-11-06: "perhaps I'll allow you to split the work", then picked
  "#554 as its own PR" over keeping it in #574 or splitting it in three). PR #574 ships the chooser without the
  ruleset step. #554 follows on a branch from master once #574 has merged, so S7 is checked live sooner. Until it
  ships, rulesets are set by hand (merge-queue.md §3).
- **#554's token: a one-shot OAuth app token, never Administration on the Apps** (owner, 2026-11-06, superseding
  the user-token design and then the scoped-Administration design). Rulesets are applied by a Spark custom action
  on the selected repositories:
  - it authorizes a dedicated OAuth app (`repo` scope, PKCE) in a popup;
  - it applies both rulesets to each repository;
  - it revokes the token in a `finally`.
  The Apps hold no Administration, so their private key cannot change rulesets. Organization rulesets (e4) are
  dropped: the organization is on GitHub Free. Evidence, the spikes and the f0–f6 plan are in the plan doc under
  "#554 redesign". The work restarted from master on `feat/issue554-rulesets-oauth` (owner, 2026-11-06: "Start fresh
  on a blank branch"). The Administration-based e0–e6 stay on the abandoned `feat/issue554-rulesets` as reference
  only.
- **M10, S7 is built on GitHub's documented rules and checked live after the deploy** (owner, 2026-11-06).
  `POST /repos/{o}/{r}/branches/{b}/rename`: "The authenticated user must have push access to the branch. If the
  branch is the default branch, the authenticated user must also have admin or owner permissions", and only the
  default branch needs `administration:write` from a fine-grained token (docs.github.com, Branches API, read
  2026-11-06). A line is never the default branch, so the App's Contents: write should do. The live check runs with
  S5.
- **M9c, a line's run reads coverage.yml from the default branch at every step** (`ConfigRef`). The default run
  keeps reading it at the commit it started on, as before. Found while wiring the ceiling: five later steps read the
  file at `ExpectedSha`, which for a line is the line's commit.

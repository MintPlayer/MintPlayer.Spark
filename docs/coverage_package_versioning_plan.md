# Automated package versioning — plan

**PRD:** [coverage_package_versioning_PRD.md](coverage_package_versioning_PRD.md) · **Issue:** #153 · **Branch:**
`feat/issue153-package-versioning` · **one pull request**, followed after the deploy by onboarding PRs in four
MintPlayer repositories (PRD D17).

## Status

2026-10-24: grilled (Q1–Q14 → D1–D17). Spikes open. No code yet.

## Spikes

Each spike ends with a written result in this file (numbers, file:line, or a red→green test) before the milestone
that depends on it starts.

| # | Question | Time box | Blocks |
|---|---|---|---|
| S1 | **MinVer and the static parser against real repos.** (a) Does `<MinVerTagPrefix>$(PackageId)/v</MinVerTagPrefix>` in `Directory.Build.targets` resolve per project, including a project that sets `PackageId` itself and one that relies on the default? (b) Does MinVer pick the right tag when `MintPlayer.Mapper/v…` and `MintPlayer.Mapper.Abstractions/v…` both exist (prefix collision)? If not, the tag format needs a terminator. (c) Run the static parser over Dotnet.Tools, AspNetCore.Tools, SpaServices and Spark: list every packable project whose TFMs, `PackageId` or inputs it cannot resolve without msbuild. | 1 day | M3, M7 |
| S2 | **npm/Nx version writing.** `npm version --no-git-tag-version` inside an npm workspace (does it touch the root lockfile?), Angular libraries built by ng-packagr from `dist/` (which `package.json` is the published one?), and rewriting `workspace:*`, `file:` and plain internal ranges. Prove on the Spark Nx workspace and one other Nx repo. | 1 day | M7 |
| S3 | **A stable API listing.** .NET: generate a PublicAPI.txt-style listing from the built assembly (Roslyn `PublicApiAnalyzer` format, or a metadata walk); TypeScript: from the `.d.ts` output, sorted and normalized. The hash must stay equal across two SDK patch versions and two TypeScript minors for unchanged source, and change on an added public member. | 1.5 days | M7 |
| S4 | **Packaging the engine for local mode.** The engine is C#. Options: a `dotnet tool` from GitHub Packages, a framework-dependent `dll` committed to the action, or a container action. Measure cold start on `ubuntu-latest` and `windows-latest`, and check the ncc `dist`-staleness rule still holds. Spark itself must run it from source (`dotnet run --project`). | 1 day | M7, M14 |
| S5 | **Tags through the existing git stack.** Create an annotated tag (`git mktag`/`git tag -a` on the bare mirror via `GitRunner`) and push it through `WriterService` as the App; push the same with `GITHUB_TOKEN` from a workflow. Confirm a tag pushed by `GITHUB_TOKEN` triggers no workflow (expected) and that the App's tag push does not trigger the consumer's `on: push: tags` either when more than three are pushed together. | 0.5 day | M5 |
| S6 | **OIDC claims for each trigger.** Token claims for a push run, a `workflow_dispatch` run fired by the App (trigger B), and a run on `release/8.x`. Which claim identifies the workflow file robustly (`workflow_ref` vs `job_workflow_ref`)? | 0.5 day | M6 |
| S7 | **Previous API hash from the registry.** Download the previously published nupkg / npm tarball for a seed tag and hash it with the S3 listing. Bounded size, no execution of package content (`.targets`, `install` scripts). | 0.5 day | M7, M13 |
| S8 | **Backport dry-run.** `RollingBranchReplay` with one merge commit onto two release branches, one clean and one conflicting; confirm nothing is pushed when one target conflicts, and which `IConflictResolver`s apply to csproj/package.json hunks. | 0.5 day | M10 |

## Grill — done 2026-10-24

Fourteen questions, all answered by the owner; the answers are D1–D17 in the PRD. Not grilled, decided by me and
marked as such in the PRD: "versioned" = a `versioning:` section in `coverage.yml` (D17).

## Milestones

Tests are written per milestone and **run once, at the end** (M16). Intermediate milestones are checked by reading
the code and type-checking.

| # | Milestone | Done when |
|---|---|---|
| M1 | **PR-title correction** (D14): `EPrTitlePolicy` (Inherit/Enabled/Disabled, Account default off) on Account and Repository; the title builder (template checkbox → existing type → linked-issue `bug` label → branch issue → `feat`; bot PRs keep their type; only same-repo issues); handler for `opened`/`edited`/`reopened` in `GitHubEventsRecipient`, skipping the App's own edits; a check run with the result. | Unit tests for the builder's precedence and the loop guard written |
| M2 | **`CodeCoverage.Versioning` library:** tag model (`<id>/v<version>`, annotation with `api-hash` and engine version), version arithmetic (D7 majors incl. free and override, D8 minor/patch and prerelease labels, never-down rule), input validation (PRD D5 table). No I/O. | Library compiles; unit tests for every rule in D7/D8 |
| M3 | **Project graph, static:** `.csproj` (`PackageId`, `IsPackable`, TFMs, `ProjectReference`, `PackageReference`, `PrivateAssets`, linked items, `$(Prop)` from the `Directory.Build.props` chain), `package.json` + `workspaces`, Nx `project.json`, `ng-package.json`, lockfiles. Path containment, symlink refusal, caps, `PathGlob`. Reads from a bare repo at a commit, never a checkout. | Parser tests on fixtures copied (scrubbed) from the S1 repos |
| M4 | **Release set:** tree-sha input diff since each package's last tag on the line, ignore globs, .NET dependent cascade, npm range/peer rule, lockstep groups (D13), human tags on HEAD as fixed versions. | Tests for the issue's three npm examples, the .NET cascade, a group, a human tag |
| M5 | **Tag writer:** annotated tags created and pushed through `GitRunner`/`WriterService` (server host) and plain git with `GITHUB_TOKEN` (local host), behind one interface; idempotent rerun; tags only on commits reachable from the default or a release branch. | Tests against a local bare repo: rerun is a no-op, unreachable commit refused |
| M6 | **Server host:** `POST /api/versioning/run` accepting the release manifest with OIDC validation (claims per S6), RavenDB run record (parameters only, no RQL splicing), per-repository serialization, check-run reporting. | Endpoint tests incl. wrong-claim 401/403 and malformed manifest 400 |
| M7 | **`versioning-action`** (`apps/CodeCoverage/versioning-action`, ncc bundle, committed `dist`, behind `ncc-build-guard`): build-side API hash (S3), evaluated TFMs, packed-package list, previous hash fallback (S7); server call with automatic fallback to the local engine (S4); `git fetch --tags` + deepen; npm version writing (S2); outputs: versions, dist-tags (D11), released ids. MinVer snippet for `Directory.Build.targets`. | Action unit tests; conformance test: one fixture repo through both hosts gives identical tags |
| M8 | **Releases** (D15): one per tag, notes from corrected PR titles grouped feat/fix/other, "Dependency update" for dependent-only releases, prerelease flag, sequential with backoff; `EReleaseOnTagPolicy`, Account default Enabled. | Tests for notes grouping and the policy matrix |
| M9 | **Release branches** (D10, D12): `versioning.releaseBranches` pattern; branch-major check; free packages only from the default branch; auto-cut `release/<old>.x` at the first parent on a major move with a PR comment; `EAutoCutReleaseBranchPolicy`, Account default Enabled. | Tests: single-target 8.x/10.x side by side, multi-target, a branch that computes the wrong major fails |
| M10 | **Backports** (D10): `backport: <line>` label and `/backport <line>` comment on merged PRs; dry-run all targets (S8); push `backport/<pr>-<line>` + open PRs with cherry-pick trailers, or comment the conflict and manual commands; nothing pushed on any conflict. | Tests: clean, one-conflicting-of-two, unknown line |
| M11 | **Trigger B** (D3): `versioning.dispatch`; on a merge to a versioned line, fire `workflow_dispatch` only when the release set (M4) is non-empty. | Tests: no-change merge dispatches nothing |
| M12 | **Forge abstraction + UI** (D16): `IForgeVersioning`, `EForgeCapability.Versioning`, GitHub implementation; Account/Repository settings for the three policies, hidden for forges without the capability; versioning history on the repository page. | Settings round-trip test; capability hides the section |
| M13 | **Onboarding** (D17): row action modelled on `DependencyOnboardingRowActions`; the PR adds MinVer + prefix, `0.0.0-PLACEHOLDER`, the action step with `permissions: contents: write`, a `versioning:` section; seed tags from the replaced versions when it merges (free packages seed their own version). | Onboarding-PR content snapshot tests on Dotnet.Tools-shaped and Nx-shaped fixtures |
| M14 | **Spark adopts it** in `mode: local` with `./apps/CodeCoverage/versioning-action`: csproj `<Version>`s and npm versions → `0.0.0-PLACEHOLDER`, `dotnet-build-master.yml` and the action-publish workflows call the in-repo action, seed tags from the current versions (the major-lock rule in CLAUDE.md is now enforced by D7 instead of review). | Workflow files reviewed; dry run of the local engine on the Spark repo prints the expected versions |
| M15 | **Docs:** `docs/code-coverage/versioning.md` (consumer guide: onboarding, triggers, coverage.yml reference, backports, the `release/*` ruleset advice, `on: release` warning), CLAUDE.md "Versioning" section updated for tags. | — |
| M16 | **Test sweep, once:** `npm run test:affected` with the Developer licence. Then the live test on the CodeCoverageDevelopment test repository (PRD D17): tagging, unchanged skip, cascade, preview, auto-cut, backport with a conflict, Releases, title correction, server stopped mid-run → fallback. | All green; live checklist ticked with evidence |
| M17 | **After merge + deploy:** onboarding PRs for MintPlayer.AspNetCore.SpaServices, MintPlayer.AspNetCore.Tools, MintPlayer.Dotnet.Tools and one Nx repository; each publishes one correct release. | Four releases on nuget.org / npm with matching tags and Releases |

## Test list (written per milestone, run once in M16)

- Version rules: every row of D7's anchor table; free package keeps its major; override `anchor:`; never-down refusal;
  `M > K` → `M.0.0`; minor iff hash changed; prerelease from anchor and from a runtime dependency, not from
  `PrivateAssets=all` / devDependencies; least-mature label wins; `.0.0-label.N` before the first stable, `.(m+1).0-label.N`
  after.
- Validation: version regex, package-id grammar + `check-ref-format`, hash regex, path escape (`..\..\`), symlink
  entry, oversized file, glob bomb, project/edge caps.
- Graph: ProjectReference chain, linked `Compile` outside the project, `Directory.Build.props` property, npm workspace,
  Nx project, packed-but-not-in-graph cross-check fails.
- Release set: issue's npm examples 1–3, exact pin, peer major, .NET cascade, unchanged skip, ignore globs, group,
  human tag on HEAD.
- Tags: annotated with hash, idempotent rerun, unreachable commit refused, server/local conformance.
- Server: OIDC claim mismatch, malformed manifest, concurrent runs on one repo serialized.
- Releases, dist-tags, release branches, auto-cut, backports, trigger B, title correction (precedence, loop guard,
  bot PRs, cross-repo issue ignored), onboarding PR content, settings visibility per forge.

## Deliberately not doing

- Per-PR preview features, conventional commits as the bump source, rebasing release branches, a major for breaking
  changes (PRD "Out of scope").
- GitLab / Bitbucket: recorded on #422.
- Nx Release or MinVer for npm: the action writes `package.json` itself (D9).

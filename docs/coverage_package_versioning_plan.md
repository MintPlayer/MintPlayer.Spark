# Automated package versioning — plan

**PRD:** [coverage_package_versioning_PRD.md](coverage_package_versioning_PRD.md) · **Issue:** #153 · **Branch:**
`feat/issue153-package-versioning` · **one pull request**, followed after the deploy by onboarding PRs in four
MintPlayer repositories (PRD D17).

## Status

2026-10-24: grilled (Q1–Q14 → D1–D17). Spikes S1–S4, S7, S8 run; S5 and S6 answered from GitHub's documented
behaviour and confirmed in the M16 live test (both need a real workflow run on GitHub). M1–M15 committed; M16 (the test sweep and the live test) next. M13 also fixed
the `VersioningControllerTests` harness, which had lacked an `IMessageBus` since M8 and failed every test in the class.
Every test written so far runs once, in M16.

2026-10-24, PR #537 open: CI is the M16 sweep (CLAUDE.md, "once a PR is open"). Run 40120193216 failed six jobs and
run 40122716503 one test; all fixed in `1c23039c`, `53905e05`, `2a9597bc`. One was a real defect: `TagWriter` asked
`merge-base --is-ancestor` about a commit the checkout lacks, and in a `blob:none` partial clone git tries an
unauthenticated lazy fetch first, failing with "could not read Username … terminal prompts disabled". It now checks
`cat-file -e <commit>^{commit}` first: the allowed branches were fetched, so a missing commit is on none of them and is
`Unreachable`. The rest were stale generated files (CodeCoverage model sync, translations golden, route snapshot gaining
`POST /api/versioning/run`), the forge-webhooks doc missing `PullRequestDescribed`/`BackportRequested`, the CSRF
exemption for `VersioningController.Run` (OIDC JWT only), and test-side mistakes (NSubstitute answers `""` for an
unconfigured `Task<string?>`; the M7 fixtures lacked the root `package.json` npm discovery requires; a release-branch
case assumed the wrong last tag; the conformance test compared `VersionTag` objects with strings). Regenerated files
(`App_Data/**`, layering goldens, route snapshots) are now `eol=lf` in `.gitattributes`, so a regeneration never shows
a line-ending-only diff (owner request). Spark's 46 seed tags were pushed by the owner at master `162c1fa3`.
Left: the M16 live test, then M17 after merge and deploy.

2026-10-24, after the merge: the first master run (40124565898) refused, so nothing was tagged. Webhooks.GitHub mirrored
Octokit.GraphQL's `beta` below its `preview` seed tag, and the other labels were wrong too (stable, or `rc` from
Endpoints). Fixed on `fix/issue153-prerelease-label` (PRD D8 amendment): a `prerelease` pin in `coverage.yml` (Spark
pins `preview`), the SDK as an upstream for TFM-anchored NuGet packages, and the registry hash reading generator
packages' `analyzers/dotnet/cs/` assembly.

2026-10-24/25, M17 onboardings:
- **Merged fixes:**
  - #539: the onboarding keeps byte order marks.
  - #540: it leaves MinVer in props that a package ships.
  - #541: multi-targeted packs (`buildMultiTargeting/` + `MPV004`) and stale branch reads.
  - #542: it references the latest stable MSBuild package from nuget.org.
- **Onboarding state:**
  - AspNetCore.Tools and SpaServices are onboarded, on MSBuild 1.0.1, and their master runs are green.
  - Dotnet.Tools #202 and ng-bootstrap #445 are open.
- **Last fix:** on `fix/issue153-untagged-pack`, an untagged package now packs at `0.0.0-untagged` in pull-request,
  merge-queue and local builds (PRD D9). Dotnet.Tools #202 found the bug: with no tag anywhere, nothing packed.
- **Left:** merge it, release MSBuild, and point #202 at the new version. Then merge #202 and #445, and check each
  repository's seed tags and first release.

## Spike results

- **S1 (MinVer, static parser).** Scratch repo with `MintPlayer.Mapper` (net10.0;net11.0, explicit `PackageId`),
  `MintPlayer.Mapper.Abstractions` (net10.0) and `Gen` (netstandard2.0, default `PackageId`), MinVer 7.0.0, tags
  `MintPlayer.Mapper/v11.2.0`, `MintPlayer.Mapper.Abstractions/v10.7.3`, `Gen/v13.0.0`, one commit past them.
  - Plain `<MinVerTagPrefix>$(PackageId)/v</MinVerTagPrefix>` in `Directory.Build.targets`: `Gen` got prefix `/v`
    (its `PackageId` defaults later, in the SDK's targets). Set inside `<Target BeforeTargets="MinVer">`: every
    project got its own prefix; Mapper → `11.2.1-alpha.0.1`, Abstractions → `10.7.4-alpha.0.1`, Gen →
    `13.0.1-alpha.0.1`; with HEAD tagged `MintPlayer.Mapper/v11.3.0` Mapper → `11.3.0`. **No prefix collision.**
  - Static parsing over Dotnet.Tools, AspNetCore.Tools, SpaServices and Spark: no `$(` in any `TargetFramework(s)` or
    `PackageId`. The parser must default `PackageId` to `AssemblyName`, else the project file name. Inputs outside the
    project directory exist: linked `Compile`/`None` items (truncate at `$(` or a wildcard; a `bin`/`obj` path maps to
    its owning project) and relative `<Import Project>` files.
- **S2 (npm writing).** In an npm workspace, `npm version 22.45.0 --no-git-tag-version -w @x/a` also rewrote the root
  `package-lock.json`; `npm pkg set 'version=1.0.0; echo pwned'` wrote the string as given. So the action edits
  `package.json` as JSON itself after validating the version (PRD D9), and writes `<ng-package dest>/package.json`
  (default `dist`) when it exists.
- **Step order.** The API hash needs the build and MinVer needs the tags, so the action runs **after build, before
  pack**, and `dotnet pack` runs without `--no-build` (PRD D3).
- **S3 (API listing).** `.NET`: `Api/DotnetApiListing.cs`, a System.Reflection.Metadata walk (no load, no execution):
  public/protected types and members, sealed types' protected members dropped, attributes, nullability and parameter
  names left out, enum underlying type kept. A sample library (records, generics with constraints, events, indexers,
  init setters, `in`/`ref readonly`, defaults, delegates, nested types) built for net8.0 with SDK 10.0.113 and SDK
  11.0.100-rc.2 gave the same hash (`sha256:d530d2a8…`); a private member added: unchanged; a public field added:
  changed. TypeScript: the `.d.ts` output normalized as text (comments dropped, whitespace collapsed around
  punctuation, files sorted by path); TypeScript 5.8.3 and 5.9.3 emitted byte-identical `.d.ts` for a sample with
  generics, enums, abstract classes, accessors, `#private`, `as const` and a default export. Private members appear
  in `.d.ts` (`private hidden;`), so the normalizer drops `private` member declarations (not `private constructor`).
- **S4 (engine packaging).** Built from source on the runner (PRD D2): `net8.0`, `RollForward=Major`, no package
  references; `dotnet run` (project mode) from a directory outside the repository, with SDK 10.0.113, against the
  repository's own `Directory.Build.props/.targets`: **18.7 s cold** (restore + build), **2.3 s warm**, ran on .NET
  8.0.32. Rejected: committed DLLs (needs a staleness guard on top of the ncc one) and a dotnet tool (its version pin
  is the chicken-and-egg the engine exists to solve).
- **S5 (tags).** GitHub documents that a push creating more than three tags creates no `push` event for them, and a
  `GITHUB_TOKEN` push triggers no workflow. Neither matters: publishing happens in the same run as the tagging (D3), and
  nothing listens for tags. Confirmed in M16.
- **S6 (OIDC).** `job_workflow_ref` names the workflow file that holds the job (a reusable workflow included), and
  `workflow_ref` the caller. The existing `ForgeOidcProfile.GitHub` (`CodeCoverage.Library/Forge/ForgeOidcProfile.cs`)
  maps `repository`, `repository_id`, owner, visibility, `run_id` and `run_attempt` only, so M6 adds `ref` and
  `workflow_ref` to the profile. The server requires `repository_id` of a known repository, a `ref` that is the default
  branch or a configured release branch (a pull-request run's `refs/pull/N/merge` is refused, which also keeps fork
  and PR runs from tagging), and records `workflow_ref`. Confirmed per trigger in M16.
- **S7 (previous hash from the registry).** `MintPlayer.SourceGenerators.Attributes` 13.0.0 from nuget.org's flat
  container (23 KB), `lib/netstandard2.0/*.dll` read by the S3 lister: `sha256:06f24f2d…`; the same source
  (`MintPlayer.Dotnet.Tools` at 33538f6) built locally on Windows: **the same hash**. The package's content is never
  executed: the nupkg is a zip, only `lib/**.dll` metadata is read. npm: the tarball holds the same `.d.ts` files the
  build emitted, read as text.
- **S8 (backport dry-run).** `RollingBranchReplay` does not fit: its resolution table takes the human's side on planned
  manifests. A backport is plain plumbing on the same `GitRunner`: `merge-tree --write-tree --merge-base=<c>^1
  <target> <c>` per target, `MergeTreeOutput.Parse`, any conflict = refuse; then `commit-tree`. No `IConflictResolver`
  applies: versions are placeholders in source, so version lines never conflict. Every target is dry-run before any
  push.

## Implementation decisions

M7 (versioning action):

- **Manifest collection is C#, in `CodeCoverage.Versioning.Cli manifest`, not TypeScript.** It needs the static
  graph (the same `ProjectGraphBuilder` the run uses) and the `System.Reflection.Metadata` API lister (S3). Porting
  either to TypeScript would give two listers that could disagree on a hash. The action shells out to
  `dotnet run --project <engine>` in both modes. Cost per S4: 18.7 s cold, 2.3 s warm.
- **"Packed" = build output was found.** For NuGet that is `<AssemblyName>.dll` under `bin/<Configuration>/<tfm>`
  or the artifacts layout. For npm it is `.d.ts` files under the ng-packagr `dest`, else under the package folder.
  A package without output is sent with no hash and a note ("its API counts as changed"), never dropped
  (`BuildManifest.cs`).
- **An id two projects declare is excluded with a warning naming both paths**, instead of failing the graph. Before
  that change, Spark's tree failed outright: test fixtures under `**/fixtures/**` re-declare real package ids.
  Test: `ProjectGraphTests.An_id_two_projects_declare_is_versioned_by_neither_and_says_where`.
- **`versioning.ignore` globs also keep matching project files out of the graph** (they used to filter changed
  paths only). Evidence: on Spark, `manifest --config {"ignore":["**/*.md","**/fixtures/**","**/Fixtures/**"]}` went
  from 20 packages to 43 (every `MintPlayer.Spark.*` plus `@mintplayer/ng-spark`, 43 API hashes, no notes). Test:
  `ProjectGraphTests.Projects_matching_versioning_ignore_are_left_out_of_the_graph`. M14 sets those globs in Spark's
  `coverage.yml`.
- **The registry fallback (S7) also covers a lightweight tag.** `{ Annotation.ApiHash: null }` never matched a tag
  without an annotation, which is exactly a tag made by hand. Fixed to `last.Annotation?.ApiHash is null`. Test:
  `BuildManifestTests` (the lightweight `@acme/util/v1.0.0` asks the registry; the annotated `Acme.Core/v1.0.0` does
  not).
- **Local mode reads `coverage.yml` from `refs/remotes/origin/<default branch>`**, the same file the server reads
  (D13). A release branch therefore cannot loosen its own rules.
- **The local token is optional.** Without `token`, the checkout's own credentials push, so `contents: write` is
  enough. With it, the token travels in `CODECOVERAGE_VERSIONING_TOKEN`, never in an argument.
- **The npm package map comes from tracked `package.json` files** (`git ls-files`, never `node_modules`). The first
  path per name wins. A released package with no tracked `package.json` fails the step.
- **Internal ranges are rewritten in `dependencies`, `peerDependencies` and `optionalDependencies` only**, to
  `^<version>`. `devDependencies` are not rewritten: they are not part of what a consumer installs.
- **Server mode falls back to local on no OIDC token, a network error, a 10-minute timeout or a 5xx.** A 4xx is the
  app's answer, and the action fails on it (`server.test.ts`).
- **Conformance (D2):** `VersioningConformanceTests` seeds two identical origins and runs two rounds, first the
  initial release and then an API change. One origin goes through the git writer's `/jobs/versioning/run`; the other
  goes through the CLI's composition (`ReleaseRun` + `TagWriter(CliTagGit)` on the checkout). Both must produce the
  same tag names, commits and annotations. The tagger differs by host and is not compared.
- **`versioning-action-publish.yml` moves `versioning-v<major>`** on changes to the action and to the engine it
  runs from source (`CodeCoverage.Versioning`, `CodeCoverage.Versioning.Cli`). Without that, local mode would run
  a stale engine at the pinned commit. The action has its own `package-lock.json`, like `dependency-action`,
  because compile-ts-action runs `npm ci` in its folder.
- **M8: the Release notes are computed by the engine, so both hosts write the same text (D2, D15).**
  `ReleaseNotes.ForAsync` reads `git log --first-parent` subjects from the package's previous tag on its line to
  the release commit, limited to the package's inputs (`:(literal)` pathspecs) and capped at 200 commits. It
  groups them into Features / Fixes / Other changes by the conventional-commit prefix, and lists every followed
  dependency as "Dependency update: <id> <version>". A dependent-only release has only those lines; a first release
  says "First release.". A merge commit "Merge pull request #N from …" is shown as its body's first line plus
  " (#N)", the PR's title. Evidence: `ReleaseNotesTests` (grouping, dependent-only, first release, a real-git
  repository with a squash commit, a merge commit and an off-input commit).
- **The coverage ignore globs are not applied to the notes log.** A commit touching only an ignored file (a `.md`)
  inside the inputs can appear in the notes. It is rare, harmless in a changelog, and keeping the log a plain
  pathspec keeps it one git call.
- **A Release is made for every tag that is in place, created or already present.** Creating one is idempotent
  (an existing Release is counted "exists"), so a rerun recovers a Release that failed the first time.
- **Server: the Releases are created by a queued message, not in the request.** `VersioningController` saves the
  run, then broadcasts `CreateVersionReleasesMessage` on `CoverageQueues.Versioning`. `VersionReleaseRecipient`
  re-reads the policy (`Repository.ResolveReleaseOnTag`, account default), spaces creates by 1 s, saves each
  outcome as it lands, and lets a `ForgeRateLimitedException` park the message for the bus's durable retry. A rate
  limit therefore never holds the action's request or its 10-minute fallback timeout, and a resumed message skips
  the Releases already done. Evidence: `VersionReleaseRecipientTests` (policy matrix, in-place tags only, the
  prerelease flag, "exists", rerun idempotence).
- **Local: the `releases` input (default `true`, the policy's default) stands in for the policy**, which the runner
  cannot read. The Releases are made with `github-token` (default `${{ github.token }}`) through the REST API,
  1 s apart, retrying only a real rate limit (`retry-after`, then `x-ratelimit-reset`, capped at 60 s); a 403
  without a rate-limit signal is a permission error and is not retried. A failure is a warning, not a step
  failure: the tags, which are the release, are already pushed. Caveat: a server run that falls back to local
  follows the input, not the app's policy. Evidence: `releases.test.ts`.
- **`make_latest: false`** on every Release: in a monorepo, "latest" across all packages means nothing, and a
  backport would otherwise steal it.
- **M9: the engine reports the cuts, the hosts make them (D2, D12).** `ReleasePlanner` marks a release
  `MovedFromMajor` when it runs on the default branch, its anchor is a platform (not free), and its new major is
  above the package's highest tag major. That previous highest tag stands in for "M(first parent)": the tags are
  the record of what the first parent shipped, and reading them needs no second plan. `ReleaseBranchCuts.ForAsync`
  groups the moved releases whose tag is in place by old major, resolves `head^1` and reads the PR number from the
  head's subject (GitHub's "Merge pull request #N from …" or a squash's trailing "(#N)"). It returns one
  `ReleaseBranchCut` per old major. A root commit, or patterns with no single-`*` entry to name the branch from,
  gives a warning and no cut. Evidence: `ReleaseBranchCutsTests` (move on net11.0, multi-target net10.0;net11.0,
  free package never moves, a `release/10.x` run beside 11.x moves nothing, naming, subject parsing, a real-git
  first parent, a root commit).
- **The branch is named from the first pattern with exactly one `*`** (and no `?`), the major in its place:
  `release/*.x` → `release/10.x`. The patterns are the repository's `versioning.releaseBranches`, the same list
  that decides which branches may tag, so the cut branch is always one the next run accepts as a release line.
- **The first package to move cuts the branch for the whole line.** Creating a branch that exists is "exists",
  not an error, and the existing branch is left where it is. The PR comment is posted only by the cut that
  created the branch.
- **Server: a queued `CutReleaseBranchesMessage` and `ReleaseBranchCutRecipient`**, as the Releases are.
  The recipient re-reads `Repository.ResolveAutoCutReleaseBranches` (Account default Enabled) when it handles the
  message, and re-validates what it sends to the forge. It accepts only a branch name that passes
  `VersioningConfig.IsBranchPattern` and has no `*`, and only a 40-character object id. Anything else is marked
  "invalid" and never sent. The GitWriter re-validates the patterns it is sent too (`VersioningSettingsTests`).
  Evidence: `ReleaseBranchCutRecipientTests` (policy matrix, existing branch, comment only on create, invalid name or
  commit).
- **Local: the `cut-release-branches` input (default `true`) stands in for the policy**, as `releases` does. The
  branch is created with `POST /git/refs`, and a 422 "Reference already exists" counts as "exists". The comment
  needs `pull-requests: write`; when it is refused, that is a warning. The comment text is the same in both hosts,
  with `\n` spelled out in C# so a CRLF checkout does not change it. Evidence: `releases.test.ts`
  (`cutReleaseBranches`, `cutComment`).
- **No branch protection is set.** Only a ruleset matching `release/*` protects the branch; the docs (M15)
  advise one, and the comment says so.
- **M10: backports are app-only, in both modes.** Labels and comments arrive as webhooks, so there is no local
  host for them; `mode: local` repositories get backports as long as the app is installed. Three triggers raise
  one `BackportRequested` event (`EBackportSource`): the `backport:` labels a pull request carries when it merges,
  a `backport:` label added to an already-merged pull request, and a `/backport <line> …` comment. The parser
  (`BackportCommand`) takes at most 10 lines of `[A-Za-z0-9._/-]{1,100}`, and only from a comment's first line.
  Evidence: `BackportTests` (CodeCoverage.Tests: label and comment parsing).
- **Who may ask:** a label or comment needs write access, read from the forge per request as the merge queue does
  (T1). Labels present at merge need no check: the merge needed write access. A comment on an open pull request is
  answered with "add the label and it runs on merge"; a label on an open pull request waits silently for the
  merge. Evidence: `BackportRecipientTests` (Triage refused, Merge accepted).
- **A line is resolved only through `versioning.releaseBranches` on the default branch.** A major or `N.x` names
  the branch the way a cut names it (`ReleaseBranchCuts.NameFor`); a branch name must match a pattern; the branch
  must exist. One unknown line refuses the whole request before the writer is reached, so a typo never produces a
  partial backport. The base the pull request merged into is skipped. Evidence: `BackportTests.A_line_resolves_…`,
  `BackportRecipientTests.A_line_naming_no_release_branch_…`.
- **The writer uses plumbing, not `RollingBranchReplay` (S8).** `BackportService` reads the merged commit's mode:
  2+ parents is a merge (base = first parent); a run of single-parent commits whose subjects equal the pull
  request's commit subjects is a rebase merge (base = the run's parent); anything else is a squash. Each target
  is merged in memory with `merge-tree --write-tree --merge-base=<base> <tip> <commit>`. **Any conflict on any
  target pushes nothing**; the comment names the files per line and the manual `git switch` + `git cherry-pick -x`
  (`-m 1` for a merge, `<base>..<commit>` for a rebase). When all apply, each target gets one commit
  (`commit-tree -p <tip>`, the original author, the versioning committer, a `(cherry picked from commit …)`
  trailer per original commit) pushed to `backport/<pr>-<line>` without force, and the app opens
  `[N.x] <title>`. CI on that pull request is the compile check against the line's platform. A head that exists is
  "exists" (never overwritten); a line whose tree already equals the result is "empty" and gets no branch.
  Evidence: `CodeCoverage.GitWriter.Tests/BackportTests` (squash on two lines, one conflicting of two pushes
  nothing, rebase run as one commit, already-applied line, malformed requests refused with 400).
- **The endpoint re-validates everything:** 1–10 targets, heads under `backport/`, no target equal to the source,
  distinct branches and heads, a 40-hex commit, a single-line title of at most 256 characters.
- **M11: trigger B consumes the existing neutral push event.** `BranchCommitPushed` already carries branch and sha
  (heads only, no deletes), so no forge code changed. `VersioningDispatchEventsRecipient` forwards a push only for
  the default branch, or for a branch matched by the release patterns cached in `VersioningDispatchState`
  (`VersioningDispatchStates/<provider>/<id>`, loaded by id, not indexed). Every other push costs one document load
  and no forge call. The forward is `BroadcastOnceAsync` keyed `repo:branch:sha`, so a redelivered webhook plans
  once. Evidence: `DispatchRecipientTests.Only_the_default_branch_and_cached_release_lines_are_forwarded`.
- **The cache is never the authority.** The worker re-reads `coverage.yml` from the default branch (D17) on every
  planned push and rewrites the state, or deletes it when `versioning.dispatch` is gone. A push to the default branch
  is always planned, so a pattern change lands with the push that made it; a release line that was never cached
  waits for one default-branch push. Evidence: `DispatchRecipientTests.Without_a_dispatch_setting_…`.
- **The plan runs in the writer, without a build** (`/jobs/versioning/plan`, `ReleaseRun.PlanAsync`). It fetches
  the branch and the tags, checks the commit is on the branch, and runs `ReleasePlanner` on the static graph, the
  tags and the input diff with no build facts. A null API hash counts as changed, so the plan can over-report a
  release (the run then tags nothing for it) but never misses one. It answers package ids only; its versions are not
  the run's. Evidence: `CodeCoverage.GitWriter.Tests/VersioningPlanTests` (untagged → `Core`; docs-only commit after
  the tag → empty and no tag written; an input change → `Core`).
- **Dispatch rule:** `workflow_dispatch` fires on the pushed branch when the plan has a release **or a refused
  package**. A refusal dispatches because the run fails with it, and that is where the owner sees it; an empty plan
  dispatches nothing. A plan that could not be made (busy, no writer access, unplannable commit) dispatches nothing
  and is logged; the next push plans again. Evidence: `DispatchRecipientTests.A_merge_that_changes_no_package_…`
  (no dispatch, access revoked) and `…_changes_a_package_dispatches_…` (`publish.yml` on `release/8.x`, major 8).
- **M12: D16 hides the policies, it does not explain them.** `VersioningForm.Shape` sets `ShowedOn = None` and
  `IsReadOnly` on `PrTitleCorrection`, `ReleaseOnTag` and `AutoCutReleaseBranches` when no `IForgeVersioning` serves
  the account's or repository's provider. `ShowedOn` alone is layout only (Spark still validates and writes a field
  it does not draw), hence the pair. Unlike the PR size labeller (#527 D8) there is no "unavailable" text: D16 says
  the UI is hidden elsewhere. The policies are protected settings automatically (deny by default in
  `OwnerSettings`), and are now classified in `OwnerPrivacyEndToEndTests` and `ConnectionStateIsReadOnlyTests`.
  Evidence: `VersioningFormTests` (both ways) and
  `OwnerPrivacyEndToEndTests.The_versioning_policies_round_trip_through_the_account_and_repository_pages`.
- **The history builds no index (D-O22).** `VersioningRun` is a read-only Spark entity with a context root, and the
  repository page shows it through `Custom.Repository_VersioningRuns`, which streams the repository's id prefix
  `VersioningRuns/<provider>/<repo id>/` (the trailing slash keeps repository 71010 out of 7101's stream). The query
  is named `GetVersioningRuns`: model sync mints a `Database.VersioningRuns` query, which would build an auto index,
  for every root unless a query of that name exists. Read is `QueryRead/VersioningRun` for everyone, narrowed to
  visible repositories by the row filter, as for commits; the nested lists are `[IgnoreProperty]` and shown as the
  `Tags` and `RefusedPackages` summaries. Evidence: `ReadRowFilterTests.Repository_VersioningRuns_…` (parent-scoped,
  newest first, wrong parent type refused); the model sync run left `VersioningRun.json` unchanged.

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
| M13 | **Onboarding** (D17): ✅ detail action `SetUpVersioning` (not a row action, superseded); the PR adds `MintPlayer.Versioning.MSBuild` (our own exact-tag package, replacing MinVer, D9) to `Directory.Build.props`, points npm publishing at `MintPlayer/github-actions/publish-npm-packages`, `0.0.0-PLACEHOLDER`, the action step with `permissions: contents: write`, a `versioning:` section; seed tags from the replaced versions when it merges (free packages seed their own version). | Onboarding-PR content snapshot tests on Dotnet.Tools-shaped and Nx-shaped fixtures |
| M14 ✅ | **Spark adopts it** in `mode: local` with `./apps/CodeCoverage/versioning-action`: csproj `<Version>`s and npm versions → `0.0.0-PLACEHOLDER`, `dotnet-build-master.yml` and the action-publish workflows call the in-repo action, seed tags from the current versions (the major-lock rule in CLAUDE.md is now enforced by D7 instead of review). | Workflow files reviewed; the placeholder and tagged packs proven on `MintPlayer.Spark.Attributes` (PRD D17); the engine dry run on Spark is part of the M16 live test. Done differently: ng-spark keeps `22.44.0` (workspace ranges), the action-publish workflows are not applicable (PRD D17) |
| M15 ✅ | **Docs:** `docs/code-coverage/versioning.md` (consumer guide: onboarding, triggers, coverage.yml reference, backports, the `release/*` ruleset advice, `on: release` warning), CLAUDE.md "Versioning" section updated for tags. | — |
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

# Changelogs — plan

**PRD:** [coverage_changelog_PRD.md](coverage_changelog_PRD.md) · **Issue:** #693 · **Branch:**
`feat/issue693-changelog` · **one pull request**.

## Status

**2026-11-22:** investigated with five read-only research passes:
- one of the owner's private apps;
- the CodeCoverage integration points;
- public changelog tools, in two web passes with cited sources;
- the existing D14 and D15 code.

Grill Q1 was answered, then superseded by the owner's "no versions in commits" principle (PRD D1 → D2). The grill is
paused.

**2026-11-22, later:** spikes S1, S2, S4 and S5 run; results are in the PRD under "Spike results".
- **Defect:** D15 release notes on release lines list the promotion merge, not the PRs.
- **New questions:** Q13 (backports carry no original PR number) and Q14 (81% of Spark's subjects are untyped).

S3, S6 and S7 are implementation-shaped and come after the grill.

**2026-11-22, grill complete:** Q1–Q15 answered (D2–D12). S6 was dropped (D10) and S3 narrowed (D11). **NEXT:** M1.

**2026-11-23:** M1–M10 committed; S8 run (results under S8). M11's policies, `versioning.changelog.include` and the
database copy with its repository query are committed (PRD D3 "Done (M11)"). The upcoming view per release line (Q12
"Done (M11)") and the server-mode `changelog.include` wire fix are committed. M12 (onboarding's template section and
repository-metadata note, PRD D7/D12 "Done (M12)") is committed. M13: `docs/code-coverage/changelog.md`, a "The changelog"
step on `/versioning/setup`, the README row, D14/D15 marked superseded in `coverage_package_versioning_PRD.md`, and the
action's stale `dist` rebuilt. **NEXT:** M14 (push, PR, checks, coverage).

**2026-11-23, M14:** PR #704 opened. The CI fixes are in `0f8d1c3e7` and `d7561668c`:
- `modelHashes.json` resynced after a hand edit;
- the new model files' `$schema` made relative;
- the Account tab group added;
- the forge-webhook reference rows added;
- `ConnectionStateIsReadOnlyTests` and `OwnerPrivacyEndToEndTests` classify the new policies.

Run 45091276261 was then green except the dependency-action jvm IT, which hung in Maven at vitest's 300 s. That code is
untouched by this PR; the same hang happened on PR #694. Patch coverage at `d7561668c` was 86.3% (753/873 lines).
- **`c60af6f56`:** tests for `GitHubPullRequestFacts` and the changelog queries (`ChangelogQueryActionsTests`, on both
  stores). They found a parsing defect, fixed (PRD Risks).
- **`8101a4592`** (owner-approved CI change):
  - the jvm IT slice caches `~/.m2/repository`;
  - the IT harness caps each tool command at 120 s and logs a killed command's output (`IT_COMMAND_TIMEOUT_MS`,
    `dependency-action/src/integration/setup.ts`).

**NEXT:** checks green, then patch coverage via the MCP. The owner queues.

## Spikes

Each spike answers one question with a measurement. Results go into the PRD's decisions with their evidence.

### S1 — Notes in the annotated tag message (answers Q2)

- **Size and format.** Measure the largest notes Spark would produce: the notes of the `/release dotnet-11.x` of
  2026-11-16 for `MintPlayer.Spark`, and an Angular package. Choose a structured format next to `api-hash:`, for
  example JSON lines or `entry:` trailers, that the C# engine and the TS action both parse.
- **Reading at pack time.** Time `git for-each-ref refs/tags/<PackageId>/v* --merged HEAD --format=%(contents)` on
  Spark's real tag set from the MSBuild task. Check that CI checkouts have the tag *messages*, not only the refs: a
  `fetch-depth: 0` checkout fetches annotated tag objects, but a shallow or partial checkout may not.
- **Corrections.** Confirm what happens when a tag is recreated: consumers' caches, the GitHub Release attached to the
  tag, and the server mirror.

### S2 — A repository-wide Release under a non-package tag (answers Q3)

- Does a tag such as `release/dotnet-11.x/2026-11-22` (or any non-`<PackageId>/v` name) pass unnoticed through:
  - `VersionCalculator` and the tag grammar;
  - `MirrorRefs`;
  - `MintPlayer.Versioning.MSBuild`'s prefix match;
  - the `release: published` handler at `GitHubEventsRecipient.cs:644`, which feeds downstream dependency updates?
- Write one red test per reader that must ignore it.

### S3 — The changelog inside the artifacts (narrowed by D11: only `PackageReleaseNotes`; no CHANGELOG.md in nupkg or npm)

- **.NET:** an MSBuild target in `MintPlayer.Versioning.MSBuild` writes `obj/CHANGELOG.md` from the reachable tags and
  packs it with `Pack="true"`, and fills `PackageReleaseNotes` (35,000-character cap). Prove it with a real
  `dotnet pack` in `RealPackTests`.
  - Check whether nuget.org shows `releaseNotes` and whether the package page links the packed file.
- **npm:** the action writes `CHANGELOG.md` into the publish folder. Check `files`/`.npmignore` behaviour with
  `npm pack --dry-run`, and whether npmjs.com displays it.

### S4 — The relevance filter on real history (answers Q4, and the "empty first changelog" risk)

- Classify Spark's master first-parent subjects since D14 went live, and before it, with the proposed filter. Report:
  - the share kept, dropped and untyped;
  - 20 sampled dropped entries, to check none were user-relevant.
- Repeat on one other onboarded repository.

### S5 — Backport deduplication (part of the proposed design, item 5)

- Read the title and body format of a real backport PR on `release/dotnet-11.x`. Does the original `(#n)` survive into
  the line's first-parent subject?
- Define the key (the original PR number) and write a red test: one change that ships on master's next line and on a
  backport line gives one entry in each line's notes and none twice.

### ~~S6 — A changelog inside a deployed Spark app (answers Q8)~~ — dropped: D10, out of scope

- Apps (`apps/*`) deploy from master untagged. Decide what "version" means for an app: deploy run, date, or the package
  versions it consumes.
- Prototype `MintPlayer.Spark.Changelog`: the build bundles the rendered changelog (JSON) as an embedded resource, a
  Spark endpoint serves it, and an `@mintplayer/ng-spark/changelog` page renders it.
- A new endpoint updates `CsrfSurfaceTests` and the route snapshot.

### S7 — Settings and onboarding surface (answers Q9, Q10)

- Add `ChangelogPolicy` to `Repository.json` / `Account`, following `ResolveReleaseOnTag`. Run
  `--spark-synchronize-model` (never hand-edit).
- Add a `changelog:` section parser following `VersioningYml.cs`.
- Check what onboarding would add as a PR template when none exists, and that `TemplateWords` maps every box.

### S8 — The previous API listing for the Public API section (D6)

- The action has the current normalized listing after the build (#153 D6). Can it reliably get the *previous*
  version's listing from the registry package, as D6's fallback already does for the hash, for NuGet and npm, including
  prereleases and `netstandard` generators?
- Measure the listing size of `MintPlayer.Spark` to decide whether it is worth keeping in RavenDB instead.
- **Result (2026-11-23):** yes. I measured it with a throwaway `dotnet run` script over `PublicPackageRegistry` against
  nuget.org and npmjs.
  - Local `MintPlayer.Spark` (net11.0): 921 lines, 124,020 characters, 6 lines over 400.
  - Registry `MintPlayer.Spark` 11.0.0: 916 lines. The delta is 5 added and 0 removed, 819 characters.
  - `@mintplayer/ng-spark` 22.0.0: 1,077 lines, 81,005 characters.
  - `MintPlayer.Spark.SourceGenerators` 11.0.0 first came back **null**. The package ships its generator under
    `analyzers/dotnet/roslyn5.9/cs/`, and the registry only looked in `analyzers/dotnet/cs/`. That was a #153 S7 bug too:
    every generator release counted its API as changed. It is fixed in this PR, which reads the newest `roslyn<x.y>`
    folder (red→green
    `PackageRegistryTests.An_analyzer_in_a_roslyn_versioned_folder_is_read_from_the_newest_one`). After the fix it reads
    564 lines.
  - **Decision:** the registry is the source and nothing goes to RavenDB. A whole listing (about 124 KB) would cost a
    document per release for data the registry already keeps. Only the capped delta travels, in the manifest.

## Milestones

Grill complete 2026-11-22 (PRD D2–D13, D16). One PR on `feat/issue693-changelog`. TDD: a red test first per behaviour, run
singly. The full gate runs **once, at the end, on CI** (push → PR → checks; no local sweep).

~~Two hosts render the notes, so every rendering change lands in both C# and TS.~~ *Corrected in M1:* only
`CodeCoverage.Versioning` (C#) renders. The action (`mode: local`) runs the C# engine CLI and posts the notes it
returns (`CodeCoverage.Versioning.Cli/Wire.cs:47`). TS changes only where the action itself calls GitHub
(`releases.ts`: `make_latest`, the per-run Release). The action's `dist` is rebuilt in the main checkout only
(CLAUDE.md, ncc).

| # | Milestone | Decisions | Notes |
|---|---|---|---|
| M1 ✅ | **Range fix:** a non-PR merge in the first-parent walk is expanded into its first parent..second parent. | D4 | Done: `ReleaseNotesTests.A_promote_merge_…` red → green. |
| M2 | **Deduplication:** key every entry by its original PR number. Read `Backport-of: #n`, then `(cherry picked from commit <sha>)`; at release time fall back to the backport PR body's `<!-- backport-of: … -->` through the API. | D5 | Red test: `da8be3a91`-style backport plus the original arriving through a promotion gives one entry. |
| M3 | **Queue trailer:** the merge queue adds `Backport-of: #n` to a backport PR's squashed message. | D5 | Beside the co-author trailers (merge-queue D13/D60). |
| M4 | **Entry model and renderer:** PR, issues, type, breaking, text, package; the D6 filter and sections; the D9 `## Changelog` body section and `changelog: skip` label; dependency-only lines. Pure functions. | D6, D9 | Replaces `ReleaseNotes.Compose`'s Features / Fixes / Other changes. C# only. |
| M5 | **Title correction:** switch it on together with the changelog policy. When no explicit signal is found (box, title type, `bug` label; never the branch name), the check run asks with three buttons (Feature / Fix / Not user-facing), and a click edits the title. | D6, D12 | `PrTitleBuilder`, `PrTitleRecipients`, the check-run button handler as for the merge queue. |
| M6 | **Retroactive typing:** for an untyped subject, the release run looks up the PR from its trailing `(#N)` and runs the builder on its current metadata. | D6 | API reads, bounded by `MaxCommits`. |
| M7 | **Tag notes:** the release run writes a notes block into the annotated tag message; `TagAnnotation` reads it back. | D3 | Format shared by C# and TS. Finish S1: do consumer CI checkouts fetch the tag objects? |
| M8 | **Releases:** per-package Releases only for packages with their own entries, `make_latest: false`. One per-run Release, tag `release/<line>/<yyyy-MM-dd>[.n]`, created last and marked latest, linking the package Releases. | D7 | `IForgeVersioning.CreateReleaseAsync` gains `make_latest`. Prove S2's "engine ignores the tag" with a test. |
| M9 | **Public API section:** diff the normalized listing against the previous version's; removals go under Breaking. | D6 | Spike S8 first: the previous listing from the registry package. |
| M10 | **`PackageReleaseNotes`:** `MintPlayer.Versioning.MSBuild` reads the tag's notes block into the nuspec, current version only, capped at 35,000 characters. | D11 | `RealPackTests`, including a tag without notes. |
| M11 | **CodeCoverage (Spark):** `ChangelogPolicy` and `PrTemplatePolicy` on Repository/Account (synchronize the model), a `changelog:` section in `coverage.yml` (parser like `VersioningYml`), the entry copy **through `ISparkSession` only** (ids + `Query<T>()`, no indexes; tests on in-memory and RavenDB), the repository changelog page and the upcoming entries per line. | D3, D10, D13, D16, Q12 | `actions.json`/`security.json` changes → synchronize and verify security; new endpoints → `CsrfSurfaceTests` and the route snapshot; translations → golden. |
| M12 | **Onboarding:** add or append the "Type of change" template section unless `PrTemplatePolicy` is Disabled; check the repository metadata (nuspec `RepositoryUrl`, npm `repository`) and warn; switch title correction on. | D7, D12 | Spark's own template and missing `RepositoryUrl`s in this PR. |
| M13 | **Docs:** a user guide `docs/code-coverage/changelog.md` and the in-app guide; D15 marked superseded where it changed in `coverage_package_versioning_PRD.md`. | all | |
| M14 | **Gate:** push, open the PR, watch every check, then ask the codecoverage MCP for patch coverage. | | Read failures from the log first. **In progress:** PR #704; see Status. |

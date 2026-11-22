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

S3, S6 and S7 are implementation-shaped and come after the grill. **NEXT:** the grill, from Q2.

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

## Milestones

To be written after the grill resumes (Q2–Q12). Tests run once, at the end (owner rules).

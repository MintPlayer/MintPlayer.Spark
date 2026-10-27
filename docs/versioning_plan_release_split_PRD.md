# Versioning: decide the versions during the build, push the tags after the tests

**Issue:** #552 · **Parent:** #153 ([coverage_package_versioning_PRD.md](coverage_package_versioning_PRD.md)) · **Follows:**
#544 ([versioning_msbuild_traps_PRD.md](versioning_msbuild_traps_PRD.md)) · **Plan:**
[versioning_plan_release_split_plan.md](versioning_plan_release_split_plan.md) · **one pull request**

> **Status 2026-10-27:** investigated and grilled (owner). The first draft is listed under "Superseded" at the end.
> It had a new engine API, a plan file, artifacts and stored plans, and the owner judged it "way too complicated".
> This version is the minimal PR that does what the owner described.
>
> **Implemented 2026-10-27** on `feat/issue552-versioning-plan-release` (D1–D8; D9 is the ng-bootstrap issue). S1's
> answer was no: `compile-ts-action` commits only to its branch, so the publish workflow tags the engine commit itself
> (plan file, S1).

## The goal (owner, 2026-10-27)

```
build job:   build → decide versions (no tags) → stamp 0.0.0-PLACEHOLDER → rebuild → upload dist   ┐ in parallel
tests:       ...                                                                                   ┘
publish job: needs [build, tests] → push the tags → push the packages
```

- **Tags only after the tests.** Owner: "Tags are pushed before the tests finish — I don't want that". A tag promises
  that the package ships (#544 D3).
- **Nothing runs in series that can overlap the tests.** Owner: "I got the ng-bootstrap repo's workflow run down from
  16 minutes to less than 5 minutes. So we cannot just run all steps in series." Running today's action after the
  tests, then rebuilding, would put a whole build after the tests.

**Observed:** all three demo sites of MintPlayer/mintplayer-ng-bootstrap show `0.0.0-PLACEHOLDER`. Its `build` job
builds the demos (`publish-master.yml:52-69`), and the action only runs later, in `publish-libs` (`:268`).
`publish-snippets` calls the action a second time with no `dist` (`:301`), so whichever caller runs first decides
every version, and without `dist` every API counts as changed (master run 40200311265).

### Why the split comes back after #544

#544 dropped a `push: false` split because "one step is enough once tagging happens after the tests"
(`versioning_msbuild_traps_PRD.md:339-340`). That holds only when nothing needs the version before the tests. Here,
the demo build does. The other rejection still stands: plan → publish → finalize, with tags only after a confirmed
publish (`:347-348`). Tags are still pushed before the packages, and a dangling tag is still recovered by re-running
(#544 D3).

## Evidence (investigation 2026-10-27)

Paths are under `apps/CodeCoverage/`.

- **The seam is the tag writer.** `ReleaseRun.RunAsync` (`CodeCoverage.Versioning/Releases/ReleaseRun.cs:30-84`)
  plans, builds the `TagToWrite` list (`:62-65`) and calls `ITagWriter.WriteAsync` (`:66`). Notes (`:69-73`) and
  branch cuts (`:76-79`) are computed for every tag the writer reports as `Created` or `AlreadyPresent`.
  - A writer that only records the tags and reports them as `Created` therefore yields the full result: versions,
    tags, annotations, notes and cuts, with nothing written. The engine needs no new entry point (D1).
- **Annotations are deterministic.** `TagAnnotation.Format` (`VersionTag.cs:63`) writes the title, `api-hash`,
  `Engine.Version` and the run, with no timestamp.
- **The writer is idempotent.** `TagWriter.cs:62-84`: a tag already on the same commit is `AlreadyPresent`, one on
  another commit is `Conflict`. A re-release after a partial push is safe.
- **Tag history** is one `git for-each-ref refs/tags` (`TagHistory.cs:26-42`). `DotnetApiListing.Hash`
  (`Api/DotnetApiListing.cs:109`) gives `sha256:` strings for the fingerprint (D3).
- **Action flow** (`versioning-action/src/main.ts:103-178`):
  1. Prepare the history.
  2. Read coverage.yml.
  3. CLI `manifest`.
  4. Server mode, falling back to local on 5xx (`:135-144`), otherwise CLI `run` (`:145`).
  5. `writeNpm` stamps source and dist (`:185-207`, idempotent).
  6. Local mode only: Releases (`src/releases.ts:26`) and branch cuts (`:49-55`).
- **Every call compiles the engine.** `src/engine.ts:285-296` runs `dotnet run --project …/CodeCoverage.Versioning.Cli`,
  about 34 s per call (issue #552, run 40200311265). The CLI is `net8.0` with `RollForward=Major`
  (`CodeCoverage.Versioning.Cli.csproj:10-11`), so a framework-dependent build runs on any hosted runner.
- **Server.** `POST api/versioning/run` (`CodeCoverage/PackageVersioning/VersioningController.cs:67`): OIDC plus
  `[SparkAuthorize("Upload","Coverage")]` (`:40-42`). It checks repository id and run id (`:74-77`), a branch ref
  (`:82`), token `sha` = manifest commit (`:84`), and the default or a release branch (`:93-107`). It calls the
  sidecar `/jobs/versioning/run` synchronously (`GitWriter/Endpoints/VersioningEndpoints.cs:15-54`). Tags are pushed
  with a short-lived App writer token (`VersioningController.cs:164,194`). It records a `VersioningRun`
  (`CodeCoverage.Library/Entities/VersioningRun.cs:27`), which feeds the commit graph's release status (#544 D5).
- **Data Protection** keys are persisted on the `dataprotection-keys` volume (`docker-compose.yml:303`). The app and
  the sidecar deploy together from one compose file (`:142`, `:167`, both `:master`).
- **Bundle gate.** The PR workflow rebuilds `dist` and fails on any byte difference
  (`.github/workflows/pull-request.yml:417-427`). `versioning-action-publish.yml` repacks and moves the
  `versioning-v<major>` tag through `compile-ts-action` (`:109-125`), and builds no .NET.

## Decisions

- **D1 — Plan = today's run with a recording tag writer** (owner, "minimal pull-request").
  - CLI `run --plan-only` runs `RunAsync` with a writer that records each `TagToWrite` and reports the new
    `TagOutcome.Planned` (`RecordingTagWriter`, `ReleaseRun.cs`), so notes and cuts are computed as for a written tag.
  - The output is today's `LocalRunResponse` plus `plan`: the commit, the run, the fingerprint (D3) and each tag's
    name and API hash. The annotation is rebuilt from these by the same `TagAnnotation.Format`.
  - No new engine API; `ReleaseRun.PlanAsync` (the trigger-B preview) keeps its name.
- **D2 — Release = write exactly the planned tags.**
  - CLI `push-tags --plan <file> [--remote]` writes the plan's tags through the existing `TagWriter`, which
    refuses on `Conflict`.
  - Notes and branch cuts come from the plan, never recomputed. Release decides nothing.
- **D3 — Stale plan: strict** (owner, Q1 = A). Release refuses with `stale-plan` when:
  - `GITHUB_SHA` (and HEAD) ≠ the plan's commit, or
  - the fingerprint differs. The fingerprint is sha256 over the sorted (name, tag-object sha, peeled sha) of every
    `<id>/v<ver>` tag, **excluding the plan's own tags**, so a re-release after a partial push is accepted.

  The error says to recover with "Re-run all jobs". "Re-run failed jobs" re-uses the old plan and stays stale.
  **Trap accepted:** a release-branch run that tags while master is between plan and release makes master's run red,
  even when its versions would not have changed.
  **The same applies to a hand-pushed major** for a free (`netstandard`/`net4x`) package (#153 D7: the user raises it
  by pushing `<id>/v5.0.0`; `VersionCalculator.cs:45` otherwise keeps the last tag's major). If the tag lands after
  the plan step, the release is refused as stale, and "Re-run all jobs" plans 5.0.0. A stale 4.x plan never ships.
  The documented order is: push the tag, then start or re-run the workflow.
- **D4 — Action `phase: all | plan | release`, default `all`, unchanged.**
  - `plan` stamps source and dist exactly as today, pushes nothing, and sets a `plan` output.
  - `release` takes a `plan` input, checks D3, pushes the tags, creates the Releases and cuts the branches (local
    mode, as today), then re-stamps through `writeNpm`. The tags are now reachable, so a job that never stamped (ng-bootstrap's
    `publish-snippets`) gets the same versions.
  - Existing outputs keep their meaning in every phase.
- **D10 — The plan stamps, and `all-versions` gives every package's version** (the issue's "Versions first, no tags";
  ng-bootstrap's analysis 2026-10-27).
  - The first implementation (`02d4fb3d`) made `plan` write nothing, and the README said so. ng-bootstrap's demos
    build between the plan and the tests and would still have shown `0.0.0-PLACEHOLDER`, and `versions` covers
    released packages only, so the consumer could not stamp them itself either.
  - `plan` now runs the same `writeNpm` a release runs. It needs no new tag: a release is stamped at its planned
    version, any other package at its last reachable tag, which a plan does not change.
  - The new `all-versions` output (every phase) maps every manifest package to its version: a release's own, else
    the last tag reachable from HEAD. A package with neither is absent. The plan output carries the package ids, so
    the release, which has no manifest, reports the same set.
  - Releasing one plan from two jobs (`publish-libs` and `publish-snippets`) is safe. The second finds its tags
    (`already-present`), and its Releases and cuts answer `exists` (`releases.ts`, 422 `already_exists` /
    "Reference already exists").
- **D5 — The plan travels as a job output** (`needs.build.outputs.plan`). It is a few KB, so it needs no artifact,
  no file and no new dependency. This supersedes the earlier "the action uploads an artifact" answer (Q3 = B), which
  was given before the plan shrank.
- **D6 — Server mode supports plan and release too** (owner, B). This keeps the App as the tagger and keeps the
  commit-graph release status (#544 D5).
  - `POST api/versioning/plan`: the same claim checks as `run`. The sidecar runs the recording-writer run
    (`/jobs/versioning/run` with `planOnly`) and the app returns the plan plus a **seal**: the whole answer with the
    repository id, run id, commit and branch, protected by a time-limited `IDataProtector` (purpose
    `CodeCoverage.Versioning.Plan.v1`, one day). It stores nothing (owner, Q2 = B).
  - `POST api/versioning/release` takes **only the seal**, so the client cannot change a version, a tag or a Release
    text. The app unseals it, then checks from the OIDC token: same repository id, **same run id** (any attempt),
    `sha` = the plan's commit, same branch. The sidecar's existing `/jobs/tags`, given the plan's `run` and
    `fingerprint`, checks D3 and writes the tags as the App. The app then creates the Releases and cuts and records
    the `VersioningRun`, as `run` does. A stale plan answers 409, which the action does not fall back on.
  - `workflow_ref` is not checked. A run id belongs to exactly one workflow, so it adds nothing, and it would
    break reusable workflows.
  - **Trap accepted:** a lost key ring between plan and release fails the release ("re-run all jobs"). The keys are
    on a persisted volume (Evidence).
- **D7 — Fallback.** A server plan that answers 5xx is planned locally (as today). A server release that answers 5xx
  releases the same plan locally, with the workflow token, after D3. A tampered local plan is no new risk, because
  local mode already pushes with that token.
- **D8 — A prebuilt engine ships with the action** (owner, A).
  - `versioning-action-publish.yml` runs `dotnet publish` (framework-dependent) and commits the output **only onto the
    `versioning-v1.x.y` tag commit**, never onto master, so the `dist` byte gate is untouched.
  - `engine.ts` runs `dotnet <dll>` when the build is present, and `dotnet run` otherwise (Spark's own `./apps/...`
    use, which must test the engine it ships).
  - Target: ≤ 5 s per call.
  - **Trap:** `compile-ts-action` may not be able to commit extra files on the tag commit. Then the PR adds a small
    publish step of its own (spike S1).
- **D9 — The ng-bootstrap change goes through an issue there** (memory `feedback_other_repos_via_issue`), carrying
  the consumer YAML below, after the action is released.

## Consumer shape (ng-bootstrap)

```yaml
build:
  permissions: { contents: read, id-token: write }
  outputs: { plan: ${{ steps.versioning.outputs.plan }} }
  steps:
    - build the libs                                  # API surface for the decision
    - id: versioning
      uses: …/versioning-action@versioning-v1
      with: { phase: plan }                           # decides + stamps, no tags; all-versions for anything else
    - build everything (demos see the real version)
    - upload dist
tests:            unchanged
publish-libs:     needs [build, tests] → download dist → versioning (phase: release, plan: needs.build.outputs.plan) → publish
docker:           needs [build, tests] → download dist (already stamped)
publish-snippets: needs [build, tests] → versioning (phase: release, same plan; tags AlreadyPresent, re-stamps) → publish
```

## Superseded (first draft, 2026-10-27)

- ~~`ReleaseRun.PrepareAsync`/`ReleaseAsync`, a `ReleasePlanFile` type, and renaming the preview to `PreviewAsync`.~~
  Not needed: the recording tag writer gives the plan from `RunAsync` unchanged (D1).
- ~~The server stores the plan on `VersioningRun` with a plan id.~~ Replaced by the seal (Q2 = B, D6).
- ~~The action uploads and downloads the plan as an artifact.~~ Replaced by a job output (D5).
- ~~A new `all-versions` output computed by the engine.~~ Not needed: `writeNpm` already stamps every package, and
  release re-stamps from the tags it just pushed (D4).
- ~~Run the unchanged action after the tests, then rebuild.~~ Rejected: it serializes a build after the tests and
  breaks the < 5 min run.
- ~~Run the unchanged action in the build job.~~ Rejected: it tags before the tests.

## Out of scope

- Testing server mode from a developer machine (a smee/WebSocket tunnel) is tracked separately.
- Plan → publish → finalize (#544, rejected).

# Plan — carry-forward + multi-step uploads for the coverage server

**Implements:** [`coverage_carryforward_PRD.md`](coverage_carryforward_PRD.md) Option C (+ C′) ·
**Status:** M1–M8 implemented on branch `coverage-carry-forward`, one commit per milestone (see *Implementation status* at the end); the dogfood checks that need the deployed server are listed there as post-merge steps

All code lands as **one pull request** in `MintPlayer.Spark` (server, action, workflows, docs, tests), per
`CLAUDE.md`. The five external consumers need no change: they pick the new action up through the
`coverage-upload-v1` tag once it is moved, and the server accepts their current payloads throughout.

`apps/CodeCoverage` is production. Every server milestone is deploy-safe on its own (additive
documents, old payloads accepted) so a half-landed branch can never show a wrong headline.

## Sequencing

```
S1..S4 (spikes, results recorded below)
  └─> M1 server: fileList v2 parsing + BlobOid on FileCoverage
        └─> M2 server: CommitAssembly + carry-forward (C), commit-level assembly across builds
              └─> M3 server: C′ fallback via compare API
              └─> M4 server: promotion/badge/status/check-run read the assembly; completeness verdict
                    └─> M5 action: OIDs in fileList, zero-report partial upload, new outputs, capability warning
                          └─> M6 UI: provenance, completeness, unfinished commits visible (T1.3)
                                └─> M7 dogfood in this repo's PR workflow + cross-run test
                                      └─> M8 docs + move `coverage-upload-v1`
```

M1–M4 can deploy before M5 exists: without OIDs the server behaves exactly as today except that the
commit headline becomes the union of all its builds (M2 step 2), which is strictly more correct.

---

## Spikes

### S1 — the OID invariant holds on real commits

Pick three merged Spark PRs (one touching only an Angular lib, one touching only a .NET test project,
one touching `package.json`). For each: check out the head, run
`npx nx affected -t test --base=<merge-base> --head=<head>` locally, glob the four report patterns,
extract the set of source paths from the reports, and compare with
`git diff --name-only <merge-base> <head>`.

Record: (a) changed files missing from the measured set, and whether each belongs to a project with
no coverage at all (acceptable) or to a covered project (a defect in the invariant — stop and rethink);
(b) count and share of files that would be carried; (c) `git ls-files -s | gzip | wc -c` for Spark and
for `mintplayer-ng-bootstrap`.

**Exit:** zero changed-and-covered files outside the measured set across all three; payload < 200 KB gz.

**Result (2026-09-02):** master is squash-merged, so merge-base = `sha^1` and head = `sha`. Four PRs
were checked at the project-graph level (`nx show projects --affected -t test`): `7ad2e306`
(ng-spark-auth republish), `b3b89f11` (#257, tests/ + libs/testing), `7d8e0a68` (#322, root
package.json + workflows), `aadd54cf` (#318, 40 files under ng-spark). **Nx never omitted a project
owning a changed file** — root `package.json`/`package-lock.json` changes mark all ten test projects
affected, `.csproj` bumps mark the dependent .NET test projects, `ng-spark-auth` is correctly left out
when only `ng-spark` changes. The npm side of `aadd54cf` was executed for real: all 16 changed
`ng-spark` sources appear in `cobertura-coverage.xml` (including `.scss`, which vitest instruments);
the misses are `.spec.ts`, `.html`, barrels, `package.json`, style partials and one deleted file —
all legitimately unmeasurable. Payload: `git ls-files -s | gzip` = 60 588 B for Spark (1 793 files),
96 144 B for ng-bootstrap (3 195 files); ≈ 30–34 B/file, well under the 200 KB bound. Measured share
of the npm-only run was 81 of 1 471 tracked files; the .NET reports would raise that, but most tracked
files (docs, tests, config, html) are never coverable, so the carried share stays high by design.

Two findings that are **not** Nx defects but shape the design:

1. **Neither vitest config sets `coverage.all: true`.** A shipped source imported by no spec
   (`libs/node_packages/ng-spark/routes/src/spark-routes.ts` today) is absent from every report. The
   OID gate keeps this honest — a changed file is never carried and an unchanged one was absent from
   the base too — but the reported total silently omits untested files. Fixed in M7 as part of the
   dogfood (`all: true` + `include` on both configs); it is a coverage-honesty bug independent of
   carry-forward.
2. **A crashing suite yields no report, and the upload step runs `if: always()`.** At `7ad2e306` the
   ng-spark-auth suite failed to even load and produced nothing. Today that project's files vanish
   from the number; with carry-forward they would be *carried from the base*, masking the crash behind
   a green-looking figure. The server cannot tell "affected but crashed" from "unaffected". So the
   action gains a `carry-forward` input that consumers wire to the test step's outcome
   (`${{ steps.test.outcome == 'success' }}`); when false the upload is still stored as measured but
   the assembly carries nothing and records `testsFailed`. Added to §5 of the PRD, M5 and M7.

**Exit met** at the Nx level; the two findings are folded into the plan rather than blocking it.

### S2 — assembly cost on the live server

Read-only RavenDB queries against `Coverage` on the production server (use the `dcg:ravendb` skill or
the studio; never write): for the latest master build of each of the six repos, count documents under
`{buildId}/files/` and sum their sizes; note the largest single `FileCoverage`.

Record per repo: files, bytes, and the projected per-commit assembly size for copy semantics.
Estimate 90 days of default-branch commits. If total > 2 GB, switch §5.2 to reference semantics
(assembled file = pointer to the origin build's `FileCoverage` + `BlobOid`), and note the read-path
cost that implies for the tree/file endpoints.

**Exit:** a number, and a decision (copy vs reference) written here.

**Result (2026-09-02, live server via the browse API, read-only):** headline of the latest default-branch
commit per repo — the assembly of a full commit holds exactly these files:

| Repo | files | coverable lines | branches |
|---|---|---|---|
| MintPlayer.Spark | 570 | 24 058 | 10 605 |
| mintplayer-ng-bootstrap | 1 241 | 25 326 | 16 473 |
| MintPlayer.Dotnet.Tools | 261 | 10 544 | 6 001 |
| MintPlayer.AI | 176 | 9 762 | 5 000 |
| CodeCoverage | 115 | 3 427 | 1 959 |
| MintPlayer.AspNetCore.Tools | 64 | 1 408 | 698 |
| MintPlayer.AspNetCore.SpaServices | 51 | 1 718 | 492 |

A `LineCoverage` serialises to ≈ 42 bytes and a `BranchCoverage` to ≈ 50 bytes of JSON, so a full
Spark assembly is ≈ 1.6 MB and one assembly of every repo together ≈ 5.3 MB — the same order as the
per-build `FileCoverage` documents the server already stores for each build, so copy semantics at most
doubles storage. Worst case of ten assembled commits per day across all repos for 90 days is
≈ 1.4 GB before PR-branch deletion at merge; the realistic figure (history endpoint shows 2–21 commits
per repo in its window) is an order of magnitude lower. **Decision: copy semantics.** Reference
semantics would save little and make every tree/file read a two-hop load.

### S3 — red test for cross-run last-finalize-wins

Integration test in `apps/CodeCoverage/CodeCoverage.Tests`: upload report A for `sha` with `runId=1`,
report B (disjoint files) with `runId=2`, finalize 1 then 2, assert `Commit.Coverage` — today it equals
B's totals alone. Then finalize in the other order and assert it equals A's. Keep both tests; M2 flips
their expectations to the union.

**Exit:** two failing tests committed, skipped with a reason pointing at M2.

**Result (2026-09-02):** `apps/CodeCoverage/CodeCoverage.Tests/Ingestion/CrossRunAssemblyTests.cs`,
built on the `CoverageRavenTest` harness and `ParseSessionRecipient` + `BuildFinalizer` exactly as
`FlagCoverageTests` does. With the Skip removed both fail as predicted:

```
Finalizing_run_1_then_run_2_yields_the_union_on_the_commit
  Expected commit.Coverage!.LinesCoverable to be 5, but found 2.
Finalizing_run_2_then_run_1_yields_the_union_on_the_commit
  Expected commit.Coverage!.LinesCoverable to be 5, but found 3.
```

The assertions already state the union (5 coverable, 3 covered, 2 files); M2 turns them green by
deleting the two `Skip` arguments, nothing else.

### S4 — C′ viability

Using `GitHubDiffService.CompareAsync` against a repo with the App installed, compare a base/head pair
with >300 changed files (GitHub's compare API caps the `files` array at 300). Confirm whether the
service pages or truncates. Decide: page (if the API allows) or treat `files.Count == 300` as
"unknown" and emit `noBlobIds` for that assembly.

**Exit:** decision recorded; if truncation is possible the guard is specified for M3.

**Result (2026-09-02):** GitHub's documentation for *Compare two commits* states that "the list of
changed files is only shown on the first page of results, and it includes up to 300 changed files for
the entire comparison" — paging (`per_page`/`page`) pages **commits**, never files. Truncation is
therefore unavoidable and undetectable beyond the cap. `GitHubDiffService.CompareAsync` already
returns `CommitComparison.Truncated = files.Count >= 300` and never pages. **Decision:** M3 treats
`Truncated == true` as "changed-file set unknown": no file is carried under C′, and the assembly's
completeness carries a `noBlobIds` reason. No new fetching code is needed.

---

## M1 — server: file list with blob OIDs

Files: `Ingestion/PathNormalizer.cs`, `Ingestion/ParseSessionRecipient.cs:42-66`, new
`Ingestion/HeadFileList.cs`, `Entities/FileCoverage.cs`.

1. `HeadFileList.Parse(string)`: sniff the first non-empty line; `^[0-9a-f]{40,64} ` ⇒ v2
   (`oid path`), else v1 (`path`). Returns `IReadOnlyDictionary<string, string?> PathToOid` (null OIDs
   for v1). Paths unified exactly as `PathNormalizer.Unify` does today.
2. `PathNormalizer` takes the parsed list; behaviour unchanged.
3. `FileCoverage` gains `string? BlobOid`. `ParseSessionRecipient` sets it from the head file list
   when the normalized path matched (`Matched == true`); unmatched files keep null.
4. `UploadsController` stores the file list attachment as today (no change to the form field name),
   and reads the optional `carryForward` form field (absent ⇒ true) onto the new `Build.CarryForward`.
5. Tests: v1 and v2 parsing; a v2 upload yields `BlobOid` on matched files; a v1 upload yields null.

**Deploy-safe:** yes — additive field, both formats accepted.

## M2 — server: `CommitAssembly` and carry-forward (Option C)

Files: new `Entities/CommitAssembly.cs`, new `Ingestion/CommitAssembler.cs`,
`Ingestion/BuildFinalizer.cs:34-40`, `Ingestion/FinalizeBuildRecipient.cs`, `Ingestion/CoverageMerger.cs`
(reuse, no semantic change), `Services/BaseResolver.cs` (read assemblies as usable bases).

1. Entities per PRD §5.2: `CommitAssembly` at `{commitId}/assembly`; assembled files at
   `{commitId}/assembly/files/{hash}` with `Origin { Kind, FromSha, FromBuildId, OriginSha }` and
   `BlobOid`; assembled tree at `{commitId}/assembly/tree`.
2. `CommitAssembler.AssembleAsync(commit)` implements PRD §5.3 steps 1–5:
   - collect finalized builds, highest attempt per `runId`;
   - measured set = max-merge across builds (stream `{buildId}/files/`, merge with
     `CoverageMerger.MergeInto` into a dictionary keyed by path);
   - base = `BaseResolver.ResolveAsync(repo, commit, declaredBaseSha)` where `declaredBaseSha` is the
     first-finalized build's; record `baseMismatch` if others differ. Skip carry-forward entirely when
     no contributing build is `Partial`;
   - carried set = for each head `(path, oid)` not measured: load base assembled file by path hash;
     copy when `oid != null && oid == baseFile.BlobOid`, with `Origin.Kind = Carried`,
     `FromSha = base.Sha`, `OriginSha = baseFile.Origin?.OriginSha ?? base.Sha`; else count as unmeasured;
   - write files (bulk insert if S2 says so), tree, totals, `OldestOriginSha`, completeness.
3. `BaseResolver.UsableBuildIdAsync` also accepts a commit with an assembly (the assembly is the
   preferred base; a bare finalized build remains acceptable for commits predating this work).
4. `BuildFinalizer.Finalize` stops writing `commit.Coverage` and instead enqueues
   `AssembleCommitMessage(commitId, buildId)` on the same strict-FIFO queue; a new
   `AssembleCommitRecipient` runs the assembler, saves, then publishes feedback for the triggering
   build. *(As built: no compare-exchange lock — the queue already serializes; and promotion to
   `Repository.LatestCoverage` moved into the assembler here rather than in M4, gated on
   `Completeness == Complete`.)*
5. Delete-at-merge (`DeletePullRequestBuildsRecipient`) also deletes the PR commit's assembly documents.
5b. `Build.CarryForward` (bool, default true; set in M1 from the `carryForward` form field). If any
   build of the commit has it false, the assembler skips the carry step entirely and adds
   `testsFailed` to `IncompleteReasons`; measured files are assembled as normal. Test: one build with
   `CarryForward = false` ⇒ zero carried files, reason present, measured totals intact.
5c. Δ stamping (PRD §5.7): after assembling, set `Commit.CoverageDeltaVsParent` from the assembly of
   `Commit.ParentSha` (git first parent; resolved via the GitHub commits API when the upload did not
   carry it) and `Commit.CoverageDeltaVsDefaultBranch` from the newest `Complete` default-branch
   assembly authored at or before this commit (index `Commits_ByRepository` gains `ParentSha` and an
   `AssemblyComplete` flag). Both null when no reference. Then re-stamp dependants: commits whose
   `ParentSha` equals this sha, and for a default-branch commit the commits that were stamped against
   the previous default-branch assembly. `UploadsController` stores whatever the action sends as `ParentSha` with
   `ParentShaSource = "upload"`; the assembler overwrites it with the API's answer
   (`ParentShaSource = "api"`) and trusts an upload-sourced value only on non-PR commits, because the
   old action sent its PR-base value on `pull_request` events only (PRD §5.7 *as built*). Tests: first
   default-branch commit ⇒ both null; second PR commit ⇒ vs-parent uses the first PR commit, vs-default
   uses master; the S3 scenario stamps once per finalize without drift.
6. Tests: S3's two tests flipped to the union; carry with matching OID; no carry on OID mismatch; no
   carry for a path absent from the head list; measured wins over carried; chain origin propagates
   (`OriginSha` survives two hops); full upload (no `Partial`) produces an assembly equal to the build.

**Deploy-safe:** yes — with no OIDs in flight nothing is carried; headlines become unions.

## M3 — server: C′ fallback via compare API

Files: `Ingestion/CommitAssembler.cs`, `Services/IGitHubDiffService.cs`.

1. When any unmeasured path has a null OID and the repo has an installation, call
   `CompareAsync(base, head)` once; carry paths that are unmeasured, present in both file lists and
   not in the changed set. Apply the S4 guard (paged or `noBlobIds` on truncation).
2. Without an installation: no carry for null-OID paths; `IncompleteReasons += noBlobIds`.
3. Tests with `ScriptedDiffService`: carry via C′; truncation guard; no-App path.

## M4 — server: everything reads the assembly

Files: `Ingestion/BuildFinalizer.cs:51-55`, `Ingestion/BuildComparer.cs`, `Ingestion/PartialComparison.cs`,
`Controllers/UploadsController.cs:248-311` (status), `Controllers/BrowseController.cs` (tree/hierarchy/
file/history), `Controllers/BadgeController.cs`, `Feedback/PublishFeedbackRecipient.cs`, `Indexes/`.

1. Promotion rule: `assembly.Completeness == Complete && (default-branch rule as today)`. Replace the
   `!build.Partial` guard.
2. `BuildComparer` compares assembled trees (head assembly vs base assembly); `PartialComparison`'s
   projection becomes redundant for OID-verified files — keep it only as the number shown for
   `unmeasured` remainder, labelled as such.
3. Status response gains `assembly { builds[], completeness, incompleteReasons[], measuredFiles,
   carriedFiles, unmeasuredFiles, oldestOriginSha, oldestOriginAge }` (documented in
   `docs/code-coverage/upload-api.md` and `upload-result-contract.md`; the existing `state` values are
   untouched — no fourth state).
4. Browse endpoints read `{commitId}/assembly/*`; per-file responses include `origin`. The
   `withCoverageOnly` default flips so unfinished/failed commits appear with their state (T1.3).
5. Check runs publish assembled numbers; the summary text names the carried share and the base.
6. Tests: promotion refused on `Partial`/`noBase`, allowed on `Complete`; status shape; badge equals
   assembled default-branch number; browse tree shows origin.

## M5 — action: OIDs, zero-report partial uploads, outputs

Files: `apps/CodeCoverage/action/src/main.ts:41-51,240-248`, `src/capabilities.ts`, `action.yml`,
`README.md` in the action folder, tests under `action/src/**/*.test.ts`.

1. Replace `git ls-files` with `git ls-files -s` and emit `oid path` per line (strip mode and stage;
   skip stage ≠ 0 entries; keep paths exactly as git prints them). Keep the form field `fileList`.
2. Zero report files **and** `partial: true` ⇒ upload a session with the file list only, then honour
   `finish`. Otherwise keep today's warning/error path.
3. Capability warning when `partial` is set and the server lacks `carry-forward`.
4. New outputs from the status response (PRD §5.5); `setResultOutputs` extended.
5. New input `carry-forward` (boolean, default `true`) posted as form field `carryForward`; documented
   with the `steps.test.outcome` idiom in the action README.
5b. `context.ts:36`: `parentSha` becomes the git first parent of the uploaded commit
   (`git rev-parse <commitSha>^1`, omitted when the history is shallow and the command fails) on every
   event, instead of the PR base sha on `pull_request` only. PRD §5.7.
6. `dist/index.js` rebuilt from `src/` with `npm run build` (ncc) and committed — the PR workflow's
   `coverage-action` job refuses a `dist/` that does not match `src/`, and the publish workflow
   rebuilds it again through `compile-ts-action` when the tag moves. Never edit `dist/` by hand.

## M6 — UI: provenance and completeness

Files: `apps/CodeCoverage/CodeCoverage/ClientApp/src/app/**` (commit page, file page, commit list).

1. Commit header: assembled %, measured/carried/unmeasured file counts, completeness pill with
   reasons, "carried from `<sha>` (oldest origin `<sha>`, N commits ago)".
2. Tree and file pages: origin marker per row; carried rows link to the origin commit.
3. Commit list: unfinished/failed commits visible with state (T1.3).
4. Vitest coverage for the new components; snapshot of the status → view-model mapping.
5. Δ columns (PRD §5.7): the existing `Δ` attribute is rebound to `CoverageDeltaVsBase` and a second
   attribute `Δ default branch` bound to `CoverageDeltaVsDefaultBranch`, both through the existing
   `coverage-delta` renderer, which already renders `null` as nothing — change that to `—` so an
   absent reference is visibly distinct from `0.0`. Model JSON in `App_Data` updated; the
   `[JsonIgnore]` on the old `CoverageDelta` and `StampCoverageDeltas` are deleted (see M2 step 5c).

## M7 — dogfood

1. `.github/workflows/pull-request.yml:190-209`: no input changes needed (already `partial: true`,
   `base-sha: ${{ env.NX_BASE }}`, `finish: true`); align the master `hashFiles` guard
   (`dotnet-build-master.yml:122`) with the PR guard's globs. Add `wait-for-finalize: true` on the PR
   upload so the run's summary shows the assembled number and completeness.
2. Open the implementation PR touching only an Angular lib file first (a comment) and verify exit
   criterion 1 against the live server *after* the server has deployed from master — sequence: land
   the server on master, deploy, then push the dogfood commit to the PR branch.
3. Cross-run test (exit criterion 2): after a PR run finished, upload a single Angular lib's report
   for the same head sha as a *second* build, by hand, straight through the upload API
   (`docs/code-coverage/upload-api.md`: create session with `partial=true` and the same `baseSha`,
   post one report, finish). Confirm the union. Decided in review: no probe workflow is committed
   for this — verification tooling does not live in the production repository.
4. Nothing-affected run (exit criterion 3): push an empty-ish commit (README typo) and check the
   assembly is 100% carried and `Complete`.
5. Wire `carry-forward: ${{ steps.test.outcome == 'success' }}` on the PR upload (give the test step
   an `id`), and set `coverage.all: true` with an `include` of `**/src/**/*.ts` in both
   `libs/node_packages/ng-spark/vitest.config.ts` and `ng-spark-auth/vitest.config.ts` (S1 finding 1).
   Expect the ng-spark number to drop slightly — that drop is the previously hidden untested files.

## M8 — docs and tag

1. `docs/code-coverage/upload-api.md`: `fileList` v2, zero-report partial uploads, `assembly` in
   status, `carry-forward` capability, `finish` = last uploader of *this run*.
2. `docs/code-coverage/coverage-analyzer-suite.md`: replace "no carryforward in any form" (§5) with a
   pointer to the PRD and the OID rule; update §4 Hazards.
3. `docs/code-coverage/roadmap-2026-08.md`: mark T1.3 done, note T1.4 unchanged.
4. `docs/code-coverage/README.md` index entries for the PRD/plan.
5. Move `coverage-upload-v1` to the new action build after the server is live (the action is
   backward compatible with the old server: an OID file list is just a file list to it, and the
   capability probe suppresses the carry-forward warning when absent).
6. Update memory notes for the project (assembly vocabulary, "partial ≠ carry-forward source").

---

## Verification sweep (run once, at the end)

- `dotnet test apps/CodeCoverage/CodeCoverage.Tests` and the action's vitest suite — green.
- `npx nx run-many -t build --projects=CodeCoverage` and the `ClientApp` `test` target — green.
- Exit criteria 1–6 from the PRD, each with the commit sha and status-endpoint JSON pasted here.
- Badges of all six consumer repos before and after deploy — unchanged for the four full-upload
  repos; Spark and ng-bootstrap master unchanged (they upload in full).

## Implementation status (2026-09-02)

| Milestone | Commit | Notes |
|---|---|---|
| Spikes + docs | `7ea6bd32` | S1–S4 results above; S3's two tests committed skipped |
| M1 | `ad7a1e59` | `HeadFileList` v1/v2, `FileCoverage.BlobOid`, `Build.CarryForward` |
| M2 | `b04cb8b8` | `CommitAssembly` + `CommitAssembler` + `AssembleCommitRecipient` on the parse queue (FIFO ⇒ no compare-exchange lock needed, a simplification of step 4); promotion moved into the assembler; S3 tests green; Δ stamping (5c) |
| M3 | `49f4b103` | compare-API fallback; `Truncated` ⇒ `noBlobIds` |
| M4 | `707bddd9` | status `assembly{}`, browse reads `{commitId}/assembly/*`, gate judges the assembly, `withCoverageOnly` default false, upload-api.md |
| M5 | `de05c991` | action: `git ls-files -s`, git first parent, `carry-forward`, zero-report partial upload, `assembly-*` outputs; server accepts zero-report partial uploads and advertises `carry-forward` |
| Backfill (owner request) | `7eeaa756` | `BackfillCommitDeltasCronJob`: verifies parents via the GitHub commits API and stamps Δ for pre-existing commits, 4 per 5 min (48/h, under the anonymous GitHub limit, one pace for every repository), drains and goes quiet. REST rather than GraphQL: Octokit.GraphQL's typed DSL cannot alias N `object(oid:)` lookups in one query, and the total volume is a few hundred calls once |
| M6 | `bf8b2d9f` | provenance in the Files card, `Δ parent` + `Δ base branch` columns, `—` for no reference, model re-synchronized |
| M7 | `8def5e3b` | PR workflow: no hashFiles gate, `carry-forward` from the test step, `wait-for-finalize`; master guard aligned; vitest `all: true` |
| M8 | this commit | docs, README index, roadmap T1.3, memory; `dist/index.js` rebuilt |

**Verified locally:** `CodeCoverage.Tests` full suite, the action's vitest + bundle suites, the SPA's
unit tests and a development build, `--spark-verify-model` in sync, ng-spark/ng-spark-auth vitest with
`all: true` (ng-spark drops to 82.8% lines; `routes/src/spark-routes.ts` now shows at 0%, the
previously hidden file from S1).

**Post-merge (needs the deployed server), in order:**

1. Merge; let `code-coverage-deploy` ship the server; let `coverage-action-publish` move
   `coverage-upload-v1` (M8 step 5 — the action is backward compatible with the old server).
2. Exit criterion 1: push a comment-only change under `libs/node_packages/ng-spark-auth` on a PR;
   expect `assembly-completeness: Complete`, measured = that lib, everything else carried.
3. Exit criterion 2: upload one Angular lib's report for that PR head by hand through the upload
   API as a second build (see dogfood step 3 above); expect `assembly.builds` to list both and
   the headline to be the union.
4. Exit criterion 3: a README-only PR; expect 100% carried, `Complete`.
5. Exit criterion 5: compare the six repos' badges before and after — unchanged.
6. Exit criterion 7: the CodeCoverage repository page; `7fc84af` shows `—` in both Δ columns once the
   backfill has run (4 commits every 5 minutes).
7. Watch the backfill log line drain to nothing; then it can be deleted in a later change or left —
   it costs one index query every five minutes.

## Decisions

- **Carry by file with OID verification, not by flag** — PRD §4.
- **Carry source is the declared `base-sha`, not the git parent** — PRD §3.3; the parent is not what
  `nx affected` compared against.
- **Assembly is per commit, builds stay per run** — a build remains "what one run uploaded", which is
  what retries, attempts and debugging need; the commit is the unit that has a coverage number.
- **Measured always beats carried; never max-merge across the boundary** — PRD §6.
- **Copy vs reference for assembled files** — **copy** (S2: ≈ 1.6 MB per full Spark assembly, same
  order as the per-build documents already stored).
- **Contract stays at 1** — everything is additive and sniffable.
- **No compare-exchange lock** — `AssembleCommitMessage` is on the strict-FIFO parse queue, which
  already serializes parse → finalize → assemble; the lock in the draft of M2 step 4 was unnecessary.
- **Δ columns reference the git parent and the default branch, never the Nx `base-sha`** — PRD §5.7.
- **Backfill uses REST, not GraphQL** — Octokit.GraphQL's typed DSL cannot alias N `object(oid:)`
  lookups in one query, the total volume is a few hundred calls once, and REST also has an anonymous
  path for repositories without the App.
- **Zero-report partial uploads are legitimate** — the file list alone lets the server carry the whole
  commit forward; the PR workflow therefore has no `hashFiles` gate.

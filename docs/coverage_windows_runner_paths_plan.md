# Development Plan: Windows-runner path resilience for the coverage action

**Issue**: none yet (raised from `MintPlayer/MintPlayer.DotnetDesktop.Tools` PR #17)
**Title**: A consumer on a windows runner uploads successfully and gets an empty report
**Type**: Bug / Enhancement
**Priority**: High (a production consumer is carrying a workaround)

## Executive Summary

Make `apps/CodeCoverage/action` work from a `windows-latest` runner without the consumer doing
anything about path separators, and make a report that resolves to nothing say so instead of going
green. The reported cause — backslashes — is refuted by measurement on both ends, so the plan opens
with a spike that names the real defect before any fix is designed on top of it.

---

## Problem Statement

### Current Behavior

`MintPlayer.DotnetDesktop.Tools` runs `_build.yml` on `runs-on: windows-latest` (the only such repo of
the nine that call the action). Tests pass, `hashFiles('coverage/*.cobertura.xml')` is non-empty, the
action uploads, the server accepts, a build is created, sessions parse cleanly — and the report page
is empty. Nothing is red anywhere. The consumer diagnosed absolute Windows paths and added a
PowerShell step (`tools/Rebase-CoveragePaths.ps1`) rewriting every `filename="…"` to a
repository-relative forward-slash path before the upload.

### Expected Behavior

A consumer passes `files: coverage/*.cobertura.xml` from any runner OS and gets a correct report. If
the server cannot resolve the report's paths against the repository, the action says so — loudly in
the log, and as a step failure under `fail-ci-if-error: true`.

### Impact

- One production consumer carries an 86-line PowerShell workaround for a defect that belongs to us.
- The failure mode is silent, so the next windows consumer loses the same afternoon.
- `apps/CodeCoverage` is production (coverage.mintplayer.com) and the action ships via a **moving
  tag** (`coverage-upload-v1`), so any change reaches all nine consumer repos at once.

---

## Technical Analysis

### What was measured (and what it rules out)

- `@actions/glob` 0.5.1 rewrites `\`→`/` on `win32` before minimatch
  (`node_modules/@actions/glob/lib/internal-pattern.js:103`) and sets `nocase: IS_WINDOWS` (:98). Six
  pattern shapes, including the exact `path.join`/`'!' + path.join` forms from `files.ts:53-54`, were
  run against a real Windows tree — all matched. **Glob discovery is not the defect.**
- The discovered path is not a wire key: only `path.basename(file) + '.gz'` is sent
  (`action/src/main.ts:97`), then sanitised server-side (`Ingestion/UploadAttachments.cs:6-13`).
- `Ingestion/PathNormalizer.cs:77` unifies `\`→`/` and applies it to `rootDir` (:19), `<source>` roots
  (:20), the file list (:21) and every raw path (:27); :30-31 then strips the root prefix
  `OrdinalIgnoreCase` — the same operation as the consumer's script. :74-75 already detects `X:`.
  **Server-side separator handling is not the defect either.**
- Therefore the empty report has a cause not yet identified. Candidates, to be discriminated in
  Phase 1: a null/empty `BuildSession.RootDir`; an empty/failed `git ls-files` on Windows flipping
  `PathNormalizer.cs:47-48` to a bare `!stillAbsolute` verdict; a path shape that prefixes neither the
  root nor any `<source>` (different drive, resolved symlink, `8.3` short path, or the first-match
  source-root pick at :34-41 choosing wrong); or a failure upstream of normalisation entirely.

### The silence mechanism (independent of the above, and fixable now)

`ParseSessionRecipient.cs:180` — `build.Coverage = CoverageMerger.Summarize(files.Where(f => f.Matched))`.
Unmatched files are retained but excluded from the headline, so 100% unmatched ⇒ a valid build with
empty coverage and no error anywhere. The signal already exists and already reaches the action:
`Ingestion/BuildComparer.cs:55-56` adds `"unmatchedPaths"` to `UploadStatusProjection.IncompleteReasons`,
returned in `UploadStatusResponse` and already typed in `action/src/status.ts`. **Nothing consumes it.**

### Files to Modify

| File | Change |
|---|---|
| `apps/CodeCoverage/action/src/context.ts` | `toPosixPath` helper; keep `rootDir` native for local I/O |
| `apps/CodeCoverage/action/src/main.ts` | posix `rootDir` on the wire (:91), log line (:59); unmatched branching (~:171-185, :247) |
| `apps/CodeCoverage/action/src/status.ts` | `UploadStatus` gains the unmatched fields |
| `apps/CodeCoverage/action/src/rebase.ts` *(new, gated)* | rewrite report-internal absolute paths pre-gzip |
| `apps/CodeCoverage/action/src/{context,outputs,files}.test.ts` | Windows-shaped cases |
| `apps/CodeCoverage/action/README.md` | the path contract |
| `apps/CodeCoverage/CodeCoverage/Controllers/UploadsController.cs` | `UnmatchedFiles` + `UnmatchedSample` on `UploadStatusResponse` (:407) |
| `apps/CodeCoverage/CodeCoverage.Tests/Ingestion/ParseSessionRecipientTests.cs` | Windows end-to-end fixture |
| `MintPlayer.DotnetDesktop.Tools` `_build.yml`, `tools/Rebase-CoveragePaths.ps1` | delete the workaround |

### Dependencies

- Phase 2–5 all depend on Phase 1's verdict; Phase 5's optional rebase depends on it explicitly.
- Phase 4 (action branching) depends on Phase 2 (server fields) being deployed to
  coverage.mintplayer.com — the action must tolerate a server that predates the fields (the existing
  `warnAboutUnsupportedInputs` / capabilities pattern at `main.ts:31-35` is the precedent).
- Phase 6 depends on the `coverage-upload-v1` tag having moved.

### Architecture Considerations

See the PRD's **Chosen Design**. Two load-bearing points: the fix belongs in the action (the only
layer that knows the runner's native form, and the only one a consumer's pinned tag actually
receives); and `toPosixPath` is a **wire-form** conversion only — applying it to the globber's search
base or to `fs`/`git -C` arguments conflates native and wire roles and will read as a bug later.

---

## Implementation Plan

### Phase 1: Spike — name the defect (M0)

1. Pull a real cobertura artifact from a `MintPlayer.DotnetDesktop.Tools` windows run (or reproduce
   locally via `Microsoft.Testing.Extensions.CodeCoverage`). Record the `<sources>` block and two
   `filename` attributes verbatim into the PRD.
2. Recover the `rootDir` and `fileList` that run actually sent, from the step log. If absent, add a
   `core.debug` and re-run — do not infer them.
3. Drive the exact `(rootDir, sourceRoots, fileList, rawPath)` tuple through `PathNormalizer` in a
   throwaway test; record `(Path, Matched)`.
4. Write the verdict into the PRD's `**M0 verdict:**` placeholder and decide FR-9 (Open Question 1).
   If none of the four candidates fits, **stop and re-plan** — Phases 2-6 assume a path-resolution defect.

### Phase 2: Canonical posix wire form (M1)

5. Add `toPosixPath` with the native-vs-wire comment; export from `context.ts` (or a new `paths.ts`).
6. Apply at `main.ts:91` and the `path.relative` log at `main.ts:59`. Leave `ctx.rootDir` native.
7. `context.test.ts`: Windows-shaped `GITHUB_WORKSPACE` (`D:\a\repo\repo`) asserting the posix wire
   form — the only existing `rootDir` assertions (:29, :150) are POSIX-shaped.

### Phase 2b: Action — absorb the rebase (M1b) — *the phase that retires the consumer scripts*

This is the governing requirement (FR-0): **no consumer repository carries path-rebasing code.** It
is not gated on Phase 1, because it must hold whatever Phase 1 names.

7a. `src/rebase.ts`: unify separators, then strip the `GITHUB_WORKSPACE` prefix case-insensitively,
    rewriting report-internal paths to workspace-relative posix before gzip. Idempotent.
7b. Handle the path-bearing token per format — cobertura/clover `filename="…"`, lcov `SF:…`, jacoco
    `sourcefile="…"`. Recognise the token, do not parse coverage semantics.
7c. Leave already-repo-relative paths untouched; count and log rewrites; do **not** hard-fail on a
    zero-rewrite run (unlike the consumer script — a relative report legitimately rewrites nothing).
    The loud failure lives in Phase 4 instead.
7d. Tests: per format, idempotence, a Windows-absolute fixture, and a relative-input no-op.

### Phase 3: Server — surface and prove (M2, M3)

8. `UploadStatusResponse` (`UploadsController.cs:407`) gains `UnmatchedFiles` (count) and a bounded
   `UnmatchedSample`, computed as `BrowseController.cs:366-369` already does, reusing
   `UnmatchedSampleSize = 50`.
9. Mirror both fields in `action/src/status.ts`'s `UploadStatus` (optional-typed, for older servers).
10. Server test: a build with unmatched files populates both fields.
11. Add a cobertura fixture with `D:\a\repo\repo\…` absolute filenames and a Windows `<source>`, using
    the literals captured in step 1.
12. Extend `ParseSessionRecipientTests.cs` to drive it with a Windows `RootDir` and a forward-slash
    file list, asserting a matched `FileCoverage` and its document id (FR-8).
13. Add the empty-file-list variant — the `PathNormalizer.cs:47-48` branch that turns an unstripped
    absolute path into `Matched: false`.

### Phase 4: Action — make the silence loud (M4)

14. In the finalize-wait path (`main.ts:171-185`, status handling near :247), warn with count and
    sample when unmatched > 0.
15. Fail under `fail-ci-if-error: true` when report files were uploaded and **every** file is
    unmatched. Explicitly exempt the zero-report-file carry-forward path (`main.ts:49-56`).
16. Distinguish matched from parsed in the `files-count` output and the summary log (FR-11).
17. Tolerate a server without the new fields (undefined ⇒ no verdict, no warning).
18. Tests: some unmatched; all unmatched with the flag on; all unmatched with the flag off; the
    carry-forward exemption; the old-server case.

### Phase 5: Optional in-action rebase + docs (M5)

19. `apps/CodeCoverage/action/README.md`: the path contract — the action rebases report paths to
    workspace-relative posix itself, separators and workspace prefixes are never the consumer's
    problem, and unmatched files are reported.

### Phase 6: Ship and retire the workaround (M5)

20. Move the `coverage-upload-v1` tag.
21. In `MintPlayer/MintPlayer.DotnetDesktop.Tools`: delete `tools/Rebase-CoveragePaths.ps1` and the
    "Rebase coverage paths to repository-relative" step at `_build.yml:102`.
22. Re-run that repo's pipeline; confirm a non-empty report with a plausible percentage (FR-3).
23. In `MintPlayer/mintplayer-ng-seo`: delete `tools/scripts/rebase-lcov-paths.mjs` and its
    pre-upload step; re-run and confirm the report still resolves. Its cause is suffix **ambiguity**
    (`PathNormalizer.cs:64` wants exactly one candidate), not separators — so verify rather than
    assume. If the ambiguity survives, keep the script and file it as its own issue.
24. Grep the org for any other pre-upload path-massaging step. FR-0 is not met while one survives.

> Per the one-PR rule in `CLAUDE.md`, steps 20-24 land in the **same unit of work** as the rest —
> the consumer-repo changes are other repositories, not follow-up PRs, sequenced after the tag move.

---

## Test Scenarios

### Scenario 1: Windows consumer, no workaround
- **Given**: a repo on `runs-on: windows-latest` producing cobertura with `D:\a\repo\repo\…` filenames,
  and no path-massaging step
- **When**: the action runs with `files: coverage/*.cobertura.xml`
- **Then**: the report page is non-empty and the percentage matches the same code built on ubuntu

### Scenario 2: Unmatched files are reported
- **Given**: a report whose paths resolve to nothing in the repository
- **When**: the action waits for finalize
- **Then**: the log warns with the unmatched count and a sample of paths

### Scenario 3: Wholly-unmatched build fails on request
- **Given**: Scenario 2 plus `fail-ci-if-error: true` and at least one uploaded report file
- **When**: the build finalizes with every file unmatched
- **Then**: the step fails, naming the count

### Scenario 4: Carry-forward is not mistaken for failure
- **Given**: zero report files (every project cached/unaffected under `nx affected`) and
  `fail-ci-if-error: true`
- **When**: the file-list-only upload finalizes
- **Then**: the step succeeds and no unmatched warning is emitted

### Scenario 5: Posix wire form regardless of host
- **Given**: `GITHUB_WORKSPACE=D:\a\repo\repo`
- **When**: the context is built and the form assembled
- **Then**: the `rootDir` field is `D:/a/repo/repo`, on Linux and Windows alike

### Scenario 6: Older server
- **Given**: a server whose status response lacks the unmatched fields
- **When**: the action waits for finalize
- **Then**: no warning, no failure, no crash

---

## Acceptance Criteria

- [ ] **FR-0 met: no MintPlayer repository contains a pre-upload path-rebasing step or script.**
      This is the one that decides whether the work succeeded.
- [ ] PRD M0 verdict is complete — the cause is named, not merely narrowed
- [ ] FR-1 … FR-11 met (PRD)
- [ ] All six test scenarios covered by automated tests
- [ ] `tools/Rebase-CoveragePaths.ps1` and its `_build.yml` step are gone from the consumer repo, and
      the post-fix run shows a non-empty report
- [ ] No new `action.yml` input was added (the consumer's surface is unchanged)
- [ ] Version bumps follow `CLAUDE.md`: the action's major tracks nothing here — this is a tag move,
      not an npm/NuGet publish; confirm nothing in `apps/CodeCoverage` publishes a package in this diff

---

## Build & Test Commands

```bash
# Action (from the repo root — npm install is root-only)
npx nx run code-coverage-action:build   > "$SCRATCH/action-build.log" 2>&1; echo "EXIT: $?"
npx nx run code-coverage-action:test    > "$SCRATCH/action-test.log"  2>&1; echo "EXIT: $?"
# The bundle suite is excluded from the default vitest run:
npm --prefix apps/CodeCoverage/action run test:bundle > "$SCRATCH/bundle.log" 2>&1; echo "EXIT: $?"

# Server
dotnet build apps/CodeCoverage/CodeCoverage/CodeCoverage.csproj -c Debug   > "$SCRATCH/build.log" 2>&1; echo "EXIT: $?"
dotnet test  apps/CodeCoverage/CodeCoverage.Tests/CodeCoverage.Tests.csproj -c Debug > "$SCRATCH/test.log" 2>&1; echo "EXIT: $?"
```

> Redirect, never pipe — a piped run reports `grep`'s exit code, not the build's. Batch the full
> sweep to the end rather than running it per phase; verify intermediate phases by reading the code
> and type-checking.

---

## Related Files

- `apps/CodeCoverage/action/src/{context,files,main,status,filelist}.ts`, `action.yml`, `README.md`
- `apps/CodeCoverage/action/src/{context,files,filelist,outputs,bundle}.test.ts`, `vitest.config.ts`
- `apps/CodeCoverage/CodeCoverage/Ingestion/{PathNormalizer,ParseSessionRecipient,HeadFileList,BuildComparer,CoverageMerger}.cs`
- `apps/CodeCoverage/CodeCoverage/Controllers/{UploadsController,BrowseController}.cs`
- `apps/CodeCoverage/CodeCoverage.Library/Entities/{FileCoverage,BuildSession}.cs`
- `apps/CodeCoverage/CodeCoverage.Tests/Ingestion/{PathNormalizerTests,ParseSessionRecipientTests,HeadFileListTests}.cs`
- `docs/coverage_windows_runner_paths_PRD.md`
- Consumer: `MintPlayer/MintPlayer.DotnetDesktop.Tools` `_build.yml`, `tools/Rebase-CoveragePaths.ps1`

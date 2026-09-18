# Development Plan: Harden the coverage upload ingest path

**Issue**: [#417](https://github.com/MintPlayer/MintPlayer.Spark/issues/417)
**Type**: Bug / Hardening
**Priority**: High — a production consumer's CI is deliberately red pending this fix
**Status**: Draft
**Created**: 2026-09-18
**Last Updated**: 2026-09-18

Companion to [`coverage_ingest_hardening_PRD.md`](coverage_ingest_hardening_PRD.md).
Predecessor: [`coverage_windows_runner_paths_PRD.md`](coverage_windows_runner_paths_PRD.md) + plan,
whose M0 closed with "nothing in this repository reproduces the failure" — #417 is what that verdict
turned into once the consumer found the BOM.

---

## Executive Summary

Uploaded report bytes are decoded to a `string` at one place — `ParseSessionRecipient.cs:202`,
`Encoding.UTF8.GetString(bytes)` — and every parser takes a `string`. A UTF-8 BOM therefore reaches
`XDocument.Parse` as document content and throws, or defeats lcov's `SF:` sniff, depending on format.
The throw is not isolated per file, so one bad report fails the whole session; `build.Coverage` is
never written on that path, so the status response says `coverage: null`, so the action's
`files-count` output is the empty string rather than `0`, so a consumer guard testing `== "0"` never
fires — and the build is accepted, finalized and green with zero files measured.

Six edits at that chokepoint plus its immediate surroundings fix the whole class. The plan sequences a
one-line stopgap first (the consumer is red *now*), then the real interface change, then the parts
that make the silence structurally impossible.

Two spikes go in front: **M0** reproduces the BOM failure before anything is fixed, and **M0b**
answers the separate question of whether a backslash-path database migration is needed — with a query,
not an argument.

---

## Problem Statement

### Current Behavior

1. `POST /api/uploads` streams each `IFormFile` straight into a Raven attachment
   (`UploadsController.cs:181-187`) and returns `202 Accepted` (`:204`) before anything is parsed.
2. The async worker reads each attachment, gunzips it, and decodes it with
   `Encoding.UTF8.GetString` (`ParseSessionRecipient.cs:183-203`).
3. `CoverageParserFactory.Resolve(content)` sniffs the **string** across three parsers in order
   (`CoverageParserFactory.cs:19-27`).
4. `parser.Parse(content)` at `ParseSessionRecipient.cs:64` is inside the attachment loop but outside
   any per-file `try`. A throw escapes to `:141`, fails the session, and discards every report that
   had already merged — `SaveChangesAsync` at `:137` is never reached.
5. `RecomputeBuildSummary` (`:168-181`) therefore never runs; `build.Coverage` stays `null`.
6. `ClassifyState` (`Entities/Build.cs:146-156`) returns `CompleteWithErrors`, which a consumer sees
   only under `wait-for-finalize: true` (default `false`, `action.yml:54-57`).
7. `files-count` is set from `coverage.filesCount` (`action/src/main.ts:318`) and comes back empty.

### Expected Behavior

A BOM, a UTF-16 encoding, leading blank lines and CRLF all parse. An empty, truncated, oversized or
unrecognised report is rejected **by name, with a reason**, without taking its siblings down. Every
terminal build carries a `CoverageSummary` — zeroed, never absent — so `files-count` is `0` rather
than `''`. `CompleteWithErrors` produces a workflow annotation without the consumer opting in. A
crafted report resolves no external entity and cannot exhaust memory.

### Impact

- `MintPlayer/MintPlayer.DotnetDesktop.Tools` records no coverage at all and its CI is **red on
  purpose** until this ships — the consumer removed their workaround deliberately rather than carry it.
- Any consumer adopting `Microsoft.Testing.Extensions.CodeCoverage` hits this the same way. That
  collector is not exotic: xUnit v3 runs on Microsoft.Testing.Platform, and MTP dropped its VSTest
  bridge on the .NET 10 SDK, so coverlet's data collector cannot attach at all.
- Clover and `coverage-final.json` are discovered and uploaded by the action today
  (`action/src/files.ts:12, :14`) and have no server-side parser — the same silent zero, already live.

---

## Technical Analysis

### What was measured, and what that rules out

| Claim | Verdict |
|---|---|
| Backslash separators drop the files | **Refuted** (#415, twice). `PathNormalizer` unifies before every comparison; the A/B resolved all 79 files with the same absolute backslash paths once the BOM was gone. |
| A missing `<sources>` element drops the files | **Refuted** by the same A/B; the report shape was identical across both runs. |
| A UTF-8 BOM drops the files | **Confirmed**, controlled A/B: BOM present → `CompleteWithErrors`, `files-count` empty; BOM stripped → `Complete`, `files-count: 79`, `line-rate: 56.9`. |
| The action's fail-on-zero guard counts its own rebases, not the server's files | **Already fixed** by PR #416. `reportUnmatched` (`main.ts:283-303`) branches on `status.unmatched`; the rebase count is an info log only (`main.ts:118`). Do not re-implement. |

### The silence mechanism, in four places

Each is independently sufficient to hide a total failure, so each needs its own fix:

| # | Where | Effect |
|---|---|---|
| 1 | `ParseSessionRecipient.cs:64` — `Parse` not wrapped per file | One bad report fails the session and discards the good ones |
| 2 | `:138` — `RecomputeBuildSummary` unreachable after a throw | `build.Coverage` null ⇒ `files-count` empty, not `0` |
| 3 | `:53`, `:60` — missing attachment / unrecognised format | `LogWarning` + `continue`; never leaves the server |
| 4 | `action.yml:54-57` — `wait-for-finalize` defaults false | `CompleteWithErrors` is invisible to a consumer who did not opt in |

### Files to Modify

| File | Change |
|---|---|
| `apps/CodeCoverage/CodeCoverage/Ingestion/ParseSessionRecipient.cs` | `ReadAttachmentText` → bytes (`:183-203`); per-file `try` around `:64`; outcome records for `:53`, `:60`; bounded gunzip (`:194-200`) |
| `.../Ingestion/Parsing/ICoverageParser.cs` | `CanParse`/`Parse` take bytes, not `string` |
| `.../Ingestion/Parsing/CoberturaParser.cs` | `:25` → `XDocument.Load(XmlReader.Create(stream, hardened))`; byte-level sniff at `:17-21` |
| `.../Ingestion/Parsing/JaCoCoParser.cs` | `:30` same; byte-level sniff at `:17-26` |
| `.../Ingestion/Parsing/LcovParser.cs` | BOM-aware decode before the `SF:`/`TN:` sniff (`:19-20`) |
| `.../Ingestion/Parsing/CoverageParserFactory.cs` | Rewind between sniffs (`:26-27`); order preserved |
| `.../Ingestion/PathNormalizer.cs` | As-received before unified (`:27`); FR-12 |
| `.../Ingestion/BuildFinalizer.cs` | Always write a `CoverageSummary` on finalize (`:14-46`) |
| `.../Controllers/UploadsController.cs` | Per-session outcomes on `UploadStatusSession` (`:505-506`); capabilities flag (`:60, :68-77`); input bounds (`:44, :91`) |
| `.../CodeCoverage.Library/Entities/BuildSession.cs` | Per-file ingest outcome records |
| `.../CodeCoverage.Library/Entities/FileCoverage.cs` | `RawPath` for diagnosis (FR-13) — **addition, `Path` and the hash identity unchanged** |
| `apps/CodeCoverage/action/src/main.ts` | `files-count: 0` on terminal (`:318`); `CompleteWithErrors` annotation without `wait-for-finalize` |
| `apps/CodeCoverage/action/src/status.ts` | Per-file outcome types on `UploadStatus` |
| `apps/CodeCoverage/CodeCoverage/ClientApp/...` | Zero-measured-files build renders as a named rejection, not a blank page |
| `docs/code-coverage/upload-api.md` | The narrowed `coverage` contract and the outcome shape |

### Dependencies

- Nothing external. No new NuGet or npm package: `XmlReader`/`XmlReaderSettings` are BCL.
- The action half ships by moving `coverage-upload-v1`, which reaches all nine consumers at once.
- Contract changes are advertised through the existing `capabilities` endpoint rather than a version bump.

### Architecture Considerations

- **One boundary.** `ReadAttachmentText` is the only `Encoding.UTF8.GetString` on the ingest path and
  already owns the other byte-level concern (gzip magic detection). That is why the fix is affordable
  and why a per-format BOM strip would be the wrong shape.
- **Sniff order is load-bearing.** lcov is sniffed first (`CoverageParserFactory.cs:19-24`) and
  Cobertura claims any root element named `coverage` — which Clover also uses. A byte-level sniff
  must rewind, or lcov's read consumes what Cobertura's needs.
- **Re-parsing is already idempotent.** Document ids are deterministic and `CoverageMerger.MergeInto`
  is max-based, so a redelivered `ParseSessionMessage` is safe. That is what makes FR-16 cheap.
- **The path is the document id.** `FileCoverage.DocumentId` = `{buildId}/files/{SHA256(path)[..20]}`.
  Nothing in this plan may change what `Normalize` returns for an input that resolves today, or every
  historical document becomes unreachable. FR-12 changes only *which* paths resolve, never the
  resolved form of one that already did — that is what the M6 fixtures pin.

---

## Implementation Plan

### Phase 0: Spike — reproduce before fixing (M0)

1. Obtain a BOM-carrying report from `Microsoft.Testing.Extensions.CodeCoverage` 18.11.2 — the
   consumer offered one; otherwise generate locally. Commit it **byte-level**, BOM intact.
2. Write a failing test proving today's pipeline reports exactly *"Data at the root level is invalid.
   Line 1, position 1."* Reproduce before fixing; that is the standing lesson of #415.
3. Byte-sniff the rest of the issue's fixture table (UTF-16 LE, leading `\n\n`, 0 bytes, truncated,
   lcov+BOM) and record which of the three parsers each reaches today.

### Phase 0b: Spike — is there a migration to do? (M0b) — ✅ CLOSED 2026-09-18, **no migration**

4. ✅ Scanned production directly. Streamed **collection** queries with a field projection, so no
   index was needed, no auto-index was created, and nothing on the server changed.
   **194,548 `FileCoverages` paths and 193,420 `BuildTreeSummaries` paths — 0 backslashes.**
   `CommitAssemblies` holds counters; its per-file documents live in `FileCoverages` and are covered
   by that count. The 194,548 equals the collection's document count from `/collections/stats`, so
   the scan was complete rather than sampled.
5. ✅ Verdict recorded in the PRD under *The migration question*, with the date and the counts.
6. ✅ Decision rule applied: zero hits ⇒ **no migration**. The re-key design stays written down in
   case a future defect reintroduces the possibility; nothing is built.
7. ✅ `BuildSession.RootDir` stays raw: 318 values, exactly one with a backslash, and that one is the
   #415 upload itself — the only surviving record of what that run actually sent.
8. **Method note, worth keeping.** The first pass reported "0" using `grep -c`, which counts matching
   *lines*, against a stream that is a single line. That zero was meaningless — indistinguishable
   from a pattern that never matches anything. The second pass counts occurrences and carries a
   positive control (`{"Path":"src/weird\\name.cs"}` → 1) and a negative control
   (`{"Path":"src/normal/name.cs"}` → 0) in the same output as the result. A zero from an unvalidated
   probe is not evidence; #415 made that point twice.
9. **One consequence to carry forward.** FR-12 (Phase 6) *creates* the single case where a backslash
   can legitimately be stored: a repository that genuinely contains `src/weird\name.cs`. That makes
   the `%5C` blob-URL edge (`GitHubContentService.cs:57`) reachable for the first time. Recorded in
   the PRD's *Out of Scope* and pinned by a test, not fixed speculatively — production occurrences
   of that file shape are zero.

### Phase 1: The one-line unblock (M1)

8. Strip a leading `U+FEFF` at `ParseSessionRecipient.cs:202`. Ship it.
9. Verify against the M0 fixture and against the consumer: `state: Complete`, `files-count: 79`,
   `line-rate: 56.9`.
10. Mark it in-code as a stopgap deleted by Phase 2. It exists because that repo is red *now* and
    should not stay red for the length of an interface change.

### Phase 2: Parse from bytes (M2)

11. `ICoverageParser` → byte-oriented; `ReadAttachmentText` → `ReadAttachmentBytes`, gzip unchanged.
12. `CoberturaParser.cs:25` and `JaCoCoParser.cs:30` → `XDocument.Load(XmlReader.Create(stream, settings))`.
13. `LcovParser`: decode with BOM detection before the `SF:`/`TN:` match.
14. Byte-level `CanParse` that skips a BOM and leading whitespace **and rewinds** between parsers.
15. Delete the Phase 1 stopgap once Phase 2's tests cover it.

### Phase 3: Per-file outcomes and failure isolation (M3)

16. Per-attachment `try` around `parser.Parse` (`:64`); a throw rejects that file and continues.
17. Turn `:53` (missing attachment) and `:60` (unrecognised format) into recorded rejections.
18. Add `empty` (0 bytes) and `truncated` as distinct named reasons.
19. Persist outcomes on `BuildSession`; return them per session on `GET /api/uploads/status`
    (`UploadStatusSession`, `UploadsController.cs:505-506`).
20. Session is `Failed` only when **every** attachment was rejected.

### Phase 4: Never silently succeed (M4)

21. Write `build.Coverage` on every finalize, including an all-failed one — zeroed, not absent
    (`BuildFinalizer.cs:14-46`).
22. Action: `files-count: 0` rather than `''` on any terminal state (`main.ts:318`).
23. Action: annotate `CompleteWithErrors` without `wait-for-finalize` — one bounded status read,
    warning only, never a wait a consumer did not ask for.
24. Commit page: a zero-measured-files build renders as a named rejection listing per-file reasons.
25. Advertise the narrowed `coverage` contract in `capabilities` and document it in `upload-api.md`.

### Phase 5: XML hardening (M5)

26. `XmlReaderSettings { DtdProcessing = Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0,
    MaxCharactersInDocument = <bound> }` on both XML parsers.
27. Bound bytes read out of the `GZipStream` (`:194-200`); exceeding it is a `tooLarge` rejection.
    Today only the **compressed** body is bounded (`UploadsController.cs:44`, `:91`).

### Phase 6: Separators, fixtures, consumer cleanup (M6)

28. `PathNormalizer.Normalize`: as-received first, unified second (FR-12).
29. Persist `ParsedFile.RawPath` onto `FileCoverage` for diagnosis (FR-13) — addition only.
30. The #417 comment's five-row fixture table, including `src/weird\name.cs`.
31. Delete the BOM-strip step from `MintPlayer/MintPlayer.DotnetDesktop.Tools#19`; re-run; confirm.
32. Move the `coverage-upload-v1` tag.

> Per the one-PR rule in `CLAUDE.md`, steps 31-32 land in the **same unit of work** — the consumer
> repo is another repository, not a follow-up PR, sequenced after the tag move.

### Phase 7: Durability and input bounds (M7)

33. Bound attachment count per upload and the `fileList` payload at the controller.
34. Make a redelivered `ParseSessionMessage` safe (the merge already is) and make a crashed handler
    report why, rather than leaving the 30-minute sweep to say "Never parsed before the build timed
    out" (`FinalizeBuildsCronJob.cs:59-63`).
35. A missing/malformed `rootDir` or `fileList` degrades to the documented fallback
    (`PathNormalizer.cs:47-48`) **and records that it did** — row E of the #415 table, made visible.

---

## Test Scenarios

Byte-level fixtures throughout — the class lives below the syntax, so a pretty-printed fixture tests
nothing. There are no on-disk fixtures today (samples are inline C# raw-string literals, e.g.
`CoberturaParserTests.cs:10-25`), so M0 establishes the first fixture directory.

### Scenario 1: BOM parity
- **Given** a valid Cobertura report, and a byte-identical copy prefixed with `EF BB BF`
- **Then** both parse to identical `ParseResult`s. *This is #415.*

### Scenario 2: The rest of the tolerance table
- UTF-16 LE BOM with UTF-16 content → parses
- Leading `\n\n` before `<?xml` → parses
- Trailing NUL/whitespace padding → parses
- CRLF throughout, including lcov → parses
- lcov with a UTF-8 BOM → parses and `SF:` matches

### Scenario 3: Named rejections
- 0 bytes → rejected, reason `empty`, file named
- truncated mid-document → rejected, reason `truncated`, file named
- Clover / `coverage-final.json` → rejected, reason `unrecognizedFormat`, file named — **not** silently
  claimed by `CoberturaParser` on its `coverage` root element

### Scenario 4: One bad file does not discard the good ones
- **Given** a batch of three where one is truncated
- **Then** two are ingested, the third is reported rejected, and the session is **not** `Failed`

### Scenario 5: Zero files is an error state
- **Given** a valid report where no path resolves
- **Then** the build reports 0 matched **and says so**; `files-count` is `0`, never `''`; the commit
  page renders a named rejection rather than a blank

### Scenario 6: `CompleteWithErrors` without opting in
- **Given** a consumer with `wait-for-finalize: false`
- **Then** the workflow log carries a non-fatal warning annotation, and CI stays green unless
  `fail-ci-if-error` is set

### Scenario 7: Security
- DTD with an external entity → entity not resolved, **no outbound request**
- billion-laughs / zip-bomb shape → rejected as `tooLarge` by name, not an OOM

### Scenario 8: Separators (the #417 comment's table)
- `D:\a\repo\repo\src\File.cs` → `src/File.cs`
- `/home/runner/work/repo/repo/src/File.cs` → `src/File.cs`
- mixed `\` and `/` in one report → all resolve
- a repo genuinely containing `src/weird\name.cs`, report using that exact path → resolves to **that
  file**, not to `src/weird/name.cs`
- relative `src/File.cs`, already normalised → passes through untouched

### Scenario 9: End-to-end, the acceptance number
- **Given** `MintPlayer/MintPlayer.DotnetDesktop.Tools` with **no** client-side BOM strip
- **Then** `state: Complete`, `files-count: 79`, `line-rate: 56.9`

---

## Acceptance Criteria

- [ ] **The issue's own bar**: for every upload the server either measures ≥ 1 file, or reports a
      specific machine-readable reason — and never accepts, finalizes and measures nothing without
      comment.
- [ ] M0 reproduced the BOM failure before any fix was written
- [ ] M0b's verdict is recorded as a **measured** count with a date, and the migration is either
      demonstrably unnecessary or designed as a re-key
- [ ] FR-1 … FR-17 met (PRD)
- [ ] All nine test scenarios covered by automated tests, with byte-level fixtures
- [ ] `files-count` is never `''` on a terminal build
- [ ] `MintPlayer.DotnetDesktop.Tools#19`'s BOM-strip step is deleted and that repo's CI is green
- [ ] `upload-api.md` documents the narrowed `coverage` contract; `capabilities` advertises it
- [ ] No new `action.yml` input — the consumer's surface is unchanged
- [ ] Version bumps follow `CLAUDE.md`: the NuGet major tracks .NET, the npm major tracks Angular;
      neither moves here. The action ships by moving `coverage-upload-v1`.

---

## Build & Test Commands

```bash
# Server
dotnet test apps/CodeCoverage/CodeCoverage.Tests/CodeCoverage.Tests.csproj \
  > "$SCRATCH/codecoverage-tests.log" 2>&1; echo "EXIT: $?"

# Action (npm install is root-only in this workspace)
npx vitest run --root apps/CodeCoverage/action \
  > "$SCRATCH/action-tests.log" 2>&1; echo "EXIT: $?"
# the bundle suite is excluded from the default vitest run
```

Per `CLAUDE.md`: batch the suites to the end rather than running them per milestone, redirect to a
file rather than piping the only copy into `grep`/`tail`, and verify intermediate milestones by
reading and type-checking.

---

## Related Files

- PRD: [`coverage_ingest_hardening_PRD.md`](coverage_ingest_hardening_PRD.md)
- Predecessor: [`coverage_windows_runner_paths_PRD.md`](coverage_windows_runner_paths_PRD.md) + plan
- Contract: [`code-coverage/upload-api.md`](code-coverage/upload-api.md),
  [`code-coverage/upload-result-contract.md`](code-coverage/upload-result-contract.md)
- Server: `apps/CodeCoverage/CodeCoverage/Ingestion/` (`ParseSessionRecipient.cs`, `Parsing/`,
  `PathNormalizer.cs`, `BuildFinalizer.cs`, `BuildComparer.cs`), `Controllers/UploadsController.cs`
- Action: `apps/CodeCoverage/action/` (`action.yml`, `src/main.ts`, `src/status.ts`, `src/rebase.ts`,
  `src/files.ts`)
- Tests: `apps/CodeCoverage/CodeCoverage.Tests/Ingestion/`, `apps/CodeCoverage/action/src/*.test.ts`
- `CLAUDE.md`: `apps/CodeCoverage` is a production app; the one-PR rule; batch test runs

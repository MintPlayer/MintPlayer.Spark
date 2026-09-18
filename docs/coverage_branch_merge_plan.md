# Plan — Format-agnostic, order-independent coverage branch merge (#420)

**PRD:** [coverage_branch_merge_PRD.md](coverage_branch_merge_PRD.md) · **Issue:** [#420](https://github.com/MintPlayer/MintPlayer.Spark/issues/420)
**Branch:** `issue-420-coverage-branch-merge` off `master`
**One PR.** Workstreams A, B and C land together — B is a correctness precondition for A's
order-independence guarantee, and C's parsers are what make A's ceiling worth raising.

---

## Spikes — run before M1

Three blocking unknowns. Each is small, and each changes a milestone if it comes back wrong.

### ~~SP1~~ — production `FileCoverage` volume ✅ **RUN 2026-09-18, read-only**
**Decided:** D7 → **one-shot migration (b)**, lazy read (a) retained as fallback.

Measured against `coverage-raven` on the production VPS:

| metric | value |
|---|---|
| `FileCoverages` documents | **200,230** — 96% of the whole database (208,177) |
| Database on disk | 2.23 GB |
| **Full collection scan** (streamed, loopback) | **7 seconds / 697 MB** |
| Average document size | ~3.5 KB |
| Carrying branch data (`BranchFormat != null`) | **24.9%** of a 2,000-doc sample ⇒ **~50,000** |

The pre-measurement fear (V4) does not bind at this volume: the scan floor is ~4% of the 180s
readiness budget, and the migration JS can `return` immediately for the 75% of documents with no
branch data.

**Still outstanding — SP1b, the write cost.** 7s is a *read* floor and does not price rewriting
~50k documents. Measuring it means a patch against production (a write-API call) and **needs the
user's explicit go-ahead**. A no-op `from FileCoverages update { }` exercises scan + per-document
script without writing, bracketing the cost from below.

**Decision rule for SP1b:** comfortably inside the budget ⇒ M9 takes the migration. Marginal or
doubtful ⇒ M9 falls back to lazy derivation, which is fully correct and carries no deploy risk. The
two compute the same thing, so the fallback is cheap either way.

⚠️ Do not run SP1b during a deploy window. Never print a connection string or credential.

### SP2 — real istanbul and clover fixtures
**Decides:** M5 and M6 fixture fidelity. **No sample of either format exists anywhere in the repo.**

Temporarily add `'clover'` and `'json'` to the `reporter` array in `apps/CodeCoverage/action/vitest.config.ts:16`,
run the action's suite once, and capture the emitted `clover.xml` and `coverage-final.json`. Revert
the config change — it is a spike, not a deliverable.

Confirm against real output, because getting either backwards is silent:
- clover `truecount`/`falsecount` are counts of **uncovered** paths (`0` ⇒ that path *was* taken),
- whether `<file>` appears under `<package>` or directly under `<project>` for this producer,
- istanbul `b` entries that are `-1` or absent, and which `branchMap` `type`s produce them,
- whether `statementMap` entries ever span lines (start ≠ end line).

Distil into inline raw-string fixtures matching the existing style (V10 — the repo has zero fixture
files on disk; do not introduce the first one).

### SP3 — where the A1 property test can actually live
**Decides:** M10's cost and shape.

Full permutations through `ParseSessionRecipient` means RavenDB.TestDriver per permutation, which is
the expensive path — and `reference_raventestdriver_disposal_trap` / `reference_test_suite_performance`
both say that is where suite CPU goes. Determine whether the order-independence property can be
asserted at the `CoverageMerger` + `CommitAssembler` level (pure, no database) while a **single**
end-to-end test covers the `ParseSessionRecipient` attachment-order path.

**Expected answer:** yes — the merge functions are static and pure. Confirm before committing to a
matrix of database-backed tests.

---

## Milestones

Each milestone is a commit. Per the repo's testing convention, **intermediate milestones are verified
by reading the code and type-checking (`dotnet build`), not by running the suite** — the full sweep is
M12.

### M1 — Parser contract: identity vs count-only, and max everywhere
*Workstream B + the precondition for A. Covers A5, A6; V2, V3.*

- `ParsedFile`: replace `AddBranch(line, block, branch, taken)` with
  `AddBranchArm(int line, string armKey, bool taken)` and `AddBranchCount(int line, int covered, int total)`.
- Internal state becomes per-line `(int Arity, HashSet<string> TakenArms, int Floor)`.
  `AddBranchArm` unions and maxes arity; `AddBranchCount` maxes floor and arity. Both commutative.
- `AddLine` changes **sum → max**. Delete the misleading comment at `ParsedFile.cs:25-27` **and** the
  false "distinct lines" premise at `CoberturaParser.cs:51-52` (V3).
- `null` never demoted to `0`: fix `(existing.Hits ?? 0) + (hits ?? 0)` and `MaxNullable(null, 0)` so
  unknown loses to any non-null in a *status* sense rather than becoming `0` (A6).
- `ResolveStatuses` (`ParsedFile.cs:52-69`): `Any(edge is null or 0)` → `covered < n`.
- Rewire the three existing parsers to the new calls: lcov → `AddBranchArm` (arm key from
  block+branch, **preserving the `e`/`f` marker** per D5 — stop `TrimStart('e','f','U')`); cobertura
  `condition-coverage` → `AddBranchCount`; JaCoCo `mb`/`cb` → `AddBranchCount`.

**Verify:** `dotnet build`. The parser tests will not compile against the new API yet — that is M11.

### M2 — Storage model and merge
*Covers A2, A3. The heart of Workstream A.*

- New `LineBranchCoverage { int Line; int Arity; List<string> TakenArms; int Floor; }` on
  `FileCoverage`, replacing `List<BranchCoverage> Branches`. `TakenArms` is stored sorted so
  documents are byte-identical regardless of insertion order — **A1 depends on this**.
- **Remove `FileCoverage.BranchFormat`.** Nothing reads it but the guard being deleted.
- `CoverageMerger.MergeInto` (both the `ParsedFile` and the `FileCoverage` overloads): drop the
  `if (target.BranchFormat == formatName)` gate entirely; merge by `max` arity, `union` arms, `max`
  floor. Delete the comment-only `else` at `:57-58`.
- `Summarize` (`:111-123`): `BranchesTotal += Σ Arity`, `BranchesCovered += Σ max(Floor, TakenArms.Count)`.
- Status recomputation (`CoverageMerger.cs:61-74`): `Any(Taken is null or 0)` → `covered < Arity`.
- `Clone` (`:99-109`) carries the new shape.
- Rewrite the doc comments at `FileCoverage.cs:37-43` and `CoverageMerger.cs:6-14` — they currently
  describe the drop as intent.

**Verify:** `dotnet build`. Reason through commutativity explicitly in the commit message.

### M3 — `CommitAssembler` path + its first branch test
*Covers A4. V8: no test drives branch data through the assembler today.*

- `CommitAssembler.cs:54` now flows through the rewritten `MergeInto(FileCoverage, FileCoverage)`;
  remove the `source.BranchFormat ?? target.BranchFormat ?? "unknown"` argument at `:93`.
- Add the first assembler tests that carry branch data at all: two builds of one commit, different
  formats, both orderings, identical result.

### M4 — Cobertura `<conditions><condition/>`
*Covers A8.*

Read the optional `<conditions>` child where present (gcovr, coverage.py emit it) and call
`AddBranchArm` with key `condition@{number}`; fall back to `AddBranchCount` from `condition-coverage`
when absent. A single report may mix both across lines — which is exactly why D1 makes identity a
per-line property.

### M5 — Clover parser + structural discriminator
*Covers A7 (half), A11. Uses SP2's fixture.*

- New `CloverParser : ICoverageParser`, `FormatName = "clover"`. Iterate `Descendants("file")`
  (V: `<file>` may sit directly under `<project>`). `RawPath` from `@path` when present, else `@name`.
  `SourceRoots` stays `[]`.
- `type="stmt" | "cond" | "method"` are all coverable → `AddLine(num, count)`.
- `type="cond"` → `AddBranchArm(num, "true", truecount == 0)` and `AddBranchArm(num, "false", falsecount == 0)`.
  ⚠️ **The inversion is the trap** — these are *uncovered* path counts.
- **Discriminator:** tighten `CoberturaParser.CanParse` to require a `<class filename=…>` descendant
  (or reject on a `clover=` root attribute), and order clover before cobertura in
  `CoverageParserFactory.cs:19-24`. Test **both** directions: clover is never cobertura, cobertura is
  never clover. Today clover is silently rejected as `noFiles`, format `"cobertura"` (V5).
- Fix `ReportIngestOutcome.cs:48` and `action/README.md:76-78`, which both wrongly claim clover lands
  in `unrecognizedFormat` (A11).

### M6 — istanbul JSON parser
*Covers A7 (other half). Uses SP2's fixture.*

- New `IstanbulParser : ICoverageParser`, `FormatName = "istanbul"`. Lines from
  `statementMap[id].start.line` + `s[id]`; arms from `branchMap[id].locations[i]` + `b[id][i]`, arm
  key `"{branchMapKey}:{armIndex}"`. Skip `fnMap`/`f` — `ParsedFile` does not model functions.
- Guard `b` entries that are `-1` or absent (SP2 confirms which types).
- **Sniff structurally and last**: parse-and-probe for `statementMap` + `s` on the first value, ordered
  after the cheap text sniffs so a JSON parse never runs over an XML report.
- **JSON safety gap (PRD §8):** XML gets `SafeXml`'s 256 MiB cap and DTD handling; JSON gets nothing.
  Add an explicit size/depth guard and throw `ReportTooLargeException` / `InvalidDataException` so
  `ClassifyParseFailure` (`ParseSessionRecipient.cs:286-295`) reports the right reason — a raw
  `JsonException` falls to `_ =>` and reports `malformed`.

### M7 — The missing diagnostic
*Covers A9. V7: this one **is** a model change.*

Add `BranchLinesIdentified` / `BranchLinesCountOnly` counters to `ReportIngestOutcome`, populated per
report in `ParseSessionRecipient`. Unlike `FileCoverage`, `ReportIngestOutcome` **has**
`App_Data/Model/ReportIngestOutcome.json` — run synchronize, expect `modelHashes.json` churn, and
expect GitGuardian to flag it (always a false positive here — dismiss, never "fix").

### M8 — Browse DTO and the file page
*Covers D6.*

- `BrowseController.cs:489` emits per-line `{ line, covered, total }` instead of the verbatim edge list.
- `browse.service.ts:135-141`: `BranchCoverageInfo` loses `blockId`/`branchId`, gains `covered`/`total`.
- `file.component.ts:117-137` **deletes** the `Map` rebuild — it currently reconstructs exactly this
  shape from the edges. Net simplification.
- Update `browse.service.spec.ts` and the file page specs.

### M9 — Legacy documents
*Covers A10. **SP1 chose the migration; SP1b confirms or falls back.***

**Default (D7 post-SP1): a one-shot `M_YYYYMMDDHHMM_*` migration**, following
`M_202609091300_ApiTokenIdIsNoLongerTheHash` exactly — `PatchByQueryOperation`, **RQL strings not
typed symbols**, already-migrated guard **inside the JS** (never a `where` clause — a stale index
matches nothing and looks exactly like "nothing to migrate"), `WaitForCompletionAsync`, throw ⇒
startup aborts and retries. The script returns immediately for documents with no branch data (~75%
of 200,230, measured) and re-derives the rest:

- legacy `BranchFormat == "lcov"` → `S` = keys of edges with `Taken > 0`, `n` = row count, `F` = 0
- legacy cobertura/JaCoCo → `F` = count of `Taken > 0`, `n` = row count, `S` = ∅ *(correct — those
  indices were never identities)*

**Fallback if SP1b is marginal:** the same derivation as a helper on the read path — when `LineBranches` is absent and the
legacy `Branches` list is present, derive `n` = row count; `S` = keys of edges with `Taken > 0` **if**
the legacy `BranchFormat` was `lcov`, else `∅`; `F` = count of `Taken > 0` otherwise. Apply in
`MergeInto`, `Summarize` and `BrowseController`. No migration, no readiness exposure. Document that
the legacy shape is a permanently supported input, not a deferral.

### M10 — The order-independence property test
*Covers A1 — the requirement-(1) gate. **Must fail on today's code.** Shape from SP3.*

- At the merge level (pure, no database): a fixed report set, **every permutation**, asserting the
  merged `FileCoverage` is identical — at minimum lcov+cobertura, lcov+istanbul, cobertura+clover,
  and lcov+cobertura+JaCoCo. Both `MergeInto` overloads.
- One end-to-end test through `ParseSessionRecipient` covering the attachment-iteration-order path
  (`ParseSessionRecipient.cs:63`), which is where the intra-upload race lives.
- A partition test for A5: one report split across two uploads equals the same data in one upload.

### M11 — Rewrite the tests that encode the bug
*PRD §10. Rewrite, never delete.*

- `CoverageMergerTests.cs:89` `Branch_detail_never_merges_across_formats` → rename and invert: the
  second-format report **contributes its floor**.
- `CoverageMergerTests.cs:50`, `:117`; `CoberturaParserTests.cs:70`; `JaCoCoParserTests.cs:79,91`;
  `LcovParserTests.cs:43,65` → assert the new per-line shape.
- `LcovParserTests.cs:82-95` `Accumulates_duplicate_DA_records` → now expects **max (3)**, not sum (5).
- Add the mirrored `CanParse` negatives each parser test already has, for the two new formats.
- **Must stay green:** `CoverageMergerTests.cs:27` (re-upload must not inflate — the max invariant)
  and `JaCoCoParserTests.cs:69-74` (covered line `Hits` is null — A6).

### M12 — Docs, release note, and the full sweep
*PRD §11.*

- Rewrite `docs/code-coverage/product-overview.md:123-128` and `:140`; `build-log-m0-m10.md:233-235`
  (M-item 31 is being reversed — say so, don't silently edit).
- Release note: **numbers will move for existing repos**, and D5 shifts lcov-only numbers slightly.
- Optional additive `SupportedFeatures` name `cross-format-branches` (`UploadsController.cs:78-99`).
  **No contract bump** — `CLIENT_CONTRACT = 1` stays. Note it only drives action *warnings* today.
- **Now** run the full sweep: `dotnet test` for `CodeCoverage.Tests`, the SPA specs, and the action's
  vitest suite. Capture each to a file in the scratchpad and check `EXIT:` — never pipe the only copy.

---

## Sequencing notes

- **M1 before M2.** The merge rewrite is meaningless while `ParsedFile` still sums.
- **M2 before M3.** The assembler calls the same rewritten function.
- **M5 before M6** — the sniffing order change belongs with the format that forces it.
- **M10 after M6** — the property test needs all five parsers to cover the required permutations.
- **M11 last among the code milestones** — rewriting the pinned tests before the behaviour exists
  makes every intermediate build red for the wrong reason.

## Risks

| risk | mitigation |
|---|---|
| Migration outruns the 180s deploy budget with no rollback (V4) | SP1 measured the read floor at **7s / 200,230 docs**, 75% skippable; SP1b prices the writes, and lazy derivation is a zero-risk fallback that computes the same thing |
| Clover `truecount`/`falsecount` inversion silently flips every partial line | SP2 confirms against real output; explicit test |
| Clover parsed as an empty cobertura (today's silent `noFiles`) | Structural discriminator + a test in **both** directions (M5) |
| istanbul JSON has no size guard and misreports as `malformed` | Explicit guard + typed exceptions (M6) |
| Order-independence regresses later | A1 is a **property** test over permutations, not an example test |
| `modelHashes.json` churn from M7 trips GitGuardian | Known false positive — dismiss, bump versions, never hand-publish |

## Out of scope

Per PRD §12 — merge key, `PathNormalizer`, historical re-ingest beyond M9's derivation, function
coverage, and any action input/output change. None of these is a deferred follow-up; they are
genuinely not being done.

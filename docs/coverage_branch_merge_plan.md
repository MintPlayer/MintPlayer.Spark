# Plan — Format-agnostic, order-independent coverage branch merge (#420)

**PRD:** [coverage_branch_merge_PRD.md](coverage_branch_merge_PRD.md) · **Issue:** [#420](https://github.com/MintPlayer/MintPlayer.Spark/issues/420)
**Branch:** `issue-420-coverage-branch-merge` off `master`
**One PR.** Workstreams A, B and C land together — B is a correctness precondition for A's
order-independence guarantee, and C's parsers are what make A's ceiling worth raising.

## Status — implemented 2026-09-19

All milestones are done. Suites: **CodeCoverage.Tests 532/532**, SPA specs green, action vitest
99/99.

| milestone | state |
|---|---|
| SP1 production volume · SP2 fixtures · SP3 test shape | ✅ run — SP2 reversed the clover model (V11) |
| M1 parser contract, max everywhere, null ≠ 0 | ✅ |
| M2 storage model + merge rewrite | ✅ |
| M3 CommitAssembler + its first branch tests | ✅ |
| M4 Cobertura `<conditions>` | ✅ |
| M5 Clover parser + structural discriminator | ✅ |
| M6 Istanbul parser + JSON guard | ✅ |
| M7 ingest diagnostic (+ model sync) | ✅ |
| M8 browse DTO + file page | ✅ |
| M9 migration | ✅ one-shot patch + `IgnoreMaxStepsForScript`, rehearsed on production data |
| M10 order-independence property test | ✅ |
| M11 rewrite the pinned tests | ✅ |
| M12 docs, capability name, full sweep | ✅ |

## SP1b + migration rehearsal — run 2026-09-19 against real production documents

351 production `FileCoverage` documents were copied to a local RavenDB and the real migration script
run against them. **It found a defect that would have taken the site down on deploy.**

- **A patch script is capped at 10,000 statements per document** (`Patching.MaxStepsForScript`), and
  the conversion walks every edge. Measured: **421 edges converts, 5,263 faults**. A census of all
  201,698 documents found **~770 above 400 edges and ten at 5,263**. The operation would have
  faulted, `UpAsync` would have thrown, startup would have aborted, and the deploy would have failed
  with nothing serving.
- Fixed with `QueryOperationOptions.IgnoreMaxStepsForScript`, scoped to this one operation — no
  server-wide limit is relaxed. Making the loop cheaper cannot work: ~24 statements per edge means
  the largest document needs ~125,000.
- **Write cost (SP1b): 38.7s for 200,000 documents** at production scale and ratio, cloned from the
  real sample. Against a 300s `WaitForCompletion`, a 180s deploy readiness poll, a 60s healthcheck
  `start_period` and a 30-minute migration lock — nothing is near firing.
- **Census correction.** The earlier "~25% carry branch data" came from a 2,000-document sample taken
  from the head of the collection and was **not representative**. The true figure over all 201,698 is
  **53.9%** (108,648 — 16,203 lcov-stamped, 92,445 cobertura-stamped, **none** JaCoCo).
- Also established: the `Branches`-field-absent document was an artefact of the jsonl stream's
  trailing `Stats` line, not a real document; **every** document has the field. And `BranchFormat`
  set ⟺ edges present, with zero exceptions, so the shape classes are exactly two.

### The requirement this serves

*Everything openable before the deploy must be openable after it.* That is now guaranteed
structurally rather than by the migration succeeding: `LegacyBranchCompatibility` converts pre-#420
documents **as they load**, so reports render correctly whether or not the migration has run, has
finished, or ever runs again. Both it and the migration are covered by tests, including the
6,000-edge document that fails without the option.

**Re-parsing from attachments was reconsidered and rejected.** A migration *can* read attachments —
it is ordinary C# with an `IDocumentStore`; only the JavaScript patch script cannot. But 315 of 316
builds retain gzipped reports, and every one inspected was lcov carrying `BRDA` (real arm identity,
already preserved exactly by re-derivation) with **zero `<conditions>`**. Re-parsing would redo path
normalization and flag/assembly attribution for no fidelity gain.

Three findings worth carrying out of the build:

- **Rehearsing against real data caught what unit tests could not.** The statement-budget defect only
  appears on documents far larger than any hand-written fixture. A test for it now exists, but it was
  written *after* the rehearsal revealed the failure — the census and the restore are what found it.

- **The first Clover/Cobertura discriminator was wrong** and the ingest-outcome tests caught it.
  Requiring Cobertura's `<class` rejects a *truncated* Cobertura report, which must still be
  diagnosed as a damaged report of a supported format. The test is now for Clover's markers only.
- **Local test runs needed an environment fix unrelated to this work.** All 285 database-backed
  tests failed with `ServerDirectory` null because the temp RavenDB provisioning
  (`%TEMP%\MintPlayer.Spark\RavenDBServer\7.2.1`) held a half-finished copy — 181 of 206 files and
  no `.spark-provisioned` marker — from an interrupted earlier run. Deleting that directory lets
  `RavenServerLocator` re-provision, which is the self-healing path it documents.

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
| Carrying branch data | ~~24.9% of a 2,000-doc sample~~ — **superseded, see the census above: 53.9%** |

The pre-measurement fear (V4) does not bind at this volume: the scan floor is ~4% of the 180s
readiness budget.

⚠️ **The 24.9% figure in this table was wrong** — the 2,000-document sample came from the head of the
collection and was not representative. The full census puts it at 53.9% (108,648 of 201,698). The
conclusion (a one-shot migration is viable) survives; the "75% skippable" reasoning does not.

**SP1b was run** — see the rehearsal section above. 38.7s for 200,000 documents, and the rehearsal
found the statement-budget defect that the read-floor measurement could not have.

⚠️ Never run a write against production during a deploy window, and never print a credential.

### ~~SP2~~ — real istanbul and clover fixtures ✅ **RUN 2026-09-18**
**Decided:** M5's clover mapping is **count-only**, not arm-identified. See PRD §14 and V11.

Generated both formats from one run of the action's own vitest suite (v8 provider, 10 files) by
temporarily adding `'clover'` and `'json'` reporters, then reverting `vitest.config.ts` and deleting
`coverage/`. Because both describe the *same* execution they cross-check each other.

Four measured results, three of which contradict the pre-spike plan:

1. **⚠️ clover `truecount`/`falsecount` are taken/untaken ARM COUNTS**, aggregated over every branch
   on the line — not a true/false pair, not hit counts, and not "uncovered path" counts as this plan
   previously said. Decisive case: line 99 of `capabilities.ts` has two `branchMap` entries totalling
   four arms, all taken, and clover reports `truecount="4" falsecount="0"`. **Clover is count-only,
   like cobertura.** Map to `AddBranchCount(line, truecount, truecount + falsecount)`.
2. **⚠️ istanbul arms must be attributed to `branchMap[id].line`**, not `locations[i].start.line` —
   **65 of ~153 branches** have arms starting on a different line. Per-arm attribution would render
   every multi-line ternary as two partial lines.
3. `<file>` sits **directly under `<project>`** for this producer (no `<package>`), so iterate
   `Descendants("file")`. Root carries `clover="3.2.0"` — usable as a discriminator.
4. No negative/absent `b` counts across 306 arms with the v8 provider; branch types seen are `if`,
   `cond-expr`, `binary-expr`. 88 statements span lines ⇒ attribute to `start.line`. Guard `-1`
   anyway for the istanbul provider.

Fixtures are distilled into inline raw strings matching the existing style (V10 — the repo has zero
fixture files on disk; do not introduce the first one). Real captures kept in the scratchpad for
reference only.

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
- `type="cond"` → **`AddBranchCount(num, truecount, truecount + falsecount)`** (SP2/V11 — clover is
  count-only; `truecount` is the number of *taken* arms on the line, not a true-arm hit count).
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
  key `"{branchMapKey}:{armIndex}"`, **attributed to `branchMap[id].line`** (SP2 finding 2 — 65 of
  ~153 branches have arms on another line). Skip `fnMap`/`f` — `ParsedFile` models no functions.
- Guard `b` entries that are `-1` or absent (not emitted by the v8 provider, but the istanbul
  provider uses them).
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

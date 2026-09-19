# Plan — Cobertura `<conditions>` discards the true branch arity (#423)

**PRD:** [coverage_cobertura_conditions_PRD.md](coverage_cobertura_conditions_PRD.md) ·
**Issue:** [#423](https://github.com/MintPlayer/MintPlayer.Spark/issues/423) ·
**Branch:** `issue-423-cobertura-conditions` off `master` ·
**One PR.** Parser fix, back-fill migration, tests and doc corrections land together.

**Status:** M1, M2, M4 and the two sibling format fixes (B1/B3) implemented 2026-09-19. All three
spikes resolved. **D1, D5, D7, D8 decided; D3 reduced to re-ingesting 5 builds.** Nothing is
blocked and nothing needs a database migration — measured, see PRD §4.2a.

| Milestone | State |
|---|---|
| SP1 — per-arm producer? | ✅ No such producer. coverage.py emits no `<conditions>`; gcovr emits one aggregate per line (read from its own writer); coverlet one per branch point. D1 → (a). |
| SP2 — back-fill window | ✅ Run read-only 2026-09-19. **5 builds** affected (all 2026-09-19, all still holding reports, 1.0 MB). Deploy stamped 2026-09-18T23:32:24Z. PRD §4.2a. |
| SP3 — re-parse cost | ✅ **Moot.** 5 builds × 1.0 MB needs no cost model; the migration infrastructure question disappears with it. |
| M1 — parser fix | ✅ `CoberturaParser.cs` reads `(k/n)` unconditionally; `<conditions>` is a documented last resort only when `condition-coverage` is absent or unparseable. |
| M2 — tests | ✅ `CoberturaConditionsTests.cs`, 10 tests, built from **verbatim** coverlet output + 2 committed real-producer fixtures. Suite **551/551**. |
| M3 — back-fill | ⏸ Reduced to re-ingesting **5 builds**; no migration. |
| B1 — Clover producer discriminator (D7) | ✅ Fixed + 2 tests; the incoherent `conditionals="6"` in the old sample corrected to `8`. |
| B3 — JaCoCo `mi` instruction-partial (D8) | ✅ Fixed + 3 tests. New `LineCoverage.InstructionsMissed`, merged by MIN. No migration — no JaCoCo data exists. |
| M4 — docs | ✅ `product-overview.md` rewritten; #420's PRD §3 row + A8 retracted, its plan M4 flagged, and its "re-parsing gains no fidelity" claim corrected. |
| M5 — prod verification | ⏸ After deploy. |
| B2 — JaCoCo `<group>` nesting | ✅ Fixed + test (see PRD §8a). |

---

## Spikes — run before the milestones

### SP1 — ✅ RUN. Does any producer emit one `<condition>` per arm? *(gated D1)*

**Answer: no.** See PRD §3. coverage.py 7.16.1 emits zero `<condition>` elements; gcovr 8.6 emits one
aggregate `<condition number="0" type="jump">` per line (verified in
`gcovr/formats/cobertura/write.py`, since no gcc was available to generate a report); coverlet emits
one per branch point. No coverlet line in 7 reports satisfies `conditions.Count == n`, so the guarded
variant would never have fired. **D1 → (a), implemented.**

Original spike text follows.

Generate cobertura from **gcovr** and **coverage.py** over a trivial two-arm branch, and inspect a
real report from each. Question: is `conditions.Count == n`, or is it `n/2` as coverlet's is?

- If **no** producer emits per-arm conditions → D1(a): delete the arm path for cobertura entirely.
- If **one does** → D1(b): keep the arm path behind the `conditions.Count == n` equality guard.

Cheap: `pip install gcovr coverage` in the scratchpad, two ~10-line source files. Do not add these
tools to the repo. **Output:** one paragraph in the PRD §3 open question, plus a fixture per producer
if either is per-arm.

### SP2 — ✅ RUN. How much history can a back-fill still reach? *(gated D3, D5)*

**Answer: 5 builds, 1.0 MB, all from 2026-09-19, none reaped.** Deploy stamped
2026-09-18T23:32:24Z by `SparkMigrationRecords/202609190900`. Four of the five are `MintPlayer.AI`,
including the issue's `249fe04f…`. Full table in PRD §4.2a.

Also measured, and it removes the migration question for B1/B3 entirely: across all 320 builds,
every attachment name plus every `Sessions[].RawFileNames` entry — 1,916 filenames covering the
app's whole history — buckets to **cobertura 1,622 / lcov 294 / nothing else**.

Method notes for whoever repeats this: query only `from Builds` (320 docs, collection index) — a
`where` over `FileCoverages` would spawn an auto-index across 203,793 documents on production. Two
traps cost time: RQL rejects `@metadata` alongside a `from X as y` alias, and `select count()` needs
a `group by`, so read `TotalResults` instead. Responses are chunked, so bare hex chunk-size lines are
spliced into the body **mid-token** — strip them before parsing, per
`reference_production_ssh_access`.

Original spike text follows.

Read-only against production (see `reference_production_ssh_access`). Count `Build` documents that
(a) were ingested after `354de79d` deployed, and (b) still carry report attachments. That number is
the back-fill's actual ceiling, and it shrinks daily under
`Coverage:Retention:ReportAttachmentDays = 7`.

Also measure: how many of those builds' reports are cobertura-with-`<conditions>`, and the total
attachment bytes a re-parse would have to stream. **Output:** the ceiling, and a recommendation on D5.

### SP3 — re-parse cost per build
On a local server restored with a handful of real builds, time `ungzip → CoberturaParser → merge →
Summarize → save` for one build. #420's lesson was that the JS-patch step cap (10,000) is invisible
until real documents are used; the equivalent unknown here is wall-clock against the 5-minute
`WaitForCompletionAsync` and the 180s deploy poll. A C# migration reading attachments is *not* a
`PatchByQueryOperation`, so the step cap does not apply — but the deploy timeout does.
**Output:** per-build seconds, extrapolated to SP2's count, and a verdict on whether the back-fill
can run at startup or must be a job.

---

## Milestones

### M1 — Fix the parser *(covers A1–A4)*
`CoberturaParser.cs:66-95`: call `AddBranchCount(number, k, n)` for every line carrying
`condition-coverage`, unconditionally. Remove the `<conditions>` preference and its `continue` (or
guard it with `conditions.Count == n` per SP1/D1). Delete the false comment at lines 63-65.

Leave `AddBranchArm` in place — the other parsers use it.

### M2 — Tests from real coverlet output *(covers A1–A5, A6)*
In `CoberturaParserTests.cs`:
- A `<conditions>` excerpt lifted verbatim from
  `CodeCoverage.Tests/coverage/45de29e4-…/coverage.cobertura.xml` (lines 69, 97, 106 give a 100%, a
  50% and a multi-condition case).
- **The asymmetry test (A2):** parse the same line with and without `<conditions>`, assert the two
  `ParsedBranches` are equal. This is the one test that would have caught #420.
- Non-binary arity (A4) from the `73.68%` line.
- A whole-fixture assertion (A5) on `Summarize` totals: `4832/16496`.

Re-run `CoverageMergerTests`, `BranchMergeOrderIndependenceTests`,
`CommitAssemblerBranchMergeTests`, `LegacyBranchCompatibilityTests`,
`BranchesBecomePerLineArmSetsMigrationTests` unchanged (A6). Per the repo's testing rule, this is the
single batched suite run — do not run it per milestone.

✅ **551/551 on a clean run.** Note for the next reader: a first run showed 8 failures, all
`RavenTestDriver` 15s/30s request timeouts, because analysis work was competing for CPU at the time —
the known starvation flake, not a regression. All 8 pass when the machine is idle. One real failure
was found and it was the *test's* expectation, not the parser: a line with untaken arms but `hits="0"`
is `NotCovered`, not `PartiallyCovered`, because `ResolveStatuses` reads hits first and branches
second. So "branch-partial lines" ≠ "PartiallyCovered lines" — 21 branch lines in the fixture give 9
branch-partials but only 7 partial statuses. The test now pins that distinction explicitly.

### M3 — ~~Back-fill migration~~ → superseded by M6 *(SP2 reduced it to 5 builds)*

> The migration below was designed against an unmeasured population. SP2 found **5** affected builds,
> all still holding their reports, so none of this machinery is warranted — see **M6**. Kept for the
> reasoning about which denormalized sites drift (§4.3), which still applies to the re-ingest.


New `ISparkMigration`, C# not JS patch, so it can open attachments:

1. Select `Build` documents ingested after the #420 deploy that still have report attachments.
2. Re-parse each retained report through the fixed parser, re-merge, and rewrite
   `FileCoverage.Branches` **and** `Lines[].Status`.
3. Recompute `Build.Coverage` via `CoverageMerger.Summarize`, then `CommitAssembly.Coverage`,
   `Commit.Coverage` and `Repository.LatestCoverage` through the existing `CommitAssembler` path
   rather than by hand (D4).
4. Idempotent by construction — re-parsing a report is deterministic, so a second run is a no-op
   write of identical values. Record the version in `SparkMigrationRecords` as usual.
5. Builds whose attachments were already reaped are **skipped and counted**, not guessed at. Log the
   count; that number is the permanent residue and belongs in the PR description.

If SP3 says startup is too slow, ship it as a one-shot cron job instead and say so here.

### M6 — Re-ingest the 5 affected builds *(covers A7)*

Not code. After deploy, either re-run those five workflows so the fixed parser re-ingests naturally
(no new code, preferred) or run a one-shot job over the five build ids. Writes to production, so it
needs Pieterjan's go-ahead and is deliberately **not** part of this PR.

**The five, measured 2026-09-19** — so the go-ahead is a decision, not a lookup:

| # | Build id | finalized (UTC) | cobertura reports |
|---|---|---|---|
| 1 | `Commits/1288608313/a603dba0b474376e65652c7e8064c0419cfed55d/builds/35445786794-1` | 13:28:46 | 1 of 5 |
| 2 | `Commits/1266490237/ee7be01f8be38f9977a0576ecaeb7eb71aec42c8/builds/35446645546-1` | 13:47:47 | 3 of 3 |
| 3 | `Commits/1266490237/249fe04f42168f00751eb66a4359278133a2ad96/builds/35446821520-1` | 13:50:38 | 3 of 3 |
| 4 | `Commits/1266490237/18dbca03c4d1940f3fc2540a479f9f541214be0e/builds/35446787299-1` | 13:50:54 | 3 of 3 |
| 5 | `Commits/1266490237/f24b67c6e5f9fbdc274b41df0a7ae67897db3193/builds/35447500968-1` | 14:05:26 | 3 of 3 |

Repository `1266490237` is `MintPlayer.AI` (#2–#5). **#3 is the evidence in #423 itself**
(`249fe04f…`), so it is the one to re-ingest first and re-check against the issue's reproduction.

### M4 — Correct the documents that assert the false premise *(covers A8, D6)*
- `docs/code-coverage/product-overview.md:124-140` — cobertura moves to the count-only group; state
  that `<condition number=>` identifies a branch *point*, not an arm.
- `docs/coverage_branch_merge_PRD.md` §3 table and §4.2 — same correction, marked as superseded by
  this PRD rather than silently edited, so the #420 record stays readable.
- `docs/coverage_branch_merge_plan.md` M4 — note the milestone shipped a defect and point here.

### M5 — Verify on production
After deploy, re-check the issue's own reproduction:
`GET /api/browse/repos/MintPlayer/MintPlayer.AI/commits/249fe04f…/file?path=…/TensorOps.cs` should
return 29/46 conditions and 15 partial lines, not 23/23 and 0. Record the before/after in the issue.

---

## Sequencing

All of it is done except M6 (re-ingest), which waits on deploy plus a go-ahead, because it writes to
production. The original sequencing — SP1 → M1, SP2/SP3 → D3/D5 → M3 — held; SP2 simply collapsed M3
from a 200k-document migration into a 5-build re-ingest.

## Risks

- **Branch rate will visibly *drop*** on every C# repo once this deploys — 29.3% where the dashboard
  reads 36.8% on the measured sample. That is the correction landing, not a new regression, but it
  will look like one. Said in the PR and worth saying in the issue before it ships.
- ~~Attachment retention is deleting the back-fill's inputs at 7 days.~~ **Retired by SP2:** all 5
  affected builds are from 2026-09-19 and still hold their reports, so the reaper only bites if this
  sits unshipped for a week. D5 is not needed.
- **D7's discriminator rests on the format spec, not on a measured Atlassian report.** No such report
  was available, and none has ever been ingested (PRD §4.2a). The mitigation is that the reading flips
  only on an unambiguous contradiction, so a real istanbul document cannot be misread — but if
  Atlassian Clover is ever ingested for real, verify against it rather than trusting this.
- **The suite flakes under CPU contention and it reads as a regression.** Runs with other work in
  parallel showed 8 and then 4 `RavenTestDriver` request timeouts; idle, the same suite is 556/556 and
  40% faster. Never judge a red run here without a quiet re-run.

## Out of scope

Everything in PRD §9. Notably: no document-retention work, no branch-rate badge or gate, no change to
the storage model or to the other four parsers.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

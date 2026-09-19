# PRD — Cobertura `<conditions>` discards the true branch arity (#423)

**Issue:** [#423](https://github.com/MintPlayer/MintPlayer.Spark/issues/423) ·
**Plan:** [coverage_cobertura_conditions_plan.md](coverage_cobertura_conditions_plan.md) ·
**Predecessor:** [coverage_branch_merge_PRD.md](coverage_branch_merge_PRD.md) (#420) — this is a
regression in that PR, and its §3/§4.2 are among the things this one has to correct.

---

## 1. Verification summary — what the issue got right, and what it got wrong

The issue is **correct on the mechanism, the location, and the blame**. Verified at `354de79d`:

- ✅ `CoberturaParser.cs:66-81` prefers `<conditions>` and `continue`s past the
  `condition-coverage="k% (k/n)"` fallback, so `Floor` is never set on that path.
- ✅ `Arity` becomes `Arms.Count` — the number of `<condition>` elements — and every condition with
  `coverage` above 0% is recorded as a taken arm (`taken = percent > 0`, line 77). A
  `coverage="50%"` condition is therefore stored as *fully taken*.
- ✅ It is a **regression introduced by #420**, not a long-standing gap. Pre-#420 (`d98525c4`) there
  was no `<conditions>` branch at all; every line went through the `(k/n)` denominator and
  `50% (1/2)` correctly produced 2 edges with 1 taken. Only three commits have ever touched
  `CoberturaParser.cs` (`e748eee5` import, `d98525c4` #418 diagnostics, `354de79d` #420).
- ✅ Line coverage, tree/repo totals, badges and gates are unaffected by the *branch* fields — no
  badge, gate or PR-comment code reads `Arity`/`Floor`/`Covered`/`IsPartial` at all.

Four corrections, all measured against the repo's own coverlet fixtures
(`CodeCoverage.Tests/coverage/*/coverage.cobertura.xml`, 7 reports, ~6,000 branch lines each):

- ❌ **"coverlet does not emit `<conditions>`" is false** — and that false claim is written into the
  code as the comment at `CoberturaParser.cs:63-65`, into `docs/code-coverage/product-overview.md`,
  and into the #420 PRD §3 ("arm identity: yes, where emitted (gcovr, coverage.py)"). Every coverlet
  fixture in this repo emits `<conditions>`. The defect is not a missing producer case; it is a
  wrong model of what a `<condition>` element *is*.
- ❌ **`PartiallyCovered` is not unreachable on the coverlet path.** Coverlet emits `coverage="0%"`
  conditions (5,174 of them in fixture `45de29e4`), which do produce untaken arms. In that fixture
  **3,856 lines are still correctly flagged partial**; the truth is 4,836, so **980 partial lines are
  missed** — exactly the lines whose only defect is a fractional condition. The issue's sampled files
  happened to contain no 0% conditions. The bug is an *under-count*, not a total failure.
- ❌ **The damage is not confined to partial lines: `Arity` is wrong on every coverlet branch line.**
  A `<condition>` is a branch *point*, and coverlet's are overwhelmingly 2-arm jumps, so the stored
  arity is half the truth. Measured distribution of (conditions, `n`): `(1,2)×4678`, `(2,4)×1034`,
  `(3,6)×224`, `(4,8)×90`, `(5,10)×24`, `(6,12)×12`. Both sides of the ratio shrink, so the headline
  branch rate moves *less* than the raw numbers suggest but still moves:
  **fixture `45de29e4` stores 3,014/8,188 = 36.8% where the truth is 4,832/16,496 = 29.3%.**
- ❌ **"Arity = 2 × conditions" is not a valid fix.** 18 of 6,094 branch lines in that fixture break
  the ×2 relation, and the fixture contains conditions with `coverage="73.68%"`, `"16.66%"` and
  `"60%"` — switch dispatch with arity 19, 6 and 5. **The line-level `(k/n)` is the only exact
  source of truth in the document**, which is why the pre-#420 reader was right.

**Net:** the issue's suggested fix (2) — "always set `Floor` from the `(k/n)` denominator" — is the
correct one, and is correct for a stronger reason than the issue gives.

## 2. Problem

Branch coverage is wrong for every cobertura report that carries `<conditions>`, which means every
coverlet-produced report, which means the entire C# side of every repository served by
coverage.mintplayer.com. Specifically, per branch line:

| | stored today | truth |
|---|---|---|
| `Arity` | number of `<condition>` elements | `n` from `condition-coverage="k% (k/n)"` |
| `Covered` | number of conditions with `coverage > 0%` | `k` |
| `IsPartial` | only when some condition is exactly `0%` | `k < n` |

The wrongness is not self-cancelling. It inflates the branch rate (29.3% → 36.8% measured), and it
suppresses the `PartiallyCovered` gutter marker on the ~16% of partial lines whose conditions are all
fractional rather than 0%.

**One consequence reaches further than branch display.** `PatchCoverageCalculator.cs:66-75` reads
`LineCoverage.Status`, and a line wrongly resolved to `Covered` instead of `PartiallyCovered` is
counted as covered patch — but so is a genuinely partial line, since patch coverage counts
`Status != NotCovered`. So the gate verdict does **not** change. Recorded here so the next reader
does not have to re-derive it.

## 3. Why the arm-identity model is wrong for cobertura, not merely mis-tuned

`#420` split formats into *arm-identifying* and *count-only*, and put cobertura's `<condition number=>`
in the first group. That classification is the root error, and it survives even if the arity is fixed:

`condition:70` names a **branch point**, not an arm of one. Two reports that each exercise a different
arm of the same point both report `condition:70`, so the union of their arm sets is `{condition:70}` —
one arm — when the truth is two. The arm set therefore cannot grow with evidence, which is the entire
purpose of storing arms rather than counts. A count-only floor (`Floor = max(k)`) is *also* lossy
across reports, but it is lossy in the safe direction and it is what every other count-only format
already uses.

So the fix is not "read `coverage` more carefully". It is: **cobertura is a count-only format**, and
`<conditions>` carries no information the line-level attribute does not already carry exactly.

**SP1 answered this: no producer emits one `<condition>` per arm.** Measured 2026-09-19:

- **coverage.py 7.16.1** — emits **no `<conditions>` element at all**. Generated a branch report over
  a sample with taken/untaken/multi-way branches: zero `<condition>` elements, branch data carried
  entirely by `condition-coverage="50% (1/2)"`.
- **gcovr 8.6** — emits exactly **one aggregate `<condition number="0" type="jump" coverage="X%"/>`
  per line**, from the whole line's stat. Read from the producer itself,
  `gcovr/formats/cobertura/write.py::_condition_element`, which hardcodes `number="0"` and
  `type="jump"` and sets `coverage` from `branch.percent`. Identical in shape to coverlet.
- **coverlet** — one `<condition>` per branch point, as measured across 7 reports.

So **all three Cobertura producers are count-only**, and #420's claim that "gcovr and coverage.py
emit [per-condition identity]" is false for both. D1 resolves to (a): drop the arm path.

## 4. Design

### 4.1 Parser change (the whole functional fix)

`CoberturaParser.cs:66-95` collapses to: for every line carrying `condition-coverage`, call
`AddBranchCount(number, k, n)`. Unconditionally. Delete the `<conditions>` preference and its
`continue`.

That is the pre-#420 behaviour expressed in the post-#420 model, and it needs no new model concepts:
`Floor = max(Floor, k)`, `Arity = max(Arity, n)`, `Covered = max(Floor, TakenArms.Count)`,
`IsPartial = Covered < Arity` all already do the right thing.

Guarded variant, only if SP1 finds a per-arm producer: keep the arm path but take it **only when
`conditions.Count == n`**, and call `AddBranchCount` as well in every case. The equality is
self-validating — it needs no producer sniffing, and it is false for 100% of coverlet lines, so
coverlet lands on the count path automatically.

**The one path still reasoned about rather than measured** is `<conditions>` present with no usable
`condition-coverage`, where element count stands in for an arity the document never states. That is
the shape of this very defect, narrowed to a case no measured producer exhibits — so it counts itself
into `ParseResult.DegradedBranchLines` and `ParseSessionRecipient` logs a warning naming the report,
the build and the line count. If a real producer ever takes that path, it says so instead of quietly
under-stating arity.

### 4.2 What does *not* change

The storage model, `CoverageMerger`, `LegacyBranchCompatibility`, the `202609190900` migration, the
other four parsers, `BrowseController.cs:492`, the Angular file view, badges, gates, indexes. This is
a one-file functional change plus a back-fill.

### 4.2a SP2 — the damaged population is 5 builds

Measured read-only against production 2026-09-19. The `SparkMigrationRecords/202609190900` document
is stamped **2026-09-18T23:32:24Z**, which dates the #420 deploy and matches the issue's timestamp
exactly.

| metric | value |
|---|---|
| `Builds` total | 320 (`FileCoverages` 203,793) |
| finalized **after** the deploy | **5** — the entire wrongly-parsed population |
| of those, still holding their reports | **5** (none reaped) |
| report bytes to re-stream | **1.0 MB** gzipped |
| span | all on 2026-09-19 |

Four of the five are `MintPlayer.AI`, including `249fe04f…` — the exact commit in the issue. All five
carry cobertura reports, so all five are affected.

**This collapses M3.** The back-fill is not a migration over 200k documents; it is a re-ingest of 5
builds whose inputs are all still present. The urgency I attached to D5 was real in kind but not in
degree: the 7-day reaper only bites if this stays unshipped for a week.

**And a second measurement removes the migration question entirely.** Bucketing every attachment name
and every `Sessions[].RawFileNames` entry across all 320 builds — 1,916 uploaded filenames covering
the app's whole history — yields **cobertura 1,622 and lcov 294, and nothing else**. No JaCoCo, no
Clover, no istanbul JSON has ever been ingested. So the two format fixes below need no back-fill:
their formats have no stored data to repair, and an absent field is the correct default for every
existing document.

## 4.3 Back-fill (the hard part)

Fixing the parser corrects **new uploads only**. Wrong data is already denormalized into five places:

1. `FileCoverage.Branches` — the arities and floors themselves.
2. `FileCoverage.Lines[].Status` — `Covered` where it should be `PartiallyCovered`.
3. `Build.Coverage`, `CommitAssembly.Coverage`, `Commit.Coverage`, `Repository.LatestCoverage` —
   `BranchesCovered`/`BranchesTotal` (percentages are never stored, so only these counts drift).

The `202609190900` migration cannot be reused: its guard is `Branches[0].BranchId === undefined`, so
it is a no-op on post-#420 documents, and it re-derives from stored edges, which no longer carry the
denominator. **The lost information is not recoverable from the database** — only from the raw report.

Raw reports *are* retained as gzipped attachments on `Build` documents, and a C# migration can read
attachments (only the JS patch script cannot). So a re-parse back-fill is possible — **but #420 also
shipped `ReapReportAttachmentsCronJob` with `Coverage:Retention:ReportAttachmentDays` defaulting to
7 days.** Every day this sits un-shipped, another day of reports becomes unrecoverable. This is the
one genuinely time-sensitive element of the work and it is why D3 must be decided before M1, not
after.

## 5. Decisions required

| # | Decision | Options | Recommendation |
|---|---|---|---|
| D1 | Cobertura's classification | (a) count-only, always; (b) arm-identifying when `conditions.Count == n`, else count | **(a) — RESOLVED by SP1.** No producer emits per-arm conditions, and no coverlet line in 7 reports has `conditions.Count == n`, so (b)'s guard would never fire. Implemented. |
| D2 | Fractional `coverage="X%"` on a condition | honour it / ignore it | **Ignore it.** With D1 the attribute is never read; the line-level `(k/n)` subsumes it exactly. |
| D3 | Back-fill scope | (a) none; (b) re-parse retained attachments; (c) re-ingest by asking repos to re-upload; (d) recompute only summaries | **(b) — and SP2 shrinks it to 5 builds**, so a `PatchByQueryOperation`-style migration is overkill. Re-ingest or a one-shot job over 5 build ids. |
| D4 | Should the back-fill also fix `Lines[].Status` and the four `CoverageSummary` sites | yes / branches only | **Yes** — a corrected `Branches` array beside a stale `Status` is worse than either alone, and `Summarize` is the only thing the UI shows. |
| D5 | Raise `ReportAttachmentDays` temporarily to widen the back-fill window | yes / no | **No longer needed.** SP2 shows all 5 affected builds are from 2026-09-19 and still hold their reports, so the 7-day window is not binding unless this sits unshipped for a week. |
| D6 | Correct the docs that assert the false premise | in this PR / later | **In this PR.** `product-overview.md` and #420's PRD §3/§4.2 both state coverlet emits no `<conditions>`. Leaving them is how this gets reintroduced. |

## 6. Blast radius

Small and well-bounded. `Arity`/`Floor`/`TakenArms`/`Covered`/`IsPartial` are read only by
`CoverageMerger` (status + `Summarize`), `ParsedFile.ResolveStatuses`, `ParseSessionRecipient`
diagnostics counters, `LegacyBranchCompatibility`, the `202609190900` migration,
`BrowseController.cs:492` (`{Line, Covered, Total}` — the only wire exposure) and
`file.component.ts:119-133`. No RavenDB static index touches `FileCoverage`; the
`Auto/FileCoverages/ByBranches[].FloorAndBuildIdAndPath` auto-index named in the issue was created by
an ad-hoc Studio query, not by repo code, and nothing depends on it.

## 7. Acceptance criteria

- **A1** A cobertura line with `condition-coverage="50% (1/2)"` **and** `<conditions><condition
  number="70" coverage="50%"/></conditions>` parses to `Arity 2`, `Floor 1`, `Covered 1`,
  `IsPartial true`, status `PartiallyCovered`.
- **A2** The same line with the `<conditions>` element deleted parses identically. The asymmetry the
  issue reproduces in one step is gone, and a test asserts the two are equal.
- **A3** A line with `condition-coverage="100% (2/2)"` and one `coverage="100%"` condition parses to
  `Arity 2`, `Covered 2`, not `1/1`.
- **A4** A condition with non-binary arity (`coverage="73.68%"`, `n=19`) parses to `Arity 19`.
- **A5** A committed fixture of real coverlet output parses to the branch totals counted
  independently from its own `condition-coverage` pairs — `36/50` over 21 branch lines for
  `Fixtures/coverlet.cobertura.xml`, where the defect read `21/21`. A real coverage.py report parses
  too, pinning the count path so it cannot be removed as "the fallback".
- **A6** Merge order-independence and cross-format merge properties still hold (existing
  `BranchMergeOrderIndependenceTests` pass unchanged).
- **A7** Per D3: the 5 affected builds are re-ingested, and their `Branches`, `Lines[].Status` and the
  four `CoverageSummary` sites agree with a fresh ingest of the same report.
- **A8** `docs/code-coverage/product-overview.md` and `docs/coverage_branch_merge_PRD.md` no longer
  claim coverlet omits `<conditions>` or that `<condition number=>` identifies an arm.
- **A9** (B2) A JaCoCo `report-aggregate` document with `<group>`-nested packages parses to its files
  instead of zero.
- **A10** (D7) An Atlassian-shaped Clover document — `conditionals` equal to 2 per cond line and
  contradicting the arm-count sum — reads `truecount="10000"` as one taken arm of two, not 10,000.
  A document with no `conditionals`, or one consistent with both formulas, keeps the istanbul reading.
- **A11** (D8) A JaCoCo line with `ci>0 && mi>0` is `PartiallyCovered` with no branch data invented;
  a second report with `mi="0"` clears it; a report that counts no instructions at all does not.
- **A12** (§8c) A hand-written `description.en` survives `--spark-synchronize-model` unchanged and
  does not fail `--spark-verify-model`; an absent or blank one is seeded from the C# summary and is
  reported by verify until it is.

## 8. Tests that encode the bug as intent — rewrite, never delete

`CoberturaParserTests.cs` has **no test for the `<conditions>` path** — that is precisely why M4 of
#420 shipped broken. Nothing currently asserts the wrong behaviour, so nothing needs deleting; the
gap is the finding. The new tests must use a **real coverlet excerpt**, not a hand-written one: the
hand-written `Sample` in that file omits `<conditions>` and is exactly how the regression slipped
past a green suite.

One sample did have to be corrected rather than merely extended. `CloverParserTests.Sample` declared
`<metrics conditionals="6">` over cond lines summing to **8** arms — Atlassian's formula applied to
istanbul's line data. It was hand-written into incoherence while being cited as the evidence for the
very semantics it was meant to prove, and it tripped D7's discriminator. The metric is now `8`; the
cond lines themselves were measured against real istanbul JSON in #420 and stand unchanged.

## 8a. The same class of defect in the other parsers

#423 is one instance of a pattern, so all four remaining parsers were audited against their formats'
real semantics. **The systemic cause: not one parser has ever been tested against real tool output.**
Every branch assertion in the suite runs against a sample hand-written by whoever wrote the parser,
so the sample encodes the parser's *belief* rather than the tool's behaviour — which is exactly how
#423 shipped green. Worse, the seven real coverlet reports that do sit in
`CodeCoverage.Tests/coverage/` are matched by `.gitignore`'s `**/coverage/` rule, so they are local
build artifacts CI never sees and no test references them.

| # | Parser | Finding | Status |
|---|---|---|---|
| B1 | `CloverParser.cs:73` | `Arity = truecount + falsecount` holds for istanbul's clover reporter (arm counts) but **not for Atlassian Clover / Clover-PHP**, where those are *execution* counts and arity is always 2. A `truecount="10000"` loop would add 10,000 phantom branches to `BranchesTotal` and report a fully-covered line as partial. | **Fixed** — discriminated on `<metrics conditionals=>`, tests added |
| B2 | `JaCoCoParser.cs:36` | `root.Elements("package")` missed `<group>`-nested packages, which is exactly what `jacoco:report-aggregate` emits for a multi-module build. Such a report parsed to **zero files** and was rejected as `noFiles`. | **Fixed** (`Descendants`), test added |
| B3 | `JaCoCoParser.cs:55` | `mi` is never read, so JaCoCo's *instruction-partial* line (`ci>0 && mi>0`, yellow in JaCoCo's own report) is stored as fully `Covered`. The class doc claims `mi` is used. | **Fixed** — new `InstructionsMissed`, tests added |
| B4 | `IstanbulParser.cs:112,122` | A `branchMap` entry with no `b` counterpart is skipped, losing that branch's arity rather than recording untaken arms. Needs a truncated/merged map to trigger; no producer confirmed. | Suspected |
| B5 | `LcovParser.cs:37-41` | An intermediate missing `end_of_record` silently discards the *preceding* file and all its data, rather than flushing it. Needs a malformed producer. | Suspected |

Audited and found **correct**, so recorded here to stop the next reader re-deriving it: lcov's
`-` vs `0` handling and its omission of BRF/BRH (lcov derives those *from* BRDA, so no arity is
lost); and istanbul's whole per-arm model, including `if` with no `else` (2 arms), `binary-expr`
(N operands), `switch`, `cond-expr` and `default-arg` (1 arm, correctly not forced to 2).

Two further decisions fall out of this:

| # | Decision | Options | Recommendation |
|---|---|---|---|
| D7 | Clover's two incompatible producers (B1) | (a) leave as-is; (b) discriminate on `<metrics conditionals=>`, which equals `2 × cond-lines` for Atlassian and `Σ(truecount+falsecount)` for istanbul | **(b) — DECIDED by Pieterjan, implemented.** The discriminator only flips the reading on an *unambiguous* contradiction; a missing or consistent total keeps the istanbul reading, which is the measured one. No migration: no Clover report has ever been ingested (§4.2a). |
| D8 | JaCoCo instruction-partial (B3) | (a) leave; (b) add a partial signal to `ParsedFile` independent of branches | **(b) — DECIDED by Pieterjan, implemented.** `LineCoverage.InstructionsMissed` (`int?`, null = no claim) merges by **MIN** over claiming reports, and `ResolveStatuses` treats `> 0` as partial alongside branch partiality. No migration needed: no JaCoCo report has ever been ingested, and absent = no claim is already correct. |

## 8b. Keeping the "no migration" conclusion honest

§4.2a's format census is a **point-in-time measurement**, and it is what justifies shipping D7 and D8
without a back-fill. It cannot be turned into a code assertion — no test can observe which formats
production has ingested — so the guard is procedural rather than automated:

- The method is recorded in the plan's SP2 section, including the RQL and chunked-response traps, so
  re-running it is minutes rather than a rediscovery.
- **Re-run it before any future decision that depends on "format X has no stored data."** It is
  already false the moment a repository starts uploading a new format, and nothing will announce that.
- `ReportIngestOutcome` already records the parsed format per upload, so the same question can be
  answered from ingest diagnostics rather than attachment names once enough history accrues.

The nearest thing to an automated guard now exists for the narrower recurrence risk:
`ParseResult.DegradedBranchLines` (see §4.1) makes the one remaining reasoned-about code path announce
itself the first time a real producer takes it.

## 8c. A Spark-core change this PR also carries: descriptions are seeded, not owned

Not a coverage defect, but uncovered by this work and landed in the same PR (see the repo's one-PR
rule). While correcting `ReportIngestOutcome.BranchLinesIdentified`'s doc comment, CI failed at
`Verify Spark models are in sync` — because under #348 a `///` summary **owned** the attribute's
`description.en` and overwrote whatever the model file said.

That ownership was wrong in kind. A `///` comment is written for the next developer; a `description`
renders as an [i] tooltip for the end user. #348 let the first overwrite the second, and made
`--spark-verify-model` **fail the build** until the human's wording was discarded.

**New rule:** the summary is a *seed*. It fills `description.en` when the model file has nothing
there — key absent, or present but blank — and JSON owns it from then on, in every language. Blanking
a description is how you ask for the seed back.

Verify is narrowed rather than removed: it still reports a missing or blank `en`, which preserves the
invariant that **verify fails exactly when synchronize would write something**. The two commands can
no longer disagree, which was the actual defect.

- Accepted cost: a corrected summary no longer reaches a description that already has text, so
  user-facing wording is changed by editing the model file. #348's rule ii existed because seeding
  only *new* attributes would leave existing ones undescribed forever — true then, spent now the
  models are seeded.
- No hash work: `description` was never in `ModelFileShape.StructuralAttributeFields`, so the drift
  check was the only gate.
- Verified end to end against `apps/CodeCoverage`: a hand-edited description survives
  `--spark-synchronize-model`, and `--spark-verify-model` still exits 0.
- Docs: `guide-attribute-descriptions.md` rewritten; `issue_348_PRD.md`'s decision table moved from
  **ii** to **i** with the supersession recorded in place; `issue_348_plan.md` S2 and the preview-70
  release note annotated rather than rewritten.

## 9. Non-goals

- Reworking the branch storage model. It is correct; only the cobertura reader lies to it.
- Document retention for `FileCoverage` (still scoped in #420 PRD §13b, still not implemented).
- Fixing `Auto/FileCoverages/…` auto-indexes, or the comment/badge divergence tracked separately in
  `project_coverage_comment_vs_badge_divergence`.
- Branch-rate badges or gates. Neither reads branch data today; adding them is separate work.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

# PRD — Format-agnostic, order-independent coverage branch merge (+ istanbul/clover)

**Issue:** [#420](https://github.com/MintPlayer/MintPlayer.Spark/issues/420)
**App:** `apps/CodeCoverage` (production — coverage.mintplayer.com)
**Status:** drafted 2026-09-18, after a four-agent code-grounded verification of every claim in the issue.
**Blocks:** [MintPlayer.Polyglot#71](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/71) / P39 — sequenced server-first.

---

## 1. Verification summary — what the issue got right, and what it got wrong

The investigation confirmed essentially all of #420 against source at `d98525c4`. Four corrections
and additions matter enough to change the design:

| # | Finding | Impact |
|---|---|---|
| **V1** | **Cobertura and JaCoCo mint *identical* key shapes** — both `AddBranch(line, "0", i)` with `i < covered ? 1 : 0` (`CoberturaParser.cs:66-69`, `JaCoCoParser.cs:63-66`) — yet drop each other purely because `formatName` differs. lcov's `("0","0")`/`("0","1")` collides with both *by accident*. | The guard is keyed on the wrong thing. Identity is a **per-line, per-report** property, not a format-level one. Drives D1. |
| **V2** | **`AddBranch` has the same sum-vs-max asymmetry as `AddLine`** (`ParsedFile.cs:38-45` vs `CoverageMerger.cs:49`). For cobertura's per-`<class>` fan-out this *remaps which synthetic indices are non-zero*. | Workstream B is not confined to `Hits`; it silently corrupts branch data too. Widens M1. |
| **V3** | **The load-bearing false premise is in `CoberturaParser.cs:51-52`**, not only the lcov comment: *"Multiple classes for the same file describe distinct lines; a duplicated line is the same run, where AddLine accumulates."* Coverlet emits one `<class>` per type **and per closed generic instantiation**, which repeat the same line numbers. | Both comments get deleted; the cobertura one is the one that actually misleads. |
| **V4** | **No chunked or resumable migration precedent exists anywhere in the repo.** All 11 migrations are single-shot `PatchByQueryOperation` + a hard `WaitForCompletionAsync(5 min)`, run **blocking before the port opens** (`SparkMigrationRunner.cs:19-20`), against a 180s deploy readiness budget **with no rollback** (`code-coverage-deploy.yml:194-206`). `FileCoverage` is the largest collection in the database by a wide margin. | The issue's A10 ("chunked and resumable") would require **building migration infrastructure that does not exist**. Drives D7 — the central open decision. |

Additional corrections, lower impact but in scope:

- **V5** — `ReportIngestOutcome.cs:48` and `action/README.md:76-78` both claim clover lands in
  `unrecognizedFormat`. **False for clover**: `CoberturaParser.CanParse` claims it on root name
  alone, finds no `<class filename=…>`, and the session is rejected as **`noFiles`, format
  `"cobertura"`**. True for istanbul JSON. Shipped docs are wrong; fix in this PR.
- **V6** — The action **already uploads both formats today**: `'**/clover.xml'` and
  `'**/coverage-final.json'` are in `DEFAULT_PATTERNS` (`action/src/files.ts:12,14`). These parsers
  close a loop the action opened; users are already paying the rejection.
- **V7** — `ReportIngestOutcome` **does** have `App_Data/Model/ReportIngestOutcome.json`, unlike
  `FileCoverage`. A9's new diagnostic field is therefore a **model change** (`modelHashes.json`
  churn, GitGuardian false positive expected — dismiss, never "fix").
- **V8** — There is **no** test that drives branch data through `CommitAssembler`. Every fixture in
  `CommitAssemblerTests.cs` / `CrossRunAssemblyTests.cs` is lcov `DA:`-only; the only `BRDA` strings
  in the whole test project are in `LcovParserTests.cs`. A4 is genuinely new coverage.
- **V9** — Parser registration is a **hardcoded static array** (`CoverageParserFactory.cs:19-24`),
  not DI and not a source generator. A new parser is one class + one order-sensitive line.
- **V10** — Fixtures are **inline raw-string literals**; there are zero fixture files on disk, and
  **no istanbul or clover sample exists anywhere in the repo** (tracked or untracked).

---

## 2. Problem

Three maintainer requirements, all violated today:

1. **Upload order must not matter.** The same set of reports must always yield the same stored result.
2. **Branch edges must never be discarded.**
3. **Full support in one version.** No staged rollout, no feature gate.

The mechanism: `FileCoverage.BranchFormat` (`FileCoverage.cs:43`) is stamped by whichever report
brings branch data first, and `CoverageMerger.cs:43` merges edges **only** when a later report's
format name matches. `:57-58` is a comment-only `else` — the edges are dropped with no log, and
`ReportIngestOutcome` records `Parsed = true` with the correct `FilesCount`, so nothing in the API,
the PR comment or the action output reveals the loss.

It fires **within a single upload**, not just across uploads: `ParseSessionRecipient.cs:63` loops
`buildSession.RawFileNames` and calls `MergeInto` at `:191`, so two formats in one upload race for
the stamp and the winner is attachment iteration order. `Summarize` (`:119-120`) then counts rows
over the surviving set, pinning both numerator and denominator to the first format's arity. The same
drop exists untested on the build-to-build path at `CommitAssembler.cs:54`.

Independently, `ParsedFile.AddLine`/`AddBranch` **sum** duplicates within one report while
`CoverageMerger` **maxes** across reports — so splitting one report into two uploads changes the
stored result (requirement 1 again, by a different route).

And `null` ("executed, count unknown" — JaCoCo's only expressible form, `JaCoCoParser.cs:59`) is
silently demoted to `0` by both `(null ?? 0) + (0 ?? 0)` and `MaxNullable(null, 0)`. In `ParsedFile`
this is **status-affecting**: `ResolveStatuses` maps `Hits == 0` to `NotCovered`, dropping the line
out of every percentage.

---

## 3. The honest ceiling

**Requirement (2) is satisfiable. "No information is lost" is not, and the gap is the formats' fault.**

Cobertura's `condition-coverage="50% (1/2)"` carries a **count only**; the parser expands it into
positional fiction. Two cobertura reports covering *different* arms of the same line both say
`edge0=1, edge1=0`, and no storage model recovers that the union is 2/2.

| format | branch payload | arm identity |
|---|---|---|
| **istanbul JSON** | `branchMap` per-arm entries + parallel `b` counts | **yes, richest** |
| **lcov** | `BRDA:<line>,<block>,<branch>,<taken>` | **yes** — real ordinals |
| **clover** | `<line type="cond" truecount= falsecount=/>` | **yes** — real two-arm |
| **cobertura** `<conditions>` | per-condition `number`/`type`/`coverage` | **yes**, where emitted (gcovr, coverage.py) |
| **cobertura** `condition-coverage` | `(covered/total)` count | no |
| **JaCoCo** | `mb`/`cb` counts | no |

What the fix recovers is the **categorical** loss: today a count-only report arriving second
contributes *nothing at all* — not even its floor.

---

## 4. Design

### 4.0 Why a format of our own, and why it is not istanbul's

The internal model below **is** a format we draft ourselves. Two rejected alternatives, because the
choice is not obvious and the reasoning should not have to be rediscovered:

**Rejected — normalize everything into istanbul's shape** (it is the richest input, so make it the
storage form). This fails on a one-way property: **you cannot normalize upward.** Converting a richer
format to a poorer one is mechanical; the reverse is impossible, because the *producer* already
destroyed the information. Cobertura's `condition-coverage="50% (1/2)"` is a count — no
transformation recovers which arm was taken. An istanbul-shaped record derived from cobertura would
be istanbul-shaped fields populated with fiction, which is precisely today's bug one layer up
(`CoberturaParser.cs:66-69` already invents positional edges, and those invented indices are what
makes the merge unsound). Istanbul's genuine extra richness — source locations, branch `type`s,
function coverage — is read by **nothing in the product** (§7), so adopting its shape would multiply
the size of the largest collection in the database to store fields no code path touches.

**Rejected — the intersection of what all formats express.** That is cobertura's counts, which
throws away the real identity lcov, clover and istanbul do carry.

**Chosen — the join on the one axis the product consumes.** `(n, S, F)` holds real arm identity where
a format carries it, a floor where it does not, and arity from either. Every format maps in without
inventing anything, richer formats are not truncated to the poorest, and `max` / set-union make
order-independence structural rather than tested-in.

### 4.1 Storage model

Per `(file, line)`, replacing the flat edge list:

- `n` — arity, **max** across reports
- `S` — set of **taken arm keys** (`string`), from reports carrying real identity
- `F` — floor, **max** across reports of the count-only covered number

Then `covered = max(F, |S|)`, `total = n`. Both `max` and set-union are commutative and associative,
so **order-independence is structural, not tested-in**.

Arm keys are **strings, not indices** — lcov identity is two-dimensional (`block:branch`), istanbul's
is `branchMapKey:armIndex`, clover's is `true`/`false`. Index flattening does not survive a later
report reporting a larger arity.

### 4.1a Worked example — a real production document

Pulled read-only from the production `Coverage` database, 2026-09-18 (MintPlayer repos are public;
this is coverage data):

```json
{
  "BuildId": "Commits/402741072/67262d58656fa932d363bcb3287e60c7542665ea/builds/31694883768-2",
  "Path": "libs/mintplayer-web-components/swiper-core/src/keymap.ts",
  "Matched": true,
  "BranchFormat": "lcov",
  "Lines": [
    { "Number": 8,  "Hits": 2, "Status": "Covered" },
    { "Number": 24, "Hits": 5, "Status": "Covered" }
  ],
  "Branches": [
    { "Line": 24, "BlockId": "0", "BranchId": "0", "Taken": 5 },
    { "Line": 24, "BlockId": "0", "BranchId": "1", "Taken": 1 }
  ],
  "@metadata": {
    "@id": "Commits/402741072/.../builds/31694883768-2/files/d50ddef262ab63a0091f",
    "@collection": "FileCoverages",
    "Raven-Clr-Type": "Coverage.Entities.FileCoverage, Coverage.Library"
  }
}
```

Under the new model line 24 becomes `n = 2`, `S = { "0:0", "0:1" }`, `F = 0`, so
`covered = max(0, 2) = 2`, `total = 2` — same rendered `2/2`, now order-independent. **D2 is visible
here**: the `Taken` counts `5` and `1` collapse to membership. Nothing reads them (the UI thresholds
at `> 0`), but this is the concrete datum being given up.

If a cobertura report for this file arrived second reporting `(1/2)` on line 24, today it is silently
dropped; under the new model it contributes `F = 1`, and `covered = max(1, 2) = 2` still — the floor
never drags a better-identified result down.

**Two incidental observations from the probe:**

- Every one of the 772 branch-carrying documents in the sampled slice is `BranchFormat: "lcov"`. The
  cross-format conflict is not yet widespread in stored data — which is consistent with it being
  *silent*, and with Polyglot's `ci.yml` (gcovr cobertura + lcov in one run) being the case that
  starts exercising it.
- Documents still carry `Raven-Clr-Type: Coverage.Entities.FileCoverage, Coverage.Library` — the
  pre-monorepo namespace. Harmless here because `FileCoverage` is always point-loaded typed, and a
  migration's JS does not read the CLR type. Worth knowing before anyone attempts an untyped
  `LoadAsync<object>` against this collection.

### 4.2 The parser contract change (from V1)

The current guard keys on format name. That is wrong twice over: cobertura and JaCoCo share a key
shape yet drop each other (V1), and cobertura is **arm-identified for some lines and count-only for
others** once `<conditions>` is read (A8). Identity is therefore a **per-line, per-report** property.

Replace `AddBranch(line, block, branch, taken)` with two intent-revealing calls:

- `AddBranchArm(int line, string armKey, bool taken)` — identity-carrying. Contributes to `S` and `n`.
- `AddBranchCount(int line, int covered, int total)` — count-only. Contributes to `F` and `n`, never `S`.

Cobertura calls the second for `condition-coverage` and the first when `<conditions>` is present;
JaCoCo always calls the second; lcov, clover and istanbul always call the first. `FileCoverage.BranchFormat`
becomes vestigial and is **removed** — nothing else reads it.

### 4.3 Merge semantics — max everywhere (Workstream B)

`AddLine` and `AddBranch*` become **max**, matching `CoverageMerger`. This affects only `Hits`, and
`Hits` reaches exactly one place in the product: the `12×` hover label at `file.component.ts:134`.
Every percentage, the tree, badges, the gate, patch coverage and the PR comment are `Status`-driven —
verified exhaustively (`CoverageMerger.cs:118`, `BuildFinalizer.cs:69,113`, `CommitAssembler.cs:315`,
`BrowseController.cs:528-529`, `PatchCoverageCalculator.cs:66`, `GateEvaluator.cs:57-62,114-115`;
zero `Hits` reads in the renderer or badges).

`null` (unknown) must never be demoted to `0`: it loses to any non-null in a **status** sense. Both
`(null ?? 0) + (0 ?? 0)` and `MaxNullable(null, 0)` are fixed.

### 4.4 Status recomputation

The `Any(edge is null or 0)` predicate exists in **two** places — `ParsedFile.ResolveStatuses`
(`:52-69`) and `CoverageMerger` (`:61-74`). Both become `covered < n ⇒ PartiallyCovered`.

---

## 5. Decisions required

| id | decision | recommendation |
|---|---|---|
| **D1** | Identity declared per-line by the parser (§4.2) vs per-format flag | **Per-line.** V1 shows format-level is the existing bug; A8 makes cobertura mixed within one report. |
| **D2** | lcov's `taken` **count** disappears (`BRDA:2,0,0,4` stores "taken", not `4`) | **Accept.** Nothing reads it — UI thresholds at `> 0`, `Summarize` at `is > 0`. It is the one real datum the old shape held. |
| **D3** | `null` vs `0` for branches conflates under a set model ("not in `S`") | **Accept.** Both make the line Partial; §4.4's `covered < n` preserves the outcome. Line-level `null` is *not* conflated (§4.3). |
| **D4** | `Hits` regression for generic-heavy cobertura (max, not sum) | **Accept the label regression.** Alternative — a separate non-merged `MaxHitsSeen` — is carried as rejected unless the label matters. |
| **D5** | lcov 2.x `e`/`f` markers are discarded (`TrimStart('e','f','U')`), so `e0` and `0` collide on block `"0"` | **Preserve the marker in the arm key.** Pre-existing, small, but a set model makes the collision *merge* arms that are not the same arm. Changes lcov-only numbers slightly; say so in the release note. |
| **D6** | Browse DTO: emit per-line `{ line, covered, total }` instead of the verbatim edge list | **Yes.** Internal API, SPA ships in the same image, and `file.component.ts:117-137` currently rebuilds exactly that with a `Map` — the component gets *simpler*. |
| **D7** | **Migration vs lazy read-path upgrade** (see §6) | **One-shot migration (b).** SP1 measured on production 2026-09-18: 200,230 docs, **7s** full scan, only ~25% carry branch data. Lazy read (a) is the fallback. Write-cost dry run still outstanding. |
| **D8** | A9 diagnostic shape on `ReportIngestOutcome` | Additive counters `BranchLinesIdentified` / `BranchLinesCountOnly`. Model change (V7). |

---

## 6. Migration (D7) — **SP1 has been run; measured on production 2026-09-18**

### Measured, not assumed

Read-only probe against `coverage-raven` on the production VPS:

| metric | value |
|---|---|
| `FileCoverages` documents | **200,230** (96% of all 208,177 documents in the database) |
| Database size on disk | 2.23 GB (`FileCoverage` dominates it) |
| Full collection scan, streamed over loopback | **7 seconds / 697 MB** |
| Average document size | ~3.5 KB |
| **Documents carrying branch data at all** (`BranchFormat != null`) | **24.9%** (498 of a 2,000-doc sample) ⇒ **~50,000** |
| Other collections | Builds 314, Commits 762, CommitAssemblies 150, Repositories 172, attachments 2,222 (1,516 unique) |

**This reverses the recommendation.** The pre-measurement worry (V4) was that a single-shot patch
over the largest collection could not fit the 180s readiness budget. A 7-second full scan says
otherwise: the scan floor is ~4% of the budget, and only ~50,000 of the 200,230 documents need
rewriting at all — the migration's JS can `return` immediately for the other 75%, which carry no
branch data.

**Revised recommendation: (b), a one-shot `PatchByQueryOperation`** — the idiom the repo already uses
11 times — keeping one shape in the database instead of two forever.

**Remaining unknown: the write cost.** 7s is a *read* floor; it does not measure rewriting ~50k
documents. That is the one number still missing, and measuring it means running a patch against
production, which is a write-API call — **needs your explicit go-ahead before I run it** (a no-op
`from FileCoverages update { }` exercises scan + per-document script without writing, which brackets
the cost from below but still does not price the writes themselves).

If the dry run comes back comfortably inside the budget, take (b). If it is marginal, (a) remains a
fully correct fallback that costs one branch in a derivation helper and carries no deploy risk.

### Why it looked worse than it is

A `FileCoverage` backfill runs:

- single-shot `PatchByQueryOperation` with a hard 5-minute `WaitForCompletionAsync` (the only idiom
  in the repo — 11 for 11),
- **blocking before the port opens**, so readiness cannot answer while it runs,
- against a **180s** deploy poll (`18 × 10s`, first probe at t=10s) that fails the deploy with **no
  rollback** — the container stays up,
- over the largest collection in the database: files × (1 + flags) **per build**, plus one per file
  **per commit assembly** (`FileCoverage.cs:67-77`, `CommitAssembly.cs:85-92`,
  `ParseSessionRecipient.cs:131-145`, `CommitAssembler.cs:294-324`),
- with **no chunked/resumable precedent and no Spark support for one** (`SparkMigrationRunner`'s only
  hook is a synchronous `.GetAwaiter().GetResult()`; its 30-min lock is not even renewed).

Those constraints are all real. What the measurement changes is that **none of them bind at this
volume** — the collection is large in document count but small in absolute terms, and three quarters
of it is skippable.

### Three options

**(a) Lazy read-path upgrade — fallback.** See below. Correct, zero deploy risk, costs two shapes
in the database permanently.

**(b) One-shot `PatchByQueryOperation` re-derivation — recommended post-SP1.** The issue's proposal
and the repo's only idiom, 11 times over. The 7s scan floor and the 75% skip rate make it viable;
confirm the write cost with a dry run first.

**(c) Re-parse from retained attachments — higher fidelity, highest cost.** The issue ruled this out
as a non-goal on two grounds, one of which is wrong: **the raw uploaded reports are still on the
`Build` documents** as attachments, and are deleted only when the build itself is
(`DeletePullRequestBuildsRecipient.cs:14`, `ParseSessionRecipient.cs:307-312`). So a migration
genuinely *can* re-parse rather than re-derive, and that recovers strictly more — a legacy cobertura
report re-parsed through M4's `<conditions>` reader comes back **arm-identified**, where re-derivation
can only ever grant it a floor. The issue's second objection (re-enqueuing would max-merge into
existing documents rather than rebuild them) is real but is an argument for delete-then-rebuild per
build, not against re-parsing.

Its cost is the opposite of cheap. There are 2,222 attachments (1,516 unique) across 314 builds, and
each would have to be read, un-gzipped and fully re-parsed — versus a 7-second collection scan for
(b). It would also have to delete and rebuild each build's `FileCoverage` documents rather than
patch them, and would very likely have to run outside startup entirely. Carried as the option to
reach for **only** if the fidelity gain is judged worth building deferred-migration infrastructure,
which does not exist today (V4).

**Fallback (a) — lazy read-path upgrade.**

Keep the legacy `Branches` list on the document and derive `(n, S, F)` from it on read when the new
field is absent. The derivation is *exactly* what the migration would compute:

- legacy lcov-stamped → `S` = edges with `Taken > 0` (keys preserved), `n` = row count, `F` = 0
- legacy cobertura/JaCoCo-stamped → `F` = count of `Taken > 0`, `n` = row count, `S` = ∅ *(correct —
  those indices were never identities)*

This is not a deferral and not a staged rollout (requirement 3 holds: every upload is fully
correct from the first deploy). It is a permanent decision that the legacy shape is a readable input
format. It eliminates the deploy risk entirely and costs a branch in one derivation helper.

Note that the derivation logic in (a) and the migration JS in (b) compute **exactly the same thing** —
so building (a) first and promoting it to (b) later is cheap, and neither choice is load-bearing on
the rest of the design.

---

## 7. Blast radius

Smaller than it looks. The **only** consumer of edge identity in the entire system is the file-viewer
annotation, and it immediately collapses edges to a per-line pair:

`BrowseController.cs:489` → `browse.service.ts:135-141` → `file.component.ts:117-137`. `blockId` and
`branchId` are never read client-side. Nothing else reads `FileCoverage.Branches` anywhere.

`CoverageSummary.BranchesCovered/Total` and the action outputs `branches-covered` / `branches-total` /
`branch-rate` keep their names and types; only the derivation in `Summarize` changes to `Σ n` and
`Σ max(F, |S|)`.

**No contract bump.** `UploadsController.cs:61-76` bumps only for a field removed, renamed or
repurposed *on the wire*, or a newly required endpoint. This removes and renames nothing;
`CLIENT_CONTRACT = 1` stays. A `SupportedFeatures` name (`cross-format-branches`) is optional and
additive — note it only produces **warnings** in the action today (`capabilities.ts`), never a branch.

**Numbers will move for existing repos.** Release note.

---

## 8. New parsers (Workstream C)

Both carry better identity than cobertura, so they raise §3's ceiling.

**istanbul JSON** — flat map of absolute path → `{ path, statementMap, s, fnMap, f, branchMap, b }`.
Lines from `statementMap[id].start.line` + `s[id]`; arms from `branchMap[id].locations[i]` + `b[id][i]`,
key `"<branchMapKey>:<armIndex>"`. Functions are not modelled by `ParsedFile` — skip, as lcov skips
`FN`/`FNDA`. Guard: `b` counts can be `-1`/absent for some branch types.

**clover** — `<coverage><project><package>?<file name= path=><line num= count= type="stmt|cond|method" truecount= falsecount=/>`.
⚠️ **`truecount`/`falsecount` are the number of *uncovered* paths** — `0` means taken. Getting this
backwards silently flips every partial line. `<file>` can sit directly under `<project>`, so iterate
`Descendants("file")`. No `SourceRoots` equivalent — leave `[]`.

**Sniffing hazard (V5).** The discriminator must be **structural**: cobertura has `<class filename=…>`;
clover has `<file name=…>` under `<project>`/`<package>` and a `clover=` attribute on the root. Order
clover **before** cobertura, or tighten `CoberturaParser.CanParse`. Test both directions explicitly.

**JSON safety gap.** XML goes through `SafeXml.Load` (`DtdProcessing.Ignore`, no resolver, 256 MiB cap).
There is **no equivalent for JSON**, and `ClassifyParseFailure` (`ParseSessionRecipient.cs:286-295`)
knows only `XmlException` / `ReportTooLargeException` / `InvalidDataException` — a raw `JsonException`
falls to `_ =>` and reports `malformed`. The istanbul parser needs its own size/depth guard and must
throw `InvalidDataException` / `ReportTooLargeException` to get the right rejection reason.

---

## 9. Acceptance criteria

- **A1** — order-independence as a **property test**: a fixed set of reports in **every permutation**
  (at minimum lcov+cobertura, lcov+istanbul, cobertura+clover, and lcov+cobertura+JaCoCo) yields
  byte-identical stored `FileCoverage` documents. Must fail on today's code.
- **A2** — no branch edge is discarded for any format combination; a count-only report arriving after
  an identity-carrying one contributes its floor.
- **A3** — `covered = max(F, |S|)`, `total = max(n)`; `BranchesCovered`/`BranchesTotal` derive from those.
- **A4** — the same holds on the `CommitAssembler.cs:54` path, **with a test** (none exists today, V8).
- **A5** — duplicate line **and branch** records merge with `max` in every path (V2); a report split
  across two uploads equals the same data in one upload.
- **A6** — `null` (unknown) is never demoted to `0`, in either `ParsedFile` or `CoverageMerger`.
- **A7** — istanbul JSON and clover parse including branch arms; clover is never mistaken for
  cobertura **and cobertura is never mistaken for clover**.
- **A8** — `<conditions><condition/>` is read where present, upgrading gcovr/coverage.py cobertura to
  arm-identified.
- **A9** — identity/count-only composition is surfaced in `ReportIngestOutcome`, not silent.
- **A10** — existing documents read correctly under the new model (lazy derivation per D7), or the
  migration completes inside the deploy readiness budget — decided by SP1.
- **A11** *(new)* — `ReportIngestOutcome.cs:48` and `action/README.md:76-78` no longer claim clover
  lands in `unrecognizedFormat` (V5).

## 10. Tests that encode the bug as intent — rewrite, never delete

- `CoverageMergerTests.cs:89` `Branch_detail_never_merges_across_formats` — **the bug, asserted as a
  feature.** New assertion: the second-format report contributes its floor.
- `CoverageMergerTests.cs:50`, `:117` — construct `BranchCoverage` literals / assert edge shape.
- `CoberturaParserTests.cs:70`, `JaCoCoParserTests.cs:79,91`, `LcovParserTests.cs:43,65` — assert the
  `(Line, BlockId, BranchId)` shape; `LcovParserTests` also pins the stored `taken` **count** (2) and
  the `-`→`null` distinction (D2, D3).
- `LcovParserTests.cs:82-95` `Accumulates_duplicate_DA_records` — the Workstream B pin (expects 5).
- `CoverageMergerTests.cs:27` (re-upload must not inflate — **stays green**, it is the max invariant).
- `JaCoCoParserTests.cs:69-74` (covered line `Hits` is null) — must stay green under A6.
- `browse.service.spec.ts` and the file page, per D6.

## 11. Docs to rewrite in the same PR

These are **accurate**, not aspirational — this is a reversal of a recorded decision.

- `docs/code-coverage/product-overview.md:123-128` (`BranchFormat` in the schema) and `:140` (the drop rule)
- `docs/code-coverage/build-log-m0-m10.md:233-235` — recorded as deliberate M-item 31, "Cross-format
  branch-merge guard"
- `FileCoverage.cs:37-43` and `CoverageMerger.cs:6-14` doc comments *(note: `:37-43` / `:6-14`, one
  line off the issue's citation)*
- `CoberturaParser.cs:51-52` — the false "distinct lines" premise (V3)
- `ReportIngestOutcome.cs:48` + `action/README.md:76-78` (V5, A11)

## 12. Non-goals

- Changing the merge key. `Commits/{repoId}/{sha}/builds/{runId}-{runAttempt}` stays.
- Path resolution / `PathNormalizer`.
- Re-ingesting historical builds beyond what §6's derivation recovers from stored edges. There is no
  admin re-parse action, and re-enqueuing `ParseSessionMessage` would max-merge into existing
  documents rather than rebuild them.
- Function coverage (`fnMap`/`f`, lcov `FN`/`FNDA`) — `ParsedFile` does not model it.
- The action. No input/output rename and no contract bump, so `coverage-action-publish.yml` is not
  involved and downstream repos do not re-pin.

## 13. Deployment

Touches `apps/CodeCoverage/**` (excluding `action/**`) ⇒ fires `code-coverage-deploy.yml` only.
Six repos publish to this ingest: `MintPlayer.Spark`, `MintPlayer.AspNetCore.SpaServices`,
`MintPlayer.Dotnet.Tools`, `MintPlayer.AI`, `MintPlayer.AspNetCore.Tools`, `mintplayer-ng-bootstrap`.
They pick up corrected numbers on their next upload. Under D7 there is no readiness-budget exposure.

---

*Verified against source at `d98525c4` by four parallel investigations, 2026-09-18. Where this PRD
and the issue disagree, the disagreement is recorded in §1 with file:line evidence.*

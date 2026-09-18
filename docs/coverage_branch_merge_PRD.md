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

| **V12** | **A patch script is capped at 10,000 statements per document.** The conversion walks every edge; measured against real production documents, 421 edges converts and 5,263 faults, and production holds ~770 documents above 400 edges. Found only by rehearsing against restored data — no hand-written fixture is that large. | The migration as first written would have faulted, aborted startup and failed the deploy with nothing serving. Fixed with `IgnoreMaxStepsForScript` (§6.1), and the read path no longer depends on the migration at all. |
| **V13** | **A migration *can* read attachments.** It is ordinary C# with an `IDocumentStore`; only the JavaScript patch script cannot. An earlier draft of this PRD conflated the two. | Re-opens option (c) as technically available — then rejected on evidence rather than on a false constraint. |

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
| **cobertura** `<conditions>` | per-condition `number`/`type`/`coverage` | **yes**, where emitted (gcovr, coverage.py) |
| **cobertura** `condition-coverage` | `(covered/total)` count | no |
| **JaCoCo** | `mb`/`cb` counts | no |
| **clover** | `<line type="cond" truecount= falsecount=/>` | **no** — see V11 |

> **V11 — the issue is wrong about clover, and so was an earlier draft of this PRD.**
> #420 states clover's `truecount`/`falsecount` is "real two-arm identity, unlike cobertura's opaque
> count". **Measured false** (SP2, §14). `truecount` is the number of **taken arms on the line** and
> `falsecount` the number of **untaken arms** — aggregated across every branch on that line, exactly
> like cobertura's `(covered/total)`. It is not a true-arm/false-arm pair and not a hit count.
> Clover is therefore a **count-only** format: `AddBranchCount(line, truecount, truecount + falsecount)`.

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
| **D7** | **Migration vs lazy read-path upgrade** (see §6) | **Both.** A one-shot migration converts stored documents, *and* the read path understands the legacy shape. Rehearsed against real production documents 2026-09-19: 38.7s for 200,000, after a defect that would have aborted startup was found and fixed. |
| **D8** | A9 diagnostic shape on `ReportIngestOutcome` | Additive counters `BranchLinesIdentified` / `BranchLinesCountOnly`. Model change (V7). |

---

## 6. Migration (D7) — **SP1 has been run; measured on production 2026-09-18**

### Measured, not assumed

Read-only probe against `coverage-raven` on the production VPS:

| metric | value |
|---|---|
| `FileCoverages` documents | **201,698** (96% of the database; 200,230 when first sampled a day earlier) |
| Database size on disk | 2.23 GB (`FileCoverage` dominates it) |
| Full collection scan, streamed over loopback | **7 seconds / 697 MB** |
| Average document size | ~3.5 KB |
| **Documents carrying branch data** (`BranchFormat != null`) | **53.9%** — 108,648, censused over all of them (16,203 lcov, 92,445 cobertura, **0** JaCoCo) |
| Documents above 400 edges (the statement-budget risk) | **~770**, ten of them at 5,263 edges |
| Other collections | Builds 314, Commits 762, CommitAssemblies 150, Repositories 172, attachments 2,222 (1,516 unique) |

**This reverses the recommendation.** The pre-measurement worry (V4) was that a single-shot patch
over the largest collection could not fit the 180s readiness budget. A 7-second full scan says
otherwise: the scan floor is ~4% of the budget.

**Revised recommendation: (b), a one-shot `PatchByQueryOperation`** — the idiom the repo already uses
11 times — keeping one shape in the database instead of two forever. **Plus (a)**, because the
requirement is that every report which opens today still opens after the deploy, and that should not
depend on 200,000 documents converting during startup (§6.1).

### 6.1 Rehearsal against real production documents — 2026-09-19

351 production documents were copied into a local RavenDB and the real migration run against them.
**It found a defect that would have taken the site down.**

- **A patch script is capped at 10,000 statements per document** (`Patching.MaxStepsForScript`) and
  the conversion walks every edge. Measured: **421 edges converts, 5,263 faults**, and production
  holds ~770 documents above 400 edges. A faulted patch throws, startup aborts, the deploy fails
  with nothing serving — not a degraded page, no page.
- Fixed with `QueryOperationOptions.IgnoreMaxStepsForScript`, scoped to this one operation. Making
  the loop cheaper cannot rescue it: at ~24 statements per edge the largest document needs ~125,000.
- **Write cost: 38.7s for 200,000 documents** at production scale and ratio. Against a 300s
  `WaitForCompletion`, a 180s readiness poll, a 60s healthcheck `start_period` and a 30-minute
  migration lock, nothing is close to firing.
- `Services/LegacyBranchCompatibility` converts legacy documents **as they load**, so reports render
  correctly whether or not the migration has run, has finished, or ever runs again.
- Both are covered by tests, including a 6,000-edge document that fails without the option.

**Re-parsing from attachments was reconsidered and rejected.** A migration *can* read attachments —
it is ordinary C# with an `IDocumentStore`; only the JavaScript patch script cannot, which an earlier
draft of this PRD conflated. But every retained report inspected was lcov carrying `BRDA` — real arm
identity that re-derivation already preserves exactly — with **zero `<conditions>`**. Re-parsing
would redo path normalization and flag/assembly attribution for no fidelity gain.

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

**(a) Lazy read-path upgrade — SHIPPED, alongside (b).** Correct, zero deploy risk. Costs two shapes
the code can read, which is the price of not coupling "the site works" to "the migration succeeded".

**(b) One-shot `PatchByQueryOperation` re-derivation — SHIPPED.** The issue's proposal and the repo's
only idiom, 11 times over. Viable at 38.7s for 200,000 documents, but **only with
`IgnoreMaxStepsForScript`** — see §6.1.

**(c) Re-parse from retained attachments — higher fidelity, highest cost.** The issue ruled this out
as a non-goal on two grounds, one of which is wrong: **the raw uploaded reports are still on the
`Build` documents** as attachments, and are deleted only when the build itself is
(`DeletePullRequestBuildsRecipient.cs:14`, `ParseSessionRecipient.cs:307-312`). So a migration
genuinely *can* re-parse rather than re-derive, and that recovers strictly more — a legacy cobertura
report re-parsed through M4's `<conditions>` reader comes back **arm-identified**, where re-derivation
can only ever grant it a floor. The issue's second objection (re-enqueuing would max-merge into
existing documents rather than rebuild them) is real but is an argument for delete-then-rebuild per
build, not against re-parsing.

**REJECTED on evidence (2026-09-19).** Every retained report inspected was lcov carrying `BRDA` —
identity that re-derivation preserves exactly — with **zero `<conditions>`**, so the fidelity gain is
nil for the data actually stored. Its cost is also the opposite of cheap: 1,899 report attachments
across 316 builds would each be read, un-gzipped and re-parsed, and each build's `FileCoverage`
documents deleted and rebuilt rather than patched, redoing path normalization and flag/assembly
attribution. Reach for it only if a future parser fix makes the retained reports say something the
stored documents cannot.

**(a) in detail — the lazy read-path upgrade.**

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

**istanbul JSON** — flat map of absolute path → `{ path, statementMap, s, fnMap, f, branchMap, b, meta }`.
Lines from `statementMap[id].start.line` + `s[id]`; arms from `branchMap[id].locations[i]` + `b[id][i]`,
key `"<branchMapKey>:<armIndex>"`. Functions are not modelled by `ParsedFile` — skip, as lcov skips
`FN`/`FNDA`.

⚠️ **Attribute every arm to `branchMap[id].line`, not to `locations[i].start.line`.** Measured in
SP2: **65 of ~153 branches have arms starting on a different line than the branch** (multi-line
ternaries and `binary-expr`). Per-arm attribution would split one 2-arm branch across two lines and
render every multi-line conditional as two *partial* lines. istanbul's own lcov and clover reporters
attribute to the branch line; matching them is what makes our clover and istanbul readings agree.

Observed branch `type`s: `if`, `cond-expr`, `binary-expr`. The v8 provider emitted **no** negative or
absent `b` counts across 306 arms, but guard anyway — the istanbul provider is documented to use `-1`.
Note 88 of the statements span multiple lines; attribute them to `start.line`.

**clover** — `<coverage clover="3.2.0"><project><package>?<file name= path=><line num= count= type="stmt|cond|method" truecount= falsecount=/>`.
Per V11 this is **count-only**: `covered = truecount`, `total = truecount + falsecount`. Verified
against istanbul JSON for the same run (§14) — a line with two separate `branchMap` entries, four
arms, all taken, is reported by clover as `truecount="4" falsecount="0"`, which no true/false pair
could express. `<file>` sits directly under `<project>` for the istanbul producer, so iterate
`Descendants("file")`. `RawPath` from `@path` when present, else `@name`. No `SourceRoots` — `[]`.

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

## 14. SP2 evidence — the clover/istanbul cross-check

Both formats were generated from one real run (the action's own vitest suite, v8 provider, 10 files,
99 tests) by temporarily adding `'clover'` and `'json'` reporters, then reverting. The two reports
describe the *same* execution, which is what makes them checkable against each other.

`src/capabilities.ts`, clover:

```xml
<line num="49" count="1" type="cond" truecount="1" falsecount="1"/>
<line num="55" count="8" type="cond" truecount="2" falsecount="0"/>
<line num="99" count="6" type="cond" truecount="4" falsecount="0"/>
<line num="117" count="6" type="cond" truecount="1" falsecount="1"/>
```

The same lines in istanbul JSON (`branchMap` id → `b` counts):

| line | istanbul | arms | taken | clover |
|---|---|---|---|---|
| 49 | `0: cond-expr` `b=[1,0]` | 2 | 1 | `true=1 false=1` |
| 55 | `1: if` `b=[1,7]` | 2 | 2 | `true=2 false=0` |
| 99 | `6: if` `b=[1,5]` **and** `7: binary-expr` `b=[6,5]` | **4** | **4** | `true=4 false=0` |
| 117 | `10: if` `b=[0,6]` | 2 | 1 | `true=1 false=1` |

Line 99 is the decisive one: **two separate branches, four arms, all taken** — reported by clover as
`truecount="4"`. A true-arm/false-arm pair cannot express four, and a hit-count reading cannot either
(the counts were 1, 5, 6, 5). `truecount` is the count of taken arms; `falsecount` the count of
untaken ones. Hence V11.

It also confirms istanbul is genuinely arm-identified, and that a `{branchMapKey}:{armIndex}` key
reproduces clover's totals exactly — which is the property A1 needs when the two formats merge.

---

*Verified against source at `d98525c4` by four parallel investigations, 2026-09-18, plus a read-only
production measurement (§6) and a generated-fixture cross-check (§14). Where this PRD and the issue
disagree, the disagreement is recorded in §1, V11 and §6 with evidence.*

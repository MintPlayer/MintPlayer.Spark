# Product Requirements Document: Harden the coverage upload ingest path

**Issue**: [#417](https://github.com/MintPlayer/MintPlayer.Spark/issues/417)
**Title**: CodeCoverage: accept every valid report, reject invalid ones loudly, never silently succeed
**Status**: Draft
**Created**: 2026-09-18
**Last Updated**: 2026-09-18

---

## Summary

Issue [#415](https://github.com/MintPlayer/MintPlayer.Spark/issues/415) ended with a measured root
cause: `Microsoft.Testing.Extensions.CodeCoverage` writes a UTF-8 BOM (`EF BB BF`) before `<?xml`,
the server decodes the uploaded bytes to a `string` before parsing, the BOM survives as a leading
`U+FEFF`, `XDocument.Parse` throws *"Data at the root level is invalid. Line 1, position 1."*, the
session fails, the build finalizes `CompleteWithErrors` with **zero files measured** — and the
consumer's CI stays green.

#417 generalises from that bug to its class. The BOM is worth fixing on its own, but the expensive
part was not the bug: it was that **nothing in the pipeline said anything was wrong**. Three wrong
diagnoses and a day went into a failure the server had already diagnosed internally and simply never
told anyone about.

So this PRD has three parts, in descending order of value:

1. **Never silently succeed.** A build that measured zero files is an error state, not an empty
   report — at the API, in the action's log, and on the commit page.
2. **Accept everything legitimately valid.** Parse from the byte stream, not from a pre-decoded
   string, and apply that at the one place uploaded bytes become text — which covers every format at
   once, including lcov, where the same BOM produces the same silent zero through a different parser.
3. **Reject what is genuinely invalid, specifically and per file.** Name the file and the reason, and
   do not let one bad report discard five good ones.

Plus a fourth, taken while the parser is open: **XML ingest hardening** (DTD/XXE, bounded
decompression), because a coverage server parses XML posted by CI systems, which on a public fork PR
is attacker-adjacent input.

The issue also asks — and Pieterjan asked directly — whether a **database migration** is needed to
convert stored backslash paths to forward slashes. **M0b is a spike that answers that with a query
rather than an assumption.** The code reading says no (see *The migration question* below); the spike
exists because "the code says it cannot happen" is exactly the class of claim #415 punished twice.

---

## Overview

### The mechanism, read in the source

The whole class has a single chokepoint, which is what makes it cheap to fix:

- `Ingestion/ParseSessionRecipient.cs:183-203` `ReadAttachmentText` is **the only place uploaded
  bytes become text**, for every format and for the `git ls-files` file list. It reads the Raven
  attachment into a `byte[]`, gunzips it when the `1f 8b` magic is present (`:194-200`), and ends at
  **`:202` `return Encoding.UTF8.GetString(bytes);`** — a raw decode with no preamble handling.
- `Ingestion/Parsing/ICoverageParser.cs:3-11` declares `bool CanParse(string content)` and
  `ParseResult Parse(string content)`. **Every parser takes a `string`, never a `Stream`.** The BOM
  bug is therefore in the interface, not in any one parser.
- `Parsing/CoberturaParser.cs:25` and `Parsing/JaCoCoParser.cs:30` both call the **string** overload
  `XDocument.Parse(content)`, so the `U+FEFF` reaches the XML reader as document content and throws.
- `Parsing/LcovParser.cs:19-20` `CanParse` is an ordinal `StartsWith("TN:"/"SF:")` on the first
  non-empty line, so a BOM makes lcov fail *detection* instead — the file is skipped with a warning
  and the build measures zero. Same outcome, different parser, which is why the fix belongs at the
  byte→text boundary and not per format.

### Why it is silent, in four independent places

1. **The parse throw is not per file.** `ParseSessionRecipient.cs:64` calls `parser.Parse(content)`
   inside the attachment loop but outside any per-file `try`. Any parser exception escapes to the
   outer `catch` at `:141`, so **one malformed report fails the entire session** and discards every
   report that had already merged — `SaveChangesAsync` at `:137` is never reached.
2. **`build.Coverage` is never written on that path.** `RecomputeBuildSummary` (`:168-181`) runs at
   `:138`, after the throw. `build.Coverage` therefore stays `null`, the status response emits
   `coverage: null`, and the action's `files-count` output is set from `coverage.filesCount` — so it
   comes back **empty, not `0`**. A consumer guard testing `== "0"` does not fire. This is the
   `files-count: (empty)` in the #415 A/B, explained.
3. **Two per-file skips only warn.** A missing attachment (`:53`) and an unrecognised format (`:60`)
   both `continue` with a `LogWarning` that never leaves the server. They fail the session only if
   *no* attachment parsed at all (`:128-129`).
4. **`CompleteWithErrors` is invisible by default.** It reaches a consumer only through
   `GET /api/uploads/status`, which the action polls **only under `wait-for-finalize: true`**
   (default `false`, `action.yml:54-57`). Without it the action learns nothing past "202 Accepted".
   Turning that flag on is precisely what finally produced the #415 diagnosis.

### What #416 already shipped, and what that removes from this issue

`4217e021` (PR #416) landed against #415 and closes three things the issue body still lists as open.
Recording them so nobody re-implements them:

- **`UploadStatusUnmatched(Files, TotalFiles, Sample)`** exists on the status response
  (`Controllers/UploadsController.cs:469`, populated `:418-429`, sample capped at
  `UnmatchedSampleSize = 50` `:411`), and is typed in the action (`action/src/status.ts:35-41`).
- **`files-matched` / `files-unmatched` action outputs** exist (`action.yml:92-95`, computed at
  `action/src/main.ts:325-326`).
- **The fail-on-zero guard already counts the server's number.** The issue says it "counts paths *it*
  rebased (197) rather than files the *server* resolved (0)". That was true of the consumer's
  PowerShell script and of the action before #416; it is **not** true now.
  `reportUnmatched` (`main.ts:283-303`) branches on `status.unmatched.files` vs
  `status.unmatched.totalFiles`. The rebase count is an informational log line only (`main.ts:118`),
  deliberately (`rebase.ts:19-27`). **This issue bullet is already closed** — what remains is that
  the guard is unreachable without `wait-for-finalize`, and returns silently when `unmatched` is
  absent (`main.ts:285`), which is the *whole* of the BOM case because nothing finalized with a tree.

### The migration question

**Asked directly: do we need a migration converting stored backslash paths to forward slashes?**

The code reading says **no**, for a reason stronger than "we normalise somewhere":
`PathNormalizer.Normalize` (`Ingestion/PathNormalizer.cs:25-68`) applies `Unify` — `path.Replace('\\','/')`
(`:77`) — to the raw path at **`:27`, as its first statement**, before any strategy runs, and returns
that unified string on **every** exit path including the unmatched one at `:67`. The value stored in
`FileCoverage.Path` (`CodeCoverage.Library/Entities/FileCoverage.cs:20`) is that return value
(`ParseSessionRecipient.cs:74, :108`). A backslash cannot reach it. Everything downstream is a copy:
`TreeFileSummary.Path` (`Entities/BuildTreeSummary.cs:133`) is `Path = file.Path` at
`BuildFinalizer.cs:59` and `:103`, and the assembly copies at `CommitAssembler.cs:60, 260, 313` are
the same value again — with GitHub's diff filenames re-unified defensively at `CommitAssembler.cs:286`.

A full inventory of persisted path-like fields turns up **exactly one raw one**, and it is not a
coverage path: `BuildSession.RootDir` (`Entities/BuildSession.cs:31`), stored verbatim from the form
at `UploadsController.cs:177`. Older action builds and direct API posters genuinely did send
`D:\a\repo\repo`, so backslashes are plausible there — and harmless, because its only consumer is
`PathNormalizer`'s constructor, which unifies it at `:19`. It is also the diagnostic field #415 spent
a day wishing it could read. **Leave it raw.** (`BuildSession.RawFileNames` `:28` are sanitised
attachment names, not filesystem paths; `ParsedFile.RawPath` (`Parsing/ParsedFile.cs:13`) is an
in-memory parser DTO that is never persisted.)

Raw backslashes *do* exist in the database, inside **attachment bytes** — the uploaded report bodies
and the `git ls-files` payload (`UploadsController.cs:181-190`). Those are the original artifacts and
must stay byte-exact; they are unified on read (`HeadFileList.cs:68, :80`).

Three things make this worth a spike rather than a footnote:

- **A migration here could not be a `PatchByQueryOperation`.** `FileCoverage.DocumentId` is
  `{buildId}/files/{SHA256(path)[..20]}` (`FileCoverage.cs:55-56, :70-74`), and so are
  `FlagDocumentId` (`:64-65`) and `CommitAssembly.FileDocumentId` (`CommitAssembly.cs:238-239`).
  **The path is the document id.** Rewriting `Path` in place would leave the id hashed from the old
  value, so ids and paths would silently disagree and `PatchCoverageCalculator.cs:43` — which joins
  GitHub's always-forward-slash compare filenames against those hashes — would keep missing
  (`:48 continue;`, silently zero patch coverage). A real migration would have to **re-key**:
  copy under the new id, merge on collision, delete the old. Every existing CodeCoverage migration is
  an RQL `PatchByQueryOperation` (`M_202608190900_BackfillBuildRun.cs:37`,
  `M_202609091200_BackfillBuildSessionKeys.cs:59-75`); none of them is this shape, and none of that
  precedent transfers.
- **The blast radius is not indexes, it is in-memory logic.** No RavenDB index projects any path —
  `Commits_ByRepository` is the only hand-written one and carries none, and `FileCoverage`,
  `BuildTreeSummary` and `CommitAssembly` have no `[GenerateIndex]`. All access is by document id or
  id-prefix stream, and every path operation is in memory afterwards: `BrowseController.cs:345`
  (`StartsWith(prefix, Ordinal)`), `:392-410` (`Split('/')` to build the tree),
  `PartialComparison.cs:32-57` (Ordinal dictionary keyed on `Path`). Client-side likewise:
  `commit-files-panel.component.ts:63-74` builds breadcrumbs by `split('/')`, `file.component.ts:106`
  takes the file name the same way, and `browse.service.ts:196-211` round-trips the stored path back
  as a query parameter that the server re-hashes into a document id. A stored backslash would not
  corrupt an index; it would render as one unsplittable segment and fail every lookup.
- **#415 punished exactly this kind of confidence twice.** The separator diagnosis and the missing
  `<sources>` diagnosis were both built on reading rather than measuring. A `from FileCoverages where
  Path like '%\\%'` against production is a two-minute query and settles it.

**M0b is that query**, and the decision rule is stated in advance: zero hits ⇒ **no migration**,
record the measured finding, pin the invariant with a test, move on. Any hits ⇒ the re-keying
migration is designed in this PRD before M1 ships, because a wrong `Path` is a wrong document id and
gets worse with every build that references it.

### The separator normalisation the issue comment asks for

The comment on #417 asks for separator normalisation in the endpoint, and is careful to say it is
**not fixing an observed bug** — the #415 A/B proved the same absolute backslash paths resolved all
79 files once the BOM was gone. It asks for existing behaviour to be made explicit and
regression-tested, with one edge case: a backslash is legal in a POSIX filename, so `src/weird\name.cs`
is a real file that a blanket unify would collide with `src/weird/name.cs`.

Reading the code changes what that ask costs, in a way worth stating plainly:

- The suggested implementation ("normalise a copy for comparison, keep the original for storage") is
  **not** what the code does today and is not cheap to adopt. `Normalize` unifies first and returns
  the unified path, and that returned path *is* the storage key via `DocumentId`'s hash. Keeping the
  original for storage means adding a `RawPath` field alongside `Path`, not swapping which one is
  stored.
- The suggested ordering ("try as received first; only retry with separators normalised if that
  fails") is a **behaviour change** to `Normalize`, not a comment. It is the right ordering and it
  does make `src/weird\name.cs` resolve correctly, but it restructures the strategy chain.

So this PRD adopts the comment's *ordering* (FR-12) as a small, well-tested change, adds the raw path
as an additional stored field for diagnosis (FR-13), and does **not** stop storing the normalised
path — because the hash identity depends on it, and that dependency is load-bearing rather than
incidental.

### Scope correction: Clover is not a supported format

The issue lists Clover among "the XML formats (Cobertura, Clover, JaCoCo)". **There is no Clover
parser.** `Parsing/CoverageParserFactory.cs:19-24` registers exactly three: `LcovParser`,
`CoberturaParser`, `JaCoCoParser`. The action *discovers* `**/clover.xml` (`action/src/files.ts:12`)
and rebases its `filename="…"` token (`rebase.ts:41`), so a Clover file is uploaded and then claimed
by `CoberturaParser` — whose `CanParse` is root element `== "coverage"` (`CoberturaParser.cs:17-21`),
which Clover also uses. It is mis-parsed to zero or near-zero files, silently. `**/coverage-final.json`
is globbed too (`files.ts:14`) and has no parser at all.

That is a second, independent instance of this issue's own headline — a discovered-and-uploaded file
that resolves to nothing without comment — and it is in scope for the *reporting* half (FR-5 names
it), but writing a Clover parser is **not** in scope (see *Out of Scope*).

---

## Goals & Objectives

### Primary Goals

> **Stated goal (Pieterjan, 2026-09-18): make the upload-coverage-report endpoint as resilient as
> possible.** Resilient here means three separable things, and the issue is careful to rank them:
> *tolerant* of everything legitimately valid, *specific* about everything that is not, and — above
> the other two — *never silent*. A parser that accepts more shapes but still fails quietly would
> not have saved the day #415 cost.

- A consumer cannot end a CI run believing coverage was recorded when it was not. For every upload
  the server either measures ≥ 1 file, or reports a specific, machine-readable reason.
- A legitimately valid report parses regardless of BOM, declared encoding, leading whitespace or line
  endings — for every format, fixed once at the byte→text boundary.
- One unparseable report in a batch of six ingests the other five and reports the sixth by name.
- A crafted report cannot make the server resolve an external entity or exhaust its memory.
- The `\`→`/` behaviour the server already has becomes explicit and regression-tested, and stops
  being something the next investigator has to re-derive.

### Success Metrics

- **The #415 acceptance number, verbatim**: an upload from `MintPlayer/MintPlayer.DotnetDesktop.Tools`
  with no client-side BOM strip lands `state: Complete`, `files-count: 79`, `line-rate: 56.9`.
  That repo's CI is currently **red on purpose** pending this fix; it going green is the end-to-end proof.
- `MintPlayer.DotnetDesktop.Tools#19`'s BOM-strip step is deleted, in this unit of work.
- `files-count` is never the empty string on a terminal build — `0` is a value.
- A build with zero measured files renders as a named rejection on the commit page, not as a blank.

---

## Chosen Design

### The shape

**1. Parse from bytes, at the one boundary (server).**
`ICoverageParser` changes from `string content` to a byte-oriented input. `ReadAttachmentText` becomes
`ReadAttachmentBytes`, and the decode moves behind each parser:

- The XML parsers use `XmlReader.Create(stream, hardenedSettings)` wrapped by `XDocument.Load`, which
  consumes any BOM natively and honours the prolog's `encoding=`.
- The lcov parser decodes with an encoding detected from the preamble (`StreamReader` with
  `detectEncodingFromByteOrderMarks: true`), which strips the BOM before the `SF:` match.
- `CanParse` sniffs bytes: skip a BOM and leading whitespace, then apply today's checks. The
  Cobertura/JaCoCo root-element sniff is already a regex scan (`CoberturaParser.cs:84-102`) and
  survives a BOM by accident today — that accident becomes intentional.

This is one change that covers the BOM, UTF-16, declared non-UTF-8 encodings, leading whitespace and
trailing padding, for all three formats, because there is exactly one boundary to change.

**2. Per-file ingest outcomes, and per-file failure isolation (server).**
The attachment loop gets a per-file `try`, and each attachment produces an outcome record — parsed
(with a file count) or rejected (with a named reason and the file name). The session fails only when
*every* attachment was rejected. Those outcomes are stored on the `BuildSession` and returned by
`GET /api/uploads/status` per session, so the action can print "5 of 6 reports ingested; `x.xml`
rejected: not well-formed XML at line 1".

Reasons are a closed, machine-readable set: `empty`, `unrecognizedFormat`, `malformed`,
`truncated`, `tooLarge`, `noFiles`.

**3. Zero measured files is an error state (server + action + UI).**
- `build.Coverage` is written on **every** finalize, including one where every session failed, so a
  terminal build always carries a `CoverageSummary` — zeroed rather than absent. `coverage: null`
  then means only "still in flight", which is a strictly narrower and more useful contract.
- The action emits `files-count: 0` rather than `''` on any terminal state.
- The commit page renders a build with zero measured files as a named rejection listing the per-file
  reasons, rather than as a page with no numbers.

**4. XML hardening (server).**
`DtdProcessing.Prohibit`, `XmlResolver = null`, `MaxCharactersFromEntities = 0`,
`MaxCharactersInDocument` set to a bound, on both XML parsers. Decompression is bounded by a cap on
bytes read from the `GZipStream` rather than by the compressed `MaxReportBytes = 50 MB`
(`UploadsController.cs:44`), which today bounds only the wire size.

**5. Separator ordering + raw path retention (server).**
`Normalize` tries the path as received before trying it unified (FR-12), and `FileCoverage` gains a
`RawPath` for diagnosis (FR-13). Both are small; both are regression-tested with the fixture table
from the issue comment.

### What complexity this hides

The single-boundary property is the whole reason this is affordable. Every alternative shape —
per-parser BOM strips, a normalisation step in the controller, a client-side fix in the action —
either multiplies by the number of formats or fails to reach direct API posters. There is one
`ReadAttachmentText`, and it already handles the one other byte-level concern (gzip), so it is the
established place for this.

### Designs considered (and rejected)

- **`content.TrimStart('\uFEFF')` at `ParseSessionRecipient.cs:202`.** One line, fixes #415 today.
  Rejected as the *only* fix: it handles the UTF-8 BOM and nothing else — UTF-16 decodes to mojibake
  through `Encoding.UTF8.GetString` before any trim can help, and a declared non-UTF-8 encoding is
  silently mis-decoded. It is however the correct **first commit** (M1), because it makes the
  consumer's CI green in minutes while the interface change is built and reviewed.
- **Strip the BOM in the action before upload.** Rejected: direct API posters get no client-side
  help, and the #415 discussion already settled that the action should not own byte-level report
  semantics. It also repeats the #416 shape of fixing the client for a server defect.
- **Make the server reject an upload whose reports all fail to parse (4xx at `POST /api/uploads`).**
  Rejected: parsing is asynchronous by design (`:199` broadcasts `ParseSessionMessage`; `:204`
  returns 202), and making it synchronous to get a status code would serialise a 50 MB multipart
  upload behind XML parsing. The verdict belongs on the finalize, which is where it already lives.
- **Fail the build by default when zero files are measured.** Rejected, consistent with the #415
  decision: a zero-report-file upload is legitimate (the `nx affected` carry-forward path,
  `main.ts:51-58`), and the tag is a moving one that reaches nine consumers at once. Warn by default,
  fail under the existing `fail-ci-if-error`. No new knob.
- **A `SparkMigration` that rewrites `FileCoverage.Path` backslashes.** Rejected pending M0b, and
  rejected in the `PatchByQueryOperation` form regardless — see *The migration question*.
- **Write a Clover parser now.** Rejected: the issue does not ask for it, it is not a regression, and
  bundling a new format into a hardening PR that moves a tag for every consumer is the wrong risk.
  Reporting it as a named rejection (FR-5) removes the silence, which is what this issue is about.

### The trap in the chosen design

**Making `build.Coverage` non-null on a terminal build is a contract change**, and
`docs/code-coverage/upload-api.md:4` says fields are "added, never removed or repurposed".
`upload-api.md` currently documents `coverage` as null "while `InFlight`, and whenever no session
produced any data at all". A consumer branching on `coverage === null` to mean "nothing was measured"
would silently start seeing a zeroed object instead.

The narrowing is the *point* — "empty and zero must not be different things" is the issue's own
wording — but it must be done as a documented contract change with the `capabilities` endpoint
(`UploadsController.cs:243-248`, `:60`, `:68-77`) advertising the new feature, not quietly. M4
carries that explicitly.

Second trap, smaller: changing `ICoverageParser` to bytes touches `CanParse`, which
`CoverageParserFactory.Resolve` runs over **all three parsers in order** (`:26-27`). Byte-level
sniffing must not consume a non-seekable stream — pass a `ReadOnlyMemory<byte>` or a seekable
`MemoryStream` that each sniff rewinds, or lcov's sniff will eat the bytes Cobertura's needs.

---

## Out of Scope

- **A Clover parser, and a `coverage-final.json` (Istanbul) parser.** Both are globbed by the action
  and neither is parseable server-side. — *Rationale: new format support is a feature, not hardening;
  FR-5 makes both loud instead of silent, which is this issue's actual acceptance bar. File
  separately once they are visible in real uploads.*
- **The suffix-ambiguity defect** (`PathNormalizer.cs:58-65` requires exactly one candidate; measured
  at 314 of 1405 files silently dropped on ng-bootstrap PR #405, and the reason `mintplayer-ng-seo`
  keeps `rebase-lcov-paths.mjs`). — *Rationale: a genuinely different defect with a different fix
  (a report-declared project root); already carved out by the #415 plan. It deserves its own issue.*
- **Backfilling the already-broken builds** for `MintPlayer.DotnetDesktop.Tools`. — *Rationale: a
  re-run after the fix produces a correct build; carried over unchanged from the #415 PRD.*
- **Streaming (`XmlReader`) instead of `XDocument` for large reports.** The issue raises it as a
  memory concern for monorepos. — *Rationale: ours are ~200 KB; the bound in FR-11 caps the exposure,
  and rewriting both parsers to a pull model is a performance change with no measurement behind it.
  Revisit when a real report makes it necessary.*
- **The `%5C` blob-URL fallback** (`Services/GitHubContentService.cs:57` un-escapes only `%2F`). —
  *Rationale: unreachable while paths are unified before storage; M0b is what confirms that, and if
  M0b finds backslashes this moves into scope with the migration.*

---

## Functional Requirements

### Must Have (P0)

- [ ] **FR-1**: A report byte-identical to a valid one except for a leading UTF-8 BOM parses
      identically, for every supported format. This is #415.
- [ ] **FR-2**: Uploaded bytes are parsed from the byte stream, not from a pre-decoded string, at the
      single boundary where they become text — so BOM handling, declared `encoding=`, UTF-16 and
      leading whitespace are covered once rather than per format.
- [ ] **FR-3**: An unparseable, empty or truncated report is rejected with a **named reason and the
      file name**, from a closed machine-readable set, surfaced in `GET /api/uploads/status`.
- [ ] **FR-4**: One rejected report does not discard the others. A batch of six with one truncated
      ingests five and reports the sixth. Only an all-rejected session is `Failed`.
- [ ] **FR-5**: A report whose format is not recognised — including Clover and `coverage-final.json`,
      which the action discovers and uploads today — is reported as a named rejection rather than
      warned about server-side and forgotten.
- [ ] **FR-6**: `files-count` is populated on every terminal build, including failures. **Empty and
      zero are not different things.** A terminal build always carries a `CoverageSummary`.
- [ ] **FR-7**: A build that measured zero files is presented as an error state with a reason — in
      the status response, in the action's log, and on the commit page — not as an empty report.
- [ ] **FR-8**: `CompleteWithErrors` reaches a consumer **without** opting into `wait-for-finalize`:
      the action emits a non-fatal warning annotation whenever the server reports errors.
- [ ] **FR-9**: DTD processing and external entity resolution are disabled on every XML parse
      (`DtdProcessing.Prohibit`, `XmlResolver = null`, `MaxCharactersFromEntities = 0`). A report
      carrying a DTD with an external entity resolves no entity and makes no outbound request.
- [ ] **FR-10**: Decompressed size is bounded, so a small upload cannot expand into memory
      exhaustion. Exceeding the bound is a named rejection (`tooLarge`), not a crash.
- [ ] **FR-11**: The regression fixtures in the issue's table exist as tests, **byte-level** rather
      than pretty-printed, since the class lives below the syntax.

### Should Have (P1)

- [ ] **FR-12**: `PathNormalizer` tries the path **as received** first and only retries with
      separators unified if that fails, so a POSIX file genuinely named `src/weird\name.cs` resolves
      to itself rather than colliding with `src/weird/name.cs`.
- [ ] **FR-13**: The raw report-supplied path is retained alongside the normalised one for diagnosis.
      The parser already carries it (`Parsing/ParsedFile.cs:13` `RawPath`) and it is dropped at
      `ParseSessionRecipient.cs:74`; this is persisting a value that already exists, on unmatched
      files at minimum. The normalised path remains the stored path and the hash identity —
      `FileCoverage.DocumentId` depends on it, so this is an addition, never a swap.
- [ ] **FR-16**: A parse worker that dies mid-handler does not strand the session. Today the only
      floor is the 30-minute timeout sweep (`FinalizeBuildsCronJob.cs:59-63`), which flips a
      still-`Pending` session to `Failed` — correct but slow, and it reports "never parsed" rather
      than why. A crashed or redelivered parse must be safe to retry: the merge is already
      max-based and therefore idempotent, so this is about redelivery and diagnosis, not about
      rewriting the merge.
- [ ] **FR-17**: The endpoint bounds its inputs before the work starts: attachment count per upload,
      per-file decompressed size (FR-10), and the `fileList` payload. A malformed or absent
      `rootDir`/`fileList` degrades to the documented fallback rather than throwing, and says so.
- [ ] **FR-14**: `docs/code-coverage/upload-api.md` documents the narrowed `coverage` contract and
      the per-file outcome shape, and the `capabilities` endpoint advertises the feature so an older
      client degrades knowingly.
- [ ] **FR-15**: The `\`→`/` behaviour is covered by the fixture table from the #417 comment, so it
      is explicit and regression-tested rather than re-derived by the next investigator.

---

## Timeline & Milestones

### Milestone 0: Spike — the BOM fixture and the byte-level truth

- [ ] Obtain a real BOM-carrying report from `Microsoft.Testing.Extensions.CodeCoverage` 18.11.2
      (the consumer offered to attach one; otherwise reproduce locally). Commit it as a **byte-level**
      fixture, BOM included, not a pretty-printed copy.
- [ ] Confirm by test, before any fix, that today's pipeline fails on it with exactly
      *"Data at the root level is invalid. Line 1, position 1."* — reproduce the reported failure
      rather than assuming it, which is the standing lesson of #415.
- [ ] Byte-sniff the other fixtures in the issue's table (UTF-16 LE, leading `\n\n`, 0 bytes,
      truncated, lcov+BOM) and record which of today's three parsers each one reaches.

### Milestone 0b: Spike — does a path migration exist to be done?

- [ ] Query production: `from FileCoverages where Path like '%\\%'`, and the same over
      `BuildTreeSummaries` (`Files[].Path`) and the `CommitAssembly` file documents. These collections
      are id-addressed and unindexed on path, so this is a scan — run it on the read replica or
      accept the cost once.
- [ ] Record the count in this PRD's *Technical Notes* as a **measured** verdict, with the date.
- [ ] **Decision rule, fixed in advance.** Zero hits ⇒ **no migration**; record the finding, add the
      invariant as a test (a backslash-bearing raw path never produces a backslash-bearing stored
      path, on both the matched and the unmatched exit), and close it. Any hits ⇒ the migration is
      designed **in this PRD before M1 ships**, as a **re-key** — copy to the new id, merge on
      collision, delete the old — and **never** a `PatchByQueryOperation` on `Path`, which would
      leave the id hashed from the old value and silently break `PatchCoverageCalculator.cs:43`.
- [ ] Either way, confirm `BuildSession.RootDir` stays raw and native. It is the one raw path field,
      it is harmless (unified on read at `PathNormalizer.cs:19`), and it is the diagnostic data #415
      spent a day wishing it could read.

### Milestone 1: The one-line unblock

- [ ] Strip a leading `U+FEFF` at `ParseSessionRecipient.cs:202` and ship it.
- [ ] Verify against the M0 fixture, and against `MintPlayer.DotnetDesktop.Tools`: `state: Complete`,
      `files-count: 79`, `line-rate: 56.9`.
- [ ] This is deliberately a stopgap inside the same PR, not a substitute for M2. That repo's CI is
      red on purpose right now; it should not stay red for the length of an interface change.

### Milestone 2: Parse from bytes

- [ ] Change `ICoverageParser` to a byte-oriented contract; `ReadAttachmentText` →
      `ReadAttachmentBytes` (`ParseSessionRecipient.cs:183-203`), gzip handling unchanged.
- [ ] `CoberturaParser.cs:25` / `JaCoCoParser.cs:30`: `XDocument.Load(XmlReader.Create(stream, settings))`.
- [ ] `LcovParser`: decode via `StreamReader` with BOM detection before the `SF:` match
      (`LcovParser.cs:19-20` is the silent-zero path for lcov).
- [ ] Byte-level `CanParse` sniffing that skips a BOM and leading whitespace, and **rewinds** — three
      parsers sniff the same bytes in sequence (`CoverageParserFactory.cs:26-27`).
- [ ] Delete the M1 stopgap in the same PR, once M2's tests cover it.

### Milestone 3: Per-file outcomes and failure isolation

- [ ] Wrap `parser.Parse` (`ParseSessionRecipient.cs:64`) per attachment; a throw rejects that file
      and continues rather than failing the session at `:141`.
- [ ] Turn the two silent `continue`s (`:53` missing attachment, `:60` unrecognised format) into
      recorded rejections.
- [ ] Add explicit rejections for empty (0 bytes) and truncated input, distinguished from each other
      and from an unrecognised format.
- [ ] Store the outcomes on `BuildSession`; return them per session from `GET /api/uploads/status`
      (`UploadStatusSession`, `UploadsController.cs:505-506`).
- [ ] Session is `Failed` only when every attachment was rejected (`:128-129` today).

### Milestone 4: Never silently succeed

- [ ] Write `build.Coverage` on every finalize, including an all-sessions-failed one, so a terminal
      build always carries a summary — zeroed, not absent (`BuildFinalizer.cs:14-46`).
- [ ] Action: `files-count: 0` rather than `''` on any terminal state (`main.ts:318`).
- [ ] Action: warn on `CompleteWithErrors` **without** `wait-for-finalize` — a single cheap status
      read after upload, or a non-fatal annotation from the accepted response, whichever the
      capabilities contract supports.
- [ ] Commit page: render a zero-measured-files build as a named rejection with the per-file reasons,
      not as a blank page.
- [ ] Advertise the narrowed `coverage` contract in `capabilities` (`UploadsController.cs:60, :68-77`)
      and document it in `upload-api.md` (FR-14).

### Milestone 5: XML hardening

- [ ] `XmlReaderSettings { DtdProcessing = Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0,
      MaxCharactersInDocument = <bound> }` on both XML parsers.
- [ ] Bound the bytes read out of the `GZipStream` (`ParseSessionRecipient.cs:194-200`); exceeding
      the bound is a `tooLarge` rejection. Today only the **compressed** size is bounded
      (`UploadsController.cs:44`, `:91`).
- [ ] Tests: a DTD + external entity resolves nothing and makes no outbound request; a
      billion-laughs-shaped body is rejected by name rather than by OOM.

### Milestone 6: Separator ordering, fixtures, and consumer cleanup

- [ ] `PathNormalizer.Normalize`: as-received first, unified second (FR-12), with the issue comment's
      five-row fixture table including `src/weird\name.cs`.
- [ ] `FileCoverage.RawPath` retained for diagnosis; `Path` and the hash identity unchanged (FR-13).
- [ ] Delete the BOM-strip step from `MintPlayer/MintPlayer.DotnetDesktop.Tools#19` and re-run, in
      **this** unit of work per the one-PR rule.
- [ ] Move the `coverage-upload-v1` tag.

### Milestone 7: Durability and input bounds (FR-16, FR-17)

- [ ] Bound the attachment count per upload and the `fileList` payload at the controller, alongside
      the existing `MaxReportBytes = 50 MB` (`UploadsController.cs:44`, `:91`) — which bounds the
      **compressed wire body only**.
- [ ] Make a redelivered or retried `ParseSessionMessage` safe and self-describing. The merge is
      already idempotent (`CoverageMerger.MergeInto` is max-based, and document ids are
      deterministic), so the work is redelivery on a crashed handler plus an error that says *why*
      rather than the timeout sweep's "Never parsed before the build timed out"
      (`FinalizeBuildsCronJob.cs:59-63`).
- [ ] A missing or malformed `rootDir` / `fileList` degrades to the documented fallback
      (`PathNormalizer.cs:47-48`) and records that it did, rather than silently producing the
      `Matched: false` verdict that row E of the #415 table describes.

---

## Open Questions

- [ ] **Does M0b find any stored backslash?** — *Assumption: no, on the reading at
      `PathNormalizer.cs:26`. The spike exists because #415 punished exactly this confidence twice,
      and because the remedy would be expensive enough that guessing is not acceptable.*
- [ ] **How does `CompleteWithErrors` reach a consumer who never sets `wait-for-finalize` (FR-8)?**
      The upload response is a 202 issued before parsing starts, so it cannot carry the verdict. The
      candidates are a single short status read after upload (cheap, but adds a request and a
      partial wait) or leaving it to the next run. — *Assumption: one bounded status read, warning
      only, never failing — a consumer who did not ask to wait must not start waiting.*
- [ ] **Is a zeroed `CoverageSummary` on a terminal build safe for existing consumers?** —
      *Assumption: yes, and it is the issue's explicit ask; mitigated by the capabilities flag and a
      documented contract note rather than by silence.*
- [ ] **Does the byte-oriented `CanParse` change format-detection precedence?** lcov is sniffed
      first today (`CoverageParserFactory.cs:19-24`) and the order is load-bearing. — *Assumption:
      order preserved exactly; tested by a fixture per format asserting which parser claims it.*

---

## Technical Notes (Issue-Specific)

- **One boundary, three formats.** `ParseSessionRecipient.cs:202` is the only `Encoding.UTF8.GetString`
  on the ingest path, and it feeds both XML parsers *and* lcov *and* the `git ls-files` file list.
  That is why FR-2 is cheap and why a per-format BOM strip would be the wrong shape.
- **The BOM defeats lcov differently.** Cobertura/JaCoCo *throw*; lcov fails `CanParse`
  (`LcovParser.cs:19-20`) and is skipped with a warning. Same zero-files outcome, different code
  path, and only the second one currently leaves the build `Complete` rather than `CompleteWithErrors`.
- **`XDocument.Parse` is not unhardened by default** — LINQ-to-XML prohibits DTDs through the default
  `XmlReaderSettings` — but nothing is configured explicitly anywhere in the app, and FR-9 makes the
  posture stated rather than inherited. Repo-wide, the only `XDocument`/`XmlReader` uses under
  `apps/CodeCoverage` are `CoberturaParser.cs:25`, `JaCoCoParser.cs:30` and an unrelated
  `Badges/BadgeRendererTests.cs:101`.
- **`MaxReportBytes = 50 MB` bounds the wire, not the work.** `UploadsController.cs:44` and the
  `[RequestSizeLimit]` at `:91` cap the *compressed* multipart body; the `GZipStream` copy at
  `ParseSessionRecipient.cs:194-200` is unbounded. That is the zip-bomb surface FR-10 closes.
- **`buildSession.FilesCount` is not the matched count.** `ParseSessionRecipient.cs:130` counts
  touched build-level document ids, matched or not, while `build.Coverage.FilesCount` comes from
  `Summarize(files.Where(f => f.Matched))` (`:180`). Two different numbers with similar names, both
  on the status response (`UploadStatusSession.FilesCount` vs `Coverage.FilesCount`). Worth a
  doc-comment while FR-6 is being written.
- **Where a backslash would be fatal if one ever reached storage** (carried forward from the #415
  PRD, and the reason M0b exists): `FileCoverage.DocumentId` (`FileCoverage.cs:55-56`) hashes the
  path with SHA256, so `a\b.cs` and `a/b.cs` become different documents with no possibility of
  recovery; and `PatchCoverageCalculator.cs:43` joins GitHub's always-forward-slash compare filenames
  against those hashes, silently yielding zero patch coverage (`:48 continue;`).
- **The migration facility exists, is mature, and is already wired into this app.**
  `ISparkMigration` from `libs/migrations/MintPlayer.Spark.Migrations`, discovered by a source
  generator (`MigrationRegistrationGenerator.cs`), enabled by `spark.AddMigrations()` at
  `apps/CodeCoverage/CodeCoverage/Program.cs:162`, and run by `SparkMigrationRunner.RunAtStartup`
  **after index creation and before serving traffic** (`SparkMigrationRunner.cs:18-20`), guarded by a
  `SparkMigrationRecords/{version}` marker (`:44-50`) and a cluster-wide compare-exchange lock with a
  30-minute TTL (`:15-16, :32-37`). Nine exist already in
  `apps/CodeCoverage/CodeCoverage/Migrations/`, all written as RQL `PatchByQueryOperation`
  (`M_202608190900_BackfillBuildRun.cs:37`, `M_202609091200_BackfillBuildSessionKeys.cs:59-75`, whose
  `:38-48` documents why RQL strings rather than typed symbols, and whose `:24-25` adds a startup
  gate refusing to serve while unmigrated rows remain). So if M0b finds hits the machinery is there —
  but **none of that precedent transfers to a path**, because a path change is a re-key rather than a
  field patch.
- **#416 closed three of this issue's bullets.** `UploadStatusUnmatched`, `files-matched`/`files-unmatched`,
  and the fail-on-zero guard reading the server's number. See *What #416 already shipped*. Do not
  re-implement them; do fix that they are unreachable without `wait-for-finalize` (FR-8) and that
  `unmatched` is null when nothing finalized with a tree (`main.ts:285`).
- **Distribution is a moving tag.** Consumers pin `@coverage-upload-v1`, so the action half of this
  ships to all nine repos the moment the tag moves. There is no staged rollout — which is why FR-8 is
  a warning and the failure stays behind the existing `fail-ci-if-error`.
- **`MintPlayer.DotnetDesktop.Tools` is deliberately red right now.** Its BOM strip was removed and
  its builds fail on `state != Complete`. M1 is sequenced first for that reason.

---

## Related

- Issue [#417](https://github.com/MintPlayer/MintPlayer.Spark/issues/417) and its separator comment.
- Issue [#415](https://github.com/MintPlayer/MintPlayer.Spark/issues/415) — the BOM investigation,
  including the controlled A/B that produced the `79` / `56.9` acceptance numbers.
- PR [#416](https://github.com/MintPlayer/MintPlayer.Spark/pull/416) (`4217e021`) — the in-action
  rebase and the unmatched reporting this PRD builds on.
- `docs/coverage_windows_runner_paths_PRD.md` + `_plan.md` — the #415 PRD. Its M0 closed with
  "nothing in this repository reproduces the failure"; #417 is what that verdict turned into.
- Consumer: `MintPlayer/MintPlayer.DotnetDesktop.Tools` PR #19 (the temporary BOM strip to delete).
- Server ingestion: `apps/CodeCoverage/CodeCoverage/Ingestion/` — `ParseSessionRecipient.cs`,
  `Parsing/`, `PathNormalizer.cs`, `BuildFinalizer.cs`, `BuildComparer.cs`.
- Contract: `docs/code-coverage/upload-api.md`.
- See `CLAUDE.md` for: `apps/CodeCoverage` is a production app, and the one-PR rule.

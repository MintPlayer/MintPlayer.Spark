# Product Requirements Document: Windows-runner path resilience for the coverage action

**Issue**: none yet (raised from `MintPlayer/MintPlayer.DotnetDesktop.Tools` PR #17)
**Title**: A consumer on a windows runner uploads successfully and gets an empty report
**Status**: Draft
**Created**: 2026-09-18
**Last Updated**: 2026-09-18

---

## Summary

`MintPlayer.DotnetDesktop.Tools` is the first repository to run `MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action`
from a `windows-latest` runner. Its tests pass, the upload is accepted, a build is created — and the
report page is empty. The consumer worked around it in PR #17 by adding a PowerShell step that
rewrites every `filename="…"` attribute in the cobertura files to a repository-relative forward-slash
path before the upload step runs.

The reported cause is backslash separators. **Four independent investigations refute that as a
sufficient explanation.** The action's globber normalises `\` to `/` on `win32` before matching, and
the server normalises `\` to `/` on the workspace root, on every `<source>` root, on the `git ls-files`
output and on every raw report path *before* any comparison — then strips the workspace-root prefix
case-insensitively, which is exactly what the consumer's script does by hand. On the evidence in the
repository the described input should have worked.

So this PRD does two things, and keeps them separate. **M0 is a spike** that reproduces the failure
with the real artifact and names the actual defect; nothing is designed on top of an unverified
diagnosis. **M1–M5 are the resilience work that is justified whatever M0 finds**, because the
undisputed fact is the one the consumer's script comment states plainly: the failure was *silent*.
The upload was accepted, the step was green, and the emptiness was only visible by opening the site.
A consumer should not have to normalise paths, and should never have to discover a dropped report
by eye.

---

## Overview

### What actually happens today

The action discovers report files, gzips each one, and posts them to `POST /api/uploads` alongside
two context fields the server needs in order to interpret the paths *inside* the reports:

- `rootDir` — `process.env['GITHUB_WORKSPACE'] || process.cwd()` (`action/src/context.ts:60`,
  sent at `action/src/main.ts:91`), shipped in the runner's native form (`D:\a\repo\repo` on Windows).
- `fileList` — `git ls-files -s` output (`action/src/main.ts:63`), always forward-slash, even on Windows.

The discovered file path itself is **not** a key: only `path.basename(file) + '.gz'` goes on the wire
(`action/src/main.ts:97`), and the server sanitises even that (`Ingestion/UploadAttachments.cs:6-13`).
The keys the server matches on are the paths inside the report body, resolved by
`Ingestion/PathNormalizer.cs`, constructed per-attachment at `Ingestion/ParseSessionRecipient.cs:65`.

A file that fails to resolve is kept with `Matched = false` rather than dropped — but
`ParseSessionRecipient.cs:180` computes the build headline as
`CoverageMerger.Summarize(files.Where(f => f.Matched))`. **If every file is unmatched, the build is
real, the sessions parsed cleanly, and the coverage is empty.** That is the silent-empty-report
mechanism, and it is the thing worth fixing regardless of what M0 finds.

### Why the backslash story does not hold up

Measured, with file references:

- `@actions/glob` 0.5.1 rewrites `\`→`/` in the pattern on Windows before constructing minimatch
  (`node_modules/@actions/glob/lib/internal-pattern.js:103`) and sets `nocase: IS_WINDOWS` (:98). A
  backslash is a separator there, never an escape. Six pattern shapes — including the exact
  `path.join(base, …)` and `'!' + path.join(base, …)` forms from `action/src/files.ts:53-54` — were
  run against a real Windows tree and all matched.
- `Ingestion/PathNormalizer.cs:77` `Unify(path) => path.Replace('\\','/')`, applied to `rootDir` (:19),
  to every `<source>` root (:20), to the file list (:21) and to every raw path (:27).
- `PathNormalizer.cs:30-31` strips the unified `rootDir` prefix with `OrdinalIgnoreCase` — the same
  operation as the consumer's `Rebase-CoveragePaths.ps1`.
- `PathNormalizer.cs:74-75` `LooksAbsolute` already recognises a `X:` drive letter post-unify.
- `Ingestion/HeadFileList.cs:80` and `Ingestion/PartialComparison.cs:39` unify defensively too.
- `CodeCoverage.Tests/Ingestion/PathNormalizerTests.cs:28-35` already asserts
  `C:\actions\work\src\Calculator.cs` → `src/Calculator.cs`.

The remaining candidate causes — each of which M0 must discriminate between — are:

1. `BuildSession.RootDir` arriving null or empty, so the step-1 prefix strip never fires, leaving the
   path absolute, `stillAbsolute = true`, and (with an empty file list) `Matched: false` for every file.
2. An empty or failed `git ls-files` on the Windows runner, which changes the fallback at
   `PathNormalizer.cs:47-48` from a suffix match to a bare `!stillAbsolute` verdict.
3. A path shape from `Microsoft.Testing.Extensions.CodeCoverage` that neither the root nor any
   declared `<source>` prefixes — a different drive, a resolved symlink/`8.3` short path, or a
   `<source>`-relative `filename` the union-within-one-report pick at `PathNormalizer.cs:34-41` gets
   wrong (it strips the **first** matching root, not the longest).
4. Something upstream of normalisation entirely (parse status, attachment, commit SHA).

### Naming note

The action is consumed at the moving tag `MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@coverage-upload-v1`.
**There is no reusable `upload-coverage.yml` workflow anywhere in the org** — the premise that one
exists is mistaken; consumers reference the action directly. Any fix ships by moving that tag, so the
blast radius is every consumer at once.

---

## Goals & Objectives

### Primary Goals

- Name the real defect with evidence, not inference (M0).
- Make a consumer's coverage upload work from a `windows-latest` runner with **no** path-massaging
  step in their workflow, so `Rebase-CoveragePaths.ps1` can be deleted from the consumer repo.
- Make a wholly-unmatched (or largely unmatched) upload **loud** — a warning by default, a step
  failure under `fail-ci-if-error` — instead of a green step and an empty page.
- Pin the Windows behaviour with tests that run on Linux CI, so this cannot regress unobserved.

### Success Metrics

- `MintPlayer.DotnetDesktop.Tools` `_build.yml` drops the rebase step and `tools/Rebase-CoveragePaths.ps1`,
  and the report page for the resulting commit is non-empty with a plausible percentage.
- A build whose files are ≥1 unmatched surfaces `unmatchedPaths` in the action log; a build that is
  100% unmatched fails the step when `fail-ci-if-error: true`.
- The action's and the server's path tests cover Windows-shaped inputs explicitly, on any platform.

---

## Chosen Design

The fix belongs **in the action, not in the consumer, and not only in the server** — the server
already normalises, and a server-only fix cannot reach a consumer pinned to an older tag. The action
is the single place that knows the runner's native form, so it is where the canonical form is
established.

### The shape

Three independent pieces, deliberately not one:

**1. Canonicalise at the boundary (`action/src/context.ts`, `action/src/files.ts`).**
`rootDir` becomes forward-slash at the point it is read, so everything the action sends downstream —
`rootDir` on the form, the relative paths it logs — is already in the form the server unifies to.
The action stops depending on the server choosing to normalise. One exported helper:

```ts
/** The canonical wire form for every path this action sends: forward slashes, no trailing slash. */
export const toPosixPath = (p: string): string => p.replace(/\\/g, '/');
```

`ctx.rootDir` keeps its native form for **local** use (`glob`, `fs`, `git -C`) and is converted only
where it crosses the wire (`main.ts:91`). Conflating the two would break the globber, which wants
native paths for its search root.

**2. Rebase the report contents in the action, not in the consumer (`action/src/rebase.ts`, new).**
Before gzipping, rewrite each report's embedded paths to workspace-relative forward-slash form — the
job `Rebase-CoveragePaths.ps1` does today, moved behind the action's boundary and generalised past
cobertura (`filename=`, lcov `SF:`, jacoco `sourcefile`). **This is the piece that discharges FR-0**,
and it is not gated on M0.

It duplicates logic the server also performs, and that is a deliberate, stated trade. The objection
(raised in issue #415) is that the action would have to understand report formats the server already
parses. True — but the rebase needs only to recognise a *path-bearing token* per format, not to parse
coverage semantics, which is perhaps thirty lines per format against a regex. Set that against the
alternative: a consumer-side script in every repo that ever runs a non-Linux runner, which is what we
have today in two repos. The asymmetry is decisive. The action is also the only layer that knows
`GITHUB_WORKSPACE` with certainty — the server knows only what arrived, and on the upload that
prompted this, its equivalent demonstrably did not fire.

**3. Make the silence loud (`action/src/main.ts`, `action/src/status.ts`, and the server's status response).**
The signal already exists and already travels: `Ingestion/BuildComparer.cs:55-56` adds
`"unmatchedPaths"` to `UploadStatusProjection.IncompleteReasons`, which is returned by
`UploadsController.cs` in `UploadStatusResponse.Projection` and already typed in the action at
`action/src/status.ts`. **Nothing consumes it.** The work is to add explicit unmatched counts to the
status response and to branch on them in the action's finalize wait.

### Designs considered (and rejected)

- *Fix it only in the server's `PathNormalizer`* — rejected: the server already does this, and a
  server-side fix reaches nobody until we also know what actually broke. It also leaves the failure
  silent, which is the more valuable half of the bug.
- *Add a `path-prefix` / `rebase-root` input to `action.yml`* — rejected: it makes the consumer
  responsible for a detail they cannot be expected to reason about, which is precisely the complaint.
  The workspace root is already known to the action.
- *Publish a reusable `upload-coverage.yml` that wraps the rebase step* — rejected: no such workflow
  exists today, so this adds a whole distribution surface to solve a problem that belongs one layer down.
- *Keep the consumer's PowerShell step and document it* — rejected by the stated requirement: the
  consumer should not have to care about separators.
- *Have the server reject an upload where 100% of files are unmatched* — rejected: an upload that
  carries only a file list is legitimate (`main.ts:49-56`, the `nx affected` path), and a hard server
  rejection would break carry-forward. The verdict belongs in the action, under `fail-ci-if-error`.

### The trap in the chosen design

`toPosixPath` must **not** be applied to the paths handed to `glob`, `fs.readFileSync` or `git -C`.
`@actions/glob` re-normalises `/`→`\` on Windows internally (`internal-path-helper.js:172-184`), so
converting early is harmless there but pointless; `fs` tolerates both; but converting `rootDir` in
`context.ts` wholesale and then using it as a *search base* mixes the two roles and will read as a
bug the next time someone touches `files.ts`. Keep native for local I/O, posix for the wire, and say
so in a comment at the conversion site.

---

## Out of Scope

- **The relative-glob anchoring gap.** `action/src/files.ts:45` passes an explicit relative `files:`
  pattern straight to `glob.create`, which roots it at `process.cwd()` rather than at `ctx.rootDir`
  like every other path in the action. A step-level `working-directory` therefore resolves `files:`
  and `directory:` against different roots. — *Rationale: real, platform-independent, and unrelated
  to this failure; file it, do not bundle the behaviour change into a tag move that reaches every
  consumer at once.*
- **The union-within-one-report source-root pick** (`PathNormalizer.cs:34-41` strips the first
  prefix-matching `<source>`, not the longest). — *Rationale: unless M0 shows it is the cause, this is
  a latent sharp edge the file-list suffix match recovers from in practice.*
- **The `%5C` blob-URL fallback** (`Services/GitHubContentService.cs:57` un-escapes only `%2F`, so a
  stored backslash would 404 rather than miss silently). — *Rationale: unreachable while paths are
  normalised before storage; note it, do not fix it speculatively.*
- **Migrating other consumers to windows runners.** Nine repos call the action; eight are ubuntu and
  one is `ubuntu-22.04`. — *Rationale: nothing to do until someone wants it.*
- **Backfilling the already-uploaded empty builds** for `MintPlayer.DotnetDesktop.Tools`. —
  *Rationale: a re-run after the fix produces a correct build; historical empties are not worth a
  migration.*

---

## Functional Requirements

### Must Have (P0)

- [ ] **FR-0** *(governing requirement)*: **No consumer repository carries code to strip a workspace
      prefix or fix separators.** A workflow author never reasons about separators or absolute CI
      paths. `Rebase-CoveragePaths.ps1` is deleted from `MintPlayer.DotnetDesktop.Tools`, and no
      future consumer needs an equivalent. Every other requirement below is subordinate to this one;
      where they conflict, this wins.
      *Scope correction (2026-09-18): this does **not** cover `mintplayer-ng-seo`'s
      `rebase-lcov-paths.mjs`, which solves the opposite problem — see Milestone 5.*
- [ ] **FR-1**: The real defect behind the empty report is reproduced and named, with the failing
      input recorded in the repository as a test fixture.
- [ ] **FR-2**: Every path the action sends over the wire is forward-slash, regardless of runner OS.
- [ ] **FR-3**: A `windows-latest` consumer with no path-massaging step produces a non-empty report.
- [ ] **FR-4**: The status response carries the unmatched-file count and a bounded sample of unmatched
      paths, not only the `unmatchedPaths` projection reason.
- [ ] **FR-5**: When any file is unmatched, the action logs a warning naming the count and a sample.
- [ ] **FR-6**: When **all** files are unmatched, the action fails the step under
      `fail-ci-if-error: true` and warns loudly otherwise. A file-list-only upload (zero report files)
      is explicitly not this case.
- [ ] **FR-7**: Action tests cover Windows-shaped `GITHUB_WORKSPACE` and Windows-shaped report paths
      on any host platform (no `process.platform` dependence).
- [ ] **FR-8**: A server test drives a Windows-absolute cobertura report end-to-end through ingestion
      to a matched `FileCoverage` document id — the gap `PathNormalizerTests` leaves open.

- [ ] **FR-9** *(promoted from P1)*: The action rebases report-internal paths to workspace-relative
      forward-slash form before upload — separators unified and the `GITHUB_WORKSPACE` prefix
      stripped — for cobertura/clover `filename=`, lcov `SF:` and jacoco `sourcefile`. This is
      `Rebase-CoveragePaths.ps1` absorbed behind the action's boundary, and it is what actually
      discharges FR-0. Idempotent: a second pass rewrites nothing. **Not** gated on M0.

### Should Have (P1)
- [ ] **FR-10**: `apps/CodeCoverage/action/README.md` states the path contract: the action sends posix
      paths, the consumer is not responsible for separators, and unmatched files are reported.
- [ ] **FR-11**: The `files-count` output and the log line distinguish "matched" from "parsed", so a
      green step with `files-count: 0` cannot be misread.

---

## Timeline & Milestones

### Milestone 0: Spike — reproduce and name the defect

- [ ] Obtain a real cobertura artifact from a `MintPlayer.DotnetDesktop.Tools` windows run (workflow
      artifact, or reproduce locally with `Microsoft.Testing.Extensions.CodeCoverage`). Record the
      literal `<sources>` block and two `filename` attributes verbatim.
- [ ] Capture the `rootDir` and `fileList` that run actually sent — read the step log, and if it is
      not there, add a `core.debug` and re-run rather than inferring.
- [ ] Drive that exact triple `(rootDir, sourceRoots, fileList, rawPath)` through `PathNormalizer` in
      a throwaway test and record `(Path, Matched)`.
- [ ] Discriminate between the four candidates in **Overview → remaining candidate causes** and write
      the verdict into this PRD's *Technical Notes* before any code is written.
- [ ] If the verdict is "none of these — normalisation was never reached", stop and re-plan: the
      milestones below assume a path-resolution defect.

### Milestone 1: Canonical posix wire form in the action

- [ ] Add `toPosixPath` to `action/src/context.ts` (or a small `paths.ts`) with the native-vs-wire
      comment from *The trap in the chosen design*.
- [ ] Apply it at `action/src/main.ts:91` (`form.set('rootDir', …)`) and at the `path.relative` log
      line (`main.ts:59`), leaving `ctx.rootDir` native for `glob`/`fs`/`git`.
- [ ] `action/src/context.test.ts`: add a Windows-shaped `GITHUB_WORKSPACE` case
      (`D:\a\repo\repo`) asserting the posix wire form — the existing assertions at :29 and :150 are
      POSIX-only.

### Milestone 1b: Action — absorb the rebase (FR-0, FR-9) — *the milestone that retires the scripts*

- [ ] `action/src/rebase.ts`: rewrite report-internal paths to workspace-relative posix before gzip.
      Unify separators, then strip the `GITHUB_WORKSPACE` prefix case-insensitively. Idempotent.
- [ ] Per-format token handling: cobertura/clover `filename="…"`, lcov `SF:…`, jacoco `sourcefile="…"`.
      Recognise the path-bearing token only — do not parse coverage semantics.
- [ ] Leave a path untouched when it neither is absolute nor carries the workspace prefix (a report
      that is already repo-relative must survive unchanged).
- [ ] Count rewrites and log them; surface the count so a zero-rewrite run on absolute input is
      visible. Do **not** hard-fail on zero — unlike the consumer script, a legitimately
      repo-relative report rewrites nothing. The loud failure is M4's unmatched verdict instead.
- [ ] Unit tests per format, plus an idempotence test and a Windows-absolute fixture.

### Milestone 2: Server — surface what was unmatched

- [ ] Add `UnmatchedFiles` (count) and a bounded `UnmatchedSample` to `UploadStatusResponse`
      (`Controllers/UploadsController.cs:407`), sourced the same way `BrowseController.cs:366-369`
      already computes them (reuse the `UnmatchedSampleSize = 50` bound).
- [ ] Mirror the new fields in `action/src/status.ts`'s `UploadStatus` interface.
- [ ] Server test asserting the fields are populated for a build with unmatched files.

### Milestone 3: Server — Windows end-to-end ingestion test

- [ ] Add a cobertura fixture with `D:\a\repo\repo\…` absolute `filename` attributes and a Windows
      `<source>`, using the literals captured in M0.
- [ ] Extend `CodeCoverage.Tests/Ingestion/ParseSessionRecipientTests.cs` to drive it with a Windows
      `RootDir` and a forward-slash `git ls-files` file list, asserting a matched `FileCoverage` with
      the expected document id (FR-8).
- [ ] Add the empty-file-list variant, which is the branch at `PathNormalizer.cs:47-48` that turns an
      unstripped absolute path into `Matched: false`.

### Milestone 4: Action — make the silence loud

- [ ] In the finalize-wait path (`action/src/main.ts:171-185` and the status handling around :247),
      branch on the new unmatched fields: warn with count + sample when > 0.
- [ ] Fail under `fail-ci-if-error: true` when report files were uploaded and **every** file is
      unmatched; explicitly exempt the zero-report-file carry-forward path (`main.ts:49-56`).
- [ ] Distinguish matched from parsed in the `files-count` output and the summary log (FR-11).
- [ ] `action/src/outputs.test.ts` / `status` tests for: some unmatched, all unmatched with the flag
      on and off, and the carry-forward exemption.

### Milestone 5: Optional in-action rebase, docs, and consumer cleanup

- [ ] Update `apps/CodeCoverage/action/README.md` with the path contract (FR-10).
- [ ] Move the `coverage-upload-v1` tag.
- [ ] In `MintPlayer/MintPlayer.DotnetDesktop.Tools`: delete `tools/Rebase-CoveragePaths.ps1` and the
      "Rebase coverage paths to repository-relative" step from `_build.yml` (the step at
      `_build.yml:102`), re-run, confirm a non-empty report (FR-3).
- [ ] Grep the org for any other pre-upload path-massaging step of the **prefix-stripping** kind and
      retire it.
- [ ] **`mintplayer-ng-seo` keeps `tools/scripts/rebase-lcov-paths.mjs` — it is a different problem.**
      Corrected after reading it: Vitest emits `SF:` paths relative to each *project's* root
      (`dock/index.ts` for `libs/mintplayer-web-components/dock/index.ts`), so that script **adds** a
      prefix inferred from the report's own directory. This work **strips** a workspace prefix. A
      path that is too short is not a path that is too long, so the in-action rebase does nothing for
      it. The underlying defect is suffix **ambiguity** — `dock/index.ts` exists under four libraries
      and `PathNormalizer.cs:64` needs exactly one candidate — measured at 314 of 1405 files (22.3%)
      silently dropped on ng-bootstrap PR #405. That deserves its own issue (a report-declared
      project root the server could trust, rather than every consumer inferring one); it is **not**
      in FR-0's scope, which is about separators and absolute workspace paths.
      **All of this lands in the same unit of work**, per the one-PR rule — sequenced after the tag move.

---

## Open Questions

- [x] ~~**Does FR-9 (the in-action rebase) survive M0?**~~ **Closed 2026-09-18: yes, and it is P0.**
      FR-0 governs — no consumer may carry a rebase script — and only the action can guarantee that
      independently of what M0 names. The duplication against the server's normaliser is accepted
      and documented in *Chosen Design*.
- [ ] **Should a 100%-unmatched build fail by default, rather than only under `fail-ci-if-error`?**
      It is the difference between "we tell you" and "we stop you", and the tag move reaches every
      consumer at once. — *Assumption: warn by default, fail only under the existing flag — no new
      knob, consistent with the comment at `main.ts:171`.*
- [ ] **Is `Microsoft.Testing.Extensions.CodeCoverage` output shaped differently from coverlet's in
      ways beyond absolute paths?** Every other consumer uses coverlet. — *Assumption: only the
      absolute-path difference matters; M0's fixture settles it.*

---

## Technical Notes (Issue-Specific)

- **Distribution is a moving tag.** Consumers pin `@coverage-upload-v1`, so the fix ships the moment
  the tag moves, to all nine repos simultaneously. There is no staged rollout. This is the reason
  FR-6's default is a warning rather than a failure.
- **`MintPlayer.DotnetDesktop.Tools` did not appear in a `search_code` sweep** for `coverage-upload-v1`
  — the one at-risk repo was invisible to the inventory that found the other nine. Do not trust code
  search as the consumer list.
- **The consumer's script hard-fails when it rebases zero paths**, deliberately converting a silent
  empty report into a red build. That instinct is correct and is what FR-5/FR-6 move into the action.
- **Where a backslash would genuinely be fatal if one ever reached storage**: `FileCoverage.DocumentId`
  (`CodeCoverage.Library/Entities/FileCoverage.cs:55-56`) hashes the path with SHA256, so
  `a\b.cs` and `a/b.cs` become different documents with no possibility of recovery; and
  `PatchCoverageCalculator.cs:43` joins GitHub's always-forward-slash compare filenames against those
  hashes, silently yielding zero patch coverage (`:48 continue;`). Normalisation before hashing is
  load-bearing, not cosmetic.
- **M0 partial verdict (2026-09-18, measured against the real `PathNormalizer`):** the literal values
  from issue #415 were driven through the class in a throwaway xUnit test. With
  `rootDir = D:\a\MintPlayer.DotnetDesktop.Tools\MintPlayer.DotnetDesktop.Tools`, the `Mesh.cs`
  absolute Windows `filename`, and `ThreeDee/MintPlayer.ThreeDee/Geometry/Mesh.cs` as the file list:
  **A** rootDir + file list → `Matched=True`; **B** + a Windows `<source>` → `True`;
  **C** rootDir **null**, file list present → `True` (the suffix match recovers);
  **D** rootDir present, file list **empty** → `True`;
  **E** rootDir null **and** file list empty → `Matched=False`, path left as `D:/a/…/Mesh.cs`.
  **The separator is ruled out.** Row E is the only failing shape, so the surviving hypothesis is that
  *both* the workspace root and the file list were absent on that upload — or that the defect is
  upstream of normalisation entirely.
- **Also ruled out: a pinned tag predating `rootDir`.** `coverage-upload-v1` → `9db74413`, which is
  after `85d00911` (the commit that added `form.set('rootDir', …)`), and the bundled `dist/index.js`
  at that tag contains it. So the running action does send the workspace root.
- **M0 CLOSED (2026-09-18) — the pipeline handles it end to end.** `UploadActionWindowsPathsTests`
  drives the **committed action bundle** against a **live server** with a real git workspace and a
  cobertura report carrying an absolute `<source>` and absolute backslash `filename` attributes, in
  the shape issue #415 quotes. Result: `Matched=True`, path stored as
  `ThreeDee/MintPlayer.ThreeDee/Geometry/Mesh.cs`, build coverage `1/2` lines over `1` file. The
  action, the form, the file list and the normaliser all handle the Windows shape correctly.
  **Nothing in this repository reproduces the reported failure**, so its cause is specific to that
  run's environment rather than to path separators — and FR-0 (no consumer carries rebasing code) is
  what the implementation delivers regardless.
- **Still open, and now the only unexplained thing:** why *that* upload produced an empty report. From
  session `31f61436b780` of build
  `Commits/215885397/d5d374e49c7ffa655c4bf0d7f1ed2f34844723e6/builds/35335931363-1` — the stored
  `BuildSession.RootDir`, the file-list attachment, and the pre-rebase `<sources>` block.
- **Consequence for FR-9 / Open Question 1: the in-action rebase is REINSTATED as P0.** It was
  briefly dropped on issue #415's argument that the action should not parse report bodies. That
  argument is about tidiness; the governing requirement (FR-0) is that **no consumer repository may
  carry a rebase script**, and only the action can guarantee that. The action knows `GITHUB_WORKSPACE`
  with certainty; the server knows only what arrived, and on the upload in question its equivalent
  demonstrably did not fire. A server-only fix leaves every consumer carrying a script if M0 turns up
  a cause other than the one assumed. Do the rebase in the action **and** fix whatever M0 names.
- **Consequence for FR-2 (posix wire form): keep it, but it is hardening, not the fix.** Nothing
  measured depends on it; it removes the action's dependence on the server choosing to normalise.

---

## Related

- Consumer workaround: `MintPlayer/MintPlayer.DotnetDesktop.Tools` PR #17, commit `6f5803b`
  (`tools/Rebase-CoveragePaths.ps1` + the "Rebase coverage paths to repository-relative" step).
- Action: `apps/CodeCoverage/action` — `src/context.ts`, `src/files.ts`, `src/main.ts`, `action.yml`.
- Server ingestion: `apps/CodeCoverage/CodeCoverage/Ingestion/` — `PathNormalizer.cs`,
  `ParseSessionRecipient.cs`, `HeadFileList.cs`, `BuildComparer.cs`.
- See `CLAUDE.md` for: `apps/` holds a production app (CodeCoverage), and the one-PR rule.
- `docs/code-coverage/README.md` for the app's wider documentation set.

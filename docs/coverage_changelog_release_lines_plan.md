# Changelog × release lines — plan

PRD: [coverage_changelog_release_lines_PRD.md](coverage_changelog_release_lines_PRD.md). Branch
`fix/changelog-release-lines`, one pull request.

## Status

**2026-11-23:** branch cut from master `79e73922e`. Spark's repository has Changelog = Keep (set in the UI). The upcoming
view shows master with no packages (PRD "The problem"). Three read-only investigation passes done (PRD
"Investigation": G1–G6); decisions R1–R5 and spikes S1–S3 written. S3 done (REST reference).

**M1 done (2026-11-23):** `VersioningPlanPromotedTests` (4 tests). Red first: the line plan at main's head failed with
"The commit … is not on release/dotnet-10.x", and `promoted` on the default branch returned 200. Fix: `Promoted` on
`VersioningPlanRequestWire` → endpoint (400 `promoted` unless the branch is a line) → `VersioningPlanJob` →
`PlanAsync` fetches and checks ancestry against the default branch instead of the line. Green: 8/8 with
`VersioningPlanTests`. S1 proven: after a second promotion (#2 promoted and tagged 10.0.1), the plan at main's head lists
Core with notes "- a leak on stop (#3)" only.

**M2 done (2026-11-23):** `UpcomingChangelogRecipientTests`. The first test moved to lines off (`versioning:` plain,
`release/11.x`): with lines off, the default branch keeps its row. Two new tests, both stores. Red first: 4 failures
(backport lines: main was planned and listed; promote: no promoted plan). Fix in `UpcomingChangelogRecipient`:
- With lines on, a default-branch push plans nothing for the default branch. It stores the default branch's document as
  the pattern cache only, with empty releases.
- With `current: promote`, it lists the line branches and plans each `pattern.CurrentLines(...)` (the same rule
  `/release` uses, so S2 needed no extraction) with `Promoted = true` at the default branch's sha.
- `ForRepositoryAsync` hides the default branch's document when it has a `LinePattern`.
- `GitWriterVersioningPlanRequest.Promoted` and `VersioningPlans.PlanAsync(promoted:)` were added.

Green: 19/19 with `ChangelogQueryActionsTests`.

**M3 done (2026-11-23):**
- **C#:** `VersionReleaseRecipientTests.Only_a_current_line_s_run_Release_becomes_latest` (3 cases). It was red only on the
  older-line case (dotnet-11.x while dotnet-12.x exists: `Latest` was true). `VersionReleaseRecipient.MayBeLatestAsync`
  reads coverage.yml and lists the line branches; it applies `pattern.CurrentLines`. A forge error gives false, so GitHub
  keeps the current Latest. Green: 10/10.
- **TS:** `isCurrentLine` in `releases.ts`. It ports the pattern regex and calls `git/matching-refs/heads/<prefix>`.
  `RunReleaseRequest.latest` feeds `make_latest`. `main.ts` passes `section.lines` on a run and re-reads the section on a
  release (a read failure only warns).
- **Caveat, no TS red:** the TS implementation was written before its tests ran, so there was no observed red on that
  side.
- **Checks:** 31/31 in `releases.test.ts` and `main.test.ts`; `tsc --noEmit` clean; `dist` rebuilt in the main checkout.

**M4 done (2026-11-23):** `PrTitleRecipientTests.The_app_s_backport_pull_request_keeps_its_title_and_asks_nothing`.
- **Red:** the title `[dotnet-10.x] fix: Crash on empty input (#12)` was rewritten to `[dotnet-10.x] fix: Crash on empty
  input`, losing `(#12)`.
- **Fix:** `PrTitleRecipient` skips a pull request whose body carries the app's `backport-of` marker for this repository
  (`BackportProvenance.FromMarker`). The check completes neutral with "Title left alone", names the source and shows no
  buttons.
- **Scope:** only the app's own backports are skipped. A hand-made "Backport of #N" pull request is still corrected,
  because its title is the author's own.
- **Green:** 11/11.

**M5 done (2026-11-23):** `ReleaseNotesTests`, two new tests.
- **A second promotion** (since = the first promotion's merge): only `fix: second (#2)` is listed. The docs-only commit is
  filtered out by path. Walking from master's head, as R1 plans, gives the same list.
- **A merge-queue backport** whose squash message carries `Backport-of: #609` among `Co-authored-by` lines, plus its
  original arriving through a promotion: one entry, `null id (#609)`.
- **Both passed on the first run.** This is coverage of edges that already worked, so there was no red and nothing to
  fix. 11/11.
- The 200-commit cap is not tested here: it is a constant, and a 200-commit fixture costs more than it proves.

**M6 done (2026-11-23):**
- **C#: no change.** The loop already advances only on `Exists`, because failures throw (PRD R5 correction).
- **TS red:** a 422 `already_exists` between the lookup and the create returned `'failed'`
  (`releases.test.ts` "moves to the next suffix…").
- **TS fix:** `createRunRelease` now runs one loop per suffix (look up, then create). A 422 `already_exists` counts as
  taken and moves on to the next suffix. `freeTag` became `isFreeTag`.
- **Checks:** green 32/32; `tsc` clean; `dist` rebuilt.

**Side request (owner, 2026-11-23):** MailManager input sanitization audit. Content and attributes are HTML-encoded by
default (`SparkMailRenderer.cs:168`). URL schemes in `href` are not checked (`SparkNotifier.cs:144-148`), and there are no
renderer tests. Filed as **#706** (not part of this PR).

**M7 done (2026-11-23):**
- `docs/code-coverage/changelog.md`: the run Release is latest only on a current line, and the `.n` suffix is explained.
  The upcoming notes are described for lines off, promote lines and older/backport lines. Troubleshooting has two rows.
- #693 PRD D3 "Consequence": the "non-merge commits" text is struck through and points to the D4 M1 revision and here.
- `UpcomingChangelog` summary updated. It is not mirrored in the model JSON, so no resync.

**M8 (2026-11-23):** pushed; **PR #707** opened. **NEXT:** the checks, then patch coverage via the codecoverage MCP; the
owner queues.

**Done (2026-11-23):** PR #707 merged (master `2ac96dc0a`) and deployed. Recorded here by the #706 pull request, the
next one to touch these docs. Spark's upcoming view fills on the next master push; `/release` on Spark awaits the owner.

## Test harness (investigation pass 3)

- **Real git, real plan job:** `CodeCoverage.GitWriter.Tests` — `VersioningPlanTests` / `VersioningPlanReleaseTests` on
  `WriterTestBase` (in-process `WriterHost` + `GitHttpServer`), fixture `Origin` (`_Infrastructure/Fixture.cs:90`:
  `Write`, `Commit`, `Checkout`, `Push`, `Head`, `Ref`), tags via `PushTag` (`VersioningPlanReleaseTests.cs:115`).
  Notes example: `VersioningPlanTests.cs:70` (`PostAsync` at `:15` hardcodes `branch="main"`). Lines settings:
  `ReleaseLinesWire { Text = ReleaseLinePattern.DefaultText }` (`:120`); line request `Line = new ReleaseLine("dotnet", 10)`.
  `PromoteTests.cs:13,17` builds promote requests. Needs only `git` on PATH.
- **Server side:** `UpcomingChangelogRecipientTests` (substituted `IGitWriterSidecar`, both stores);
  `ReleaseFlowTests.PromoteYml` (`:28`) and `git.ListBranchesAsync` stub (`:46`) for the current lines.

## Spikes

S1–S3 as in the PRD ("Spikes"). S1 doubles as M1's first red test.

## Milestones

Each: red test first, then the change, then the test green. Single test classes only; CI is the full gate.

| # | Milestone | Decision | Notes |
|---|---|---|---|
| M1 | **Writer: plan a line at a default-branch sha.** A target-line field on the plan request and wire; fetch the default branch; accept a commit on it; `Line`/`BranchMajor` from the target. | R1, S1 | `VersioningPlanTests`, real git. |
| M2 | **Server: compose per current line on a default-branch push** (promote mode); shared current-line rule; default-branch row hidden with lines on. | R1, S2 | `UpcomingChangelogRecipientTests` with `PromoteYml`, both stores; `ChangelogQueryActionsTests`. |
| M3 | **Run Release latest only for a current line** (C# + TS, `dist` rebuilt in the main checkout). | R2, S3 | `VersionReleaseRecipient` tests; `releases.test.ts`. |
| M4 | **Title correction skips backport PRs.** | R3 | `PrTitleRecipient` tests with a bracketed backport title. |
| M5 | **Promote-notes edges** (second promotion, path filter, trailer + co-authors). | R4 | `ReleaseNotesTests`. |
| M6 | **Run Release suffix only on "already exists"** (C# + TS). | R5 | |
| M7 | **Docs:** `docs/code-coverage/changelog.md` (line repositories), #693 PRD wording fix and pointer here. | all | |
| M8 | **Gate:** push, PR, checks, patch coverage via the MCP; the owner queues. | | |

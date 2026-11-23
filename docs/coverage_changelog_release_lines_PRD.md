# Changelog × release lines — PRD

Follow-up to #693 (`coverage_changelog_PRD.md`, PR #704) and the release-lines work (`coverage_backport_plan.md`,
`release_lines_recovery_plan.md`). Plan: [coverage_changelog_release_lines_plan.md](coverage_changelog_release_lines_plan.md).
Branch `fix/changelog-release-lines`, **one pull request**.

## The problem, stated plainly

After #704 was deployed and Spark's repository got **Changelog = Keep a changelog** (2026-11-23), the repository page's
upcoming changelog showed one row, `master` at `79e7392`, with **no packages**.

Spark releases with `lines: { current: promote, trigger: manual }`:
- master tags nothing;
- `/release` promotes master into each current line (`release/dotnet-11.x`, `release/angular-22.x`), and the line's run
  tags and releases.

The upcoming view (#693 Q12, `UpcomingChangelogRecipients.cs`) composes only:
- the pushed branch's own plan — for master that is "nothing", since master releases nothing (`:91-93`);
- a line when the line is pushed (`:39-47`) — and a promote line is pushed only by `/release` itself, after which
  nothing is pending.

So for a promote repository the view never shows what Q12 asked for: "per release line, the entries since the line's
last tag" — i.e. what the next `/release` would ship.

**Owner's goal (2026-11-23):** "make the release-lines feature and changelog feature work together flawlessly". Tests
first (red), then the fix (green).

## Investigation (2026-11-23)

Three read-only passes: the promote/plan path, every changelog × lines interaction, and the test harness. Results below.

### Pass 1: the promote and plan path

Paths under `apps/CodeCoverage/`.

- **`/release`** is `CodeCoverage/PackageVersioning/ReleaseRecipients.cs` (`ReleaseRecipient`, :58-209). It needs
  `lines.current: promote` (:143). The current lines are the highest major per ecosystem among the line branches
  (:148-149). The commit is the newest green `lines.workflow` run on the default branch, or a named sha (:179-180). Then
  `writer.VersioningPromoteAsync`, with `onlyIfChanged` when no line is named (:195-200).
- **A promotion is a merge, not a fast-forward:** `git commit-tree <master tree> -p <line tip> -p <master sha>`
  (`CodeCoverage.GitWriter/Jobs/PromoteService.cs:176-179`). The line tip is the first parent, master's sha the second;
  the line and its tags go in one atomic push leased on the old tip (:181-183). A package goes to the line of its
  ecosystem (`Ecosystems.Of(graph, package, LinePins)`, :108-115). With `onlyIfChanged`, lines whose ecosystem did not
  change since the merge base are skipped (`ChangedEcosystems.OfAsync`, :146-155).
- **Why master plans nothing:** the writer derives `Line` from the branch (`VersioningEndpoints.cs:63-66, 155-159`),
  null for the default branch. With lines on and no line, every package is skipped, "Packages release only from release
  lines" (`CodeCoverage.Versioning/Releases/ReleasePlanner.cs:322`).
- **Planning "line L as if master's sha were promoted" needs no commit.** The planner's head is just a sha
  (`ReleaseRun.cs:193-204`). Only two things stand in the way, both in `VersioningRunService.PlanAsync`
  (`CodeCoverage.GitWriter/Jobs/VersioningRunService.cs:149-180`): it fetches only `Branch` plus tags (:152-153), and it
  refuses a commit that is not an ancestor of `Branch` (:157-159).
- **"Since the last tag" is a tree diff, not reachability.** `TagHistory.ReadAsync` reads every tag (`TagHistory.cs:42-57`);
  `lastOnLine` is the highest tag at the line's major (`ReleasePlanner.cs:222-224`); "changed" is
  `diff-tree <tag commit> <head>` (:233, 468-469). A promotion's tree equals the master commit's tree
  (`PromoteService.cs:178`), so diffing a line tag against a master head is exact.
- **Notes walk `git log --first-parent <since>..<head>`** (`ReleaseNotes.cs:70-71`). From a master head that is master's
  own commits since the last promoted one (the tag's promotion merge has that commit as its second parent, so it and its
  ancestors are excluded).
- **Caveat:** hand tags the server records (`ReleaseVersion`, `ReleaseRecipients.cs:169-177`) are deleted from git, so
  `Tags.On(Head)` (`ReleasePlanner.cs:185`) would not see them.

### Pass 2: every changelog × release-lines interaction

Paths under `apps/CodeCoverage/`. Severity is the pass's estimate.

| # | Finding | Evidence | Severity |
|---|---|---|---|
| G1 | **Upcoming view empty for line repositories.** It composes the default branch and any line-pattern branch whatever the mode; with lines on, the planner skips every package on the default branch, and a promote line moves only on `/release`. The tests stub the writer to return notes for `main` and cover only `lines: {}` — no promote, no backport config. | `UpcomingChangelogRecipients.cs:39-46, :91`; `ReleasePlanner.cs:215-217, :322`; `UpcomingChangelogRecipientTests.cs:25, :83-84` | high |
| G2 | **Every run Release is marked latest, whatever its line.** Two current lines released by one `/release` (dotnet-11.x, angular-22.x): the last run to finish wins. A backport release on an old line (dotnet-10.x) takes "Latest" from the current line. npm dist-tags do guard older majors; the run Release doesn't. | `VersionReleaseRecipient.cs:82` (`Latest: true`); `versioning-action/src/releases.ts:84`; contrast `versioning-action/src/semver.ts:42-55` | high |
| G3 | **Title correction mangles the bot's backport PRs.** Titles are `[dotnet-10.x] fix: …`; the type regex needs a letter first, so no type is found. It strips the trailing `(#251)` and rebuilds issues only from `Closes` keywords, which a "Backport of #609" body lacks. No exemption for backport PRs; no test with a bracketed title. (The changelog parser itself strips `[line]`.) | `PrTitleBuilder.cs:66, :81, :107, :140`; `PrTitleRecipients.cs:41-48, :90`; `ChangelogEntry.cs:174`; `BackportLinkRecipientTests.cs:60` | medium |
| G4 | **Promote notes: fixed, edges untested.** The walk is `--first-parent`; a merge whose subject is not "Merge pull request #N" expands to the first-parent commits between its parents, up to 3 levels — not the "non-merge commits" approach the #693 PRD describes (`coverage_changelog_PRD.md:157`). Tested: one promotion from the merge base (`ReleaseNotesTests.cs:142-170`). Untested: a **second** promotion (since = the first promotion), path filtering where the promotion's tree equals its second parent, the 200-commit cap. | `ReleaseNotes.cs:24, :64, :70, :81-84` | low |
| G5 | **Same-day run Release suffix.** The line is in the tag (`RunRelease.cs:20`), so lines never collide. The .NET path tries the next `.n` after any non-Created result, failures included (`VersionReleaseRecipient.cs:88`); the TS path looks the tag up and then posts, so two runs can race between check and post (`releases.ts:106-119`). | as stated | low |
| G6 | **Promote + backport mix:** tested (`ReleaseNotesTests.cs:173-205`). Untested end to end: a merge-queue commit carrying the trailer and co-author lines together (the trailer alone: `MergeQueueEngineTests.cs:2671`). | as stated | low |

### Pass 3: test harness

See the plan's "Test harness" section.

## Decisions

The owner delegated the how ("make the release-lines feature and changelog feature work together flawlessly",
2026-11-23); these are mine, each with its reason. Each lands red test first.

### R1 — The upcoming view of a line repository is per line, composed from the default branch (G1)

- With lines on, the default branch never releases, so it gets **no upcoming row** of its own. Its document stays as
  the cache of the line patterns the push filter reads (`LinePattern`, `ReleaseBranches`), and the query skips it.
- **Promote lines** (`current: promote`): a push to the default branch composes **each current line as if `/release`
  promoted that commit now**. The plan job gets a target line and plans it at the default branch's sha; nothing is
  committed. This is exact, because a promotion's tree is the master commit's tree and the notes walk is first-parent
  from master (Pass 1). The current lines are those `/release` would pick: the highest major per ecosystem among the line
  branches (`ReleaseRecipients.cs:148-149`, shared rather than copied).
- **Backport lines** and older lines keep composing on their own pushes (a merged backport moves them), as today.
- After a `/release`, the line push recomposes the line at its new tip: empty until master moves again. Correct.
- **Why not compose from the line on a timer:** the default-branch push is exactly when "what would ship" changes.

### R2 — Only a current line's run Release may become "Latest" (G2)

- A run Release on an older line (a backport release, e.g. dotnet-10.x while dotnet-11.x is current) is created with
  `make_latest: false`. Same rule in C# (`VersionReleaseRecipient`) and TypeScript (`releases.ts`); `dist` rebuilt.
- Between two current lines released by one `/release`, the later run stays latest: both are the newest release of their
  ecosystem, and GitHub has one "Latest" per repository. Deterministic enough; not worth a cross-run lock.

### R3 — Title correction leaves the bot's backport PRs alone (G3)

- A backport PR (the `<!-- backport-of: … -->` marker in its body, which the bot writes) is skipped by title correction:
  its title copies the original's already-corrected title, and its changelog entry is keyed to the original PR (#693 D5),
  so the backport's own title is never an entry. Rewriting it only loses the `[line]` prefix and the `(#n)` reference.

### R4 — The promote-notes edges get tests; a red one is fixed here (G4, G6)

- A second promotion (since = the first promotion merge), path filtering through a promotion, and a merge-queue commit
  carrying `Backport-of:` plus co-authors. The #693 PRD's wording ("non-merge commits") is corrected to the merge
  expansion the code does.

### R5 — The run Release suffix advances only on "already exists" (G5)

- C#: retry the next `.n` only when the tag exists, not after any failure. TS: treat a 422 `already_exists` from the
  create call as "taken" and move to the next suffix, closing the check-then-post race.
- **Correction (M6, 2026-11-23):** the C# half of G5 was wrong. `ForgeWriteOutcome` has only `Created` and `Exists`
  (`IForgeVersioning.cs:136-140`). A failed create throws, and the message is retried, so the C# loop already advances
  only on "exists". Only the TS race was real; it is fixed.

## Spikes

- **S1 — Plan a line at a default-branch sha.** In `CodeCoverage.GitWriter.Tests`, promote once, merge two PRs on main,
  then plan `release/dotnet-N.x` at main's head with notes. Prove the packages and notes equal what the next promotion's
  real run releases. Needs the fetch of the default branch and the ancestor check relaxed for this mode
  (`VersioningRunService.cs:152-159`).
  **Done (M1):** `VersioningPlanPromotedTests`. After a second promotion, the plan at main's head lists only the
  unpromoted `(#3)`, which is what that promotion's run would release.
- **S2 — Current lines, shared.** Extract `ReleaseRecipient`'s current-line pick (`:148-149`) so the upcoming
  recipient and the run Release's latest flag use the same rule; check what the action (local mode) can see of the line
  branches.
  **Done (M2, M3):**
  - No extraction was needed: the rule is already the shared `ReleaseLinePattern.CurrentLines`. The upcoming recipient
    and `VersionReleaseRecipient` call it after `ListBranchesAsync(BranchPrefix(pattern))`, as `/release` does.
  - The action's checkout holds only the branch and the default branch. It lists the line branches through
    `git/matching-refs/heads/<prefix>` with its token instead (`isCurrentLine`, a TS port of the pattern regex).
- **S3 — `make_latest: false` keeps the existing Latest.** **Done (2026-11-23):** GitHub's REST "Create a release" takes
  `make_latest` as `true`, `false` or `legacy` and defaults to `true` for a newly published release; `false` does not set
  the new release as latest, so the current Latest stays. Source: the REST reference (not re-fetched this session).

## Risks

- **Hand tags recorded by the server are deleted from git** (`ReleaseRecipients.cs:169-177`), so the planner's
  `Tags.On(Head)` (`ReleasePlanner.cs:185`) cannot see them in an upcoming plan. A hand-tagged version can be listed as
  upcoming until the next real release.
- **One plan job per current line per default-branch push.** Spark has two current lines, so two writer jobs per merge.
  They run on the versioning queue, deduplicated per sha (`BroadcastOnceAsync`).

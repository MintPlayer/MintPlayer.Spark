# Changelogs — the GitHub App writes the changelog of every onboarded repository, without committing a version

**Status:** Grilled 2026-11-22. Q1–Q15 are answered: D1 was superseded by D2, and D2–D12 stand. Spikes S1, S2, S4 and
S5 have run. S3 (narrowed), S7, S8 and the rest of S1 run inside the milestones. S6 was dropped. Not started; see
[coverage_changelog_plan.md](coverage_changelog_plan.md).
**Issue:** #693 (empty; its description is rewritten from this PRD) · **App:** `apps/CodeCoverage` (production,
coverage.mintplayer.com) · **one pull request**.

## The problem, stated plainly

The owner wants the CodeCoverage bot to produce and maintain a changelog for every onboarded repository. Three
requirements come from the owner (2026-11-22):

- **Only the relevant lines.** Features, fixes, breaking changes; not chores, CI, test or dependency noise.
- **Generic**, for any onboarded repository, and **integrated with the Spark framework**.
- **No separate commit after the squash** ("Ideally the changelog file-update should happen in the same pull-request").

And one principle, raised by the owner when Q1 was answered, that now governs the design:

> "by using semver and git-tags, we're doing everything in our power to keep package-version-numbers out of our
> commits. however, when we'd be computing and including the version numbers in a Changelog.md file shipped in the
> git-repository contents, we're doing the exact opposite..."

This is the same principle as #153's D1 (one annotated tag per package is the only version record) and its prior-art
verdict "what we deliberately don't take: version files in source" (`coverage_package_versioning_PRD.md`, Prior art).

## The owner's three opening questions, answered from the code

| Question | Answer | Evidence |
|---|---|---|
| Do repositories need conventional commits? | **No.** With squash merges the PR title is the only commit subject on the default branch, and D14 title correction already makes that title conventional. Per-commit messages are discarded by the squash. | `PrTitleBuilder.cs` (D14); `ReleaseNotes.SubjectsAsync` reads first-parent subjects only (`CodeCoverage.Versioning/Releases/ReleaseNotes.cs:34`) |
| Should the PR template carry a type checklist (as in the DeCronosGroep template: Feature, Bug Fix, Documentation, Refactor, Performance, Test, Build, Chore)? | **Optional input, already supported.** A ticked box is step 1 of D14's type order; `TemplateWords` maps "bug", "feature", "documentation", "refactor", "performance", "test", "build", "chore" to types. Whether onboarding should *ship* such a template is Q10. | `PrTitleBuilder.cs:34-39`, `:66`, `TemplateType` `:135` |
| Should the bot ship a PR-title updater, `feat: … (#issue1 - #issue2) (#pr)`? | **It exists (D14)**, off by default. Format `<type>: <title> (#i1/#i2)`; the squash appends `(#pr)`. The separator differs from the owner's example (`/` vs ` - `): Q11. | `coverage_package_versioning_PRD.md` D14 (`:405`); `PrTitleBuilder.cs:83` |

## What exists to build on

Verified against `master` @ `a2704bf02`. Paths under `apps/CodeCoverage/`.

| Need | Already there |
|---|---|
| Typed, clean entries | D14 title correction: `PrTitleBuilder` (`CodeCoverage.Library/PrTitles/PrTitleBuilder.cs`), loop-safe fixed point, policy Inherit/Enabled/Disabled (Account default off). Handled in `PackageVersioning/PrTitleRecipients.cs`. |
| Per-package notes | D15 `ReleaseNotes.Compose` (`CodeCoverage.Versioning/Releases/ReleaseNotes.cs:55`): first-parent subjects since the package's previous tag, **limited to the package's input paths**, grouped Features / Fixes / Other changes / Dependencies. A second copy lives in `versioning-action/src/releases.ts:32`. |
| GitHub Releases | One per tag: `PackageVersioning/VersionReleaseRecipient.cs:51` → `IForgeVersioning.CreateReleaseAsync` (`GitHubForgeVersioning.cs:124`), behind `Repository.ResolveReleaseOnTag` (Account default Enabled). Notes are built per tag in `ReleaseRun.cs:107-111`. |
| Version record | The annotated `<PackageId>/v<version>` tag; its message already carries `api-hash:` and the engine version (D6). |
| Pack-time version | `MintPlayer.Versioning.MSBuild` reads the highest reachable tag at pack time (D9); the npm side writes `package.json` in the working tree at publish time and commits nothing (D9). The same two places can render a `CHANGELOG.md` into the artifact. |
| Release trigger | `/release` (`CodeCoverage.Library/Forge/ReleaseCommand.cs:20`, handled at `GitHubEventsRecipient.cs:594`) and `POST api/versioning/plan|release` (`VersioningController.cs:104/188`). Since #568 M11 Spark releases only from release lines. |
| Webhooks | `GitHubEventsRecipient.cs:44-90`: `pull_request` (`PullRequestMerged`, `PullRequestDescribed`, `PullRequestUpdated`), `issue_comment`, `release` (published, `:644`, which feeds the downstream dependency updater). |
| coverage.yml | One parser per section on the shared file; `PackageVersioning/VersioningYml.cs:24` is the pattern for a new `changelog:` section; `Feedback/CoverageYmlSections.cs:34` inserts sections during onboarding. |
| Per-repo settings | Policy fields on `Repository` with inherit-from-Account (`CodeCoverage.Library/Entities/Repository.cs:237-259`, `Account.cs:229`), `App_Data/Model/Repository.json:492-511`, hidden per forge by `VersioningForm.cs`. |
| Merge conflicts | `**/CHANGELOG.md` is already a keep-both default of the merge queue (`docs/code-coverage/merge-queue.md:302`). Irrelevant if nothing is committed (D2). |

## Prior art (research 2026-11-22)

### One of the owner's private apps

Changelog job of about 1,100 lines, no unit tests. Reads only the subjects of commits on the production branch, parses
them as conventional commits, and **takes each entry's text from the linked issue's title**. Writes one committed
`ChangeLog.md` (newest first, one heading per production deployment, `docs: Automated …` commit), with a hidden
`<!-- Version:x LastCommit:sha -->` cursor comment. It has no sections, only emoji prefixes. Ignores
`chore`, `chore(deps)`, `chore(release)` and `build`, but not `ci`, `test` or `refactor`. Commits without a linked issue
collapse into "Various changes". Lines are prepend-only and are never revised when a title changes. Its PR-title
rewriter is the origin of D14.

**How it works, read first-hand 2026-11-22** (2,033 lines over 8 partial files):

| Step | Mechanism | Weakness |
|---|---|---|
| **Wake-up** (`HasWork`) | Polls the clone. Wakes when there's no file, the file format's version constant changed, or an unprocessed commit has a build tag or a successful check run named after the repository. | Polling; one GitHub check-run request per commit. |
| **Cursor** | `<!-- Version:x LastCommit:sha[,sha] -->` in the committed file, plus a second cursor in the database. When a cursor commit has vanished (production reset), it falls back to the newest production commit that has a database entry. | Two cursors to keep in agreement, with repair code for reset branches. |
| **Version heading** | One heading per build tag, or per commit with a successful deployment check run. Version = MinVer computed for that commit, else the build number from the check run's title. Deployments older than a year are dropped. | A heading per deploy, many of them empty. The version is guessed after the fact. |
| **Parse** | The subject line only, `type(scope)!: content`. Every `#N` is a reference. | `#N` mixes issue and PR numbers. |
| **Entry text** | The **linked issue's title**: references matched to issues, else PR → closing issues through GraphQL. | Wrong or truncated text. Without an issue the entry becomes "Various changes", unless build tags are used. |
| **Filter** | Drops `chore`, `chore(deps)`, `chore(release)`, `build`, per-module ignored scopes, and its own `docs: Automated` commits. `chore(deps)` becomes a package-version line. | `ci`, `test`, `refactor`, `style` and `docs` stay in. The list is duplicated (`:526`, `:673`). |
| **Rendering** | An emoji per type, `⚠️ BREAKING:` and `[scope]` prefixes, prepended to the file. | No sections. Lines are never revised. |
| **Write** | Checks out the *development* branch, commits `docs: Automated …` and pushes, after the deploy. | Exactly the "separate commit after the squash" the owner rules out. |
| **Overrides** | A PR comment command, `changelog-set issue=N message="…"`, stores an override keyed by the PR's **head sha**. The job applies it when a deployed commit has that sha (`ChangeLog.cs:999`). | With squash merges the deployed commit has a new sha, so the override appears never to match. Inferred from the code, not reproduced. |
| **Database copy** | A `ChangeLogs` document with production and staging entries, a formatting-version migration, and phantom-row cleanup. It feeds release notes, not the GitHub Release. | Grows without limit and needs its own repair code. |

What we take: the type order of D14 (already taken). What we avoid: issue titles as entry text (often wrong or cut off
with "..."), a heading per deploy (many empty versions), no sections, a committed file with a cursor comment, lost
entries without an issue.

### Public tools (sources cited)

| Tool | Where the changelog lives | Versions written into git? | Monorepo |
|---|---|---|---|
| release-please | committed per-package `CHANGELOG.md` (`changelog-path`); Release body parsed from the release PR ([design.md](https://github.com/googleapis/release-please/blob/main/docs/design.md)) | **yes**, via the release PR (changelog, manifest, `package.json`); tag on the merge commit | manifest of paths, `<component>-v<version>` tags ([manifest-releaser.md](https://raw.githubusercontent.com/googleapis/release-please/main/docs/manifest-releaser.md)) |
| changesets | committed per-package `CHANGELOG.md` from `.changeset/*.md` intent files ([repo](https://github.com/changesets/changesets)) | **yes** (`changeset version` commits versions) | per declared package |
| semantic-release | GitHub Release body; the file only with `@semantic-release/changelog` **plus** `@semantic-release/git` to commit it ([changelog plugin](https://github.com/semantic-release/changelog), [plugins](https://semantic-release.gitbook.io/semantic-release/usage/plugins)) | **no**, unless the git plugin is added | single package by design |
| Nx Release | `workspaceChangelog` / `projectChangelogs`, each with `file` (or `file: false`) and `createRelease: "github"` ([nx.json reference](https://nx.dev/docs/reference/nx-json)) | `release.git.commit` / `release.git.tag` are separate flags | per project; no workspace changelog for independent releases ([guide](https://nx.dev/docs/guides/nx-release/release-projects-independently)) |
| GitHub generated notes | Release body via `POST /repos/{o}/{r}/releases/generate-notes` (`tag_name`, `previous_tag_name`), categories from `.github/release.yml` ([REST](https://docs.github.com/en/rest/releases/releases), [docs](https://docs.github.com/en/repositories/releasing-projects-on-github/automatically-generated-release-notes)) | no | none (one flat list by label) |
| release-drafter | a draft Release, updated per merge ([repo](https://github.com/release-drafter/release-drafter)) | no | only `tag-prefix` and category `paths` |
| git-cliff | stdout or `--output`/`--prepend` file; `--tag`, `--unreleased`, `--include-path` ([args](https://git-cliff.org/docs/usage/args), [monorepos](https://git-cliff.org/docs/usage/monorepos)) | only if you commit its output | `--include-path` globs |
| GoReleaser | Release body; `use: github-native` ([changelog](https://goreleaser.com/customization/changelog/)) | no | `paths` is Pro-only |

**Every tool that commits a changelog also commits versions.** The ones that don't are semantic-release without its git
plugin, Nx with `file: false`, and the Release-body tools.

What real projects do (repository roots read with `gh api`, 2026-11-22):
- **Commit a CHANGELOG.md:** angular/angular, nodejs/node, kubernetes/kubernetes, adamralph/minver.
- **Notes in GitHub Releases only:** dotnet/runtime, dotnet/aspnetcore, Nerdbank.GitVersioning, GitVersion (via
  GitReleaseManager).

Tag-only versioning doesn't force either choice (MinVer commits one), so this is a choice of principle: the owner's.

Packaging facts:
- **NuGet:** `releaseNotes` (`PackageReleaseNotes`) is capped at **35,000 characters** on nuget.org
  ([nuspec](https://learn.microsoft.com/en-us/nuget/reference/nuspec)). Any `.md` can be packed with `Pack="true"`
  ([msbuild targets](https://learn.microsoft.com/en-us/nuget/reference/msbuild-targets)). That nuget.org displays
  `releaseNotes` is unverified (S3).
- **npm:** CHANGELOG is **not** among the always-included files ([files](https://docs.npmjs.com/cli/v10/configuring-npm/package-json#files)).
  That npmjs.com renders it is unverified (S3).
- **GitHub Release bodies:** the ~125,000-character limit comes from tool issues, not GitHub's docs (unverified).
  `ReleaseNotes.MaxCommits = 200` already guards it.

### Robustness: what the design must do better than the private app

| Problem there | Answer here |
|---|---|
| A guessed version per deploy | The version is the tag the `/release` run itself creates (#153 D1), so nothing is guessed. |
| Cursors in a file and in the database, with repair code | No cursor. The range is always previous tag..this tag of the package, recomputed from git (`ReleaseNotes.SubjectsAsync`). |
| Polling and a check-run request per commit | Event-driven. Runs inside the `/release` run and on `pull_request` events. |
| Entry text from the issue title | The corrected PR title (D14), with an optional body override. |
| `#N` ambiguous between issue and PR | The PR number is the trailing `(#N)` GitHub's squash appends. Issues come from closing keywords (`PrTitleBuilder.LinkedIssues`). |
| Overrides keyed by the PR head sha, lost on squash | Overrides keyed by the PR number, read when the release is rendered. |
| A commit after the deploy | No commit at all (D2). |
| One file for the whole repository | Per package, by the package's input paths (D15 already does this). |
| No sections, noise kept | Sections; a filter shared by notes and changelog (Q4). |
| About 2,000 lines without tests | Pure `Compose`-style functions with unit tests, and real-git tests as in `CodeCoverage.Versioning.Tests`. |

## Spike results (2026-11-22)

### S1: notes in the annotated tag message

- **The tags are already annotated** and carry a parseable message. Spark has 490 tags, 478 of them package tags.
  `MintPlayer.Spark/v11.1.0` reads `MintPlayer.Spark 11.1.0`, then `api-hash: sha256:…`, `engine: CodeCoverage.Versioning
  1.0.0` and `run: <workflow run url>` (`git cat-file -p`). `TagAnnotation` (`CodeCoverage.Versioning/Versions/VersionTag.cs`)
  already reads keyed lines, so a notes block fits.
- **Reading every tag at pack time is cheap.** `git for-each-ref "refs/tags/*/v*" --merged origin/release/dotnet-11.x`
  listed 464 tags with their subjects in **0.40 s**.
- **Sizes:** the current Release bodies are 150 characters (`MintPlayer.Spark/v11.1.0`) and 262 characters (`v11.0.0`).
  They are tiny because of the next finding.
- **Still open:** whether CI checkouts of consumer repositories fetch the tag *objects*, and what recreating a tag does
  (S1 remainder).

### Defect found: the current D15 notes on release lines list the promotion, not the PRs

- **What the published Release says.** The GitHub Release `MintPlayer.Spark/v11.1.0` reads, in full:
  "Other changes: Promote release/dotnet-11.x to master fc95abe3820e", plus one dependency line. PR #690 (Spark templates,
  CLI) is the change it shipped, and it is missing. `v11.0.0` shows the same thing.
- **Why.** A release line's first-parent history between two tags is the promotion merge alone
  (`abeeb94cd parents=ff9b6ca2d fc95abe38 | Promote release/dotnet-11.x to master …`). The squashed PR commits sit on its
  second parent. `ReleaseNotes.SubjectsAsync` walks `--first-parent` (`ReleaseNotes.cs:36`), so it sees only the merge
  subject.
- **Proof of the fix's input.** `git log --no-merges MintPlayer.Spark/v11.0.0..MintPlayer.Spark/v11.1.0 --
  libs/spark/MintPlayer.Spark` gives exactly `fc95abe38 Spark templates, spark CLI / create-spark-app (dnx) (#690)`.
- **Consequence.** ~~Since the squash queue makes every non-merge commit one PR, the range has to be the **non-merge
  commits** of previous-tag..tag below the package's inputs, not the first-parent subjects.~~ *Superseded by D4's M1
  revision below:* the walk stays first-parent, and a non-PR merge ("Promote …") expands into the commits it brings in
  (`ReleaseNotes.cs:70, 81-84`). That's a bug fix to D15 in this PR; it's also the changelog's core. Release lines ×
  changelog follow-up: [coverage_changelog_release_lines_PRD.md](coverage_changelog_release_lines_PRD.md).

### S5: backports

- **What a backport commit looks like on the line:** `[dotnet-11.x] Reference ids bound to their declared collection, on
  read and on save (#251) (#610)`. #251 is the issue (D14's suffix) and #610 is the backport PR. **The original PR
  (#609) is not in the commit.** It is only in the backport PR's body: `Backport of #609` and
  `<!-- backport-of: MintPlayer/MintPlayer.Spark#609 -->` (`gh pr view 610`). The squashed message carries only the
  title and `Co-authored-by` trailers (`git log -1 --format=%B da8be3a91`).
- **Duplicates are real.** A line receives a change through its backport commit, and later the same change arrives
  again through a "Promote master … onto release/…" merge (`6e7481da6`). Deduplicating by the original PR number needs
  that number somewhere git can read it.
- **Fix candidate (Q13).** The merge queue composes the squashed commit's message itself (title plus co-author trailers,
  merge-queue PRD D13/D60), so it can add a `Backport-of: #609` trailer. git alone then answers "is this the same
  change?", with no API call at render time.

### S4: relevance filter on real history

The filter is the proposed type list. Results on Spark `master`, first-parent:

| Range | Subjects | Typed | Untyped |
|---|---|---|---|
| since 2026-09-01 | 185 | 52 (27 `fix`, 14 `feat`, 2 `docs`, 1 `ci`, 1 `chore`; 7 use a scope as type, e.g. `codecoverage:`) | **133 (72%)** |
| since 2026-10-25 (D14 shipped) | 94 | 18 | **76 (81%)** |

Untyped examples: `File and image attributes (#700) (#702)`, `#691 Spark Apps and outbound Standard Webhooks (#694)`,
`Spark templates, spark CLI / create-spark-app (dnx) (#690)`, and the dependency bot's own
`Update dependencies (44 groups) (#685)`.

**Spark itself does not have title correction on, so a type-only filter would drop most of Spark's real features**
(#690, #694, #700). The changelog either requires D14 on, or needs a rule for untyped subjects (Q14). The dependency
bot's titles are untyped too: it needs a `chore(deps)` title, or the filter needs to recognise it by author.

### S2: a repository-wide Release under a non-package tag

- **The tag grammar ignores it.** `VersionTag.TryParse` splits at the last `/v` and requires a package id and one of
  our versions, so `release/dotnet-11.x/2026-11-22` is ignored (`VersionTag.cs:25-35`). The MSBuild prefix
  `<PackageId>/v` doesn't match it either.
- **The release webhook still reacts.** Its `release: published` event raises `ReleasePublished` for any tag
  (`GitHubEventsRecipient.cs:639-648`). The only consumer, `DependencyUpdateRecipients.cs:156`, wakes downstream
  updaters as a producer event: one extra wake-up per `/release`, never a wrong update.
- **Conclusion:** a repository-wide Release is technically safe. Q3 is a product choice, not a risk.

## Decisions

Each decision records its evidence; superseded text stays, struck through.

### ~~D1 — The changelog is written inside the merge queue's squashed commit; `/release` goes through a release PR~~ (Q1)

~~A queued PR's squashed commit adds its entries under `## Unreleased` in a committed `CHANGELOG.md`. `/release`
opens a bot release PR that renames `Unreleased` to the version, and the tags go on its merge commit (release-please's
model). The changelog requires versioning and the merge queue.~~ Evidence at the time: the queue already publishes
one squashed commit to the PR before merging (`docs/code-coverage/merge-queue.md:18`, `:49`, `:57`), and versions are
unknown at queue time since tags are pushed only by `/release` on a release line (#568 M11). Owner, Q1: "I think
probably A".

*Superseded by D2 the same day.* Owner: committing a version-bearing changelog is "the exact opposite" of keeping
versions out of commits. The research above confirmed it: every tool with a committed changelog commits versions.

### D2 — No version number is ever committed by the changelog feature (owner, 2026-11-22)

The changelog is **rendered from git tags and PR metadata**, never committed with versions in it.
- **Where it is rendered:** the GitHub Release (D15), the package artifacts at pack time, and the CodeCoverage UI.
- **What this removes from D1:** no queue hook, no release PR, no reworked `/release`, and no dependency on the merge
  queue.
- **Evidence:** the owner's principle quoted above; #153 D1; the prior-art table.
- **Still open:** whether any committed file remains at all, for example a version-free pointer or index (Q7).

### D3 — The release run freezes the notes into the annotated tag; the database keeps a queryable copy (Q2)

- **Who renders:** the `/release` run renders the finished sections, after applying the PR-body overrides and the
  `changelog:` labels, which only it can read through the API.
- **Where they go:** a notes block in the tag message, below `api-hash:`, `engine:` and `run:` (D6). Pack, the action and
  the server mirror only read and join tag messages; no rendering logic goes into the MSBuild task.
- **Database:** keeps the entries (per PR, package and release) for the CodeCoverage repository page, the upcoming view
  and the overrides set before a release, through the storage-agnostic `ISparkSession` (D16; RavenDB today). CI never
  depends on the server being up.
- **Corrections after a release** edit only the GitHub Release body. The tag stays as it was written.
- **Done (M11):** the copy is one `ChangelogRelease` per version tag, `ChangelogReleases/{provider}/{repoId}/{tag}`,
  written by `ChangelogRecipient` through `ISparkSession` only (loads by id, `LoadStartingWithAsync` for the page, no
  index). *Deviation:* its source is the forge's `release` webhook (`published`, and `edited` for the corrections
  above), not the versioning endpoint's `FinishAsync`. The action's local mode, which Spark itself runs, never calls the
  server (`versioning-action/src/main.ts:351`), while both hosts create the Releases. The run Release (not a version
  tag) and dependency-only packages (no Release, D7) get no copy. Gated by `Repository.ResolveChangelog` (account
  default off: it switches title correction on, which edits other people's pull requests). Tests:
  `ChangelogRecipientTests`, every case on the in-memory store and on RavenDB.
- **Config placement (M11):** the filter overrides live under `versioning.changelog.include` in `coverage.yml`, not a
  top-level `changelog:`. The action hands the engine only the `versioning:` section (`versioning-action/src/main.ts`,
  `versioningSection`), so a top-level section would never reach local mode.
- **Defect fixed (M11, per D4):** the writer's `VersioningSettingsWire` had no `ChangelogInclude`, so a server-mode run
  ignored `versioning.changelog.include` silently. The wire now carries it, validated against
  `VersioningChangelogConfig.Includable`. Red→green: `VersioningSettingsTests.Changelog_include_survives_the_wire_from_the_app_and_an_unknown_type_is_refused`.
- **Evidence:** S1 (the tags are annotated and keyed, 0.40 s to read 464 of them). Owner, Q2: "I'll let you decide.
  A probably looks good … we probably 'could' store some information in the ravendb database".

### D4 — The defects found by the spikes are fixed in this pull request (owner, 2026-11-22)

- **The range on a release line.** ~~It becomes the non-merge commits of previous-tag..tag below the package's inputs,
  instead of the first-parent subjects. Both copies change: `ReleaseNotes.cs` and `versioning-action/src/releases.ts`.~~
  *Revised in M1:* the walk stays first-parent, and a merge that is not a pull request's ("Promote …") is replaced by
  the first-parent subjects of its first parent..second parent (nesting up to 3 deep). `--no-merges` would have broken
  repositories that merge PRs with merge commits: their PR title is in the merge's body, and the branch's WIP commits
  would have replaced it (`ReleaseNotesTests.Reads_first_parent_subjects_below_the_inputs_and_a_merge_commits_PR_title`).
  Only `ReleaseNotes.cs` changes: `releases.ts` posts the notes the C# engine CLI renders (`Wire.cs:47`); it has no copy.
  Red → green: `ReleaseNotesTests.A_promote_merge_on_a_release_line_lists_the_pull_requests_it_brings_in` (red found
  only "Promote release/1.x to master …").
- **Duplicates.** A backport and its original arriving through a promotion give one entry (Q13).

"Promote" commits are Segment P's (`docs/release_lines_recovery_plan.md` §2 Q-A = B, §2b): on a `mode: promote`
current line, `/release` writes a commit whose tree is a green master commit's, with the line tip and that commit as
parents. `release/dotnet-11.x` holds cherry-picks up to `da8be3a91`, from before PR #626 switched Spark to promote, and
promotions after it. Owner: "yes you should fix the detected bugs in this very same pull-request/branch".

### D5 — A backport commit names its original in a trailer (Q13)

- **New backports:** when the merge queue squashes a backport PR, it adds `Backport-of: #<n>` to the commit message,
  beside its `Co-authored-by` trailers.
- **Manual cherry-picks:** git's own `(cherry picked from commit <sha>)` (`-x`) is recognised too. That sha's commit
  carries the original `(#n)`.
- **Older backports** have no trailer. The release run falls back to the backport PR body's
  `<!-- backport-of: owner/repo#n -->` through the API (`GET /commits/{sha}/pulls`).
- **Deduplication key:** the original PR number. A change is one entry however it reached the line.
- **Evidence:** S5 (`da8be3a91` carries only title and co-authors; PR #610's body carries the marker). Owner, Q13:
  "I think i'll follow you".

### D6 — Relevance comes from paths, sections from the type, plus a Public API section; title correction comes with the changelog and also classifies old PRs (Q14)

- **Relevance:** an entry exists only when the PR changed the package's inputs (`ReleaseNotes` input paths). CI, docs
  and test-only PRs never touch a package's inputs. The bot's own dependency PRs (author = the App, branch
  `mintplayer/dependencies`) become the "Dependencies" lines.
- **Section:** the PR's type, decided by D14's order. ~~That order gains one step: the branch prefix (`fix/`, `feat/`,
  `docs/`, …) right after "the type already in the title".~~ *Withdrawn 2026-11-22 (owner): branch names are free-form
  for human developers, and Spark's prefixes are Claude Code's own choice, so the prefix is never read as a type.* Only
  explicit signals count: a ticked template box, a type in the title, a `bug` label on a linked issue, or a D12 button.
  D14's `feat` default no longer applies to the changelog (D12).
  - Shown: `feat`, `fix`, `perf` and `revert`, plus a Breaking section for `!`.
  - Dropped: `chore`, `ci`, `test`, `docs`, `build`, `style`, `refactor`.
  - Nothing found at all: "Changes".
- **Title correction is switched on together with the changelog** (owner: "willing to also allow/enforce the
  title-correction feature, if we can also retroactively process the old pull-requests").
- **Old PRs** are classified retroactively at render time. For an untyped subject, the release run looks up the PR from
  its trailing `(#N)` and runs the same builder on the PR's current title, body and linked issues. The result is frozen
  into the tag (D3), so merged PR titles and git history are never rewritten. An old PR with no explicit signal goes
  under "Changes". For Spark's history that's most of it (3 of the last 60 PRs carry an explicit signal), so its first
  changelog will have a large "Changes" block. A `## Changelog` body section (D9) can still type an old PR before its
  release.
- **Public API section per release:** the action diffs the normalized API listing (D6 of #153) against the previous
  version's. Added and removed symbols are listed, and a removal goes under Breaking. This is objective, needs no
  title, and works for old history too. **Done (M9):** S8 answered yes (see the plan's S8 results). The action reads the
  previous version's listing from its registry package (`IPackageRegistry.ApiListingAsync`), whenever the last tag's hash
  differs from the build. Behind an annotated tag, the delta is used only when the registry package hashes to the
  annotation, so a different build of that version is never diffed against. The delta travels in the manifest
  (`ManifestPackage.Api`, `ApiDelta`), so both hosts get it. A .NET line that changed in every framework is one line, a
  framework added or dropped is one line, and removals are kept first. The caps are 100 lines of 400 characters per
  package and 512 Ki characters per manifest, inside the 2 MiB upload. Past those caps a package loses its section and
  the action says so. Nothing is stored in RavenDB: the registry already holds every released listing.
  A re-run of a tagged commit has no section, because its last tag is the release itself.
- **Not done:** an API delta per PR. The hash exists per release, not per PR. Computing one per PR means building every
  PR commit, and an API change isn't a feature/fix classification anyway (a fix can add API, a perf change touches none,
  an app feature has no public API).
- **Evidence:**
  - Paths: Spark's commits below `libs/spark/MintPlayer.Spark/` are almost all user-facing (#700, #690, #609, #561, #555).
    The remaining noise is the dependency bot and a few incidental touches (#577, #586).
  - Old PRs: in Spark's last 60 merged PRs, D14 as it stands found a type for only 3 (title 1, template 1, bug label 1)
    and defaulted 57 to `feat`, including the fix #684. ~~58 of 60 branch names carry a type prefix~~ (GraphQL over the
    merged PRs, 2026-11-22). The prefixes are Claude Code's, which is no evidence for human developers, so the step was
    withdrawn.
  - Owner, Q14: "Otherwise, B looks good".

### D7 — Release notes reach Renovate and Dependabot through per-package Releases; one Release per run is the overview (Q3)

The owner's requirement: "let it be compatible with the conventions you mentioned (like renovate and dependabot)".
Both bots read the notes from a **GitHub Release on the version tag**; a bare tag is not enough. Our tag format matches
both. Sources, read at HEAD on 2026-11-22:

- **Renovate:**
  - **Tag match:** `getExactReleaseMatch` matches `(?:^|/)<packageName>[@_/-]v?<version>`, so `MintPlayer.Spark/v11.1.0`
    and `@mintplayer/ng-spark/v22.4.0` both match (`lib/workers/repository/update/pr/changelog/release-notes.ts`
    ~190-215).
  - **Releases are listed, not tags:** `github/source.ts:168`.
  - **Changelog files** are read from the *default branch* only (`github/source.ts:103-160`), and tried before the
    Releases (`source.ts:79-81`).
  - **Compare links** use the prefix `(pkg)[@_-]v?`, so `/v` gives no compare link (`source.ts:279-285`). That's
    cosmetic.
  - **The NuGet source URL** comes from the nuspec `repository@url` (`datasource/nuget/v3.ts:248-250`).
- **Dependabot:**
  - **Releases** are those whose tag starts with the dependency name, with the version matched at the end of the tag or
    name (`common/lib/dependabot/metadata_finders/base/release_finder.rb:126-135`, `:261-267`).
  - **Changelog files** are read from the default branch first, then at the tag
    (`changelog_finder.rb:113-135`). A package directory is trusted only for npm `repository.directory` (`base.rb:18`).

Decisions:
- **Per package:** one Release per package tag stays (D15, `ResolveReleaseOnTag` default on), with the D6 sections. It's
  created with `make_latest: false`.
- **Per run:** one Release per `/release` run, created last and marked latest. It summarises every package the run
  released, with links to the per-package Releases, so the repository's "Latest release" is the overview. Its tag is
  `release/<line>/<yyyy-MM-dd>[.<n>]`, or `release/<yyyy-MM-dd>[.<n>]` without lines. The engine ignores that tag (S2).
- **Fewer Releases:** a package released only because a dependency moved gets **no** per-package Release. It is listed
  in the run's Release, its tag annotation and its packaged CHANGELOG.md. Measured: of the 30 Releases of 2026-11-18, 20
  carried only "Dependency update" lines. The runs of 2026-11-12, 11-16 and 11-18 made 23, 46 and 30 Releases. The 11-16
  count is inflated by the D4 defect: every package's notes held the promotion line.
- **Metadata:** every package must name its repository, so both bots find the Releases: the nuspec `RepositoryUrl` and
  npm `repository.url` plus `repository.directory`. Spark sets `RepositoryUrl` in only a few of its 68 packable csprojs
  (`MintPlayer.Spark.AllFeatures`, `MintPlayer.Spark.Apps`, …). Onboarding checks it and the versioning check run warns
  when it's missing (S7).
  - **Done (M12):** the graph reads `ProjectNode.NamesRepository` (`RepositoryUrl`, or `PublishRepositoryUrl=true` for
    Source Link; npm `repository` as a string or with a `url`). With the changelog on, the onboarding pull request lists
    the packages without it under "To do by hand" (`ChangelogOnboarding`). **Correction (2026-11-23):** Spark itself
    already names it everywhere. Every one of the 171 tracked csprojs that declares a `PackageId` or `IsPackable=true`
    sets `RepositoryUrl`, and both npm packages have `repository`; the "only a few" above was stale.
  - **Deviation:** the versioning check run does not warn. `graph.Warnings` is what the check shows, and putting the
    warning there would repeat it on every run of a repository that chose not to fix it; onboarding is where the owner
    acts on it.

### D8 — No changelog file is committed, not even a pointer (Q7)

- **Why:** Renovate tries changelog files on the default branch *before* the Releases (`source.ts:79-81`), and
  Dependabot does the same (`changelog_finder.rb:113-135`). A committed root `CHANGELOG.md` without our versions would be
  found first and hold nothing to match, which is at best a wasted lookup. Not committing one means both go straight to
  the Releases.
- **Also:** it follows from D2.
- **Exception:** a hand-written `CHANGELOG.md` a repository already has is left alone.

### D9 — An entry's text comes from the PR title, or from a `## Changelog` section in the PR body (Q5)

- **The section in the body:** each bullet of a `## Changelog` section becomes one entry. A bullet may carry its own
  type (`- fix: …`). An empty section, or the text `none`, means no entry.
- **Shortcut:** the `changelog: skip` label drops the PR without editing the body.
- **When it is read:** by the release run, so the body can be corrected until `/release`. After that the text is frozen
  in the tag (D3). An edit made after the merge leaves no trace in git: it is the correction window, and it is not
  reviewed.
- **Not done:** a bot command such as the private app's `changelog-set`. It is hidden state, and there it was keyed by a
  sha that the squash changes.
- **Evidence:** owner, Q5: "I'll follow you".

Q4 (filter, and D15 adopting it), Q6 (a Breaking section, from `!` and from removed public API) and Q9 (title
correction comes with the changelog) are answered by D6.

### D10 — No changelog inside deployed apps; "Spark integration" means CodeCoverage's own pages (Q8)

- **What ships:** the changelog appears as GitHub Releases (D7).
- **Out of scope:** a `MintPlayer.Spark.Changelog` package and an ng-spark page for deployed apps (proposed design
  item 6, spike S6).
- **What "Spark integration" means:** CodeCoverage's changelog settings and its repository changelog page are Spark
  pages (persistent objects, queries, `actions.json`, `security.json`).
- **Evidence:** owner, Q8: "I think I'd go for C, we'll only ship github-releases that contain the generated changelog
  file". Context: apps deploy untagged from master, and their image build has no git (`.dockerignore:4` `**/.git`).

### D11 — The Release body is the changelog; the nupkg also carries `PackageReleaseNotes` (Q15)

- **The GitHub Release body** (D7) is the generated changelog. Nothing is attached to the Release.
- **NuGet:** `MintPlayer.Versioning.MSBuild` already resolves the highest reachable tag (#153 D9). It also reads that
  tag's notes block (D3) into `PackageReleaseNotes`, the current version's section only, truncated to nuget.org's
  35,000-character cap with a link to the Release. Visual Studio's NuGet window shows it.
- **npm** gets nothing extra. npm has no release-notes field, and CHANGELOG.md in a tarball is read by nobody that
  matters here (Renovate reads the default branch, D7).
- **No cumulative CHANGELOG.md in packages.**
- **Spike:** S3 shrinks to the MSBuild part: `RealPackTests` proves the nuspec `releaseNotes` from a tag annotation,
  including a tag without notes (seed tags, human tags), which leaves it empty.
- **Evidence:** owner, Q15: "I tend to go for C".

### D12 — Human contributors: a "Type of change" template section and check-run buttons (Q10)

- **Caveat to D6's evidence:** Spark's branch prefixes are chosen by Claude Code, so "58 of 60 branches carry a type"
  says nothing about human contributors. They name branches `patch-1` or `my-change` and rarely type `feat:` (owner:
  "Other people won't be using claude-code or an ai agent at all, and just choose their branch names themselves").
- **Template:** the onboarding PR adds a short "Type of change" section to `.github/pull_request_template.md`. It
  creates the file if the repository has none, or appends the section visibly in the onboarding PR so the owner reviews
  it. The box wording is the wording `PrTitleBuilder.TemplateWords` already maps. Spark's own template gets the section
  in this PR.
- **No silent `feat`:** when D14's order finds no explicit signal (no ticked box, no type in the title, no `bug` label;
  the branch name is never read, D6), the title-correction check run asks with three buttons: **Feature**, **Fix** and
  **Not user-facing**. "Not user-facing" means the same as `changelog: skip`. A click edits the title. The plumbing
  exists: check-run buttons already drive the merge queue (`OnQueueCheckButtonAsync`). GitHub allows three actions per
  check run.
- **Limits accepted:** perf, revert and breaking still need a box or a type in the title. Only users with write access
  can click, so an outside contributor's PR waits for a maintainer or a ticked box.
- **Evidence:** owner, Q10 (re-asked): "A".
- **Done (M12):** `ChangelogOnboarding` writes the section when versioning is planned and the changelog is on. It
  creates `.github/pull_request_template.md`, or appends to an existing single template (`.github/`, the root or `docs/`,
  any case). A template whose heading already says "Type of change" is left alone, so a rebuilt pull request changes
  nothing. Boxes: Feature, Fix, Performance, Documentation, Refactor, Chore. Every one maps through
  `PrTitleBuilder.TemplateWords`: `ChangelogOnboardingTests.Every_box_maps_to_a_type`. Title correction needs no switch
  of its own: M11's `ResolvePrTitleCorrection` already follows the changelog. Spark's template has the section.

### D13 — The template section can be switched off (owner, 2026-11-22)

*Numbering: D14 and D15 are skipped, because in this PRD a bare D14 or D15 means the versioning PRD's title correction and release notes.*

- **Setting:** a `PrTemplatePolicy` (Inherit / Enabled / Disabled) on Repository with an Account default (Enabled),
  the same shape as the other policies (`Repository.cs:237-259`). Owner: "users should probably be able to disable the
  'auto-correct the pull-request template' feature which adds the checkboxes to the template".
- **When the bot writes the section:** only in the onboarding PR, and in a re-onboarding while the policy is Enabled.
  It never rewrites the template between onboardings, so a repository owner's later edits stay.
- **When Disabled:** the template is not touched. The check-run buttons (D12) still work, so the type can still be
  given.

### D16 — New changelog storage goes through the storage-agnostic `ISparkSession` (owner, 2026-11-22)

- **Session:** the database copy (D3), the settings and the CodeCoverage pages use `ISparkSession`
  (`libs/spark/MintPlayer.Spark.Abstractions/Storage/ISparkSession.cs`, #551), never `IAsyncDocumentSession`. Owner:
  "make sure you use the storage-agnostic provider, in case we'd ever start supporting SQL Server or mariadb".
- **Available today:** `spark.UseRavenDb()` (`apps/CodeCoverage/CodeCoverage/Program.cs:98`) registers it
  (`SparkRavenDbExtensions.cs:86`).
- **Fits every provider:** documents with deterministic ids, read by `LoadAsync(ids)` (batched; the 30-request session
  cap), and `Query<T>()` LINQ filters. No `Query<T, TIndex>()`, which binds to a RavenDB index
  (`docs/sql_server_provider_PRD.md` Q5), no static indexes, and no RQL or patch scripts.
- **Proof:** the changelog store's tests run on the in-memory provider (`InMemorySparkSession`,
  `libs/spark/MintPlayer.Spark.InMemory`) as well as RavenDB (`RavenSparkSession`).
- **Scope:** CodeCoverage itself still uses `IAsyncDocumentSession` in 115 files. Migrating those is out of scope; only
  the new code is storage-agnostic.

## Original proposed design (before the grill; the decisions above win where they differ)

1. **One entry per merged PR and per affected release unit.** The entry text is the corrected PR title (D14), without
   the issue suffix. A `## Changelog` block in the PR body can override the text (Q5). Each entry keeps its PR number,
   the issues it closes, its type, and a breaking marker (`!`).
2. **Relevance filter:**
   - **Included:** `feat`, `fix`, `perf`, `revert`, and anything breaking (`!`, any type).
   - **Dropped:** `chore`, `ci`, `test`, `build`, `docs`, `refactor`, `style`, untyped subjects, and bot dependency
     bumps.
   - **Dependencies:** follow-only releases keep the one "Dependency update" line.
   - **Overrides:** a `changelog: skip` label drops an entry; a `changelog: include` label adds one.
   - **Release notes:** D15's "Other changes" section goes away, so the notes and the changelog are the same text (Q4).
3. **Rendered at release time, from tags.** The `/release` run already builds the notes per tag (`ReleaseRun.cs:107`).
   Proposal: store them **structured in the annotated tag's message**, beside `api-hash:` (D6). Then every reader works
   offline from git: the pack, the action, and the server's mirror (Q2, S1).
4. **Outputs:**
   - **GitHub Release per tag:** D15, with the filtered sections: Breaking changes / Features / Fixes / Performance /
     Dependencies.
   - **Per-package `CHANGELOG.md` in the artifact:** concatenates every reachable tag of that package, newest first.
     For .NET it is packed with `Pack="true"`, and `PackageReleaseNotes` gets the current version's section (truncated
     to 35,000 characters). For npm it is written into the publish folder.
   - **A repository-wide view:**
     - (a) a CodeCoverage page per repository: a Spark query over its releases, grouped by release run;
     - (b) optionally a repository-wide GitHub Release per `/release` run, under a tag the engine never reads as a
       version (Q3, S2).
   - **Unreleased changes are visible without a commit:** a per-line "upcoming" view in CodeCoverage, and optionally a
     draft GitHub Release per release line, as release-drafter does (Q12).
5. **Backports:** an entry is keyed by the **original** PR number. A backport PR's notes on a release line reference
   the original, so a change that ships on two lines is one entry, listed under each line's version (S5).
6. **Spark integration (Q8):**
   - The CodeCoverage pages are Spark (persistent objects, queries, `actions.json`, `security.json`).
   - **Proposed:** a generic package, `MintPlayer.Spark.Changelog` plus an `@mintplayer/ng-spark/changelog` entry point.
     It serves a changelog bundled into a deployed Spark app at build time, through a Spark endpoint and a ready-made
     page.
   - Apps deploy from master and are untagged, so how an app's changelog gets versioned is S6.
7. **Settings:**
   - A `ChangelogPolicy` (Inherit / Enabled / Disabled, Account default) on `Repository`.
   - A `changelog:` section in `coverage.yml` for the filter overrides and the outputs.
   - Requires versioning. Title correction (D14) is strongly recommended, and the check run warns when it is off (Q9).

## Questions (all answered 2026-11-22)

| # | Question | Recommendation |
|---|---|---|
| Q1 | ~~How does a version reach a committed CHANGELOG.md?~~ | answered, then superseded: D1 → D2 |
| Q2 | ~~Where do the rendered notes live as source of truth?~~ | answered: D3 |
| Q3 | ~~A repository-wide GitHub Release per `/release` run?~~ | answered: D7 |
| Q4 | ~~Relevance filter~~ | answered: D6 |
| Q5 | ~~Entry text~~ | answered: D9 |
| Q6 | ~~Breaking changes~~ | answered: D6 |
| Q7 | ~~Any committed file at all?~~ | answered: D8 |
| Q8 | ~~Spark changelog package for deployed apps?~~ | answered: D10 (out of scope) |
| Q9 | ~~Does the changelog require title correction?~~ | answered: D6 (switched on with it) |
| Q10 | ~~PR template type checklist?~~ | answered: D12 |
| Q11 | ~~Title separator~~ | answered (owner: "A"): keep D14's `(#1/#2)`. The changelog shows the text without the suffix; issues become links behind the entry, and the PR number comes from the squash's final `(#N)`. |
| Q12 | ~~Unreleased visibility~~ | answered (owner: "A"): a CodeCoverage page per repository shows, per release line, the entries since the line's last tag. It includes each entry's override (D9) and type source (D12), with links to fix them in the PR. No draft Releases. A `/release --dry-run` was proposed but not chosen. **Done (M11):** a push to the default branch or a release line (the patterns cached on the default branch's `UpcomingChangelog` document, so the push filter reads no `coverage.yml`) asks the writer's plan job with `notes: true`, which composes each planned package's notes from the commits since its tag (`VersioningRunService.PlanAsync`, a budget of 512 Ki characters, an over-budget package is marked omitted). The result is stored per line as `UpcomingChangelogs/{provider}/{repo}/{escaped branch}` through `ISparkSession` and listed by the `repository-upcoming-changelog` query. **Deviation:** no per-entry override (D9) or type source (D12) is shown. The writer's job token is scoped to contents, so the plan sees commits only, not pull-request bodies or labels; the upcoming notes are therefore what the commit subjects say, and a D9 override shows up only in the released notes. Widening the token was not chosen. Tests: `UpcomingChangelogRecipientTests` (memory and RavenDB), `VersioningPlanTests.With_notes_…`. |
| Q13 | ~~How does git know a backport commit is the same change as its original PR?~~ | answered: D5 |
| Q14 | ~~What happens to untyped subjects?~~ | answered: D6 |

## Out of scope

- Versions in any committed file (D2).
- Issue titles as entry text.
- GitLab and Bitbucket. GitHub only behind `IForgeVersioning`, as in #153 D16.
- Rewriting the notes of releases cut before the feature ships.

## Risks

- **History before D14 is untyped.** On repositories onboarded before title correction, many subjects have no type and
  the filter drops them. The first changelog could look empty (S4).
- **Tags are immutable.** A wrong entry in a tag message is permanent unless the tag is recreated, which consumers'
  caches won't see (Q2).
- **Two copies of the notes logic** (`ReleaseNotes.cs` and `versioning-action/src/releases.ts`) must change together.
  The action's `dist` is committed and byte-checked. Since M2 the action no longer composes package notes (they come
  from the engine), so only the run Release body (`runBody`) is TypeScript. The source changes of M2–M8 had left `dist`
  stale; it was rebuilt in the main checkout before the pull request (2026-11-23).
- **A label added by hand doesn't refresh the title check.** Title correction runs on `opened`, `reopened` and a human's
  `edited` only (`GitHubEventsRecipient.cs:474,505`), so a `changelog: skip` label added in the sidebar shows on the check
  at the next edit. The release run reads labels fresh, so the changelog itself is right.
- **A malformed answer from the pull-request lookup used to drop its whole batch (fixed).** `GitHubPullRequestFacts.Read`
  called `TryGetInt32` on a `number` that wasn't a JSON number, and `TryGetInt32` throws on that rather than returning
  false. The batch of up to 50 pull requests then fell back to the commit subjects, losing the `## Changelog` overrides
  and skip labels. It is now skipped. Red then green:
  `GitHubPullRequestFactsTests.Reads_labels_a_linked_bug_and_the_backport_marker_and_skips_issues_and_odd_shapes`
  (`c60af6f56`).
- **The upcoming view has no pull-request facts** (Q12 "Done"): its notes follow the commit subjects, not a `## Changelog`
  section or a skip label, until the release.

# Changelogs — the GitHub App writes the changelog of every onboarded repository, without committing a version

**Status:** Investigated 2026-11-22 (five read-only research passes, below). Grilling paused after Q1 on the owner's
objection (D1 superseded by D2); Q2–Q12 open. Spikes S1–S7 open, see [coverage_changelog_plan.md](coverage_changelog_plan.md).
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
- **Consequence.** Since the squash queue makes every non-merge commit one PR, the range has to be the **non-merge
  commits** of previous-tag..tag below the package's inputs, not the first-parent subjects. That's a bug fix to D15 in
  this PR; it's also the changelog's core.

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

### D3 — The release run freezes the notes into the annotated tag; RavenDB keeps a queryable copy (Q2)

- **Who renders:** the `/release` run renders the finished sections, after applying the PR-body overrides and the
  `changelog:` labels, which only it can read through the API.
- **Where they go:** a notes block in the tag message, below `api-hash:`, `engine:` and `run:` (D6). Pack, the action and
  the server mirror only read and join tag messages; no rendering logic goes into the MSBuild task.
- **RavenDB:** keeps the entries (per PR, package and release) for the CodeCoverage repository page, the upcoming view
  and the overrides set before a release. CI never depends on the server being up.
- **Corrections after a release** edit only the GitHub Release body. The tag stays as it was written.
- **Evidence:** S1 (the tags are annotated and keyed, 0.40 s to read 464 of them). Owner, Q2: "I'll let you decide.
  A probably looks good … we probably 'could' store some information in the ravendb database".

### D4 — The defects found by the spikes are fixed in this pull request (owner, 2026-11-22)

- **The range on a release line.** It becomes the non-merge commits of previous-tag..tag below the package's inputs,
  instead of the first-parent subjects. Both copies change: `ReleaseNotes.cs` and `versioning-action/src/releases.ts`.
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
- **Section:** the PR's type, decided by D14's order, which gains one step. **The branch prefix (`fix/`, `feat/`,
  `docs/`, …) comes right after "the type already in the title".**
  - Shown: `feat`, `fix`, `perf` and `revert`, plus a Breaking section for `!`.
  - Dropped: `chore`, `ci`, `test`, `docs`, `build`, `style`, `refactor`.
  - Nothing found at all: "Changes".
- **Title correction is switched on together with the changelog** (owner: "willing to also allow/enforce the
  title-correction feature, if we can also retroactively process the old pull-requests").
- **Old PRs** are classified retroactively at render time. For an untyped subject, the release run looks up the PR from
  its trailing `(#N)` and runs the same builder on the PR's current title, body, branch and linked issues. The result
  is frozen into the tag (D3), so merged PR titles and git history are never rewritten.
- **Public API section per release:** the action diffs the normalized API listing (D6 of #153) against the previous
  version's. Added and removed symbols are listed, and a removal goes under Breaking. This is objective, needs no
  title, and works for old history too (S8: is the previous listing reliably available from the registry package?).
- **Not done:** an API delta per PR. The hash exists per release, not per PR. Computing one per PR means building every
  PR commit, and an API change isn't a feature/fix classification anyway (a fix can add API, a perf change touches none,
  an app feature has no public API).
- **Evidence:**
  - Paths: Spark's commits below `libs/spark/MintPlayer.Spark/` are almost all user-facing (#700, #690, #609, #561, #555).
    The remaining noise is the dependency bot and a few incidental touches (#577, #586).
  - Old PRs: in Spark's last 60 merged PRs, D14 as it stands found a type for only 3 (title 1, template 1, bug label 1)
    and defaulted 57 to `feat`, including the fix #684. 58 of 60 branch names carry a type prefix: `fix/` 27, `feat/` 24,
    `docs/` 2, `test/`, `ci/` and `chore/` 1 each, plus `mintplayer/` 3 and `backport/` 1 (GraphQL over the merged PRs,
    2026-11-22).
  - Owner, Q14: "Otherwise, B looks good".

## Proposed design (to be grilled: Q3–Q12)

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

## Questions (grill, paused after Q1)

| # | Question | Recommendation |
|---|---|---|
| Q1 | ~~How does a version reach a committed CHANGELOG.md?~~ | answered, then superseded: D1 → D2 |
| Q2 | ~~Where do the rendered notes live as source of truth?~~ | answered: D3 |
| Q3 | A repository-wide GitHub Release per `/release` run: yes or no? And its tag name? | No Release in v1. Use the CodeCoverage repository page instead. A non-package tag risks the engine and the downstream `release` webhook (S2). |
| Q4 | Relevance filter: the type list, and does D15 adopt it (dropping "Other changes")? | Yes, one filter for both. |
| Q5 | Entry text: corrected PR title, an optional `## Changelog` body block, or the issue title? | Title, with the body block as the override. Never the issue title (prior art). |
| Q6 | Breaking changes: a section of their own? D8 makes a break inside a platform generation a minor. | A "Breaking changes" section from `!`, independent of the version bump. |
| Q7 | Any committed file at all? For example a version-free root `CHANGELOG.md` that links to the Releases and the CodeCoverage page. | No file, or a one-time static pointer written by onboarding (no versions, never rewritten). |
| Q8 | Is the Spark-framework changelog package for deployed apps in scope of this PR? | Decide after S6. |
| Q9 | Does the changelog require title correction (D14) on, or only warn? | Warn. |
| Q10 | Should onboarding add a PR template with the type checklist when a repository has none? | Yes, only when the repository has none, using wording `TemplateWords` already maps. |
| Q11 | Title separator: keep `(#1/#2)` or switch to the owner's `(#1 - #2)`? | Keep `/`. `IssueSuffix` strips it, and existing titles use it. |
| Q12 | Unreleased visibility: only a CodeCoverage view, or also a draft GitHub Release per line? | Only the CodeCoverage view. Drafts add a second writer of Releases. |
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
  The action's `dist` is committed and byte-checked.

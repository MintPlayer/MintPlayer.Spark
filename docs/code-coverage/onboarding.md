# Set up features

**Set up features** on a repository's page in CodeCoverage sets up any mix of the app's features in one pull request
(#571, `docs/coverage_backport_PRD.md` D3). It replaced **Set up versioning**. The in-app guide is `/onboarding/setup`
(Guides → Getting started).

## Who may run it

An owner of the organization, or an admin of the repository on the forge, checked when the action runs
(`IDependencyOnboardingService.CheckAsync`). The repository must be connected, not archived, and have a default branch.
The git writer must be configured on the server.

## The features

| Feature | In the pull request | When it merges |
|---|---|---|
| Code coverage | A checklist item pointing to the upload step for your stack (`/coverage/setup`). The bot never edits a CI workflow it cannot know. | — |
| Dependency updates | `.github/workflows/dependency-lookup.yml` and `dependency-apply.yml`, a `dependencies:` section; `.github/dependabot.yml` is removed. See [dependency-updates.md](dependency-updates.md). | `DependencyUpdates` set to Enabled and the onboard signal sent. |
| Merge queue | A `mergeQueue:` section. With release lines, `branches: [<default>, supported-lines]`; an existing section without `branches` gets that key, one with its own list gets a note. See [merge-queue.md](merge-queue.md). | — |
| Package versioning | What [versioning.md § Onboarding](versioning.md#onboarding) lists. | One seed tag per package at the merge commit. |
| Release lines | `versioning.lines` and the table of old line branches to rename (D2). With exactly one push workflow running the versioning step, the current lines are promoted with `/release` and wait for that workflow (`current: promote`, segment T6; [versioning.md](versioning.md#promoted-lines-release-segment-p)), and ruleset 3 gives the App its bypass at once. With none or several, `lines: {}` (backports) and a note saying how to switch. An existing `versioning.lines` is left as it is; when it takes backports but exactly one workflow could prove a commit, a note says how to switch (gap G2). With lines, the versioning workflows' `push` filter and every `pull_request` filter that names the default branch also take `'release/*-*.x'`, so the lines publish and their pull requests get checked; a trigger without a filter, any other workflow's `push`, and filters that `branches-ignore` the lines or negate a branch (a note) are left alone (gap G3). Picking it includes package versioning. The bot also creates the release lines that have no branch: the current line of each package's platform, and, for each lower `netX.0` a package still targets, an older line once you choose **Create it** for it ([onboarding_line_cuts_PRD.md](../onboarding_line_cuts_PRD.md)). A package with its own `versioning.packages.<id>.anchor` is left to the versioning run. See [backports.md](backports.md). | The renames, the release lines created from the merge commit, and a regenerate-workflow pull request into each supported line (D9). |
| Size labels | Nothing. | `PrSizeLabels` set to Enabled. |

A choice that changes no file (size labels alone, or features already set up) opens no pull request: its settings,
renames and release lines apply at once, the lines from the default branch's head. Running **Set up features** again on
an onboarded repository is how its missing lines get created.

**Why the bot creates the lines:** no person can. Ruleset 3 requires the "Line policy" check on every line, a new
branch has no such check, and only the App (exempt) bypasses it: `POST /git/refs` for a line answers
`422 Reference update failed` for a person (Tools, 2026-11-12). The versioning run's own cut does not cover it either,
since it cuts only a major no package is tagged at yet.

## The pull request

- Branch `mintplayer/onboarding`, title "Set up MintPlayer CodeCoverage", one commit by the bot.
- `coverage.yml` sections are inserted in alphabetical order, each as its own block, so two bot pull requests that each
  add a section touch different lines (M10a).
- **Rebuilt on every new default-branch head while open** (`OnboardingRebuildEventsRecipient`): the bot plans it again on
  the new head and force-pushes the branch, so it never needs to merge the default branch and never conflicts. A push
  of your own to the branch is replaced by the next rebuild.
- Running **Set up features** again while it is open updates the same pull request with the new choice. Every step
  checks before it edits, so a later run proposes only what is missing.

The state lives in the repository's `VersioningOnboardingState` document (collection name kept for the production data):
the features, the base commit, the pull request, the seeds, the renames, the release lines to create (`Cuts`, and the
older lines you chose, `OlderLines`, which a rebuild plans again) and the on-merge settings.

## On merge

The merged webhook on `mintplayer/onboarding` reaches `IOnboardingService.MergedAsync` under the versioning run lock. In order:

1. Dependency updates and size labels are switched on.
2. Each planned rename runs through GitHub's `POST /repos/{o}/{r}/branches/{branch}/rename` with the App's token
   (Contents: write; a line is never the default branch). GitHub moves the open pull requests to the new name, and
   the REST API still answers for the old one, but `git fetch` does not: a clone tracking the old name must switch
   to the new one (`git fetch origin` then `git switch <new name>`). A refusal is noted on the state, not retried.
   Verified live on 2026-11-06 (#571 S7).
3. Each planned release line is created from the merge commit with the App's token (`POST /git/refs`), so it carries
   the onboarding's own line triggers. A line that exists already (the first versioning run cut it) counts as done; a
   refusal is noted on the state, not retried.
4. The history backfill runs, with the tags the owner chose to **Move** (#638), then the seed tags are pushed
   (`IVersioningOnboardingService.PushSeedsAsync`). The backfill runs whether or not the plan has seeds: a repository
   whose packages are all tagged has none (`docs/onboarding_merge_backfill_PRD.md` D1). A versioning run on the merge
   commit that gets there first pushes the seeds itself, but leaves the backfill to this step while line cuts are
   pending (D2), so a version goes on the line of its major.
5. For each supported line missing a workflow that the default branch's `mergeQueue` names (`postRebaseActionName`
   and each `autoResolve` rule's `action`), the file is copied from the default branch onto
   `mintplayer/onboarding-<line>` and a pull request into the line is opened. Check it builds with the line's SDKs.

6. Each older .NET line cut here from a commit that targets a higher major gets a narrowing pull request (#638 D15).

The merge is handled once: `MergeHandledAt` holds the merge commit (`SeedCommit` and `BackfilledAt` for step 4).

When Set up features has nothing to change in the repository, no pull request opens and steps 1-6 run at once, in the
same order (D3).

## Left to you

- The coverage upload step in CI.
- The merge queue's rulesets, only when the chooser could not set them up. On GitHub, with the merge queue picked, it
  asks for a GitHub popup and sets them up on the default branch right after the pull request opens
  ([merge-queue.md §3](merge-queue.md)). Without the rulesets OAuth app, or when the read is refused, the confirmation
  says they are left to set by hand. Ruleset 1 covers the release lines too, so their updates go through the queue.
- Anything under "To do by hand" in the pull request: a file the bot could not edit safely.

## App permissions

Contents: write and Pull requests: write; **Workflows: write** for the workflow files (the onboarding branch and the
line pull requests). Without it the push is refused and the action says the installation's owner has to accept it.

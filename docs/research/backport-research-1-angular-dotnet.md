# Backport mechanisms: Angular, dotnet, and bots (research for Spark #568)

Local clones: C:\Repos\dev-infra, C:\Repos\arcade, C:\Repos\backport-action (shallow). C:\Repos\angular and C:\Repos\aspnetcore used read-only.

## 1. Angular (ng-dev, angular/dev-infra)

Model: **backport happens at merge time, by a human-run CLI (`ng-dev pr merge`), not by a bot after merge.** A PR carries exactly one `target:` label; the label + release-train state decide the set of branches; the tool cherry-picks into all of them atomically.

- Config: C:\Repos\angular\.ng-dev\pull-request.mjs (merge config: required statuses, `githubApiMerge`, `requiredBaseCommits` per branch e.g. `main`, `10.0.x`), .ng-dev\release.mjs (npm packages, `releasePrLabels`).
- Labels: dev-infra/ng-dev/pr/common/labels/target.ts: `target: automation | feature | lts | major | minor | patch | rc`.
- Label -> branches: dev-infra/ng-dev/pr/common/targeting/labels.ts (`getTargetLabelConfigsForActiveReleaseTrains`, ~lines 50-175):
  - `major` -> [next/main] (error if next is not a major train).
  - `minor` -> [main] (or the exceptional-minor branch if the PR's GitHub base is it).
  - `patch` -> [main, latest `N.M.x`] + [rc branch if an RC train exists] + [exceptional-minor branch if any]. **If the PR's GitHub base branch already IS the patch branch, only that branch is used, no cherry-pick.** Comment in source: "useful if a PR could not be applied cleanly, and a separate PR for the patch branch has been created." This is the "separate PR on conflict" flow: author opens a second PR against `N.M.x`, labels it `target: patch`, and the tool merges it straight in.
  - `rc` -> [main, rc branch]; same direct-merge rule when base == rc branch.
  - `lts` -> only the PR's base branch, which must be an active LTS version branch (lts-branch.ts `assertActiveLtsBranch`). Source comment: LTS branches diverge too fast, so cherry-picking into LTS is NOT done; authors open a separate PR per LTS branch.
  - `feature` / `automation`: base branch only (automation only for next/version branches).
  - Exactly one target label required: `getMatchingTargetLabelConfigForPullRequest` (targeting/target-label.ts) throws on 0 or >1 matches.
- Release trains: dev-infra/ng-dev/release/versioning/active-release-trains.ts, release-trains.ts, version-branches.ts. Version branches are discovered by regex `^(\d+)\.(\d+)\.x$` (version-branches.ts:14), the version read from package.json on each branch. Trains: `next` (main), `releaseCandidate` (rc / feature freeze, may be null), `latest` (patch), plus `exceptionalMinor` flagged in package.json via `__ngDevExceptionalMinor__`.
- LTS tracking: release/versioning/long-term-support.ts: **derived from the npm registry dist-tags `vN-lts`**; branch = `${major}.${minor}.x` of that tagged version; active window computed from the `N.0.0` publish date (6 months active + 12 months LTS constants). So "which branches are supported" is data, not a hardcoded list.
- Applying commits: pr/merge/strategies/strategy.ts. `prepare()` fetches target branches + `pull/N/head` into temp local branches and **fails closed if fetched head SHA != validated head SHA** (race against author pushing after review). `check()` **dry-runs `git cherry-pick <range>` on every target branch first** (`_assertMergeableOrThrow`, `cherryPickIntoTargetBranches` with dryRun, `reset --hard HEAD~n`), so a conflict on any branch aborts BEFORE anything is merged anywhere. `cherry-pick --abort` on failure. Pushes all branches in one `git push --atomic` (`pushTargetBranchesUpstream`).
- Merge strategies: strategies/api-merge.ts (GitHub API merge into the PR's own base - usually `squash` or rebase - then re-fetch and `cherryPickIntoTargetBranches(..., {linkToOriginalCommits: true})` into the *other* branches, with `-x` so the message gets "(cherry picked from commit <sha>)"; lines ~184-215). strategies/autosquash-merge.ts: interactive autosquash rebase, `filter-branch --msg-filter` to append the PR number, cherry-pick into all targets, push, close PR via API (GitHub does not auto-close PRs whose commits land on non-default branches) and close linked issues manually (`closeLinkedIssues`).
- Conflict: `MergeConflictsFatalError` (merge/failures.ts:40): "Could not merge the specified pull request into the target branches due to conflicts: <branches>. Please rebase the PR or update the target label." Nothing is merged; human decides (rebase onto stricter base, change label, or open a separate PR against the patch branch).
- Post-merge: a comment "This PR was merged into the repository. The changes were merged into the following branches: - branch: sha" (strategy.ts `createMergeComment`).
- Also: pr/check-target-branches and pr/discover-new-conflicts subcommands (pre-merge warnings on which branches a PR can land in).

Takeaways: (a) one declarative label -> deterministic branch set; (b) all-or-nothing atomic across branches; (c) conflicts push the work back to a human via a separate PR on the release branch, and a "base == release branch" rule means the tool needs no special backport mode to accept it; (d) supported-branch discovery from branch-name regex + registry data.

## 2. dotnet: aspnetcore + arcade

- aspnetcore: C:\Repos\aspnetcore\.github\workflows\backport.yml (67 lines) is a thin caller of `dotnet/arcade/.github/workflows/backport-base.yml@<pinned sha>` (Dependabot-style bumps: "[main] (deps): Bump dotnet/arcade/.github/workflows/backport-base.yml (#70001)"). Triggers: `issue_comment: created` and a daily cron that calls `scheduled-action-cleanup-base.yml`. It passes only a PR description template (servicing template: Customer Impact, Regression?, Risk, Verification, Packaging changes reviewed, "When servicing release/2.3 edit eng/PatchConfig.props").
- Engine: C:\Repos\arcade\.github\workflows\backport-base.yml (395 lines). Key facts:
  - Trigger: comment line `/backport to <branch>` or `/backport <branch>` on a PR; regex line 64 `^\s*\/backport (?:to )?((?!to(?:\s|$))[a-zA-Z\d\/\.\-\_]+)` (one target per comment; several targets = several comments, as seen in dotnet/aspnetcore#68102 which has four "Started backporting to release/..." comments).
  - Guard: `repository_owners` input (default dotnet,microsoft) and `github.event.issue.pull_request != ''` (line 48).
  - Permission: `getCollaboratorPermissionLevel` must be `admin` or `write` (lines 130-147); otherwise error comment.
  - Requires merged PR (lines 149-161), else comment "...not run because the source pull request has not been merged".
  - Posts "Started backporting to `X` ([link to workflow run])" first. Unlocks a locked PR to be able to comment and re-locks it at the end.
  - Target branch must exist (`git ls-remote --exit-code --heads origin <branch>`).
  - Temp branch `backport/pr-<N>-to-<branch>`; **idempotent by branch name**: if it already exists on origin, the run force-pushes to it and does NOT open a second PR (lines 181-187, 322-327).
  - Apply strategy, in order: (1) if the PR's `merge_commit_sha` is a single-parent commit whose `git patch-id` equals the patch-id of the whole PR diff (`gh pr diff`) - i.e. squash / single-commit rebase - then `git cherry-pick <merge_commit>`; (2) otherwise or on failure (abort), `git fetch origin refs/pull/N/head` and `git am --3way --empty=keep --ignore-whitespace --keep-non-patch changes.patch` where the patch is `gh pr diff --patch N` (commit series). Extra switches via `additional_git_am_switches`; optional `conflict_resolution_command` hook (shell cmd, then `git add -A`, check `git diff --diff-filter=U`, `git am --continue`).
  - **Conflict: NO PR is opened, NO conflict markers are pushed.** It posts a comment `@user backporting to X failed, the patch most likely resulted in conflicts. Please backport manually!` with a `<details>` block holding the cherry-pick and git-am output, and fails the job. Real example: https://github.com/dotnet/aspnetcore/pull/68102 (Http3Stream.cs conflict into release/10.0). Other errors get a generic comment with a link to the run.
  - Success: opens a PR titled `[<branch>] <source title>` from the temp branch; body from template with `%source_pr_number%`, `%cc_users%` (commenter + author) etc.; labels from `pr_labels`.
  - Runs with `GITHUB_TOKEN` (permissions contents/issues/pull-requests write), so the bot's PRs do not trigger CI by default (known GitHub restriction; not verified in this repo).
- Servicing process: C:\Repos\aspnetcore\docs\Servicing.md. Backport PR to `release/X`; description must use the servicing template (aka.ms/aspnet/servicing/template); when "ready to merge" the author applies `servicing-consider`; Shiproom/Tactics (twice weekly) applies `servicing-approved` + milestone (target patch), `servicing-more-info`, or `servicing-rejected`. "Only a repository admin can merge changes to `release/*` branches"; admins merge `servicing-approved` PRs when the branches open for the patch. Status board: dotnet orgs project 448. M2 approval variant during RC1/RC2. docs\PreparingPatchUpdates.md: per-patch prep of branch.
  Takeaway: the **mechanism (backport PR creation) is cheap and permissive; the policy (what may ship) is separate, human, and enforced by labels + branch protection on release/***.

## 3. Bots

### korthout/backport-action (cloned C:\Repos\backport-action; action.yml, src/backport.ts)
- Trigger: `pull_request_target: closed` (merged) with label `backport <branch>` (default `label_pattern: ^backport ([^ ]+)$`, capture group = target branch; multiple labels = multiple targets), also optional `issue_comment` `/backport`, or explicit `target_branches` input. Only merged PRs (backport.ts:206).
- Commit selection `cherry_picking: auto` (default): squash -> the squash commit; rebase-merge -> the rebased commits; merge-commit -> the PR's commits; or `pull_request_head`. `merge_commits: fail|skip`. `cherry_picking_merge_mode: whitespace_tolerant` = `-Xignore-space-at-eol`. Uses `git cherry-pick` (not am).
- Conflict: `experimental.conflict_resolution`: `fail` (default; comment on source PR, no PR) or `draft_commit_conflicts` (commits the first conflict (markers) and opens a DRAFT PR; auto-merge disabled since drafts can't; instructions on the original PR on how to resolve and continue the cherry-pick).
- Idempotency: branch name `backport-${pull_number}-to-${target_branch}` (`branch_name` template). If cherry-pick result is empty: skipped "target already contains changes" (backport.ts ~L455). If push fails because the branch exists: fetches it to recover a previous run and retries PR creation. If PR creation returns 422 "A pull request already exists for ...": skipped "PR already exists" (L509-518).
- Linking: PR body template contains the source PR number; copy labels (`copy_labels_pattern`), assignees, milestone, reviewers; `add_labels`; `auto_merge_enabled`; `comment_style: legacy|summary`; outputs `created_pull_numbers`.
- Security note in README: use `pull_request_target` so forks' merged PRs get write token.

### tibdex/backport (src/backport.ts, last push 2024-08)
- Trigger: `pull_request` closed or `labeled` with regex `^backport (?<base>([^ ]+))$`; for a PR already merged adding the label later works. Warns that it only supports squash-merge (or single-commit rebase/merge) (`warnIfSquashIsNotTheOnlyAllowedMergeMethod`, L54-72).
- `git switch base; switch --create head; git cherry-pick -x <merge_commit_sha>`; on error `cherry-pick --abort` and throws; pushes; opens PR; labels copied except backport labels.
- Conflict: posts a comment with the error and a copy-paste manual recipe (git worktree add, switch --create, `cherry-pick -x --mainline 1 <sha>`, push, open PR with base=target) (`getFailedBackportCommentBody` L136-175). Per-branch failure doesn't block other branches.

### sorenlouv/backport (Kibana; tool used by `kibanamachine`)
- Trigger on merge of a PR labelled with versions (`v9.5.0`, `v8.19.0`); `.backportrc.json` in elastic/kibana: `branchLabelMapping {"^v9.7.0$":"main","^v(\d+).(\d+).\d+$":"$1.$2"}`, `targetBranchChoices` (list of every release branch), `targetPRLabels: ["backport"]`, `autoMerge: true`, `autoMergeMethod: squash`, `prDescription` with `<!--BACKPORT {commits} BACKPORT-->` marker (machine-readable provenance of what was backported).
- Version label -> branch mapping means the team labels in terms of *release versions*, the bot derives branches. The "main = highest version" label is skipped.
- On conflict the bot (kibanamachine) comments "Some backports could not be created" / "All backports failed" with manual `backport --pr N` CLI instructions (the tool can also be run locally, interactive conflict resolution in the user's checkout). Kibana now also has an agentic workflow (.github/workflows/failed-backport-resolver.md) triggered by that comment text that resolves failed backports by cherry-picking into active version branches and opening PRs (a "conflict -> follow-up automation" step). Also `auto-approve-backports.yml` and `assign-backport-reviewer.yml` (clean backports are auto-approved; conflicting ones need review).
- Squash is standard so one commit per backport.

### Kubernetes prow cherrypicker (kubernetes-sigs/prow, cmd/external-plugins/cherrypicker/server.go; docs site/content/en/docs/components/external-plugins/cherrypicker.md)
- Trigger: comment `/cherrypick <branch>` (works on open PRs too: bot replies "once the present PR merges, I will cherry-pick it on top of X in a new PR and assign it to you" and acts on merge) or label `cherrypick/<branch>` (default prefix `cherrypick/`). Multiple branches per comment allowed.
- Authorization: requester must be an org member (`IsMember`) unless `allowAll`; for labels, labeler or PR author must be a member (`labelRequester` L570).
- Apply: fetches PR patch from GitHub (`GetPullRequestPatch`), `git am` (`r.Am`, L678). Pushes to the **bot's own fork** (branch `cherry-pick-<N>-to-<branch>`) and opens a PR from the fork.
- Idempotency: if the branch exists, looks for an open PR with that head and comments "Looks like #N has already been cherry picked in <url>" (L653-667).
- Conflict: comment "#N failed to apply on top of branch X: <git output>"; with `issueOnConflict` also files an issue "Manual cherrypick required" assigned to the requester. No conflict-marker PR.
- Policy: separate plugin `cherrypick-approved`/label `cherry-pick-approved` and branch-manager approval gate merges on release branches (not read in detail here).

## Comparison / design lessons for MintPlayer.Spark

| Aspect | Angular ng-dev | arcade backport-base | korthout | tibdex | sqren (Kibana) | prow cherrypicker |
|---|---|---|---|---|---|---|
| When | at merge | after merge | after merge | after merge/label | after merge | after merge (or arm on open PR) |
| Trigger | one `target:` label | `/backport to X` comment | `backport X` label (or comment) | `backport X` label | version labels -> map | `/cherrypick X` or label |
| Apply | cherry-pick -x (dry run first) | cherry-pick merge commit if patch-id identical, else `git am --3way` | cherry-pick | cherry-pick -x | cherry-pick | git am |
| Conflict | abort everything; author makes separate PR on branch | comment w/ output, no PR | fail comment, or draft PR with markers | comment + manual recipe | comment + CLI instr. | comment (+ issue) |
| Idempotency | atomic push | deterministic branch, force-push, no 2nd PR | deterministic branch, empty = skipped, 422 = skipped | none notable | PR marker | branch exists -> link PR |
| Authz | merge rights | write/admin collaborator | workflow-level | workflow-level | label permissions | org member |

Common patterns worth copying:
1. Deterministic branch name `backport-<N>-to-<branch>` for idempotency; detect empty cherry-pick (already applied) and "PR already exists".
2. Squash/rebase/merge-commit handling: patch-id equality check (arcade) between merge commit diff and full PR diff is the most robust way to prefer one clean cherry-pick; fall back to commit series.
3. Cherry-pick with `-x` or equivalent provenance (marker in PR body) for later "was it backported?" queries.
4. Conflict: every project that is battle-tested treats a conflict as "tell a human, keep the evidence, don't merge broken code". The only variant that preserves partial work is korthout's draft PR with committed markers. Never auto-merge a conflicted PR.
5. Branch discovery: either declarative labels (Angular, korthout) or derived from data (Angular: branch regex + npm dist-tags for LTS; Kibana: version label mapping). For Spark (release branch per .NET / Angular major): branch regex like `release/(\d+)` or TFM-based mapping is analogous to Angular `N.M.x` + `branchLabelMapping`.
6. Authorization: only writers/org members may trigger (arcade, prow). Policy (what may ship) is separate from mechanism (dotnet: servicing labels + admin-only merge to release/*).
7. Report on the source PR: start comment, per-branch result comments, link to created PR(s).

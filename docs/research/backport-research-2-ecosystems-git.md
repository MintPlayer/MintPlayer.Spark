# Research 2: backport mechanisms in other ecosystems + git mechanics (issue #568)

Evidence legend: [V] = verified by me (command output / source read), [D] = from docs fetched via WebFetch, [R] = reasoning, not tested.
Local clones (shallow, blob:none): C:\Repos\miss-islington, C:\Repos\cherry-picker, C:\Repos\node-core-utils.
Experiment scripts and raw logs: scratchpad\exp1.sh/.log, exp2.sh/.log, exp3.sh/.log (repo in scratchpad\gitlab-exp\repo; git 2.47.1.windows.1).

## 1. Ecosystems

### CPython: miss-islington + cherry_picker (closest model)  [V from source]
- Trigger: GitHub App webhook on `pull_request` `closed` and `labeled`; acts only if `merged`.
  C:\Repos\miss-islington\miss_islington\backport_pr.py:16-17 (router), :19 (`if ...["merged"]`).
- Branch selection by label: `needs backport to 3.12` -> `label["name"].split()[-1]` = "3.12" (backport_pr.py:36-40).
  One label per target branch; the label is the whole configuration (no config file).
- Source commit: `pull_request.merge_commit_sha` (backport_pr.py:~25). CPython squash-merges, so it is one plain commit and no `-m` is needed.
- Posts an "I'm working now to backport this PR to: 3.12, 3.11" comment first (backport_pr.py:55-60), then enqueues one Celery/Redis task per branch,
  newest branch first (sorted descending by version tuple, :62-68). Queue = durable, retry = remove+re-add label.
- Worker (tasks.py:95 `backport_task_asyncio`): gets a GitHub App installation token (tasks.py:~108), clears leftover cherry-picker state from
  git config (`git config --local --remove-section cherry-picker`, tasks.py:143-147), then drives the `cherry_picker` library from a
  persistent local cpython clone.
- On conflict (`CherryPickException`, tasks.py:208-232): comment "Sorry, @author and @merger, I could not cleanly backport this to `3.12` due to a conflict.
  Please backport manually with cherry_picker ... `cherry_picker <sha> 3.12`", assign the ORIGINAL PR to the merger (`assign_pr_to_core_dev`), log, then
  `cp.abort_cherry_pick()`. No conflict markers are ever pushed. The bot never tries to resolve.
  Same pattern for other failures (InvalidRepo, BranchCheckout, GitHubException): comment + "remove and re-add the label to retry".
- Idempotency: I grepped for dedup logic: none (only "directory already exists"). Dedup is by humans: label is applied once, re-adding the label is the retry.
  The `closed` AND `labeled` handlers both fire, so a label added after merge triggers the backport; labels added before merge trigger at merge.
- Backport PR is auto-merged once CI is green and a core dev approved (README, WebFetch of python/miss-islington README.rst).
- cherry_picker (C:\Repos\cherry-picker\cherry_picker\cherry_picker.py):
  - Runs exactly `git cherry-pick -x <sha>` (line 320). Branch name `backport-<sha7>-<maint_branch>` (line 216-217), pushed to the bot's fork.
  - Commit message rewrite (get_updated_commit_message, :343-390): prefix `[3.12] `, `(cherry picked from commit <sha>)` line, adds `Co-authored-by:` of the original author via `git interpret-trailers --where start`.
  - PR title `[3.12] <orig title>` (create_gh_pr :457-467); the PR body links the original.
  - State machine persisted in the repo's git config (`cherry-picker.state`, WORKFLOW_STATES; paused state -> `--continue` / `--abort` / `--status`).
    This is how a human resumes after a conflict without redoing anything.
  - Config `.cherry_picker.toml`: team, repo, check_sha, fix_commit_msg, default_branch (cherry_picker.py:29-31).
- Take-aways for #568: label-per-target-branch, one PR per branch, `-x` trailer, bot never pushes conflict markers,
  conflict = comment + assign the author/merger + a copy-paste local command, retry = re-label.

### Node.js  [D from nodejs/node doc/contributing/backporting-to-release-lines.md; V from node-core-utils source]
- Release lines: staging branch `vN.x-staging` per major (backport doc; BackportSession.stagingBranch node-core-utils lib/backport_session.js:46-47).
- Labels: `backport-requested-vN.x` (needs backport; also set when a cherry-pick does not apply cleanly), `backport-open-vN.x` (backport PR open),
  `backported-to-vN.x` (done), `backport-blocked-vN.x`, `baking-for-lts`; LTS requires 2 weeks in Current. (`dont-land-on-vN.x` is not in that doc; I did not verify it.)
- Tooling is a human-run CLI, not a bot: `git node backport <pr> --to=N` (@node-core/utils). It creates `backport-<pr>-to-vN.x` from the staging branch and
  cherry-picks (lib/backport_session.js:184 `git checkout -b newBranch staging`, :198 prints `git cherry-pick <cherries>`), then the human resolves conflicts
  (`git add`, `git cherry-pick --continue`), runs tests and opens PR titled `[vN.x backport] <title>`.
- Conflict pre-check: `getPotentialConflicts(rev, staging)` (backport_session.js:53-115) lists ancestor commits of the target commit that are NOT yet on the
  staging branch, i.e. "you probably need to backport these first". Useful idea: warn about unbackported prerequisite PRs.
- `branch-diff` (nodejs/branch-diff README, [D]): lists commits on one branch missing from another. NOT patch-id: a commit counts as present if sha identical,
  or summary+description identical and `PR-URL` metadata matches. Flags `--require-label`, `--exclude-label`, `--patch-only`, `--find-matching-prs`, `--format sha`.
  Lesson: Node keeps a `PR-URL:` trailer in every commit so identity survives rewording and conflict resolution; patch-id would break after conflict resolution.

### Vue (vuejs/core)  [V via gh api]
- Branches: `main`, `minor`, `3.4` (last commit 2024-08-15, "release: v3.4.38", unprotected), `vapor-alpha-branch`, many contributor branches.
- CONTRIBUTING (.github/contributing.md:40-43): two work branches `main` (fixes) and `minor` (new API surface). That is a SemVer-level split, not per-platform.
- 3.4 is an abandoned maintenance branch; no backport automation seen. Releases tag `main` (releases list: v3.5.45 -> main, v3.6.0-rc.14 -> main).
- Not relevant as a model for "one branch per platform major".

### React (facebook/react)  [V via gh api, limited]
- Branches are `main` plus legacy `0.x-stable`, `15-stable`, `16.8.x`, `17.0.x` tags-as-branches, `18-3`; recent releases (v19.0.9 ... v19.3.1)
  all have target_commitish `main`. No maintained per-major release branches for the current majors, so no backport process to copy. I did not investigate the
  release-tooling scripts under scripts/release.

### Rust  [D from forge.rust-lang.org/release/backporting.html]
- Labels: `beta-nominated` (anyone with triage) -> release team decides -> `beta-accepted`; same for `stable-nominated`/`stable-accepted`.
- Manual: someone (often release team) cherry-picks accepted PRs onto a branch from `beta`, opens one rollup PR titled `[beta] ...`, bors merges (`@bors r+ rollup=never`).
- No bot, no documented conflict process ("may not apply cleanly, UI tests may need re-blessing").
- Lesson: two-stage label (nominated -> accepted) is a human gate. Not needed for #568 unless approval per branch is desired.

### Kubernetes  [D + source via WebFetch of hack/cherry_pick_pull.sh]
- `hack/cherry_pick_pull.sh upstream/release-1.30 <pr>`; one invocation = one PR per release branch, newest to oldest.
- Mechanism: `gh pr diff <pr> --patch > /tmp/<pr>.patch`, then `git am -3 /tmp/<pr>.patch`. On failure it lists unmerged files, asks the human to fix them
  in another window and prompts `Proceed? [y/n]`; trap runs `git am --abort`.
- Branch `automated-cherry-pick-of-#<pr>-<target>-<unix timestamp>`; title `Automated cherry pick of #<pr>: <title>`; body copies the release-note block and
  `/kind` lines from the original PR; new PR gets `do-not-merge/cherry-pick-not-approved` until a release manager approves (`cherrypickapproved` prow plugin -> `cherry-pick-approved`).
- Lesson: PR diff as a patch + `am -3` works with squash merges; metadata (kind, release note) copied to the new PR; a human approval label gates merge.

### Comparison for a bot
| | trigger | per-branch PR | on conflict | dedupe |
|---|---|---|---|---|
| CPython | label `needs backport to X.Y` (before or after merge) | yes, auto | comment + assign merger, abort, human runs `cherry_picker` | none; re-label to retry |
| Node | label `backport-requested-vN.x` (human CLI) | yes, manual | human resolves | `PR-URL` trailer, branch-diff |
| Rust | `beta-nominated` -> `beta-accepted` | one rollup, manual | human | label removed after landing |
| k8s | human runs script | yes | interactive resolve | none |
| Vue/React | none | n/a | n/a | n/a |

## 2. Git mechanics (all [V] unless marked)

Setup (exp1.sh): `main` and `release/10` share a base. main bumped global.json (10.0.100 -> 11.0.100) and `<TargetFramework>net10.0 -> net11.0`;
release/10 kept net10 and added a drift commit to code.txt. Then main got:
A = edit of code.txt (far from drift), B = change `<Nullable>enable -> disable` on the line right BELOW `<TargetFramework>`,
C = global.json 11.0.100 -> 11.0.200 (the marker line itself), M = a true `--no-ff` merge commit adding feat.txt.

### 2.1 Cherry-pick removes the commit from the source? REFUTED
```
git log --oneline main | head -8 > main_before.txt ; git cherry-pick -x $A (on release/10)  -> exit 0
git log --oneline main | head -8 > main_after.txt   ; diff main_before.txt main_after.txt   -> "main UNCHANGED after cherry-pick"
```
Cherry-pick creates a NEW commit on the current branch (different sha ef34409 vs 0be8b6f) and never writes to the source branch. The issue's claim is false
(exp1.log lines 4-9). It is also impossible in principle: it only moves HEAD of the checked-out branch. (The confusion probably comes from `git cherry-pick`
being compared to a move/rebase.)

### 2.2 Method comparison on B (adjacent-to-marker change)
Release has `net10.0` + `Nullable enable`; B's patch context says `net11.0` + `Nullable enable` -> the hunk's context does not match.

| Method | Result | Conflict markers? | Notes |
|---|---|---|---|
| `git cherry-pick -x B` | exit 1, `UU App.csproj` | yes: `<<<<<<< HEAD` ... `>>>>>>> 5f86913 (PR B: Nullable disable)` (exp1.log 26-38) | Real 3-way merge using B's parent as base. Both lines (TargetFramework + Nullable) land inside ONE conflict hunk because they are adjacent. Needs `--abort`/`--continue`/`--skip`. |
| `git format-patch --stdout \| git am -3` | exit 128, same conflict, label is the commit title (exp2.log 3-30) | yes | "Using index info to reconstruct a base tree... Falling back to patching base and 3-way merge". Works only because the blob ids in the patch's `index` line exist in the repo; with a patch whose blobs are missing, -3 cannot rebuild the base. |
| `git am` (no -3) | exit 128, "patch does not apply" | NO | No markers, nothing to resolve; useless for a bot. |
| `git apply --3way` | exit 1, "Applied patch to 'App.csproj' with conflicts", markers labelled `ours`/`theirs` (exp2.log 42-58) | yes | Works on the working tree/index, creates no commit, no authorship/message. Good for a server doing its own commit. |
| `git apply` (plain) | exit 1, patch does not apply | NO | |
| `git apply --reject` | exit 1, writes `App.csproj.rej` | NO (rej file) | Not mergeable in-place. |
| `git merge-file -p ours base theirs` | exit code = number of conflicts (1), markers printed (exp2.log 83-95) | yes | Per-file plumbing; you supply the three blobs (`git show B~1:f`, `git show B:f`, `git show HEAD:f`). `--zdiff3` also prints the base section. `--ours/--theirs/--union` auto-resolve. No commit semantics. |
| `git merge-tree --write-tree --merge-base=B~1 release/10 B` | exit 1, prints a tree id + the conflicted stage entries 1/2/3 + "CONFLICT (content)" (exp3.log 87-99) | n/a: no working tree | Server-side dry run: detects conflict without touching any checkout. `--name-only --no-messages` returns just the conflicted paths. Git >= 2.38. Ideal for a GitHub App that wants "will it apply?" before creating a branch. |

Conclusion: `cherry-pick -x`, `am -3`, `apply --3way`, `merge-file` all produce a true 3-way merge with real markers; `am` without -3 and plain `apply` do not.
For a bot: `cherry-pick -x` (what miss-islington/cherry_picker use) or `merge-tree` for the dry run.

### 2.3 TargetFramework / marker line scenarios
- Change adjacent to the differing line (B): CONFLICT with every method that does a 3-way merge (above). Git conflicts when two changes touch the same or ADJACENT lines (no unchanged line between them).
- Change ON the differing line (C: global.json version bump, release has a different version): CONFLICT with markers (exp1.log 39-55). Even though the bump is semantically irrelevant for release/10.
- Change far from the marker (A, code.txt): clean "Auto-merging code.txt" (exp1.log 4).
- Cure by layout (exp3.sh section C): when a stable line (a comment, a blank line) separates the volatile marker line from the line being edited, the same kind of change (B2) cherry-picks cleanly ("Auto-merging", exit 0, TargetFramework stays net10.0, Nullable becomes disable). So the cheapest conflict reduction is structural: keep every platform-specific line isolated (one per file, ideally in Directory.Build.props / a dedicated props file, a separate `global.json`, and package versions in separate lines in Directory.Packages.props, sorted/blank-line-separated) so that PRs rarely edit lines adjacent to them.

### 2.4 Squash-merge commit vs true merge commit
- Squash-merge (what GitHub makes: one parent): plain `cherry-pick -x <sha>` works (A, exp1.log 4-12).
- True merge commit M: `git cherry-pick -x M` -> `error: commit d01240a is a merge but no -m option was given.`, exit 128 (exp1.log 57-59).
  `git cherry-pick -x -m 1 M` -> succeeded, produced ONE commit "Merge PR M" with feat.txt, trailer `(cherry picked from commit d01240a...)`, i.e. it folds the whole merged branch into a single commit
  against the first parent (exp1.log 61-68). `-m 1` = mainline is the first parent = the target branch side.
- [R, from GitHub docs/API knowledge, not tested here] `pull_request.merge_commit_sha`: squash = the squash commit; merge commit = the merge commit (use -m 1);
  rebase-merge = the LAST rebased commit only (the earlier commits are not named by this field; use the PR's commits list `GET /pulls/{n}/commits` or compare base...head from the `merged` event).
  A robust bot should read the merge method, or detect parent count: `git rev-list --parents -n1 <sha>` (2 parents -> `-m 1`) and for rebase merges cherry-pick the range.

### 2.5 Dangerous auto-resolve strategies (found by testing)
- `git cherry-pick -X ours B`: exit 1 with "The previous cherry-pick is now empty"; App.csproj stayed all-"ours" (net10.0, Nullable enable). B's REAL change (Nullable disable) was silently discarded
  because it sat in the same conflict hunk as the marker line (exp2.log 123-145). `-X patience` still conflicted (exp2.log 148-159).
- A per-file merge driver `git config merge.keeptarget.driver true` + `.git/info/attributes: global.json merge=keeptarget` (exp3.log 1-18): the global.json bump in C was dropped,
  global.json kept 10.0.100, and the cherry-pick ended as an EMPTY pick (exit 1 "nothing to commit"), which is the desired outcome for a pure marker file.
  The same driver on App.csproj (exp3.log 19-41) silently lost B's real Nullable change. So: only use "keep target version" on files that carry nothing but platform markers
  (global.json, maybe a props file with only TargetFramework) and never on mixed files (csproj, package.json, Directory.Packages.props).
- Practical implication: an empty cherry-pick must be detected (`git cherry-pick` exit 1 + clean tree; `--keep-redundant-commits`/`--allow-empty` to keep, `--skip` otherwise) and reported as "nothing to backport (already present or only touched marker files)" rather than as a failure or a silent success.
  Also report which hunks were auto-dropped; the bot cannot know unless it compares diffs (list the paths that matched a keep-target rule and show `git diff --stat` before/after).

### 2.6 rerere
`git config rerere.enabled true`; resolve B by hand; commit. `.git/rr-cache/37b6b838...` created. Reset, repeat the same cherry-pick:
`Resolved 'App.csproj' using previous resolution.` and the working tree has the resolved content (exp3.log 42-67; index remains `UU`, still needs `git add` + `--continue`).
Limits: [V] it keys on the exact conflict text (preimage hash). Works for "same PR, same target branch, retried" (e.g. bot re-run after a human fixed it once)
and for the same conflict reappearing across N picks. It does NOT generalize across different PRs touching the marker lines (each conflict body differs) and the rr-cache lives in `.git/rr-cache` of that one clone,
so a stateless bot needs a persistent clone (as miss-islington has) or has to persist rr-cache (cache dir / shared via a separate branch). [R]

### 2.7 Detecting "already backported" (idempotence)
- `-x` trailer: after `git cherry-pick -x A`, message ends `(cherry picked from commit 0be8b6f5...)`. Detection:
  `git log release/10 --grep="cherry picked from commit $A" --format=%h` -> `fb54a4c` (exp2.log 202-203). Survives conflict resolution and rerere, fails if someone squashes/rewrites messages.
  Caveat: the -x line records the sha on the default branch; if the default branch is squash-merged the sha is stable (good), but if a PR is rebased-merged, record every commit sha.
- `git cherry -v release/10 main`: `-` = an equivalent patch-id exists upstream, `+` = not (exp2.log 191-196): A showed `-`, B/C/feat showed `+`. patch-id equal for A and its pick
  (`git patch-id --stable` -> a3a8ff29... both, exp2.log 205-206). `git rev-list --cherry-mark --left-right main...release/10` marks them `=` (exp2.log 208-214).
  Caveat [R]: patch-id hashes the diff hunks; after conflict resolution (or any context change) the id differs, so a conflict-resolved backport shows `+` forever. Use patch-id as a fast first
  check and the `-x` trailer (or a PR-number trailer / PR label / marker in PR body) as the authoritative record. Node chose a `PR-URL:` trailer; cherry_picker adds the sha line.
- Re-applying an already applied commit: `git cherry-pick -x A` a second time -> exit 1, "The previous cherry-pick is now empty" with a clean tree (exp2.log 176-190). Git does not
  say "already applied" loudly; the bot should pre-check with the trailer grep and treat the empty result as "already backported".
- Dedup on the GitHub side for a stateless App: look for an open or merged PR whose head branch is deterministic (`backport/<pr>-to-<branch>`, like cherry_picker's `backport-<sha7>-<branch>`) or a hidden marker
  (`<!-- backport-of: #123 -->`) in the PR body, plus a `backported-to-X` label on the original PR (Node), before creating anything.

### 2.8 Detecting conflict markers programmatically (exp3.log 82-86)
After a failed pick: `git diff --name-only --diff-filter=U` -> `App.csproj`; `git diff --check` -> `App.csproj:3: leftover conflict marker` etc. (for sanity before commit/push).

## 3. Recommended design implications for #568 (from this research)
1. Trigger = label on the merged PR (`backport to release/net10` style, one label per target branch), plus optional "all supported branches" via a repo config; works before and after merge (CPython pattern).
2. One PR per target branch, deterministic head branch name, title `[release/10] <title> (#123)`, `-x` trailer and a PR-number marker for idempotence.
3. Use cherry-pick (squash commits: plain; merge commits: `-m 1`; rebase merges: range). Dry-run with `git merge-tree --write-tree` (no working tree needed).
4. Conflict policy (CPython): never push markers on automatic runs; abort, comment on the original PR with conflicted files + the exact local command, assign the merger; retry via re-label. Optionally offer (as an explicit opt-in, Rust/k8s style)
   a "draft PR with markers" mode so a human can fix in the GitHub UI/branch. Not in CPython, my suggestion.
5. Per-file marker policy: a declarative list in coverage.yml (e.g. `keep-target: [global.json]`) implemented as a merge driver/attributes or post-step "checkout target version", ONLY for pure-marker files; warn when an auto-dropped hunk was non-empty. Others (csproj, package.json, lockfiles) go through normal conflict handling. Lockfiles: regenerate (never merge) after the pick: [R] standard practice.
6. Reduce conflicts structurally: isolate platform lines (own lines separated by stable context, Directory.Build.props/Directory.Packages.props layout), see 2.3.
7. Warn on missing prerequisites like Node's `getPotentialConflicts` (ancestor commits not on the target branch) - optional.

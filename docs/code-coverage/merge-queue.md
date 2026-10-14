# CodeCoverage — the merge queue

A merge queue for GitHub repositories, run by CodeCoverage itself. The design and its decision log are in
[`../coverage_merge_queue_PRD.md`](../coverage_merge_queue_PRD.md), whose §0 table (O1–O20, D12–D51) supersedes its
body. This page describes what is built; where it and the PRD's body disagree, this page and §0 win.

**Status (2026-10-14):** built and tested piece by piece (PRD M2–M5, M7), not yet deployed. No end-to-end run against a
live repository yet (M10). The repository-page button (M6) and the in-app setup guide (O13) are still being built.

Paths below are relative to `apps/CodeCoverage/` unless they start with `.github/` or `docs/`.

## 1. What it does

Two pull requests that both change a Spark model always conflict in `App_Data/modelHashes.json`, and both sides are
wrong until synchronize runs again on the rebased branch ([`../model-hash.md`](../model-hash.md#merge-conflicts)).
GitHub's own merge queue evicts on any conflict. This one takes the base side of the wholly generated files, has the
repository's regenerate workflow rewrite them on a queue branch, publishes **one** squashed commit to the pull request,
waits for every check, and merges. Any other conflict, a failed check, or a push it did not make evicts the pull
request, with the reason on its check run.

```
enqueue: label · /queue · button (O8)
  │  refused, not queued, unless every enqueue check passes (§4.2, §5)
  ▼
QUEUED       one pull request at a time per base branch (D11)
  ▼
START        the head already contains the latest base? ── yes ─► case 1: CHECKS on the head itself, nothing pushed
  ▼ no
PREPARE      git writer: bare blob-less clone, fetch refs/pull/N/head, assert head == the SHA pinned at enqueue (T3)
             net change touches .github/** ─► EVICT touches-workflows (D12)
             merge-tree base ⟵ head; a conflict only in autoResolve files ─► take the base side (O1, O19)
             any other conflict ─► EVICT; commit-tree ONE commit on the base; lease-push to mq/<base>/pr-<n>
  ▼          nothing resolved, and postRebaseWhen is not always ─► case 2: PUBLISH without a patch
REGENERATE   case 3: the workflow's blob on mq/ must equal the default branch's (D12); dispatch postRebaseActionName
             on mq/<base>/pr-<n>; the run must build the prepared SHA (D14), succeed, and upload merge-queue-patch.
             It never pushes (D4).
  ▼
PUBLISH      git writer: validate the patch (T4); prepared tree + patch = ONE squashed commit;
             lease-push to the pull request's branch (a fork's through "Allow edits by maintainers", O3)
  ▼
CHECKS       every check run, commit status and check suite on that commit, minus exclusions (O12);
             a failed check run is re-run retryFlaky times (D9); the base moved ─► back to START
  ▼
MERGE        the person who queued it still has write (O9); the base is unchanged, else back to START;
             merge-async squash, sha = the final commit, merge_action = direct_merge (D13); poll until merged (D17)
  ▼
MERGED       label removed, mq/ branch deleted under a lease, the lane's next pull request starts

Any step ─► EVICTED <reason> (§5): check run + comment, label removed, mq/ branch deleted, the next one starts.
A push to the pull request that the queue did not make ─► EVICTED author-push, at any step (T3).
```

- **Three cases (O20).** Case 1 merges the head as it is, on its own checks. Case 2 (the base moved, the merge is clean)
  and case 3 (resolved and regenerated) publish one new commit, and CI runs again on it. A clean textual merge is never
  trusted on its own, because it misses semantic conflicts.
- **Why a queue branch (D5).** Steps PREPARE and REGENERATE run on `mq/<base>/pr-<n>`, not on the pull request. The
  bare rebase has stale hashes, so it is known to fail `--spark-verify-model`; keeping it off the pull request means the
  consumer's PR CI runs once, on the final commit. The queue branch is deleted when the entry ends.
- **The queue knows its own pushes by SHA** (`KnownShas`, D46); any other new head on the pull request evicts it.

The code: the saga `CodeCoverage/MergeQueue/MergeQueueEngine.cs` (messaging lane `coverage-merge-queue`); the git writer
`CodeCoverage.GitWriter` with the resolver in `CodeCoverage.Git/MergeQueue/`; `IForgeMergeQueue` and `CheckGate`
(`CodeCoverage.Library/Forge/`), implemented by `GitHubForgeMergeQueue` (`CodeCoverage.GithubIntegration/Services/`).

## 2. Enabling it for a repository

### 2.1 The GitHub App: permissions and events

The full App reference is [`hosting.md`](hosting.md) §6, "GitHub App". What the queue needs:

| Permission | Level | The queue uses it for |
|---|---|---|
| Actions | **Read & write** (new) | Dispatching the regenerate workflow, reading its run, downloading `merge-queue-patch`, re-running a failed Actions job (D9). |
| Workflows | **Read & write** (new) | GitHub refuses an App push that brings in commits touching `.github/workflows/**`. A rebased branch can carry the base's own workflow changes. |
| Commit statuses | Read-only (new) | The gate counts commit statuses as well as check runs (O12). Not yet measured with the App (D40). |
| Contents | Read & write | The writer's pushes, `merge-async`, `coverage.yml`, the workflow blob check (D12). |
| Pull requests | Read & write | The pull request itself, reviews (O11), the label, the queue's comments. |
| Checks | Read & write | The "Merge queue" check run; reading check runs and suites; re-requesting another App's check. |
| Metadata | Read-only | Roles (`collaborators/{login}/permission`, O9) and rulesets with `current_user_can_bypass` (D16). |
| Issues | Read-only | The `issue_comment` event (`/queue`, `/dequeue`). |

**Administration is not requested.** Classic branch protection's required checks are readable without it; its review
settings are not. The queue then reports the branch as `ChecksOnly` and gives no O18 warnings from classic protection.

| Webhook event | The queue uses it for |
|---|---|
| Pull request | `labeled`/`unlabeled` (label commands), `synchronize` (a push to a queued pull request), `closed` |
| Push | The base branch moved: back to PREPARE |
| Issue comment | `/queue` and `/dequeue` |
| Pull request review | Wakes the checks wait |
| Check run | Checks changed on the commit the queue waits for |
| Check suite (new) | "Every suite on the final commit has completed" (O12) |
| Workflow run (new) | The regenerate run completed |
| Status (new) | Commit statuses (O12) |

Check runs, check suites, statuses, workflow runs and reviews are forwarded only while the repository's queue has
something in it: one point load of `MergeQueues/{provider}/{repoId}` per delivery (D43).

**Accepting the raised permissions (O17).** A self-hosted App first gets the new rows on its GitHub settings page.
Then every installation has to accept them. The CodeCoverage Account page (`/po/account/{id}`) shows the account's
managers a notice with the installation's own acceptance link while anything the App declares is still pending. A
queue call that GitHub refuses for a permission ends the entry with `permission`, and the check run and the comment
carry the same link.

### 2.2 `coverage.yml` → `mergeQueue:`

The file behaviour of the queue lives in the repository; the trust policy lives in CodeCoverage (§2.4, O15).
**`mergeQueue:` is read from the repository's default branch, never from the pull request's base (O16).** Otherwise a
pull request into branch A could run under a configuration someone had widened on A. A change to the section takes
effect once it is merged to the default branch. Parser: `CodeCoverage/MergeQueue/MergeQueueYml.cs`.

A Spark repository needs no more than this. The root [`coverage.yml`](../../coverage.yml) of this repository is the
same, with the defaults spelled out.

```yaml
mergeQueue:
  enabled: true
  branches: [master]
  postRebaseActionName: regenerate.yml
  regeneratedPaths: ["**/App_Data/modelHashes.json", "**/App_Data/securityPosture.txt",
                     "**/App_Data/translations.json", "**/App_Data/security.json", "**/App_Data/Model/*.json"]
  autoResolve:
    - path: "**/App_Data/modelHashes.json"      # take-base, any line (O19)
    - path: "**/App_Data/securityPosture.txt"
```

| Key | Type | Default | Rules |
|---|---|---|---|
| `enabled` | bool | `false` | The only on/off switch; there is none in the UI (D49). |
| `branches` | list of branch names | `[<default branch>]` | Not empty, no empty name. A pull request into any other branch is refused (`branch`). |
| `mergeMethod` | string | `squash` | `squash` only in v1. |
| `enqueue.label` | string | `merge-queue` | 1–50 characters. The queue does not create the label (`gh label create merge-queue`). |
| `postRebaseActionName` | file name | none | A workflow file name under `.github/workflows`, without a path: `^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\.ya?ml$`. |
| `postRebaseTimeout` | duration | `20m` | `90s`, `20m`, `1h`; a bare number is minutes and seconds round up; 1–360 minutes. |
| `postRebaseWhen` | `auto-resolved` · `always` · `never` | `auto-resolved` | Dispatch only after a resolved conflict, after every rebase, or never. |
| `regeneratedPaths` | list of globs | none | The only paths a regenerate patch may touch (T4). No empty pattern. |
| `autoResolve[].path` | glob | — | Required. |
| `autoResolve[].strategy` | `take-base` | `take-base` | Anything else is an error: `json-union` was dropped (O1). |
| `autoResolve[].keys` | list of top-level JSON member names | none | None: a conflict on any line of the file resolves (O19). Given: only lines that hold one of these members do (O2), and every side must be a JSON object. |
| `checks.retryFlaky` | int | `1` | 0–3. Re-runs of a failed check run before evicting (D9). |
| `checks.timeout` | duration | `60m` | 5–720 minutes, from the moment the queue starts waiting for checks. |

Globs: `**` spans directories, `*` and `?` stay within one, and matching is case-sensitive, like git paths
(`CodeCoverage.Git/PathGlob.cs`). Across keys: `autoResolve` needs `postRebaseActionName` and cannot be combined with
`postRebaseWhen: never`, because the base side of a generated file is stale on purpose and only the regenerate run
fixes it. `postRebaseWhen: always` needs an action, and an action that can run needs `regeneratedPaths`.

**Only wholly generated files belong in `autoResolve`** (O1): `modelHashes.json` and `securityPosture.txt` for Spark.
A conflict in a hand-authored file (`translations.json`, `security.json`, `programUnits.json`, `Model/<Entity>.json`)
always evicts, though synchronize's sorted output keeps most of their merges clean. `regeneratedPaths` is wider: it
lists every file synchronize may rewrite.

Unknown keys are ignored, so a newer file does not break an older server. Errors are returned, not thrown. A `/queue` or
the button gets a `configuration` refusal that lists them, and so does the configured label on a broken section. A
label on a repository whose queue is not enabled, or any other label, is ignored without a word.

**Known gap: always list at least one `regeneratedPaths` entry**, even without a regenerate step. The parser accepts
an empty list then, but the git writer refuses a publish request whose `regeneratedPaths` is empty. It answers a bare
400 (`WireValidation.Globs` needs 1–64 globs), which the queue treats as transient, so every case-2 entry is retried six
times and evicted as `error`. The same happens with more than 64 `regeneratedPaths` or `autoResolve` entries, a glob over
256 characters or with a backslash or a leading `/`, and a branch name outside `[A-Za-z0-9._/-]`. This is inferred from
the code; no test covers it.

### 2.3 The regenerate workflow (`postRebaseActionName`)

It runs only when `postRebaseActionName` is set, and either a conflict was resolved (`auto-resolved`) or always.

**The contract the queue holds the workflow to:**

- It exists at `.github/workflows/<postRebaseActionName>` on the **default branch**: a workflow GitHub has not
  registered there cannot be dispatched (D12). Its blob on the `mq/` commit must equal the default branch's.
- It declares `workflow_dispatch` with the inputs `expected_sha`, `pr_number` and `queue_run_id`. GitHub refuses a
  dispatch carrying an input the workflow does not declare.
- The run builds the prepared commit, concludes `success`, and uploads an artifact named `merge-queue-patch` holding
  exactly one file: the patch, at most 2 MiB, `git diff --binary --no-renames` text edits of regular files (D35).
  **An empty patch is uploaded too**: green + empty means "nothing to regenerate", while a failed run or a missing
  artifact evicts.
- It never commits or pushes (D4): a `GITHUB_TOKEN` push starts no CI, so no check would ever appear on the result.

**For a Spark repository**, copy [`templates/regenerate.yml`](templates/regenerate.yml) to
`.github/workflows/regenerate.yml` on the default branch, list the apps, and pin the action. The action is
[`.github/actions/spark-regenerate`](../../.github/actions/spark-regenerate/README.md). It checks that `HEAD` is
`expected_sha` and that the tree is clean. It builds in Debug (without the SPA), then runs
`--spark-synchronize-model` and `--spark-synchronize-security` per project in the same `dotnet run` form as the verify
steps (D34). It runs both a second time and fails unless that changes nothing, and then uploads the patch.

Inputs: `expected_sha` (pass the dispatch input through) and `projects` (app `.csproj` paths, one per line) are
required. `dotnet-version` (empty reads `global.json`), `configuration` (`Debug`, keep it equal to the verify step's),
`skip-spa-build` (`true`) and `extra-build-args` are optional. Outputs: `changed`, `file-count`, `patch-path`. This
repository dogfoods it with the local path in [`.github/workflows/regenerate.yml`](../../.github/workflows/regenerate.yml).

**The workflow builds and runs untrusted pull-request code in your repository's context (T5).** So:

- `permissions: contents: read` and nothing more; never `id-token: write`. GitHub-hosted runners only: on a
  self-hosted one the pull request's build code would run on your machine.
- No secrets, no `${{ secrets.* }}`, no environment. Restore from public feeds only: a repository that needs a private
  feed cannot use this without handing untrusted code a credential.
- `actions/checkout` with `ref: ${{ github.sha }}` (the dispatched `mq/` head) and `persist-credentials: false`, so the
  job token is not left in `.git/config` while the build runs.
- **Pin the action by a full commit SHA, never `@master` (D36).** This monorepo's default branch moves many times a day,
  and there is no moving tag for this action.
- `timeout-minutes` at or below `postRebaseTimeout` (the template has 20 for 20m). S4 measured 36–42 s for DemoApp
  locally; the CI measurement is still open.

### 2.4 Organization and repository settings (trust policy, in the database)

The approval floor and the check exclusions are set on CodeCoverage, not in the repository, so a pull request cannot
change them (O15). They are set on the **Account page** (group "Merge queue"), and a repository's detail page can
override them. Only the account's owner may change them, through the live forge ownership check. They are readable like
`DeleteBranchOnPrClose` (D49).

**Approval floor (O11, tightened by D47).** It is checked at enqueue; GitHub's ruleset 2 still enforces its own reviews
at the merge.

| Value | Label | Needs before queueing |
|---|---|---|
| `ExternalAuthors` | Approval for external authors **(default)** | Nothing when the author has `write`; otherwise an approval. Bots such as Dependabot have no role, so they need one. |
| `RepositoryRules` | Repository rules only | Nothing of the queue's own; the repository's rules decide. A single-maintainer organization can choose this. |
| `Always` | Always require an approval | An approval for every pull request. |

A repository's floor is "Use account setting" (`Inherit`) or one of the three. **An approval counts only** when it is
an `APPROVED` review of the pinned commit, by a `write` user other than the author, and still that reviewer's latest
word. A later "changes requested" or a dismissal withdraws it; a comment does not (D47).

**Checks the merge queue ignores (O12).** These are names of check runs or commit-status contexts, matched exactly and
case-sensitively. A repository has "Ignored checks from" set to `Inherit` (the account's list) or `Override` (its own
list, possibly empty), so "exclude nothing here" can be said. A check a ruleset requires is always waited for and cannot
be excluded. The queue's own "Merge queue" check is always ignored. The typical exclusion is a secret scanner that flags
the hash lines of `modelHashes.json`.

**What the queue waits for (O12, D41, D45).** On the commit it is about to merge, it waits for every check run, commit
status and check suite that is not excluded, and it fails at once on any failure. `neutral` and `skipped` pass; every
other conclusion fails (`cancelled`, `timed_out`, `action_required` and `stale` too), as does a status `error`. On top
of that, every required check and every check that had reported on the **pinned head at enqueue** must report. So
queue a pull request after its CI has started: a workflow that never ran on the queued commit is not in that minimum.
A check suite with no runs is not waited for: GitHub creates one for every App with checks permission, and it can stay
queued forever.

## 3. Recommended GitHub setup

The aim: every merge goes through the queue, and the queue is held to exactly the reviews and checks everyone else is.
One ruleset cannot do both, because the App would need a bypass that also skips the reviews and checks. So there are
**two rulesets** (O9), measured live with an App on a public scratch repository (D16). **Ruleset 1** restricts updates
of the queue's branches, with the App as its only bypass actor (mode `exempt`), so *Squash and merge* is blocked for
everyone, maintainers and admins included. **Ruleset 2** holds the reviews and required checks, with **no** bypass actors.

The role check of §4.2 is independent of ruleset 1, so maintainers keep `write` and can still queue. An App exempt from
ruleset 1 is still held to ruleset 2, even with `bypass_rules: true`; a direct push to the branch is rejected (GH013), and
so is a merge while reviews or checks are unmet (D16).

**Ruleset 1** (`ruleset-1.json`; `<APP_ID>` is the App's numeric id):

```json
{
  "name": "merge-queue: restrict updates",
  "target": "branch",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["~DEFAULT_BRANCH"], "exclude": [] } },
  "rules": [{ "type": "update", "parameters": { "update_allows_fetch_and_merge": false } }],
  "bypass_actors": [{ "actor_id": <APP_ID>, "actor_type": "Integration", "bypass_mode": "exempt" }]
}
```

**Ruleset 2** (`ruleset-2.json`; one entry per required check):

```json
{
  "name": "merge-queue: reviews and checks",
  "target": "branch",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["~DEFAULT_BRANCH"], "exclude": [] } },
  "rules": [
    { "type": "pull_request", "parameters": {
        "required_approving_review_count": 1,
        "dismiss_stale_reviews_on_push": false,
        "require_last_push_approval": false,
        "require_code_owner_review": false,
        "required_review_thread_resolution": false } },
    { "type": "required_status_checks", "parameters": {
        "strict_required_status_checks_policy": true,
        "required_status_checks": [{ "context": "<CHECK_NAME>" }] } }
  ],
  "bypass_actors": []
}
```

```bash
gh api apps/<app-slug> --jq .id                                       # <APP_ID>
gh api --method POST repos/<owner>/<repo>/rulesets --input ruleset-1.json
gh api --method POST repos/<owner>/<repo>/rulesets --input ruleset-2.json
gh api repos/<owner>/<repo>/rules/branches/<branch>                   # what now applies to the branch
```

Notes on the two rulesets:

- **Branches.** Include every branch in `mergeQueue.branches` (`~DEFAULT_BRANCH`, or `refs/heads/<name>` patterns).
  Never target `~ALL`, and never cover `refs/heads/mq/**`. The queue creates, force-updates and deletes
  `mq/<base>/pr-<n>`, and force-pushes contributors' pull-request branches, so a rule that blocks either one
  (`pull_request`, `non_fast_forward`, `update`) ends those entries with `push-rejected`.
- **Approvals.** `required_approving_review_count` is what your team requires. A single-maintainer repository sets
  `0`, because GitHub does not let authors approve their own pull requests. The approval floor (§2.4) still holds
  outside authors to an approval.
- **Required checks.** Require the job that runs `--spark-verify-model` and `--spark-verify-security`: it is what proves
  the regenerate step did its job (T4, O19). The queue waits for every check anyway (O12); requiring it makes GitHub
  enforce it too, and a required check can never be excluded. **Never require the "Merge queue" check itself**: it is
  still in progress while the queue merges, so GitHub would refuse every merge the queue requests. (Inferred from the
  check run's states, not measured.)
- `strict_required_status_checks_policy: true` costs nothing: the queue only merges a commit built on the base as it is
  at merge time, and goes back to PREPARE when the base moves.
- GitHub adds defaults on creation, among them `allowed_merge_methods` (keep `squash` in it) and
  `require_extra_approval_for_unattributed_changes: true`. The spikes ran with that default, but never together with
  O18's settings and a regenerate commit: unmeasured until M10.
- **The queue checks the bypass itself** at every enqueue, with its own token. It refuses (`bypass`) when a ruleset
  holding a `pull_request` or `required_status_checks` rule shows `current_user_can_bypass` other than `never`, or
  cannot be read (T7).

### 3.1 Why "dismiss stale approvals" and "require approval of the most recent push" are off (O18)

GitHub dismisses an approval only when a push changes the pull request's *diff* (D18): a pure rebase or squash by the
queue keeps it, but the regenerate patch changes the diff. With `dismiss_stale_reviews_on_push` every model pull request
would lose its approval at PUBLISH, and with `require_last_push_approval` (the queue is always the last pusher) every
merge would need a human re-approval. The owner chose that the queue never pauses for one. It covers the gap itself: it
pins the head at enqueue and evicts on any push it did not make (T3), its own change is limited to the validated
`regeneratedPaths` (T4), and the verify checks run on the final commit. A repository that keeps either rule gets a
**warning** on the check run at enqueue, and then `merge-refused` with GitHub's rule-violation message.

### 3.2 Everything else

- **GitHub's native merge queue rule off (D8).** The two queues cannot share a branch; enqueue refuses `native-queue`
  when a ruleset requires it. The queue also passes `merge_action=direct_merge`, so it never ends up in GitHub's queue.
- **Squash merging allowed** (Settings → General → Pull Requests).
- **`branches-ignore: ['mq/**']`** on every `push:` trigger that would otherwise match the queue's branches (D14).
  Pushes to `mq/` fire `push` workflows, never `pull_request`. Their check runs land on the prepared commit, which *is*
  the final commit when nothing was regenerated (D31), so a push-only workflow there would gate the merge. A `push:`
  limited to `branches: [master]` needs nothing; this repository's workflows are all like that (D37).
- **Fork pull requests need "Allow edits by maintainers" ticked (O3).** The queue pushes the rebased commit to the
  fork's branch; there is no bot-made fork. GitHub offers the box only on forks owned by a user, not by an organization.
  Whether GitHub accepts an App's push through it is still unverified (S6, §8).
- **Private repositories on GitHub Free have no rulesets.** GitHub answers 403 "Upgrade to GitHub Pro…", which the
  queue reads as "no rules" (D15). The queue still waits for every check (O12), but nothing forces merges through it.

### 3.3 Merging what the queue will not

Ruleset 1 blocks hand merges for everyone, so a pull request the queue refuses by design needs an admin. That covers
three cases: a change under `.github/**` (`touches-workflows`, D12, which includes Dependabot's GitHub Actions updates),
a pull request from an organization-owned fork, and a fork push GitHub refused (`fork-push-rejected`). The admin sets
ruleset 1's enforcement to *Disabled* (Settings → Rules → Rulesets), merges once ruleset 2 is satisfied, and sets it
back to *Active*.

## 4. Using it

### 4.1 Queueing and dequeueing

| Way | Queue | Dequeue |
|---|---|---|
| Label | Add the `enqueue.label` label (`merge-queue`). | Remove it. |
| Comment | `/queue` as the first word of a new comment on the pull request. | `/dequeue`, the same way. |
| Button | On the CodeCoverage repository page (O8, M6): the user's linked GitHub login acts; without one, refused. | — |

- The actor is always the signed webhook's sender, or the signed-in user's linked login (T1). Text after the command is
  ignored ("/queue as @maintainer" is the commenter's command), and edited comments do not count. Every path then runs
  the same checks.
- **A bot never commands the queue** (D46): its label or comment is dropped, which also makes the queue ignore its own
  label removal.
- A second `/queue`, or the label re-applied, while queued changes nothing. A refused label is removed again and the
  check run says why; a refused comment or button gets a comment.

### 4.2 Who may

- **Queue:** a GitHub login with at least the `write` role on the repository (O9). The role is checked at enqueue and
  again right before the merge; a lost role evicts (`role-revoked`). The pull request must also be open, not a draft,
  into a branch listed in `mergeQueue.branches`, and meet the approval floor (§2.4).
- **Dequeue:** the pull request's author (withdrawing is not a privilege), or anyone with `write`. Anyone else is
  ignored; the attempt is noted in the entry's history.

### 4.3 What happens to the pull request

- **The queue rewrites the branch.** In cases 2 and 3 the pull request's branch is replaced, under a lease on the queued
  head, by **one** commit on the latest base. That commit carries the net change plus the regenerated files. Its author is
  the pull request author's login with `{login}@users.noreply.github.com`, its committer is `CodeCoverage merge queue`,
  and its message is `<title> (#<n>)` (D50). Other commit authors are not carried over as `Co-authored-by:` trailers.
  After an eviction past PUBLISH, the branch holds that commit: reset to it before pushing more.
- **Pushing while queued evicts** (`author-push`). The queue merges only the commit that was queued; queue it again to
  merge the new head.
- **The merge** is GitHub's squash merge with the title `<title> (#<n>)`, performed by the App.
- **At the end**, whatever the outcome: the label is removed, the `mq/` branch is deleted under a lease, and the next
  pull request in the lane starts. An eviction posts a comment with the reason and how to queue again; a merge posts
  nothing.

### 4.4 The "Merge queue" check run

The queue keeps one check run named **Merge queue** on the commit the pull request should have now: the queued head,
then the final commit (`MergeQueueCheckRenderer.cs`). Its Details link opens the repository on CodeCoverage
(`{Coverage:BaseUrl}/{provider}/r/{owner}/{name}`, only when the base URL is https).

Its title follows the state: *Queued, position n* (queued); *Rebasing onto the latest base*, *Regenerating generated
files*, *Publishing the rebased commit*, *Waiting for checks*, *Merging* (in progress); *Merged by the queue*
(success); *Removed from the queue* (neutral, dequeued); *Removed from the queue: reason* or *Not queued* (failure).

The summary holds the reason's message and the paths behind it (up to 50), the permission-acceptance link (O17), and a
table of the state, position, who queued it and how, the pinned head, the final commit, the regenerate run and the
merge commit. Then come the **Warnings** (O18's review settings) and the **Checks not waited for** (O12). Last is the
**Effective configuration** (O15): every `mergeQueue:` value plus `approvalFloor` and `checkExclusions`, each with its
source (`coverage.yml @ <default branch>`, `default`, `organization` or `repository`).

A run keeps the configuration it was queued with, so changing `coverage.yml` or a setting does not affect entries
already running. Re-running the check on GitHub does nothing; queue again with `/queue`.

## 5. Why a pull request left the queue

Each code is stored as the entry's `EvictionReason` and logged. An eviction's check-run title shows it; a refusal's
title is "Not queued", with the message in the summary. The codes come from `MergeQueueEngine` (`RefusalAsync`, the
`EvictAsync` calls); the rebase codes are `MergeQueueResolver.EvictionReason` (`CodeCoverage.Git/MergeQueue/`),
kebab-cased by the writer.

**Refused at enqueue** (state Refused, "Not queued"):

| Code | Meaning | What to do |
|---|---|---|
| `configuration` | `mergeQueue:` on the default branch has errors, listed in the summary. | Fix `coverage.yml` on the default branch. |
| `disabled` | No `enabled: true` on the default branch. Only a comment or the button hears this. | Enable it there. |
| `closed`, `draft` | The pull request is closed, or a draft. | Reopen it or mark it ready, then queue it. |
| `branch` | Its base is not in `mergeQueue.branches` (O16). | Retarget it, or add the branch on the default branch. |
| `role` | The actor has less than `write` (O9). | Ask someone with `write`. |
| `fork` | The fork is gone, or "Allow edits by maintainers" is not ticked (O3). | Tick the box. For an organization-owned fork, see §3.3. |
| `approval` | The approval floor is not met on the queued commit (O11, D47). | A writer other than the author approves the current head; queue again. |
| `native-queue` | The base requires GitHub's own merge queue (D8). | Remove that rule. |
| `bypass` | The App can bypass a ruleset with reviews or checks, or its bypass could not be read (T7). | Remove the App from that ruleset's bypass list (§3). |
| `permission` | GitHub refused a call for a permission. | Accept the new permissions (linked, O17). |

**Evicted during a run** (state Evicted, "Removed from the queue: *code*", with a comment):

| Code | Meaning | What to do |
|---|---|---|
| `author-push` | The branch moved to a commit the queue did not write, at any step (T3). | Queue it again; the new head is what gets merged. |
| `retargeted` | The pull request's base was changed. | Queue it again. |
| `base-missing` | The base branch no longer exists. | — |
| `base-churn` | The base moved more than 5 times during one run. | Queue it again when the base is quieter. |
| `touches-workflows` | The pull request's own change touches `.github/**` (D12). | Merge it by hand (§3.3). |
| `conflict-outside-allow-list` | A conflict in a file no `autoResolve` rule covers (O1). | Rebase by hand, then queue again. |
| `hunk-outside-allowed-keys` | A `keys:` rule met a conflict on another line (O2). | Rebase by hand; drop `keys:` for a wholly generated file (O19). |
| `unsupported-conflict-type` | A modify/delete, rename, directory/file, binary, mode or submodule conflict. | Rebase by hand. |
| `conflict-without-stages` | git reported a conflict without a conflicted file, e.g. a directory rename. | Rebase by hand. |
| `unsupported-entry`, `too-large`, `not-json` | An allow-listed file's sides are not two or three regular 100644 files, a side is over 1 MiB, or (a `keys:` rule) not a JSON object. | Rebase by hand. |
| `workflow-missing` | `.github/workflows/<postRebaseActionName>` is not on the default branch. | Add it there. |
| `workflow-differs` | The workflow on the queue commit differs from the default branch's (D12). This happens for a base other than the default branch with an older copy. | Bring that branch's copy in line. |
| `dispatch-refused` | GitHub refused the dispatch: the workflow is not registered, it has no `workflow_dispatch` trigger, or an input is undeclared. | Fix the workflow (§2.3). |
| `dispatch-uncorrelated` | GitHub started the run without returning its id (D14). | Report it: the API changed. |
| `regenerate-missing`, `regenerate-wrong-commit` | The run disappeared, or built a different commit than the prepared one (D14). | Queue it again. |
| `regenerate-failed` | The run did not conclude `success`; the message links it. | Read its log; regenerate by hand if needed. |
| `regenerate-timeout` | It did not finish within `postRebaseTimeout`. | Raise the timeout, or speed the workflow up. |
| `regenerate-artifact` | `merge-queue-patch` is missing, expired, over 2 MiB, or not exactly one file. | Fix the upload step. |
| `patch-rejected` | The patch broke a T4 rule; the sub-reason and paths are shown (below). | Fix the generator, or `regeneratedPaths`. |
| `push-rejected` | GitHub refused a push to `mq/…` or to the pull request's branch, or the deletion of `mq/…`; GitHub's message is shown. | Usually a rule covering `mq/**` or the pull request's branch (§3), or Workflows not accepted. |
| `push-refused` | No credential to push with: the fork is gone or its box was unticked since enqueue, or there is no installation. | As the message says. |
| `fork-push-rejected` | GitHub refused the push to the fork's branch (S6). | Merge by hand (§3.3). |
| `writer-unavailable` | This server has no git writer (`MergeQueue:GitWriterUrl` unset); only case 1 can merge. | Operator: §7. |
| `checks-failed` | A counted check failed, after `retryFlaky` re-runs of the failed check runs. A commit status cannot be re-run. | Fix and queue again, or exclude a check that must not gate (§2.4). |
| `checks-timeout` | A counted check was still pending after `checks.timeout`, even after a last look. | Raise the timeout, or exclude a check that never finishes. |
| `role-revoked` | The person who queued it lost `write` before the merge (O9). | Someone with `write` queues it again. |
| `head-modified` | GitHub says the head is no longer the final commit, after one retry (D17). | Queue it again. |
| `merge-refused` | GitHub refused the merge; its message is shown. | Usually ruleset 2's review settings (§3.1). |
| `merge-timeout` | The merge request stayed pending for 40 polls (about 75 minutes). | Look at the pull request on GitHub; queue again. |
| `permission` | A call was refused for a permission; the check run and the comment link the acceptance page (O17). | Accept the new permissions. |
| `error` | The seventh transient failure since the entry last changed state: GitHub 5xx or rate limit, or the writer busy, failed or unreachable. | Operator: §7.4. |

**`patch-rejected` sub-reasons** (`CodeCoverage.GitWriter/Jobs/PatchValidator.cs`). These are checked before anything
is cloned: `too-large` (over 2 MiB or 500 files); `malformed` (not UTF-8, a NUL byte, text outside a file section, an
unknown header, hunk line counts that do not match their header); `outside-regenerated-paths` (either end of a rename
counts); `unsafe-path` (a path git would quote, whitespace, `..`, a `.git` or `.github` component, a `.gitmodules` or
`.gitattributes` file); `mode-change`, `symlink`, `submodule`, `binary`, `copy`, and `unsupported-mode` for anything
but 100644. After applying: `apply-failed` (git refused it) and `unexpected-change` (the applied tree changed a path
the patch did not declare).

**Ended without an eviction** (state Dequeued, neutral): the pull request was closed, or merged outside the queue, or
`/dequeue` or the label removal took it out.

## 6. Security model

The bot holds merge power that its triggers do not: a label, a comment or a button can be pressed by someone with less
power, or forged. It must never merge more than was approved or authorised (O10). In short (PRD §5.7):

| # | Threat | What stops it |
|---|---|---|
| T1 | A forged actor | The actor is the HMAC-verified webhook's `sender`, or the signed-in session's linked login. It is never comment text or a request body. The button is an antiforgery-guarded POST. Bots never command (D46). |
| T2 | Too little role | `write` at enqueue and again before the merge. Dequeue needs the author or `write`. |
| T3 | A swap after approval | The head SHA is pinned at enqueue, and PREPARE fetches exactly it. Any push whose SHA the queue did not write evicts. Every push is a lease, and the merge carries `sha`. The approval floor counts only approvals of the pinned commit. |
| T4 | Content smuggled in the patch | Strict parser before any clone (D30): only `regeneratedPaths`, 100644 text, no mode change, symlink, submodule, binary, copy, `.github`, `.gitattributes` or `.gitmodules`; ≤ 500 files, ≤ 2 MiB. The trees are compared after applying. The verify checks run on the final commit. |
| T5 | Secrets stolen through the dispatched workflow | **D12:** a dispatched run uses the workflow file of the dispatched `mq/` ref, not the default branch's (T5(a) was refuted live). So the queue evicts every pull request that touches `.github/**`, and asserts the workflow's blob on `mq/` equals the default branch's before dispatching. The main defence is §2.3: `contents: read`, no secrets, no `id-token`, GitHub-hosted runners, because the pull request's build code runs either way. A dispatch happens only after an authorised enqueue. |
| T6 | A pull request widening the configuration | `mergeQueue:` comes from the default branch only (O16); trust settings are in the database (O15). |
| T7 | The bot skipping the repository's rules | The App is exempt only from ruleset 1. Enqueue refuses any ruleset holding reviews or checks that the App can bypass (`current_user_can_bypass`, D16). |
| T8 | Hostile git content | Its own sidecar (D2), a bare blob-less clone, no checkout, worktree or hooks (an empty, read-only `core.hooksPath` and `--template=`). `attr.tree` is the empty tree, so no in-repo `.gitattributes` picks a merge driver (D21). `transfer.fsckObjects`, file/ext transports off, https only, the git floor (D29), job caps (D32). |
| T9 | A leaked token | The App's installation token, from the shared cache with at least 15 minutes left, is sent in the request body per job. git gets it only through `GIT_CONFIG_*` environment pairs and stderr is scrubbed. The writer holds no App key and nothing at rest. |

## 7. Operations

### 7.1 The git writer sidecar

`coverage-git-writer` (`CodeCoverage.GitWriter`, image `ghcr.io/mintplayer/codecoverage-git-writer:master`) is the only
container that writes git; the commit-graph mirror stays read-only (D2). Its API has no authentication, so the network
is the boundary. `POST /jobs/prepare` is PREPARE. `POST /jobs/publish` is PUBLISH: it applies the patch with
`git apply --cached` on a throwaway index. `DELETE /jobs/queue-branch` removes `mq/…` under a lease, and `GET /healthz`
answers `ok`. Every job starts from an empty directory, deleted when the job ends. Every answer is 200 with an outcome;
only a request that fails validation is a bare 400. The limits (`CodeCoverage.GitWriter/GitWriterOptions.cs`, compose):

| Limit | Value |
|---|---|
| Jobs at once | 1 (`GitWriter__MaxConcurrentJobs`, at most 2, refused otherwise at startup) |
| Memory, processes | `mem_limit: 768m`, `pids_limit: 128`: 512 MB and 64 for the job, the rest for the host (S8 peaked at 100 MB) |
| Disk per job | 1 GiB, measured every second (`too-large`); host floor 2 GiB free (`disk-floor`) |
| Wall clock per job | 180 s, then the git process tree is killed (`timeout`) |
| Waiting for a slot | 30 s, then `busy` (the saga retries) |
| File system | read-only, except the `codecoverage-git-writer-jobs` volume and a 64 MB `/tmp` |

Networks: `coverage-git-writer` is internal and holds exactly `coverage-app` and the writer. `coverage-git-writer-egress`
is the writer's way out, shared with nothing. The writer is never on the database's network, and
`tools/verify-backend-networks.test.mjs` asserts all of it. **Adding any container to `coverage-git-writer` lets it
have the writer push with the token the app hands over.** The image's Dockerfile fails the build when its git lacks the
July 2025 fixes (D29). Details: [`hosting.md`](hosting.md) §1, "Compose services".

### 7.2 Timeouts, polls and retries

| What | Bound | Set by |
|---|---|---|
| One writer request from the app | 240 s (the job's 180 s, a 30 s slot wait and a margin) | `MergeQueue:JobRequestTimeoutSeconds` |
| The regenerate run | `postRebaseTimeout` (20 m) from the dispatch. Polled every 3 min besides the `workflow_run` webhook. | `coverage.yml` |
| The checks wait | `checks.timeout` (60 m) from entering it, then one last look. Polled every 2 min besides the check webhooks. | `coverage.yml` |
| A failed check run | `checks.retryFlaky` re-runs (default 1): an Actions job is re-run, another App's check re-requested | `coverage.yml` |
| The merge request | Polls after 15 s, 30 s and 60 s, then every 2 min; after 40 polls `merge-timeout` | `MergeQueueEngine.MaxMergePolls` |
| "Head branch was modified" | One retry after 15 s: GitHub may not have registered the queue's own push yet (D17) | `HeadModifiedRetryDelay` |
| Base moves in one run | 5 restarts, then `base-churn` | `MaxPrepareAttempts` |
| A transient failure | The same step again after 15 s, 30 s, 1, 2, 4 and 8 min, then `error` | `MaxTransientFailures` (6) |

**Transient** means GitHub 5xx and rate limits (`ForgeRequestException`), a writer `busy` or `failed` outcome, transport
errors (including a writer 400) and timeouts. A refused permission (401/403) is never retried: it ends the entry with
`permission` and the O17 link. Anything else escapes to Spark Messaging's own retry: 5 attempts, 5 s to 1 h, then the
message is dead-lettered. That also applies to a transient failure while a command is being enqueued. A step meant for
another run or state is a no-op, so a redelivered message or a late poll changes nothing (D44).

### 7.3 App configuration

Section `MergeQueue` (`CodeCoverage/MergeQueue/GitWriterSidecarClient.cs`, `MergeQueueOptions`); only `GitWriterUrl` is
set in compose.

| Key | Default | Meaning |
|---|---|---|
| `MergeQueue:GitWriterUrl` | unset; compose: `http://coverage-git-writer:8080` | Unset turns the git steps off. Case 1 still merges; anything needing a rebase evicts `writer-unavailable`. |
| `MergeQueue:JobRequestTimeoutSeconds` | 240 | The bound on one writer request; keep it under the messaging claim of 5 min. |
| `MergeQueue:CommitterName`, `CommitterEmail` | `CodeCoverage merge queue`, `merge-queue@noreply.coverage.mintplayer.com` | The committer of every commit the queue writes. |
| `MergeQueue:AuthorEmailFormat` | `{login}@users.noreply.github.com` | The rebased commit's author email. Cosmetic: GitHub's squash sets the merged commit's author. |
| `MergeQueue:PatchArtifactName` | `merge-queue-patch` | Must match the action's artifact. |
| `MergeQueue:MaxPatchBytes` | 2 MiB | Keep it equal to the writer's `GitWriter:MaxPatchBytes`. |
| `Coverage:BaseUrl` | — | The check run's Details link (https only). |

The writer's `GitWriter:*` keys are in [`hosting.md`](hosting.md) §4, "Application configuration keys".

### 7.4 Where to look

- **App log** (`docker compose logs coverage-app`), lines starting `Merge queue:`. Entries end with
  `evicted #<n> in <repo>: <reason>` or `refused #<n> in <repo>: <code>`, and transient failures log
  `<Step> of <entry> failed (<n>/6); retrying in <delay>`. A failed check-run publish, a label removal or a comment is
  logged as a warning and does not stop the run.
- **Writer log** (`docker compose logs coverage-git-writer`): `<prepare|publish|…> for <repo>: busy`,
  `… finished after <ms> ms`, and `… failed (<reason>) after <ms> ms`.
- **RavenDB:** `MergeQueues/github/<repoId>` holds the lanes (per base, the current entry and the waiting ones).
  `MergeQueueEntries/github/<repoId>/<n>` is one entry per pull request: `State`, `Run`, `EvictionReason`,
  `EvictionMessage`, `History` (the last 50 steps), `PastRuns` (the last 10 outcomes), `TransientFailures`, `LastError`,
  `KnownShas`. `<repoId>` is GitHub's numeric id (`gh api repos/<owner>/<repo> --jq .id`). Dead-lettered steps:
  `from SparkMessages where QueueName = 'coverage-merge-queue' and Status = 'DeadLettered'`.
- **On GitHub:** the check run, the regenerate run it links, and any `mq/` branch left behind. Deleting a leftover `mq/`
  branch by hand is safe when no entry of that pull request is running.

**Stopping things.** `/dequeue`, or removing the label, ends one entry. `enabled: false` on the default branch stops new
entries; running ones finish under the configuration they were queued with. Removing `MergeQueue__GitWriterUrl` stops
every rebase on the server. A lane has a `Paused` flag that the engine honours, but nothing sets it yet.

## 8. Limits

Design choices, recorded so they can be revisited (D51):

- **Case 1 waits.** A head that already contains the latest base, with checks still pending, waits for them instead of
  being refused. Failed checks evict after D9's re-run.
- **The author may `/dequeue` without `write`**, because withdrawing is not a privilege. Anyone else needs `write`.
- **One lane for every repository.** All steps of all repositories share the `coverage-merge-queue` messaging lane, so a
  240 s writer job delays every other repository's steps. At current volume (a handful of pull requests a day) that is
  accepted, as is the serial queue per base without batching (D11).

Not verified, or not supported:

- **Fork pull requests are unverified (S6).** Whether GitHub counts an App installation token as a maintainer for
  "Allow edits by maintainers" was never measured. If not, every fork's PUBLISH ends with `fork-push-rejected`.
- **GitHub only (O7).** `IForgeMergeQueue` is forge-neutral; GitLab and Bitbucket follow under #422.
- **Squash only**, and no signed commits. The queue pushes unsigned bot commits with git (O14), and a branch that
  requires signatures is untested.
- **`.github/**` changes never go through the queue** (D12): they need the admin path of §3.3.
- Unmeasured with the production App so far: Commit statuses: read (D40); that an Actions check run's id is its job's id
  for re-runs (D48); the regenerate run's duration on CI (S4); the prepare step on the largest installed repository
  against the 180 s job clock (S8).

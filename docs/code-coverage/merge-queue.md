# CodeCoverage — the merge queue

A merge queue for GitHub repositories, run by CodeCoverage itself. The design and its decision log are in
[`../coverage_merge_queue_PRD.md`](../coverage_merge_queue_PRD.md), whose §0 table (O1–O22, D12–D64) supersedes its
body. This page describes what is built; where it and the PRD's body disagree, this page and §0 win.

**Status (2026-10-14):** built and tested piece by piece (PRD M2–M8), not yet deployed. No end-to-end run against a
live repository yet (M10). The repository page carries the queue's panel with its Queue and Dequeue buttons (M6, §4.1),
and `/merge-queue/setup` is the in-app setup guide (O13), which walks through §2 and §3 with screenshots.

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
PREPARE      git writer, with a token minted for this job alone (T9, D59): bare blob-less clone, fetch
             refs/pull/N/head, assert head == the SHA pinned at enqueue (T3)
             merge-tree base ⟵ head; a conflict only in autoResolve files ─► take the base side (O1, O19)
             any other conflict ─► EVICT; reports what the commit changes under .github/** against the base
             (D65); commit-tree ONE commit on the base, every date the run's (D65), crediting the other
             authors as Co-authored-by (D60). It pushes NOTHING (D65).
  ▼          nothing resolved, and postRebaseWhen is not always ─► case 2: PUBLISH without a patch
             (a .github/** change goes through here, a fork's too: nothing is dispatched, O21 A, O25)
REGENERATE   case 3: a .github/** change ─► EVICT touches-workflows, unless the bypass role (O22) lets a
             same-repository one through; a fork's ─► EVICT fork-touches-workflows, whatever the role (O25);
             a fork's pull request: the fork precondition (D65) or EVICT fork-unsafe-workflows; the writer
             rebuilds the prepared commit and lease-pushes it to mq/<base>/pr-<n>, the only push to a queue
             branch (D65), never a fork's .github/** change (it refuses that itself, D67); the workflow's blob on
             mq/ must equal the default branch's (D12); dispatch postRebaseActionName on mq/<base>/pr-<n>; a run
             of any other commit than the prepared one evicts (regenerate-wrong-commit, D14); it must succeed
             and upload merge-queue-patch. It never pushes (D4).
  ▼
PUBLISH      git writer: validate the patch (T4); rebuild the prepared commit, which must be the one PREPARE
             answered (D65); prepared tree + patch = ONE squashed commit; lease-push to the pull request's
             branch (a fork's through "Allow edits by maintainers", O3)
  ▼
CHECKS       every check run, commit status and check suite on that commit, minus exclusions (O12);
             a failed check run is re-run retryFlaky times (D9); the base moved ─► back to START
  ▼
MERGE        the person who queued it still has write (O9), the approval floor still holds unless their role
             still reaches the bypass role (D61, O22); the base is unchanged, else back to START; merge-async
             squash, sha = the final commit, merge_action = direct_merge, the co-authors as its message (D13, D60);
             poll until merged (D17)
  ▼
MERGED       label removed, the mq/ branch (only a dispatch pushed one) deleted under a lease, the lane's next
             pull request starts

Any step ─► EVICTED <reason> (§5): check run + comment, label removed, the mq/ branch deleted if one was pushed,
             a regenerate run it stopped waiting for cancelled (D53), the next one starts.
A push to the pull request that the queue did not make ─► EVICTED author-push, at any step (T3).
```

- **Three cases (O20).** Case 1 merges the head as it is, on its own checks. Case 2 (the base moved, the merge is clean)
  and case 3 (resolved and regenerated) publish one new commit, and CI runs again on it. A clean textual merge is never
  trusted on its own, because it misses semantic conflicts.
- **Why a queue branch (D5), and only for a dispatch (D65).** REGENERATE runs on `mq/<base>/pr-<n>`, not on the pull
  request. The bare rebase has stale hashes, so it is known to fail `--spark-verify-model`; keeping it off the pull
  request means the consumer's PR CI runs once, on the final commit. But a push to `mq/` is a push to a branch of the
  base repository, which starts its `push` workflows on the pushed code, with its secrets (D14, D19). So the queue
  pushes there only right before a dispatch, and for a fork's pull request only after the fork precondition (§2.3).
  PREPARE pushes nothing: its commit is a function of its request (every date is the run's), so the queue-branch push
  and PUBLISH rebuild exactly it and check it against the SHA PREPARE answered. The queue branch is deleted when the
  entry ends.
- **The queue knows its own pushes by SHA** (`KnownShas`, D46); any other new head on the pull request evicts it.

The code: the saga `CodeCoverage/MergeQueue/MergeQueueEngine.cs` (messaging lane `coverage-merge-queue`); the git writer
`CodeCoverage.GitWriter` with the resolver in `CodeCoverage.Git/MergeQueue/`; `IForgeMergeQueue` and `CheckGate`
(`CodeCoverage.Library/Forge/`), implemented by `GitHubForgeMergeQueue` (`CodeCoverage.GithubIntegration/Services/`).

## 2. Enabling it for a repository

### 2.1 The GitHub App: permissions and events

The full App reference is [`hosting.md`](hosting.md) §6, "GitHub App". What the queue needs:

| Permission | Level | The queue uses it for |
|---|---|---|
| Actions | **Read & write** (new) | Dispatching the regenerate workflow, reading its run, downloading `merge-queue-patch`, re-running a failed Actions job (D9), cancelling a run the queue stopped waiting for (D53). |
| Workflows | **Read & write** (new) | GitHub refuses an App push that brings workflow files the target repository has on no branch. Only the writer jobs that may do that ask for it (D59): a same-repository change to `.github/workflows/**` that goes through (O21 A), and a fork's publish whose commit changes `.github/workflows` against the fork branch's head (the base changed them since) or against the base (the fork's own change, O25, D67). |
| Commit statuses | Read-only (new) | The gate counts commit statuses as well as check runs (O12). Not yet measured with the App (D40). |
| Contents | Read & write | The writer's pushes, `merge-async`, `coverage.yml`, the workflow blob check (D12). |
| Pull requests | Read & write | The pull request itself, reviews (O11), its commits for the co-author trailers (D60), the label, the queue's comments. |
| Checks | Read & write | The "Merge queue" check run; reading check runs and suites; re-requesting another App's check. |
| Metadata | Read-only | Roles (`collaborators/{login}/permission`, O9) and rulesets with `current_user_can_bypass` (D16). |
| Issues | Read-only | The `issue_comment` event (`/queue`, `/dequeue`). |

**Administration is not requested.** Classic branch protection's required checks are readable without it; its review
settings are not. The queue then reports the branch as `ChecksOnly` and gives no O18 warnings from classic protection.

**The git writer never holds the installation's token (T9, D59).** Each writer job (prepare, publish, the queue
branch's reset and its deletion) gets a token minted for it alone: `POST /app/installations/{id}/access_tokens` with
this repository's id and `contents: write`, plus `workflows: write` only when that job's push may carry workflow files
into this repository: the queue-branch push of a change the bypass role let through, and a same-repository publish
whose own change touches `.github/workflows/**`. It is never cached and is revoked (`DELETE /installation/token`) when
the job answers or fails. A fork's publish uses a token scoped to the base repository too: that is the write access
"Allow edits by maintainers" honours (S6, verified live in D69). It never asks for `workflows`: GitHub evaluates that
permission against the fork, where the base's installation holds none, so it would change nothing there (D69, D71). A
mint GitHub refuses, typically a `workflows` permission the installation has not accepted, ends the entry `permission`
with the acceptance link.

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

![The notice on a CodeCoverage organization page: GitHub is waiting for you to accept new permissions, the pending permission, and the links Review and accept on GitHub and Merge queue setup guide](https://github.com/user-attachments/assets/813f0779-6f7f-464b-a46a-b30971ab57da)

The link opens the installation's page on GitHub, where the request is reviewed. The review page lists whatever the App
is requesting at the time (the one below is an example) above **Accept new permissions**:

![GitHub asking an organization owner to accept updated permissions for the CodeCoverage app: the requested repository permission, marked New request, and the Accept new permissions button](https://github.com/user-attachments/assets/558c6563-d0e3-43b9-abf3-2dd29914a7f3)

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
| `branches` | list of branch names | `[<default branch>]` | Not empty, no empty name, each one the git writer accepts (below). A pull request into any other branch is refused (`branch`). |
| `mergeMethod` | string | `squash` | `squash` only in v1. |
| `enqueue.label` | string | `merge-queue` | 1–50 characters. The queue does not create the label (`gh label create merge-queue`). |
| `postRebaseActionName` | file name | none | A workflow file name under `.github/workflows`, without a path: `^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\.ya?ml$`. |
| `postRebaseTimeout` | duration | `20m` | `90s`, `20m`, `1h`; a bare number is minutes and seconds round up; 1–360 minutes. |
| `postRebaseWhen` | `auto-resolved` · `always` · `never` | `auto-resolved` | Dispatch only after a resolved conflict, after every rebase, or never. |
| `regeneratedPaths` | list of globs | none | The only paths a regenerate patch may touch (T4). At most 64, no empty pattern. None is fine without a regenerate step. |
| `autoResolve[].path` | glob | — | Required. At most 64 entries. |
| `autoResolve[].strategy` | `take-base` | `take-base` | Anything else is an error: `json-union` was dropped (O1). |
| `autoResolve[].keys` | list of top-level JSON member names | none | None: a conflict on any line of the file resolves (O19). Given: only lines that hold one of these members do (O2), and every side must be a JSON object. At most 64 keys of 1–256 characters, no control character. |
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

**What parses, the git writer accepts (D57).** The parser holds every value that reaches the writer to the writer's own
rules (`MergeQueueWireRules`, `CodeCoverage.Git/MergeQueue/`): a glob is 1–256 characters, with no control character
or backslash, and does not start with `/`. A branch name holds only letters, digits, `.`, `_`, `/` and `-`. It does
not start with `-`, `/` or `.`, does not end with `/` or `.`, has no `..` or `//`, and no part starts with `.` or ends
in `.lock`. It must also be short enough for its queue branch `mq/<branch>/pr-<n>` to stay within 200 characters.
Anything else is a `configuration` error at enqueue. A pull request whose own branch name breaks the branch rule (GitHub
allows a `+`, for one) is evicted `branch-name` once it needs a rebase, because the writer could not publish to it. Case
1 still merges it.

### 2.3 The regenerate workflow (`postRebaseActionName`)

It runs only when `postRebaseActionName` is set, and either a conflict was resolved (`auto-resolved`) or always.
`MergeQueueWorkflowGuard.AfterPrepare` makes that decision, and only it, together with the `.github/**` guard (D63,
D67): a pull request whose change touches `.github/**` is never dispatched, unless the bypass role (§2.4) lets a
same-repository one through; a fork's never (`fork-touches-workflows`, O25). Such a change that needs no regeneration
goes through, a fork's too: nothing of it reaches your repository before the merge (D65). The git writer holds the fork
rule itself as well: its queue-branch push refuses a fork's commit whose `.github` differs from the base's, whatever
the request says (D67).

**The contract the queue holds the workflow to:**

- It exists at `.github/workflows/<postRebaseActionName>` on the **default branch**: a workflow GitHub has not
  registered there cannot be dispatched (D12). Its blob on the `mq/` commit must equal the default branch's, and no
  setting bypasses that.
- It declares `workflow_dispatch` with the inputs `expected_sha`, `pr_number` and `queue_run_id`. GitHub refuses a
  dispatch carrying an input the workflow does not declare.
- The run builds the prepared commit, concludes `success`, and uploads an artifact named `merge-queue-patch` holding
  exactly one file: the patch, at most 2 MiB, `git diff --binary --no-renames` text edits of regular files (D35).
  **An empty patch is uploaded too**: green + empty means "nothing to regenerate", while a failed run or a missing
  artifact evicts.
- It never commits or pushes (D4): a `GITHUB_TOKEN` push starts no CI, so no check would ever appear on the result.

**Runs the queue stops waiting for are cancelled (D53).** When the entry leaves the regenerate step other than by the
run's own completion (the base moved, an author push, a dequeue, a close, a merge elsewhere, the timeout, a refused
permission, or too many transient failures), the run is cancelled through the outbox, so it does not keep spending CI
minutes. Before each dispatch, any run of the workflow still going on the entry's own queue branch is cancelled too,
in case an earlier dispatch's answer was lost. A run that finished by itself is never cancelled.

**A fork's pull request is dispatched only after the fork precondition (D65).** The dispatch needs the fork's code on
`mq/<base>/pr-<n>`, a branch of your repository. Before it pushes there, the queue reads the workflows and refuses
(`fork-unsafe-workflows`, naming the file, the line and the fix) unless:

1. **no workflow of the pushed commit runs on that push.** These are the base branch's workflows, since the pushed
   commit's `.github/` is the base's (a fork's commit that changes it never gets this far: it is evicted
   `fork-touches-workflows`, O25, and the writer refuses its push, D67). A `push:` trigger must leave the branch out
   with `branches-ignore: ['mq/**']`, or a `branches:` list that does not match it (or ends with `'!mq/**'`), or name
   tags only. `paths:` filters do not count: the fork chooses the pushed paths. No `create:` trigger, which runs on
   every new branch.
2. **the regenerate workflow (the default branch's) is read-only:** `permissions:` at the top or in every job, granting
   at most `contents: read`; no `id-token`; no `secrets` anywhere (a `${{ secrets.* }}`, a `secrets:` key, an `if:`
   reading it); no `environment:`; no reusable workflow (`uses:` at job level); and only GitHub-hosted runner labels
   (`ubuntu-*`, `windows-*`, `macos-*`), no `self-hosted`, no runner group, no expression.
3. **no default-branch workflow follows its runs on that branch:** a `workflow_run:` trigger that names the regenerate
   workflow must have `branches-ignore: ['mq/**']` (or a `branches:` list that leaves `mq/` out).
4. **no workflow runs on `check_run`, `check_suite`, `status`, `deployment` or `deployment_status`** (O25, D67), at the
   base commit or on the default branch, whatever its `types:`. A check run or suite on the `mq/` commit (its push's
   own workflows, the regenerate run, another App's checks), a commit status on it, or a deployment of it (a preview
   service that deploys every pushed branch) starts such a workflow with your repository's secrets, and its jobs often
   check out the commit the event names: the fork's code. Whether a job skips `mq/` or never checks that commit out is
   not something the queue can verify, so while such a trigger exists, a fork's pull request that needs regeneration
   is regenerated by hand.

The guard is conservative: anything it cannot evaluate with certainty counts as unsafe, such as a file it cannot read
or parse, an unknown filter key, or a pattern character outside GitHub's filter cheat sheet. A same-repository pull
request needs none of this: its author has write access and their own push already ran those workflows. A fork's pull
request that needs no regeneration never pushes to your repository at all.

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

The approval floor, the check exclusions and the bypass role are set on CodeCoverage, not in the repository, so a pull
request cannot change them (O15). They are set on the **Account page** (group "Merge queue"), and a repository's detail
page can override them. Only the account's owner may change them, through the live forge ownership check. They are
readable like `DeleteBranchOnPrClose` (D49, D64).

![The Merge queue group on a CodeCoverage organization page at its defaults: Merge queue approval floor on Approval for external authors, no Checks the merge queue ignores, Merge queue bypass role on Nobody](https://github.com/user-attachments/assets/41dc6e47-3634-4d83-935d-1b20f48a08a7)

**Approval floor (O11, tightened by D47 and D61).** It is checked at enqueue and again right before the merge, like
O9's role. GitHub's ruleset 2 still enforces its own reviews at the merge.

| Value | Label | Needs before queueing |
|---|---|---|
| `ExternalAuthors` | Approval for external authors **(default)** | Nothing when the author has `write`; otherwise an approval. Bots such as Dependabot have no role, so they need one. |
| `RepositoryRules` | Repository rules only | Nothing of the queue's own; the repository's rules decide. A single-maintainer organization can choose this. |
| `Always` | Always require an approval | An approval for every pull request. |

A repository's floor is "Use account setting" (`Inherit`) or one of the three. **An approval counts only** when it is
an `APPROVED` review of the pinned commit (or of the commit the queue published from it), by a `write` user other than
the author, and still that reviewer's latest word. A later "changes requested" or a dismissal withdraws it; a comment
does not (D47). **A writer's request for changes vetoes** under both floors, approvals or not: while a `write` user's
latest review is "changes requested", the queue refuses (`approval`) at enqueue, and evicts (`approval-withdrawn`)
right before the merge (D61).

**Bypass role (O22, D64).** "Merge queue bypass role" names the lowest GitHub repository role whose holders bypass the
queue's own rules when they queue a pull request. It is the enqueuer's role, read from GitHub at enqueue, when a prepare
decides the dispatch, and right before the merge, like O9's role.

| Value | Label | Who bypasses |
|---|---|---|
| `None` | Nobody **(default)** | Nobody. |
| `Maintain` | Maintain or admin role | Whoever queues with the maintain or admin role. |
| `Admin` | Admin role only | Whoever queues with the admin role. |

A repository's value is "Use account setting" (`Inherit`) or one of the three, so a repository can also turn its
account's bypass off. Holding the role skips **the approval floor** (D61's veto included). It also lets **a
same-repository `.github/**` change through the regenerate dispatch**; a fork's never (§6, T5). If the role no longer
reaches it right before the merge, the floor applies again, and a `.github/**` change it let through the dispatch is
evicted `bypass-revoked`. **Never bypassed:** the check gate (O12), the regenerate workflow's blob identity (D12), the
patch validation (T4), the lease and SHA pinning (T3), and the enqueuer's own `write` minimum (O9). CodeCoverage's own
groups play no part: they are platform-wide, so they would cross tenants.

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
of the queue's branches. Its bypass actors are the App (mode `exempt`) and repository admins (mode `pull_request`, O21
B), so *Squash and merge* is blocked for everyone else, maintainers included, and admins can only merge through a pull
request, never push. **Ruleset 2** holds the reviews and required checks. **The App must not be a bypass actor of
ruleset 2** (O22). People may be, as your policy wants: a repository role, a team or a user. The recommended setup
below lists none, so admins too are held to the reviews and checks.

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
  "bypass_actors": [
    { "actor_id": <APP_ID>, "actor_type": "Integration", "bypass_mode": "exempt" },
    { "actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "pull_request" }
  ]
}
```

The second actor is the **Repository admin** role (O21 B). GitHub's REST reference for rulesets documents
`actor_type: RepositoryRole` and `bypass_mode: pull_request` ("an actor can only bypass rules on pull requests"), but
not the role ids. The ids `maintain` 2, `write` 4 and `admin` 5 are from the GitHub Terraform provider's ruleset
documentation (`integrations/terraform-provider-github`, `docs/resources/repository_ruleset.md`, checked 2026-10-14).
After creating the ruleset, check that `gh api repos/<owner>/<repo>/rulesets/<id> --jq .bypass_actors` lists the admin
role. Picking "Repository admin" in the UI's bypass list writes the same entry.

![Ruleset 1's bypass list on GitHub, the Repository admin role allowed for pull requests only and the CodeCoverage App exempt, above its target, the default branch](https://github.com/user-attachments/assets/1f80bbe3-b7c5-4ed8-a4e5-58d8e29b5dfd)

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

![Ruleset 2's Require status checks to pass rule on GitHub, with Require branches to be up to date before merging ticked and the repository's CI check required, not the Merge queue check](https://github.com/user-attachments/assets/8d8fbe19-6195-4325-9131-3698d0b1773c)

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
- **Approvals.** `required_approving_review_count` is what your team requires. **A solo maintainer must set it to
  `0`**, because GitHub never lets you approve your own pull request, so with 1 no merge of theirs could ever happen.
  Pair that with the `RepositoryRules` approval floor (§2.4), which adds no approval of the queue's own. The default
  `ExternalAuthors` floor also works: the maintainer's own pull requests pass it, and outside authors and bots (such
  as Dependabot) need the maintainer's approval. `Always` would refuse every pull request of the maintainer's own.
- **Required checks.** Require the job that runs `--spark-verify-model` and `--spark-verify-security`: it is what proves
  the regenerate step did its job (T4, O19). The queue waits for every check anyway (O12); requiring it makes GitHub
  enforce it too, and a required check can never be excluded. **Never require the "Merge queue" check itself**: it is
  still in progress while the queue merges, so GitHub would refuse every merge the queue requests. The queue refuses
  such a branch at enqueue (`self-required`, D62).
- `strict_required_status_checks_policy: true` costs nothing: the queue only merges a commit built on the base as it is
  at merge time, and goes back to PREPARE when the base moves.
- GitHub adds defaults on creation, among them `allowed_merge_methods` (keep `squash` in it) and
  `require_extra_approval_for_unattributed_changes: true` on ruleset 2. GitHub's ruleset page names that one
  "Require an additional approval for unattributed **Copilot** pull requests": it applies only when Copilot opens a pull
  request without a human collaborator, so it never touches the queue's regenerate push and can stay as it is (PRD O24).
- **The queue checks the App's bypass itself** at every enqueue, with its own token. It refuses (`bypass`) when a ruleset
  holding a `pull_request` or `required_status_checks` rule shows `current_user_can_bypass` other than `never`, or
  cannot be read (T7). That field is the App's own. People listed as bypass actors on ruleset 2 do not change it, so
  they never make the queue refuse (O22, D64).

### 3.1 Why "dismiss stale approvals" and "require approval of the most recent push" are off (O18)

GitHub dismisses an approval only when a push changes the pull request's *diff* (D18): a pure rebase or squash by the
queue keeps it, but the regenerate patch changes the diff. With `dismiss_stale_reviews_on_push` every model pull request
would lose its approval at PUBLISH, and with `require_last_push_approval` (the queue is always the last pusher) every
merge would need a human re-approval. The owner chose that the queue never pauses for one. It covers the gap itself: it
pins the head at enqueue and evicts on any push it did not make (T3), its own change is limited to the validated
`regeneratedPaths` (T4), and the verify checks run on the final commit. A repository that keeps either rule gets a
**warning** on the check run at enqueue, and then `merge-refused` with GitHub's rule-violation message.

![Ruleset 2's Require a pull request before merging rule on GitHub, with Dismiss stale pull request approvals and Require approval of the most recent reviewable push unticked](https://github.com/user-attachments/assets/d0bcd270-53c6-4841-b47d-8d5702da77f1)

### 3.2 Everything else

- **GitHub's native merge queue rule off (D8).** The two queues cannot share a branch; enqueue refuses `native-queue`
  when a ruleset requires it. The queue also passes `merge_action=direct_merge`, so it never ends up in GitHub's queue.

  ![A branch ruleset's rules on GitHub with Require merge queue unticked](https://github.com/user-attachments/assets/d11824a1-0040-4dd3-bf67-472bcaa84f62)
- **Squash merging allowed** (Settings → General → Pull Requests).

  ![The Pull Requests section of a repository's general settings on GitHub with Allow squash merging ticked](https://github.com/user-attachments/assets/f20b7b2d-bcff-4a93-8fa2-ea95545f13ad)
- **`branches-ignore: ['mq/**']`** on every `push:` trigger that would otherwise match the queue's branches (D14).
  Since D65 this is **enforced for forks**: a fork's pull request that needs the regenerate workflow is evicted
  `fork-unsafe-workflows` while any `push:` (or `create:`) trigger would start on its queue branch (§2.3). Pushes to
  `mq/` fire `push` workflows, never `pull_request`, and only a dispatch pushes there (D65). Their check runs land on
  the prepared commit, which *is* the final commit when the regenerate run changed nothing (D31, D35), so a push-only
  workflow there would gate the merge. A `push:` limited to `branches: [master]` needs nothing; this repository's
  workflows are all like that (D37). Such a run uses the pushed commit's own workflow files, which is why a fork's
  `.github/**` change never reaches `mq/` (O25, D67): it could delete that very filter.
- **Fork pull requests need "Allow edits by maintainers" ticked (O3).** The queue pushes the rebased commit to the
  fork's branch; there is no bot-made fork. GitHub offers the box only on forks owned by a user, not by an organization.
  The push uses a token scoped to the base repository (D59), whose write access is what the box honours; GitHub accepts
  it (S6, verified live in D69).

  ![The form that opens a pull request from a fork on GitHub, with Allow edits by maintainers ticked beside the Create pull request button](https://github.com/user-attachments/assets/c35a720c-0704-4cd0-89c0-c8d9c41b27b9)
- **A fork's branch must already hold the base's workflow files (D69).** GitHub never lets an App create or update a
  workflow file that the fork has on no branch, whatever permission its token carries. When the base changed its
  workflows since the fork branched, the publish is refused and the entry ends `fork-workflow-update-needed`. Someone
  clicks **Update branch** on the pull request (or merges the base into the branch), which brings those files in as a
  person's push, and queues it again.
- **Private repositories on GitHub Free have no rulesets.** GitHub answers 403 "Upgrade to GitHub Pro…", which the
  queue reads as "no rules" (D15). The queue still waits for every check (O12), but nothing forces merges through it.

### 3.3 Merging what the queue will not

Ruleset 1 blocks hand merges for everyone but admins, who bypass it through a pull request (O21 B). A pull request the
queue refuses by design is merged by an admin clicking **Merge** on it; nobody disables a ruleset. Ruleset 2 still
holds the admin to its reviews and checks, unless your policy listed them as its bypass actors (O22). The cases:

- a change under `.github/**` that needs the regenerate workflow (`touches-workflows`), queued without the bypass role
  (§2.4). A workflow-only change, such as Dependabot's GitHub Actions updates, needs no regeneration and goes through the
  queue (O21 A), a fork's too (O25);
- a fork's change under `.github/**` that needs the regenerate workflow (`fork-touches-workflows`), whatever the role
  (O25). Rebasing it onto the latest base and regenerating by hand also works: a head that contains the latest base is
  merged as it is (O20 case 1);
- a fork's pull request that needs the regenerate workflow while the repository's workflows could run its code on the
  queue branch (`fork-unsafe-workflows`, D65). Better: fix the workflows the message names, then queue it again;
- a pull request from an organization-owned fork (`fork`), and a fork push GitHub refused (`fork-push-rejected`).

A fork whose branch lacks the base's workflow files (`fork-workflow-update-needed`, D69) needs no admin: **Update
branch** on the pull request, then queue it again.

The admin's merge is GitHub's own: nothing is rebased or regenerated, and the queue's own checks (the approval floor,
the check gate) do not run.

## 4. Using it

### 4.1 Queueing and dequeueing

| Way | Queue | Dequeue |
|---|---|---|
| Label | Add the `enqueue.label` label (`merge-queue`). | Remove it. |
| Comment | `/queue` as the first word of a new comment on the pull request. | `/dequeue`, the same way. |
| Button | **Queue** in the merge-queue panel of the CodeCoverage repository page (O8, M6; `POST /api/repos/{provider}/{owner}/{name}/merge-queue/{number}`): the user's linked GitHub login acts; without one, refused. | **Dequeue** in the same panel (`DELETE` on the same route), offered to the author and to `write`. |

- The actor is always the signed webhook's sender, or the signed-in user's linked login (T1). Text after the command is
  ignored ("/queue as @maintainer" is the commenter's command), and edited comments do not count. Every path then runs
  the same checks.
- **A bot never commands the queue** (D46): its label or comment is dropped, which also makes the queue ignore its own
  label removal.
- A second `/queue`, or the label re-applied, while queued changes nothing. A refused label is removed again and the
  check run says why; a refused comment or button gets a comment.

The panel shows each base branch's lane: the pull request the queue is working on and the waiting ones, then the latest
outcomes and the configuration the run read (D55).

![The merge-queue panel of a CodeCoverage repository page: the main branch's lane with the pull request being published and two waiting ones, each with Dequeue, the latest outcomes, the effective configuration and the Queue button](https://github.com/user-attachments/assets/d8ddd90f-209c-4079-b95f-f06ffc245552)

### 4.2 Who may

- **Queue:** a GitHub login with at least the `write` role on the repository (O9). The role is checked at enqueue and
  again right before the merge; a lost role evicts (`role-revoked`). The pull request must also be open, not a draft,
  into a branch listed in `mergeQueue.branches`, and meet the approval floor (§2.4), which is checked again right
  before the merge too (`approval-withdrawn`, D61). A login whose role reaches the bypass role skips the floor (O22);
  so does that check before the merge, as long as the role still reaches it.
- **Dequeue:** the pull request's author (withdrawing is not a privilege), or anyone with `write`. Anyone else is
  ignored; the attempt is noted in the entry's history.

### 4.3 What happens to the pull request

- **The queue rewrites the branch.** In cases 2 and 3 the pull request's branch is replaced, under a lease on the queued
  head, by **one** commit on the latest base. That commit carries the net change plus the regenerated files. Its author is
  the pull request author's login with `{login}@users.noreply.github.com`, its committer is `CodeCoverage merge queue`,
  and its message is `<title> (#<n>)` (D50) followed by the co-author trailers below. After an eviction past PUBLISH,
  the branch holds that commit: reset to it before pushing more.
- **Co-authors are credited (D60).** Every other author of the pull request's commits, and everyone a commit credits in
  a `Co-authored-by: Name <email>` line of its message, becomes a `Co-authored-by:` trailer: once per email, in commit
  order, at most 100. Not credited: the pull request's author (by account, by their commits' emails, and by GitHub's
  noreply forms of the login), bots (`[bot]`), the queue itself, and identities git would refuse. The trailers are read
  once when the run starts, from GitHub's list of the pull request's commits (at most 250 commits), and go both into
  the rebased commit and into the merge's `commit_message`, from which GitHub builds the merged commit. Because the
  rebased commit carries them, a pull request queued again after an eviction past PUBLISH keeps its credits. A pull
  request with one author gets the title alone.
- **Pushing while queued evicts** (`author-push`). The queue merges only the commit that was queued; queue it again to
  merge the new head.
- **The merge** is GitHub's squash merge with the title `<title> (#<n>)` and the co-author trailers as its message,
  performed by the App.
- **At the end**, whatever the outcome: the label is removed, the `mq/` branch (if a dispatch pushed one, D65) is
  deleted under a lease, a regenerate
  run the entry no longer waits for is cancelled (D53), and the next pull request in the lane starts. An eviction posts
  a comment with the reason and how to queue again; a merge posts nothing.

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
**Effective configuration** (O15): every `mergeQueue:` value plus `approvalFloor`, `checkExclusions` and `bypassRole`,
each with its source (`coverage.yml @ <default branch>`, `default`, `organization` or `repository`).

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
| `approval` | The approval floor is not met on the queued commit (O11, D47), or a writer's latest review requests changes (D61). | A writer other than the author approves the current head, or the one who asked for changes approves; queue again. |
| `native-queue` | The base requires GitHub's own merge queue (D8). | Remove that rule. |
| `self-required` | The base requires the queue's own "Merge queue" check, which is still in progress while the queue merges (D62). | Remove it from the required checks (§3). |
| `bypass` | The App can bypass a ruleset with reviews or checks, or its bypass could not be read (T7). People listed as bypass actors never cause this (O22). | Remove the App from that ruleset's bypass list (§3). |
| `permission` | GitHub refused a call for a permission. | Accept the new permissions (linked, O17). |

**Evicted during a run** (state Evicted, "Removed from the queue: *code*", with a comment):

| Code | Meaning | What to do |
|---|---|---|
| `author-push` | The branch moved to a commit the queue did not write, at any step (T3). | Queue it again; the new head is what gets merged. |
| `retargeted` | The pull request's base was changed. | Queue it again. |
| `base-missing` | The base branch no longer exists. | — |
| `base-churn` | The base moved more than 5 times during one run. | Queue it again when the base is quieter. |
| `touches-workflows` | A pull request from this repository changes `.github/**` against its base (its own change, or one a rename on the base carries there, D65) and needs the regenerate workflow, queued without the bypass role (O21 A, O22). Without a dispatch such a change goes through. | An admin merges it (§3.3), or someone with the bypass role queues it. |
| `fork-touches-workflows` | A fork's pull request changes `.github/**` against its base (its own change, or one a rename on the base carries there) and needs the regenerate workflow: the queue-branch push would run the fork's workflow files with this repository's secrets (O25). Nothing was pushed, and no role bypasses it. The git writer refuses that push itself as well; when it is the writer that refused, the message says so (D67). Without a dispatch such a change goes through. | Rebase it onto the latest base and regenerate by hand (a head that contains the latest base is merged as it is, O20 case 1), or an admin merges it (§3.3). |
| `fork-unsafe-workflows` | A fork's pull request needs the regenerate workflow, and a workflow could run its code on the queue branch with this repository's secrets: a `push:` or `create:` trigger, a regenerate workflow that grants more than `contents: read`, reads a secret or may run self-hosted, a `workflow_run:` that follows it there, or a `check_run:`, `check_suite:`, `status:`, `deployment:` or `deployment_status:` trigger (D65, O25, §2.3). Nothing was pushed. | Fix the workflows the message names (usually `branches-ignore: ['mq/**']`; a check, status or deployment trigger has to go), then queue it again, or regenerate by hand. |
| `prepared-differs` | The git writer rebuilt the prepared commit for the queue-branch push or PUBLISH and got another one (D65): the writer's git, or the repository's history under the pinned commits, changed since PREPARE. | Queue it again. |
| `branch-name` | It needs a rebase, and its branch name is one the git writer does not push to (D57). | Rename the branch, or rebase by hand. |
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
| `push-rejected` | GitHub refused the push to `mq/…` before a dispatch or to the pull request's branch, or the deletion of `mq/…`; GitHub's message is shown. | Usually a rule covering `mq/**` or the pull request's branch (§3). |
| `push-refused` | No credential to push with: the fork is gone or its box was unticked since enqueue, or there is no installation. | As the message says. |
| `fork-workflow-update-needed` | GitHub refused to write workflow files into the fork's branch: an App may not create or update a workflow file the fork has on no branch, whatever its permissions, and the base changed its workflows since the fork branched (D69). | Click **Update branch** on the pull request (or merge the base into the branch), then queue it again. |
| `fork-push-rejected` | GitHub refused the push to the fork's branch for another reason; GitHub's message is shown. | Act on GitHub's message; an admin can merge it (§3.3). |
| `writer-unavailable` | This server has no git writer (`MergeQueue:GitWriterUrl` unset); only case 1 can merge. | Operator: §7. |
| `writer-refused` | The git writer refused the queue's own request: the field and the rule it broke are shown (D57). The parser and the enqueue checks hold everything to the same rules, so this points at a defect. | Report it with the message. |
| `checks-failed` | A counted check failed, after `retryFlaky` re-runs of the failed check runs. A commit status cannot be re-run. | Fix and queue again, or exclude a check that must not gate (§2.4). |
| `checks-timeout` | A counted check was still pending after `checks.timeout`, even after a last look. | Raise the timeout, or exclude a check that never finishes. |
| `role-revoked` | The person who queued it lost `write` before the merge (O9). | Someone with `write` queues it again. |
| `approval-withdrawn` | Right before the merge, the approval floor no longer held: an approval was dismissed or withdrawn, or a writer requested changes (D61). | Get the approval back; queue again. |
| `bypass-revoked` | The bypass role let a `.github/**` change through the regenerate workflow, and the person who queued it no longer has that role (O22). | Queue it again; it is evicted `touches-workflows` unless the queuer has the role. |
| `head-modified` | GitHub says the head is no longer the final commit, after one retry (D17). | Queue it again. |
| `merge-refused` | GitHub refused the merge; its message is shown. | Usually ruleset 2's review settings (§3.1). |
| `merge-timeout` | The merge request stayed pending for 40 polls (about 75 minutes). | Look at the pull request on GitHub; queue again. |
| `permission` | A call was refused for a permission; the check run and the comment link the acceptance page (O17). | Accept the new permissions. |
| `error` | The seventh transient failure in a row since the entry last changed state (the first and 6 retries, D58): GitHub 5xx or rate limit, or the writer busy, failed or unreachable. | Operator: §7.4. |

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
| T1 | A forged actor | The actor is the HMAC-verified webhook's `sender`, or the signed-in session's linked login. It is never comment text or a request body. The buttons are an antiforgery-guarded POST (queue) and DELETE (dequeue). Bots never command (D46). |
| T2 | Too little role | `write` at enqueue and again before the merge. Dequeue needs the author or `write`. The bypass role (O22) is likewise read when it decides and again before the merge. |
| T3 | A swap after approval | The head SHA is pinned at enqueue, and PREPARE fetches exactly it. Any push whose SHA the queue did not write evicts. Every push is a lease, and the merge carries `sha`. The approval floor counts only approvals of the pinned commit (or the queue's own commit from it), and is checked again right before the merge, where a writer's request for changes vetoes (D61). |
| T4 | Content smuggled in the patch | Strict parser before any clone (D30): only `regeneratedPaths`, 100644 text, no mode change, symlink, submodule, binary, copy, `.github`, `.gitattributes` or `.gitmodules`; ≤ 500 files, ≤ 2 MiB. The trees are compared after applying. The verify checks run on the final commit. |
| T5 | Secrets stolen through a workflow the pull request changed, or through the base's own workflows running a fork's code | **D12:** a dispatched run uses the workflow file of the dispatched `mq/` ref, not the default branch's (T5(a) was refuted live), and a push to `mq/` starts `push` workflows from the pushed commit's own files, on that commit, with the repository's secrets. So the queue pushes to `mq/` only right before a dispatch (D65), never at PREPARE. A fork's commit that changes `.github/**` against its base never reaches `mq/`: it is evicted `fork-touches-workflows` before a dispatch, whatever the role (O25), and the git writer refuses its queue-branch push itself (D67); D65 reads the merged tree, so a rename on the base cannot carry a fork's edit into a workflow unseen. Without a dispatch it goes to the fork's own branch only, as it would without the queue (O25). A fork's commit is pushed to `mq/` only after the fork precondition (§2.3, D65): no `push`/`create` trigger runs on that branch, the regenerate workflow is read-only, no `workflow_run` follows it there, and no workflow runs on the commit's checks, statuses or deployments (O25). A same-repository change, whose author's own push already ran those workflows, goes through only when nothing is dispatched, or through the bypass role (O21 A, O22). Before every dispatch the workflow's blob on `mq/` must equal the default branch's, and nothing bypasses that. The main defence stays §2.3: `contents: read`, no secrets, no `id-token`, GitHub-hosted runners, because the pull request's build code runs either way. A dispatch happens only after an authorised enqueue. |
| T6 | A pull request widening the configuration | `mergeQueue:` comes from the default branch only (O16); trust settings are in the database (O15). |
| T7 | The bot skipping the repository's rules | The App is exempt only from ruleset 1. Enqueue refuses any ruleset holding reviews or checks that the App itself can bypass (`current_user_can_bypass` read with its own token, D16). People may bypass ruleset 2 (O22); that is the repository's policy, not the bot's. |
| T8 | Hostile git content | Its own sidecar (D2), a bare blob-less clone, no checkout, worktree or hooks (an empty, read-only `core.hooksPath` and `--template=`). `attr.tree` is the empty tree, so no in-repo `.gitattributes` picks a merge driver (D21). `transfer.fsckObjects`, file/ext transports off, https only, the git floor (D29), job caps (D32). |
| T9 | A leaked token | Each writer job gets a token minted for it alone (D59): this one repository, `contents: write`, plus `workflows: write` only when its push needs it, and never for a fork's branch, where GitHub ignores it (D69). It is never cached, and is revoked when the job ends. It travels in the request body; git gets it only through `GIT_CONFIG_*` environment pairs, and stderr is scrubbed. The writer holds no App key and nothing at rest. |

## 7. Operations

### 7.1 The git writer sidecar

`coverage-git-writer` (`CodeCoverage.GitWriter`, image `ghcr.io/mintplayer/codecoverage-git-writer:master`) is the only
container that writes git; the commit-graph mirror stays read-only (D2). Its API has no authentication, so the network
is the boundary. `POST /jobs/prepare` is PREPARE; it pushes nothing and answers the commit's id, which is a function of
the request: every author and committer date is the run's, and a request without one is refused (D65).
`POST /jobs/queue-branch` rebuilds that commit, answers `prepared-differs` unless it is the one PREPARE answered, and
pushes it to `mq/…` under a lease; the saga calls it only before a dispatch. Its request must say whether the head is a
fork's (`headFromFork`), and it answers `fork-touches-workflows` without pushing for a fork's commit whose `.github`
differs from the base's, whatever else the request says (O25, D67). PREPARE and PUBLISH refuse no `.github/**` change:
the one pushes nothing, the other pushes to the pull request's own branch. `POST /jobs/publish` is PUBLISH: it
rebuilds and checks the prepared commit the same way (it never reads `mq/`), then applies the patch with
`git apply --cached` on a throwaway index. A retried push meets its own commit on the branch and answers
`already-pushed` (D52). `DELETE /jobs/queue-branch` removes `mq/…` under a lease, and `GET /healthz` answers `ok`. Every job starts from an empty directory, deleted when the job ends. Every answer is 200 with an outcome;
only a request that fails validation is a 400, whose body `{ "outcome": "refused", "field", "detail" }` names the
first field and the rule it broke, never a value (D57). The limits (`CodeCoverage.GitWriter/GitWriterOptions.cs`, compose):

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
have the writer push with the token the app hands over** (one job's token, for one repository, D59). The image's
Dockerfile fails the build when its git lacks the
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
| A transient failure | The same step again after 15 s, 30 s, 1, 2, 4 and 8 min; the seventh failure in a row is `error` | `MaxTransientRetries` (6) |
| A writer job's token | Minted for the job, revoked when it answers or fails; a revocation waits at most 10 s | `GitHubForgeMergeQueue` (D59) |

**Transient** means GitHub 5xx and rate limits (`ForgeRequestException`), a writer `busy` or `failed` outcome, transport
errors and timeouts. A refused permission (401/403) is never retried: it ends the entry with `permission` and the O17
link. Neither is a request the writer refuses (a 400): the same request would be refused again, so it ends the entry
with `writer-refused` and the writer's reason (D57). Anything else escapes to Spark Messaging's own retry: 5 attempts, 5 s to 1 h, then the
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
  `<Step> of <entry> failed (<n>/7); retrying in <delay>`. A failed check-run publish, a label removal, a comment or a
  token revocation is logged as a warning and does not stop the run.
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

- **A fork's branch must already hold the base's workflow files (D69).** The App's push to a fork works through "Allow
  edits by maintainers" (S6, measured live), but GitHub never lets an App create or update a workflow file the fork has
  on no branch, whatever permission its token carries. The queue cannot bring such files into a fork, so the publish is
  refused and the entry ends `fork-workflow-update-needed`, until a person clicks **Update branch** (or merges the base).
  There is no pre-check: a file the fork has on another branch is accepted, so the attempt is the only reliable test.
- **GitHub only (O7).** `IForgeMergeQueue` is forge-neutral; GitLab and Bitbucket follow under #422.
- **Squash only**, and no signed commits. The queue pushes unsigned bot commits with git (O14), and a branch that
  requires signatures is untested.
- **A `.github/**` change goes through only when nothing is dispatched**, a fork's too, or with the bypass role for a
  same-repository pull request (O21 A, O22, O25). A fork's with a dispatch never does (O25). The rest needs the admin
  path of §3.3.
- **What the fork precondition cannot see (D65).** A self-hosted runner registered under a GitHub-hosted label's name,
  and a service outside GitHub Actions that builds every pushed branch with secrets of its own: neither shows in the
  repository's workflows. Workflows on `check_run`, `check_suite`, `status`, `deployment` and `deployment_status` are
  read since O25, and refused whether or not their jobs filter `mq/`, which the queue cannot verify (D67).
- **Co-authors beyond GitHub's 250-commit listing** of a pull request are not credited (D60).
- Unmeasured with the production App so far: Commit statuses: read (D40); that an Actions check run's id is its job's id
  for re-runs (D48); that the App's `workflows` permission follows the OAuth scope's "same file on another branch" rule
  for a same-repository push (D59; for a fork's branch the rule held without the permission, D69); the
  regenerate run's duration on CI (S4); the prepare step on the largest installed repository against the 180 s job
  clock (S8).

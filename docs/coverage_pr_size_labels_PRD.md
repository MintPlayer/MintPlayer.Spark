# Pull-request size labels — the GitHub App labels every PR by the lines it changes

**Status:** Draft, grilled 2026-10-23 (Q1–Q4 answered), spikes not started. See [coverage_pr_size_labels_plan.md](coverage_pr_size_labels_plan.md)
**Issue:** #527 · **App:** `apps/CodeCoverage` (production, coverage.mintplayer.com) · one pull request, which also
moves "Set up coverage uploads" from the Repository page into a Guides page (D10)
**Prior art:** `C:\Repos\ProjectDashboard\ProjectDashboard\Service\Recipients\PullRequestSizeLabeler.cs`. That repository
is private, so the parts that matter are quoted below.

## The problem, stated plainly

PR size labels used to come from a GitHub Action and later moved into the ProjectDashboard GitHub App. That implementation
is a fixed table in code:

- **Every setting is a `static readonly`.** That covers six thresholds, the label names, the colours and the ignore
  rules (`PullRequestSizeLabeler.cs:14-36`). Changing a threshold means a deploy.
- **It never creates a label.** It looks up the repository's existing `size/*` labels and indexes the dictionary with
  the computed name (`:85-87`, `:100`). If someone deletes `size/M`, or a repository never had it, the lookup throws
  `KeyNotFoundException` and the PR gets an error status. The labels had to exist beforehand, which the old GitHub Action
  originally took care of.
- **No account can switch it off,** and it cannot be turned off for one repository either.

#527 asks for the same behaviour inside the CodeCoverage GitHub App, with three changes:

1. it can be switched on or off **per Account**
2. the size scale can be configured per Account: thresholds, and labels added or removed
3. **the app owns the label definitions:** it creates, updates and deletes them

## Where the old scheme came from (research)

No public Action uses exactly this scheme (0/30/100/300/600/1200 with these colours). The wording "Denotes a PR that
changes N-M lines" is Kubernetes/prow's. Prow's buckets are 0/10/30/100/500/1000.

| Action | Buckets | Useful options |
|---|---|---|
| [pascalgn/size-label-action](https://github.com/pascalgn/size-label-action) | XS–XXL, the prow buckets | `sizes` JSON, `IGNORED` globs with `!` to re-include, `pull-requests: write` |
| [CodelyTV/pr-size-labeler](https://github.com/CodelyTV/pr-size-labeler) | XS–XL, 10/100/500/1000 | `files_to_ignore`, `ignore_line_deletions`, `ignore_file_deletions`, `fail_if_xl`, `message_if_xl` |
| [cbrgm/pr-size-labeler-action](https://github.com/cbrgm/pr-size-labeler-action) | no built-in defaults; its example is xs 25 / s 150 / m 600 / l 2500 / xl 5000 | `.github/pull-request-size.yml` with `exclude_files`, `label_configs` (a `diff` **and** a `files` threshold per size, several `labels` per size), `added_lines_only` |

The owner isn't sure which Action it was: pascalgn's, cbrgm's or another one. *"It doesn't really matter that much …
we can grab the defaults from one of these actions"* (owner, 2026-10-23). The reference's colours are Kubernetes' own
`size/*` colours, which pascalgn's setup script copies.

We take configurable thresholds, names and colours, ignore globs with `!` to re-include, and **pascalgn's defaults**
(D2). We don't take failing a check or commenting on oversized PRs, cbrgm's file-count threshold, or several labels per
size (see Out of scope).

## What exists to build on

All verified against `master` @ `9d818517`. Paths are under `apps/CodeCoverage/`.

| Need | Already there |
|---|---|
| PR opened/updated event | `GitHubEventsRecipient.OnPullRequest` (`CodeCoverage.GithubIntegration/Recipients/GitHubEventsRecipient.cs:417`) turns `opened\|synchronize\|reopened\|ready_for_review` (and `edited` when the base changed) into `ForgeWebhookMessage<PullRequestUpdated>` (`:452-474`) on queue `spark-forge-all`. Its consumer today is `ForgeEventsRecipient.HandleAsync(PullRequestUpdated)` (`CodeCoverage/Recipients/ForgeEventsRecipient.cs:63`). |
| Installation client | `IGitHubInstallationService.CreateInstallationClientAsync` / `CreateGraphQLConnectionAsync`, with the installation resolved from the repository's Account (`GitHubForgeClient.cs:250`). |
| Add/remove a PR label | `GitHubForgeMergeQueue.AddLabelAsync`/`RemoveLabelAsync` (`:978-1005`), declared on `IForgeMergeQueue` (`:195,198`). Nothing creates, updates or deletes **label definitions**, and nothing lists PR files. |
| App permissions | Issues **R&W** and Pull requests **R&W** are already granted (`docs/code-coverage/hosting.md:401,404`). GitHub accepts either one for every label endpoint, so **no permission change is needed and no installation has to re-approve.** |
| Account default + repository override | `Account.DeleteBranchOnPrClose` (bool) with `Repository.DeleteBranchOnPrClose : EDeleteBranchPolicy { Inherit, Enabled, Disabled }` (`CodeCoverage.Library/LookupReferences/DeleteBranchPolicy.cs:20-30`), resolved by `Repository.ResolveDeleteBranchOnPrClose` (`Repository.cs:200-206`). [coverage_branch_deletion_setting_PRD.md](coverage_branch_deletion_setting_PRD.md) explains why this is an enum and not a `bool?`. |
| Collection override | `MergeQueueCheckExclusionsPolicy` plus the repository's own list (`Repository.cs:220-236`). |
| Editable rows on Account | `List<DependencyRule>` (`Account.cs:190-191`), a `[ValueObject]` with `[ValueKey] Id` (`DependencyRule.cs:22-27`), rendered `AsDetail`, `editMode: inline` (`App_Data/Model/Account.json:134-148`). |
| React to a settings change | `AccountActions` `IBeforeSave` + `IAfterSaveCommitted` already fan an account default out to inheriting repositories (`CodeCoverage/Actions/AccountActions.cs:46-64`). |
| Who may edit | Managers only. Writes are row-filtered (`AccountActions.cs:75-86`), and settings are redacted **deny-by-default** through `OwnerSettings.ProtectedAccountAttributes` (`Services/OwnerSettings.cs:41-57`), so new attributes are protected automatically. |
| Path globs | `CodeCoverage.Git/PathGlob.cs`: `**` spans directories, case-sensitive like git. |
| Forge abstraction | `IForgeIntegration` with `EForgeCapability` flags (`CodeCoverage.Library/Forge/IForgeIntegration.cs:38,52`). Unsupported members throw `NotSupportedException`. |

## Decisions

The owner left the per-repository question to me ("I'll let you decide"). The open questions Q1–Q4 were grilled with
the owner on 2026-10-23, and their answers are folded into D1, D4, D5, D7 and D10 below. Each decision gives its
reason.

### D1 — Account default, with a per-repository on/off override. The scale stays per Account.

- `Account.PrSizeLabels` is a `bool` and defaults to **false**.
- `Repository.PrSizeLabels` is an `EPrSizeLabelPolicy { Inherit, Enabled, Disabled }`.

This copies `DeleteBranchOnPrClose`, including the enum instead of a `bool?` (the lookup key is a string on the wire,
`LookupReferenceService.cs:282`). The real cases are "on for my org, except the docs repo" and "off for my org, but try
it on one repo", and an on/off override covers both.

**The size scale gets no per-repository override in v1.** An override would need a second editable `AsDetail` list on
Repository, a policy enum, and per-repository label state that diverges from the account's. That is the
`MergeQueueCheckExclusions` shape, and it is affordable. Still, no consumer has asked for a different scale per
repository, and the owner wants a lean API (`feedback_minimal_pr_diff`). **Ignore rules are the exception.** They name
repository paths, so a repository may add its own rules (D5, Q1).

**Default off.** Turning it on writes labels into *every* repository of the account. Consent is required for that, so
the app must not do it silently on deploy.

### D2 — The scale is an editable list of rows on Account

`[ValueObject] PrSizeLabel { [ValueKey] Id, Name, MinLines, Color, Description }` is held in
`List<PrSizeLabel> PrSizeLabelScale`, edited inline like `DependencyRules`.

**The defaults are pascalgn/size-label-action's** (owner, 2026-10-23: "grab the defaults from one of these actions").
pascalgn's is the only candidate with complete built-in defaults, since cbrgm only shows an example. The six rows,
ascending, are `size/XS` 0 `009900`, `size/S` 10 `77bb00`, `size/M` 30 `eebb00`, `size/L` 100 `ee9900`, `size/XL` 500
`ee5500` and `size/XXL` 1000 `ee0000`. Those are the prow buckets with Kubernetes' colours.

The ProjectDashboard reference used 0/30/100/300/600/1200 with the same names and colours. An account that wants that
scale edits five numbers.

- Labels can be added and removed by adding and removing rows, as the issue asks.
- **An empty description is generated** ("Denotes a PR that changes 100-299 lines") from the row and the next one, so
  it stays correct when the thresholds move.
- **Validation in `BeforeSave`** keeps bad data away from GitHub:
  - names are unique and case-insensitive, because GitHub label names are
  - `MinLines` values are unique, and the smallest is `0` so every PR gets a label
  - `Color` is 6 hex digits without `#` (GitHub's format)
  - the description is ≤ 100 characters (GitHub's documented limit)
  - the name length limit is undocumented, so spike **S1** measures it
- Enabling the feature with an empty scale is refused, not silently ignored.

### D3 — The app owns its labels, tracked by row id, so a rename is a rename

Labels are reconciled per repository from the effective scale. The app has to remember **which labels are its own**:

- It must never delete a user's `size/whatever` label.
- Renaming row `size/M` → `M` must **PATCH with `new_name`**. That keeps the label on every PR that already carries it.
  Delete-and-create would strip it from those PRs (to be confirmed in **S1**).

`Repository.PrSizeLabelState : List<ManagedLabel { RowId, Name, Color, Description }>` records what was last written
to GitHub for each row. Reconciliation then has four cases:

| Scale row vs state | Repository | Action |
|---|---|---|
| row, no state | label with that name exists | **adopt**: PATCH colour/description, record it. This picks up the old Action's `size/*` labels without duplicating them. |
| row, no state | absent | create |
| row and state, differing | — | PATCH by the *recorded* name (`new_name` if renamed). If that name 404s because someone deleted it by hand, create. |
| state, no row | — | delete by the recorded name, then drop the state |

State is written only after GitHub confirms, so a crash part-way leaves a state that the next run converges from.

### D4 — When labels are reconciled

1. **Lazily, before applying a label to a PR.** The applier reconciles first when the state differs from the scale. This
   fixes the `KeyNotFoundException` in the reference for good: a missing label is created, never looked up and thrown on.
2. **Eagerly, after an account saves a changed scale.** `IAfterSaveCommitted` fans out one reconcile message per
   repository that **already has label state**. Repositories that never had a PR since the feature was enabled are
   picked up lazily. This bounds the burst (180 repositories × 6 labels ≈ 1 080 writes) and GitHub's secondary
   content-creation limits; spike **S1** measures the pacing.
3. **On disable, the save asks** (Q2, owner 2026-10-23). The triggers are `Account.PrSizeLabels` going true → false
   (`IsValueChanged`), or a repository's effective value becoming off.
   - `OnBeforeSaveAsync` raises `manager.Retry.Action`: *"Also remove the size labels from the N repositories that
     inherit this setting? Pull requests that carry them, including closed ones, lose them."* The options are
     **Remove labels** / **Keep labels**, plus the built-in **Cancel**, which throws `SparkCancelException` and stores
     nothing.
   - It is a **normal save**: no custom action, and the attribute stays editable. Precedent: `CarActions.cs:115-145`
     prompts from `OnBeforeSaveAsync` on `IsValueChanged`.
   - **No entered value is lost.** On a 449 the browser resends the *same request body* with `retryResults` appended
     (`ng-spark/services/src/spark.service.ts:476-530`).
   - The deletion runs **after the commit**, as a fan-out message from `IAfterSaveCommitted`, never inside
     `BeforeSave`. The answer is carried from the one hook to the other through a scoped holder.
   - The retry reruns the hook from the top. That is harmless here, because nothing before the prompt has side
     effects.
   - **Keep labels** stops labelling and leaves the labels and their state. Turning it back on adopts them again.

There is **no subscription to the `label` webhook**. A manual edit or delete of a managed label in GitHub is repaired at
the next reconcile, by the 404 → create path and by the adopt rule. Subscribing would change the App's events, and every
installation would have to re-approve.

### D5 — How size is computed, and what is ignored

`size = Σ over files not ignored of (additions + deletions)`. A file under an *ignore-additions* rule counts
`max(deletions − additions, 0)`. That is the reference's rule: generated content inflates additions, while a large
deletion may be an accident worth seeing (`PullRequestSizeLabeler.cs:35,70`).

The ignore rules are rows `{ Glob, Mode: Ignore | IgnoreAdditions }` matched by `PathGlob`.

**Account rules, plus a repository's own extra rules** (Q1 C, owner 2026-10-23):

- `Account.PrSizeIgnoreRules` and `Repository.PrSizeIgnoreRules`. There is no policy enum: a repository's rules are
  always *added after* the account's.
- **Matching is path-level, and the last matching rule wins** (Q1b A, gitignore/pascalgn semantics). A leading `!`
  means "count this file fully" and carries no mode. So a repository rule `!apps/web/**/*.json` re-includes JSON under
  `apps/web` only, and `!**/*.json` re-includes all of it.
- Rule-level cancellation by exact glob text was rejected, because a typo would be indistinguishable from a working
  rule.
- **The accepted trap:** an account rule added later applies to every repository unless that repository counters it.

The account defaults are the reference's:

| Glob | Mode |
|---|---|
| `**/package-lock.json`, `**/packages.lock.json`, `**/Directory.Packages.props`, `**/*.md` | Ignore |
| `**/*.json`, `**/*Tests/**` | IgnoreAdditions |

Per-file data comes from GraphQL `pullRequest.files`, paged in full as the reference does. GitHub returns at most **3 000
files**. When `changedFiles > 3000`, the remaining files are counted at their PR-level totals, which can only make the
size larger, never smaller. `PullRequestUpdated` carries no additions, deletions or file counts (`ForgeEvents.cs:90-99`),
so the applier always fetches (**S2**).

### D6 — Exactly one size label per PR, and it is idempotent

- Add the computed label, and remove every **managed** label that is not it. A user's own labels are never touched.
- Skip the call when the PR already carries exactly that label (the reference's `:74`).
- A user who removes the size label by hand gets it back on the next push. That matches the old behaviour, and it is
  not fought on `unlabeled`.
- The merge queue's labeled/unlabeled handling runs first on `pull_request` events (`GitHubEventsRecipient.cs:66-71`).
  The size labeller only consumes `PullRequestUpdated`, which `labeled` never produces, so **changing a label cannot
  loop back into another labelling.**

### D7 — A neutral check run with the size breakdown (Q3, owner 2026-10-23)

The owner chose this over no status and over a check run only on failure.

- **When labelling starts,** a check run named "PR size" is created `in_progress` ("Calculating size…").
- **When it ends,** it is completed **neutral** with the title `size/M · 87 lines changed`. The summary is a table of
  counted files, ignored files, and files counted for deletions only, each with the rule that matched. It is capped at
  about 50 rows plus "and N more", because GitHub limits check-run output text to 65 535 characters.
- **A failure also completes it neutral,** titled "Size label not applied", with the reason (for example a 403 →
  "missing Issues: write").
- **It is never red,** so it is never a gate.

**No permission change.** The App already has Checks R&W (`docs/code-coverage/hosting.md:402`). A commit status like the
reference's would have needed Commit statuses *write*, which the App lacks (`:406`). The labeller goes through the
forge-neutral `IForgeFeedbackPublisher`, whose GitHub implementation maps `EForgeOutcome.Neutral` to
`CheckConclusion.Neutral` losslessly (`GitHubForgeFeedbackPublisher.cs:15-30,69`). The other forges degrade as they
already do.

**The pending-left-behind trap**, since the merge queue gate waits for every check run (merge-queue PRD O12):

- Completion runs in a `finally` path, so any exception still completes the run.
- The run is **idempotent by name and head SHA.** A messaging retry after a killed process updates the same check run,
  never a second one, and so completes the stuck one.
- Precedent: `DependencyUpdateEngine.PublishOwnCheckAsync` (`:1872`) publishes its own in-progress check runs the same
  way.

### D8 — Behind the forge abstraction; GitHub first, GitLab if S4 is cheap

The new `EForgeCapability.PullRequestLabels` comes with `IForgeLabels` (list/create/update/delete label definitions,
list PR labels, add/remove, list PR files with line counts).

- **GitHub** implements it.
- **Bitbucket Cloud has no PR labels.** It declares no capability, and the setting is shown disabled with that reason.
- **GitLab** has merge-request labels. Spike **S4** decides whether it lands in this PR. It is either in, or recorded in
  Out of scope as not being done, and never parked as a follow-up.

### D9 — One writer per repository

The PR applier and the reconciler both write a repository's label state. Two `synchronize` events, or a settings save
that lands during a push, would race on `PrSizeLabelState`. RavenDB optimistic concurrency on the Repository save plus
the messaging retry is the default. Spike **S5** checks whether a per-repository messaging lane is cheaper and clearer.

### D10 — A Guides page for size labels, plus the missing coverage-uploads guide (owner, 2026-10-23)

The owner asked for both in this PR: *"add a Guides page, as already exists for the merge-queue, Dependency-updater and
MCP-server. There's also no guide yet for the coverage setup. The information that's currently on the repository page
('Set up coverage uploads') can move to a guide page."*

The existing pattern, verified in `CodeCoverage/ClientApp/src/app`, has five parts:

- a static page under `pages/<feature>-setup/` (`.component.ts/.html/.scss`, a `*.snippets.ts` for code blocks and a
  spec for it), styles in a sibling `.scss`
- a route open to anonymous visitors (`app.routes.ts:52-59`)
- a `url` program unit in the sidebar's **Guides** group (`App_Data/programUnits.json:64-94`, group
  `b218c106-…`), with its comment block at `:30-33` kept current
- a `translations.json` entry (`mergeQueueSetup` at `:3094`)
- a link from the feature's panel header to the guide (`repo-merge-queue-panel.component.ts:69-70,258-259`)

**Size-labels guide, `/pr-size-labels/setup`.** It covers:

- what the labeller does
- enabling it per account and overriding it per repository (D1)
- editing the scale (D2) and what a rename or delete does on GitHub (D3)
- the ignore rules, their defaults, `!`, and a repository's extra rules (D5)
- the neutral "PR size" check run and its breakdown (D7)
- what disabling does: the Remove / Keep / Cancel prompt (D4.3)
- adopting labels from an older GitHub Action (D3)
- that Bitbucket has no PR labels (D8)

The Account form's size-label group and the Repository override link to it.

**Coverage-uploads guide, `/coverage/setup`.** It is a *move*, not a rewrite. `RepoSetupPanelComponent`
(`components/repo-setup-panel/repo-setup-panel.component.ts`, 262 lines: per-forge, per-language workflow examples) has
no repository input at all, only `baseUrl` (`:95`), so it is already a static guide sitting on a repository page. Its
only host is the Repository extras in `spark/po-detail-page.component.ts:70`.

- The examples and their snippets move into `pages/coverage-setup/`. The page takes the server's `baseUrl` from the
  same source the panel uses today (`browse.service.ts:26`) and falls back to `location.origin` (`:122`).
- The Repository page loses the large card. In its place is a one-line link "Set up coverage uploads →", following the
  merge-queue panel's pattern. **It is shown only while the repository has no coverage yet** (Q4 B, owner 2026-10-23).
  Use a signal the panels above already load, not a new query.
  - The accepted trap: a repository whose uploads break later has no link and finds the guide through the sidebar.
- The panel component and its spec are deleted, not left as dead code.
- A Guides unit is added at order 0 (coverage is the app's first feature), and the other units keep their ids.

## Requirements

| # | Requirement |
|---|---|
| FR1 | A manager can switch size labels on/off per Account; the default is off. |
| FR2 | A manager can set a repository to Inherit / Enabled / Disabled, and the effective value and its source are shown (account / repository). |
| FR3 | A manager can add, remove, rename, re-threshold and re-colour scale rows; invalid scales are refused with a message that names the row. |
| FR4 | On PR opened / synchronize / reopened / ready-for-review / base-changed, with the feature effective, the PR carries exactly one managed size label matching D5. |
| FR5 | Labels the app needs are created when missing, without the reference's `KeyNotFoundException`. |
| FR6 | Renaming a row renames the label in every reconciled repository, and PRs keep it. |
| FR7 | Removing a row deletes that label in every reconciled repository. |
| FR8 | Pre-existing labels with a scale row's name are adopted, not duplicated. |
| FR9 | User labels, including other `size/*` names not in the scale, are never modified or removed. |
| FR10 | Non-managers can neither read nor write any of these settings (deny-by-default redaction holds). |
| FR11 | Switching off prompts Remove labels / Keep labels / Cancel on the save. Every other edited value survives the prompt, only Remove deletes, and Cancel stores nothing. |
| FR14 | A repository's extra ignore rules apply after the account's, last match wins, and `!` counts a file fully. |
| FR15 | Every labelling run leaves exactly one completed **neutral** "PR size" check run on the head commit, with the breakdown, also on failure. |
| FR12 | `/pr-size-labels/setup` exists, anonymous-readable, in the sidebar's Guides group, linked from the Account and Repository settings. |
| FR13 | `/coverage/setup` holds everything the "Set up coverage uploads" card showed, with identical snippets for the same `baseUrl`; the Repository page links to it instead of embedding it, and `RepoSetupPanelComponent` is gone. |

## Out of scope

The following are genuinely not being done:

- **Failing a check or commenting when a PR is too large** (`fail_if_xl` / `message_if_xl`). That would be a gate, and
  this is a label.
- **Ignore rules in `coverage.yml`.** A repository-file source for configuration is a different mechanism; the repository's own rules (D5) are
  the in-app alternative.
- **Bitbucket.** The forge has no PR labels.
- **cbrgm's extras:** a file-count threshold next to the line threshold, several labels per size (`pairing-wanted`), and
  `added_lines_only`. Ignore-additions rows cover the useful part of the last one.

## Questions — all answered 2026-10-23

| # | Question | Answer |
|---|---|---|
| Q1 | Per-repository ignore rules? | **Extra repository rules after the account's** (no replace, no policy enum); path-level, last match wins, `!` = count fully (D5) |
| Q2 | On disable, delete the labels? | **Ask on the save**: Retry.Action with Remove / Keep / Cancel (D4.3) |
| Q3 | Commit status? | **A check run**, `in_progress` → neutral, with the size breakdown (D7) |
| Q4 | When to show the coverage-setup link? | **Only while the repository has no coverage yet** (D10) |

## Risks

- **Write bursts.** Enabling a scale change on a large org is bounded by D4.2, but still real. S1 measures the pacing.
- **Adoption overwrites colours.** The adopt rule takes over a hand-made `size/M` and repaints it. That is intended, but
  it is visible to users.
- **Remove labels is irreversible** from GitHub's side: the labels are removed from closed PRs too. The prompt says so.
- **Production app.** The new settings are protected deny-by-default. FR10 still gets an explicit test, because a
  missing redaction would leak an organization's configuration to anonymous readers (`security.json:21` grants
  `QueryRead/Account` anonymously).

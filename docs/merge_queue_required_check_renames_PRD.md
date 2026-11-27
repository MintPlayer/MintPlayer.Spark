# Merge queue: renaming a required CI job (#714) — PRD and plan

Issue #714 (bug), with both of its comments. One pull request. This file holds the findings, the options, the owner's
questions, the spikes and the milestones; the decisions and the log follow as they are made. Paths are under
`apps/CodeCoverage/` unless they start with `docs/`, `.github/` or `CLAUDE.md`.

Status: **draft, 2026-11-27.** Nothing is decided yet. §2 corrects part of the issue (F1) and finds two more stalls
(F3, F4). §5 holds the owner's questions. Spikes S1, S3 and S5 are done (§6.1). S1 found that `/release` itself is not
at risk, so nothing here has a deadline any more.

## 1. What happened

- PR #697 joined two matrix jobs in `.github/workflows/test.yml` (#536 M9a, `2ca3ed499`). `tests / Library suites` and
  `tests / CodeCoverage git suites` became `tests / Library + git suites`, and `tests / SQL Server` and
  `tests / PostgreSQL` were added, to stay at 20 concurrent jobs.
- Ruleset 26719109 (ruleset 2, "merge-queue: reviews and checks") still required the two old names. On head
  `6b25997d7` every reported check had completed. GitHub showed the two old names as "Expected — waiting for status to
  be reported", and the "Merge queue" check stayed in progress. Nothing would ever report them.
- The owner fixed the ruleset by hand at 06:59 UTC (its live `updated_at` is 2026-11-27T07:59:51+01:00). The entry
  still waited: it keeps the rules it read when it was enabled (`entry.RequiredChecks`), and every 2-minute poll
  checks against that copy until `checks.timeout` (60 min).
- `/dequeue` (07:03:38 UTC) and `/queue` (07:04:25) started a new entry at 07:04:33. It read the fixed ruleset, found
  every check reported, and merged at 07:04:44.

The issue proposes three directions:
1. The setup accepts check names the default branch has not reported yet.
2. The queue names the deadlock.
3. Docs.

Comment 1 adds a fourth: re-read the required checks when a poll finds every check suite completed and a required
check still missing.

## 2. Findings (2026-11-27)

Three read-only investigations looked at the setup, the engine, and CI with the live rulesets. Every claim below was
checked again against the code or the API before it went in.

### F1. The setup can already take a name the default branch never reported

The issue's "Why the setup can't fix it" is out of date.
- Since #554 (2026-11-07) the planner no longer guesses from the default branch's head. That head carried push-only
  jobs, so ruleset 26719109 once required checks no pull request could pass (`MergeQueueRulesets.cs:205-209`).
- The form starts from ruleset 2's own contexts, or else classic protection's (`MergeQueueRulesets.cs:210-227`).
- Its picker offers the checks seen on the repository's pull requests (`Repository.KnownChecks`). "Add new" takes a
  typed name (`MergeQueueRulesetSetup.cs:157-162`).

So direction 1 exists already. Four things are missing:
- **Which names changed.** The confirmation says only that `required_status_checks` changed. It doesn't say which
  names were added or dropped.
- **Stale names.** Nothing flags a required name that no recent pull request reported. The form's list replaces the
  ruleset's (`MergeQueueRulesetSetup.cs:176-185`), so an old name stays until the admin removes it.
- **A prompt.** Nobody is told to run the setup. The pull request that renames a job has no idea a ruleset names it.
- **The doc.** `docs/code-coverage/merge-queue.md:598-599` still says "Ruleset 2 requires the checks the default
  branch's head reports".

### F2. The engine knows which checks are missing, but it never says so and never re-reads

- **Both lists are set once per queue run.** The gate waits for two lists, and both are set in the enable step:
  - `RequiredChecks`, from `GetBranchRulesAsync` (`MergeQueueEngine.cs:534-535, 558`);
  - `ExpectedChecks`, the checks that reported on the head pinned at enqueue (`:560-561`, merge-queue PRD D45).

  Only the enqueue reset clears them (`:463-464`). Rebases, re-runs and restarts keep them. `MergeQueueEntry` is a
  stored RavenDB document, so its properties are never renamed.
- **The gate already computes the missing names.** `CheckGate.Evaluate` puts every required or expected name that
  never reported in `Missing` (`CheckGate.cs:95-97`), and counts that as pending (`:118-122`).
- **The engine says nothing about them.** The stuck state is `Verdict == Pending` with `Pending` and `PendingSuites`
  empty and `Missing` not empty. In that state the engine's `default:` branch schedules the next poll and says nothing
  (`MergeQueueEngine.cs:1788-1790`). The check run's title stays "Waiting for checks" (`MergeQueueCheckRenderer.cs:35`).
- **The timeout message is wrong.** At the timeout, the eviction says the checks "did not finish within 60 minutes"
  (`MergeQueueEngine.cs:1806-1807`), although every check had finished.
- **The data to name the ruleset is already read.** GitHub's `rules/branches/{branch}` returns a `ruleset_id` with
  each rule, and the forge reads it (`GitHubForgeMergeQueue.cs:117-136`). `ForgeBranchRules` keeps only the merged
  `RequiredChecks` and each ruleset's rule types, so it cannot say which ruleset requires which name.
- **A re-read costs 3 or more calls.** `GetBranchRulesAsync` makes the paged rules read, one `rulesets/{id}` read per
  ruleset, and the classic-protection read (`GitHubForgeMergeQueue.cs:108-145`).
- **One forge.** `GitHubForgeMergeQueue` is the only `IForgeMergeQueue` implementation (engine investigation, not
  re-checked).

### F3. The expected list stalls an entry queued behind a rename, even with a correct ruleset

This comes from reading the code. It has not been observed.
1. Entry B is enabled with the check names of its own head, so with the old name.
2. Pull request A, the rename, merges first.
3. The queue rebases B onto the new base. B's CI on the rebased commit runs the base's workflows and reports the new
   name.
4. `ExpectedChecks` still holds the old name. It stays in `Missing` for good, and the entry ends with
   `checks-timeout` after 60 minutes.

The engine already recognises this case. The git writer reports `BaseChangedWorkflows` when the prepared commit's
`.github/workflows` differs from the pinned head's (`MergeQueueEngine.cs:1123`, `GitWriterSidecarClient.cs:378-380`).
Comment 2 says "an entry already in the queue also needs `/dequeue` and `/queue`". That is right, and F3 is a second
reason for it, besides the ruleset.

### F4. Ruleset 3 still requires the old names; `/release` is not at risk (S1)

> **Superseded by S1 (2026-11-27).** This finding first said "Ruleset 3 is likely to stall the release lines at the next
> `/release`". S1 showed that `/release` never goes through ruleset 3 (§6.1). The exposure is any pull request against
> a line after the next promotion, and a past line cut later. Both are rare today; A1 removes them for good.

- **What ruleset 3 requires.** Ruleset 27018553 ("merge-queue: release-line checks", `refs/heads/release/*-*.x`,
  updated 2026-11-12) requires `tests / Library suites` and `tests / CodeCoverage git suites` (live GET, 2026-11-27).
- **What the lines produce.** Both lines still produce those names: `release/dotnet-11.x` at `abeeb94cd` and
  `release/angular-22.x` at `55d7dfffb` (from `git show origin/<line>:.github/workflows/test.yml`).
- **Why a line PR would stall.** The queue holds pull requests on a line to ruleset 3's checks itself, even where
  the App is exempt from that ruleset (`MergeQueueEngine.cs:542-547`: "the queue waits for the required checks
  itself"). A line pull request that brings #697's `test.yml` onto a line reports `tests / Library + git suites`
  instead, so it would wait like #697 did. S1 answers whether a promotion takes that path or only later backports do.
  **S1's answer:** a promotion is a direct push, so neither path applies today (§6.1). Only a pull request opened
  against a line after the next promotion would stall.
- **Why no list of job names can fit.** One ruleset covers both lines, and the lines receive the rename at different
  times. In between, no list of job names fits both lines. Only a name that never changes does.

### F5. Spark already has a check whose name never changes: the aggregate `pull-request` job

- **The job.** `pull-request.yml:288-301`: "One check that is green only when every job is".
  - It `needs: [build, angular, tests, coverage, actions]`.
  - It runs `if: always()`.
  - It fails when any of those jobs failed, was cancelled or was skipped.
- **What it covers.** `tests` is the call to the reusable `test.yml`, so its result covers every matrix leg, SQL
  Server and PostgreSQL included.
- **Ruleset 2's 24 contexts** (live GET, 2026-11-27):
  - 21 are jobs the aggregate already covers: `build + verify`, `Angular apps`, `coverage upload`, `action bundles`
    and the 17 `tests / …` jobs.
  - The other 3 are `pull-request` itself and CodeCoverage's checks `coverage/patch` and `coverage/project`. These
    two are check runs of the CodeCoverage App (4574022) that end `neutral`, not commit statuses (S5).
  - No context pins an `integration_id`.
- **Ruleset 3** has the same shape with the old names, plus `Line policy`, which is pinned to the App (4574022).
- **What requiring only the aggregate would mean.** Rulesets that require only `pull-request`, `coverage/patch` and
  `coverage/project` (plus `Line policy` on ruleset 3) give the same protection from GitHub, and no job rename touches
  a ruleset again. The queue's own gate is not weakened: it waits for every check that reports, required or not
  (`CheckGate.cs:84-87`), and for the expected list.
- **What it costs.**
  - The aggregate's name becomes the one name that matters: renaming `pull-request` recreates #714.
  - A new job must be added to the aggregate's `needs`, or GitHub will not enforce it (the queue still does). Today a
    new job has to be added to the ruleset instead, so this is no worse.

### F6. Catching the rename inside the pull request is feasible, with one open point

- `GET /repos/{o}/{r}/rules/branches/{branch}` answers an authenticated `gh` call (live, 2026-11-27).
- Whether a workflow's default `GITHUB_TOKEN` (`contents: read`) may read it is untested (S4).
- A static test needs no API at all (D1).

## 3. Goals and non-goals

Goals:
- **G1.** Renaming, joining or splitting a CI job never stalls a pull request, on master or on a line.
- **G2.** When a required or expected check can never report, the queue says so while it waits: which names, and
  which ruleset requires each. It no longer waits 60 minutes without a word and then says the checks "did not
  finish".
- **G3.** A ruleset fixed while an entry waits frees that entry at the next poll, without `/dequeue` and `/queue`.
- **G4.** An entry queued behind a rename does not wait for the old names (F3).
- **G5.** The docs say what to do.

Non-goals and constraints:
- **The App never writes rulesets.** Administration was removed from both Apps on 2026-11-06, and GitHub has no
  narrower permission (owner; see the "#554 redesign" in `docs/coverage_backport_plan.md`). Every ruleset change stays
  with a person: the "Set up rulesets" action's one-shot OAuth token, or GitHub's UI.
- **Not GitHub's native merge queue.** The engine refuses it (`MergeQueueEngine.cs:536-537`).
- **One pull request** for everything (global rule 8). CodeCoverage is production.

## 4. Options

### Part A: Spark's own rulesets (F4, F5)

| | Option | For | Against |
|---|---|---|---|
| A1 | Rulesets 2 and 3 require only `pull-request`, `coverage/patch` and `coverage/project` (plus `Line policy` on ruleset 3) | No job rename touches a ruleset again. One list fits both lines, whatever their tree. GitHub protects exactly as much as today (F5) | The aggregate's name is the one that matters. GitHub's merge box lists 3 required checks instead of 24 |
| A2 | Keep a context per job, and change the rulesets in the same change as every rename | Nothing to do now | Cannot fit F4 (two lines, one ruleset). Every rename needs an admin at the right moment |
| A3 | One ruleset per line, each with its own list | Lines can differ | The setup writes one ruleset 3 for the pattern. More rulesets to keep in step. Still work on every rename |

**Recommendation: A1.** The owner applies it, through "Set up rulesets" or GitHub's UI. There is no deadline (S1).

> **Superseded (2026-11-27):** "applied before the next `/release` (S1 says how urgent)". S1 found no release risk.

> **Superseded by Dec2 and Dec3 (2026-11-27):** "The owner applies it, through 'Set up rulesets' or GitHub's UI".
> Nobody edits the bot's rulesets by hand. A1 becomes the shape the setup writes for every repository, and Spark
> switches to it when the owner runs the setup after the deploy (M7).

**A1 hardening: pin each context to its App.** Under A1, the whole of GitHub's protection rests on one name. Pinning
`pull-request` to GitHub Actions (`integration_id` 15368) and the two coverage checks to the CodeCoverage App (4574022)
means no other App can satisfy them. The App ids were read from #697's head (S5), and `Line policy` is pinned this way
already. Re-running the setup keeps a pin: the planner keeps the `integration_id` of a check that is already required
(`MergeQueueRulesets.cs:107, 373-378`).

### Part B: the setup (direction 1, F1)

- **B1.** The confirmation names, per ruleset, the checks it adds and the checks it drops. Today it says only "rule
  changed: required_status_checks". *Recommended.*
- **B2.** The form marks required checks that no pull request reported in the last N days. `KnownChecks` keeps
  `LastSeenUtc` and `OnPullRequest` for each name. *Recommended.*
- **B3.** The form's help text recommends requiring one aggregate check, and says why. That brings A1 to every
  onboarded repository, not only Spark. *Recommended.*
- **B4.** Prefill the form from a chosen pull request's head, the issue's first example. *Not recommended:* typed
  names already work (F1), and with B2 and B3 a rename rarely needs the setup.

### Part C: the engine (directions 2 and 4; F2, F3)

- **C1. Detect the stuck state:** `Pending` and `PendingSuites` empty, `Missing` not empty (F2).
- **C2. Re-read the rules at that point** (comment 1). Call `GetBranchRulesAsync` again. If `RequiredChecks`
  changed, replace it, note the change on the entry, and evaluate again. At most once per N minutes per entry, since a
  read is 3 or more calls (F2). *Recommended.*
- **C3. Name the deadlock.** *Recommended.*
  - The check run's title becomes "Waiting for checks that never reported".
  - Its summary lists each missing name with where it comes from: the ruleset (name and id) that requires it, or
    "reported on the queued head" for an expected name.
  - The timeout eviction says the same, instead of "did not finish".
  - `ForgeBranchRules` has to keep each ruleset's required checks for this. The `ruleset_id` is already read (F2).
- **C4. Drop stale expected names (F3).** When `BaseChangedWorkflows` is set and every suite of the App that
  reported the name has completed, a missing expected name is dropped, with a note. Names from a commit status or
  from another App are still waited for. S3 settles the exact rule. *Recommended, subject to S3.* **S3's rule** is
  in §6.1. It adds one condition: the commit must have been stuck at the previous poll as well.
- **C5. Evict early** instead of waiting for `checks.timeout`. *Not recommended.* A workflow started by
  `workflow_run` can start after the first suites complete, and external statuses arrive on their own (`CheckGate.cs:18-25`
  describes what `RunsFollow` already covers). With C2 and C3, the wait is visible, and it ends as soon as someone
  fixes the ruleset.

### Part D: catching the rename inside the pull request (F6)

- **D1. A static guard test, with no API.** It parses `pull-request.yml` and asserts that the aggregate is named
  `pull-request` and `needs` every other job. Under A1 this is the whole contract. *Recommended.*
- **D2. A dynamic CI step.** It reads `rules/branches/<base>` with the default token and fails when a required
  context is not produced at this head. It needs S4, and it must work out the matrix jobs' names. *Not recommended
  under A1.*
- **D3. A hint from the queue.** When C1 fires and the pull request's own diff touches `.github/workflows`
  (`entry.WorkflowPaths`, `MergeQueueEngine.cs:1124`), C3's summary adds: "this pull request changes workflows: a
  renamed job may still be required by the ruleset". It is cheap. *Recommended, together with C3.*

### Part E: docs (direction 3)

- **`CLAUDE.md`**, "Running the test suites": the "CI layout" paragraph and the bullet on 20 concurrent jobs. Say
  that the rulesets require only the aggregate, and that renaming `pull-request` (or a coverage check) means
  changing rulesets 2 and 3 first.
- **`docs/code-coverage/merge-queue.md` §3**, "Recommended GitHub setup":
  - replace the stale lines 598-599;
  - recommend one aggregate check;
  - add a troubleshooting entry, "a required check never reports": what C3 shows, and what to do.
- **`docs/code-coverage/onboarding.md`**, "Left to you": the aggregate recommendation.

## 5. Questions for the owner

| # | Question | Recommended | Risk if the answer is wrong |
|---|---|---|---|
| Q1 | Should Spark's rulesets 2 and 3 require only `pull-request`, `coverage/patch` and `coverage/project`, plus `Line policy` on ruleset 3 (A1)? And should each be pinned to its App (A1 hardening)? | Yes to both, with no deadline (S1) | Without it, every rename needs a ruleset edit at the right moment, and a pull request against a line after the next promotion stalls (F4) |
| Q2 | In the stuck state, re-read the rules and name the deadlock while keeping the 60-minute timeout (C2, C3), or also evict early (C5)? | Re-read and name it; no early eviction | A 60-minute wait remains when nobody fixes the ruleset, but now it says why |
| Q3 | Drop stale expected names after the base changed its workflows (C4)? | Yes, with the rule from S3 | Too loose: an entry merges before a late workflow reports. Too strict: F3 stays |
| Q4 | Setup: B1, B2 and B3, but not B4? | Yes | None worth noting |
| Q5 | Guard: the static test (D1) and the queue's hint (D3), without the dynamic CI step (D2)? | Yes | Without D1, a renamed aggregate slips through |

All five were answered in the grilling round of 2026-11-27, together with the follow-up questions Q1a–Q1f, Q2a and
Q4b. §5.1 records the answers as Dec1–Dec12; where a decision differs from the table above, the decision wins.

### 5.1 Decisions

**Dec1 (owner, 2026-11-27, answering Q1): nobody edits a ruleset by hand.**
- The owner's words: "The most important thing is that the developers don't need to manually modify their rulesets."
  It "has to be as low-effort as possible for the user".
- The flow:
  - When a repository's required checks no longer match the checks its workflows report, the app shows a bell
    notification.
  - The notification offers to run the setup again.
  - The setup's popup asks for the short-lived user token again, and the fix is one confirmation.
- Q1 itself (A1 for Spark, and pinning) stays open. It is asked again once the automatic fix is settled.
- Evidence that the parts exist (2026-11-27):
  - **The bell.** CodeCoverage registers `AddNotifications()` (`Program.cs:327`), and format runs already notify
    through `ISparkNotifier` (`FormatRunEngine.cs:337`).
  - **The popup.** `MergeQueueRulesetSetup.AuthorizeAsync` gets a one-shot OAuth lease, which is revoked when the
    action ends (`MergeQueueRulesetSetup.cs:31-34, 106-108`).
- **What is missing.** The form starts from the ruleset's own list (`MergeQueueRulesets.cs:196-227`), so running it
  again only repeats the stale names. It has to start from the corrected list instead.
- **Pinning under Dec1.** The setup cannot add a pin. It keeps a pin that is already there (`MergeQueueRulesets.cs:107`)
  and pins only `Line policy` (`:348`). So if checks are to be pinned, the setup has to learn to pin them.
  `KnownChecks` records each name's App by slug, not by id (`ForgeCheck.cs:53`).

**Dec2 (owner, 2026-11-27): people edit only the rulesets they made by hand.**
- The owner's words: "We just need to modify the rulesets we created ourself/ weren't created by this bot".
- **The cause.** A developer pull request renamed the jobs, and the rulesets were not updated. The app's only part
  was the silent 60-minute wait (F2, F3).
- **Spark has no hand-made ruleset that requires checks.** Live list, 2026-11-27, `includes_parents=true`, with no
  organization rulesets:
  - Four of the five rulesets are the bot's: `merge-queue: restrict updates` (26211184), `reviews and checks`
    (26719109), `release-line checks` (27018553) and `backport branches` (27098177). Their names come from
    `MergeQueueRulesets.cs:128-131`.
  - The fifth, "Spark schema revisions (schemas/*)" (24486043), protects `refs/tags/schemas/*` with `update` and
    `deletion` only. It requires no checks.
- **So the drift sits in two of the bot's rulesets,** which hold the names of jobs from Spark's own workflows. Keeping
  them in step is therefore the bot's job (Dec1). That reading is ours; it is put to the owner as Q1c.
- **State on 2026-11-27:**
  - Ruleset 2 was corrected at 07:59 and matches master. It holds `tests / Library + git suites`, `tests / PostgreSQL`
    and `tests / SQL Server`.
  - Ruleset 3 still matches the lines, which carry the old `test.yml`. It drifts at the next promotion.
  - The `pull-request` summary job, with the same `needs`, is on master and on both lines.

**Dec3 (owner, 2026-11-27, answering Q1c with "A"): the bot writes only names that a job rename cannot touch.**
- **What it writes.** The bot's rulesets require the App's own checks (`coverage/patch`, `coverage/project`, and
  `Line policy` on ruleset 3) plus one summary job. The user picks that job once in the setup; for Spark it is
  `pull-request`. This is A1 made the setup's shape, and B3 becomes part of the form.
- **What that buys.** Renaming, joining or splitting a job inside the workflow never touches a bot ruleset.
- **Where Dec1's drift bell is still needed:**
  - when someone renames the summary job;
  - for a repository without a summary job, which keeps a list with one entry per job.
- **What the setup must explain.** The bot never edits a CI workflow (`onboarding.md:17`), so it cannot add a summary
  job itself. The setup explains why one is worth adding.
- **Evidence:**
  - S5: every check ruleset 2 required was in `pull-request`'s `needs`.
  - Live, 2026-11-27: the summary job is on master and on both lines, with the same `needs`.
- **Spark gets this through the setup itself.** After the deploy, the owner runs "Set up rulesets" again (M5). Nobody
  edits a ruleset by hand (Dec2).

**Dec4 (owner, 2026-11-27, answering Q1d with "A"): the bot pins every check it requires.**
- **The App's own checks** get the App's id, as `Line policy` already does (`MergeQueueRulesets.cs:348`).
- **The summary job** gets the id of the App seen reporting it: 15368 for GitHub Actions (S5). It stays unpinned while
  that id is not known.
- **`ForgeCheck` gains an `AppId`.** It is filled from the webhook's `check_run.app.id`, read next to the slug that
  `GitHubEventsRecipient.cs:785` reads today, and from the backfill. It is a new property, so stored documents are safe.
- **The risk accepted.** A summary job that moves to another CI service reports from another App. The queue ignores
  pins (`CheckGate.cs:29`), so it sees the job green, and GitHub refuses the merge. Dec1's drift check therefore covers
  this case too: a pinned name reported by a different App.

**Dec5 (owner, 2026-11-27, answering Q1e with "A"): the queue detects drift, and managers see it when they come in.**
- **Detection.** The queue records drift on the repository at two moments, both of them certain:
  - an entry is stuck: a required name is missing and nothing else is pending (C1);
  - GitHub refuses a merge because of a pin (Dec4).
- **Delivery.** Each user who manages the repository gets one bell notification per drift, the next time they sign in
  or open the app. Its link opens "Set up rulesets".
- **Why at sign-in.** "Manages" can only be checked for the viewer who is present: a manager of the account, or an
  admin of the repository on the forge, asked at that moment (`DependencyOnboardingService.cs:89-93`). So no list of
  managers exists to notify in the background.
- **Rejected:**
  - Notifying whoever last ran the setup: they may have left.
  - A live ruleset read at every sign-in: API calls on every sign-in for a rare event.
- **The risk accepted.** The first pull request queued after a rename of the summary job is stuck once. C2 and C3
  soften that (Q2).

**Dec6 (owner, 2026-11-27, answering Q1f with "A"): opened from the bell, the form starts from the fixed list and
guesses nothing.**
- **A pin mismatch** is re-pinned to the App that reports the name now. That App is known, so nothing is guessed.
- **A repository with one required check per job** has its dead names removed, and its new names listed unticked.
  This was Q1a's option A, and it can never block a pull request.
- **A renamed summary job** has its dead name removed. The summary-job field is left empty and highlighted, with the
  names reported on the stuck pull request listed to pick from.
- **Rejected: preselecting the job that started last.** Timing proves the order the jobs ran in, not what a job waits
  on. A wrong pick that someone confirms without reading makes every pull request wait for the wrong job.

**Dec7 (owner, 2026-11-27, answering Q2 with "A"): while stuck, the queue names the missing checks and re-reads the
rules at every poll.**
- **C3, naming them.** The check run's title becomes "Waiting for checks that never reported". Its summary lists each
  missing name and the ruleset that requires it, and says that the managers have been notified (Dec5).
- **C2, re-reading.** The rules are read again at every poll while the entry is stuck, every 2 minutes. A fixed
  ruleset frees the entry at the next poll, whoever fixed it.
  - It is not throttled. It costs about 3 calls per poll, so at most about 90 per stuck hour, against the
    installation's 5,000 per hour.
  - The queue cannot be told about a fix instead. GitHub sends ruleset events only to an App with Administration
    access, which this App does not have (non-goal).
- **The timeout stays at 60 minutes,** and its message names the missing checks.
- **No early eviction (C5).** A workflow started by `workflow_run`, or an external status, may legitimately arrive
  late (`CheckGate.cs:18-25`).

**Dec8 (owner, 2026-11-27, answering Q2a with "A"): the queue comments once when an entry is stuck.**
- **The owner's question:** "So the app will post a comment explaining that some required checks don't occur in the
  github workflows?" Until now the queue commented only at eviction (`MergeQueueEngine.cs:2007-2009`) and for held
  backports (`:700`, `:733`). While waiting for checks it updated only its check run.
- **When.** One comment per entry, once the stuck state has held at two polls in a row (2 minutes, S3's condition 4).
  It shares the "stuck since" time that C4 needs.
- **What the comment says:**
  - each missing name, and the ruleset that requires it (or "reported on the queued head", for an expected name);
  - that nothing on this commit reported it, although every other check has finished;
  - D3's hint, when the pull request changes workflows;
  - that the managers will be notified (Dec5), that "Set up rulesets" fixes it, and that the queue picks up the fix by
    itself (Dec7);
  - when the entry will be removed otherwise.
- **The wording.** The app never reads workflow files, so it cannot say that a check "doesn't occur in the
  workflows". It says "hasn't reported", never "doesn't exist".
- **Rejected:**
  - Commenting at the first stuck poll: a workflow started by `workflow_run` can briefly look absent.
  - Commenting only at eviction: that is the 60-minute silence #714 describes.

**Dec9 (owner, 2026-11-27, answering Q3 with "A"): a stale expected name is dropped by S3's rule (C4).**
- **The rule.** A missing expected name is dropped, with a note on the entry and in the check run, only when all four
  conditions hold:
  1. `BaseChangedWorkflows` is set;
  2. the name was a GitHub Actions check run;
  3. the gate is stuck;
  4. it was also stuck at the previous poll.
- **What is never dropped:** a commit status, or another App's check run. Dec8's comment names it instead.
- **Storage:**
  - Each expected name's App is captured when the entry is enabled. `GetCheckNamesOnCommitAsync` returns
    `ForgeReportedCheck` instead of bare names.
  - It is stored in a new property, so `ExpectedChecks` keeps its type and no data migration is needed.
  - The "stuck since" time is shared with Dec8.
- **Rejected:**
  - Discarding the whole expected list after a workflow change: it stops waiting for late external checks.
  - No drop at all: it leaves a manual `/dequeue` and `/queue` on an innocent pull request.

**Dec10 (owner, 2026-11-27, answering Q4 with "A"): the setup shows what changes and marks stale names.**
- **B1.** The confirmation names, for each ruleset, the checks it adds and the checks it drops. This is what makes
  Dec6's one-click fix safe to confirm.
- **B2.** When the form is opened by hand, a required name that no pull request reported in the last 14 days is marked
  "not seen since …", from `ForgeCheck.LastSeenUtc` (`ForgeCheck.cs:56`). The mark removes nothing.
- **B3** is part of Dec3. **B4** is replaced by Dec6's prefill.

**Dec11 (owner, 2026-11-27, answering Q4b with "B, with another github-user-auth-popup if necessary"): one bell per
repository invites the switch to a summary job.**
- **Who is told.** The managers of every repository whose rulesets still require one check per job get a one-time bell:
  "Your rulesets can switch to one summary job".
  - It is delivered the way Dec5 delivers, at sign-in or when the app is opened.
  - Its link opens "Set up rulesets", which shows the usual popup for the short-lived user token, offers the switch,
    and lists the changes (B1).
- **What counts as "one check per job".** The required list holds more than one name that the App does not report
  itself. The App reads that list with its own token: `GetBranchRulesAsync` needs no admin rights.
- **When it is checked:**
  - each time the queue reads the rules at enable;
  - once for every repository with a `mergeQueue` section after the deploy, so a quiet repository is told too.
- **The same offer** appears whenever the setup opens on such a list, by hand or from a drift bell (Dec5).

**Dec12 (owner, 2026-11-27, answering Q5 with "A"): a static test guards Spark's summary job (D1).**
- **The test.** `tools/verify-pull-request-summary.test.mjs` asserts three things about `pull-request.yml`:
  - the summary job is named `pull-request` and runs `if: always()`;
  - its `needs` lists every other job in the file;
  - it fails when any of them failed, was cancelled or was skipped.
- **Where it runs.** It joins `npm run test:tools`, which every pull request already runs (`pull-request.yml:100`). The
  precedent for parsing a workflow file is `tools/verify-backend-networks.test.mjs:32-37`.
- **Why Spark guards this itself.** The bot never reads or edits workflows, so only Spark can catch a job left out of
  `needs`. Such a job never makes an entry stuck, so the drift bell would never fire for it.
- **D2 is dropped, and S4 with it.** D3 is part of Dec8.

## 6. Spikes

| # | Question | Where | Done when |
|---|---|---|---|
| S1 | How do a promotion and a backport reach `release/*-*.x`, and which list holds them? Confirm F4: does the first line pull request that carries #697's `test.yml` wait for `tests / Library suites` for good? Does a promotion go through the queue's gate at all, or does the exempt App push it directly, so that only later backports stall? | This repo, read-only (`MergeQueueEngine.cs:542-547` and the promote path), plus GETs on the lines' latest pull requests | A trace with file:line, and an answer to "must ruleset 3 change before the next `/release`?". **Done 2026-11-27: no (§6.1).** |
| S2 | End-to-end repro on a sandbox. A ruleset requires `ci / job-a`; pull request 1 renames that job to `job-b`; pull request 2 is queued behind it. Before the fix, both stall (F2, F3). After C2–C4 (dev App), fixing the ruleset frees pull request 1 within one poll, and pull request 2 drops `job-a` with a note. Under A1 (only the aggregate required), pull request 1 merges without any ruleset change | `PieterjanDeClippel/SparkTest` with the dev App. This writes to GitHub, so it needs the owner's approval first | The four outcomes recorded, with check-run titles and timestamps |
| S3 | The rule for C4: which signals prove that an expected name is gone for good? Candidates: `BaseChangedWorkflows`; the name's App (`KnownChecks` keeps `AppSlug` for each name); every suite of that App completed on `FinalSha`; `RunsFollow`. What happens with a workflow started by `workflow_run`, and with a commit status? | This repo, read-only, then `CheckGate` table tests | The rule written down, with a test for each case, including the two "keep waiting" cases. **Rule done 2026-11-27 (§6.1); the tests belong to M2.** |
| S4 | Can a workflow's default `GITHUB_TOKEN` (`contents: read`) read `GET /repos/{o}/{r}/rules/branches/{b}` on a public repository? | A throwaway workflow run on SparkTest | Answered, with the response. **Dropped: Q5 chose D1, not D2 (Dec12).** |
| S5 | Does today's aggregate already cover everything ruleset 2 requires, on a real head? Compare the check runs of a recent master pull request's head with the aggregate's `needs` and the coverage statuses | This repo, plus GET `commits/<sha>/check-runs` | An empty "required but not covered" list, so A1 loses nothing. **Done 2026-11-27: empty (§6.1).** |

### 6.1 Results (2026-11-27)

**S1: `/release` does not go through ruleset 3 or the queue.**
- `/release` (promote mode) does three things:
  - it picks the newest default-branch commit whose `versioning.lines.workflow` run succeeded;
  - it gets the App's writer access (`pushesWorkflowChanges: true`);
  - it has the git writer's `PromoteService` push the promotion straight to the line.

  Evidence: `PackageVersioning/ReleaseRecipients.cs:43-57, 179-196`.
- Ruleset 3 exempts that App: `bypass_actors` = actor 4574022, `bypass_mode: exempt` (live GET of ruleset 27018553).
- The last promotion went this way: `abeeb94cd` "Promote release/dotnet-11.x to master fc95abe3820e", 2026-11-18,
  followed by that day's package tags (`MintPlayer.Spark.Templates/v11.0.0` and others).
- **No pull requests reach a current line.** A `backport:` label on a current line opens no pull request: the bot
  answers that the change ships with the next `/release` (seen on #696, 2026-11-19). The latest pull requests against
  either line are from 2026-11-07 to 2026-11-09 (#581–#610), before the lines switched to promote mode.
- **So the exposure is:**
  - a pull request opened against a line by hand after the next promotion, which brings #697's `test.yml` there;
  - a past line (`dotnet-10.x`, `angular-21.x`) cut later, whose tree has other job names than the current lines
    under the same ruleset 3.

  A1 covers both, so A1 is no longer urgent.

**S3: the rule for dropping a stale expected name (C4).** A missing expected name is dropped, with a note on the entry
and in the check run, only when all of these hold:
1. `entry.BaseChangedWorkflows` is set. The base changed `.github/workflows` since the head pinned at enqueue. Without
   that, a name that reported on the pinned head can only be late, never gone. The pull request's own workflow change
   is already in the pinned head's CI.
2. The name was a check run of GitHub Actions (`github-actions`). A commit status, or another App's check run (an
   external scanner, CodeCoverage's own checks), is never dropped: it may report late, and C3 names it instead.
3. The gate's stuck state holds: no pending check, no pending suite (`RunsFollow` included), only `Missing`.
4. The same stuck state held at the previous poll too, one `ChecksPollInterval` (2 min) earlier. This covers a
   workflow that `workflow_run` starts: its suite appears only once the run that triggers it has completed, so for a
   moment every suite on the commit can look complete. This repository has no such workflow (no `workflow_run`,
   `check_suite` or `repository_dispatch` trigger under `.github/workflows`), but other repositories on the queue may.

A required name (from a ruleset) is never dropped: GitHub would refuse the merge anyway. For it, the fix is C2's
re-read.

Implementation constraints found:
- `ExpectedChecks` stores names only. `GetCheckNamesOnCommitAsync` returns the names of check runs and statuses,
  without their App (`GitHubForgeMergeQueue.cs:352-365`).
- Condition 2 therefore needs each expected name's App, captured at enable time. Two ways:
  - return `ForgeReportedCheck` (name, kind, App; `IForgeMergeQueue.cs:383`) from that call;
  - read `Repository.KnownChecks`. That list may have dropped old names (`ForgeCheck.MaxKnown`), so it is the weaker
    source.
- `MergeQueueEntry` is stored. A new property is safe. Changing the type of `ExpectedChecks` would change its stored
  format, which needs a RavenDB data migration (feedback: write a migration, don't freeze the format).
- Condition 4 needs a "stuck since" time on the entry: one more new property.

The tests (M2), one per case:
- each of the four conditions failing alone, and the entry waits;
- all four holding, and the name is dropped and the entry merges;
- the two "keep waiting" cases: a commit status, and another App's check run.

**S5: nothing ruleset 2 requires falls outside the aggregate.** Head `6b25997d7` (#697's last head, merged) has 36
check runs and no commit status:
- 30 from `github-actions` (App 15368):
  - the 21 per-job contexts ruleset 2 requires, every one of them in `pull-request.needs`;
  - `pull-request` itself;
  - 8 "Build the … image (no push)" jobs from path-filtered workflows. Ruleset 2 does not require those today, and the
    queue waits for them anyway, because it waits for everything that reports (`CheckGate.cs:84-87`).
- 5 from `coverageproduction` (App 4574022): `coverage/patch` and `coverage/project` (both `neutral`), `Merge queue`,
  `PR size` and `Backport targets`.
- 1 from GitGuardian, which ruleset 2 does not require.

So the "required but not covered" list is empty, and A1 loses nothing. One correction to F5: the coverage checks are
check runs, not commit statuses. Evidence: GET `commits/6b25997d7…/check-runs` and `…/status`, 2026-11-27.

## 7. Milestones (one pull request)

> **Rewritten after the grilling round (2026-11-27).** The first version had the owner change rulesets 2 and 3 by hand
> in M1, a throttled re-read in M2, and no bell. Dec1–Dec12 replace that.

- **M0.** Done:
  - S1, S3 and S5 (§6.1);
  - Q1–Q5, answered as Dec1–Dec12 (§5.1);
  - S4, dropped (Dec12).

  S2 still needs the owner's approval, because it writes to GitHub.
- **M1. Spark.**
  - The D1 guard test, `tools/verify-pull-request-summary.test.mjs` (Dec12).
  - The `CLAUDE.md` CI-layout note: the rulesets require only `pull-request` and the App's own checks; renaming
    `pull-request` means running "Set up rulesets" again.
- **M2. What the queue records.** Every change here adds a property; none renames one.
  - `ForgeCheck.AppId`, from the webhook and from the backfill (Dec4).
  - `GetCheckNamesOnCommitAsync` returns `ForgeReportedCheck`. The entry stores the expected names' Apps and a
    "stuck since" time (Dec8, Dec9).
  - `ForgeBranchRules` keeps each ruleset's required checks with its name and id (C3), in the GitHub forge and in the
    fake.
- **M3. Engine.**
  - C1: the stuck state, held at two polls in a row.
  - C2: the rules are re-read at every poll while the entry is stuck (Dec7).
  - C3: the check run's title and summary, and the timeout message (Dec7).
  - Dec8: one comment per stuck entry, with D3's hint.
  - C4: a stale expected name is dropped by S3's rule (Dec9).
  - Drift is recorded on the repository: an entry stuck on a required name, or GitHub refusing a merge because of a pin
    (Dec5).
  - A "one check per job" flag is set when the rules are read at enable (Dec11).
  - Tests in `MergeQueueEngineTests` (the fake's `Rules` and `CheckStates` can change between polls), `CheckGateTests`
    and `GitHubForgeMergeQueueTests`.
- **M4. Setup.**
  - Dec3's shape: the App's own checks, plus one summary job picked in the form, with the help text.
  - Dec4's pins.
  - Dec6's prefill when the form is opened from a bell.
  - Dec10's change list (B1) and stale marks (B2).
  - Dec11's offer to switch.
  - The form's new field means running `--spark-synchronize-model`, and new strings in `translations.json` mean
    regenerating the golden in the same commit.
  - Tests in `MergeQueueRulesetPlannerTests` and `SetUpRulesetsActionTests`.
- **M5. Bell.**
  - At sign-in or when the app is opened, each manager of a repository with drift (Dec5), or with a per-job list
    (Dec11), gets one notification through `ISparkNotifier`, linking to "Set up rulesets".
  - Notifications are deduplicated per user, per drift and per repository.
  - A one-off scan of the repositories with a `mergeQueue` section runs after the deploy (Dec11).
  - A new endpoint, if one is needed, means updating `CsrfSurfaceTests` and the route snapshots.
- **M6. Docs.**
  - `merge-queue.md` §3: the stale lines 598-599, the summary-job shape, and a troubleshooting entry, "a required check
    never reports".
  - `onboarding.md` "Left to you": why a summary job is worth adding.
- **M7.** Run the touched test classes once, at the end. Then:
  - the owner deploys;
  - the owner follows the bell, or runs "Set up rulesets" on Spark, to switch rulesets 2 and 3 to `pull-request`,
    `coverage/patch` and `coverage/project` (plus `Line policy`), pinned;
  - the "after" half of S2 on SparkTest, if the owner approves it.

## 8. Until this ships: the workaround

From comment 2, with F3 and F4 added.
- When a pull request renames, joins or splits a required job, update ruleset 2 by hand to the new names first. Do the
  same for ruleset 3 when the change reaches the lines (F4). That only matters for a pull request against a line,
  because `/release` pushes past ruleset 3 (S1).
- Then `/dequeue` and `/queue` every pull request that is already queued. Its entry holds the old required list (F2)
  and the old expected list (F3).
- Other open pull requests: their finished CI carries the old names. The new name appears with their next CI run (a
  push, a re-run or the queue's rebase), because a `pull_request` run takes its workflows from the merge with the
  base.

## 9. Log

- **2026-11-27.** Read issue #714 and both comments. Ran three read-only investigations: the setup, the engine, and CI
  with the rulesets. Checked F1–F6 again against the code, the lines' `test.yml`, and the live rulesets 26719109 and
  27018553. Wrote this file.
- **2026-11-27, later.** Ran spikes S1, S3 and S5 (read-only, §6.1):
  - S1 found that `/release` is a direct push by the exempt App, so F4 has no deadline (marked superseded).
  - S5 found nothing ruleset 2 requires outside the aggregate, and corrected F5 (the coverage checks are check runs).
    It also gave the App ids behind A1's hardening.
  - S3 wrote C4's rule.
- **2026-11-27, grilling round.** The owner answered Q1–Q5 and the follow-ups Q1a–Q1f, Q2a and Q4b, recorded as
  Dec1–Dec12 (§5.1). Facts were checked along the way:
  - **The rulesets:** a live list of all five, and live reads of rulesets 2 and 3. Ruleset 2 was corrected at 07:59;
    ruleset 3 still matches the lines.
  - **The workflows:** the lines' `pull-request.yml`, which all have the summary job.
  - **The code:** the bell (`ISparkNotifier`), the setup's OAuth lease, the manager check, D62 (`Merge queue` can
    never be required) and the queue's comment sites.
  - **The plan:** §7 was rewritten to match.
- **2026-11-27, M1–M6 implemented** on `fix/issue714-required-check-renames`, in one pull request. Every stored change
  adds a property or a document; none renames one. The decisions made while building, with their evidence:
  - **Notices are documents, one per repository and kind** (`RulesetNotices/{repository}/{kind}`, kinds
    `MissingCheck`, `PinMismatch` and `SummaryJob`; `RulesetNotice.cs`). A drift raised again with other names gets a new
    `Key` and an empty `NotifiedUsers`, so each manager is told once per drift (Dec5's deduplication). "Set up rulesets"
    resolves the repository's notices when it applies, and the stuck queue resolves a `MissingCheck` itself when a
    re-read finds the rules fixed (C2). Evidence: `RulesetNoticesTests`.
  - **Delivery is a request from the client, not `ISparkSignedInHandler`.** That handler runs before the sign-in ticket
    is issued (`SparkAccountContracts.cs:47`), so `ISparkCurrentPrincipal` is still anonymous there and the manager check
    (`IDependencyOnboardingService.CheckAsync`, the same one "Set up rulesets" uses) cannot run. The handler is also meant
    to stay "a load by id, not a query" (`:53`). Instead `RulesetNoticesService` posts `POST api/me/ruleset-notices`
    whenever the auth state turns signed in. That covers both Dec5 moments, signing in and opening the app already signed
    in, and a refreshed user object asks nothing more. The endpoint loads the open notices first, and makes no forge call
    when none waits for this user. Evidence: `RulesetNoticeBellTests`, `ruleset-notices.service.spec.ts`; the route
    snapshot gained that one line, antiforgery required.
  - **A drift raised while the bell delivers is not lost.** The bell marks the user through a deferred patch that runs
    only when the notice's `Key` is still the one delivered. The notification and the mark commit in one `SaveChanges`.
    Evidence: `RulesetNoticeBellTests.A_drift_raised_while_the_bell_delivers_still_reaches_the_viewer`.
  - **⚠️ Deviation from Dec5's "Its link opens 'Set up rulesets'".** The link opens the account's page,
    `/{forge}/a/{owner}`. The bell's text says to select the repository and run "Set up rulesets". The reason: Spark has
    no URL that runs an action. "Set up rulesets" is a query action that needs a selected row, and it is enabled only on
    the `Account_Repositories` and `GetRepositories` lists (`RepositoryActions.cs:121-124`). Making the link run the action
    takes a new ng-spark feature, a route that selects rows and starts an action. That is left for the owner to decide.
  - **"Every repository with a `mergeQueue` section" (Dec11's post-deploy pass) is read as "the last dependency run
    found the merge queue enabled"** (`DependencyUpdateRun.FileMergeQueue == Enabled`, recorded at every `coverage.yml`
    read). The migration `M_202611271200_OfferSummaryJob` reads no file and calls no forge at startup. It queues one
    `OfferSummaryJobMessage` per such repository through `BroadcastOnceAsync`. The recipient reads the default branch's
    rules with the App's own token and raises the offer when they require one check per job. A disconnected or archived
    repository, or a forge that refuses, is skipped. This follows `M_202610151200_BackfillRepositoryKnownChecks`.
    Evidence: `SummaryJobOfferTests`.
  - **The bell's text is translated** in the viewer's language (`rulesets.bell.*` in `translations.json`, golden
    regenerated in the same commit).

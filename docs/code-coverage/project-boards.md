# Project boards

CodeCoverage moves the cards of a GitHub Projects board between its columns when an issue or pull request changes:
*when a pull request is merged, move it to Done*. The in-app guide is `/project-boards/setup` (#625), in the sidebar's
Guides group. The design is [`../coverage_project_automation_PRD.md`](../coverage_project_automation_PRD.md) and its
plan [`../coverage_project_automation_plan.md`](../coverage_project_automation_plan.md); the messaging decisions are
[`../decisions_messaging_and_project_automation.md`](../decisions_messaging_and_project_automation.md).

Paths below are relative to `apps/CodeCoverage/`.

## Setting it up

1. **App permissions** ([`hosting.md`](hosting.md)): Organization → Projects read & write, plus Issues, Pull requests
   and Checks; events Issues, Pull request, Pull request review, Issue comment and Check run. An organization owner has
   to accept a raised permission before GitHub sends anything for it, and nothing is logged until then.
2. **The board's columns** are the options of its single-select field named `Status`, matched case-insensitively
   (`CodeCoverage.GithubIntegration/Services/IInstallationProjects.cs`). A board without one has no columns.
3. **Discovery.** Boards are not added by hand. The reconciler finds them, up to 100 per owner, every night and on
   **Resync** (Home) (`CodeCoverage.GithubIntegration/Services/GitHubStateReconciler.cs`). They are listed on the
   account's page under **Project boards** (`Account_Projects`).
4. **Enable.** On the board, switch on **Automation Enabled**, then choose **Sync columns**
   (`CodeCoverage/CustomActions/SyncColumnsAction.cs`).
5. **Rules** are the board's **Events** (`EventColumnMapping`). Each rule has an Event, a Target Column and three
   switches, all on by default: **Enabled**, **Move Linked Issues** and **Add To Board**
   (`CodeCoverage.Library/Entities/EventColumnMapping.cs`). A board has at most one rule per event; a duplicate is
   refused on save (`CodeCoverage/Actions/GitHubProjectActions.cs`). A rule applies to every repository of the board's
   owner.

There are no `coverage.yml` keys for this feature.

## Events

The eighteen events are `WebhookEventType.Items` (`CodeCoverage.Library/LookupReferences/WebhookEventType.cs`):

| Group | Events | Moves |
|---|---|---|
| Issues | opened, closed, reopened, labeled, unlabeled, assigned, unassigned | the issue's card |
| Pull requests | opened, closed without merging, merged, ready for review, converted to draft, review requested | the PR's card, and with Move Linked Issues the issues it closes |
| Reviews | approved, changes requested, dismissed | as pull requests |
| Checks | check run completed | the PRs listed on the check run (none for forks), as pull requests |
| Comments | issue comment created | the commented issue's or PR's card |

- Merged and closed-without-merging share `pull_request.closed` and are told apart by `merged`; approved and
  changes-requested share `pull_request_review.submitted` and are told apart by the review state
  (`CodeCoverage/Recipients/ProjectAutomationRecipient.cs`).
- The issues a PR closes are GitHub's `closingIssuesReferences`: closing keywords and the Development sidebar.
- Deliveries this App caused are dropped before any rule runs: its own check runs (`check_run.app.id`) and resources
  marked `performed_via_github_app` with its id (`CodeCoverage/Recipients/ProjectAutomationRouter.cs`). Without
  `GitHub:{Env}:AppId` nothing is dropped, and a warning is logged.

## Column changes

GitHub sends no event when a `Status` option is renamed, added or deleted, for either owner type (PRD §3). The cached
columns are corrected only by the nightly reconciliation or by **Sync columns**, which names the enabled rules whose
column no longer exists.

## Troubleshooting

- Each rule records **Last Fired** (`LastFiredAtUtc`), and **Error** / **Failed At** (`LastError`, `LastErrorAtUtc`),
  which clear on the next run without an error. A delivery is dead-lettered only when every matching rule failed.
- *Target column … no longer exists*: run **Sync columns** and point the rule at an existing column.
- *no cached Status field*: the board has no `Status` field, or its columns were never synchronized.
- With **Add To Board** off, a card that is not on the board is skipped without an error.
- A board the App can no longer reach is marked disconnected (`DisconnectedReason` on the document) and hidden from the
  account's list (`CodeCoverage.GithubIntegration/Services/GitHubProjectVisibility.cs`).
- Logs: `Rule {EventType} on board #N ({Name}): {Outcome}` at Information. Messages go through the
  `coverage-project-automation` queue.
- Boards owned by a personal account: the App's permission is the organization Projects permission, and card moves on a
  user-owned board are not verified (`IInstallationProjects.cs`).

## Who sees what

There is no public tier. Reading a board needs an owner you are allowed to see; editing it and **Sync columns** need an
owner you manage (`GitHubProjectActions.cs`, `App_Data/security.json`).

# Screenshots for the "Optimal GitHub setup" page

The page `/merge-queue/setup` (`src/app/pages/merge-queue-setup/`) shows the PNG files below. The build copies this
folder to `/merge-queue-setup/` and leaves this README out (`ignore` in `project.json`). Until a file exists, or when it
fails to load, the page shows a "Screenshot pending" box of the same size instead of a broken image.

Each image is rendered as `<img loading="lazy" width="1280" height="720">`, so every capture must be **1280×720**: set
the viewport to 1280×720 and capture the viewport, not the full page.

## How to capture (docs/coverage_merge_queue_PRD.md O13)

- Use the `playwright_node` MCP. The GitHub screens need a signed-in GitHub session: the owner signs in in that
  browser, so credentials never pass through the assistant.
- Use a neutral demo repository, with the GitHub UI in its light theme.
- Before each capture, run a `browser_evaluate` that blanks out identifying content: account and organization names,
  avatars, email addresses, and every repository name other than the demo repository's.
- Scroll so the setting the step is about is fully in view.

## Files

| File | Step | Screen | What it must show |
|---|---|---|---|
| `accept-permissions.png` | 1 | GitHub: the organization's Settings → GitHub Apps → the CodeCoverage installation → *Review request* | The App's request for its new permissions: Actions and Workflows *Read and write*, Commit statuses *Read-only*, and the *Accept new permissions* button. |
| `native-merge-queue-off.png` | 2 | GitHub: Settings → Rules → Rulesets → a branch ruleset of the demo repository, its *Branch rules* | *Require merge queue* unticked. |
| `squash-merging.png` | 2 | GitHub: Settings → General → *Pull Requests* | *Allow squash merging* ticked. |
| `ruleset-1-bypass.png` | 3 | GitHub: ruleset 1 ("Only the merge queue updates the default branch"), its *Bypass list* | The CodeCoverage App set to *Exempt*, and the *Repository admin* role set to *For pull requests only*. |
| `ruleset-1-rules.png` | 3 | GitHub: ruleset 1, *Target branches* and *Branch rules* | *Default* branch targeted; *Restrict updates* ticked; *Require a pull request*, *Require status checks* and *Require merge queue* unticked. |
| `ruleset-2-pull-request.png` | 4 | GitHub: ruleset 2 ("Reviews and checks"), *Require a pull request before merging* expanded | The required approvals; *Dismiss stale pull request approvals when new commits are pushed* and *Require approval of the most recent reviewable push* both unticked; *Squash* among the allowed merge methods. |
| `ruleset-2-status-checks.png` | 4 | GitHub: ruleset 2, *Require status checks to pass* expanded | The demo repository's CI checks listed (no "Merge queue" check) and *Require branches to be up to date before merging* ticked. |
| `allow-edits-by-maintainers.png` | 7 | GitHub: a pull request from a user-owned fork into the demo repository, its sidebar | *Allow edits by maintainers* ticked. |
| `account-merge-queue-settings.png` | 8 | CodeCoverage: the demo account's organization page (`/github/a/<login>`), signed in as its owner | The *Merge queue* group: *Merge queue approval floor*, *Checks the merge queue ignores* and the bypass role. |

A new screenshot needs three things: its file here, a row in this table, and an entry in the component's `shots`
(the spec counts the images and checks each one's size, lazy loading and description).

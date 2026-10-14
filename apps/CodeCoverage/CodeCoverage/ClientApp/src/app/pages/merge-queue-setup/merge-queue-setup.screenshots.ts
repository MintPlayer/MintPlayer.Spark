/*
 * The screenshots of the "Optimal GitHub setup" page (/merge-queue/setup), by name.
 *
 * They are not files in this repository (merge-queue PRD O23). Each one is a GitHub attachment: captured into the
 * git-ignored `.playwright-mcp/merge-queue-setup/<name>.png`, then uploaded through the browser into a pull request's
 * description box, which turns it into a public `https://github.com/user-attachments/assets/<id>` URL. That URL goes
 * into MERGE_QUEUE_SETUP_SCREENSHOTS below, and nowhere else in the app. Keep the form the description box inserts:
 * the address a rendered image is then served from can carry a token that expires (the spec holds every URL to it).
 *
 * Until a screenshot has a URL, the page shows a "Screenshot pending" box of the same size in its place and requests
 * nothing. An image that fails to load becomes the same box.
 *
 * Capturing one (O13): the playwright_node MCP with the viewport set to 1280×720, capturing the viewport and not the
 * full page (the page declares that size); GitHub in its light theme, on a neutral demo repository; and before each
 * capture, a `browser_evaluate` that blanks out account and organization names, avatars, email addresses and every
 * repository name other than the demo repository's.
 */

/** A screenshot of the page. The component's `shots` gives each one its description and caption. */
export type MergeQueueSetupScreenshot =
  | 'accept-permissions'
  | 'native-merge-queue-off'
  | 'squash-merging'
  | 'ruleset-1-bypass'
  | 'ruleset-1-rules'
  | 'ruleset-2-pull-request'
  | 'ruleset-2-status-checks'
  | 'allow-edits-by-maintainers'
  | 'account-merge-queue-settings';

/**
 * Each screenshot's `https://github.com/user-attachments/assets/<id>` URL, or `undefined` while it is pending. The
 * comment above each entry is the screen to capture and what it must show.
 */
export const MERGE_QUEUE_SETUP_SCREENSHOTS: Record<MergeQueueSetupScreenshot, string | undefined> = {
  // Step 1. GitHub: the organization's Settings → GitHub Apps → the CodeCoverage installation → Review request. The
  // App's request for its new permissions (Actions and Workflows "Read and write", Commit statuses "Read-only") and
  // the "Accept new permissions" button.
  'accept-permissions': undefined,

  // Step 2. GitHub: Settings → Rules → Rulesets → a branch ruleset of the demo repository, its Branch rules.
  // "Require merge queue" unticked.
  'native-merge-queue-off': undefined,

  // Step 2. GitHub: Settings → General → Pull Requests. "Allow squash merging" ticked.
  'squash-merging': undefined,

  // Step 3. GitHub: ruleset 1 ("Only the merge queue updates the default branch"), its Bypass list. The CodeCoverage
  // App set to "Exempt", and the Repository admin role set to "For pull requests only".
  'ruleset-1-bypass': undefined,

  // Step 3. GitHub: ruleset 1, its Target branches and Branch rules. The default branch targeted; "Restrict updates"
  // ticked; "Require a pull request", "Require status checks" and "Require merge queue" unticked.
  'ruleset-1-rules': undefined,

  // Step 4. GitHub: ruleset 2 ("Reviews and checks"), "Require a pull request before merging" expanded. The required
  // approvals; "Dismiss stale pull request approvals when new commits are pushed" and "Require approval of the most
  // recent reviewable push" both unticked; Squash among the allowed merge methods.
  'ruleset-2-pull-request': undefined,

  // Step 4. GitHub: ruleset 2, "Require status checks to pass" expanded. The demo repository's CI checks, without the
  // "Merge queue" check, and "Require branches to be up to date before merging" ticked.
  'ruleset-2-status-checks': undefined,

  // Step 7. GitHub: a pull request from a user-owned fork into the demo repository, its sidebar. "Allow edits by
  // maintainers" ticked.
  'allow-edits-by-maintainers': undefined,

  // Step 8. CodeCoverage: the demo account's organization page (/github/a/<login>), signed in as its owner. The Merge
  // queue group: "Merge queue approval floor", "Checks the merge queue ignores" and "Merge queue bypass role".
  'account-merge-queue-settings': undefined,
};

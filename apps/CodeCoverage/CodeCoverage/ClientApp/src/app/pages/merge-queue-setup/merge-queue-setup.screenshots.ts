/*
 * The screenshots of the "Optimal GitHub setup" page (/merge-queue/setup), by name.
 *
 * They are not files in this repository (merge-queue PRD O23). Each one is a GitHub attachment: captured into the
 * git-ignored `.playwright-mcp/merge-queue-setup/<name>.png`, then uploaded through the browser into a pull request's
 * description box, which turns it into a `https://github.com/user-attachments/assets/<id>` URL. GitHub publishes it once
 * that pull request is created; until then an anonymous request for it answers 404. That URL goes into
 * MERGE_QUEUE_SETUP_SCREENSHOTS below, and nowhere else in the app. Keep the form the description box inserts: the
 * address a rendered image is then served from can carry a token that expires (the spec holds every URL to it).
 *
 * Until a screenshot has a URL, the page shows a "Screenshot pending" box in its place and requests nothing. An image
 * that fails to load, an unpublished one included, becomes the same box, at the image's size.
 *
 * How they were captured (O13), with the playwright_node MCP:
 * - GitHub, and CodeCoverage, in the dark theme.
 * - A GitHub screen is a capture of the 1280×720 viewport, not of the full page.
 * - A part of a CodeCoverage page is a capture of that element alone, at its own size: the organization page's Merge
 *   queue group, and its permissions notice.
 * - Before each capture, a `browser_evaluate` scrubbed the page's DOM: account and organization names, avatars, email
 *   addresses, ids and repository names.
 */

/** A screenshot of the page. The component's `shots` gives each one its description and caption. */
export type MergeQueueSetupScreenshot =
  | 'accept-permissions'
  | 'app-pending-permissions-banner'
  | 'native-merge-queue-off'
  | 'squash-merging'
  | 'ruleset-1-bypass'
  | 'ruleset-1-rules'
  | 'ruleset-2-pull-request'
  | 'ruleset-2-status-checks'
  | 'allow-edits-by-maintainers'
  | 'account-merge-queue-settings';

/**
 * A screenshot's GitHub attachment, and the image's own size in pixels. The page declares that size as the image's
 * `width` and `height`, so its box is reserved before it loads.
 */
export interface MergeQueueSetupAttachment {
  url: string;
  width: number;
  height: number;
}

/**
 * Each screenshot's attachment, or `undefined` while it is pending. The comment above each entry is the screen that was
 * captured and what it shows.
 */
export const MERGE_QUEUE_SETUP_SCREENSHOTS: Record<MergeQueueSetupScreenshot, MergeQueueSetupAttachment | undefined> = {
  // Step 1. GitHub: the organization's Settings → GitHub Apps → the CodeCoverage installation → Review request. "The
  // CodeCoverage app by <organization> is requesting updated permissions", the requested permission marked "New
  // request", and the "Accept new permissions" button. The page lists whatever the App is requesting at the time; this
  // capture holds a test request (Discussions, read-only), so it illustrates the page, not the queue's own permissions.
  'accept-permissions': {
    url: 'https://github.com/user-attachments/assets/558c6563-d0e3-43b9-abf3-2dd29914a7f3',
    width: 1280,
    height: 720,
  },

  // Step 1. CodeCoverage: an organization page, signed in as its owner, while that request is pending. The notice alone:
  // "GitHub is waiting for you to accept new permissions: …", "Review and accept on GitHub" and "Merge queue setup guide".
  'app-pending-permissions-banner': {
    url: 'https://github.com/user-attachments/assets/813f0779-6f7f-464b-a46a-b30971ab57da',
    width: 953,
    height: 83,
  },

  // Step 2. GitHub: Settings → Rules → Rulesets → a branch ruleset, its Branch rules. "Require merge queue" unticked.
  'native-merge-queue-off': {
    url: 'https://github.com/user-attachments/assets/d11824a1-0040-4dd3-bf67-472bcaa84f62',
    width: 1280,
    height: 720,
  },

  // Step 2. GitHub: Settings → General → Pull Requests. "Allow squash merging" ticked.
  'squash-merging': {
    url: 'https://github.com/user-attachments/assets/f20b7b2d-bcff-4a93-8fa2-ea95545f13ad',
    width: 1280,
    height: 720,
  },

  // Step 3. GitHub: ruleset 1, its Bypass list and Target branches. The Repository admin role on "Allow for pull requests
  // only" and the CodeCoverage App on "Exempt"; the default branch targeted; "Restrict updates" ticked below them.
  'ruleset-1-bypass': {
    url: 'https://github.com/user-attachments/assets/1f80bbe3-b7c5-4ed8-a4e5-58d8e29b5dfd',
    width: 1280,
    height: 720,
  },

  // Step 3. GitHub: ruleset 1, its Branch rules. "Restrict updates" ticked and every other rule unticked, "Require a pull
  // request before merging" and "Require status checks to pass" among them. GitHub lists no "Require merge queue" here.
  'ruleset-1-rules': {
    url: 'https://github.com/user-attachments/assets/5b381bec-f411-4fda-bdfb-70217b20bf28',
    width: 1280,
    height: 720,
  },

  // Step 4. GitHub: ruleset 2 ("Reviews and checks"), "Require a pull request before merging" expanded. One required
  // approval; "Dismiss stale pull request approvals when new commits are pushed" and "Require approval of the most
  // recent reviewable push" both unticked; Squash the allowed merge method. The Copilot extra-approval setting keeps
  // GitHub's default.
  'ruleset-2-pull-request': {
    url: 'https://github.com/user-attachments/assets/d0bcd270-53c6-4841-b47d-8d5702da77f1',
    width: 1280,
    height: 720,
  },

  // Step 4. GitHub: ruleset 2, "Require status checks to pass" expanded. "Require branches to be up to date before
  // merging" ticked, and the repository's CI check required, without the "Merge queue" check.
  'ruleset-2-status-checks': {
    url: 'https://github.com/user-attachments/assets/8d8fbe19-6195-4325-9131-3698d0b1773c',
    width: 1280,
    height: 720,
  },

  // Step 7. GitHub: the form that opens a pull request from a user-owned fork. "Allow edits by maintainers" ticked beside
  // "Create pull request".
  'allow-edits-by-maintainers': {
    url: 'https://github.com/user-attachments/assets/c35a720c-0704-4cd0-89c0-c8d9c41b27b9',
    width: 1280,
    height: 720,
  },

  // Step 8. CodeCoverage: an organization page (/github/a/<login>), signed in as its owner. The Merge queue group alone,
  // at its defaults: "Merge queue approval floor" on Approval for external authors, "Checks the merge queue ignores"
  // empty, and "Merge queue bypass role" on Nobody.
  'account-merge-queue-settings': {
    url: 'https://github.com/user-attachments/assets/41dc6e47-3634-4d83-935d-1b20f48a08a7',
    width: 896,
    height: 236,
  },
};

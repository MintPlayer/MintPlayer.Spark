/*
 * The code blocks of the "Optimal GitHub setup" page (/merge-queue/setup), apart from the component so that each one
 * reads as the file it shows.
 *
 * ⚠️ Two are copies, so change them together with their source. merge-queue-setup.snippets.spec.ts reads both sources
 * from disk and fails when a copy falls behind:
 * - REGENERATE_WORKFLOW is docs/code-coverage/templates/regenerate.yml, byte for byte, without its final newline.
 * - COVERAGE_YML is the `mergeQueue:` section of the repository's own coverage.yml, with the plan's decision numbers
 *   taken out of its comments (they mean nothing in someone else's repository). Its keys and values are the same.
 */

/** What a Spark repository needs in `coverage.yml` on its default branch. */
export const COVERAGE_YML = `mergeQueue:
  enabled: true
  branches: [master]
  mergeMethod: squash
  enqueue:
    label: merge-queue
  # .github/workflows/regenerate.yml: builds every app and runs synchronize, returning a patch artifact.
  postRebaseActionName: regenerate.yml
  postRebaseTimeout: 20m
  postRebaseWhen: auto-resolved
  # The only paths the regenerate patch may touch. Synchronize writes nothing else.
  regeneratedPaths:
    - "**/App_Data/modelHashes.json"
    - "**/App_Data/securityPosture.txt"
    - "**/App_Data/translations.json"
    - "**/App_Data/security.json"
    - "**/App_Data/Model/*.json"
  # Wholly generated files only: a conflict anywhere in them takes the base side and the
  # regenerate step rewrites them. A conflict in any other file evicts the pull request.
  autoResolve:
    - path: "**/App_Data/modelHashes.json"
      strategy: take-base
    - path: "**/App_Data/securityPosture.txt"
      strategy: take-base`;

/** The queue does not create its label. */
export const CREATE_LABEL = 'gh label create merge-queue';

/**
 * Ruleset 1: only the App updates the branch. The App is exempt; Repository admin (GitHub's built-in role 5) may
 * merge by hand through a pull request, never by a direct push (merge-queue PRD O9, O21 B).
 */
export const RULESET_1 = `{
  "name": "Only the merge queue updates the default branch",
  "target": "branch",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["~DEFAULT_BRANCH"], "exclude": [] } },
  "rules": [{ "type": "update", "parameters": { "update_allows_fetch_and_merge": false } }],
  "bypass_actors": [
    { "actor_id": <APP_ID>, "actor_type": "Integration", "bypass_mode": "exempt" },
    { "actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "pull_request" }
  ]
}`;

/**
 * Ruleset 2: the reviews and checks, which hold the App too. Both approval settings off (O18); no bypass actor in the
 * sample, and never the App (O22).
 */
export const RULESET_2 = `{
  "name": "Reviews and checks",
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
}`;

export const CREATE_RULESETS = `gh api apps/<app-slug> --jq .id                                       # <APP_ID>
gh api --method POST repos/<owner>/<repo>/rulesets --input ruleset-1.json
gh api --method POST repos/<owner>/<repo>/rulesets --input ruleset-2.json
gh api repos/<owner>/<repo>/rules/branches/<branch>                   # what now applies to the branch`;

/** Pushes to the queue's mq/ branches start push workflows only; an unfiltered push trigger must skip them (D14, D37). */
export const PUSH_TRIGGER = `on:
  push:
    branches-ignore: ['mq/**']
  pull_request:`;

/** docs/code-coverage/templates/regenerate.yml. */
export const REGENERATE_WORKFLOW = `# Template: the merge queue's post-rebase workflow for a Spark repository.
# Copy to .github/workflows/regenerate.yml on the default branch, list your apps under \`projects:\`, and set
# \`postRebaseActionName: regenerate.yml\` in coverage.yml's \`mergeQueue:\` section.
#
# The CodeCoverage merge queue dispatches this workflow on its mq/<base>/pr-<n> branch after a rebase it
# resolved. The run regenerates Spark's generated files (modelHashes.json, securityPosture.txt, ...) and
# uploads them as a patch. The queue validates the patch against \`regeneratedPaths\` and pushes it itself:
# this workflow never commits or pushes.
#
# ┌─ SECURITY ─────────────────────────────────────────────────────────────────────────────────────────┐
# │ This workflow BUILDS AND RUNS UNTRUSTED PULL-REQUEST CODE, in the context of your repository.      │
# │  - Never add a secret, an environment, or \`\${{ secrets.* }}\` of any kind.                          │
# │  - Never widen \`permissions:\`; never add \`id-token: write\`.                                        │
# │  - Never run it on a self-hosted runner: the PR's build code would run on your machine.            │
# │  - Keep \`persist-credentials: false\`, so the job token is not left in .git/config for the build.   │
# │ The queue never dispatches this workflow for a PR that changes anything under .github/, unless     │
# │ the repository's bypass role lets a same-repository PR through (a fork's never). Before every      │
# │ dispatch it checks that the mq/ branch's copy of this file equals the default branch's, because a  │
# │ dispatched run uses the workflow file from the dispatched branch. That check is never bypassed.    │
# │ For a fork's PR it pushes to mq/ only while this file keeps the rules above and no workflow runs   │
# │ on a push to mq/: give every \`on: push\` a \`branches-ignore: ['mq/**']\`.                            │
# └────────────────────────────────────────────────────────────────────────────────────────────────────┘

name: regenerate

on:
  workflow_dispatch:
    inputs:
      expected_sha:
        description: The commit the merge queue prepared. The run fails if the branch head differs.
        required: true
      pr_number:
        description: The pull request being merged (shown in the run name only).
        required: true
      queue_run_id:
        description: The merge queue's id for this attempt (shown in the run name only).
        required: true

run-name: regenerate PR #\${{ inputs.pr_number }} (\${{ inputs.queue_run_id }})

permissions:
  contents: read

concurrency:
  group: regenerate-\${{ github.ref }}
  cancel-in-progress: true

jobs:
  regenerate:
    runs-on: ubuntu-latest
    timeout-minutes: 20
    steps:
      - uses: actions/checkout@v7
        with:
          # The dispatched mq/ branch as of the dispatch. The action compares it with expected_sha.
          ref: \${{ github.sha }}
          persist-credentials: false

      # Pin to a full commit SHA of MintPlayer/MintPlayer.Spark (Dependabot's github-actions ecosystem
      # keeps it current). Never @master: that repository's default branch moves many times a day for
      # reasons unrelated to this action.
      - uses: MintPlayer/MintPlayer.Spark/.github/actions/spark-regenerate@<commit-sha>
        with:
          expected_sha: \${{ inputs.expected_sha }}
          projects: |
            src/MyApp/MyApp.csproj`;

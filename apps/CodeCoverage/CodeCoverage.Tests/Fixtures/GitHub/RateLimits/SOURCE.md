# GitHub rate-limit / permission fixtures (spike SP4, 2026-10-16)

Copied flat into this folder on 2026-10-16 from the spike folders named below; only `a1`, `a2`, `b1`, `b2` and `c1` were
kept. Used by `Services/GitHubRateLimitTests.cs` through `Fixture.Http`.

All `.http` files are raw `curl -i` / `gh api -i` output (status line, response headers, body).
Scrubbed: the laptop's public IP in the primary-limit message replaced by `203.0.113.7` (TEST-NET-3).
Token scan (`gh[pousr]_`, `github_pat_`, `Bearer`, `Authorization:`) over this folder: no hits.

## primary-unauthenticated/ — REAL, captured 2026-10-16T18:27:41Z
- `exhaust.sh` (one level up): 60 unauthenticated `GET /users/octocat` until the 61st returned 403 (`a-loop.log`).
- `a1-primary-exhausted-rest.http`: `403 rate limit exceeded`, `X-RateLimit-Remaining: 0`, `X-RateLimit-Reset`,
  `X-RateLimit-Resource: core`, **no `Retry-After`**, body `API rate limit exceeded for <ip>. ...`.
- `a2-primary-exhausted-trees.http`: same for `GET /repos/MintPlayer/MintPlayer.Spark/git/trees/master` — the
  endpoint `GetTreeAsync` calls; shows a tree read gets a 403, not a 404, when throttled.
- `a0`/`a3`: `GET /rate_limit` before and after (does not count against the quota).
- An installation token's message reads `API rate limit exceeded for installation ID <n>.` (shape already used
  in `GitHubForgeMergeQueueTests.cs:408`); the status/headers are the same family.

## permission-403/ — REAL, captured 2026-10-16 with `gh api -i` (authenticated user token, not printed)
- `b1-collaborators-no-push.http`: `GET /repos/torvalds/linux/collaborators` → `403 Forbidden`,
  `X-Ratelimit-Remaining: 4997` (non-zero), body `Must have push access to view repository collaborators.`
- `b2-org-admin-endpoint.http`: `GET /orgs/github/actions/permissions` → `403`, `X-Accepted-Oauth-Scopes: admin:org`,
  body `You must be an org admin or have the actions policies fine-grained permission.`
- Discriminator vs rate limit: remaining > 0, no `Retry-After`, message has no "rate limit".

## secondary-403/ — NOT captured by us (deliberately not provoked)
- `c1-secondary-search-no-retry-after.http`: reconstructed verbatim from a real log posted in
  https://github.com/changesets/action/issues/192 (comment by varl, 2022-09-29): `403`, `x-ratelimit-remaining: 30`
  (NOT 0), **no `retry-after`**, body `You have exceeded a secondary rate limit. Please wait a few minutes before
  you try again.` Newer responses append ` If you reach out to GitHub Support for help, please include the request ID ...`.
- GitHub docs (https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api): primary and
  secondary limits return **403 or 429**; honour `retry-after` when present; else if `x-ratelimit-remaining: 0`
  wait until `x-ratelimit-reset`; else wait **at least one minute**, exponentially increasing on repeats.
- Repo precedent: `GitHubForgeMergeQueue.cs:1180` catches Octokit's `SecondaryRateLimitExceededException`.

## GraphQL note
GraphQL primary-limit exhaustion is HTTP 200 with `{"errors":[{"type":"RATE_LIMITED",...}]}`
(fixture shape in `GitHubForgeMergeQueueTests.cs:918`, not captured here).

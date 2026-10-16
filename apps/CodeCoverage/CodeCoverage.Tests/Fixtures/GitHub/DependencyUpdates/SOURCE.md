# GitHub read responses for `GitHubForgeDependencyUpdatesTests` (re-captured 2026-10-16, dependency-updates PRD §13.13)

REAL bodies, captured with `gh api -i` (the owner's user token, held by `gh`, never printed) right after the S18 run,
against the public scratch repository the run used. The bodies were then scrubbed by a script: the owner's login and the
repository name replaced by the tests' placeholders `acme` / `widget`, e-mail addresses by `someone@example.invalid`,
avatar URLs by a placeholder; gh's trailing `gh: …` line dropped. A token scan (`gh[pousr]_…`, `github_pat_`) found nothing.
Ids, shas, node ids, timestamps and every field name are GitHub's own.

| File | Request |
|---|---|
| `pulls-open-by-head.json` | `GET /repos/{o}/{r}/pulls?state=all&head={o}:mintplayer/dependencies` (the rolling PRs #2 and #3, by `coverageproduction[bot]`) |
| `pull-3.json` | `GET /repos/{o}/{r}/pulls/3` (the scheduled rolling PR the merge queue merged) |
| `ref-tag-lightweight.json` | `GET /repos/actions/checkout/git/ref/tags/v4.2.0` |
| `ref-tag-dependency-update-v1.json` | `GET /repos/MintPlayer/MintPlayer.Spark/git/ref/tags/dependency-update-v1` (the onboarding pin, lightweight) |
| `ref-tag-annotated-node.json`, `tag-object-node.json` | `GET /repos/nodejs/node/git/ref/tags/v20.0.0` → `type: tag`, then `GET /repos/nodejs/node/git/tags/{sha}` (an annotated tag: no annotated tag exists in the scratch repository) |
| `contents-file.json` | `GET /repos/{o}/{r}/contents/coverage.yml?ref=master` (the file onboarding PR #1 wrote) |
| `contents-dir.json` | `GET /repos/{o}/{r}/contents/.github?ref=master` (a directory answers an array) |
| `contents-missing.json` | `GET /repos/{o}/{r}/contents/nope.yml?ref=master` → 404 |
| `run-artifacts.json` | `GET /repos/{o}/{r}/actions/runs/{id}/artifacts` of a dependency workflow run |

**Still docs-derived** (write endpoints, not re-captured: provoking them again would write to the repository): the bodies of
`POST …/pulls`, `PATCH …/pulls/{n}`, `POST …/issues/comments/{id}/reactions`, `POST`/`PATCH …/check-runs`, and the
artifact zip download. They stay inline in the tests, marked "docs:".

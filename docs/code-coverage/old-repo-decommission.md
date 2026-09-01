# Handoff — decommission `MintPlayer/CodeCoverage`

**Status:** pending, owner action · **Raised by:** [coverage_monorepo_plan.md](../coverage_monorepo_plan.md) M16

The application's source now lives at `apps/CodeCoverage` in this repository, and its docs at
[`docs/code-coverage/`](README.md). The old repository still holds a full copy of both, plus the
upload action.

Nothing in this workspace depends on it any more (`grep -rn "MintPlayer/CodeCoverage" .github/`
returns nothing), so it can be emptied — but **not yet**, and the order matters.

## Do not start until all three are true

1. **This PR is merged**, so the code exists on `master` here.
2. **`mintplayer-ng-bootstrap` has been repointed** at the action's new path — see
   [`ng-bootstrap-action-path.md`](ng-bootstrap-action-path.md). Until then the old repo is still
   serving `MintPlayer/CodeCoverage/action@master` to it, and archiving would break its uploads.
3. **One deploy has succeeded from this repository.** `code-coverage-deploy.yml` publishes the same
   image name the VPS already pulls, but that path has never run. If it fails, the old repo's
   `publish.yml` is the working fallback — and only while the old repo is intact.

## Then

1. Strip the repository to a README pointing at
   `MintPlayer/MintPlayer.Spark/tree/master/apps/CodeCoverage`, and remove `action/` (it moved).
2. Disable its workflows before archiving, or the last scheduled run may deploy a stale image over
   the one this repo just published.
3. Archive it. Do not delete: the history is the origin of four documents that are now stubs here,
   and each names the old path.

## Deliberately not transferred

Twenty-one merged PRs and the issue history stay where they are. Transferring issues is a manual
GitHub operation with no automation here, and the archived repository keeps them readable.

## What "emptied" does not mean

The RavenDB database is still called `Coverage` and the published image is still
`ghcr.io/mintplayer/codecoverage`. Neither is a leftover to tidy up: the first is production data
that would need a migration, the second is what the VPS pulls. See the note in `CLAUDE.md`.

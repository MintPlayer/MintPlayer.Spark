# Handoff — repoint `mintplayer-ng-bootstrap` at the moved coverage action

**Status:** pending · **Raised by:** [coverage_monorepo_plan.md](../coverage_monorepo_plan.md) M7 ·
**Target repo:** `C:\Repos\mintplayer-ng-bootstrap`

## Why

The coverage upload action has moved out of `MintPlayer/CodeCoverage`, which is being
decommissioned. It now lives in **`MintPlayer/github-actions`** alongside the other actions —
[PR #5](https://github.com/MintPlayer/github-actions/pull/5) — rather than travelling into
`MintPlayer.Spark` with the server application.

`mintplayer-ng-bootstrap` is the only consumer outside the two MintPlayer repos already updated.

## The change

Two lines, both a straight substitution. Note the ref is **`@main`**, not `@master`:
`github-actions`' default branch is `main`.

| File | Line | From | To |
|---|---|---|---|
| `.github/workflows/publish-master.yml` | 96 | `uses: MintPlayer/CodeCoverage/action@master` | `uses: MintPlayer/github-actions/coverage-upload@main` |
| `.github/workflows/pull-request.yml` | 125 | `uses: MintPlayer/CodeCoverage/action@master` | `uses: MintPlayer/github-actions/coverage-upload@main` |

Nothing else changes. All 15 inputs and 26 outputs keep their names, and the `url:`, `token:`,
`flags:`, `partial:` and `base-sha:` arguments already in those workflows stay exactly as they are.
The bundle was rebuilt during the port and its 35 tests pass, converted from vitest to jest.

## Sequencing

Apply this **after** [github-actions#5](https://github.com/MintPlayer/github-actions/pull/5) is on
`main`, and **before** `MintPlayer/CodeCoverage` is archived. Until the old repository is archived
the existing pin keeps resolving, so there is no window where uploads fail — but the order matters,
because pointing at `@main` before that PR merges resolves to nothing.

The change cannot be made from a session rooted in the Spark repo, so it needs one rooted in
`C:\Repos\mintplayer-ng-bootstrap`.

## Verifying it landed

On the next push to that repo:

- the workflow resolves the action (a bad path fails fast with
  *"Can't find 'action.yml' … in 'MintPlayer/github-actions/coverage-upload'"*)
- the upload step reports a build id, and the run appears at
  `https://coverage.mintplayer.com/r/MintPlayer/mintplayer-ng-bootstrap`

`grep -rn "MintPlayer/CodeCoverage" .github/` in that repo must return nothing.

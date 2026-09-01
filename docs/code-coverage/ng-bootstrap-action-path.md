# Handoff — repoint `mintplayer-ng-bootstrap` at the moved coverage action

**Status:** pending · **Raised by:** [coverage_monorepo_plan.md](../coverage_monorepo_plan.md) M7 ·
**Target repo:** `C:\Repos\mintplayer-ng-bootstrap`

## Why

The coverage upload action moved out of `MintPlayer/CodeCoverage` and into the Spark monorepo when
the Coverage app was absorbed. The old repository is being emptied and archived, so the pinned path
stops resolving.

`mintplayer-ng-bootstrap` is the only consumer outside the Spark repo. Spark's own two workflows
already switched to the in-repo relative path (`./apps/CodeCoverage/action`), which needs no
published action at all.

## The change

Two lines, both a straight substitution:

| File | Line | From | To |
|---|---|---|---|
| `.github/workflows/publish-master.yml` | 96 | `uses: MintPlayer/CodeCoverage/action@master` | `uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@master` |
| `.github/workflows/pull-request.yml` | 125 | `uses: MintPlayer/CodeCoverage/action@master` | `uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@master` |

Nothing else changes: the action's `action.yml` is byte-identical, all 14 inputs and 25 outputs keep
their names, and the committed `dist/index.js` was rebuilt during the move and verified unchanged.
The `url:`, `token:`, `flags:`, `partial:` and `base-sha:` arguments already in those workflows stay
exactly as they are.

## Sequencing

Apply this **before** `MintPlayer/CodeCoverage` is archived, and ideally before the Spark PR merges,
so there is no window where ng-bootstrap's uploads fail. Until then the old path keeps working — the
action is still present in the old repository; the Spark PR does not delete it.

The change cannot be made from a session rooted in the Spark repo, so it needs a session rooted in
`C:\Repos\mintplayer-ng-bootstrap`. It is not a separate unit of work: per the one-PR rule it belongs
to the same change set as the migration, and this file exists only because the write has to happen
from a different working directory.

## Verifying it landed

After the edit, on the next push to that repo:

- the workflow resolves the action (a bad path fails fast with
  *"Can't find 'action.yml' … in 'MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action'"*)
- the upload step reports a build id, and the run appears at
  `https://coverage.mintplayer.com/r/MintPlayer/mintplayer-ng-bootstrap`

`grep -rn "MintPlayer/CodeCoverage" .github/` in that repo must return nothing.

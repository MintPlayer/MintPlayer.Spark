# Source: S13 apply spike, yarn

Generated 2026-10-15 by `spikes/s13/run-yarn2.sh, run-js-conflicts.sh` (sample repo built under `spikes/s13/work/`, one base commit, one pinned bump per case).
Each `*.diff` is `git diff` (untracked files included via `git add -N`) after the case's command, against the base commit.
Timings: `spikes/s13/logs/_timings.txt`; raw tool output: `spikes/s13/logs/<case>.log`.

yarn 4.5.3 via corepack, enableScripts:false. Base lock pinned with `yarn set resolution`.
- native-up: `yarn up express@4.19.2` - drops the caret (^4.18.2 -> 4.19.2): changes the range strategy (NOT pure).
- native-up-exact: `yarn up chalk@4.1.2` - pure for an exact pin.
- textedit-update-lockfile: text edit + `yarn install --mode=update-lockfile` - lock jumps to 4.22.3.
- textedit-set-resolution: text edit + `yarn set resolution express@npm:^4.19.2 npm:4.19.2` + `yarn install --mode=update-lockfile` - pure, exact. RECOMMENDED (set resolution runs a full link step, ~12-20 s; scripts stay off).
- Yarn normalises package.json (sorts deps) on any install - an already-yarn-managed repo is already sorted.
- conflict-peer.ndjson.log: `--json` NDJSON, YN0060 peer warnings (exit 0; peers are warnings in yarn).

# Source: S13 apply spike, pnpm

Generated 2026-10-15 by `spikes/s13/run-pnpm-yarn.sh, run-js-conflicts.sh` (sample repo built under `spikes/s13/work/`, one base commit, one pinned bump per case).
Each `*.diff` is `git diff` (untracked files included via `git add -N`) after the case's command, against the base commit.
Timings: `spikes/s13/logs/_timings.txt`; raw tool output: `spikes/s13/logs/<case>.log`.

pnpm 11.2.2.
- native-add: `pnpm add express@4.19.2 --lockfile-only --ignore-scripts` - re-sorts dependencies (NOT pure).
- textedit-lockonly: text edit + `pnpm install --lockfile-only` - lock jumps to 4.22.3 (max satisfying).
- textedit-plus-update: text edit + `pnpm update express@4.19.2 --lockfile-only --ignore-scripts` - pure manifest, lock exactly 4.19.2. RECOMMENDED.
- conflict-peer.ndjson.log: `--reporter=ndjson` emits pnpm:peer-dependency-issues with {bad:{react:[{foundVersion,wantedRange,parents}]}} - machine-readable.

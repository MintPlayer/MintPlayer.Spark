# Source: S13 apply spike, uv

Generated 2026-10-15 by `spikes/s13/run-python.sh (uv 0.13.1)` (sample repo built under `spikes/s13/work/`, one base commit, one pinned bump per case).
Each `*.diff` is `git diff` (untracked files included via `git add -N`) after the case's command, against the base commit.
Timings: `spikes/s13/logs/_timings.txt`; raw tool output: `spikes/s13/logs/<case>.log`.

- native-add: `uv add requests==2.31.0 --no-sync` - pure (comment kept) + uv.lock.
- textedit-lock: text edit + `uv lock` - identical result. RECOMMENDED (text edit + `uv lock --upgrade-package name==ver` for ranges).
- inrange-upgrade-package: `uv lock --upgrade-package click==8.1.7` - lock-only pin for a range dep.
- conflict-no-solution.log: prose ('No solution found when resolving dependencies'), no JSON flag.

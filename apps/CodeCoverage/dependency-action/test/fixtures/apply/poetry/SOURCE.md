# Source: S13 apply spike, poetry

Generated 2026-10-15 by `spikes/s13/run-poetry.sh (Poetry 2.5.1)` (sample repo built under `spikes/s13/work/`, one base commit, one pinned bump per case).
Each `*.diff` is `git diff` (untracked files included via `git add -N`) after the case's command, against the base commit.
Timings: `spikes/s13/logs/_timings.txt`; raw tool output: `spikes/s13/logs/<case>.log`.

- native-add: `poetry add requests@2.31.0 --lock` - pure.
- textedit-lock: text edit + `poetry lock` - identical. RECOMMENDED.
- conflict-SolverProblemError.log: prose ('version solving failed').

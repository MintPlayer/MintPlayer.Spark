# Source: S13 apply spike, pip

Generated 2026-10-15 by `spikes/s13/run-python.sh (python:3.12-slim + pip-tools 7.6.2)` (sample repo built under `spikes/s13/work/`, one base commit, one pinned bump per case).
Each `*.diff` is `git diff` (untracked files included via `git add -N`) after the case's command, against the base commit.
Timings: `spikes/s13/logs/_timings.txt`; raw tool output: `spikes/s13/logs/<case>.log`.

- requirements-textedit: text edit; verify with `pip install --dry-run --ignore-installed --report` (JSON report of the resolved set). No lockfile.
- pip-compile-upgrade-package: `pip-compile --upgrade-package requests==2.31.0` - pure for the compiled requirements.txt.
- conflict-ResolutionImpossible.log: prose only ('ResolutionImpossible', 'The conflict is caused by:').

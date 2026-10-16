# Source: S13 apply spike, docker

Generated 2026-10-15 by `spikes/s13/run-docker-actions.sh` (sample repo built under `spikes/s13/work/`, one base commit, one pinned bump per case).
Each `*.diff` is `git diff` (untracked files included via `git add -N`) after the case's command, against the base commit.
Timings: `spikes/s13/logs/_timings.txt`; raw tool output: `spikes/s13/logs/<case>.log`.

No native tool. Text edit of the FROM tag; digest from `docker buildx imagetools inspect <ref> --format '{{json .Manifest.Digest}}'`. Both diffs pure (stage name and comment kept).

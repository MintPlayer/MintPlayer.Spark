# Source: S13 apply spike, go

Generated 2026-10-15 by `spikes/s13/run-composer-cargo-go.sh (golang:1.23, GOTOOLCHAIN=local, GOFLAGS=-mod=mod)` (sample repo built under `spikes/s13/work/`, one base commit, one pinned bump per case).
Each `*.diff` is `git diff` (untracked files included via `git add -N`) after the case's command, against the base commit.
Timings: `spikes/s13/logs/_timings.txt`; raw tool output: `spikes/s13/logs/<case>.log`.

- native-get: `go get github.com/google/uuid@v1.4.0` - pure go.mod (comment kept); go.sum gains lines, old ones kept.
- textedit-tidy: text edit + `go mod tidy` - pure, go.sum swapped. RECOMMENDED (tidy may also prune unrelated unused requirements in a dirty repo - check).
- textedit-tidy-needs-newer-go + conflict-toolchain.log: x/text v0.30.0 needs go >= 1.24 -> 'requires go >= 1.24.0 (running go 1.23.12; GOTOOLCHAIN=local)'. Prose; a platform blocker (R7).

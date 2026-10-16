# Source: S13 apply spike, cargo

Generated 2026-10-15 by `spikes/s13/run-composer-cargo-go.sh + manual (cargo 1.95.0)` (sample repo built under `spikes/s13/work/`, one base commit, one pinned bump per case).
Each `*.diff` is `git diff` (untracked files included via `git add -N`) after the case's command, against the base commit.
Timings: `spikes/s13/logs/_timings.txt`; raw tool output: `spikes/s13/logs/<case>.log`.

- native-add: `cargo add itoa@1.0.10` - manifest pure but the lock goes to 1.0.18 (latest compatible), NOT the plan.
- textedit-update-precise: text edit + `cargo update -p itoa --precise 1.0.10` - exact here...
- textedit-update-precise-serde: ...but for serde the implicit re-resolve after the manifest edit jumps to 1.0.229 (+serde_core, syn 3) and --precise is not honoured.
- lockfirst-precise-then-textedit: `cargo update -p serde --precise 1.0.200` FIRST (fits the old ^1.0.190), THEN text edit, then `cargo metadata --locked` to verify - pure + exact. RECOMMENDED.
- conflict.log: exit 101, prose 'failed to select a version for serde_derive'.

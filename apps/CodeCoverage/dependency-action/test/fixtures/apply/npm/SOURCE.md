# Source: S13 apply spike, npm

Generated 2026-10-15 by `spikes/s13/run-npm.sh, run-npm2.sh` (sample repo built under `spikes/s13/work/`, one base commit, one pinned bump per case).
Each `*.diff` is `git diff` (untracked files included via `git add -N`) after the case's command, against the base commit.
Timings: `spikes/s13/logs/_timings.txt`; raw tool output: `spikes/s13/logs/<case>.log`.

npm 11.12.1, node 24.15. Base lock pinned at express 4.18.2 under ^4.18.2.
- native-install(.diff): `npm install express@4.19.2 --ignore-scripts` - re-sorts dependencies, reflows devDependencies (NOT pure).
- native-install-exact: `npm install chalk@4.1.2 --save-exact` - same re-sort.
- textedit-lockonly: edit ^4.18.2->^4.19.2 + `npm install --package-lock-only --ignore-scripts` - manifest pure, but lock jumps to 4.22.3 (max satisfying), NOT the plan's 4.19.2.
- textedit-lockonly-exact: exact pin chalk 4.1.0->4.1.2 + lock-only - pure and exact.
- hybrid-textedit-pin-lock: text edit, `npm install express@4.19.2 --package-lock-only --ignore-scripts`, restore our package.json, lock-only again - pure manifest, lock pinned to 4.19.2, minimal (same 4 packages as native). RECOMMENDED.
- textedit-npm-update: `npm update express@4.19.2` - fails (EXIT 1), npm update takes names not specs.
- conflict-eresolve.json.log: `--json` gives {error:{code:ERESOLVE, summary, detail}}; detail is prose (parse 'peer X from Y').

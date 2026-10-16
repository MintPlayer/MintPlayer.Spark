# Source: a real apply run

- Captured 2026-10-16 (milestone N3) with this action's `runApply()` and real npm 11.12 / Node 24.15
  against registry.npmjs.org, on Windows 11 (scratchpad `gen/gen-contract-fixtures.ts`).
- Repo: the S13 npm sample (`../apply/npm/base/package.json`) plus `react`/`react-dom` 17.0.2, locked at
  express 4.18.2 with `npm install --package-lock-only --ignore-scripts`, committed; then the plan
  commit `.mintplayer/dependency-plan.json` = `npm-three-groups.plan.json`.
- Outcome: express applied (lock pinned at exactly 4.19.2, S13 hybrid), react-dom unresolvable
  (ERESOLVE under `--strict-peer-deps`, blocker `react`, group reset), chalk applied on top; whole-tree
  `npm ci --dry-run` passed.
- `npm-three-groups.patch` is the `dependency.patch` artifact (`git diff --binary` against the base, the
  plan file excluded). Local paths in `result.json` messages scrubbed to `<HOME>`.

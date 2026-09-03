# CodeCoverage — application docs

The docs of the Coverage application, which lived at `MintPlayer/CodeCoverage` until it was absorbed
into this workspace as `apps/CodeCoverage`. See
[`../coverage_monorepo_PRD.md`](../coverage_monorepo_PRD.md) for the move itself.

Two files in the parent directory are about the *app* but are not part of this set:
`PRD-CoverageHandoff.md` and `coverage-handoff-plan.md` are the **Spark** side of the hardening this
app asked for in preview.42. `docs/codecov/` is unrelated — it is about a coverage-*report* upload
failure on Spark PR #123.

## Live — still authoritative

| Doc | What it is |
|---|---|
| [`upload-api.md`](upload-api.md) | The stable CI↔server upload contract. Fields are added, never removed; the action and every consuming pipeline depend on it. |
| [`../../apps/CodeCoverage/action/README.md`](../../apps/CodeCoverage/action/README.md) | The upload action itself: what it sends, how it degrades against an older server, and **how to release a new version** (the one-click bump, and the two-tag scheme). |
| [`compile-ts-action-handoff.md`](compile-ts-action-handoff.md) | **Delivered.** The shared TypeScript→`index.js` build action in `MintPlayer/github-actions` that builds the bundle, why it lives there rather than here, and the six ways the implementation had to deviate from the spec. |
| [`roadmap-2026-08.md`](roadmap-2026-08.md) | Proposed next phase. Parts were absorbed elsewhere, but T0.1 (backups) and T1.1–T1.4 (honest numbers) are unbuilt — this is the live backlog. |
| [`product-overview.md`](product-overview.md) | Product and architecture overview; the first thing to read. Renamed from `PRD.md`, which meant nothing in a directory of PRDs. |
| [`ng-bootstrap-action-path.md`](ng-bootstrap-action-path.md) | **Historical.** Superseded twice over: the action came back to `apps/CodeCoverage/action`, and every consumer now pins `coverage-upload-v1`. Kept because it explains an intermediate state visible in git history. |
| [`old-repo-decommission.md`](old-repo-decommission.md) | **Done.** All three preconditions were met and the repository is archived at `MintPlayer-Archive/CodeCoverage`. |

## Historical — shipped, kept as the record of why

| Doc | What it records |
|---|---|
| [`build-log-m0-m10.md`](build-log-m0-m10.md) | How M0–M10 were sequenced and built. Renamed from `PLAN.md`. |
| [`upload-result-contract.md`](upload-result-contract.md) | Issue #9 — the upload result contract, as specified. Now described by `upload-api.md`. |
| [`coverage-analyzer-suite.md`](coverage-analyzer-suite.md) | Partial uploads, patch coverage, check-runs, thresholds, flags. |
| [`../coverage_carryforward_PRD.md`](../coverage_carryforward_PRD.md) · [`../coverage_carryforward_plan.md`](../coverage_carryforward_plan.md) | Commit assembly: multi-run uploads unioned, `nx affected` gaps carried from the base by git blob OID, the two Δ columns. |
| [`../coverage_branch_pr_badges_PRD.md`](../coverage_branch_pr_badges_PRD.md) · [`../coverage_branch_pr_badges_plan.md`](../coverage_branch_pr_badges_plan.md) | Branch and pull-request badges, and the sticky PR comment (roadmap T2.1 M11.5). Records that `?branch=` shipped long before it was documented, the four defects behind it, and why the private-repo comment carries a per-PR signature rather than the badge token. |
| [`adoption-findings.md`](adoption-findings.md) | Consumer-side findings, including the `showedOn` wipe that became Spark #274. |
| [`adopt-generated-indexes.md`](adopt-generated-indexes.md) | Adopting `[GenerateIndex]` — which entities, and the `Build.ComposeRun` backfill. |
| [`adopt-spark-generic-ui.md`](adopt-spark-generic-ui.md) | ng-spark generic UI adoption; explains today's `/po/...` grid links. |
| [`adopt-spark-preview-57.md`](adopt-spark-preview-57.md) | The preview.53→57 upgrade, and the hash-movement findings. |
| [`program-units-PRD.md`](program-units-PRD.md) · [`program-units-plan.md`](program-units-plan.md) | Adopting program units: composed home, server-driven menu. |
| [`self-coverage-PRD.md`](self-coverage-PRD.md) | How this repo measures its own coverage. Overlaps `../codecov/`; the two should be reconciled. |
| [`reauth-on-401.md`](reauth-on-401.md) | GitHub user-token expiry: silent refresh, reconnect fallback. |

Several of these are the *application* side of a framework change whose *framework* side also lives
here — `adopt-generated-indexes.md` ↔ `issue_210_*`, `adoption-findings.md` ↔ `issue_274_*`,
`adopt-spark-preview-57.md` ↔ `issue_281_*`, `program-units-*` ↔ `issue_324_*`/`issue_327_*`. Both
halves are worth keeping: one says what was built, the other says what it cost to adopt.

## Superseded — reduced to pointers

`composed-queries-PRD.md`, `spark-issue-279-PRD.md`, `spark-handoff.md` and
`spark-async-row-filter.md` each duplicated a Spark PRD that now lives in the same repository. Each
already admitted as much in its own header, so each is now a stub naming the authoritative file. The
original text remains in the history of `MintPlayer/CodeCoverage`.

## Removed

`parse-session-stuck-pending.md` (a resolved bug write-up) and `ng-bootstrap-handoff.md` (a change
list applied in 2026-08) were dropped rather than carried over.

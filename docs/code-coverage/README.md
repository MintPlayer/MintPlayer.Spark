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
| [`hosting.md`](hosting.md) | **The hosting reference.** Every compose service, `.env` variable, configuration key, GitHub App permission and event, deploy secret and action input, with its default cited to the file that sets it, plus the inconsistencies found while writing it. The step-by-step guide it backs is [`apps/CodeCoverage/README.md` § Hosting your own instance](../../apps/CodeCoverage/README.md#hosting-your-own-instance). |
| [`commit-graph.md`](commit-graph.md) | **The commit graph on the repository page** (#504): what it shows, the `coverage-git` mirror sidecar, how mirrors sync (lazy on view, push, hourly), quotas, the disk budget and eviction, deletion triggers, security, configuration, and how to inspect or clear a mirror on the VPS. In-app guide: `/commit-graph/setup`. Design and decision log: [`../coverage_commit_graph_PRD.md`](../coverage_commit_graph_PRD.md) + [plan](../coverage_commit_graph_plan.md). |
| [`onboarding.md`](onboarding.md) | **Set up features** (#571). The repository page's chooser: tick the features, review the plan, and the bot opens one `mintplayer/onboarding` pull request, rebuilt on every new default-branch head. On merge it pushes the seed tags, switches dependency updates and size labels on, renames old line branches and opens a regenerate-workflow pull request into each line. In-app guide: `/onboarding/setup`. Design: [`../coverage_backport_PRD.md`](../coverage_backport_PRD.md) D2, D3, D9, §8 M10. |
| [`dependency-updates.md`](dependency-updates.md) | **Dependency updates** — live since PR #522/#525 (2026-10-17). One rolling pull request per repository, rebuilt on a schedule through the repository's own lookup and apply workflows; any ecosystem the lookup reports, plus the platform SDKs. The guide covers onboarding (the onboarding page, and the workflows calling the `dependency-action` GitHub Action that the onboarding pull request adds) and App permissions, the `coverage.yml` `dependencies:` reference with its defaults, the comment commands and approvals, platforms and migrations, the fleet view and readiness, the security model with its residual risk, and troubleshooting. Design and decision log: [`dependency-updates-PRD.md`](dependency-updates-PRD.md). |
| Dependency updater design, by change | Each PRD holds its decisions and their evidence. [`sdk-updates-PRD.md`](sdk-updates-PRD.md): SDK moves and companions (#606, #617). [`platform-locations-PRD.md`](platform-locations-PRD.md): moves leave no location behind (#666). [`prerelease-dependencies-PRD.md`](prerelease-dependencies-PRD.md): prerelease tracks and platform-major opt-in (#671), continuing [`versioning-beta-upstream-PRD.md`](versioning-beta-upstream-PRD.md). [`dependency-companion-pins-PRD.md`](dependency-companion-pins-PRD.md): `follow:` pins and lockstep (#672). [`follower-cooldown-PRD.md`](follower-cooldown-PRD.md): follower cooldown, upstream companions, .NET Standard (#674, H1 in #676). [`companion-version-maps-PRD.md`](companion-version-maps-PRD.md): companion ranges per SDK channel or band (#676). [`command-reactions-PRD.md`](command-reactions-PRD.md): bot command reactions (#660). |
| [`versioning.md`](versioning.md) | **Package versioning** — live (#153); release lines and backports: #568. Every NuGet and npm package is versioned by an annotated `<PackageId>/v<version>` tag: only changed packages release, the major follows the platform, minor vs patch follows the public API, previews mirror upstream. The guide covers onboarding (seed tags), the workflow step and `MintPlayer.Versioning.MSBuild`, server vs local mode, the `coverage.yml` `versioning:` reference, release branches and backports (with the `release/*` ruleset advice), Releases and the `on: release` trap, title correction, and troubleshooting. In-app guide: `/versioning/setup`. Design and decision log: [`../coverage_package_versioning_PRD.md`](../coverage_package_versioning_PRD.md). |
| [`changelog.md`](changelog.md) | **Changelog** — #693. Each package Release's notes in sections (Breaking changes, Features, Fixes, …, Public API), built from the pull requests merged since its last tag and frozen in the tag; no changelog file is committed. Types come from explicit signals only (template box, title, `bug` label, check buttons); a `## Changelog` body section overrides an entry, `changelog: skip` drops it. One run Release per `/release`, `PackageReleaseNotes` in the nupkg, and the repository page's released and upcoming notes per line. In-app guide: `/versioning/setup#changelog`. Design and decision log: [`../coverage_changelog_PRD.md`](../coverage_changelog_PRD.md). |
| [`pr-size-labels.md`](pr-size-labels.md) | **PR size labels** — live (#527). Labels every GitHub pull request `size/XS`–`size/XXL` from the lines it changes, with a neutral **PR size** check run that explains the count; replaces `pascalgn/size-label-action` with its defaults. The guide covers the account and repository settings, the ignore rules (last match wins, `!` re-includes), how the app owns, renames and deletes its labels, switching off, and troubleshooting. No new App permission. Design and decision log: [`../coverage_pr_size_labels_PRD.md`](../coverage_pr_size_labels_PRD.md). |
| [`backports.md`](backports.md) | **Backports** — #570. A merged pull request carried onto older release lines by a `backport:` label or `/backport` comment: platform values normalized per line, conflicts resolved by the queue's rules or reported per line, regenerated files rebuilt by the line's workflow, backport pull requests handed to the merge queue, default labels, provenance (`backported:` labels, hand-made backports) and the cross-repo dependency diagnosis. |
| [`project-boards.md`](project-boards.md) | **Project boards** — #625. Moves the cards of a GitHub Projects board between its `Status` columns on issue, pull request, review, check-run and comment events, by rules per board (event → column, optionally moving the issues a pull request closes). The guide covers the App permissions, board discovery, Sync columns, the eighteen events, column changes and troubleshooting. In-app guide: `/project-boards/setup`. Design: [`../coverage_project_automation_PRD.md`](../coverage_project_automation_PRD.md). |
| [`merge-queue.md`](merge-queue.md) | **The merge queue** — live (#515, #517). It rebases queued pull requests, takes the base side of generated files, has the repository's own CI regenerate them, and squash-merges. The guide covers enabling it (App permissions, `coverage.yml` `mergeQueue:`, the regenerate workflow), the two-ruleset GitHub setup, the queue's commands and check run, every eviction reason, the security model, and operating the git writer. Design and decision log: [`../coverage_merge_queue_PRD.md`](../coverage_merge_queue_PRD.md). |
| [`raven-tls-migration.md`](raven-tls-migration.md) | **The secured RavenDB server** shared with SeoTester: the network matrix, the certificates and who holds them, and the one-time migration of the live server (backup, certificates, registration, rollback). Proven in Docker with the pinned image (2026-10-11). |
| [`mcp.md`](mcp.md) | **AI agents (MCP)** — draft until deployed: the `codecoverage_*` tools, signing in (embedded authorization server, personal access tokens), the hotspot ranking (spike S8). Operator notes in [`hosting.md`](hosting.md#ai-agents-mcp). Design: [`../spark_mcp_PRD.md`](../spark_mcp_PRD.md) §4.8. |
| [`forge-webhooks.md`](forge-webhooks.md) | **How to handle a webhook from any forge.** The minimal recipient API — one interface, one method — the six-event neutral vocabulary with its GitHub/GitLab/Bitbucket mapping, and the traps a port must not get wrong. Start here for anything webhook-shaped. |
| [`upload-api.md`](upload-api.md) | The stable CI↔server upload contract. Fields are added, never removed; the action and every consuming pipeline depend on it. |
| [`../../apps/CodeCoverage/action/README.md`](../../apps/CodeCoverage/action/README.md) | The upload action itself: what it sends, how it degrades against an older server, and **how to release a new version** (the one-click bump, and the two-tag scheme). |
| [`compile-ts-action-handoff.md`](compile-ts-action-handoff.md) | **Delivered.** The shared TypeScript→`index.js` build action in `MintPlayer/github-actions` that builds the bundle, why it lives there rather than here, and the six ways the implementation had to deviate from the spec. |
| [`roadmap-2026-08.md`](roadmap-2026-08.md) | Proposed next phase. Parts were absorbed elsewhere, but T0.1 (backups) and T1.1–T1.4 (honest numbers) are unbuilt — this is the live backlog. |
| [`product-overview.md`](product-overview.md) | Product and architecture overview; the first thing to read. Renamed from `PRD.md`, which meant nothing in a directory of PRDs. |
| [`../coverage_account_sync_PRD.md`](../coverage_account_sync_PRD.md) + [plan](../coverage_account_sync_plan.md) | The repository connection lifecycle and the nightly reconciler. Read it before touching anything that decides whether a repository is advertised, or that deletes coverage data — losing access to a repository deliberately destroys nothing, and only an owner-invoked action does. |
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

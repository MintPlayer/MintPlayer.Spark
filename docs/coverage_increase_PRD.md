# PRD — Raise code coverage (baseline 83.6% → target ≥ 91% line)

## Baseline

Master `ddcd8907`, as served by coverage.mintplayer.com: **83.6% line (29,447/35,218), 73.1% branch
(11,431/15,648)**, 790 matched files, 8 reports merged (5 .NET, 3 vitest).

| Area | Line % | Uncovered |
|---|---|---|
| apps/CodeCoverage (.NET + ClientApp) | 71.4% | 2,345 |
| libs/spark | 89.3% | 1,165 |
| add-on libs (identity_provider, replication, webhooks, messaging, testing, subscription_worker, cron, all_features) | 47–88% | ~1,330 |
| libs/source_generators | 92.2% | 346 |
| libs/node_packages (ng-spark, ng-spark-auth) | 80–84% | 420 |

## Scope decisions (owner, 2026-09-27)

- **Demo apps (`apps/DemoApp`, `apps/Fleet`, `apps/HR`) are excluded** from coverage, deliberately
  rather than by accident as today.
- **`apps/CodeCoverage` is production and stays measured**, including its ClientApp and `action/`.
- Everything lands in **one PR**.

## Goals

1. **Measurement is honest and fails loudly.** Every suite that should report does, every reported
   path reaches the badge, and a missing report fails CI instead of vanishing.
2. **Coverage rises by real tests**, not by exclusions. Exclusions are limited to code that genuinely
   cannot execute under test and are each justified in place.
3. **Bugs found by the investigation are fixed** with a regression test each.

## Measurement defects to fix

1. `apps/CodeCoverage/action` has **never** produced coverage: its `project.json` `test` target has no
   `executor`, so Nx merges it into the `nx:run-script` target inferred from `package.json`
   (`vitest run`, no `--coverage`).
2. Vitest cobertura filenames are relative to each package's `<source>`; the server's
   `PathNormalizer` never joins `<source>` + filename, so tail-matching is ambiguous for files that
   exist in both ng-spark and ng-spark-auth (`pipes/src/translate-key.pipe.ts`, `src/test-utils.ts`)
   and they are silently dropped. `tools/verify-coverage-paths.mjs` joins them, so it reports green on
   paths the server drops.
3. `tools/verify-coverage-paths.mjs` passes on **zero** reports — it needs an expected-report list.
4. E2E tests run Fleet/HR as separate processes; coverlet measures only the test process, so framework
   code exercised end-to-end (e.g. `ModuleCertificateAuthentication`, 18%) counts as uncovered.
5. Demo exclusion is accidental. Make it explicit: coverlet `<Exclude>` by assembly, upload globs
   narrowed to `apps/CodeCoverage/...`.

## Test work (estimates from the investigation)

| Area | Est. lines gained | Prerequisites |
|---|---|---|
| CodeCoverage server | ~1,300 | Octokit REST+GraphQL over `StubHttpMessageHandler`; one shared fake `IGitHubInstallationService`; `CoverageWebAppFactory` service-override hook |
| Core libs (spark, generators, authorization, client) | ~790 (+ ~75 dead code removed) | shared `ScratchContentRoot`; isolated index-fixture assembly |
| Add-on libs | ~700 in-process, more via E2E subprocess coverage | none for most; self-signed cert in-test |
| Front ends | CodeCoverage SPA 5.5% → ~90%; ng-spark branch 64.6% → high 70s | `BrowseService` stub, `ActivatedRoute` stub, shared `settle()`, chart stand-ins |

## Bugs to fix (each with a regression test; verify before fixing)

- `AttributeRenderer.cs:185` emits `(global::E)-1` for negative enum values (does not compile).
- `GitHubAuthenticationExtensions.cs:78` hard-codes `api.github.com` (GitHub Enterprise token sent to github.com).
- `EntityMapper` parses dates with the server culture and swallows failures; enum parse is case-sensitive.
- `ValidationService.cs:324` throws `OverflowException` on NaN/Infinity (500).
- `SparkExternalLoginLinker.cs:275` reports "Linked" when the login belongs to another user (unverified).
- Update/Refresh 500 when the body lacks `persistentObject` (unverified).
- `SecurityConfigurationAnalyzer` key regex assumes 2-space indentation.
- `FinalizeBuildsCronJob` dispatches assemble/feedback for builds it skipped.
- `SparkSubscriptionWorker` leaves `ExecuteAsync` via an uncaught exception on stop (unverified).
- Dev websocket endpoint sets `StatusCode = 401` after the upgrade; `WebSocketDevClientService` leaks a
  `ClientWebSocket` per reconnect and throws on duplicate header names.
- `OidcTokenCleanupService` never deletes expired revoked tokens.
- `MessageQueueRouter`: a transient claim-renew error in `finally` masks the processing outcome.
- `EtlTaskManager.FindExistingEtlTaskId` swallows all exceptions → duplicate add.
- `DetailWithholdTests` copies the filter logic instead of calling it; StreamExecuteQuery tests never
  complete the WebSocket close — fix the tests so they can fail.

## Non-goals

- A hard CI coverage gate. The realistic outcome of this work (~91–92%) is below the 95% gate the owner
  set on 2026-09-01, and the number moves down before it moves up while measurement is fixed.
- Testing the demo apps.

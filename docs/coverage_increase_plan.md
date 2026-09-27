# Plan — Raise code coverage

PRD: [coverage_increase_PRD.md](coverage_increase_PRD.md). One branch (`feat/coverage-increase`), one PR.
Workstreams B–F run in parallel on separate worktrees and are merged into the branch; test suites are
run once, in full, after the merge.

## A — Measurement fixes

- [x] `apps/CodeCoverage/action/project.json`: `"executor": "nx:run-commands"` on `test`, so
      `test:coverage` actually runs; confirm the output path is in the target `outputs`.
- [x] Server `PathNormalizer`: join the cobertura `<source>` with the filename before tail matching;
      unit test with two packages sharing a tail.
- [x] Vitest cobertura for ng-spark / ng-spark-auth / SPA: repo-relative filenames (`projectRoot`).
- [x] `tools/verify-coverage-paths.mjs`: expected-report list (fail on missing), same matching rules
      as the server, globs limited to `apps/CodeCoverage/...` for apps.
- [x] `coverlet.runsettings`: `<Exclude>[DemoApp*]*,[Fleet*]*,[HR*]*</Exclude>`.
- [x] Workflows (`pull-request.yml`, `dotnet-build-master.yml`): upload globs narrowed to
      `apps/CodeCoverage/...`; `hashFiles` gate updated.
- [x] ng-spark: exclude `**/test-utils.ts` from coverage (as ng-spark-auth does).

## B — CodeCoverage server tests (`apps/CodeCoverage/CodeCoverage.Tests`)

- [x] Row security: Commit/Build/GitHubProject row filters; anonymous `/spark/po/load` of a private
      repo's Commit/Build is indistinguishable from missing.
- [x] `ForkUploadsController`: every refusal branch + accept path; HTTP refusal cases.
- [x] Cron jobs + handlers (`FinalizeBuildsCronJob` — fix the skipped-build dispatch bug,
      `ReconcileForgeStateCronJob`, Finalize/Assemble/ReconcileAccount, `ProjectAutomationRouter`,
      `PublishFeedbackRecipient`).
- [x] `GitHubStateReconciler` with scripted installation fakes.
- [x] Remaining `BrowseController` endpoints; `UploadsController` gaps + one real HTTP upload with a
      seeded upload token; `SyncColumnsAction`, `ResyncAction`, remaining Actions;
      `ParseSessionRecipient` (gzip, bomb limit, missing attachment, truncated XML); migration guards.
- [x] Seams: Octokit REST + GraphQL over `StubHttpMessageHandler` with captured JSON; shared fake
      `IGitHubInstallationService`; `CoverageWebAppFactory` override hook. Then the GitHub REST
      wrappers, `GitHubProjectCards`, `InstallationProjects`.

## C — Core libs tests (`tests/Spark.Tests`, `SourceGenerators.Tests`, `Client.Tests`)

- [x] `SparkDevelopmentExtensions` verify rejection paths (`[Theory]` of planted model JSON).
- [x] Delete the 7 zero-caller methods (grep-verified).
- [x] MiniJson via `LibraryTranslationsGenerator`; `EntityMapper` converters; `QueryExecutor`
      JsonElement filter branch; `ValidationService`, breadcrumb renderer, `ProjectedOffsetRestorer`.
- [x] Endpoint error paths (`SparkEndpointFactory`), standalone New, DistinctValues; `SparkClient` gaps.
- [x] JWT bearer options + GitHub `OnCreatingTicket` via fake handler.
- [x] Bugs: AttributeRenderer negative enum, GitHub Enterprise emails URL, EntityMapper culture/enum,
      ValidationService NaN, ExternalLoginLinker, Update/Refresh missing body, analyzer indentation;
      fix `DetailWithholdTests` and StreamExecuteQuery tests.

## D — Add-on libs tests

- [x] `ModuleCertificateAuthentication` via TestServer + in-test self-signed cert.
- [x] `SparkSubscriptionWorker` error/stop paths (verify the shutdown bug first).
- [x] `OidcTokenGenerator` claim switches, `Token.cs` guards, signing-key reload, token cleanup
      (revoked-token bug).
- [x] `MessageProcessor` direct; lease/claims (claim-renew masking bug).
- [x] `SmeeBackgroundService`, `WebSocketDevClientService` over loopback Kestrel (socket leak,
      duplicate header, 401-after-upgrade bugs).
- [x] Cron error branches; `all_features` generator with Actions/CustomAction/Recipient;
      `EtlTaskManager` (exception-swallow bug); `libs/testing` stays measured.

## E — Front ends

- [x] Shared test utilities: `settle()` into test-utils, `BrowseService` / `ActivatedRoute` stubs,
      chart stand-ins.
- [x] SPA: rule-evaluation, commit-files-panel, file page, 11 renderers, vanity-redirect guards,
      browse.service, repo-badge-panel, route-order spec.
- [x] ng-spark: po-detail patch/lookup/AsDetail paths, spec-less services and pipes, po-form,
      query-list; ng-spark-auth: passkeys component, error paths.
- [x] `apps/CodeCoverage/action` specs for `main.ts`.

## F — E2E subprocess coverage

- [x] Run Fleet/HR hosts under `dotnet-coverage` (filtered to `MintPlayer.Spark*`), graceful shutdown
      so hits flush, emit cobertura, add it to the upload and to the verifier's expected list.

## G — Integration

- [ ] Merge B–F, resolve conflicts, full sweep of all 5 .NET test projects + all vitest projects.
- [ ] PR; read the new baseline from coverage.mintplayer.com.

## Deliberately not done in this PR

- `--spark-verify-model` index checks and the ambiguous `[DefaultIndex]` exit-2 case: need an isolated
  fixture assembly (fixtures leak into assembly-wide scans).
- CodeCoverage: `/health/ready` ready/failed paths (need a real App key), `Program.cs` command-line modes,
  `GitHubAccessService`, `SmtpLinkConfirmationSender`, `ForgeEventsRecipient`, `CommitAssembler`.
- Add-on libs: `UseSparkReplication` startup, `ModuleCertificateValidator` / `SyncActionRetrySweeper`
  branches, `MessageQueueRouter` handler-timeout path (only reachable through a flaky cancellation).

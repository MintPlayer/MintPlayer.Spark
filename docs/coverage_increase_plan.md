# Plan — Raise code coverage

PRD: [coverage_increase_PRD.md](coverage_increase_PRD.md). One branch (`feat/coverage-increase`), one PR.
Workstreams B–F run in parallel on separate worktrees and are merged into the branch; test suites are
run once, in full, after the merge.

## A — Measurement fixes

- [ ] `apps/CodeCoverage/action/project.json`: `"executor": "nx:run-commands"` on `test`, so
      `test:coverage` actually runs; confirm the output path is in the target `outputs`.
- [ ] Server `PathNormalizer`: join the cobertura `<source>` with the filename before tail matching;
      unit test with two packages sharing a tail.
- [ ] Vitest cobertura for ng-spark / ng-spark-auth / SPA: repo-relative filenames (`projectRoot`).
- [ ] `tools/verify-coverage-paths.mjs`: expected-report list (fail on missing), same matching rules
      as the server, globs limited to `apps/CodeCoverage/...` for apps.
- [ ] `coverlet.runsettings`: `<Exclude>[DemoApp*]*,[Fleet*]*,[HR*]*</Exclude>`.
- [ ] Workflows (`pull-request.yml`, `dotnet-build-master.yml`): upload globs narrowed to
      `apps/CodeCoverage/...`; `hashFiles` gate updated.
- [ ] ng-spark: exclude `**/test-utils.ts` from coverage (as ng-spark-auth does).

## B — CodeCoverage server tests (`apps/CodeCoverage/CodeCoverage.Tests`)

- [ ] Row security: Commit/Build/GitHubProject row filters; anonymous `/spark/po/load` of a private
      repo's Commit/Build is indistinguishable from missing.
- [ ] `ForkUploadsController`: every refusal branch + accept path; HTTP refusal cases.
- [ ] Cron jobs + handlers (`FinalizeBuildsCronJob` — fix the skipped-build dispatch bug,
      `ReconcileForgeStateCronJob`, Finalize/Assemble/ReconcileAccount, `ProjectAutomationRouter`,
      `PublishFeedbackRecipient`).
- [ ] `GitHubStateReconciler` with scripted installation fakes.
- [ ] Remaining `BrowseController` endpoints; `UploadsController` gaps + one real HTTP upload with a
      seeded upload token; `SyncColumnsAction`, `ResyncAction`, remaining Actions;
      `ParseSessionRecipient` (gzip, bomb limit, missing attachment, truncated XML); migration guards.
- [ ] Seams: Octokit REST + GraphQL over `StubHttpMessageHandler` with captured JSON; shared fake
      `IGitHubInstallationService`; `CoverageWebAppFactory` override hook. Then the GitHub REST
      wrappers, `GitHubProjectCards`, `InstallationProjects`.

## C — Core libs tests (`tests/Spark.Tests`, `SourceGenerators.Tests`, `Client.Tests`)

- [ ] `SparkDevelopmentExtensions` verify rejection paths (`[Theory]` of planted model JSON).
- [ ] Delete the 7 zero-caller methods (grep-verified).
- [ ] MiniJson via `LibraryTranslationsGenerator`; `EntityMapper` converters; `QueryExecutor`
      JsonElement filter branch; `ValidationService`, breadcrumb renderer, `ProjectedOffsetRestorer`.
- [ ] Endpoint error paths (`SparkEndpointFactory`), standalone New, DistinctValues; `SparkClient` gaps.
- [ ] JWT bearer options + GitHub `OnCreatingTicket` via fake handler.
- [ ] Bugs: AttributeRenderer negative enum, GitHub Enterprise emails URL, EntityMapper culture/enum,
      ValidationService NaN, ExternalLoginLinker, Update/Refresh missing body, analyzer indentation;
      fix `DetailWithholdTests` and StreamExecuteQuery tests.

## D — Add-on libs tests

- [ ] `ModuleCertificateAuthentication` via TestServer + in-test self-signed cert.
- [ ] `SparkSubscriptionWorker` error/stop paths (verify the shutdown bug first).
- [ ] `OidcTokenGenerator` claim switches, `Token.cs` guards, signing-key reload, token cleanup
      (revoked-token bug).
- [ ] `MessageProcessor` direct; lease/claims (claim-renew masking bug).
- [ ] `SmeeBackgroundService`, `WebSocketDevClientService` over loopback Kestrel (socket leak,
      duplicate header, 401-after-upgrade bugs).
- [ ] Cron error branches; `all_features` generator with Actions/CustomAction/Recipient;
      `EtlTaskManager` (exception-swallow bug); `libs/testing` stays measured.

## E — Front ends

- [ ] Shared test utilities: `settle()` into test-utils, `BrowseService` / `ActivatedRoute` stubs,
      chart stand-ins.
- [ ] SPA: rule-evaluation, commit-files-panel, file page, 11 renderers, vanity-redirect guards,
      browse.service, repo-badge-panel, route-order spec.
- [ ] ng-spark: po-detail patch/lookup/AsDetail paths, spec-less services and pipes, po-form,
      query-list; ng-spark-auth: passkeys component, error paths.
- [ ] `apps/CodeCoverage/action` specs for `main.ts`.

## F — E2E subprocess coverage

- [ ] Run Fleet/HR hosts under `dotnet-coverage` (filtered to `MintPlayer.Spark*`), graceful shutdown
      so hits flush, emit cobertura, add it to the upload and to the verifier's expected list.

## G — Integration

- [ ] Merge B–F, resolve conflicts, full sweep of all 5 .NET test projects + all vitest projects.
- [ ] PR; read the new baseline from coverage.mintplayer.com.

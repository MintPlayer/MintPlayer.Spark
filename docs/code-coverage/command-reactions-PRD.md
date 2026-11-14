# Bot command reactions and command logs (#660): PRD and plan

Date: 2026-11-14. Issue: [#660](https://github.com/MintPlayer/MintPlayer.Spark/issues/660). Owner request: "launch a team
to investigate, then create a new PRD/Plan file, which may include several spikes." That was followed the same day by a
grilling session (the owner's answers are recorded as decisions G1–G16 in §3) and by an added requirement: **every bot
command starts with `/`**.

The issue asks for:

| Reaction | Meaning |
|---|---|
| 👀 `eyes` | the bot detected the command |
| 🚀 `rocket` / 🎉 `hooray` | everything is done, success |
| 👍 `+1` | nothing changed |
| 👎 `-1` | error |

The issue also asks for the log or report to be stored in the database and rendered in the browser through a
protected Spark action, visible only to users who should have access to it.

Status: **design decided (§3, G1–G17). Spikes S1–S3 (§5) come next, then M0–M7, all in one pull request** (global rule 7).

## 1. Findings

### 1a. GitHub App permissions: nothing to change

The owner asked for the permissions of both Apps to be adjusted if needed. Both installations were read on 2026-11-14
with `gh api orgs/MintPlayer/installations`. `coveragedevelopment` and `coverageproduction` have identical sets:

- `issues: write`, `pull_requests: write` (plus actions, checks, contents and workflows write; metadata, members,
  statuses and vulnerability_alerts read; organization_projects write)
- events: `issue_comment` and `pull_request_review_comment`, among others

`POST /repos/{o}/{r}/issues/comments/{id}/reactions` is allowed with Issues **or** Pull requests write, and both Apps
have both. **No App setting changes.** The docs already list this: `docs/code-coverage/hosting.md:406` (Issues R&W) and
`:439`.

There is one code-level gap. `GitHubInstallationPermissions.cs:233-252` declares what each feature needs, and the merge
queue declares only `issues: read` (`:238`). Reacting to `/queue` therefore has to declare `issues: write` there (M3).

### 1b. Where commands are parsed and run

Every comment command is parsed in `GitHubEventsRecipient.OnIssueCommentAsync`
(`apps/CodeCoverage/CodeCoverage.GithubIntegration/Recipients/GitHubEventsRecipient.cs:580-626`), only for
`action == "created"`, never for the App itself or a bot author (`:592`, `:610-611`). It is then raised as a forge
event and forwarded to a queued message:

| Command | Parser | Forge event (`ForgeEvents.cs`) | Handler | Carries comment id? |
|---|---|---|---|---|
| `/release …` (issue or PR) | `ReleaseCommand.Parse` (`:590`) | `ReleaseRequested` (`:366`) | `ReleaseRecipient` (`PackageVersioning/ReleaseRecipients.cs:55`) | **no** |
| `/queue`, `/dequeue` | first word (`:599-604`) | `MergeQueueCommandIssued` (`:247`) | `MergeQueueEngine.EnqueueAsync` (`MergeQueue/MergeQueueEngine.cs:288`) | **no** |
| `approve`, `hold`, `unhold`, `bisect`, `refresh` (**no slash today**) | `DependencyCommandParser.cs:21-29` | `DependencyCommandIssued` (`:317`) | `DependencyUpdateEngine.CommandAsync` (`Dependencies/Lifecycle/DependencyUpdateEngine.cs:1424`) | yes |
| `/backport <lines> [--draft]` | `BackportCommand.Parse` (`:608`) | `BackportRequested` (`:357`) | `BackportRecipient` (`PackageVersioning/BackportRecipients.cs:57`) | **no** |

Only GitHub raises comment commands. `MergeQueueCommandIssued` also comes from a label, the check button
(`GitHubEventsRecipient.cs:554`, `:811`) and the site button (`Controllers/MergeQueueController.cs:286`).
`BackportRequested` also comes from a label or a merge (`:539`, `:573`).

The bare dependency commands also match ordinary comments that start with "Hold on…" or "Refresh my memory…". The
command hints the bot writes are in:

- `Dependencies/Lifecycle/DependencyPullRequestBody.cs`, which is rewritten on every run and so updates by itself
- `DependencyOnboarding.cs`
- the refusal replies in `DependencyUpdateEngine.cs:1448-1480`
- `ClientApp/.../repo-dependency-updates-panel.component.ts` and `services/browse.service.ts`
- `apps/CodeCoverage/README.md`
- `docs/code-coverage/dependency-updates.md`

### 1c. Reactions today

- `IForgeDependencyUpdates.AddCommentReactionAsync` (`CodeCoverage.Library/Forge/IForgeDependencyUpdates.cs:53`) and the
  enum `EForgeReaction { ThumbsUp, ThumbsDown, Eyes }` (`:94`). The GitHub implementation is at
  `GitHubForgeDependencyUpdates.cs:195-204`, called best effort through `DependencyUpdateEngine.ReactAsync` (`:1992`).
- Only the dependency commands react. There, 👍 means **accepted** (`:1470`, `:1491`). #660 makes 👍 mean "nothing
  changed", so `CodeCoverage.Tests/Dependencies/DependencySagaTests.cs:458-520` changes.

### 1d. How outcomes are reported today

- Dependency: 👍/👎, plus a reply comment on refusal, plus `run.Note(...)`.
- Merge queue: a refusal goes to the check and a comment (`RefuseAsync`, `MergeQueueEngine.cs:900`). An already-queued
  pull request returns silently (`:295-299`).
- Backport: a refusal is a comment (`BackportRecipients.cs:83`). A forge failure becomes a `BackportAttempt` and a log line
  (`:87-96`).
- Release: the answer is a comment (`ReleaseRecipients.cs:76-77`). **A forge failure is only logged** (`:79-86`), so the
  commenter sees nothing at all.

### 1e. Building blocks

- **Storage seam (#620):** `ISparkStore.OpenSession()`, `ISparkSession` and `ISparkStoreExpiration` are in
  `libs/spark/MintPlayer.Spark.Abstractions/Storage/`. A library that references only Abstractions works on the RavenDB
  and the InMemory providers, and on a future EF Core provider (#536).
- **No database logger provider exists.** The only `ILoggerProvider` implementations in the repo are test helpers
  (`tests/MintPlayer.Spark.Tests/Messaging/TestOutputLoggerProvider.cs`).
- **Attribute permissions already exist, on top of type permissions:**
  - Static `{verb}/{Type}/{Attribute}` rights for Query/Read/Edit/New
    (`MintPlayer.Spark.Abstractions/Authorization/SparkAttributeRights.cs`, `docs/guide-authorization.md:98-152`). A
    refused attribute is removed from the payload. An attribute right never unlocks a refused type right. An AsDetail
    row is judged by its row type (`Read/LogEntry/Exception`).
  - The per-row hook `IPersistentObjectActions.GetProtectedAttributesAsync(action, entity)` (`:164-175`). It empties the
    values on every read path and protects them from write-back, and takes dotted names into AsDetail rows
    (`Entries.Exception`).
- **Run records with the same shape:** `VersioningRun` and `BackportAttempt` (read-only Spark entities, listed by an
  id-prefix stream, `Actions/VersioningRunActions.cs:33-45`, row-filtered through
  `ISparkVisibility.GetVisibleRepositoryIdsAsync`).
- **Uniform 401/404:** `IRepositoryScope.ResolveAsync` + `SparkDenial.Refuse` (`SparkDenial.cs:72-78`). The template is
  `ReleaseRunsController.cs:55-61,175`.
- **Retention:** `Coverage:Retention:ReportAttachmentDays` (`Ingestion/ReapReportAttachmentsCronJob.cs:50-65`) is the
  precedent for a retention setting.
- **Duplicate deliveries** are deduplicated on `X-GitHub-Delivery` (`SparkWebhookEventProcessor.cs:152-170`). Handlers must
  still be idempotent, because queued messages can be retried.

## 2. Requirements

- **R0** Every bot command starts with `/`: `/approve`, `/hold`, `/unhold`, `/bisect`, `/refresh`, `/queue`, `/dequeue`,
  `/backport` and `/release` (G1).
- **R1** Every comment command gets 👀 when its handler starts (G4).
- **R2** It ends with exactly one current bot reaction: 🎉 done, 🚀 long-running process started, 👍 nothing changed,
  👎 refused or failed (G2). Long-running commands later get their final reaction (G3). Each new state replaces the
  previous reaction (G4).
- **R3** Every comment command is stored as a command record (outcome + a message the user can act on) with a log (G5,
  G13).
- **R4** Logs are written through `ILogger` into a storage-neutral `Logs` collection by a new, application-independent
  library (G6–G10), shipped with a `Log` persistent object whose access is secure by default (G15, G16).
- **R5** Label, button and merge-triggered queue and backport requests get a log too, without a command record or a
  reaction (G11).
- **R6** On 👎, the bot's reply quotes the outcome message and links to the record. A release forge failure gets a reply,
  which it doesn't today.
- **R7** Reactions are best effort. A failed reaction never fails the command, and a retry never doubles a reaction
  (S1).

## 3. Decisions (owner, grilling session 2026-11-14)

- **G1. Hard cut to `/`.** Only the slash forms are commands. A comment starting with bare `approve` is an ordinary
  comment: no reaction, no log, no hint. This ends the "Hold on…" false matches.
  - The trade-off accepted: someone typing the old form out of habit gets silence.
  - Every hint in §1b changes in the same pull request.
  - An open dependency pull request shows the old hint until its next run rewrites the body.
  - The memory note "bot commands have NO slash" becomes wrong when this ships.
- **G2. 🎉 = done, 🚀 = long-running process started.**
  - 🎉: `/approve`, `/hold`, `/unhold`, `/dequeue`, `/release`, `/backport`.
  - 🚀: `/queue`, `/bisect`, `/refresh`.
  - `/approve`, `/hold` and `/unhold` also start a refresh run at the end (`DependencyUpdateEngine.cs:1491-1492`), but
    they still get 🎉: their own effect (the approval or hold) is recorded, and the run is a side effect. Only a command
    whose *own* effect is the long process gets 🚀.
- **G3. Final reactions for long-running commands.**
  - `/queue`: 🎉 when merged, 👎 when refused or failed. After a `/dequeue`, the `/queue` comment gets no final reaction,
    and the `/dequeue` comment gets its own 🎉.
  - `/refresh`: 🎉 when the run applied updates, 👍 when there was nothing to update, 👎 when it failed.
  - `/bisect`: 🎉 when the culprit is found, 👎 when it failed.
  - A second `/refresh` while a run is going gets 👍 straight away, and only the first comment gets the run's final reaction.
  - The command record stores the merge-queue run id (not just the pull request number), so an old `/queue` never picks
    up a later run's 🎉. Likewise it stores the dependency run that `/refresh` or `/bisect` started, so the run's end
    finds the comment to finish.
- **G4. One current reaction, one shared lifecycle.** Each state deletes the bot's previous reaction by its stored id.
  - 👀 is added at the start of each command's own handler, never in a separate recipient. A separate one could land
    after the outcome and stay forever.
  - All four command families go through one shared lifecycle (start → started → final). No command reacts its own way.
    `DependencyUpdateEngine.ReactAsync` goes away.
- **G5. Retention:** `Coverage:Retention:CommandInvocationDays`, default 90. Only finished records expire (a `/queue`
  still in progress never does). Expiry goes through `ISparkStoreExpiration`, not Raven's `@expires` directly. A link in
  an old comment then answers with the uniform 404. That ambiguity is accepted, because it is what keeps private
  repositories from leaking.
- **G6. A new library, `MintPlayer.Spark.Logging`** (`libs/logging/`, a new NuGet package that gets its first version
  from the versioning engine). It never references RavenDB.
  - *Refined in M1:* it references Spark **core**, not Abstractions only. Its `LogActions` derives from
    `DefaultPersistentObjectActions<Log>` and asks core's `ISparkReadAccess` (S3), and both live in core. Storage still
    goes only through `ISparkStore`/`ISparkSession`, and the tests run the same suite on RavenDB and InMemory
    (`tests/MintPlayer.Spark.Tests/Logging/LogScopeTests.cs`).
  - Log ids are `Logs/{key}` (`LogIds.ForKey`), or `Logs/{guid}` for `CreateScope()`.
- **G7. Scoped capture through the built-in logging scope.** `ILogger.BeginScope` plus a provider that implements
  `ISupportExternalScope`, under `[ProviderAlias("Document")]`.
  - Only entries inside a log scope are written.
  - `Logging:Document:LogLevel` in `appsettings.json` filters categories and levels, like any provider.
  - Any `ILogger` deeper in the same async flow is captured too.
  - **Nested scopes:** an entry goes to the **innermost** log scope only, never to every open one.
    - The command lifecycle does not open its own scope around an engine that keeps its own log. That covers the merge
      queue (`MergeQueueEntry`) and backports (`BackportAttempt`), both of which keep a log under G11.
    - For those commands, the command record's `LogId` points at the engine's log, so nothing is written twice.
- **G8. The API:**
  ```csharp
  using (ILoggerScope scope = loggerScopeProvider.CreateScope())        // generates a Guid id
      scope.Logger.LogInformation("…");    // the DI ILogger: console, Seq, … and the Logs document
  ```
  - `scope.Logger` is the ordinary DI-resolved `ILogger`, so one call reaches every sink.
  - `Dispose` closes the scope and saves the buffered entries in one write.
  - A one-line method opens and closes the scope itself.
- **G9. One plain collection:** `Logs`, `{ Id, RelatedPersistentObjectId?, Entries: [{ At, Level, Text, Exception? }] }`.
  - Through `ISparkSession` it is one document on RavenDB, and would be a `Logs` table plus a `LogEntries` table on
    EF Core.
  - The exception is stored in its own field (G12), and the entry has no other fields.
- **G10. Ids:**
  - `CreateScope()` generates a Guid.
  - `CreateScope(id, relatedPersistentObjectId?, expiresAt?)` reopens an existing log and appends to it, by loading it,
    adding the entries and saving with the expected version, retrying on a conflict. The abstraction has no patch
    operation.
  - Ids passed in are validated: allowed characters, maximum length, and a feature prefix such as
    `commands/github/{repoId}/{commentId}`.
  - The command lifecycle uses a fixed id, so retries and the final step reach the same log.
- **G11. Triggers that aren't comments get a log, not a command record.** The log id goes on `MergeQueueEntry` and
  `BackportAttempt`, and each page shows a "view log" link.
  - Those logs expire after the same `Coverage:Retention:CommandInvocationDays` (G5), counted from the end of the run
    or attempt, even though their owners are kept forever.
  - Once the log is gone, the "view log" link disappears, because the owner checks that `LogId` still exists.
- **G12. `Exception` is its own field.** Handlers must pass exceptions as `LogX(ex, "…")`. An exception's text formatted
  into the message is visible to every reader, so M4 reviews the handlers for this.
- **G13.** `Log` stays fully decoupled from application code. It ships with a `Log.json` persistent-object model and
  default rights. ~~The consumer declares `IQueryable<Log> Logs` on its context for now; generating that property from a
  per-feature attribute comes later.~~
  - *Superseded in M1:* the consumer declares nothing. A library model cannot use a `Database.X` query source (the
    reason is in the Settings library's `SettingActions`), so `Log.json`'s `Logs` query uses `Custom.Logs`, which
    `LogActions.Logs()` serves. `spark.AddLogs()` is the whole opt-in, plus the rights.
- **G14. Finer control is the app's own `LogActions`.** An app overrides `GetRowFilterAsync` and
  `GetProtectedAttributesAsync` in its own `LogActions` class, using normal Spark extension points.
- **G15. The shipped default inherits from the related object.**
  - The shipped `LogActions` makes a log with a `RelatedPersistentObjectId` readable (detail load) only if the caller
    may read that object, through Spark's own read path: type right, row filter, `DisableActions`.
  - A log without one follows the `Log` type right alone.
  - ~~Listing logs (Query) follows the type right only.~~
  - *Superseded in M1:* **list rows pass the same gate as a detail load.** In Spark a `Read` grant implies `Query`
    (`libs/spark/MintPlayer.Spark/AGENTS.md:25-29`). So the `Read/Log` grant that G16 gives everyone would have
    listed every log of every record to anyone. Found red by `LogAccessTests`, where ann, holding `Read/Log` only, got
    200 from `Custom.Logs`. `LogActions.IsAllowedAsync` now gates `"Query"` like `"Read"`, which the list calls per row.
    Green: `The_list_holds_only_the_logs_the_caller_may_open`.
  - A missing or expired related object means the log can't be read (fail closed).
- **G16. Secure by default.** The shipped `security.json` grants **nobody** `Read/Log` or `Query/Log`.
  - CodeCoverage opts in: `Read/Log` for everyone (so detail loads inherit from the command record, `MergeQueueEntry` or
    `BackportAttempt`), and `Query/Log` for its admin group only.
  - CodeCoverage's `LogActions` protects `Entries.Exception` for callers without write access to the related object's
    repository.
  - ~~A startup warning or SPARK diagnostic flags `Query/Log` granted to an everyone group.~~
  - *Superseded in M1:* there is no such warning. `Read/Log`, which CodeCoverage grants everyone, already implies
    `Query/Log` (G15 note), so the warning would fire on the intended setup or be trivially sidestepped. What it guarded
    against, everyone listing every log, cannot happen now that list rows pass the read gate. "Admins only" for the
    list is CodeCoverage's own `LogActions` refusing `Query` rows to non-admins (G14), and M6 tests it.
- **G17. An app takes over a library's actions class through the source generator.**
  - **The problem.** `ActionsResolver.FindActionsType` (`libs/spark/MintPlayer.Spark/Services/ActionsResolver.cs:121-156`)
    throws when two non-abstract classes share an actions name. So the library's `LogActions` (G15) and an app's
    `LogActions` (G14) could not both exist.
  - **The fix.** `ActionsRegistrationGenerator` (`libs/source_generators/.../Generators/ActionsRegistrationGenerator.cs:15-55`)
    already finds each actions class and its entity in the consuming project. When a project's class derives from a
    referenced library's actions class for the same entity, the generator emits
    `[assembly: SparkActionsOverride(typeof(Log), typeof(App.LogActions))]`.
  - `SparkActionsOverrideAttribute` lives in Abstractions. `ActionsResolver` consults these attributes before its name
    scan. Unrelated duplicates still throw, as today.
  - New SPARK diagnostics:
    - an **error** when two projects override the same entity
    - a **warning** when an app declares an actions class with a library actions class's name that does not derive from
      it, which silently drops the library's protections
  - A host that doesn't run the generator keeps today's behaviour (the ambiguity exception), so it fails loudly.
  - This is general: Organizations, Notifications, Settings, Dashboards, the identity provider and Authorization all ship
    actions classes that apps could not override until now.

## 4. Command record

`CommandInvocation` (CodeCoverage, read-only Spark entity).

- **Id:** `CommandInvocations/{provider}/{repoId}/{commentId}`.
- **Fields:**
  - Repository, Number, CommentId, Command (first line, length-capped), Actor
  - ReceivedAt, FinishedAt
  - `ECommandOutcome { Received, Started, Done, NoChange, Refused, Failed }` and Message (the sentence the user can act on)
  - CurrentReactionId, MergeQueueRun and DependencyRun (for G3)
  - LogId
- **Listing:** per repository by id-prefix stream (like `VersioningRunActions.cs:33-45`), row-filtered through
  `ISparkVisibility`, and shown as a "Commands" list on the repository page.

## 5. Spikes (before M1)

- **S1. GitHub reactions API, live, with the development App on a sandbox repository.**
  - The status of a second POST of the same reaction: expected 200 with the existing reaction, versus 201.
  - `DELETE …/issues/comments/{c}/reactions/{id}` with an installation token on the App's own reaction.
  - The `X-Accepted-GitHub-Permissions` header on both calls.
  - That an issue (not pull request) comment, for `/release`, uses the same endpoint.

  **Measured 2026-11-14** with the owner's own `gh` token on the owner's comment 6878064747 on PR #666 (an 👀 added,
  then deleted). The App's installation token was not used: both Apps already hold `issues: write` and
  `pull_requests: write` (`gh api orgs/MintPlayer/installations`), which is what the reactions endpoints require.
  - First POST of 👀 → **201** with a new reaction id. A second POST of the same content → **200 with the same id**,
    so adding is idempotent and a replayed delivery cannot stack reactions.
  - `DELETE …/issues/comments/{c}/reactions/{id}` → **204**; the same DELETE again → **404**. The reaction service
    treats 404 on delete as success.
  - Pull-request conversation comments are issue comments, so `issues/comments/{id}/reactions` serves `/release` on an
    issue and every pull-request command alike.
- **S2. The logging scope.**
  - Check that an `ISupportExternalScope` provider sees the `CreateScope` state from an `ILogger` resolved elsewhere in
    the same async flow, including inside a Spark messaging handler.
  - Decide what a fire-and-forget task started inside a scope does after `Dispose`: drop late entries, or write them in a
    second save.
  - Measure the cost of the append path (load, add, save with expected version) on RavenDB and InMemory.

  **Result (M1, 2026-11-14), proven by `LogScopeTests` on both stores, 35/35 green:**
  - **Flow capture works.** An `ILogger<T>` resolved on its own, after an `await`, lands in the scope's log
    (`What_any_logger_in_the_flow_logs_inside_a_scope_is_saved_in_one_log`). The messaging-handler case is covered
    when M3 opens the lifecycle's scope inside one.
  - **Late entries are dropped** (decided): a closed scope's state refuses new entries, so a fire-and-forget task
    neither writes a second save nor files its entries under the outer log
    (`A_task_still_logging_after_its_scope_closed_is_dropped_not_filed_under_the_outer_log`).
  - ⚠️ **Found: `DisposeAsync` must not be `async`.** It pops the `BeginScope` handle, and an AsyncLocal change made
    inside an async method is undone when that method returns. The closed inner scope therefore stayed current for
    the caller, and the outer scope's next entries were dropped. Red: `An_entry_goes_to_the_innermost_scope_only`
    (outer log held `["Outer before"]` only). Green: the pop happens synchronously before the save's own async method
    starts (`LoggerScopeProvider.cs`, `LoggerScope.DisposeAsync`). The same rule binds callers, so `guide-logging.md`
    says to open and close a scope in one method.
  - **Append cost:** five writers closing on one key at once all land, through the expected-version retry
    (`Scopes_with_one_key_closing_together_all_land`). The whole class runs in under a second per store, so no
    separate measurement was made. The command lifecycle appends a handful of times per command.
- **S3. Inheriting access, and the attribute hook on AsDetail rows.**
  - Confirm that the shipped `LogActions` can ask Spark's read path "may this caller read object X?" for an arbitrary
    `RelatedPersistentObjectId`, and how the type is resolved from the id.
  - Confirm that `GetProtectedAttributesAsync` returning `Entries.Exception` empties it on the detail read path.
  - Check that a signed-out click on a log link lands on sign-in and returns to the page.

  **Result (M1):**
  - **Core had no public "may the caller read X?".** The pieces were internal (`IRowSecurity.AreAllowedAsync`,
    `ISparkTypeResolver`). M1 adds the public `ISparkReadAccess.MayReadAsync(id)`
    (`libs/spark/MintPlayer.Spark/Services/SparkReadAccess.cs`). It resolves the type from the **stored collection** of
    the id: a side-session load, so the caller's session never tracks an untyped document. It then answers with the
    `Read` type right plus the row rules, which is what a detail load applies. An id of no model type, or of nothing,
    is `false`. Proven by `LogAccessTests` against the Organizations package's row rule on a note: the owner gets 200,
    an outsider 404, anonymous 401, and a missing record 404.
  - **Dotted names into AsDetail rows are supported** by `GetProtectedAttributesAsync` (`RowSecurity.RedactAttribute`).
    M6 proves `Entries.Exception` end to end.
  - The signed-out click is checked in M6, with the views.

## 6. Milestones (TDD: red test first, per memory `feedback_tdd_red_green`)

- **M0. Overriding library actions classes (G17).**
  - `SparkActionsOverrideAttribute` in Abstractions, the generator's override detection, and `ActionsResolver`
    consulting the attribute first.
  - The two SPARK diagnostics, documented in `docs/diagnostics.md`.
  - Generator snapshot goldens.
  - Tests:
    - an app's class that derives from the library's wins
    - unrelated duplicates still throw
    - two projects overriding the same entity is an error
    - a class with the same name that doesn't derive from the library's gives the warning
  - Docs: `docs/guide-library-layers.md`.

- **M1. `MintPlayer.Spark.Logging` (G6–G10, G13, G15, G16).**
  - The library: provider, `ILoggerScopeProvider`/`ILoggerScope`, `Log` entity + `Log.json`, the shipped `LogActions`
    and `security.json` (no grants), id validation, append with concurrency retry, expiry through `ISparkStoreExpiration`.
  - Tests run on both providers, in the style of `SparkStoreConformanceTests`.
  - A guide page `docs/guide-logging.md`.
- **M2. Reaction service.**
  - `IForgeCommentReactions` keyed by provider: `AddAsync` returns the reaction id, plus `RemoveAsync`.
    `EForgeReaction` gains `Rocket` and `Hooray`.
  - Remove `IForgeDependencyUpdates.AddCommentReactionAsync`, with no shim (preview).
  - `StubGitHub` routes for POST and DELETE.
- **M3. The `/` cut, events carry the comment id, the shared command lifecycle (G1, G4).**
  - `DependencyCommandParser` takes only the slash forms, and every hint in §1b changes.
  - `MergeQueueCommandIssued`, `BackportRequested` and `ReleaseRequested` gain `long? CommentId`.
  - The `CommandInvocation` entity + lifecycle service (start → 👀 + log scope; started → 🚀; final → 🎉/👍/👎, each
    replacing the previous reaction).
  - Declare `issues: write` for the merge queue (`GitHubInstallationPermissions.cs:238`).
  - Tests: a bare `approve` is ignored. A replayed delivery gives one record and one reaction.
- **M4. Outcomes for each command (G2, G12).**
  - The dependency engine, merge queue, backport and release recipients finish through the lifecycle, and every exit
    path is mapped. That includes the silent `return`s and the rate-limit rethrow, which gets no 👎 until the retry
    gives up.
  - Saga tests change for the new 👍 meaning.
  - The release forge failure gets a reply comment.
  - Review that exceptions are passed as `LogX(ex, …)`.
- **M5. Final reactions (G3).** The merge queue's terminal transition, the dependency run's end and bisect's end each
  finish the invocation that started them.
- **M6. Logs for triggers that aren't comments, and the views (G11, G14–G16).**
  - `LogId` on `MergeQueueEntry` and `BackportAttempt`.
  - CodeCoverage's `LogActions : MintPlayer.Spark.Logging.LogActions` (G17), which hides `Entries.Exception` from users
    without write access.
  - Its `security.json` grants: `Read/Log` for everyone, `Query/Log` for admins, and `QueryRead/CommandInvocation` for
    the same groups as `VersioningRun`.
  - The "Commands" list and the "view log" links, which hide themselves once the log has expired.
  - `--spark-synchronize-model`, never a hand edit.
  - Tests: anonymous 401, signed-in without access 404, reader sees no Exception, user with write access sees it.
  - Route snapshot and `CsrfSurfaceTests` if an endpoint is added.
- **M7. Retention + docs.**
  - `Coverage:Retention:CommandInvocationDays` (G5).
  - Docs: `hosting.md` (§1a rows and the retention setting), `merge-queue.md`, `dependency-updates.md` (slash forms,
    👍 meaning), `backports.md`, `versioning.md`, `apps/CodeCoverage/README.md`.
  - The translations golden if `translations.json` changes.
- **Gate.** Run `npm run test:affected` once at the end, then push, open the pull request (`closes #660`) and mark it
  ready.

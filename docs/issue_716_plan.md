# #716: a view collides with the class-based index it replaced, and the batch drops every view

Issue: https://github.com/MintPlayer/MintPlayer.Spark/issues/716. Found 2026-11-27, the day #536 (relational providers)
was deployed.

## Symptom

After the #536 deploy, CodeCoverage and SeoTester process no messages: every `SparkMessages` document stays `Pending` with
zero attempts, and `MessageFeeder` and `MessageRetrySweeper` throw `IndexDoesNotExistException: Could not find index
SparkMessages_ByState` every 30 seconds. At startup the console shows:

```
Error creating RavenDB indexes from Spark views: Raven.Client.Exceptions.Documents.Indexes.IndexCreationException: Failed to create indexes. Cluster is probably down.
 ---> Raven.Client.Exceptions.Documents.Indexes.IndexCreationException: Could not create index 'OidcGrants_BySubject' because it would result in directory name collision with 'OidcGrants/BySubject' index
```

Production logs, 2026-11-27, since the 07:04 deploy: CodeCoverage logged 436 `SparkMessages_ByState` errors and
SeoTester 429, each with the collision line at startup. mintplayer.com (no identity provider) deployed its views and is
unaffected. The same day, PR #715's coverage upload to that CodeCoverage server hung until the 15-minute job limit; that
was not traced further.

## Cause

1. **Two index names, one folder.** A class-based index is named after its class with `_` turned into `/`; before #536
   the identity provider's `OidcGrants_BySubject : AbstractIndexCreationTask<OidcGrant>` deployed as
   `OidcGrants/BySubject`. #536 turned that class name into a `SparkView<OidcGrantState>`, which `RavenViewIndex` names
   verbatim: `OidcGrants_BySubject`. RavenDB stores an index in a folder named after it with `/` turned into `_`, so the
   two share a folder and the server refuses the view. Nothing removed the old index.
2. **One batch for all views.** `RavenIndexDeployment.DeployViews` passed every view to one `IndexCreation.CreateIndexes`
   call. RavenDB applied it all or nothing, so the one refusal kept all nine views of a CodeCoverage database out,
   messaging's `SparkMessages_ByState` (#536 D42) among them.
3. **Console only.** Both failure paths wrote to `Console.WriteLine`, so no log sink recorded the cause.

## Evidence

- Repro on a throwaway database holding only `OidcGrants/BySubject`, given the nine views CodeCoverage deploys through the
  same `IndexCreation.CreateIndexes` call: the call threw the error above and created **no** view. Without that index, it
  created all nine. Each view compiled and ran on its own (`POST /indexes/test`).
- Deleting `OidcGrants/BySubject` from the local Coverage database and restarting deployed all nine views, and the 14
  messages that had been waiting were processed, a GitHub `issues` delivery among them.
- Raven.Client 7.2.7 has no option to replace a conflicting index. `IndexCreation.CreateIndexes` takes only the indexes,
  the store, conventions and the database. `PutIndexesOperation` takes only the definitions, and
  `IndexDefinition.DeploymentMode` chooses between rolling and parallel deployment.

## Decisions

- **D1. Deployment deletes the index a view replaced.** Before a view is deployed, an existing index is deleted when its
  name differs from the view's but equals it once `/` is read as `_` (case-insensitive, as RavenDB compares index names):
  `X/Y` for the view `X_Y`. While the class-based index is still declared, it shadows the view of its class name and the
  view is not deployed, so in the normal case only a leftover can match. The deletion is logged.
  - *Guard:* an index that a native index task of this application still declares under that name (an `IndexName`
    override) is never deleted.
  - *Rejected:* deploying views under the `/` form of their names, so that they replace the old indexes in place. That
    reverses #536's naming of a view by its declared name, and the view's queries, bindings and the other providers all
    use that name.
  - *Rejected:* a one-off data migration per database. Every database that ran the identity provider before #536 has the
    leftover, including those of applications built on the published packages, which no migration in this repository
    reaches.
- **D2. Each view deploys on its own.** One refused view no longer keeps the others out of the database. The cost is one
  request per view at startup, about ten for CodeCoverage, in place of one.
- **D3. Failures are logged through `ILogger<RavenIndexDeployment>` as errors**, with the exception, in place of the console.
  The progress lines moved to the same logger at Information level.

## Tests

`RavenSparkViewTests` (`tests/MintPlayer.Spark.Tests/Storage/`), written red first:
- `A_view_replaces_the_class_based_index_it_superseded`: an index `Employee/Names` exists, and the views `Employee_Names`
  and `EmployeesWithCompany` deploy; both views exist afterwards and `Employee/Names` is gone.
- `A_view_the_server_refuses_keeps_no_other_view_out`: an index `EmployeesWithCompany` locked with `LockedError` refuses
  the view's definition; `Employee_Names` deploys anyway, and the refusal is logged as an error naming the view.

Red on master `4d642620b` (2026-11-27): the first found `{"Employee/Names"}` and no view, the second `{"EmployeesWithCompany"}`
and no `Employee_Names`. Green with the fix, alongside the classes whose construction changed
(`SparkExtensionsPrivateHelpersTests`, and `MessageRetrySweeperStaleIndexTests` through `RavenStorageSeam`, which now adds
logging to its container): 24 passed.

## Rollout

CodeCoverage and SeoTester are deployed from master, so each repairs itself at the first startup after the merge: the
leftover `OidcGrants/BySubject` is deleted, the nine views are created, and the waiting messages are processed.

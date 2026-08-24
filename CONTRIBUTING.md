# Contributing to MintPlayer.Spark

Thanks for contributing. This file covers what you need to run the test suite; the
[README](README.md) covers the architecture, build and coding standards.

## Running the tests

```bash
npx nx run-many --target=test
```

That runs both stacks — the four .NET test projects and the Angular suites. **You do not need a
RavenDB licence**, and you do not need a RavenDB installation: the .NET suites start their own
embedded RavenDB server per test run.

### About the RavenDB licence

Most of the suite runs against an unlicensed embedded server, in RavenDB's AGPL mode. That mode is
capped at 3 CPU cores and switches off the licensed features — ETL, encryption, documents
compression, data archival, backups — but store, load, query, update, indexing and subscriptions all
work, so the large majority of tests are unaffected.

The handful of tests that genuinely need a licensed feature are marked
`[RequiresLicensedFeature("...")]` and **skip themselves** when no licence is present. A skipped test
is reported as a skip, with a reason naming the feature — it is not a failure, and you do not need to
do anything about it. Those tests run on the maintainers' CI, which does hold a licence.

So: a green run with some skips is the expected result for a contributor, and it is an honest one.

If you *want* the full suite locally, request a free developer licence from
[ravendb.net/buy](https://ravendb.net/buy) and make it available either way:

- set `RAVENDB_LICENSE` to the JSON content, or
- save it as `raven-license.log` in the repository root.

`raven-license.log` is gitignored by an explicit rule. **Do not commit a licence** — the RavenDB
EULA forbids providing licence keys to third parties, and GitHub's secret scanning will not catch
it, because RavenDB is not a scanning partner and no provider pattern matches.

### Why your pull request may show fewer tests than ours

GitHub does not expose repository secrets to workflow runs triggered from a fork. That is a
deliberate GitHub security boundary, not a misconfiguration, and it applies to every open-source
project. It means your PR's CI run has no licence and skips the licensed tests, exactly as your
local run does.

Those tests still gate your contribution — they run before it lands on `master`, on infrastructure
that does hold the licence. If one of them fails there, a maintainer will tell you what broke.

## Test suite notes

- **Databases per run.** `SparkTestDriver` creates a fresh RavenDB database per *test case*, which
  is several hundred per run. Parallelism is capped in `xunit.runner.json` for that reason — read
  the comment there before raising it.
- **Batch your test runs.** The suites are slow. Run them once when your change is complete rather
  than after each edit.
- **Flakiness.** The E2E suite has a known intermittent failure where the host hangs after the tests
  themselves have passed. If a run fails with no test failures, re-run before investigating.

## Pull requests

Follow the [Contribution Workflow](README.md#contribution-workflow) in the README. Two repository
conventions worth knowing before you open a PR:

- **Package majors track the platform, not our API.** The npm major equals the Angular major it
  supports; the NuGet major equals the targeted .NET major. A breaking change in our own API is a
  *minor* bump. Getting this wrong publishes a version number that cannot be reclaimed.
- **Keep the diff to the change.** Revert incidental formatter or schematic churn before opening the
  PR.

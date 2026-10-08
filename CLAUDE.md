# MintPlayer.Spark — repository instructions

## `apps/` holds applications, and one of them is production

`apps/` (renamed from `Demo/`) holds the five demos **and `apps/CodeCoverage`**, which is not a demo:
it is the coverage server running at coverage.mintplayer.com, absorbed from `MintPlayer/CodeCoverage`.
Treat changes to it as production changes. Its docs are in [docs/code-coverage/](docs/code-coverage/README.md).

- **`apps/SparkId`** is the demo identity provider (https 5011). HR, Fleet and QnA sign in against it,
  and Fleet validates the machine tokens it issues. Identity-provider work lands there, not in HR
  (`docs/identity_provider_platform_PRD.md`).
- **`apps/DemoApp`** is the minimal Spark app: core only, no Authorization, no identity provider. It
  is the guides' running example and the only app on plain `AddSpark` (SPARK030). New features don't
  land there.

Two consequences worth knowing before editing it:

- The app is named **CodeCoverage**, not Coverage, and that is load-bearing. `**/coverage/` in
  `.gitignore` matches a path component equal to `coverage`, case-insensitively on Windows, so a
  directory named `Coverage/` would be silently untracked. Never rename it back.
- Its RavenDB database is still called `Coverage` and its `[SparkAuthorize(..., Coverage)]`
  resources still say `Coverage`. Those are production data and authorization identifiers, not
  naming leftovers.

## Running the apps: never start the Angular dev server yourself

Every app (`apps/CodeCoverage`, `apps/DemoApp`, `apps/Fleet`, `apps/HR`, `apps/QnA`, `apps/SparkId`) hosts its SPA through
**`UseAngularCliServer`** — the ASP.NET Core host spawns `npm start` itself and proxies it. So:

- **`dotnet run` is the whole command.** Do not run `ng serve` / `npm start` alongside it; a second
  dev server just fights for ports.
- **Do not run `ng build` / `ng test` against that workspace while the host is running.** It is
  unnecessary (the host builds), it shares `.angular/cache` with the live dev server, and it can
  wedge the file watcher.
- **To see a client change:** save the file. The dev server rebuilds and the browser live-reloads.
- **To see a *server* change:** restart the host — watch mode does not reload C#. Kill the whole
  `dotnet run` process **tree**, not just the child `<App>.exe`, or the next build fails with
  MSB3027/MSB3021 "file is locked by <App>".
- **If output looks stale**, suspect a wedged watcher rather than your code, and restart the host.

The host prints the dev server's own port (`➜ Local: http://localhost:NNNNN/`) once it is ready;
that is the signal the app is actually serviceable, not `Now listening on:`.

## Running the test suites

**Local test runs take over 30 minutes; test runs on GitHub Actions take 17 minutes.** The local run
is serial (RavenDB tests starve each other's CPU when run in parallel) and usually skips E2E, while
CI runs everything, E2E included, in parallel with its Nx cache and the Developer RavenDB licence.

- **CI runs cost money: never push just to get a test run.** The sweep runs locally. Making it fast
  is tracked work (see `docs/contributions_PRD.md` §5c).
- When CI is red anyway (after a push that was asked for), read the failure from its log first:
  `gh run view <run> --job <job> --log-failed`, grepping for `Failed` and `Error Message`.
- Locally, re-run only the single failing test class while fixing it.
- A local-only failure may come from the machine's environment rather than the code. For example,
  a `RAVENDB_LICENSE` that holds the Community licence gives a `LicenseLimitException` ("revisions
  1000 > licensed 2") that CI never sees.

### The local sweep: `npm run test:affected`

One script runs every **affected** test project, unit and E2E together (`tools/test-local.mjs`;
CI never calls it). Extra arguments go to `nx affected`, e.g. `-- --skip-nx-cache`.

- **Licence:** set `RAVENDB_LICENSE` to the path of the **Developer** licence file,
  `C:\Repos\MintPlayer.Spark\.secrets\raven-license.log`. A shell started before that variable
  changed still has the old value; set it on the command
  (`RAVENDB_LICENSE='C:\Repos\MintPlayer.Spark\.secrets\raven-license.log' npm run test:affected`).
- **Coverage is off locally:** every test target has a `local` configuration (`-c local`) whose
  command drops the coverlet collector and vitest `--coverage`. The default configuration, which CI
  runs, is unchanged, and the different command keeps local and CI cache entries apart.
- **Parallel:** the script passes `--parallel=4` (CI keeps nx.json's 3). That gives the long
  CodeCoverage.Tests chain a slot early; at 5 a sweep ran out of memory.
- **E2E:** when it is affected, the script first builds the apps it hosts (`Fleet`, `QnA`) through
  nx, then sets `SPARK_E2E_SKIP_APP_BUILD=1` so `SparkAppTestHost` skips its own per-app
  `dotnet build`. The variable is opt-in; unset (CI) the host still builds. A new E2E host app must
  be added to `E2E_APPS` in the script.
- Every RavenTestDriver base (`SparkTestDriver`, `SparkSharedDatabase`, `CoverageRavenTest`)
  deletes its databases without the server's 15 s confirmation wait, through
  `RavenDatabaseDeletion.DeleteOnDispose` in `PreInitialize`. This applies on CI as well.

**Now (2026-10-02): 7m39s (459 s) for everything including E2E and builds, all green, 2.85×
faster than the 21m49s below.** The gains came from:
- test hosts deploying only the indexes a test needs
- CodeCoverage.Tests deploying its index only where a class queries one
- per-class hosts and databases for the OIDC and read-only classes
- no dynamic PGO in test processes or the embedded server
- `--parallel=4`

The evidence is in `docs/contributions_PRD.md` §5d items 10–14. Three tests used to fail only under a
fully loaded sweep (`S_M3`, `ModerationVoteTests.M5`, `ComplexFieldIndexingTests.Verbatim_…`). All
three are fixed at the root (§5c): S_M3 exposed a real sweeper bug, stale-index patches that
rewrote completed messages. M5 and the Corax test waited on the wrong condition. A test that fails
only in the sweep is a **defect to locate**, never "load" or "flakiness": none of these turned out to
be CPU. Time each step of the failing test (stopwatch laps, the server's own timestamps) and read the
documents' revision histories before naming a cause or changing a wait. **Do not run CPU burners**
(`node -e "for(;;){}"`) on the owner's machine: the owner works on it (2026-10-07).

**No test process uses the machine's proxy.** With Windows' WPAD auto-detection on, the first
`HttpClient` request to a host name in a process waited 1–10+ s for proxy discovery and failed a 10 s
test bound on an idle machine (`DevWebSocketEndpointTests`, 2026-10-07; 33 ms with the proxy off).
`tests/Shared/NoSystemProxy.cs` turns the proxy off for every `*.Tests` project through
`Directory.Build.targets`; a new test project gets it by being named `*.Tests`. Evidence in
`docs/test-suite-performance-PRD.md` §3, "No proxy in test processes".

**WireMock's first request scans the whole bin.** WireMock.Net 2.15 loads every DLL in the test
project's output (168) on its first request, 0.9 s warm and 10.8–24.6 s on freshly built binaries, which
failed the same 10 s bound (2026-10-08). `WireMockWarmUp` (Spark.Tests, `Webhooks/GitHub/_Infrastructure`)
pays it before the clock starts; a new test that bounds a first WireMock request needs it too. Evidence
in `docs/test-suite-performance-PRD.md` §3, "WireMock's first-request plugin scan".

Measured 2026-10-01 on this machine (`--skip-nx-cache`, Developer licence, everything affected):
**21m49s wall for everything including E2E and builds, all green.** Compare the earlier serial
sweep, which took 26.2 min **without** E2E. Per project:

| Project | Serial, coverage on | `test:affected` (parallel, no coverage) |
|---|---|---|
| MintPlayer.Spark.Tests (3510) | 21m21s | 18m20s (the critical path) |
| CodeCoverage.Tests (1045) | 3m34s | ⚠️ 10m41s (CPU contention with Spark.Tests) |
| MintPlayer.Spark.SourceGenerators.Tests | 64s | 1m20s |
| MintPlayer.Spark.Client.Tests | 7s | 0.2s |
| MintPlayer.Spark.E2E.Tests (138) | not run | 3m41s |

The wall time is now set by `MintPlayer.Spark.Tests` alone. Running projects in parallel does not
make a single project faster: the suites compete for the same cores.

## RavenDB queries: never splice strings into RQL

Never build RQL or a patch script by interpolating a string, even when today's callers only pass
constants. A helper that takes arbitrary strings becomes an injection the moment a caller passes
request data. (Owner, 2026-10-05, on `ForgeQualifiedIdVerifier`: "Make sure nobody can inject malicious
strings here. You're not escaping untrusted inputs here".)

- **Values** (ids, prefixes, filters, search text) always go through query parameters:
  `$p` + `AddParameter`, `IndexQuery.QueryParameters`, or patch-script `args`.
- **Identifiers** (collection, field path, index name) can't be parameters in RQL. Validate them
  against a strict allow-pattern before splicing, using the shared validator
  `RqlIdentifier.Collection(name)` / `RqlIdentifier.FieldPath(path)`
  (`libs/spark/MintPlayer.Spark.Abstractions/RqlIdentifier.cs`, public because apps and the
  Authorization package splice identifiers too). It throws `ArgumentException` on anything else.
  RavenDB's own escaper (`QueryFieldUtil`) is internal, so don't reach for it.
- Subscription queries take no parameters. There, constrain the input with an allow-list first, as
  the messaging queue names already are.
- Review every new migration, verifier and query builder for this.

## Versioning: major version is locked to the targeted platform

The major version of every published package in this repository is **not** a semver
signal we are free to bump. It states which platform the package targets, and it moves
only when that platform moves.

### npm packages (`@mintplayer/ng-spark`, …)

`@mintplayer/ng-spark-auth` no longer exists: it lives in `@mintplayer/ng-spark` as the
`@mintplayer/ng-spark/auth/*` entry points (#464/#490).

The major version **must equal the major Angular version the package is compatible with**.

- Compatible with Angular 22 → `22.x.x`
- Compatible with Angular 23 → `23.x.x`

A breaking change in our own API is **not** a reason to bump the major. Ship it as a
minor (`22.2.0` → `22.3.0`) and describe the break in the release notes. The major is
reserved for the Angular upgrade that actually makes the package require the new
framework version.

All npm packages in the workspace move their major **together**, in the same PR as the
Angular upgrade itself.

### NuGet packages (`MintPlayer.Spark*`)

The major version **must equal the major .NET version the package targets**.

- `net10.0` → `10.x.x`
- `net11.0` → `11.x.x` (current — the solution targets `net11.0`, packages are `11.0.0-preview.*`)

Same rule as above: an API break inside a .NET generation is a minor bump, never a
major one.

### Before bumping a version

Ask: *did the targeted Angular / .NET major change?* If the answer is no, the major
digit stays exactly where it is. Getting this wrong is expensive — a wrongly published
major cannot be reused on npm even after it is unpublished or deprecated, so the real
`23.0.0` (or `11.0.0`) is burned forever.

CI publishes on push to `master`, so a wrong version number in `package.json` /
`.csproj` becomes a permanent public artifact as soon as the PR merges. Check the
version diff in the PR review.

#!/usr/bin/env node
// `npm run test:affected` — fast LOCAL run of every affected test project, unit and E2E together.
// CI never calls this: .github/workflows/pull-request.yml runs `npx nx run-many --target=test`
// with each test target's DEFAULT configuration (coverage on), which this does not touch.
//
// What makes it faster than a plain `nx affected -t test`:
//   - `-c local`: every test target has a `local` configuration whose command drops the coverlet
//     collector / vitest `--coverage`. Because the command differs, local and CI results never
//     share a cache hash.
//   - SPARK_E2E_SKIP_APP_BUILD=1: when the E2E project is affected, the apps it hosts are built
//     once through nx first, so SparkAppTestHost skips its own per-app `dotnet build`.
//   - `--parallel=4` (nx.json says 3, which CI keeps): measured 2026-10-02 on a 4-core/8-thread
//     laptop, full `--skip-nx-cache` sweeps took 557 s at 3, 459 s at 4 and 445 s at 5. At 3, the
//     long CodeCoverage.Tests build+test chain waited ~2 min for a free slot (nx "recoverable time"
//     23%; 5% at 5). 4, not 5: a sweep at 5 was stopped by Claude Code for low memory (each heavy
//     suite runs its own in-memory RavenDB). An explicit `--parallel` argument still wins.
//
// Extra arguments go to `nx affected`, e.g. `npm run test:affected -- --skip-nx-cache`.
//
// Licence: the embedded RavenDB reads RAVENDB_LICENSE. Point it at the DEVELOPER licence file
// (.secrets/raven-license.log); the Community one caps the server at 3 cores and has no ETL.
import { spawnSync } from 'node:child_process';

const E2E = 'MintPlayer.Spark.E2E.Tests';
// The apps the E2E suite hosts (the SparkAppDescriptor in each *TestHost.cs under
// tests/MintPlayer.Spark.E2E.Tests). Add one here when a new host appears, or it runs stale.
const E2E_APPS = ['Fleet', 'QnA'];

const passthrough = process.argv.slice(2);

function nx(args, { env = process.env, capture = false } = {}) {
  console.log(`> nx ${args.join(' ')}`);
  const result = spawnSync('npx', ['nx', ...args], {
    stdio: capture ? ['inherit', 'pipe', 'inherit'] : 'inherit',
    shell: true,
    env,
    encoding: 'utf8',
  });
  if (result.error) throw result.error;
  return { status: result.status ?? 1, stdout: result.stdout ?? '' };
}

if (!process.env.RAVENDB_LICENSE) {
  console.warn('[test:affected] RAVENDB_LICENSE is not set: RavenDB tests run unlicensed or fail. ' +
    'Set it to the path of .secrets/raven-license.log (Developer licence).');
}

// Same base/head arguments as the test run, so both agree on what is affected.
const affectedArgs = passthrough.filter(a => /^--(base|head|files|uncommitted|untracked)\b/.test(a));
const shown = nx(['show', 'projects', '--affected', '--json', ...affectedArgs], { capture: true });
if (shown.status !== 0) process.exit(shown.status);
const affected = JSON.parse(shown.stdout.slice(shown.stdout.indexOf('[')));

const env = { ...process.env };
if (affected.includes(E2E)) {
  const built = nx(['run-many', '-t', 'build', `--projects=${E2E_APPS.join(',')}`]);
  if (built.status !== 0) process.exit(built.status);
  env.SPARK_E2E_SKIP_APP_BUILD = '1';
}

const parallel = passthrough.some(a => /^--parallel\b/.test(a)) ? [] : ['--parallel=4'];
process.exit(nx(['affected', '-t', 'test', '-c', 'local', ...parallel, ...passthrough], { env }).status);

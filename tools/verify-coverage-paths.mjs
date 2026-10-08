#!/usr/bin/env node
/**
 * Verifies that every coverage report CI is supposed to upload exists, and that every
 * file path in it resolves to a tracked file the same way the ingestion server
 * resolves it.
 *
 *   node tools/verify-coverage-paths.mjs [--json] [--dry-run] [--help] [glob ...]
 *
 * Why this exists, and why it verifies rather than rewrites
 * --------------------------------------------------------
 * The server (`PathNormalizer`) resolves a reported path by stripping the uploader's
 * `rootDir`, then stripping a report-declared `<source>` root, then suffix-matching
 * against `git ls-files`. A path it cannot resolve is returned `Matched = false`, and
 * `ParseSessionRecipient` summarises matched files only — so an unresolvable path is
 * **excluded from the percentage with no error anywhere**. The report simply arrives
 * smaller, which reads as a coverage drop rather than as a bug.
 *
 * Cobertura already carries its `<source>` roots, so unlike lcov there is nothing to
 * rebase — see the header of `apps/CodeCoverage/tools/rebase-lcov-paths.mjs`, which
 * makes that case. Rewriting the paths here would duplicate, and could disagree with,
 * logic the server already performs correctly. What is missing is not a rewrite but a
 * *tripwire*: something that turns the silent drop into a red build.
 *
 * The matching rules below are a port of the server's, not an approximation of them.
 * An earlier version tried every `<source>` + filename join and accepted ANY tracked
 * hit, which the server never did: it passed `pipes/src/translate-key.pipe.ts` (a tail
 * ng-spark and the former ng-spark-auth package shared) while the server dropped both files. A verifier
 * more lenient than the thing it verifies is a false green. Change the two together:
 * `apps/CodeCoverage/CodeCoverage/Ingestion/PathNormalizer.cs` and `createResolver` here.
 *
 * A report that is missing altogether is the other silent failure: the upload glob
 * matches nothing, nothing complains, and the suite simply is not in the number
 * (the upload action went unmeasured for its whole life that way). EXPECTED_REPORTS
 * turns that into a red build too.
 */
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { globSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

// One slug per app the E2E suite hosts (SparkAppDescriptor.CoverageSlug): Fleet, QnA since #460 M13, HR since #264.
const E2E_HOST_REPORT = /^tests\/MintPlayer\.Spark\.E2E\.Tests\/coverage\/(fleet|qna|hr)-host-[^/]+\/coverage\.cobertura\.xml$/;

/**
 * The marker SparkAppTestHost writes into a host's report directory before it starts the app under
 * dotnet-coverage (`coverage/{slug}-host-{env}-{suffix}/host-started.txt`). It says "this host's
 * tests ran", so a host report is required only for the apps whose tests actually ran: a CI run that
 * filters the QnA (or Fleet) tests out with host coverage on does not fail, while a host that started
 * and then lost its report still does.
 */
export const HOST_STARTED_MARKERS = 'tests/MintPlayer.Spark.E2E.Tests/coverage/*-host-*/host-started.txt';
const HOST_STARTED_MARKER = /^tests\/MintPlayer\.Spark\.E2E\.Tests\/coverage\/([a-z0-9]+)-host-[^/]+\/host-started\.txt$/;

/** The host slugs that started, from the marker paths on disk. */
export function startedHostSlugs(markerPaths) {
  return new Set(markerPaths.map((p) => HOST_STARTED_MARKER.exec(p)?.[1]).filter(Boolean));
}

const hostCoverageOn = (env) => /^(1|true)$/i.test(env.SPARK_E2E_HOST_COVERAGE ?? '');

/**
 * Required when host coverage is on and the host `slug` started. Without marker information
 * (`context.startedHosts` undefined) it stays required: failing closed is the tripwire's purpose.
 */
const hostReportRequired = (slug) => (env, context = {}) =>
  hostCoverageOn(env) && (context.startedHosts === undefined || context.startedHosts.has(slug));

/**
 * Every report CI must produce, one per coverage-producing Nx project. A missing one
 * fails the build.
 *
 * To add a report (for example the E2E subprocess coverage of the Fleet/HR hosts):
 *   1. append an entry here — `name` is what the error message calls it, `glob` is
 *      where the report lands, repo-relative, forward slashes. Optional: `match`, a
 *      RegExp every glob hit must also satisfy (for a shape a glob cannot state);
 *      `exclude`, a RegExp of hits that belong to ANOTHER entry (so one report cannot satisfy
 *      two entries), and `required`, a function deciding at run time whether its
 *      absence fails (for a report CI produces only when switched on);
 *   2. make sure UPLOAD_GLOBS below matches it (the run fails if it does not, since a
 *      report that is verified but never uploaded is the same hole);
 *   3. add the same path to the `files:` list of the "Upload coverage" step in BOTH
 *      .github/workflows/pull-request.yml and dotnet-build-master.yml, and to the
 *      `hashFiles` gate in dotnet-build-master.yml.
 */
export const EXPECTED_REPORTS = [
  { name: 'MintPlayer.Spark.Tests', glob: 'tests/MintPlayer.Spark.Tests/coverage/**/coverage.cobertura.xml' },
  {
    name: 'MintPlayer.Spark.E2E.Tests',
    glob: 'tests/MintPlayer.Spark.E2E.Tests/coverage/**/coverage.cobertura.xml',
    exclude: E2E_HOST_REPORT,
  },
  // The Fleet hosts the E2E tests start as subprocesses, measured by dotnet-coverage
  // (FleetTestHost): one report per host session, no <source>, absolute workspace paths.
  // Required only when CI switches host coverage on (a local E2E run does not produce it) and a
  // Fleet host started (HOST_STARTED_MARKERS).
  {
    name: 'E2E host subprocess coverage',
    glob: 'tests/MintPlayer.Spark.E2E.Tests/coverage/fleet-host-*/coverage.cobertura.xml',
    match: E2E_HOST_REPORT,
    required: hostReportRequired('fleet'),
  },
  // The QnA host (QnATestHost), the same shape. A separate entry, so a run that measured Fleet but
  // lost QnA's report still fails.
  {
    name: 'E2E QnA host subprocess coverage',
    glob: 'tests/MintPlayer.Spark.E2E.Tests/coverage/qna-host-*/coverage.cobertura.xml',
    match: E2E_HOST_REPORT,
    required: hostReportRequired('qna'),
  },
  // The HR host (HRTestHost, #264), the same shape.
  {
    name: 'E2E HR host subprocess coverage',
    glob: 'tests/MintPlayer.Spark.E2E.Tests/coverage/hr-host-*/coverage.cobertura.xml',
    match: E2E_HOST_REPORT,
    required: hostReportRequired('hr'),
  },
  { name: 'MintPlayer.Spark.SourceGenerators.Tests', glob: 'tests/MintPlayer.Spark.SourceGenerators.Tests/coverage/**/coverage.cobertura.xml' },
  { name: 'MintPlayer.Spark.Client.Tests', glob: 'tests/MintPlayer.Spark.Client.Tests/coverage/**/coverage.cobertura.xml' },
  { name: 'CodeCoverage.Tests', glob: 'apps/CodeCoverage/CodeCoverage.Tests/coverage/**/coverage.cobertura.xml' },
  { name: '@mintplayer/ng-spark', glob: 'libs/node_packages/ng-spark/coverage/cobertura-coverage.xml' },
  { name: '@spark-apps/code-coverage (SPA)', glob: 'coverage/@spark-apps/code-coverage/cobertura-coverage.xml' },
  { name: '@mintplayer/coverage-upload-action', glob: 'apps/CodeCoverage/action/coverage/cobertura-coverage.xml' },
];

/**
 * Mirrors the `files:` list of the workflows' "Upload coverage" step: what is verified
 * is what is uploaded. Apps are limited to apps/CodeCoverage — the demo apps are not
 * measured (docs/coverage_increase_PRD.md).
 */
export const UPLOAD_GLOBS = [
  'tests/*/coverage/**/coverage.cobertura.xml',
  'apps/CodeCoverage/*/coverage/**/coverage.cobertura.xml',
  'libs/node_packages/*/coverage/cobertura-coverage.xml',
  'apps/CodeCoverage/CodeCoverage/ClientApp/coverage/cobertura-coverage.xml',
  'coverage/@spark-apps/code-coverage/cobertura-coverage.xml',
  'apps/CodeCoverage/action/coverage/cobertura-coverage.xml',
];

const unify = (p) => p.replace(/\\/g, '/');
const looksAbsolute = (p) => p.startsWith('/') || (p.length >= 2 && p[1] === ':');
const withSlash = (p) => unify(p).replace(/\/+$/, '') + '/';

/**
 * What the upload action does to every `filename` before the server sees it
 * (`apps/CodeCoverage/action/src/paths.ts` `rebasePath`): unify separators, and strip
 * the workspace prefix case-insensitively. `<source>` is left alone.
 */
export function rebasePath(rawPath, repoRoot) {
  const unified = unify(rawPath);
  const prefix = withSlash(repoRoot);
  return unified.toLowerCase().startsWith(prefix.toLowerCase()) ? unified.slice(prefix.length) : unified;
}

function endsWithPath(full, tail) {
  return (
    full.toLowerCase().endsWith(tail.toLowerCase()) &&
    (full.length === tail.length || full[full.length - tail.length - 1] === '/')
  );
}

/**
 * A port of `PathNormalizer`: the resolver for one report, given the uploader's root
 * (the server receives it as `rootDir`), the report's `<source>` roots and the tracked
 * file list. Returns `(rawPath) => { path, matched }`.
 */
export function createResolver(rootDir, sourceRoots, fileList) {
  const root = rootDir == null ? null : withSlash(rootDir);
  const sources = sourceRoots.map(withSlash);
  const files = new Set(fileList.map(unify));
  const bySuffix = new Map();
  for (const f of files) {
    const base = f.slice(f.lastIndexOf('/') + 1).toLowerCase();
    if (!bySuffix.has(base)) bySuffix.set(base, []);
    bySuffix.get(base).push(f);
  }

  const findExact = (p) => {
    if (files.has(p)) return p;
    const lower = p.toLowerCase();
    for (const f of files) if (f.toLowerCase() === lower) return f;
    return null;
  };

  const resolveAgainstSources = (p) => {
    let hit = null;
    for (const source of sources) {
      let relativeRoot = source;
      if (root !== null && relativeRoot.toLowerCase().startsWith(root.toLowerCase()))
        relativeRoot = relativeRoot.slice(root.length);
      if (looksAbsolute(relativeRoot)) continue;
      const candidate = findExact(relativeRoot.replace(/^\/+/, '') + p);
      if (candidate === null) continue;
      if (hit !== null && hit !== candidate) return null;
      hit = candidate;
    }
    return hit;
  };

  return (rawPath) => {
    let p = unify(rawPath);
    let stripped = false;

    // 1. Strip the workspace root.
    if (root !== null && p.toLowerCase().startsWith(root.toLowerCase())) {
      p = p.slice(root.length);
      stripped = true;
    }

    // 2. Strip the first report-declared source root that prefixes it.
    for (const source of sources) {
      if (p.toLowerCase().startsWith(source.toLowerCase())) {
        p = p.slice(source.length);
        stripped = true;
        break;
      }
    }

    const stillAbsolute = looksAbsolute(p);
    p = p.replace(/^\/+/, '');

    // 3. Without a file list only root-stripped relative paths are trusted.
    if (files.size === 0) return { path: p, matched: !stillAbsolute };

    // 3a. A relative filename is relative to its <source>.
    if (!stripped && !stillAbsolute) {
      const joined = resolveAgainstSources(p);
      if (joined !== null) return { path: joined, matched: true };
    }

    const exact = findExact(p);
    if (exact !== null) return { path: exact, matched: true };

    // 4. A UNIQUE tracked file sharing the tail. Two candidates is a drop, not a guess.
    const base = p.slice(p.lastIndexOf('/') + 1).toLowerCase();
    const candidates = (bySuffix.get(base) ?? []).filter((f) => endsWithPath(f, p) || endsWithPath(p, f));
    if (candidates.length === 1) return { path: candidates[0], matched: true };

    return { path: p, matched: false };
  };
}

export function parseReport(xml) {
  const sources = [...xml.matchAll(/<source>([^<]*)<\/source>/g)].map((m) => m[1].trim());
  const filenames = [...xml.matchAll(/\bfilename="([^"]+)"/g)].map((m) => m[1]);
  return { sources, filenames: [...new Set(filenames)] };
}

/**
 * Paths that are *supposed* to resolve to nothing, and must not fail the build.
 *
 * Build output under `obj/` is generated, is gitignored, and therefore can never appear in
 * `git ls-files` — the server drops it for exactly that reason, which is the correct outcome,
 * not a defect. It should never have been uploaded at all: `coverlet.runsettings` asks for
 * `**\/*.g.cs,**\/obj\/**` to be excluded.
 *
 * Measured 2026-09-06: that exclusion DOES NOT WORK for the source generator's `Inject.g.cs`.
 * Separator variants and a separator-free pattern were both tried against a valid settings file
 * and changed nothing; 7 files / 68 lines still arrive, all 100% covered. Without this allowance
 * the tripwire is permanently red, which would make it worthless — a check that always fails
 * teaches people to ignore it, and then it cannot report the thing it exists for.
 *
 * Keep this as narrow as the evidence: `obj/` only. A real source file that stops resolving —
 * coverlet's `<source>` root moving, say — must still fail.
 */
export function isExpectedUnmatched(filename) {
  return /(^|[/\\])obj[/\\]/.test(filename);
}

function trackedFiles(repoRoot) {
  const output = execFileSync('git', ['ls-files'], { cwd: repoRoot, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  return output.split('\n').filter(Boolean);
}

function expand(globs, repoRoot) {
  return [
    ...new Set(
      globs.flatMap((g) => {
        try {
          return globSync(g, { cwd: repoRoot }).map(unify);
        } catch {
          return [];
        }
      }),
    ),
  ].sort();
}

/** The glob hits of one EXPECTED_REPORTS entry, narrowed by its `match` / `exclude`. */
export function filterEntryHits(entry, hits) {
  return hits.filter((f) => (!entry.match || entry.match.test(f)) && !(entry.exclude && entry.exclude.test(f)));
}

/**
 * Whether an entry's absence fails the run; an entry without `required` always does.
 * `context.startedHosts` is the set of E2E host slugs that started (see HOST_STARTED_MARKERS).
 */
export function isRequired(entry, env, context = {}) {
  return entry.required ? entry.required(env, context) : true;
}

/**
 * Which expected reports are absent, and which were found but would not be uploaded.
 * Pure, so the tests can drive it without a filesystem.
 */
export function checkExpected(expected, foundByEntry, uploaded, env = {}, context = {}) {
  const uploadedSet = new Set(uploaded);
  const missing = [];
  const notUploaded = [];
  for (const entry of expected) {
    const found = foundByEntry.get(entry) ?? [];
    if (found.length === 0 && isRequired(entry, env, context)) missing.push(entry);
    for (const f of found) if (!uploadedSet.has(f)) notUploaded.push({ entry, report: f });
  }
  return { missing, notUploaded };
}

const HELP = `Usage: node tools/verify-coverage-paths.mjs [--json] [--dry-run] [--help] [glob ...]

Run from the repository root, after the test targets have written their reports.

  (no glob)   Check that every EXPECTED_REPORTS entry exists and is matched by
              UPLOAD_GLOBS, then resolve every path in every uploaded report with
              the server's rules. A missing report or an unresolvable path fails.
  glob ...    Resolve the paths in these reports only. The expected-report check
              is skipped: it is about what CI uploads, not about ad-hoc files.
  --dry-run   List the expected reports and upload globs and what each matches on
              disk. Reads no report and always exits 0.
  --json      Also print a per-report summary as JSON.
  --help      This text.
`;

function main(argv) {
  if (argv.includes('--help')) {
    console.log(HELP);
    return 0;
  }
  const asJson = argv.includes('--json');
  const dryRun = argv.includes('--dry-run');
  const globs = argv.filter((a) => !a.startsWith('--'));
  const repoRoot = process.cwd();
  const explicit = globs.length > 0;

  const reports = expand(explicit ? globs : UPLOAD_GLOBS, repoRoot);
  const foundByEntry = new Map(EXPECTED_REPORTS.map((e) => [e, filterEntryHits(e, expand([e.glob], repoRoot))]));
  const context = { startedHosts: startedHostSlugs(expand([HOST_STARTED_MARKERS], repoRoot)) };

  if (dryRun) {
    if (!explicit) {
      console.log('Expected reports:');
      for (const [entry, found] of foundByEntry) {
        const state = found.length > 0 ? 'found  ' : isRequired(entry, process.env, context) ? 'MISSING' : 'absent, not required';
        console.log(`  ${state}  ${entry.name}  (${entry.glob})`);
        for (const f of found) console.log(`             ${f}`);
      }
    }
    console.log(`Reports that would be verified${explicit ? '' : ' and uploaded'}: ${reports.length}`);
    for (const r of reports) console.log(`  ${r}`);
    return 0;
  }

  let failed = false;

  if (!explicit) {
    const { missing, notUploaded } = checkExpected(EXPECTED_REPORTS, foundByEntry, reports, process.env, context);
    for (const entry of missing) {
      failed = true;
      console.error(
        `::error::Expected coverage report missing: ${entry.name} (${entry.glob}). Its suite either ` +
          `did not run with coverage or wrote the report elsewhere; either way it is absent from the number.`,
      );
    }
    for (const { entry, report } of notUploaded) {
      failed = true;
      console.error(`::error file=${report}::${entry.name} produced ${report}, but no upload glob matches it.`);
    }
  }

  if (reports.length === 0) {
    console.error('::error::No coverage reports found — nothing was measured.');
    return 1;
  }

  const fileList = trackedFiles(repoRoot);
  const summary = [];

  for (const report of reports) {
    const { sources, filenames } = parseReport(readFileSync(path.join(repoRoot, report), 'utf8'));
    const resolve = createResolver(repoRoot, sources, fileList);
    const unresolvable = filenames.filter(
      (f) => !resolve(rebasePath(f, repoRoot)).matched && !isExpectedUnmatched(f),
    );

    summary.push({ report, sources, total: filenames.length, unresolvable: unresolvable.length });

    if (filenames.length === 0) {
      console.error(`::error::${report} declares no files — nothing was measured.`);
      failed = true;
      continue;
    }

    if (unresolvable.length > 0) {
      failed = true;
      console.error(
        `::error file=${report}::${unresolvable.length} of ${filenames.length} paths resolve to no ` +
          `tracked file, or to several. The server drops these silently, so the uploaded percentage ` +
          `would be measured over a smaller denominator than you think. Declared <source> roots: ` +
          `${sources.join(', ') || '(none)'}. First few unresolvable:`,
      );
      for (const f of unresolvable.slice(0, 10)) console.error(`  ${f}`);
    } else {
      console.log(`OK  ${report} — ${filenames.length} path(s) all resolve.`);
    }
  }

  if (asJson) console.log(JSON.stringify(summary, null, 2));

  if (failed) {
    console.error(
      '\nIf these are generated files, they should be excluded by coverlet.runsettings ' +
        '(ExcludeByFile) rather than uploaded. If they are real source, coverlet\'s <source> ' +
        'root has moved — usually because the set of instrumented assemblies changed. If two ' +
        'packages share a relative path, make the reporter write repo-relative filenames ' +
        '(the vitest configs set cobertura `projectRoot` for exactly that).',
    );
  }
  return failed ? 1 : 0;
}

// Importable for the tests; only the CLI invocation touches the filesystem.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exit(main(process.argv.slice(2)));
}

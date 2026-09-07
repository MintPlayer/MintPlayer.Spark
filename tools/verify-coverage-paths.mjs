#!/usr/bin/env node
/**
 * Verifies that every file path in every Cobertura coverage report will resolve to a
 * tracked file, the same way the ingestion server resolves it.
 *
 *   node tools/verify-coverage-paths.mjs [--json] [glob ...]
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
 * Since `coverlet.runsettings` began excluding `**\/*.g.cs` and `**\/obj\/**`, the
 * expected number of unresolvable paths is zero. Anything else is a real defect —
 * usually coverlet's `<source>` moving because the set of instrumented assemblies
 * changed, which is invisible in the report itself.
 */
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { globSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const DEFAULT_GLOBS = [
  'tests/*/coverage/**/coverage.cobertura.xml',
  'apps/*/*/coverage/**/coverage.cobertura.xml',
  'libs/node_packages/*/coverage/cobertura-coverage.xml',
  'coverage/*/*/cobertura-coverage.xml',
];

/** Repo-root-relative, forward slashes — the shape `git ls-files` prints. */
export function toRepoRelative(candidate, repoRoot) {
  const normalized = candidate.replace(/\\/g, '/');
  const isAbsolute = path.isAbsolute(normalized) || /^[a-zA-Z]:\//.test(normalized);
  const relative = isAbsolute ? path.relative(repoRoot, normalized) : normalized;
  return relative.split(path.sep).join('/').replace(/^\.\//, '');
}

/**
 * Every candidate the server could plausibly resolve this filename to: joined with
 * each declared `<source>`, and bare. Order does not matter — we only ask whether
 * *any* candidate is tracked.
 */
export function resolutionCandidates(filename, sources, repoRoot) {
  const bare = toRepoRelative(filename, repoRoot);
  const joined = sources.map((source) =>
    toRepoRelative(path.posix.join(source.replace(/\\/g, '/').replace(/\/$/, ''), filename.replace(/\\/g, '/')), repoRoot),
  );
  return [bare, ...joined];
}

export function parseReport(xml) {
  const sources = [...xml.matchAll(/<source>([^<]*)<\/source>/g)].map((m) => m[1].trim());
  const filenames = [...xml.matchAll(/\bfilename="([^"]+)"/g)].map((m) => m[1]);
  return { sources, filenames: [...new Set(filenames)] };
}

/** A path is resolvable if any candidate is tracked, exactly or by unique suffix. */
export function isResolvable(candidates, tracked, trackedBySuffix) {
  if (candidates.some((c) => tracked.has(c))) return true;
  return candidates.some((c) => {
    const base = c.slice(c.lastIndexOf('/') + 1);
    const matches = trackedBySuffix.get(base);
    return matches?.some((t) => t === c || t.endsWith(`/${c}`));
  });
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
function isExpectedUnmatched(filename) {
  return /(^|[/\\])obj[/\\]/.test(filename);
}

function trackedFiles(repoRoot) {
  const output = execFileSync('git', ['ls-files'], { cwd: repoRoot, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  const tracked = new Set(output.split('\n').filter(Boolean));
  const bySuffix = new Map();
  for (const file of tracked) {
    const base = file.slice(file.lastIndexOf('/') + 1);
    if (!bySuffix.has(base)) bySuffix.set(base, []);
    bySuffix.get(base).push(file);
  }
  return { tracked, bySuffix };
}

function main(argv) {
  const asJson = argv.includes('--json');
  const globs = argv.filter((a) => !a.startsWith('--'));
  const repoRoot = process.cwd();

  const reports = (globs.length > 0 ? globs : DEFAULT_GLOBS).flatMap((g) => {
    try {
      return globSync(g, { cwd: repoRoot });
    } catch {
      return [];
    }
  });

  if (reports.length === 0) {
    console.log('No coverage reports found — nothing to verify.');
    return 0;
  }

  const { tracked, bySuffix } = trackedFiles(repoRoot);
  const summary = [];
  let failed = false;

  for (const report of reports.sort()) {
    const { sources, filenames } = parseReport(readFileSync(path.join(repoRoot, report), 'utf8'));
    const unresolvable = filenames.filter(
      (f) => !isResolvable(resolutionCandidates(f, sources, repoRoot), tracked, bySuffix) && !isExpectedUnmatched(f),
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
          `tracked file. The server drops these silently, so the uploaded percentage would be ` +
          `measured over a smaller denominator than you think. Declared <source> roots: ` +
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
        'root has moved — usually because the set of instrumented assemblies changed.',
    );
  }
  return failed ? 1 : 0;
}

// Importable for the tests; only the CLI invocation touches the filesystem.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exit(main(process.argv.slice(2)));
}

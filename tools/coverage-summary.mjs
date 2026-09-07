#!/usr/bin/env node
/**
 * Aggregates every Cobertura report in the workspace into one honest number.
 *
 *   node tools/coverage-summary.mjs [--json]
 *
 * Why this is not a one-liner
 * ---------------------------
 * Reports disagree about what a path means. Each declares its own `<source>` root, and those
 * roots differ per suite and cannot be made uniform — measured 2026-09-06, `--settings` does
 * not change them, because the root is the compilation's common path prefix. So `tests/*`
 * reports say `client/MintPlayer.Spark.Client/SparkClient.cs` against a `.../libs/` root while
 * `apps/CodeCoverage/CodeCoverage.Tests` says `apps/...` against the repo root.
 *
 * Joining a filename to the WRONG root, or to no root, silently splits one file into two
 * entries — typically one at 0% and one at nearly 100%. That is not hypothetical: doing this
 * naively during the 2026-09-06 audit produced a total **12 percentage points** too low, and
 * an earlier pass reported ~68% for the same reason. Always join each filename to its own
 * report's `<source>`.
 *
 * Lines are deduplicated per (file, line) with hits taken as the MAX across reports, because
 * several suites legitimately cover the same file and summing would double-count both the
 * numerator and the denominator.
 */
import { readFileSync } from 'node:fs';
import { globSync } from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const repoRoot = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();

const PATTERNS = [
  'tests/*/coverage/**/coverage.cobertura.xml',
  'apps/*/*/coverage/**/coverage.cobertura.xml',
  'libs/node_packages/*/coverage/cobertura-coverage.xml',
  'apps/*/*/ClientApp/coverage/cobertura-coverage.xml',
  'apps/*/action/coverage/cobertura-coverage.xml',
  'coverage/*/*/cobertura-coverage.xml',
];

function unify(p) {
  return p.replace(/\\/g, '/');
}

/** Every `<source>` root a report declares, longest first so the most specific wins. */
function sourcesOf(xml) {
  return [...xml.matchAll(/<source>([^<]*)<\/source>/g)]
    .map((m) => unify(m[1]).replace(/\/+$/, ''))
    .sort((a, b) => b.length - a.length);
}

/**
 * Resolve a report-relative filename to a repo-relative path, using THIS report's roots.
 * Falls back to the raw filename, which is already repo-relative in some producers.
 */
function resolve(filename, sources) {
  const f = unify(filename);
  if (path.posix.isAbsolute(f) || /^[a-zA-Z]:/.test(f)) {
    const root = sources.find((s) => f.toLowerCase().startsWith(s.toLowerCase()));
    if (root) return f.slice(root.length).replace(/^\/+/, '');
  }
  for (const s of sources) {
    const rel = unify(path.posix.relative(unify(repoRoot), s));
    if (rel && !rel.startsWith('..')) return path.posix.join(rel, f);
  }
  return f;
}

const lines = new Map(); // "file\tline" -> hits
const perFile = new Map(); // file -> {covered, total}
let reportCount = 0;

for (const pattern of PATTERNS) {
  for (const rel of globSync(pattern, { cwd: repoRoot })) {
    const xml = readFileSync(path.join(repoRoot, rel), 'utf8');
    const sources = sourcesOf(xml);
    reportCount++;

    // Split per <class>, because filename lives on the class and lines beneath it.
    for (const cls of xml.split('<class ').slice(1)) {
      const nameMatch = cls.match(/filename="([^"]*)"/);
      if (!nameMatch) continue;
      const file = resolve(nameMatch[1], sources);

      for (const m of cls.matchAll(/<line number="(\d+)" hits="(\d+)"/g)) {
        const key = `${file}\t${m[1]}`;
        const hits = Number(m[2]);
        lines.set(key, Math.max(lines.get(key) ?? 0, hits));
      }
    }
  }
}

for (const [key, hits] of lines) {
  const file = key.split('\t')[0];
  const e = perFile.get(file) ?? { covered: 0, total: 0 };
  e.total++;
  if (hits > 0) e.covered++;
  perFile.set(file, e);
}

const total = [...perFile.values()].reduce((a, e) => a + e.total, 0);
const covered = [...perFile.values()].reduce((a, e) => a + e.covered, 0);
const pct = total === 0 ? 0 : (covered / total) * 100;

if (process.argv.includes('--json')) {
  console.log(JSON.stringify({ reports: reportCount, files: perFile.size, total, covered, pct }, null, 2));
} else {
  console.log(`reports  ${reportCount}`);
  console.log(`files    ${perFile.size}`);
  console.log(`lines    ${covered}/${total}`);
  console.log(`coverage ${pct.toFixed(2)}%`);

  const worst = [...perFile.entries()]
    .filter(([, e]) => e.total >= 20)
    .sort((a, b) => a[1].covered / a[1].total - b[1].covered / b[1].total)
    .slice(0, 15);
  console.log('\nlargest gaps (>=20 measurable lines):');
  for (const [file, e] of worst) {
    console.log(`  ${((e.covered / e.total) * 100).toFixed(0).padStart(3)}%  ${String(e.total - e.covered).padStart(4)} uncovered  ${file}`);
  }
}

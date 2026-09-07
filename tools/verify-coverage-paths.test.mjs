import { strict as assert } from 'node:assert';
import { test } from 'node:test';

import { isResolvable, parseReport, resolutionCandidates, toRepoRelative } from './verify-coverage-paths.mjs';

const REPO = 'C:/Repos/MintPlayer.Spark';

function suffixIndex(files) {
  const bySuffix = new Map();
  for (const f of files) {
    const base = f.slice(f.lastIndexOf('/') + 1);
    if (!bySuffix.has(base)) bySuffix.set(base, []);
    bySuffix.get(base).push(f);
  }
  return { tracked: new Set(files), bySuffix };
}

test('toRepoRelative normalises backslashes', () => {
  assert.equal(toRepoRelative('libs\\spark\\Foo.cs', REPO), 'libs/spark/Foo.cs');
});

test('toRepoRelative strips an absolute repo-root prefix', () => {
  assert.equal(toRepoRelative(`${REPO}/libs/spark/Foo.cs`, REPO), 'libs/spark/Foo.cs');
});

test('toRepoRelative handles a Windows absolute path with backslashes', () => {
  assert.equal(toRepoRelative('C:\\Repos\\MintPlayer.Spark\\libs\\spark\\Foo.cs', REPO), 'libs/spark/Foo.cs');
});

test('parseReport extracts sources and de-duplicates filenames', () => {
  const xml = `<coverage><sources><source>${REPO}/libs/</source></sources>` +
    `<class filename="spark/Foo.cs"/><class filename="spark/Foo.cs"/><class filename="spark/Bar.cs"/></coverage>`;
  const { sources, filenames } = parseReport(xml);
  assert.deepEqual(sources, [`${REPO}/libs/`]);
  assert.deepEqual(filenames, ['spark/Foo.cs', 'spark/Bar.cs']);
});

// The real shape of the tests/* reports: <source> roots at libs/, filenames relative to it.
test('a filename resolves when joined with its declared source root', () => {
  const { tracked, bySuffix } = suffixIndex(['libs/spark/MintPlayer.Spark/Services/DatabaseAccess.cs']);
  const candidates = resolutionCandidates(
    'spark/MintPlayer.Spark/Services/DatabaseAccess.cs',
    [`${REPO}/libs/`],
    REPO,
  );
  assert.equal(isResolvable(candidates, tracked, bySuffix), true);
});

// The real shape of the CodeCoverage.Tests report: repo root, backslashes.
test('a backslashed repo-root-relative filename resolves', () => {
  const { tracked, bySuffix } = suffixIndex(['libs/spark/MintPlayer.Spark/Services/DatabaseAccess.cs']);
  const candidates = resolutionCandidates(
    'libs\\spark\\MintPlayer.Spark\\Services\\DatabaseAccess.cs',
    [REPO],
    REPO,
  );
  assert.equal(isResolvable(candidates, tracked, bySuffix), true);
});

test('generated output under obj/ does not resolve', () => {
  const { tracked, bySuffix } = suffixIndex(['libs/spark/MintPlayer.Spark/Services/DatabaseAccess.cs']);
  const candidates = resolutionCandidates(
    'all_features/MintPlayer.Spark.AllFeatures.SourceGenerators/obj/Debug/netstandard2.0/X/TreeValueComparers.g.cs',
    [`${REPO}/libs/`],
    REPO,
  );
  assert.equal(isResolvable(candidates, tracked, bySuffix), false);
});

test('a path naming no tracked file does not resolve', () => {
  const { tracked, bySuffix } = suffixIndex(['libs/spark/Foo.cs']);
  const candidates = resolutionCandidates('libs/spark/Deleted.cs', [], REPO);
  assert.equal(isResolvable(candidates, tracked, bySuffix), false);
});

// Documents why a moved <source> root is a *silent* failure rather than a loud one:
// the server matches by longest suffix, so a path whose tail is still correct keeps
// resolving even when its declared root is wrong. This is deliberately asserted as
// resolvable — it is the server's real behaviour, and the reason this script cannot
// detect a moved root on its own.
test('suffix matching rescues a path whose declared source root has moved', () => {
  const { tracked, bySuffix } = suffixIndex(['libs/spark/MintPlayer.Spark/Services/DatabaseAccess.cs']);
  const candidates = resolutionCandidates(
    'MintPlayer.Spark/Services/DatabaseAccess.cs',
    [`${REPO}/apps/`],
    REPO,
  );
  assert.equal(isResolvable(candidates, tracked, bySuffix), true);
});

// The genuinely dangerous case, and the one the lcov rebaser was written for: two
// tracked files share a suffix, so the server cannot tell them apart and drops both.
// We can only flag that the path is ambiguous, not which file was meant.
test('an ambiguous basename still counts as resolvable but names several files', () => {
  const { tracked, bySuffix } = suffixIndex([
    'apps/CodeCoverage/CodeCoverage/ClientApp/src/main.ts',
    'apps/DemoApp/DemoApp/ClientApp/src/main.ts',
  ]);
  const candidates = resolutionCandidates('src/main.ts', [], REPO);
  assert.equal(isResolvable(candidates, tracked, bySuffix), true);
  assert.equal(bySuffix.get('main.ts').length, 2);
});

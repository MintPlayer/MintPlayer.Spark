import { strict as assert } from 'node:assert';
import { test } from 'node:test';

import {
  EXPECTED_REPORTS,
  UPLOAD_GLOBS,
  checkExpected,
  createResolver,
  filterEntryHits,
  isExpectedUnmatched,
  parseReport,
  rebasePath,
} from './verify-coverage-paths.mjs';

const REPO = 'C:/Repos/MintPlayer.Spark';

/** Resolve the way the server does after the upload action rebased the filename. */
function resolves(filename, sources, files) {
  return createResolver(REPO, sources, files)(rebasePath(filename, REPO)).matched;
}

test('rebasePath normalises backslashes', () => {
  assert.equal(rebasePath('libs\\spark\\Foo.cs', REPO), 'libs/spark/Foo.cs');
});

test('rebasePath strips an absolute repo-root prefix, case-insensitively', () => {
  assert.equal(rebasePath('c:\\Repos\\MintPlayer.Spark\\libs\\spark\\Foo.cs', REPO), 'libs/spark/Foo.cs');
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
  assert.equal(
    resolves('spark/MintPlayer.Spark/Services/DatabaseAccess.cs', [`${REPO}/libs/`], [
      'libs/spark/MintPlayer.Spark/Services/DatabaseAccess.cs',
    ]),
    true,
  );
});

// The real shape of the CodeCoverage.Tests report: repo root, backslashes.
test('a backslashed repo-root-relative filename resolves', () => {
  assert.equal(
    resolves('libs\\spark\\MintPlayer.Spark\\Services\\DatabaseAccess.cs', [REPO], [
      'libs/spark/MintPlayer.Spark/Services/DatabaseAccess.cs',
    ]),
    true,
  );
});

test('generated output under obj/ does not resolve, and is the documented allowance', () => {
  const filename = 'all_features/MintPlayer.Spark.AllFeatures.SourceGenerators/obj/Debug/netstandard2.0/X/TreeValueComparers.g.cs';
  assert.equal(resolves(filename, [`${REPO}/libs/`], ['libs/spark/MintPlayer.Spark/Services/DatabaseAccess.cs']), false);
  assert.equal(isExpectedUnmatched(filename), true);
});

test('a path naming no tracked file does not resolve', () => {
  assert.equal(resolves('libs/spark/Deleted.cs', [], ['libs/spark/Foo.cs']), false);
});

// Documents why a moved <source> root is a *silent* failure rather than a loud one:
// the server matches by longest suffix, so a path whose tail is still correct keeps
// resolving even when its declared root is wrong.
test('suffix matching rescues a path whose declared source root has moved', () => {
  assert.equal(
    resolves('MintPlayer.Spark/Services/DatabaseAccess.cs', [`${REPO}/apps/`], [
      'libs/spark/MintPlayer.Spark/Services/DatabaseAccess.cs',
    ]),
    true,
  );
});

// The server refuses to guess between two files sharing a tail. The previous verifier
// said "resolvable" here, which was the false green on master ddcd8907.
test('an ambiguous tail with no usable source does not resolve', () => {
  assert.equal(
    resolves('src/main.ts', [], [
      'apps/CodeCoverage/CodeCoverage/ClientApp/src/main.ts',
      'apps/DemoApp/DemoApp/ClientApp/src/main.ts',
    ]),
    false,
  );
});

const SHARED_TAIL = [
  'libs/node_packages/ng-spark/pipes/src/translate-key.pipe.ts',
  'libs/node_packages/ng-spark-auth/pipes/src/translate-key.pipe.ts',
];

test('a shared tail resolves through its package source, as the server now joins it', () => {
  const resolve = createResolver(REPO, [`${REPO}/libs/node_packages/ng-spark-auth`], SHARED_TAIL);
  assert.deepEqual(resolve('pipes/src/translate-key.pipe.ts'), {
    path: 'libs/node_packages/ng-spark-auth/pipes/src/translate-key.pipe.ts',
    matched: true,
  });
});

test('a repo-relative filename (cobertura projectRoot) resolves exactly', () => {
  assert.equal(resolves('libs\\node_packages\\ng-spark\\pipes\\src\\translate-key.pipe.ts', [REPO], SHARED_TAIL), true);
});

test('a missing expected report is reported, a present one is not', () => {
  const [present, absent] = EXPECTED_REPORTS;
  const found = new Map([[present, ['tests/MintPlayer.Spark.Tests/coverage/x/coverage.cobertura.xml']]]);
  const { missing, notUploaded } = checkExpected([present, absent], found, [
    'tests/MintPlayer.Spark.Tests/coverage/x/coverage.cobertura.xml',
  ]);
  assert.deepEqual(missing, [absent]);
  assert.deepEqual(notUploaded, []);
});

test('an expected report the upload globs would not send is reported', () => {
  const entry = { name: 'extra', glob: 'somewhere/cobertura.xml' };
  const { notUploaded } = checkExpected([entry], new Map([[entry, ['somewhere/cobertura.xml']]]), []);
  assert.deepEqual(notUploaded, [{ entry, report: 'somewhere/cobertura.xml' }]);
});

const E2E = EXPECTED_REPORTS.find((e) => e.name === 'MintPlayer.Spark.E2E.Tests');
const HOST = EXPECTED_REPORTS.find((e) => e.name === 'E2E host subprocess coverage');
const HOST_REPORT = 'tests/MintPlayer.Spark.E2E.Tests/coverage/fleet-host-1a2b/coverage.cobertura.xml';
const IN_PROCESS_REPORT = 'tests/MintPlayer.Spark.E2E.Tests/coverage/3fd1518e/coverage.cobertura.xml';

test('a host report cannot stand in for the in-process E2E report', () => {
  assert.deepEqual(filterEntryHits(E2E, [HOST_REPORT, IN_PROCESS_REPORT]), [IN_PROCESS_REPORT]);
  assert.deepEqual(filterEntryHits(HOST, [HOST_REPORT, IN_PROCESS_REPORT]), [HOST_REPORT]);
  const { missing } = checkExpected([E2E], new Map([[E2E, filterEntryHits(E2E, [HOST_REPORT])]]), [HOST_REPORT]);
  assert.deepEqual(missing, [E2E]);
});

test('the host report is required only when SPARK_E2E_HOST_COVERAGE is 1 or true', () => {
  const none = new Map([[HOST, []]]);
  assert.deepEqual(checkExpected([HOST], none, [], {}).missing, []);
  assert.deepEqual(checkExpected([HOST], none, [], { SPARK_E2E_HOST_COVERAGE: '0' }).missing, []);
  assert.deepEqual(checkExpected([HOST], none, [], { SPARK_E2E_HOST_COVERAGE: '1' }).missing, [HOST]);
  assert.deepEqual(checkExpected([HOST], none, [], { SPARK_E2E_HOST_COVERAGE: 'True' }).missing, [HOST]);
});

const QNA_HOST = EXPECTED_REPORTS.find((e) => e.name === 'E2E QnA host subprocess coverage');
const QNA_HOST_REPORT = 'tests/MintPlayer.Spark.E2E.Tests/coverage/qna-host-E2E-5e6f/coverage.cobertura.xml';

test('the QnA host report is its own entry, and cannot stand in for the in-process one either', () => {
  assert.deepEqual(filterEntryHits(E2E, [QNA_HOST_REPORT, IN_PROCESS_REPORT]), [IN_PROCESS_REPORT]);
  assert.deepEqual(filterEntryHits(QNA_HOST, [QNA_HOST_REPORT, IN_PROCESS_REPORT]), [QNA_HOST_REPORT]);
  // Fleet's report does not satisfy QnA's entry: a run that lost one host's report still fails.
  const { missing } = checkExpected([QNA_HOST], new Map([[QNA_HOST, []]]), [HOST_REPORT], { SPARK_E2E_HOST_COVERAGE: '1' });
  assert.deepEqual(missing, [QNA_HOST]);
});

test('several host reports are all uploaded by the E2E upload glob', () => {
  const hosts = [HOST_REPORT, HOST_REPORT.replace('1a2b', '3c4d')];
  const { missing, notUploaded } = checkExpected([HOST], new Map([[HOST, hosts]]), hosts, { SPARK_E2E_HOST_COVERAGE: 'true' });
  assert.deepEqual(missing, []);
  assert.deepEqual(notUploaded, []);
  assert.ok(UPLOAD_GLOBS.includes('tests/*/coverage/**/coverage.cobertura.xml'));
});

// The host report's shape: no <source>, absolute workspace paths.
test('an absolute workspace path with no <source> resolves', () => {
  assert.equal(
    resolves(`${REPO}/libs/spark/MintPlayer.Spark/Services/DatabaseAccess.cs`, [], [
      'libs/spark/MintPlayer.Spark/Services/DatabaseAccess.cs',
    ]),
    true,
  );
});

test('no upload glob reaches a demo app', () => {
  for (const g of UPLOAD_GLOBS) assert.doesNotMatch(g, /^apps\/(\*|DemoApp|Fleet|HR|QnA)\//);
});

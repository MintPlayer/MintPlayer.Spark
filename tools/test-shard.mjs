// Runs one shard of a .NET test project, so CI can spread MintPlayer.Spark.Tests (the long pole: ~8 min
// of a ~9 min test step) over parallel jobs.
//
//   SPARK_TEST_SHARD=2/4 node tools/test-shard.mjs <project dir> dotnet test --no-build ...
//
// Without SPARK_TEST_SHARD the command runs unchanged, so local runs and `nx run-many` see no difference.
// With it, `--filter` and a per-shard trx logger are appended:
//
// - The test classes are found in the project's sources (a file with a [Fact] or [Theory]) and packed into
//   N groups of about equal weight, greedily, heaviest first. Weight is the file's test-attribute count
//   ([Fact], [Theory], each [InlineData]); a file that stands up RavenDB or a host costs RAVEN_FACTOR times
//   as much per test. tools/test-shard-weights.json, when present, replaces the estimate with measured
//   seconds per class (written by --timings from the shards' trx files).
// - Shards 1..N-1 run their own classes: `FullyQualifiedName~Ns.Class.`, the trailing dot so that `Foo`
//   does not also select `FooBar`.
// - Shard N runs everything the other shards do NOT select. So a class this parser misses (a generic test
//   class, a nested one, one inheriting all its tests) still runs, in the last shard, and no test can fall
//   through the gaps. A test matched by two shards' patterns runs twice, never zero times.
//
// node tools/test-shard.mjs --plan <project dir> <count>     prints the groups and their weights
// node tools/test-shard.mjs --timings <project dir> <trx...> writes tools/test-shard-weights.json
import { spawn } from 'node:child_process';
import { readdirSync, readFileSync, existsSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const WEIGHTS_FILE = path.join(ROOT, 'tools', 'test-shard-weights.json');

/** Tests that start RavenDB (embedded) or an app host take seconds each; pure unit tests take milliseconds. */
export const RAVEN_FACTOR = 4;
const HEAVY = /\b(SparkTestDriver|RavenTestDriver|SparkSharedDatabase|CoverageRavenTest|WebApplicationFactory|TestServer|SparkAppTestHost|IDocumentStore)\b/;
const TEST_ATTRIBUTE = /\[(Fact|Theory|InlineData)\b/g;
const HAS_TEST = /\[(Fact|Theory)\b/;
const CLASS = /^\s*(?:(?:public|internal|sealed|static|partial|abstract)\s+)*class\s+([A-Za-z_][A-Za-z0-9_]*)/gm;

function* csFiles(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name === 'bin' || entry.name === 'obj' || entry.name === 'coverage') continue;
      yield* csFiles(path.join(dir, entry.name));
    } else if (entry.name.endsWith('.cs')) {
      yield path.join(dir, entry.name);
    }
  }
}

/**
 * One unit per test file: its namespace, the classes that carry test attributes (the text from one class
 * declaration to the next), and its estimated weight. Helper classes and records in the same file are left
 * out, which keeps the filters short; a test class misattributed by this split still runs in the last shard.
 */
export function parseTestFile(source) {
  const tests = source.match(TEST_ATTRIBUTE)?.length ?? 0;
  if (tests === 0) return null;
  const ns = /^\s*namespace\s+([A-Za-z0-9_.]+)/m.exec(source)?.[1];
  if (!ns) return null;
  const declarations = [...source.matchAll(CLASS)];
  const classes = declarations
    .filter((m, i) => HAS_TEST.test(source.slice(m.index, declarations[i + 1]?.index ?? source.length)))
    .map((m) => `${ns}.${m[1]}`);
  if (classes.length === 0) return null;
  return { classes, weight: tests * (HEAVY.test(source) ? RAVEN_FACTOR : 1) };
}

export function discover(projectDir) {
  return [...csFiles(projectDir)]
    .map((file) => parseTestFile(readFileSync(file, 'utf8')))
    .filter(Boolean);
}

/** Measured seconds per class replace the estimate; a class without a measurement keeps its estimate. */
export function applyWeights(units, measured) {
  if (!measured) return units;
  return units.map((u) => {
    const known = u.classes.map((c) => measured[c]).filter((s) => typeof s === 'number');
    return known.length ? { ...u, weight: known.reduce((a, b) => a + b, 0) } : u;
  });
}

/** Longest-processing-time packing into `count` groups; deterministic for a given input. */
export function pack(units, count) {
  const groups = Array.from({ length: count }, () => ({ classes: [], weight: 0 }));
  const sorted = [...units].sort((a, b) => b.weight - a.weight || a.classes[0].localeCompare(b.classes[0]));
  for (const unit of sorted) {
    const lightest = groups.reduce((min, g) => (g.weight < min.weight ? g : min));
    lightest.classes.push(...unit.classes);
    lightest.weight += unit.weight;
  }
  for (const g of groups) g.classes.sort();
  return groups;
}

/** The VSTest filter for shard `index` (1-based) of `groups.length`. */
export function filterFor(groups, index) {
  if (index < groups.length) {
    return groups[index - 1].classes.map((c) => `FullyQualifiedName~${c}.`).join('|');
  }
  return groups
    .slice(0, -1)
    .flatMap((g) => g.classes)
    .map((c) => `FullyQualifiedName!~${c}.`)
    .join('&');
}

export function parseShard(value) {
  const m = /^(\d+)\/(\d+)$/.exec(value ?? '');
  if (!m) return null;
  const index = Number(m[1]);
  const count = Number(m[2]);
  if (count < 1 || index < 1 || index > count) throw new Error(`SPARK_TEST_SHARD=${value}: expected i/n with 1 <= i <= n`);
  return { index, count };
}

function loadWeights() {
  return existsSync(WEIGHTS_FILE) ? JSON.parse(readFileSync(WEIGHTS_FILE, 'utf8')) : null;
}

/** Sums each class's test durations from trx files (VSTest's <UnitTestResult duration="hh:mm:ss.fffffff">). */
export function timingsFromTrx(xmlDocs) {
  const seconds = {};
  for (const xml of xmlDocs) {
    const classOf = new Map();
    for (const m of xml.matchAll(/<UnitTest [^>]*id="([^"]+)"[\s\S]*?<TestMethod [^>]*className="([^"]+)"/g)) classOf.set(m[1], m[2]);
    for (const m of xml.matchAll(/<UnitTestResult [^>]*testId="([^"]+)"[^>]*duration="(\d+):(\d+):([\d.]+)"/g)) {
      const cls = classOf.get(m[1]);
      if (!cls) continue;
      seconds[cls] = (seconds[cls] ?? 0) + Number(m[2]) * 3600 + Number(m[3]) * 60 + Number(m[4]);
    }
  }
  return Object.fromEntries(Object.entries(seconds).sort(([a], [b]) => a.localeCompare(b)).map(([k, v]) => [k, Math.round(v * 100) / 100]));
}

function main(argv) {
  if (argv[0] === '--plan') {
    const groups = pack(applyWeights(discover(path.resolve(argv[1])), loadWeights()), Number(argv[2] ?? 4));
    groups.forEach((g, i) => console.log(`shard ${i + 1}/${groups.length}: weight ${Math.round(g.weight)}, ${g.classes.length} classes`));
    return;
  }
  if (argv[0] === '--timings') {
    const weights = timingsFromTrx(argv.slice(2).map((f) => readFileSync(f, 'utf8')));
    writeFileSync(WEIGHTS_FILE, JSON.stringify(weights, null, 2) + '\n');
    console.log(`Wrote ${Object.keys(weights).length} classes to ${path.relative(ROOT, WEIGHTS_FILE)}`);
    return;
  }

  const [projectDir, command, ...args] = argv;
  const shard = parseShard(process.env.SPARK_TEST_SHARD);
  if (shard) {
    const groups = pack(applyWeights(discover(path.resolve(projectDir)), loadWeights()), shard.count);
    args.push('--filter', filterFor(groups, shard.index));
    args.push('--logger', `trx;LogFileName=shard-${shard.index}-of-${shard.count}.trx`);
    console.log(`Shard ${shard.index}/${shard.count}: ${shard.index < shard.count ? `${groups[shard.index - 1].classes.length} classes` : 'every class the other shards do not run'}.`);
  }
  const child = spawn(command, args, { stdio: 'inherit' });
  child.on('exit', (code, signal) => process.exit(signal ? 1 : code ?? 1));
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2));
}

// tools/test-shard.mjs splits MintPlayer.Spark.Tests over CI jobs. Its one hard promise: every test runs
// in at least one shard, whatever the parser misses. These tests check that promise against the real
// project and the VSTest filter semantics (`~` is "contains", `|` or, `&` and, `!~` "does not contain").
//
// Run: npm run test:tools (node --test over tools/*.test.mjs).
import { strict as assert } from 'node:assert';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { applyWeights, discover, filterFor, pack, parseShard, parseTestFile, timingsFromTrx } from './test-shard.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PROJECT = path.join(ROOT, 'tests', 'MintPlayer.Spark.Tests');

/** Evaluates a filter as VSTest does, for the operators filterFor emits. */
function selects(filter, fqn) {
  const term = (t) => {
    const m = /^FullyQualifiedName(!?~)(.*)$/.exec(t);
    return m[1] === '~' ? fqn.includes(m[2]) : !fqn.includes(m[2]);
  };
  // filterFor emits either an OR of `~` terms (shards 1..n-1) or an AND of `!~` terms (shard n).
  return filter.includes('!~') ? filter.split('&').every(term) : filter.split('|').some(term);
}

test('every discovered test class runs in exactly one of four shards', () => {
  const groups = pack(discover(PROJECT), 4);
  const classes = groups.flatMap((g) => g.classes);
  assert.ok(classes.length > 100, `only ${classes.length} test classes found: the parser broke`);
  const filters = [1, 2, 3, 4].map((i) => filterFor(groups, i));
  for (const cls of classes) {
    const hits = filters.filter((f) => selects(f, `${cls}.SomeTest`)).length;
    assert.ok(hits >= 1, `${cls} runs in no shard`);
  }
});

test('a class the parser never saw still runs, in the last shard', () => {
  const groups = pack(discover(PROJECT), 4);
  const unknown = 'MintPlayer.Spark.Tests.Somewhere.GenericTests`1.Works';
  assert.equal(selects(filterFor(groups, 4), unknown), true);
  for (const i of [1, 2, 3]) assert.equal(selects(filterFor(groups, i), unknown), false);
});

test('a class name that prefixes another does not drag it along', () => {
  const groups = [{ classes: ['Ns.Foo'], weight: 1 }, { classes: ['Ns.FooBar'], weight: 1 }];
  assert.equal(selects(filterFor(groups, 1), 'Ns.FooBar.Test'), false);
  assert.equal(selects(filterFor(groups, 2), 'Ns.FooBar.Test'), true);
});

test('the shards are balanced by weight', () => {
  const groups = pack(discover(PROJECT), 4);
  const weights = groups.map((g) => g.weight);
  assert.ok(Math.max(...weights) / Math.min(...weights) < 1.1, `unbalanced: ${weights.join(', ')}`);
});

test('helper classes in a test file are not test classes', () => {
  const unit = parseTestFile(`namespace A.B;
public class Widgets { [Fact] public void X() {} [Theory] [InlineData(1)] [InlineData(2)] public void Y(int i) {} }
internal sealed record Row(string Id);
internal class Helper { }`);
  assert.deepEqual(unit.classes, ['A.B.Widgets']);
  assert.equal(unit.weight, 4);
});

test('measured seconds replace the estimate', () => {
  const units = [{ classes: ['A.X'], weight: 10 }, { classes: ['A.Y'], weight: 10 }];
  assert.deepEqual(applyWeights(units, { 'A.X': 2.5 }).map((u) => u.weight), [2.5, 10]);
});

test('trx durations are summed per class', () => {
  const trx = `<TestRun><Results>
  <UnitTestResult testId="1" testName="A" duration="00:00:01.5000000" />
  <UnitTestResult testId="2" testName="B" duration="00:01:00.0000000" />
</Results><TestDefinitions>
  <UnitTest name="A" id="1"><TestMethod className="Ns.One" name="A" /></UnitTest>
  <UnitTest name="B" id="2"><TestMethod className="Ns.One" name="B" /></UnitTest>
</TestDefinitions></TestRun>`;
  assert.deepEqual(timingsFromTrx([trx]), { 'Ns.One': 61.5 });
});

test('SPARK_TEST_SHARD is i/n', () => {
  assert.deepEqual(parseShard('2/4'), { index: 2, count: 4 });
  assert.equal(parseShard(''), null);
  assert.equal(parseShard(undefined), null);
  assert.throws(() => parseShard('5/4'));
});

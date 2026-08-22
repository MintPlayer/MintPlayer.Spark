import { describe, expect, it } from 'vitest';
import { parseSelectionRule } from './selection-rule';
import fixture from './selection-rule.fixture.json';

/**
 * Driven by the same fixture as the C# `SelectionRuleParserTests`. If these two ever
 * disagree, a rule means one thing in the button's disabled state and another in the
 * server's 400 — which is exactly the drift Vidyano's own two ports have.
 */
describe('parseSelectionRule', () => {
  const counts = fixture.counts;

  for (const testCase of fixture.cases) {
    const label = testCase.rule === null ? 'null' : `'${testCase.rule}'`;

    if (testCase.valid === false) {
      it(`${label} is malformed, so every count is refused`, () => {
        const predicate = parseSelectionRule(testCase.rule);
        for (const count of counts) {
          expect(predicate(count), `count ${count}`).toBe(false);
        }
      });
      continue;
    }

    it(`${label} matches the fixture${testCase.why ? ` — ${testCase.why}` : ''}`, () => {
      const predicate = parseSelectionRule(testCase.rule);
      counts.forEach((count, i) => {
        expect(predicate(count), `count ${count}`).toBe(testCase.expected![i]);
      });
    });
  }

  it('never throws, whatever it is handed', () => {
    for (const rule of ['', '=', 'X', 'XX', '><', '=-', undefined, null]) {
      expect(() => parseSelectionRule(rule as any)).not.toThrow();
    }
  });
});

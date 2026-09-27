import { describe, expect, it } from 'vitest';
import { EvaluableAttribute, evaluateRules } from './rule-evaluation';
import { ValidationRule } from './validation-rule';

/**
 * The browser-side mirror of the server's `ValidationService`. The contract that matters is parity:
 * a rule the server would ignore must be ignored here too, because a client that rejects what the
 * server accepts blocks the user with no recourse.
 */

const attr = (over: Partial<EvaluableAttribute> = {}): EvaluableAttribute =>
  ({ name: 'Title', ...over });

const withRule = (rule: ValidationRule, over: Partial<EvaluableAttribute> = {}) =>
  attr({ rules: [rule], ...over });

describe('evaluateRules', () => {
  describe('required', () => {
    it.each([null, undefined, '', '   ', []])('reports %j as missing', value => {
      expect(evaluateRules(attr({ isRequired: true }), value)).toEqual([
        { attributeName: 'Title', ruleType: 'required', message: 'Title is required' },
      ]);
    });

    it('short-circuits: a missing required value reports only "required", not its other rules', () => {
      const a = attr({ isRequired: true, rules: [{ type: 'minLength', value: 3 }, { type: 'email' }] });
      const failures = evaluateRules(a, '');
      expect(failures.map(f => f.ruleType)).toEqual(['required']);
    });

    it('treats 0 and false as present values', () => {
      expect(evaluateRules(attr({ isRequired: true }), 0)).toEqual([]);
      expect(evaluateRules(attr({ isRequired: true }), false)).toEqual([]);
    });
  });

  it('skips every other rule when the value is empty and the attribute is optional', () => {
    const a = attr({ rules: [{ type: 'minLength', value: 3 }, { type: 'regex', value: '^x$' }] });
    expect(evaluateRules(a, '')).toEqual([]);
    expect(evaluateRules(a, null)).toEqual([]);
  });

  it('returns nothing for an attribute without rules', () => {
    expect(evaluateRules(attr(), 'anything')).toEqual([]);
  });

  describe('label', () => {
    it('uses the English label when there is one', () => {
      const a = attr({ isRequired: true, label: { en: 'Book title', nl: 'Boektitel' } });
      expect(evaluateRules(a, '')[0].message).toBe('Book title is required');
    });

    it('falls back to the attribute name when the label has no English text', () => {
      const a = attr({ isRequired: true, label: { nl: 'Boektitel' } });
      expect(evaluateRules(a, '')[0].message).toBe('Title is required');
    });
  });

  describe('maxLength / minLength', () => {
    it('reports a value longer than maxLength with the default text', () => {
      expect(evaluateRules(withRule({ type: 'maxLength', value: 3 }), 'abcd')).toEqual([
        { attributeName: 'Title', ruleType: 'maxLength', message: 'Title must be at most 3 characters' },
      ]);
    });

    it('accepts a value exactly at maxLength and exactly at minLength', () => {
      expect(evaluateRules(withRule({ type: 'maxLength', value: 3 }), 'abc')).toEqual([]);
      expect(evaluateRules(withRule({ type: 'minLength', value: 3 }), 'abc')).toEqual([]);
    });

    it('reports a value shorter than minLength', () => {
      expect(evaluateRules(withRule({ type: 'minLength', value: '3' }), 'ab')).toEqual([
        { attributeName: 'Title', ruleType: 'minLength', message: 'Title must be at least 3 characters' },
      ]);
    });

    it('matches the rule type case-insensitively', () => {
      expect(evaluateRules(withRule({ type: 'MAXLENGTH', value: 1 }), 'ab')).toHaveLength(1);
    });

    it('measures the string form of a non-string value', () => {
      expect(evaluateRules(withRule({ type: 'maxLength', value: 2 }), 12345)).toHaveLength(1);
    });

    // The server's TryGetIntValue rejects all of these, so the rule does not apply there. The client
    // used `Number(value)`, which turns null and '' into 0 ("must be at most 0 characters" on every
    // non-empty value) and silently truncates 2.5 to 2.
    it.each([
      ['a non-numeric string', 'lots'],
      ['null', null],
      ['an empty string', ''],
      ['a fractional number', 2.5],
      ['a boolean', true],
      ['undefined', undefined],
    ])('ignores a length rule whose value is %s, as the server does', (_label, value) => {
      expect(evaluateRules(withRule({ type: 'maxLength', value }), 'abc')).toEqual([]);
      expect(evaluateRules(withRule({ type: 'minLength', value }), 'a')).toEqual([]);
    });

    it('accepts an integer written as a string with surrounding whitespace, as int.TryParse does', () => {
      expect(evaluateRules(withRule({ type: 'maxLength', value: ' 2 ' }), 'abc')).toHaveLength(1);
    });
  });

  describe('range', () => {
    it('reports a value below min', () => {
      expect(evaluateRules(withRule({ type: 'range', min: 10, max: 20 }), 5)).toEqual([
        { attributeName: 'Title', ruleType: 'range', message: 'Title must be at least 10' },
      ]);
    });

    it('reports a value above max', () => {
      expect(evaluateRules(withRule({ type: 'range', min: 10, max: 20 }), '25')).toEqual([
        { attributeName: 'Title', ruleType: 'range', message: 'Title must be at most 20' },
      ]);
    });

    it('accepts the bounds themselves', () => {
      expect(evaluateRules(withRule({ type: 'range', min: 10, max: 20 }), 10)).toEqual([]);
      expect(evaluateRules(withRule({ type: 'range', min: 10, max: 20 }), 20)).toEqual([]);
    });

    it('treats a null or absent bound as unbounded on that side', () => {
      const rule = { type: 'range', min: null, max: undefined } as unknown as ValidationRule;
      expect(evaluateRules(withRule(rule), -1e9)).toEqual([]);
      expect(evaluateRules(withRule(rule), 1e9)).toEqual([]);
    });

    it('treats a min of 0 as a real bound', () => {
      expect(evaluateRules(withRule({ type: 'range', min: 0 }), -1)).toHaveLength(1);
    });

    it('ignores a non-numeric value rather than guessing', () => {
      expect(evaluateRules(withRule({ type: 'range', min: 1, max: 2 }), 'abc')).toEqual([]);
    });
  });

  describe('regex', () => {
    it('accepts a matching value', () => {
      expect(evaluateRules(withRule({ type: 'regex', value: '^[A-Z]{3}$' }), 'ABC')).toEqual([]);
    });

    it('reports a non-matching value', () => {
      expect(evaluateRules(withRule({ type: 'regex', value: '^[A-Z]{3}$' }), 'abc')).toEqual([
        { attributeName: 'Title', ruleType: 'regex', message: 'Title is not in the expected format' },
      ]);
    });

    it('stays silent on an unparseable pattern, leaving the server to report it', () => {
      expect(evaluateRules(withRule({ type: 'regex', value: '([a-z' }), 'abc')).toEqual([]);
    });

    it('ignores a rule with an empty or absent pattern', () => {
      expect(evaluateRules(withRule({ type: 'regex', value: '' }), 'abc')).toEqual([]);
      expect(evaluateRules(withRule({ type: 'regex' }), 'abc')).toEqual([]);
    });
  });

  describe('email / url', () => {
    it('accepts a well-formed address and rejects a malformed one', () => {
      expect(evaluateRules(withRule({ type: 'email' }), 'someone@example.com')).toEqual([]);
      expect(evaluateRules(withRule({ type: 'email' }), 'not-an-address')).toEqual([
        { attributeName: 'Title', ruleType: 'email', message: 'Title must be a valid email address' },
      ]);
    });

    it('accepts an http(s) URL and rejects anything else', () => {
      expect(evaluateRules(withRule({ type: 'url' }), 'https://example.com/x')).toEqual([]);
      expect(evaluateRules(withRule({ type: 'url' }), 'ftp://example.com')).toEqual([
        { attributeName: 'Title', ruleType: 'url', message: 'Title must be a valid URL' },
      ]);
    });
  });

  it('prefers the rule\'s own English message over the default text', () => {
    const rule: ValidationRule = { type: 'maxLength', value: 1, message: { en: 'Too long!', nl: 'Te lang!' } };
    expect(evaluateRules(withRule(rule), 'ab')[0].message).toBe('Too long!');
  });

  it('falls back to the default text when the custom message has no English entry', () => {
    const rule: ValidationRule = { type: 'maxLength', value: 1, message: { nl: 'Te lang!' } };
    expect(evaluateRules(withRule(rule), 'ab')[0].message).toBe('Title must be at most 1 characters');
  });

  it('ignores an unknown rule type instead of guessing at it', () => {
    expect(evaluateRules(withRule({ type: 'futureRule', value: 1 }), 'abc')).toEqual([]);
  });

  it('ignores a rule without a type', () => {
    expect(evaluateRules(withRule({ value: 1 } as unknown as ValidationRule), 'abc')).toEqual([]);
  });

  it('collects every failing rule, in rule order', () => {
    const a = attr({ rules: [{ type: 'minLength', value: 5 }, { type: 'email' }, { type: 'maxLength', value: 10 }] });
    expect(evaluateRules(a, 'ab').map(f => f.ruleType)).toEqual(['minLength', 'email']);
  });
});

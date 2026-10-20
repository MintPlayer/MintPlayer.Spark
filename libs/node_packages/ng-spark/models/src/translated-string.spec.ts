import { describe, expect, it } from 'vitest';
import {
  TranslatedString,
  TranslatedStringEntry,
  isCurrentEntry,
  lookupChain,
  resolveTranslation,
  translated,
} from './translated-string';
import fixture from './translated-string.fixture.json';

interface FixtureCase {
  name: string;
  value: TranslatedString;
  ranges: string[];
  defaultLanguage?: string | null;
  asOf?: string;
  expected: string;
}

/**
 * Driven by the same fixture as the C# `TranslatedStringFallbackParityTests`: the server
 * (`TranslatedString.Resolve`) and the client resolve every case to the same text (commerce X17).
 */
describe('TranslatedString fallback chain (shared fixture)', () => {
  const cases = fixture.cases as unknown as FixtureCase[];

  it('loads the fixture', () => {
    expect(fixture.chains.length).toBeGreaterThan(0);
    expect(cases.length).toBeGreaterThan(0);
  });

  for (const c of fixture.chains) {
    it(`lookup chain of '${c.range}'`, () => {
      expect(lookupChain(c.range)).toEqual(c.chain);
    });
  }

  for (const c of cases) {
    it(c.name, () => {
      // Absent: the default 'en' (undefined); null: no default language.
      const defaultLanguage = 'defaultLanguage' in c ? c.defaultLanguage : undefined;
      expect(resolveTranslation(c.value, c.ranges, { defaultLanguage, asOf: c.asOf })).toBe(c.expected);
    });
  }
});

describe('translated', () => {
  it('builds one plain entry per language, in order', () => {
    expect(translated({ en: 'Save', nl: 'Opslaan' })).toEqual({
      Entries: [{ Tag: 'en', Value: 'Save' }, { Tag: 'nl', Value: 'Opslaan' }],
    });
  });

  it('takes any language tag', () => {
    expect(translated({ 'zh-Hant': '儲存' }).Entries[0]).toEqual({ Tag: 'zh-Hant', Value: '儲存' });
  });
});

describe('isCurrentEntry', () => {
  const entry = (extra: Partial<TranslatedStringEntry>): TranslatedStringEntry => ({ Tag: 'nl', Value: 'x', ...extra });

  it('a plain entry is always current', () => {
    expect(isCurrentEntry(entry({}), '2025-06-01')).toBe(true);
  });

  it('a historic entry never is', () => {
    expect(isCurrentEntry(entry({ Kind: 'historic' }), '2025-06-01')).toBe(false);
  });

  it('ValidFrom is inclusive, ValidTo exclusive', () => {
    const window = entry({ ValidFrom: '2025-01-01', ValidTo: '2026-01-01' });
    expect(isCurrentEntry(window, '2024-12-31')).toBe(false);
    expect(isCurrentEntry(window, '2025-01-01')).toBe(true);
    expect(isCurrentEntry(window, '2025-12-31')).toBe(true);
    expect(isCurrentEntry(window, '2026-01-01')).toBe(false);
  });
});

describe('resolveTranslation', () => {
  it('is empty for a missing or empty value', () => {
    expect(resolveTranslation(undefined, 'en')).toBe('');
    expect(resolveTranslation(null, 'en')).toBe('');
    expect(resolveTranslation({ Entries: [] }, 'en')).toBe('');
  });
});

import { describe, expect, it } from 'vitest';
import { ParsedDatePipe } from './parsed-date.pipe';

/**
 * The detail page's date parsing. Its contract is deliberately `queryCellValue`'s, so the grid and
 * the detail page show the same instant: a `Date`, or `null` so the caller keeps its own text.
 */
describe('ParsedDatePipe', () => {
  const pipe = new ParsedDatePipe();

  it('parses an ISO string with an offset to the same instant', () => {
    const result = pipe.transform('2026-12-31T23:59:00-08:00');
    expect(result).toBeInstanceOf(Date);
    expect(result!.toISOString()).toBe('2027-01-01T07:59:00.000Z');
  });

  it('parses an epoch-milliseconds number', () => {
    expect(pipe.transform(0)!.toISOString()).toBe('1970-01-01T00:00:00.000Z');
  });

  it('passes a valid Date through unchanged', () => {
    const d = new Date('2026-05-05T00:00:00Z');
    expect(pipe.transform(d)).toBe(d);
  });

  it.each([
    ['null', null],
    ['undefined', undefined],
    ['an empty string', ''],
    ['unparseable text', 'not a date'],
    ['an invalid Date', new Date('nope')],
    ['an object', { year: 2026 }],
    ['a boolean', true],
  ])('returns null for %s', (_label, value) => {
    expect(pipe.transform(value)).toBeNull();
  });
});

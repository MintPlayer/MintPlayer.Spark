import { describe, expect, it } from 'vitest';
import { asMoney, currencyChoices, currencyFractionDigits, formatMoney } from './money';

/**
 * Money on the client (commerce X4): read the one wire shape, format with Intl.NumberFormat (K11), and
 * never hide precision the server stored.
 */
describe('money', () => {
  describe('asMoney', () => {
    it('reads the wire shape, with the CLR member names', () => {
      expect(asMoney({ Amount: 12.3, Currency: 'EUR' })).toEqual({ Amount: 12.3, Currency: 'EUR' });
    });

    it('also reads a camel-cased value and a numeric string, and upper-cases the code', () => {
      expect(asMoney({ amount: '12.30', currency: 'eur' })).toEqual({ Amount: 12.3, Currency: 'EUR' });
    });

    it('is null for anything that is not money', () => {
      expect(asMoney(null)).toBeNull();
      expect(asMoney(12.3)).toBeNull();
      expect(asMoney({ Amount: 12.3 })).toBeNull();
      expect(asMoney({ Amount: 'abc', Currency: 'EUR' })).toBeNull();
      expect(asMoney({ Amount: '', Currency: 'EUR' })).toBeNull();
    });
  });

  describe('formatMoney', () => {
    it('formats with the currency in the given locale', () => {
      expect(formatMoney({ Amount: 12.3, Currency: 'EUR' }, 'en-US')).toBe('€12.30');
      expect(formatMoney({ Amount: 1230, Currency: 'JPY' }, 'en-US')).toBe('¥1,230');
    });

    it('follows the locale: a Dutch viewer gets a decimal comma', () => {
      expect(formatMoney({ Amount: 12.3, Currency: 'EUR' }, 'nl-BE')).toMatch(/12,30/);
    });

    it('shows every decimal the value carries, even where CLDR shows fewer than ISO 4217', () => {
      // CLDR formats IQD with 0 decimals; ISO 4217 gives it 3, and the server stores all three.
      expect(formatMoney({ Amount: 1.234, Currency: 'IQD' }, 'en-US')).toMatch(/1\.234/);
    });

    it('is empty for a non-value', () => {
      expect(formatMoney(null, 'en-US')).toBe('');
      expect(formatMoney('12.30', 'en-US')).toBe('');
    });

    it('falls back to "amount code" for a code the platform cannot format', () => {
      expect(formatMoney({ Amount: 1.5, Currency: 'X1' }, 'en-US')).toBe('1.5 X1');
    });
  });

  it('knows each currency\'s display decimals from the platform', () => {
    expect(currencyFractionDigits('EUR')).toBe(2);
    expect(currencyFractionDigits('JPY')).toBe(0);
  });

  it('offers the configured currencies plus the ones a value needs, sorted', () => {
    expect(currencyChoices(['usd', 'EUR'], 'GBP', null)).toEqual(['EUR', 'GBP', 'USD']);
  });
});

import { describe, expect, it } from 'vitest';
import { asPackagingLevels, asQuantity, breakdownQuantity, formatQuantity, formatQuantityBreakdown } from './quantity';

/**
 * Quantity on the client (warehouse WX8): read the one wire shape, format with Intl.NumberFormat, and break
 * a quantity down into whole packages the way the server's PackagingLevels.Breakdown does.
 */
describe('quantity', () => {
  const bottles = [{ unit: 'CS', baseQuantity: 12 }, { unit: 'PAL', baseQuantity: 480 }];

  describe('asQuantity', () => {
    it('reads the wire shape, with the CLR member names', () => {
      expect(asQuantity({ Amount: 12, Unit: 'EA' })).toEqual({ Amount: 12, Unit: 'EA' });
    });

    it('also reads a camel-cased value and a numeric string, and upper-cases the unit', () => {
      expect(asQuantity({ amount: '12.5', unit: 'cs' })).toEqual({ Amount: 12.5, Unit: 'CS' });
    });

    it('is null for anything that is not a quantity', () => {
      expect(asQuantity(null)).toBeNull();
      expect(asQuantity(12)).toBeNull();
      expect(asQuantity({ Amount: 12 })).toBeNull();
      expect(asQuantity({ Amount: 'abc', Unit: 'EA' })).toBeNull();
    });
  });

  describe('formatQuantity', () => {
    it('formats the number in the locale and appends the unit', () => {
      expect(formatQuantity({ Amount: 1234.5, Unit: 'EA' }, 'en-US')).toBe('1,234.5 EA');
      expect(formatQuantity({ Amount: 1234.5, Unit: 'KG' }, 'nl-BE')).toBe('1.234,5 KG');
    });

    it('is empty for no value', () => {
      expect(formatQuantity(null)).toBe('');
    });
  });

  describe('breakdownQuantity', () => {
    it('gives whole packages largest first, then the rest in the base unit', () => {
      expect(breakdownQuantity({ Amount: 507, Unit: 'EA' }, 'EA', bottles)).toEqual([
        { Amount: 1, Unit: 'PAL' }, { Amount: 2, Unit: 'CS' }, { Amount: 3, Unit: 'EA' },
      ]);
    });

    it('converts a packaged value through the base unit first', () => {
      expect(breakdownQuantity({ Amount: 2.5, Unit: 'CS' }, 'EA', bottles)).toEqual([
        { Amount: 2, Unit: 'CS' }, { Amount: 6, Unit: 'EA' },
      ]);
    });

    it('breaks a negative quantity down with every part negative, and zero as the base unit', () => {
      expect(breakdownQuantity({ Amount: -13, Unit: 'EA' }, 'EA', bottles)).toEqual([
        { Amount: -1, Unit: 'CS' }, { Amount: -1, Unit: 'EA' },
      ]);
      expect(breakdownQuantity({ Amount: 0, Unit: 'EA' }, 'EA', bottles)).toEqual([{ Amount: 0, Unit: 'EA' }]);
    });

    it('leaves a unit the levels do not know as it is', () => {
      expect(breakdownQuantity({ Amount: 2, Unit: 'KG' }, 'EA', bottles)).toEqual([{ Amount: 2, Unit: 'KG' }]);
    });

    it('formats the parts joined with a plus', () => {
      expect(formatQuantityBreakdown({ Amount: 27, Unit: 'EA' }, 'EA', bottles, 'en-US')).toBe('2 CS + 3 EA');
    });
  });

  it('reads the levels option and drops entries that are not levels', () => {
    expect(asPackagingLevels([{ unit: 'CS', baseQuantity: 12 }, { unit: '', baseQuantity: 1 }, { unit: 'X', baseQuantity: 0 }, null]))
      .toEqual([{ unit: 'CS', baseQuantity: 12 }]);
    expect(asPackagingLevels('CS')).toEqual([]);
  });
});

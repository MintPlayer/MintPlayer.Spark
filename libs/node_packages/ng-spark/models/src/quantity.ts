import { viewerLocale } from './money';

/**
 * A Spark `Quantity` value (warehouse WX8) as it travels: an amount in one unit (`EA`, `CS`, `KG`, ...).
 *
 * The member names are the CLR names, `Amount` and `Unit`, as for {@link Money}: the server stores and
 * sends one shape. {@link asQuantity} also reads the camel-cased spelling.
 *
 * The server is exact and never mixes units: 2 CS and 3 EA only add up through an item's packaging levels.
 * The client formats, breaks a base quantity down into packages for display, and edits; it does no stock
 * arithmetic.
 */
export interface Quantity {
  Amount: number;
  Unit: string;
}

/** One packaging level of an item: how many base units one `unit` holds (a case of 12: `{ unit: 'CS', baseQuantity: 12 }`). */
export interface PackagingLevel {
  unit: string;
  baseQuantity: number;
}

/** The model `dataType` of a Quantity attribute. */
export const QUANTITY_DATA_TYPE = 'Quantity';

/** The core renderer model sync writes on a Quantity attribute that has none. */
export const QUANTITY_RENDERER = 'quantity';

/**
 * Reads a value as {@link Quantity}: `{ Amount, Unit }` (or `{ amount, unit }`), with the amount a number or
 * a numeric string. Anything else, `null` included, is `null`.
 */
export function asQuantity(value: unknown): Quantity | null {
  if (value === null || typeof value !== 'object') return null;
  const record = value as Record<string, unknown>;
  const rawAmount = record['Amount'] ?? record['amount'];
  const rawUnit = record['Unit'] ?? record['unit'];
  const amount = typeof rawAmount === 'number' ? rawAmount
    : typeof rawAmount === 'string' && rawAmount.trim() !== '' ? Number(rawAmount)
    : NaN;
  if (!Number.isFinite(amount) || typeof rawUnit !== 'string' || rawUnit.trim() === '') return null;
  return { Amount: amount, Unit: rawUnit.trim().toUpperCase() };
}

/**
 * Formats a quantity as a localized number and its unit code: `1,234.5 EA` for `en-US`, `1.234,5 EA` for
 * `nl-BE`. Every decimal the value carries is shown. Returns `''` for anything that is not a quantity.
 */
export function formatQuantity(value: unknown, locale: string | string[] = viewerLocale()): string {
  const quantity = asQuantity(value);
  if (!quantity) return '';
  let number: string;
  try {
    number = new Intl.NumberFormat(locale, { maximumFractionDigits: 20 }).format(quantity.Amount);
  } catch {
    number = String(quantity.Amount);
  }
  return `${number} ${quantity.Unit}`;
}

/** Rounds away binary floating-point noise (0.1 + 0.2): quantities are decimal on the server. */
function clean(value: number): number {
  return Math.round(value * 1e9) / 1e9;
}

/**
 * The quantity as whole packages, largest level first, with what is left in the base unit: 27 EA in a case
 * of 12 is `[2 CS, 3 EA]`. The same rule as the server's `PackagingLevels.Breakdown`. A unit the levels do
 * not know, or a value that is not a quantity, gives the value unchanged (or `[]`).
 */
export function breakdownQuantity(value: unknown, baseUnit: string, levels: readonly PackagingLevel[]): Quantity[] {
  const quantity = asQuantity(value);
  if (!quantity) return [];
  const base = baseUnit.trim().toUpperCase();
  const factors = new Map<string, number>([[base, 1]]);
  for (const level of levels) {
    const unit = level.unit?.trim().toUpperCase();
    if (unit && unit !== base && level.baseQuantity > 0) factors.set(unit, level.baseQuantity);
  }
  const factor = factors.get(quantity.Unit);
  if (factor === undefined) return [quantity];

  const total = clean(quantity.Amount * factor);
  const sign = total < 0 ? -1 : 1;
  let remaining = Math.abs(total);
  const parts: Quantity[] = [];
  const ordered = [...factors.entries()]
    .filter(([unit]) => unit !== base)
    .sort((a, b) => b[1] - a[1] || (a[0] < b[0] ? -1 : 1));
  for (const [unit, size] of ordered) {
    const count = Math.floor(clean(remaining / size));
    if (count <= 0) continue;
    parts.push({ Amount: sign * count, Unit: unit });
    remaining = clean(remaining - count * size);
  }
  if (remaining !== 0 || parts.length === 0) parts.push({ Amount: remaining === 0 ? 0 : sign * remaining, Unit: base });
  return parts;
}

/** {@link breakdownQuantity}, formatted and joined: `2 CS + 3 EA`. */
export function formatQuantityBreakdown(
  value: unknown,
  baseUnit: string,
  levels: readonly PackagingLevel[],
  locale: string | string[] = viewerLocale(),
): string {
  return breakdownQuantity(value, baseUnit, levels).map(part => formatQuantity(part, locale)).join(' + ');
}

/**
 * Reads the `levels` renderer option: `[{ "unit": "CS", "baseQuantity": 12 }]`. Entries that are not a
 * unit with a positive base quantity are dropped.
 */
export function asPackagingLevels(value: unknown): PackagingLevel[] {
  if (!Array.isArray(value)) return [];
  return value
    .map(entry => entry as Record<string, unknown> | null)
    .filter((entry): entry is Record<string, unknown> => !!entry && typeof entry === 'object')
    .map(entry => ({ unit: entry['unit'] ?? entry['Unit'], baseQuantity: Number(entry['baseQuantity'] ?? entry['BaseQuantity']) }))
    .filter((entry): entry is PackagingLevel => typeof entry.unit === 'string' && entry.unit.trim() !== ''
      && Number.isFinite(entry.baseQuantity) && entry.baseQuantity > 0);
}

import { Pipe, PipeTransform } from '@angular/core';

/**
 * Parses a wire value into a `Date`, or `null` when it is absent or unparseable.
 *
 * Exists so the detail page and the grid agree. The grid gets its `Date` from `queryCellValue`, which
 * needs a cell and a column and so cannot be reused on a detail attribute; before this, the detail page
 * had no parsing step at all and printed the raw ISO string — `2026-12-31T23:59:00-08:00` in a
 * definition list, while the grid showed `01/01/2027, 08:59` for the same document.
 *
 * The parsing contract is deliberately identical to `queryCellValue`'s: `new Date(...)`, and an
 * unparseable value yields `null` so the caller can fall back to its own text rather than rendering
 * "Invalid Date".
 *
 * Note what `new Date(...)` does to an offset, because it is the whole reason this is a separate step:
 * it collapses the value to an instant and forgets the offset it was written with. That is correct for
 * display — a viewer wants the moment in their own zone — but it means a `Date` can never be the thing
 * you reach for when you need the *originating* wall clock. Read that from the wire value instead.
 */
@Pipe({ name: 'parsedDate', standalone: true, pure: true })
export class ParsedDatePipe implements PipeTransform {
  transform(value: unknown): Date | null {
    if (value == null || value === '') return null;
    if (value instanceof Date) return Number.isNaN(value.getTime()) ? null : value;
    if (typeof value !== 'string' && typeof value !== 'number') return null;

    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? null : parsed;
  }
}

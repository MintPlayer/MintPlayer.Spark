import { Pipe, PipeTransform } from '@angular/core';

/**
 * A displayed value, or `-` when there is none: `null`, `undefined` or an empty (or blank) string. Unlike
 * `value || '-'` it keeps the falsy values that are data: the number `0` (a page at depth 0, a count of 0) and `false`.
 */
export function sparkOrDash(value: unknown): unknown {
  if (value === null || value === undefined) return '-';
  if (typeof value === 'string' && value.trim() === '') return '-';
  return value;
}

@Pipe({ name: 'orDash', standalone: true, pure: true })
export class OrDashPipe implements PipeTransform {
  transform(value: unknown): unknown {
    return sparkOrDash(value);
  }
}

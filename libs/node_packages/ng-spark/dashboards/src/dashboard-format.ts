import { formatNumber } from '@angular/common';

/** How a dashboard value is written: a plain number, bytes (binary units), a percentage, a date, or as is. */
export type SparkDashboardFormat = 'number' | 'bytes' | 'percent' | 'date' | 'datetime' | 'text';

/** A finite number from a wire value (a number or a numeric string), else null. */
export function toSparkNumber(value: unknown): number | null {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  if (typeof value === 'string' && value.trim() !== '') {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return null;
}

/** 1536 → "1.5 KB": binary units, one decimal from KB up; "—" for no value. */
export function formatSparkBytes(bytes: number | null | undefined, locale = 'en'): string {
  if (bytes == null || !Number.isFinite(bytes)) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  let value = bytes;
  let unit = 0;
  while (Math.abs(value) >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return unit === 0 ? `${formatNumber(value, locale, '1.0-0')} B` : `${formatNumber(value, locale, '1.1-1')} ${units[unit]}`;
}

/**
 * A value as a dashboard writes it: "—" for none; numbers by `format` (`decimals` places for `number`/`percent`,
 * default 0); dates in the viewer's locale; anything else as text.
 */
export function formatSparkDashboardValue(value: unknown, format: SparkDashboardFormat | undefined, locale = 'en', decimals?: number): string {
  if (value === null || value === undefined || value === '') return '—';
  switch (format) {
    case 'bytes':
      return formatSparkBytes(toSparkNumber(value), locale);
    case 'percent': {
      const n = toSparkNumber(value);
      const d = decimals ?? 1;
      return n === null ? '—' : `${formatNumber(n, locale, `1.${d}-${d}`)}%`;
    }
    case 'date':
    case 'datetime': {
      const date = new Date(String(value));
      if (Number.isNaN(date.getTime())) return String(value);
      return format === 'date' ? date.toLocaleDateString(locale) : date.toLocaleString(locale);
    }
    case 'text':
      return String(value);
    case 'number':
    default: {
      const n = typeof value === 'boolean' ? null : toSparkNumber(value);
      if (n === null) return String(value);
      const d = decimals ?? 0;
      return formatNumber(n, locale, `1.${d}-${d}`);
    }
  }
}

/** The colour a threshold gives a value: `danger` at or past `danger`, `warning` at or past `warning`, else none. */
export function sparkThresholdColor(
  value: number | null,
  thresholds: { warning?: number; danger?: number; direction?: 'above' | 'below' },
): 'warning' | 'danger' | null {
  if (value === null) return null;
  const past = (limit: number | undefined) =>
    typeof limit === 'number' && (thresholds.direction === 'below' ? value <= limit : value >= limit);
  if (past(thresholds.danger)) return 'danger';
  if (past(thresholds.warning)) return 'warning';
  return null;
}

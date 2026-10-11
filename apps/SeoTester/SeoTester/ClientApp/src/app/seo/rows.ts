/**
 * An AsDetail array's rows as plain records, whichever shape the renderer got: nested persistent objects
 * (`{ attributes: [{ name, value }] }`, the detail page) or plain records (the share page, a grid cell).
 */
export function asRows(value: unknown): Record<string, unknown>[] {
  if (!Array.isArray(value)) return [];
  return value
    .filter((row): row is Record<string, unknown> => !!row && typeof row === 'object')
    .map(row => Array.isArray((row as { attributes?: unknown }).attributes)
      ? Object.fromEntries(((row as { attributes: { name: string; value?: unknown }[] }).attributes).map(a => [a.name, a.value]))
      : row);
}

/** A string field of a row, or `null`. */
export function str(row: Record<string, unknown> | undefined, name: string): string | null {
  const v = row?.[name];
  return typeof v === 'string' && v.length > 0 ? v : null;
}

/** A number field of a row, or `null` (a numeric string counts; empty does not). */
export function num(row: Record<string, unknown> | undefined, name: string): number | null {
  const v = row?.[name];
  if (typeof v === 'number' && Number.isFinite(v)) return v;
  if (typeof v === 'string' && v.trim() !== '' && Number.isFinite(Number(v))) return Number(v);
  return null;
}

/** A list of strings of a row (an empty list for anything else). */
export function strings(row: Record<string, unknown> | undefined, name: string): string[] {
  const v = row?.[name];
  return Array.isArray(v) ? v.filter((s): s is string => typeof s === 'string') : [];
}

// Internal: not exported from the entry point.

/** An option that must be a finite number, else `fallback`. */
export function numberOption(value: unknown, fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}

/** An option that must be a string, else `fallback`. */
export function stringOption(value: unknown, fallback: string): string {
  return typeof value === 'string' ? value : fallback;
}

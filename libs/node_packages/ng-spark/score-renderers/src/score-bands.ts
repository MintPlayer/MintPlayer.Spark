import { Color } from '@mintplayer/ng-bootstrap';

/** A Bootstrap theme colour a band paints with: `bg-*` on a bar, `--bs-*` on a gauge, `text-bg-*` on a grade. */
export type SparkScoreColor = 'primary' | 'secondary' | 'success' | 'danger' | 'warning' | 'info' | 'light' | 'dark';

/**
 * One band of a score scale: it applies to a value `>= min`. The band without a `min` is the floor,
 * applying to everything below the lowest `min`. Bands may be listed in any order; the highest `min`
 * the value reaches wins.
 */
export interface SparkScoreBand {
  /** Inclusive lower bound. Omit for the floor band. */
  min?: number;
  color: SparkScoreColor;
  /** A grade shown next to a gauge ("A"). Ignored by the threshold bar. */
  label?: string;
}

/**
 * The bands a `bands` / `grades` option accepts: a list, or the name of a preset (so a model's
 * `rendererOptions` JSON, which cannot reference a TypeScript constant, can still pick one).
 */
export type SparkScoreBandsOption = readonly SparkScoreBand[] | 'traffic-light' | 'letter-grades';

/**
 * Green from 80, amber from 60, red below: the coverage thresholds the CodeCoverage badge and bar
 * have always used. The default of every score renderer.
 */
export const sparkTrafficLightBands: readonly SparkScoreBand[] = [
  { min: 80, color: 'success' },
  { min: 60, color: 'warning' },
  { color: 'danger' },
];

/** A ≥ 90, B ≥ 80, C ≥ 70, D ≥ 50, else F: the grade scale of a 0–100 score (SeoTester). */
export const sparkLetterGrades: readonly SparkScoreBand[] = [
  { min: 90, color: 'success', label: 'A' },
  { min: 80, color: 'success', label: 'B' },
  { min: 70, color: 'warning', label: 'C' },
  { min: 50, color: 'warning', label: 'D' },
  { color: 'danger', label: 'F' },
];

/** The bands an option names, or `fallback` for an absent or unrecognised one. */
export function resolveSparkScoreBands(option: unknown, fallback: readonly SparkScoreBand[] = sparkTrafficLightBands): readonly SparkScoreBand[] {
  if (option === 'traffic-light') return sparkTrafficLightBands;
  if (option === 'letter-grades') return sparkLetterGrades;
  if (Array.isArray(option) && option.length > 0) return option as SparkScoreBand[];
  return fallback;
}

/** The band `value` falls in: the one with the highest `min` it reaches, else the floor, else none. */
export function sparkScoreBand(value: number | null | undefined, bands: readonly SparkScoreBand[]): SparkScoreBand | null {
  if (value === null || value === undefined || Number.isNaN(value)) return null;
  let best: SparkScoreBand | null = null;
  for (const band of bands) {
    const min = band.min ?? -Infinity;
    if (value >= min && (best === null || min > (best.min ?? -Infinity))) best = band;
  }
  return best;
}

/** The ng-bootstrap {@link Color} of `value`'s band; `secondary` when there is no value or no band. */
export function sparkScoreColor(value: number | null | undefined, bands: readonly SparkScoreBand[] = sparkTrafficLightBands): Color {
  const band = sparkScoreBand(value, bands);
  return band ? Color[band.color] : Color.secondary;
}

/**
 * A cell value as a number, or null for "no value". Numbers pass through, numeric strings parse,
 * and null, undefined, the empty string and anything unparseable are no value. Never 0: an absent
 * measurement must not draw as a real zero.
 */
export function toSparkScoreNumber(raw: unknown): number | null {
  const value = typeof raw === 'number' ? raw : raw === null || raw === undefined || raw === '' ? NaN : Number(raw);
  return Number.isNaN(value) ? null : value;
}

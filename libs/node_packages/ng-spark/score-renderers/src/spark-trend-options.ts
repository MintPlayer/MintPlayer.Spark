import type { TrendPoint } from '@mintplayer/ng-bootstrap/charts/trend';

/** `rendererOptions` of the `spark-trend` renderer. Every member is optional. */
export interface SparkTrendOptions {
  /** `'sparkline'` (default): a compact inline line for a cell. `'chart'`: an interactive chart with axes. */
  display?: 'sparkline' | 'chart';
  /** Fixed scale minimum, e.g. 0 for a percentage. Default: fitted to the data. */
  yMin?: number;
  /** Fixed scale maximum, e.g. 100 for a percentage. Default: fitted to the data. */
  yMax?: number;
  /** Accessible name of the line or chart. */
  label?: string;
  /** Fewer points than this draw the dash: one point is not a trend. Default 2. */
  minPoints?: number;
  /** Sparkline CSS width. Default `'90px'`. */
  width?: string;
  /** Sparkline CSS height. Default `'24px'`. */
  height?: string;
  /** Chart only: a goal line at this value. */
  goal?: number;
  /** Chart only: the goal line's label. */
  goalLabel?: string;
  /** Chart only: the series id. Default `'value'`. */
  seriesId?: string;
  /** Chart only: the series label (legend, tooltip). Default `label`. */
  seriesLabel?: string;
}

/** One point of a trend value: a plain number (x = its index), null for a gap, or an explicit `{ x, y }`. */
export type SparkTrendValuePoint = number | null | { x: number | Date; y: number | null };

/**
 * The value as `{ x, y }` points, or null when it is not a series or has fewer than `minPoints`.
 * A number is a point at its index; an array holding anything else is not a series.
 */
export function toSparkTrendPoints(value: unknown, minPoints = 2): TrendPoint[] | null {
  if (!Array.isArray(value)) return null;
  const points: TrendPoint[] = [];
  for (let i = 0; i < value.length; i++) {
    const point = value[i] as SparkTrendValuePoint;
    if (point === null || typeof point === 'number') {
      points.push({ x: i, y: point });
    } else if (typeof point === 'object' && 'y' in point) {
      points.push({ x: point.x, y: point.y });
    } else {
      return null;
    }
  }
  return points.length >= minPoints ? points : null;
}

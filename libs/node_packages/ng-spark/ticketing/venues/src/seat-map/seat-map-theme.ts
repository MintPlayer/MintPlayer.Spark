import { DEFAULT_SEAT_MAP_COLORS, SeatMapColors } from './seat-map-renderer';

/**
 * The seat-map colours from the theme: `--spark-seat-free`, `--spark-seat-held`, `--spark-seat-sold`,
 * `--spark-seat-unavailable`, `--spark-seat-accessible`, `--spark-seat-selected`, `--spark-seat-highlight`,
 * `--spark-seat-block`, `--spark-seat-block-stroke`, `--spark-seat-decor`, `--spark-seat-stage`, `--spark-seat-text`
 * and `--spark-seat-background` (a canvas cannot use CSS variables itself, so they are read once per theme change).
 * Unset variables keep the defaults, which the component's stylesheet already maps to Bootstrap's for dark mode.
 */
export function readSeatMapColors(element: Element): SeatMapColors {
  const style = getComputedStyle(element);
  const read = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback;
  const d = DEFAULT_SEAT_MAP_COLORS;
  return {
    background: read('--spark-seat-background', d.background),
    free: read('--spark-seat-free', d.free),
    held: read('--spark-seat-held', d.held),
    sold: read('--spark-seat-sold', d.sold),
    unavailable: read('--spark-seat-unavailable', d.unavailable),
    accessible: read('--spark-seat-accessible', d.accessible),
    selected: read('--spark-seat-selected', d.selected),
    highlight: read('--spark-seat-highlight', d.highlight),
    blockFill: read('--spark-seat-block', d.blockFill),
    blockStroke: read('--spark-seat-block-stroke', d.blockStroke),
    decor: read('--spark-seat-decor', d.decor),
    stage: read('--spark-seat-stage', d.stage),
    text: read('--spark-seat-text', d.text),
  };
}

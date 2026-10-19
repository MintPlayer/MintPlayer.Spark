import type { LayoutRow, LayoutSeat } from '../generated/contracts';

/** Row shapes of the generator (PRD §4.11: straight, angled or curved). */
export type SeatRowShape = 'straight' | 'angled' | 'curved';

/** Labelling schemes: rows A, B, … Z, AA, …; or 1, 2, …; seats 1, 2, …; odd (1, 3, 5) or even (2, 4, 6) numbering. */
export type RowLabelScheme = 'letters' | 'numbers';
export type SeatLabelScheme = 'numbers' | 'odd' | 'even';

export interface SeatGeneratorOptions {
  /** The block the rows are for: part of each generated key. */
  blockKey: string;
  rows: number;
  seatsPerRow: number;
  /** The first row's left end (straight, angled) or centre (curved), plan units. */
  originX: number;
  originY: number;
  /** Distance between seat centres in a row, and between rows. */
  seatSpacing: number;
  rowSpacing: number;
  shape: SeatRowShape;
  /** Straight: the block's rotation; angled: how far each half bends back (degrees). */
  angle: number;
  /** Curved: the radius of the first row (the arc's centre lies `radius` before the first row). */
  radius: number;
  rowLabels: RowLabelScheme;
  /** The first row's label index (0 = A or 1). */
  rowStart: number;
  /** Rows are labelled from the back (the last generated row gets the first label). */
  rowsFromBack: boolean;
  seatLabels: SeatLabelScheme;
  seatStart: number;
  /** Seats are numbered from the right end of the row. */
  seatsFromRight: boolean;
  /** Seats added per row for each row further back (curved and angled stands widen). */
  widenBy: number;
}

export const DEFAULT_GENERATOR: Omit<SeatGeneratorOptions, 'blockKey'> = {
  rows: 10,
  seatsPerRow: 20,
  originX: 100,
  originY: 100,
  seatSpacing: 10,
  rowSpacing: 12,
  shape: 'straight',
  angle: 0,
  radius: 200,
  rowLabels: 'letters',
  rowStart: 0,
  rowsFromBack: false,
  seatLabels: 'numbers',
  seatStart: 1,
  seatsFromRight: false,
  widenBy: 0,
};

/** A, B, … Z, AA, AB, … for 0, 1, … */
export function letterLabel(index: number): string {
  let label = '';
  let n = index + 1;
  while (n > 0) {
    const r = (n - 1) % 26;
    label = String.fromCharCode(65 + r) + label;
    n = Math.floor((n - 1) / 26);
  }
  return label;
}

/** A key from free text, as the server's `LayoutCsv.Slug`: letters, digits, `_ . -`, at most 64 characters. */
export function slugKey(text: string): string {
  const slug = text.replace(/[^A-Za-z0-9_.-]/g, '-').replace(/^-+|-+$/g, '') || 'x';
  return slug.slice(0, 64);
}

/**
 * Generates rows of seats (rows × seats; straight, angled or curved; labelling schemes). Seats come out in row order
 * left to right (the single-empty-seat rule reads that order); labels follow the scheme. Keys are
 * `{block}-{row}-{seat}`, made unique against `taken` (the plan's other seat keys).
 */
export function generateRows(options: SeatGeneratorOptions, taken: ReadonlySet<string> = new Set()): LayoutRow[] {
  const used = new Set(taken);
  const rows: LayoutRow[] = [];
  const rad = (options.angle * Math.PI) / 180;
  for (let r = 0; r < options.rows; r++) {
    const count = Math.max(1, options.seatsPerRow + Math.round(r * options.widenBy));
    const labelIndex = options.rowStart + (options.rowsFromBack ? options.rows - 1 - r : r);
    const rowLabel = options.rowLabels === 'letters' ? letterLabel(labelIndex) : String(labelIndex + 1);
    const positions = rowPositions(options, r, count, rad);
    const seats: LayoutSeat[] = positions.map(([x, y], s) => {
      const ordinal = options.seatsFromRight ? count - 1 - s : s;
      const number = options.seatLabels === 'numbers' ? options.seatStart + ordinal
        : options.seatLabels === 'odd' ? options.seatStart * 2 - 1 + ordinal * 2
          : options.seatStart * 2 + ordinal * 2;
      const label = String(number);
      let key = slugKey(`${options.blockKey}-${rowLabel}-${label}`);
      for (let n = 2; used.has(key); n++) key = slugKey(`${options.blockKey}-${rowLabel}-${label}-${n}`);
      used.add(key);
      return { key, label, x: round(x), y: round(y), tags: 0, category: null, companionOf: null };
    });
    rows.push({ key: slugKey(rowLabel), label: rowLabel, seats });
  }
  return rows;
}

function rowPositions(o: SeatGeneratorOptions, r: number, count: number, rad: number): [number, number][] {
  const points: [number, number][] = [];
  const extra = (count - o.seatsPerRow) / 2;
  if (o.shape === 'curved') {
    // Concentric arcs around a centre `radius` before the first row; constant spacing along each arc.
    const radius = Math.max(o.seatSpacing, o.radius + r * o.rowSpacing);
    const step = o.seatSpacing / radius;
    const cx = o.originX, cy = o.originY - o.radius;
    for (let s = 0; s < count; s++) {
      const a = (s - (count - 1) / 2) * step;
      points.push([cx - Math.sin(a) * radius, cy + Math.cos(a) * radius]);
    }
    return points;
  }
  for (let s = 0; s < count; s++) {
    // Along the row, from the left end; a widened row starts further left.
    const along = (s - extra) * o.seatSpacing;
    const back = r * o.rowSpacing;
    let px: number, py: number;
    if (o.shape === 'angled') {
      // Two straight halves bent back by `angle` from the row's middle.
      const half = ((o.seatsPerRow - 1) * o.seatSpacing) / 2;
      const offset = along - half;
      const bend = Math.abs(offset) * Math.tan(Math.abs(rad));
      px = o.originX + half + offset * Math.cos(Math.abs(rad));
      py = o.originY + back + bend;
    } else {
      px = o.originX + along * Math.cos(rad) - back * Math.sin(rad);
      py = o.originY + along * Math.sin(rad) + back * Math.cos(rad);
    }
    points.push([px, py]);
  }
  return points;
}

function round(value: number): number {
  return Math.round(value * 100) / 100;
}

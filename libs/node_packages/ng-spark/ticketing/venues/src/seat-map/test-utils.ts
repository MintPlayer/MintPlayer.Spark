import type { SeatMapBlock, SeatMapPayload } from '../generated/contracts';

// Spec and perf-harness scaffolding (S7), not shipped: a synthetic stadium of `blocksX × blocksY` blocks of
// `rows × seatsPerRow` seats. The default is 8 × 5 blocks of 25 × 50 = 50,000 seats. Erasable TypeScript only.

export interface SyntheticStadium {
  blocksX?: number;
  blocksY?: number;
  rows?: number;
  seatsPerRow?: number;
  pitch?: number;
}

export function syntheticStadium(options: SyntheticStadium = {}): SeatMapPayload {
  const blocksX = options.blocksX ?? 8;
  const blocksY = options.blocksY ?? 5;
  const rowsPerBlock = options.rows ?? 25;
  const seatsPerRow = options.seatsPerRow ?? 50;
  const pitch = options.pitch ?? 10;
  const gap = pitch * 6;
  const blockWidth = seatsPerRow * pitch;
  const blockHeight = rowsPerBlock * pitch;

  const keys: string[] = [], labels: string[] = [], x: number[] = [], y: number[] = [], row: number[] = [], tags: number[] = [], category: number[] = [];
  const rowLabels: string[] = [], rowBlock: number[] = [], rowStart: number[] = [], rowCount: number[] = [];
  const blocks: SeatMapBlock[] = [];

  for (let by = 0; by < blocksY; by++) {
    for (let bx = 0; bx < blocksX; bx++) {
      const b = blocks.length;
      const ox = gap + bx * (blockWidth + gap);
      const oy = gap + by * (blockHeight + gap);
      const seatStart = keys.length;
      const firstRow = rowLabels.length;
      for (let r = 0; r < rowsPerBlock; r++) {
        rowLabels.push(String(r + 1));
        rowBlock.push(b);
        rowStart.push(keys.length);
        rowCount.push(seatsPerRow);
        for (let s = 0; s < seatsPerRow; s++) {
          keys.push(`b${b}-r${r}-s${s}`);
          labels.push(String(s + 1));
          x.push(ox + s * pitch + pitch / 2);
          y.push(oy + r * pitch + pitch / 2);
          row.push(rowLabels.length - 1);
          tags.push(r === 0 && s < 2 ? (s === 0 ? 1 : 2) : 0);
          category.push(r < 8 ? 0 : 1);
        }
      }
      blocks.push({
        key: `b${b}`,
        name: `Block ${b + 1}`,
        kind: 'seatedNumbered',
        polygon: [ox - pitch / 2, oy - pitch / 2, ox + blockWidth + pitch / 2, oy - pitch / 2, ox + blockWidth + pitch / 2, oy + blockHeight + pitch / 2, ox - pitch / 2, oy + blockHeight + pitch / 2],
        capacity: rowsPerBlock * seatsPerRow,
        category: 0,
        seatStart,
        seatCount: keys.length - seatStart,
        rowStart: firstRow,
        rowCount: rowLabels.length - firstRow,
      });
    }
  }

  return {
    snapshotId: 'VenueLayoutSnapshots/synthetic',
    venueName: 'Synthetic stadium',
    width: gap + blocksX * (blockWidth + gap),
    height: gap + blocksY * (blockHeight + gap),
    background: null,
    categories: [{ key: 'cat1', name: 'Category 1', color: '#0ea5e9' }, { key: 'cat2', name: 'Category 2', color: '#a855f7' }],
    blocks,
    decor: [{ key: 'stage', kind: 'stage', points: [0, 0, gap * 2, 0, gap * 2, gap / 2, 0, gap / 2], label: 'Stage', color: null }],
    chains: [],
    rows: { labels: rowLabels, block: rowBlock, seatStart: rowStart, seatCount: rowCount },
    seats: { keys, labels, x, y, row, tags, category },
  };
}

/** A deterministic pseudo-random state mix (about 40% sold, 10% held), as numbers per seat. */
export function syntheticStates(count: number, seed = 7): Uint8Array {
  const states = new Uint8Array(count);
  let s = seed >>> 0;
  for (let i = 0; i < count; i++) {
    s = (s * 1664525 + 1013904223) >>> 0;
    const r = s / 4294967296;
    states[i] = r < 0.4 ? 2 : r < 0.5 ? 1 : 0;
  }
  return states;
}

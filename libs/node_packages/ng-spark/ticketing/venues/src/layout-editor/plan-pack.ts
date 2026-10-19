import type { LayoutPlan, SeatMapBlock, SeatMapPayload } from '../generated/contracts';

/**
 * A plan packed like the server packs a snapshot (`SeatMapPacking.Payload`), so the editor, the approver's diff and
 * the seat map share one renderer. Same order: block by block, row by row.
 */
export function packPlan(plan: LayoutPlan, snapshotId = ''): SeatMapPayload {
  const categoryIndex = new Map(plan.categories.map((c, i) => [c.key, i]));
  const category = (key: string | null | undefined) => (key != null && categoryIndex.has(key) ? categoryIndex.get(key)! : -1);
  const keys: string[] = [], labels: string[] = [], x: number[] = [], y: number[] = [], row: number[] = [], tags: number[] = [], categories: number[] = [];
  const rowLabels: string[] = [], rowBlock: number[] = [], rowStart: number[] = [], rowCount: number[] = [];
  const blocks: SeatMapBlock[] = [];

  plan.blocks.forEach((block, b) => {
    const seatStart = keys.length;
    const firstRow = rowLabels.length;
    if (block.kind === 'seatedNumbered') {
      for (const r of block.rows) {
        rowLabels.push(r.label);
        rowBlock.push(b);
        rowStart.push(keys.length);
        rowCount.push(r.seats.length);
        for (const seat of r.seats) {
          keys.push(seat.key);
          labels.push(seat.label);
          x.push(seat.x);
          y.push(seat.y);
          row.push(rowLabels.length - 1);
          tags.push(seat.tags);
          categories.push(category(seat.category ?? block.category));
        }
      }
    }
    blocks.push({
      key: block.key,
      name: block.name,
      kind: block.kind,
      polygon: block.polygon,
      capacity: block.kind === 'seatedNumbered' ? keys.length - seatStart : block.capacity,
      category: category(block.category),
      seatStart,
      seatCount: keys.length - seatStart,
      rowStart: firstRow,
      rowCount: rowLabels.length - firstRow,
    });
  });

  return {
    snapshotId,
    venueName: '',
    width: plan.width,
    height: plan.height,
    background: plan.background ?? null,
    categories: plan.categories,
    blocks,
    decor: plan.decor,
    chains: plan.chains.map((c) => ({ key: c.key, name: c.name, blocks: c.blocks.map((b) => b.blockKey) })),
    rows: { labels: rowLabels, block: rowBlock, seatStart: rowStart, seatCount: rowCount },
    seats: { keys, labels, x, y, row, tags, category: categories },
  };
}

/** An empty plan. */
export function emptyPlan(width = 1000, height = 700): LayoutPlan {
  return { width, height, background: null, categories: [], blocks: [], decor: [], chains: [] };
}

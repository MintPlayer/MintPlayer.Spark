import { describe, expect, it } from 'vitest';
import type { LayoutPlan } from '../generated/contracts';
import { exportCsv, importCsv, parseCsv } from './layout-csv';
import { addToChain, LayoutHistory, mapSeats, moveSeats, removeBlocks, reorderChain, setCategories, setSeatTag, snap } from './plan-edits';
import { packPlan } from './plan-pack';
import { DEFAULT_GENERATOR, generateRows, letterLabel } from './seat-generator';

// Commerce #521 M14: the layout editor's pure parts (D32): seat generation, CSV, edits with undo, packing.

function planWithBlock(): LayoutPlan {
  return {
    width: 500, height: 400, background: null,
    categories: [{ key: 'cat1', name: 'Cat 1', color: '#ff0000' }],
    blocks: [
      { key: 'A', name: 'A', kind: 'seatedNumbered', polygon: [0, 0, 100, 0, 100, 100, 0, 100], capacity: 0, category: 'cat1',
        rows: generateRows({ ...DEFAULT_GENERATOR, blockKey: 'A', rows: 2, seatsPerRow: 3 }) },
      { key: 'S', name: 'Pit', kind: 'standing', polygon: [200, 0, 300, 0, 300, 100], capacity: 500, category: null, rows: [] },
    ],
    decor: [],
    chains: [{ key: 'c1', name: 'Main', blocks: [] }],
  };
}

describe('seat generator', () => {
  it('labels rows A..Z, AA and seats by scheme, in row order left to right', () => {
    expect([0, 25, 26, 27, 701].map(letterLabel)).toEqual(['A', 'Z', 'AA', 'AB', 'ZZ']);
    const rows = generateRows({ ...DEFAULT_GENERATOR, blockKey: 'B1', rows: 2, seatsPerRow: 4, seatLabels: 'odd' });
    expect(rows.map((r) => r.label)).toEqual(['A', 'B']);
    expect(rows[0].seats.map((s) => s.label)).toEqual(['1', '3', '5', '7']);
    expect(rows[0].seats[0].key).toBe('B1-A-1');
    expect(rows[0].seats[1].x).toBeGreaterThan(rows[0].seats[0].x);
    const fromRight = generateRows({ ...DEFAULT_GENERATOR, blockKey: 'B', rows: 1, seatsPerRow: 3, seatsFromRight: true, rowsFromBack: true });
    expect(fromRight[0].seats.map((s) => s.label)).toEqual(['3', '2', '1']);
  });

  it('keeps keys unique against the plan', () => {
    const rows = generateRows({ ...DEFAULT_GENERATOR, blockKey: 'B', rows: 1, seatsPerRow: 1 }, new Set(['B-A-1']));
    expect(rows[0].seats[0].key).toBe('B-A-1-2');
  });

  it('puts curved rows on concentric arcs with the seat spacing along the arc', () => {
    const rows = generateRows({ ...DEFAULT_GENERATOR, blockKey: 'C', rows: 2, seatsPerRow: 5, shape: 'curved', radius: 100, originX: 0, originY: 100 });
    const centre = { x: 0, y: 0 };
    for (const [r, row] of rows.entries()) {
      for (const seat of row.seats) expect(Math.hypot(seat.x - centre.x, seat.y - centre.y)).toBeCloseTo(100 + r * DEFAULT_GENERATOR.rowSpacing, 1);
    }
    expect(Math.hypot(rows[0].seats[1].x - rows[0].seats[0].x, rows[0].seats[1].y - rows[0].seats[0].y)).toBeCloseTo(DEFAULT_GENERATOR.seatSpacing, 0);
  });

  it('bends the halves of an angled row back symmetrically', () => {
    const [row] = generateRows({ ...DEFAULT_GENERATOR, blockKey: 'D', rows: 1, seatsPerRow: 5, shape: 'angled', angle: 20, originX: 0, originY: 0 });
    expect(row.seats[0].y).toBeCloseTo(row.seats[4].y, 5);
    expect(row.seats[2].y).toBeCloseTo(0, 5);
    expect(row.seats[0].y).toBeGreaterThan(0);
  });
});

describe('layout CSV (D32)', () => {
  it('round-trips the seats, tags and categories', () => {
    const plan = setSeatTag(planWithBlock(), new Set(['A-A-1']), 1, true);
    const csv = exportCsv(plan);
    const cleared: LayoutPlan = { ...plan, blocks: plan.blocks.map((b) => (b.key === 'A' ? { ...b, rows: [] } : b)) };
    const { plan: imported, problems } = importCsv(cleared, csv);
    expect(problems).toEqual([]);
    const a = imported!.blocks.find((b) => b.key === 'A')!;
    expect(a.rows.map((r) => r.label)).toEqual(['A', 'B']);
    expect(a.rows[0].seats[0]).toMatchObject({ key: 'A-A-1', tags: 1 });
    expect(imported!.blocks.find((b) => b.key === 'S')!.capacity).toBe(500);
  });

  it('guards formula injection on export and undoes the guard on import', () => {
    const plan = planWithBlock();
    plan.blocks[0].rows[0].label = '=HYPERLINK("x")';
    const csv = exportCsv(plan);
    expect(csv).toContain(`"'=HYPERLINK(""x"")"`);
    const { plan: back } = importCsv(plan, csv);
    expect(back!.blocks[0].rows[0].label).toBe('=HYPERLINK("x")');
  });

  it('creates a missing block from its seats and refuses a bad line', () => {
    const created = importCsv(planWithBlock(), 'block,row,seat,x,y\r\nNEW,1,1,10,20\r\nNEW,1,2,20,20\r\n');
    expect(created.plan!.blocks.find((b) => b.key === 'NEW')!.polygon).toEqual([0, 10, 30, 10, 30, 30, 0, 30]);
    expect(importCsv(planWithBlock(), 'block,row,seat,x,y\r\nNEW,1,1,abc,20\r\n').problems[0]).toEqual({ path: 'line 2', code: 'ticketingVenues.csv.position' });
    expect(importCsv(planWithBlock(), 'nope\r\n').problems[0].code).toBe('ticketingVenues.csv.header');
    expect(importCsv(planWithBlock(), 'block,row,seat,x,y\r\nS,1,1,1,1\r\n').problems[0].code).toBe('ticketingVenues.csv.notNumbered');
  });

  it('parses quoted fields with commas, quotes and line breaks', () => {
    expect(parseCsv('a,"b,c","d""e","f\ng"\r\n')).toEqual([['a', 'b,c', 'd"e', 'f\ng']]);
  });
});

describe('plan edits and undo', () => {
  it('shares untouched blocks and undoes and redoes by reference', () => {
    const plan = planWithBlock();
    const history = new LayoutHistory(plan);
    const moved = history.apply((p) => moveSeats(p, new Set(['A-A-1']), 5, 0));
    expect(moved.blocks[1]).toBe(plan.blocks[1]);
    expect(moved.blocks[0].rows[1]).toBe(plan.blocks[0].rows[1]);
    expect(moved.blocks[0].rows[0].seats[0].x).toBe(plan.blocks[0].rows[0].seats[0].x + 5);
    expect(history.undo()).toBe(plan);
    expect(history.redo()).toBe(moved);
    // An edit that changes nothing is not recorded.
    history.apply((p) => mapSeats(p, new Set(['nope']), (s) => s));
    expect(history.undo()).toBe(plan);
  });

  it('keeps chains consistent: one chain per block, reorder, removal', () => {
    let plan = planWithBlock();
    plan = { ...plan, chains: [...plan.chains, { key: 'c2', name: 'Other', blocks: [] }] };
    plan = addToChain(plan, 'c1', 'A');
    plan = addToChain(plan, 'c2', 'A');
    expect(plan.chains[0].blocks).toEqual([]);
    expect(plan.chains[1].blocks.map((b) => b.blockKey)).toEqual(['A']);
    plan = addToChain(plan, 'c2', 'B');
    plan = reorderChain(plan, 'c2', 1, 0);
    expect(plan.chains[1].blocks.map((b) => b.blockKey)).toEqual(['B', 'A']);
    plan = removeBlocks(plan, new Set(['A']));
    expect(plan.chains[1].blocks.map((b) => b.blockKey)).toEqual(['B']);
  });

  it('drops references to a removed category', () => {
    const plan = setCategories(planWithBlock(), []);
    expect(plan.blocks[0].category).toBeNull();
  });

  it('snaps to the grid', () => {
    expect(snap(12.4, 5)).toBe(10);
    expect(snap(12.4, 0)).toBe(12.4);
  });
});

describe('packPlan', () => {
  it('packs like the server: seats block by block, row by row, categories by index', () => {
    const payload = packPlan(planWithBlock());
    expect(payload.seats.keys).toHaveLength(6);
    expect(payload.blocks[0]).toMatchObject({ seatStart: 0, seatCount: 6, rowCount: 2, category: 0 });
    expect(payload.blocks[1]).toMatchObject({ kind: 'standing', capacity: 500, seatCount: 0 });
    expect(payload.seats.category.every((c) => c === 0)).toBe(true);
  });
});

import type { LayoutBlock, LayoutCategory, LayoutChain, LayoutDecor, LayoutPlan, LayoutRow, LayoutSeat } from '../generated/contracts';

// The editor's edits (PRD §4.11, D32) as pure functions over an immutable plan: each returns a new plan that shares
// every untouched block, so a 50k-seat plan costs only what an edit touches and undo is keeping the old reference.

/** Undo and redo over plan snapshots (structural sharing makes them cheap). */
export class LayoutHistory {
  private past: LayoutPlan[] = [];
  private future: LayoutPlan[] = [];

  constructor(public present: LayoutPlan, private readonly limit = 200) {}

  get canUndo(): boolean { return this.past.length > 0; }

  get canRedo(): boolean { return this.future.length > 0; }

  /** Applies an edit; an edit that returns the same plan records nothing. */
  apply(edit: (plan: LayoutPlan) => LayoutPlan): LayoutPlan {
    const next = edit(this.present);
    if (next === this.present) return next;
    this.past.push(this.present);
    if (this.past.length > this.limit) this.past.shift();
    this.future = [];
    this.present = next;
    return next;
  }

  undo(): LayoutPlan {
    const previous = this.past.pop();
    if (previous) { this.future.push(this.present); this.present = previous; }
    return this.present;
  }

  redo(): LayoutPlan {
    const next = this.future.pop();
    if (next) { this.past.push(this.present); this.present = next; }
    return this.present;
  }

  /** Starts over from `plan` (after loading or a server-side import). */
  reset(plan: LayoutPlan): void {
    this.past = [];
    this.future = [];
    this.present = plan;
  }
}

/** Rounds to the snapping grid; `grid` 0 is no snapping. */
export function snap(value: number, grid: number): number {
  return grid > 0 ? Math.round(value / grid) * grid : value;
}

export function uniqueKey(base: string, taken: Iterable<string>): string {
  const used = new Set(taken);
  if (!used.has(base)) return base;
  for (let n = 2; ; n++) if (!used.has(`${base}-${n}`)) return `${base}-${n}`;
}

export function addBlock(plan: LayoutPlan, block: LayoutBlock): LayoutPlan {
  return { ...plan, blocks: [...plan.blocks, block] };
}

export function updateBlock(plan: LayoutPlan, key: string, patch: Partial<LayoutBlock>): LayoutPlan {
  const index = plan.blocks.findIndex((b) => b.key === key);
  if (index < 0) return plan;
  const blocks = [...plan.blocks];
  blocks[index] = { ...blocks[index], ...patch };
  return { ...plan, blocks };
}

/** Removes blocks, and drops them from every chain. */
export function removeBlocks(plan: LayoutPlan, keys: ReadonlySet<string>): LayoutPlan {
  if (keys.size === 0) return plan;
  return {
    ...plan,
    blocks: plan.blocks.filter((b) => !keys.has(b.key)),
    chains: plan.chains.map((c) => ({ ...c, blocks: c.blocks.filter((b) => !keys.has(b.blockKey)) })),
  };
}

export function setRows(plan: LayoutPlan, blockKey: string, rows: LayoutRow[]): LayoutPlan {
  return updateBlock(plan, blockKey, { rows });
}

/** Applies `change` to every listed seat, copying only the rows and blocks that hold one. */
export function mapSeats(plan: LayoutPlan, keys: ReadonlySet<string>, change: (seat: LayoutSeat) => LayoutSeat | null): LayoutPlan {
  if (keys.size === 0) return plan;
  let touched = false;
  const blocks = plan.blocks.map((block) => {
    let blockTouched = false;
    const rows = block.rows.map((row) => {
      if (!row.seats.some((s) => keys.has(s.key))) return row;
      blockTouched = true;
      const seats: LayoutSeat[] = [];
      for (const seat of row.seats) {
        if (!keys.has(seat.key)) { seats.push(seat); continue; }
        const next = change(seat);
        if (next) seats.push(next);
      }
      return { ...row, seats };
    }).filter((row) => row.seats.length > 0);
    if (!blockTouched) return block;
    touched = true;
    return { ...block, rows };
  });
  return touched ? { ...plan, blocks } : plan;
}

export function moveSeats(plan: LayoutPlan, keys: ReadonlySet<string>, dx: number, dy: number): LayoutPlan {
  if (dx === 0 && dy === 0) return plan;
  return mapSeats(plan, keys, (s) => ({ ...s, x: round(s.x + dx), y: round(s.y + dy) }));
}

export function removeSeats(plan: LayoutPlan, keys: ReadonlySet<string>): LayoutPlan {
  return mapSeats(plan, keys, () => null);
}

/** Moves blocks with their seats. */
export function moveBlocks(plan: LayoutPlan, keys: ReadonlySet<string>, dx: number, dy: number): LayoutPlan {
  if (keys.size === 0 || (dx === 0 && dy === 0)) return plan;
  return {
    ...plan,
    blocks: plan.blocks.map((b) => !keys.has(b.key) ? b : {
      ...b,
      polygon: b.polygon.map((v, i) => round(v + (i % 2 === 0 ? dx : dy))),
      rows: b.rows.map((r) => ({ ...r, seats: r.seats.map((s) => ({ ...s, x: round(s.x + dx), y: round(s.y + dy) })) })),
    }),
  };
}

/** Sets or clears a seat tag (accessible, companion, restricted view) on the listed seats. */
export function setSeatTag(plan: LayoutPlan, keys: ReadonlySet<string>, tag: number, on: boolean): LayoutPlan {
  return mapSeats(plan, keys, (s) => ({ ...s, tags: on ? s.tags | tag : s.tags & ~tag, companionOf: on || !(tag & 2) ? s.companionOf : null }));
}

export function setSeatCategory(plan: LayoutPlan, keys: ReadonlySet<string>, category: string | null): LayoutPlan {
  return mapSeats(plan, keys, (s) => ({ ...s, category }));
}

export function setCategories(plan: LayoutPlan, categories: LayoutCategory[]): LayoutPlan {
  const keys = new Set(categories.map((c) => c.key));
  const keep = (key: string | null | undefined) => (key && keys.has(key) ? key : null);
  return {
    ...plan,
    categories,
    // A removed category leaves no dangling reference (the server would refuse the plan).
    blocks: plan.blocks.map((b) => {
      const dangling = (b.category && !keys.has(b.category)) || b.rows.some((r) => r.seats.some((s) => s.category && !keys.has(s.category)));
      return dangling ? { ...b, category: keep(b.category), rows: b.rows.map((r) => ({ ...r, seats: r.seats.map((s) => ({ ...s, category: keep(s.category) })) })) } : b;
    }),
  };
}

export function setChains(plan: LayoutPlan, chains: LayoutChain[]): LayoutPlan {
  return { ...plan, chains };
}

/** Moves a block within its chain from `from` to `to` (chain order by dragging, or by the arrow buttons). */
export function reorderChain(plan: LayoutPlan, chainKey: string, from: number, to: number): LayoutPlan {
  const chain = plan.chains.find((c) => c.key === chainKey);
  if (!chain || from === to || from < 0 || to < 0 || from >= chain.blocks.length || to >= chain.blocks.length) return plan;
  const blocks = [...chain.blocks];
  const [moved] = blocks.splice(from, 1);
  blocks.splice(to, 0, moved);
  return setChains(plan, plan.chains.map((c) => (c.key === chainKey ? { ...c, blocks } : c)));
}

/** Adds a numbered block to a chain's end, taking it out of any other chain (a block is in at most one). */
export function addToChain(plan: LayoutPlan, chainKey: string, blockKey: string): LayoutPlan {
  return setChains(plan, plan.chains.map((c) => {
    const without = c.blocks.filter((b) => b.blockKey !== blockKey);
    return c.key === chainKey ? { ...c, blocks: [...without, { blockKey, backToFront: false, reverseSeats: false }] } : { ...c, blocks: without };
  }));
}

export function addDecor(plan: LayoutPlan, decor: LayoutDecor): LayoutPlan {
  return { ...plan, decor: [...plan.decor, decor] };
}

export function removeDecor(plan: LayoutPlan, keys: ReadonlySet<string>): LayoutPlan {
  return keys.size === 0 ? plan : { ...plan, decor: plan.decor.filter((d) => !keys.has(d.key)) };
}

/** Every seat key of the plan. */
export function seatKeys(plan: LayoutPlan): Set<string> {
  const keys = new Set<string>();
  for (const b of plan.blocks) for (const r of b.rows) for (const s of r.seats) keys.add(s.key);
  return keys;
}

function round(value: number): number {
  return Math.round(value * 100) / 100;
}

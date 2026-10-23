import { withoutNarrowerLayouts, type TileLayoutChange, type TileLayouts } from '@mintplayer/ng-bootstrap/tile-manager';
import { SparkBoard, SparkBoardSaveRequest, SparkBoardTile, toTileLayouts } from './spark-board.models';

/**
 * The working copy `spark-board` edits (D9: explicit Save and Cancel). Pure and immutable, so the
 * component only swaps one signal and the spec drives it without a DOM.
 */
export interface SparkBoardDraft {
  tiles: SparkBoardTile[];
  /** Defaults the user removed, so a save can state them again and Add tile can offer them. */
  removed: SparkBoardTile[];
  layouts: TileLayouts;
  /** Counts whose layout the user's document stores, or will once saved. */
  counts: number[];
}

export function startDraft(board: SparkBoard): SparkBoardDraft {
  return {
    tiles: board.tiles.map(t => ({ ...t })),
    removed: board.removed.map(t => ({ ...t })),
    layouts: toTileLayouts(board.layouts),
    counts: [...board.customizedCounts],
  };
}

/** A tile-manager commit: its layouts become the draft's, and the count it was made at is the user's from now on. */
export function commitLayout(draft: SparkBoardDraft, change: Pick<TileLayoutChange, 'columnCount' | 'layouts'>): SparkBoardDraft {
  return { ...draft, layouts: change.layouts, counts: withCount(draft.counts, change.columnCount) };
}

/** "Also apply to smaller screens?" — yes: the narrower stored layouts go, so those widths derive from this one. */
export function applyToNarrower(draft: SparkBoardDraft, columnCount: number): SparkBoardDraft {
  return { ...draft, layouts: withoutNarrowerLayouts(draft.layouts, columnCount) };
}

/** A tile id in the user's id space (`u-…`), unique on this board. */
export function newUserTileId(draft: SparkBoardDraft, random: () => number = Math.random): string {
  const taken = new Set([...draft.tiles, ...draft.removed].map(t => t.id));
  for (;;) {
    const id = `u-${Math.floor(random() * 0x7fffffff).toString(36)}`;
    if (!taken.has(id)) return id;
  }
}

/**
 * Adds a tile. A removed default is restored rather than duplicated; anything else becomes a user
 * tile. It gets no position: the tile manager appends it below the last row at every count.
 */
export function addTile(draft: SparkBoardDraft, tile: Omit<SparkBoardTile, 'id' | 'isDefault'> & { id?: string }): SparkBoardDraft {
  const restored = tile.id ? draft.removed.find(t => t.id === tile.id) : undefined;
  if (restored) {
    return { ...draft, tiles: [...draft.tiles, restored], removed: draft.removed.filter(t => t !== restored) };
  }
  const added: SparkBoardTile = { ...tile, id: newUserTileId(draft), isDefault: false };
  return { ...draft, tiles: [...draft.tiles, added] };
}

/** Removes a tile from the board and from every layout, since the server refuses a position for a tile that is gone. */
export function removeTile(draft: SparkBoardDraft, id: string): SparkBoardDraft {
  const tile = draft.tiles.find(t => t.id === id);
  if (!tile) return draft;
  const layouts: Record<number, TileLayouts[number]> = {};
  for (const [count, entries] of Object.entries(draft.layouts)) layouts[Number(count)] = entries.filter(e => e.id !== id);
  return {
    ...draft,
    tiles: draft.tiles.filter(t => t.id !== id),
    removed: tile.isDefault ? [...draft.removed, { ...tile, customTitle: null }] : draft.removed,
    layouts,
  };
}

/** Renames a tile; an empty title drops the rename. */
export function renameTile(draft: SparkBoardDraft, id: string, title: string): SparkBoardDraft {
  const trimmed = title.trim();
  return { ...draft, tiles: draft.tiles.map(t => t.id === id ? { ...t, customTitle: trimmed || null } : t) };
}

/**
 * The save body. A save states the whole customization: every user tile, every removal, every rename,
 * and the layouts of the counts the document should store. A count reset in this session ("Reset
 * this width") has no layout any more, so it drops out and derives from the default again.
 */
export function toSaveRequest(draft: SparkBoardDraft, changeVector: string | null | undefined): SparkBoardSaveRequest {
  const layouts: Record<number, TileLayouts[number]> = {};
  const visible = new Set(draft.tiles.map(t => t.id));
  for (const count of draft.counts) {
    const entries = draft.layouts[count];
    if (entries) layouts[count] = entries.filter(e => visible.has(e.id)).map(e => ({ id: e.id, position: { ...e.position } }));
  }
  const titles: Record<string, string> = {};
  for (const tile of draft.tiles) if (tile.customTitle) titles[tile.id] = tile.customTitle;
  return {
    changeVector: changeVector ?? null,
    tiles: draft.tiles.filter(t => !t.isDefault).map(t => ({ id: t.id, kind: t.kind, target: { ...t.target }, defaultSize: t.defaultSize ?? null })),
    removed: draft.removed.filter(t => t.isDefault).map(t => t.id),
    titles,
    layouts,
  };
}

function withCount(counts: readonly number[], count: number): number[] {
  return counts.includes(count) ? [...counts] : [...counts, count].sort((a, b) => a - b);
}

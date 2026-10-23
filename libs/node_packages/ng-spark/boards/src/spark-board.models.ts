import type { TileLayouts, TileLayoutSnapshot, TileSize } from '@mintplayer/ng-bootstrap/tile-manager';
import type { TranslatedString } from '@mintplayer/ng-spark/models';

/** The built-in tile kinds of `MintPlayer.Spark.Boards` (#530). An app registers more on both sides. */
export const SPARK_TILE_KINDS = {
  query: 'query',
  count: 'count',
  persistentObject: 'persistentObject',
  programUnit: 'programUnit',
} as const;

/** One tile of a board as the caller may see it (`SparkBoardTileView`). */
export interface SparkBoardTile {
  id: string;
  kind: string;
  /** What the tile shows: `{ query }`, `{ entityType, id }`, `{ programUnit }`, or an app kind's own keys. */
  target: Record<string, string>;
  /** The resolved title: the user's rename, else the board's, else the target's own name. */
  title?: TranslatedString | null;
  /** The user's rename, when there is one. */
  customTitle?: string | null;
  defaultSize?: TileSize | null;
  /** True for a tile the application ships; false for one the user added (`u-…`). */
  isDefault: boolean;
}

/** `GET /spark/boards/{board}`: the caller's board, merged and filtered by the server. */
export interface SparkBoard {
  id: string;
  title?: TranslatedString | null;
  tiles: SparkBoardTile[];
  /** Positions per column count (`TileLayouts`); JSON keys are the counts as strings. */
  layouts: Record<string, TileLayoutSnapshot>;
  /** The counts the caller's own layout stores. A save sends these and the counts edited since. */
  customizedCounts: number[];
  /** The default tiles the caller removed and may still see. */
  removed: SparkBoardTile[];
  /** True when the caller has a stored layout (Reset has something to delete). */
  customized: boolean;
  canCustomize: boolean;
  /** Sent back with the next save; a stale one answers 409. */
  changeVector?: string | null;
}

/** `PUT /spark/boards/{board}`: the whole customization, not a delta. */
export interface SparkBoardSaveRequest {
  changeVector: string | null;
  /** The caller's own tiles (`u-…`), all of them. */
  tiles: { id: string; kind: string; target: Record<string, string>; defaultSize?: TileSize | null }[];
  /** Every default the caller removed. */
  removed: string[];
  /** Renames by tile id. */
  titles: Record<string, string>;
  /** The layouts of the counts the caller's document should store. */
  layouts: Record<number, TileLayoutSnapshot>;
}

/** `GET /spark/boards/{board}/catalog`: what the caller may add. */
export interface SparkBoardCatalogEntry {
  /** The kinds this target can be shown as; the first is the natural one. */
  kinds: string[];
  target: Record<string, string>;
  title?: TranslatedString | null;
  icon?: string | null;
  /** `programUnits` or `queries`. */
  group: string;
}

export interface SparkBoardCatalog {
  entries: SparkBoardCatalogEntry[];
  /** Every kind the server knows. */
  kinds: string[];
}

/** What a tile renderer is told about the board it sits on. */
export interface SparkTileContext {
  boardId: string;
  editing: boolean;
  /** Changes whenever the board reloads; a renderer that fetches re-fetches on it. */
  reloadToken: number;
}

/** The layouts record as the server sends it, with numeric keys. */
export function toTileLayouts(layouts: Record<string, TileLayoutSnapshot> | null | undefined): TileLayouts {
  const result: Record<number, TileLayoutSnapshot> = {};
  for (const [key, entries] of Object.entries(layouts ?? {})) {
    const count = Number(key);
    if (Number.isInteger(count) && count > 0) result[count] = entries.map(e => ({ id: e.id, position: { ...e.position } }));
  }
  return result;
}

import { TranslatedString } from './translated-string';

/**
 * What a query returns: the column metadata once, then one lightweight row per result.
 *
 * Rows used to be full `PersistentObject`s, so every row repeated the complete attribute
 * metadata the client already held from `/spark/types` and never read off the row. More
 * importantly it conflated two things that are not the same: a **row is a projection**, a
 * **persistent object is a document**. Nothing here is entitled to act on a row — every
 * mutating path re-loads by id and re-applies security server-side.
 */
export interface QueryResult {
  columns: QueryColumn[];
  items: QueryResultItem[];
  /** Rows matching the query after filtering, before paging. */
  totalItems: number;
  skip: number;
  take: number;
  typeHints?: Record<string, string>;
}

/**
 * The subset of column metadata a cell needs in order to render.
 *
 * Both {@link QueryColumn} (a query result's column) and `EntityAttributeDefinition` (a detail
 * page's attribute) satisfy this, which is what lets one cell component serve the query grid and
 * the AsDetail sub-table without either side knowing about the other's shape.
 */
export interface SparkCellColumn {
  name: string;
  label?: TranslatedString;
  dataType: string;
  isArray?: boolean;
  renderer?: string;
  rendererOptions?: Record<string, any>;
  referenceType?: string;
  lookupReferenceType?: string;
  asDetailType?: string;
}

/** One column of a query result, sent once rather than repeated on every row. */
export interface QueryColumn extends SparkCellColumn {
  order: number;
  isSortable?: boolean;
  /**
   * Whether the grid draws this column. `false` means the row carries the value but no column is
   * rendered — for a renderer that needs a sibling value (a lock glyph beside a name) without
   * spending a column on it. `showedOn` decides what ships; this decides what is drawn.
   */
  isVisible?: boolean;
  /** The query backing a Reference column, by name — the client's option source. */
  query?: string;
}

/**
 * One row: an id, a display string, and a value per column.
 *
 * Deliberately too weak to act on — no attribute metadata, no `can` block, no etag, because none
 * of those can be trusted from a projection.
 */
export interface QueryResultItem {
  /** Never null and unique within a result; the server refuses anything else. */
  id: string;
  /** What to show when the row is named rather than tabulated (a reference picker's value). */
  breadcrumb?: string;
  values: QueryResultItemValue[];
  /** Presentation hints for the whole row. Keys arrive lower-cased. */
  typeHints?: Record<string, string>;
}

/** One cell. */
export interface QueryResultItemValue {
  /** The {@link QueryColumn.name} this value belongs to. */
  key: string;
  /** Typed JSON, not a string — the server converts on the way out. */
  value?: any;
  /** For a reference cell: the target document's id, so a link needs no second lookup. */
  objectId?: string;
  /** For a reference cell: the target's display string. */
  breadcrumb?: string;
  /** Resolved display labels per referenced id, for a multi-reference cell. */
  breadcrumbs?: Record<string, string | null>;
  typeHints?: Record<string, string>;
}

/** The value in a row for a given column, or undefined when the row carries none. */
export function valueFor(item: QueryResultItem | null | undefined, columnName: string): QueryResultItemValue | undefined {
  return item?.values.find(v => v.key === columnName);
}

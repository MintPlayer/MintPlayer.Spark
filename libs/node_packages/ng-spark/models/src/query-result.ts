import { TranslatedString } from './translated-string';
import { PersistentObject } from './persistent-object';
import { AS_DETAIL_BREADCRUMBS_KEY } from './as-detail-conversions';

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

/**
 * Every shape a renderer's `item` can arrive in.
 *
 * Three, and they are genuinely different objects rather than variations of one: a query grid hands
 * a {@link QueryResultItem}, an AsDetail sub-table hands the flat record `nestedPoToDisplayRow`
 * built, and a detail or form host hands the `PersistentObject` itself. A renderer reused across a
 * grid and an AsDetail table therefore sees two of them, which is why {@link valueFor} reads all
 * three rather than making every app write the branch.
 */
export type SparkRow = QueryResultItem | PersistentObject | Record<string, unknown>;

/**
 * The value a row carries for a column, whatever shape the row is in — or `undefined` when it
 * carries none.
 *
 * ```ts
 * const isPrivate = valueFor(this.item(), 'IsPrivate')?.value === true;
 * ```
 *
 * Always returns the **cell** rather than the bare value, because a reference cell is worth as much
 * for its `objectId` as for its text, and a helper that discarded it would have to be replaced the
 * first time a renderer wanted to link somewhere. `?.value` is the cost, once per call site.
 *
 * ⚠️ A value only reaches a grid row if its attribute is on the **query surface**. To read a sibling
 * the grid does not draw, mark it `"showedOn": "Query", "isVisible": false` — shipped, not drawn. An
 * attribute marked `"showedOn": "PersistentObject"` is absent from a row by design, and this returns
 * `undefined` for it; that is the first thing to check when a sibling read comes back empty.
 */
export function valueFor(item: SparkRow | null | undefined, columnName: string): QueryResultItemValue | undefined {
  if (!item) return undefined;

  if (isQueryRow(item)) {
    return item.values.find(v => v.key === columnName);
  }

  if (isPersistentObject(item)) {
    const attribute = item.attributes.find(a => a.name === columnName);
    if (!attribute) return undefined;
    return {
      key: attribute.name,
      value: attribute.value,
      // A single reference's value IS the target's id, which is what the row shape carries
      // separately. Deriving it here keeps a renderer from having to know that.
      objectId: attribute.dataType === 'Reference' && !attribute.isArray && attribute.value != null
        ? String(attribute.value)
        : undefined,
      breadcrumb: attribute.breadcrumb,
      breadcrumbs: attribute.breadcrumbs,
    };
  }

  const record = item as Record<string, unknown>;
  if (!(columnName in record)) return undefined;

  const value = record[columnName];
  // The flat row's side channel is populated only for single references, so its presence for this
  // key is what identifies one — the record itself carries no dataType to ask.
  const breadcrumb = (record[AS_DETAIL_BREADCRUMBS_KEY] as Record<string, string> | undefined)?.[columnName];

  return {
    key: columnName,
    value,
    objectId: breadcrumb !== undefined && value != null ? String(value) : undefined,
    breadcrumb,
  };
}

/**
 * Whether this row is a query result item.
 *
 * Discriminated on the *elements*, not just on `Array.isArray(values)`: a flat AsDetail record may
 * legitimately have a column named `values` holding an array, and mistaking one for the other would
 * silently read the wrong thing.
 */
export function isQueryRow(row: SparkRow | null | undefined): row is QueryResultItem {
  const values = (row as QueryResultItem | undefined)?.values;
  return Array.isArray(values) && (values.length === 0 || typeof values[0]?.key === 'string');
}

/** Whether this row is a full persistent object. Discriminated on elements, as above. */
export function isPersistentObject(row: SparkRow | null | undefined): row is PersistentObject {
  const attributes = (row as PersistentObject | undefined)?.attributes;
  return Array.isArray(attributes) && (attributes.length === 0 || typeof attributes[0]?.name === 'string');
}

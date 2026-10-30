import { QueryColumn } from './query-result';
import { SparkSelectionModeSetting } from './selection-mode';
import { TranslatedString } from './translated-string';

export type SparkQueryRenderMode = 'Pagination' | 'VirtualScrolling';

export interface SparkQuerySortColumn {
  property: string;
  direction: string;
}

export interface SparkQuery {
  id: string;
  name: string;
  /** The query's display name, resolved by the server (#467; formerly `description`). */
  label?: TranslatedString;
  source: string;
  alias?: string;
  sortColumns: SparkQuerySortColumn[];
  renderMode?: SparkQueryRenderMode;
  /** The RavenDB index this query runs against, resolved by name server-side. */
  indexName?: string;
  /** Optional entity type name (e.g., "Person"). When set, used for entity type resolution. */
  entityType?: string;
  /**
   * Whether grids showing this query follow it over `/spark/live` (X4): the page re-renders when a row in
   * it changes, appears or disappears, with server-side paging, sort, search and filters intact. Opt-in
   * per query; a grid's `[live]` input overrides it. See `docs/guide-live-updates.md`.
   */
  live?: boolean;
  /**
   * Whether the grid offers row selection (#460, D17). Absent is `'auto'`: derived from the offered
   * actions. A sub-query entry on the parent's type may override it.
   */
  selectionMode?: SparkSelectionModeSetting;
  /**
   * The row type's attribute that points at the parent when the query is a sub-query — only needed
   * when the row type references the parent's type more than once (#460, D19). Server-side use.
   */
  parentReference?: string;
  /**
   * The query's column order by attribute name (#284). A `!` prefix (`"!Account"`) keeps the column
   * listed but hidden. The server applies it to the result's `columns`; the grid does not read it.
   */
  columnOrder?: string[];
  /**
   * The columns the query's result will ship, sent by `queries/get` so a grid draws its cells before
   * its first fetch (docs/spark_ssr_PRD.md D13). Exact for a `Database.*` query; for a `Custom.*` one the
   * result's columns may narrow `canSort`/`canFilter`. Absent when the server could not describe them.
   */
  resultColumns?: QueryColumn[];
}

/**
 * Which rows a query asks for with respect to soft deletion (#460). Core carries it to every row
 * policy; the SoftDelete package's policy honours it only for holders of `ViewDeleted` on the type.
 * Omitted means `'exclude'`.
 */
export type SparkDeletedFilter = 'exclude' | 'include' | 'only';

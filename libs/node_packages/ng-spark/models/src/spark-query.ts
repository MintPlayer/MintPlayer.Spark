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
  description?: TranslatedString;
  source: string;
  alias?: string;
  sortColumns: SparkQuerySortColumn[];
  renderMode?: SparkQueryRenderMode;
  /** The RavenDB index this query runs against, resolved by name server-side. */
  indexName?: string;
  /** Optional entity type name (e.g., "Person"). When set, used for entity type resolution. */
  entityType?: string;
  /** When true, this query uses WebSocket streaming with snapshot + patch updates. */
  isStreamingQuery?: boolean;
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
}

/**
 * Which rows a query asks for with respect to soft deletion (#460). Core carries it to every row
 * policy; the SoftDelete package's policy honours it only for holders of `ViewDeleted` on the type.
 * Omitted means `'exclude'`.
 */
export type SparkDeletedFilter = 'exclude' | 'include' | 'only';

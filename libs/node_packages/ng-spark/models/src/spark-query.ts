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
   * Custom actions to offer on this query, narrowing the entity type's query-side actions.
   * Undefined offers all of them. Narrows DISPLAY only — the grant is the gate, enforced
   * server-side per action.
   */
  actions?: string[];
  /** Registered query-chrome component rendering this query's header, replacing caption and action bar. */
  headerRenderer?: string;
  /** Opaque options handed to the {@link headerRenderer} component. */
  headerRendererOptions?: Record<string, unknown>;
  /**
   * Whether rows are documents with a detail page, controlling the first column's automatic
   * link. Undefined means yes — only the query author knows when rows are fabricated.
   */
  rowsNavigable?: boolean;
}

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
}

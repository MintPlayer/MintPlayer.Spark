import { TranslatedString } from './translated-string';

export interface SparkQuery {
  id: string;
  name: string;
  description?: TranslatedString;
  source: string;
  alias?: string;
  sortBy?: string;
  sortDirection: string;
  /** Optional RavenDB index name for queries using indexes */
  indexName?: string;
  /** When true, uses the projection type from [QueryType] attribute */
  useProjection?: boolean;
  /** Optional entity type name (e.g., "Person"). When set, used for entity type resolution. */
  entityType?: string;
  /** When true, this query uses WebSocket streaming with snapshot + patch updates. */
  isStreamingQuery?: boolean;
}

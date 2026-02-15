export interface SparkQuery {
  id: string;
  name: string;
  contextProperty: string;
  alias?: string;
  sortBy?: string;
  sortDirection: string;
  /** Optional RavenDB index name for queries using indexes */
  indexName?: string;
  /** When true, uses the projection type from [QueryType] attribute */
  useProjection?: boolean;
}

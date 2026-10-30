import type { SortColumn } from '@mintplayer/ng-bootstrap/datatable';
import { SparkQuery } from '@mintplayer/ng-spark/models';

/** Page sizes offered by every Spark grid. */
export const SPARK_GRID_PAGE_SIZES = [10, 25, 50];

/** Where a grid's paging and sorting start. */
export interface SparkGridSettings {
  page: number;
  perPage: number;
  sortColumns: SortColumn[];
}

/**
 * Initial datatable settings for a query, seeded with the query's declared sort.
 *
 * The datatable owns paging and sorting from here on and calls the fetch callback per page; this
 * only decides where it starts.
 */
export function initialGridSettings(query: SparkQuery | null): SparkGridSettings {
  // Nullable because one caller resolves its query from a route param and may not have one yet;
  // an unsorted grid is the right fallback, not a crash.
  const sortColumns: SortColumn[] = (query?.sortColumns || []).map(sc => ({
    property: sc.property,
    direction: sc.direction === 'desc' ? 'descending' as const : 'ascending' as const,
  }));

  return { page: 1, perPage: SPARK_GRID_PAGE_SIZES[0], sortColumns };
}

/** Whether a query renders as a virtual-scrolling grid rather than a paged one. */
export function isVirtualScrollingQuery(query: SparkQuery | null): boolean {
  return query?.renderMode === 'VirtualScrolling';
}

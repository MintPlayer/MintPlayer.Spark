import { DatatableSettings } from '@mintplayer/ng-bootstrap/datatable';
import { SortColumn } from '@mintplayer/pagination';
import { SparkQuery } from '@mintplayer/ng-spark/models';

/** Page sizes offered by every Spark grid. */
export const SPARK_GRID_PAGE_SIZES = [10, 25, 50];

/**
 * Initial datatable settings for a query, seeded with the query's declared sort.
 *
 * The datatable owns paging and sorting from here on and calls the fetch callback per page; this
 * only decides where it starts.
 */
export function initialGridSettings(query: SparkQuery | null): DatatableSettings {
  // Nullable because one caller resolves its query from a route param and may not have one yet;
  // an unsorted grid is the right fallback, not a crash.
  const sortColumns: SortColumn[] = (query?.sortColumns || []).map(sc => ({
    property: sc.property,
    direction: sc.direction === 'desc' ? 'descending' as const : 'ascending' as const,
  }));

  return new DatatableSettings({
    perPage: { values: SPARK_GRID_PAGE_SIZES, selected: SPARK_GRID_PAGE_SIZES[0] },
    page: { values: [1], selected: 1 },
    sortColumns,
  });
}

/** Whether a query renders as a virtual-scrolling grid rather than a paged one. */
export function isVirtualScrollingQuery(query: SparkQuery | null): boolean {
  return query?.renderMode === 'VirtualScrolling';
}

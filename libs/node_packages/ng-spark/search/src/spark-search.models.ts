import { InjectionToken } from '@angular/core';

/** One hit of `MintPlayer.Spark.Search` (`SparkSearchHit` on the server): an object the caller may see. */
export interface SparkSearchHit {
  /** The model's entity type name. */
  entityType: string;
  /** The type's label in the request's language. */
  typeLabel: string;
  id: string;
  /** The unambiguous display string: two objects never share one (`docs/spark_search_PRD.md` D3). */
  label: string;
  /** The object's page, an absolute path (`/po/:type/:id/:slug`). */
  url: string;
}

/** What Enter on a typed term does when it names exactly one object (D4). */
export type SparkSearchSingleHit = 'item' | 'searchPage';

export interface SparkSearchOptions {
  /**
   * The results page, as an absolute path. It must equal `sparkSearchRoutes(path)` and the server's
   * `SparkSearchOptions.SearchPagePath`, whose prerender contributor server-renders it. Default `/search`.
   */
  pagePath: string;
  /** Default `item`: a term that names one object opens it; `searchPage` always lists the results. */
  singleHit: SparkSearchSingleHit;
}

export const SPARK_SEARCH_OPTIONS = new InjectionToken<SparkSearchOptions>('SPARK_SEARCH_OPTIONS', {
  providedIn: 'root',
  factory: () => ({ pagePath: '/search', singleHit: 'item' }),
});

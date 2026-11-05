import { DOCUMENT } from '@angular/common';
import { inject, provideEnvironmentInitializer } from '@angular/core';
import { Routes } from '@angular/router';
import { sparkFeature, SparkFeature, SparkFeatureOrder } from '@mintplayer/ng-spark';
import { SPARK_SEARCH_OPTIONS, SparkSearchOptions } from './spark-search.models';
import { SparkSearchPageComponent } from './spark-search-page.component';

/**
 * Spark search (#238) for `provideSpark(...)`: the options `<spark-site-search>` and the results page read. Needs the
 * server package `MintPlayer.Spark.Search` (`spark.AddSearch()`).
 *
 * ```ts
 * provideSpark(withSearch()),
 * // routes: [...sparkSearchRoutes(), ...sparkRoutes()]
 * ```
 */
export function withSearch(options: Partial<SparkSearchOptions> = {}): SparkFeature {
  return sparkFeature('search', SparkFeatureOrder.modules, [
    { provide: SPARK_SEARCH_OPTIONS, useValue: { pagePath: '/search', singleHit: 'item', ...options } satisfies SparkSearchOptions },
  ]);
}

/** The `<link rel="search">` type of an OpenSearch description. */
const OPEN_SEARCH_TYPE = 'application/opensearchdescription+xml';

/**
 * Lets browsers add the site as a search engine: a `<link rel="search">` to the OpenSearch description in the
 * document's head, written on the server too, so it is in the page a browser or crawler first receives. Needs the
 * server package `MintPlayer.Spark.OpenSearchDescription` (`spark.AddOpenSearchDescription(...)`), whose description is
 * served at `href` (default `/opensearch.xml`). Adds {@link withSearch} when it is not listed.
 */
export function withOpenSearchDescription(options: { title: string; href?: string }): SparkFeature {
  return sparkFeature('openSearchDescription', SparkFeatureOrder.modules, [
    provideEnvironmentInitializer(() => {
      const document = inject(DOCUMENT);
      const head = document.head;
      if (!head) return;
      // Hydration keeps the server's head, so the link may already be there: update it rather than add a second.
      let link = head.querySelector<HTMLLinkElement>(`link[rel="search"][type="${OPEN_SEARCH_TYPE}"]`);
      if (!link) {
        link = document.createElement('link');
        link.setAttribute('rel', 'search');
        link.setAttribute('type', OPEN_SEARCH_TYPE);
        head.appendChild(link);
      }
      link.setAttribute('title', options.title);
      link.setAttribute('href', options.href ?? '/opensearch.xml');
    }),
  ], { requires: [() => withSearch()] });
}

/**
 * The results page at `search` (`/search?q=`). The path must match `withSearch({ pagePath })` and the server's
 * `SearchPagePath`. Put it before `sparkRoutes()`.
 */
export function sparkSearchRoutes(path = 'search'): Routes {
  return [{ path, component: SparkSearchPageComponent }];
}

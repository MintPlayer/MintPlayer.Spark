import { ViewportScroller } from '@angular/common';
import { inject, provideAppInitializer } from '@angular/core';
import { sparkFeature, SparkFeature, SparkFeatureOrder } from '@mintplayer/ng-spark';

import { SparkFragmentScrolling } from './spark-fragment-scrolling';
import { SparkViewportScroller } from './spark-viewport-scroller';

/**
 * `#fragment` links that scroll to the section they name (#721, `docs/section_fragment_links_PRD.md` Part E): an object
 * page's group card, tab or sub-query card, or any element with that id on an app's own page.
 *
 * Opt-in per app (Dec9), and it takes two lines, because the router is the one that asks for the anchor:
 *
 * ```ts
 * provideRouter(routes, withInMemoryScrolling({ anchorScrolling: 'enabled' })),
 * provideSpark(..., withFragmentScrolling()),
 * ```
 *
 * It replaces Angular's {@link ViewportScroller} with {@link SparkViewportScroller}, which scrolls Spark's `#spark-main`
 * instead of the window, waits for a section that renders after its data, opens the tab that holds it, and keeps it in
 * place while the page settles. Without `anchorScrolling` a dev build warns (Dec11). See `docs/guide-object-urls.md`.
 */
export function withFragmentScrolling(): SparkFeature {
  return sparkFeature('fragmentScrolling', SparkFeatureOrder.fragmentScrolling, [
    SparkViewportScroller,
    { provide: ViewportScroller, useExisting: SparkViewportScroller },
    provideAppInitializer(() => inject(SparkFragmentScrolling).start()),
  ]);
}

import { sparkFeature, SparkFeature, SparkFeatureOrder } from '@mintplayer/ng-spark';
import { SparkAttributeRendererRegistration } from '@mintplayer/ng-spark/renderers';
import { withDetailActions, withQueryRowActions } from '@mintplayer/ng-spark/panels';
import { SparkContributionAttributionComponent } from './spark-contribution-attribution.component';
import { SparkLineDiffComponent } from './spark-line-diff.component';
import { SparkRevertContributionComponent, sparkRevertContributionRowAction } from './spark-revert-contribution';

/**
 * Adds the Contributions UI to the pages `sparkRoutes()` routes to — no routes of its own (PRD Q7):
 * "Revert to this version" in the contributions query's row menu and on a contribution's detail page.
 * Needs the server package `MintPlayer.Spark.Contributions`.
 *
 * ```ts
 * provideSpark(
 *   withContributions(),
 *   withAttributeRenderers([...sparkContributionRenderers, ...yourRenderers]),
 * )
 * ```
 */
export function withContributions(options?: { detailActionPriority?: number }): SparkFeature {
  return sparkFeature('contributions', SparkFeatureOrder.modules, [
    ...withQueryRowActions(sparkRevertContributionRowAction).providers,
    ...withDetailActions({ id: 'spark-revert-contribution', component: SparkRevertContributionComponent, priority: options?.detailActionPriority ?? 6 }).providers,
  ]);
}

/**
 * The two renderers the Contributions model names:
 *
 * - `contributionAttribution` — an AsDetail **row** renderer: "by Alice · 3 days ago · History (4)"
 *   once per row, in place of the `ContributorName`/`UpdatedAt`/`ContributionCount` columns;
 * - `lineDiff` — a detail renderer, generic: a text diffed line by line against a text of another
 *   object (Contributions seeds it on a contribution's text values, against the current version).
 */
export const sparkContributionRenderers: SparkAttributeRendererRegistration[] = [
  { name: 'contributionAttribution', rowComponent: SparkContributionAttributionComponent },
  { name: 'lineDiff', detailComponent: SparkLineDiffComponent },
];

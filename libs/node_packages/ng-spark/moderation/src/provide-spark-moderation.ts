import { Routes } from '@angular/router';
import { sparkFeature, SparkFeature, SparkFeatureOrder } from '@mintplayer/ng-spark';
import { SparkAttributeRendererRegistration } from '@mintplayer/ng-spark/renderers';
import { withDetailActions, withDetailPanels } from '@mintplayer/ng-spark/panels';
import { SparkFlagButtonComponent } from './spark-flag-button.component';
import { SparkModeratorPanelComponent } from './spark-moderator-panel.component';
import { SparkReviewQueueComponent } from './spark-review-queue.component';
import { SparkVoteComponent } from './spark-vote.component';
import { SPARK_MODERATION_REVIEW_PATH } from './spark-moderation.tokens';

/**
 * Adds the Moderation UI (#460) to the pages `sparkRoutes()` routes to: a **Flag** action on every
 * detail page and the **moderator panel** under it. Needs the server package
 * `MintPlayer.Spark.Moderation`.
 *
 * ```ts
 * provideSpark(
 *   withModeration(),
 *   withAttributeRenderers([...sparkModerationRenderers, ...yourRenderers]),
 * )
 * // routes: [...sparkModerationRoutes(), ...sparkRoutes()]
 * ```
 *
 * The vote widget is an attribute renderer (`"renderer": "spark-vote"` on an attribute of an
 * `IModeratable` type), registered through {@link sparkModerationRenderers}.
 */
export function withModeration(options?: { reviewPath?: string; panelOrder?: number }): SparkFeature {
  return sparkFeature('moderation', SparkFeatureOrder.modules, [
    ...withDetailActions({ id: 'spark-moderation-flag', component: SparkFlagButtonComponent, priority: 90 }).providers,
    ...withDetailPanels({ id: 'spark-moderation-panel', component: SparkModeratorPanelComponent, order: options?.panelOrder ?? 90 }).providers,
    { provide: SPARK_MODERATION_REVIEW_PATH, useValue: options?.reviewPath ?? '/moderation/review' },
  ]);
}

/** The vote widget as an attribute renderer named `spark-vote` (detail page and grid column). */
export const sparkModerationRenderers: SparkAttributeRendererRegistration[] = [
  { name: 'spark-vote', detailComponent: SparkVoteComponent, columnComponent: SparkVoteComponent },
];

/**
 * The review-queue page at `moderation/review`. Put it **before** `sparkRoutes()` — a literal first
 * segment is fine anywhere, but keeping add-on routes first avoids surprises with parameterised ones.
 */
export function sparkModerationRoutes(path = 'moderation/review'): Routes {
  return [{ path, component: SparkReviewQueueComponent }];
}

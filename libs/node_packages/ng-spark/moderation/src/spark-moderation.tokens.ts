import { InjectionToken } from '@angular/core';

/** Where the review queue is routed; the moderator panel links there. Set by {@link provideSparkModeration}. */
export const SPARK_MODERATION_REVIEW_PATH = new InjectionToken<string>('SparkModerationReviewPath', {
  factory: () => '/moderation/review',
});

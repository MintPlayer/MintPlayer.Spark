// Root entry point — bootstrap API only.
// For other members import from sub-paths:
//   @mintplayer/ng-spark/models             (PersistentObject, EntityType, SparkQuery, etc. + currentLanguage/resolveTranslation/ShowedOn/ELookupDisplayType)
//   @mintplayer/ng-spark/services           (SparkService, SparkStreamingService, SparkLanguageService, RetryActionService, SparkIconRegistry)
//   @mintplayer/ng-spark/pipes              (all 22 pipes)
//   @mintplayer/ng-spark/renderers          (SPARK_ATTRIBUTE_RENDERERS, provideSparkAttributeRenderers, renderer interfaces)
//   @mintplayer/ng-spark/routes             (sparkRoutes, SparkRouteConfig)
//   @mintplayer/ng-spark/po-form            (SparkPoFormComponent)
//   @mintplayer/ng-spark/po-create          (SparkPoCreateComponent)
//   @mintplayer/ng-spark/po-edit            (SparkPoEditComponent)
//   @mintplayer/ng-spark/po-detail          (SparkPoDetailComponent)
//   @mintplayer/ng-spark/query-list         (SparkQueryListComponent)
//   @mintplayer/ng-spark/retry-action-modal (SparkRetryActionModalComponent)
//   @mintplayer/ng-spark/icon               (SparkIconComponent)
//   @mintplayer/ng-spark/shell              (SparkShellComponent + slots, SparkProgramUnitsComponent, SparkLanguageSelectorComponent)
//   @mintplayer/ng-spark/panels             (SPARK_DETAIL_PANELS, SPARK_DETAIL_ACTIONS, SPARK_QUERY_LIST_ACTIONS + provide* helpers, contexts)
//   @mintplayer/ng-spark/soft-delete        (provideSparkSoftDelete, Deleted toggle, Restore/Purge, SparkSoftDeleteService)
//   @mintplayer/ng-spark/history            (provideSparkHistory, <spark-po-history>, SparkHistoryService)

export type { SparkConfig } from './lib/spark-config';
export { SPARK_CONFIG, defaultSparkConfig } from './lib/spark-config';
export { SPARK_AUTH_STATE } from './lib/spark-auth-state';
export { provideSpark } from './lib/provide-spark';

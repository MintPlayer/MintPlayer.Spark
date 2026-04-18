// Root entry point — bootstrap API only.
// For other members import from sub-paths:
//   @mintplayer/ng-spark-auth/core         (SparkAuthService, SparkAuthTranslationService)
//   @mintplayer/ng-spark-auth/models       (types + SPARK_AUTH_ROUTE_PATHS)
//   @mintplayer/ng-spark-auth/guards       (sparkAuthGuard)
//   @mintplayer/ng-spark-auth/interceptors (sparkAuthInterceptor)
//   @mintplayer/ng-spark-auth/pipes        (TranslateKeyPipe)
//   @mintplayer/ng-spark-auth/routes       (sparkAuthRoutes)
//   @mintplayer/ng-spark-auth/auth-bar     (SparkAuthBarComponent)
//   @mintplayer/ng-spark-auth/{login,two-factor,register,forgot-password,reset-password}

export type { SparkAuthConfig } from '@mintplayer/ng-spark-auth/models';
export { SPARK_AUTH_CONFIG, defaultSparkAuthConfig } from '@mintplayer/ng-spark-auth/models';
export { provideSparkAuth, withSparkAuth } from './lib/provide-spark-auth';

import { EnvironmentProviders, inject, makeEnvironmentProviders } from '@angular/core';
import { HttpFeature, HttpFeatureKind, withInterceptors, withXsrfConfiguration } from '@angular/common/http';
import { SPARK_AUTH_STATE } from '@mintplayer/ng-spark';
import { provideSparkClientMethods } from '@mintplayer/ng-spark/client-operations';
import {
  defaultSparkAuthConfig,
  SPARK_AUTH_CONFIG,
  SparkAuthConfig,
} from '@mintplayer/ng-spark-auth/models';
import { sparkAuthInterceptor } from '@mintplayer/ng-spark-auth/interceptors';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { sparkAuthClientMethods } from './webauthn-client-methods';

export function provideSparkAuth(
  config?: Partial<SparkAuthConfig>,
): EnvironmentProviders {
  return makeEnvironmentProviders([
    {
      provide: SPARK_AUTH_CONFIG,
      useValue: { ...defaultSparkAuthConfig, ...config },
    },
    // Bridges sign-in/out into ng-spark (which must not depend on this package): auth-sensitive
    // consumers there — the program-units menu — track this signal and re-fetch on change.
    {
      provide: SPARK_AUTH_STATE,
      useFactory: () => inject(SparkAuthService).user,
    },
    // The browser steps the library's server actions invoke: `webauthn.create` for AddPasskey.
    provideSparkClientMethods(sparkAuthClientMethods),
  ]);
}

export function withSparkAuth(): HttpFeature<HttpFeatureKind>[] {
  return [
    withInterceptors([sparkAuthInterceptor]),
    withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' }),
  ];
}

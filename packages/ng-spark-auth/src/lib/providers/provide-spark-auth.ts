import { EnvironmentProviders, makeEnvironmentProviders } from '@angular/core';
import { HttpFeature, HttpFeatureKind, withInterceptors, withXsrfConfiguration } from '@angular/common/http';
import {
  defaultSparkAuthConfig,
  SPARK_AUTH_CONFIG,
  SparkAuthConfig,
} from '../models';
import { sparkAuthInterceptor } from '../interceptors/spark-auth.interceptor';

export function provideSparkAuth(
  config?: Partial<SparkAuthConfig>,
): EnvironmentProviders {
  return makeEnvironmentProviders([
    {
      provide: SPARK_AUTH_CONFIG,
      useValue: { ...defaultSparkAuthConfig, ...config },
    },
  ]);
}

export function withSparkAuth(): HttpFeature<HttpFeatureKind>[] {
  return [
    withInterceptors([sparkAuthInterceptor]),
    withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' }),
  ];
}

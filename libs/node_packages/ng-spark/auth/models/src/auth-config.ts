import { InjectionToken } from '@angular/core';
import { SparkExternalLoginModeSetting } from './external-login';

export interface SparkAuthConfig {
  apiBasePath: string;
  defaultRedirectUrl: string;
  loginUrl: string;
  /**
   * How external sign-in and linking reach the provider by default. `'auto'` (the default) is a
   * popup in a browser tab and a full-page redirect inside an installed web app. A call's own
   * `mode` option overrides it.
   */
  externalLoginMode?: SparkExternalLoginModeSetting;
}

export const SPARK_AUTH_CONFIG = new InjectionToken<SparkAuthConfig>('SPARK_AUTH_CONFIG');

export const defaultSparkAuthConfig: SparkAuthConfig = {
  apiBasePath: '/spark/auth',
  defaultRedirectUrl: '/',
  loginUrl: '/login',
  externalLoginMode: 'auto',
};

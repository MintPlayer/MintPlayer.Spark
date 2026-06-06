import { InjectionToken } from '@angular/core';

export interface SparkAuthConfig {
  apiBasePath: string;
  defaultRedirectUrl: string;
  loginUrl: string;
}

export const SPARK_AUTH_CONFIG = new InjectionToken<SparkAuthConfig>('SPARK_AUTH_CONFIG');

export const defaultSparkAuthConfig: SparkAuthConfig = {
  apiBasePath: '/spark/auth',
  defaultRedirectUrl: '/',
  loginUrl: '/login',
};

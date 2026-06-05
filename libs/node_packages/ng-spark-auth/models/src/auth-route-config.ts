import { InjectionToken, Type } from '@angular/core';

export type SparkAuthRouteEntry = string | { path: string; component?: Type<unknown> };

export interface SparkAuthRouteConfig {
  login?: SparkAuthRouteEntry;
  twoFactor?: SparkAuthRouteEntry;
  register?: SparkAuthRouteEntry;
  forgotPassword?: SparkAuthRouteEntry;
  resetPassword?: SparkAuthRouteEntry;
}

export type SparkAuthRoutePaths = Required<Record<keyof SparkAuthRouteConfig, string>>;

export const SPARK_AUTH_ROUTE_PATHS = new InjectionToken<SparkAuthRoutePaths>('SPARK_AUTH_ROUTE_PATHS');

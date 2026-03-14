import { InjectionToken } from '@angular/core';

export interface SparkOidcProvider {
  /** Unique scheme name matching backend AddOidcLogin() scheme, e.g. "sparkid" */
  scheme: string;
  /** Display name shown on button, e.g. "SparkId" */
  displayName: string;
  /** Bootstrap icon name, e.g. "shield-lock" */
  icon?: string;
  /** Color class for the button, e.g. "primary", "dark" */
  buttonClass?: string;
}

export const SPARK_OIDC_PROVIDERS = new InjectionToken<SparkOidcProvider[]>('SPARK_OIDC_PROVIDERS');

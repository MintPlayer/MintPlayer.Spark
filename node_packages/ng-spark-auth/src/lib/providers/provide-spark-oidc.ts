import { EnvironmentProviders, makeEnvironmentProviders } from '@angular/core';
import { SPARK_OIDC_PROVIDERS, SparkOidcProvider } from '../models/oidc-provider';
import { SparkOidcLoginConfig } from '../models/oidc-login-config';

/**
 * Registers an OIDC login provider button on the Spark login page.
 * The actual OIDC flow is backend-initiated — clicking the button
 * navigates to /spark/auth/external-login/{scheme}.
 *
 * Can be called multiple times to add multiple providers:
 *   provideSparkOidcLogin({ scheme: 'sparkid', displayName: 'SparkId', icon: 'shield-lock' }),
 *   provideSparkOidcLogin({ scheme: 'google', displayName: 'Google', icon: 'google' }),
 */
export function provideSparkOidcLogin(
  config: SparkOidcLoginConfig
): EnvironmentProviders {
  const provider: SparkOidcProvider = {
    scheme: config.scheme,
    displayName: config.displayName,
    icon: config.icon,
    buttonClass: config.buttonClass,
  };

  return makeEnvironmentProviders([
    {
      provide: SPARK_OIDC_PROVIDERS,
      useValue: provider,
      multi: true,
    },
  ]);
}

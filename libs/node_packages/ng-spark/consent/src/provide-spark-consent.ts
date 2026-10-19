import { Routes } from '@angular/router';
import { SparkConsentSettingsComponent } from './spark-consent-settings.component';
import { SparkConsentUnsubscribeComponent } from './spark-consent-unsubscribe.component';

/**
 * The consent pages (commerce X21/GA9), to mount under `consent` (the server's `Spark:Consent:UnsubscribePagePath`
 * defaults to `/consent/unsubscribe`). Needs `withComponentInputBinding()` for the `token` query parameter.
 *
 * ```ts
 * provideRouter([{ path: 'consent', children: sparkConsentRoutes() }], withComponentInputBinding())
 * ```
 * - `settings` — the settings page (link it from the footer: withdrawing must be as easy as giving, GDPR Art. 7(3)).
 * - `unsubscribe?token=` — a marketing mail's unsubscribe link.
 *
 * Place `<spark-consent-banner />` once in the app shell.
 */
export function sparkConsentRoutes(): Routes {
  return [
    { path: 'settings', component: SparkConsentSettingsComponent },
    { path: 'unsubscribe', component: SparkConsentUnsubscribeComponent },
  ];
}

import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideSparkAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSparkServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withSparkTimezone } from '@mintplayer/ng-spark/services';
import { provideSparkAttributeRenderers } from '@mintplayer/ng-spark/renderers';
import { provideReferenceData } from '@mintplayer/ng-spark/reference-data';
import { sparkAddressRenderers } from '@mintplayer/ng-spark/reference-data/spark';
import {
  provideSparkCommerce,
  withShopRoutes,
  withTicketingRoutes,
  withWaitingRoomRoutes,
} from '@mintplayer/ng-spark/commerce/storefront';

import { routes } from './app.routes';
import { referenceDataBaseUrl } from './reference-data-url';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideSparkServiceWorker(),
    // The festival storefront (commerce #521 M17/M18, PRD §3.3): the core pages (basket, checkout, orders, account,
    // legal pages, accessibility statement, consent) plus the shop, ticketing and waiting-room features. Its routes are
    // registered ahead of the app's own (ROUTES token), so they come before this app's redirects.
    provideSparkCommerce(
      { signInUrl: '/login?returnUrl={returnUrl}' },
      withShopRoutes(),
      withTicketingRoutes(),
      withWaitingRoomRoutes(),
    ),
    // The storefront pages take slug, number, code, key and query values as inputs.
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(...withSparkAuth(), ...withSparkTimezone()),
    provideSparkAuth(),
    provideAnimations(),
    provideZonelessChangeDetection(),
    // Addresses (commerce §4.18, reference-data plan R12): the host-configured reference-data instance, or free text.
    provideReferenceData({ baseUrl: referenceDataBaseUrl(location) }),
    provideSparkAttributeRenderers([...sparkAddressRenderers]),
  ]
};

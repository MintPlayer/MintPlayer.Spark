import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideSparkAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSparkServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withSparkTimezone } from '@mintplayer/ng-spark/services';
import { provideSparkAttributeRenderers } from '@mintplayer/ng-spark/renderers';
import { provideReferenceData } from '@mintplayer/ng-spark/reference-data';
import { sparkAddressRenderers } from '@mintplayer/ng-spark/reference-data/spark';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideSparkServiceWorker(),
    provideRouter(routes),
    // Administrators sign in with a local password (RD-DEF 2); the auth interceptor never sees /api/v1 calls, which
    // ReferenceDataApi sends through HttpBackend.
    provideHttpClient(...withSparkAuth(), ...withSparkTimezone()),
    provideSparkAuth(),
    provideAnimations(),
    provideZonelessChangeDetection(),
    // The consumer half against this very instance (same origin). A consumer app points at its host's instance,
    // or passes null for free-text address entry (RD-DEF 8). Also wires the zone-name source (D36 step 1).
    provideReferenceData({ baseUrl: location.origin }),
    provideSparkAttributeRenderers([...sparkAddressRenderers]),
  ]
};

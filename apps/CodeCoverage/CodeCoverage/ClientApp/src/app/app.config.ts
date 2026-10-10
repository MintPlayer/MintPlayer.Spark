import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideSparkServiceWorker } from '@mintplayer/ng-spark/pwa';
import { provideHttpClient } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSparkAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
import { provideSparkAttributeRenderers } from '@mintplayer/ng-spark/renderers';
import { provideSparkClientOperations } from '@mintplayer/ng-spark/client-operations';
import { withSparkLanguage, withSparkTimezone } from '@mintplayer/ng-spark/services';

import { routes } from './app.routes';
import { HOME_URL } from './spark/home-route';
import { codeCoverageRenderers } from './spark/renderers';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideSparkServiceWorker(),
    provideRouter(routes),
    provideHttpClient(...withSparkLanguage(), ...withSparkAuth(), ...withSparkTimezone()),
    provideAnimations(),
    // ⚠️ Not the default. loginUrl defaults to '/login', and this app mounts '/sign-in'
    // (sparkAuthRoutes(withExternalLogin(...)) in app.routes.ts) — so the route guard, the 401
    // interceptor and the auth bar were all redirecting to a page that does not exist. The
    // symptom was a blank shell rather than an error, which is why it survived.
    provideSparkAuth({ loginUrl: '/sign-in', defaultRedirectUrl: HOME_URL }),
    provideSparkAttributeRenderers(codeCoverageRenderers),
    // The Resync custom action's refreshQuery operation is dispatched here; without
    // this provider the action runs and the grid silently keeps its stale rows.
    provideSparkClientOperations(),
    provideZonelessChangeDetection()
  ]
};

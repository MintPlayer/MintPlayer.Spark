import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideSparkServiceWorker } from '@mintplayer/ng-spark/pwa';
import { provideHttpClient } from '@angular/common/http';
import { withSparkTimezone } from '@mintplayer/ng-spark/services';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSparkAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
import { provideSparkClientOperations } from '@mintplayer/ng-spark/client-operations';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideSparkServiceWorker(),
    provideRouter(routes),
    provideHttpClient(...withSparkAuth(), ...withSparkTimezone()),
    provideAnimations(),
    provideSparkAuth(),
    // `showSecret` (a new client secret) and the other operations the server sends with an action's result.
    provideSparkClientOperations(),
    provideZonelessChangeDetection()
  ]
};

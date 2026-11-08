import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { withLanguageUrls, withSparkLanguage, withSparkTimezone } from '@mintplayer/ng-spark/services';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSpark } from '@mintplayer/ng-spark';
import { withServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
import { withClientOperations } from '@mintplayer/ng-spark/client-operations';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(...withSparkAuth(), ...withSparkLanguage(), ...withSparkTimezone()),
    provideAnimations(),
    provideSpark(
      withLanguageUrls(),
      withServiceWorker(),
      withAuth(),
      // `showSecret` (a new client secret) and the other operations the server sends with an action's result.
      withClientOperations(),
    ),
    provideZonelessChangeDetection()
  ]
};

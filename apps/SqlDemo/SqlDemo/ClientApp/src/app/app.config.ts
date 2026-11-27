import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSpark } from '@mintplayer/ng-spark';
import { withServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withLanguageUrls, withSparkLanguage, withSparkTimezone } from '@mintplayer/ng-spark/services';
import { withAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
import { withClientOperations } from '@mintplayer/ng-spark/client-operations';
import { withHistory } from '@mintplayer/ng-spark/history';

import { routes } from './app.routes';

// The client knows nothing of the store: the same ng-spark pages run against PostgreSQL here as against RavenDB in the
// other demos (#536).
export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(...withSparkAuth(), ...withSparkLanguage(), ...withSparkTimezone()),
    provideAnimations(),
    provideZonelessChangeDetection(),
    provideSpark(
      withLanguageUrls(),
      withServiceWorker(),
      withAuth(),
      withClientOperations(),
      // The History card (revisions, diff, Revert) under every detail page: revisions are rows in PostgreSQL here.
      withHistory(),
    ),
  ]
};

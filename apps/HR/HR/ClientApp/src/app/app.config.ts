import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { withLanguageUrls, withSparkLanguage, withSparkTimezone } from '@mintplayer/ng-spark/services';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSpark } from '@mintplayer/ng-spark';
import { withServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(...withSparkAuth(), ...withSparkLanguage(), ...withSparkTimezone()),
    provideAnimations(),
    provideSpark(withLanguageUrls(), withServiceWorker(), withAuth()),
    provideZonelessChangeDetection()
  ]
};

import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideSparkServiceWorker } from '@mintplayer/ng-spark/pwa';
import { provideHttpClient } from '@angular/common/http';
import { withSparkTimezone } from '@mintplayer/ng-spark/services';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSparkAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideSparkServiceWorker(),
    provideRouter(routes),
    provideHttpClient(...withSparkAuth(), ...withSparkTimezone()),
    provideAnimations(),
    provideSparkAuth(),
    provideZonelessChangeDetection()
  ]
};

import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSparkServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withSparkTimezone } from '@mintplayer/ng-spark/services';
import { provideSparkAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
import { provideSparkClientOperations } from '@mintplayer/ng-spark/client-operations';
import { provideSparkNotifications } from '@mintplayer/ng-spark/notifications';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // The PWA (docs/guide-pwa.md): Spark's update policy, and push clicks navigate in-app (X10).
    provideSparkServiceWorker(),
    provideRouter(routes),
    provideHttpClient(...withSparkAuth(), ...withSparkTimezone()),
    provideAnimations(),
    provideSparkAuth(),
    provideSparkClientOperations(),
    // The bell's recent list and "see all" link (docs/guide-notifications.md).
    provideSparkNotifications(),
    provideZonelessChangeDetection(),
  ]
};

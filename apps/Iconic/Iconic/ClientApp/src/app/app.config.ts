import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { provideSpark } from '@mintplayer/ng-spark';
import { withAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
import { withClientOperations } from '@mintplayer/ng-spark/client-operations';
import { withServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withSparkLanguage, withSparkTimezone } from '@mintplayer/ng-spark/services';
import { routes } from './app.routes';
import { provideAppIcons } from './icons';

/** No hydration or SEO: Program.cs serves the editor without SSR; it is a client-only canvas app, not a content site. */
export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(...withSparkLanguage(), ...withSparkAuth(), ...withSparkTimezone()),
    provideAnimations(),
    provideSpark(withServiceWorker(), withAuth(), withClientOperations()),
    provideAppIcons(),
    provideZonelessChangeDetection(),
  ],
};

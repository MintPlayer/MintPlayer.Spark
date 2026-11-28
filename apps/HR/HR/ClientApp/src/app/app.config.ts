import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, withInMemoryScrolling } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { withLanguageUrls, withSparkLanguage, withSparkTimezone } from '@mintplayer/ng-spark/services';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSpark } from '@mintplayer/ng-spark';
import { withServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
import { withFragmentScrolling } from '@mintplayer/ng-spark/fragment-scrolling';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // #721: a link's #fragment (/po/person/…#career) scrolls to the section it names (docs/guide-object-urls.md).
    provideRouter(routes, withInMemoryScrolling({ anchorScrolling: 'enabled' })),
    provideHttpClient(...withSparkAuth(), ...withSparkLanguage(), ...withSparkTimezone()),
    provideAnimations(),
    provideSpark(withLanguageUrls(), withServiceWorker(), withAuth(), withFragmentScrolling()),
    provideZonelessChangeDetection()
  ]
};

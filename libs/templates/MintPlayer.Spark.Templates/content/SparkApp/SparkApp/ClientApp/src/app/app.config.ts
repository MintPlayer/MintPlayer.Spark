import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withXsrfConfiguration } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSpark } from '@mintplayer/ng-spark';
import { withServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withLanguageUrls, withSparkLanguage, withSparkTimezone } from '@mintplayer/ng-spark/services';
//#if (HasAuth)
import { withAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
//#endif
//#if (UseSoftDelete)
import { withSoftDelete } from '@mintplayer/ng-spark/soft-delete';
//#endif
//#if (UseHistory)
import { withHistory } from '@mintplayer/ng-spark/history';
//#endif

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
//#if (HasAuth)
    provideHttpClient(...withSparkAuth(), ...withSparkLanguage(), ...withSparkTimezone()),
//#else
    provideHttpClient(withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' }), ...withSparkLanguage(), ...withSparkTimezone()),
//#endif
    provideAnimations(),
    provideZonelessChangeDetection(),
    provideSpark(
      withLanguageUrls(),
      withServiceWorker(),
//#if (HasAuth)
      withAuth(),
//#endif
//#if (UseSoftDelete)
      withSoftDelete(),
//#endif
//#if (UseHistory)
      withHistory(),
//#endif
    ),
  ]
};

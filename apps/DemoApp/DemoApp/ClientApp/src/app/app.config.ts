import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withXsrfConfiguration } from '@angular/common/http';
import { withSparkTimezone } from '@mintplayer/ng-spark/services';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSpark } from '@mintplayer/ng-spark';
import { withServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withAttributeRenderers } from '@mintplayer/ng-spark/renderers';

import { routes } from './app.routes';
import { AddressCardDetailRendererComponent } from './renderers/address-card-detail-renderer.component';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' }), ...withSparkTimezone()),
    provideAnimations(),
    provideZonelessChangeDetection(),
    provideSpark(
      withServiceWorker(),
      // Detail-only registration: columnComponent/editComponent deliberately omitted (#241/#245).
      withAttributeRenderers([
        { name: 'address-card', detailComponent: AddressCardDetailRendererComponent },
      ]),
    ),
  ]
};

import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withXsrfConfiguration } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSparkAttributeRenderers } from '@mintplayer/ng-spark/renderers';

import { routes } from './app.routes';
import { AddressCardDetailRendererComponent } from './renderers/address-card-detail-renderer.component';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' })),
    provideAnimations(),
    provideZonelessChangeDetection(),
    // Detail-only registration: columnComponent/editComponent deliberately omitted (#241/#245).
    provideSparkAttributeRenderers([
      { name: 'address-card', detailComponent: AddressCardDetailRendererComponent },
    ]),
  ]
};

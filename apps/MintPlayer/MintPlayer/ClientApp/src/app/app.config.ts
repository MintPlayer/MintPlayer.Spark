import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSparkServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withSparkLanguage, withSparkTimezone } from '@mintplayer/ng-spark/services';
import { provideSparkAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
import { provideSparkClientOperations } from '@mintplayer/ng-spark/client-operations';
import { provideSparkAttributeRenderers } from '@mintplayer/ng-spark/renderers';
import { mintPlayerRenderers } from './renderers/renderers';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideSparkServiceWorker(),
    provideRouter(routes),
    // withSparkLanguage: server-resolved texts (refusals, page titles) follow the language selector, not the browser.
    provideHttpClient(...withSparkLanguage(), ...withSparkAuth(), ...withSparkTimezone()),
    provideAnimations(),
    provideSparkAuth(),
    provideSparkClientOperations(),
    // media-player (Medium.Value) and color-swatch (TagCategory.Color), by the model's names.
    provideSparkAttributeRenderers(mintPlayerRenderers),
    provideZonelessChangeDetection(),
  ]
};

import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSparkServiceWorker } from '@mintplayer/ng-spark/pwa';
import { provideSparkHydration, provideSparkSeo, withSparkLanguage, withSparkTimezone } from '@mintplayer/ng-spark/services';
import { provideSparkAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
import { provideSparkClientOperations } from '@mintplayer/ng-spark/client-operations';
import { provideSparkBoards } from '@mintplayer/ng-spark/boards';
import { provideSparkAttributeRenderers } from '@mintplayer/ng-spark/renderers';
import { provideVideoApis } from '@mintplayer/ng-video-player';
import { mintPlayerRenderers } from './renderers/renderers';
import { videoPlugins } from './player/video-apis';
import { mintPlayerSeo } from './seo';

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
    provideSparkBoards(),
    // media-player (Medium.Value) and color-swatch (TagCategory.Color), by the model's names.
    provideSparkAttributeRenderers(mintPlayerRenderers),
    provideVideoApis(...videoPlugins),
    provideZonelessChangeDetection(),
    // Server rendering (docs/spark_ssr_PRD.md): hydrate instead of re-rendering, and reuse the server's responses.
    provideSparkHydration(),
    provideSparkSeo(mintPlayerSeo),
  ]
};

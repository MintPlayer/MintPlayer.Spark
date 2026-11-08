import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSpark } from '@mintplayer/ng-spark';
import { withServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withHydration, withSeo, withLanguageUrls, withSparkLanguage, withSparkTimezone } from '@mintplayer/ng-spark/services';
import { withAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
import { withClientOperations } from '@mintplayer/ng-spark/client-operations';
import { withBoards } from '@mintplayer/ng-spark/boards';
import { withAttributeRenderers } from '@mintplayer/ng-spark/renderers';
import { withOpenSearchDescription } from '@mintplayer/ng-spark/search';
import { provideVideoApis } from '@mintplayer/ng-video-player';
import { mintPlayerRenderers } from './renderers/renderers';
import { videoPlugins } from './player/video-apis';
import { mintPlayerSeo } from './seo';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    // withSparkLanguage: server-resolved texts (refusals, page titles) follow the language selector, not the browser.
    provideHttpClient(...withSparkLanguage(), ...withSparkAuth(), ...withSparkTimezone()),
    provideAnimations(),
    provideSpark(
      withLanguageUrls(),
      withServiceWorker(),
      withAuth(),
      withClientOperations(),
      withBoards(),
      // media-player (Medium.Value) and color-swatch (TagCategory.Color), by the model's names.
      withAttributeRenderers(mintPlayerRenderers),
      // Server rendering (docs/spark_ssr_PRD.md): hydrate instead of re-rendering, and reuse the server's responses.
      withHydration(),
      withSeo(mintPlayerSeo),
      // <link rel="search"> to Program.cs' AddOpenSearchDescription: browsers offer mintplayer.com as a search engine.
      withOpenSearchDescription({ title: 'MintPlayer' }),
    ),
    provideVideoApis(...videoPlugins),
    provideZonelessChangeDetection(),
  ]
};

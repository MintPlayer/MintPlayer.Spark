import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { withSparkTimezone } from '@mintplayer/ng-spark/services';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSpark } from '@mintplayer/ng-spark';
import { withServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
import { withAttributeRenderers } from '@mintplayer/ng-spark/renderers';
import { withClientOperations } from '@mintplayer/ng-spark/client-operations';
import { withBoards } from '@mintplayer/ng-spark/boards';

import { routes } from './app.routes';
import { ColorDetailRendererComponent } from './renderers/color-detail-renderer.component';
import { ColorColumnRendererComponent } from './renderers/color-column-renderer.component';
import { VideoPlayerDetailRendererComponent } from './renderers/video-player-detail-renderer.component';
import { VideoPlayerColumnRendererComponent } from './renderers/video-player-column-renderer.component';
import { ColorEditRendererComponent } from './renderers/color-edit-renderer.component';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(...withSparkAuth(), ...withSparkTimezone()),
    provideAnimations(),
    provideZonelessChangeDetection(),
    provideSpark(
      withServiceWorker(),
      withAuth(),
      withClientOperations(),
      withBoards(),
      withAttributeRenderers([
        {
          name: 'color-swatch',
          detailComponent: ColorDetailRendererComponent,
          columnComponent: ColorColumnRendererComponent,
          editComponent: ColorEditRendererComponent,
        },
        {
          name: 'video-player',
          detailComponent: VideoPlayerDetailRendererComponent,
          columnComponent: VideoPlayerColumnRendererComponent,
        },
      ]),
    ),
  ]
};

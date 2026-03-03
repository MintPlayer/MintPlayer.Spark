import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSparkAuth, withSparkAuth } from '@mintplayer/ng-spark-auth';
import { provideSparkAttributeRenderers } from '@mintplayer/ng-spark';

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
    provideHttpClient(...withSparkAuth()),
    provideAnimations(),
    provideSparkAuth(),
    provideZonelessChangeDetection(),
    provideSparkAttributeRenderers([
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
  ]
};

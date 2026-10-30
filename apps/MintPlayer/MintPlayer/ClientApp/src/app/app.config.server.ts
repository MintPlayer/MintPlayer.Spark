import { mergeApplicationConfig } from '@angular/core';
import { provideSparkServerRendering } from '@mintplayer/ng-spark/ssr';
import { appConfig } from './app.config';

export const config = mergeApplicationConfig(appConfig, {
  providers: [provideSparkServerRendering()],
});

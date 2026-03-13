import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSparkAuth, withSparkAuth, provideSparkOidcLogin } from '@mintplayer/ng-spark-auth';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(...withSparkAuth()),
    provideAnimations(),
    provideSparkAuth(),
    provideSparkOidcLogin({
      scheme: 'sparkid',
      displayName: 'SparkId',
      icon: 'shield-lock',
      buttonClass: 'primary',
    }),
    provideZonelessChangeDetection()
  ]
};

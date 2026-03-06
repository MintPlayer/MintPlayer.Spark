import { mergeApplicationConfig, ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { APP_BASE_HREF } from '@angular/common';
import { appConfig } from './app.config';

const getBaseUrl = () => {
  return document.getElementsByTagName('base')[0].href.slice(0, -1);
}

const browserConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    { provide: APP_BASE_HREF, useFactory: getBaseUrl },
  ]
};

export const config = mergeApplicationConfig(appConfig, browserConfig);

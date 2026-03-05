import { bootstrapApplication } from '@angular/platform-browser';
import { config as browserConfig } from './app/app.config.browser';
import { App } from './app/app';

bootstrapApplication(App, browserConfig)
  .catch((err) => console.error(err));

import { BootstrapContext, bootstrapApplication } from '@angular/platform-browser';
import { createSparkServerRenderer } from '@mintplayer/ng-spark/ssr';
import { App } from './app/app';
import { config } from './app/app.config.server';

const bootstrap = (context: BootstrapContext) => bootstrapApplication(App, config, context);

// Invoked by UseSparkPrerendering (SpaServices) for every server-rendered page.
export default createSparkServerRenderer(bootstrap);

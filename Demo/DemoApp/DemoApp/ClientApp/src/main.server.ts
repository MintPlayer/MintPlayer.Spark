import 'reflect-metadata';
import { provideServerRendering, renderApplication } from '@angular/platform-server';
import { enableProdMode, StaticProvider } from '@angular/core';
import { createServerRenderer } from 'aspnet-prerendering';
import { SPARK_SERVER_DATA, currentLanguage } from '@mintplayer/ng-spark';
import { DATA_FROM_SERVER } from './app/providers/data-from-server';
import { App } from './app/app';
import { bootstrapApplication, BootstrapContext } from '@angular/platform-browser';
import { config as serverConfig } from './app/app.config.server';

enableProdMode();

export default createServerRenderer(params => {
  const providers: StaticProvider[] = [
    { provide: DATA_FROM_SERVER, useValue: params.data },
    { provide: SPARK_SERVER_DATA, useValue: params.data },
  ];

  const options = {
    document: params.data.originalHtml,
    url: params.url,
    platformProviders: providers
  };

  // Set the language signal from the server-resolved Accept-Language header
  if (params.data.language) {
    currentLanguage.set(params.data.language);
  }

  // Bypass ssr api call cert warnings in development
  process.env['NODE_TLS_REJECT_UNAUTHORIZED'] = "0";

  const renderPromise = renderApplication((context: BootstrapContext) => bootstrapApplication(App, serverConfig, context), options);

  return renderPromise.then(html => ({ html }));
});

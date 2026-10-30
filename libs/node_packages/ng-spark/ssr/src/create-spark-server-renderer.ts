/// <reference types="node" />
// Node types for this entry point only: it runs in the prerendering Node process, never in a browser.
import { AsyncLocalStorage } from 'node:async_hooks';
import { ApplicationRef } from '@angular/core';
import { BootstrapContext } from '@angular/platform-browser';
import { renderApplication } from '@angular/platform-server';
import { SPARK_SERVER_DATA, SparkServerData } from './spark-server-data';

/** The application's bootstrap, as `main.server.ts` exports it. */
export type SparkServerBootstrap = (context: BootstrapContext) => Promise<ApplicationRef>;

/** What SpaServices' `prerenderer.js` reads back: the page, and optionally a status or a redirect. */
interface RenderToStringResult {
  html?: string;
  statusCode?: number;
  redirectUrl?: string;
}

type RenderCallback = (error: unknown, result: RenderToStringResult | null) => void;

const DEFAULT_TIMEOUT_MILLISECONDS = 30_000;

/**
 * The language of the render running in the current async context. One Node process renders many
 * requests concurrently and they interleave at every `await`, so the process-wide `currentLanguage`
 * signal cannot carry a per-request value; `activeLanguage()` (ng-spark/models) reads this instead.
 */
const languageScope = new AsyncLocalStorage<string>();
(globalThis as { __sparkScopedLanguage?: () => string | undefined }).__sparkScopedLanguage =
  () => languageScope.getStore();

/**
 * One Node process renders every request (SpaServices' NodeServices), and Node ends the process on an
 * unhandled rejection. A component that fires a request and never catches it, which is harmless in a
 * browser, would otherwise take every concurrent and later render down with it: SP1 hit exactly that
 * with a supplied-response miss. It is logged with the render's path instead.
 */
const renderPath = new AsyncLocalStorage<string>();
process.on('unhandledRejection', reason => {
  const detail = reason instanceof Error ? (reason.stack ?? reason.message) : (reason as { message?: string })?.message ?? String(reason);
  console.error(`[spark-ssr] unhandled rejection while rendering ${renderPath.getStore() ?? '(unknown)'}: ${detail}`);
});

/**
 * The boot function `UseSpaPrerendering` (MintPlayer.AspNetCore.SpaServices.Prerendering) invokes
 * for every server-rendered page. Export its result as the server bundle's default export:
 *
 * ```ts
 * export default createSparkServerRenderer(bootstrap);
 * ```
 *
 * It replaces `aspnet-prerendering`'s `createServerRenderer`, whose `domain`-based task tracking
 * Angular does not need (`renderApplication` waits for the app to become stable itself). The page is
 * rendered into the template the middleware captured (`originalHtml`), at the request's path, with the
 * responses `SparkPrerenderingService` supplied (`data.spark`).
 *
 * The status code is not returned here: the .NET side already set it on the response in
 * `OnSupplyData`, from the page's own read (PRD D4), and SpaServices keeps it.
 */
export function createSparkServerRenderer(bootstrap: SparkServerBootstrap) {
  const render = (
    callback: RenderCallback,
    _applicationBasePath: string,
    _bootModule: unknown,
    _absoluteRequestUrl: string,
    requestPathAndQuery: string,
    customDataParameter: { originalHtml: string; spark?: SparkServerData },
    overrideTimeoutMilliseconds?: number,
  ): void => {
    const timeout = overrideTimeoutMilliseconds || DEFAULT_TIMEOUT_MILLISECONDS;
    const rendering = renderPath.run(requestPathAndQuery, () => languageScope.run(customDataParameter.spark?.language ?? 'en', () => renderApplication(bootstrap, {
      document: customDataParameter.originalHtml,
      // Relative on purpose: an absolute URL makes Angular check it against `allowedHosts`, and the
      // host has already been checked by ASP.NET Core (AllowedHosts) before the request got here.
      url: requestPathAndQuery,
      platformProviders: customDataParameter.spark
        ? [{ provide: SPARK_SERVER_DATA, useValue: customDataParameter.spark }]
        : [],
    })));

    withTimeout(rendering, timeout, requestPathAndQuery).then(
      html => callback(null, { html }),
      error => callback(error instanceof Error ? (error.stack ?? error.message) : String(error), null),
    );
  };
  // prerenderer.js invokes a function carrying this flag directly; without it, it falls back to the
  // aspnet-prerendering v1 path.
  (render as unknown as { isServerRenderer: boolean }).isServerRenderer = true;
  return render;
}

function withTimeout<T>(promise: Promise<T>, milliseconds: number, path: string): Promise<T> {
  if (milliseconds < 0) return promise;
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(
      () => reject(new Error(`Server rendering of ${path} did not finish within ${milliseconds} ms.`)),
      milliseconds,
    );
    promise.then(
      value => { clearTimeout(timer); resolve(value); },
      error => { clearTimeout(timer); reject(error); },
    );
  });
}

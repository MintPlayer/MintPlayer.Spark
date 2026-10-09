import { isDevMode } from '@angular/core';
import { Route, Router } from '@angular/router';
import { SparkAuthConfig } from './auth-config';

/**
 * Where to send someone who is not signed in.
 *
 * `loginUrl` is the single source of truth for this, and the only thing the guard, the interceptor
 * and the auth bar should consult — an application that turns off local credentials points it at
 * its own sign-in landing route.
 *
 * The reason this is a function rather than a property read is the failure mode it closes: nothing
 * connects `loginUrl` to the routes that actually exist, so pointing it at a route that was never
 * registered produces a redirect into a blank page with no type error, no build failure and no
 * runtime error. In development that now warns, once, naming the value and the fix.
 */
export function resolveSignInUrl(config: SparkAuthConfig, router: Router): string {
  if (isDevMode() && !warned && !routeExists(router, config.loginUrl)) {
    warned = true;
    console.warn(
      `[ng-spark-auth] loginUrl is "${config.loginUrl}", but no route matches it, so redirecting an `
      + `unauthenticated user will land on a blank page. If this app runs with local credentials `
      + `disabled, point loginUrl at its sign-in route (for example provideSparkAuth({ loginUrl: '/sign-in' })).`,
    );
  }

  return config.loginUrl;
}

let warned = false;

function routeExists(router: Router, url: string): boolean {
  const target = url.replace(/^\/+/, '').split('?')[0].split('#')[0];
  return matches(router.config, target);
}

function matches(routes: Route[], target: string, prefix = ''): boolean {
  return routes.some(route => {
    const path = [prefix, route.path ?? ''].filter(Boolean).join('/');
    if (path === target) return true;
    // A wildcard route would match anything, including a URL nobody registered — treating it as a
    // match would make this check always pass and warn about nothing.
    return route.children ? matches(route.children, target, path) : false;
  });
}

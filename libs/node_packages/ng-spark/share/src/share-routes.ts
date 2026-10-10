import type { Type } from '@angular/core';
import type { Routes } from '@angular/router';

// Type-only: the page stays out of this entry point's bundle, loaded on navigation.
export type { SparkSharedObject } from '@mintplayer/ng-spark/share/page';

/** Options of {@link withShareRoutes}. */
export interface SparkShareRoutesOptions {
  /** The route path; it must hold `:type` and `:token`. Default `shared/:type/:token`, matching the server's `Spark:Share:LinkPath` (`/shared`). */
  path?: string;
  /** A page to use instead of `SparkSharedObjectComponent`. */
  component?: Type<unknown>;
}

/**
 * The page a share link opens (`docs/seo_analyzer_PRD.md` X6, D31): `{origin}/shared/{type}/{token}?id={id}`.
 * Anonymous by design (the token is the credential), so it carries no guard; put it where no app chrome
 * assumes a signed-in user:
 *
 * ```ts
 * export const routes: Routes = [
 *   ...withShareRoutes(),
 *   { path: '', component: ShellComponent, children: [...sparkRoutes()] },
 * ];
 * ```
 *
 * Moving the page means setting `Spark:Share:LinkPath` to match, so the links the server makes follow.
 */
export function withShareRoutes(options?: SparkShareRoutesOptions): Routes {
  const component = options?.component;
  return [
    {
      path: options?.path ?? 'shared/:type/:token',
      loadComponent: component
        ? () => Promise.resolve(component)
        : () => import('@mintplayer/ng-spark/share/page').then(m => m.SparkSharedObjectComponent),
    },
  ];
}

import { Location } from '@angular/common';
import { Injectable, isDevMode, inject } from '@angular/core';
import { Route, Router } from '@angular/router';

/**
 * Keeps the address bar on an object's canonical page (#602): after a load, the object's `href` replaces the current
 * path when they differ (no slug yet, a stale one, the type's id instead of its alias). No navigation and no reload:
 * only the URL changes, and the query string (`?lang`, `?deleted`) is kept.
 */
@Injectable({ providedIn: 'root' })
export class SparkObjectUrlService {
  private readonly location = inject(Location);
  private readonly router = inject(Router);

  apply(item: { href?: string | null }): void {
    const href = item.href;
    if (!href) return;
    const current = this.location.path();
    const queryAt = current.search(/[?#]/);
    const path = queryAt < 0 ? current : current.slice(0, queryAt);
    const rest = queryAt < 0 ? '' : current.slice(queryAt);
    if (sameSegments(path, href)) return;

    if (isDevMode() && !hasRouteFor(this.router.config, segmentsOf(href)))
      console.warn(`[spark] No route renders ${href}. The entity type's "route" needs a matching sparkObjectRoute().`);
    this.location.replaceState(href + rest);
  }
}

function segmentsOf(path: string): string[] {
  return path.split('/').filter(s => s.length > 0).map(s => {
    try { return decodeURIComponent(s); } catch { return s; }
  });
}

function sameSegments(a: string, b: string): boolean {
  const x = segmentsOf(a), y = segmentsOf(b);
  return x.length === y.length && x.every((s, i) => s === y[i]);
}

/** Whether a route other than the catch-all matches the path: literal segments and `:params`, children included. */
function hasRouteFor(routes: Route[], segments: string[]): boolean {
  return routes.some(route => {
    if (route.path === '**' || route.redirectTo !== undefined) return false;
    const parts = (route.path ?? '').split('/').filter(p => p.length > 0);
    if (parts.length > segments.length) return false;
    if (!parts.every((p, i) => p.startsWith(':') || p.toLowerCase() === segments[i].toLowerCase())) return false;
    const rest = segments.slice(parts.length);
    if (rest.length === 0 && (route.component || route.loadComponent)) return true;
    // Lazy children are not loaded yet: give them the benefit of the doubt.
    if (route.loadChildren) return true;
    return !!route.children && hasRouteFor(route.children, rest);
  });
}

import { Injectable, signal } from '@angular/core';

/**
 * Carries a server-issued `refreshQuery` to whichever grids are showing that query.
 *
 * A broadcast signal rather than a registry of component handles: grids come and go behind
 * `@if` and lazy routes, and nothing else in ng-spark holds a component reference. A grid
 * reads {@link tokenFor} in an effect and re-fetches when it changes, which is the same
 * declarative shape as the `reloadToken` input a host would use.
 *
 * Until this existed the server could emit the operation and the dispatcher dropped it: only
 * `notify` was registered, and unknown types are ignored silently — so `refreshOnCompleted`
 * on the server had no effect on any grid the action did not happen to be hosted in.
 */
@Injectable({ providedIn: 'root' })
export class SparkQueryRefreshService {
  private readonly tokens = signal<Record<string, number>>({});

  /**
   * Bumped every time a refresh is asked for this key.
   *
   * ⚠️ Reading it makes the caller depend on EVERY key, not just this one: the counters share one
   * signal. A grid must therefore compare the value with the one it last saw, never react to the
   * read alone (#319) — otherwise it re-fetches for every query refreshed anywhere in the app.
   */
  tokenFor(queryId: string | undefined): number {
    if (!queryId) return 0;
    return this.tokens()[normalize(queryId)] ?? 0;
  }

  /**
   * Ask every grid showing `queryId` to re-fetch.
   *
   * `queryId` is the query's id or its alias. A grid answers to both, whichever its own `queryId`
   * input holds, because it learns the other from the query it resolved. Keys are matched
   * case-insensitively, as the server resolves them (`QueryLoader.ResolveQuery`).
   *
   * Callers bumping several keys for one user action should do so in one synchronous run: the
   * signal coalesces bumps within a tick into a single effect run, and bumps split across an
   * await become one re-fetch each (#319).
   */
  request(queryId: string): void {
    const key = normalize(queryId);
    this.tokens.update(current => ({ ...current, [key]: (current[key] ?? 0) + 1 }));
  }
}

function normalize(queryId: string): string {
  return queryId.toLowerCase();
}

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

  /** Bumped every time the server asks for this query to refresh. */
  tokenFor(queryId: string | undefined): number {
    if (!queryId) return 0;
    return this.tokens()[queryId] ?? 0;
  }

  /**
   * Ask every grid showing `queryId` to re-fetch.
   *
   * Matched on the EXACT string a grid passes to {@link tokenFor} -- whichever of id or alias
   * its `queryId` input holds. A caller that knows only the other form will not reach it.
   *
   * Callers bumping several keys for one user action should do so in one synchronous run: the
   * signal coalesces bumps within a tick into a single effect run, and bumps split across an
   * await become one re-fetch each.
   */
  request(queryId: string): void {
    this.tokens.update(current => ({ ...current, [queryId]: (current[queryId] ?? 0) + 1 }));
  }
}

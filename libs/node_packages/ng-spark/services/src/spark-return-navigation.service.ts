import { inject, Injectable } from '@angular/core';
import { Router } from '@angular/router';
import { SparkService } from './spark.service';

/** The `history.state` key a list row link stores the list's own URL under. */
export const SPARK_RETURN_URL_STATE_KEY = 'sparkReturnUrl';

/**
 * Where a detail page goes back to after its row stops existing — a Delete, or a Purge from the
 * recycle bin (#460).
 *
 * Neither "the start page" nor "the previous history entry" is the right answer: the first loses the
 * user's place, the second may be the deleted row's edit page, or another site. The right answer is
 * the list the row was opened from, so a list's row link records that list's URL in the navigation
 * state ({@link stateForRowLink}); a detail page reached otherwise (a bookmark, a reference link)
 * falls back to the row type's own list — its program-unit query, else any query over the type.
 */
@Injectable({ providedIn: 'root' })
export class SparkReturnNavigationService {
  private readonly router = inject(Router);
  private readonly spark = inject(SparkService);

  /** Navigation state for a row link: the current URL (the list, with its `?deleted=` mode). */
  stateForRowLink(): Record<string, string> {
    return { [SPARK_RETURN_URL_STATE_KEY]: this.router.url };
  }

  /** The list URL the current detail page was opened from, when a row link recorded one. */
  recordedReturnUrl(): string | null {
    const state = (typeof history !== 'undefined' ? history.state : null) as Record<string, unknown> | null;
    const url = state?.[SPARK_RETURN_URL_STATE_KEY];
    // Only an in-app path: the state is the app's own, but a protocol-relative "//host" is not a path.
    return typeof url === 'string' && url.startsWith('/') && !url.startsWith('//') ? url : null;
  }

  /**
   * Navigates to the list the row came from, else to the type's list (`?deleted=only` kept when
   * `deletedView`), else to the start page. `replaceUrl`: the gone row's page is not worth a Back.
   */
  async returnToList(entityTypeName: string | null | undefined, options?: { deletedView?: boolean }): Promise<void> {
    const recorded = this.recordedReturnUrl();
    if (recorded) {
      await this.router.navigateByUrl(recorded, { replaceUrl: true });
      return;
    }
    const route = entityTypeName ? await this.listRouteFor(entityTypeName) : null;
    if (route) {
      await this.router.navigate(route, {
        replaceUrl: true,
        ...(options?.deletedView ? { queryParams: { deleted: 'only' } } : {}),
      });
      return;
    }
    await this.router.navigate(['/'], { replaceUrl: true });
  }

  /** `['/query', alias]` of the type's list: a menu (program-unit) query first, else any query. */
  async listRouteFor(entityTypeName: string): Promise<unknown[] | null> {
    let queries;
    try {
      queries = (await this.spark.getQueries()).filter(q => q.entityType === entityTypeName);
    } catch {
      return null;
    }
    if (!queries.length) return null;
    try {
      const units = (await this.spark.getProgramUnits()).programUnitGroups
        .flatMap(g => g.programUnits)
        .filter(u => u.type === 'query')
        .sort((a, b) => a.order - b.order);
      for (const unit of units) {
        if (queries.some(q => q.id === unit.queryId || (!!unit.alias && q.alias === unit.alias)))
          return ['/query', unit.alias || unit.queryId!];
      }
    } catch {
      // No menu: any query over the type still beats the start page.
    }
    return ['/query', queries[0].alias || queries[0].id];
  }
}

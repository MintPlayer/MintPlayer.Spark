import { ChangeDetectionStrategy, Component } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import type { CustomActionDefinition, PersistentObject } from '@mintplayer/ng-spark/models';
import { valueFor } from '@mintplayer/ng-spark/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkQueryCardComponent } from '@mintplayer/ng-spark/grid';
import { SparkPoDetailComponent } from '@mintplayer/ng-spark/po-detail';
import { SeoSiteTrendPanelComponent } from './panels/site-trend-panel.component';

/** Page details (PageResult) are kept this long after the run (PRD D8). */
export const PAGE_DETAILS_DAYS = 7;

/** Whether a run's page details have expired: it finished (or started) more than 7 days before `now`. */
export function pageDetailsExpired(po: PersistentObject | null | undefined, now = new Date()): boolean {
  if (!po) return false;
  const at = valueFor(po, 'CompletedAtUtc')?.value ?? valueFor(po, 'StartedAtUtc')?.value;
  if (!at) return false;
  const time = new Date(String(at)).getTime();
  return Number.isFinite(time) && now.getTime() - time > PAGE_DETAILS_DAYS * 86_400_000;
}

/**
 * The address a finished *Download PDF* (`ExportReport`) result downloads at once: a report already printed, on this
 * origin only. `null` otherwise (the server then prints it and sends a notification with the link).
 */
export function downloadUrlOf(action: string, result: unknown): string | null {
  if (action !== 'ExportReport') return null;
  const r = result as { url?: unknown; ready?: unknown } | null | undefined;
  return r?.ready === true && typeof r.url === 'string' && r.url.startsWith('/') && !r.url.startsWith('//') ? r.url : null;
}

/**
 * The app's poDetail route (`sparkRoutes({ poDetail })`): the generic detail page for every type, with SeoTester's
 * panels where an attribute renderer does not fit (PRD §4.5): a site's score trend, the note that a run's page details
 * expired (or, before that, that a later run takes the pages it checks again: one result per page), and the abuse log under the operator dashboard (whose figures are the dashboards package's tiles, #504). The
 * report itself (progress, scores, results with how-to-fix) is the run's own attributes, drawn by the app's renderers
 * (seo/renderers.ts).
 */
@Component({
  selector: 'app-po-detail-page',
  imports: [SparkPoDetailComponent, SeoSiteTrendPanelComponent, SparkQueryCardComponent, BsAlertComponent, TranslateKeyPipe],
  template: `
    <spark-po-detail [extraContentTemplate]="extras" (customActionExecuted)="onAction($event)" />

    <ng-template #extras let-po let-entityType="entityType">
      @switch (entityType.name) {
        @case ('Site') {
          <app-seo-site-trend-panel [siteId]="po.id" [version]="latestRun(po)" />
        }
        @case ('AuditRun') {
          @if (expired(po)) {
            <bs-alert [type]="colors.secondary" class="d-block m-3" data-pages-expired>{{ 'seoUi.pagesExpired' | t }}</bs-alert>
          } @else {
            <!-- One result per page and site, the latest (owner decision 2026-10-12): a later audit takes the pages it checks again. -->
            <bs-alert [type]="colors.secondary" class="d-block m-3" data-pages-latest>{{ 'seoUi.pagesLatestOnly' | t }}</bs-alert>
          }
        }
        @case ('OperatorDashboard') {
          <!-- The abuse log (D28): every start, join and refusal; Administrators only (the query's own right). -->
          <div class="m-3" data-abuse-log><spark-query-card queryId="audit-log" /></div>
        }
      }
    </ng-template>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class PoDetailPageComponent {
  protected readonly colors = Color;

  protected expired(po: PersistentObject): boolean {
    return pageDetailsExpired(po);
  }

  protected latestRun(po: PersistentObject): unknown {
    return valueFor(po, 'LatestRunId')?.value ?? null;
  }

  /** *Download PDF* (`ExportReport`): a report already printed downloads at once; otherwise a notification follows. */
  protected onAction(event: { action: CustomActionDefinition; result?: unknown }): void {
    const url = downloadUrlOf(event.action.name, event.result);
    if (url) window.location.assign(url);
  }
}

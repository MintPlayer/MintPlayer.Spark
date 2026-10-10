import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsProgressBarComponent, BsProgressComponent } from '@mintplayer/ng-bootstrap/progress-bar';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';

/** The pipeline's stages in order (PRD §4.3), for "step 2 of 5". */
export const SEO_PHASES = ['probe', 'crawl', 'render', 'psi', 'aggregate'];

/** What the progress panel shows for a run's values. Pure, so it is tested on its own. */
export function seoRunProgress(data: Record<string, unknown> | undefined) {
  const status = typeof data?.['Status'] === 'string' ? (data['Status'] as string) : '';
  const running = status === 'Queued' || status === 'Running';
  const done = Number(data?.['PagesDone'] ?? 0) || 0;
  const discovered = Number(data?.['PagesDiscovered'] ?? 0) || 0;
  const max = Number(data?.['MaxPages'] ?? 0) || 0;
  // The crawl stops at the page limit, so the bar fills towards whichever comes first.
  const total = Math.max(done, max > 0 ? Math.min(Math.max(discovered, 1), max) : discovered, 1);
  const phase = typeof data?.['Phase'] === 'string' ? (data['Phase'] as string).toLowerCase() : '';
  const step = SEO_PHASES.indexOf(phase);
  return {
    status, running, done, discovered, total,
    percent: Math.round(Math.min(100, (done / total) * 100)),
    phase: step >= 0 ? phase : null,
    step: step >= 0 ? step + 1 : null,
    currentUrl: typeof data?.['CurrentUrl'] === 'string' ? (data['CurrentUrl'] as string) : null,
    cancelling: data?.['CancelRequested'] === true && running,
    error: typeof data?.['Error'] === 'string' && data['Error'] ? (data['Error'] as string) : null,
  };
}

/**
 * `seo-run-progress`, the full-width renderer on `AuditRun.Phase`: the run's state in one panel. While it runs, live
 * over `/spark/live` (the detail page follows the live type, PRD §4.4): the stage, pages crawled of the run's limit and
 * the page being fetched. Once over: why it stopped when it did not complete (robots.txt, an error, a cancel).
 */
@Component({
  selector: 'app-seo-run-progress',
  imports: [BsAlertComponent, BsProgressComponent, BsProgressBarComponent, TranslateKeyPipe],
  template: `
    @let p = progress();
    @if (p.running) {
      <div data-running aria-live="polite">
        <div class="d-flex flex-wrap justify-content-between small mb-1">
          <span>
            @if (p.phase) { <strong>{{ ('seoUi.phase.' + p.phase) | t }}</strong> <span class="text-body-secondary">({{ p.step }}/5)</span> }
            @else { <strong>{{ 'seoUi.phase.queued' | t }}</strong> }
            @if (p.cancelling) { · <span class="text-warning">{{ 'seoUi.cancelling' | t }}</span> }
          </span>
          <span>{{ p.done }} / {{ p.total }} {{ 'seoUi.results.pages' | t }}</span>
        </div>
        <bs-progress [height]="10">
          <bs-progress-bar [value]="p.percent" [color]="colors.primary" [striped]="true" [animated]="true" [ariaLabel]="'seoUi.progress' | t" />
        </bs-progress>
        @if (p.currentUrl) { <div class="small text-body-secondary text-truncate mt-1" [title]="p.currentUrl">{{ p.currentUrl }}</div> }
      </div>
    } @else if (p.status === 'Blocked') {
      <bs-alert [type]="colors.warning" class="mb-0">{{ 'seoUi.blocked' | t }}</bs-alert>
    } @else if (p.status === 'Failed') {
      <bs-alert [type]="colors.danger" class="mb-0">{{ 'seoUi.failed' | t }}@if (p.error) { {{ p.error }} }</bs-alert>
    } @else if (p.status === 'Cancelled') {
      <bs-alert [type]="colors.secondary" class="mb-0">{{ 'seoUi.cancelled' | t }}</bs-alert>
    } @else if (p.status === 'Completed') {
      <span class="text-success" data-completed>{{ 'seoUi.completed' | t }} · {{ p.done }} {{ (p.done === 1 ? 'seoUi.results.page' : 'seoUi.results.pages') | t }}</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SeoRunProgressComponent {
  readonly formData = input<Record<string, unknown>>({});
  protected readonly colors = Color;
  protected readonly progress = computed(() => seoRunProgress(this.formData()));
}

import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import type { QueryResultItem } from '@mintplayer/ng-spark/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkTrendChartComponent, SparkTrendOptions, SparkTrendValuePoint } from '@mintplayer/ng-spark/score-renderers';
import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';

export interface SeoTrendRun { at: Date; score: number; version: string | null }

/** A site's completed runs, oldest first, and the dates the scoring changed (PRD F8: the trend marks ScoringVersion changes). */
export function seoTrend(items: readonly QueryResultItem[]) {
  const value = (item: QueryResultItem, key: string) => item.values.find(v => v.key === key)?.value;
  const runs: SeoTrendRun[] = items
    .map(item => ({ at: new Date(String(value(item, 'StartedAtUtc') ?? '')), score: value(item, 'Score') == null ? NaN : Number(value(item, 'Score')), version: (value(item, 'ScoringVersion') as string | undefined) ?? null, status: value(item, 'Status') }))
    .filter(r => r.status === 'Completed' && Number.isFinite(r.score) && !Number.isNaN(r.at.getTime()))
    .map(({ at, score, version }) => ({ at, score, version }))
    .sort((a, b) => a.at.getTime() - b.at.getTime());
  const changes = runs.filter((r, i) => i > 0 && r.version && runs[i - 1].version && r.version !== runs[i - 1].version).map(r => r.at);
  return { runs, changes, points: runs.map<SparkTrendValuePoint>(r => ({ x: r.at, y: r.score })) };
}

/**
 * The site's score over its runs (PRD F8, §4.5), from the `site-runs` sub-query: ng-spark's trend chart on 0–100, and
 * a line per scoring change (an administrator changed weights or checks, so scores before and after compare less well).
 */
@Component({
  selector: 'app-seo-site-trend-panel',
  imports: [BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, SparkTrendChartComponent, TranslateKeyPipe],
  template: `
    @let t = trend();
    @if (t.runs.length > 0) {
      <bs-card class="d-block m-3" data-trend>
        <bs-card-header><i class="bi bi-graph-up"></i> {{ 'seoUi.trend.title' | t }}</bs-card-header>
        <bs-card-body>
          @if (t.points.length >= 2) {
            <div style="max-width: 720px"><spark-trend-chart [value]="t.points" [options]="options()" /></div>
          } @else {
            <p class="text-body-secondary mb-0">{{ 'seoUi.trend.oneRun' | t }}</p>
          }
          @for (at of t.changes; track at.getTime()) {
            <div class="small text-body-secondary" data-scoring-change><i class="bi bi-sliders"></i> {{ 'seoUi.trend.scoringChanged' | t }} {{ at.toLocaleDateString(lang.language()) }}</div>
          }
        </bs-card-body>
      </bs-card>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SeoSiteTrendPanelComponent {
  readonly siteId = input.required<string>();
  /** Bumped by the page when the site changed (a run finished), to fetch again. */
  readonly version = input<unknown>(null);

  private readonly spark = inject(SparkService);
  protected readonly lang = inject(SparkLanguageService);
  private readonly items = signal<QueryResultItem[]>([]);

  protected readonly trend = computed(() => seoTrend(this.items()));
  protected readonly options = computed<SparkTrendOptions>(() => ({
    display: 'chart', yMin: 0, yMax: 100, minPoints: 2,
    label: this.lang.t('seoUi.trend.title', 'Score over time'),
    seriesId: 'score', seriesLabel: this.lang.t('seoUi.trend.series', 'Score'),
  }));

  constructor() {
    effect(async () => {
      const siteId = this.siteId();
      this.version();
      try {
        const result = await this.spark.executeQueryByName('SiteRuns', { parentId: siteId, parentType: 'Site', skip: 0, take: 100 });
        this.items.set(result.items);
      } catch {
        this.items.set([]);
      }
    });
  }
}

import { ChangeDetectionStrategy, Component, computed } from '@angular/core';
import { BsTrendChartComponent, type TrendSeries } from '@mintplayer/ng-bootstrap/charts/trend';
import { SparkTrendBase } from './spark-trend-base';

/**
 * The chart half of `spark-trend`, usable on its own: an interactive trend chart of one series with
 * an optional goal line, or a muted dash. Imports only the trend chart.
 */
@Component({
  selector: 'spark-trend-chart',
  imports: [BsTrendChartComponent],
  template: `
    @if (points()) {
      <bs-trend-chart [series]="series()" [yMin]="settings().yMin" [yMax]="settings().yMax"
                      [goal]="settings().goal" [goalLabel]="settings().goalLabel" [inputLabel]="settings().label" />
    } @else {
      <span class="text-muted small">—</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkTrendChartComponent extends SparkTrendBase {
  protected readonly series = computed<TrendSeries[]>(() => {
    const s = this.settings();
    return [{ id: s.seriesId, label: s.seriesLabel, points: this.points() ?? [] }];
  });
}

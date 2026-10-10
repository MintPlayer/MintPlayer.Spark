import { ChangeDetectionStrategy, Component } from '@angular/core';
import { BsSparklineComponent } from '@mintplayer/ng-bootstrap/charts/sparkline';
import type { TrendPoint } from '@mintplayer/ng-bootstrap/charts/trend';
import { SparkTrendBase } from './spark-trend-base';

// A file of its own, apart from the chart: a bundler splits by module, so a module holding both
// halves would put the trend chart in every chunk that draws a sparkline.

/**
 * The sparkline half of `spark-trend`, usable on its own: a compact line for a cell, or a muted dash.
 * Imports only the sparkline, so an app drawing sparklines does not load the trend chart with them.
 */
@Component({
  selector: 'spark-trend-sparkline',
  imports: [BsSparklineComponent],
  template: `
    @if (points(); as p) {
      <bs-sparkline class="d-inline-block" [style.width]="settings().width" [style.height]="settings().height"
                    [points]="ys(p)" [yMin]="settings().yMin" [yMax]="settings().yMax"
                    [inputLabel]="settings().label" />
    } @else {
      <span class="text-muted small">—</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkTrendSparklineComponent extends SparkTrendBase {
  protected ys(points: TrendPoint[]): (number | null)[] {
    return points.map(p => p.y);
  }
}

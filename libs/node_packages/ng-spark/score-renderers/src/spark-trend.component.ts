import { ChangeDetectionStrategy, Component } from '@angular/core';
import { SparkTrendBase } from './spark-trend-base';
import { SparkTrendChartComponent } from './spark-trend-chart.component';
import { SparkTrendSparklineComponent } from './spark-trend-sparkline.component';

/**
 * The `spark-trend` renderer: a series as a sparkline in a cell, or with `display: 'chart'` as an
 * interactive trend chart. The value is an array of numbers (null for a gap) or of `{ x, y }` points
 * (x a number or a Date); anything else, or fewer than `minPoints` points, draws a muted dash.
 *
 * Generalised from CodeCoverage's repository sparkline (90×24 on a fixed 0–100 scale) and its
 * "coverage over time" chart (an 80% goal line). Registering this renderer loads both charts; a
 * template that needs only one uses `<spark-trend-sparkline>` or `<spark-trend-chart>`.
 */
@Component({
  selector: 'spark-trend',
  imports: [SparkTrendSparklineComponent, SparkTrendChartComponent],
  template: `
    @if (settings().display === 'chart') {
      <spark-trend-chart [value]="value()" [options]="options()" />
    } @else {
      <spark-trend-sparkline [value]="value()" [options]="options()" />
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkTrendComponent extends SparkTrendBase {}

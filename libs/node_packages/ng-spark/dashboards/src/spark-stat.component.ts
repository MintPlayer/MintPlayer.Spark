import { ChangeDetectionStrategy, Component, computed, inject, input, LOCALE_ID } from '@angular/core';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { formatSparkDashboardValue, SparkDashboardFormat, sparkThresholdColor, toSparkNumber } from './dashboard-format';

/** `rendererOptions` of the `spark-stat` renderer. Every member is optional. */
export interface SparkStatOptions {
  /** How the value is written. Default `'number'`. */
  format?: SparkDashboardFormat;
  /** Decimals for `number` (default 0) and `percent` (default 1). */
  decimals?: number;
  /** Text after the value, e.g. `' sites'`. Default none. */
  suffix?: string;
  /** The value turns amber at or past this. */
  warning?: number;
  /** The value turns red at or past this. */
  danger?: number;
  /** `'above'` (default): high is bad, as for disk use. `'below'`: low is bad, as for free space. */
  direction?: 'above' | 'below';
}

/**
 * The `spark-stat` renderer: one figure, large on a detail page (a dashboard tile) and plain in a grid cell,
 * coloured amber or red once it passes the model's `warning` / `danger` threshold. No value draws a muted dash.
 */
@Component({
  selector: 'spark-stat',
  template: `
    <span class="spark-stat" [class.text-warning-emphasis]="color() === 'warning'" [class.text-danger]="color() === 'danger'"
          [class.text-body-secondary]="text() === '—'" [attr.data-threshold]="color()">{{ text() }}</span>
  `,
  styles: [`
    :host { display: inline-block; }
    .spark-stat { font-size: var(--spark-stat-font-size, 1.75rem); font-weight: 600; line-height: 1.2; font-variant-numeric: tabular-nums; }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkStatComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  private readonly locale = inject(LOCALE_ID);

  readonly value = input<any>();
  readonly options = input<Record<string, any> | undefined>();

  private readonly settings = computed(() => (this.options() ?? {}) as SparkStatOptions);

  protected readonly text = computed(() => {
    const s = this.settings();
    const text = formatSparkDashboardValue(this.value(), s.format, this.locale, typeof s.decimals === 'number' ? s.decimals : undefined);
    return text === '—' || typeof s.suffix !== 'string' ? text : text + s.suffix;
  });

  protected readonly color = computed(() => sparkThresholdColor(toSparkNumber(this.value()), this.settings()));
}

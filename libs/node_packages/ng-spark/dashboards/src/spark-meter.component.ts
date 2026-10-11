import { ChangeDetectionStrategy, Component, computed, inject, input, LOCALE_ID } from '@angular/core';
import { formatNumber } from '@angular/common';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsProgressBarComponent, BsProgressComponent } from '@mintplayer/ng-bootstrap/progress-bar';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { formatSparkDashboardValue, SparkDashboardFormat, sparkThresholdColor, toSparkNumber } from './dashboard-format';

/** `rendererOptions` of the `spark-meter` renderer. Every member is optional. */
export interface SparkMeterOptions {
  /** The ceiling when the value is a plain number (a value `{ value, max }` brings its own). Default 100. */
  max?: number;
  /** How the value and the ceiling are written. Default `'number'`. */
  format?: SparkDashboardFormat;
  /** Amber from this share of the ceiling, in percent. Default 75. */
  warning?: number;
  /** Red from this share of the ceiling, in percent. Default 90. */
  danger?: number;
  /** The meter's accessible name. Default `'Usage'`. */
  label?: string;
}

interface MeterView {
  value: number;
  max: number;
  /** The share of the ceiling, in percent (0–100), one decimal. */
  share: number;
  color: Color;
  text: string;
}

/**
 * The `spark-meter` renderer: how much of something is used, as a bar coloured by its share (green, amber from 75 %,
 * red from 90 % by default) with "used of total (share)" next to it. The value is a number against the `max` option, or
 * `{ value, max }` from the server (the dashboards package's `DashboardValues.Meter`), e.g. disk bytes used of the total.
 * No value, or no ceiling, draws a muted dash.
 */
@Component({
  selector: 'spark-meter',
  imports: [BsProgressComponent, BsProgressBarComponent],
  template: `
    @if (view(); as v) {
      <div class="d-flex flex-column gap-1 spark-meter">
        <bs-progress [style.height]="'.75rem'">
          <bs-progress-bar [value]="v.share" [minimum]="0" [maximum]="100" [color]="v.color"
                           [ariaLabel]="label()" [valueText]="v.text" />
        </bs-progress>
        <span class="small text-nowrap" data-meter-text>{{ v.text }}</span>
      </div>
    } @else {
      <span class="text-muted small">—</span>
    }
  `,
  styles: [`:host { display: block; min-width: 8rem; }`],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkMeterComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  private readonly locale = inject(LOCALE_ID);

  readonly value = input<any>();
  readonly options = input<Record<string, any> | undefined>();

  private readonly settings = computed(() => (this.options() ?? {}) as SparkMeterOptions);
  protected readonly label = computed(() => (typeof this.settings().label === 'string' ? this.settings().label! : 'Usage'));

  protected readonly view = computed<MeterView | null>(() => {
    const raw = this.value();
    const s = this.settings();
    const isPair = raw !== null && typeof raw === 'object' && 'value' in raw;
    const value = toSparkNumber(isPair ? raw.value : raw);
    const max = toSparkNumber(isPair ? raw.max : undefined) ?? toSparkNumber(s.max) ?? (isPair ? null : 100);
    if (value === null || max === null || max <= 0) return null;

    const share = Math.round(Math.min(100, Math.max(0, (value / max) * 100)) * 10) / 10;
    const threshold = sparkThresholdColor(share, {
      warning: typeof s.warning === 'number' ? s.warning : 75,
      danger: typeof s.danger === 'number' ? s.danger : 90,
    });
    const format = s.format ?? 'number';
    return {
      value,
      max,
      share,
      color: threshold === 'danger' ? Color.danger : threshold === 'warning' ? Color.warning : Color.success,
      text: `${formatSparkDashboardValue(value, format, this.locale)} / ${formatSparkDashboardValue(max, format, this.locale)} (${formatNumber(share, this.locale, '1.0-1')}%)`,
    };
  });
}

import { ChangeDetectionStrategy, Component, computed, inject, input, LOCALE_ID } from '@angular/core';
import { formatNumber } from '@angular/common';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsProgressComponent, BsProgressBarComponent } from '@mintplayer/ng-bootstrap/progress-bar';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { resolveSparkScoreBands, SparkScoreBandsOption, sparkScoreColor, toSparkScoreNumber } from './score-bands';
import { numberOption, stringOption } from './option-readers';

/** `rendererOptions` of the `spark-threshold-bar` renderer. Every member is optional. */
export interface SparkThresholdBarOptions {
  /** Scale minimum. Default 0. */
  min?: number;
  /** Scale maximum. Default 100. */
  max?: number;
  /** Colour bands. Default `'traffic-light'` (green ≥ 80, amber ≥ 60, red below). */
  bands?: SparkScoreBandsOption;
  /** Decimals of the shown value. Default 1. */
  decimals?: number;
  /** Text after the shown value. Default `'%'`. */
  suffix?: string;
  /** The bar's accessible name. Default `'Value'`. */
  label?: string;
  /** CSS width of the bar. Default `'80px'`. */
  width?: string;
  /** CSS height of the bar. Default `'8px'`. */
  height?: string;
  /** Show the value as text next to the bar. Default true. */
  showValue?: boolean;
}

interface ThresholdBarView {
  value: number;
  text: string;
  color: Color;
}

/**
 * The `spark-threshold-bar` renderer: a compact progress bar coloured by the band the value falls
 * in, with the value next to it. No value (null, undefined, empty, unparseable) draws a muted dash
 * rather than an empty bar, which would read as a real 0. The colour comes from the unrounded value,
 * so 59.95 shows "60.0%" yet stays below a 60 threshold.
 *
 * Generalised from CodeCoverage's coverage bar, which is this renderer with `label: 'Coverage'`.
 */
@Component({
  selector: 'spark-threshold-bar',
  imports: [BsProgressComponent, BsProgressBarComponent],
  template: `
    @if (view(); as v) {
      <div class="d-flex align-items-center gap-2">
        <bs-progress [style.width]="settings().width" [style.height]="settings().height">
          <bs-progress-bar [value]="v.value" [minimum]="settings().min" [maximum]="settings().max" [color]="v.color"
                           [ariaLabel]="settings().label" [valueText]="v.text" />
        </bs-progress>
        @if (settings().showValue) {
          <span class="small text-nowrap">{{ v.text }}</span>
        }
      </div>
    } @else {
      <span class="text-muted small">—</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkThresholdBarComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  private readonly locale = inject(LOCALE_ID);

  readonly value = input<any>();
  readonly options = input<Record<string, any> | undefined>();

  protected readonly settings = computed(() => {
    const o = (this.options() ?? {}) as SparkThresholdBarOptions;
    const decimals = Math.max(0, Math.trunc(numberOption(o.decimals, 1)));
    return {
      min: numberOption(o.min, 0),
      max: numberOption(o.max, 100),
      bands: resolveSparkScoreBands(o.bands),
      decimals,
      suffix: stringOption(o.suffix, '%'),
      label: stringOption(o.label, 'Value'),
      width: stringOption(o.width, '80px'),
      height: stringOption(o.height, '8px'),
      showValue: o.showValue !== false,
    };
  });

  protected readonly view = computed<ThresholdBarView | null>(() => {
    const value = toSparkScoreNumber(this.value());
    if (value === null) return null;
    const s = this.settings();
    return {
      value,
      text: formatNumber(value, this.locale, `1.${s.decimals}-${s.decimals}`) + s.suffix,
      color: sparkScoreColor(value, s.bands),
    };
  });
}

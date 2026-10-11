import { ChangeDetectionStrategy, Component, computed, inject, input, LOCALE_ID } from '@angular/core';
import { formatNumber } from '@angular/common';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { resolveSparkScoreBands, SparkScoreBandsOption, sparkScoreBand, SparkScoreColor, toSparkScoreNumber } from './score-bands';
import { numberOption, stringOption } from './option-readers';

/** `rendererOptions` of the `spark-gauge` renderer. Every member is optional. */
export interface SparkGaugeOptions {
  /** Scale minimum. Default 0. */
  min?: number;
  /** Scale maximum. Default 100. */
  max?: number;
  /**
   * The bands that colour the ring and, when they carry a `label`, name the grade drawn next to it.
   * `'letter-grades'` is A ≥ 90, B ≥ 80, C ≥ 70, D ≥ 50, else F. Default `'traffic-light'` (no grade).
   */
  bands?: SparkScoreBandsOption;
  /** Decimals of the score in the ring. Default 0. */
  decimals?: number;
  /** Text after the score, e.g. `'%'`. Default none. */
  suffix?: string;
  /** The gauge's accessible name. Default `'Score'`. */
  label?: string;
  /** Diameter in CSS pixels. Default 48. */
  size?: number;
}

interface GaugeView {
  value: number;
  text: string;
  color: SparkScoreColor;
  grade: string | null;
  /** The grade badge's colour (the band's). */
  badge: Color;
  /** Arc length on a ring of circumference 100: the share of the scale reached, clamped to 0–100. */
  arc: number;
}

/**
 * The `spark-gauge` renderer: a score as a ring filled to its share of the scale, coloured by its
 * band, with the score inside and the band's grade (when the bands carry labels) beside it. No value
 * draws a muted dash rather than an empty ring, which would read as a real 0.
 *
 * Exposed as an ARIA `meter` whose value text includes the grade ("72 (C)").
 */
@Component({
  selector: 'spark-gauge',
  // The grade is ng-bootstrap's badge: its CSS ships with the component, so a bare `.badge` class drew an unstyled letter.
  imports: [BsBadgeComponent],
  template: `
    @if (view(); as v) {
      <span class="d-inline-flex align-items-center gap-2" role="meter"
            [attr.aria-label]="settings().label" [attr.aria-valuenow]="v.value"
            [attr.aria-valuemin]="settings().min" [attr.aria-valuemax]="settings().max"
            [attr.aria-valuetext]="v.grade ? v.text + ' (' + v.grade + ')' : v.text">
        <svg viewBox="0 0 36 36" [attr.width]="settings().size" [attr.height]="settings().size" aria-hidden="true">
          <circle class="spark-gauge-track" cx="18" cy="18" r="15.9155" fill="none" stroke-width="3.5" />
          <circle class="spark-gauge-arc" cx="18" cy="18" r="15.9155" fill="none" stroke-width="3.5"
                  stroke-linecap="round" transform="rotate(-90 18 18)"
                  [attr.stroke]="'var(--bs-' + v.color + ')'" [attr.stroke-dasharray]="v.arc + ' 100'" />
          <text x="18" y="18" text-anchor="middle" dominant-baseline="central" class="spark-gauge-text">{{ v.text }}</text>
        </svg>
        @if (v.grade) {
          <bs-badge [type]="v.badge">{{ v.grade }}</bs-badge>
        }
      </span>
    } @else {
      <span class="text-muted small">—</span>
    }
  `,
  styles: [`
    .spark-gauge-track { stroke: var(--bs-secondary-bg); }
    .spark-gauge-text { fill: var(--bs-body-color); font-size: 10px; font-weight: 600; }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkGaugeComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  private readonly locale = inject(LOCALE_ID);

  readonly value = input<any>();
  readonly options = input<Record<string, any> | undefined>();

  protected readonly settings = computed(() => {
    const o = (this.options() ?? {}) as SparkGaugeOptions;
    return {
      min: numberOption(o.min, 0),
      max: numberOption(o.max, 100),
      bands: resolveSparkScoreBands(o.bands),
      decimals: Math.max(0, Math.trunc(numberOption(o.decimals, 0))),
      suffix: stringOption(o.suffix, ''),
      label: stringOption(o.label, 'Score'),
      size: Math.max(1, numberOption(o.size, 48)),
    };
  });

  protected readonly view = computed<GaugeView | null>(() => {
    const value = toSparkScoreNumber(this.value());
    if (value === null) return null;
    const s = this.settings();
    const band = sparkScoreBand(value, s.bands);
    const span = s.max - s.min;
    const share = span > 0 ? (value - s.min) / span : 0;
    return {
      value,
      text: formatNumber(value, this.locale, `1.${s.decimals}-${s.decimals}`) + s.suffix,
      color: band?.color ?? 'secondary',
      grade: band?.label ?? null,
      badge: Color[band?.color ?? 'secondary'],
      arc: Math.round(Math.min(1, Math.max(0, share)) * 1000) / 10,
    };
  });
}
